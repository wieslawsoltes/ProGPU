#ifndef PROGPU_NATIVE_OPEN_TYPE_GSUB_INTERNAL_HPP
#define PROGPU_NATIVE_OPEN_TYPE_GSUB_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <algorithm>
#include <cstdint>

namespace progpu::native::text::detail {

inline constexpr std::uint32_t fallback_ligature_count_mask = 0x000007F8U;
inline constexpr std::uint32_t fallback_ligature_count_shift = 3U;
inline constexpr std::uint32_t fallback_ligature_component_mask = 0x0007F800U;
inline constexpr std::uint32_t fallback_ligature_component_shift = 11U;
inline constexpr std::uint32_t fallback_ligature_metadata_mask =
    fallback_ligature_count_mask | fallback_ligature_component_mask;
inline constexpr std::uint32_t arabic_stretch_multiplied_mask = 0x00080000U;
inline constexpr std::uint32_t arabic_stretch_component_mask = 0x0FF00000U;
inline constexpr std::uint32_t arabic_stretch_component_shift = 20U;
inline constexpr std::uint32_t arabic_stretch_metadata_mask =
    arabic_stretch_multiplied_mask | arabic_stretch_component_mask;

inline std::uint32_t raw_glyph_flags(const shaping_glyph& glyph) noexcept {
    return static_cast<std::uint32_t>(glyph.flags);
}

inline void set_fallback_ligature_count(
    shaping_glyph& glyph,
    std::uint16_t count) noexcept {
    const auto bounded = std::min<std::uint16_t>(count, 0xFFU);
    glyph.flags = static_cast<shaping_glyph_flags>(
        (raw_glyph_flags(glyph) & ~fallback_ligature_count_mask) |
        (static_cast<std::uint32_t>(bounded) <<
            fallback_ligature_count_shift));
}

inline void set_fallback_ligature_component(
    shaping_glyph& glyph,
    std::uint16_t component) noexcept {
    const auto encoded = static_cast<std::uint16_t>(
        std::min<std::uint16_t>(component, 0xFEU) + 1U);
    glyph.flags = static_cast<shaping_glyph_flags>(
        (raw_glyph_flags(glyph) & ~fallback_ligature_component_mask) |
        (static_cast<std::uint32_t>(encoded) <<
            fallback_ligature_component_shift));
}

inline std::uint8_t fallback_ligature_count(
    const shaping_glyph& glyph) noexcept {
    return static_cast<std::uint8_t>(
        (raw_glyph_flags(glyph) & fallback_ligature_count_mask) >>
        fallback_ligature_count_shift);
}

inline std::uint8_t fallback_ligature_component(
    const shaping_glyph& glyph) noexcept {
    const auto encoded = static_cast<std::uint8_t>(
        (raw_glyph_flags(glyph) & fallback_ligature_component_mask) >>
        fallback_ligature_component_shift);
    return encoded == 0U ? 0xFFU : static_cast<std::uint8_t>(encoded - 1U);
}

inline void clear_fallback_ligature_metadata(
    shaping_glyph& glyph) noexcept {
    glyph.flags = static_cast<shaping_glyph_flags>(
        raw_glyph_flags(glyph) & ~fallback_ligature_metadata_mask);
}

inline void set_arabic_stretch_component(
    shaping_glyph& glyph,
    std::uint16_t component) noexcept {
    const auto bounded = std::min<std::uint16_t>(component, 0xFFU);
    glyph.flags = static_cast<shaping_glyph_flags>(
        (raw_glyph_flags(glyph) & ~arabic_stretch_metadata_mask) |
        arabic_stretch_multiplied_mask |
        (static_cast<std::uint32_t>(bounded) <<
            arabic_stretch_component_shift));
}

inline bool is_arabic_stretch_multiplied(
    const shaping_glyph& glyph) noexcept {
    return (raw_glyph_flags(glyph) & arabic_stretch_multiplied_mask) != 0U;
}

inline std::uint8_t arabic_stretch_component(
    const shaping_glyph& glyph) noexcept {
    return static_cast<std::uint8_t>(
        (raw_glyph_flags(glyph) & arabic_stretch_component_mask) >>
        arabic_stretch_component_shift);
}

inline void clear_arabic_stretch_metadata(
    shaping_glyph& glyph) noexcept {
    glyph.flags = static_cast<shaping_glyph_flags>(
        raw_glyph_flags(glyph) & ~arabic_stretch_metadata_mask);
}

} // namespace progpu::native::text::detail

#endif
