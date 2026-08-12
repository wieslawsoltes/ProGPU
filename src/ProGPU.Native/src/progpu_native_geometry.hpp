#pragma once

#include "progpu_native.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <numbers>
#include <vector>

namespace progpu::native {

struct vector_vertex {
    float position[2];
    float color[4];
    float texture_coordinate[2];
    float brush_index;
    float shape_size[2];
    float corner_radius;
    float stroke_thickness;
    float shape_type;
};

static_assert(sizeof(vector_vertex) == 56U);
static_assert(offsetof(vector_vertex, color) == 8U);
static_assert(offsetof(vector_vertex, texture_coordinate) == 24U);
static_assert(offsetof(vector_vertex, brush_index) == 32U);
static_assert(offsetof(vector_vertex, shape_size) == 36U);
static_assert(offsetof(vector_vertex, shape_type) == 52U);
static_assert(sizeof(progpu_native_affine_2d) == 24U);
static_assert(sizeof(progpu_native_image_rect) == 16U);
static_assert(sizeof(progpu_native_analytic_primitive) == 72U);
static_assert(sizeof(progpu_native_point) == 8U);
static_assert(sizeof(progpu_native_geometry_primitive) == 88U);
static_assert(sizeof(progpu_native_polyline) ==
    (sizeof(std::size_t) == 8U ? 72U : 64U));
static_assert(sizeof(progpu_native_dash_style) ==
    (sizeof(std::size_t) == 8U ? 32U : 24U));
static_assert(sizeof(progpu_native_spline) ==
    (sizeof(std::size_t) == 8U ? 112U : 88U));
static_assert(sizeof(progpu_native_path_segment) == 48U);
static_assert(sizeof(progpu_native_path_fill) ==
    (sizeof(std::size_t) == 8U ? 80U : 72U));
static_assert(sizeof(progpu_native_geometry_frame) ==
    (sizeof(std::size_t) == 8U ? 144U : 92U));
static_assert(offsetof(progpu_native_geometry_frame, reserved) ==
    (sizeof(std::size_t) == 8U ? 60U : 48U));
static_assert(sizeof(progpu_native_path_frame) ==
    (sizeof(std::size_t) == 8U ? 80U : 60U));
static_assert(sizeof(progpu_native_image_frame) ==
    (sizeof(std::size_t) == 8U ? 160U : 144U));
static_assert(sizeof(progpu_native_image_frame_metrics) == 72U);

inline bool is_finite(const progpu_native_color& color) noexcept {
    return std::isfinite(color.r) &&
        std::isfinite(color.g) &&
        std::isfinite(color.b) &&
        std::isfinite(color.a);
}

inline bool is_finite(const progpu_native_affine_2d& transform) noexcept {
    return std::isfinite(transform.m11) &&
        std::isfinite(transform.m12) &&
        std::isfinite(transform.m21) &&
        std::isfinite(transform.m22) &&
        std::isfinite(transform.m31) &&
        std::isfinite(transform.m32);
}

inline bool is_finite(const progpu_native_point& point) noexcept {
    return std::isfinite(point.x) && std::isfinite(point.y);
}

inline bool is_finite(const progpu_native_rect& rect) noexcept {
    return std::isfinite(rect.x) &&
        std::isfinite(rect.y) &&
        std::isfinite(rect.width) &&
        std::isfinite(rect.height) &&
        is_finite(rect.color);
}

inline void transform_point(
    const progpu_native_affine_2d& transform,
    float x,
    float y,
    float& result_x,
    float& result_y) noexcept {
    result_x = x * transform.m11 + y * transform.m21 + transform.m31;
    result_y = x * transform.m12 + y * transform.m22 + transform.m32;
}

inline bool try_get_stroke_scales(
    const progpu_native_affine_2d& transform,
    float& maximum_scale,
    float& minimum_scale) noexcept {
    if (!is_finite(transform)) {
        return false;
    }
    const double m11 = transform.m11;
    const double m12 = transform.m12;
    const double m21 = transform.m21;
    const double m22 = transform.m22;
    const double sum = m11 * m11 + m12 * m12 + m21 * m21 + m22 * m22;
    const double determinant = m11 * m22 - m12 * m21;
    const double discriminant = std::max(
        0.0,
        sum * sum - 4.0 * determinant * determinant);
    const double maximum_squared = std::max(
        0.0,
        (sum + std::sqrt(discriminant)) * 0.5);
    const double minimum_squared = std::max(
        0.0,
        (sum - std::sqrt(discriminant)) * 0.5);
    const double maximum_value = std::sqrt(maximum_squared);
    const double minimum_value = std::sqrt(minimum_squared);
    if (!std::isfinite(maximum_value) || maximum_value <= 0.000001 ||
        !std::isfinite(minimum_value) || minimum_value <= 0.000001) {
        return false;
    }
    maximum_scale = static_cast<float>(maximum_value);
    minimum_scale = static_cast<float>(minimum_value);
    return std::isfinite(maximum_scale) && maximum_scale > 0.000001F &&
        std::isfinite(minimum_scale) && minimum_scale > 0.000001F;
}

inline bool try_get_minimum_scale(
    const progpu_native_affine_2d& transform,
    float& minimum_scale) noexcept {
    float maximum_scale = 0.0F;
    return try_get_stroke_scales(
        transform,
        maximum_scale,
        minimum_scale);
}

inline bool requires_affine_stroke_geometry(
    const progpu_native_affine_2d& transform) noexcept {
    const float length_x = std::hypot(transform.m11, transform.m12);
    const float length_y = std::hypot(transform.m21, transform.m22);
    if (!std::isfinite(length_x) || !std::isfinite(length_y) ||
        length_x <= 0.000001F || length_y <= 0.000001F) {
        return false;
    }
    const float scale = std::max(length_x, length_y);
    const float dot = transform.m11 * transform.m21 +
        transform.m12 * transform.m22;
    return std::abs(length_x - length_y) > scale * 0.0001F ||
        std::abs(dot) > length_x * length_y * 0.0001F;
}

inline bool geometry_uses_payload_brush(
    const progpu_native_geometry_primitive& primitive) noexcept {
    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER ||
        primitive.kind == PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER) {
        return true;
    }
    return primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE &&
        ((primitive.flags & (
            PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK |
            PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK)) != 0U ||
        ((primitive.flags & (
            PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
            PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE)) == 0U &&
        requires_affine_stroke_geometry(primitive.transform)));
}

inline void set_color(
    vector_vertex& vertex,
    const progpu_native_color& color) noexcept {
    vertex.color[0] = color.r;
    vertex.color[1] = color.g;
    vertex.color[2] = color.b;
    vertex.color[3] = color.a;
}

constexpr std::size_t direct_curve_segment_count = 24U;
constexpr std::size_t minimum_affine_curve_segment_count = 24U;
constexpr std::size_t maximum_affine_curve_segment_count = 1024U;
constexpr double affine_curve_max_device_error = 0.25;

inline progpu_native_point transformed_point(
    const progpu_native_affine_2d& transform,
    const progpu_native_point& point) noexcept {
    progpu_native_point result{};
    transform_point(transform, point.x, point.y, result.x, result.y);
    return result;
}

inline progpu_native_point transformed_direction(
    const progpu_native_affine_2d& transform,
    const progpu_native_point& direction) noexcept {
    return {
        direction.x * transform.m11 + direction.y * transform.m21,
        direction.x * transform.m12 + direction.y * transform.m22
    };
}

inline progpu_native_point evaluate_quadratic(
    const progpu_native_point& p0,
    const progpu_native_point& p1,
    const progpu_native_point& p2,
    float t) noexcept {
    const float inverse = 1.0F - t;
    return {
        inverse * inverse * p0.x +
            2.0F * inverse * t * p1.x + t * t * p2.x,
        inverse * inverse * p0.y +
            2.0F * inverse * t * p1.y + t * t * p2.y
    };
}

inline progpu_native_point evaluate_cubic(
    const progpu_native_point& p0,
    const progpu_native_point& p1,
    const progpu_native_point& p2,
    const progpu_native_point& p3,
    float t) noexcept {
    const float inverse = 1.0F - t;
    return {
        inverse * inverse * inverse * p0.x +
            3.0F * inverse * inverse * t * p1.x +
            3.0F * inverse * t * t * p2.x + t * t * t * p3.x,
        inverse * inverse * inverse * p0.y +
            3.0F * inverse * inverse * t * p1.y +
            3.0F * inverse * t * t * p2.y + t * t * t * p3.y
    };
}

inline progpu_native_point quadratic_tangent(
    const progpu_native_point& p0,
    const progpu_native_point& p1,
    const progpu_native_point& p2,
    float t) noexcept {
    return {
        2.0F * ((1.0F - t) * (p1.x - p0.x) + t * (p2.x - p1.x)),
        2.0F * ((1.0F - t) * (p1.y - p0.y) + t * (p2.y - p1.y))
    };
}

inline progpu_native_point cubic_tangent(
    const progpu_native_point& p0,
    const progpu_native_point& p1,
    const progpu_native_point& p2,
    const progpu_native_point& p3,
    float t) noexcept {
    const float inverse = 1.0F - t;
    return {
        3.0F * (inverse * inverse * (p1.x - p0.x) +
            2.0F * inverse * t * (p2.x - p1.x) +
            t * t * (p3.x - p2.x)),
        3.0F * (inverse * inverse * (p1.y - p0.y) +
            2.0F * inverse * t * (p2.y - p1.y) +
            t * t * (p3.y - p2.y))
    };
}

inline std::size_t resolve_affine_curve_segment_count(
    double squared_count) noexcept {
    if (!std::isfinite(squared_count) ||
        squared_count >=
            static_cast<double>(maximum_affine_curve_segment_count) *
                maximum_affine_curve_segment_count) {
        return maximum_affine_curve_segment_count;
    }
    if (squared_count <=
        static_cast<double>(minimum_affine_curve_segment_count) *
            minimum_affine_curve_segment_count) {
        return minimum_affine_curve_segment_count;
    }
    return static_cast<std::size_t>(std::ceil(std::sqrt(squared_count)));
}

inline std::size_t affine_quadratic_segment_count(
    const progpu_native_geometry_primitive& primitive) noexcept {
    const auto p0 = transformed_point(primitive.transform, primitive.p0);
    const auto p1 = transformed_point(primitive.transform, primitive.p1);
    const auto p2 = transformed_point(primitive.transform, primitive.p2);
    const double x = static_cast<double>(p0.x) - 2.0 * p1.x + p2.x;
    const double y = static_cast<double>(p0.y) - 2.0 * p1.y + p2.y;
    return resolve_affine_curve_segment_count(
        std::hypot(x, y) / (4.0 * affine_curve_max_device_error));
}

inline std::size_t affine_cubic_segment_count(
    const progpu_native_geometry_primitive& primitive) noexcept {
    const auto p0 = transformed_point(primitive.transform, primitive.p0);
    const auto p1 = transformed_point(primitive.transform, primitive.p1);
    const auto p2 = transformed_point(primitive.transform, primitive.p2);
    const auto p3 = transformed_point(primitive.transform, primitive.p3);
    const double x0 = static_cast<double>(p0.x) - 2.0 * p1.x + p2.x;
    const double y0 = static_cast<double>(p0.y) - 2.0 * p1.y + p2.y;
    const double x1 = static_cast<double>(p1.x) - 2.0 * p2.x + p3.x;
    const double y1 = static_cast<double>(p1.y) - 2.0 * p2.y + p3.y;
    return resolve_affine_curve_segment_count(
        0.75 * std::max(std::hypot(x0, y0), std::hypot(x1, y1)) /
            affine_curve_max_device_error);
}

inline bool try_normalize(
    progpu_native_point direction,
    const progpu_native_point& fallback,
    progpu_native_point& normalized) noexcept {
    float length = std::hypot(direction.x, direction.y);
    if (!std::isfinite(length) || length <= 0.0001F) {
        direction = fallback;
        length = std::hypot(direction.x, direction.y);
    }
    if (!std::isfinite(length) || length <= 0.0001F) {
        normalized = {};
        return false;
    }
    normalized = {direction.x / length, direction.y / length};
    return true;
}

inline bool try_select_direction(
    const progpu_native_point& first,
    const progpu_native_point& second,
    const progpu_native_point& third,
    progpu_native_point& direction) noexcept {
    const progpu_native_point candidates[3] = {first, second, third};
    for (const auto& candidate : candidates) {
        const float length = std::hypot(candidate.x, candidate.y);
        if (std::isfinite(length) && length > 0.0001F) {
            direction = candidate;
            return true;
        }
    }
    direction = {};
    return false;
}

