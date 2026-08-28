#pragma once

#include "progpu_native_geometry_base.hpp"

namespace progpu::native {

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

inline bool is_valid_geometry_primitive(
    const progpu_native_geometry_primitive& primitive) noexcept {
    constexpr std::uint32_t all_line_flags =
        PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED |
        PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
        PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE |
        PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK |
        PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK;
    if (primitive.kind > PROGPU_NATIVE_GEOMETRY_PATH_JOIN ||
        !is_finite(primitive.p0) || !is_finite(primitive.p1) ||
        !is_finite(primitive.p2) || !is_finite(primitive.p3) ||
        !std::isfinite(primitive.stroke_thickness) ||
        !std::isfinite(primitive.reserved) || primitive.reserved != 0.0F ||
        !is_finite(primitive.color) || !is_finite(primitive.transform)) {
        return false;
    }
    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_DOT_GRID) {
        return (primitive.flags &
                ~PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED) == 0U &&
            primitive.p1.x >= 0.0F && primitive.p1.y >= 0.0F &&
            primitive.p3.x > 0.0F && primitive.p3.y > 0.0F &&
            primitive.stroke_thickness == 0.0F;
    }
    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_TRIANGLE ||
        primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRILATERAL) {
        return (primitive.flags &
                ~PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED) == 0U &&
            primitive.stroke_thickness == 0.0F;
    }
    if ((primitive.flags & ~all_line_flags) != 0U) {
        return false;
    }
    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_ARC &&
        (std::abs(primitive.p1.x * primitive.p2.y -
                primitive.p1.y * primitive.p2.x) <= 0.000001F ||
            std::abs(primitive.p3.y) <= 0.000001F ||
            std::abs(primitive.p3.y) >
                std::numbers::pi_v<float> * 2.0F + 0.001F ||
            (primitive.flags & (
                PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK |
                PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK)) != 0U)) {
        return false;
    }
    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_CAP) {
        const std::uint32_t cap =
            (primitive.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >>
                PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT;
        if (cap == PROGPU_NATIVE_STROKE_CAP_FLAT ||
            (primitive.flags & PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK) != 0U ||
            (primitive.p2.x != 0.0F && primitive.p2.x != 1.0F) ||
            primitive.p2.y != 0.0F || primitive.p3.x != 0.0F ||
            primitive.p3.y != 0.0F) {
            return false;
        }
    }
    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_JOIN) {
        const std::uint32_t join =
            (primitive.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >>
                PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT;
        if (join > PROGPU_NATIVE_STROKE_JOIN_ROUND ||
            (primitive.flags & PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK) != 0U ||
            !(primitive.p3.x >= 1.0F) || primitive.p3.y != 0.0F) {
            return false;
        }
    }
    const bool hairline = (primitive.flags &
        PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE) != 0U;
    const bool fixed_device = (primitive.flags &
        PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE) != 0U;
    if ((hairline && fixed_device) ||
        (hairline && primitive.stroke_thickness != 0.0F) ||
        (!hairline && primitive.stroke_thickness <= 0.0F)) {
        return false;
    }
    float maximum_scale = 0.0F;
    float minimum_scale = 0.0F;
    return try_get_stroke_scales(
        primitive.transform,
        maximum_scale,
        minimum_scale);
}

