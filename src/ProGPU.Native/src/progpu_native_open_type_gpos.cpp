#include "progpu_native_text.hpp"
#include "progpu_native_open_type_gpos_internal.hpp"

#include <bit>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct native port of ProGPU-owned raw GPOS execution and attachment
// resolution in OpenTypeTextShaper.cs at checkpoint e4d836b2.

namespace progpu::native::text {
namespace {

constexpr std::uint32_t maximum_lookup_nesting_depth = 64U;

using apply_result = detail::gpos_apply_result;

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

bool read_device_delta(
    std::span<const std::byte> table,
    std::size_t parent,
    std::uint16_t relative,
    std::uint16_t pixels_per_em,
    const open_type_gpos_apply_options& options,
    std::int32_t& delta) noexcept {
    delta = 0;
    if (relative == 0U) {
        return true;
    }
    std::size_t device = 0U;
    if (!try_add(parent, relative, device) || !can_read(table, device, 6U)) {
        return false;
    }
    const std::uint16_t first = read_u16(table, device);
    const std::uint16_t second = read_u16(table, device + 2U);
    const std::uint16_t format = read_u16(table, device + 4U);
    if (format == 0x8000U) {
        if (options.font == nullptr) {
            return true;
        }
        float variation = 0.0F;
        bool uses_store = false;
        if (!options.font->try_get_layout_variation(
                first,
                second,
                options.normalized_coordinates,
                variation,
                uses_store)) {
            return false;
        }
        if (uses_store) {
            delta = static_cast<std::int32_t>(std::lround(variation));
        }
        return true;
    }
    if (format < 1U || format > 3U || second < first) {
        return false;
    }
    const std::uint32_t bits = 1U << format;
    const std::uint32_t values_per_word = 16U / bits;
    const std::uint32_t value_count =
        static_cast<std::uint32_t>(second) - first + 1U;
    const std::size_t word_count =
        (value_count + values_per_word - 1U) / values_per_word;
    if (!can_read(table, device + 6U, word_count * 2U)) {
        return false;
    }
    if (pixels_per_em < first || pixels_per_em > second ||
        options.font == nullptr || pixels_per_em == 0U) {
        return true;
    }
    const std::uint32_t value_index = pixels_per_em - first;
    const std::uint16_t packed = read_u16(
        table,
        device + 6U +
            static_cast<std::size_t>(value_index / values_per_word) * 2U);
    const std::uint32_t shift =
        16U - bits * (value_index % values_per_word + 1U);
    const std::uint32_t mask = (1U << bits) - 1U;
    std::int32_t pixels = static_cast<std::int32_t>(
        (static_cast<std::uint32_t>(packed) >> shift) & mask);
    const std::int32_t sign = 1 << (bits - 1U);
    if ((pixels & sign) != 0) {
        pixels -= 1 << bits;
    }
    sfnt_header_metrics header{};
    if (!options.font->try_get_header_metrics(header) ||
        header.units_per_em == 0U) {
        return false;
    }
    delta = static_cast<std::int32_t>(std::lround(
        static_cast<double>(pixels) * header.units_per_em /
        pixels_per_em));
    return true;
}

bool parse_value_record(
    std::span<const std::byte> table,
    std::size_t parent,
    std::size_t offset,
    std::uint16_t format,
    const open_type_gpos_apply_options& options,
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
        std::int32_t delta = 0;
        const bool horizontal = bit == 0x0010U || bit == 0x0040U;
        if (!read_device_delta(
                table,
                parent,
                relative,
                horizontal ? options.pixels_per_em_x
                           : options.pixels_per_em_y,
                options,
                delta)) {
            return false;
        }
        if (bit == 0x0010U) {
            result.offset_x += delta;
        } else if (bit == 0x0020U) {
            result.offset_y += delta;
        } else if (bit == 0x0040U) {
            result.advance_x += delta;
        } else {
            result.advance_y += delta;
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

std::size_t previous_eligible(
    std::span<const shaping_glyph> glyphs,
    std::size_t before,
    std::uint16_t flags,
    std::uint16_t mark_filtering_set,
    const open_type_gdef_view* gdef,
    bool skip_marks) noexcept {
    while (before != 0U) {
        --before;
        if (!is_eligible(glyphs[before], flags, mark_filtering_set, gdef)) {
            continue;
        }
        if (skip_marks && gdef != nullptr &&
            gdef->glyph_class(static_cast<std::uint16_t>(
                glyphs[before].glyph_id)) == open_type_glyph_class::mark) {
            continue;
        }
        return before;
    }
    return glyphs.size();
}

bool parse_anchor(
    std::span<const std::byte> table,
    std::size_t offset,
    const open_type_gpos_apply_options& options,
    std::int32_t& x,
    std::int32_t& y) noexcept {
    x = 0;
    y = 0;
    if (!can_read(table, offset, 6U)) {
        return false;
    }
    const std::uint16_t format = read_u16(table, offset);
    if (format < 1U || format > 3U ||
        (format == 2U && !can_read(table, offset, 8U)) ||
        (format == 3U && !can_read(table, offset, 10U))) {
        return false;
    }
    x = read_i16(table, offset + 2U);
    y = read_i16(table, offset + 4U);
    if (format == 3U) {
        std::int32_t x_delta = 0;
        std::int32_t y_delta = 0;
        if (!read_device_delta(
                table,
                offset,
                read_u16(table, offset + 6U),
                options.pixels_per_em_x,
                options,
                x_delta) ||
            !read_device_delta(
                table,
                offset,
                read_u16(table, offset + 8U),
                options.pixels_per_em_y,
                options,
                y_delta)) {
            return false;
        }
        x += x_delta;
        y += y_delta;
    }
    return true;
}

apply_result apply_cursive(
    std::span<const std::byte> table,
    std::size_t subtable,
    std::span<shaping_glyph> glyphs,
    std::size_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gpos_apply_options& options) noexcept {
    if (!can_read(table, subtable, 6U) ||
        read_u16(table, subtable) != 1U) {
        return apply_result::malformed;
    }
    const std::uint16_t coverage_relative = read_u16(table, subtable + 2U);
    std::size_t coverage_offset = 0U;
    open_type_coverage_view coverage{};
    if (coverage_relative == 0U ||
        !try_add(subtable, coverage_relative, coverage_offset) ||
        !open_type_coverage_view::try_create(table, coverage_offset, coverage)) {
        return apply_result::malformed;
    }
    const std::int32_t current_coverage = coverage.find(
        static_cast<std::uint16_t>(glyphs[position].glyph_id));
    const std::uint16_t record_count = read_u16(table, subtable + 4U);
    if (current_coverage < 0) {
        return apply_result::no_match;
    }
    if (static_cast<std::uint32_t>(current_coverage) >= record_count ||
        !can_read(table, subtable + 6U,
            static_cast<std::size_t>(record_count) * 4U)) {
        return apply_result::malformed;
    }
    const std::uint16_t entry_relative = read_u16(
        table, subtable + 6U + current_coverage * 4U);
    if (entry_relative == 0U) {
        return apply_result::no_match;
    }
    const std::size_t previous = previous_eligible(
        glyphs,
        position,
        lookup_flags,
        mark_filtering_set,
        options.gdef,
        false);
    if (previous >= glyphs.size()) {
        return apply_result::no_match;
    }
    const std::int32_t previous_coverage = coverage.find(
        static_cast<std::uint16_t>(glyphs[previous].glyph_id));
    if (previous_coverage < 0 ||
        static_cast<std::uint32_t>(previous_coverage) >= record_count) {
        return apply_result::no_match;
    }
    const std::uint16_t exit_relative = read_u16(
        table, subtable + 8U + previous_coverage * 4U);
    std::size_t entry_offset = 0U;
    std::size_t exit_offset = 0U;
    std::int32_t entry_x = 0;
    std::int32_t entry_y = 0;
    std::int32_t exit_x = 0;
    std::int32_t exit_y = 0;
    if (exit_relative == 0U ||
        !try_add(subtable, entry_relative, entry_offset) ||
        !try_add(subtable, exit_relative, exit_offset) ||
        !parse_anchor(table, entry_offset, options, entry_x, entry_y) ||
        !parse_anchor(table, exit_offset, options, exit_x, exit_y)) {
        return apply_result::malformed;
    }
    shaping_glyph& first = glyphs[previous];
    shaping_glyph& current = glyphs[position];
    const bool horizontal = options.direction == shaping_direction::left_to_right ||
        options.direction == shaping_direction::right_to_left;
    switch (options.direction) {
        case shaping_direction::left_to_right: {
            first.advance_x = exit_x + first.offset_x;
            const std::int32_t delta = entry_x + current.offset_x;
            current.advance_x -= delta;
            current.offset_x -= delta;
            break;
        }
        case shaping_direction::right_to_left: {
            const std::int32_t delta = exit_x + first.offset_x;
            first.advance_x -= delta;
            first.offset_x -= delta;
            current.advance_x = entry_x + current.offset_x;
            break;
        }
        case shaping_direction::top_to_bottom: {
            first.advance_y = exit_y + first.offset_y;
            const std::int32_t delta = entry_y + current.offset_y;
            current.advance_y -= delta;
            current.offset_y -= delta;
            break;
        }
        case shaping_direction::bottom_to_top: {
            const std::int32_t delta = exit_y + first.offset_y;
            first.advance_y -= delta;
            first.offset_y -= delta;
            current.advance_y = entry_y;
            break;
        }
        default:
            return apply_result::malformed;
    }
    const bool right_to_left = (lookup_flags & 0x0001U) != 0U;
    const std::size_t child = right_to_left ? previous : position;
    const std::size_t parent = right_to_left ? position : previous;
    options.attachments[child].target = static_cast<std::int32_t>(parent);
    options.attachments[child].kind = horizontal
        ? shaping_attachment_kind::cursive_horizontal
        : shaping_attachment_kind::cursive_vertical;
    if (horizontal) {
        glyphs[child].offset_y = right_to_left
            ? entry_y - exit_y
            : exit_y - entry_y;
    } else {
        glyphs[child].offset_x = right_to_left
            ? entry_x - exit_x
            : exit_x - entry_x;
    }
    if (options.attachments[parent].target == static_cast<std::int32_t>(child) &&
        options.attachments[parent].kind == options.attachments[child].kind) {
        options.attachments[parent] = {};
        options.attachments[parent].target = -1;
        if (horizontal) {
            glyphs[parent].offset_y = 0;
        } else {
            glyphs[parent].offset_x = 0;
        }
    }
    return apply_result::applied;
}

struct mark_attachment_header final {
    std::int32_t mark_coverage_index = -1;
    open_type_coverage_view target_coverage{};
    std::uint16_t class_count = 0U;
    std::size_t mark_array = 0U;
    std::size_t target_array = 0U;
};

apply_result parse_mark_attachment_header(
    std::span<const std::byte> table,
    std::size_t subtable,
    std::uint16_t glyph,
    mark_attachment_header& result) noexcept {
    result = {};
    if (!can_read(table, subtable, 12U) ||
        read_u16(table, subtable) != 1U) {
        return apply_result::malformed;
    }
    const std::uint16_t mark_coverage_relative = read_u16(table, subtable + 2U);
    const std::uint16_t target_coverage_relative = read_u16(table, subtable + 4U);
    const std::uint16_t mark_array_relative = read_u16(table, subtable + 8U);
    const std::uint16_t target_array_relative = read_u16(table, subtable + 10U);
    std::size_t mark_coverage_offset = 0U;
    std::size_t target_coverage_offset = 0U;
    open_type_coverage_view mark_coverage{};
    if (mark_coverage_relative == 0U || target_coverage_relative == 0U ||
        mark_array_relative == 0U || target_array_relative == 0U ||
        !try_add(subtable, mark_coverage_relative, mark_coverage_offset) ||
        !try_add(subtable, target_coverage_relative, target_coverage_offset) ||
        !try_add(subtable, mark_array_relative, result.mark_array) ||
        !try_add(subtable, target_array_relative, result.target_array) ||
        !open_type_coverage_view::try_create(
            table, mark_coverage_offset, mark_coverage) ||
        !open_type_coverage_view::try_create(
            table, target_coverage_offset, result.target_coverage) ||
        !can_read(table, result.mark_array, 2U) ||
        !can_read(table, result.target_array, 2U)) {
        return apply_result::malformed;
    }
    result.mark_coverage_index = mark_coverage.find(glyph);
    if (result.mark_coverage_index < 0) {
        return apply_result::no_match;
    }
    result.class_count = read_u16(table, subtable + 6U);
    return result.class_count == 0U
        ? apply_result::malformed
        : apply_result::applied;
}

apply_result attach_mark(
    std::span<const std::byte> table,
    std::span<shaping_glyph> glyphs,
    std::size_t mark_index,
    std::size_t target_index,
    const mark_attachment_header& header,
    std::int32_t target_coverage_index,
    std::size_t target_anchor_base,
    std::size_t target_records,
    const open_type_gpos_apply_options& options) noexcept {
    const std::uint16_t mark_count = read_u16(table, header.mark_array);
    if (static_cast<std::uint32_t>(header.mark_coverage_index) >= mark_count ||
        !can_read(table, header.mark_array + 2U,
            static_cast<std::size_t>(mark_count) * 4U)) {
        return apply_result::malformed;
    }
    const std::size_t mark_record = header.mark_array + 2U +
        static_cast<std::size_t>(header.mark_coverage_index) * 4U;
    const std::uint16_t mark_class = read_u16(table, mark_record);
    const std::uint16_t mark_anchor_relative = read_u16(table, mark_record + 2U);
    if (mark_class >= header.class_count || mark_anchor_relative == 0U ||
        target_coverage_index < 0) {
        return apply_result::malformed;
    }
    const std::size_t target_record = target_records +
        (static_cast<std::size_t>(target_coverage_index) * header.class_count +
            mark_class) * 2U;
    if (!can_read(table, target_record, 2U)) {
        return apply_result::malformed;
    }
    const std::uint16_t target_anchor_relative = read_u16(table, target_record);
    std::size_t mark_anchor = 0U;
    std::size_t target_anchor = 0U;
    std::int32_t mark_x = 0;
    std::int32_t mark_y = 0;
    std::int32_t target_x = 0;
    std::int32_t target_y = 0;
    if (target_anchor_relative == 0U ||
        !try_add(header.mark_array, mark_anchor_relative, mark_anchor) ||
        !try_add(target_anchor_base, target_anchor_relative, target_anchor) ||
        !parse_anchor(table, mark_anchor, options, mark_x, mark_y) ||
        !parse_anchor(
            table, target_anchor, options, target_x, target_y)) {
        return apply_result::malformed;
    }
    glyphs[mark_index].offset_x = target_x - mark_x;
    glyphs[mark_index].offset_y = target_y - mark_y;
    glyphs[mark_index].flags = static_cast<shaping_glyph_flags>(
        static_cast<std::uint32_t>(glyphs[mark_index].flags) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break));
    options.attachments[mark_index].target =
        static_cast<std::int32_t>(target_index);
    options.attachments[mark_index].kind = shaping_attachment_kind::mark;
    return apply_result::applied;
}

apply_result apply_single(
    std::span<const std::byte> table,
    std::size_t subtable,
    std::span<shaping_glyph> glyphs,
    std::size_t position,
    const open_type_gpos_apply_options& options) noexcept {
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
            table, subtable, value_offset, value_format, options, value)) {
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
            table,
            subtable,
            value1_offset,
            value_format1,
            options,
            value1) ||
        !parse_value_record(
            table,
            subtable,
            value1_offset + value_size1,
            value_format2,
            options,
            value2)) {
        return apply_result::malformed;
    }
    apply_value(glyphs[position], value1);
    apply_value(glyphs[second], value2);
    return apply_result::applied;
}

