#pragma once

#include "progpu_native_com.hpp"
#include "progpu_native_direct2d.h"

#include <array>
#include <cstdint>

namespace progpu::native::direct2d::core {

inline constexpr float default_flattening_tolerance = 0.25F;

/* Direct2D rectangles use edge coordinates rather than x/y/width/height.
 * Keeping that representation in the behavior core preserves valid spans
 * whose mathematical width is larger than FLT_MAX. */
struct rectangle_edges_f final {
    float left;
    float top;
    float right;
    float bottom;
};

struct size_f final {
    float width;
    float height;
};

enum class arc_sweep_direction : std::uint32_t {
    counter_clockwise = 0U,
    clockwise = 1U
};

enum class arc_size_kind : std::uint32_t {
    small_value = 0U,
    large_value = 1U
};

struct arc_segment_f final {
    progpu_native_direct2d_point_2f point;
    size_f size;
    float rotation_angle;
    arc_sweep_direction sweep;
    arc_size_kind size_kind;
};

struct cubic_bezier_segment_f final {
    progpu_native_direct2d_point_2f point1;
    progpu_native_direct2d_point_2f point2;
    progpu_native_direct2d_point_2f point3;
};

struct ellipse_f final {
    progpu_native_direct2d_point_2f point;
    float radius_x;
    float radius_y;
};

[[nodiscard]] bool valid_transform(
    const progpu_native_direct2d_matrix_3x2_f* transform) noexcept;

/* Direct2D uses row-vector affine matrices. The returned matrix applies
 * first, followed by second (or identity when second is null). */
[[nodiscard]] com::result compose_transform(
    const progpu_native_direct2d_matrix_3x2_f& first,
    const progpu_native_direct2d_matrix_3x2_f* second,
    progpu_native_direct2d_matrix_3x2_f* result) noexcept;

[[nodiscard]] bool valid_arc_segment(
    const arc_segment_f& arc) noexcept;

/* Converts a Direct2D endpoint arc to zero through four cubic pieces. Zero
 * pieces means a coincident endpoint or a zero-radius line-equivalent arc;
 * callers retain those two cases from the source values. */
[[nodiscard]] com::result arc_to_cubics(
    progpu_native_direct2d_point_2f start,
    const arc_segment_f& arc,
    std::array<cubic_bezier_segment_f, 4U>* cubics,
    std::uint32_t* cubic_count) noexcept;

[[nodiscard]] bool valid_ellipse(const ellipse_f& ellipse) noexcept;

[[nodiscard]] com::result ellipse_to_cubics(
    const ellipse_f& ellipse,
    progpu_native_direct2d_point_2f* start,
    std::array<cubic_bezier_segment_f, 4U>* cubics) noexcept;

[[nodiscard]] com::result ellipse_fill_contains_point(
    const ellipse_f& ellipse,
    progpu_native_direct2d_point_2f point,
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    float flattening_tolerance,
    std::uint32_t* contains) noexcept;

class rectangle_geometry final {
public:
    explicit rectangle_geometry(rectangle_edges_f rectangle) noexcept;

    [[nodiscard]] static bool valid_rectangle(
        const rectangle_edges_f& rectangle) noexcept;

    [[nodiscard]] com::result vertices(
        const progpu_native_direct2d_matrix_3x2_f* world_transform,
        std::array<progpu_native_direct2d_point_2f, 4U>& value) const
        noexcept;

    [[nodiscard]] com::result bounds(
        const progpu_native_direct2d_matrix_3x2_f* world_transform,
        rectangle_edges_f* value) const noexcept;

    [[nodiscard]] com::result fill_contains_point(
        progpu_native_direct2d_point_2f point,
        const progpu_native_direct2d_matrix_3x2_f* world_transform,
        float flattening_tolerance,
        std::uint32_t* contains) const noexcept;

    [[nodiscard]] com::result tessellate(
        const progpu_native_direct2d_matrix_3x2_f* world_transform,
        float flattening_tolerance,
        std::array<progpu_native_direct2d_triangle, 2U>* triangles) const
        noexcept;

    [[nodiscard]] com::result area(
        const progpu_native_direct2d_matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* value) const noexcept;

    [[nodiscard]] com::result length(
        const progpu_native_direct2d_matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* value) const noexcept;

    [[nodiscard]] com::result point_at_length(
        float length,
        const progpu_native_direct2d_matrix_3x2_f* world_transform,
        float flattening_tolerance,
        progpu_native_direct2d_point_2f* point,
        progpu_native_direct2d_point_2f* unit_tangent) const noexcept;

    [[nodiscard]] const rectangle_edges_f& rectangle() const
        noexcept;

private:
    rectangle_edges_f rectangle_{};
};

} // namespace progpu::native::direct2d::core
