#pragma once

#include "progpu_native_geometry_base.hpp"

namespace progpu::native {

inline bool point_batch_capacity(
    const progpu_native_scene_point_batch& batch,
    std::size_t available_point_count,
    std::size_t& vertex_count,
    std::size_t& index_count) noexcept {
    vertex_count = 0U;
    index_count = 0U;
    constexpr std::uint32_t allowed_flags =
        PROGPU_NATIVE_POINT_BATCH_EDGE_ALIASED |
        PROGPU_NATIVE_POINT_BATCH_ROUND |
        PROGPU_NATIVE_POINT_BATCH_HAIRLINE |
        PROGPU_NATIVE_POINT_BATCH_FIXED_DEVICE_RADIUS;
    if (batch.struct_size != sizeof(batch) ||
        (batch.flags & ~allowed_flags) != 0U ||
        batch.point_count == 0U ||
        batch.point_offset > available_point_count ||
        batch.point_count > available_point_count - batch.point_offset ||
        !std::isfinite(batch.radius) || batch.radius <= 0.0F ||
        batch.reserved != 0.0F || !is_finite(batch.color) ||
        !is_finite(batch.transform) ||
        ((batch.flags & PROGPU_NATIVE_POINT_BATCH_HAIRLINE) != 0U &&
            (batch.radius != 0.5F ||
                (batch.flags &
                    PROGPU_NATIVE_POINT_BATCH_FIXED_DEVICE_RADIUS) != 0U)) ||
        batch.point_count >
            std::numeric_limits<std::uint32_t>::max() / 6U) {
        return false;
    }
    vertex_count = static_cast<std::size_t>(batch.point_count) * 4U;
    index_count = static_cast<std::size_t>(batch.point_count) * 6U;
    return true;
}

inline bool is_valid_point_batch(
    const progpu_native_scene_point_batch& batch,
    const progpu_native_point* points,
    std::size_t available_point_count) noexcept {
    std::size_t vertex_count = 0U;
    std::size_t index_count = 0U;
    if (!point_batch_capacity(
            batch,
            available_point_count,
            vertex_count,
            index_count) ||
        points == nullptr) {
        return false;
    }
    (void)vertex_count;
    (void)index_count;
    const bool aliased =
        (batch.flags & PROGPU_NATIVE_POINT_BATCH_EDGE_ALIASED) != 0U;
    const bool hairline =
        (batch.flags & PROGPU_NATIVE_POINT_BATCH_HAIRLINE) != 0U;
    const bool fixed_device_radius =
        (batch.flags &
            PROGPU_NATIVE_POINT_BATCH_FIXED_DEVICE_RADIUS) != 0U;
    const float extent = batch.radius + (aliased ? 0.0F : 1.5F);
    constexpr progpu_native_point corners[4] = {
        {-1.0F, -1.0F},
        {1.0F, -1.0F},
        {1.0F, 1.0F},
        {-1.0F, 1.0F}
    };
    for (std::uint32_t point_index = 0U;
         point_index < batch.point_count;
         ++point_index) {
        const auto center = points[batch.point_offset + point_index];
        if (!is_finite(center)) {
            return false;
        }
        const std::uint32_t corner_count = hairline ? 1U : 4U;
        for (std::uint32_t corner_index = 0U;
             corner_index < corner_count;
             ++corner_index) {
            const auto corner = corners[corner_index];
            const progpu_native_point local_position = hairline
                ? center
                : progpu_native_point{
                    center.x + corner.x * extent,
                    center.y + corner.y * extent};
            progpu_native_point position{};
            if (fixed_device_radius && !hairline) {
                transform_point(
                    batch.transform,
                    center.x,
                    center.y,
                    position.x,
                    position.y);
                position.x += corner.x * extent;
                position.y += corner.y * extent;
            } else {
                transform_point(
                    batch.transform,
                    local_position.x,
                    local_position.y,
                    position.x,
                    position.y);
            }
            if (!is_finite(local_position) || !is_finite(position)) {
                return false;
            }
        }
    }
    return true;
}

inline bool append_point_batch(
    const progpu_native_scene_point_batch& batch,
    const progpu_native_point* points,
    std::size_t available_point_count,
    float brush_index,
    bool local_brush_coordinates,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    std::size_t vertex_count = 0U;
    std::size_t index_count = 0U;
    if (!point_batch_capacity(
            batch,
            available_point_count,
            vertex_count,
            index_count) ||
        !is_valid_point_batch(batch, points, available_point_count) ||
        vertices.size() >
            std::numeric_limits<std::uint32_t>::max() - vertex_count ||
        vertices.size() >
            std::numeric_limits<std::size_t>::max() - vertex_count ||
        indices.size() >
            std::numeric_limits<std::size_t>::max() - index_count) {
        return false;
    }

    const bool aliased =
        (batch.flags & PROGPU_NATIVE_POINT_BATCH_EDGE_ALIASED) != 0U;
    const bool round =
        (batch.flags & PROGPU_NATIVE_POINT_BATCH_ROUND) != 0U;
    const bool hairline =
        (batch.flags & PROGPU_NATIVE_POINT_BATCH_HAIRLINE) != 0U;
    const bool fixed_device_radius =
        (batch.flags &
            PROGPU_NATIVE_POINT_BATCH_FIXED_DEVICE_RADIUS) != 0U;
    const float extent = batch.radius + (aliased ? 0.0F : 1.5F);
    const float diameter = batch.radius * 2.0F;
    const float shape_type = hairline
        ? (round ? 20.0F : 19.0F)
        : (round ? 1.0F : 0.0F);
    const float encoded_shape_type = shape_type +
        (aliased ? 1000.0F : 0.0F);
    constexpr progpu_native_point corners[4] = {
        {-1.0F, -1.0F},
        {1.0F, -1.0F},
        {1.0F, 1.0F},
        {-1.0F, 1.0F}
    };

    for (std::uint32_t point_index = 0U;
         point_index < batch.point_count;
         ++point_index) {
        const auto center = points[batch.point_offset + point_index];
        progpu_native_point transformed_center{};
        transform_point(
            batch.transform,
            center.x,
            center.y,
            transformed_center.x,
            transformed_center.y);
        const std::uint32_t base = static_cast<std::uint32_t>(vertices.size());
        for (const auto& corner : corners) {
            const progpu_native_point local_position{
                center.x + corner.x * extent,
                center.y + corner.y * extent};
            progpu_native_point position = transformed_center;
            if (fixed_device_radius && !hairline) {
                position.x += corner.x * extent;
                position.y += corner.y * extent;
            } else if (!hairline) {
                transform_point(
                    batch.transform,
                    local_position.x,
                    local_position.y,
                    position.x,
                    position.y);
            }
            vector_vertex vertex{};
            vertex.position[0] = position.x;
            vertex.position[1] = position.y;
            if (local_brush_coordinates) {
                vertex.color[0] = center.x;
                vertex.color[1] = center.y;
            } else {
                vertex.color[0] = batch.color.r;
                vertex.color[1] = batch.color.g;
                vertex.color[2] = batch.color.b;
                vertex.color[3] = batch.color.a;
            }
            vertex.texture_coordinate[0] = corner.x * extent;
            vertex.texture_coordinate[1] = corner.y * extent;
            vertex.brush_index = brush_index;
            vertex.shape_size[0] = diameter;
            vertex.shape_size[1] = diameter;
            vertex.shape_type = encoded_shape_type;
            vertices.push_back(vertex);
        }
        indices.push_back(base);
        indices.push_back(base + 1U);
        indices.push_back(base + 2U);
        indices.push_back(base);
        indices.push_back(base + 2U);
        indices.push_back(base + 3U);
    }
    return true;
}

} // namespace progpu::native
