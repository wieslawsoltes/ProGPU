#include "progpu_native_frame_execution_common.hpp"

namespace progpu::native::execution {

struct semantic_render_bundle_commands final {
    using encoder_type = WGPURenderBundleEncoder;

    static void set_pipeline(
        encoder_type encoder,
        WGPURenderPipeline pipeline) noexcept {
        wgpuRenderBundleEncoderSetPipeline(encoder, pipeline);
    }

    static void set_bind_group(
        encoder_type encoder,
        std::uint32_t index,
        WGPUBindGroup bind_group) noexcept {
        wgpuRenderBundleEncoderSetBindGroup(
            encoder, index, bind_group, 0U, nullptr);
    }

    static void set_vertex_buffer(
        encoder_type encoder,
        WGPUBuffer buffer,
        std::uint64_t size) noexcept {
        wgpuRenderBundleEncoderSetVertexBuffer(
            encoder, 0U, buffer, 0U, size);
    }

    static void set_index_buffer(
        encoder_type encoder,
        WGPUBuffer buffer,
        std::uint64_t size) noexcept {
        wgpuRenderBundleEncoderSetIndexBuffer(
            encoder, buffer, WGPUIndexFormat_Uint32, 0U, size);
    }

    static void draw(
        encoder_type encoder,
        std::uint32_t vertex_count,
        std::uint32_t instance_count,
        std::uint32_t first_vertex,
        std::uint32_t first_instance) noexcept {
        wgpuRenderBundleEncoderDraw(
            encoder,
            vertex_count,
            instance_count,
            first_vertex,
            first_instance);
    }

