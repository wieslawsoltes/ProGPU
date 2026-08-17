#ifndef PROGPU_NATIVE_OPEN_TYPE_PROJECTION_INTERNAL_HPP
#define PROGPU_NATIVE_OPEN_TYPE_PROJECTION_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

bool try_project_open_type_shape_glyph(
    const sfnt_font_view& font,
    std::span<const std::int16_t> normalized_coordinates,
    const shaping_glyph& glyph,
    float scale,
    shaping_direction direction,
    open_type_shaped_glyph& result,
    sfnt_glyph_phantom_variation_scratch* advance_scratch,
    font_error* error) noexcept;

} // namespace progpu::native::text::detail

#endif