apply_result apply_mark_to_base_or_mark(
    std::span<const std::byte> table,
    std::uint16_t type,
    std::size_t subtable,
    std::span<shaping_glyph> glyphs,
    std::size_t mark_index,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gpos_apply_options& options) noexcept {
    mark_attachment_header header{};
    const apply_result header_result = parse_mark_attachment_header(
        table,
        subtable,
        static_cast<std::uint16_t>(glyphs[mark_index].glyph_id),
        header);
    if (header_result != apply_result::applied) {
        return header_result;
    }
    const std::size_t target = previous_eligible(
        glyphs,
        mark_index,
        lookup_flags,
        mark_filtering_set,
        options.gdef,
        type == 4U);
    if (target >= glyphs.size()) {
        return apply_result::no_match;
    }
    if (options.gdef != nullptr) {
        const auto target_class = options.gdef->glyph_class(
            static_cast<std::uint16_t>(glyphs[target].glyph_id));
        if ((type == 4U && target_class != open_type_glyph_class::base &&
                target_class != open_type_glyph_class::unclassified) ||
            (type == 6U && target_class != open_type_glyph_class::mark)) {
            return apply_result::no_match;
        }
    }
    const std::int32_t target_coverage = header.target_coverage.find(
        static_cast<std::uint16_t>(glyphs[target].glyph_id));
    if (target_coverage < 0) {
        return apply_result::no_match;
    }
    const std::uint16_t target_count = read_u16(table, header.target_array);
    if (static_cast<std::uint32_t>(target_coverage) >= target_count) {
        return apply_result::malformed;
    }
    return attach_mark(
        table,
        glyphs,
        mark_index,
        target,
        header,
        target_coverage,
        header.target_array,
        header.target_array + 2U,
        options);
}

