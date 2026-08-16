#include "progpu_native_scene_builder.hpp"
#include "progpu_native_scene_builder_internal.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>
#include <utility>

namespace progpu::native {
using scene_builder_detail::copy_bytes;
using scene_builder_detail::finite_primitive;
using scene_builder_detail::finite_rect;
using scene_builder_detail::finite_transform;

namespace {

constexpr std::uint32_t align8(std::uint32_t value) noexcept {
    return (value + 7U) & ~7U;
}

} // namespace

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
            PROGPU_NATIVE_SCENE_STATE_MASK)) != 0U ||
        ((source.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U &&
            !finite_rect(source.clip_rect)) ||
        ((source.flags & PROGPU_NATIVE_SCENE_STATE_MASK) != 0U &&
            (source.mask_resource_index >=
                    implementation_->resources.size() ||
                implementation_->resources[source.mask_resource_index]
                        .record.kind !=
                    PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK)) ||
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
        state.reserved1 = 0U;
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

bool semantic_scene_builder::save(
    std::uint32_t state_resource_index) noexcept {
    if (!implementation_->valid_state_index(state_resource_index)) {
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

bool semantic_scene_builder::build(
    std::vector<std::byte>& stream,
    scene_build_metrics* metrics) const noexcept {
    if (metrics != nullptr) {
        *metrics = {};
    }
    if (implementation_->scene_id == 0U ||
        implementation_->generation == 0U ||
        implementation_->stack_depth != 0U) {
        implementation_->error = implementation_->stack_depth == 0U
            ? scene_build_error::invalid_state
            : scene_build_error::unbalanced_stack;
        return false;
    }
    std::uint64_t previous_resource_id = 0U;
    for (const auto& resource : implementation_->resources) {
        if (resource.record.resource_id <= previous_resource_id ||
            resource.record.generation == 0U) {
            implementation_->error = scene_build_error::invalid_state;
            return false;
        }
        previous_resource_id = resource.record.resource_id;
    }
    try {
        const std::uint32_t command_count = static_cast<std::uint32_t>(
            implementation_->commands.size());
        const std::uint32_t resource_count = static_cast<std::uint32_t>(
            implementation_->resources.size());
        const std::uint64_t command_bytes =
            static_cast<std::uint64_t>(command_count) *
                sizeof(progpu_native_scene_command);
        const std::uint64_t resource_bytes =
            static_cast<std::uint64_t>(resource_count) *
                sizeof(progpu_native_scene_resource);
        const std::uint64_t command_offset =
            align8(sizeof(progpu_native_scene_header));
        const std::uint64_t resource_offset = align8(
            static_cast<std::uint32_t>(command_offset + command_bytes));
        const std::uint64_t arena_offset = align8(
            static_cast<std::uint32_t>(resource_offset + resource_bytes));
        if (arena_offset > PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES) {
            implementation_->error = scene_build_error::capacity_exceeded;
            return false;
        }

        std::vector<std::byte> built;
        const std::uint64_t reserved_size = std::min<std::uint64_t>(
            PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES,
            arena_offset + implementation_->arena_reserve);
        built.reserve(static_cast<std::size_t>(reserved_size));
        built.assign(static_cast<std::size_t>(arena_offset), std::byte{});
        std::vector<progpu_native_scene_resource> resources(resource_count);
        std::vector<progpu_native_scene_command> commands(command_count);
        const auto append = [&](const std::byte* bytes,
                                std::size_t count,
                                std::uint32_t& offset) -> bool {
            if (count == 0U) {
                offset = 0U;
                return true;
            }
            const std::size_t aligned = (built.size() + 7U) & ~7U;
            if (aligned > PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES ||
                count > PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES - aligned) {
                return false;
            }
            built.resize(aligned, std::byte{});
            offset = static_cast<std::uint32_t>(aligned);
            built.insert(built.end(), bytes, bytes + count);
            return true;
        };

        for (std::uint32_t index = 0U; index < resource_count; ++index) {
            const auto& source = implementation_->resources[index];
            auto resource = source.record;
            std::vector<std::byte> brush_payload{};
            std::vector<std::byte> brush_auxiliary{};
            std::vector<std::byte> text_style_payload{};
            const std::vector<std::byte>* payload = &source.payload;
            if (source.brush_table) {
                brush_payload = copy_bytes(
                    std::span<const progpu_native_scene_brush>(
                    implementation_->brushes.data(),
                    implementation_->brushes.size()));
                payload = &brush_payload;
                brush_auxiliary = copy_bytes(
                    std::span<const progpu_native_scene_gradient_stop>(
                        implementation_->gradient_stops.data(),
                        implementation_->gradient_stops.size()));
            } else if (source.text_style_table) {
                text_style_payload = copy_bytes(
                    std::span<const progpu_native_scene_text_style>(
                    implementation_->text_styles.data(),
                    implementation_->text_styles.size()));
                payload = &text_style_payload;
            }
            if (!append(
                    payload->data(),
                    payload->size(),
                    resource.payload_offset) ||
                !append(
                    source.brush_table
                        ? brush_auxiliary.data()
                        : source.auxiliary.data(),
                    source.brush_table
                        ? brush_auxiliary.size()
                        : source.auxiliary.size(),
                    resource.auxiliary_offset)) {
                implementation_->error = scene_build_error::capacity_exceeded;
                return false;
            }
            resource.payload_size = static_cast<std::uint32_t>(payload->size());
            resource.auxiliary_size = static_cast<std::uint32_t>(
                source.brush_table
                    ? brush_auxiliary.size()
                    : source.auxiliary.size());
            resources[index] = resource;
        }
        for (std::uint32_t index = 0U; index < command_count; ++index) {
            const auto& source = implementation_->commands[index];
            auto command = source.record;
            if (!append(
                    source.payload.data(),
                    source.payload.size(),
                    command.payload_offset)) {
                implementation_->error = scene_build_error::capacity_exceeded;
                return false;
            }
            command.payload_size = static_cast<std::uint32_t>(
                source.payload.size());
            commands[index] = command;
        }
        if (built.size() > PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES ||
            built.size() > std::numeric_limits<std::uint32_t>::max()) {
            implementation_->error = scene_build_error::capacity_exceeded;
            return false;
        }

        progpu_native_scene_header header{};
        header.struct_size = sizeof(header);
        header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
        header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
        header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
        header.total_size = static_cast<std::uint32_t>(built.size());
        header.scene_id = implementation_->scene_id;
        header.generation = implementation_->generation;
        header.command_offset = static_cast<std::uint32_t>(command_offset);
        header.command_count = command_count;
        header.command_stride = sizeof(progpu_native_scene_command);
        header.resource_offset = static_cast<std::uint32_t>(resource_offset);
        header.resource_count = resource_count;
        header.resource_stride = sizeof(progpu_native_scene_resource);
        header.arena_offset = static_cast<std::uint32_t>(arena_offset);
        header.arena_size = header.total_size - header.arena_offset;
        std::memcpy(built.data(), &header, sizeof(header));
        if (!commands.empty()) {
            std::memcpy(
                built.data() + header.command_offset,
                commands.data(),
                commands.size() * sizeof(commands[0]));
        }
        if (!resources.empty()) {
            std::memcpy(
                built.data() + header.resource_offset,
                resources.data(),
                resources.size() * sizeof(resources[0]));
        }
        if (metrics != nullptr) {
            metrics->command_count = command_count;
            metrics->resource_count = resource_count;
            metrics->brush_count = static_cast<std::uint32_t>(
                implementation_->brushes.size());
            metrics->text_style_count = static_cast<std::uint32_t>(
                implementation_->text_styles.size());
            metrics->maximum_stack_depth =
                implementation_->maximum_stack_depth;
            metrics->arena_bytes = header.arena_size;
            metrics->stream_bytes = header.total_size;
        }
        stream.swap(built);
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        implementation_->error = scene_build_error::out_of_memory;
        return false;
    } catch (...) {
        implementation_->error = scene_build_error::invalid_state;
        return false;
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
