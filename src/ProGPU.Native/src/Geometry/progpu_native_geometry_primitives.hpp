#pragma once

#include "progpu_native_geometry_types.hpp"

#include <array>
#include <cstdint>

namespace progpu::native::geometry {

[[nodiscard]] bool valid_transform(const matrix_3x2_f* transform) noexcept;

[[nodiscard]] bool valid_arc_segment(const arc_segment_f& arc) noexcept;

/* Converts an endpoint arc to zero through four cubic pieces. Zero pieces
 * means a coincident endpoint or a zero-radius line-equivalent arc; callers
 * retain those two cases from the source values. */
[[nodiscard]] status arc_to_cubics(
    point_2f start,
    const arc_segment_f& arc,
    std::array<cubic_bezier_segment_f, 4U>* cubics,
    std::uint32_t* cubic_count) noexcept;

[[nodiscard]] bool valid_stroke_style(
    const stroke_style_properties_f& properties,
    const float* dashes,
    std::uint32_t dash_count) noexcept;

} // namespace progpu::native::geometry
