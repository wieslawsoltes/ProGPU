#pragma once

#include "progpu_native_text_hinting.h"
#include <cmath>
#include <span>

namespace progpu::native::text {

// BinaryGlyphRasterizer.wgsl owns two crossing slots per original segment.
inline constexpr std::size_t binary_glyph_max_segments = 256U;
inline constexpr std::uint32_t binary_glyph_descriptor_mask =
    PROGPU_NATIVE_FONT_SCAN_SEGMENT_MARKER | PROGPU_NATIVE_FONT_SCAN_CONTOUR_OVERRIDE |
    PROGPU_NATIVE_FONT_SCAN_ORIGINAL_FLAGS_MASK | 7U;

inline bool valid_glyph_scan_segment(const progpu_native_path_segment& segment) noexcept {
    const auto finite = [](progpu_native_point p) { return std::isfinite(p.x) && std::isfinite(p.y); };
    if (!finite(segment.p0) || !finite(segment.p1) || !finite(segment.p2) || !finite(segment.p3)) return false;
    if (segment.pad0 == 0U)
        return segment.kind <= PROGPU_NATIVE_PATH_SEGMENT_CUBIC && segment.pad1 == 0U && segment.pad2 == 0U;
    return segment.kind <= PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC &&
        (segment.pad0 & PROGPU_NATIVE_FONT_SCAN_SEGMENT_MARKER) != 0U &&
        (segment.pad0 & ~binary_glyph_descriptor_mask) == 0U && segment.pad2 != 0U;
}

// Deliberately glyph-only. Generic path, clip and RGB-glyph validators keep
// their independent zero-reserved-word contracts. The caller has already
// validated the original outline range/bounds and passes exactly that range.
template<class Outline, class SegmentAt>
bool valid_glyph_scan_outline(const Outline& outline, std::size_t count, SegmentAt at) noexcept {
    if (count == 0U) return false;
    const bool binary = at(0U).pad0 != 0U;
    if (!binary) {
        for (std::size_t index = 0U; index < count; ++index) {
            const auto segment = at(index);
            if (segment.pad0 != 0U || !valid_glyph_scan_segment(segment)) return false;
        }
        return true;
    }
    if (count > binary_glyph_max_segments || outline.raster_scale != 1.0F || outline.subpixel_x != 0.0F)
        return false;
    const auto flags = at(0U).pad0 & PROGPU_NATIVE_FONT_SCAN_ORIGINAL_FLAGS_MASK;
    const auto equal = [](progpu_native_point a, progpu_native_point b) { return a.x == b.x && a.y == b.y; };
    const auto inside = [&](progpu_native_point p) {
        return p.x >= outline.min_x && p.x <= outline.max_x && p.y >= outline.min_y && p.y <= outline.max_y;
    };
    std::size_t start = 0U;
    while (start < count) {
        const auto first = at(start);
        if (first.pad0 == 0U || !valid_glyph_scan_segment(first) || first.pad1 != start || first.pad2 > count - start ||
            (first.pad0 & PROGPU_NATIVE_FONT_SCAN_ORIGINAL_FLAGS_MASK) != flags) return false;
        const auto end = start + first.pad2;
        auto previous = first.p0;
        for (auto index = start; index < end; ++index) {
            const auto segment = at(index);
            if (!valid_glyph_scan_segment(segment) || segment.pad0 != first.pad0 ||
                segment.pad1 != first.pad1 || segment.pad2 != first.pad2 || !equal(previous, segment.p0) ||
                !inside(segment.p0) || !inside(segment.p1) ||
                (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC && !inside(segment.p2))) return false;
            previous = segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE ? segment.p1 : segment.p2;
        }
        if (!equal(previous, first.p0)) return false;
        start = end;
    }
    return true;
}

template<class Outline>
bool valid_glyph_scan_outline(const Outline& outline,
    std::span<const progpu_native_path_segment> segments) noexcept {
    return valid_glyph_scan_outline(outline, segments.size(),
        [&](std::size_t index) { return segments[index]; });
}
} // namespace progpu::native::text