inline bool append_stroke_quadrilateral(
    const progpu_native_point (&points)[4],
    float brush_index,
    bool aliased,
    float shape_type,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    const float edge0_x = points[1].x - points[0].x;
    const float edge0_y = points[1].y - points[0].y;
    const float edge1_x = points[2].x - points[0].x;
    const float edge1_y = points[2].y - points[0].y;
    const float area = edge0_x * edge1_y - edge0_y * edge1_x;
    if (!std::isfinite(area) || std::abs(area) <= 0.0001F) {
        return true;
    }
    float min_x = points[0].x;
    float min_y = points[0].y;
    float max_x = points[0].x;
    float max_y = points[0].y;
    for (std::size_t index = 1U; index < 4U; ++index) {
        min_x = std::min(min_x, points[index].x);
        min_y = std::min(min_y, points[index].y);
        max_x = std::max(max_x, points[index].x);
        max_y = std::max(max_y, points[index].y);
    }
    constexpr float padding = 1.5F;
    min_x -= padding;
    min_y -= padding;
    max_x += padding;
    max_y += padding;
    if (!std::isfinite(min_x) || !std::isfinite(min_y) ||
        !std::isfinite(max_x) || !std::isfinite(max_y)) {
        return false;
    }
    const std::uint32_t base = static_cast<std::uint32_t>(vertices.size());
    const std::size_t vertex_start = vertices.size();
    vertices.resize(vertex_start + 4U);
    const auto write = [&](std::size_t index, float x, float y) {
        vector_vertex vertex{};
        vertex.position[0] = x;
        vertex.position[1] = y;
        vertex.color[0] = points[0].x;
        vertex.color[1] = points[0].y;
        vertex.color[2] = points[1].x;
        vertex.color[3] = points[1].y;
        vertex.texture_coordinate[0] = x;
        vertex.texture_coordinate[1] = y;
        vertex.brush_index = brush_index;
        vertex.shape_size[0] = points[2].x;
        vertex.shape_size[1] = points[2].y;
        vertex.corner_radius = points[3].x;
        vertex.stroke_thickness = points[3].y;
        vertex.shape_type = shape_type + (aliased ? 1000.0F : 0.0F);
        vertices[vertex_start + index] = vertex;
    };
    write(0U, min_x, min_y);
    write(1U, max_x, min_y);
    write(2U, max_x, max_y);
    write(3U, min_x, max_y);
    const std::size_t index_start = indices.size();
    indices.resize(index_start + 6U);
    indices[index_start] = base;
    indices[index_start + 1U] = base + 1U;
    indices[index_start + 2U] = base + 2U;
    indices[index_start + 3U] = base;
    indices[index_start + 4U] = base + 2U;
    indices[index_start + 5U] = base + 3U;
    return true;
}

struct stroke_triangle {
    progpu_native_point p0;
    progpu_native_point p1;
    progpu_native_point p2;
};

inline bool points_match(
    const progpu_native_point& left,
    const progpu_native_point& right) noexcept {
    const float dx = left.x - right.x;
    const float dy = left.y - right.y;
    return dx * dx + dy * dy <= 0.0001F * 0.0001F;
}

inline bool edges_match(
    const progpu_native_point& a0,
    const progpu_native_point& a1,
    const progpu_native_point& b0,
    const progpu_native_point& b1) noexcept {
    return (points_match(a0, b0) && points_match(a1, b1)) ||
        (points_match(a0, b1) && points_match(a1, b0));
}

inline bool point_on_cap_interface(
    const progpu_native_point& point,
    const progpu_native_point& center,
    const progpu_native_point& direction) noexcept {
    const float dx = point.x - center.x;
    const float dy = point.y - center.y;
    const float tolerance = 0.0001F *
        std::max(1.0F, std::hypot(dx, dy));
    return std::abs(dx * direction.x + dy * direction.y) <= tolerance;
}

inline void classify_triangle_edges(
    const stroke_triangle* triangles,
    std::size_t triangle_count,
    std::size_t triangle_index,
    bool has_cap_interface,
    const progpu_native_point& cap_center,
    const progpu_native_point& cap_direction,
    std::uint32_t& exterior_mask,
    std::uint32_t& owned_internal_mask) noexcept {
    exterior_mask = 0U;
    owned_internal_mask = 0U;
    const auto& triangle = triangles[triangle_index];
    const progpu_native_point starts[3] = {
        triangle.p0, triangle.p1, triangle.p2
    };
    const progpu_native_point ends[3] = {
        triangle.p1, triangle.p2, triangle.p0
    };
    for (std::size_t edge = 0U; edge < 3U; ++edge) {
        const std::uint32_t bit = 1U << edge;
        if (has_cap_interface &&
            point_on_cap_interface(starts[edge], cap_center, cap_direction) &&
            point_on_cap_interface(ends[edge], cap_center, cap_direction)) {
            continue;
        }
        std::size_t shared_index = triangle_count;
        for (std::size_t candidate_index = 0U;
            candidate_index < triangle_count;
            ++candidate_index) {
            if (candidate_index == triangle_index) {
                continue;
            }
            const auto& candidate = triangles[candidate_index];
            if (edges_match(
                    starts[edge], ends[edge], candidate.p0, candidate.p1) ||
                edges_match(
                    starts[edge], ends[edge], candidate.p1, candidate.p2) ||
                edges_match(
                    starts[edge], ends[edge], candidate.p2, candidate.p0)) {
                shared_index = candidate_index;
                break;
            }
        }
        if (shared_index == triangle_count) {
            exterior_mask |= bit;
        } else if (triangle_index < shared_index) {
            owned_internal_mask |= bit;
        }
    }
}

// Cap meshes have fixed fan/quad topology. Deriving edge ownership from the
// construction index avoids the quadratic approximate edge matching used by
// arbitrary triangle sets while producing the same shader masks.
inline void classify_cap_triangle_edges(
    std::uint32_t cap,
    std::size_t triangle_count,
    std::size_t triangle_index,
    std::uint32_t& exterior_mask,
    std::uint32_t& owned_internal_mask) noexcept {
    exterior_mask = 0U;
    owned_internal_mask = 0U;
    if (triangle_index >= triangle_count || triangle_count == 0U) {
        return;
    }
    if (cap == PROGPU_NATIVE_STROKE_CAP_SQUARE && triangle_count == 2U) {
        exterior_mask = triangle_index == 0U ? 0x3U : 0x2U;
        owned_internal_mask = triangle_index == 0U ? 0x4U : 0U;
        return;
    }
    if (cap == PROGPU_NATIVE_STROKE_CAP_TRIANGLE && triangle_count == 1U) {
        exterior_mask = 0x3U;
        return;
    }

    // Round caps are a center fan. Edge 1 is the curved exterior; the first
    // edge of the first triangle and last edge of the final triangle are the
    // stroke-body interface. Each triangle owns its following radial seam.
    exterior_mask = 0x2U;
    if (triangle_index + 1U < triangle_count) {
        owned_internal_mask = 0x4U;
    }
}

// Join meshes likewise have construction-defined topology. A single bevel
// (or miter fallback) triangle retains the historical all-exterior mask. A
// two-triangle miter shares its diagonal as edge 2 in both triangles. Round
// joins are center fans whose first/last radial edges remain exterior.
inline void classify_join_triangle_edges(
    std::uint32_t join,
    std::size_t triangle_count,
    std::size_t triangle_index,
    std::uint32_t& exterior_mask,
    std::uint32_t& owned_internal_mask) noexcept {
    exterior_mask = 0U;
    owned_internal_mask = 0U;
    if (triangle_index >= triangle_count || triangle_count == 0U) {
        return;
    }
    if (triangle_count == 1U) {
        exterior_mask = 0x7U;
        return;
    }
    if (join == PROGPU_NATIVE_STROKE_JOIN_MITER && triangle_count == 2U) {
        exterior_mask = 0x3U;
        owned_internal_mask = triangle_index == 0U ? 0x4U : 0U;
        return;
    }

    exterior_mask = 0x2U;
    if (triangle_index == 0U) {
        exterior_mask |= 0x1U;
    }
    if (triangle_index + 1U == triangle_count) {
        exterior_mask |= 0x4U;
    } else {
        owned_internal_mask = 0x4U;
    }
}

inline bool append_stroke_triangle(
    const stroke_triangle& triangle,
    float brush_index,
    std::uint32_t exterior_mask,
    std::uint32_t owned_internal_mask,
    bool aliased,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    const float edge0_x = triangle.p1.x - triangle.p0.x;
    const float edge0_y = triangle.p1.y - triangle.p0.y;
    const float edge1_x = triangle.p2.x - triangle.p0.x;
    const float edge1_y = triangle.p2.y - triangle.p0.y;
    const float area = edge0_x * edge1_y - edge0_y * edge1_x;
    if (!std::isfinite(area) || std::abs(area) <= 0.0001F) {
        return true;
    }
    constexpr float padding = 1.5F;
    const float min_x = std::min({
        triangle.p0.x, triangle.p1.x, triangle.p2.x}) - padding;
    const float min_y = std::min({
        triangle.p0.y, triangle.p1.y, triangle.p2.y}) - padding;
    const float max_x = std::max({
        triangle.p0.x, triangle.p1.x, triangle.p2.x}) + padding;
    const float max_y = std::max({
        triangle.p0.y, triangle.p1.y, triangle.p2.y}) + padding;
    if (!std::isfinite(min_x) || !std::isfinite(min_y) ||
        !std::isfinite(max_x) || !std::isfinite(max_y)) {
        return false;
    }
    const std::uint32_t base = static_cast<std::uint32_t>(vertices.size());
    const std::size_t vertex_start = vertices.size();
    vertices.resize(vertex_start + 4U);
    const auto write = [&](std::size_t index, float x, float y) {
        vector_vertex vertex{};
        vertex.position[0] = x;
        vertex.position[1] = y;
        vertex.color[0] = triangle.p0.x;
        vertex.color[1] = triangle.p0.y;
        vertex.color[2] = triangle.p1.x;
        vertex.color[3] = triangle.p1.y;
        vertex.texture_coordinate[0] = x;
        vertex.texture_coordinate[1] = y;
        vertex.brush_index = brush_index;
        vertex.shape_size[0] = triangle.p2.x;
        vertex.shape_size[1] = triangle.p2.y;
        vertex.corner_radius = static_cast<float>(exterior_mask);
        vertex.stroke_thickness = static_cast<float>(owned_internal_mask);
        vertex.shape_type = 13.0F + (aliased ? 1000.0F : 0.0F);
        vertices[vertex_start + index] = vertex;
    };
    write(0U, min_x, min_y);
    write(1U, max_x, min_y);
    write(2U, max_x, max_y);
    write(3U, min_x, max_y);
    const std::size_t index_start = indices.size();
    indices.resize(index_start + 6U);
    indices[index_start] = base;
    indices[index_start + 1U] = base + 1U;
    indices[index_start + 2U] = base + 2U;
    indices[index_start + 3U] = base;
    indices[index_start + 4U] = base + 2U;
    indices[index_start + 5U] = base + 3U;
    return true;
}

