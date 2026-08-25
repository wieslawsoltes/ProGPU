#include "progpu_native_frame_execution_common.hpp"
#include "progpu_native_semantic_draw_execution.hpp"
#include "progpu_native_3d_execution.hpp"

namespace progpu::native::execution {

progpu_native_status render_scene(
    progpu_native_engine* engine,
    const progpu_native_scene_frame* frame,
    progpu_native_scene_frame_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    constexpr std::uint32_t legacy_metrics_size = offsetof(
        progpu_native_scene_frame_metrics,
        brush_upload_bytes);
    constexpr std::uint32_t legacy_frame_size = offsetof(
        progpu_native_scene_frame,
        flags);
    if (metrics != nullptr &&
        metrics->struct_size >= legacy_metrics_size) {
        const std::uint32_t struct_size = metrics->struct_size;
        std::memset(
            metrics,
            0,
            std::min<std::size_t>(
                struct_size,
                sizeof(progpu_native_scene_frame_metrics)));
        metrics->struct_size = struct_size;
    }
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "Semantic scene rendering is owner-thread affine.");
    }
    if (frame == nullptr ||
        frame->struct_size < legacy_frame_size ||
        frame->width == 0U ||
        frame->height == 0U || !std::isfinite(frame->dpi_scale) ||
        frame->dpi_scale <= 0.0F || frame->target_view == 0U ||
        !std::isfinite(frame->clear_color.r) ||
        !std::isfinite(frame->clear_color.g) ||
        !std::isfinite(frame->clear_color.b) ||
        !std::isfinite(frame->clear_color.a) ||
        frame->scene_id == 0U || frame->generation == 0U) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The semantic scene frame descriptor is invalid.");
    }
    constexpr std::uint32_t allowed_frame_flags =
        PROGPU_NATIVE_SCENE_FRAME_PRESERVE_TARGET |
        PROGPU_NATIVE_SCENE_FRAME_DAMAGE_RECT;
    const bool has_extended_frame =
        frame->struct_size >= sizeof(progpu_native_scene_frame);
    const auto frame_flags = has_extended_frame ? frame->flags : 0U;
    const bool damage_requested =
        (frame_flags & PROGPU_NATIVE_SCENE_FRAME_DAMAGE_RECT) != 0U;
    const bool preserve_requested =
        (frame_flags & PROGPU_NATIVE_SCENE_FRAME_PRESERVE_TARGET) != 0U;
    const auto logical_width =
        static_cast<float>(frame->width) / frame->dpi_scale;
    const auto logical_height =
        static_cast<float>(frame->height) / frame->dpi_scale;
    if ((frame_flags & ~allowed_frame_flags) != 0U ||
        (damage_requested &&
            (!preserve_requested || !std::isfinite(frame->damage_x) ||
                !std::isfinite(frame->damage_y) ||
                !std::isfinite(frame->damage_width) ||
                !std::isfinite(frame->damage_height) ||
                frame->damage_width <= 0.0F ||
                frame->damage_height <= 0.0F ||
                frame->damage_x >= logical_width ||
                frame->damage_y >= logical_height ||
                frame->damage_x + frame->damage_width <= 0.0F ||
                frame->damage_y + frame->damage_height <= 0.0F))) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The semantic scene frame damage descriptor is invalid.");
    }
    if (frame->scene_id != engine->semantic_scene_id ||
        frame->generation != engine->semantic_scene_generation ||
        engine->semantic_scene_snapshot.empty()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The requested immutable semantic scene generation is not installed.");
    }

    const auto* bytes = engine->semantic_scene_snapshot.data();
    const auto& header = engine->semantic_scene_header;
    const auto read_command = [&](std::uint32_t index) noexcept {
        progpu_native_scene_command command{};
        std::memcpy(
            &command,
            bytes + header.command_offset +
                static_cast<std::size_t>(index) * header.command_stride,
            sizeof(command));
        return command;
    };
    const auto read_resource = [&](std::uint32_t index) noexcept {
        progpu_native_scene_resource resource{};
        std::memcpy(
            &resource,
            bytes + header.resource_offset +
                static_cast<std::size_t>(index) * header.resource_stride,
            sizeof(resource));
        return resource;
    };
    const auto revision32 = [](std::uint64_t value) noexcept {
        std::uint32_t result = static_cast<std::uint32_t>(
            value ^ (value >> 32U));
        return result == 0U ? 1U : result;
    };
    const auto span_is_multiple = [](std::uint32_t size,
                                     std::size_t stride) noexcept {
        return stride != 0U && size != 0U && size % stride == 0U;
    };
    const auto try_get_path_resource_counts = [&](
        const progpu_native_scene_resource& resource,
        std::uint64_t& path_count,
        std::uint64_t& segment_count,
        std::uint64_t& boolean_node_count) noexcept {
        path_count = 0U;
        segment_count = 0U;
        boolean_node_count = 0U;
        if (!span_is_multiple(
                resource.payload_size,
                sizeof(progpu_native_scene_path_fill))) {
            return false;
        }
        path_count = resource.payload_size /
            sizeof(progpu_native_scene_path_fill);
        for (std::uint64_t index = 0U; index < path_count; ++index) {
            progpu_native_scene_path_fill path{};
            std::memcpy(
                &path,
                bytes + resource.payload_offset +
                    index * sizeof(path),
                sizeof(path));
            if (path.segment_offset > segment_count ||
                (path.boolean_node_count != 0U &&
                    path.boolean_node_offset != boolean_node_count) ||
                path.segment_count >
                    std::numeric_limits<std::uint64_t>::max() -
                        path.segment_offset ||
                path.boolean_node_count >
                    std::numeric_limits<std::uint64_t>::max() -
                        boolean_node_count) {
                return false;
            }
            segment_count = std::max(
                segment_count,
                path.segment_offset + path.segment_count);
            boolean_node_count += path.boolean_node_count;
        }
        if (segment_count >
                std::numeric_limits<std::uint64_t>::max() /
                    sizeof(progpu_native_path_segment) ||
            boolean_node_count >
                (std::numeric_limits<std::uint64_t>::max() -
                    segment_count * sizeof(progpu_native_path_segment)) /
                    sizeof(progpu_native_scene_path_boolean_node)) {
            return false;
        }
        const std::uint64_t required_auxiliary =
            segment_count * sizeof(progpu_native_path_segment) +
            boolean_node_count *
                sizeof(progpu_native_scene_path_boolean_node);
        return required_auxiliary == resource.auxiliary_size;
    };

    auto& semantic_brush_page = engine->semantic_brush_cache;
    if (!semantic_brush_page.cache_valid ||
        semantic_brush_page.scene_hash != engine->semantic_hashes.brush) {
        if (!compile_brush_page(
                bytes,
                header,
                engine->semantic_hashes.brush,
                semantic_brush_page)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic retained-brush page could not be compiled.");
        }
    }
    auto& semantic_text_style_page = engine->semantic_text_style_cache;
    if (!semantic_text_style_page.cache_valid ||
        semantic_text_style_page.scene_hash !=
            engine->semantic_hashes.text_style) {
        if (!compile_text_style_page(
                bytes,
                header,
                engine->semantic_hashes.text_style,
                semantic_text_style_page)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic retained text-style page could not be compiled.");
        }
    }
    const auto resolve_brush = [&](
        std::uint32_t command_index,
        const progpu_native_scene_command& command,
        std::uint32_t record_index,
        std::uint32_t& packed_index,
        const progpu_native_scene_brush*& brush) noexcept {
        packed_index = 0U;
        brush = nullptr;
        if (command.payload_size == 0U) {
            return true;
        }
        if (!try_get_draw_brush_index(
                semantic_brush_page,
                command_index,
                record_index,
                packed_index)) {
            return false;
        }
        brush = try_get_packed_brush(semantic_brush_page, packed_index);
        return brush != nullptr;
    };
    const auto apply_analytic_material = [](
        progpu_native_analytic_primitive& primitive,
        const progpu_native_scene_brush& brush) noexcept {
        if (brush.type == PROGPU_NATIVE_SCENE_BRUSH_SOLID) {
            primitive.color = brush.colors[0];
        } else {
            primitive.color = {
                primitive.x + primitive.width * 0.5F,
                primitive.y + primitive.height * 0.5F,
                0.0F,
                1.0F};
        }
    };
    const auto apply_path_material = [](
        auto& path,
        const progpu_native_scene_brush& brush) noexcept {
        if (brush.type == PROGPU_NATIVE_SCENE_BRUSH_SOLID) {
            path.color = brush.colors[0];
        }
    };
    const auto apply_geometry_material = [](
        progpu_native_geometry_primitive& primitive,
        const progpu_native_scene_brush& brush) noexcept {
        if (brush.type == PROGPU_NATIVE_SCENE_BRUSH_SOLID) {
            primitive.color = brush.colors[0];
        }
    };

    semantic_layer_budget layer_budget{};
    semantic_layer_target_cursor layer_budget_cursor(
        bytes,
        frame->width,
        frame->height,
        frame->dpi_scale);
    bool semantic_has_materialized_layers = false;
    bool semantic_has_layer_masks = false;
    std::uint32_t semantic_layer_mask_kind =
        PROGPU_NATIVE_GROUP_MASK_NONE;
    bool semantic_has_layer_effects = false;
    bool semantic_has_drop_shadows = false;
    std::uint32_t semantic_materialized_layer_count = 0U;
    std::uint32_t semantic_backdrop_layer_count = 0U;
    std::uint32_t semantic_effected_backdrop_layer_count = 0U;
    std::uint32_t semantic_advanced_layer_count = 0U;
    std::uint32_t semantic_advanced_source_width = 0U;
    std::uint32_t semantic_advanced_source_height = 0U;
    std::uint32_t semantic_effect_node_count = 0U;
    std::uint32_t semantic_effect_pass_count = 0U;
    std::uint32_t semantic_effect_chain_revision = 0U;
    std::uint64_t semantic_layer_coverage_texture_bytes = 0U;
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto command = read_command(index);
        const auto parent_target_extent = layer_budget_cursor.current();
        const auto target_extent = layer_budget_cursor.advance(command);
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) {
            auto layer = semantic_default_layer();
            if (command.payload_size != 0U) {
                std::memcpy(
                    &layer,
                    bytes + command.payload_offset,
                    sizeof(layer));
            }
            const bool materialized =
                progpu::native::scene::layer_requires_materialization(layer);
            semantic_has_materialized_layers |= materialized;
            if (layer.mask_resource_index !=
                    PROGPU_NATIVE_SCENE_NO_INDEX) {
                semantic_has_layer_masks = true;
                const auto mask_resource = read_resource(
                    layer.mask_resource_index);
                std::uint32_t mask_kind = 0U;
                std::memcpy(
                    &mask_kind,
                    bytes + mask_resource.payload_offset +
                        sizeof(std::uint32_t),
                    sizeof(mask_kind));
                if (mask_kind ==
                    PROGPU_NATIVE_SCENE_LAYER_MASK_ANALYTIC_CHAIN) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_UNSUPPORTED,
                        "Nested analytic masks are currently supported for per-draw state, not isolated layer composites.");
                }
                semantic_layer_mask_kind = mask_kind ==
                        PROGPU_NATIVE_SCENE_LAYER_MASK_COVERAGE_BITMAP ||
                        mask_kind ==
                            PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN ||
                        mask_kind == PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH ||
                        mask_kind == PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY ||
                        mask_kind == PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE ||
                        mask_kind == PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE
                    ? PROGPU_NATIVE_GROUP_MASK_TEXTURE
                    : PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE;
                if (mask_kind ==
                        PROGPU_NATIVE_SCENE_LAYER_MASK_COVERAGE_BITMAP ||
                    mask_kind ==
                        PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN ||
                    mask_kind == PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH ||
                    mask_kind == PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY ||
                    mask_kind == PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE ||
                    mask_kind == PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE) {
                    std::uint64_t texture_multiplier = 1U;
                    if (mask_kind ==
                        PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE) {
                        progpu_native_scene_layer_composite_mask composite{};
                        std::memcpy(
                            &composite,
                            bytes + mask_resource.payload_offset,
                            std::min<std::size_t>(
                                sizeof(composite),
                                mask_resource.payload_size));
                        texture_multiplier =
                            static_cast<std::uint64_t>(
                                composite.component_count) + 2U +
                            static_cast<std::uint64_t>(
                                composite.picture_mask_count) * 4U;
                    } else if (mask_kind ==
                        PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE) {
                        texture_multiplier = 5U;
                    }
                    const std::uint64_t mask_texture_bytes = mask_kind ==
                            PROGPU_NATIVE_SCENE_LAYER_MASK_COVERAGE_BITMAP
                        ? mask_resource.auxiliary_size
                        : static_cast<std::uint64_t>(
                            parent_target_extent.width) *
                            parent_target_extent.height * texture_multiplier;
                    if (mask_texture_bytes >
                            PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES -
                                semantic_layer_coverage_texture_bytes) {
                        return engine->fail(
                            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                            "The semantic coverage-mask textures exceed their bounded aggregate budget.");
                    }
                    semantic_layer_coverage_texture_bytes +=
                        mask_texture_bytes;
                }
            }
            const bool effected = layer.effect_resource_index !=
                PROGPU_NATIVE_SCENE_NO_INDEX;
            semantic_has_layer_effects |= effected;
            if (effected) {
                const auto effect_resource = read_resource(
                    layer.effect_resource_index);
                progpu_native_scene_effect_chain chain{};
                std::memcpy(
                    &chain,
                    bytes + effect_resource.payload_offset,
                    sizeof(chain));
                if (chain.effect_count >
                    PROGPU_NATIVE_MAX_GROUP_EFFECTS ||
                    semantic_effect_node_count >
                        semantic_max_effect_passes - chain.effect_count) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "The semantic effect-chain node count exceeds its bounded compilation budget.");
                }
                semantic_effect_node_count += chain.effect_count;
                semantic_effect_chain_revision = chain.revision;
                for (std::uint32_t effect_index = 0U;
                     effect_index < chain.effect_count;
                     ++effect_index) {
                    progpu_native_group_effect effect{};
                    std::memcpy(
                        &effect,
                        bytes + effect_resource.auxiliary_offset +
                            static_cast<std::size_t>(effect_index) *
                                sizeof(effect),
                        sizeof(effect));
                    const bool drop_shadow = effect.kind ==
                        PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW;
                    const std::uint32_t passes = drop_shadow ? 3U : 2U;
                    if (semantic_effect_pass_count >
                        semantic_max_effect_passes - passes) {
                        return engine->fail(
                            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                            "The semantic effect-chain pass count exceeds its bounded compilation budget.");
                    }
                    semantic_effect_pass_count += passes;
                    semantic_has_drop_shadows |= drop_shadow;
                    constexpr float maximum_physical_sigma =
                        128.0F / 3.0F;
                    const float sigma_x = effect.sigma_x * frame->dpi_scale;
                    const float sigma_y = effect.sigma_y * frame->dpi_scale;
                    const float offset_x = effect.offset_x * frame->dpi_scale;
                    const float offset_y = effect.offset_y * frame->dpi_scale;
                    if (!std::isfinite(sigma_x) ||
                        !std::isfinite(sigma_y) ||
                        sigma_x > maximum_physical_sigma ||
                        sigma_y > maximum_physical_sigma ||
                        (drop_shadow &&
                            (!std::isfinite(offset_x) ||
                             !std::isfinite(offset_y)))) {
                        return engine->fail(
                            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                            "A semantic effect exceeds the finite physical kernel contract.");
                    }
                }
            }
            if (materialized && semantic_materialized_layer_count ==
                    semantic_max_draw_passes) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                    "The semantic isolated-layer pass count exceeds its bounded compilation budget.");
            }
            semantic_materialized_layer_count += materialized ? 1U : 0U;
            const bool backdrop = materialized &&
                (layer.flags & PROGPU_NATIVE_SCENE_LAYER_BACKDROP) != 0U;
            semantic_backdrop_layer_count += backdrop ? 1U : 0U;
            semantic_effected_backdrop_layer_count +=
                backdrop && effected ? 1U : 0U;
            if (materialized &&
                is_advanced_group_blend(layer.blend_mode)) {
                ++semantic_advanced_layer_count;
                semantic_advanced_source_width = std::max(
                    semantic_advanced_source_width,
                    target_extent.width);
                semantic_advanced_source_height = std::max(
                    semantic_advanced_source_height,
                    target_extent.height);
            }
            if (!layer_budget.push(
                    target_extent,
                    materialized,
                    effected)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                    "The semantic isolated-layer stack exceeds its bounded depth or aggregate pixel budget.");
            }
        } else if (command.kind ==
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) {
            layer_budget.pop();
        }
    }

    /* Preflight every typed payload before the first target submission. */
    std::uint32_t semantic_draw_count = 0U;
    std::uint32_t semantic_analytic_draw_count = 0U;
    std::uint32_t semantic_path_draw_count = 0U;
    std::uint32_t semantic_glyph_draw_count = 0U;
    std::uint32_t semantic_image_draw_count = 0U;
    std::uint64_t semantic_image_vertex_count = 0U;
    std::uint32_t semantic_3d_draw_count = 0U;
    std::uint64_t semantic_analytic_vertex_bytes = 0U;
    std::uint64_t semantic_analytic_index_bytes = 0U;
    std::uint64_t semantic_path_count = 0U;
    std::uint64_t semantic_path_segment_count = 0U;
    std::uint64_t semantic_path_boolean_node_count = 0U;
    std::uint64_t semantic_glyph_outline_count = 0U;
    std::uint64_t semantic_glyph_segment_count = 0U;
    std::uint64_t semantic_glyph_count = 0U;
    std::uint64_t semantic_color_glyph_bitmap_count = 0U;
    std::uint64_t semantic_color_glyph_pixel_bytes = 0U;
    bool semantic_has_styled_glyphs = false;
    bool semantic_has_image_color_matrices = false;
    bool semantic_has_state_masks = false;
    bool semantic_has_vector_mask_chains = false;
    bool semantic_has_text_mask_chains = false;
    bool semantic_has_image_mask_chains = false;
    bool semantic_has_masked_glyphs = false;
    bool semantic_has_masked_images = false;
    semantic_compilation_budget compilation_budget{};
    semantic_state_cursor preflight_state_cursor(
        bytes, header, frame->dpi_scale);
    semantic_layer_target_cursor preflight_target_cursor(
        bytes,
        frame->width,
        frame->height,
        frame->dpi_scale);
    std::vector<std::uint8_t> semantic_generated_masks_budgeted;
    std::vector<std::uint8_t> semantic_glyph_resources_budgeted;
    try {
        semantic_generated_masks_budgeted.resize(header.resource_count, 0U);
        semantic_glyph_resources_budgeted.resize(header.resource_count, 0U);
    } catch (const std::bad_alloc&) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The semantic vector-mask budget table could not be allocated.");
    }
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto command = read_command(index);
        const auto target_extent = preflight_target_cursor.advance(command);
        const auto state = localize_semantic_state(
            preflight_state_cursor.advance(command),
            target_extent,
            frame->dpi_scale);
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_SAVE ||
            command.kind == PROGPU_NATIVE_SCENE_COMMAND_RESTORE) {
            continue;
        }
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER ||
            command.kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) {
            continue;
        }
        if (command.kind < PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
            command.kind >
                PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH) {
            continue;
        }
        if ((state.flags & PROGPU_NATIVE_SCENE_STATE_MASK) != 0U) {
            if (command.kind ==
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_LINE_3D_BATCH ||
                command.kind ==
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_UNSUPPORTED,
                    "Per-draw masks over native retained 3D commands require an isolated layer.");
            }
            const auto mask_resource = read_resource(
                state.mask_resource_index);
            std::uint32_t mask_kind = 0U;
            std::memcpy(
                &mask_kind,
                bytes + mask_resource.payload_offset +
                    sizeof(std::uint32_t),
                sizeof(mask_kind));
            if (mask_kind !=
                    PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE &&
                mask_kind !=
                    PROGPU_NATIVE_SCENE_LAYER_MASK_COVERAGE_BITMAP &&
                mask_kind !=
                    PROGPU_NATIVE_SCENE_LAYER_MASK_ANALYTIC_CHAIN &&
                mask_kind !=
                    PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN &&
                mask_kind != PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH &&
                mask_kind != PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY &&
                mask_kind != PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE &&
                mask_kind != PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_UNSUPPORTED,
                    "The per-draw semantic mask kind is unsupported.");
            }
            if ((mask_kind ==
                    PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN ||
                    mask_kind == PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH ||
                    mask_kind == PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY ||
                    mask_kind == PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE ||
                    mask_kind == PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE) &&
                semantic_generated_masks_budgeted[
                    state.mask_resource_index] == 0U) {
                std::uint64_t texture_multiplier = 1U;
                if (mask_kind == PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE) {
                    progpu_native_scene_layer_composite_mask composite{};
                    std::memcpy(
                        &composite,
                        bytes + mask_resource.payload_offset,
                        std::min<std::size_t>(
                            sizeof(composite),
                            mask_resource.payload_size));
                    texture_multiplier =
                        static_cast<std::uint64_t>(
                            composite.component_count) + 2U +
                        static_cast<std::uint64_t>(
                            composite.picture_mask_count) * 4U;
                } else if (mask_kind ==
                    PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE) {
                    texture_multiplier = 5U;
                }
                const std::uint64_t mask_texture_bytes =
                    static_cast<std::uint64_t>(target_extent.width) *
                        target_extent.height * texture_multiplier;
                if (mask_texture_bytes >
                        PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES -
                            semantic_layer_coverage_texture_bytes) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "The semantic generated-mask textures exceed their bounded aggregate budget.");
                }
                semantic_layer_coverage_texture_bytes += mask_texture_bytes;
                semantic_generated_masks_budgeted[
                    state.mask_resource_index] = 1U;
            }
            semantic_has_state_masks = true;
            const bool mask_chain = mask_kind ==
                PROGPU_NATIVE_SCENE_LAYER_MASK_ANALYTIC_CHAIN;
            if (mask_chain) {
                if (command.kind ==
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN) {
                    semantic_has_text_mask_chains = true;
                } else if (command.kind ==
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
                    semantic_has_image_mask_chains = true;
                } else {
                    semantic_has_vector_mask_chains = true;
                }
            }
            semantic_layer_mask_kind =
                mask_kind ==
                    PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE ||
                mask_kind ==
                    PROGPU_NATIVE_SCENE_LAYER_MASK_ANALYTIC_CHAIN
                ? PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE
                : PROGPU_NATIVE_GROUP_MASK_TEXTURE;
            semantic_has_masked_glyphs |= !mask_chain && command.kind ==
                PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN;
            semantic_has_masked_images |= !mask_chain && command.kind ==
                PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE;
        }
        const auto resource = read_resource(command.resource_index);
        bool valid = false;
        bool budget_valid = true;
        std::uint64_t compiled_vertex_bytes = 0U;
        std::uint64_t compiled_index_bytes = 0U;
        std::uint64_t compiled_texture_bytes = 0U;
        std::uint64_t compiled_coverage_bytes = 0U;
        bool first_semantic_glyph_resource = true;
        switch (command.kind) {
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC: {
                valid = span_is_multiple(
                    resource.payload_size,
                    sizeof(progpu_native_analytic_primitive)) &&
                    resource.auxiliary_size == 0U;
                const std::uint64_t primitive_count = resource.payload_size /
                    sizeof(progpu_native_analytic_primitive);
                valid = valid && primitive_count <=
                    std::numeric_limits<std::uint32_t>::max() / 6U;
                compiled_vertex_bytes = primitive_count * 4U *
                    sizeof(progpu::native::vector_vertex);
                compiled_index_bytes = primitive_count * 6U *
                    sizeof(std::uint32_t);
                for (std::uint64_t primitive_index = 0U;
                     valid && primitive_index < primitive_count;
                     ++primitive_index) {
                    progpu_native_analytic_primitive primitive{};
                    std::memcpy(
                        &primitive,
                        bytes + resource.payload_offset +
                            primitive_index * sizeof(primitive),
                        sizeof(primitive));
                    std::uint32_t brush_index = 0U;
                    const progpu_native_scene_brush* brush = nullptr;
                    valid = resolve_brush(
                        index,
                        command,
                        static_cast<std::uint32_t>(primitive_index),
                        brush_index,
                        brush);
                    if (!valid) {
                        break;
                    }
                    if (brush == nullptr) {
                        apply_semantic_state(primitive, state);
                    } else {
                        apply_semantic_transform(primitive, state);
                        apply_analytic_material(primitive, *brush);
                    }
                    valid = is_valid_semantic_analytic(primitive);
                }
                break;
            }
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY: {
                valid = span_is_multiple(
                        resource.payload_size,
                        sizeof(progpu_native_geometry_primitive)) &&
                    resource.auxiliary_size == 0U;
                const std::uint64_t primitive_count = resource.payload_size /
                    sizeof(progpu_native_geometry_primitive);
                valid = valid && primitive_count <=
                    std::numeric_limits<std::uint32_t>::max();
                for (std::uint64_t primitive_index = 0U;
                     valid && budget_valid &&
                        primitive_index < primitive_count;
                     ++primitive_index) {
                    progpu_native_geometry_primitive primitive{};
                    std::memcpy(
                        &primitive,
                        bytes + resource.payload_offset +
                            primitive_index * sizeof(primitive),
                        sizeof(primitive));
                    std::uint32_t brush_index = 0U;
                    const progpu_native_scene_brush* brush = nullptr;
                    valid = resolve_brush(
                        index,
                        command,
                        static_cast<std::uint32_t>(primitive_index),
                        brush_index,
                        brush);
                    if (!valid) {
                        break;
                    }
                    if (brush == nullptr) {
                        apply_semantic_state(primitive, state);
                    } else {
                        apply_semantic_transform(primitive, state);
                        apply_geometry_material(primitive, *brush);
                    }
                    std::size_t vertex_count = 0U;
                    std::size_t index_count = 0U;
                    valid = progpu::native::geometry_primitive_capacity(
                        primitive,
                        vertex_count,
                        index_count);
                    budget_valid = valid &&
                        vertex_count <=
                            (std::numeric_limits<std::uint64_t>::max() -
                                compiled_vertex_bytes) /
                                sizeof(progpu::native::vector_vertex) &&
                        index_count <=
                            (std::numeric_limits<std::uint64_t>::max() -
                                compiled_index_bytes) /
                                sizeof(std::uint32_t);
                    if (budget_valid) {
                        compiled_vertex_bytes += vertex_count *
                            sizeof(progpu::native::vector_vertex);
                        compiled_index_bytes += index_count *
                            sizeof(std::uint32_t);
                    }
                }
                break;
            }
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_POINT_BATCH: {
                valid = span_is_multiple(
                        resource.payload_size,
                        sizeof(progpu_native_scene_point_batch)) &&
                    span_is_multiple(
                        resource.auxiliary_size,
                        sizeof(progpu_native_point));
                const std::uint64_t batch_count = resource.payload_size /
                    sizeof(progpu_native_scene_point_batch);
                const std::uint64_t point_count = resource.auxiliary_size /
                    sizeof(progpu_native_point);
                valid = valid && batch_count != 0U && point_count != 0U &&
                    batch_count <=
                        PROGPU_NATIVE_SCENE_MAX_DRAW_BRUSH_INDICES &&
                    point_count <=
                        std::numeric_limits<std::uint32_t>::max();
                const auto* points = reinterpret_cast<
                    const progpu_native_point*>(
                        bytes + resource.auxiliary_offset);
                for (std::uint64_t batch_index = 0U;
                     valid && budget_valid && batch_index < batch_count;
                     ++batch_index) {
                    progpu_native_scene_point_batch batch{};
                    std::memcpy(
                        &batch,
                        bytes + resource.payload_offset + batch_index *
                            sizeof(batch),
                        sizeof(batch));
                    std::uint32_t brush_index = 0U;
                    const progpu_native_scene_brush* brush = nullptr;
                    valid = resolve_brush(
                        index,
                        command,
                        static_cast<std::uint32_t>(batch_index),
                        brush_index,
                        brush);
                    if (!valid) {
                        break;
                    }
                    if (brush == nullptr) {
                        apply_semantic_state(batch, state);
                    } else {
                        apply_semantic_transform(batch, state);
                        if (brush->type ==
                            PROGPU_NATIVE_SCENE_BRUSH_SOLID) {
                            batch.color = brush->colors[0];
                        }
                    }
                    std::size_t vertex_count = 0U;
                    std::size_t index_count = 0U;
                    valid = progpu::native::is_valid_point_batch(
                            batch,
                            points,
                            static_cast<std::size_t>(point_count)) &&
                        progpu::native::point_batch_capacity(
                            batch,
                            static_cast<std::size_t>(point_count),
                            vertex_count,
                            index_count);
                    budget_valid = valid &&
                        vertex_count <=
                            (std::numeric_limits<std::uint64_t>::max() -
                                compiled_vertex_bytes) /
                                sizeof(progpu::native::vector_vertex) &&
                        index_count <=
                            (std::numeric_limits<std::uint64_t>::max() -
                                compiled_index_bytes) /
                                sizeof(std::uint32_t);
                    if (budget_valid) {
                        compiled_vertex_bytes += vertex_count *
                            sizeof(progpu::native::vector_vertex);
                        compiled_index_bytes += index_count *
                            sizeof(std::uint32_t);
                    }
                }
                break;
            }
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_VERTEX_MESH: {
                valid = span_is_multiple(
                    resource.payload_size,
                    sizeof(progpu_native_scene_vertex_mesh));
                const std::size_t mesh_count = resource.payload_size /
                    sizeof(progpu_native_scene_vertex_mesh);
                const auto* meshes = reinterpret_cast<
                    const progpu_native_scene_vertex_mesh*>(
                        bytes + resource.payload_offset);
                std::size_t source_vertex_count = 0U;
                std::size_t source_index_count = 0U;
                valid = valid && mesh_count <=
                        PROGPU_NATIVE_SCENE_MAX_DRAW_BRUSH_INDICES &&
                    progpu::native::vertex_mesh_resource_layout(
                        meshes,
                        mesh_count,
                        resource.auxiliary_size,
                        source_vertex_count,
                        source_index_count);
                const auto* source_vertices = reinterpret_cast<
                    const progpu_native_scene_mesh_vertex*>(
                        bytes + resource.auxiliary_offset);
                for (std::size_t mesh_index = 0U;
                     valid && budget_valid && mesh_index < mesh_count;
                     ++mesh_index) {
                    auto mesh = meshes[mesh_index];
                    std::uint32_t brush_index = 0U;
                    const progpu_native_scene_brush* brush = nullptr;
                    valid = resolve_brush(
                        index,
                        command,
                        static_cast<std::uint32_t>(mesh_index),
                        brush_index,
                        brush);
                    if (!valid) {
                        break;
                    }
                    apply_semantic_transform(mesh, state);
                    std::size_t vertex_count = 0U;
                    std::size_t index_count = 0U;
                    valid = progpu::native::is_valid_vertex_mesh(
                            mesh,
                            source_vertices,
                            source_vertex_count,
                            source_index_count) &&
                        progpu::native::vertex_mesh_capacity(
                            mesh,
                            source_vertex_count,
                            source_index_count,
                            vertex_count,
                            index_count);
                    budget_valid = valid &&
                        vertex_count <=
                            (std::numeric_limits<std::uint64_t>::max() -
                                compiled_vertex_bytes) /
                                sizeof(progpu::native::vector_vertex) &&
                        index_count <=
                            (std::numeric_limits<std::uint64_t>::max() -
                                compiled_index_bytes) /
                                sizeof(std::uint32_t);
                    if (budget_valid) {
                        compiled_vertex_bytes += vertex_count *
                            sizeof(progpu::native::vector_vertex);
                        compiled_index_bytes += index_count *
                            sizeof(std::uint32_t);
                    }
                }
                break;
            }
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH: {
                valid = span_is_multiple(
                    resource.payload_size,
                    sizeof(progpu_native_scene_stroke));
                const std::size_t stroke_count = resource.payload_size /
                    sizeof(progpu_native_scene_stroke);
                const auto* strokes = reinterpret_cast<
                    const progpu_native_scene_stroke*>(
                        bytes + resource.payload_offset);
                std::size_t point_count = 0U;
                std::size_t double_count = 0U;
                valid = valid && stroke_count <=
                        PROGPU_NATIVE_SCENE_MAX_DRAW_BRUSH_INDICES &&
                    progpu::native::semantic_stroke_resource_layout(
                        strokes,
                        stroke_count,
                        resource.auxiliary_size,
                        point_count,
                        double_count);
                const auto* points = reinterpret_cast<
                    const progpu_native_point*>(
                        bytes + resource.auxiliary_offset);
                const auto* doubles = reinterpret_cast<const double*>(
                    bytes + resource.auxiliary_offset +
                        point_count * sizeof(progpu_native_point));
                for (std::size_t double_index = 0U;
                     valid && double_index < double_count;
                     ++double_index) {
                    valid = std::isfinite(doubles[double_index]);
                }
                for (std::size_t stroke_index = 0U;
                     valid && budget_valid && stroke_index < stroke_count;
                     ++stroke_index) {
                    auto stroke = strokes[stroke_index];
                    std::uint32_t brush_index = 0U;
                    const progpu_native_scene_brush* brush = nullptr;
                    valid = resolve_brush(
                        index,
                        command,
                        static_cast<std::uint32_t>(stroke_index),
                        brush_index,
                        brush);
                    if (!valid) {
                        break;
                    }
                    apply_semantic_transform(stroke, state);
                    if (brush == nullptr) {
                        stroke.color.a *= state.opacity;
                    } else if (brush->type ==
                        PROGPU_NATIVE_SCENE_BRUSH_SOLID) {
                        stroke.color = brush->colors[0];
                    }
                    std::size_t vertex_count = 0U;
                    std::size_t index_count = 0U;
                    valid = progpu::native::semantic_stroke_capacity(
                        stroke,
                        points + stroke.point_offset,
                        doubles,
                        double_count,
                        engine->spline_sampled_points,
                        engine->spline_work,
                        vertex_count,
                        index_count);
                    budget_valid = valid &&
                        vertex_count <=
                            (std::numeric_limits<std::uint64_t>::max() -
                                compiled_vertex_bytes) /
                                sizeof(progpu::native::vector_vertex) &&
                        index_count <=
                            (std::numeric_limits<std::uint64_t>::max() -
                                compiled_index_bytes) /
                                sizeof(std::uint32_t);
                    if (budget_valid) {
                        compiled_vertex_bytes += vertex_count *
                            sizeof(progpu::native::vector_vertex);
                        compiled_index_bytes += index_count *
                            sizeof(std::uint32_t);
                    }
                }
                break;
            }
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH: {
                std::uint64_t path_count = 0U;
                std::uint64_t segment_count = 0U;
                std::uint64_t boolean_node_count = 0U;
                valid = try_get_path_resource_counts(
                    resource,
                    path_count,
                    segment_count,
                    boolean_node_count);
                valid = valid && path_count <= (1U << 20U) &&
                    segment_count <= (1U << 24U) &&
                    boolean_node_count <= (1U << 22U) &&
                    path_count <=
                        std::numeric_limits<std::uint32_t>::max() / 6U;
                const auto* boolean_nodes = reinterpret_cast<const
                    progpu_native_scene_path_boolean_node*>(
                        bytes + resource.auxiliary_offset +
                            segment_count *
                                sizeof(progpu_native_path_segment));
                compiled_vertex_bytes = path_count * 4U *
                    sizeof(progpu::native::vector_vertex);
                compiled_index_bytes = path_count * 6U *
                    sizeof(std::uint32_t);
                for (std::uint64_t segment_index = 0U;
                     valid && segment_index < segment_count;
                     ++segment_index) {
                    progpu_native_path_segment segment{};
                    std::memcpy(
                        &segment,
                        bytes + resource.auxiliary_offset +
                            segment_index * sizeof(segment),
                        sizeof(segment));
                    valid = is_valid_semantic_segment(segment, true);
                }
                for (std::uint64_t path_index = 0U;
                     valid && budget_valid && path_index < path_count;
                     ++path_index) {
                    progpu_native_scene_path_fill path{};
                    std::memcpy(
                        &path,
                        bytes + resource.payload_offset +
                            path_index * sizeof(path),
                        sizeof(path));
                    std::uint32_t brush_index = 0U;
                    const progpu_native_scene_brush* brush = nullptr;
                    valid = resolve_brush(
                        index,
                        command,
                        static_cast<std::uint32_t>(path_index),
                        brush_index,
                        brush);
                    if (!valid) {
                        break;
                    }
                    if (brush == nullptr) {
                        apply_semantic_state(path, state);
                    } else {
                        apply_semantic_transform(path, state);
                        apply_path_material(path, *brush);
                    }
                    std::uint64_t path_coverage_bytes = 0U;
                    valid = is_valid_semantic_path(
                        path,
                        segment_count,
                        boolean_nodes,
                        boolean_node_count,
                        &path_coverage_bytes);
                    budget_valid = valid &&
                        path_coverage_bytes <=
                            semantic_max_coverage_bytes -
                                compiled_coverage_bytes;
                    if (budget_valid) {
                        compiled_coverage_bytes += path_coverage_bytes;
                    }
                }
                break;
            }
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN: {
                std::uint32_t glyph_payload_offset = 0U;
                std::uint32_t glyph_count32 = 0U;
                const bool color_glyphs = is_color_glyph_resource(resource);
                valid = try_get_glyph_payload(
                        bytes,
                        command,
                        glyph_payload_offset,
                        glyph_count32);
                const std::uint64_t outline_count = color_glyphs
                    ? resource.payload_size /
                        sizeof(progpu_native_scene_color_glyph_bitmap)
                    : resource.payload_size /
                        sizeof(progpu_native_scene_glyph_outline);
                const std::uint64_t segment_count = color_glyphs
                    ? 0U
                    : resource.auxiliary_size /
                        sizeof(progpu_native_path_segment);
                valid = valid && (color_glyphs
                    ? span_is_multiple(
                        resource.payload_size,
                        sizeof(progpu_native_scene_color_glyph_bitmap))
                    : span_is_multiple(
                            resource.payload_size,
                            sizeof(progpu_native_scene_glyph_outline)) &&
                        span_is_multiple(
                            resource.auxiliary_size,
                            sizeof(progpu_native_path_segment)));
                const std::uint64_t glyph_count = glyph_count32;
                first_semantic_glyph_resource =
                    semantic_glyph_resources_budgeted[
                        command.resource_index] == 0U;
                valid = valid && outline_count <= (1U << 20U) &&
                    segment_count <= (1U << 24U) &&
                    glyph_count <= (1U << 24U);
                compiled_vertex_bytes = glyph_count *
                    sizeof(gpu_glyph_instance);
                compiled_texture_bytes = color_glyphs &&
                        first_semantic_glyph_resource
                    ? resource.auxiliary_size
                    : 0U;
                // The snapshot owns one immutable outline/segment payload per
                // resource index. Validate and budget that payload on its
                // first draw; later commands still validate their independent
                // positioned-glyph payload below.
                for (std::uint64_t segment_index = 0U;
                     valid && first_semantic_glyph_resource &&
                         segment_index < segment_count;
                     ++segment_index) {
                    progpu_native_path_segment segment{};
                    std::memcpy(
                        &segment,
                        bytes + resource.auxiliary_offset +
                            segment_index * sizeof(segment),
                        sizeof(segment));
                    valid = is_valid_semantic_segment(segment, false);
                }
                for (std::uint64_t outline_index = 0U;
                     valid && budget_valid && first_semantic_glyph_resource &&
                         outline_index < outline_count;
                     ++outline_index) {
                    if (color_glyphs) {
                        continue;
                    }
                    progpu_native_scene_glyph_outline outline{};
                    std::memcpy(
                        &outline,
                        bytes + resource.payload_offset +
                            outline_index * sizeof(outline),
                        sizeof(outline));
                    std::uint64_t outline_coverage_bytes = 0U;
                    valid = is_valid_semantic_glyph_outline(
                        outline,
                        segment_count,
                        &outline_coverage_bytes);
                    budget_valid = valid &&
                        outline_coverage_bytes <=
                            semantic_max_coverage_bytes -
                                compiled_coverage_bytes;
                    if (budget_valid) {
                        compiled_coverage_bytes += outline_coverage_bytes;
                    }
                }
                for (std::uint64_t glyph_index = 0U;
                     valid && glyph_index < glyph_count;
                     ++glyph_index) {
                    progpu_native_positioned_glyph glyph{};
                    std::memcpy(
                        &glyph,
                        bytes + glyph_payload_offset +
                            glyph_index * sizeof(glyph),
                        sizeof(glyph));
                    if ((command.flags &
                            PROGPU_NATIVE_SCENE_GLYPH_STYLED) != 0U) {
                        apply_semantic_transform(glyph, state);
                    } else {
                        apply_semantic_state(glyph, state);
                    }
                    valid = is_valid_semantic_positioned_glyph(
                        glyph,
                        outline_count);
                }
                break;
            }
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE: {
                if (command.payload_size <
                    sizeof(progpu_native_scene_image_draw)) {
                    break;
                }
                progpu_native_scene_image_draw image{};
                std::memcpy(
                    &image,
                    bytes + command.payload_offset,
                    sizeof(image));
                apply_semantic_state(image, state);
                semantic_image_options image_options{};
                const bool external_image =
                    (resource.flags & PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE) != 0U;
                const std::uint64_t validation_bytes = external_image
                    ? static_cast<std::uint64_t>(image.row_bytes) *
                            (image.image_height - 1U) +
                        static_cast<std::uint64_t>(image.image_width) * 4U
                    : resource.payload_size;
                const auto* external_binding = external_image
                    ? engine->find_semantic_external_image_binding(
                        resource.resource_id,
                        resource.generation)
                    : nullptr;
                const auto* chroma_binding = external_image
                    ? engine->find_semantic_external_image_binding(
                        resource.resource_id,
                        resource.generation,
                        PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE_CHROMA)
                    : nullptr;
                const auto* effect_mask_binding = external_image
                    ? engine->find_semantic_external_image_binding(
                        resource.resource_id,
                        resource.generation,
                        PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE_MASK)
                    : nullptr;
                valid = resource.auxiliary_size == 0U &&
                    validate_image_draw_payload(
                        bytes,
                        command,
                        image,
                        validation_bytes,
                        image_options) &&
                    (!external_image ||
                        (external_binding != nullptr &&
                            external_binding->width == image.image_width &&
                            external_binding->height == image.image_height)) &&
                    (!image_options.has_effect || (
                        (image_options.effect.flags0[0] <= 0.5F ||
                            chroma_binding != nullptr) &&
                        (image_options.effect.flags0[1] <= 0.5F ||
                            effect_mask_binding != nullptr)));
                semantic_has_image_color_matrices |=
                    valid && image_options.has_color_matrix;
                const std::uint64_t image_vertex_count =
                    image_options.patch_count == 0U
                    ? 4U
                    : static_cast<std::uint64_t>(image_options.patch_count) *
                        6U;
                compiled_vertex_bytes = image_vertex_count *
                    sizeof(progpu::native::vector_vertex);
                compiled_index_bytes = image_options.patch_count == 0U
                    ? 6U * sizeof(std::uint32_t)
                    : 0U;
                compiled_texture_bytes = external_image
                    ? 0U
                    : resource.payload_size;
                if (valid) {
                    semantic_image_vertex_count += image_vertex_count;
                }
                break;
            }
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_LINE_3D_BATCH: {
                valid = resource.kind ==
                        PROGPU_NATIVE_SCENE_RESOURCE_LINE_3D_BATCH &&
                    resource.payload_size != 0U &&
                    span_is_multiple(resource.payload_size,
                        sizeof(progpu_native_scene_line_3d)) &&
                    command.payload_size ==
                        sizeof(progpu_native_scene_camera_3d);
                compiled_vertex_bytes = resource.payload_size +
                    sizeof(progpu::native::three_d::camera_record);
                break;
            }
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH: {
                valid = resource.kind ==
                        PROGPU_NATIVE_SCENE_RESOURCE_MESH_3D_BATCH &&
                    resource.payload_size != 0U &&
                    resource.auxiliary_size != 0U &&
                    span_is_multiple(resource.payload_size,
                        sizeof(progpu_native_scene_mesh_3d)) &&
                    command.payload_size ==
                        sizeof(progpu_native_scene_camera_3d);
                compiled_vertex_bytes = resource.payload_size +
                    resource.auxiliary_size +
                    sizeof(progpu::native::three_d::camera_record);
                break;
            }
            default:
                break;
        }
        if (!valid) {
            const char* family = command.kind ==
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC
                ? "analytic"
                : command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH
                    ? "path"
                    : command.kind ==
                            PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN
                        ? "glyph"
                        : command.kind ==
                                PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE
                            ? "image"
                            : command.kind ==
                                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_POINT_BATCH
                                ? "point-batch"
                            : command.kind ==
                                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_VERTEX_MESH
                                ? "vertex-mesh"
                            : command.kind ==
                                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH
                                ? "stroke-batch"
                            : command.kind ==
                                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_LINE_3D_BATCH
                                ? "line-3d-batch"
                            : command.kind ==
                                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH
                                ? "mesh-3d-batch"
                                : "geometry";
            const std::string detail = std::string("A typed semantic ") +
                family + " scene resource payload is invalid.";
            return engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                detail.c_str());
        }
        if (!budget_valid || !compilation_budget.add(
                compiled_vertex_bytes,
                compiled_index_bytes,
                compiled_texture_bytes,
                compiled_coverage_bytes)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic scene exceeds the bounded aggregate compilation budget.");
        }
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN &&
            first_semantic_glyph_resource) {
            semantic_glyph_resources_budgeted[
                command.resource_index] = 1U;
        }
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
            command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY ||
            command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_POINT_BATCH ||
            command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_VERTEX_MESH ||
            command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH) {
            ++semantic_analytic_draw_count;
            semantic_analytic_vertex_bytes += compiled_vertex_bytes;
            semantic_analytic_index_bytes += compiled_index_bytes;
        } else if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH) {
            std::uint64_t path_count = 0U;
            std::uint64_t segment_count = 0U;
            std::uint64_t boolean_node_count = 0U;
            if (!try_get_path_resource_counts(
                    resource,
                    path_count,
                    segment_count,
                    boolean_node_count)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "A validated semantic path resource could not be decoded.");
            }
            ++semantic_path_draw_count;
            semantic_path_count += path_count;
            semantic_path_segment_count += segment_count;
            semantic_path_boolean_node_count += boolean_node_count;
        } else if (command.kind ==
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN) {
            std::uint32_t glyph_payload_offset = 0U;
            std::uint32_t glyph_count = 0U;
            if (!try_get_glyph_payload(
                    bytes,
                    command,
                    glyph_payload_offset,
                    glyph_count)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A semantic glyph payload prefix is invalid.");
            }
            ++semantic_glyph_draw_count;
            if (first_semantic_glyph_resource &&
                is_color_glyph_resource(resource)) {
                semantic_color_glyph_bitmap_count += resource.payload_size /
                    sizeof(progpu_native_scene_color_glyph_bitmap);
                semantic_color_glyph_pixel_bytes += resource.auxiliary_size;
            } else if (first_semantic_glyph_resource) {
                semantic_glyph_outline_count += resource.payload_size /
                    sizeof(progpu_native_scene_glyph_outline);
                semantic_glyph_segment_count += resource.auxiliary_size /
                    sizeof(progpu_native_path_segment);
            }
            semantic_glyph_count += glyph_count;
            semantic_has_styled_glyphs =
                semantic_has_styled_glyphs ||
                (command.flags & PROGPU_NATIVE_SCENE_GLYPH_STYLED) != 0U;
        } else if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            ++semantic_image_draw_count;
        } else if (command.kind ==
                PROGPU_NATIVE_SCENE_COMMAND_DRAW_LINE_3D_BATCH ||
            command.kind ==
                PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH) {
            ++semantic_3d_draw_count;
        }
        ++semantic_draw_count;
    }

    const std::uint64_t semantic_effect_uniform_bytes =
        static_cast<std::uint64_t>(semantic_effect_pass_count) *
            semantic_effect_uniform_alignment;
    const std::uint64_t pooled_layer_bytes = layer_budget.pooled_bytes();
    const std::uint64_t pooled_effect_bytes =
        layer_budget.pooled_effect_bytes();
    const auto texture_bytes = [](std::uint32_t width,
                                  std::uint32_t height,
                                  std::uint64_t texture_count,
                                  std::uint64_t& result) noexcept {
        const std::uint64_t pixels =
            static_cast<std::uint64_t>(width) * height;
        if (pixels > std::numeric_limits<std::uint64_t>::max() / 4U ||
            pixels * 4U >
                std::numeric_limits<std::uint64_t>::max() / texture_count) {
            return false;
        }
        result = pixels * 4U * texture_count;
        return true;
    };
    const bool semantic_destination_sampling_active =
        semantic_advanced_layer_count != 0U ||
        semantic_backdrop_layer_count != 0U;
    const bool semantic_partial_replay_supported =
        !semantic_has_materialized_layers && !semantic_has_state_masks &&
        !semantic_destination_sampling_active && semantic_3d_draw_count == 0U;
    const bool semantic_partial_damage_active =
        damage_requested && semantic_partial_replay_supported;
    const bool semantic_preserve_target_active =
        preserve_requested && semantic_partial_replay_supported &&
        (!damage_requested || semantic_partial_damage_active);
    semantic_scissor semantic_frame_damage{
        0U, 0U, frame->width, frame->height, true};
    if (semantic_partial_damage_active) {
        const auto left = std::max(
            0.0,
            std::floor(
                static_cast<double>(frame->damage_x) * frame->dpi_scale));
        const auto top = std::max(
            0.0,
            std::floor(
                static_cast<double>(frame->damage_y) * frame->dpi_scale));
        const auto right = std::min(
            static_cast<double>(frame->width),
            std::ceil(
                static_cast<double>(frame->damage_x + frame->damage_width) *
                frame->dpi_scale));
        const auto bottom = std::min(
            static_cast<double>(frame->height),
            std::ceil(
                static_cast<double>(frame->damage_y + frame->damage_height) *
                frame->dpi_scale));
        semantic_frame_damage = {
            static_cast<std::uint32_t>(left),
            static_cast<std::uint32_t>(top),
            static_cast<std::uint32_t>(right - left),
            static_cast<std::uint32_t>(bottom - top),
            right > left && bottom > top};
    }
    std::uint64_t semantic_destination_frame_bytes = 0U;
    std::uint64_t semantic_advanced_source_bytes = 0U;
    const std::uint64_t destination_frame_texture_count =
        semantic_advanced_layer_count != 0U
            ? 2U
            : semantic_destination_sampling_active ? 1U : 0U;
    const bool invalid_destination_pool =
        (destination_frame_texture_count != 0U &&
            !texture_bytes(
                frame->width,
                frame->height,
                destination_frame_texture_count,
                semantic_destination_frame_bytes)) ||
        (semantic_advanced_layer_count != 0U &&
            !texture_bytes(
                std::max(semantic_advanced_source_width, 1U),
                std::max(semantic_advanced_source_height, 1U),
                1U,
                semantic_advanced_source_bytes)) ||
        semantic_destination_frame_bytes >
            PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES ||
        semantic_advanced_source_bytes >
            PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES -
                semantic_destination_frame_bytes;
    const std::uint64_t semantic_destination_texture_bytes =
        invalid_destination_pool
            ? std::numeric_limits<std::uint64_t>::max()
            : semantic_destination_frame_bytes +
                semantic_advanced_source_bytes;
    const bool invalid_layer_pool =
        pooled_layer_bytes > PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES ||
        pooled_effect_bytes >
            PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES - pooled_layer_bytes ||
        invalid_destination_pool;
    const std::uint64_t retained_layer_base = invalid_layer_pool
        ? std::numeric_limits<std::uint64_t>::max()
        : pooled_layer_bytes + pooled_effect_bytes +
            semantic_destination_texture_bytes;
    const bool invalid_mask_pool = invalid_layer_pool ||
        semantic_layer_coverage_texture_bytes >
            PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES - retained_layer_base;
    const std::uint64_t retained_layer_bytes = invalid_mask_pool
        ? std::numeric_limits<std::uint64_t>::max()
        : retained_layer_base +
            semantic_layer_coverage_texture_bytes;
    const std::uint64_t semantic_brush_bytes =
        semantic_brush_page.brushes.size() *
            sizeof(progpu_native_scene_brush);
    const std::uint64_t semantic_gradient_stop_bytes =
        semantic_brush_page.gradient_stops.size() *
            sizeof(progpu_native_scene_gradient_stop);
    const std::uint64_t semantic_text_style_bytes =
        semantic_text_style_page.styles.size() *
            sizeof(progpu_native_scene_text_style);
    const std::uint64_t semantic_material_bytes =
        semantic_brush_bytes + semantic_gradient_stop_bytes +
            semantic_text_style_bytes;
    const std::uint64_t compiled_payload_bytes =
        compilation_budget.total_bytes();
    const bool invalid_compiled_materials =
        semantic_material_bytes > semantic_max_total_compiled_bytes ||
        compiled_payload_bytes >
            semantic_max_total_compiled_bytes - semantic_material_bytes;
    const std::uint64_t compiled_bytes = invalid_compiled_materials
        ? std::numeric_limits<std::uint64_t>::max()
        : compiled_payload_bytes + semantic_material_bytes;
    if (invalid_compiled_materials || invalid_layer_pool ||
        semantic_effect_uniform_bytes >
            semantic_max_total_compiled_bytes - compiled_bytes ||
        std::max(layer_budget.peak_bytes, retained_layer_bytes) >
            semantic_max_total_compiled_bytes - compiled_bytes -
                semantic_effect_uniform_bytes) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The semantic scene exceeds the combined layer, effect, and compiled-payload budget.");
    }

    if (semantic_path_count > (1U << 20U) ||
        semantic_path_segment_count > (1U << 24U) ||
        semantic_path_boolean_node_count > (1U << 22U) ||
        semantic_path_count >
            std::numeric_limits<std::uint32_t>::max() / 6U) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The aggregate semantic path page exceeds the native safety bound.");
    }
    if (semantic_glyph_outline_count > (1U << 20U) ||
        semantic_glyph_segment_count > (1U << 24U) ||
        semantic_glyph_count > (1U << 24U) ||
        semantic_glyph_count >
            std::numeric_limits<std::uint32_t>::max()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The aggregate semantic glyph page exceeds the native safety bound.");
    }
    if (semantic_color_glyph_bitmap_count > (1U << 20U) ||
        semantic_color_glyph_pixel_bytes >
            PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The aggregate semantic color-glyph page exceeds the native safety bound.");
    }

    std::uint64_t semantic_brush_upload_bytes = 0U;
    std::uint64_t semantic_gradient_stop_upload_bytes = 0U;
    std::uint64_t semantic_text_style_upload_bytes = 0U;
    std::uint64_t semantic_color_glyph_upload_bytes = 0U;
    if (semantic_analytic_draw_count != 0U ||
        semantic_path_draw_count != 0U) {
        if (engine->analytic_pipeline == nullptr &&
            !create_analytic_pipeline(*engine)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic retained-brush pipeline could not be created.");
        }
        if (semantic_has_state_masks &&
            !create_analytic_masked_pipeline(*engine)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The per-draw semantic mask pipeline could not be created.");
        }
        if (!ensure_analytic_material_buffers(
                *engine,
                semantic_brush_bytes,
                semantic_gradient_stop_bytes)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic retained-brush GPU page could not be allocated.");
        }
        if (engine->analytic_material_owner_hash !=
                engine->semantic_hashes.brush ||
            engine->analytic_material_owner_hash == 0U) {
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->analytic_brush_buffer,
                0U,
                semantic_brush_page.brushes.data(),
                semantic_brush_bytes);
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->analytic_gradient_buffer,
                0U,
                semantic_brush_page.gradient_stops.data(),
                semantic_gradient_stop_bytes);
            engine->analytic_material_owner_hash =
                engine->semantic_hashes.brush;
            semantic_brush_upload_bytes = semantic_brush_bytes;
            semantic_gradient_stop_upload_bytes =
                semantic_gradient_stop_bytes;
        }
    }
    if (semantic_has_styled_glyphs) {
        if (!create_glyph_resources(*engine)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic retained text-style pipeline could not be created.");
        }
        if (!ensure_text_style_buffer(
                *engine,
                semantic_text_style_bytes)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic retained text-style GPU page could not be allocated.");
        }
        if (engine->text_style_owner_hash !=
                engine->semantic_hashes.text_style ||
            engine->text_style_owner_hash == 0U) {
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->text_style_buffer,
                0U,
                semantic_text_style_page.styles.data(),
                semantic_text_style_bytes);
            engine->text_style_owner_hash =
                engine->semantic_hashes.text_style;
            semantic_text_style_upload_bytes = semantic_text_style_bytes;
        }
    }

    auto semantic_bundle_replay_hash = engine->semantic_scene_hash;
    semantic_bundle_replay_hash = append_fnv1a64(
        semantic_bundle_replay_hash,
        &semantic_partial_damage_active,
        sizeof(semantic_partial_damage_active));
    if (semantic_partial_damage_active) {
        const std::array damage_identity{
            semantic_frame_damage.x,
            semantic_frame_damage.y,
            semantic_frame_damage.width,
            semantic_frame_damage.height};
        semantic_bundle_replay_hash = append_fnv1a64(
            semantic_bundle_replay_hash,
            damage_identity.data(),
            sizeof(damage_identity));
    }
    const bool semantic_render_bundle_hit =
        engine->semantic_render_bundle_valid &&
        engine->semantic_render_bundle_scene_hash ==
            semantic_bundle_replay_hash &&
        engine->semantic_render_bundle_dpi_scale == frame->dpi_scale &&
        engine->semantic_render_bundle_width == frame->width &&
        engine->semantic_render_bundle_height == frame->height &&
        (semantic_path_draw_count == 0U ||
            engine->semantic_path_gpu_scene_hash ==
                engine->semantic_hashes.path) &&
        (semantic_glyph_draw_count == 0U ||
            engine->semantic_glyph_gpu_scene_hash ==
                engine->semantic_hashes.glyph);
    if (!semantic_render_bundle_hit) {
        engine->release_semantic_render_bundle();
    }

    std::uint64_t semantic_analytic_vertex_upload_bytes = 0U;
    std::uint64_t semantic_analytic_index_upload_bytes = 0U;
    auto& semantic_analytic_page = engine->semantic_analytic_cache;
    const bool semantic_analytic_page_hit =
        semantic_analytic_draw_count != 0U &&
        semantic_analytic_page.cache_valid &&
        semantic_analytic_page.scene_hash ==
            engine->semantic_hashes.analytic &&
        semantic_analytic_page.dpi_scale == frame->dpi_scale &&
        semantic_analytic_page.target_width == frame->width &&
        semantic_analytic_page.target_height == frame->height &&
        semantic_analytic_page.draws.size() ==
            semantic_analytic_draw_count;
    if (semantic_analytic_draw_count != 0U &&
        !semantic_analytic_page_hit) {
        std::vector<semantic_analytic_draw> compiled_draws;
        try {
            compiled_draws.reserve(semantic_analytic_draw_count);
            engine->vertices.clear();
            engine->indices.clear();
            engine->vertices.reserve(static_cast<std::size_t>(
                semantic_analytic_vertex_bytes /
                    sizeof(progpu::native::vector_vertex)));
            engine->indices.reserve(static_cast<std::size_t>(
                semantic_analytic_index_bytes /
                    sizeof(std::uint32_t)));
            engine->geometry_cache_valid = false;
            engine->geometry_gpu_cache_valid = false;

            semantic_state_cursor state_cursor(
                bytes, header, frame->dpi_scale);
            semantic_layer_target_cursor target_cursor(
                bytes,
                frame->width,
                frame->height,
                frame->dpi_scale);
            for (std::uint32_t index = 0U;
                 index < header.command_count;
                 ++index) {
                const auto command = read_command(index);
                const auto target_extent = target_cursor.advance(command);
                const auto state = localize_semantic_state(
                    state_cursor.advance(command),
                    target_extent,
                    frame->dpi_scale);
                if (command.kind !=
                        PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC &&
                    command.kind !=
                        PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY &&
                    command.kind !=
                        PROGPU_NATIVE_SCENE_COMMAND_DRAW_POINT_BATCH &&
                    command.kind !=
                        PROGPU_NATIVE_SCENE_COMMAND_DRAW_VERTEX_MESH &&
                    command.kind !=
                        PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH) {
                    continue;
                }
                const auto resource = read_resource(command.resource_index);
                const std::size_t vertex_start = engine->vertices.size();
                const std::size_t index_start = engine->indices.size();
                if (command.kind ==
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC) {
                    const std::size_t primitive_count = resource.payload_size /
                        sizeof(progpu_native_analytic_primitive);
                    for (std::size_t primitive_index = 0U;
                         primitive_index < primitive_count;
                         ++primitive_index) {
                        progpu_native_analytic_primitive primitive{};
                        std::memcpy(
                            &primitive,
                            bytes + resource.payload_offset +
                                primitive_index * sizeof(primitive),
                            sizeof(primitive));
                        std::uint32_t brush_index = 0U;
                        const progpu_native_scene_brush* brush = nullptr;
                        if (!resolve_brush(
                                index,
                                command,
                                static_cast<std::uint32_t>(primitive_index),
                                brush_index,
                                brush)) {
                            return engine->fail(
                                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                                "A validated semantic analytic brush map could not be resolved.");
                        }
                        if (brush == nullptr) {
                            apply_semantic_state(primitive, state);
                        } else {
                            apply_semantic_transform(primitive, state);
                            apply_analytic_material(primitive, *brush);
                        }
                        float minimum_scale = 0.0F;
                        if (!progpu::native::try_get_minimum_scale(
                                primitive.transform,
                                minimum_scale) ||
                            !progpu::native::append_analytic_primitive(
                                primitive,
                                antialias_padding_pixels / minimum_scale,
                                engine->vertices,
                                engine->indices,
                                static_cast<float>(brush_index))) {
                            return engine->fail(
                                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                                "A preflighted semantic analytic payload could not be compiled.");
                        }
                    }
                } else if (command.kind ==
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY) {
                    const std::size_t primitive_count = resource.payload_size /
                        sizeof(progpu_native_geometry_primitive);
                    for (std::size_t primitive_index = 0U;
                         primitive_index < primitive_count;
                         ++primitive_index) {
                        progpu_native_geometry_primitive primitive{};
                        std::memcpy(
                            &primitive,
                            bytes + resource.payload_offset +
                                primitive_index * sizeof(primitive),
                            sizeof(primitive));
                        std::uint32_t brush_index = 0U;
                        const progpu_native_scene_brush* brush = nullptr;
                        if (!resolve_brush(
                                index,
                                command,
                                static_cast<std::uint32_t>(primitive_index),
                                brush_index,
                                brush)) {
                            return engine->fail(
                                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                                "A validated semantic geometry brush map could not be resolved.");
                        }
                        if (brush == nullptr) {
                            apply_semantic_state(primitive, state);
                        } else {
                            apply_semantic_transform(primitive, state);
                            apply_geometry_material(primitive, *brush);
                        }
                        if (!progpu::native::append_geometry_primitive(
                                primitive,
                                static_cast<float>(brush_index),
                                engine->vertices,
                                engine->indices)) {
                            return engine->fail(
                                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                                "A preflighted semantic geometry payload could not be compiled.");
                        }
                    }
                } else if (command.kind ==
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_POINT_BATCH) {
                    const std::size_t batch_count = resource.payload_size /
                        sizeof(progpu_native_scene_point_batch);
                    const std::size_t point_count = resource.auxiliary_size /
                        sizeof(progpu_native_point);
                    const auto* points = reinterpret_cast<
                        const progpu_native_point*>(
                            bytes + resource.auxiliary_offset);
                    for (std::size_t batch_index = 0U;
                         batch_index < batch_count;
                         ++batch_index) {
                        progpu_native_scene_point_batch batch{};
                        std::memcpy(
                            &batch,
                            bytes + resource.payload_offset + batch_index *
                                sizeof(batch),
                            sizeof(batch));
                        std::uint32_t brush_index = 0U;
                        const progpu_native_scene_brush* brush = nullptr;
                        if (!resolve_brush(
                                index,
                                command,
                                static_cast<std::uint32_t>(batch_index),
                                brush_index,
                                brush)) {
                            return engine->fail(
                                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                                "A validated semantic point-batch brush map could not be resolved.");
                        }
                        if (brush == nullptr) {
                            apply_semantic_state(batch, state);
                        } else {
                            apply_semantic_transform(batch, state);
                            if (brush->type ==
                                PROGPU_NATIVE_SCENE_BRUSH_SOLID) {
                                batch.color = brush->colors[0];
                            }
                        }
                        const bool local_brush_coordinates = brush != nullptr &&
                            brush->type != PROGPU_NATIVE_SCENE_BRUSH_SOLID;
                        if (!progpu::native::append_point_batch(
                                batch,
                                points,
                                point_count,
                                static_cast<float>(brush_index),
                                local_brush_coordinates,
                                engine->vertices,
                                engine->indices)) {
                            return engine->fail(
                                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                                "A preflighted semantic point-batch payload could not be compiled.");
                        }
                    }
                } else if (command.kind ==
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_VERTEX_MESH) {
                    const std::size_t mesh_count = resource.payload_size /
                        sizeof(progpu_native_scene_vertex_mesh);
                    const auto* meshes = reinterpret_cast<
                        const progpu_native_scene_vertex_mesh*>(
                            bytes + resource.payload_offset);
                    std::size_t source_vertex_count = 0U;
                    std::size_t source_index_count = 0U;
                    if (!progpu::native::vertex_mesh_resource_layout(
                            meshes,
                            mesh_count,
                            resource.auxiliary_size,
                            source_vertex_count,
                            source_index_count)) {
                        return engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "A preflighted semantic vertex-mesh layout could not be resolved.");
                    }
                    const auto* source_vertices = reinterpret_cast<
                        const progpu_native_scene_mesh_vertex*>(
                            bytes + resource.auxiliary_offset);
                    const auto* source_indices = reinterpret_cast<
                        const std::uint16_t*>(
                            bytes + resource.auxiliary_offset +
                            source_vertex_count *
                                sizeof(progpu_native_scene_mesh_vertex));
                    for (std::size_t mesh_index = 0U;
                         mesh_index < mesh_count;
                         ++mesh_index) {
                        auto mesh = meshes[mesh_index];
                        std::uint32_t brush_index = 0U;
                        const progpu_native_scene_brush* brush = nullptr;
                        if (!resolve_brush(
                                index,
                                command,
                                static_cast<std::uint32_t>(mesh_index),
                                brush_index,
                                brush)) {
                            return engine->fail(
                                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                                "A validated semantic vertex-mesh brush map could not be resolved.");
                        }
                        apply_semantic_transform(mesh, state);
                        if (!progpu::native::append_vertex_mesh(
                                mesh,
                                source_vertices,
                                source_vertex_count,
                                source_indices,
                                source_index_count,
                                state.opacity,
                                static_cast<float>(brush_index),
                                engine->vertices,
                                engine->indices)) {
                            return engine->fail(
                                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                                "A preflighted semantic vertex-mesh payload could not be compiled.");
                        }
                    }
                } else {
                    const std::size_t stroke_count = resource.payload_size /
                        sizeof(progpu_native_scene_stroke);
                    const auto* strokes = reinterpret_cast<
                        const progpu_native_scene_stroke*>(
                            bytes + resource.payload_offset);
                    std::size_t point_count = 0U;
                    std::size_t double_count = 0U;
                    if (!progpu::native::semantic_stroke_resource_layout(
                            strokes,
                            stroke_count,
                            resource.auxiliary_size,
                            point_count,
                            double_count)) {
                        return engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "A preflighted semantic stroke-batch layout could not be resolved.");
                    }
                    const auto* points = reinterpret_cast<
                        const progpu_native_point*>(
                            bytes + resource.auxiliary_offset);
                    const auto* doubles = reinterpret_cast<const double*>(
                        bytes + resource.auxiliary_offset +
                            point_count * sizeof(progpu_native_point));
                    for (std::size_t stroke_index = 0U;
                         stroke_index < stroke_count;
                         ++stroke_index) {
                        auto stroke = strokes[stroke_index];
                        std::uint32_t brush_index = 0U;
                        const progpu_native_scene_brush* brush = nullptr;
                        if (!resolve_brush(
                                index,
                                command,
                                static_cast<std::uint32_t>(stroke_index),
                                brush_index,
                                brush)) {
                            return engine->fail(
                                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                                "A validated semantic stroke-batch brush map could not be resolved.");
                        }
                        apply_semantic_transform(stroke, state);
                        if (brush == nullptr) {
                            stroke.color.a *= state.opacity;
                        } else if (brush->type ==
                            PROGPU_NATIVE_SCENE_BRUSH_SOLID) {
                            stroke.color = brush->colors[0];
                        }
                        if (!progpu::native::append_semantic_stroke(
                                stroke,
                                points + stroke.point_offset,
                                doubles,
                                double_count,
                                static_cast<float>(brush_index),
                                engine->spline_sampled_points,
                                engine->spline_work,
                                engine->vertices,
                                engine->indices)) {
                            return engine->fail(
                                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                                "A preflighted semantic stroke-batch payload could not be compiled.");
                        }
                    }
                }
                const std::size_t vertex_count =
                    engine->vertices.size() - vertex_start;
                const std::size_t index_count =
                    engine->indices.size() - index_start;
                if (vertex_count >
                        std::numeric_limits<std::uint32_t>::max() ||
                    index_count >
                        std::numeric_limits<std::uint32_t>::max()) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "A semantic analytic packed draw exceeds WebGPU index limits.");
                }
                compiled_draws.push_back({
                    vertex_start *
                        sizeof(progpu::native::vector_vertex),
                    index_start * sizeof(std::uint32_t),
                    static_cast<std::uint32_t>(vertex_count),
                    static_cast<std::uint32_t>(index_count)});
            }
        } catch (const std::bad_alloc&) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic analytic packed page could not be compiled.");
        }

        const std::uint64_t compiled_vertex_bytes =
            engine->vertices.size() *
                sizeof(progpu::native::vector_vertex);
        const std::uint64_t compiled_index_bytes =
            engine->indices.size() * sizeof(std::uint32_t);
        if (compiled_draws.size() != semantic_analytic_draw_count ||
            compiled_vertex_bytes > semantic_analytic_vertex_bytes ||
            compiled_index_bytes > semantic_analytic_index_bytes) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic analytic packed-page budget did not match compilation.");
        }
        if (!engine->ensure_semantic_analytic_page_buffers(
                compiled_vertex_bytes,
                compiled_index_bytes)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic analytic packed GPU page could not be allocated.");
        }
        wgpuQueueWriteBuffer(
            engine->queue,
            semantic_analytic_page.vertex_buffer,
            0U,
            engine->vertices.data(),
            static_cast<std::size_t>(compiled_vertex_bytes));
        wgpuQueueWriteBuffer(
            engine->queue,
            semantic_analytic_page.index_buffer,
            0U,
            engine->indices.data(),
            static_cast<std::size_t>(compiled_index_bytes));
        semantic_analytic_page.draws = std::move(compiled_draws);
        semantic_analytic_page.vertex_bytes = compiled_vertex_bytes;
        semantic_analytic_page.index_bytes = compiled_index_bytes;
        semantic_analytic_page.scene_hash =
            engine->semantic_hashes.analytic;
        semantic_analytic_page.dpi_scale = frame->dpi_scale;
        semantic_analytic_page.target_width = frame->width;
        semantic_analytic_page.target_height = frame->height;
        semantic_analytic_page.cache_valid = true;
        semantic_analytic_vertex_upload_bytes = compiled_vertex_bytes;
        semantic_analytic_index_upload_bytes = compiled_index_bytes;
    }

    auto& semantic_path_page = engine->semantic_path_cache;
    const bool semantic_path_page_hit =
        semantic_path_draw_count != 0U &&
        semantic_path_page.cache_valid &&
        semantic_path_page.scene_hash == engine->semantic_hashes.path &&
        semantic_path_page.dpi_scale == frame->dpi_scale &&
        semantic_path_page.target_width == frame->width &&
        semantic_path_page.target_height == frame->height &&
        semantic_path_page.boolean_nodes.size() ==
            semantic_path_boolean_node_count &&
        semantic_path_page.draws.size() == semantic_path_draw_count;
    if (semantic_path_draw_count != 0U && !semantic_path_page_hit) {
        std::vector<progpu_native_scene_path_fill> compiled_paths;
        std::vector<progpu_native_path_segment> compiled_segments;
        std::vector<progpu_native_scene_path_boolean_node>
            compiled_boolean_nodes;
        std::vector<std::uint32_t> compiled_brush_indices;
        std::vector<semantic_path_draw> compiled_draws;
        try {
            compiled_paths.reserve(
                static_cast<std::size_t>(semantic_path_count));
            compiled_segments.reserve(
                static_cast<std::size_t>(semantic_path_segment_count));
            compiled_boolean_nodes.reserve(
                static_cast<std::size_t>(
                    semantic_path_boolean_node_count));
            compiled_brush_indices.reserve(
                static_cast<std::size_t>(semantic_path_count));
            compiled_draws.reserve(semantic_path_draw_count);
            semantic_state_cursor state_cursor(
                bytes, header, frame->dpi_scale);
            semantic_layer_target_cursor target_cursor(
                bytes,
                frame->width,
                frame->height,
                frame->dpi_scale);
            for (std::uint32_t index = 0U;
                 index < header.command_count;
                 ++index) {
                const auto command = read_command(index);
                const auto target_extent = target_cursor.advance(command);
                const auto state = localize_semantic_state(
                    state_cursor.advance(command),
                    target_extent,
                    frame->dpi_scale);
                if (command.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH) {
                    continue;
                }
                const auto resource = read_resource(command.resource_index);
                const std::size_t path_start = compiled_paths.size();
                const std::size_t segment_start = compiled_segments.size();
                const std::size_t boolean_node_start =
                    compiled_boolean_nodes.size();
                std::uint64_t path_count64 = 0U;
                std::uint64_t segment_count64 = 0U;
                std::uint64_t boolean_node_count64 = 0U;
                if (!try_get_path_resource_counts(
                        resource,
                        path_count64,
                        segment_count64,
                        boolean_node_count64)) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                        "A validated semantic path page could not be decoded.");
                }
                const std::size_t path_count =
                    static_cast<std::size_t>(path_count64);
                const std::size_t segment_count =
                    static_cast<std::size_t>(segment_count64);
                const std::size_t boolean_node_count =
                    static_cast<std::size_t>(boolean_node_count64);
                const auto* source_segments = reinterpret_cast<
                    const progpu_native_path_segment*>(
                        bytes + resource.auxiliary_offset);
                compiled_segments.insert(
                    compiled_segments.end(),
                    source_segments,
                    source_segments + segment_count);
                const auto* source_boolean_nodes = reinterpret_cast<const
                    progpu_native_scene_path_boolean_node*>(
                        bytes + resource.auxiliary_offset +
                            segment_count *
                                sizeof(progpu_native_path_segment));
                for (std::size_t node_index = 0U;
                     node_index < boolean_node_count;
                     ++node_index) {
                    auto node = source_boolean_nodes[node_index];
                    if (node.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF) {
                        node.segment_offset += segment_start;
                    }
                    compiled_boolean_nodes.push_back(node);
                }
                for (std::size_t path_index = 0U;
                     path_index < path_count;
                     ++path_index) {
                    progpu_native_scene_path_fill path{};
                    std::memcpy(
                        &path,
                        bytes + resource.payload_offset +
                            path_index *
                                sizeof(progpu_native_scene_path_fill),
                        sizeof(path));
                    std::uint32_t brush_index = 0U;
                    const progpu_native_scene_brush* brush = nullptr;
                    if (!resolve_brush(
                            index,
                            command,
                            static_cast<std::uint32_t>(path_index),
                            brush_index,
                            brush)) {
                        return engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "A validated semantic path brush map could not be resolved.");
                    }
                    if (brush == nullptr) {
                        apply_semantic_state(path, state);
                    } else {
                        apply_semantic_transform(path, state);
                        apply_path_material(path, *brush);
                    }
                    path.segment_offset += segment_start;
                    if (path.boolean_node_count != 0U) {
                        path.boolean_node_offset += boolean_node_start;
                    }
                    compiled_paths.push_back(path);
                    compiled_brush_indices.push_back(brush_index);
                }
                compiled_draws.push_back({
                    static_cast<std::uint32_t>(path_start * 6U),
                    static_cast<std::uint32_t>(path_count * 6U)});
            }
        } catch (const std::bad_alloc&) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic path packed page could not be compiled.");
        }
        if (compiled_paths.size() != semantic_path_count ||
            compiled_segments.size() != semantic_path_segment_count ||
            compiled_boolean_nodes.size() !=
                semantic_path_boolean_node_count ||
            compiled_brush_indices.size() != semantic_path_count ||
            compiled_draws.size() != semantic_path_draw_count) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic path packed-page budget did not match compilation.");
        }
        semantic_path_page.paths = std::move(compiled_paths);
        semantic_path_page.segments = std::move(compiled_segments);
        semantic_path_page.boolean_nodes =
            std::move(compiled_boolean_nodes);
        semantic_path_page.brush_indices =
            std::move(compiled_brush_indices);
        semantic_path_page.draws = std::move(compiled_draws);
        semantic_path_page.scene_hash = engine->semantic_hashes.path;
        semantic_path_page.dpi_scale = frame->dpi_scale;
        semantic_path_page.target_width = frame->width;
        semantic_path_page.target_height = frame->height;
        semantic_path_page.cache_valid = true;
        engine->semantic_path_gpu_scene_hash = 0U;
    }

    if (semantic_path_draw_count != 0U &&
        engine->semantic_path_gpu_scene_hash !=
            engine->semantic_hashes.path) {
        engine->path_cache_valid = false;
        engine->path_gpu_cache_valid = false;
    }

    auto& semantic_glyph_page = engine->semantic_glyph_cache;
    const bool semantic_glyph_page_hit =
        semantic_glyph_draw_count != 0U &&
        semantic_glyph_page.cache_valid &&
        semantic_glyph_page.scene_hash == engine->semantic_hashes.glyph &&
        semantic_glyph_page.dpi_scale == frame->dpi_scale &&
        semantic_glyph_page.target_width == frame->width &&
        semantic_glyph_page.target_height == frame->height &&
        semantic_glyph_page.style_indices.size() == semantic_glyph_count &&
        semantic_glyph_page.color_bitmap_indices.size() ==
            semantic_glyph_count &&
        semantic_glyph_page.color_bitmaps.size() ==
            semantic_color_glyph_bitmap_count &&
        semantic_glyph_page.draws.size() == semantic_glyph_draw_count;
    if (semantic_glyph_draw_count != 0U && !semantic_glyph_page_hit) {
        std::vector<progpu_native_scene_glyph_outline> compiled_outlines;
        std::vector<progpu_native_path_segment> compiled_segments;
        std::vector<progpu_native_positioned_glyph> compiled_glyphs;
        std::vector<std::uint32_t> compiled_style_indices;
        std::vector<progpu_native_scene_color_glyph_bitmap>
            compiled_color_bitmaps;
        std::vector<std::byte> compiled_color_pixels;
        std::vector<std::uint32_t> compiled_color_bitmap_indices;
        std::vector<semantic_glyph_draw> compiled_draws;
        struct compiled_glyph_resource_layout {
            std::size_t outline_start = 0U;
            std::size_t segment_start = 0U;
            std::size_t color_bitmap_start = 0U;
            std::size_t color_pixel_start = 0U;
            bool compiled = false;
            bool color = false;
        };
        std::vector<compiled_glyph_resource_layout> compiled_resources;
        try {
            compiled_outlines.reserve(
                static_cast<std::size_t>(semantic_glyph_outline_count));
            compiled_segments.reserve(
                static_cast<std::size_t>(semantic_glyph_segment_count));
            compiled_glyphs.reserve(
                static_cast<std::size_t>(semantic_glyph_count));
            compiled_style_indices.reserve(
                static_cast<std::size_t>(semantic_glyph_count));
            compiled_color_bitmaps.reserve(static_cast<std::size_t>(
                semantic_color_glyph_bitmap_count));
            compiled_color_pixels.reserve(static_cast<std::size_t>(
                semantic_color_glyph_pixel_bytes));
            compiled_color_bitmap_indices.reserve(
                static_cast<std::size_t>(semantic_glyph_count));
            compiled_draws.reserve(semantic_glyph_draw_count);
            compiled_resources.resize(header.resource_count);
            semantic_state_cursor state_cursor(
                bytes, header, frame->dpi_scale);
            semantic_layer_target_cursor target_cursor(
                bytes,
                frame->width,
                frame->height,
                frame->dpi_scale);
            for (std::uint32_t index = 0U;
                 index < header.command_count;
                 ++index) {
                const auto command = read_command(index);
                const auto target_extent = target_cursor.advance(command);
                const auto state = localize_semantic_state(
                    state_cursor.advance(command),
                    target_extent,
                    frame->dpi_scale);
                if (command.kind !=
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN) {
                    continue;
                }
                const auto resource = read_resource(command.resource_index);
                const bool color_glyphs = is_color_glyph_resource(resource);
                auto& resource_layout =
                    compiled_resources[command.resource_index];
                if (!resource_layout.compiled) {
                    resource_layout.outline_start = compiled_outlines.size();
                    resource_layout.segment_start = compiled_segments.size();
                    resource_layout.color_bitmap_start =
                        compiled_color_bitmaps.size();
                    resource_layout.color_pixel_start =
                        compiled_color_pixels.size();
                    resource_layout.color = color_glyphs;
                } else if (resource_layout.color != color_glyphs) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                        "A retained semantic glyph resource changed kind during compilation.");
                }
                const std::size_t outline_start =
                    resource_layout.outline_start;
                const std::size_t segment_start =
                    resource_layout.segment_start;
                const std::size_t color_bitmap_start =
                    resource_layout.color_bitmap_start;
                const std::size_t color_pixel_start =
                    resource_layout.color_pixel_start;
                const std::size_t glyph_start = compiled_glyphs.size();
                const std::size_t outline_count = color_glyphs
                    ? resource.payload_size /
                        sizeof(progpu_native_scene_color_glyph_bitmap)
                    : resource.payload_size /
                        sizeof(progpu_native_scene_glyph_outline);
                const std::size_t segment_count = color_glyphs
                    ? 0U
                    : resource.auxiliary_size /
                        sizeof(progpu_native_path_segment);
                std::uint32_t glyph_payload_offset = 0U;
                std::uint32_t glyph_count32 = 0U;
                if (!try_get_glyph_payload(
                        bytes,
                        command,
                        glyph_payload_offset,
                        glyph_count32)) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                        "The semantic glyph payload changed after validation.");
                }
                const std::size_t glyph_count = glyph_count32;
                std::uint32_t text_style_index =
                    PROGPU_NATIVE_SCENE_NO_INDEX;
                // Unstyled glyphs consume their inline color and do not depend
                // on the command-indexed retained text-style page. Its family-
                // local identity may therefore remain valid across unrelated
                // command insertions that change this command's global index.
                if ((command.flags &
                        PROGPU_NATIVE_SCENE_GLYPH_STYLED) != 0U &&
                    (!try_get_command_text_style_index(
                            semantic_text_style_page,
                            index,
                            text_style_index) ||
                        text_style_index ==
                            PROGPU_NATIVE_SCENE_NO_INDEX)) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                        "The semantic glyph text style changed after validation.");
                }
                if (!resource_layout.compiled && color_glyphs) {
                    compiled_color_pixels.insert(
                        compiled_color_pixels.end(),
                        bytes + resource.auxiliary_offset,
                        bytes + resource.auxiliary_offset +
                            resource.auxiliary_size);
                    for (std::size_t bitmap_index = 0U;
                         bitmap_index < outline_count;
                         ++bitmap_index) {
                        progpu_native_scene_color_glyph_bitmap bitmap{};
                        std::memcpy(
                            &bitmap,
                            bytes + resource.payload_offset +
                                bitmap_index * sizeof(bitmap),
                            sizeof(bitmap));
                        bitmap.pixel_offset += color_pixel_start;
                        compiled_color_bitmaps.push_back(bitmap);
                    }
                } else if (!resource_layout.compiled) {
                    const auto* source_segments = reinterpret_cast<
                        const progpu_native_path_segment*>(
                            bytes + resource.auxiliary_offset);
                    compiled_segments.insert(
                        compiled_segments.end(),
                        source_segments,
                        source_segments + segment_count);
                    for (std::size_t outline_index = 0U;
                         outline_index < outline_count;
                         ++outline_index) {
                        progpu_native_scene_glyph_outline outline{};
                        std::memcpy(
                            &outline,
                            bytes + resource.payload_offset +
                                outline_index * sizeof(outline),
                            sizeof(outline));
                        outline.segment_offset += segment_start;
                        compiled_outlines.push_back(outline);
                    }
                }
                resource_layout.compiled = true;
                for (std::size_t glyph_index = 0U;
                     glyph_index < glyph_count;
                     ++glyph_index) {
                    progpu_native_positioned_glyph glyph{};
                    std::memcpy(
                        &glyph,
                        bytes + glyph_payload_offset +
                            glyph_index * sizeof(glyph),
                        sizeof(glyph));
                    if (text_style_index ==
                        PROGPU_NATIVE_SCENE_NO_INDEX) {
                        apply_semantic_state(glyph, state);
                    } else {
                        apply_semantic_transform(glyph, state);
                    }
                    const std::uint32_t source_index = glyph.outline_index;
                    if (color_glyphs) {
                        compiled_color_bitmap_indices.push_back(
                            static_cast<std::uint32_t>(
                                color_bitmap_start + source_index));
                        glyph.outline_index = 0U;
                    } else {
                        compiled_color_bitmap_indices.push_back(
                            PROGPU_NATIVE_SCENE_NO_INDEX);
                        glyph.outline_index += static_cast<std::uint32_t>(
                            outline_start);
                    }
                    compiled_glyphs.push_back(glyph);
                    compiled_style_indices.push_back(text_style_index);
                }
                compiled_draws.push_back({
                    static_cast<std::uint32_t>(glyph_start),
                    static_cast<std::uint32_t>(glyph_count)});
            }
        } catch (const std::bad_alloc&) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic glyph packed page could not be compiled.");
        }
        if (compiled_outlines.size() != semantic_glyph_outline_count ||
            compiled_segments.size() != semantic_glyph_segment_count ||
            compiled_glyphs.size() != semantic_glyph_count ||
            compiled_style_indices.size() != semantic_glyph_count ||
            compiled_color_bitmaps.size() !=
                semantic_color_glyph_bitmap_count ||
            compiled_color_pixels.size() !=
                semantic_color_glyph_pixel_bytes ||
            compiled_color_bitmap_indices.size() != semantic_glyph_count ||
            compiled_draws.size() != semantic_glyph_draw_count) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic glyph packed-page budget did not match compilation.");
        }
        semantic_glyph_page.outlines = std::move(compiled_outlines);
        semantic_glyph_page.segments = std::move(compiled_segments);
        semantic_glyph_page.glyphs = std::move(compiled_glyphs);
        semantic_glyph_page.style_indices =
            std::move(compiled_style_indices);
        semantic_glyph_page.color_bitmaps =
            std::move(compiled_color_bitmaps);
        semantic_glyph_page.color_pixels =
            std::move(compiled_color_pixels);
        semantic_glyph_page.color_bitmap_indices =
            std::move(compiled_color_bitmap_indices);
        semantic_glyph_page.color_rasters.clear();
        semantic_glyph_page.draws = std::move(compiled_draws);
        semantic_glyph_page.scene_hash = engine->semantic_hashes.glyph;
        semantic_glyph_page.dpi_scale = frame->dpi_scale;
        semantic_glyph_page.target_width = frame->width;
        semantic_glyph_page.target_height = frame->height;
        semantic_glyph_page.cache_valid = true;
        engine->semantic_glyph_gpu_scene_hash = 0U;
    }

    if (semantic_glyph_draw_count != 0U &&
        engine->semantic_glyph_gpu_scene_hash !=
            engine->semantic_hashes.glyph) {
        engine->glyph_cache_valid = false;
        engine->glyph_gpu_cache_valid = false;
    }
    if (semantic_glyph_draw_count != 0U &&
        !prepare_color_glyph_atlas(
            *engine,
            semantic_glyph_page,
            engine->semantic_hashes.glyph,
            semantic_color_glyph_upload_bytes)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The semantic retained color-glyph atlas could not be prepared.");
    }

    std::uint64_t semantic_image_vertex_upload_bytes = 0U;
    std::uint64_t semantic_image_index_upload_bytes = 0U;
    std::uint64_t semantic_image_texture_upload_bytes = 0U;
    std::uint64_t semantic_image_color_uniform_upload_bytes = 0U;
    auto& semantic_image_page = engine->semantic_image_cache;
    const bool semantic_image_page_hit =
        semantic_image_draw_count != 0U &&
        semantic_image_page.cache_valid &&
        semantic_image_page.scene_hash == engine->semantic_hashes.image &&
        semantic_image_page.dpi_scale == frame->dpi_scale &&
        semantic_image_page.target_width == frame->width &&
        semantic_image_page.target_height == frame->height &&
        semantic_image_page.draws.size() == semantic_image_draw_count;
    if (semantic_image_draw_count != 0U && !semantic_image_page_hit) {
        const bool created_resources = engine->image_pipeline == nullptr;
        if (!create_image_resources(*engine)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic image WebGPU resources could not be created.");
        }
        if (semantic_has_image_color_matrices &&
            !create_image_mask_resources(*engine)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic image color-matrix WebGPU resources could not be created.");
        }
        std::vector<progpu::native::vector_vertex> vertices;
        std::vector<semantic_image_draw> compiled_draws;
        WGPUBuffer compiled_vertex_buffer = nullptr;
        const auto release_compiled = [&]() noexcept {
            for (auto& draw : compiled_draws) {
                semantic::release_semantic_image_blur_resources(draw);
                if (draw.effect_dummy_mask_bind_group != nullptr) {
                    wgpuBindGroupRelease(draw.effect_dummy_mask_bind_group);
                }
                if (draw.effect_texture_bind_group != nullptr) {
                    wgpuBindGroupRelease(draw.effect_texture_bind_group);
                }
                if (draw.effect_uniform_bind_group != nullptr) {
                    wgpuBindGroupRelease(draw.effect_uniform_bind_group);
                }
                if (draw.effect_uniform_buffer != nullptr) {
                    wgpuBufferDestroy(draw.effect_uniform_buffer);
                    wgpuBufferRelease(draw.effect_uniform_buffer);
                }
                if (draw.effect_mask_uniform_buffer != nullptr) {
                    wgpuBufferDestroy(draw.effect_mask_uniform_buffer);
                    wgpuBufferRelease(draw.effect_mask_uniform_buffer);
                }
                if (draw.color_matrix_bind_group != nullptr) {
                    wgpuBindGroupRelease(draw.color_matrix_bind_group);
                }
                if (draw.color_matrix_buffer != nullptr) {
                    wgpuBufferDestroy(draw.color_matrix_buffer);
                    wgpuBufferRelease(draw.color_matrix_buffer);
                }
                if (draw.texture_bind_group != nullptr) {
                    wgpuBindGroupRelease(draw.texture_bind_group);
                }
                if (draw.view != nullptr) {
                    wgpuTextureViewRelease(draw.view);
                }
                if (draw.texture != nullptr) {
                    wgpuTextureDestroy(draw.texture);
                    wgpuTextureRelease(draw.texture);
                }
            }
            compiled_draws.clear();
            if (compiled_vertex_buffer != nullptr) {
                wgpuBufferDestroy(compiled_vertex_buffer);
                wgpuBufferRelease(compiled_vertex_buffer);
                compiled_vertex_buffer = nullptr;
            }
        };
        try {
            vertices.reserve(static_cast<std::size_t>(
                semantic_image_vertex_count));
            compiled_draws.reserve(semantic_image_draw_count);
            semantic_state_cursor state_cursor(
                bytes, header, frame->dpi_scale);
            semantic_layer_target_cursor target_cursor(
                bytes,
                frame->width,
                frame->height,
                frame->dpi_scale);
            for (std::uint32_t index = 0U;
                 index < header.command_count;
                 ++index) {
                const auto command = read_command(index);
                const auto target_extent = target_cursor.advance(command);
                const auto state = localize_semantic_state(
                    state_cursor.advance(command),
                    target_extent,
                    frame->dpi_scale);
                if (command.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
                    continue;
                }
                const auto resource = read_resource(command.resource_index);
                progpu_native_scene_image_draw image{};
                std::memcpy(
                    &image,
                    bytes + command.payload_offset,
                    sizeof(image));
                apply_semantic_state(image, state);
                semantic_image_options image_options{};
                const bool external_image =
                    (resource.flags & PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE) != 0U;
                const std::uint64_t validation_bytes = external_image
                    ? static_cast<std::uint64_t>(image.row_bytes) *
                            (image.image_height - 1U) +
                        static_cast<std::uint64_t>(image.image_width) * 4U
                    : resource.payload_size;
                const auto* external_binding = external_image
                    ? engine->find_semantic_external_image_binding(
                        resource.resource_id,
                        resource.generation)
                    : nullptr;
                const auto* chroma_binding = external_image
                    ? engine->find_semantic_external_image_binding(
                        resource.resource_id,
                        resource.generation,
                        PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE_CHROMA)
                    : nullptr;
                const auto* effect_mask_binding = external_image
                    ? engine->find_semantic_external_image_binding(
                        resource.resource_id,
                        resource.generation,
                        PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE_MASK)
                    : nullptr;
                if (!validate_image_draw_payload(
                        bytes,
                        command,
                        image,
                        validation_bytes,
                        image_options) ||
                    (external_image &&
                        (external_binding == nullptr ||
                            external_binding->width != image.image_width ||
                            external_binding->height != image.image_height))) {
                    release_compiled();
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A retained image sampling payload is invalid.");
                }
                const bool cubic_sampling =
                    image.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC;
                const std::uint32_t first_vertex =
                    static_cast<std::uint32_t>(vertices.size());
                const auto append_quad = [&image, &image_options, &vertices,
                    frame, cubic_sampling](
                    const progpu_native_scene_image_patch* patch) {
                    const auto& source = patch == nullptr
                        ? image.source_rect
                        : patch->source_rect;
                    const auto& destination = patch == nullptr
                        ? image.destination_rect
                        : patch->destination_rect;
                    const auto transform = patch == nullptr
                        ? image.transform
                        : progpu::native::compose_affine(
                            patch->transform,
                            image.transform);
                    float color[4]{};
                    float patch_kind = 0.0F;
                    float color_blend_mode = 0.0F;
                    float patch_opacity = 1.0F;
                    if (patch == nullptr) {
                        semantic::resolve_image_vertex_color(
                            image,
                            image_options.has_effect,
                            color);
                    } else {
                        semantic::resolve_image_patch_vertex_attributes(
                            image,
                            *patch,
                            image_options.has_effect,
                            color,
                            patch_kind,
                            color_blend_mode,
                            patch_opacity);
                    }
                    const bool samples_texture = patch == nullptr ||
                        patch->kind !=
                            PROGPU_NATIVE_SCENE_IMAGE_PATCH_FIXED_COLOR;
                    const float x0 = destination.x;
                    const float y0 = destination.y;
                    const float x1 = x0 + destination.width;
                    const float y1 = y0 + destination.height;
                    const float u0 = samples_texture
                        ? source.x / static_cast<float>(image.image_width)
                        : 0.0F;
                    const float v0 = samples_texture
                        ? source.y / static_cast<float>(image.image_height)
                        : 0.0F;
                    const float u1 = samples_texture
                        ? (source.x + source.width) /
                            static_cast<float>(image.image_width)
                        : 1.0F;
                    const float v1 = samples_texture
                        ? (source.y + source.height) /
                            static_cast<float>(image.image_height)
                        : 1.0F;
                    constexpr std::array<std::uint32_t, 6U>
                        triangle_corners{0U, 1U, 2U, 0U, 2U, 3U};
                    constexpr std::array<
                        std::array<std::uint32_t, 2U>, 4U> corners{{
                        {0U, 0U}, {1U, 0U}, {1U, 1U}, {0U, 1U}
                    }};
                    const std::uint32_t vertex_count = patch == nullptr
                        ? 4U
                        : 6U;
                    for (std::uint32_t index = 0U;
                         index < vertex_count;
                         ++index) {
                        const auto& corner = corners[patch == nullptr
                            ? index
                            : triangle_corners[index]];
                        progpu::native::vector_vertex vertex{};
                        progpu::native::transform_point(
                            transform,
                            corner[0] == 0U ? x0 : x1,
                            corner[1] == 0U ? y0 : y1,
                            vertex.position[0],
                            vertex.position[1]);
                        if ((image.flags &
                                PROGPU_NATIVE_SCENE_IMAGE_SNAP_TO_PIXELS) !=
                            0U) {
                            semantic::snap_semantic_image_point(
                                vertex.position[0],
                                vertex.position[1],
                                frame->dpi_scale);
                        }
                        std::copy(
                            std::begin(color),
                            std::end(color),
                            std::begin(vertex.color));
                        vertex.texture_coordinate[0] =
                            corner[0] == 0U ? u0 : u1;
                        vertex.texture_coordinate[1] =
                            corner[1] == 0U ? v0 : v1;
                        vertex.brush_index = patch_kind;
                        vertex.shape_size[0] = cubic_sampling
                            ? image_options.cubic_b
                            : 0.0F;
                        vertex.shape_size[1] = cubic_sampling
                            ? image_options.cubic_c
                            : 0.5F;
                        vertex.corner_radius = color_blend_mode;
                        vertex.stroke_thickness = patch_opacity;
                        vertex.shape_type = 0.0F;
                        vertices.push_back(vertex);
                    }
                };
                if (image_options.patch_count == 0U) {
                    append_quad(nullptr);
                } else {
                    for (std::uint32_t patch_index = 0U;
                         patch_index < image_options.patch_count;
                         ++patch_index) {
                        progpu_native_scene_image_patch patch{};
                        std::memcpy(
                            &patch,
                            image_options.patch_bytes +
                                static_cast<std::size_t>(patch_index) *
                                    sizeof(patch),
                            sizeof(patch));
                        append_quad(&patch);
                    }
                }

                WGPUTextureDescriptor texture_descriptor{};
                texture_descriptor.label =
                    progpu::native::webgpu::string_view(
                        "ProGPU semantic retained RGBA image");
                texture_descriptor.usage = WGPUTextureUsage_TextureBinding |
                    WGPUTextureUsage_CopyDst;
                texture_descriptor.dimension = WGPUTextureDimension_2D;
                texture_descriptor.size = {
                    image.image_width, image.image_height, 1U};
                texture_descriptor.format = WGPUTextureFormat_RGBA8Unorm;
                texture_descriptor.mipLevelCount = 1U;
                texture_descriptor.sampleCount = 1U;
                semantic_image_draw draw{};
                draw.first_vertex = first_vertex;
                draw.vertex_count = image_options.patch_count == 0U
                    ? 4U
                    : image_options.patch_count * 6U;
                draw.sampling = image.sampling;
                draw.has_color_matrix = image_options.has_color_matrix;
                draw.has_effect = image_options.has_effect;
                const bool requires_chroma = draw.has_effect &&
                    image_options.effect.flags0[0] > 0.5F;
                if (requires_chroma && chroma_binding == nullptr) {
                    release_compiled();
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A planar image effect is missing its chroma binding.");
                }
                const bool requires_effect_mask = draw.has_effect &&
                    image_options.effect.flags0[1] > 0.5F;
                if (requires_effect_mask && effect_mask_binding == nullptr) {
                    release_compiled();
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "An image effect is missing its mask binding.");
                }
                draw.has_effect_mask = requires_effect_mask;
                if (external_image) {
                    draw.view = external_binding->view;
                    progpu::native::webgpu::texture_view_add_ref(draw.view);
                } else {
                    draw.texture = wgpuDeviceCreateTexture(
                        engine->device,
                        &texture_descriptor);
                }
                if (!external_image && draw.texture != nullptr) {
                    draw.view = wgpuTextureCreateView(draw.texture, nullptr);
                }
                const WGPUSampler image_sampler =
                    semantic::resolve_semantic_image_sampler(
                        *engine,
                        image.sampling,
                        image.max_anisotropy);
                if (draw.view != nullptr && image_sampler != nullptr) {
                    draw.texture_bind_group = create_image_texture_bind_group(
                        *engine,
                        image_sampler,
                        draw.view,
                        "ProGPU semantic retained image bind group");
                    if (draw.has_color_matrix &&
                        !create_semantic_image_color_matrix_resources(
                            *engine,
                            draw.view,
                            image_options.color_matrix,
                            draw.color_matrix_buffer,
                            draw.color_matrix_bind_group)) {
                        draw.has_color_matrix = false;
                    }
                    progpu_native_scene_image_effect render_effect =
                        image_options.effect;
                    if (draw.has_effect &&
                        (render_effect.effects1[2] > 0.01F ||
                            (render_effect.flags &
                                PROGPU_NATIVE_SCENE_IMAGE_EFFECT_UNFILTERABLE_PLANAR) !=
                                0U) &&
                        !semantic::create_semantic_image_blur_resources(
                            *engine,
                            draw.view,
                            requires_chroma ? chroma_binding->view : nullptr,
                            image.image_width,
                            image.image_height,
                            render_effect,
                            draw)) {
                        draw.has_effect = false;
                    }
                    if (draw.has_live_blur) {
                        render_effect.effects1[2] = 0.0F;
                        render_effect.flags0[0] = 0.0F;
                        render_effect.flags = 0U;
                    }
                    if (draw.has_effect &&
                        !create_semantic_image_effect_resources(
                            *engine,
                            draw.has_live_blur
                                ? draw.blur_output_view
                                : draw.view,
                            requires_chroma && !draw.has_live_blur
                                ? chroma_binding->view
                                : nullptr,
                            requires_effect_mask
                                ? effect_mask_binding->view
                                : nullptr,
                            requires_effect_mask
                                ? effect_mask_binding->width
                                : 0U,
                            requires_effect_mask
                                ? effect_mask_binding->height
                                : 0U,
                            image_sampler,
                            render_effect,
                            draw.effect_uniform_buffer,
                            draw.effect_mask_uniform_buffer,
                            draw.effect_uniform_bind_group,
                            draw.effect_texture_bind_group,
                            draw.effect_dummy_mask_bind_group)) {
                        draw.has_effect = false;
                    }
                }
                compiled_draws.push_back(draw);
                auto& retained_draw = compiled_draws.back();
                if ((!external_image && retained_draw.texture == nullptr) ||
                    retained_draw.view == nullptr ||
                    retained_draw.texture_bind_group == nullptr ||
                    (image_options.has_color_matrix &&
                        retained_draw.color_matrix_bind_group == nullptr) ||
                    (image_options.has_effect &&
                        (retained_draw.effect_uniform_bind_group == nullptr ||
                            retained_draw.effect_texture_bind_group == nullptr ||
                            retained_draw.effect_dummy_mask_bind_group ==
                                nullptr))) {
                    release_compiled();
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "A semantic image page texture could not be allocated.");
                }
                if (!external_image) {
                    progpu::native::webgpu::image_copy_texture destination{};
                    destination.texture = retained_draw.texture;
                    destination.aspect = WGPUTextureAspect_All;
                    progpu::native::webgpu::texture_data_layout layout{};
                    layout.bytesPerRow = image.row_bytes;
                    layout.rowsPerImage = image.image_height;
                    const std::uint64_t upload_bytes =
                        static_cast<std::uint64_t>(image.row_bytes) *
                            (image.image_height - 1U) +
                        static_cast<std::uint64_t>(image.image_width) * 4U;
                    const WGPUExtent3D extent{
                        image.image_width, image.image_height, 1U};
                    wgpuQueueWriteTexture(
                        engine->queue,
                        &destination,
                        bytes + resource.payload_offset,
                        static_cast<std::size_t>(upload_bytes),
                        &layout,
                        &extent);
                    semantic_image_texture_upload_bytes += upload_bytes;
                }
                semantic_image_color_uniform_upload_bytes +=
                    image_options.has_color_matrix
                    ? sizeof(progpu::native::gpu_mask_sampling_uniforms)
                    : image_options.has_effect
                    ? offsetof(
                        progpu_native_scene_image_effect,
                        struct_size) +
                        (draw.has_live_blur ? 1824U : 0U)
                    : 0U;
            }
        } catch (const std::bad_alloc&) {
            release_compiled();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic image packed page could not be compiled.");
        }
        const std::uint64_t vertex_bytes = vertices.size() *
            sizeof(progpu::native::vector_vertex);
        if (compiled_draws.size() != semantic_image_draw_count ||
            vertex_bytes != semantic_image_vertex_count *
                    sizeof(progpu::native::vector_vertex)) {
            release_compiled();
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic image packed-page budget did not match compilation.");
        }
        WGPUBufferDescriptor vertex_descriptor{};
        vertex_descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU semantic image packed vertex page");
        vertex_descriptor.usage =
            WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst;
        vertex_descriptor.size = vertex_bytes;
        compiled_vertex_buffer = wgpuDeviceCreateBuffer(
            engine->device,
            &vertex_descriptor);
        if (compiled_vertex_buffer == nullptr) {
            release_compiled();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic image packed vertex page could not be allocated.");
        }
        wgpuQueueWriteBuffer(
            engine->queue,
            compiled_vertex_buffer,
            0U,
            vertices.data(),
            static_cast<std::size_t>(vertex_bytes));
        engine->release_semantic_image_page();
        semantic_image_page.vertex_buffer = compiled_vertex_buffer;
        compiled_vertex_buffer = nullptr;
        semantic_image_page.vertex_bytes = vertex_bytes;
        semantic_image_page.draws = std::move(compiled_draws);
        semantic_image_page.scene_hash = engine->semantic_hashes.image;
        semantic_image_page.dpi_scale = frame->dpi_scale;
        semantic_image_page.target_width = frame->width;
        semantic_image_page.target_height = frame->height;
        semantic_image_page.cache_valid = true;
        semantic_image_vertex_upload_bytes = vertex_bytes;
        semantic_image_index_upload_bytes = created_resources
            ? 6U * sizeof(std::uint32_t)
            : 0U;
    }

    const std::uint64_t submission_start = engine->submission_count;
    std::uint32_t draw_calls = 0U;
    std::uint32_t family_switches = 0U;
    std::uint32_t previous_family = 0U;
    std::uint64_t vertex_upload_bytes =
        semantic_analytic_vertex_upload_bytes +
        semantic_image_vertex_upload_bytes;
    std::uint64_t index_upload_bytes =
        semantic_analytic_index_upload_bytes +
        semantic_image_index_upload_bytes;
    std::uint64_t texture_upload_bytes =
        semantic_image_texture_upload_bytes +
        semantic_color_glyph_upload_bytes;
    std::uint64_t uniform_upload_bytes =
        semantic_image_color_uniform_upload_bytes;
    std::uint64_t coverage_staging_bytes = 0U;
    std::uint64_t semantic_layer_vertex_upload_bytes = 0U;
    std::uint64_t semantic_layer_uniform_upload_bytes = 0U;
    std::uint64_t semantic_layer_mask_uniform_upload_bytes = 0U;
    std::uint64_t semantic_layer_effect_uniform_upload_bytes = 0U;
    std::uint32_t semantic_layer_effect_pass_count = 0U;
    std::uint32_t semantic_effect_operation_count = 0U;
    std::uint32_t semantic_effect_cache_hit_count = 0U;
    std::array<progpu::native::effects::semantic_output_cache,
        PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS>
        semantic_effect_working_caches{};
    std::array<bool, PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS>
        semantic_effect_cache_updates{};
    for (std::size_t index = 0U;
         index < semantic_effect_working_caches.size();
         ++index) {
        semantic_effect_working_caches[index] =
            engine->semantic_layer_slots[index].effect_output_cache;
    }
    const std::uint64_t payload_hash = engine->semantic_scene_hash;
    std::uint32_t semantic_analytic_draw_index = 0U;
    std::uint32_t semantic_path_draw_index = 0U;
    std::uint32_t semantic_glyph_draw_index = 0U;
    std::uint32_t semantic_image_draw_index = 0U;
    std::uint32_t semantic_3d_draw_index = 0U;

    const auto discard_encoder = [&]() noexcept {
        if (engine->semantic_encoder != nullptr) {
            wgpuCommandEncoderRelease(engine->semantic_encoder);
            engine->semantic_encoder = nullptr;
        }
    };
    const auto begin_encoder = [&]() noexcept {
        if (engine->semantic_encoder != nullptr) {
            return true;
        }
        WGPUCommandEncoderDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native semantic scene encoder");
        engine->semantic_encoder = wgpuDeviceCreateCommandEncoder(
            engine->device,
            &descriptor);
        return engine->semantic_encoder != nullptr;
    };
    const auto flush_encoder = [&]() noexcept {
        if (engine->semantic_encoder == nullptr) {
            return PROGPU_NATIVE_STATUS_SUCCESS;
        }
        WGPUCommandEncoder encoder = engine->semantic_encoder;
        engine->semantic_encoder = nullptr;
        WGPUCommandBufferDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native semantic scene commands");
        WGPUCommandBuffer command = wgpuCommandEncoderFinish(
            encoder,
            &descriptor);
        wgpuCommandEncoderRelease(encoder);
        if (command == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic scene command buffer could not be finished.");
        }
        engine->submit(command);
        wgpuCommandBufferRelease(command);
        return PROGPU_NATIVE_STATUS_SUCCESS;
    };

    const auto note_family = [&](std::uint32_t family) noexcept {
        if (family != previous_family) {
            ++family_switches;
            previous_family = family;
        }
    };
    const auto reset_semantic_prepare_state = [&]() noexcept {
        engine->semantic_prepare_only = false;
        engine->semantic_load_target = false;
        engine->semantic_path_draw_active = false;
        engine->semantic_path_first_index = 0U;
        engine->semantic_path_index_count = 0U;
        engine->semantic_path_materials_active = false;
        engine->semantic_glyph_draw_active = false;
        engine->semantic_glyph_first_instance = 0U;
        engine->semantic_glyph_instance_count = 0U;
    };

    if ((semantic_draw_count != 0U ||
            semantic_has_materialized_layers) &&
        !begin_encoder()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic scene command encoder could not be created.");
    }

    if (engine->semantic_encoder != nullptr &&
        !semantic::encode_semantic_image_blurs(
            *engine,
            engine->semantic_encoder,
            semantic_image_page)) {
        discard_encoder();
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic image live-blur passes could not be encoded.");
    }

    if (semantic_path_draw_count != 0U &&
        (engine->semantic_path_gpu_scene_hash !=
                engine->semantic_hashes.path ||
            !engine->path_cache_valid ||
            !engine->path_gpu_cache_valid)) {
        progpu_native_path_frame family{};
        family.struct_size = sizeof(family);
        family.width = frame->width;
        family.height = frame->height;
        family.dpi_scale = frame->dpi_scale;
        family.target_view = frame->target_view;
        family.clear_color = frame->clear_color;
#if SIZE_MAX == UINT64_MAX
        static_assert(sizeof(std::size_t) == sizeof(std::uint64_t));
        static_assert(sizeof(progpu_native_scene_path_fill) ==
            sizeof(progpu_native_path_fill));
        static_assert(offsetof(
            progpu_native_scene_path_fill,
            segment_offset) == offsetof(
            progpu_native_path_fill,
            segment_offset));
        static_assert(offsetof(
            progpu_native_scene_path_fill,
            fill_rule) == offsetof(
            progpu_native_path_fill,
            fill_rule));
        static_assert(sizeof(progpu_native_scene_path_boolean_node) ==
            sizeof(progpu_native_path_boolean_node));
        family.paths = reinterpret_cast<const progpu_native_path_fill*>(
            semantic_path_page.paths.data());
        family.path_count = semantic_path_page.paths.size();
        family.boolean_nodes = reinterpret_cast<const
            progpu_native_path_boolean_node*>(
                semantic_path_page.boolean_nodes.data());
        family.boolean_node_count =
            semantic_path_page.boolean_nodes.size();
#else
        std::vector<progpu_native_path_fill> translated_paths;
        std::vector<progpu_native_path_boolean_node>
            translated_boolean_nodes;
        try {
            translated_paths.reserve(semantic_path_page.paths.size());
            translated_boolean_nodes.reserve(
                semantic_path_page.boolean_nodes.size());
            for (const auto& source : semantic_path_page.paths) {
                if (source.segment_offset > SIZE_MAX ||
                    source.segment_count > SIZE_MAX ||
                    source.boolean_node_offset > SIZE_MAX ||
                    source.boolean_node_count > SIZE_MAX) {
                    discard_encoder();
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "A semantic path index exceeds the wasm32 address range.");
                }
                translated_paths.push_back({
                    static_cast<std::size_t>(source.segment_offset),
                    static_cast<std::size_t>(source.segment_count),
                    static_cast<std::size_t>(source.boolean_node_offset),
                    static_cast<std::size_t>(source.boolean_node_count),
                    source.min_x,
                    source.min_y,
                    source.max_x,
                    source.max_y,
                    source.color,
                    source.transform,
                    source.fill_rule,
                    source.sample_grid});
            }
            for (const auto& source : semantic_path_page.boolean_nodes) {
                if (source.segment_offset > SIZE_MAX ||
                    source.segment_count > SIZE_MAX) {
                    discard_encoder();
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "A semantic boolean-path index exceeds the wasm32 address range.");
                }
                translated_boolean_nodes.push_back({
                    static_cast<std::size_t>(source.segment_offset),
                    static_cast<std::size_t>(source.segment_count),
                    source.min_x,
                    source.min_y,
                    source.max_x,
                    source.max_y,
                    source.fill_rule,
                    source.kind,
                    source.reserved0,
                    source.reserved1});
            }
        } catch (const std::bad_alloc&) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The wasm32 semantic path translation could not be allocated.");
        }
        family.paths = translated_paths.data();
        family.path_count = translated_paths.size();
        family.boolean_nodes = translated_boolean_nodes.data();
        family.boolean_node_count = translated_boolean_nodes.size();
