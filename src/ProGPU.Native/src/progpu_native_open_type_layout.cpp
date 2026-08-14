#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct native port of ProGPU-owned FindCoverage/GetGlyphClass and lazy raw
// GSUB/GPOS lookup access in OpenTypeTextShaper.cs at checkpoint 89d610c2.

namespace progpu::native::text {
namespace {

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

bool try_multiply(
    std::size_t left,
    std::size_t right,
    std::size_t& result) noexcept {
    if (left != 0U && right > std::numeric_limits<std::size_t>::max() / left) {
        return false;
    }
    result = left * right;
    return true;
}

std::uint16_t read_u16(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return static_cast<std::uint16_t>(
        (std::to_integer<std::uint16_t>(bytes[offset]) << 8U) |
        std::to_integer<std::uint16_t>(bytes[offset + 1U]));
}

std::uint32_t read_u32(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return (static_cast<std::uint32_t>(read_u16(bytes, offset)) << 16U) |
        read_u16(bytes, offset + 2U);
}

bool validate_record_array(
    std::span<const std::byte> table,
    std::size_t offset,
    std::size_t stride) noexcept {
    if (!can_read(table, offset, 2U)) {
        return false;
    }
    std::size_t bytes = 0U;
    std::size_t records = 0U;
    return try_multiply(read_u16(table, offset), stride, bytes) &&
        try_add(offset, 2U, records) && can_read(table, records, bytes);
}

} // namespace

bool open_type_coverage_view::try_create(
    std::span<const std::byte> table,
    std::size_t offset,
    open_type_coverage_view& result,
    font_error* error) noexcept {
    result = {};
    if (!can_read(table, offset, 4U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const std::uint16_t format = read_u16(table, offset);
    const std::uint16_t count = read_u16(table, offset + 2U);
    std::size_t payload = 0U;
    std::size_t payload_offset = 0U;
    if (!try_add(offset, 4U, payload_offset)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    if (format == 1U) {
        if (!try_multiply(count, 2U, payload) ||
            !can_read(table, payload_offset, payload)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        std::uint16_t previous = 0U;
        for (std::uint16_t index = 0U; index < count; ++index) {
            const std::uint16_t glyph =
                read_u16(table, payload_offset + index * 2U);
            if (index != 0U && glyph <= previous) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            previous = glyph;
        }
    } else if (format == 2U) {
        if (!try_multiply(count, 6U, payload) ||
            !can_read(table, payload_offset, payload)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        std::uint32_t expected_coverage = 0U;
        std::uint16_t previous_end = 0U;
        for (std::uint16_t index = 0U; index < count; ++index) {
            const std::size_t range = payload_offset + index * 6U;
            const std::uint16_t start = read_u16(table, range);
            const std::uint16_t end = read_u16(table, range + 2U);
            const std::uint16_t coverage = read_u16(table, range + 4U);
            if (start > end || (index != 0U && start <= previous_end) ||
                coverage != expected_coverage) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            expected_coverage += static_cast<std::uint32_t>(end) - start + 1U;
            if (expected_coverage > 0x10000U) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            previous_end = end;
        }
    } else {
        set_error(error, font_error::invalid_face);
        return false;
    }
    result.table_ = table;
    result.offset_ = offset;
    result.count_ = count;
    result.format_ = format;
    set_error(error, font_error::none);
    return true;
}

std::int32_t open_type_coverage_view::find(
    std::uint16_t glyph_id) const noexcept {
    std::uint32_t low = 0U;
    std::uint32_t high = count_;
    const std::size_t payload = offset_ + 4U;
    if (format_ == 1U) {
        while (low < high) {
            const std::uint32_t middle = low + (high - low) / 2U;
            const std::uint16_t current =
                read_u16(table_, payload + middle * 2U);
            if (glyph_id < current) {
                high = middle;
            } else if (glyph_id > current) {
                low = middle + 1U;
            } else {
                return static_cast<std::int32_t>(middle);
            }
        }
        return -1;
    }
    if (format_ == 2U) {
        while (low < high) {
            const std::uint32_t middle = low + (high - low) / 2U;
            const std::size_t range = payload + middle * 6U;
            const std::uint16_t start = read_u16(table_, range);
            const std::uint16_t end = read_u16(table_, range + 2U);
            if (glyph_id < start) {
                high = middle;
            } else if (glyph_id > end) {
                low = middle + 1U;
            } else {
                return static_cast<std::int32_t>(
                    read_u16(table_, range + 4U) + glyph_id - start);
            }
        }
    }
    return -1;
}

bool open_type_class_definition_view::try_create(
    std::span<const std::byte> table,
    std::size_t offset,
    open_type_class_definition_view& result,
    font_error* error) noexcept {
    result = {};
    if (!can_read(table, offset, 4U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const std::uint16_t format = read_u16(table, offset);
    std::uint16_t count = 0U;
    std::uint16_t start_glyph = 0U;
    if (format == 1U) {
        if (!can_read(table, offset, 6U)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        start_glyph = read_u16(table, offset + 2U);
        count = read_u16(table, offset + 4U);
        const std::uint32_t end =
            static_cast<std::uint32_t>(start_glyph) + count;
        if (end > 0x10000U || !can_read(table, offset + 6U, count * 2U)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
    } else if (format == 2U) {
        count = read_u16(table, offset + 2U);
        if (!can_read(table, offset + 4U, count * 6U)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        std::uint16_t previous_end = 0U;
        for (std::uint16_t index = 0U; index < count; ++index) {
            const std::size_t range = offset + 4U + index * 6U;
            const std::uint16_t start = read_u16(table, range);
            const std::uint16_t end = read_u16(table, range + 2U);
            if (start > end || (index != 0U && start <= previous_end)) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            previous_end = end;
        }
    } else {
        set_error(error, font_error::invalid_face);
        return false;
    }
    result.table_ = table;
    result.offset_ = offset;
    result.count_ = count;
    result.start_glyph_ = start_glyph;
    result.format_ = format;
    set_error(error, font_error::none);
    return true;
}

std::uint16_t open_type_class_definition_view::get(
    std::uint16_t glyph_id) const noexcept {
    if (format_ == 1U) {
        const std::uint32_t index =
            static_cast<std::uint32_t>(glyph_id) - start_glyph_;
        return index < count_
            ? read_u16(table_, offset_ + 6U + index * 2U)
            : 0U;
    }
    if (format_ == 2U) {
        std::uint32_t low = 0U;
        std::uint32_t high = count_;
        while (low < high) {
            const std::uint32_t middle = low + (high - low) / 2U;
            const std::size_t range = offset_ + 4U + middle * 6U;
            const std::uint16_t start = read_u16(table_, range);
            const std::uint16_t end = read_u16(table_, range + 2U);
            if (glyph_id < start) {
                high = middle;
            } else if (glyph_id > end) {
                low = middle + 1U;
            } else {
                return read_u16(table_, range + 4U);
            }
        }
    }
    return 0U;
}

bool open_type_lookup_view::try_get_subtable(
    std::uint16_t index,
    std::size_t& subtable_offset,
    font_error* error) const noexcept {
    subtable_offset = 0U;
    if (index >= subtable_count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    const std::size_t record = offset + 6U + index * 2U;
    const std::uint16_t relative = read_u16(table, record);
    if (relative == 0U || !try_add(offset, relative, subtable_offset) ||
        !can_read(table, subtable_offset, 2U)) {
        subtable_offset = 0U;
        set_error(error, font_error::invalid_face);
        return false;
    }
    set_error(error, font_error::none);
    return true;
}

bool open_type_layout_table_view::try_create(
    std::span<const std::byte> table,
    open_type_layout_table_view& result,
    font_error* error) noexcept {
    result = {};
    if (!can_read(table, 0U, 10U) || read_u16(table, 0U) != 1U) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const std::uint16_t minor = read_u16(table, 2U);
    if (minor > 1U || (minor == 1U && !can_read(table, 0U, 14U))) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const std::size_t script_list = read_u16(table, 4U);
    const std::size_t feature_list = read_u16(table, 6U);
    const std::size_t lookup_list = read_u16(table, 8U);
    const std::size_t feature_variations = minor == 1U
        ? read_u32(table, 10U)
        : 0U;
    if (script_list == 0U || feature_list == 0U || lookup_list == 0U ||
        !validate_record_array(table, script_list, 6U) ||
        !validate_record_array(table, feature_list, 6U) ||
        !validate_record_array(table, lookup_list, 2U) ||
        (feature_variations != 0U &&
            !can_read(table, feature_variations, 8U))) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    result.table_ = table;
    result.script_list_offset_ = script_list;
    result.feature_list_offset_ = feature_list;
    result.lookup_list_offset_ = lookup_list;
    result.feature_variations_offset_ = feature_variations;
    result.lookup_count_ = read_u16(table, lookup_list);
    set_error(error, font_error::none);
    return true;
}

bool open_type_layout_table_view::try_get_lookup(
    std::uint16_t index,
    open_type_lookup_view& result,
    font_error* error) const noexcept {
    result = {};
    if (index >= lookup_count_) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    const std::size_t record = lookup_list_offset_ + 2U + index * 2U;
    const std::uint16_t relative = read_u16(table_, record);
    std::size_t lookup = 0U;
    if (relative == 0U || !try_add(lookup_list_offset_, relative, lookup) ||
        !can_read(table_, lookup, 6U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const std::uint16_t type = read_u16(table_, lookup);
    const std::uint16_t flags = read_u16(table_, lookup + 2U);
    const std::uint16_t subtable_count = read_u16(table_, lookup + 4U);
    std::size_t subtable_bytes = 0U;
    if (!try_multiply(subtable_count, 2U, subtable_bytes) ||
        !can_read(table_, lookup + 6U, subtable_bytes)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    std::uint16_t mark_filtering_set = 0xFFFFU;
    if ((flags & 0x0010U) != 0U) {
        const std::size_t mark_offset = lookup + 6U + subtable_bytes;
        if (!can_read(table_, mark_offset, 2U)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        mark_filtering_set = read_u16(table_, mark_offset);
    }
    for (std::uint16_t subtable = 0U;
         subtable < subtable_count;
         ++subtable) {
        const std::uint16_t subtable_relative =
            read_u16(table_, lookup + 6U + subtable * 2U);
        std::size_t subtable_offset = 0U;
        if (subtable_relative == 0U ||
            !try_add(lookup, subtable_relative, subtable_offset) ||
            !can_read(table_, subtable_offset, 2U)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
    }
    result = open_type_lookup_view{
        table_,
        lookup,
        type,
        flags,
        subtable_count,
        mark_filtering_set};
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
