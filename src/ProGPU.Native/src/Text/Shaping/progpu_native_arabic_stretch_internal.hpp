#ifndef PROGPU_NATIVE_ARABIC_STRETCH_INTERNAL_HPP
#define PROGPU_NATIVE_ARABIC_STRETCH_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

bool try_apply_arabic_stretch_from_glyph_actions(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    bool right_to_left,
    std::span<const std::int16_t> normalized_coordinates,
    std::span<arabic_stretch_run> run_scratch,
    font_error* error) noexcept;

} // namespace progpu::native::text::detail

#endif
