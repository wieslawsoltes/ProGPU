#include "progpu_native_scene_builder_internal.hpp"

#include "progpu_native_semantic_brush.hpp"
#include "progpu_native_semantic_validation.hpp"

#include <cstring>
#include <limits>
#include <new>
#include <utility>

namespace progpu::native {
using scene_builder_detail::copy_bytes;
using scene_builder_detail::finite_rect;

bool semantic_scene_builder::append_3d_command(
    std::uint32_t resource_kind,
    std::uint32_t command_kind,
    std::vector<std::byte> payload,
    std::vector<std::byte> auxiliary,
    const progpu_native_scene_camera_3d& camera,
    std::span<const std::uint32_t> material_brush_indices,
    progpu_native_image_rect bounds,
    std::uint32_t state_resource_index) {
    implementation::resource_entry resource{};
    resource.record.struct_size = sizeof(resource.record);
    resource.record.kind = resource_kind;
    resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
    resource.record.resource_id = implementation_->resources.size() + 1U;
    resource.record.generation = implementation_->generation;
    resource.payload = std::move(payload);
    resource.auxiliary = std::move(auxiliary);
    const std::uint32_t resource_index = static_cast<std::uint32_t>(
        implementation_->resources.size());

    implementation::command_entry command{};
    command.record.struct_size = sizeof(command.record);
    command.record.kind = command_kind;
    command.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
    command.record.command_id = implementation_->commands.size() + 1U;
    command.record.state_index = state_resource_index;
    command.record.resource_index = resource_index;
    command.record.bounds_x = bounds.x;
    command.record.bounds_y = bounds.y;
    command.record.bounds_width = bounds.width;
    command.record.bounds_height = bounds.height;
    const std::size_t material_payload_size =
        material_brush_indices.empty()
        ? 0U
        : sizeof(progpu_native_scene_mesh_3d_materials) +
            material_brush_indices.size_bytes();
    command.payload.resize(sizeof(camera) + material_payload_size);
    std::memcpy(command.payload.data(), &camera, sizeof(camera));
    if (!material_brush_indices.empty()) {
        progpu_native_scene_mesh_3d_materials materials{};
        materials.struct_size = sizeof(materials);
        materials.brush_resource_index =
            implementation_->brush_resource_index;
        materials.brush_count = static_cast<std::uint32_t>(
            material_brush_indices.size());
        std::memcpy(
            command.payload.data() + sizeof(camera),
            &materials,
            sizeof(materials));
        std::memcpy(
            command.payload.data() + sizeof(camera) + sizeof(materials),
            material_brush_indices.data(),
            material_brush_indices.size_bytes());
    }
    implementation_->resources.push_back(std::move(resource));
    implementation_->commands.push_back(std::move(command));
    implementation_->error = scene_build_error::none;
    return true;
}

bool semantic_scene_builder::draw_lines_3d(
    std::span<const progpu_native_scene_line_3d> lines,
    const progpu_native_scene_camera_3d& camera,
    progpu_native_image_rect bounds,
    std::uint32_t state_resource_index) noexcept {
    if (lines.empty() || !finite_rect(bounds) ||
        !implementation_->valid_state_index(state_resource_index) ||
        !semantic::is_valid_semantic_camera_3d(camera) ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES ||
        implementation_->commands.size() >=
            PROGPU_NATIVE_SCENE_MAX_COMMANDS) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    for (const auto& line : lines) {
        if (!semantic::is_valid_semantic_line_3d(line)) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
    }
    try {
        implementation_->resources.reserve(
            implementation_->resources.size() + 1U);
        implementation_->commands.reserve(
            implementation_->commands.size() + 1U);
        return append_3d_command(
            PROGPU_NATIVE_SCENE_RESOURCE_LINE_3D_BATCH,
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_LINE_3D_BATCH,
            copy_bytes(lines),
            {},
            camera,
            {},
            bounds,
            state_resource_index);
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

bool semantic_scene_builder::draw_meshes_3d(
    std::span<const progpu_native_scene_mesh_3d> meshes,
    std::span<const progpu_native_scene_mesh_3d_vertex> vertices,
    std::span<const std::uint32_t> indices,
    const progpu_native_scene_camera_3d& camera,
    progpu_native_image_rect bounds,
    std::uint32_t state_resource_index) noexcept {
    return draw_meshes_3d(
        meshes,
        vertices,
        indices,
        std::span<const progpu_native_scene_light_3d>{},
        camera,
        bounds,
        state_resource_index);
}

bool semantic_scene_builder::draw_meshes_3d(
    std::span<const progpu_native_scene_mesh_3d> meshes,
    std::span<const progpu_native_scene_mesh_3d_vertex> vertices,
    std::span<const std::uint32_t> indices,
    std::span<const progpu_native_scene_light_3d> lights,
    const progpu_native_scene_camera_3d& camera,
    progpu_native_image_rect bounds,
    std::uint32_t state_resource_index) noexcept {
    return draw_meshes_3d(
        meshes,
        vertices,
        indices,
        lights,
        std::span<const progpu_native_scene_brush>{},
        std::span<const progpu_native_scene_gradient_stop>{},
        camera,
        bounds,
        state_resource_index);
}

bool semantic_scene_builder::draw_meshes_3d(
    std::span<const progpu_native_scene_mesh_3d> meshes,
    std::span<const progpu_native_scene_mesh_3d_vertex> vertices,
    std::span<const std::uint32_t> indices,
    std::span<const progpu_native_scene_light_3d> lights,
    std::span<const progpu_native_scene_brush> materials,
    std::span<const progpu_native_scene_gradient_stop> gradient_stops,
    const progpu_native_scene_camera_3d& camera,
    progpu_native_image_rect bounds,
    std::uint32_t state_resource_index) noexcept {
    const std::uint64_t auxiliary_bytes =
        static_cast<std::uint64_t>(vertices.size_bytes()) +
        indices.size_bytes() + lights.size_bytes();
    const std::size_t required_resource_count =
        1U + (!materials.empty() &&
                implementation_->brush_resource_index ==
                    PROGPU_NATIVE_SCENE_NO_INDEX
            ? 1U
            : 0U);
    if (meshes.empty() || vertices.empty() || indices.empty() ||
        (!materials.empty() && materials.size() != meshes.size()) ||
        (materials.empty() && !gradient_stops.empty()) ||
        auxiliary_bytes > PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES ||
        !finite_rect(bounds) ||
        !implementation_->valid_state_index(state_resource_index) ||
        !semantic::is_valid_semantic_camera_3d(camera) ||
        required_resource_count > PROGPU_NATIVE_SCENE_MAX_RESOURCES -
            implementation_->resources.size() ||
        implementation_->commands.size() >=
            PROGPU_NATIVE_SCENE_MAX_COMMANDS) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    for (const auto& vertex : vertices) {
        if (!semantic::is_valid_semantic_mesh_3d_vertex(vertex)) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
    }
    for (const auto& light : lights) {
        if (!semantic::is_valid_semantic_light_3d(light)) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
    }
    for (const auto& mesh : meshes) {
        if (!semantic::is_valid_semantic_mesh_3d(
                mesh, vertices.size(), indices.size(), lights.size())) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
        for (std::size_t index = mesh.index_offset;
             index < static_cast<std::size_t>(mesh.index_offset) +
                mesh.index_count;
             ++index) {
            if (indices[index] >= mesh.vertex_count) {
                return implementation_->fail(
                    scene_build_error::invalid_argument);
            }
        }
    }
    if (!materials.empty()) {
        for (const auto& material : materials) {
            if (!semantic::is_valid_semantic_brush(
                    material, gradient_stops) ||
                (material.type != PROGPU_NATIVE_SCENE_BRUSH_SOLID &&
                    material.type !=
                        PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT &&
                    material.type !=
                        PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT)) {
                return implementation_->fail(
                    scene_build_error::invalid_argument);
            }
        }
    }
    try {
        std::vector<std::uint32_t> material_brush_indices;
        material_brush_indices.reserve(materials.size());
        for (const auto& material : materials) {
            const std::uint32_t stored_stop_count =
                semantic::semantic_brush_stored_stop_count(material);
            std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            if (!add_brush(
                    material,
                    gradient_stops.subspan(
                        material.stop_offset,
                        stored_stop_count),
                    brush_index)) {
                return false;
            }
            material_brush_indices.push_back(brush_index);
        }
        implementation_->resources.reserve(
            implementation_->resources.size() + 1U);
        implementation_->commands.reserve(
            implementation_->commands.size() + 1U);
        std::vector<std::byte> auxiliary(
            static_cast<std::size_t>(auxiliary_bytes));
        std::memcpy(
            auxiliary.data(), vertices.data(), vertices.size_bytes());
        std::memcpy(
            auxiliary.data() + vertices.size_bytes(),
            indices.data(),
            indices.size_bytes());
        if (!lights.empty()) {
            std::memcpy(
                auxiliary.data() + vertices.size_bytes() +
                    indices.size_bytes(),
                lights.data(),
                lights.size_bytes());
        }
        return append_3d_command(
            PROGPU_NATIVE_SCENE_RESOURCE_MESH_3D_BATCH,
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH,
            copy_bytes(meshes),
            std::move(auxiliary),
            camera,
            material_brush_indices,
            bounds,
            state_resource_index);
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

} // namespace progpu::native
