#include "progpu_native_scene_builder_internal.hpp"

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
    command.payload.resize(sizeof(camera));
    std::memcpy(command.payload.data(), &camera, sizeof(camera));
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
    const std::uint64_t auxiliary_bytes =
        static_cast<std::uint64_t>(vertices.size_bytes()) +
        indices.size_bytes();
    if (meshes.empty() || vertices.empty() || indices.empty() ||
        auxiliary_bytes > PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES ||
        !finite_rect(bounds) ||
        !implementation_->valid_state_index(state_resource_index) ||
        !semantic::is_valid_semantic_camera_3d(camera) ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES ||
        implementation_->commands.size() >=
            PROGPU_NATIVE_SCENE_MAX_COMMANDS) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    for (const auto& vertex : vertices) {
        if (!semantic::is_valid_semantic_mesh_3d_vertex(vertex)) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
    }
    for (const auto& mesh : meshes) {
        if (!semantic::is_valid_semantic_mesh_3d(
                mesh, vertices.size(), indices.size())) {
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
    try {
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
        return append_3d_command(
            PROGPU_NATIVE_SCENE_RESOURCE_MESH_3D_BATCH,
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH,
            copy_bytes(meshes),
            std::move(auxiliary),
            camera,
            bounds,
            state_resource_index);
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

} // namespace progpu::native