inline std::size_t create_cap_triangles(
    std::array<stroke_triangle, 8U>& triangles,
    std::uint32_t cap,
    float thickness,
    const progpu_native_point& center,
    progpu_native_point direction,
    bool is_start) noexcept {
    const float length = std::hypot(direction.x, direction.y);
    if (cap == PROGPU_NATIVE_STROKE_CAP_FLAT ||
        !std::isfinite(length) || length <= 0.0001F ||
        !std::isfinite(thickness) || thickness <= 0.0001F) {
        return 0U;
    }
    direction.x /= length;
    direction.y /= length;
    const float radius = thickness * 0.5F;
    const progpu_native_point outward{
        is_start ? -direction.x : direction.x,
        is_start ? -direction.y : direction.y
    };
    const progpu_native_point normal{
        -direction.y * radius,
        direction.x * radius
    };
    if (cap == PROGPU_NATIVE_STROKE_CAP_SQUARE) {
        const progpu_native_point inner0{
            center.x - normal.x, center.y - normal.y};
        const progpu_native_point inner1{
            center.x + normal.x, center.y + normal.y};
        const progpu_native_point outer0{
            inner0.x + outward.x * radius,
            inner0.y + outward.y * radius};
        const progpu_native_point outer1{
            inner1.x + outward.x * radius,
            inner1.y + outward.y * radius};
        triangles[0] = {inner0, outer0, outer1};
        triangles[1] = {inner0, outer1, inner1};
        return 2U;
    }
    if (cap == PROGPU_NATIVE_STROKE_CAP_TRIANGLE) {
        triangles[0] = {
            {center.x - normal.x, center.y - normal.y},
            {center.x + outward.x * radius,
                center.y + outward.y * radius},
            {center.x + normal.x, center.y + normal.y}
        };
        return 1U;
    }
    constexpr std::size_t segment_count = 8U;
    constexpr std::array<float, segment_count + 1U> cosine{
        0.0F, 0.3826834324F, 0.7071067812F, 0.9238795325F, 1.0F,
        0.9238795325F, 0.7071067812F, 0.3826834324F, 0.0F
    };
    constexpr std::array<float, segment_count + 1U> sine{
        -1.0F, -0.9238795325F, -0.7071067812F, -0.3826834324F, 0.0F,
        0.3826834324F, 0.7071067812F, 0.9238795325F, 1.0F
    };
    const progpu_native_point perpendicular{-outward.y, outward.x};
    const auto circle_point = [&](std::size_t index) {
        return progpu_native_point{
            center.x + (outward.x * cosine[index] +
                perpendicular.x * sine[index]) * radius,
            center.y + (outward.y * cosine[index] +
                perpendicular.y * sine[index]) * radius
        };
    };
    for (std::size_t index = 0U; index < segment_count; ++index) {
        triangles[index] = {
            center,
            circle_point(index),
            circle_point(index + 1U)
        };
    }
    return segment_count;
}

inline bool append_affine_round_cap(
    float thickness,
    const progpu_native_point& center,
    progpu_native_point direction,
    bool is_start,
    const progpu_native_affine_2d* outline_transform,
    float brush_index,
    bool aliased,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    if (!try_normalize(direction, {}, direction) ||
        !std::isfinite(thickness) || thickness <= 0.0001F) {
        return true;
    }
    const progpu_native_point outward{
        is_start ? -direction.x : direction.x,
        is_start ? -direction.y : direction.y
    };
    const progpu_native_point normal{-outward.y, outward.x};
    float local_padding = 1.5F;
    if (outline_transform != nullptr) {
        float maximum_scale = 0.0F;
        float minimum_scale = 0.0F;
        if (!try_get_stroke_scales(
                *outline_transform,
                maximum_scale,
                minimum_scale)) {
            return false;
        }
        local_padding /= minimum_scale;
    }
    const float radius = thickness * 0.5F;
    const float minimum_x = -local_padding;
    const float maximum_x = radius + local_padding;
    const float minimum_y = -radius - local_padding;
    const float maximum_y = radius + local_padding;
    const progpu_native_point coordinates[4] = {
        {minimum_x, minimum_y},
        {maximum_x, minimum_y},
        {maximum_x, maximum_y},
        {minimum_x, maximum_y}
    };
    const std::uint32_t base = static_cast<std::uint32_t>(vertices.size());
    const std::size_t vertex_start = vertices.size();
    vertices.resize(vertex_start + 4U);
    for (std::size_t index = 0U; index < 4U; ++index) {
        const auto coordinate = coordinates[index];
        progpu_native_point position{
            center.x + outward.x * coordinate.x + normal.x * coordinate.y,
            center.y + outward.y * coordinate.x + normal.y * coordinate.y
        };
        if (outline_transform != nullptr) {
            position = transformed_point(*outline_transform, position);
        }
        vector_vertex vertex{};
        vertex.position[0] = position.x;
        vertex.position[1] = position.y;
        vertex.texture_coordinate[0] = coordinate.x;
        vertex.texture_coordinate[1] = coordinate.y;
        vertex.brush_index = brush_index;
        vertex.shape_size[0] = thickness;
        vertex.corner_radius =
            static_cast<float>(PROGPU_NATIVE_STROKE_CAP_ROUND);
        vertex.shape_type = 24.0F + (aliased ? 1000.0F : 0.0F);
        vertices[vertex_start + index] = vertex;
    }
    const std::size_t index_start = indices.size();
    indices.resize(index_start + 6U);
    indices[index_start] = base;
    indices[index_start + 1U] = base + 1U;
    indices[index_start + 2U] = base + 2U;
    indices[index_start + 3U] = base;
    indices[index_start + 4U] = base + 2U;
    indices[index_start + 5U] = base + 3U;
    return true;
}

inline bool append_cpu_cap(
    std::uint32_t cap,
    float thickness,
    const progpu_native_point& center,
    const progpu_native_point& direction,
    bool is_start,
    const progpu_native_affine_2d* outline_transform,
    float brush_index,
    bool aliased,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    if (cap == PROGPU_NATIVE_STROKE_CAP_ROUND) {
        return append_affine_round_cap(
            thickness,
            center,
            direction,
            is_start,
            outline_transform,
            brush_index,
            aliased,
            vertices,
            indices);
    }
    std::array<stroke_triangle, 8U> triangles{};
    const std::size_t count = create_cap_triangles(
        triangles,
        cap,
        thickness,
        center,
        direction,
        is_start);
    if (count == 0U) {
        return true;
    }
    for (std::size_t index = 0U; index < count; ++index) {
        std::uint32_t exterior_mask = 0U;
        std::uint32_t owned_internal_mask = 0U;
        classify_cap_triangle_edges(
            cap,
            count,
            index,
            exterior_mask,
            owned_internal_mask);
        stroke_triangle output = triangles[index];
        if (outline_transform != nullptr) {
            output.p0 = transformed_point(*outline_transform, output.p0);
            output.p1 = transformed_point(*outline_transform, output.p1);
            output.p2 = transformed_point(*outline_transform, output.p2);
        }
        if (!append_stroke_triangle(
                output,
                brush_index,
                exterior_mask,
                owned_internal_mask,
                aliased,
                vertices,
                indices)) {
            return false;
        }
    }
    return true;
}

inline void append_device_cap(
    std::uint32_t cap,
    const progpu_native_point& center,
    const progpu_native_point& direction,
    bool is_start,
    float encoded_thickness,
    float brush_index,
    bool aliased,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    if (cap == PROGPU_NATIVE_STROKE_CAP_FLAT) {
        return;
    }
    const std::uint32_t base = static_cast<std::uint32_t>(vertices.size());
    vector_vertex descriptor{};
    descriptor.position[0] = center.x;
    descriptor.position[1] = center.y;
    descriptor.color[0] = static_cast<float>(cap);
    descriptor.color[1] = is_start ? 1.0F : 0.0F;
    descriptor.texture_coordinate[0] = direction.x;
    descriptor.texture_coordinate[1] = direction.y;
    descriptor.brush_index = brush_index;
    descriptor.shape_size[0] = static_cast<float>(base);
    descriptor.stroke_thickness = encoded_thickness;
    descriptor.shape_type = 22.0F + (aliased ? 1000.0F : 0.0F);
    vertices.insert(vertices.end(), 4U, descriptor);
    indices.insert(indices.end(), {
        base, base + 1U, base + 2U,
        base, base + 2U, base + 3U
    });
}

inline bool append_primitive_caps(
    const progpu_native_geometry_primitive& primitive,
    bool hairline,
    bool fixed_device,
    bool affine_outline,
    float maximum_scale,
    float encoded_thickness,
    float brush_index,
    bool aliased,
    std::uint32_t selection,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    const std::uint32_t caps[2] = {
        (primitive.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >>
            PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT,
        (primitive.flags & PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK) >>
            PROGPU_NATIVE_PRIMITIVE_END_CAP_SHIFT
    };
    if (caps[0] == PROGPU_NATIVE_STROKE_CAP_FLAT &&
        caps[1] == PROGPU_NATIVE_STROKE_CAP_FLAT) {
        return true;
    }
    progpu_native_point centers[2] = {primitive.p0, primitive.p1};
    progpu_native_point first_candidates[3] = {
        {primitive.p1.x - primitive.p0.x,
            primitive.p1.y - primitive.p0.y},
        {},
        {}
    };
    progpu_native_point last_candidates[3] = {
        {primitive.p1.x - primitive.p0.x,
            primitive.p1.y - primitive.p0.y},
        {},
        {}
    };
    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER) {
        centers[1] = primitive.p2;
        first_candidates[0] = {
            primitive.p1.x - primitive.p0.x,
            primitive.p1.y - primitive.p0.y};
        first_candidates[1] = {
            primitive.p2.x - primitive.p0.x,
            primitive.p2.y - primitive.p0.y};
        last_candidates[0] = {
            primitive.p2.x - primitive.p1.x,
            primitive.p2.y - primitive.p1.y};
        last_candidates[1] = first_candidates[1];
    } else if (primitive.kind == PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER) {
        centers[1] = primitive.p3;
        first_candidates[0] = {
            primitive.p1.x - primitive.p0.x,
            primitive.p1.y - primitive.p0.y};
        first_candidates[1] = {
            primitive.p2.x - primitive.p0.x,
            primitive.p2.y - primitive.p0.y};
        first_candidates[2] = {
            primitive.p3.x - primitive.p0.x,
            primitive.p3.y - primitive.p0.y};
        last_candidates[0] = {
            primitive.p3.x - primitive.p2.x,
            primitive.p3.y - primitive.p2.y};
        last_candidates[1] = {
            primitive.p3.x - primitive.p1.x,
            primitive.p3.y - primitive.p1.y};
        last_candidates[2] = first_candidates[2];
    }
    for (std::size_t index = 0U; index < 2U; ++index) {
        if ((selection & (1U << index)) == 0U) {
            continue;
        }
        progpu_native_point direction{};
        const progpu_native_point* candidates = index == 0U
            ? first_candidates
            : last_candidates;
        if (!try_select_direction(
                candidates[0],
                candidates[1],
                candidates[2],
                direction)) {
            continue;
        }
        if (hairline || fixed_device) {
            const auto center = transformed_point(
                primitive.transform,
                centers[index]);
            const auto device_direction = transformed_direction(
                primitive.transform,
                direction);
            append_device_cap(
                caps[index],
                center,
                device_direction,
                index == 0U,
                encoded_thickness,
                brush_index,
                aliased,
                vertices,
                indices);
        } else if (affine_outline) {
            if (!append_cpu_cap(
                    caps[index],
                    primitive.stroke_thickness,
                    centers[index],
                    direction,
                    index == 0U,
                    &primitive.transform,
                    brush_index,
                    aliased,
                    vertices,
                    indices)) {
                return false;
            }
        } else {
            const auto center = transformed_point(
                primitive.transform,
                centers[index]);
            const auto device_direction = transformed_direction(
                primitive.transform,
                direction);
            if (!append_cpu_cap(
                    caps[index],
                    primitive.stroke_thickness * maximum_scale,
                    center,
                    device_direction,
                    index == 0U,
                    nullptr,
                    brush_index,
                    aliased,
                    vertices,
                    indices)) {
                return false;
            }
        }
    }
    return true;
}

inline bool geometry_primitive_capacity(
    const progpu_native_geometry_primitive& primitive,
    std::size_t& vertex_count,
    std::size_t& index_count) noexcept {
    if (primitive.kind > PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER) {
        return false;
    }
    const std::size_t cap_count =
        ((primitive.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) != 0U
            ? 1U : 0U) +
        ((primitive.flags & PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK) != 0U
            ? 1U : 0U);
    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_TRIANGLE) {
        vertex_count = 3U;
        index_count = 3U;
        return true;
    }
    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRILATERAL ||
        primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE) {
        vertex_count = 4U + cap_count * 32U;
        index_count = 6U + cap_count * 48U;
        return true;
    }
    const bool affine =
        (primitive.flags & (
            PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
            PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE)) == 0U &&
        requires_affine_stroke_geometry(primitive.transform);
    const std::size_t segments = affine
        ? primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER
            ? affine_quadratic_segment_count(primitive)
            : affine_cubic_segment_count(primitive)
        : direct_curve_segment_count;
    vertex_count = (affine ? segments * 4U : 2U * (segments + 1U)) +
        cap_count * 32U;
    index_count = segments * 6U + cap_count * 48U;
    return true;
}

