#ifndef PROGPU_NATIVE_VOWEL_CONSTRAINTS_INTERNAL_HPP
#define PROGPU_NATIVE_VOWEL_CONSTRAINTS_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

bool has_vowel_constraints(open_type_tag script) noexcept;

std::size_t count_vowel_constraint_insertions(
    open_type_tag script,
    std::span<const shaping_glyph> glyphs) noexcept;

bool try_apply_vowel_constraints(
    const sfnt_font_view& font,
    open_type_tag script,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    font_error* error) noexcept;

} // namespace progpu::native::text::detail

#endif
