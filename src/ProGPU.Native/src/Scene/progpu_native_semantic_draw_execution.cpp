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
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group,
    WGPUBindGroup mask_chain_bind_group) {
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
    const bool masked = mask_bind_group != nullptr ||
        mask_chain_bind_group != nullptr;
    const bool chained = mask_chain_bind_group != nullptr;
    if ((!masked && engine.analytic_pipeline == nullptr &&
            !create_analytic_pipeline(engine)) ||
        (masked && !chained && engine.analytic_masked_pipeline == nullptr &&
            !create_analytic_masked_pipeline(engine)) ||
        (chained && engine.analytic_mask_chain_pipeline == nullptr &&
            !create_semantic_vector_mask_chain_pipeline(engine))) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic analytic WebGPU pipeline could not be created.");
    }

    Commands::set_pipeline(
        encoder,
        chained ? engine.analytic_mask_chain_pipeline :
        masked ? engine.analytic_masked_pipeline : engine.analytic_pipeline);
    Commands::set_bind_group(
        encoder, 0U, uniform_group);
    Commands::set_bind_group(
        encoder, 1U, engine.analytic_atlas_bind_group);
    if (masked) {
        Commands::set_bind_group(
            encoder, 2U,
            chained ? mask_chain_bind_group : mask_bind_group);
    }
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
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group,
    WGPUBindGroup mask_chain_bind_group) {
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
    const bool masked = mask_bind_group != nullptr ||
        mask_chain_bind_group != nullptr;
    const bool chained = mask_chain_bind_group != nullptr;
    if (masked && !chained && engine.analytic_masked_pipeline == nullptr &&
        !create_analytic_masked_pipeline(engine)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic masked path pipeline could not be created.");
    }
    if (chained && engine.analytic_mask_chain_pipeline == nullptr &&
        !create_semantic_vector_mask_chain_pipeline(engine)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic mask-chain path pipeline could not be created.");
    }
    Commands::set_pipeline(
        encoder,
        chained ? engine.analytic_mask_chain_pipeline :
        masked ? engine.analytic_masked_pipeline : engine.analytic_pipeline);
    Commands::set_bind_group(
        encoder, 0U, uniform_group);
    Commands::set_bind_group(
        encoder, 1U, engine.path_atlas_bind_group);
    if (masked) {
        Commands::set_bind_group(
            encoder, 2U,
            chained ? mask_chain_bind_group : mask_bind_group);
    }
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
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group,
    WGPUBindGroup mask_chain_bind_group) {
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
    const bool masked = mask_bind_group != nullptr ||
        mask_chain_bind_group != nullptr;
    const bool chained = mask_chain_bind_group != nullptr;
    if (masked && !chained && engine.text_masked_pipeline == nullptr &&
        !create_text_masked_pipeline(engine)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic masked glyph pipeline could not be created.");
    }
    if (chained && engine.text_mask_chain_pipeline == nullptr &&
        !create_semantic_text_mask_chain_pipeline(engine)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic mask-chain glyph pipeline could not be created.");
    }
    Commands::set_pipeline(
        encoder,
        chained ? engine.text_mask_chain_pipeline :
        masked ? engine.text_masked_pipeline : engine.text_pipeline);
    Commands::set_bind_group(
        encoder, 0U, uniform_group);
    Commands::set_bind_group(
        encoder, 1U, engine.text_atlas_bind_group);
    if (masked) {
        Commands::set_bind_group(
            encoder, 2U,
            chained ? mask_chain_bind_group : mask_bind_group);
    }
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
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group,
    WGPUBindGroup mask_chain_bind_group) {
    auto& page = engine.semantic_image_cache;
    WGPUBindGroup texture_group = draw.texture_bind_group;
    WGPUBindGroup uniform_group =
        select_semantic_image_uniform_bind_group(
            engine,
            target_layer);
    if (!page.cache_valid || page.vertex_buffer == nullptr ||
        page.vertex_bytes == 0U || texture_group == nullptr ||
        engine.image_index_buffer == nullptr || uniform_group == nullptr ||
        encoder == nullptr ||
        (draw.vertex_count != 4U &&
            (draw.vertex_count == 0U || draw.vertex_count % 6U != 0U)) ||
        draw.first_vertex >
            std::numeric_limits<std::uint32_t>::max() - draw.vertex_count ||
        static_cast<std::uint64_t>(draw.first_vertex + draw.vertex_count) *
                sizeof(progpu::native::vector_vertex) >
            page.vertex_bytes) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic image packed page is incomplete.");
    }
    const bool masked = draw.has_effect_mask ||
        mask_bind_group != nullptr || mask_chain_bind_group != nullptr;
    const bool chained = !draw.has_effect_mask &&
        mask_chain_bind_group != nullptr;
    const auto encode_vertices = [&]() noexcept {
        Commands::set_vertex_buffer(
            encoder, page.vertex_buffer, page.vertex_bytes);
        if (draw.vertex_count == 4U) {
            Commands::set_index_buffer(
                encoder, engine.image_index_buffer,
                6U * sizeof(std::uint32_t));
            Commands::draw_indexed(
                encoder, 6U, 0U,
                static_cast<std::int32_t>(draw.first_vertex));
        } else {
            Commands::draw(
                encoder,
                draw.vertex_count,
                1U,
                draw.first_vertex,
                0U);
        }
    };
    if (draw.has_effect) {
        if ((!chained && !create_semantic_image_effect_pipelines(engine)) ||
            (chained &&
                !create_semantic_image_effect_mask_chain_pipeline(engine)) ||
            draw.effect_uniform_bind_group == nullptr ||
            draw.effect_texture_bind_group == nullptr ||
            draw.effect_dummy_mask_bind_group == nullptr) {
            return engine.fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic image-effect pipeline is incomplete.");
        }
        Commands::set_pipeline(
            encoder,
            chained
                ? engine.image_effect_mask_chain_pipeline
                : engine.image_effect_pipeline);
        Commands::set_bind_group(encoder, 0U, uniform_group);
        Commands::set_bind_group(
            encoder, 1U, draw.effect_uniform_bind_group);
        Commands::set_bind_group(
            encoder, 2U, draw.effect_texture_bind_group);
        Commands::set_bind_group(
            encoder,
            3U,
            chained
                ? mask_chain_bind_group
                : draw.has_effect_mask
                ? draw.effect_dummy_mask_bind_group
                : masked
                    ? mask_bind_group
                    : draw.effect_dummy_mask_bind_group);
        encode_vertices();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    if (masked && !chained && engine.image_mask_pipeline == nullptr &&
        !create_image_mask_resources(engine)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic masked image pipeline could not be created.");
    }
    if (chained && engine.image_mask_chain_pipeline == nullptr &&
        !create_semantic_image_mask_chain_pipelines(engine)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic mask-chain image pipeline could not be created.");
    }
    WGPURenderPipeline pipeline = chained
        ? draw.has_color_matrix
            ? engine.image_mask_chain_color_matrix_pipeline
            : engine.image_mask_chain_pipeline
        : masked
        ? draw.has_color_matrix
            ? engine.image_masked_color_matrix_pipeline
            : engine.image_mask_pipeline
        : draw.has_color_matrix
            ? engine.image_color_matrix_pipeline
            : engine.image_pipeline;
    if (pipeline == nullptr ||
        (draw.has_color_matrix && draw.color_matrix_bind_group == nullptr)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic image color pipeline is incomplete.");
    }
    Commands::set_pipeline(encoder, pipeline);
    Commands::set_bind_group(
        encoder, 0U, uniform_group);
    Commands::set_bind_group(encoder, 1U, texture_group);
    if (masked) {
        Commands::set_bind_group(
            encoder, 2U,
            chained ? mask_chain_bind_group : mask_bind_group);
        if (draw.has_color_matrix) {
            Commands::set_bind_group(
                encoder, 3U, draw.color_matrix_bind_group);
        }
    } else if (draw.has_color_matrix) {
        Commands::set_bind_group(
            encoder, 2U, draw.color_matrix_bind_group);
    }
    encode_vertices();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status encode_semantic_analytic_bundle_draw(
    progpu_native_engine& engine,
    WGPURenderBundleEncoder encoder,
    const semantic_analytic_draw& draw,
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group,
    WGPUBindGroup mask_chain_bind_group) {
    return encode_semantic_analytic_draw<semantic_render_bundle_commands>(
        engine, encoder, draw, target_layer, mask_bind_group,
        mask_chain_bind_group);
}

progpu_native_status encode_semantic_path_bundle_draw(
    progpu_native_engine& engine,
    WGPURenderBundleEncoder encoder,
    const semantic_path_draw& draw,
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group,
    WGPUBindGroup mask_chain_bind_group) {
    return encode_semantic_path_draw<semantic_render_bundle_commands>(
        engine, encoder, draw, target_layer, mask_bind_group,
        mask_chain_bind_group);
}

progpu_native_status encode_semantic_glyph_bundle_draw(
    progpu_native_engine& engine,
    WGPURenderBundleEncoder encoder,
    const semantic_glyph_draw& draw,
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group,
    WGPUBindGroup mask_chain_bind_group) {
    return encode_semantic_glyph_draw<semantic_render_bundle_commands>(
        engine, encoder, draw, target_layer, mask_bind_group,
        mask_chain_bind_group);
}

progpu_native_status encode_semantic_image_bundle_draw(
    progpu_native_engine& engine,
    WGPURenderBundleEncoder encoder,
    const semantic_image_draw& draw,
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group,
    WGPUBindGroup mask_chain_bind_group) {
    return encode_semantic_image_draw<semantic_render_bundle_commands>(
        engine, encoder, draw, target_layer, mask_bind_group,
        mask_chain_bind_group);
}

} // namespace progpu::native::execution