inline bool append_geometry_primitive(
    const progpu_native_geometry_primitive& primitive,
    float brush_index,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    constexpr std::uint32_t all_line_flags =
        PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED |
        PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
        PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE |
        PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK |
        PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK;
    if (primitive.kind > PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER ||
        !std::isfinite(brush_index) || brush_index < 0.0F ||
        !is_finite(primitive.p0) || !is_finite(primitive.p1) ||
        !is_finite(primitive.p2) || !is_finite(primitive.p3) ||
        !std::isfinite(primitive.stroke_thickness) ||
        !std::isfinite(primitive.reserved) || primitive.reserved != 0.0F ||
        !is_finite(primitive.color) || !is_finite(primitive.transform)) {
        return false;
    }

    const bool is_curve =
        primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER ||
        primitive.kind == PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER;
    const bool uses_affine_curve = is_curve &&
        (primitive.flags & (
            PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
            PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE)) == 0U &&
        requires_affine_stroke_geometry(primitive.transform);
    const std::size_t curve_segments = uses_affine_curve
        ? primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER
            ? affine_quadratic_segment_count(primitive)
            : affine_cubic_segment_count(primitive)
        : direct_curve_segment_count;
    const std::size_t cap_count =
        ((primitive.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) != 0U
            ? 1U : 0U) +
        ((primitive.flags & PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK) != 0U
            ? 1U : 0U);
    const std::size_t maximum_vertices_to_add = (is_curve
        ? uses_affine_curve ? curve_segments * 4U : 2U * (curve_segments + 1U)
        : 4U) + cap_count * 32U;
    const std::size_t maximum_indices_to_add = (is_curve
        ? curve_segments * 6U
        : 6U) + cap_count * 48U;
    if (vertices.size() >
            std::numeric_limits<std::uint32_t>::max() - maximum_vertices_to_add ||
        vertices.size() >
            std::numeric_limits<std::size_t>::max() - maximum_vertices_to_add ||
        indices.size() >
            std::numeric_limits<std::size_t>::max() - maximum_indices_to_add) {
        return false;
    }
    const std::size_t initial_vertex_count = vertices.size();
    const std::size_t initial_index_count = indices.size();

    const bool aliased =
        (primitive.flags & PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED) != 0U;
    const float alias_offset = aliased ? 1000.0F : 0.0F;
    const std::uint32_t base = static_cast<std::uint32_t>(vertices.size());
    const auto transformed = [&](const progpu_native_point& point) {
        progpu_native_point result{};
        transform_point(
            primitive.transform,
            point.x,
            point.y,
            result.x,
            result.y);
        return result;
    };
    const auto append_fill_vertex = [&](const progpu_native_point& point) {
        const auto position = transformed(point);
        vector_vertex vertex{};
        vertex.position[0] = position.x;
        vertex.position[1] = position.y;
        set_color(vertex, primitive.color);
        vertex.texture_coordinate[0] = point.x;
        vertex.texture_coordinate[1] = point.y;
        vertex.brush_index = brush_index;
        vertex.shape_type = 7.0F + alias_offset;
        vertices.push_back(vertex);
    };

    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_TRIANGLE ||
        primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRILATERAL) {
        if ((primitive.flags & ~PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED) != 0U ||
            primitive.stroke_thickness != 0.0F) {
            return false;
        }
        append_fill_vertex(primitive.p0);
        append_fill_vertex(primitive.p1);
        append_fill_vertex(primitive.p2);
        indices.push_back(base);
        indices.push_back(base + 1U);
        indices.push_back(base + 2U);
        if (primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRILATERAL) {
            append_fill_vertex(primitive.p3);
            indices.push_back(base);
            indices.push_back(base + 2U);
            indices.push_back(base + 3U);
        }
        return true;
    }

    if ((primitive.flags & ~all_line_flags) != 0U) {
        return false;
    }
    const bool hairline =
        (primitive.flags & PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE) != 0U;
    const bool fixed_device =
        (primitive.flags & PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE) != 0U;
    if ((hairline && fixed_device) ||
        (!hairline && primitive.stroke_thickness <= 0.0F) ||
        (hairline && primitive.stroke_thickness != 0.0F)) {
        return false;
    }
    float maximum_scale = 0.0F;
    float minimum_scale = 0.0F;
    if (!try_get_stroke_scales(
            primitive.transform,
            maximum_scale,
            minimum_scale)) {
        return false;
    }

    const float encoded_thickness = hairline
        ? -1.0F
        : fixed_device
            ? -std::max(
                primitive.stroke_thickness + 1.0F,
                std::nextafter(1.0F, 2.0F))
            : primitive.stroke_thickness * maximum_scale;

    const bool affine_stroke_geometry = !hairline && !fixed_device &&
        requires_affine_stroke_geometry(primitive.transform);
    if (!append_primitive_caps(
            primitive,
            hairline,
            fixed_device,
            affine_stroke_geometry,
            maximum_scale,
            encoded_thickness,
            brush_index,
            aliased,
            1U,
            vertices,
            indices)) {
        vertices.resize(initial_vertex_count);
        indices.resize(initial_index_count);
        return false;
    }

    if (is_curve) {
        if (uses_affine_curve) {
            progpu_native_point previous = primitive.p0;
            progpu_native_point previous_tangent =
                primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER
                ? quadratic_tangent(
                    primitive.p0, primitive.p1, primitive.p2, 0.0F)
                : cubic_tangent(
                    primitive.p0,
                    primitive.p1,
                    primitive.p2,
                    primitive.p3,
                    0.0F);
            for (std::size_t section = 1U;
                section <= curve_segments;
                ++section) {
                const float t = static_cast<float>(section) /
                    static_cast<float>(curve_segments);
                const progpu_native_point point =
                    primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER
                    ? evaluate_quadratic(
                        primitive.p0, primitive.p1, primitive.p2, t)
                    : evaluate_cubic(
                        primitive.p0,
                        primitive.p1,
                        primitive.p2,
                        primitive.p3,
                        t);
                const progpu_native_point tangent =
                    primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER
                    ? quadratic_tangent(
                        primitive.p0, primitive.p1, primitive.p2, t)
                    : cubic_tangent(
                        primitive.p0,
                        primitive.p1,
                        primitive.p2,
                        primitive.p3,
                        t);
                const progpu_native_point chord{
                    point.x - previous.x,
                    point.y - previous.y
                };
                progpu_native_point start_direction{};
                progpu_native_point end_direction{};
                if (try_normalize(
                        previous_tangent,
                        chord,
                        start_direction) &&
                    try_normalize(tangent, chord, end_direction)) {
                    const float radius = primitive.stroke_thickness * 0.5F;
                    const progpu_native_point start_normal{
                        -start_direction.y * radius,
                        start_direction.x * radius
                    };
                    const progpu_native_point end_normal{
                        -end_direction.y * radius,
                        end_direction.x * radius
                    };
                    const progpu_native_point local_points[4] = {
                        {previous.x + start_normal.x,
                            previous.y + start_normal.y},
                        {point.x + end_normal.x,
                            point.y + end_normal.y},
                        {point.x - end_normal.x,
                            point.y - end_normal.y},
                        {previous.x - start_normal.x,
                            previous.y - start_normal.y}
                    };
                    progpu_native_point points[4]{};
                    for (std::size_t index = 0U; index < 4U; ++index) {
                        points[index] = transformed_point(
                            primitive.transform,
                            local_points[index]);
                    }
                    const bool first = section == 1U;
                    const bool last = section == curve_segments;
                    const float shape_type = first
                        ? last ? 14.0F : 16.0F
                        : last ? 17.0F : 15.0F;
                    if (!append_stroke_quadrilateral(
                            points,
                            brush_index,
                            aliased,
                            shape_type,
                            vertices,
                            indices)) {
                        vertices.resize(initial_vertex_count);
                        indices.resize(initial_index_count);
                        return false;
                    }
                }
                previous = point;
                previous_tangent = tangent;
            }
            if (!append_primitive_caps(
                    primitive,
                    hairline,
                    fixed_device,
                    true,
                    maximum_scale,
                    encoded_thickness,
                    brush_index,
                    aliased,
                    2U,
                    vertices,
                    indices)) {
                vertices.resize(initial_vertex_count);
                indices.resize(initial_index_count);
                return false;
            }
            return true;
        }

        const auto p0 = transformed(primitive.p0);
        const auto p1 = transformed(primitive.p1);
        const auto p2 = transformed(primitive.p2);
        const auto p3 = transformed(primitive.p3);
        const std::uint32_t curve_base =
            static_cast<std::uint32_t>(vertices.size());
        vector_vertex base_vertex{};
        base_vertex.position[0] = p0.x;
        base_vertex.position[1] = p0.y;
        if (primitive.kind == PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER) {
            base_vertex.color[0] = p3.x;
            base_vertex.color[1] = p3.y;
        }
        base_vertex.texture_coordinate[0] = p1.x;
        base_vertex.texture_coordinate[1] = p1.y;
        base_vertex.brush_index = brush_index;
        base_vertex.shape_size[0] = p2.x;
        base_vertex.shape_size[1] = p2.y;
        base_vertex.corner_radius = static_cast<float>(curve_base);
        base_vertex.stroke_thickness = encoded_thickness;
        base_vertex.shape_type =
            (primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER
                ? 5.0F
                : 6.0F) + alias_offset;
        vertices.insert(
            vertices.end(),
            2U * (direct_curve_segment_count + 1U),
            base_vertex);
        for (std::size_t section = 0U;
            section < direct_curve_segment_count;
            ++section) {
            const std::uint32_t left = curve_base +
                static_cast<std::uint32_t>(section * 2U);
            const std::uint32_t right = left + 1U;
            const std::uint32_t next_left = left + 2U;
            const std::uint32_t next_right = left + 3U;
            indices.insert(indices.end(), {
                left, right, next_left,
                right, next_right, next_left
            });
        }
        if (!append_primitive_caps(
                primitive,
                hairline,
                fixed_device,
                false,
                maximum_scale,
                encoded_thickness,
                brush_index,
                aliased,
                2U,
                vertices,
                indices)) {
            vertices.resize(initial_vertex_count);
            indices.resize(initial_index_count);
            return false;
        }
        return true;
    }

    const float delta_x = primitive.p1.x - primitive.p0.x;
    const float delta_y = primitive.p1.y - primitive.p0.y;
    const float length = std::hypot(delta_x, delta_y);
    if (!std::isfinite(length)) {
        return false;
    }
    if (length <= 0.000001F) {
        return true;
    }

    const bool uses_affine_quad = !hairline && !fixed_device &&
        requires_affine_stroke_geometry(primitive.transform);
    const std::uint32_t line_base =
        static_cast<std::uint32_t>(vertices.size());
    if (uses_affine_quad) {
        const float inverse_length = 1.0F / length;
        const float half_thickness = primitive.stroke_thickness * 0.5F;
        const float normal_x = -delta_y * inverse_length * half_thickness;
        const float normal_y = delta_x * inverse_length * half_thickness;
        const progpu_native_point local_points[4] = {
            {primitive.p0.x + normal_x, primitive.p0.y + normal_y},
            {primitive.p1.x + normal_x, primitive.p1.y + normal_y},
            {primitive.p1.x - normal_x, primitive.p1.y - normal_y},
            {primitive.p0.x - normal_x, primitive.p0.y - normal_y}
        };
        progpu_native_point points[4]{};
        for (std::size_t index = 0; index < 4U; ++index) {
            points[index] = transformed(local_points[index]);
        }
        float min_x = points[0].x;
        float min_y = points[0].y;
        float max_x = points[0].x;
        float max_y = points[0].y;
        for (std::size_t index = 1; index < 4U; ++index) {
            min_x = std::min(min_x, points[index].x);
            min_y = std::min(min_y, points[index].y);
            max_x = std::max(max_x, points[index].x);
            max_y = std::max(max_y, points[index].y);
        }
        min_x -= 1.5F;
        min_y -= 1.5F;
        max_x += 1.5F;
        max_y += 1.5F;
        const auto append_quad_vertex = [&](float x, float y) {
            vector_vertex vertex{};
            vertex.position[0] = x;
            vertex.position[1] = y;
            vertex.color[0] = points[0].x;
            vertex.color[1] = points[0].y;
            vertex.color[2] = points[1].x;
            vertex.color[3] = points[1].y;
            vertex.texture_coordinate[0] = x;
            vertex.texture_coordinate[1] = y;
            vertex.brush_index = brush_index;
            vertex.shape_size[0] = points[2].x;
            vertex.shape_size[1] = points[2].y;
            vertex.corner_radius = points[3].x;
            vertex.stroke_thickness = points[3].y;
            vertex.shape_type = 14.0F + alias_offset;
            vertices.push_back(vertex);
        };
        append_quad_vertex(min_x, min_y);
        append_quad_vertex(max_x, min_y);
        append_quad_vertex(max_x, max_y);
        append_quad_vertex(min_x, max_y);
    } else {
        const auto start = transformed(primitive.p0);
        const auto end = transformed(primitive.p1);
        const auto append_line_vertex = [&](
            const progpu_native_point& position,
            float corner) {
            vector_vertex vertex{};
            vertex.position[0] = position.x;
            vertex.position[1] = position.y;
            set_color(vertex, primitive.color);
            vertex.texture_coordinate[0] = start.x;
            vertex.texture_coordinate[1] = start.y;
            vertex.brush_index = brush_index;
            vertex.shape_size[0] = end.x;
            vertex.shape_size[1] = end.y;
            vertex.corner_radius = corner;
            vertex.stroke_thickness = encoded_thickness;
            vertex.shape_type = 3.0F + alias_offset;
            vertices.push_back(vertex);
        };
        append_line_vertex(start, 1.0F);
        append_line_vertex(start, -1.0F);
        append_line_vertex(end, 2.0F);
        append_line_vertex(end, -2.0F);
    }

    indices.push_back(line_base);
    indices.push_back(line_base + 1U);
    indices.push_back(line_base + 2U);
    indices.push_back(uses_affine_quad ? line_base : line_base + 1U);
    indices.push_back(uses_affine_quad ? line_base + 2U : line_base + 3U);
    indices.push_back(uses_affine_quad ? line_base + 3U : line_base + 2U);
    if (!append_primitive_caps(
            primitive,
            hairline,
            fixed_device,
            uses_affine_quad,
            maximum_scale,
            encoded_thickness,
            brush_index,
            aliased,
            2U,
            vertices,
            indices)) {
        vertices.resize(initial_vertex_count);
        indices.resize(initial_index_count);
        return false;
    }
    return true;
}