    static void draw_indexed(
        encoder_type encoder,
        std::uint32_t index_count,
        std::uint32_t first_index,
        std::int32_t base_vertex) noexcept {
        wgpuRenderBundleEncoderDrawIndexed(
            encoder, index_count, 1U, first_index, base_vertex, 0U);
    }
};

WGPUBindGroup select_semantic_analytic_uniform_bind_group(
    progpu_native_engine& engine,
    std::uint32_t target_layer) noexcept {
    return target_layer == PROGPU_NATIVE_SCENE_NO_INDEX
        ? engine.semantic_destination_sampling_active
            ? engine.semantic_root_slot.analytic_uniform_bind_group
            : engine.analytic_uniform_bind_group
        : target_layer < engine.semantic_layer_slots.size()
            ? engine.semantic_layer_slots[target_layer]
                .analytic_uniform_bind_group
            : nullptr;
}

WGPUBindGroup select_semantic_text_uniform_bind_group(
    progpu_native_engine& engine,
    std::uint32_t target_layer) noexcept {
    return target_layer == PROGPU_NATIVE_SCENE_NO_INDEX
        ? engine.semantic_destination_sampling_active
            ? engine.semantic_root_slot.text_uniform_bind_group
            : engine.text_uniform_bind_group
        : target_layer < engine.semantic_layer_slots.size()
            ? engine.semantic_layer_slots[target_layer]
                .text_uniform_bind_group
            : nullptr;
}

WGPUBindGroup select_semantic_image_uniform_bind_group(
    progpu_native_engine& engine,
    std::uint32_t target_layer) noexcept {
    return target_layer == PROGPU_NATIVE_SCENE_NO_INDEX
        ? engine.semantic_destination_sampling_active
            ? engine.semantic_root_slot.image_uniform_bind_group
            : engine.image_uniform_bind_group
        : target_layer < engine.semantic_layer_slots.size()
            ? engine.semantic_layer_slots[target_layer]
                .image_uniform_bind_group
            : nullptr;
}

template<typename Commands>
progpu_native_status encode_semantic_analytic_draw(
    progpu_native_engine& engine,
    typename Commands::encoder_type encoder,
    const semantic_analytic_draw& draw,
    std::uint32_t target_layer) {
    auto& page = engine.semantic_analytic_cache;
    WGPUBindGroup uniform_group =
        select_semantic_analytic_uniform_bind_group(
            engine,
            target_layer);
    if (!page.cache_valid || page.vertex_buffer == nullptr ||
        page.index_buffer == nullptr || encoder == nullptr ||
        uniform_group == nullptr || draw.vertex_count == 0U ||
        draw.index_count == 0U ||
        draw.vertex_offset_bytes >= page.vertex_bytes ||
        draw.index_offset_bytes >= page.index_bytes ||
        draw.vertex_count >
            (page.vertex_bytes - draw.vertex_offset_bytes) /
                sizeof(progpu::native::vector_vertex) ||
        draw.index_count >
            (page.index_bytes - draw.index_offset_bytes) /
                sizeof(std::uint32_t)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic analytic packed page is incomplete.");
    }
    if (engine.analytic_pipeline == nullptr &&
        !create_analytic_pipeline(engine)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic analytic WebGPU pipeline could not be created.");
    }

    Commands::set_pipeline(encoder, engine.analytic_pipeline);
    Commands::set_bind_group(
        encoder, 0U, uniform_group);
    Commands::set_bind_group(
        encoder, 1U, engine.analytic_atlas_bind_group);
    Commands::set_vertex_buffer(
        encoder, page.vertex_buffer, page.vertex_bytes);
    Commands::set_index_buffer(
        encoder, page.index_buffer, page.index_bytes);
    Commands::draw_indexed(
        encoder,
        draw.index_count,
        static_cast<std::uint32_t>(
            draw.index_offset_bytes / sizeof(std::uint32_t)),
        0);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

template<typename Commands>
progpu_native_status encode_semantic_path_draw(
    progpu_native_engine& engine,
    typename Commands::encoder_type encoder,
    const semantic_path_draw& draw,
    std::uint32_t target_layer) {
    const std::uint64_t vertex_bytes = engine.path_vertices.size() *
        sizeof(progpu::native::vector_vertex);
    const std::uint64_t index_bytes = engine.path_indices.size() *
        sizeof(std::uint32_t);
    WGPUBindGroup uniform_group =
        select_semantic_analytic_uniform_bind_group(
            engine,
            target_layer);
    if (!engine.semantic_path_cache.cache_valid ||
        !engine.path_cache_valid || !engine.path_gpu_cache_valid ||
        engine.path_vertex_buffer == nullptr ||
        engine.path_index_buffer == nullptr ||
        engine.path_atlas_bind_group == nullptr ||
        encoder == nullptr || uniform_group == nullptr ||
        draw.index_count == 0U ||
        draw.first_index > engine.path_indices.size() ||
        draw.index_count >
            engine.path_indices.size() - draw.first_index) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic path packed page is incomplete.");
    }
    Commands::set_pipeline(encoder, engine.analytic_pipeline);
    Commands::set_bind_group(
        encoder, 0U, uniform_group);
    Commands::set_bind_group(
        encoder, 1U, engine.path_atlas_bind_group);
    Commands::set_vertex_buffer(
        encoder, engine.path_vertex_buffer, vertex_bytes);
    Commands::set_index_buffer(
        encoder, engine.path_index_buffer, index_bytes);
    Commands::draw_indexed(
        encoder, draw.index_count, draw.first_index, 0);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

template<typename Commands>
progpu_native_status encode_semantic_glyph_draw(
    progpu_native_engine& engine,
    typename Commands::encoder_type encoder,
    const semantic_glyph_draw& draw,
    std::uint32_t target_layer) {
    const std::uint64_t instance_bytes = engine.glyph_instances.size() *
        sizeof(gpu_glyph_instance);
    WGPUBindGroup uniform_group =
        select_semantic_text_uniform_bind_group(
            engine,
            target_layer);
    if (!engine.semantic_glyph_cache.cache_valid ||
        !engine.glyph_cache_valid || !engine.glyph_gpu_cache_valid ||
        engine.text_vertex_buffer == nullptr ||
        engine.text_atlas_bind_group == nullptr ||
        encoder == nullptr || uniform_group == nullptr ||
        draw.instance_count == 0U ||
        draw.first_instance > engine.glyph_instances.size() ||
        draw.instance_count >
            engine.glyph_instances.size() - draw.first_instance) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic glyph packed page is incomplete.");
    }
    Commands::set_pipeline(encoder, engine.text_pipeline);
    Commands::set_bind_group(
        encoder, 0U, uniform_group);
    Commands::set_bind_group(
        encoder, 1U, engine.text_atlas_bind_group);
    Commands::set_vertex_buffer(
        encoder, engine.text_vertex_buffer, instance_bytes);
    Commands::draw(
        encoder, 6U, draw.instance_count, 0U, draw.first_instance);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

template<typename Commands>
progpu_native_status encode_semantic_image_draw(
    progpu_native_engine& engine,
    typename Commands::encoder_type encoder,
    const semantic_image_draw& draw,
    std::uint32_t target_layer) {
    auto& page = engine.semantic_image_cache;
    WGPUBindGroup texture_group =
        draw.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST
        ? draw.nearest_bind_group
        : draw.linear_bind_group;
    WGPUBindGroup uniform_group =
        select_semantic_image_uniform_bind_group(
            engine,
            target_layer);
    if (!page.cache_valid || page.vertex_buffer == nullptr ||
        page.vertex_bytes == 0U || texture_group == nullptr ||
        engine.image_index_buffer == nullptr || uniform_group == nullptr ||
        encoder == nullptr ||
        draw.first_vertex >
            std::numeric_limits<std::uint32_t>::max() - 4U ||
        static_cast<std::uint64_t>(draw.first_vertex + 4U) *
                sizeof(progpu::native::vector_vertex) >
            page.vertex_bytes) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic image packed page is incomplete.");
    }
    Commands::set_pipeline(encoder, engine.image_pipeline);
    Commands::set_bind_group(
        encoder, 0U, uniform_group);
    Commands::set_bind_group(encoder, 1U, texture_group);
    Commands::set_vertex_buffer(
        encoder, page.vertex_buffer, page.vertex_bytes);
    Commands::set_index_buffer(
        encoder, engine.image_index_buffer, 6U * sizeof(std::uint32_t));
    Commands::draw_indexed(
        encoder, 6U, 0U, static_cast<std::int32_t>(draw.first_vertex));
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status update_scene(
    progpu_native_engine* engine,
    const void* stream,
    size_t stream_size,
    progpu_native_scene_metrics* metrics) {
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        &engine->webgpu_dispatch);
    if (std::this_thread::get_id() != engine->owner_thread) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "Native scene updates are owner-thread affine.");
    }
    if (stream != nullptr && !engine->semantic_scene_snapshot.empty() &&
        engine->semantic_scene_snapshot.size() == stream_size &&
        std::memcmp(
            engine->semantic_scene_snapshot.data(),
            stream,
            stream_size) == 0) {
        if (metrics != nullptr &&
            metrics->struct_size >= sizeof(progpu_native_scene_metrics)) {
            const std::uint32_t struct_size = metrics->struct_size;
            *metrics = engine->semantic_scene_metrics;
            metrics->struct_size = struct_size;
            metrics->flags |= PROGPU_NATIVE_SCENE_METRICS_SNAPSHOT_REUSED;
        }
        engine->last_error.clear();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    const auto validation =
        progpu::native::scene::validate(stream, stream_size);
    progpu::native::scene::write_metrics(validation, metrics);
    if (validation.status != PROGPU_NATIVE_STATUS_SUCCESS) {
        return engine->fail(
            validation.status,
            "The semantic scene stream failed transactional validation.");
    }

    if (validation.header.scene_id == engine->semantic_scene_id) {
        if (validation.header.generation <
            engine->semantic_scene_generation) {
            if (metrics != nullptr &&
                metrics->struct_size >= sizeof(progpu_native_scene_metrics)) {
                metrics->validation_error =
                    PROGPU_NATIVE_SCENE_VALIDATION_GENERATION;
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The semantic scene generation regressed.");
        }
        if (validation.header.generation ==
            engine->semantic_scene_generation) {
            if (metrics != nullptr && metrics->struct_size >=
                sizeof(progpu_native_scene_metrics)) {
                metrics->validation_error =
                    PROGPU_NATIVE_SCENE_VALIDATION_GENERATION;
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "One semantic scene generation must be immutable.");
        }

        std::uint32_t error_offset = 0U;
        if (!progpu::native::scene::generations_do_not_regress(
                engine->semantic_scene_snapshot.data(),
                engine->semantic_scene_header,
                stream,
                validation.header,
                error_offset)) {
            if (metrics != nullptr && metrics->struct_size >=
                sizeof(progpu_native_scene_metrics)) {
                metrics->validation_error =
                    PROGPU_NATIVE_SCENE_VALIDATION_GENERATION;
                metrics->error_offset = error_offset;
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "A retained semantic resource generation regressed.");
        }
    }

    try {
        std::vector<std::byte> next(stream_size);
        std::memcpy(next.data(), stream, stream_size);
        const std::uint64_t next_hash = append_fnv1a64(
            14695981039346656037ULL,
            stream,
            stream_size);
        engine->release_semantic_render_bundle();
        engine->semantic_scene_snapshot.swap(next);
        engine->semantic_scene_id = validation.header.scene_id;
        engine->semantic_scene_generation = validation.header.generation;
        engine->semantic_scene_hash = next_hash;
        engine->semantic_scene_header = validation.header;
        engine->semantic_scene_metrics = {};
        engine->semantic_scene_metrics.struct_size =
            sizeof(progpu_native_scene_metrics);
        progpu::native::scene::write_metrics(
            validation,
            &engine->semantic_scene_metrics);
        engine->last_error.clear();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The immutable semantic scene snapshot could not be allocated.");
    } catch (...) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The immutable semantic scene snapshot could not be committed.");
    }
}

