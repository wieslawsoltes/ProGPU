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
    factory* owner,
    const rectangle_f& rectangle,
    point_2f point,
    float stroke_width,
    stroke_style* style,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    std::int32_t* contains) noexcept;

[[nodiscard]] com::result compare_rectangle(
    factory* owner,
    const rectangle_f& rectangle,
    geometry* candidate,
    const matrix_3x2_f* candidate_transform,
    float flattening_tolerance,
    geometry_relation* relation) noexcept;

[[nodiscard]] com::result combine_rectangle(
    factory* owner,
    const rectangle_f& rectangle,
    geometry* candidate,
    combine_mode mode,
    const matrix_3x2_f* candidate_transform,
    float flattening_tolerance,
    simplified_geometry_sink* sink) noexcept;

[[nodiscard]] com::result outline_rectangle(
    const rectangle_f& rectangle,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    simplified_geometry_sink* sink) noexcept;

[[nodiscard]] com::result widen_rectangle(
    factory* owner,
    const rectangle_f& rectangle,
    float stroke_width,
    stroke_style* style,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    simplified_geometry_sink* sink) noexcept;

// Success with a null output preserves the caller's legacy route. A non-null
// result owns the closed ordered centerline after intrinsic transforms, before
// stroke expansion or the caller's world transform. No target/DPI policy is inferred.
[[nodiscard]] com::result create_transformed_rectangle_stroke_path(
    factory* owner,
    geometry* source,
    const matrix_3x2_f& intrinsic,
    float stroke_width,
    stroke_style* style,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    path_geometry** value) noexcept;

// Materialize only the established direct-rectangle/positive-diagonal intrinsic
// lane. Success with selected=false leaves unrelated geometry on its old route.
[[nodiscard]] com::result try_get_default_transformed_rectangle(
    factory* owner,
    geometry* source,
    const matrix_3x2_f& intrinsic,
    rectangle_f* rectangle,
    bool* selected) noexcept;

// Preserve the original default transformed rectangle sink transcript, including
// winding-only zero width. This is not the generic closed-path Widen output.
[[nodiscard]] com::result widen_transformed_rectangle(
    const rectangle_f& rectangle,
    float stroke_width,
    stroke_style* style,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    simplified_geometry_sink* sink) noexcept;

} // namespace progpu::native::direct2d::compat::detail
