#ifndef PROGPU_NATIVE_SPACE_FALLBACK_INTERNAL_HPP
#define PROGPU_NATIVE_SPACE_FALLBACK_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

bool try_map_space_fallback(
    const sfnt_font_view& font,
    std::uint32_t code_point,
    std::uint16_t& glyph,
    font_error* error) noexcept;

bool try_apply_space_fallback(
    const sfnt_font_view& font,
    shaping_direction direction,
    std::span<const std::int16_t> normalized_coordinates,
    fallback_mark_positioning_scratch* scratch,
    shaping_glyph& glyph,
    font_error* error) noexcept;

} // namespace progpu::native::text::detail

#endif
