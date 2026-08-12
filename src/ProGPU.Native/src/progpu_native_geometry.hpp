#pragma once

#include "progpu_native.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
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
static_assert(sizeof(progpu_native_analytic_primitive) == 72U);
static_assert(sizeof(progpu_native_point) == 8U);
static_assert(sizeof(progpu_native_geometry_primitive) == 88U);

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
        (primitive.flags & (
            PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
            PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE)) == 0U &&
        requires_affine_stroke_geometry(primitive.transform);
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
    const auto append = [&](float x, float y) {
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
        vertices.push_back(vertex);
    };
    append(min_x, min_y);
    append(max_x, min_y);
    append(max_x, max_y);
    append(min_x, max_y);
    indices.insert(indices.end(), {
        base, base + 1U, base + 2U,
        base, base + 2U, base + 3U
    });
    return true;
}

inline bool geometry_primitive_capacity(
    const progpu_native_geometry_primitive& primitive,
    std::size_t& vertex_count,
    std::size_t& index_count) noexcept {
    if (primitive.kind > PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER) {
        return false;
    }
    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_TRIANGLE) {
        vertex_count = 3U;
        index_count = 3U;
        return true;
    }
    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRILATERAL ||
        primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE) {
        vertex_count = 4U;
        index_count = 6U;
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
    vertex_count = affine ? segments * 4U : 2U * (segments + 1U);
    index_count = segments * 6U;
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
        PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE;
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
    const std::size_t maximum_vertices_to_add = is_curve
        ? uses_affine_curve ? curve_segments * 4U : 2U * (curve_segments + 1U)
        : 4U;
    const std::size_t maximum_indices_to_add = is_curve
        ? curve_segments * 6U
        : 6U;
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

    indices.push_back(base);
    indices.push_back(base + 1U);
    indices.push_back(base + 2U);
    indices.push_back(uses_affine_quad ? base : base + 1U);
    indices.push_back(uses_affine_quad ? base + 2U : base + 3U);
    indices.push_back(uses_affine_quad ? base + 3U : base + 2U);
    return true;
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
