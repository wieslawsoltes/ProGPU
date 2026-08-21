#include "progpu_native_text.hpp"
#include "../progpu_native_font_bytes.hpp"

#include <cstddef>
#include <cstdint>

// Direct native port provenance: ProGPU-owned TtfFont.GetAdvanceHeight,
// GetVerticalOriginY, TryGetVorgOrigin, and GetTrueTypeVerticalOrigin at
// repository checkpoint f56a73cd. All table data remains borrowed.

namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_i16;
using detail::read_u16;

constexpr auto vhea_tag = open_type_tag::from_chars('v', 'h', 'e', 'a');
constexpr auto vmtx_tag = open_type_tag::from_chars('v', 'm', 't', 'x');
constexpr auto vorg_tag = open_type_tag::from_chars('V', 'O', 'R', 'G');

std::int32_t floor_half(std::int32_t value) noexcept {
    return value >= 0 ? value / 2 : -((-value + 1) / 2);
}

bool try_get_vorg_origin(
    const sfnt_font_view& font,
    std::uint16_t glyph_index,
    std::int32_t& result) noexcept {
    sfnt_table_view vorg{};
    if (!font.try_get_table(vorg_tag, vorg) || vorg.bytes.size() < 8U) {
        return false;
    }
    const auto default_origin = read_i16(vorg.bytes, 4U);
    const auto count = read_u16(vorg.bytes, 6U);
    if (count > (vorg.bytes.size() - 8U) / 4U) return false;
    std::uint16_t low = 0U;
    std::uint16_t high = count;
    while (low < high) {
        const auto middle = static_cast<std::uint16_t>(
            low + static_cast<std::uint16_t>((high - low) / 2U));
        const auto offset = 8U + static_cast<std::size_t>(middle) * 4U;
        const auto candidate = read_u16(vorg.bytes, offset);
        if (glyph_index < candidate) {
            high = middle;
        } else if (glyph_index > candidate) {
            low = static_cast<std::uint16_t>(middle + 1U);
        } else {
            result = read_i16(vorg.bytes, offset + 2U);
            return true;
        }
    }
    result = default_origin;
    return true;
}

} // namespace

bool sfnt_font_view::try_get_vertical_header_metrics(
    sfnt_vertical_header_metrics& result) const noexcept {
    result = {};
    sfnt_table_view vhea{};
    if (!try_get_table(vhea_tag, vhea) || vhea.bytes.size() < 36U) {
        return false;
    }
    result = sfnt_vertical_header_metrics{
        read_i16(vhea.bytes, 4U),
        read_i16(vhea.bytes, 6U),
        read_i16(vhea.bytes, 8U),
        read_u16(vhea.bytes, 10U),
        read_u16(vhea.bytes, 34U)};
    return true;
}

bool sfnt_font_view::try_get_vertical_glyph_metrics(
    std::uint16_t glyph_index,
    sfnt_vertical_glyph_metrics& result) const noexcept {
    result = {};
    sfnt_vertical_header_metrics header{};
    sfnt_table_view vmtx{};
    if (!try_get_vertical_header_metrics(header) ||
        !try_get_table(vmtx_tag, vmtx) ||
        header.number_of_vertical_metrics == 0U) {
        return false;
    }
    const auto count = header.number_of_vertical_metrics;
    const auto advance_offset = glyph_index < count
        ? static_cast<std::size_t>(glyph_index) * 4U
        : static_cast<std::size_t>(count - 1U) * 4U;
    const auto bearing_offset = glyph_index < count
        ? advance_offset + 2U
        : static_cast<std::size_t>(count) * 4U +
            static_cast<std::size_t>(glyph_index - count) * 2U;
    if (!can_read(vmtx.bytes, advance_offset, 2U)) return false;
    result.advance_height = read_u16(vmtx.bytes, advance_offset);
    if (can_read(vmtx.bytes, bearing_offset, 2U)) {
        result.top_side_bearing = read_i16(vmtx.bytes, bearing_offset);
        result.has_top_side_bearing = true;
    }
    return true;
}

bool sfnt_font_view::try_get_glyph_bounds(
    std::uint16_t glyph_index,
    sfnt_glyph_bounds& result) const noexcept {
    result = {};
    std::uint16_t count = 0U;
    if (!try_get_glyph_count(count) || glyph_index >= count) return false;
    sfnt_glyph_data_view glyph{};
    if (!try_get_glyph_data(glyph_index, glyph) || glyph.empty()) return false;
    result = sfnt_glyph_bounds{
        glyph.x_min, glyph.y_min, glyph.x_max, glyph.y_max};
    return result.x_max > result.x_min && result.y_max > result.y_min;
}

bool sfnt_font_view::try_get_design_advance_height(
    std::uint16_t glyph_index,
    std::int32_t& result) const noexcept {
    result = 0;
    sfnt_vertical_glyph_metrics vertical{};
    if (try_get_vertical_glyph_metrics(glyph_index, vertical)) {
        result = vertical.advance_height;
        return true;
    }
    sfnt_horizontal_header_metrics horizontal{};
    if (!try_get_horizontal_header_metrics(horizontal)) return false;
    result = static_cast<std::int32_t>(horizontal.ascender) -
        horizontal.descender;
    return true;
}

bool sfnt_font_view::try_get_design_vertical_origin_y(
    std::uint16_t glyph_index,
    std::int32_t& result) const noexcept {
    result = 0;
    if (try_get_vorg_origin(*this, glyph_index, result)) return true;

    sfnt_vertical_glyph_metrics vertical{};
    sfnt_glyph_bounds bounds{};
    if (try_get_vertical_glyph_metrics(glyph_index, vertical) &&
        vertical.has_top_side_bearing &&
        try_get_glyph_bounds(glyph_index, bounds)) {
        result = static_cast<std::int32_t>(bounds.y_max) +
            vertical.top_side_bearing;
        return true;
    }

    sfnt_horizontal_header_metrics horizontal{};
    if (!try_get_horizontal_header_metrics(horizontal)) return false;
    if (try_get_glyph_bounds(glyph_index, bounds)) {
        const auto advance = static_cast<std::int32_t>(horizontal.ascender) -
            horizontal.descender;
        const auto height = static_cast<std::int32_t>(bounds.y_max) -
            bounds.y_min;
        result = static_cast<std::int32_t>(bounds.y_max) +
            floor_half(advance - height);
        return true;
    }
    std::uint16_t glyph_count = 0U;
    if (!try_get_glyph_count(glyph_count)) return false;
    result = glyph_index < glyph_count
        ? floor_half(static_cast<std::int32_t>(horizontal.ascender) -
            horizontal.descender)
        : horizontal.ascender;
    return true;
}

} // namespace progpu::native::text