apply_result apply_mark_to_ligature(
    std::span<const std::byte> table,
    std::size_t subtable,
    std::span<shaping_glyph> glyphs,
    std::size_t mark_index,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gpos_apply_options& options) noexcept {
    mark_attachment_header header{};
    const apply_result header_result = parse_mark_attachment_header(
        table,
        subtable,
        static_cast<std::uint16_t>(glyphs[mark_index].glyph_id),
        header);
    if (header_result != apply_result::applied) {
        return header_result;
    }
    const std::size_t target = previous_eligible(
        glyphs,
        mark_index,
        lookup_flags,
        mark_filtering_set,
        options.gdef,
        true);
    if (target >= glyphs.size()) {
        return apply_result::no_match;
    }
    if (options.gdef != nullptr &&
        options.gdef->glyph_class(
            static_cast<std::uint16_t>(glyphs[target].glyph_id)) !=
            open_type_glyph_class::ligature) {
        return apply_result::no_match;
    }
    const std::int32_t target_coverage = header.target_coverage.find(
        static_cast<std::uint16_t>(glyphs[target].glyph_id));
    const std::uint16_t ligature_count = read_u16(table, header.target_array);
    if (target_coverage < 0) {
        return apply_result::no_match;
    }
    if (static_cast<std::uint32_t>(target_coverage) >= ligature_count ||
        !can_read(table, header.target_array + 2U,
            static_cast<std::size_t>(ligature_count) * 2U)) {
        return apply_result::malformed;
    }
    const std::uint16_t ligature_relative = read_u16(
        table,
        header.target_array + 2U +
            static_cast<std::size_t>(target_coverage) * 2U);
    std::size_t ligature_attach = 0U;
    if (ligature_relative == 0U ||
        !try_add(header.target_array, ligature_relative, ligature_attach) ||
        !can_read(table, ligature_attach, 2U)) {
        return apply_result::malformed;
    }
    const std::uint16_t component_count = read_u16(table, ligature_attach);
    if (component_count == 0U) {
        return apply_result::malformed;
    }
    // The public bulk glyph record intentionally omits transient ligature
    // component metadata. The managed fallback selects the last component when
    // no explicit component survives, which is the deterministic native rule.
    const std::size_t component_records = ligature_attach + 2U +
        static_cast<std::size_t>(component_count - 1U) *
            header.class_count * 2U;
    return attach_mark(
        table,
        glyphs,
        mark_index,
        target,
        header,
        0,
        ligature_attach,
        component_records,
        options);
}

