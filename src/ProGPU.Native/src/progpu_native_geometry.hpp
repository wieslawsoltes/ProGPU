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

inline bool try_get_minimum_scale(
    const progpu_native_affine_2d& transform,
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
    const double minimum_squared = std::max(
        0.0,
        (sum - std::sqrt(discriminant)) * 0.5);
    const double value = std::sqrt(minimum_squared);
    if (!std::isfinite(value) || value <= 0.000001) {
        return false;
    }
    minimum_scale = static_cast<float>(value);
    return std::isfinite(minimum_scale) && minimum_scale > 0.000001F;
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