inline bool geometry_primitive_capacity(
    const progpu_native_geometry_primitive& primitive,
    std::size_t& vertex_count,
    std::size_t& index_count) noexcept {
    if (!is_valid_geometry_primitive(primitive)) {
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
    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_CAP ||
        primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_JOIN) {
        vertex_count = 32U;
        index_count = 48U;
        return true;
    }
    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_ARC) {
        const bool affine =
            (primitive.flags & (
                PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
                PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE)) == 0U &&
            requires_affine_stroke_geometry(primitive.transform);
        const std::size_t sections = affine
            ? std::clamp(
                static_cast<std::size_t>(std::ceil(
                    std::abs(primitive.p3.y) /
                    (std::numbers::pi_v<float> / 24.0F))),
                std::size_t{1U},
                std::size_t{48U})
            : 1U;
        vertex_count = sections * 4U;
        index_count = sections * 6U;
        return true;
    }
    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRILATERAL ||
        primitive.kind == PROGPU_NATIVE_GEOMETRY_DOT_GRID ||
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
    std::vector<std::uint32_t>& indices);

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
    std::vector<std::uint32_t>& indices);

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
    if (!is_valid_geometry_primitive(primitive) ||
        !std::isfinite(brush_index) || brush_index < 0.0F) {
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
    std::size_t maximum_vertices_to_add = 0U;
    std::size_t maximum_indices_to_add = 0U;
    if (!geometry_primitive_capacity(
            primitive,
            maximum_vertices_to_add,
            maximum_indices_to_add)) {
        return false;
    }
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

    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_DOT_GRID) {
        const progpu_native_point points[4] = {
            primitive.p0,
            {primitive.p0.x + primitive.p1.x, primitive.p0.y},
            {primitive.p0.x + primitive.p1.x,
                primitive.p0.y + primitive.p1.y},
            {primitive.p0.x, primitive.p0.y + primitive.p1.y}
        };
        const auto append_dot_grid_vertex = [&](
            const progpu_native_point& point) {
            const auto position = transformed(point);
            vector_vertex vertex{};
            vertex.position[0] = position.x;
            vertex.position[1] = position.y;
            set_color(vertex, primitive.color);
            vertex.texture_coordinate[0] = point.x;
            vertex.texture_coordinate[1] = point.y;
            vertex.brush_index = brush_index;
            vertex.shape_size[0] = primitive.p3.x;
            vertex.shape_size[1] = primitive.p3.y;
            vertex.corner_radius = primitive.p2.x;
            vertex.stroke_thickness = primitive.p2.y;
            vertex.shape_type = 21.0F + alias_offset;
            vertices.push_back(vertex);
        };
        for (const auto& point : points) {
            append_dot_grid_vertex(point);
        }
        indices.push_back(base);
        indices.push_back(base + 1U);
        indices.push_back(base + 2U);
        indices.push_back(base);
        indices.push_back(base + 2U);
        indices.push_back(base + 3U);
        return true;
    }

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

    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_CAP) {
        const std::uint32_t cap =
            (primitive.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >>
                PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT;
        const bool is_start = primitive.p2.x == 1.0F;
        if (hairline || fixed_device) {
            append_device_cap(
                cap,
                transformed_point(primitive.transform, primitive.p0),
                transformed_direction(primitive.transform, primitive.p1),
                is_start,
                encoded_thickness,
                brush_index,
                aliased,
                vertices,
                indices);
            return true;
        }
        return append_cpu_cap(
            cap,
            affine_stroke_geometry
                ? primitive.stroke_thickness
                : primitive.stroke_thickness * maximum_scale,
            affine_stroke_geometry
                ? primitive.p0
                : transformed_point(primitive.transform, primitive.p0),
            affine_stroke_geometry
                ? primitive.p1
                : transformed_direction(primitive.transform, primitive.p1),
            is_start,
            affine_stroke_geometry ? &primitive.transform : nullptr,
            brush_index,
            aliased,
            vertices,
            indices);
    }

    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_JOIN) {
        const std::uint32_t join =
            (primitive.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >>
                PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT;
        if (hairline || fixed_device) {
            append_device_join(
                join,
                primitive.p3.x,
                transformed_point(primitive.transform, primitive.p0),
                transformed_direction(primitive.transform, primitive.p1),
                transformed_direction(primitive.transform, primitive.p2),
                encoded_thickness,
                brush_index,
                aliased,
                vertices,
                indices);
            return true;
        }
        return append_cpu_join(
            join,
            affine_stroke_geometry
                ? primitive.stroke_thickness
                : primitive.stroke_thickness * maximum_scale,
            primitive.p3.x,
            affine_stroke_geometry
                ? primitive.p0
                : transformed_point(primitive.transform, primitive.p0),
            affine_stroke_geometry
                ? primitive.p1
                : transformed_direction(primitive.transform, primitive.p1),
            affine_stroke_geometry
                ? primitive.p2
                : transformed_direction(primitive.transform, primitive.p2),
            affine_stroke_geometry ? &primitive.transform : nullptr,
            brush_index,
            aliased,
            vertices,
            indices);
    }

    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_ARC) {
        const progpu_native_point local_axis_x = primitive.p1;
        const progpu_native_point local_axis_y = primitive.p2;
        const auto evaluate_arc = [&](float theta) {
            return progpu_native_point{
                primitive.p0.x + local_axis_x.x * std::cos(theta) +
                    local_axis_y.x * std::sin(theta),
                primitive.p0.y + local_axis_x.y * std::cos(theta) +
                    local_axis_y.y * std::sin(theta)
            };
        };
        const auto evaluate_arc_tangent = [&](float theta) {
            const float direction = std::copysign(1.0F, primitive.p3.y);
            return progpu_native_point{
                (-local_axis_x.x * std::sin(theta) +
                    local_axis_y.x * std::cos(theta)) * direction,
                (-local_axis_x.y * std::sin(theta) +
                    local_axis_y.y * std::cos(theta)) * direction
            };
        };
        if (affine_stroke_geometry) {
            const std::size_t sections = std::clamp(
                static_cast<std::size_t>(std::ceil(
                    std::abs(primitive.p3.y) /
                    (std::numbers::pi_v<float> / 24.0F))),
                std::size_t{1U},
                std::size_t{48U});
            progpu_native_point previous = evaluate_arc(primitive.p3.x);
            progpu_native_point previous_tangent =
                evaluate_arc_tangent(primitive.p3.x);
            for (std::size_t section = 1U; section <= sections; ++section) {
                const float t = static_cast<float>(section) /
                    static_cast<float>(sections);
                const float theta = primitive.p3.x + primitive.p3.y * t;
                const progpu_native_point point = evaluate_arc(theta);
                const progpu_native_point tangent = evaluate_arc_tangent(theta);
                const progpu_native_point chord{
                    point.x - previous.x,
                    point.y - previous.y
                };
                progpu_native_point start_direction{};
                progpu_native_point end_direction{};
                if (try_normalize(previous_tangent, chord, start_direction) &&
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
                    const float shape_type = section == 1U
                        ? section == sections ? 14.0F : 16.0F
                        : section == sections ? 17.0F : 15.0F;
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

        const progpu_native_point center = transformed_point(
            primitive.transform,
            primitive.p0);
        const progpu_native_point axis_x = transformed_direction(
            primitive.transform,
            local_axis_x);
        const progpu_native_point axis_y = transformed_direction(
            primitive.transform,
            local_axis_y);
        const float padding = hairline
            ? 2.0F
            : fixed_device
                ? primitive.stroke_thickness * 0.5F + 1.5F
                : encoded_thickness * 0.5F + 2.0F;
        const float extent_x =
            std::abs(axis_x.x) + std::abs(axis_y.x) + padding;
        const float extent_y =
            std::abs(axis_x.y) + std::abs(axis_y.y) + padding;
        const std::uint32_t arc_base =
            static_cast<std::uint32_t>(vertices.size());
        const auto append_arc_vertex = [&](float x, float y) {
            vector_vertex vertex{};
            vertex.position[0] = x;
            vertex.position[1] = y;
            vertex.color[0] = center.x;
            vertex.color[1] = center.y;
            vertex.color[2] = primitive.p3.x;
            vertex.color[3] = primitive.p3.y;
            vertex.texture_coordinate[0] = axis_x.x;
            vertex.texture_coordinate[1] = axis_x.y;
            vertex.brush_index = brush_index;
            vertex.shape_size[0] = axis_y.x;
            vertex.shape_size[1] = axis_y.y;
            vertex.stroke_thickness = encoded_thickness;
            vertex.shape_type = 12.0F + alias_offset;
            vertices.push_back(vertex);
        };
        append_arc_vertex(center.x - extent_x, center.y - extent_y);
        append_arc_vertex(center.x + extent_x, center.y - extent_y);
        append_arc_vertex(center.x + extent_x, center.y + extent_y);
        append_arc_vertex(center.x - extent_x, center.y + extent_y);
        indices.insert(indices.end(), {
            arc_base, arc_base + 1U, arc_base + 2U,
            arc_base, arc_base + 2U, arc_base + 3U
        });
        return true;
    }

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
            // WPF's public Miter join clips the outer corner at the nominal
            // miter-limit distance instead of falling back to a bevel.
            const float dot = incoming.x * outgoing.x +
                incoming.y * outgoing.y;
            const float denominator = radius * std::sqrt(
                std::max(0.0F, (1.0F - dot) * 0.5F));
            const float numerator = radius * std::sqrt(
                std::max(0.0F, (1.0F + dot) * 0.5F));
            if (!std::isfinite(denominator) || denominator <= 0.0001F) {
                return 1U;
            }
            const float ratio = std::max(
                0.0F,
                (resolved_limit * radius - numerator) / denominator);
            const progpu_native_point first_clip{
                previous_outer.x + incoming.x * radius * ratio,
                previous_outer.y + incoming.y * radius * ratio};
            const progpu_native_point second_clip{
                next_outer.x - outgoing.x * radius * ratio,
                next_outer.y - outgoing.y * radius * ratio};
            if (!is_finite(first_clip) || !is_finite(second_clip)) {
                return 1U;
            }
            triangles[0] = {join_point, previous_outer, first_clip};
            triangles[1] = {join_point, first_clip, second_clip};
            triangles[2] = {join_point, second_clip, next_outer};
            return 3U;
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

} // namespace progpu::native
