#include "progpu_native_scene_builder_internal.hpp"

#include "progpu_native_geometry_stroke.hpp"
#include "progpu_native_semantic_stroke.hpp"

#include <cmath>
#include <cstring>
#include <new>
#include <utility>

namespace progpu::native {
using scene_builder_detail::copy_bytes;
using scene_builder_detail::finite_rect;

bool semantic_scene_builder::draw_geometry(
    std::span<const progpu_native_geometry_primitive> primitives,
    std::span<const std::uint32_t> brush_indices,
    progpu_native_image_rect bounds,
    std::uint32_t state_resource_index) noexcept {
    if (primitives.empty() || !finite_rect(bounds) ||
        !implementation_->valid_state_index(state_resource_index) ||
        (!brush_indices.empty() &&
            brush_indices.size() != primitives.size()) ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES ||
        implementation_->commands.size() >=
            PROGPU_NATIVE_SCENE_MAX_COMMANDS) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    for (const auto& primitive : primitives) {
        if (!is_valid_geometry_primitive(primitive)) {
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
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.payload = copy_bytes(primitives);
        const std::uint32_t resource_index = static_cast<std::uint32_t>(
            implementation_->resources.size());

        implementation::command_entry command{};
        command.record.struct_size = sizeof(command.record);
        command.record.kind = PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY;
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

bool semantic_scene_builder::draw_strokes(
    std::span<const progpu_native_scene_stroke> strokes,
    std::span<const progpu_native_point> points,
    std::span<const double> doubles,
    std::span<const std::uint32_t> brush_indices,
    progpu_native_image_rect bounds,
    std::uint32_t state_resource_index) noexcept {
    const std::uint64_t auxiliary_size =
        static_cast<std::uint64_t>(points.size_bytes()) +
        doubles.size_bytes();
    std::size_t validated_point_count = 0U;
    std::size_t validated_double_count = 0U;
    if (strokes.empty() || points.empty() || !finite_rect(bounds) ||
        !implementation_->valid_state_index(state_resource_index) ||
        (!brush_indices.empty() && brush_indices.size() != strokes.size()) ||
        auxiliary_size > PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES ||
        !semantic_stroke_resource_layout(
            strokes.data(),
            strokes.size(),
            static_cast<std::size_t>(auxiliary_size),
            validated_point_count,
            validated_double_count) ||
        validated_point_count != points.size() ||
        validated_double_count != doubles.size() ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES ||
        implementation_->commands.size() >=
            PROGPU_NATIVE_SCENE_MAX_COMMANDS) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    for (const auto& point : points) {
        if (!std::isfinite(point.x) || !std::isfinite(point.y)) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
    }
    for (const double value : doubles) {
        if (!std::isfinite(value)) {
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
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.payload = copy_bytes(strokes);
        resource.auxiliary.resize(static_cast<std::size_t>(auxiliary_size));
        if (!points.empty()) {
            std::memcpy(
                resource.auxiliary.data(),
                points.data(),
                points.size_bytes());
        }
        if (!doubles.empty()) {
            std::memcpy(
                resource.auxiliary.data() + points.size_bytes(),
                doubles.data(),
                doubles.size_bytes());
        }
        const std::uint32_t resource_index = static_cast<std::uint32_t>(
            implementation_->resources.size());

        implementation::command_entry command{};
        command.record.struct_size = sizeof(command.record);
        command.record.kind = PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH;
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