apply_result apply_lookup_at(
    const open_type_layout_table_view& gpos,
    std::uint16_t lookup_index,
    std::span<shaping_glyph> glyphs,
    std::size_t position,
    const open_type_gpos_apply_options& options,
    std::uint32_t depth) noexcept;


apply_result apply_subtable(
    const open_type_layout_table_view& gpos,
    std::span<const std::byte> table,
    std::uint16_t type,
    std::size_t subtable,
    std::span<shaping_glyph> glyphs,
    std::size_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gpos_apply_options& options,
    std::uint32_t depth) noexcept {
    if (type >= 3U && type <= 6U &&
        options.attachments.size() != glyphs.size()) {
        return apply_result::invalid_argument;
    }
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
            gpos,
            table,
            extension_type,
            extension,
            glyphs,
            position,
            lookup_flags,
            mark_filtering_set,
            options,
            depth);
    }
    if (type == 1U) {
        return apply_single(table, subtable, glyphs, position, options);
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
    if (type == 3U) {
        return apply_cursive(
            table,
            subtable,
            glyphs,
            position,
            lookup_flags,
            mark_filtering_set,
            options);
    }
    if (type == 4U || type == 6U) {
        return apply_mark_to_base_or_mark(
            table,
            type,
            subtable,
            glyphs,
            position,
            lookup_flags,
            mark_filtering_set,
            options);
    }
    if (type == 5U) {
        return apply_mark_to_ligature(
            table,
            subtable,
            glyphs,
            position,
            lookup_flags,
            mark_filtering_set,
            options);
    }
    if (type == 7U || type == 8U) {
        return detail::apply_gpos_context_subtable(
            gpos,
            table,
            type,
            subtable,
            glyphs,
            position,
            lookup_flags,
            mark_filtering_set,
            options,
            depth);
    }
    return apply_result::no_match;
}

