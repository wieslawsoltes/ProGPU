#include "progpu_native_scene_builder_internal.hpp"

#include "progpu_native_scene.hpp"
#include "progpu_native_semantic_layer_mask.hpp"
#include "progpu_native_semantic_validation.hpp"

#include <algorithm>
#include <cstring>
#include <limits>
#include <new>
#include <utility>

namespace progpu::native {
using scene_builder_detail::copy_bytes;

bool semantic_scene_builder::add_rounded_rectangle_mask(
    const progpu_native_scene_layer_mask& source,
    std::uint32_t& resource_index) noexcept {
    resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    progpu_native_scene_layer_mask mask = source;
    mask.struct_size = sizeof(mask);
    mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE;
    mask.flags = 0U;
    mask.reserved = 0U;
    mask.reserved0 = 0U;
    mask.reserved1 = 0U;
    mask.reserved2 = 0U;
    if (!semantic::is_valid_semantic_layer_mask(mask) ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    try {
        implementation_->resources.reserve(
            implementation_->resources.size() + 1U);
        implementation::resource_entry resource{};
        resource.record.struct_size = sizeof(resource.record);
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.payload = copy_bytes(
            std::span<const progpu_native_scene_layer_mask>(&mask, 1U));
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

bool semantic_scene_builder::add_coverage_mask(
    const progpu_native_scene_layer_coverage_mask& source,
    std::span<const std::byte> coverage,
    std::uint32_t& resource_index) noexcept {
    resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    progpu_native_scene_layer_coverage_mask mask = source;
    mask.struct_size = sizeof(mask);
    mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_COVERAGE_BITMAP;
    mask.flags = 0U;
    mask.reserved0 = 0U;
    mask.reserved1 = 0U;
    if (!semantic::is_valid_semantic_layer_coverage_mask(
            mask,
            coverage.size()) ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    try {
        implementation_->resources.reserve(
            implementation_->resources.size() + 1U);
        implementation::resource_entry resource{};
        resource.record.struct_size = sizeof(resource.record);
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.payload = copy_bytes(
            std::span<const progpu_native_scene_layer_coverage_mask>(
                &mask,
                1U));
        resource.auxiliary.assign(coverage.begin(), coverage.end());
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

bool semantic_scene_builder::add_brush_mask(
    const progpu_native_scene_layer_brush_mask& source,
    std::span<const progpu_native_scene_gradient_stop> gradient_stops,
    std::uint32_t& resource_index) noexcept {
    resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (gradient_stops.size() >
            std::numeric_limits<std::uint32_t>::max() ||
        gradient_stops.size() > PROGPU_NATIVE_SCENE_MAX_GRADIENT_STOPS ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    progpu_native_scene_layer_brush_mask mask = source;
    mask.struct_size = sizeof(mask);
    mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH;
    mask.flags = 0U;
    mask.gradient_stop_count = static_cast<std::uint32_t>(
        gradient_stops.size());
    mask.reserved0 = 0U;
    mask.brush.stop_offset = 0U;
    if (!semantic::is_valid_semantic_layer_brush_mask(
            mask, gradient_stops)) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    try {
        implementation_->resources.reserve(
            implementation_->resources.size() + 1U);
        implementation::resource_entry resource{};
        resource.record.struct_size = sizeof(resource.record);
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.payload = copy_bytes(
            std::span<const progpu_native_scene_layer_brush_mask>(&mask, 1U));
        resource.auxiliary = copy_bytes(gradient_stops);
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

bool semantic_scene_builder::add_analytic_mask_chain(
    std::span<const progpu_native_scene_layer_mask> masks,
    std::uint32_t& resource_index) noexcept {
    resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (masks.size() < 2U ||
        masks.size() > PROGPU_NATIVE_SCENE_MAX_ANALYTIC_MASKS ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    progpu_native_scene_layer_mask_chain chain{};
    chain.struct_size = sizeof(chain);
    chain.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ANALYTIC_CHAIN;
    chain.mask_count = static_cast<std::uint32_t>(masks.size());
    for (std::size_t index = 0U; index < masks.size(); ++index) {
        auto mask = masks[index];
        mask.struct_size = sizeof(mask);
        mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE;
        mask.flags = 0U;
        mask.reserved = 0U;
        mask.reserved0 = 0U;
        mask.reserved1 = 0U;
        mask.reserved2 = 0U;
        chain.masks[index] = mask;
    }
    if (!semantic::is_valid_semantic_layer_mask_chain(chain)) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    try {
        implementation_->resources.reserve(
            implementation_->resources.size() + 1U);
        implementation::resource_entry resource{};
        resource.record.struct_size = sizeof(resource.record);
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.payload = copy_bytes(
            std::span<const progpu_native_scene_layer_mask_chain>(
                &chain,
                1U));
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

bool semantic_scene_builder::add_vector_clip_mask(
    std::span<const progpu_native_scene_clip_path> paths,
    std::span<const progpu_native_path_segment> segments,
    float opacity,
    std::uint32_t& resource_index) noexcept {
    return add_vector_clip_mask(
        paths, segments, {}, opacity, resource_index);
}

bool semantic_scene_builder::add_vector_clip_mask(
    std::span<const progpu_native_scene_clip_path> paths,
    std::span<const progpu_native_path_segment> segments,
    std::span<const progpu_native_scene_path_boolean_node> boolean_nodes,
    float opacity,
    std::uint32_t& resource_index) noexcept {
    resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (paths.size() > std::numeric_limits<std::uint32_t>::max() ||
        segments.size() > std::numeric_limits<std::uint32_t>::max() ||
        boolean_nodes.size() > std::numeric_limits<std::uint32_t>::max() ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    progpu_native_scene_layer_vector_mask mask{};
    mask.struct_size = sizeof(mask);
    mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN;
    mask.path_count = static_cast<std::uint32_t>(paths.size());
    mask.segment_count = static_cast<std::uint32_t>(segments.size());
    mask.boolean_node_count =
        static_cast<std::uint32_t>(boolean_nodes.size());
    mask.opacity = opacity;
    if (!semantic::is_valid_semantic_layer_vector_mask(
            mask, paths, segments, boolean_nodes)) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    try {
        implementation_->resources.reserve(
            implementation_->resources.size() + 1U);
        implementation::resource_entry resource{};
        resource.record.struct_size = sizeof(resource.record);
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.payload = copy_bytes(
            std::span<const progpu_native_scene_layer_vector_mask>(
                &mask, 1U));
        resource.auxiliary.resize(
            paths.size_bytes() + segments.size_bytes() +
            boolean_nodes.size_bytes());
        std::memcpy(
            resource.auxiliary.data(),
            paths.data(),
            paths.size_bytes());
        std::memcpy(
            resource.auxiliary.data() + paths.size_bytes(),
            segments.data(),
            segments.size_bytes());
        if (!boolean_nodes.empty()) {
            std::memcpy(
                resource.auxiliary.data() + paths.size_bytes() +
                    segments.size_bytes(),
                boolean_nodes.data(),
                boolean_nodes.size_bytes());
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

bool semantic_scene_builder::add_effect_chain(
    std::span<const progpu_native_group_effect> sources,
    std::uint32_t revision,
    std::uint32_t& resource_index) noexcept {
    resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (sources.empty() || sources.size() > PROGPU_NATIVE_MAX_GROUP_EFFECTS ||
        revision == 0U || implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    try {
        std::vector<progpu_native_group_effect> effects;
        effects.reserve(sources.size());
        for (const auto& source : sources) {
            auto effect = source;
            effect.struct_size = sizeof(effect);
            effect.flags = 0U;
            effect.reserved = 0U;
            effect.reserved2 = 0U;
            if (!semantic::is_valid_semantic_effect(effect)) {
                return implementation_->fail(
                    scene_build_error::invalid_argument);
            }
            effects.push_back(effect);
        }
        const progpu_native_scene_effect_chain chain{
            sizeof(progpu_native_scene_effect_chain),
            static_cast<std::uint32_t>(effects.size()),
            revision,
            0U};
        implementation_->resources.reserve(
            implementation_->resources.size() + 1U);
        implementation::resource_entry resource{};
        resource.record.struct_size = sizeof(resource.record);
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_EFFECT_CHAIN;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.payload = copy_bytes(
            std::span<const progpu_native_scene_effect_chain>(&chain, 1U));
        resource.auxiliary = copy_bytes(
            std::span<const progpu_native_group_effect>(effects));
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

bool semantic_scene_builder::push_layer(
    const progpu_native_scene_layer& source) noexcept {
    progpu_native_scene_layer layer = source;
    layer.struct_size = sizeof(layer);
    const bool local_cache = (layer.flags &
        PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE) != 0U;
    const bool explicit_composite_state = (layer.flags &
        PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE) != 0U;
    if (!local_cache && !explicit_composite_state) {
        layer.reserved0 = 0U;
    }
    layer.reserved1 = 0U;
    const auto valid_resource = [&](std::uint32_t index,
                                    std::uint32_t kind) noexcept {
        return index == PROGPU_NATIVE_SCENE_NO_INDEX ||
            (index < implementation_->resources.size() &&
                implementation_->resources[index].record.kind == kind);
    };
    const auto valid_composite_state = [&]() noexcept {
        if (!local_cache && !explicit_composite_state) {
            return true;
        }
        if (layer.reserved0 >= implementation_->resources.size()) {
            return false;
        }
        const auto& resource = implementation_->resources[layer.reserved0];
        if (resource.record.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE ||
            resource.payload.size() != sizeof(progpu_native_scene_state)) {
            return false;
        }
        progpu_native_scene_state state{};
        std::memcpy(&state, resource.payload.data(), sizeof(state));
        const std::uint32_t composite_flags = local_cache
            ? PROGPU_NATIVE_SCENE_STATE_CLIP_RECT |
                PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET
            : PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
        const bool guideline_is_valid =
            (state.flags & PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) == 0U ||
            (state.guideline_resource_index <
                    implementation_->resources.size() &&
                implementation_->resources[state.guideline_resource_index]
                        .record.kind ==
                    PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET);
        const bool canonical_transform = local_cache ||
            (state.transform.m11 == 1.0F &&
                state.transform.m12 == 0.0F &&
                state.transform.m21 == 0.0F &&
                state.transform.m22 == 1.0F &&
                state.transform.m31 == 0.0F &&
                state.transform.m32 == 0.0F);
        return (state.flags & ~composite_flags) == 0U &&
            canonical_transform &&
            state.opacity == 1.0F && state.mask_resource_index == 0U &&
            guideline_is_valid;
    };
    const bool materialized = scene::layer_requires_materialization(layer);
    if (!semantic::is_valid_semantic_layer(layer) ||
        !valid_resource(
            layer.mask_resource_index,
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) ||
        !valid_resource(
            layer.effect_resource_index,
            PROGPU_NATIVE_SCENE_RESOURCE_EFFECT_CHAIN) ||
        !valid_composite_state()) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    if (implementation_->stack_depth >=
            PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH ||
        (materialized && implementation_->materialized_layer_depth >=
            PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS) ||
        implementation_->commands.size() >=
            PROGPU_NATIVE_SCENE_MAX_COMMANDS) {
        return implementation_->fail(scene_build_error::capacity_exceeded);
    }
    try {
        implementation_->commands.reserve(
            implementation_->commands.size() + 1U);
        implementation::command_entry command{};
        command.record.struct_size = sizeof(command.record);
        command.record.kind = PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER;
        command.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        command.record.command_id = implementation_->commands.size() + 1U;
        command.record.state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        command.record.resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        command.payload = copy_bytes(
            std::span<const progpu_native_scene_layer>(&layer, 1U));
        implementation_->commands.push_back(std::move(command));
        implementation_->stack_kinds[implementation_->stack_depth] =
            materialized ? 3U : 2U;
        ++implementation_->stack_depth;
        implementation_->materialized_layer_depth += materialized ? 1U : 0U;
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

bool semantic_scene_builder::pop_layer() noexcept {
    if (implementation_->stack_depth == 0U) {
        return implementation_->fail(scene_build_error::unbalanced_stack);
    }
    const std::uint8_t kind =
        implementation_->stack_kinds[implementation_->stack_depth - 1U];
    if (kind != 2U && kind != 3U) {
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
        command.record.kind = PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER;
        command.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        command.record.command_id = implementation_->commands.size() + 1U;
        command.record.state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        command.record.resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        implementation_->commands.push_back(std::move(command));
        --implementation_->stack_depth;
        implementation_->stack_kinds[implementation_->stack_depth] = 0U;
        implementation_->materialized_layer_depth -= kind == 3U ? 1U : 0U;
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

} // namespace progpu::native
