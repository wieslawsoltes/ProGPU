#ifndef PROGPU_NATIVE_ARABIC_ACTIONS_INTERNAL_HPP
#define PROGPU_NATIVE_ARABIC_ACTIONS_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

inline constexpr std::uint32_t arabic_action_mask = 0xF0000000U;
inline constexpr std::uint32_t arabic_action_shift = 28U;

inline void set_arabic_action(
    shaping_glyph& glyph,
    open_type_arabic_action action) noexcept {
    const auto flags = static_cast<std::uint32_t>(glyph.flags);
    glyph.flags = static_cast<shaping_glyph_flags>(
        (flags & ~arabic_action_mask) |
        (static_cast<std::uint32_t>(action) << arabic_action_shift));
}

inline open_type_arabic_action get_arabic_action(
    const shaping_glyph& glyph) noexcept {
    return static_cast<open_type_arabic_action>(
        (static_cast<std::uint32_t>(glyph.flags) & arabic_action_mask) >>
        arabic_action_shift);
}

inline void clear_arabic_actions(
    std::span<shaping_glyph> glyphs) noexcept {
    for (auto& glyph : glyphs) {
        glyph.flags = static_cast<shaping_glyph_flags>(
            static_cast<std::uint32_t>(glyph.flags) & ~arabic_action_mask);
    }
}

} // namespace progpu::native::text::detail

#endif
