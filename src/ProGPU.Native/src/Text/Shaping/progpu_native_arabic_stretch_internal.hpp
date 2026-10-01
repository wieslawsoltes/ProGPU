#ifndef PROGPU_NATIVE_ARABIC_STRETCH_INTERNAL_HPP
#define PROGPU_NATIVE_ARABIC_STRETCH_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

struct device_stretch_metrics final {
    const void* owner = nullptr;
    bool (*get_advance)(const void*, std::size_t, std::uint32_t,
        std::int32_t&) noexcept = nullptr;
};

bool try_apply_arabic_stretch_from_glyph_actions(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    bool right_to_left,
    std::span<const std::int16_t> normalized_coordinates,
    std::span<arabic_stretch_run> run_scratch,
    font_error* error) noexcept;

// Synchronous borrowed, disjoint scratch. The callback supplies the original
// captured unpositioned horizontal advance, using the current descriptor mapping.
// Mapping has final glyph capacity; widths have original glyph-count capacity.
bool try_apply_device_arabic_stretch_from_glyph_actions(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    bool right_to_left,
    std::span<const std::int16_t> normalized_coordinates,
    std::span<arabic_stretch_run> run_scratch,
    const device_stretch_metrics& metrics,
    std::span<std::uint32_t> descriptor_mapping,
    std::span<std::int32_t> width_scratch,
    font_error* error) noexcept;

} // namespace progpu::native::text::detail

#endif
