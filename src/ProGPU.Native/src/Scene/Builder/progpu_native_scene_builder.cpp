#include "progpu_native_scene_builder.hpp"
#include "progpu_native_scene_builder_internal.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <new>
#include <utility>

namespace progpu::native {
using scene_builder_detail::copy_bytes;
using scene_builder_detail::finite_primitive;
using scene_builder_detail::finite_rect;
using scene_builder_detail::finite_transform;

semantic_scene_builder::semantic_scene_builder(
    std::uint64_t scene_id,
    std::uint64_t generation)
    : implementation_(std::make_unique<implementation>()) {
    if (!reset(scene_id, generation)) {
        implementation_->scene_id = 0U;
        implementation_->generation = 0U;
    }
}

semantic_scene_builder::~semantic_scene_builder() = default;
semantic_scene_builder::semantic_scene_builder(
    semantic_scene_builder&&) noexcept = default;
semantic_scene_builder& semantic_scene_builder::operator=(
    semantic_scene_builder&&) noexcept = default;

bool semantic_scene_builder::reserve(
    std::uint32_t command_count,
    std::uint32_t resource_count,
    std::uint64_t arena_bytes) noexcept {
    if (command_count > PROGPU_NATIVE_SCENE_MAX_COMMANDS ||
        resource_count > PROGPU_NATIVE_SCENE_MAX_RESOURCES ||
        arena_bytes > PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES) {
        return implementation_->fail(scene_build_error::capacity_exceeded);
    }
    try {
        implementation_->commands.reserve(command_count);
        implementation_->resources.reserve(resource_count);
        implementation_->brushes.reserve(std::min(
            resource_count,
            static_cast<std::uint32_t>(PROGPU_NATIVE_SCENE_MAX_BRUSHES)));
        implementation_->arena_reserve = arena_bytes;
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

bool semantic_scene_builder::reset(
    std::uint64_t scene_id,
    std::uint64_t generation) noexcept {
    if (scene_id == 0U || generation == 0U) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    implementation_->scene_id = scene_id;
    implementation_->generation = generation;
    implementation_->resources.clear();
    implementation_->commands.clear();
    implementation_->brushes.clear();
    implementation_->gradient_stops.clear();
    implementation_->text_styles.clear();
    implementation_->brush_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    implementation_->text_style_resource_index =
        PROGPU_NATIVE_SCENE_NO_INDEX;
    implementation_->stack_depth = 0U;
    implementation_->materialized_layer_depth = 0U;
    implementation_->maximum_stack_depth = 0U;
    implementation_->stack_kinds.fill(0U);
    implementation_->arena_reserve = 0U;
    implementation_->error = scene_build_error::none;
    return true;
}

bool semantic_scene_builder::advance_generation(
    std::uint64_t generation) noexcept {
    if (generation <= implementation_->generation) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    implementation_->generation = generation;
    implementation_->error = scene_build_error::none;
    return true;
}

bool semantic_scene_builder::set_resource_identity(
    std::uint32_t resource_index,
    std::uint64_t resource_id,
    std::uint64_t generation) noexcept {
    if (resource_index >= implementation_->resources.size() ||
        resource_id == 0U || generation == 0U) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    auto& resource = implementation_->resources[resource_index].record;
    resource.resource_id = resource_id;
    resource.generation = generation;
    implementation_->error = scene_build_error::none;
    return true;
}

bool semantic_scene_builder::add_state(
    const progpu_native_scene_state& source,
    std::uint32_t& resource_index) noexcept {
    resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!finite_transform(source.transform) ||
        !std::isfinite(source.opacity) || source.opacity < 0.0F ||
        source.opacity > 1.0F ||
        (source.flags & ~(PROGPU_NATIVE_SCENE_STATE_CLIP_RECT |
            PROGPU_NATIVE_SCENE_STATE_MASK |
            PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET)) != 0U ||
        ((source.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U &&
            !finite_rect(source.clip_rect)) ||
        ((source.flags & PROGPU_NATIVE_SCENE_STATE_MASK) != 0U &&
            (source.mask_resource_index >=
                    implementation_->resources.size() ||
                implementation_->resources[source.mask_resource_index]
                        .record.kind !=
                    PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK)) ||
        ((source.flags & PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) != 0U &&
            (source.guideline_resource_index >=
                    implementation_->resources.size() ||
                implementation_->resources[source.guideline_resource_index]
                        .record.kind !=
                    PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET)) ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    try {
        implementation_->resources.reserve(
            implementation_->resources.size() + 1U);
        progpu_native_scene_state state = source;
        state.struct_size = sizeof(state);
        state.reserved = 0U;
        state.mask_resource_index =
            (state.flags & PROGPU_NATIVE_SCENE_STATE_MASK) != 0U
            ? source.mask_resource_index
            : 0U;
        state.guideline_resource_index =
            (state.flags & PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) != 0U
            ? source.guideline_resource_index
            : 0U;
        implementation::resource_entry resource{};
        resource.record.struct_size = sizeof(resource.record);
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_STATE;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.payload = copy_bytes(
            std::span<const progpu_native_scene_state>(&state, 1U));
        resource_index = static_cast<std::uint32_t>(
            implementation_->resources.size());
        implementation_->resources.push_back(std::move(resource));
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

bool semantic_scene_builder::add_guideline_set(
    std::span<const double> guidelines_x,
    std::span<const double> guidelines_y,
    std::uint32_t& resource_index,
    bool composite_only,
    bool per_point) noexcept {
    resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    const bool multiple =
        guidelines_x.size() > 1U || guidelines_y.size() > 1U;
    if ((composite_only && per_point) ||
        multiple != (composite_only || per_point) ||
        guidelines_x.size() > PROGPU_NATIVE_SCENE_MAX_GUIDELINES_PER_AXIS ||
        guidelines_y.size() > PROGPU_NATIVE_SCENE_MAX_GUIDELINES_PER_AXIS ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES ||
        !std::ranges::is_sorted(guidelines_x) ||
        !std::ranges::is_sorted(guidelines_y) ||
        std::ranges::any_of(guidelines_x,
            [](double value) { return !std::isfinite(value); }) ||
        std::ranges::any_of(guidelines_y,
            [](double value) { return !std::isfinite(value); })) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    try {
        implementation_->resources.reserve(
            implementation_->resources.size() + 1U);
        progpu_native_scene_guideline_set header{};
        header.struct_size = sizeof(header);
        header.flags = composite_only
            ? static_cast<std::uint32_t>(
                PROGPU_NATIVE_SCENE_GUIDELINE_COMPOSITE_ONLY)
            : per_point
                ? static_cast<std::uint32_t>(
                    PROGPU_NATIVE_SCENE_GUIDELINE_PER_POINT)
                : 0U;
        header.guideline_x_count =
            static_cast<std::uint32_t>(guidelines_x.size());
        header.guideline_y_count =
            static_cast<std::uint32_t>(guidelines_y.size());
        implementation::resource_entry resource{};
        resource.record.struct_size = sizeof(resource.record);
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.payload.resize(sizeof(header) +
            (guidelines_x.size() + guidelines_y.size()) * sizeof(double));
        std::memcpy(resource.payload.data(), &header, sizeof(header));
        std::size_t offset = sizeof(header);
        if (!guidelines_x.empty()) {
            std::memcpy(resource.payload.data() + offset,
                guidelines_x.data(), guidelines_x.size_bytes());
            offset += guidelines_x.size_bytes();
        }
        if (!guidelines_y.empty()) {
            std::memcpy(resource.payload.data() + offset,
                guidelines_y.data(), guidelines_y.size_bytes());
        }
        resource_index = static_cast<std::uint32_t>(
            implementation_->resources.size());
        implementation_->resources.push_back(std::move(resource));
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

bool semantic_scene_builder::add_guideline_set_with_offsets(
    std::span<const double> guidelines_x,
    std::span<const double> guidelines_y,
    std::span<const double> offsets_x,
    std::span<const double> offsets_y,
    std::uint32_t& resource_index,
    bool composite_only,
    bool per_point) noexcept {
    resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    const bool multiple =
        guidelines_x.size() > 1U || guidelines_y.size() > 1U;
    const auto finite_offset = [](double value) noexcept {
        return std::isfinite(value) && std::abs(value) <= 1.0;
    };
    if (offsets_x.size() != guidelines_x.size() ||
        offsets_y.size() != guidelines_y.size() ||
        (composite_only && per_point) ||
        multiple != (composite_only || per_point) ||
        guidelines_x.size() > PROGPU_NATIVE_SCENE_MAX_GUIDELINES_PER_AXIS ||
        guidelines_y.size() > PROGPU_NATIVE_SCENE_MAX_GUIDELINES_PER_AXIS ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES ||
        !std::ranges::is_sorted(guidelines_x) ||
        !std::ranges::is_sorted(guidelines_y) ||
        std::ranges::any_of(guidelines_x,
            [](double value) { return !std::isfinite(value); }) ||
        std::ranges::any_of(guidelines_y,
            [](double value) { return !std::isfinite(value); }) ||
        !std::ranges::all_of(offsets_x, finite_offset) ||
        !std::ranges::all_of(offsets_y, finite_offset)) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    try {
        progpu_native_scene_guideline_set header{};
        header.struct_size = sizeof(header);
        header.flags = static_cast<std::uint32_t>(
            PROGPU_NATIVE_SCENE_GUIDELINE_EXPLICIT_OFFSETS);
        if (composite_only) {
            header.flags |= static_cast<std::uint32_t>(
                PROGPU_NATIVE_SCENE_GUIDELINE_COMPOSITE_ONLY);
        } else if (per_point) {
            header.flags |= static_cast<std::uint32_t>(
                PROGPU_NATIVE_SCENE_GUIDELINE_PER_POINT);
        }
        header.guideline_x_count =
            static_cast<std::uint32_t>(guidelines_x.size());
        header.guideline_y_count =
            static_cast<std::uint32_t>(guidelines_y.size());
        implementation::resource_entry resource{};
        resource.record.struct_size = sizeof(resource.record);
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        const std::size_t values_size =
            (guidelines_x.size() + guidelines_y.size()) * sizeof(double);
        resource.payload.resize(sizeof(header) + values_size * 2U);
        std::memcpy(resource.payload.data(), &header, sizeof(header));
        std::size_t offset = sizeof(header);
        const auto append = [&resource, &offset](std::span<const double> data) {
            if (!data.empty()) {
                std::memcpy(
                    resource.payload.data() + offset,
                    data.data(),
                    data.size_bytes());
                offset += data.size_bytes();
            }
        };
        append(guidelines_x);
        append(guidelines_y);
        append(offsets_x);
        append(offsets_y);
        implementation_->resources.reserve(
            implementation_->resources.size() + 1U);
        resource_index = static_cast<std::uint32_t>(
            implementation_->resources.size());
        implementation_->resources.push_back(std::move(resource));
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

bool semantic_scene_builder::save(
    std::uint32_t state_resource_index) noexcept {
    if (!implementation_->valid_state_index(
            state_resource_index, true)) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    if (implementation_->stack_depth >=
            PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH ||
        implementation_->commands.size() >=
            PROGPU_NATIVE_SCENE_MAX_COMMANDS) {
        return implementation_->fail(scene_build_error::capacity_exceeded);
    }
    try {
        implementation_->commands.reserve(
            implementation_->commands.size() + 1U);
        implementation::command_entry command{};
        command.record.struct_size = sizeof(command.record);
        command.record.kind = PROGPU_NATIVE_SCENE_COMMAND_SAVE;
        command.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        command.record.command_id = implementation_->commands.size() + 1U;
        command.record.state_index = state_resource_index;
        command.record.resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        implementation_->commands.push_back(std::move(command));
        implementation_->stack_kinds[implementation_->stack_depth] = 1U;
        ++implementation_->stack_depth;
        implementation_->maximum_stack_depth = std::max(
            implementation_->maximum_stack_depth,
            implementation_->stack_depth);
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

bool semantic_scene_builder::restore() noexcept {
    if (implementation_->stack_depth == 0U ||
        implementation_->stack_kinds[implementation_->stack_depth - 1U] !=
            1U) {
        return implementation_->fail(scene_build_error::unbalanced_stack);
    }
    if (implementation_->commands.size() >=
        PROGPU_NATIVE_SCENE_MAX_COMMANDS) {
        return implementation_->fail(scene_build_error::capacity_exceeded);
    }
    try {
        implementation_->commands.reserve(
            implementation_->commands.size() + 1U);
        implementation::command_entry command{};
        command.record.struct_size = sizeof(command.record);
        command.record.kind = PROGPU_NATIVE_SCENE_COMMAND_RESTORE;
        command.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        command.record.command_id = implementation_->commands.size() + 1U;
        command.record.state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        command.record.resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        implementation_->commands.push_back(std::move(command));
        --implementation_->stack_depth;
        implementation_->stack_kinds[implementation_->stack_depth] = 0U;
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

bool semantic_scene_builder::draw_analytic(
    std::span<const progpu_native_analytic_primitive> primitives,
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
        if (!finite_primitive(primitive)) {
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
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.payload = copy_bytes(primitives);
        const std::uint32_t resource_index = static_cast<std::uint32_t>(
            implementation_->resources.size());

        implementation::command_entry command{};
        command.record.struct_size = sizeof(command.record);
        command.record.kind = PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC;
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

scene_build_error semantic_scene_builder::last_error() const noexcept {
    return implementation_->error;
}

std::uint64_t semantic_scene_builder::scene_id() const noexcept {
    return implementation_->scene_id;
}

std::uint64_t semantic_scene_builder::generation() const noexcept {
    return implementation_->generation;
}

progpu_native_affine_2d
semantic_scene_builder::identity_transform() noexcept {
    return {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
}

progpu_native_scene_state semantic_scene_builder::identity_state() noexcept {
    progpu_native_scene_state state{};
    state.struct_size = sizeof(state);
    state.transform = identity_transform();
    state.opacity = 1.0F;
    return state;
}

} // namespace progpu::native
