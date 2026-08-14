#pragma once

#include "progpu_native_geometry_base.hpp"

namespace progpu::native {

inline bool vertex_mesh_resource_layout(
    const progpu_native_scene_vertex_mesh* meshes,
    std::size_t mesh_count,
    std::size_t auxiliary_size,
    std::size_t& vertex_count,
    std::size_t& index_count) noexcept {
    vertex_count = 0U;
    index_count = 0U;
    if (meshes == nullptr || mesh_count == 0U) {
        return false;
    }
    std::uint64_t expected_vertices = 0U;
    std::uint64_t expected_indices = 0U;
    for (std::size_t index = 0U; index < mesh_count; ++index) {
        const auto& mesh = meshes[index];
        if (mesh.struct_size != sizeof(mesh) ||
            (mesh.flags & ~PROGPU_NATIVE_VERTEX_MESH_EDGE_ALIASED) != 0U ||
            mesh.topology > PROGPU_NATIVE_VERTEX_MESH_TRIANGLE_FAN ||
            mesh.color_blend_mode > 28U || mesh.vertex_count == 0U ||
            mesh.vertex_offset != expected_vertices ||
            mesh.index_offset != expected_indices ||
            !is_finite(mesh.transform) ||
            mesh.reserved[0] != 0U || mesh.reserved[1] != 0U) {
            return false;
        }
        expected_vertices += mesh.vertex_count;
        expected_indices += mesh.index_count;
    }
    const std::uint64_t expected_size =
        expected_vertices * sizeof(progpu_native_scene_mesh_vertex) +
        expected_indices * sizeof(std::uint16_t);
    if (expected_vertices >
            std::numeric_limits<std::uint32_t>::max() ||
        expected_indices >
            std::numeric_limits<std::uint32_t>::max() ||
        expected_size != auxiliary_size) {
        return false;
    }
    vertex_count = static_cast<std::size_t>(expected_vertices);
    index_count = static_cast<std::size_t>(expected_indices);
    return true;
}

inline bool vertex_mesh_capacity(
    const progpu_native_scene_vertex_mesh& mesh,
    std::size_t available_vertex_count,
    std::size_t available_index_count,
    std::size_t& vertex_count,
    std::size_t& maximum_index_count) noexcept {
    vertex_count = 0U;
    maximum_index_count = 0U;
    if (mesh.struct_size != sizeof(mesh) ||
        (mesh.flags & ~PROGPU_NATIVE_VERTEX_MESH_EDGE_ALIASED) != 0U ||
        mesh.topology > PROGPU_NATIVE_VERTEX_MESH_TRIANGLE_FAN ||
        mesh.color_blend_mode > 28U ||
        mesh.vertex_count == 0U ||
        mesh.vertex_offset > available_vertex_count ||
        mesh.vertex_count > available_vertex_count - mesh.vertex_offset ||
        mesh.index_offset > available_index_count ||
        mesh.index_count > available_index_count - mesh.index_offset ||
        !is_finite(mesh.transform) ||
        mesh.reserved[0] != 0U || mesh.reserved[1] != 0U) {
        return false;
    }
    const std::uint32_t element_count = mesh.index_count != 0U
        ? mesh.index_count
        : mesh.vertex_count;
    if (element_count >
        std::numeric_limits<std::uint32_t>::max() / 3U) {
        return false;
    }
    const std::uint32_t triangle_count =
        mesh.topology == PROGPU_NATIVE_VERTEX_MESH_TRIANGLES
            ? element_count / 3U
            : element_count >= 3U ? element_count - 2U : 0U;
    vertex_count = mesh.vertex_count;
    maximum_index_count = static_cast<std::size_t>(triangle_count) * 3U;
    return true;
}

inline bool is_valid_vertex_mesh(
    const progpu_native_scene_vertex_mesh& mesh,
    const progpu_native_scene_mesh_vertex* vertices,
    std::size_t available_vertex_count,
    std::size_t available_index_count) noexcept {
    std::size_t vertex_count = 0U;
    std::size_t index_count = 0U;
    if (!vertex_mesh_capacity(
            mesh,
            available_vertex_count,
            available_index_count,
            vertex_count,
            index_count) ||
        vertices == nullptr) {
        return false;
    }
    (void)vertex_count;
    (void)index_count;
    for (std::uint32_t index = 0U; index < mesh.vertex_count; ++index) {
        const auto& source = vertices[mesh.vertex_offset + index];
        progpu_native_point transformed{};
        transform_point(
            mesh.transform,
            source.position.x,
            source.position.y,
            transformed.x,
            transformed.y);
        if (!is_finite(source.position) ||
            !is_finite(source.texture_coordinate) ||
            !is_finite(source.color) ||
            !is_finite(transformed)) {
            return false;
        }
    }
    return true;
}

inline bool append_vertex_mesh(
    const progpu_native_scene_vertex_mesh& mesh,
    const progpu_native_scene_mesh_vertex* source_vertices,
    std::size_t available_vertex_count,
    const std::uint16_t* source_indices,
    std::size_t available_index_count,
    float opacity,
    float brush_index,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    std::size_t vertex_count = 0U;
    std::size_t maximum_index_count = 0U;
    if (!is_valid_vertex_mesh(
            mesh,
            source_vertices,
            available_vertex_count,
            available_index_count) ||
        !vertex_mesh_capacity(
            mesh,
            available_vertex_count,
            available_index_count,
            vertex_count,
            maximum_index_count) ||
        (mesh.index_count != 0U && source_indices == nullptr) ||
        !std::isfinite(opacity) || opacity < 0.0F || opacity > 1.0F ||
        vertices.size() >
            std::numeric_limits<std::uint32_t>::max() - vertex_count ||
        vertices.size() >
            std::numeric_limits<std::size_t>::max() - vertex_count ||
        indices.size() >
            std::numeric_limits<std::size_t>::max() - maximum_index_count) {
        return false;
    }

    const std::uint32_t base = static_cast<std::uint32_t>(vertices.size());
    const float shape_type = 18.0F +
        ((mesh.flags & PROGPU_NATIVE_VERTEX_MESH_EDGE_ALIASED) != 0U
            ? 1000.0F
            : 0.0F);
    for (std::uint32_t index = 0U; index < mesh.vertex_count; ++index) {
        const auto& source = source_vertices[mesh.vertex_offset + index];
        vector_vertex vertex{};
        transform_point(
            mesh.transform,
            source.position.x,
            source.position.y,
            vertex.position[0],
            vertex.position[1]);
        vertex.color[0] = source.color.r * source.color.a;
        vertex.color[1] = source.color.g * source.color.a;
        vertex.color[2] = source.color.b * source.color.a;
        vertex.color[3] = source.color.a;
        vertex.texture_coordinate[0] = source.texture_coordinate.x;
        vertex.texture_coordinate[1] = source.texture_coordinate.y;
        vertex.brush_index = brush_index;
        vertex.corner_radius = static_cast<float>(mesh.color_blend_mode);
        // Shape 18 reserves stroke_thickness for post-blend state opacity.
        // Every vertex in a retained mesh receives the same value, so the
        // fragment interpolation is exact and adds no resource payload.
        vertex.stroke_thickness = opacity;
        vertex.shape_type = shape_type;
        vertices.push_back(vertex);
    }

    const std::uint32_t element_count = mesh.index_count != 0U
        ? mesh.index_count
        : mesh.vertex_count;
    const std::uint32_t triangle_count =
        mesh.topology == PROGPU_NATIVE_VERTEX_MESH_TRIANGLES
            ? element_count / 3U
            : element_count >= 3U ? element_count - 2U : 0U;
    const auto element = [&](std::uint32_t offset) noexcept {
        return mesh.index_count != 0U
            ? static_cast<std::uint32_t>(
                source_indices[mesh.index_offset + offset])
            : offset;
    };
    for (std::uint32_t triangle = 0U;
         triangle < triangle_count;
         ++triangle) {
        std::uint32_t index0 = 0U;
        std::uint32_t index1 = 0U;
        std::uint32_t index2 = 0U;
        if (mesh.topology == PROGPU_NATIVE_VERTEX_MESH_TRIANGLE_STRIP) {
            index0 = element(triangle + ((triangle & 1U) == 0U ? 0U : 1U));
            index1 = element(triangle + ((triangle & 1U) == 0U ? 1U : 0U));
            index2 = element(triangle + 2U);
        } else if (mesh.topology == PROGPU_NATIVE_VERTEX_MESH_TRIANGLE_FAN) {
            index0 = element(0U);
            index1 = element(triangle + 1U);
            index2 = element(triangle + 2U);
        } else {
            const std::uint32_t offset = triangle * 3U;
            index0 = element(offset);
            index1 = element(offset + 1U);
            index2 = element(offset + 2U);
        }
        if (index0 >= mesh.vertex_count ||
            index1 >= mesh.vertex_count ||
            index2 >= mesh.vertex_count) {
            continue;
        }
        indices.push_back(base + index0);
        indices.push_back(base + index1);
        indices.push_back(base + index2);
    }
    return true;
}

} // namespace progpu::native