progpu_native_status render_scene(
    progpu_native_engine* engine,
    const progpu_native_scene_frame* frame,
    progpu_native_scene_frame_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_scene_frame_metrics)) {
        const std::uint32_t struct_size = metrics->struct_size;
        *metrics = {};
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
        frame->struct_size < sizeof(progpu_native_scene_frame) ||
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

    semantic_layer_budget layer_budget{};
    semantic_layer_target_cursor layer_budget_cursor(
        bytes,
        frame->width,
        frame->height,
        frame->dpi_scale);
    bool semantic_has_materialized_layers = false;
    bool semantic_has_layer_masks = false;
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
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto command = read_command(index);
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
            semantic_has_layer_masks |= layer.mask_resource_index !=
                PROGPU_NATIVE_SCENE_NO_INDEX;
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
    std::uint64_t semantic_analytic_vertex_bytes = 0U;
    std::uint64_t semantic_analytic_index_bytes = 0U;
    std::uint64_t semantic_path_count = 0U;
    std::uint64_t semantic_path_segment_count = 0U;
    std::uint64_t semantic_glyph_outline_count = 0U;
    std::uint64_t semantic_glyph_segment_count = 0U;
    std::uint64_t semantic_glyph_count = 0U;
    semantic_compilation_budget compilation_budget{};
    semantic_state_cursor preflight_state_cursor(bytes, header);
    semantic_layer_target_cursor preflight_target_cursor(
        bytes,
        frame->width,
        frame->height,
        frame->dpi_scale);
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
            command.kind > PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            continue;
        }
        const auto resource = read_resource(command.resource_index);
        bool valid = false;
        bool budget_valid = true;
        std::uint64_t compiled_vertex_bytes = 0U;
        std::uint64_t compiled_index_bytes = 0U;
        std::uint64_t compiled_texture_bytes = 0U;
        std::uint64_t compiled_coverage_bytes = 0U;
        switch (command.kind) {
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC: {
                valid = span_is_multiple(
                    resource.payload_size,
                    sizeof(progpu_native_analytic_primitive)) &&
                    resource.auxiliary_size == 0U &&
                    command.payload_size == 0U;
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
                    apply_semantic_state(primitive, state);
                    valid = is_valid_semantic_analytic(primitive);
                }
                break;
            }
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH: {
                valid = span_is_multiple(
                        resource.payload_size,
                        sizeof(progpu_native_scene_path_fill)) &&
                    span_is_multiple(
                        resource.auxiliary_size,
                        sizeof(progpu_native_path_segment)) &&
                    command.payload_size == 0U;
                const std::uint64_t path_count = resource.payload_size /
                    sizeof(progpu_native_scene_path_fill);
                const std::uint64_t segment_count = resource.auxiliary_size /
                    sizeof(progpu_native_path_segment);
                valid = valid && path_count <= (1U << 20U) &&
                    segment_count <= (1U << 24U) &&
                    path_count <=
                        std::numeric_limits<std::uint32_t>::max() / 6U;
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
                    apply_semantic_state(path, state);
                    std::uint64_t path_coverage_bytes = 0U;
                    valid = is_valid_semantic_path(
                        path,
                        segment_count,
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
                valid = span_is_multiple(
                        resource.payload_size,
                        sizeof(progpu_native_scene_glyph_outline)) &&
                    span_is_multiple(
                        resource.auxiliary_size,
                        sizeof(progpu_native_path_segment)) &&
                    span_is_multiple(
                        command.payload_size,
                        sizeof(progpu_native_positioned_glyph));
                const std::uint64_t outline_count = resource.payload_size /
                    sizeof(progpu_native_scene_glyph_outline);
                const std::uint64_t segment_count = resource.auxiliary_size /
                    sizeof(progpu_native_path_segment);
                const std::uint64_t glyph_count = command.payload_size /
                    sizeof(progpu_native_positioned_glyph);
                valid = valid && outline_count <= (1U << 20U) &&
                    segment_count <= (1U << 24U) &&
                    glyph_count <= (1U << 24U);
                compiled_vertex_bytes = glyph_count *
                    sizeof(gpu_glyph_instance);
                for (std::uint64_t segment_index = 0U;
                     valid && segment_index < segment_count;
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
                     valid && budget_valid && outline_index < outline_count;
                     ++outline_index) {
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
                        bytes + command.payload_offset +
                            glyph_index * sizeof(glyph),
                        sizeof(glyph));
                    apply_semantic_state(glyph, state);
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
                valid = image.struct_size >= sizeof(image) &&
                    image.struct_size <= command.payload_size &&
                    resource.auxiliary_size == 0U &&
                    is_valid_semantic_image(
                        image,
                        resource.payload_size);
                compiled_vertex_bytes =
                    4U * sizeof(progpu::native::vector_vertex);
                compiled_index_bytes = 6U * sizeof(std::uint32_t);
                compiled_texture_bytes = resource.payload_size;
                break;
            }
            default:
                break;
        }
        if (!valid) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "A typed semantic scene resource payload is invalid.");
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
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC) {
            ++semantic_analytic_draw_count;
            semantic_analytic_vertex_bytes += compiled_vertex_bytes;
            semantic_analytic_index_bytes += compiled_index_bytes;
        } else if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH) {
            ++semantic_path_draw_count;
            semantic_path_count += resource.payload_size /
                sizeof(progpu_native_scene_path_fill);
            semantic_path_segment_count += resource.auxiliary_size /
                sizeof(progpu_native_path_segment);
        } else if (command.kind ==
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN) {
            ++semantic_glyph_draw_count;
            semantic_glyph_outline_count += resource.payload_size /
                sizeof(progpu_native_scene_glyph_outline);
            semantic_glyph_segment_count += resource.auxiliary_size /
                sizeof(progpu_native_path_segment);
            semantic_glyph_count += command.payload_size /
                sizeof(progpu_native_positioned_glyph);
        } else if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            ++semantic_image_draw_count;
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
    const std::uint64_t retained_layer_bytes = invalid_layer_pool
        ? std::numeric_limits<std::uint64_t>::max()
        : pooled_layer_bytes + pooled_effect_bytes +
            semantic_destination_texture_bytes;
    const std::uint64_t compiled_bytes =
        compilation_budget.total_bytes();
    if (invalid_layer_pool ||
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

    const bool semantic_render_bundle_hit =
        engine->semantic_render_bundle_valid &&
        engine->semantic_render_bundle_scene_hash ==
            engine->semantic_scene_hash &&
        engine->semantic_render_bundle_dpi_scale == frame->dpi_scale &&
        engine->semantic_render_bundle_width == frame->width &&
        engine->semantic_render_bundle_height == frame->height &&
        (semantic_path_draw_count == 0U ||
            engine->semantic_path_gpu_scene_hash ==
                engine->semantic_scene_hash) &&
        (semantic_glyph_draw_count == 0U ||
            engine->semantic_glyph_gpu_scene_hash ==
                engine->semantic_scene_hash);
    if (!semantic_render_bundle_hit) {
        engine->release_semantic_render_bundle();
    }

    std::uint64_t semantic_analytic_vertex_upload_bytes = 0U;
    std::uint64_t semantic_analytic_index_upload_bytes = 0U;
    auto& semantic_analytic_page = engine->semantic_analytic_cache;
    const bool semantic_analytic_page_hit =
        semantic_analytic_draw_count != 0U &&
        semantic_analytic_page.cache_valid &&
        semantic_analytic_page.scene_hash == engine->semantic_scene_hash &&
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

            semantic_state_cursor state_cursor(bytes, header);
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
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC) {
                    continue;
                }
                const auto resource = read_resource(command.resource_index);
                const std::size_t vertex_start = engine->vertices.size();
                const std::size_t index_start = engine->indices.size();
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
                    apply_semantic_state(primitive, state);
                    float minimum_scale = 0.0F;
                    if (!progpu::native::try_get_minimum_scale(
                            primitive.transform,
                            minimum_scale) ||
                        !progpu::native::append_analytic_primitive(
                            primitive,
                            antialias_padding_pixels / minimum_scale,
                            engine->vertices,
                            engine->indices)) {
                        return engine->fail(
                            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                            "A preflighted semantic analytic payload could not be compiled.");
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
            compiled_vertex_bytes != semantic_analytic_vertex_bytes ||
            compiled_index_bytes != semantic_analytic_index_bytes) {
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
        semantic_analytic_page.scene_hash = engine->semantic_scene_hash;
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
        semantic_path_page.scene_hash == engine->semantic_scene_hash &&
        semantic_path_page.dpi_scale == frame->dpi_scale &&
        semantic_path_page.target_width == frame->width &&
        semantic_path_page.target_height == frame->height &&
        semantic_path_page.draws.size() == semantic_path_draw_count;
    if (semantic_path_draw_count != 0U && !semantic_path_page_hit) {
        std::vector<progpu_native_path_fill> compiled_paths;
        std::vector<progpu_native_path_segment> compiled_segments;
        std::vector<semantic_path_draw> compiled_draws;
        try {
            compiled_paths.reserve(
                static_cast<std::size_t>(semantic_path_count));
            compiled_segments.reserve(
                static_cast<std::size_t>(semantic_path_segment_count));
            compiled_draws.reserve(semantic_path_draw_count);
            semantic_state_cursor state_cursor(bytes, header);
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
                const std::size_t path_count = resource.payload_size /
                    sizeof(progpu_native_scene_path_fill);
                const std::size_t segment_count = resource.auxiliary_size /
                    sizeof(progpu_native_path_segment);
                const auto* source_segments = reinterpret_cast<
                    const progpu_native_path_segment*>(
                        bytes + resource.auxiliary_offset);
                compiled_segments.insert(
                    compiled_segments.end(),
                    source_segments,
                    source_segments + segment_count);
                for (std::size_t path_index = 0U;
                     path_index < path_count;
                     ++path_index) {
                    progpu_native_path_fill path{};
                    std::memcpy(
                        &path,
                        bytes + resource.payload_offset +
                            path_index *
                                sizeof(progpu_native_scene_path_fill),
                        sizeof(path));
                    apply_semantic_state(path, state);
                    path.segment_offset += segment_start;
                    compiled_paths.push_back(path);
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
            compiled_draws.size() != semantic_path_draw_count) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic path packed-page budget did not match compilation.");
        }
        semantic_path_page.paths = std::move(compiled_paths);
        semantic_path_page.segments = std::move(compiled_segments);
        semantic_path_page.draws = std::move(compiled_draws);
        semantic_path_page.scene_hash = engine->semantic_scene_hash;
        semantic_path_page.dpi_scale = frame->dpi_scale;
        semantic_path_page.target_width = frame->width;
        semantic_path_page.target_height = frame->height;
        semantic_path_page.cache_valid = true;
        engine->semantic_path_gpu_scene_hash = 0U;
    }

    if (semantic_path_draw_count != 0U &&
        engine->semantic_path_gpu_scene_hash !=
            engine->semantic_scene_hash) {
        engine->path_cache_valid = false;
        engine->path_gpu_cache_valid = false;
    }

    auto& semantic_glyph_page = engine->semantic_glyph_cache;
    const bool semantic_glyph_page_hit =
        semantic_glyph_draw_count != 0U &&
        semantic_glyph_page.cache_valid &&
        semantic_glyph_page.scene_hash == engine->semantic_scene_hash &&
        semantic_glyph_page.dpi_scale == frame->dpi_scale &&
        semantic_glyph_page.target_width == frame->width &&
        semantic_glyph_page.target_height == frame->height &&
        semantic_glyph_page.draws.size() == semantic_glyph_draw_count;
    if (semantic_glyph_draw_count != 0U && !semantic_glyph_page_hit) {
        std::vector<progpu_native_glyph_outline> compiled_outlines;
        std::vector<progpu_native_path_segment> compiled_segments;
        std::vector<progpu_native_positioned_glyph> compiled_glyphs;
        std::vector<semantic_glyph_draw> compiled_draws;
        try {
            compiled_outlines.reserve(
                static_cast<std::size_t>(semantic_glyph_outline_count));
            compiled_segments.reserve(
                static_cast<std::size_t>(semantic_glyph_segment_count));
            compiled_glyphs.reserve(
                static_cast<std::size_t>(semantic_glyph_count));
            compiled_draws.reserve(semantic_glyph_draw_count);
            semantic_state_cursor state_cursor(bytes, header);
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
                const std::size_t outline_start = compiled_outlines.size();
                const std::size_t segment_start = compiled_segments.size();
                const std::size_t glyph_start = compiled_glyphs.size();
                const std::size_t outline_count = resource.payload_size /
                    sizeof(progpu_native_scene_glyph_outline);
                const std::size_t segment_count = resource.auxiliary_size /
                    sizeof(progpu_native_path_segment);
                const std::size_t glyph_count = command.payload_size /
                    sizeof(progpu_native_positioned_glyph);
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
                    progpu_native_glyph_outline outline{};
                    std::memcpy(
                        &outline,
                        bytes + resource.payload_offset +
                            outline_index *
                                sizeof(progpu_native_scene_glyph_outline),
                        sizeof(outline));
                    outline.segment_offset += segment_start;
                    compiled_outlines.push_back(outline);
                }
                for (std::size_t glyph_index = 0U;
                     glyph_index < glyph_count;
                     ++glyph_index) {
                    progpu_native_positioned_glyph glyph{};
                    std::memcpy(
                        &glyph,
                        bytes + command.payload_offset +
                            glyph_index * sizeof(glyph),
                        sizeof(glyph));
                    apply_semantic_state(glyph, state);
                    glyph.outline_index += static_cast<std::uint32_t>(
                        outline_start);
                    compiled_glyphs.push_back(glyph);
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
            compiled_draws.size() != semantic_glyph_draw_count) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic glyph packed-page budget did not match compilation.");
        }
        semantic_glyph_page.outlines = std::move(compiled_outlines);
        semantic_glyph_page.segments = std::move(compiled_segments);
        semantic_glyph_page.glyphs = std::move(compiled_glyphs);
        semantic_glyph_page.draws = std::move(compiled_draws);
        semantic_glyph_page.scene_hash = engine->semantic_scene_hash;
        semantic_glyph_page.dpi_scale = frame->dpi_scale;
        semantic_glyph_page.target_width = frame->width;
        semantic_glyph_page.target_height = frame->height;
        semantic_glyph_page.cache_valid = true;
        engine->semantic_glyph_gpu_scene_hash = 0U;
    }

    if (semantic_glyph_draw_count != 0U &&
        engine->semantic_glyph_gpu_scene_hash !=
            engine->semantic_scene_hash) {
        engine->glyph_cache_valid = false;
        engine->glyph_gpu_cache_valid = false;
    }

    std::uint64_t semantic_image_vertex_upload_bytes = 0U;
    std::uint64_t semantic_image_index_upload_bytes = 0U;
    std::uint64_t semantic_image_texture_upload_bytes = 0U;
    auto& semantic_image_page = engine->semantic_image_cache;
    const bool semantic_image_page_hit =
        semantic_image_draw_count != 0U &&
        semantic_image_page.cache_valid &&
        semantic_image_page.scene_hash == engine->semantic_scene_hash &&
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
        std::vector<progpu::native::vector_vertex> vertices;
        std::vector<semantic_image_draw> compiled_draws;
        WGPUBuffer compiled_vertex_buffer = nullptr;
        const auto release_compiled = [&]() noexcept {
            for (auto& draw : compiled_draws) {
                if (draw.linear_bind_group != nullptr) {
                    wgpuBindGroupRelease(draw.linear_bind_group);
                }
                if (draw.nearest_bind_group != nullptr) {
                    wgpuBindGroupRelease(draw.nearest_bind_group);
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
            vertices.reserve(
                static_cast<std::size_t>(semantic_image_draw_count) * 4U);
            compiled_draws.reserve(semantic_image_draw_count);
            semantic_state_cursor state_cursor(bytes, header);
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
                const std::uint32_t first_vertex =
                    static_cast<std::uint32_t>(vertices.size());
                const float x0 = image.destination_rect.x;
                const float y0 = image.destination_rect.y;
                const float x1 = x0 + image.destination_rect.width;
                const float y1 = y0 + image.destination_rect.height;
                const float u0 = image.source_rect.x /
                    static_cast<float>(image.image_width);
                const float v0 = image.source_rect.y /
                    static_cast<float>(image.image_height);
                const float u1 = (image.source_rect.x +
                    image.source_rect.width) /
                    static_cast<float>(image.image_width);
                const float v1 = (image.source_rect.y +
                    image.source_rect.height) /
                    static_cast<float>(image.image_height);
                constexpr std::array<
                    std::array<std::uint32_t, 2U>, 4U> corners{{
                    {0U, 0U}, {1U, 0U}, {1U, 1U}, {0U, 1U}
                }};
                for (const auto& corner : corners) {
                    const float x = corner[0] == 0U ? x0 : x1;
                    const float y = corner[1] == 0U ? y0 : y1;
                    progpu::native::vector_vertex vertex{};
                    progpu::native::transform_point(
                        image.transform,
                        x,
                        y,
                        vertex.position[0],
                        vertex.position[1]);
                    vertex.color[0] = 1.0F;
                    vertex.color[1] = 0.0F;
                    vertex.color[2] = 1.0F;
                    vertex.color[3] = image.opacity;
                    vertex.texture_coordinate[0] =
                        corner[0] == 0U ? u0 : u1;
                    vertex.texture_coordinate[1] =
                        corner[1] == 0U ? v0 : v1;
                    vertex.brush_index = 0.0F;
                    vertex.shape_size[0] = 0.0F;
                    vertex.shape_size[1] = 0.5F;
                    vertex.corner_radius = 0.0F;
                    vertex.stroke_thickness = 1.0F;
                    vertex.shape_type = 0.0F;
                    vertices.push_back(vertex);
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
                draw.sampling = image.sampling;
                draw.texture = wgpuDeviceCreateTexture(
                    engine->device,
                    &texture_descriptor);
                if (draw.texture != nullptr) {
                    draw.view = wgpuTextureCreateView(draw.texture, nullptr);
                }
                if (draw.view != nullptr) {
                    draw.nearest_bind_group = create_image_texture_bind_group(
                        *engine,
                        engine->image_nearest_sampler,
                        draw.view,
                        "ProGPU semantic nearest image bind group");
                    draw.linear_bind_group = create_image_texture_bind_group(
                        *engine,
                        engine->image_linear_sampler,
                        draw.view,
                        "ProGPU semantic linear image bind group");
                }
                compiled_draws.push_back(draw);
                auto& retained_draw = compiled_draws.back();
                if (retained_draw.texture == nullptr ||
                    retained_draw.view == nullptr ||
                    retained_draw.nearest_bind_group == nullptr ||
                    retained_draw.linear_bind_group == nullptr) {
                    release_compiled();
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "A semantic image page texture could not be allocated.");
                }
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
        } catch (const std::bad_alloc&) {
            release_compiled();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic image packed page could not be compiled.");
        }
        const std::uint64_t vertex_bytes = vertices.size() *
            sizeof(progpu::native::vector_vertex);
        if (compiled_draws.size() != semantic_image_draw_count ||
            vertex_bytes != static_cast<std::uint64_t>(
                semantic_image_draw_count) * 4U *
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
        semantic_image_page.scene_hash = engine->semantic_scene_hash;
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
        semantic_image_texture_upload_bytes;
    std::uint64_t uniform_upload_bytes = 0U;
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

    if (semantic_path_draw_count != 0U &&
        (engine->semantic_path_gpu_scene_hash !=
                engine->semantic_scene_hash ||
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
        family.paths = semantic_path_page.paths.data();
        family.path_count = semantic_path_page.paths.size();
#else
        std::vector<progpu_native_path_fill> translated_paths;
        try {
            translated_paths.reserve(semantic_path_page.paths.size());
            for (const auto& source : semantic_path_page.paths) {
                if (source.segment_offset > SIZE_MAX ||
                    source.segment_count > SIZE_MAX) {
                    discard_encoder();
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "A semantic path index exceeds the wasm32 address range.");
                }
                translated_paths.push_back({
                    static_cast<std::size_t>(source.segment_offset),
                    static_cast<std::size_t>(source.segment_count),
                    source.min_x,
                    source.min_y,
                    source.max_x,
                    source.max_y,
                    source.color,
                    source.transform,
                    source.fill_rule,
                    source.sample_grid});
            }
        } catch (const std::bad_alloc&) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The wasm32 semantic path translation could not be allocated.");
        }
        family.paths = translated_paths.data();
        family.path_count = translated_paths.size();
#endif
        family.segments = semantic_path_page.segments.data();
        family.segment_count = semantic_path_page.segments.size();
        family.flags =
            PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD;
        family.content_revision = revision32(engine->semantic_scene_hash);
        progpu_native_path_frame_metrics family_metrics{};
        family_metrics.struct_size = sizeof(family_metrics);
        engine->semantic_prepare_only = true;
        engine->semantic_path_draw_active = true;
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
        engine->semantic_path_gpu_scene_hash = engine->semantic_scene_hash;
    }

    if (semantic_glyph_draw_count != 0U &&
        (engine->semantic_glyph_gpu_scene_hash !=
                engine->semantic_scene_hash ||
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
        family.outlines = semantic_glyph_page.outlines.data();
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
        family.content_revision = revision32(engine->semantic_scene_hash);
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
            engine->semantic_scene_hash;
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
    if (semantic_has_materialized_layers) {
        const std::uint32_t semantic_layer_quad_count =
            semantic_materialized_layer_count +
            semantic_advanced_layer_count * 2U +
            (semantic_destination_sampling_active ? 1U : 0U) +
            semantic_effected_backdrop_layer_count;
        if (semantic_has_layer_masks &&
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
        semantic_scissor active_scissor{};
        bool has_active_scissor = false;
        std::uint32_t active_target_layer =
            PROGPU_NATIVE_SCENE_NO_INDEX;
        const auto release_compiled_spans = [&]() noexcept {
            for (auto& span : compiled_spans) {
                if (span.mask_bind_group != nullptr) {
                    wgpuBindGroupRelease(span.mask_bind_group);
                    span.mask_bind_group = nullptr;
                }
                if (span.mask_uniform_buffer != nullptr) {
                    wgpuBufferDestroy(span.mask_uniform_buffer);
                    wgpuBufferRelease(span.mask_uniform_buffer);
                    span.mask_uniform_buffer = nullptr;
                }
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
            release_compiled_spans();
            discard_encoder();
            return status;
        };
        const auto finish_active_bundle = [&]() {
            if (bundle_encoder == nullptr) {
                return PROGPU_NATIVE_STATUS_SUCCESS;
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
            compiled_spans.push_back(operation);
            return PROGPU_NATIVE_STATUS_SUCCESS;
        };
        const auto begin_active_bundle = [&](
            semantic_scissor scissor,
            std::uint32_t target_layer) {
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
            has_active_scissor = true;
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

        semantic_state_cursor state_cursor(bytes, header);
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
                    if (layer.mask_resource_index !=
                            PROGPU_NATIVE_SCENE_NO_INDEX) {
                        const auto resource = read_resource(
                            layer.mask_resource_index);
                        progpu_native_scene_layer_mask mask{};
                        std::memcpy(
                            &mask,
                            bytes + resource.payload_offset,
                            sizeof(mask));
                        if (!create_semantic_layer_mask_binding(
                                *engine,
                                mask,
                                target_extent,
                                frame->dpi_scale,
                                operation)) {
                            return fail_bundle(engine->fail(
                                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                                "A retained semantic layer-mask binding could not be prepared."));
                        }
                        semantic_layer_mask_uniform_upload_bytes +=
                            sizeof(gpu_mask_sampling_uniforms);
                        semantic_layer_uniform_upload_bytes +=
                            sizeof(gpu_mask_sampling_uniforms);
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
                command.kind > PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
                continue;
            }
            const auto scissor = resolve_semantic_target_scissor(
                state,
                target_extent,
                frame->width,
                frame->height,
                frame->dpi_scale);
            if (scissor.drawable &&
                (!has_active_scissor || scissor != active_scissor ||
                    current_target_layer != active_target_layer)) {
                const auto finish_status = finish_active_bundle();
                if (finish_status != PROGPU_NATIVE_STATUS_SUCCESS) {
                    return fail_bundle(finish_status);
                }
                const auto begin_status = begin_active_bundle(
                    scissor,
                    current_target_layer);
                if (begin_status != PROGPU_NATIVE_STATUS_SUCCESS) {
                    return fail_bundle(begin_status);
                }
            }
            if (scissor.drawable) {
                note_family(command.kind);
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
                        status = encode_semantic_analytic_draw<
                            semantic_render_bundle_commands>(
                                *engine,
                                bundle_encoder,
                                semantic_analytic_page.draws[draw_index],
                                current_target_layer);
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
                        status = encode_semantic_path_draw<
                            semantic_render_bundle_commands>(
                                *engine,
                                bundle_encoder,
                                semantic_path_page.draws[draw_index],
                                current_target_layer);
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
                        status = encode_semantic_glyph_draw<
                            semantic_render_bundle_commands>(
                                *engine,
                                bundle_encoder,
                                semantic_glyph_page.draws[draw_index],
                                current_target_layer);
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
                        status = encode_semantic_image_draw<
                            semantic_render_bundle_commands>(
                                *engine,
                                bundle_encoder,
                                semantic_image_page.draws[draw_index],
                                current_target_layer);
                    }
                    break;
                }
                default:
                    break;
            }
            if (status != PROGPU_NATIVE_STATUS_SUCCESS) {
                return fail_bundle(status);
            }
            draw_calls += scissor.drawable ? 1U : 0U;
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
            semantic_image_draw_index != semantic_image_draw_count) {
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
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->semantic_layer_vertex_buffer,
                0U,
                semantic_layer_vertices.data(),
                layer_vertex_bytes);
            vertex_upload_bytes += layer_vertex_bytes;
            semantic_layer_vertex_upload_bytes = layer_vertex_bytes;
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
            engine->semantic_scene_hash;
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
    if (semantic_draw_count != 0U &&
        !semantic_has_materialized_layers) {
        WGPURenderPassColorAttachment color_attachment{};
        progpu::native::webgpu::initialize_color_attachment(
            color_attachment);
        color_attachment.view = reinterpret_cast<WGPUTextureView>(
            frame->target_view);
        color_attachment.loadOp = WGPULoadOp_Clear;
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
        }
        wgpuRenderPassEncoderEnd(pass);
        wgpuRenderPassEncoderRelease(pass);
    } else if (semantic_has_materialized_layers) {
        std::uint32_t active_target_layer =
            PROGPU_NATIVE_SCENE_NO_INDEX;
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
            if (operation.kind == semantic_replay_kind::push_layer) {
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
                }
                if (!begin_pass(
                        operation.target_layer,
                        operation.backdrop
                            ? WGPULoadOp_Load
                            : WGPULoadOp_Clear)) {
                    return fail_replay(
                        "A semantic isolated-layer content pass could not be created.");
                }
                continue;
            }
            if (operation.kind == semantic_replay_kind::pop_layer) {
                finish_pass();
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
                    if (progpu::native::effects::semantic_output_cache_hit(
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
                    (is_advanced_group_blend(operation.blend_mode)
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
                        : (begin_pass(
                                operation.target_layer,
                                WGPULoadOp_Load) &&
                            encode_semantic_layer_composite(
                                *engine,
                                pass,
                                operation)));
                if (!composite_ready) {
                    return fail_replay(
                        "A semantic isolated-layer composite pass could not be encoded.");
                }
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
    }

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

    if (semantic_has_materialized_layers) {
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
            semantic_materialized_layer_count;
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
        engine->last_layer_metrics.mask_kind = semantic_has_layer_masks
            ? PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE
            : PROGPU_NATIVE_GROUP_MASK_NONE;
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
    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_scene_frame_metrics)) {
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
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

} // namespace progpu::native::execution