apply_result apply_lookup_at(
    const open_type_layout_table_view& gpos,
    std::uint16_t lookup_index,
    std::span<shaping_glyph> glyphs,
    std::size_t position,
    const open_type_gpos_apply_options& options,
    std::uint32_t depth) noexcept {
    if (depth >= maximum_lookup_nesting_depth || position >= glyphs.size()) {
        return apply_result::no_match;
    }
    open_type_lookup_view lookup{};
    if (!gpos.try_get_lookup(lookup_index, lookup)) {
        return apply_result::malformed;
    }
    if (!is_eligible(
            glyphs[position],
            lookup.flags,
            lookup.mark_filtering_set,
            options.gdef)) {
        return apply_result::no_match;
    }
    if (lookup.type >= 3U && lookup.type <= 6U &&
        options.attachments.size() != glyphs.size()) {
        return apply_result::invalid_argument;
    }
    for (std::uint16_t index = 0U; index < lookup.subtable_count; ++index) {
        std::size_t subtable = 0U;
        if (!lookup.try_get_subtable(index, subtable)) {
            return apply_result::malformed;
        }
        const apply_result result = apply_subtable(
            gpos,
            lookup.table,
            lookup.type,
            subtable,
            glyphs,
            position,
            lookup.flags,
            lookup.mark_filtering_set,
            options,
            depth);
        if (result != apply_result::no_match) {
            return result;
        }
    }
    return apply_result::no_match;
}