inline float cross_product(
    const progpu_native_point& left,
    const progpu_native_point& right) noexcept {
    return left.x * right.y - left.y * right.x;
}

inline bool try_intersect_lines(
    const progpu_native_point& first_point,
    const progpu_native_point& first_direction,
    const progpu_native_point& second_point,
    const progpu_native_point& second_direction,
    progpu_native_point& intersection) noexcept {
    const float denominator = cross_product(
        first_direction,
        second_direction);
    if (!std::isfinite(denominator) || std::abs(denominator) <= 0.0001F) {
        intersection = {};
        return false;
    }
    const progpu_native_point delta{
        second_point.x - first_point.x,
        second_point.y - first_point.y
    };
    const float distance =
        cross_product(delta, second_direction) / denominator;
    intersection = {
        first_point.x + first_direction.x * distance,
        first_point.y + first_direction.y * distance
    };
    return is_finite(intersection);
}

inline std::size_t create_join_triangles(
    std::array<stroke_triangle, 8U>& triangles,
    std::uint32_t join,
    float thickness,
    float miter_limit,
    const progpu_native_point& join_point,
    progpu_native_point incoming,
    progpu_native_point outgoing) noexcept {
    if (!try_normalize(incoming, {}, incoming) ||
        !try_normalize(outgoing, {}, outgoing) ||
        !std::isfinite(thickness) || thickness <= 0.0001F) {
        return 0U;
    }
    const float turn = cross_product(incoming, outgoing);
    if (!std::isfinite(turn) || std::abs(turn) <= 0.0001F) {
        return 0U;
    }
    const float radius = thickness * 0.5F;
    const float outer_sign = turn > 0.0F ? -1.0F : 1.0F;
    const progpu_native_point previous_outer{
        join_point.x - incoming.y * outer_sign * radius,
        join_point.y + incoming.x * outer_sign * radius
    };
    const progpu_native_point next_outer{
        join_point.x - outgoing.y * outer_sign * radius,
        join_point.y + outgoing.x * outer_sign * radius
    };
    if (join == PROGPU_NATIVE_STROKE_JOIN_BEVEL) {
        triangles[0] = {previous_outer, join_point, next_outer};
        return 1U;
    }
    if (join == PROGPU_NATIVE_STROKE_JOIN_MITER) {
        progpu_native_point miter{};
        const float resolved_limit =
            std::isfinite(miter_limit) && miter_limit >= 1.0F
            ? miter_limit
            : 1.0F;
        const bool has_miter = try_intersect_lines(
            previous_outer,
            incoming,
            next_outer,
            outgoing,
            miter) &&
            std::hypot(
                miter.x - join_point.x,
                miter.y - join_point.y) <=
                radius * resolved_limit + 0.0001F;
        triangles[0] = {previous_outer, join_point, next_outer};
        if (!has_miter) {
            return 1U;
        }
        triangles[1] = {previous_outer, miter, next_outer};
        return 2U;
    }

    float start = std::atan2(
        previous_outer.y - join_point.y,
        previous_outer.x - join_point.x);
    float end = std::atan2(
        next_outer.y - join_point.y,
        next_outer.x - join_point.x);
    constexpr float two_pi = std::numbers::pi_v<float> * 2.0F;
    if (turn > 0.0F) {
        while (end < start) {
            end += two_pi;
        }
    } else {
        while (end > start) {
            end -= two_pi;
        }
    }
    const float sweep = end - start;
    const std::size_t segment_count = std::clamp(
        static_cast<std::size_t>(std::ceil(
            std::abs(sweep) / (std::numbers::pi_v<float> / 8.0F))),
        std::size_t{1U},
        triangles.size());
    for (std::size_t index = 0U; index < segment_count; ++index) {
        const float angle0 = start + sweep * static_cast<float>(index) /
            static_cast<float>(segment_count);
        const float angle1 = start + sweep * static_cast<float>(index + 1U) /
            static_cast<float>(segment_count);
        triangles[index] = {
            join_point,
            {join_point.x + std::cos(angle0) * radius,
                join_point.y + std::sin(angle0) * radius},
            {join_point.x + std::cos(angle1) * radius,
                join_point.y + std::sin(angle1) * radius}
        };
    }
    return segment_count;
}

inline bool append_cpu_join(
    std::uint32_t join,
    float thickness,
    float miter_limit,
    const progpu_native_point& join_point,
    const progpu_native_point& incoming,
    const progpu_native_point& outgoing,
    const progpu_native_affine_2d* outline_transform,
    float brush_index,
    bool aliased,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    std::array<stroke_triangle, 8U> triangles{};
    const std::size_t count = create_join_triangles(
        triangles,
        join,
        thickness,
        miter_limit,
        join_point,
        incoming,
        outgoing);
    for (std::size_t index = 0U; index < count; ++index) {
        std::uint32_t exterior_mask = 0U;
        std::uint32_t owned_internal_mask = 0U;
        classify_join_triangle_edges(
            join,
            count,
            index,
            exterior_mask,
            owned_internal_mask);
        stroke_triangle output = triangles[index];
        if (outline_transform != nullptr) {
            output.p0 = transformed_point(*outline_transform, output.p0);
            output.p1 = transformed_point(*outline_transform, output.p1);
            output.p2 = transformed_point(*outline_transform, output.p2);
        }
        if (!append_stroke_triangle(
                output,
                brush_index,
                exterior_mask,
                owned_internal_mask,
                aliased,
                vertices,
                indices)) {
            return false;
        }
    }
    return true;
}

inline void append_device_join(
    std::uint32_t join,
    float miter_limit,
    const progpu_native_point& join_point,
    const progpu_native_point& incoming,
    const progpu_native_point& outgoing,
    float encoded_thickness,
    float brush_index,
    bool aliased,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    const std::uint32_t base = static_cast<std::uint32_t>(vertices.size());
    vector_vertex descriptor{};
    descriptor.position[0] = join_point.x;
    descriptor.position[1] = join_point.y;
    descriptor.color[0] = static_cast<float>(join);
    descriptor.color[1] =
        std::isfinite(miter_limit) && miter_limit >= 1.0F
        ? miter_limit
        : 1.0F;
    descriptor.color[2] = static_cast<float>(base);
    descriptor.texture_coordinate[0] = incoming.x;
    descriptor.texture_coordinate[1] = incoming.y;
    descriptor.brush_index = brush_index;
    descriptor.shape_size[0] = outgoing.x;
    descriptor.shape_size[1] = outgoing.y;
    descriptor.stroke_thickness = encoded_thickness;
    descriptor.shape_type = 23.0F + (aliased ? 1000.0F : 0.0F);
    vertices.insert(vertices.end(), 4U, descriptor);
    indices.insert(indices.end(), {
        base, base + 1U, base + 2U,
        base, base + 2U, base + 3U
    });
}

inline bool append_connected_line_body(
    const progpu_native_point& local_start,
    const progpu_native_point& local_end,
    const progpu_native_affine_2d& transform,
    const progpu_native_color& color,
    float local_thickness,
    float encoded_thickness,
    bool affine_outline,
    float brush_index,
    bool aliased,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    const float delta_x = local_end.x - local_start.x;
    const float delta_y = local_end.y - local_start.y;
    const float length = std::hypot(delta_x, delta_y);
    if (!std::isfinite(length)) {
        return false;
    }
    if (length <= 0.000001F) {
        return true;
    }
    const std::uint32_t base = static_cast<std::uint32_t>(vertices.size());
    const float shape_type = (affine_outline ? 14.0F : 3.0F) +
        (aliased ? 1000.0F : 0.0F);
    if (affine_outline) {
        const float inverse_length = 1.0F / length;
        const float half_thickness = local_thickness * 0.5F;
        const float normal_x = -delta_y * inverse_length * half_thickness;
        const float normal_y = delta_x * inverse_length * half_thickness;
        const progpu_native_point local_points[4] = {
            {local_start.x + normal_x, local_start.y + normal_y},
            {local_end.x + normal_x, local_end.y + normal_y},
            {local_end.x - normal_x, local_end.y - normal_y},
            {local_start.x - normal_x, local_start.y - normal_y}
        };
        progpu_native_point points[4]{};
        for (std::size_t index = 0U; index < 4U; ++index) {
            points[index] = transformed_point(transform, local_points[index]);
        }
        float minimum_x = points[0].x;
        float minimum_y = points[0].y;
        float maximum_x = points[0].x;
        float maximum_y = points[0].y;
        for (std::size_t index = 1U; index < 4U; ++index) {
            minimum_x = std::min(minimum_x, points[index].x);
            minimum_y = std::min(minimum_y, points[index].y);
            maximum_x = std::max(maximum_x, points[index].x);
            maximum_y = std::max(maximum_y, points[index].y);
        }
        minimum_x -= 1.5F;
        minimum_y -= 1.5F;
        maximum_x += 1.5F;
        maximum_y += 1.5F;
        const auto append_vertex = [&](float x, float y) {
            vector_vertex vertex{};
            vertex.position[0] = x;
            vertex.position[1] = y;
            vertex.color[0] = points[0].x;
            vertex.color[1] = points[0].y;
            vertex.color[2] = points[1].x;
            vertex.color[3] = points[1].y;
            vertex.texture_coordinate[0] = x;
            vertex.texture_coordinate[1] = y;
            vertex.brush_index = brush_index;
            vertex.shape_size[0] = points[2].x;
            vertex.shape_size[1] = points[2].y;
            vertex.corner_radius = points[3].x;
            vertex.stroke_thickness = points[3].y;
            vertex.shape_type = shape_type;
            vertices.push_back(vertex);
        };
        append_vertex(minimum_x, minimum_y);
        append_vertex(maximum_x, minimum_y);
        append_vertex(maximum_x, maximum_y);
        append_vertex(minimum_x, maximum_y);
    } else {
        const progpu_native_point start = transformed_point(
            transform,
            local_start);
        const progpu_native_point end = transformed_point(
            transform,
            local_end);
        const auto append_vertex = [&] (
            const progpu_native_point& position,
            float corner) {
            vector_vertex vertex{};
            vertex.position[0] = position.x;
            vertex.position[1] = position.y;
            set_color(vertex, color);
            vertex.texture_coordinate[0] = start.x;
            vertex.texture_coordinate[1] = start.y;
            vertex.brush_index = brush_index;
            vertex.shape_size[0] = end.x;
            vertex.shape_size[1] = end.y;
            vertex.corner_radius = corner;
            vertex.stroke_thickness = encoded_thickness;
            vertex.shape_type = shape_type;
            vertices.push_back(vertex);
        };
        append_vertex(start, 1.0F);
        append_vertex(start, -1.0F);
        append_vertex(end, 2.0F);
        append_vertex(end, -2.0F);
    }
    indices.push_back(base);
    indices.push_back(base + 1U);
    indices.push_back(base + 2U);
    indices.push_back(affine_outline ? base : base + 1U);
    indices.push_back(affine_outline ? base + 2U : base + 3U);
    indices.push_back(affine_outline ? base + 3U : base + 2U);
    return true;
}

