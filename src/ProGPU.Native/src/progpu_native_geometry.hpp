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

inline bool append_geometry_primitive(
    const progpu_native_geometry_primitive& primitive,
    float brush_index,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    constexpr std::uint32_t all_line_flags =
        PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED |
        PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
        PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE;
    if (primitive.kind > PROGPU_NATIVE_GEOMETRY_QUADRILATERAL ||
        !std::isfinite(brush_index) || brush_index < 0.0F ||
        !is_finite(primitive.p0) || !is_finite(primitive.p1) ||
        !is_finite(primitive.p2) || !is_finite(primitive.p3) ||
        !std::isfinite(primitive.stroke_thickness) ||
        !std::isfinite(primitive.reserved) || primitive.reserved != 0.0F ||
        !is_finite(primitive.color) || !is_finite(primitive.transform) ||
        vertices.size() > std::numeric_limits<std::uint32_t>::max() - 4U ||
        vertices.size() > std::numeric_limits<std::size_t>::max() - 4U ||
        indices.size() > std::numeric_limits<std::size_t>::max() - 6U) {
        return false;
    }

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
    const float delta_x = primitive.p1.x - primitive.p0.x;
    const float delta_y = primitive.p1.y - primitive.p0.y;
    const float length = std::hypot(delta_x, delta_y);
    if (!std::isfinite(length)) {
        return false;
    }
    if (length <= 0.000001F) {
        return true;
    }

    float maximum_scale = 0.0F;
    float minimum_scale = 0.0F;
    if (!try_get_stroke_scales(
            primitive.transform,
            maximum_scale,
            minimum_scale)) {
        return false;
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
        const float encoded_thickness = hairline
            ? -1.0F
            : fixed_device
                ? -std::max(
                    primitive.stroke_thickness + 1.0F,
                    std::nextafter(1.0F, 2.0F))
                : primitive.stroke_thickness * maximum_scale;
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
