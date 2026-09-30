#ifndef PROGPU_NATIVE_FALLBACK_MARKS_INTERNAL_HPP
#define PROGPU_NATIVE_FALLBACK_MARKS_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

struct gpos_device_frame;

// Captured, unpositioned metrics from the same owned hinted generation as the
// frame. Every metric is in that frame's signed device 26.6 units; height uses
// the original fallback walker's negative-downward extent convention.
struct device_mark_bounds final {
    std::int32_t x_bearing = 0;
    std::int32_t y_bearing = 0;
    std::int32_t width = 0;
    std::int32_t negative_height = 0;
    std::int32_t horizontal_advance_26_6 = 0;
};

// Synchronously borrowed native generation. Descriptor identity is required:
// repeated glyph IDs may refer to distinct retained source descriptors. The
// callback reads captured metrics only, without font/GPU work or allocation.
// Its wrapper owner need not be the frame's owner; the constructing native
// caller owns their common generation and retains both leases for this call.
struct device_mark_extents final {
    const void* owner = nullptr;
    bool (*try_get_extents)(const void*, std::size_t, std::uint32_t,
        device_mark_bounds&, bool& found) noexcept = nullptr;
};

bool try_apply_fallback_mark_positioning_from_attachments(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    shaping_direction direction,
    std::span<const shaping_attachment> metadata,
    std::span<const std::int16_t> normalized_coordinates,
    fallback_mark_positioning_scratch* scratch,
    font_error* error) noexcept;

bool try_apply_device_fallback_mark_positioning_from_attachments(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    shaping_direction direction,
    std::span<const shaping_attachment> metadata,
    std::span<const std::int16_t> normalized_coordinates,
    fallback_mark_positioning_scratch* scratch,
    const gpos_device_frame& frame,
    const device_mark_extents& extents,
    font_error* error) noexcept;

} // namespace progpu::native::text::detail

#endif
