#ifndef PROGPU_NATIVE_CBDT_INTERNAL_HPP
#define PROGPU_NATIVE_CBDT_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

struct cbdt_glyph_metrics final {
    std::uint8_t width = 0U;
    std::uint8_t height = 0U;
    std::int8_t bearing_x = 0;
    std::int8_t bearing_y = 0;

    bool valid() const noexcept {
        return width != 0U && height != 0U;
    }
};

struct cbdt_image_range final {
    std::uint64_t start = 0U;
    std::uint64_t end = 0U;
    cbdt_glyph_metrics index_metrics{};
};

cbdt_glyph_metrics read_small_cbdt_metrics(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept;

cbdt_glyph_metrics read_big_cbdt_metrics(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept;

bool try_resolve_cbdt_image_range(
    std::span<const std::byte> cblc,
    std::size_t subtable_offset,
    std::size_t subtable_limit,
    std::uint16_t first_glyph,
    std::uint16_t last_glyph,
    std::uint16_t glyph_index,
    cbdt_image_range& result) noexcept;

bool try_read_cbdt_image(
    std::span<const std::byte> cbdt,
    cbdt_image_range range,
    std::uint16_t image_format,
    std::uint16_t pixels_per_em,
    std::uint8_t strike_flags,
    sfnt_bitmap_glyph_data_view& result) noexcept;

} // namespace progpu::native::text::detail

#endif
