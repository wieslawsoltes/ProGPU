#ifndef PROGPU_NATIVE_FALLBACK_MARKS_INTERNAL_HPP
#define PROGPU_NATIVE_FALLBACK_MARKS_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

bool try_apply_fallback_mark_positioning_from_attachments(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    shaping_direction direction,
    std::span<const shaping_attachment> metadata,
    std::span<const std::int16_t> normalized_coordinates,
    font_error* error) noexcept;

} // namespace progpu::native::text::detail

#endif