std::int32_t add_clamped(std::int32_t left, std::int64_t right) noexcept {
    const std::int64_t sum = static_cast<std::int64_t>(left) + right;
    if (sum < std::numeric_limits<std::int32_t>::min()) {
        return std::numeric_limits<std::int32_t>::min();
    }
    if (sum > std::numeric_limits<std::int32_t>::max()) {
        return std::numeric_limits<std::int32_t>::max();
    }
    return static_cast<std::int32_t>(sum);
}

bool resolve_attachment(
    std::size_t index,
    std::span<shaping_glyph> glyphs,
    std::span<const shaping_attachment> attachments,
    shaping_direction direction,
    std::span<std::uint8_t> states,
    std::uint32_t depth) noexcept {
    if (states[index] == 2U) {
        return true;
    }
    if (states[index] == 1U || depth >= 64U) {
        return false;
    }
    states[index] = 1U;
    const shaping_attachment attachment = attachments[index];
    if (attachment.target >= 0 &&
        static_cast<std::size_t>(attachment.target) < glyphs.size()) {
        const std::size_t target = static_cast<std::size_t>(attachment.target);
        if (!resolve_attachment(
                target, glyphs, attachments, direction, states, depth + 1U)) {
            return false;
        }
        shaping_glyph& glyph = glyphs[index];
        const shaping_glyph& parent = glyphs[target];
        if (attachment.kind == shaping_attachment_kind::mark) {
            glyph.offset_x = add_clamped(glyph.offset_x, parent.offset_x);
            glyph.offset_y = add_clamped(glyph.offset_y, parent.offset_y);
            const bool forward = direction == shaping_direction::left_to_right ||
                direction == shaping_direction::top_to_bottom;
            if (target < index) {
                const std::size_t start = forward ? target : target + 1U;
                const std::size_t end = forward ? index : index + 1U;
                const std::int64_t sign = forward ? -1 : 1;
                for (std::size_t advance = start; advance < end; ++advance) {
                    glyph.offset_x = add_clamped(
                        glyph.offset_x, sign * glyphs[advance].advance_x);
                    glyph.offset_y = add_clamped(
                        glyph.offset_y, sign * glyphs[advance].advance_y);
                }
            } else if (target > index) {
                const std::size_t start = forward ? index : index + 1U;
                const std::size_t end = forward ? target : target + 1U;
                const std::int64_t sign = forward ? 1 : -1;
                for (std::size_t advance = start; advance < end; ++advance) {
                    glyph.offset_x = add_clamped(
                        glyph.offset_x, sign * glyphs[advance].advance_x);
                    glyph.offset_y = add_clamped(
                        glyph.offset_y, sign * glyphs[advance].advance_y);
                }
            }
        } else if (attachment.kind ==
            shaping_attachment_kind::cursive_vertical) {
            glyph.offset_x = add_clamped(glyph.offset_x, parent.offset_x);
        } else if (attachment.kind ==
            shaping_attachment_kind::cursive_horizontal) {
            glyph.offset_y = add_clamped(glyph.offset_y, parent.offset_y);
        }
    }
    states[index] = 2U;
    return true;
}

} // namespace