struct dash_pattern_state {
    const double* intervals = nullptr;
    std::size_t source_count = 0U;
    std::size_t effective_count = 0U;
    std::size_t index = 0U;
    float distance = 0.0F;
    float thickness = 0.0F;
    std::uint32_t cap = PROGPU_NATIVE_STROKE_CAP_FLAT;
};

inline float resolved_dash_interval(
    const dash_pattern_state& pattern,
    std::size_t index) noexcept {
    constexpr float epsilon = 0.0001F;
    const double scaled =
        pattern.intervals[index % pattern.source_count] * pattern.thickness;
    if (!std::isfinite(scaled) || scaled < 0.0) {
        return std::numeric_limits<float>::quiet_NaN();
    }
    const float interval = static_cast<float>(scaled);
    return interval <= epsilon
        ? std::max(epsilon * 2.0F, pattern.thickness * 0.001F)
        : interval;
}

inline bool try_create_dash_pattern(
    const progpu_native_polyline& polyline,
    const progpu_native_dash_style* styles,
    std::size_t style_count,
    const double* doubles,
    std::size_t double_count,
    dash_pattern_state& pattern) noexcept {
    constexpr float epsilon = 0.0001F;
    pattern = {};
    if (polyline.dash_style == 0U) {
        return false;
    }
    const std::size_t style_index =
        static_cast<std::size_t>(polyline.dash_style - 1U);
    if (styles == nullptr || style_index >= style_count || doubles == nullptr) {
        return false;
    }
    const auto& style = styles[style_index];
    if (style.interval_count == 0U ||
        style.interval_offset > double_count ||
        style.interval_count > double_count - style.interval_offset ||
        style.interval_count > std::numeric_limits<std::size_t>::max() / 2U ||
        !std::isfinite(style.offset) ||
        style.cap > PROGPU_NATIVE_STROKE_CAP_TRIANGLE ||
        style.reserved != 0U) {
        return false;
    }
    const bool hairline =
        (polyline.flags & PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE) != 0U;
    const float thickness = hairline ? 1.0F : polyline.stroke_thickness;
    if (!std::isfinite(thickness) || thickness <= 0.0F) {
        return false;
    }
    pattern.intervals = doubles + style.interval_offset;
    pattern.source_count = style.interval_count;
    pattern.effective_count = (style.interval_count & 1U) == 0U
        ? style.interval_count
        : style.interval_count * 2U;
    pattern.thickness = thickness;
    pattern.cap = style.cap;
    float pattern_length = 0.0F;
    for (std::size_t index = 0U; index < pattern.effective_count; ++index) {
        const float interval = resolved_dash_interval(pattern, index);
        if (!std::isfinite(interval) || interval <= epsilon ||
            pattern_length > std::numeric_limits<float>::max() - interval) {
            return false;
        }
        pattern_length += interval;
    }
    const double scaled_offset = style.offset * thickness;
    if (!std::isfinite(scaled_offset) ||
        !std::isfinite(pattern_length) || pattern_length <= epsilon) {
        return false;
    }
    float distance = std::fmod(
        static_cast<float>(scaled_offset),
        pattern_length);
    if (!std::isfinite(distance)) {
        return false;
    }
    if (distance < 0.0F) {
        distance += pattern_length;
    }
    while (distance >= resolved_dash_interval(pattern, pattern.index)) {
        distance -= resolved_dash_interval(pattern, pattern.index);
        pattern.index = (pattern.index + 1U) % pattern.effective_count;
    }
    pattern.distance = distance;
    return true;
}

inline void advance_dash_pattern(
    dash_pattern_state& pattern,
    float remaining,
    float step) noexcept {
    constexpr float epsilon = 0.0001F;
    if (step >= remaining - epsilon) {
        pattern.distance = 0.0F;
        pattern.index = (pattern.index + 1U) % pattern.effective_count;
    } else {
        pattern.distance += step;
    }
}

template <typename Body, typename Cap, typename Join>
inline bool walk_dashed_polyline(
    const progpu_native_polyline& polyline,
    const progpu_native_point* points,
    dash_pattern_state pattern,
    Body&& append_body,
    Cap&& append_cap,
    Join&& append_join) {
    constexpr float epsilon = 0.0001F;
    const bool closed =
        (polyline.flags & PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) != 0U;
    const bool device_space =
        (polyline.flags & (PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE |
            PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE)) != 0U;
    const std::size_t segment_count = closed
        ? polyline.point_count
        : polyline.point_count - 1U;
    const auto point_at = [&](std::size_t index) {
        return device_space
            ? transformed_point(polyline.transform, points[index])
            : points[index];
    };

    bool first_visible = false;
    bool last_visible = false;
    bool continuing_run = false;
    bool have_first_direction = false;
    bool have_previous_direction = false;
    progpu_native_point first_direction{};
    progpu_native_point previous_direction{};
    progpu_native_point first_visible_direction{};
    progpu_native_point last_visible_direction{};
    progpu_native_point first_visible_point{};
    progpu_native_point last_visible_point{};

    for (std::size_t segment = 0U; segment < segment_count; ++segment) {
        const std::size_t next = (segment + 1U) % polyline.point_count;
        const progpu_native_point start = point_at(segment);
        const progpu_native_point end = point_at(next);
        const progpu_native_point delta{end.x - start.x, end.y - start.y};
        const float length = std::hypot(delta.x, delta.y);
        if (!std::isfinite(length)) {
            return false;
        }
        if (length <= epsilon) {
            continue;
        }
        const progpu_native_point direction{
            delta.x / length,
            delta.y / length
        };
        if (!have_first_direction) {
            first_direction = direction;
            have_first_direction = true;
        }
        if (continuing_run && have_previous_direction) {
            if (!append_join(start, previous_direction, direction)) {
                return false;
            }
        }

        float distance = 0.0F;
        bool segment_ends_visible = false;
        bool segment_continues = false;
        while (distance < length - epsilon) {
            const float interval = resolved_dash_interval(pattern, pattern.index);
            if (!std::isfinite(interval) || interval <= epsilon) {
                return false;
            }
            const float remaining = interval - pattern.distance;
            const float step = std::min(remaining, length - distance);
            const bool on = (pattern.index & 1U) == 0U;
            const bool at_segment_start = distance <= epsilon;
            const bool at_segment_end = distance + step >= length - epsilon;
            const bool pattern_ends = step >= remaining - epsilon;
            if (on && step > epsilon) {
                const progpu_native_point body_start{
                    start.x + direction.x * distance,
                    start.y + direction.y * distance
                };
                const progpu_native_point body_end{
                    start.x + direction.x * (distance + step),
                    start.y + direction.y * (distance + step)
                };
                if (!append_body(body_start, body_end)) {
                    return false;
                }
                const bool starts_new_run =
                    !(continuing_run && at_segment_start);
                if (starts_new_run) {
                    const bool source_start = !closed && segment == 0U &&
                        at_segment_start;
                    const std::uint32_t cap = source_start
                        ? (polyline.flags & PROGPU_NATIVE_POLYLINE_START_CAP_MASK) >>
                            PROGPU_NATIVE_POLYLINE_START_CAP_SHIFT
                        : pattern.cap;
                    if (closed && segment == 0U && at_segment_start) {
                        first_visible = true;
                        first_visible_point = body_start;
                        first_visible_direction = direction;
                    } else if (!append_cap(
                            cap,
                            body_start,
                            direction,
                            true)) {
                        return false;
                    }
                }

                const bool source_end = !closed &&
                    segment + 1U == segment_count && at_segment_end;
                if (source_end) {
                    const std::uint32_t cap =
                        (polyline.flags & PROGPU_NATIVE_POLYLINE_END_CAP_MASK) >>
                        PROGPU_NATIVE_POLYLINE_END_CAP_SHIFT;
                    if (!append_cap(cap, body_end, direction, false)) {
                        return false;
                    }
                } else if (closed && segment + 1U == segment_count &&
                    at_segment_end) {
                    last_visible = true;
                    last_visible_point = body_end;
                    last_visible_direction = direction;
                } else if (pattern_ends) {
                    if (!append_cap(
                            pattern.cap,
                            body_end,
                            direction,
                            false)) {
                        return false;
                    }
                }
                segment_ends_visible = at_segment_end;
                segment_continues = at_segment_end && !pattern_ends &&
                    !source_end;
            } else if (at_segment_end) {
                segment_ends_visible = false;
                segment_continues = false;
            }
            advance_dash_pattern(pattern, remaining, step);
            distance += step;
        }
        continuing_run = segment_ends_visible && segment_continues;
        previous_direction = direction;
        have_previous_direction = true;
    }

    if (closed) {
        if (first_visible && last_visible && have_first_direction &&
            have_previous_direction) {
            if (!append_join(
                    first_visible_point,
                    last_visible_direction,
                    first_visible_direction)) {
                return false;
            }
        } else {
            if (first_visible && !append_cap(
                    pattern.cap,
                    first_visible_point,
                    first_visible_direction,
                    true)) {
                return false;
            }
            if (last_visible && !append_cap(
                    pattern.cap,
                    last_visible_point,
                    last_visible_direction,
                    false)) {
                return false;
            }
        }
    }
    return true;
}

inline bool polyline_capacity(
    const progpu_native_polyline& polyline,
    std::size_t& vertex_count,
    std::size_t& index_count) noexcept {
    const bool closed =
        (polyline.flags & PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) != 0U;
    if (polyline.point_count < 2U ||
        (closed && polyline.point_count < 3U)) {
        return false;
    }
    const std::size_t segment_count = closed
        ? polyline.point_count
        : polyline.point_count - 1U;
    const std::size_t join_count = closed
        ? polyline.point_count
        : polyline.point_count - 2U;
    const std::size_t cap_count = closed
        ? 0U
        : ((polyline.flags & PROGPU_NATIVE_POLYLINE_START_CAP_MASK) != 0U
            ? 1U : 0U) +
          ((polyline.flags & PROGPU_NATIVE_POLYLINE_END_CAP_MASK) != 0U
            ? 1U : 0U);
    if (segment_count > std::numeric_limits<std::size_t>::max() / 4U ||
        join_count > std::numeric_limits<std::size_t>::max() / 32U ||
        cap_count > std::numeric_limits<std::size_t>::max() / 32U) {
        return false;
    }
    vertex_count = segment_count * 4U + join_count * 32U + cap_count * 32U;
    index_count = segment_count * 6U + join_count * 48U + cap_count * 48U;
    return true;
}

inline bool polyline_capacity(
    const progpu_native_polyline& polyline,
    const progpu_native_point* points,
    const progpu_native_dash_style* dash_styles,
    std::size_t dash_style_count,
    const double* doubles,
    std::size_t double_count,
    std::size_t& vertex_count,
    std::size_t& index_count) {
    if (polyline.dash_style == 0U) {
        return polyline_capacity(polyline, vertex_count, index_count);
    }
    if (points == nullptr) {
        return false;
    }
    dash_pattern_state pattern{};
    if (!try_create_dash_pattern(
            polyline,
            dash_styles,
            dash_style_count,
            doubles,
            double_count,
            pattern)) {
        return false;
    }
    vertex_count = 0U;
    index_count = 0U;
    bool valid = true;
    const auto add = [&](std::size_t vertices_to_add,
                         std::size_t indices_to_add) {
        if (vertex_count > std::numeric_limits<std::size_t>::max() -
                vertices_to_add ||
            index_count > std::numeric_limits<std::size_t>::max() -
                indices_to_add) {
            valid = false;
            return false;
        }
        vertex_count += vertices_to_add;
        index_count += indices_to_add;
        return true;
    };
    const auto body = [&](const progpu_native_point&,
                          const progpu_native_point&) {
        return add(4U, 6U);
    };
    const auto cap = [&](std::uint32_t cap_kind,
                         const progpu_native_point&,
                         const progpu_native_point&,
                         bool) {
        return cap_kind == PROGPU_NATIVE_STROKE_CAP_FLAT || add(32U, 48U);
    };
    const auto join = [&](const progpu_native_point&,
                          const progpu_native_point&,
                          const progpu_native_point&) {
        return add(32U, 48U);
    };
    return walk_dashed_polyline(
        polyline,
        points,
        pattern,
        body,
        cap,
        join) && valid;
}

