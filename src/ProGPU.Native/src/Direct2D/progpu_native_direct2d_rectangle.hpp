#pragma once

#include "progpu_native_direct2d_compat.hpp"

namespace progpu::native::direct2d::compat::detail {

[[nodiscard]] com::result get_rectangle_widened_bounds(
    factory* owner,
    const rectangle_f& rectangle,
    float stroke_width,
    stroke_style* style,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    rectangle_f* bounds) noexcept;

[[nodiscard]] com::result rectangle_stroke_contains_point(
    const rectangle_f& rectangle,
    point_2f point,
    float stroke_width,
    stroke_style* style,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    std::int32_t* contains) noexcept;

[[nodiscard]] com::result outline_rectangle(
    const rectangle_f& rectangle,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    simplified_geometry_sink* sink) noexcept;

[[nodiscard]] com::result widen_rectangle(
    const rectangle_f& rectangle,
    float stroke_width,
    stroke_style* style,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    simplified_geometry_sink* sink) noexcept;

} // namespace progpu::native::direct2d::compat::detail