detail::gpos_apply_result detail::apply_gpos_lookup_at(
    const open_type_layout_table_view& gpos,
    std::uint16_t lookup_index,
    std::span<shaping_glyph> glyphs,
    std::size_t position,
    const open_type_gpos_apply_options& options,
    std::uint32_t depth) noexcept {
    return apply_lookup_at(
        gpos, lookup_index, glyphs, position, options, depth);
}

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
    if (lookup.type > 9U || lookup.type == 0U) {
        set_error(error, font_error::none);
        return true;
    }
    if (lookup.type >= 3U && lookup.type <= 6U &&
        options.attachments.size() != glyphs.size()) {
        set_error(error, font_error::invalid_argument);
        return false;
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
                gpos,
                lookup.table,
                lookup.type,
                subtable,
                glyphs,
                position,
                lookup.flags,
                lookup.mark_filtering_set,
                options,
                0U);
            if (result == apply_result::malformed) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            if (result == apply_result::invalid_argument) {
                set_error(error, font_error::invalid_argument);
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

bool try_resolve_open_type_attachments(
    std::span<shaping_glyph> glyphs,
    std::span<const shaping_attachment> attachments,
    shaping_direction direction,
    std::span<std::uint8_t> state_scratch,
    font_error* error) noexcept {
    if (attachments.size() != glyphs.size()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    if (state_scratch.size() < glyphs.size()) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    for (std::size_t index = 0U; index < glyphs.size(); ++index) {
        state_scratch[index] = 0U;
    }
    for (std::size_t index = 0U; index < glyphs.size(); ++index) {
        if (!resolve_attachment(
                index,
                glyphs,
                attachments,
                direction,
                state_scratch,
                0U)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