inline bool append_polyline(
    const progpu_native_polyline& polyline,
    const progpu_native_point* points,
    float brush_index,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices,
    const progpu_native_dash_style* dash_styles = nullptr,
    std::size_t dash_style_count = 0U,
    const double* doubles = nullptr,
    std::size_t double_count = 0U,
    bool capacity_prevalidated = false) {
    constexpr std::uint32_t all_flags =
        PROGPU_NATIVE_POLYLINE_FLAG_EDGE_ALIASED |
        PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE |
        PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE |
        PROGPU_NATIVE_POLYLINE_START_CAP_MASK |
        PROGPU_NATIVE_POLYLINE_END_CAP_MASK |
        PROGPU_NATIVE_POLYLINE_JOIN_MASK |
        PROGPU_NATIVE_POLYLINE_FLAG_CLOSED;
    const std::uint32_t join =
        (polyline.flags & PROGPU_NATIVE_POLYLINE_JOIN_MASK) >>
        PROGPU_NATIVE_POLYLINE_JOIN_SHIFT;
    const bool closed =
        (polyline.flags & PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) != 0U;
    const bool hairline =
        (polyline.flags & PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE) != 0U;
    const bool fixed_device =
        (polyline.flags &
            PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE) != 0U;
    if (points == nullptr || polyline.point_count < 2U ||
        (closed && polyline.point_count < 3U) ||
        (polyline.flags & ~all_flags) != 0U ||
        join > PROGPU_NATIVE_STROKE_JOIN_ROUND ||
        !is_finite(polyline.color) || !is_finite(polyline.transform) ||
        !std::isfinite(polyline.stroke_thickness) ||
        !std::isfinite(polyline.miter_limit) ||
        (hairline && fixed_device) ||
        (hairline && polyline.stroke_thickness != 0.0F) ||
        (!hairline && polyline.stroke_thickness <= 0.0F)) {
        return false;
    }
    for (std::size_t index = 0U; index < polyline.point_count; ++index) {
        if (!is_finite(points[index])) {
            return false;
        }
    }

    std::size_t capacity_vertices = 0U;
    std::size_t capacity_indices = 0U;
    if ((!capacity_prevalidated && !polyline_capacity(
            polyline,
            points,
            dash_styles,
            dash_style_count,
            doubles,
            double_count,
            capacity_vertices,
            capacity_indices)) ||
        vertices.size() > std::numeric_limits<std::uint32_t>::max() -
            capacity_vertices ||
        vertices.size() > std::numeric_limits<std::size_t>::max() -
            capacity_vertices ||
        indices.size() > std::numeric_limits<std::size_t>::max() -
            capacity_indices) {
        return false;
    }
    const std::size_t initial_vertex_count = vertices.size();
    const std::size_t initial_index_count = indices.size();
    const bool aliased =
        (polyline.flags & PROGPU_NATIVE_POLYLINE_FLAG_EDGE_ALIASED) != 0U;
    float maximum_scale = 0.0F;
    float minimum_scale = 0.0F;
    if (!try_get_stroke_scales(
            polyline.transform,
            maximum_scale,
            minimum_scale)) {
        return false;
    }
    const bool affine_outline = !hairline && !fixed_device &&
        requires_affine_stroke_geometry(polyline.transform);
    const float encoded_thickness = hairline
        ? -1.0F
        : fixed_device
            ? -std::max(
                polyline.stroke_thickness + 1.0F,
                std::nextafter(1.0F, 2.0F))
            : polyline.stroke_thickness * maximum_scale;
    const std::uint32_t primitive_flags =
        (aliased ? PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED : 0U) |
        (hairline ? PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE : 0U) |
        (fixed_device
            ? PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE
            : 0U);

    const auto make_segment = [&](std::size_t first, std::size_t second) {
        progpu_native_geometry_primitive segment{};
        segment.kind = PROGPU_NATIVE_GEOMETRY_LINE;
        segment.flags = primitive_flags;
        segment.p0 = points[first];
        segment.p1 = points[second];
        segment.stroke_thickness = polyline.stroke_thickness;
        segment.color = polyline.color;
        segment.transform = polyline.transform;
        return segment;
    };
    const auto rollback = [&]() {
        vertices.resize(initial_vertex_count);
        indices.resize(initial_index_count);
        return false;
    };

    if (polyline.dash_style != 0U) {
        dash_pattern_state pattern{};
        if (!try_create_dash_pattern(
                polyline,
                dash_styles,
                dash_style_count,
                doubles,
                double_count,
                pattern)) {
            return rollback();
        }
        const progpu_native_affine_2d identity{
            1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F
        };
        const auto& body_transform = hairline || fixed_device
            ? identity
            : polyline.transform;
        const auto append_body = [&](const progpu_native_point& start,
                                     const progpu_native_point& end) {
            return append_connected_line_body(
                start,
                end,
                body_transform,
                polyline.color,
                polyline.stroke_thickness,
                encoded_thickness,
                affine_outline,
                brush_index,
                aliased,
                vertices,
                indices);
        };
        const auto append_cap_at = [&](std::uint32_t cap,
                                       const progpu_native_point& center,
                                       const progpu_native_point& direction,
                                       bool is_start) {
            if (hairline || fixed_device) {
                append_device_cap(
                    cap,
                    center,
                    direction,
                    is_start,
                    encoded_thickness,
                    brush_index,
                    aliased,
                    vertices,
                    indices);
                return true;
            }
            if (affine_outline) {
                return append_cpu_cap(
                    cap,
                    polyline.stroke_thickness,
                    center,
                    direction,
                    is_start,
                    &polyline.transform,
                    brush_index,
                    aliased,
                    vertices,
                    indices);
            }
            return append_cpu_cap(
                cap,
                polyline.stroke_thickness * maximum_scale,
                transformed_point(polyline.transform, center),
                transformed_direction(polyline.transform, direction),
                is_start,
                nullptr,
                brush_index,
                aliased,
                vertices,
                indices);
        };
        const auto append_join_at_points = [&] (
            const progpu_native_point& point,
            const progpu_native_point& incoming,
            const progpu_native_point& outgoing) {
            if (hairline || fixed_device) {
                append_device_join(
                    join,
                    polyline.miter_limit,
                    point,
                    incoming,
                    outgoing,
                    encoded_thickness,
                    brush_index,
                    aliased,
                    vertices,
                    indices);
                return true;
            }
            if (affine_outline) {
                return append_cpu_join(
                    join,
                    polyline.stroke_thickness,
                    polyline.miter_limit,
                    point,
                    incoming,
                    outgoing,
                    &polyline.transform,
                    brush_index,
                    aliased,
                    vertices,
                    indices);
            }
            return append_cpu_join(
                join,
                polyline.stroke_thickness * maximum_scale,
                polyline.miter_limit,
                transformed_point(polyline.transform, point),
                transformed_direction(polyline.transform, incoming),
                transformed_direction(polyline.transform, outgoing),
                nullptr,
                brush_index,
                aliased,
                vertices,
                indices);
        };
        if (!walk_dashed_polyline(
                polyline,
                points,
                pattern,
                append_body,
                append_cap_at,
                append_join_at_points)) {
            return rollback();
        }
        return true;
    }

    const auto append_join_at = [&](std::size_t previous,
                                    std::size_t current,
                                    std::size_t next) {
        const progpu_native_point incoming{
            points[current].x - points[previous].x,
            points[current].y - points[previous].y
        };
        const progpu_native_point outgoing{
            points[next].x - points[current].x,
            points[next].y - points[current].y
        };
        if (hairline || fixed_device) {
            append_device_join(
                join,
                polyline.miter_limit,
                transformed_point(polyline.transform, points[current]),
                transformed_direction(polyline.transform, incoming),
                transformed_direction(polyline.transform, outgoing),
                encoded_thickness,
                brush_index,
                aliased,
                vertices,
                indices);
            return true;
        }
        if (affine_outline) {
            return append_cpu_join(
                join,
                polyline.stroke_thickness,
                polyline.miter_limit,
                points[current],
                incoming,
                outgoing,
                &polyline.transform,
                brush_index,
                aliased,
                vertices,
                indices);
        }
        return append_cpu_join(
            join,
            polyline.stroke_thickness * maximum_scale,
            polyline.miter_limit,
            transformed_point(polyline.transform, points[current]),
            transformed_direction(polyline.transform, incoming),
            transformed_direction(polyline.transform, outgoing),
            nullptr,
            brush_index,
            aliased,
            vertices,
            indices);
    };

    if (!closed) {
        auto start = make_segment(0U, 1U);
        start.flags |= polyline.flags & PROGPU_NATIVE_POLYLINE_START_CAP_MASK;
        if (!append_primitive_caps(
                start,
                hairline,
                fixed_device,
                affine_outline,
                maximum_scale,
                encoded_thickness,
                brush_index,
                aliased,
                1U,
                vertices,
                indices)) {
            return rollback();
        }
    }

    const std::size_t segment_count = closed
        ? polyline.point_count
        : polyline.point_count - 1U;
    for (std::size_t index = 0U; index < segment_count; ++index) {
        const std::size_t next = (index + 1U) % polyline.point_count;
        if (!append_connected_line_body(
                points[index],
                points[next],
                polyline.transform,
                polyline.color,
                polyline.stroke_thickness,
                encoded_thickness,
                affine_outline,
                brush_index,
                aliased,
                vertices,
                indices)) {
            return rollback();
        }
        if (closed || next + 1U < polyline.point_count) {
            const std::size_t after = (next + 1U) % polyline.point_count;
            if (!append_join_at(index, next, after)) {
                return rollback();
            }
        }
    }

    if (!closed) {
        auto end = make_segment(
            polyline.point_count - 2U,
            polyline.point_count - 1U);
        end.flags |= polyline.flags & PROGPU_NATIVE_POLYLINE_END_CAP_MASK;
        if (!append_primitive_caps(
                end,
                hairline,
                fixed_device,
                affine_outline,
                maximum_scale,
                encoded_thickness,
                brush_index,
                aliased,
                2U,
                vertices,
                indices)) {
            return rollback();
        }
    }
    return true;
}

struct spline_homogeneous_point {
    float x;
    float y;
    float weight;
};

inline bool try_get_spline_domain(
    const progpu_native_spline& spline,
    const double* knots,
    double& start_knot,
    double& end_knot) noexcept {
    start_knot = 0.0;
    end_knot = 0.0;
    const std::size_t degree = spline.degree;
    const std::size_t control_count = spline.stroke.point_count;
    if (knots == nullptr || control_count < 2U ||
        degree > std::numeric_limits<std::size_t>::max() - control_count - 1U ||
        spline.knot_count < control_count + degree + 1U ||
        degree >= spline.knot_count) {
        return false;
    }
    const std::size_t end_index = spline.knot_count - degree - 1U;
    if (end_index <= degree || end_index >= spline.knot_count) {
        return false;
    }
    start_knot = knots[degree];
    end_knot = knots[end_index];
    return std::isfinite(start_knot) && std::isfinite(end_knot) &&
        end_knot > start_knot;
}

inline bool try_get_spline_segment_count(
    const progpu_native_spline& spline,
    const progpu_native_point* control_points,
    std::size_t& segment_count) noexcept {
    segment_count = 0U;
    if (control_points == nullptr || spline.stroke.point_count == 0U ||
        !is_finite(spline.stroke.transform)) {
        return false;
    }
    float minimum_x = std::numeric_limits<float>::max();
    float minimum_y = std::numeric_limits<float>::max();
    float maximum_x = std::numeric_limits<float>::lowest();
    float maximum_y = std::numeric_limits<float>::lowest();
    for (std::size_t index = 0U;
         index < spline.stroke.point_count;
         ++index) {
        if (!is_finite(control_points[index])) {
            return false;
        }
        const progpu_native_point screen = transformed_point(
            spline.stroke.transform,
            control_points[index]);
        if (!is_finite(screen)) {
            return false;
        }
        minimum_x = std::min(minimum_x, screen.x);
        minimum_y = std::min(minimum_y, screen.y);
        maximum_x = std::max(maximum_x, screen.x);
        maximum_y = std::max(maximum_y, screen.y);
    }
    const float extent = std::hypot(
        maximum_x - minimum_x,
        maximum_y - minimum_y);
    if (!std::isfinite(extent)) {
        return false;
    }
    if (extent < 2.0F) {
        return true;
    }
    segment_count = extent < 20.0F
        ? 10U
        : extent < 80.0F
            ? 25U
            : extent < 250.0F ? 50U : 100U;
    return true;
}

