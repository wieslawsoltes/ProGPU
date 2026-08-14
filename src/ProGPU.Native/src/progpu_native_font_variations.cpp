#include "progpu_native_text.hpp"

#include "progpu_native_font_bytes.hpp"

#include <cstddef>

// Direct native port provenance: ProGPU-owned
// OpenTypeVariationData.ParseAxes at repository checkpoint e94630ef. This
// translation unit preserves its bounded fvar range and fixed-coordinate
// contracts without porting foreign font-engine implementation source.

namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_i32;
using detail::read_u16;
using detail::read_u32;

constexpr auto fvar_tag = open_type_tag::from_chars('f', 'v', 'a', 'r');

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool try_get_axis_layout(
    const sfnt_font_view& font,
    sfnt_table_view& table,
    std::uint16_t& count,
    std::uint16_t& size,
    std::size_t& axes_offset,
    font_error* error) noexcept {
    table = {};
    count = 0U;
    size = 0U;
    axes_offset = 0U;
    set_error(error, font_error::none);
    if (!font.try_get_table(fvar_tag, table)) {
        return true;
    }
    if (!can_read(table.bytes, 0U, 16U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    axes_offset = read_u16(table.bytes, 4U);
    count = read_u16(table.bytes, 8U);
    size = read_u16(table.bytes, 10U);
    if (count == 0U || size < 20U) {
        count = 0U;
        return true;
    }
    if (axes_offset > table.bytes.size() ||
        count > (table.bytes.size() - axes_offset) / size) {
        count = 0U;
        set_error(error, font_error::invalid_face);
        return false;
    }
    return true;
}

} // namespace

float sfnt_variation_axis::minimum() const noexcept {
    return static_cast<float>(minimum_fixed) / 65536.0F;
}

float sfnt_variation_axis::default_value() const noexcept {
    return static_cast<float>(default_fixed) / 65536.0F;
}

float sfnt_variation_axis::maximum() const noexcept {
    return static_cast<float>(maximum_fixed) / 65536.0F;
}

bool sfnt_variation_axis::hidden() const noexcept {
    return (flags & 1U) != 0U;
}

bool sfnt_font_view::try_get_variation_axis_count(
    std::uint16_t& result,
    font_error* error) const noexcept {
    sfnt_table_view table{};
    std::uint16_t size = 0U;
    std::size_t offset = 0U;
    return try_get_axis_layout(
        *this,
        table,
        result,
        size,
        offset,
        error);
}

bool sfnt_font_view::try_decode_variation_axes(
    std::span<sfnt_variation_axis> axes,
    std::uint16_t& written,
    font_error* error) const noexcept {
    written = 0U;
    sfnt_table_view table{};
    std::uint16_t count = 0U;
    std::uint16_t size = 0U;
    std::size_t axes_offset = 0U;
    if (!try_get_axis_layout(
            *this,
            table,
            count,
            size,
            axes_offset,
            error)) {
        return false;
    }
    if (axes.size() < count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    for (std::uint16_t index = 0U; index < count; ++index) {
        const auto offset = axes_offset +
            static_cast<std::size_t>(index) * size;
        axes[index] = {
            {read_u32(table.bytes, offset)},
            read_i32(table.bytes, offset + 4U),
            read_i32(table.bytes, offset + 8U),
            read_i32(table.bytes, offset + 12U),
            read_u16(table.bytes, offset + 16U),
            read_u16(table.bytes, offset + 18U)};
    }
    written = count;
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
