#include "progpu_native_text.hpp"

#include <limits>

// Direct native port provenance: ProGPU-owned SfntFontFace.cs at repository
// checkpoint 2f2a92c4286da763d4e4be0908b0f6b706a86c3f. This file preserves
// its SFNT/TTC, table, metrics, and cmap contracts using a borrowed C++ view;
// no third-party implementation source or structure is used.

namespace progpu::native::text {
namespace {

constexpr auto collection_tag = open_type_tag::from_chars('t', 't', 'c', 'f');
constexpr auto woff1_tag = open_type_tag::from_chars('w', 'O', 'F', 'F');
constexpr auto woff2_tag = open_type_tag::from_chars('w', 'O', 'F', '2');
constexpr auto cmap_tag = open_type_tag::from_chars('c', 'm', 'a', 'p');
constexpr auto head_tag = open_type_tag::from_chars('h', 'e', 'a', 'd');
constexpr auto hhea_tag = open_type_tag::from_chars('h', 'h', 'e', 'a');
constexpr auto hmtx_tag = open_type_tag::from_chars('h', 'm', 't', 'x');
constexpr auto maxp_tag = open_type_tag::from_chars('m', 'a', 'x', 'p');

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool can_read(
    std::span<const std::byte> data,
    std::size_t offset,
    std::size_t length) noexcept {
    return offset <= data.size() && length <= data.size() - offset;
}

std::uint16_t read_u16(
    std::span<const std::byte> data,
    std::size_t offset) noexcept {
    return static_cast<std::uint16_t>(
        (std::to_integer<std::uint16_t>(data[offset]) << 8U) |
        std::to_integer<std::uint16_t>(data[offset + 1U]));
}

std::int16_t read_i16(
    std::span<const std::byte> data,
    std::size_t offset) noexcept {
    return static_cast<std::int16_t>(read_u16(data, offset));
}

std::uint32_t read_u32(
    std::span<const std::byte> data,
    std::size_t offset) noexcept {
    return
        (std::to_integer<std::uint32_t>(data[offset]) << 24U) |
        (std::to_integer<std::uint32_t>(data[offset + 1U]) << 16U) |
        (std::to_integer<std::uint32_t>(data[offset + 2U]) << 8U) |
        std::to_integer<std::uint32_t>(data[offset + 3U]);
}

bool is_unicode_cmap(
    std::uint16_t platform_id,
    std::uint16_t encoding_id) noexcept {
    return platform_id == 0U ||
        (platform_id == 3U &&
            (encoding_id == 1U || encoding_id == 10U));
}

std::span<const std::byte> cmap_subtable(
    std::span<const std::byte> cmap,
    std::uint32_t offset,
    std::uint16_t format) noexcept {
    const auto subtable_offset = static_cast<std::size_t>(offset);
    if (format == 4U) {
        if (!can_read(cmap, subtable_offset, 4U)) {
            return {};
        }
        const auto length = read_u16(cmap, subtable_offset + 2U);
        return length > 0U && can_read(cmap, subtable_offset, length)
            ? cmap.subspan(subtable_offset, length)
            : std::span<const std::byte>{};
    }
    if (format == 12U || format == 13U) {
        if (!can_read(cmap, subtable_offset, 8U)) {
            return {};
        }
        const auto length = read_u32(cmap, subtable_offset + 4U);
        return length > 0U && can_read(cmap, subtable_offset, length)
            ? cmap.subspan(subtable_offset, length)
            : std::span<const std::byte>{};
    }
    return {};
}

bool lookup_format12_or_13(
    std::span<const std::byte> table,
    std::uint32_t code_point,
    bool constant_glyph,
    std::uint16_t& result) noexcept {
    result = 0U;
    if (table.size() < 16U) {
        return false;
    }
    const auto group_count = read_u32(table, 12U);
    if (group_count > (table.size() - 16U) / 12U) {
        return false;
    }
    std::uint32_t low = 0U;
    std::uint32_t high = group_count;
    while (low < high) {
        const auto middle = low + ((high - low) / 2U);
        const auto offset = 16U + static_cast<std::size_t>(middle) * 12U;
        const auto start = read_u32(table, offset);
        const auto end = read_u32(table, offset + 4U);
        if (code_point < start) {
            high = middle;
        } else if (code_point > end) {
            low = middle + 1U;
        } else {
            auto glyph = read_u32(table, offset + 8U);
            if (!constant_glyph) {
                const auto delta = code_point - start;
                if (glyph > std::numeric_limits<std::uint32_t>::max() - delta) {
                    return false;
                }
                glyph += delta;
            }
            result = glyph <= std::numeric_limits<std::uint16_t>::max()
                ? static_cast<std::uint16_t>(glyph)
                : 0U;
            return true;
        }
    }
    return true;
}

bool lookup_format4(
    std::span<const std::byte> table,
    std::uint32_t code_point,
    std::uint16_t& result) noexcept {
    result = 0U;
    if (table.size() < 14U ||
        code_point > std::numeric_limits<std::uint16_t>::max()) {
        return false;
    }
    const auto segment_count = read_u16(table, 6U) / 2U;
    if (segment_count == 0U) {
        return false;
    }
    const std::size_t end_code_offset = 14U;
    const auto start_code_offset =
        end_code_offset + static_cast<std::size_t>(segment_count) * 2U + 2U;
    const auto delta_offset =
        start_code_offset + static_cast<std::size_t>(segment_count) * 2U;
    const auto range_offset =
        delta_offset + static_cast<std::size_t>(segment_count) * 2U;
    if (!can_read(
            table,
            range_offset,
            static_cast<std::size_t>(segment_count) * 2U)) {
        return false;
    }

    const auto code = static_cast<std::uint16_t>(code_point);
    std::uint16_t segment = 0U;
    while (segment < segment_count &&
        read_u16(table, end_code_offset + segment * 2U) < code) {
        ++segment;
    }
    if (segment == segment_count ||
        read_u16(table, start_code_offset + segment * 2U) > code) {
        return true;
    }

    const auto delta = read_i16(table, delta_offset + segment * 2U);
    const auto glyph_range = read_u16(table, range_offset + segment * 2U);
    if (glyph_range == 0U) {
        result = static_cast<std::uint16_t>(code + delta);
        return true;
    }
    const auto range_address = range_offset + segment * 2U;
    const auto start = read_u16(table, start_code_offset + segment * 2U);
    const auto glyph_address = range_address + glyph_range +
        static_cast<std::size_t>(code - start) * 2U;
    if (!can_read(table, glyph_address, 2U)) {
        return true;
    }
    const auto raw = read_u16(table, glyph_address);
    result = raw == 0U
        ? 0U
        : static_cast<std::uint16_t>(raw + delta);
    return true;
}

bool lookup_selected_cmap(
    std::span<const std::byte> format4,
    std::span<const std::byte> format12,
    std::span<const std::byte> format13,
    std::uint32_t code_point,
    std::uint16_t& result) noexcept {
    result = 0U;
    if (!format12.empty() &&
        lookup_format12_or_13(format12, code_point, false, result) &&
        (result != 0U || format4.empty())) {
        return true;
    }
    if (!format13.empty() &&
        lookup_format12_or_13(format13, code_point, true, result) &&
        (result != 0U || format4.empty())) {
        return true;
    }
    if (!format4.empty() && lookup_format4(format4, code_point, result)) {
        return true;
    }
    result = 0U;
    return true;
}

} // namespace

bool sfnt_font_view::try_get_face_count(
    std::span<const std::byte> data,
    std::uint32_t& face_count,
    font_error* error) noexcept {
    face_count = 0U;
    set_error(error, font_error::none);
    if (data.size() < 12U) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto signature = read_u32(data, 0U);
    if (signature == woff1_tag.value || signature == woff2_tag.value) {
        set_error(error, font_error::unsupported_container);
        return false;
    }
    if (signature != collection_tag.value) {
        face_count = 1U;
        return true;
    }
    face_count = read_u32(data, 8U);
    if (face_count == 0U || face_count > (data.size() - 12U) / 4U) {
        face_count = 0U;
        set_error(error, font_error::invalid_collection);
        return false;
    }
    return true;
}

bool sfnt_font_view::try_create(
    std::span<const std::byte> data,
    std::uint32_t face_index,
    sfnt_font_view& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    std::uint32_t face_count = 0U;
    if (!try_get_face_count(data, face_count, error)) {
        return false;
    }
    if (face_index >= face_count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    const auto is_collection = read_u32(data, 0U) == collection_tag.value;
    const auto face_offset = is_collection
        ? read_u32(data, 12U + static_cast<std::size_t>(face_index) * 4U)
        : 0U;
    if (!can_read(data, face_offset, 12U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto face_offset_value = static_cast<std::size_t>(face_offset);
    const auto table_count = read_u16(data, face_offset_value + 4U);
    const auto directory_offset = face_offset_value + 12U;
    if (!can_read(
            data,
            directory_offset,
            static_cast<std::size_t>(table_count) * 16U)) {
        set_error(error, font_error::truncated_directory);
        return false;
    }

    result.data_ = data;
    result.face_index_ = face_index;
    result.face_offset_ = face_offset;
    result.directory_offset_ = directory_offset;
    result.table_count_ = table_count;

    sfnt_table_view cmap{};
    if (!result.try_get_table(cmap_tag, cmap) || cmap.bytes.size() < 4U) {
        return true;
    }
    std::span<const std::byte> symbol_format4{};
    const auto record_count = read_u16(cmap.bytes, 2U);
    for (std::uint16_t index = 0U; index < record_count; ++index) {
        const auto record_offset = 4U + static_cast<std::size_t>(index) * 8U;
        if (!can_read(cmap.bytes, record_offset, 8U)) {
            break;
        }
        const auto platform_id = read_u16(cmap.bytes, record_offset);
        const auto encoding_id = read_u16(cmap.bytes, record_offset + 2U);
        const auto subtable_offset = read_u32(cmap.bytes, record_offset + 4U);
        if (!can_read(cmap.bytes, subtable_offset, 2U)) {
            continue;
        }
        const auto format = read_u16(cmap.bytes, subtable_offset);
        const auto subtable = cmap_subtable(
            cmap.bytes,
            subtable_offset,
            format);
        if (subtable.empty()) {
            continue;
        }
        if (format == 12U && is_unicode_cmap(platform_id, encoding_id)) {
            result.cmap_format12_ = subtable;
        } else if (
            format == 13U && is_unicode_cmap(platform_id, encoding_id)) {
            result.cmap_format13_ = subtable;
        } else if (
            format == 4U && is_unicode_cmap(platform_id, encoding_id)) {
            result.cmap_format4_ = subtable;
            result.uses_symbol_character_map_ = false;
        } else if (
            format == 4U && platform_id == 3U && encoding_id == 0U) {
            symbol_format4 = subtable;
        }
    }
    if (!symbol_format4.empty()) {
        result.cmap_format4_ = symbol_format4;
        result.cmap_format12_ = {};
        result.cmap_format13_ = {};
        result.uses_symbol_character_map_ = true;
    }
    return true;
}

bool sfnt_font_view::try_get_table(
    open_type_tag tag,
    sfnt_table_view& result) const noexcept {
    result = {};
    for (std::uint16_t index = table_count_; index > 0U; --index) {
        const auto offset = static_cast<std::size_t>(directory_offset_) +
            static_cast<std::size_t>(index - 1U) * 16U;
        if (read_u32(data_, offset) != tag.value) {
            continue;
        }
        const auto checksum = read_u32(data_, offset + 4U);
        const auto table_offset = read_u32(data_, offset + 8U);
        const auto table_length = read_u32(data_, offset + 12U);
        if (!can_read(data_, table_offset, table_length)) {
            continue;
        }
        result = sfnt_table_view{
            tag,
            checksum,
            data_.subspan(table_offset, table_length)};
        return true;
    }
    return false;
}

bool sfnt_font_view::try_get_header_metrics(
    sfnt_header_metrics& result) const noexcept {
    result = {};
    sfnt_table_view table{};
    if (!try_get_table(head_tag, table) || table.bytes.size() < 52U) {
        return false;
    }
    result = sfnt_header_metrics{
        read_u16(table.bytes, 18U),
        read_i16(table.bytes, 36U),
        read_i16(table.bytes, 38U),
        read_i16(table.bytes, 40U),
        read_i16(table.bytes, 42U),
        read_i16(table.bytes, 50U)};
    return true;
}

bool sfnt_font_view::try_get_horizontal_header_metrics(
    sfnt_horizontal_header_metrics& result) const noexcept {
    result = {};
    sfnt_table_view table{};
    if (!try_get_table(hhea_tag, table) || table.bytes.size() < 36U) {
        return false;
    }
    result = sfnt_horizontal_header_metrics{
        read_i16(table.bytes, 4U),
        read_i16(table.bytes, 6U),
        read_i16(table.bytes, 8U),
        read_u16(table.bytes, 10U),
        read_u16(table.bytes, 34U)};
    return true;
}

bool sfnt_font_view::try_get_horizontal_glyph_metrics(
    std::uint16_t glyph_index,
    sfnt_horizontal_glyph_metrics& result) const noexcept {
    result = {};
    sfnt_horizontal_header_metrics header{};
    sfnt_table_view hmtx{};
    if (!try_get_horizontal_header_metrics(header) ||
        !try_get_table(hmtx_tag, hmtx)) {
        return false;
    }
    const auto count = header.number_of_horizontal_metrics;
    if (count == 0U) {
        return false;
    }
    const auto advance_offset = glyph_index < count
        ? static_cast<std::size_t>(glyph_index) * 4U
        : static_cast<std::size_t>(count - 1U) * 4U;
    const auto bearing_offset = glyph_index < count
        ? advance_offset + 2U
        : static_cast<std::size_t>(count) * 4U +
            static_cast<std::size_t>(glyph_index - count) * 2U;
    if (!can_read(hmtx.bytes, advance_offset, 2U)) {
        return false;
    }
    result.advance_width = read_u16(hmtx.bytes, advance_offset);
    result.left_side_bearing = can_read(hmtx.bytes, bearing_offset, 2U)
        ? read_i16(hmtx.bytes, bearing_offset)
        : 0;
    return true;
}

bool sfnt_font_view::try_get_glyph_count(std::uint16_t& result) const noexcept {
    result = 0U;
    sfnt_table_view table{};
    if (!try_get_table(maxp_tag, table) || table.bytes.size() < 6U) {
        return false;
    }
    result = read_u16(table.bytes, 4U);
    return true;
}

bool sfnt_font_view::try_get_glyph_index(
    std::uint32_t code_point,
    std::uint16_t& result) const noexcept {
    result = 0U;
    if (cmap_format4_.empty() &&
        cmap_format12_.empty() &&
        cmap_format13_.empty()) {
        return false;
    }
    if (lookup_selected_cmap(
            cmap_format4_,
            cmap_format12_,
            cmap_format13_,
            code_point,
            result) && result != 0U) {
        return true;
    }
    if (uses_symbol_character_map_ && code_point <= 0xFFU) {
        return lookup_selected_cmap(
            cmap_format4_,
            cmap_format12_,
            cmap_format13_,
            0xF000U + code_point,
            result);
    }
    result = 0U;
    return true;
}

std::span<const std::byte> sfnt_font_view::data() const noexcept {
    return data_;
}

std::uint32_t sfnt_font_view::face_index() const noexcept {
    return face_index_;
}

std::uint32_t sfnt_font_view::face_offset() const noexcept {
    return face_offset_;
}

std::uint16_t sfnt_font_view::table_count() const noexcept {
    return table_count_;
}

bool sfnt_font_view::uses_symbol_character_map() const noexcept {
    return uses_symbol_character_map_;
}

} // namespace progpu::native::text
