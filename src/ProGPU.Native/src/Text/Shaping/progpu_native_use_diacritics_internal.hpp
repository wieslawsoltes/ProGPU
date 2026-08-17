#ifndef PROGPU_NATIVE_USE_DIACRITICS_INTERNAL_HPP
#define PROGPU_NATIVE_USE_DIACRITICS_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

bool try_get_use_diacritic_glyph_count(
    std::span<const unicode_scalar> input,
    const unicode_normalization_data& normalization,
    std::uint32_t& result,
    font_error* error) noexcept;

bool try_get_use_diacritic_additions(
    const sfnt_font_view& font,
    const unicode_normalization_data& normalization,
    std::span<const shaping_glyph> glyphs,
    std::size_t& additions,
    font_error* error) noexcept;

bool try_normalize_use_diacritics(
    const sfnt_font_view& font,
    const unicode_normalization_data& normalization,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    font_error* error) noexcept;

} // namespace progpu::native::text::detail

#endif