#endif
        family.segments = semantic_path_page.segments.data();
        family.segment_count = semantic_path_page.segments.size();
        family.flags =
            PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD;
        family.content_revision = revision32(engine->semantic_hashes.path);
        progpu_native_path_frame_metrics family_metrics{};
        family_metrics.struct_size = sizeof(family_metrics);
        engine->semantic_prepare_only = true;
        engine->semantic_path_draw_active = true;
        engine->semantic_path_materials_active = true;
        engine->semantic_path_first_index =
            semantic_path_page.draws.front().first_index;
        engine->semantic_path_index_count =
            semantic_path_page.draws.front().index_count;
        const auto status = progpu_native_engine_render_paths(
            engine, &family, &family_metrics);
        reset_semantic_prepare_state();
        vertex_upload_bytes += family_metrics.vertex_upload_bytes;
        index_upload_bytes += family_metrics.index_upload_bytes;
        uniform_upload_bytes += family_metrics.uniform_upload_bytes;
        coverage_staging_bytes += family_metrics.coverage_staging_bytes;
        if (status != PROGPU_NATIVE_STATUS_SUCCESS) {
            discard_encoder();
            return status;
        }
        engine->semantic_path_gpu_scene_hash =
            engine->semantic_hashes.path;
    }

    if (semantic_glyph_draw_count != 0U &&
        (engine->semantic_glyph_gpu_scene_hash !=
                engine->semantic_hashes.glyph ||
            !engine->glyph_cache_valid ||
            !engine->glyph_gpu_cache_valid)) {
        progpu_native_glyph_frame family{};
        family.struct_size = sizeof(family);
        family.width = frame->width;
        family.height = frame->height;
        family.dpi_scale = frame->dpi_scale;
        family.target_view = frame->target_view;
        family.clear_color = frame->clear_color;
#if SIZE_MAX == UINT64_MAX
        static_assert(sizeof(std::size_t) == sizeof(std::uint64_t));
        static_assert(sizeof(progpu_native_scene_glyph_outline) ==
            sizeof(progpu_native_glyph_outline));
        static_assert(offsetof(
            progpu_native_scene_glyph_outline,
            segment_offset) == offsetof(
            progpu_native_glyph_outline,
            segment_offset));
        static_assert(offsetof(
            progpu_native_scene_glyph_outline,
            raster_scale) == offsetof(
            progpu_native_glyph_outline,
            raster_scale));
        family.outlines = reinterpret_cast<
            const progpu_native_glyph_outline*>(
                semantic_glyph_page.outlines.data());
        family.outline_count = semantic_glyph_page.outlines.size();
#else
        std::vector<progpu_native_glyph_outline> translated_outlines;
        try {
            translated_outlines.reserve(
                semantic_glyph_page.outlines.size());
            for (const auto& source : semantic_glyph_page.outlines) {
                if (source.segment_offset > SIZE_MAX ||
                    source.segment_count > SIZE_MAX) {
                    discard_encoder();
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "A semantic glyph index exceeds the wasm32 address range.");
                }
                translated_outlines.push_back({
                    static_cast<std::size_t>(source.segment_offset),
                    static_cast<std::size_t>(source.segment_count),
                    source.min_x,
                    source.min_y,
                    source.max_x,
                    source.max_y,
                    source.raster_scale,
                    source.subpixel_x});
            }
        } catch (const std::bad_alloc&) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The wasm32 semantic glyph translation could not be allocated.");
        }
        family.outlines = translated_outlines.data();
        family.outline_count = translated_outlines.size();