inline bool try_evaluate_spline_point(
    const progpu_native_spline& spline,
    const progpu_native_point* control_points,
    const double* knots,
    const double* weights,
    double parameter,
    std::vector<spline_homogeneous_point>& work,
    progpu_native_point& output) {
    const std::size_t degree = spline.degree;
    const std::size_t end_index = spline.knot_count - degree - 1U;
    parameter = std::clamp(parameter, knots[degree], knots[end_index]);
    std::size_t span = std::numeric_limits<std::size_t>::max();
    for (std::size_t index = degree;
         index + 1U < spline.knot_count;
         ++index) {
        if (parameter >= knots[index] && parameter <= knots[index + 1U]) {
            span = index;
            break;
        }
    }
    if (span == std::numeric_limits<std::size_t>::max()) {
        span = spline.knot_count - degree - 2U;
    }

    work.resize(degree + 1U);
    for (std::size_t index = 0U; index <= degree; ++index) {
        const std::ptrdiff_t control_index =
            static_cast<std::ptrdiff_t>(span) -
            static_cast<std::ptrdiff_t>(degree) +
            static_cast<std::ptrdiff_t>(index);
        if (control_index >= 0 &&
            static_cast<std::size_t>(control_index) <
                spline.stroke.point_count) {
            float weight = 1.0F;
            if (weights != nullptr &&
                static_cast<std::size_t>(control_index) <
                    spline.weight_count) {
                weight = static_cast<float>(weights[control_index]);
            }
            const auto& control = control_points[control_index];
            work[index] = {
                control.x * weight,
                control.y * weight,
                weight
            };
        } else {
            work[index] = {};
        }
    }

    for (std::size_t level = 1U; level <= degree; ++level) {
        for (std::size_t index = degree; index >= level; --index) {
            const std::size_t knot_index = span - degree + index;
            const double denominator =
                knots[knot_index + degree + 1U - level] -
                knots[knot_index];
            const float alpha = denominator > 1.0e-9
                ? static_cast<float>(
                    (parameter - knots[knot_index]) / denominator)
                : 0.0F;
            const float inverse = 1.0F - alpha;
            work[index] = {
                inverse * work[index - 1U].x + alpha * work[index].x,
                inverse * work[index - 1U].y + alpha * work[index].y,
                inverse * work[index - 1U].weight +
                    alpha * work[index].weight
            };
        }
    }

    const auto final = work[degree];
    output = std::abs(final.weight) > 1.0e-9F
        ? progpu_native_point{
            final.x / final.weight,
            final.y / final.weight}
        : progpu_native_point{final.x, final.y};
    return is_finite(output);
}

inline bool spline_capacity(
    const progpu_native_spline& spline,
    const progpu_native_point* control_points,
    const double* knots,
    std::size_t& segment_count,
    std::size_t& vertex_count,
    std::size_t& index_count) noexcept {
    if (spline.reserved != 0U || spline.degree > (1U << 20U)) {
        return false;
    }
    if (spline.stroke.point_count < 2U || spline.knot_count == 0U) {
        segment_count = 0U;
        vertex_count = 0U;
        index_count = 0U;
        return true;
    }
    double start_knot = 0.0;
    double end_knot = 0.0;
    if (!try_get_spline_domain(
            spline,
            knots,
            start_knot,
            end_knot)) {
        segment_count = spline.stroke.point_count - 1U;
        return polyline_capacity(spline.stroke, vertex_count, index_count);
    }
    if (!try_get_spline_segment_count(
            spline,
            control_points,
            segment_count)) {
        return false;
    }
    if (segment_count == 0U) {
        vertex_count = 0U;
        index_count = 0U;
        return true;
    }
    progpu_native_polyline sampled_stroke = spline.stroke;
    sampled_stroke.point_offset = 0U;
    sampled_stroke.point_count = segment_count + 1U;
    return polyline_capacity(sampled_stroke, vertex_count, index_count);
}

inline bool spline_capacity(
    const progpu_native_spline& spline,
    const progpu_native_point* control_points,
    const double* knots,
    const double* weights,
    const progpu_native_dash_style* dash_styles,
    std::size_t dash_style_count,
    const double* doubles,
    std::size_t double_count,
    std::size_t& segment_count,
    std::array<progpu_native_point, 101U>& sampled_points,
    std::vector<spline_homogeneous_point>& work,
    std::size_t& vertex_count,
    std::size_t& index_count) {
    if (!spline_capacity(
            spline,
            control_points,
            knots,
            segment_count,
            vertex_count,
            index_count)) {
        return false;
    }
    if (spline.stroke.dash_style == 0U || segment_count == 0U) {
        return true;
    }
    double start_knot = 0.0;
    double end_knot = 0.0;
    if (!try_get_spline_domain(spline, knots, start_knot, end_knot)) {
        return polyline_capacity(
            spline.stroke,
            control_points,
            dash_styles,
            dash_style_count,
            doubles,
            double_count,
            vertex_count,
            index_count);
    }
    if (segment_count >= sampled_points.size()) {
        return false;
    }
    const double delta =
        (end_knot - start_knot) / static_cast<double>(segment_count);
    for (std::size_t index = 0U; index <= segment_count; ++index) {
        if (!try_evaluate_spline_point(
                spline,
                control_points,
                knots,
                weights,
                start_knot + static_cast<double>(index) * delta,
                work,
                sampled_points[index])) {
            return false;
        }
    }
    progpu_native_polyline sampled_stroke = spline.stroke;
    sampled_stroke.point_offset = 0U;
    sampled_stroke.point_count = segment_count + 1U;
    return polyline_capacity(
        sampled_stroke,
        sampled_points.data(),
        dash_styles,
        dash_style_count,
        doubles,
        double_count,
        vertex_count,
        index_count);
}

inline bool append_spline(
    const progpu_native_spline& spline,
    const progpu_native_point* control_points,
    const double* knots,
    const double* weights,
    std::size_t segment_count,
    float brush_index,
    std::array<progpu_native_point, 101U>& sampled_points,
    std::vector<spline_homogeneous_point>& work,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices,
    const progpu_native_dash_style* dash_styles = nullptr,
    std::size_t dash_style_count = 0U,
    const double* doubles = nullptr,
    std::size_t double_count = 0U) {
    double start_knot = 0.0;
    double end_knot = 0.0;
    if (!try_get_spline_domain(
            spline,
            knots,
            start_knot,
            end_knot)) {
        return append_polyline(
            spline.stroke,
            control_points,
            brush_index,
            vertices,
            indices,
            dash_styles,
            dash_style_count,
            doubles,
            double_count);
    }
    if (segment_count == 0U || segment_count >= sampled_points.size()) {
        return segment_count == 0U;
    }
    const double delta =
        (end_knot - start_knot) / static_cast<double>(segment_count);
    for (std::size_t index = 0U; index <= segment_count; ++index) {
        if (!try_evaluate_spline_point(
                spline,
                control_points,
                knots,
                weights,
                start_knot + static_cast<double>(index) * delta,
                work,
                sampled_points[index])) {
            return false;
        }
    }
    progpu_native_polyline sampled_stroke = spline.stroke;
    sampled_stroke.point_offset = 0U;
    sampled_stroke.point_count = segment_count + 1U;
    return append_polyline(
        sampled_stroke,
        sampled_points.data(),
        brush_index,
        vertices,
        indices,
        dash_styles,
        dash_style_count,
        doubles,
        double_count);
}

inline bool append_analytic_primitive(
    const progpu_native_analytic_primitive& primitive,
    float antialias_padding,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    if (primitive.kind > PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE ||
        (primitive.flags & ~PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED) != 0U ||
        !std::isfinite(primitive.x) || !std::isfinite(primitive.y) ||
        !std::isfinite(primitive.width) || !std::isfinite(primitive.height) ||
        primitive.width < 0.0F || primitive.height < 0.0F ||
        !std::isfinite(primitive.corner_radius) ||
        !std::isfinite(primitive.stroke_thickness) ||
        primitive.stroke_thickness < 0.0F ||
        !is_finite(primitive.color) || !is_finite(primitive.transform) ||
        !std::isfinite(antialias_padding) || antialias_padding <= 0.0F ||
        vertices.size() > std::numeric_limits<std::uint32_t>::max() - 4U ||
        vertices.size() > std::numeric_limits<std::size_t>::max() - 4U ||
        indices.size() > std::numeric_limits<std::size_t>::max() - 6U) {
        return false;
    }

    float radius = 0.0F;
    if (primitive.kind == PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE) {
        radius = std::min(
            std::abs(primitive.corner_radius),
            std::min(primitive.width, primitive.height) * 0.5F);
    }
    const float stroke_padding = primitive.stroke_thickness * 0.5F;
    const float padding = antialias_padding + stroke_padding;
    const float half_width = primitive.width * 0.5F;
    const float half_height = primitive.height * 0.5F;
    const float left = primitive.x - padding;
    const float top = primitive.y - padding;
    const float right = primitive.x + primitive.width + padding;
    const float bottom = primitive.y + primitive.height + padding;
    const float local_left = -half_width - padding;
    const float local_top = -half_height - padding;
    const float local_right = half_width + padding;
    const float local_bottom = half_height + padding;
    const float encoded_shape_type =
        static_cast<float>(primitive.kind) +
        ((primitive.flags & PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED) != 0U
            ? 1000.0F
            : 0.0F);

    const auto make_vertex = [&](float x, float y, float u, float v) {
        vector_vertex vertex{};
        transform_point(
            primitive.transform,
            x,
            y,
            vertex.position[0],
            vertex.position[1]);
        vertex.color[0] = primitive.color.r;
        vertex.color[1] = primitive.color.g;
        vertex.color[2] = primitive.color.b;
        vertex.color[3] = primitive.color.a;
        vertex.texture_coordinate[0] = u;
        vertex.texture_coordinate[1] = v;
        vertex.shape_size[0] = primitive.width;
        vertex.shape_size[1] = primitive.height;
        vertex.corner_radius = radius;
        vertex.stroke_thickness = primitive.stroke_thickness;
        vertex.shape_type = encoded_shape_type;
        return vertex;
    };

    const std::uint32_t base = static_cast<std::uint32_t>(vertices.size());
    vertices.push_back(make_vertex(left, top, local_left, local_top));
    vertices.push_back(make_vertex(right, top, local_right, local_top));
    vertices.push_back(make_vertex(right, bottom, local_right, local_bottom));
    vertices.push_back(make_vertex(left, bottom, local_left, local_bottom));
    indices.push_back(base);
    indices.push_back(base + 1U);
    indices.push_back(base + 2U);
    indices.push_back(base);
    indices.push_back(base + 2U);
    indices.push_back(base + 3U);
    return true;
}

inline bool append_solid_rect(
    const progpu_native_rect& rect,
    float antialias_padding,
    std::vector<vector_vertex>& destination) {
    if (!is_finite(rect) || rect.width < 0.0F || rect.height < 0.0F ||
        !std::isfinite(antialias_padding) || antialias_padding <= 0.0F) {
        return false;
    }

    if (destination.size() >
        std::numeric_limits<std::size_t>::max() - 6U) {
        return false;
    }

    const float half_width = rect.width * 0.5F;
    const float half_height = rect.height * 0.5F;
    const float left = rect.x - antialias_padding;
    const float top = rect.y - antialias_padding;
    const float right = rect.x + rect.width + antialias_padding;
    const float bottom = rect.y + rect.height + antialias_padding;
    const float local_left = -half_width - antialias_padding;
    const float local_top = -half_height - antialias_padding;
    const float local_right = half_width + antialias_padding;
    const float local_bottom = half_height + antialias_padding;

    const auto make_vertex = [&](float x, float y, float u, float v) {
        vector_vertex vertex{};
        vertex.position[0] = x;
        vertex.position[1] = y;
        vertex.color[0] = rect.color.r;
        vertex.color[1] = rect.color.g;
        vertex.color[2] = rect.color.b;
        vertex.color[3] = rect.color.a;
        vertex.texture_coordinate[0] = u;
        vertex.texture_coordinate[1] = v;
        vertex.shape_size[0] = rect.width;
        vertex.shape_size[1] = rect.height;
        return vertex;
    };

    const vector_vertex top_left =
        make_vertex(left, top, local_left, local_top);
    const vector_vertex top_right =
        make_vertex(right, top, local_right, local_top);
    const vector_vertex bottom_right =
        make_vertex(right, bottom, local_right, local_bottom);
    const vector_vertex bottom_left =
        make_vertex(left, bottom, local_left, local_bottom);

    destination.push_back(top_left);
    destination.push_back(top_right);
    destination.push_back(bottom_right);
    destination.push_back(top_left);
    destination.push_back(bottom_right);
    destination.push_back(bottom_left);
    return true;
}

} // namespace progpu::native
