#include "progpu_native_text.hpp"

#include <bit>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct native port of ProGPU-owned raw Single/Pair GPOS execution in
// OpenTypeTextShaper.cs at checkpoint 9d1c6417.

namespace progpu::native::text {
namespace {

enum class apply_result : std::uint8_t {
    no_match,
    applied,
    malformed
};

struct value_adjustment final {
    std::int32_t offset_x = 0;
    std::int32_t offset_y = 0;
    std::int32_t advance_x = 0;
    std::int32_t advance_y = 0;
};

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

bool can_read(
    std::span<const std::byte> bytes,
    std::size_t offset,
    std::size_t length) noexcept {
    return offset <= bytes.size() && length <= bytes.size() - offset;
}

bool try_add(
    std::size_t left,
    std::size_t right,
    std::size_t& result) noexcept {
    if (right > std::numeric_limits<std::size_t>::max() - left) {
        return false;
    }
    result = left + right;
    return true;
}

std::uint16_t read_u16(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return static_cast<std::uint16_t>(
        (std::to_integer<std::uint16_t>(bytes[offset]) << 8U) |
        std::to_integer<std::uint16_t>(bytes[offset + 1U]));
}

std::int16_t read_i16(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return static_cast<std::int16_t>(read_u16(bytes, offset));
}

std::uint32_t read_u32(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return (static_cast<std::uint32_t>(read_u16(bytes, offset)) << 16U) |
        read_u16(bytes, offset + 2U);
}

std::size_t value_record_size(std::uint16_t format) noexcept {
    return static_cast<std::size_t>(
        std::popcount(static_cast<unsigned>(format))) * 2U;
}

bool parse_value_record(
    std::span<const std::byte> table,
    std::size_t parent,
    std::size_t offset,
    std::uint16_t format,
    value_adjustment& result) noexcept {
    result = {};
    if ((format & 0xFF00U) != 0U ||
        !can_read(table, offset, value_record_size(format))) {
        return false;
    }
    std::size_t cursor = offset;
    const auto read_value = [&](std::uint16_t bit, std::int32_t& value) {
        if ((format & bit) != 0U) {
            value = read_i16(table, cursor);
            cursor += 2U;
        }
    };
    read_value(0x0001U, result.offset_x);
    read_value(0x0002U, result.offset_y);
    read_value(0x0004U, result.advance_x);
    read_value(0x0008U, result.advance_y);
    for (std::uint16_t bit = 0x0010U; bit <= 0x0080U; bit <<= 1U) {
        if ((format & bit) == 0U) {
            continue;
        }
        const std::uint16_t relative = read_u16(table, cursor);
        cursor += 2U;
        std::size_t device = 0U;
        if (relative != 0U &&
            (!try_add(parent, relative, device) ||
                !can_read(table, device, 6U))) {
            return false;
        }
    }
    return true;
}

void apply_value(shaping_glyph& glyph, const value_adjustment& value) noexcept {
    glyph.offset_x += value.offset_x;
    glyph.offset_y += value.offset_y;
    glyph.advance_x += value.advance_x;
    glyph.advance_y += value.advance_y;
}

bool is_eligible(
    const shaping_glyph& glyph,
    std::uint16_t flags,
    std::uint16_t mark_filtering_set,
    const open_type_gdef_view* gdef) noexcept {
    if (glyph.glyph_id > 0xFFFFU) {
        return false;
    }
    if (gdef == nullptr) {
        return true;
    }
    const auto glyph_class =
        gdef->glyph_class(static_cast<std::uint16_t>(glyph.glyph_id));
    if (glyph_class == open_type_glyph_class::base &&
        (flags & 0x0002U) != 0U) {
        return false;
    }
    if (glyph_class == open_type_glyph_class::ligature &&
        (flags & 0x0004U) != 0U) {
        return false;
    }
    if (glyph_class != open_type_glyph_class::mark) {
        return true;
    }
    if ((flags & 0x0008U) != 0U) {
        return false;
    }
    if ((flags & 0x0010U) != 0U) {
        return mark_filtering_set != 0xFFFFU &&
            gdef->is_in_mark_set(
                mark_filtering_set,
                static_cast<std::uint16_t>(glyph.glyph_id));
    }
    const std::uint16_t attachment =
        static_cast<std::uint16_t>(flags >> 8U);
    return attachment == 0U ||
        gdef->mark_attachment_class(
            static_cast<std::uint16_t>(glyph.glyph_id)) == attachment;
}

std::size_t next_eligible(
    std::span<const shaping_glyph> glyphs,
    std::size_t start,
    std::uint16_t flags,
    std::uint16_t mark_filtering_set,
    const open_type_gdef_view* gdef) noexcept {
    for (std::size_t index = start; index < glyphs.size(); ++index) {
        if (is_eligible(glyphs[index], flags, mark_filtering_set, gdef)) {
            return index;
        }
    }
    return glyphs.size();
}

apply_result apply_single(
    std::span<const std::byte> table,
    std::size_t subtable,
    std::span<shaping_glyph> glyphs,
    std::size_t position) noexcept {
    if (!can_read(table, subtable, 6U)) {
        return apply_result::malformed;
    }
    const std::uint16_t format = read_u16(table, subtable);
    const std::uint16_t coverage_relative = read_u16(table, subtable + 2U);
    const std::uint16_t value_format = read_u16(table, subtable + 4U);
    std::size_t coverage_offset = 0U;
    open_type_coverage_view coverage{};
    if (coverage_relative == 0U ||
        !try_add(subtable, coverage_relative, coverage_offset) ||
        !open_type_coverage_view::try_create(table, coverage_offset, coverage)) {
        return apply_result::malformed;
    }
    const std::int32_t coverage_index = coverage.find(
        static_cast<std::uint16_t>(glyphs[position].glyph_id));
    if (coverage_index < 0) {
        return apply_result::no_match;
    }
    std::size_t value_offset = subtable + 6U;
    if (format == 2U) {
        if (!can_read(table, value_offset, 2U)) {
            return apply_result::malformed;
        }
        const std::uint16_t value_count = read_u16(table, value_offset);
        value_offset += 2U;
        if (static_cast<std::uint32_t>(coverage_index) >= value_count ||
            !can_read(table, value_offset,
                static_cast<std::size_t>(value_count) *
                    value_record_size(value_format))) {
            return apply_result::malformed;
        }
        value_offset += static_cast<std::size_t>(coverage_index) *
            value_record_size(value_format);
    } else if (format != 1U) {
        return apply_result::malformed;
    }
    value_adjustment value{};
    if (!parse_value_record(
            table, subtable, value_offset, value_format, value)) {
        return apply_result::malformed;
    }
    apply_value(glyphs[position], value);
    return apply_result::applied;
}

apply_result apply_pair(
    std::span<const std::byte> table,
    std::size_t subtable,
    std::span<shaping_glyph> glyphs,
    std::size_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gpos_apply_options& options) noexcept {
    if (!can_read(table, subtable, 10U)) {
        return apply_result::malformed;
    }
    const std::uint16_t format = read_u16(table, subtable);
    const std::uint16_t coverage_relative = read_u16(table, subtable + 2U);
    const std::uint16_t value_format1 = read_u16(table, subtable + 4U);
    const std::uint16_t value_format2 = read_u16(table, subtable + 6U);
    if ((value_format1 & 0xFF00U) != 0U ||
        (value_format2 & 0xFF00U) != 0U) {
        return apply_result::malformed;
    }
    std::size_t coverage_offset = 0U;
    open_type_coverage_view coverage{};
    if (coverage_relative == 0U ||
        !try_add(subtable, coverage_relative, coverage_offset) ||
        !open_type_coverage_view::try_create(table, coverage_offset, coverage)) {
        return apply_result::malformed;
    }
    const std::int32_t coverage_index = coverage.find(
        static_cast<std::uint16_t>(glyphs[position].glyph_id));
    if (coverage_index < 0) {
        return apply_result::no_match;
    }
    const std::size_t second = next_eligible(
        glyphs,
        position + 1U,
        lookup_flags,
        mark_filtering_set,
        options.gdef);
    if (second >= glyphs.size()) {
        return apply_result::no_match;
    }
    const std::size_t value_size1 = value_record_size(value_format1);
    const std::size_t value_size2 = value_record_size(value_format2);
    std::size_t value1_offset = 0U;
    if (format == 1U) {
        const std::uint16_t pair_set_count = read_u16(table, subtable + 8U);
        if (static_cast<std::uint32_t>(coverage_index) >= pair_set_count ||
            !can_read(table, subtable + 10U,
                static_cast<std::size_t>(pair_set_count) * 2U)) {
            return apply_result::malformed;
        }
        const std::uint16_t relative = read_u16(
            table, subtable + 10U + coverage_index * 2U);
        std::size_t pair_set = 0U;
        if (relative == 0U || !try_add(subtable, relative, pair_set) ||
            !can_read(table, pair_set, 2U)) {
            return apply_result::malformed;
        }
        const std::uint16_t pair_count = read_u16(table, pair_set);
        const std::size_t stride = 2U + value_size1 + value_size2;
        if (!can_read(table, pair_set + 2U,
                static_cast<std::size_t>(pair_count) * stride)) {
            return apply_result::malformed;
        }
        std::uint32_t low = 0U;
        std::uint32_t high = pair_count;
        while (low < high) {
            const std::uint32_t middle = low + (high - low) / 2U;
            const std::size_t record = pair_set + 2U + middle * stride;
            const std::uint16_t glyph = read_u16(table, record);
            if (glyphs[second].glyph_id < glyph) {
                high = middle;
            } else if (glyphs[second].glyph_id > glyph) {
                low = middle + 1U;
            } else {
                value1_offset = record + 2U;
                break;
            }
        }
        if (value1_offset == 0U) {
            return apply_result::no_match;
        }
    } else if (format == 2U) {
        if (!can_read(table, subtable, 16U)) {
            return apply_result::malformed;
        }
        const std::uint16_t class1_relative = read_u16(table, subtable + 8U);
        const std::uint16_t class2_relative = read_u16(table, subtable + 10U);
        const std::uint16_t class1_count = read_u16(table, subtable + 12U);
        const std::uint16_t class2_count = read_u16(table, subtable + 14U);
        std::size_t class1_offset = 0U;
        std::size_t class2_offset = 0U;
        open_type_class_definition_view class1{};
        open_type_class_definition_view class2{};
        if (class1_relative == 0U || class2_relative == 0U ||
            !try_add(subtable, class1_relative, class1_offset) ||
            !try_add(subtable, class2_relative, class2_offset) ||
            !open_type_class_definition_view::try_create(
                table, class1_offset, class1) ||
            !open_type_class_definition_view::try_create(
                table, class2_offset, class2)) {
            return apply_result::malformed;
        }
        const std::uint16_t first_class = class1.get(
            static_cast<std::uint16_t>(glyphs[position].glyph_id));
        const std::uint16_t second_class = class2.get(
            static_cast<std::uint16_t>(glyphs[second].glyph_id));
        if (first_class >= class1_count || second_class >= class2_count) {
            return apply_result::no_match;
        }
        const std::size_t stride = value_size1 + value_size2;
        const std::size_t cell =
            static_cast<std::size_t>(first_class) * class2_count + second_class;
        value1_offset = subtable + 16U + cell * stride;
        if (!can_read(table, value1_offset, stride)) {
            return apply_result::malformed;
        }
    } else {
        return apply_result::malformed;
    }

    value_adjustment value1{};
    value_adjustment value2{};
    if (!parse_value_record(
            table, subtable, value1_offset, value_format1, value1) ||
        !parse_value_record(
            table,
            subtable,
            value1_offset + value_size1,
            value_format2,
            value2)) {
        return apply_result::malformed;
    }
    apply_value(glyphs[position], value1);
    apply_value(glyphs[second], value2);
    return apply_result::applied;
}

apply_result apply_subtable(
    std::span<const std::byte> table,
    std::uint16_t type,
    std::size_t subtable,
    std::span<shaping_glyph> glyphs,
    std::size_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gpos_apply_options& options) noexcept {
    if (type == 9U) {
        if (!can_read(table, subtable, 8U) || read_u16(table, subtable) != 1U) {
            return apply_result::malformed;
        }
        const std::uint16_t extension_type = read_u16(table, subtable + 2U);
        const std::uint32_t relative = read_u32(table, subtable + 4U);
        std::size_t extension = 0U;
        if (extension_type == 9U || relative == 0U ||
            !try_add(subtable, relative, extension) ||
            !can_read(table, extension, 2U)) {
            return apply_result::malformed;
        }
        return apply_subtable(
            table,
            extension_type,
            extension,
            glyphs,
            position,
            lookup_flags,
            mark_filtering_set,
            options);
    }
    if (type == 1U) {
        return apply_single(table, subtable, glyphs, position);
    }
    if (type == 2U) {
        return apply_pair(
            table,
            subtable,
            glyphs,
            position,
            lookup_flags,
            mark_filtering_set,
            options);
    }
    return apply_result::no_match;
}

} // namespace

bool try_apply_open_type_gpos_lookup(
    const open_type_layout_table_view& gpos,
    std::uint16_t lookup_index,
    std::span<shaping_glyph> glyphs,
    const open_type_gpos_apply_options& options,
    bool& applied,
    font_error* error) noexcept {
    applied = false;
    open_type_lookup_view lookup{};
    if (!gpos.try_get_lookup(lookup_index, lookup, error)) {
        return false;
    }
    if (lookup.type != 1U && lookup.type != 2U && lookup.type != 9U) {
        set_error(error, font_error::none);
        return true;
    }
    for (std::size_t position = 0U; position < glyphs.size(); ++position) {
        if (!is_eligible(
                glyphs[position],
                lookup.flags,
                lookup.mark_filtering_set,
                options.gdef)) {
            continue;
        }
        for (std::uint16_t index = 0U; index < lookup.subtable_count; ++index) {
            std::size_t subtable = 0U;
            if (!lookup.try_get_subtable(index, subtable, error)) {
                return false;
            }
            const apply_result result = apply_subtable(
                lookup.table,
                lookup.type,
                subtable,
                glyphs,
                position,
                lookup.flags,
                lookup.mark_filtering_set,
                options);
            if (result == apply_result::malformed) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            if (result == apply_result::applied) {
                applied = true;
                break;
            }
        }
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