#endif
        family.segments = semantic_glyph_page.segments.data();
        family.segment_count = semantic_glyph_page.segments.size();
        family.glyphs = semantic_glyph_page.glyphs.data();
        family.glyph_count = semantic_glyph_page.glyphs.size();
        family.flags =
            PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD;
        family.content_revision = revision32(engine->semantic_hashes.glyph);
        progpu_native_glyph_frame_metrics family_metrics{};
        family_metrics.struct_size = sizeof(family_metrics);
        engine->semantic_prepare_only = true;
        engine->semantic_glyph_draw_active = true;
        engine->semantic_glyph_first_instance =
            semantic_glyph_page.draws.front().first_instance;
        engine->semantic_glyph_instance_count =
            semantic_glyph_page.draws.front().instance_count;
        const auto status = progpu_native_engine_render_glyphs(
            engine, &family, &family_metrics);
        reset_semantic_prepare_state();
        vertex_upload_bytes += family_metrics.instance_upload_bytes;
        uniform_upload_bytes += family_metrics.uniform_upload_bytes;
        coverage_staging_bytes += family_metrics.coverage_staging_bytes;
        if (status != PROGPU_NATIVE_STATUS_SUCCESS) {
            discard_encoder();
            return status;
        }
        engine->semantic_glyph_gpu_scene_hash =
            engine->semantic_hashes.glyph;
    }

    if (semantic_has_masked_glyphs &&
        !create_text_masked_pipeline(*engine)) {
        discard_encoder();
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic per-draw masked glyph pipeline could not be created.");
    }
    if (semantic_has_masked_images &&
        !create_image_mask_resources(*engine)) {
        discard_encoder();
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic per-draw masked image pipeline could not be created.");
    }
    if (semantic_has_vector_mask_chains &&
        !create_semantic_vector_mask_chain_pipeline(*engine)) {
        discard_encoder();
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic bounded vector mask-chain pipeline could not be created.");
    }
    if (semantic_has_text_mask_chains &&
        !create_semantic_text_mask_chain_pipeline(*engine)) {
        discard_encoder();
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic bounded text mask-chain pipeline could not be created.");
    }
    if (semantic_has_image_mask_chains &&
        !create_semantic_image_mask_chain_pipelines(*engine)) {
        discard_encoder();
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic bounded image mask-chain pipelines could not be created.");
    }

    const gpu_uniforms uniforms = create_uniforms(
        frame->width,
        frame->height,
        frame->dpi_scale);
    if (semantic_analytic_draw_count != 0U ||
        semantic_path_draw_count != 0U ||
        semantic_glyph_draw_count != 0U) {
        if (engine->analytic_pipeline == nullptr &&
            !create_analytic_pipeline(*engine)) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic vector pipeline could not be created.");
        }
        const bool uploaded = engine->upload_uniform_if_changed(
            engine->analytic_uniform_buffer,
            uniforms,
            engine->cached_analytic_uniforms,
            engine->analytic_uniform_cache_valid);
        uniform_upload_bytes += uploaded ? sizeof(gpu_uniforms) : 0U;
    }
    if (semantic_image_draw_count != 0U) {
        if (!create_image_resources(*engine)) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic image pipeline could not be created.");
        }
        const bool uploaded = engine->upload_uniform_if_changed(
            engine->image_uniform_buffer,
            uniforms,
            engine->cached_image_uniforms,
            engine->image_uniform_cache_valid);
        uniform_upload_bytes += uploaded ? sizeof(gpu_uniforms) : 0U;
    }
    std::uint64_t semantic_3d_upload_bytes = 0U;
    if (semantic_3d_draw_count != 0U) {
        const auto status = compile_semantic_3d_page(
            *engine,
            bytes,
            header,
            *frame,
            semantic_3d_draw_count,
            semantic_3d_upload_bytes);
        if (status != PROGPU_NATIVE_STATUS_SUCCESS) {
            discard_encoder();
            return status;
        }
        if (!prepare_semantic_depth_resources(
                *engine,
                layer_budget,
                frame->width,
                frame->height)) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native retained 3D depth targets could not be prepared.");
        }
        vertex_upload_bytes += semantic_3d_upload_bytes;
    }
    if (semantic_has_materialized_layers) {
        const std::uint32_t semantic_layer_quad_count =
            semantic_materialized_layer_count +
            semantic_advanced_layer_count * 2U +
            (semantic_destination_sampling_active ? 1U : 0U) +
            semantic_effected_backdrop_layer_count;
        if ((semantic_has_layer_masks || semantic_has_state_masks) &&
            !create_layer_mask_resources(*engine)) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The retained semantic layer-mask pipeline could not be prepared.");
        }
        if (semantic_has_layer_effects &&
            (!create_gaussian_effect_resources(*engine) ||
             (semantic_has_drop_shadows &&
                !create_drop_shadow_effect_resources(*engine)) ||
             !ensure_semantic_effect_uniform_buffer(
                *engine,
                semantic_effect_uniform_bytes))) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The retained semantic effect-chain resources could not be prepared.");
        }
        if (!prepare_semantic_layer_resources(
                *engine,
                layer_budget,
                frame->width,
                frame->height,
                frame->dpi_scale,
                semantic_layer_quad_count,
                semantic_layer_uniform_upload_bytes)) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The bounded semantic isolated-layer GPU pool could not be prepared.");
        }
        std::uint64_t advanced_uniform_upload_bytes = 0U;
        if (!prepare_semantic_advanced_blend_resources(
                *engine,
                frame->width,
                frame->height,
                std::max(semantic_advanced_source_width, 1U),
                std::max(semantic_advanced_source_height, 1U),
                semantic_advanced_layer_count,
                frame->dpi_scale,
                advanced_uniform_upload_bytes)) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The bounded semantic destination-aware blend pool could not be prepared.");
        }
        semantic_layer_uniform_upload_bytes +=
            advanced_uniform_upload_bytes;
        std::uint64_t backdrop_uniform_upload_bytes = 0U;
        if (!prepare_semantic_backdrop_resources(
                *engine,
                frame->width,
                frame->height,
                semantic_backdrop_layer_count,
                frame->dpi_scale,
                backdrop_uniform_upload_bytes)) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The bounded semantic backdrop-capture root could not be prepared.");
        }
        semantic_layer_uniform_upload_bytes +=
            backdrop_uniform_upload_bytes;
    }
    engine->semantic_destination_sampling_active =
        semantic_destination_sampling_active;

    if ((semantic_draw_count != 0U ||
            semantic_has_materialized_layers) &&
        !engine->semantic_render_bundle_valid) {
        WGPURenderBundleEncoderDescriptor bundle_descriptor{};
        bundle_descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU retained semantic mixed-scene bundle encoder");
        bundle_descriptor.colorFormatCount = 1U;
        bundle_descriptor.colorFormats = &engine->target_format;
        bundle_descriptor.depthStencilFormat = semantic_3d_draw_count != 0U
            ? WGPUTextureFormat_Depth24Plus
            : WGPUTextureFormat_Undefined;
        bundle_descriptor.sampleCount = 1U;
        std::vector<semantic_render_bundle_span> compiled_spans;
        std::vector<semantic_effect_dispatch> compiled_effect_dispatches;
        std::vector<std::byte> semantic_effect_uniform_data;
        std::vector<progpu::native::vector_vertex>
            semantic_layer_vertices;
        try {
            compiled_spans.reserve(header.command_count);
            compiled_effect_dispatches.reserve(semantic_effect_node_count);
            semantic_effect_uniform_data.resize(
                static_cast<std::size_t>(semantic_effect_uniform_bytes));
            semantic_layer_vertices.reserve(
                static_cast<std::size_t>(
                    semantic_materialized_layer_count +
                    semantic_advanced_layer_count * 2U +
                    (semantic_destination_sampling_active ? 1U : 0U) +
                    semantic_effected_backdrop_layer_count) * 4U);
        } catch (const std::bad_alloc&) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The retained semantic clip-span table could not be allocated.");
        }
        WGPURenderBundleEncoder bundle_encoder = nullptr;
        std::uint32_t semantic_effect_uniform_cursor = 0U;
        std::uint32_t active_bundle_draw_count = 0U;
        semantic_scissor active_scissor{};
        bool has_active_scissor = false;
        std::uint32_t active_target_layer =
            PROGPU_NATIVE_SCENE_NO_INDEX;
        std::uint32_t active_mask_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX;
        semantic_render_bundle_span active_mask{};
        enum class pending_draw_kind : std::uint8_t {
            none,
            analytic,
            path,
            glyph,
        };
        pending_draw_kind pending_kind = pending_draw_kind::none;
        semantic_analytic_draw pending_analytic_draw{};
        semantic_path_draw pending_path_draw{};
        semantic_glyph_draw pending_glyph_draw{};
        const auto release_mask_resources = [](
            semantic_render_bundle_span& span) noexcept {
            if (span.mask_bind_group != nullptr) {
                wgpuBindGroupRelease(span.mask_bind_group);
                span.mask_bind_group = nullptr;
            }
            if (span.mask_chain_bind_group != nullptr) {
                wgpuBindGroupRelease(span.mask_chain_bind_group);
                span.mask_chain_bind_group = nullptr;
            }
            if (span.mask_uniform_buffer != nullptr) {
                wgpuBufferDestroy(span.mask_uniform_buffer);
                wgpuBufferRelease(span.mask_uniform_buffer);
                span.mask_uniform_buffer = nullptr;
            }
            if (span.mask_chain_uniform_buffer != nullptr) {
                wgpuBufferDestroy(span.mask_chain_uniform_buffer);
                wgpuBufferRelease(span.mask_chain_uniform_buffer);
                span.mask_chain_uniform_buffer = nullptr;
            }
            if (span.mask_texture_view != nullptr) {
                wgpuTextureViewRelease(span.mask_texture_view);
                span.mask_texture_view = nullptr;
            }
            if (span.mask_texture != nullptr) {
                wgpuTextureDestroy(span.mask_texture);
                wgpuTextureRelease(span.mask_texture);
                span.mask_texture = nullptr;
            }
        };
        const auto release_compiled_spans = [&]() noexcept {
            for (auto& span : compiled_spans) {
                release_mask_resources(span);
                if (span.advanced_blend_bind_group != nullptr) {
                    wgpuBindGroupRelease(span.advanced_blend_bind_group);
                    span.advanced_blend_bind_group = nullptr;
                }
                if (span.bundle != nullptr) {
                    wgpuRenderBundleRelease(span.bundle);
                    span.bundle = nullptr;
                }
            }
            compiled_spans.clear();
        };
        const auto fail_bundle = [&](progpu_native_status status) noexcept {
            if (bundle_encoder != nullptr) {
                wgpuRenderBundleEncoderRelease(bundle_encoder);
                bundle_encoder = nullptr;
            }
            release_mask_resources(active_mask);
            release_compiled_spans();
            discard_encoder();
            return status;
        };
        const auto flush_pending_draw = [&]() {
            progpu_native_status status = PROGPU_NATIVE_STATUS_SUCCESS;
            switch (pending_kind) {
                case pending_draw_kind::analytic:
                    status = encode_semantic_analytic_bundle_draw(
                        *engine,
                        bundle_encoder,
                        pending_analytic_draw,
                        active_target_layer,
                        active_mask.mask_bind_group,
                        active_mask.mask_chain_bind_group);
                    break;
                case pending_draw_kind::path:
                    status = encode_semantic_path_bundle_draw(
                        *engine,
                        bundle_encoder,
                        pending_path_draw,
                        active_target_layer,
                        active_mask.mask_bind_group,
                        active_mask.mask_chain_bind_group);
                    break;
                case pending_draw_kind::glyph:
                    status = encode_semantic_glyph_bundle_draw(
                        *engine,
                        bundle_encoder,
                        pending_glyph_draw,
                        active_target_layer,
                        active_mask.mask_bind_group,
                        active_mask.mask_chain_bind_group);
                    break;
                case pending_draw_kind::none:
                    return PROGPU_NATIVE_STATUS_SUCCESS;
            }
            if (status == PROGPU_NATIVE_STATUS_SUCCESS) {
                ++draw_calls;
                ++active_bundle_draw_count;
                pending_kind = pending_draw_kind::none;
            }
            return status;
        };
        const auto finish_active_bundle = [&]() {
            if (bundle_encoder == nullptr) {
                return PROGPU_NATIVE_STATUS_SUCCESS;
            }
            const auto flush_status = flush_pending_draw();
            if (flush_status != PROGPU_NATIVE_STATUS_SUCCESS) {
                return flush_status;
            }
            WGPURenderBundleDescriptor finish_descriptor{};
            finish_descriptor.label = progpu::native::webgpu::string_view(
                "ProGPU retained semantic clip-span bundle");
            WGPURenderBundle bundle = wgpuRenderBundleEncoderFinish(
                bundle_encoder,
                &finish_descriptor);
            wgpuRenderBundleEncoderRelease(bundle_encoder);
            bundle_encoder = nullptr;
            if (bundle == nullptr) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "A retained semantic clip-span bundle could not be finished.");
            }
            semantic_render_bundle_span operation{};
            operation.kind = semantic_replay_kind::bundle;
            operation.bundle = bundle;
            operation.clip_x = active_scissor.x;
            operation.clip_y = active_scissor.y;
            operation.clip_width = active_scissor.width;
            operation.clip_height = active_scissor.height;
            operation.target_layer = active_target_layer;
            operation.draw_call_count = active_bundle_draw_count;
            operation.mask_uniform_buffer =
                active_mask.mask_uniform_buffer;
            operation.mask_chain_uniform_buffer =
                active_mask.mask_chain_uniform_buffer;
            operation.mask_texture = active_mask.mask_texture;
            operation.mask_texture_view = active_mask.mask_texture_view;
            operation.mask_bind_group = active_mask.mask_bind_group;
            operation.mask_chain_bind_group =
                active_mask.mask_chain_bind_group;
            operation.mask_uniform_upload_bytes =
                active_mask.mask_uniform_upload_bytes;
            active_mask.mask_uniform_buffer = nullptr;
            active_mask.mask_chain_uniform_buffer = nullptr;
            active_mask.mask_texture = nullptr;
            active_mask.mask_texture_view = nullptr;
            active_mask.mask_bind_group = nullptr;
            active_mask.mask_chain_bind_group = nullptr;
            active_mask.mask_uniform_upload_bytes = 0U;
            compiled_spans.push_back(operation);
            active_bundle_draw_count = 0U;
            active_mask_resource_index =
                PROGPU_NATIVE_SCENE_NO_INDEX;
            return PROGPU_NATIVE_STATUS_SUCCESS;
        };
        const auto begin_active_bundle = [&](
            semantic_scissor scissor,
            std::uint32_t target_layer,
            std::uint32_t mask_resource_index,
            semantic_scissor target_extent) {
            bundle_encoder = wgpuDeviceCreateRenderBundleEncoder(
                engine->device,
                &bundle_descriptor);
            if (bundle_encoder == nullptr) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "A retained semantic clip-span encoder could not be created.");
            }
            active_scissor = scissor;
            active_target_layer = target_layer;
            active_mask_resource_index = mask_resource_index;
            active_bundle_draw_count = 0U;
            has_active_scissor = true;
            if (mask_resource_index !=
                PROGPU_NATIVE_SCENE_NO_INDEX) {
                const auto resource = read_resource(mask_resource_index);
                std::uint64_t mask_texture_upload_bytes = 0U;
                if (!create_semantic_layer_mask_binding(
                        *engine,
                        bytes,
                        resource,
                        target_extent,
                        frame->dpi_scale,
                        active_mask,
                        mask_texture_upload_bytes)) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "A retained per-draw semantic mask binding could not be prepared.");
                }
                texture_upload_bytes += mask_texture_upload_bytes;
                semantic_layer_mask_uniform_upload_bytes +=
                    active_mask.mask_uniform_upload_bytes;
                semantic_layer_uniform_upload_bytes +=
                    active_mask.mask_uniform_upload_bytes;
            }
            return PROGPU_NATIVE_STATUS_SUCCESS;
        };
        const auto append_effect_program = [&](
            std::uint32_t resource_index,
            semantic_render_bundle_span& operation) {
            if (resource_index == PROGPU_NATIVE_SCENE_NO_INDEX) {
                return;
            }
            const auto resource = read_resource(resource_index);
            progpu_native_scene_effect_chain chain{};
            std::memcpy(
                &chain,
                bytes + resource.payload_offset,
                sizeof(chain));
            std::array<progpu_native_group_effect,
                PROGPU_NATIVE_MAX_GROUP_EFFECTS> effects{};
            for (std::uint32_t effect_index = 0U;
                 effect_index < chain.effect_count;
                 ++effect_index) {
                std::memcpy(
                    &effects[effect_index],
                    bytes + resource.auxiliary_offset +
                        static_cast<std::size_t>(effect_index) *
                            sizeof(progpu_native_group_effect),
                    sizeof(progpu_native_group_effect));
            }
            const auto plan =
                progpu::native::effects::create_chain_plan(
                    effects.data(),
                    chain.effect_count);
            operation.first_effect_dispatch =
                static_cast<std::uint32_t>(
                    compiled_effect_dispatches.size());
            operation.effect_count = chain.effect_count;
            operation.final_effect_texture =
                plan[chain.effect_count - 1U].output;
            const auto append_effect_uniform = [&]<typename T>(
                const T& value) {
                const std::uint32_t offset =
                    semantic_effect_uniform_cursor;
                std::memcpy(
                    semantic_effect_uniform_data.data() + offset,
                    &value,
                    sizeof(value));
                semantic_effect_uniform_cursor +=
                    semantic_effect_uniform_alignment;
                return offset;
            };
            for (std::uint32_t effect_index = 0U;
                 effect_index < chain.effect_count;
                 ++effect_index) {
                const auto& effect = effects[effect_index];
                semantic_effect_dispatch dispatch{};
                dispatch.kind = effect.kind;
                dispatch.source_texture = plan[effect_index].source;
                dispatch.horizontal_texture =
                    plan[effect_index].horizontal;
                dispatch.vertical_texture = plan[effect_index].vertical;
                dispatch.output_texture = plan[effect_index].output;
                const auto create_blur = [frame](float sigma) {
                    gpu_gaussian_blur_params parameters{};
                    parameters.sigma = sigma * frame->dpi_scale;
                    parameters.radius =
                        static_cast<std::uint32_t>(std::clamp(
                            static_cast<int>(std::ceil(
                                parameters.sigma * 3.0F)),
                            0,
                            128));
                    return parameters;
                };
                const auto horizontal = create_blur(effect.sigma_x);
                const auto vertical = create_blur(effect.sigma_y);
                dispatch.horizontal_uniform_offset =
                    append_effect_uniform(horizontal);
                dispatch.vertical_uniform_offset =
                    append_effect_uniform(vertical);
                if (effect.kind ==
                    PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW) {
                    gpu_drop_shadow_params drop{};
                    drop.offset[0] = effect.offset_x * frame->dpi_scale;
                    drop.offset[1] = effect.offset_y * frame->dpi_scale;
                    drop.color[0] = effect.color_r;
                    drop.color[1] = effect.color_g;
                    drop.color[2] = effect.color_b;
                    drop.color[3] = effect.color_a;
                    dispatch.drop_shadow_uniform_offset =
                        append_effect_uniform(drop);
                }
                compiled_effect_dispatches.push_back(dispatch);
            }
        };

        semantic_state_cursor state_cursor(
            bytes, header, frame->dpi_scale);
        semantic_layer_target_cursor target_cursor(
            bytes,
            frame->width,
            frame->height,
            frame->dpi_scale);
        std::array<bool,
            PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH>
            layer_scope_materialized{};
        std::array<progpu_native_scene_layer,
            PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS>
            materialized_layers{};
        std::array<semantic_scissor,
            PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS>
            materialized_extents{};
        std::array<std::size_t,
            PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS>
            materialized_push_span_indices{};
        std::uint32_t layer_scope_depth = 0U;
        std::uint32_t materialized_depth = 0U;
        std::uint32_t advanced_operation_index = 0U;
        std::uint32_t current_target_layer =
            PROGPU_NATIVE_SCENE_NO_INDEX;
        for (std::uint32_t index = 0U;
             index < header.command_count;
             ++index) {
            const auto command = read_command(index);
            const auto state = state_cursor.advance(command);
            const auto target_extent = target_cursor.advance(command);
            if (command.kind ==
                PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) {
                auto layer = semantic_default_layer();
                if (command.payload_size != 0U) {
                    std::memcpy(
                        &layer,
                        bytes + command.payload_offset,
                        sizeof(layer));
                }
                const bool materialized =
                    progpu::native::scene::layer_requires_materialization(
                        layer);
                layer_scope_materialized[layer_scope_depth++] =
                    materialized;
                if (materialized) {
                    const auto finish_status = finish_active_bundle();
                    if (finish_status != PROGPU_NATIVE_STATUS_SUCCESS) {
                        return fail_bundle(finish_status);
                    }
                    const std::uint32_t parent_layer =
                        materialized_depth == 0U
                            ? PROGPU_NATIVE_SCENE_NO_INDEX
                            : materialized_depth - 1U;
                    const semantic_scissor parent_extent =
                        parent_layer == PROGPU_NATIVE_SCENE_NO_INDEX
                            ? semantic_scissor{
                                0U,
                                0U,
                                frame->width,
                                frame->height,
                                true}
                            : materialized_extents[parent_layer];
                    const std::uint32_t slot = materialized_depth;
                    materialized_layers[materialized_depth++] = layer;
                    materialized_extents[slot] = target_extent;
                    semantic_render_bundle_span operation{};
                    operation.kind = semantic_replay_kind::push_layer;
                    operation.target_layer = slot;
                    operation.source_layer = slot;
                    operation.parent_layer = parent_layer;
                    operation.operation_id = command.command_id;
                    operation.backdrop =
                        (layer.flags &
                            PROGPU_NATIVE_SCENE_LAYER_BACKDROP) != 0U;
                    if (operation.backdrop) {
                        operation.source_width = target_extent.width;
                        operation.source_height = target_extent.height;
                        operation.backdrop_source_x =
                            target_extent.x - parent_extent.x;
                        operation.backdrop_source_y =
                            target_extent.y - parent_extent.y;
                        append_effect_program(
                            layer.effect_resource_index,
                            operation);
                        if (operation.effect_count != 0U) {
                            operation.first_backdrop_resolve_vertex =
                                static_cast<std::uint32_t>(
                                    semantic_layer_vertices.size());
                            append_semantic_layer_quad(
                                semantic_layer_vertices,
                                target_extent,
                                target_extent,
                                layer_budget.slot_widths[slot],
                                layer_budget.slot_heights[slot],
                                frame->dpi_scale,
                                1.0F);
                        }
                        draw_calls += operation.effect_count == 0U
                            ? 0U
                            : 1U;
                    }
                    materialized_push_span_indices[slot] =
                        compiled_spans.size();
                    compiled_spans.push_back(operation);
                    current_target_layer = slot;
                    has_active_scissor = false;
                }
                continue;
            }
            if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) {
                const bool materialized =
                    layer_scope_materialized[--layer_scope_depth];
                if (materialized) {
                    const auto finish_status = finish_active_bundle();
                    if (finish_status != PROGPU_NATIVE_STATUS_SUCCESS) {
                        return fail_bundle(finish_status);
                    }
                    const std::uint32_t source_layer =
                        --materialized_depth;
                    const auto& layer = materialized_layers[source_layer];
                    const auto& source_extent =
                        materialized_extents[source_layer];
                    const std::uint32_t first_vertex =
                        static_cast<std::uint32_t>(
                            semantic_layer_vertices.size());
                    append_semantic_layer_quad(
                        semantic_layer_vertices,
                        source_extent,
                        target_extent,
                        layer_budget.slot_widths[source_layer],
                        layer_budget.slot_heights[source_layer],
                        frame->dpi_scale,
                        layer.opacity);
                    semantic_render_bundle_span operation{};
                    operation.kind = semantic_replay_kind::pop_layer;
                    operation.operation_id = command.command_id;
                    operation.target_layer = materialized_depth == 0U
                        ? PROGPU_NATIVE_SCENE_NO_INDEX
                        : materialized_depth - 1U;
                    operation.source_layer = source_layer;
                    operation.first_composite_vertex = first_vertex;
                    operation.blend_mode = layer.blend_mode;
                    operation.backdrop =
                        (layer.flags &
                            PROGPU_NATIVE_SCENE_LAYER_BACKDROP) != 0U;
                    const bool advanced_blend =
                        is_advanced_group_blend(layer.blend_mode);
                    if (!operation.backdrop) {
                        append_effect_program(
                            layer.effect_resource_index,
                            operation);
                    }
                    if (operation.effect_count != 0U &&
                        !operation.backdrop) {
                        auto& push_operation = compiled_spans[
                            materialized_push_span_indices[source_layer]];
                        push_operation.effect_cache_operation_id =
                            operation.operation_id;
                        push_operation.can_skip_content_on_effect_cache =
                            true;
                    }
                    if (layer.mask_resource_index !=
                            PROGPU_NATIVE_SCENE_NO_INDEX) {
                        const auto resource = read_resource(
                            layer.mask_resource_index);
                        std::uint64_t mask_texture_upload_bytes = 0U;
                        if (!create_semantic_layer_mask_binding(
                                *engine,
                                bytes,
                                resource,
                                target_extent,
                                frame->dpi_scale,
                                operation,
                                mask_texture_upload_bytes)) {
                            return fail_bundle(engine->fail(
                                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                                "A retained semantic layer-mask binding could not be prepared."));
                        }
                        texture_upload_bytes += mask_texture_upload_bytes;
                        semantic_layer_mask_uniform_upload_bytes +=
                            operation.mask_uniform_upload_bytes;
                        semantic_layer_uniform_upload_bytes +=
                            operation.mask_uniform_upload_bytes;
                    }
                    if (advanced_blend) {
                        operation.first_resolve_vertex =
                            static_cast<std::uint32_t>(
                                semantic_layer_vertices.size());
                        append_semantic_layer_quad(
                            semantic_layer_vertices,
                            source_extent,
                            source_extent,
                            layer_budget.slot_widths[source_layer],
                            layer_budget.slot_heights[source_layer],
                            frame->dpi_scale,
                            layer.opacity);
                        operation.target_width =
                            operation.target_layer ==
                                PROGPU_NATIVE_SCENE_NO_INDEX
                                ? frame->width
                                : layer_budget.slot_widths[
                                    operation.target_layer];
                        operation.target_height =
                            operation.target_layer ==
                                PROGPU_NATIVE_SCENE_NO_INDEX
                                ? frame->height
                                : layer_budget.slot_heights[
                                    operation.target_layer];
                        // WebGPU rejects zero-sized scissors. Empty semantic
                        // sources retain their canonical zero sampling extent,
                        // but resolve through the one-pixel bounded scratch
                        // allocation established by preflight.
                        operation.source_width =
                            std::max(source_extent.width, 1U);
                        operation.source_height =
                            std::max(source_extent.height, 1U);
                        operation.first_copy_vertex =
                            static_cast<std::uint32_t>(
                                semantic_layer_vertices.size());
                        const semantic_scissor full_target{
                            target_extent.x,
                            target_extent.y,
                            operation.target_width,
                            operation.target_height,
                            true};
                        append_semantic_layer_quad(
                            semantic_layer_vertices,
                            full_target,
                            full_target,
                            engine->semantic_advanced_output_slot.width,
                            engine->semantic_advanced_output_slot.height,
                            frame->dpi_scale,
                            1.0F);
                        operation.advanced_uniform_offset =
                            advanced_operation_index * 256U;
                        gpu_advanced_blend_sampling_uniforms sampling{};
                        sampling.source_origin[0] =
                            static_cast<float>(source_extent.x) -
                            static_cast<float>(target_extent.x);
                        sampling.source_origin[1] =
                            static_cast<float>(source_extent.y) -
                            static_cast<float>(target_extent.y);
                        sampling.source_extent[0] =
                            static_cast<float>(source_extent.width);
                        sampling.source_extent[1] =
                            static_cast<float>(source_extent.height);
                        sampling.blend_mode = layer.blend_mode;
                        WGPUTextureView destination_view =
                            operation.target_layer ==
                                PROGPU_NATIVE_SCENE_NO_INDEX
                                ? engine->semantic_root_slot.view
                                : engine->semantic_layer_slots[
                                    operation.target_layer].view;
                        if (!create_semantic_advanced_blend_binding(
                                *engine,
                                destination_view,
                                sampling,
                                operation)) {
                            return fail_bundle(engine->fail(
                                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                                "A retained semantic destination-aware blend binding could not be prepared."));
                        }
                        semantic_layer_uniform_upload_bytes +=
                            sizeof(sampling);
                        ++advanced_operation_index;
                    }
                    compiled_spans.push_back(operation);
                    current_target_layer = operation.target_layer;
                    has_active_scissor = false;
                    draw_calls += advanced_blend ? 3U : 1U;
                }
                continue;
            }
            if (command.kind <
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
                command.kind >
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH) {
                continue;
            }
            auto scissor = resolve_semantic_target_scissor(
                state,
                target_extent,
                frame->width,
                frame->height,
                frame->dpi_scale);
            if (semantic_partial_damage_active &&
                current_target_layer == PROGPU_NATIVE_SCENE_NO_INDEX) {
                scissor = intersect_semantic_scissors(
                    scissor,
                    semantic_frame_damage);
            }
            const std::uint32_t mask_resource_index =
                (state.flags & PROGPU_NATIVE_SCENE_STATE_MASK) != 0U
                ? state.mask_resource_index
                : PROGPU_NATIVE_SCENE_NO_INDEX;
            if (scissor.drawable &&
                (!has_active_scissor || scissor != active_scissor ||
                    current_target_layer != active_target_layer ||
                    mask_resource_index != active_mask_resource_index)) {
                const auto finish_status = finish_active_bundle();
                if (finish_status != PROGPU_NATIVE_STATUS_SUCCESS) {
                    return fail_bundle(finish_status);
                }
                const auto begin_status = begin_active_bundle(
                    scissor,
                    current_target_layer,
                    mask_resource_index,
                    target_extent);
                if (begin_status != PROGPU_NATIVE_STATUS_SUCCESS) {
                    return fail_bundle(begin_status);
                }
            }
            if (scissor.drawable) {
                note_family((command.kind ==
                            PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY ||
                        command.kind ==
                            PROGPU_NATIVE_SCENE_COMMAND_DRAW_POINT_BATCH ||
                        command.kind ==
                            PROGPU_NATIVE_SCENE_COMMAND_DRAW_VERTEX_MESH ||
                        command.kind ==
                            PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH)
                    ? static_cast<std::uint32_t>(
                        PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC)
                    : command.kind);
            }
            progpu_native_status status =
                PROGPU_NATIVE_STATUS_SUCCESS;
            switch (command.kind) {
                case PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC: {
                    if (semantic_analytic_draw_index >=
                        semantic_analytic_page.draws.size()) {
                        return fail_bundle(engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "The semantic analytic packed-page index is invalid."));
                    }
                    const auto draw_index = semantic_analytic_draw_index;
                    ++semantic_analytic_draw_index;
                    if (scissor.drawable) {
                        const auto& draw =
                            semantic_analytic_page.draws[draw_index];
                        if (pending_kind != pending_draw_kind::analytic ||
                            !try_merge_semantic_analytic_draw(
                                pending_analytic_draw,
                                draw)) {
                            status = flush_pending_draw();
                            if (status == PROGPU_NATIVE_STATUS_SUCCESS) {
                                pending_analytic_draw = draw;
                                pending_kind = pending_draw_kind::analytic;
                            }
                        }
                    }
                    break;
                }
                case PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY: {
                    if (semantic_analytic_draw_index >=
                        semantic_analytic_page.draws.size()) {
                        return fail_bundle(engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "The semantic geometry packed-page index is invalid."));
                    }
                    const auto draw_index = semantic_analytic_draw_index;
                    ++semantic_analytic_draw_index;
                    if (scissor.drawable) {
                        const auto& draw =
                            semantic_analytic_page.draws[draw_index];
                        if (pending_kind != pending_draw_kind::analytic ||
                            !try_merge_semantic_analytic_draw(
                                pending_analytic_draw,
                                draw)) {
                            status = flush_pending_draw();
                            if (status == PROGPU_NATIVE_STATUS_SUCCESS) {
                                pending_analytic_draw = draw;
                                pending_kind = pending_draw_kind::analytic;
                            }
                        }
                    }
                    break;
                }
                case PROGPU_NATIVE_SCENE_COMMAND_DRAW_POINT_BATCH: {
                    if (semantic_analytic_draw_index >=
                        semantic_analytic_page.draws.size()) {
                        return fail_bundle(engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "The semantic point-batch packed-page index is invalid."));
                    }
                    const auto draw_index = semantic_analytic_draw_index;
                    ++semantic_analytic_draw_index;
                    if (scissor.drawable) {
                        const auto& draw =
                            semantic_analytic_page.draws[draw_index];
                        if (pending_kind != pending_draw_kind::analytic ||
                            !try_merge_semantic_analytic_draw(
                                pending_analytic_draw,
                                draw)) {
                            status = flush_pending_draw();
                            if (status == PROGPU_NATIVE_STATUS_SUCCESS) {
                                pending_analytic_draw = draw;
                                pending_kind = pending_draw_kind::analytic;
                            }
                        }
                    }
                    break;
                }
                case PROGPU_NATIVE_SCENE_COMMAND_DRAW_VERTEX_MESH: {
                    if (semantic_analytic_draw_index >=
                        semantic_analytic_page.draws.size()) {
                        return fail_bundle(engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "The semantic vertex-mesh packed-page index is invalid."));
                    }
                    const auto draw_index = semantic_analytic_draw_index;
                    ++semantic_analytic_draw_index;
                    if (scissor.drawable) {
                        const auto& draw =
                            semantic_analytic_page.draws[draw_index];
                        if (pending_kind != pending_draw_kind::analytic ||
                            !try_merge_semantic_analytic_draw(
                                pending_analytic_draw,
                                draw)) {
                            status = flush_pending_draw();
                            if (status == PROGPU_NATIVE_STATUS_SUCCESS) {
                                pending_analytic_draw = draw;
                                pending_kind = pending_draw_kind::analytic;
                            }
                        }
                    }
                    break;
                }
                case PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH: {
                    if (semantic_analytic_draw_index >=
                        semantic_analytic_page.draws.size()) {
                        return fail_bundle(engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "The semantic stroke-batch packed-page index is invalid."));
                    }
                    const auto draw_index = semantic_analytic_draw_index;
                    ++semantic_analytic_draw_index;
                    if (scissor.drawable) {
                        const auto& draw =
                            semantic_analytic_page.draws[draw_index];
                        if (pending_kind != pending_draw_kind::analytic ||
                            !try_merge_semantic_analytic_draw(
                                pending_analytic_draw,
                                draw)) {
                            status = flush_pending_draw();
                            if (status == PROGPU_NATIVE_STATUS_SUCCESS) {
                                pending_analytic_draw = draw;
                                pending_kind = pending_draw_kind::analytic;
                            }
                        }
                    }
                    break;
                }
                case PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH: {
                    if (semantic_path_draw_index >=
                        semantic_path_page.draws.size()) {
                        return fail_bundle(engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "The semantic path packed-page index is invalid."));
                    }
                    const auto draw_index = semantic_path_draw_index;
                    ++semantic_path_draw_index;
                    if (scissor.drawable) {
                        const auto& draw = semantic_path_page.draws[draw_index];
                        if (pending_kind != pending_draw_kind::path ||
                            !try_merge_semantic_path_draw(
                                pending_path_draw,
                                draw)) {
                            status = flush_pending_draw();
                            if (status == PROGPU_NATIVE_STATUS_SUCCESS) {
                                pending_path_draw = draw;
                                pending_kind = pending_draw_kind::path;
                            }
                        }
                    }
                    break;
                }
                case PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN: {
                    if (semantic_glyph_draw_index >=
                        semantic_glyph_page.draws.size()) {
                        return fail_bundle(engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "The semantic glyph packed-page index is invalid."));
                    }
                    const auto draw_index = semantic_glyph_draw_index;
                    ++semantic_glyph_draw_index;
                    if (scissor.drawable) {
                        const auto& draw =
                            semantic_glyph_page.draws[draw_index];
                        if (pending_kind != pending_draw_kind::glyph ||
                            !try_merge_semantic_glyph_draw(
                                pending_glyph_draw,
                                draw)) {
                            status = flush_pending_draw();
                            if (status == PROGPU_NATIVE_STATUS_SUCCESS) {
                                pending_glyph_draw = draw;
                                pending_kind = pending_draw_kind::glyph;
                            }
                        }
                    }
                    break;
                }
                case PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE: {
                    if (semantic_image_draw_index >=
                        semantic_image_page.draws.size()) {
                        return fail_bundle(engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "The semantic image packed-page index is invalid."));
                    }
                    const auto draw_index = semantic_image_draw_index;
                    ++semantic_image_draw_index;
                    if (scissor.drawable) {
                        status = flush_pending_draw();
                        if (status == PROGPU_NATIVE_STATUS_SUCCESS) {
                            status = encode_semantic_image_bundle_draw(
                                    *engine,
                                    bundle_encoder,
                                    semantic_image_page.draws[draw_index],
                                    current_target_layer,
                                    active_mask.mask_bind_group,
                                    active_mask.mask_chain_bind_group);
                            if (status == PROGPU_NATIVE_STATUS_SUCCESS) {
                                ++draw_calls;
                                ++active_bundle_draw_count;
                            }
                        }
                    }
                    break;
                }
                case PROGPU_NATIVE_SCENE_COMMAND_DRAW_LINE_3D_BATCH:
                case PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH: {
                    if (semantic_3d_draw_index >=
                        engine->semantic_3d_cache.draws.size()) {
                        return fail_bundle(engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "The semantic retained 3D packed-page index is invalid."));
                    }
                    const auto draw_index = semantic_3d_draw_index++;
                    if (scissor.drawable) {
                        status = flush_pending_draw();
                        if (status == PROGPU_NATIVE_STATUS_SUCCESS) {
                            status = encode_semantic_3d_bundle_draw(
                                *engine,
                                bundle_encoder,
                                engine->semantic_3d_cache.draws[draw_index]);
                            if (status == PROGPU_NATIVE_STATUS_SUCCESS) {
                                ++draw_calls;
                                ++active_bundle_draw_count;
                            }
                        }
                    }
                    break;
                }
                default:
                    break;
            }
            if (status != PROGPU_NATIVE_STATUS_SUCCESS) {
                return fail_bundle(status);
            }
        }

        if (semantic_destination_sampling_active) {
            engine->semantic_root_copy_vertex =
                static_cast<std::uint32_t>(
                    semantic_layer_vertices.size());
            const semantic_scissor root_extent{
                0U, 0U, frame->width, frame->height, true};
            append_semantic_layer_quad(
                semantic_layer_vertices,
                root_extent,
                root_extent,
                engine->semantic_root_slot.width,
                engine->semantic_root_slot.height,
                frame->dpi_scale,
                1.0F);
            ++draw_calls;
        }

        if (semantic_analytic_draw_index !=
                semantic_analytic_draw_count ||
            semantic_path_draw_index != semantic_path_draw_count ||
            semantic_glyph_draw_index != semantic_glyph_draw_count ||
            semantic_image_draw_index != semantic_image_draw_count ||
            semantic_3d_draw_index != semantic_3d_draw_count) {
            return fail_bundle(engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "A semantic packed-page draw count is inconsistent."));
        }
        if (layer_scope_depth != 0U || materialized_depth != 0U ||
            semantic_layer_vertices.size() !=
                static_cast<std::size_t>(
                    semantic_materialized_layer_count +
                    semantic_advanced_layer_count * 2U +
                    (semantic_destination_sampling_active ? 1U : 0U) +
                    semantic_effected_backdrop_layer_count) * 4U ||
            advanced_operation_index != semantic_advanced_layer_count ||
            compiled_effect_dispatches.size() !=
                semantic_effect_node_count ||
            semantic_effect_uniform_cursor !=
                semantic_effect_uniform_bytes) {
            return fail_bundle(engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic isolated-layer replay program is inconsistent."));
        }

        const auto finish_status = finish_active_bundle();
        if (finish_status != PROGPU_NATIVE_STATUS_SUCCESS) {
            return fail_bundle(finish_status);
        }
        if (!semantic_layer_vertices.empty()) {
            const std::uint64_t layer_vertex_bytes =
                semantic_layer_vertices.size() *
                sizeof(progpu::native::vector_vertex);
            const std::uint64_t layer_vertex_hash = append_fnv1a64(
                14695981039346656037ULL,
                semantic_layer_vertices.data(),
                static_cast<std::size_t>(layer_vertex_bytes));
            if (engine->semantic_layer_vertex_content_hash !=
                    layer_vertex_hash ||
                engine->semantic_layer_vertex_content_bytes !=
                    layer_vertex_bytes) {
                wgpuQueueWriteBuffer(
                    engine->queue,
                    engine->semantic_layer_vertex_buffer,
                    0U,
                    semantic_layer_vertices.data(),
                    layer_vertex_bytes);
                engine->semantic_layer_vertex_content_hash =
                    layer_vertex_hash;
                engine->semantic_layer_vertex_content_bytes =
                    layer_vertex_bytes;
                vertex_upload_bytes += layer_vertex_bytes;
                semantic_layer_vertex_upload_bytes = layer_vertex_bytes;
            }
        }
        if (!semantic_effect_uniform_data.empty()) {
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->semantic_effect_uniform_buffer,
                0U,
                semantic_effect_uniform_data.data(),
                semantic_effect_uniform_data.size());
            semantic_layer_effect_uniform_upload_bytes =
                semantic_effect_uniform_data.size();
            semantic_layer_uniform_upload_bytes +=
                semantic_effect_uniform_data.size();
        }
        engine->semantic_render_bundle_spans = std::move(compiled_spans);
        engine->semantic_effect_dispatches =
            std::move(compiled_effect_dispatches);
        engine->semantic_render_bundle_valid = true;
        engine->semantic_render_bundle_scene_hash =
            semantic_bundle_replay_hash;
        engine->semantic_render_bundle_dpi_scale = frame->dpi_scale;
        engine->semantic_render_bundle_width = frame->width;
        engine->semantic_render_bundle_height = frame->height;
        engine->semantic_render_bundle_draw_call_count = draw_calls;
        engine->semantic_render_bundle_family_switch_count =
            family_switches;
    } else if (semantic_draw_count != 0U ||
        semantic_has_materialized_layers) {
        draw_calls = engine->semantic_render_bundle_draw_call_count;
        family_switches =
            engine->semantic_render_bundle_family_switch_count;
    }
    uniform_upload_bytes += semantic_layer_uniform_upload_bytes;
    if (engine->semantic_destination_sampling_active !=
            semantic_destination_sampling_active) {
        discard_encoder();
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "Semantic destination sampling changed during bundle compilation.");
    }

    WGPURenderPassEncoder pass = nullptr;
    std::uint32_t executed_draw_calls = 0U;
    std::uint32_t semantic_layer_content_pass_count = 0U;
    if (semantic_draw_count != 0U &&
        !semantic_has_materialized_layers) {
        WGPURenderPassColorAttachment color_attachment{};
        progpu::native::webgpu::initialize_color_attachment(
            color_attachment);
        color_attachment.view = reinterpret_cast<WGPUTextureView>(
            frame->target_view);
        color_attachment.loadOp = semantic_preserve_target_active
            ? WGPULoadOp_Load
            : WGPULoadOp_Clear;
        color_attachment.storeOp = WGPUStoreOp_Store;
        color_attachment.clearValue = WGPUColor{
            frame->clear_color.r,
            frame->clear_color.g,
            frame->clear_color.b,
            frame->clear_color.a};
        WGPURenderPassDescriptor pass_descriptor{};
        pass_descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU retained semantic bundle replay pass");
        pass_descriptor.colorAttachmentCount = 1U;
        pass_descriptor.colorAttachments = &color_attachment;
        WGPURenderPassDepthStencilAttachment depth_attachment{};
        if (semantic_3d_draw_count != 0U) {
            depth_attachment.view = engine->semantic_root_slot.depth_view;
            depth_attachment.depthLoadOp = WGPULoadOp_Clear;
            depth_attachment.depthStoreOp = WGPUStoreOp_Store;
            depth_attachment.depthClearValue = 1.0F;
            depth_attachment.stencilLoadOp = WGPULoadOp_Undefined;
            depth_attachment.stencilStoreOp = WGPUStoreOp_Undefined;
            pass_descriptor.depthStencilAttachment = &depth_attachment;
        }
        pass = wgpuCommandEncoderBeginRenderPass(
            engine->semantic_encoder,
            &pass_descriptor);
        if (pass == nullptr) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic bundle replay pass could not be created.");
        }
        for (const auto& span : engine->semantic_render_bundle_spans) {
            wgpuRenderPassEncoderSetScissorRect(
                pass,
                span.clip_x,
                span.clip_y,
                span.clip_width,
                span.clip_height);
            wgpuRenderPassEncoderExecuteBundles(
                pass, 1U, &span.bundle);
            executed_draw_calls += span.draw_call_count;
        }
        wgpuRenderPassEncoderEnd(pass);
        wgpuRenderPassEncoderRelease(pass);
    } else if (semantic_has_materialized_layers) {
        std::uint32_t active_target_layer =
            PROGPU_NATIVE_SCENE_NO_INDEX;
        std::uint32_t skipped_cached_depth = 0U;
        std::array<bool,
            PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS>
            cached_layer_replay{};
        const auto finish_pass = [&]() noexcept {
            if (pass != nullptr) {
                wgpuRenderPassEncoderEnd(pass);
                wgpuRenderPassEncoderRelease(pass);
                pass = nullptr;
            }
        };
        const auto target_view = [&](std::uint32_t target_layer) {
            if (target_layer == PROGPU_NATIVE_SCENE_NO_INDEX) {
                return engine->semantic_destination_sampling_active
                    ? engine->semantic_root_slot.view
                    : reinterpret_cast<WGPUTextureView>(
                        frame->target_view);
            }
            return target_layer < engine->semantic_layer_slots.size()
                ? engine->semantic_layer_slots[target_layer].view
                : nullptr;
        };
        const auto target_depth_view = [&](std::uint32_t target_layer) {
            if (semantic_3d_draw_count == 0U) {
                return static_cast<WGPUTextureView>(nullptr);
            }
            if (target_layer == PROGPU_NATIVE_SCENE_NO_INDEX) {
                return engine->semantic_root_slot.depth_view;
            }
            return target_layer < engine->semantic_layer_slots.size()
                ? engine->semantic_layer_slots[target_layer].depth_view
                : nullptr;
        };
        const auto begin_pass = [&](
            std::uint32_t target_layer,
            WGPULoadOp load_op) {
            WGPUTextureView view = target_view(target_layer);
            if (view == nullptr) {
                return false;
            }
            WGPURenderPassColorAttachment color_attachment{};
            progpu::native::webgpu::initialize_color_attachment(
                color_attachment);
            color_attachment.view = view;
            color_attachment.loadOp = load_op;
            color_attachment.storeOp = WGPUStoreOp_Store;
            color_attachment.clearValue = target_layer ==
                    PROGPU_NATIVE_SCENE_NO_INDEX
                ? WGPUColor{
                    frame->clear_color.r,
                    frame->clear_color.g,
                    frame->clear_color.b,
                    frame->clear_color.a}
                : WGPUColor{0.0, 0.0, 0.0, 0.0};
            WGPURenderPassDescriptor pass_descriptor{};
            pass_descriptor.label = progpu::native::webgpu::string_view(
                "ProGPU retained semantic isolated-layer replay pass");
            pass_descriptor.colorAttachmentCount = 1U;
            pass_descriptor.colorAttachments = &color_attachment;
            WGPURenderPassDepthStencilAttachment depth_attachment{};
            depth_attachment.view = target_depth_view(target_layer);
            if (depth_attachment.view != nullptr) {
                depth_attachment.depthLoadOp = load_op;
                depth_attachment.depthStoreOp = WGPUStoreOp_Store;
                depth_attachment.depthClearValue = 1.0F;
                depth_attachment.stencilLoadOp = WGPULoadOp_Undefined;
                depth_attachment.stencilStoreOp = WGPUStoreOp_Undefined;
                pass_descriptor.depthStencilAttachment = &depth_attachment;
            }
            pass = wgpuCommandEncoderBeginRenderPass(
                engine->semantic_encoder,
                &pass_descriptor);
            active_target_layer = target_layer;
            return pass != nullptr;
        };
        const auto fail_replay = [&](const char* message) {
            finish_pass();
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                message);
        };

        if (!begin_pass(
                PROGPU_NATIVE_SCENE_NO_INDEX,
                WGPULoadOp_Clear)) {
            return fail_replay(
                "The semantic isolated-layer root pass could not be created.");
        }
        for (const auto& operation :
             engine->semantic_render_bundle_spans) {
            if (skipped_cached_depth != 0U) {
                if (operation.kind == semantic_replay_kind::push_layer) {
                    ++skipped_cached_depth;
                    continue;
                }
                if (operation.kind != semantic_replay_kind::pop_layer) {
                    continue;
                }
                --skipped_cached_depth;
                if (skipped_cached_depth != 0U) {
                    continue;
                }
            }
            if (operation.kind == semantic_replay_kind::push_layer) {
                if (operation.can_skip_content_on_effect_cache &&
                    operation.source_layer <
                        engine->semantic_layer_slots.size()) {
                    const auto& slot = engine->semantic_layer_slots[
                        operation.source_layer];
                    const progpu::native::effects::semantic_output_cache_key
                        cache_key{
                            engine->semantic_scene_hash,
                            operation.effect_cache_operation_id,
                            slot.effect_generation,
                            slot.effect_width,
                            slot.effect_height};
                    if (progpu::native::effects::semantic_output_cache_hit(
                            semantic_effect_working_caches[
                                operation.source_layer],
                            cache_key)) {
                        cached_layer_replay[operation.source_layer] = true;
                        ++semantic_effect_cache_hit_count;
                        skipped_cached_depth = 1U;
                        continue;
                    }
                }
                finish_pass();
                if (operation.backdrop) {
                    if (operation.effect_count != 0U) {
                        ++semantic_effect_operation_count;
                    }
                    if (!encode_semantic_backdrop_capture(
                            *engine,
                            engine->semantic_encoder,
                            operation,
                            semantic_layer_effect_pass_count)) {
                        return fail_replay(
                            "A semantic backdrop capture could not be encoded.");
                    }
                    executed_draw_calls +=
                        operation.effect_count == 0U ? 0U : 1U;
                }
                if (!begin_pass(
                        operation.target_layer,
                        operation.backdrop
                            ? WGPULoadOp_Load
                            : WGPULoadOp_Clear)) {
                    return fail_replay(
                        "A semantic isolated-layer content pass could not be created.");
                }
                ++semantic_layer_content_pass_count;
                continue;
            }
            if (operation.kind == semantic_replay_kind::pop_layer) {
                const bool content_cached =
                    operation.source_layer < cached_layer_replay.size() &&
                    cached_layer_replay[operation.source_layer];
                if (operation.source_layer < cached_layer_replay.size()) {
                    cached_layer_replay[operation.source_layer] = false;
                }
                const bool advanced_blend =
                    is_advanced_group_blend(operation.blend_mode);
                if (!content_cached || advanced_blend) {
                    finish_pass();
                }
                bool effect_ready = true;
                if (operation.effect_count != 0U) {
                    ++semantic_effect_operation_count;
                    if (operation.source_layer >=
                            engine->semantic_layer_slots.size()) {
                        return fail_replay(
                            "A semantic effect layer index is invalid.");
                    }
                    const auto& slot = engine->semantic_layer_slots[
                        operation.source_layer];
                    const progpu::native::effects::semantic_output_cache_key
                        cache_key{
                            engine->semantic_scene_hash,
                            operation.operation_id,
                            slot.effect_generation,
                            slot.effect_width,
                            slot.effect_height};
                    if (content_cached) {
                        // Push-layer cache lookup already proved that the
                        // immutable effect output belongs to this exact scene,
                        // operation, texture generation, and extent.
                    } else if (progpu::native::effects::semantic_output_cache_hit(
                            semantic_effect_working_caches[
                                operation.source_layer],
                            cache_key)) {
                        ++semantic_effect_cache_hit_count;
                    } else {
                        effect_ready = encode_semantic_effect_chain(
                            *engine,
                            engine->semantic_encoder,
                            operation,
                            semantic_layer_effect_pass_count);
                        if (effect_ready) {
                            progpu::native::effects::
                                commit_semantic_output_cache(
                                    semantic_effect_working_caches[
                                        operation.source_layer],
                                    cache_key);
                            semantic_effect_cache_updates[
                                operation.source_layer] = true;
                        }
                    }
                }
                const bool composite_ready = effect_ready &&
                    (advanced_blend
                        ? ([&]() {
                            WGPUBindGroup parent_uniform_group =
                                operation.target_layer ==
                                    PROGPU_NATIVE_SCENE_NO_INDEX
                                    ? engine->semantic_root_slot
                                        .image_uniform_bind_group
                                    : engine->semantic_layer_slots[
                                        operation.target_layer]
                                        .image_uniform_bind_group;
                            return encode_semantic_advanced_blend(
                                    *engine,
                                    engine->semantic_encoder,
                                    target_view(operation.target_layer),
                                    parent_uniform_group,
                                    operation) &&
                                begin_pass(
                                    operation.target_layer,
                                    WGPULoadOp_Load);
                        })()
                        : (((pass != nullptr) || begin_pass(
                                  operation.target_layer,
                                  WGPULoadOp_Load)) &&
                            encode_semantic_layer_composite(
                                *engine,
                                pass,
                                operation)));
                if (!composite_ready) {
                    return fail_replay(
                        "A semantic isolated-layer composite pass could not be encoded.");
                }
                executed_draw_calls += advanced_blend ? 3U : 1U;
                continue;
            }
            if (operation.target_layer != active_target_layer) {
                finish_pass();
                if (!begin_pass(
                        operation.target_layer,
                        WGPULoadOp_Load)) {
                    return fail_replay(
                        "A semantic isolated-layer continuation pass could not be created.");
                }
            }
            wgpuRenderPassEncoderSetScissorRect(
                pass,
                operation.clip_x,
                operation.clip_y,
                operation.clip_width,
                operation.clip_height);
            wgpuRenderPassEncoderExecuteBundles(
                pass,
                1U,
                &operation.bundle);
            executed_draw_calls += operation.draw_call_count;
        }
        finish_pass();
        if (engine->semantic_destination_sampling_active &&
            !encode_semantic_root_copy(
                *engine,
                engine->semantic_encoder,
                reinterpret_cast<WGPUTextureView>(frame->target_view),
                engine->semantic_root_copy_vertex)) {
            return fail_replay(
                "The semantic destination-aware root copy could not be encoded.");
        }
        executed_draw_calls +=
            engine->semantic_destination_sampling_active ? 1U : 0U;
    }

    draw_calls = executed_draw_calls;

    const auto flush_status = flush_encoder();
    if (flush_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        engine->semantic_load_target = false;
        return flush_status;
    }
    for (std::size_t index = 0U;
         index < semantic_effect_cache_updates.size();
         ++index) {
        if (semantic_effect_cache_updates[index]) {
            engine->semantic_layer_slots[index].effect_output_cache =
                semantic_effect_working_caches[index];
        }
    }

    if (semantic_draw_count == 0U &&
        !semantic_has_materialized_layers) {
        progpu_native_analytic_frame clear{};
        clear.struct_size = sizeof(clear);
        clear.width = frame->width;
        clear.height = frame->height;
        clear.dpi_scale = frame->dpi_scale;
        clear.target_view = frame->target_view;
        clear.clear_color = frame->clear_color;
        progpu_native_analytic_frame_metrics clear_metrics{};
        clear_metrics.struct_size = sizeof(clear_metrics);
        const auto status = progpu_native_engine_render_analytic(
            engine, &clear, &clear_metrics);
        if (status != PROGPU_NATIVE_STATUS_SUCCESS) {
            return status;
        }
        uniform_upload_bytes += clear_metrics.uniform_upload_bytes;
    }

    if (semantic_has_materialized_layers || semantic_has_state_masks) {
        engine->last_layer_metrics = {};
        engine->last_layer_metrics.struct_size =
            sizeof(progpu_native_layer_metrics);
        std::uint32_t texture_generation = 0U;
        std::uint32_t effect_texture_generation = 0U;
        for (std::uint32_t index = 0U;
             index < layer_budget.peak_materialized_depth;
             ++index) {
            texture_generation = std::max(
                texture_generation,
                engine->semantic_layer_slots[index].generation);
            effect_texture_generation = std::max(
                effect_texture_generation,
                engine->semantic_layer_slots[index].effect_generation);
        }
        engine->last_layer_metrics.texture_width =
            layer_budget.maximum_width();
        engine->last_layer_metrics.texture_height =
            layer_budget.maximum_height();
        engine->last_layer_metrics.texture_generation = texture_generation;
        engine->last_layer_metrics.allocation_count =
            engine->semantic_layer_allocation_count;
        engine->last_layer_metrics.content_pass_count =
            semantic_layer_content_pass_count;
        engine->last_layer_metrics.composite_pass_count =
            semantic_materialized_layer_count;
        engine->last_layer_metrics.cache_hit =
            semantic_render_bundle_hit ? 1U : 0U;
        engine->last_layer_metrics.texture_bytes =
            layer_budget.pooled_bytes() +
            semantic_destination_texture_bytes;
        engine->last_layer_metrics.vertex_upload_bytes =
            semantic_layer_vertex_upload_bytes;
        engine->last_layer_metrics.uniform_upload_bytes =
            semantic_layer_uniform_upload_bytes;
        engine->last_layer_metrics.mask_kind = semantic_layer_mask_kind;
        engine->last_layer_metrics.mask_bind_group_generation =
            engine->layer_mask_bind_group_generation;
        engine->last_layer_metrics.mask_uniform_upload_bytes =
            semantic_layer_mask_uniform_upload_bytes;
        engine->last_layer_metrics.effect_kind =
            semantic_has_layer_effects
                ? semantic_has_drop_shadows
                    ? PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW
                    : PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR
                : PROGPU_NATIVE_GROUP_EFFECT_NONE;
        engine->last_layer_metrics.effect_revision =
            semantic_has_layer_effects
                ? semantic_effect_chain_revision
                : 0U;
        engine->last_layer_metrics.effect_pass_count =
            semantic_layer_effect_pass_count;
        engine->last_layer_metrics.effect_texture_generation =
            semantic_has_layer_effects ? effect_texture_generation : 0U;
        engine->last_layer_metrics.effect_allocation_count =
            semantic_has_layer_effects
                ? engine->semantic_effect_allocation_count
                : 0U;
        engine->last_layer_metrics.effect_cache_hit =
            semantic_effect_operation_count != 0U &&
                semantic_effect_cache_hit_count ==
                    semantic_effect_operation_count
            ? 1U
            : 0U;
        engine->last_layer_metrics.effect_texture_bytes =
            pooled_effect_bytes;
        engine->last_layer_metrics.effect_uniform_upload_bytes =
            semantic_layer_effect_uniform_upload_bytes;
        engine->last_layer_metrics.effect_count =
            semantic_effect_node_count;
        engine->last_layer_metrics.effect_chain_revision =
            semantic_has_layer_effects
                ? semantic_effect_chain_revision
                : 0U;
        engine->last_layer_metrics.blend_mode =
            PROGPU_NATIVE_BLEND_SRC_OVER;
    }

    engine->last_error.clear();
    if (metrics != nullptr &&
        metrics->struct_size >= legacy_metrics_size) {
        metrics->command_count = header.command_count;
        metrics->draw_call_count = draw_calls;
        metrics->family_switch_count = family_switches;
        metrics->submission_count =
            engine->submission_count - submission_start;
        metrics->vertex_upload_bytes = vertex_upload_bytes;
        metrics->index_upload_bytes = index_upload_bytes;
        metrics->texture_upload_bytes = texture_upload_bytes;
        metrics->uniform_upload_bytes = uniform_upload_bytes;
        metrics->coverage_staging_bytes = coverage_staging_bytes;
        metrics->payload_hash = payload_hash;
        if (metrics->struct_size >= offsetof(
                progpu_native_scene_frame_metrics,
                brush_upload_bytes) + sizeof(std::uint64_t)) {
            metrics->brush_upload_bytes =
                semantic_brush_upload_bytes;
        }
        if (metrics->struct_size >= offsetof(
                progpu_native_scene_frame_metrics,
                gradient_stop_upload_bytes) + sizeof(std::uint64_t)) {
            metrics->gradient_stop_upload_bytes =
                semantic_gradient_stop_upload_bytes;
        }
        if (metrics->struct_size >= offsetof(
                progpu_native_scene_frame_metrics,
                text_style_upload_bytes) + sizeof(std::uint64_t)) {
            metrics->text_style_upload_bytes =
                semantic_text_style_upload_bytes;
        }
        if (metrics->struct_size >= offsetof(
                progpu_native_scene_frame_metrics,
                color_glyph_upload_bytes) + sizeof(std::uint64_t)) {
            metrics->color_glyph_upload_bytes =
                semantic_color_glyph_upload_bytes;
        }
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

} // namespace progpu::native::execution
