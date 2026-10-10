#pragma once

#include "progpu_native_geometry_base.hpp"

#include <array>
#include <cstring>

#if defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#elif defined(__wasm_simd128__)
#include <wasm_simd128.h>
#endif

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

// Source coverage is an explicit command policy, never inferred from ordinary
// mesh colors. Positions are original physical pixels; paint coordinates retain
// the independently captured target-DIP frame. O(M+V), bounded immutable input.
inline bool valid_source_coverage_frame(const progpu_native_scene_source_coverage_frame& frame) noexcept {
    return frame.struct_size == sizeof(frame) && frame.version == 1U &&
        std::isfinite(frame.dpi_scale_x) && frame.dpi_scale_x > 0.0F &&
        std::isfinite(frame.dpi_scale_y) && frame.dpi_scale_y > 0.0F &&
        frame.pixel_width > 0U && frame.pixel_width <= 16384U &&
        frame.pixel_height > 0U && frame.pixel_height <= 16384U &&
        frame.flags == 0U && frame.reserved == 0U;
}

inline bool valid_source_coverage_mesh_shape(const progpu_native_scene_vertex_mesh& mesh,
    std::size_t count) noexcept {
    std::size_t vertex_count{}, index_count{};
    if (!vertex_mesh_capacity(mesh, count, 0U, vertex_count, index_count) ||
        mesh.flags != PROGPU_NATIVE_VERTEX_MESH_EDGE_ALIASED || mesh.topology != PROGPU_NATIVE_VERTEX_MESH_TRIANGLES ||
        mesh.color_blend_mode != 5U || mesh.vertex_count % 3U != 0U || mesh.index_count != 0U ||
        mesh.transform.m11 != 1.0F || mesh.transform.m12 != 0.0F || mesh.transform.m21 != 0.0F ||
        mesh.transform.m22 != 1.0F || mesh.transform.m31 != 0.0F || mesh.transform.m32 != 0.0F) return false;
    return true;
}

inline bool valid_source_coverage_vertex(const progpu_native_scene_mesh_vertex& v,
    const progpu_native_scene_source_coverage_frame& frame) noexcept {
    return is_finite(v.position) && is_finite(v.texture_coordinate) &&
        v.color.r == 1.0F && v.color.g == 1.0F && v.color.b == 1.0F && (v.color.a == 0.0F || v.color.a == 1.0F) &&
        v.position.x >= 0.0F && v.position.y >= 0.0F && v.position.x <= frame.pixel_width && v.position.y <= frame.pixel_height &&
        v.texture_coordinate.x == v.position.x / frame.dpi_scale_x &&
        v.texture_coordinate.y == v.position.y / frame.dpi_scale_y;
}

inline bool valid_source_coverage_mesh(const progpu_native_scene_vertex_mesh& mesh,
    const progpu_native_scene_mesh_vertex* vertices, std::size_t count,
    const progpu_native_scene_source_coverage_frame& frame) noexcept {
    if (!valid_source_coverage_frame(frame) || !valid_source_coverage_mesh_shape(mesh, count) || !vertices) return false;
    for (std::size_t i = mesh.vertex_offset; i < mesh.vertex_offset + mesh.vertex_count; ++i) {
        if (!valid_source_coverage_vertex(vertices[i], frame)) return false;
    }
    return true;
}

