#include "progpu_native_scene_builder_internal.hpp"

#include "progpu_native_semantic_validation.hpp"

#include <cstring>
#include <limits>
#include <new>
#include <utility>

namespace progpu::native {
using scene_builder_detail::copy_bytes;
using scene_builder_detail::finite_rect;

bool semantic_scene_builder::draw_paths(
    std::span<const progpu_native_scene_path_fill> paths,
    std::span<const progpu_native_path_segment> segments,
    std::span<const std::uint32_t> brush_indices,
    progpu_native_image_rect bounds,
    std::uint32_t state_resource_index,
    std::span<const progpu_native_scene_path_boolean_node>
        boolean_nodes) noexcept {
    if (paths.empty() || segments.empty() || !finite_rect(bounds) ||
        !implementation_->valid_state_index(
            state_resource_index, true) ||
        (!brush_indices.empty() && brush_indices.size() != paths.size()) ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES ||
        implementation_->commands.size() >=
            PROGPU_NATIVE_SCENE_MAX_COMMANDS) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    std::uint64_t covered_segment_count = 0U;
    std::uint64_t expected_boolean_node_offset = 0U;
    for (const auto& path : paths) {
        if (path.segment_offset > covered_segment_count ||
            (path.boolean_node_count != 0U &&
                path.boolean_node_offset != expected_boolean_node_offset) ||
            !semantic::is_valid_semantic_path(
                path,
                segments.size(),
                boolean_nodes.data(),
                boolean_nodes.size()) ||
            path.segment_count >
                std::numeric_limits<std::uint64_t>::max() -
                    path.segment_offset ||
            path.boolean_node_count >
                std::numeric_limits<std::uint64_t>::max() -
                    expected_boolean_node_offset) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
        covered_segment_count = std::max(
            covered_segment_count,
            path.segment_offset + path.segment_count);
        expected_boolean_node_offset += path.boolean_node_count;
    }
    if (covered_segment_count != segments.size() ||
        expected_boolean_node_offset != boolean_nodes.size()) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    for (const auto& segment : segments) {
        if (!semantic::is_valid_semantic_segment(segment, true)) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
    }
    for (const std::uint32_t brush_index : brush_indices) {
        if (brush_index >= implementation_->brushes.size()) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
    }
    try {
        implementation_->resources.reserve(
            implementation_->resources.size() + 1U);
        implementation_->commands.reserve(
            implementation_->commands.size() + 1U);
        implementation::resource_entry resource{};
        resource.record.struct_size = sizeof(resource.record);
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.payload = copy_bytes(paths);
        resource.auxiliary.resize(
            segments.size_bytes() + boolean_nodes.size_bytes());
        std::memcpy(
            resource.auxiliary.data(),
            segments.data(),
            segments.size_bytes());
        if (!boolean_nodes.empty()) {
            std::memcpy(
                resource.auxiliary.data() + segments.size_bytes(),
                boolean_nodes.data(),
                boolean_nodes.size_bytes());
        }
        const std::uint32_t resource_index = static_cast<std::uint32_t>(
            implementation_->resources.size());

        implementation::command_entry command{};
        command.record.struct_size = sizeof(command.record);
        command.record.kind = PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH;
        command.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        command.record.command_id = implementation_->commands.size() + 1U;
        command.record.state_index = state_resource_index;
        command.record.resource_index = resource_index;
        command.record.bounds_x = bounds.x;
        command.record.bounds_y = bounds.y;
        command.record.bounds_width = bounds.width;
        command.record.bounds_height = bounds.height;
        if (!brush_indices.empty()) {
            const progpu_native_scene_draw_brushes draw{
                sizeof(progpu_native_scene_draw_brushes),
                implementation_->brush_resource_index,
                static_cast<std::uint32_t>(brush_indices.size()),
                0U};
            command.payload.resize(
                sizeof(draw) + brush_indices.size_bytes());
            std::memcpy(command.payload.data(), &draw, sizeof(draw));
            std::memcpy(
                command.payload.data() + sizeof(draw),
                brush_indices.data(),
                brush_indices.size_bytes());
        }
        implementation_->resources.push_back(std::move(resource));
        implementation_->commands.push_back(std::move(command));
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

} // namespace progpu::native
