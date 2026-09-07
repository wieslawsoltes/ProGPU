#pragma once

#include "progpu_native_direct2d_compat.hpp"
#include "progpu_native.h"
#include <span>
#include <vector>

namespace progpu::native::direct2d::compat::detail {

// Typed internal bridge for shared native fill programs; no platform activation.
[[nodiscard]] com::result create_native_fill_geometry(factory* owner,
    std::span<const progpu_native_path_segment> segments, fill_mode mode,
    path_geometry** value) noexcept;

// One canonical stroke contour. Joins describe segment i -> i+1, including
// the closing seam. Unlike fill conversion, returning to the start does not
// close an open contour and disconnected segments are rejected.
[[nodiscard]] com::result create_native_stroke_geometry(factory* owner,
    std::span<const progpu_native_path_segment> segments,
    std::span<const std::uint8_t> smooth_joins, bool closed,
    path_geometry** value) noexcept;

// Bounds of emitted stroke coverage only, unlike GetWidenedBounds which may
// include original path extents. Does not materialize a second path geometry.
// Empty output is explicit; both outputs change only on success.
[[nodiscard]] com::result get_widened_outline_bounds(geometry* source,
    float width, stroke_style* style, const matrix_3x2_f* transform,
    float tolerance, rectangle_f& bounds, bool& has_outline) noexcept;

// Returns the actual filled boundary, not the original operand stroke paths.
// Output changes only on success; tolerance is in the geometry's coordinates.
[[nodiscard]] com::result extract_outline_contours(geometry* source, float tolerance,
    std::vector<std::vector<point_2f>>& contours) noexcept;

[[nodiscard]] com::result create_path_geometry(
    factory* owner,
    path_geometry** value) noexcept;

} // namespace progpu::native::direct2d::compat::detail
