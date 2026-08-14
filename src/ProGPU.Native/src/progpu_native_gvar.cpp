#include "progpu_native_text.hpp"

#include "progpu_native_font_bytes.hpp"

#include <cstddef>
#include <limits>

// Direct native port provenance: ProGPU-owned
// OpenTypeVariationData.TryParseGvar/GetGlyphVariation at checkpoint 26da237d.
// The native surface retains borrowed bounded table/glyph slices and exact
// caller-owned shared tuples; tuple payload decoding remains in granular files.

namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_i16;
using detail::read_u16;
using detail::read_u32;

constexpr auto gvar_tag = open_type_tag::from_chars('g', 'v', 'a', 'r');

struct gvar_layout final {
    sfnt_table_view table{};
    sfnt_gvar_header header{};
    std::size_t offsets_start = 0U;
    std::size_t shared_tuples_offset = 0U;
    std::size_t glyph_data_offset = 0U;
};

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool try_get_layout(
    const sfnt_font_view& font,
    gvar_layout& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    sfnt_table_view table{};
    if (!font.try_get_table(gvar_tag, table)) {
        return true;
    }
    if (!can_read(table.bytes, 0U, 20U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto axis_count = read_u16(table.bytes, 4U);
    const auto shared_count = read_u16(table.bytes, 6U);
    const auto shared_offset = read_u32(table.bytes, 8U);
    const auto glyph_count = read_u16(table.bytes, 12U);
    const auto flags = read_u16(table.bytes, 14U);
    const auto data_offset = read_u32(table.bytes, 16U);
    std::uint16_t font_glyph_count = 0U;
    std::uint16_t font_axis_count = 0U;
    if (!font.try_get_glyph_count(font_glyph_count) ||
        !font.try_get_variation_axis_count(font_axis_count, error) ||
        axis_count != font_axis_count || glyph_count != font_glyph_count) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const bool long_offsets = (flags & 1U) != 0U;
    const std::size_t offset_size = long_offsets ? 4U : 2U;
    const auto offset_count = static_cast<std::size_t>(glyph_count) + 1U;
    if (!can_read(table.bytes, 20U, offset_count * offset_size) ||
        (shared_count > 0U &&
            (axis_count == 0U ||
                shared_count >
                    std::numeric_limits<std::size_t>::max() /
                        (static_cast<std::size_t>(axis_count) * 2U))) ||
        !can_read(
            table.bytes,
            shared_offset,
            static_cast<std::size_t>(shared_count) * axis_count * 2U) ||
        data_offset > table.bytes.size()) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    result.table = table;
    result.header = {
        axis_count,
        shared_count,
        glyph_count,
        long_offsets};
    result.offsets_start = 20U;
    result.shared_tuples_offset = shared_offset;
    result.glyph_data_offset = data_offset;
    return true;
}

bool try_read_glyph_offset(
    const gvar_layout& layout,
    std::uint16_t index,
    std::size_t& result) noexcept {
    const std::size_t stride = layout.header.uses_long_offsets ? 4U : 2U;
    const auto offset = layout.offsets_start +
        static_cast<std::size_t>(index) * stride;
    const std::uint32_t relative = layout.header.uses_long_offsets
        ? read_u32(layout.table.bytes, offset)
        : static_cast<std::uint32_t>(
            read_u16(layout.table.bytes, offset)) * 2U;
    if (relative > layout.table.bytes.size() - layout.glyph_data_offset) {
        return false;
    }
    result = layout.glyph_data_offset + relative;
    return true;
}

} // namespace

bool sfnt_font_view::try_get_gvar_header(
    sfnt_gvar_header& result,
    font_error* error) const noexcept {
    gvar_layout layout{};
    if (!try_get_layout(*this, layout, error)) {
        result = {};
        return false;
    }
    result = layout.header;
    return true;
}

bool sfnt_font_view::try_get_glyph_variation_data(
    std::uint16_t glyph_index,
    sfnt_glyph_variation_data_view& result,
    font_error* error) const noexcept {
    result = {};
    gvar_layout layout{};
    if (!try_get_layout(*this, layout, error)) {
        return false;
    }
    if (layout.table.bytes.empty() || glyph_index >= layout.header.glyph_count) {
        set_error(error, layout.table.bytes.empty()
            ? font_error::none
            : font_error::invalid_argument);
        return layout.table.bytes.empty();
    }
    std::size_t start = 0U;
    std::size_t end = 0U;
    if (!try_read_glyph_offset(layout, glyph_index, start) ||
        !try_read_glyph_offset(
            layout,
            static_cast<std::uint16_t>(glyph_index + 1U),
            end) ||
        end < start) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    if (start == end) {
        return true;
    }
    const auto bytes = layout.table.bytes.subspan(start, end - start);
    if (!can_read(bytes, 0U, 4U)) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    const auto count_and_flags = read_u16(bytes, 0U);
    const auto data_offset = read_u16(bytes, 2U);
    if (!can_read(bytes, data_offset, 0U)) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    result.bytes = bytes;
    result.tuple_count = count_and_flags & 0x0FFFU;
    result.serialized_data_offset = data_offset;
    result.has_shared_point_numbers = (count_and_flags & 0x8000U) != 0U;
    return true;
}

bool sfnt_font_view::try_decode_gvar_shared_tuple(
    std::uint16_t tuple_index,
    std::span<std::int16_t> coordinates,
    std::uint16_t& written,
    font_error* error) const noexcept {
    written = 0U;
    gvar_layout layout{};
    if (!try_get_layout(*this, layout, error)) {
        return false;
    }
    if (tuple_index >= layout.header.shared_tuple_count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    if (coordinates.size() < layout.header.axis_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    const auto offset = layout.shared_tuples_offset +
        static_cast<std::size_t>(tuple_index) * layout.header.axis_count * 2U;
    for (std::uint16_t axis = 0U;
         axis < layout.header.axis_count;
         ++axis) {
        coordinates[axis] = read_i16(
            layout.table.bytes,
            offset + static_cast<std::size_t>(axis) * 2U);
    }
    written = layout.header.axis_count;
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
