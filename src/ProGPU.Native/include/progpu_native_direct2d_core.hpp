#pragma once

#include "progpu_native_com.hpp"
#include "progpu_native_direct2d.h"
#include "progpu_native_geometry.hpp"

#include <array>
#include <bit>
#include <cstddef>
#include <cstdint>

namespace progpu::native::direct2d::core {

inline constexpr float default_flattening_tolerance =
    geometry::default_flattening_tolerance;

/* Direct2D rectangles use edge coordinates rather than x/y/width/height.
 * Keeping that representation in the behavior core preserves valid spans
 * whose mathematical width is larger than FLT_MAX. The point-free shapes,
 * enums and stroke properties are the neutral Geometry types. */
using rectangle_edges_f = geometry::rectangle_edges_f;
using size_f = geometry::size_f;
using arc_sweep_direction = geometry::arc_sweep_direction;
using arc_size_kind = geometry::arc_size_kind;

/* Shapes with point members keep the Direct2D C point type, the public C ABI;
 * the neutral Geometry types have the same layout and the converters below
 * translate at the boundary. */
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

using rounded_rectangle_f = geometry::rounded_rectangle_f;
using cap_style = geometry::cap_style;
using line_join = geometry::line_join;
using dash_style = geometry::dash_style;
using stroke_style_properties_f = geometry::stroke_style_properties_f;

static_assert(sizeof(progpu_native_direct2d_point_2f) ==
    sizeof(geometry::point_2f));
static_assert(offsetof(progpu_native_direct2d_point_2f, x) ==
    offsetof(geometry::point_2f, x));
static_assert(offsetof(progpu_native_direct2d_point_2f, y) ==
    offsetof(geometry::point_2f, y));
static_assert(sizeof(progpu_native_direct2d_matrix_3x2_f) ==
    sizeof(geometry::matrix_3x2_f));
static_assert(offsetof(progpu_native_direct2d_matrix_3x2_f, m11) ==
    offsetof(geometry::matrix_3x2_f, m11));
static_assert(offsetof(progpu_native_direct2d_matrix_3x2_f, m12) ==
    offsetof(geometry::matrix_3x2_f, m12));
static_assert(offsetof(progpu_native_direct2d_matrix_3x2_f, m21) ==
    offsetof(geometry::matrix_3x2_f, m21));
static_assert(offsetof(progpu_native_direct2d_matrix_3x2_f, m22) ==
    offsetof(geometry::matrix_3x2_f, m22));
static_assert(offsetof(progpu_native_direct2d_matrix_3x2_f, m31) ==
    offsetof(geometry::matrix_3x2_f, m31));
static_assert(offsetof(progpu_native_direct2d_matrix_3x2_f, m32) ==
    offsetof(geometry::matrix_3x2_f, m32));
static_assert(sizeof(progpu_native_direct2d_triangle) ==
    sizeof(geometry::triangle_f));
static_assert(offsetof(progpu_native_direct2d_triangle, point2) ==
    offsetof(geometry::triangle_f, point2));
static_assert(offsetof(progpu_native_direct2d_triangle, point3) ==
    offsetof(geometry::triangle_f, point3));
static_assert(sizeof(arc_segment_f) == sizeof(geometry::arc_segment_f));
static_assert(offsetof(arc_segment_f, size) ==
    offsetof(geometry::arc_segment_f, size));
static_assert(offsetof(arc_segment_f, rotation_angle) ==
    offsetof(geometry::arc_segment_f, rotation_angle));
static_assert(offsetof(arc_segment_f, sweep) ==
    offsetof(geometry::arc_segment_f, sweep));
static_assert(offsetof(arc_segment_f, size_kind) ==
    offsetof(geometry::arc_segment_f, size_kind));
static_assert(sizeof(cubic_bezier_segment_f) ==
    sizeof(geometry::cubic_bezier_segment_f));
static_assert(offsetof(cubic_bezier_segment_f, point2) ==
    offsetof(geometry::cubic_bezier_segment_f, point2));
static_assert(offsetof(cubic_bezier_segment_f, point3) ==
    offsetof(geometry::cubic_bezier_segment_f, point3));
static_assert(sizeof(ellipse_f) == sizeof(geometry::ellipse_f));
static_assert(offsetof(ellipse_f, radius_x) ==
    offsetof(geometry::ellipse_f, radius_x));
static_assert(offsetof(ellipse_f, radius_y) ==
    offsetof(geometry::ellipse_f, radius_y));

/* Zero-cost conversions between the Direct2D C structs and the neutral
 * Geometry types (identical layouts, asserted above). */
[[nodiscard]] inline geometry::point_2f to_geometry(
    progpu_native_direct2d_point_2f value) noexcept
{
    return std::bit_cast<geometry::point_2f>(value);
}

[[nodiscard]] inline progpu_native_direct2d_point_2f from_geometry(
    geometry::point_2f value) noexcept
{
    return std::bit_cast<progpu_native_direct2d_point_2f>(value);
}

[[nodiscard]] inline geometry::matrix_3x2_f to_geometry(
    const progpu_native_direct2d_matrix_3x2_f& value) noexcept
{
    return std::bit_cast<geometry::matrix_3x2_f>(value);
}

[[nodiscard]] inline progpu_native_direct2d_matrix_3x2_f from_geometry(
    const geometry::matrix_3x2_f& value) noexcept
{
    return std::bit_cast<progpu_native_direct2d_matrix_3x2_f>(value);
}

/* Optional transform: null stays null (identity); otherwise the converted
 * value is stored in storage and its address returned. */
[[nodiscard]] inline const geometry::matrix_3x2_f* to_geometry(
    const progpu_native_direct2d_matrix_3x2_f* value,
    geometry::matrix_3x2_f& storage) noexcept
{
    if (value == nullptr) {
        return nullptr;
    }
    storage = std::bit_cast<geometry::matrix_3x2_f>(*value);
    return &storage;
}

[[nodiscard]] inline geometry::arc_segment_f to_geometry(
    const arc_segment_f& value) noexcept
{
    return std::bit_cast<geometry::arc_segment_f>(value);
}

[[nodiscard]] inline arc_segment_f from_geometry(
    const geometry::arc_segment_f& value) noexcept
{
    return std::bit_cast<arc_segment_f>(value);
}

[[nodiscard]] inline geometry::cubic_bezier_segment_f to_geometry(
    const cubic_bezier_segment_f& value) noexcept
{
    return std::bit_cast<geometry::cubic_bezier_segment_f>(value);
}

[[nodiscard]] inline cubic_bezier_segment_f from_geometry(
    const geometry::cubic_bezier_segment_f& value) noexcept
{
    return std::bit_cast<cubic_bezier_segment_f>(value);
}

[[nodiscard]] inline geometry::ellipse_f to_geometry(
    const ellipse_f& value) noexcept
{
    return std::bit_cast<geometry::ellipse_f>(value);
}

[[nodiscard]] inline progpu_native_direct2d_triangle from_geometry(
    const geometry::triangle_f& value) noexcept
{
    return std::bit_cast<progpu_native_direct2d_triangle>(value);
}

[[nodiscard]] bool valid_transform(
    const progpu_native_direct2d_matrix_3x2_f* transform) noexcept;

[[nodiscard]] bool valid_target_extent(
    const progpu_native_direct2d_target_extent* target) noexcept;

/* Direct2D uses row-vector affine matrices. The returned matrix applies
 * first, followed by second (or identity when second is null). */
[[nodiscard]] com::result compose_transform(
    const progpu_native_direct2d_matrix_3x2_f& first,
    const progpu_native_direct2d_matrix_3x2_f* second,
    progpu_native_direct2d_matrix_3x2_f* result) noexcept;

/* Inverse-map a target-space rectangle to a conservative local material domain.
 * The output is not an exact clip. Origins and dimensions stay double until
 * outward float origin/extent rounding; independent coordinates use SIMD. */
[[nodiscard]] com::result rectangular_coverage_bounds(
    const progpu_native_direct2d_matrix_3x2_f& inverse,
    double target_x,
    double target_y,
    double target_width,
    double target_height,
    rectangle_edges_f* result) noexcept;

/* Zero-origin viewport specialization. Error clears result. */
[[nodiscard]] com::result viewport_coverage_bounds(
    const progpu_native_direct2d_matrix_3x2_f& inverse,
    double target_width,
    double target_height,
    rectangle_edges_f* result) noexcept;

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

[[nodiscard]] bool valid_rounded_rectangle(
    const rounded_rectangle_f& rectangle) noexcept;

[[nodiscard]] com::result rounded_rectangle_to_path(
    const rounded_rectangle_f& rectangle,
    progpu_native_direct2d_point_2f* start,
    std::array<progpu_native_direct2d_point_2f, 4U>* line_ends,
    std::array<cubic_bezier_segment_f, 4U>* corners) noexcept;

[[nodiscard]] com::result rounded_rectangle_fill_contains_point(
    const rounded_rectangle_f& rectangle,
    progpu_native_direct2d_point_2f point,
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    float flattening_tolerance,
    std::uint32_t* contains) noexcept;

[[nodiscard]] bool valid_stroke_style(
    const stroke_style_properties_f& properties,
    const float* dashes,
    std::uint32_t dash_count) noexcept;

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
