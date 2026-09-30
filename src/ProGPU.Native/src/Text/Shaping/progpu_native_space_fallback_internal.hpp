#ifndef PROGPU_NATIVE_SPACE_FALLBACK_INTERNAL_HPP
#define PROGPU_NATIVE_SPACE_FALLBACK_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

struct gpos_device_frame;
struct gpos_metric_vector;

enum class device_space_advance_kind : std::uint8_t { figure, punctuation };

// Native-only, synchronously borrowed auxiliary metrics from this exact
// immutable font/variation/device generation. The owner retains its lease;
// callbacks cannot enter managed code or retain these stack spans. Advances
// are raw hinted x/y advances in 26.6, before vertical direction negation.
// The role selects its exact retained descriptor slice even when two roles
// contain equal or repeated glyph IDs.
struct device_space_advances final {
    const void* owner = nullptr;
    bool (*get_advances)(const void*, device_space_advance_kind,
        std::span<const std::uint16_t>,
        std::span<gpos_metric_vector>) noexcept = nullptr;
};

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

// Preserves the original design-space fallback policy, projecting only its
// em fractions. Existing device advances and auxiliary hinted metrics are
// never projected again. Failure leaves the entire source glyph unchanged.
bool try_apply_device_space_fallback(
    const sfnt_font_view& font,
    shaping_direction direction,
    std::span<const std::int16_t> normalized_coordinates,
    shaping_glyph& glyph,
    const gpos_device_frame& frame,
    const device_space_advances& advances,
    font_error* error = nullptr) noexcept;

} // namespace progpu::native::text::detail

#endif