inline bool validate_source_coverage_draw(const std::byte* bytes, const progpu_native_scene_header& header,
    const progpu_native_scene_command& command, std::uint32_t& error_offset) noexcept {
    error_offset = command.payload_offset;
    if (command.resource_index >= header.resource_count || command.payload_size <
        sizeof(progpu_native_scene_draw_brushes) + sizeof(progpu_native_scene_source_coverage_frame)) return false;
    progpu_native_scene_source_coverage_frame frame{};
    std::memcpy(&frame, bytes + command.payload_offset + command.payload_size - sizeof(frame), sizeof(frame));
    if (!valid_source_coverage_frame(frame)) return false;
    progpu_native_scene_resource resource{};
    std::memcpy(&resource, bytes + header.resource_offset + std::size_t{command.resource_index} * header.resource_stride, sizeof(resource));
    if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_VERTEX_MESH || resource.payload_size == 0U ||
        resource.payload_size % sizeof(progpu_native_scene_vertex_mesh) != 0U ||
        resource.auxiliary_size % sizeof(progpu_native_scene_mesh_vertex) != 0U ||
        resource.payload_offset % alignof(progpu_native_scene_vertex_mesh) != 0U ||
        resource.auxiliary_offset % alignof(progpu_native_scene_mesh_vertex) != 0U) return false;
    const auto count = resource.auxiliary_size / sizeof(progpu_native_scene_mesh_vertex);
    if (count > 1048576U) return false;
    std::size_t expected = 0U;
    // Raw validation also accepts an unaligned caller buffer; only owned wire
    // offsets are aligned. Never form typed pointers into external byte storage.
    for (std::size_t offset = 0U; offset < resource.payload_size; offset += sizeof(progpu_native_scene_vertex_mesh)) {
        progpu_native_scene_vertex_mesh mesh{};
        std::memcpy(&mesh, bytes + resource.payload_offset + offset, sizeof(mesh));
        if (!valid_source_coverage_mesh_shape(mesh, count) || mesh.vertex_offset != expected || mesh.index_offset != 0U) {
            error_offset = resource.payload_offset + static_cast<std::uint32_t>(offset); return false;
        }
        expected += mesh.vertex_count;
    }
    if (expected != count) return false;
    for (std::size_t i = 0U; i < count; ++i) {
        progpu_native_scene_mesh_vertex vertex{};
        std::memcpy(&vertex, bytes + resource.auxiliary_offset + i * sizeof(vertex), sizeof(vertex));
        if (!valid_source_coverage_vertex(vertex, frame)) return false;
    }
    return true;
}

// The caller preflights exact integral physical translation and actual target
// containment. Three original binary coverages fit in a flat integer bit mask;
// all three physical corners are duplicated per vertex without changing the
// established 56-byte vector ABI. Independent point lanes use explicit SIMD;
// scalar builds retain the identical bounded reference arithmetic. Triangle
// ordering and ownership stay original. O(V) time and retained storage.
inline void append_source_coverage(const progpu_native_scene_vertex_mesh& mesh,
    const progpu_native_scene_mesh_vertex* source, float offset_x, float offset_y,
    float opacity, float brush_index, std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    for (std::size_t i = mesh.vertex_offset; i < mesh.vertex_offset + mesh.vertex_count; i += 3U) {
        alignas(16) float points[8]{source[i].position.x, source[i].position.y,
            source[i+1U].position.x, source[i+1U].position.y,
            source[i+2U].position.x, source[i+2U].position.y, 0.0F, 0.0F};
        alignas(16) const float offset[4]{offset_x, offset_y, offset_x, offset_y};
        for (std::size_t lane = 0U; lane < 8U; lane += 4U) {
#if defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
            vst1q_f32(points + lane, vaddq_f32(vld1q_f32(points + lane), vld1q_f32(offset)));
#elif defined(__SSE2__) || defined(_M_X64)
            _mm_store_ps(points + lane, _mm_add_ps(_mm_load_ps(points + lane), _mm_load_ps(offset)));
#elif defined(__wasm_simd128__)
            wasm_v128_store(points + lane, wasm_f32x4_add(wasm_v128_load(points + lane), wasm_v128_load(offset)));
#else
            for (std::size_t k = 0U; k < 4U; ++k) points[lane+k] += offset[k];
#endif
        }
        std::uint32_t coverage = 0U;
        for (std::size_t k = 0U; k < 3U; ++k) {
            if (source[i+k].color.a == 1.0F) coverage |= 1U << k;
        }
        for (std::size_t k = 0U; k < 3U; ++k) {
            vector_vertex v{};
            v.position[0] = points[k*2U]; v.position[1] = points[k*2U+1U];
            std::copy_n(points, 4U, v.color);
            v.shape_size[0] = points[4]; v.shape_size[1] = points[5];
            v.texture_coordinate[0] = source[i+k].texture_coordinate.x;
            v.texture_coordinate[1] = source[i+k].texture_coordinate.y;
            v.corner_radius = static_cast<float>(coverage); v.stroke_thickness = opacity;
            v.brush_index = brush_index; v.shape_type = 1026.0F;
            indices.push_back(static_cast<std::uint32_t>(vertices.size())); vertices.push_back(v);
        }
    }
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
