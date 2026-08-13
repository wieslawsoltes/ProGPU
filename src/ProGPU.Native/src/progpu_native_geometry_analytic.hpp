#pragma once

#include "progpu_native_geometry_base.hpp"

namespace progpu::native {

inline bool is_valid_analytic_primitive(
    const progpu_native_analytic_primitive& primitive,
    float antialias_padding) noexcept {
    if (primitive.kind > PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE ||
        (primitive.flags & ~PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED) != 0U ||
        !std::isfinite(primitive.x) || !std::isfinite(primitive.y) ||
        !std::isfinite(primitive.width) || !std::isfinite(primitive.height) ||
        primitive.width < 0.0F || primitive.height < 0.0F ||
        !std::isfinite(primitive.corner_radius) ||
        !std::isfinite(primitive.stroke_thickness) ||
        primitive.stroke_thickness < 0.0F ||
        !is_finite(primitive.color) || !is_finite(primitive.transform) ||
        !std::isfinite(antialias_padding) || antialias_padding <= 0.0F) {
        return false;
    }
    return true;
}

inline bool append_analytic_primitive(
    const progpu_native_analytic_primitive& primitive,
    float antialias_padding,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    if (!is_valid_analytic_primitive(primitive, antialias_padding) ||
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
