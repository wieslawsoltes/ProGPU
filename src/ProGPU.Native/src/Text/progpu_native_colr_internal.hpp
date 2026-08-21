#ifndef PROGPU_NATIVE_COLR_INTERNAL_HPP
#define PROGPU_NATIVE_COLR_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

struct colr_layer_range final {
    std::span<const std::byte> colr{};
    std::size_t layer_records_offset = 0U;
    std::uint16_t first_layer = 0U;
    std::uint16_t layer_count = 0U;
};

struct cpal_view final {
    std::span<const std::byte> cpal{};
    std::size_t color_records_offset = 0U;
    std::size_t palette_indices_offset = 0U;
    std::uint16_t entries_per_palette = 0U;
    std::uint16_t palette_count = 0U;
};

bool try_find_colr_layers(
    std::span<const std::byte> colr,
    std::uint16_t glyph_index,
    colr_layer_range& result) noexcept;

bool try_parse_cpal(
    std::span<const std::byte> cpal,
    cpal_view& result) noexcept;

void resolve_cpal_color(
    cpal_view palette,
    std::uint16_t palette_index,
    std::uint16_t entry_index,
    sfnt_color_rgba8& result,
    bool& uses_foreground) noexcept;

} // namespace progpu::native::text::detail

#endif
