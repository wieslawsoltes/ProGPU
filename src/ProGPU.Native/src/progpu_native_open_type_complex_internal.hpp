#ifndef PROGPU_NATIVE_OPEN_TYPE_COMPLEX_INTERNAL_HPP
#define PROGPU_NATIVE_OPEN_TYPE_COMPLEX_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstdint>
#include <span>

namespace progpu::native::text::complex_detail {

inline constexpr std::uint32_t category_mask = 0x000001F8U;
inline constexpr std::uint32_t category_shift = 3U;
inline constexpr std::uint32_t position_mask = 0x00001E00U;
inline constexpr std::uint32_t position_shift = 9U;
inline constexpr std::uint32_t syllable_mask = 0x001FE000U;
inline constexpr std::uint32_t syllable_shift = 13U;
inline constexpr std::uint32_t feature_mask = 0x0FE00000U;
inline constexpr std::uint32_t feature_shift = 21U;
inline constexpr std::uint32_t metadata_mask =
    category_mask | position_mask | syllable_mask | feature_mask;

inline std::uint32_t raw_flags(const shaping_glyph& glyph) noexcept {
    return static_cast<std::uint32_t>(glyph.flags);
}

inline void set_field(
    shaping_glyph& glyph,
    std::uint32_t mask,
    std::uint32_t shift,
    std::uint8_t value) noexcept {
    glyph.flags = static_cast<shaping_glyph_flags>(
        (raw_flags(glyph) & ~mask) |
        ((static_cast<std::uint32_t>(value) << shift) & mask));
}

inline std::uint8_t get_field(
    const shaping_glyph& glyph,
    std::uint32_t mask,
    std::uint32_t shift) noexcept {
    return static_cast<std::uint8_t>((raw_flags(glyph) & mask) >> shift);
}

inline void set_category(shaping_glyph& glyph, std::uint8_t value) noexcept {
    set_field(glyph, category_mask, category_shift, value);
}

inline std::uint8_t category(const shaping_glyph& glyph) noexcept {
    return get_field(glyph, category_mask, category_shift);
}

inline void set_position(shaping_glyph& glyph, std::uint8_t value) noexcept {
    set_field(glyph, position_mask, position_shift, value);
}

inline std::uint8_t position(const shaping_glyph& glyph) noexcept {
    return get_field(glyph, position_mask, position_shift);
}

inline void set_syllable(shaping_glyph& glyph, std::uint8_t value) noexcept {
    set_field(glyph, syllable_mask, syllable_shift, value);
}

inline std::uint8_t syllable(const shaping_glyph& glyph) noexcept {
    return get_field(glyph, syllable_mask, syllable_shift);
}

inline void add_feature(shaping_glyph& glyph, std::uint8_t value) noexcept {
    const auto current = get_field(glyph, feature_mask, feature_shift);
    set_field(glyph, feature_mask, feature_shift,
        static_cast<std::uint8_t>(current | value));
}

inline void clear_metadata(std::span<shaping_glyph> glyphs) noexcept {
    for (auto& glyph : glyphs) {
        glyph.flags = static_cast<shaping_glyph_flags>(
            raw_flags(glyph) & ~metadata_mask);
    }
}

bool try_prepare_khmer(
    const sfnt_font_view& font,
    shaping_buffer_flags buffer_flags,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    std::span<std::uint8_t> category_scratch,
    std::span<std::uint8_t> syllable_scratch,
    font_error* error) noexcept;

bool try_prepare_myanmar(
    const sfnt_font_view& font,
    shaping_buffer_flags buffer_flags,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    std::span<std::uint8_t> category_scratch,
    std::span<std::uint8_t> syllable_scratch,
    font_error* error) noexcept;

bool try_prepare_use(
    const sfnt_font_view& font,
    shaping_buffer_flags buffer_flags,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    std::span<std::uint8_t> category_scratch,
    std::span<std::uint8_t> syllable_scratch,
    std::span<std::uint32_t> index_scratch,
    font_error* error) noexcept;

} // namespace progpu::native::text::complex_detail

#endif
