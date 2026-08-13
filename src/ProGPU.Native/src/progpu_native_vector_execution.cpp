#include "progpu_native_frame_execution_common.hpp"

namespace progpu::native::execution {

progpu_native_status render_solid(
    progpu_native_engine* engine,
    const progpu_native_frame* frame,
    progpu_native_frame_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    clear_metrics(metrics);
    if (engine == nullptr || frame == nullptr ||
        frame->struct_size < offsetof(progpu_native_frame, draw_state) ||
        frame->width == 0U || frame->height == 0U ||
        !std::isfinite(frame->dpi_scale) || frame->dpi_scale <= 0.0F ||
        frame->target_view == 0U ||
        (frame->rect_count != 0U && frame->rects == nullptr) ||
        !std::isfinite(frame->clear_color.r) ||
        !std::isfinite(frame->clear_color.g) ||
        !std::isfinite(frame->clear_color.b) ||
        !std::isfinite(frame->clear_color.a)) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The frame descriptor is invalid.");
    }
    resolved_draw_state draw_state{};
    const auto* requested_draw_state =
        frame->struct_size >= sizeof(progpu_native_frame)
            ? frame->draw_state
            : nullptr;
    if (!resolve_draw_state(
            requested_draw_state,
            frame->target_view,
            frame->width,
            frame->height,
            frame->dpi_scale,
            draw_state)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The frame draw state is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer must be used from its owner thread.");
    }
    engine->release_semantic_render_bundle();
    reset_layer_metrics(*engine);
    engine->geometry_cache_valid = false;
    engine->geometry_gpu_cache_valid = false;
    if (frame->rect_count >
            std::numeric_limits<std::size_t>::max() / 6U ||
        frame->rect_count >
            std::numeric_limits<std::uint32_t>::max() / 6U) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The rectangle batch is too large.");
    }
    bool use_group_layer = false;
    bool group_cache_hit = false;
    const auto group_status = prepare_group_layer(
        *engine,
        layer_family::solid,
        frame->width,
        frame->height,
        frame->dpi_scale,
        reinterpret_cast<WGPUTextureView>(frame->target_view),
        frame->clear_color,
        draw_state,
        use_group_layer,
        group_cache_hit);
    if (group_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        return group_status;
    }
    if (group_cache_hit) {
        if (metrics != nullptr && metrics->struct_size >=
                sizeof(progpu_native_frame_metrics)) {
            metrics->submission_count = engine->submission_count;
        }
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }

    try {
        engine->vertices.clear();
        engine->vertices.reserve(frame->rect_count * 6U);
        const float local_padding =
            antialias_padding_pixels / frame->dpi_scale;
        for (std::size_t index = 0; index < frame->rect_count; ++index) {
            if (!progpu::native::append_solid_rect(
                    frame->rects[index],
                    local_padding,
                    engine->vertices)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A rectangle contains invalid geometry or color values.");
            }
        }
        multiply_vertex_alpha(engine->vertices, draw_state.opacity);
    } catch (const std::bad_alloc&) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The native rectangle batch could not be allocated.");
    }

    const std::uint64_t vertex_bytes =
        engine->vertices.size() * sizeof(progpu::native::vector_vertex);
    if (!engine->ensure_vertex_buffer(vertex_bytes)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The native WebGPU vertex buffer could not be allocated.");
    }

    const gpu_uniforms uniforms = create_uniforms(
        frame->width,
        frame->height,
        frame->dpi_scale);
    const bool uploaded_uniforms = engine->upload_uniform_if_changed(
        engine->uniform_buffer,
        uniforms,
        engine->cached_uniforms,
        engine->uniform_cache_valid);
    if (vertex_bytes != 0U) {
        wgpuQueueWriteBuffer(
            engine->queue,
            engine->vertex_buffer,
            0U,
            engine->vertices.data(),
            static_cast<std::size_t>(vertex_bytes));
    }

    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view("ProGPU native frame encoder");
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
        engine->device,
        &encoder_descriptor);
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native frame command encoder could not be created.");
    }

    WGPURenderPassColorAttachment color_attachment{};
    progpu::native::webgpu::initialize_color_attachment(color_attachment);
    color_attachment.view = use_group_layer
        ? engine->layer_texture_view
        : reinterpret_cast<WGPUTextureView>(frame->target_view);
    color_attachment.loadOp = !use_group_layer &&
            engine->semantic_load_target
        ? WGPULoadOp_Load
        : WGPULoadOp_Clear;
    color_attachment.storeOp = WGPUStoreOp_Store;
    color_attachment.clearValue = use_group_layer
        ? WGPUColor{0.0, 0.0, 0.0, 0.0}
        : WGPUColor{
            frame->clear_color.r,
            frame->clear_color.g,
            frame->clear_color.b,
            frame->clear_color.a};
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = progpu::native::webgpu::string_view("ProGPU native solid rectangle pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &color_attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        wgpuCommandEncoderRelease(encoder);
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native render pass could not be created.");
    }

    if (!engine->vertices.empty() && draw_state.opacity != 0.0F &&
        (use_group_layer || draw_state.has_drawable_clip)) {
        if (!use_group_layer) {
            apply_scissor(pass, draw_state);
        }
        wgpuRenderPassEncoderSetPipeline(pass, engine->pipeline);
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            0U,
            engine->uniform_bind_group,
            0U,
            nullptr);
        wgpuRenderPassEncoderSetVertexBuffer(
            pass,
            0U,
            engine->vertex_buffer,
            0U,
            vertex_bytes);
        wgpuRenderPassEncoderDraw(
            pass,
            static_cast<std::uint32_t>(engine->vertices.size()),
            1U,
            0U,
            0U);
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    if (use_group_layer) {
        engine->last_layer_metrics.content_pass_count = 1U;
        if (!encode_group_effect(
                *engine,
                encoder,
                draw_state,
                frame->dpi_scale) ||
            !encode_layer_composite(
                *engine,
                encoder,
                reinterpret_cast<WGPUTextureView>(frame->target_view),
                frame->clear_color,
                draw_state)) {
            wgpuCommandEncoderRelease(encoder);
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The group composite pass could not be created.");
        }
    }

    WGPUCommandBufferDescriptor command_descriptor{};
    command_descriptor.label = progpu::native::webgpu::string_view("ProGPU native frame commands");
    WGPUCommandBuffer command = wgpuCommandEncoderFinish(
        encoder,
        &command_descriptor);
    wgpuCommandEncoderRelease(encoder);
    if (command == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native frame command buffer could not be finished.");
    }

    engine->submit(command);
    wgpuCommandBufferRelease(command);
    if (use_group_layer) {
        retain_group_layer_content(
            *engine,
            layer_family::solid,
            frame->dpi_scale,
            draw_state);
    }
    engine->last_error.clear();

    if (metrics != nullptr &&
        metrics->struct_size >= sizeof(progpu_native_frame_metrics)) {
        metrics->draw_call_count = engine->vertices.empty() ||
            draw_state.opacity == 0.0F ||
            (!use_group_layer && !draw_state.has_drawable_clip)
            ? 0U
            : 1U;
        metrics->vertex_count =
            static_cast<std::uint32_t>(engine->vertices.size());
        metrics->vertex_upload_bytes = vertex_bytes;
        metrics->uniform_upload_bytes = uploaded_uniforms
            ? sizeof(uniforms)
            : 0U;
        metrics->submission_count = engine->submission_count;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status render_analytic(
    progpu_native_engine* engine,
    const progpu_native_analytic_frame* frame,
    progpu_native_analytic_frame_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    clear_metrics(metrics);
    if (engine == nullptr || frame == nullptr ||
        frame->struct_size < offsetof(progpu_native_analytic_frame, draw_state) ||
        frame->width == 0U || frame->height == 0U ||
        !std::isfinite(frame->dpi_scale) || frame->dpi_scale <= 0.0F ||
        frame->target_view == 0U ||
        (frame->primitive_count != 0U && frame->primitives == nullptr) ||
        !progpu::native::is_finite(frame->clear_color)) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The analytic frame descriptor is invalid.");
    }
    resolved_draw_state draw_state{};
    const auto* requested_draw_state =
        frame->struct_size >= sizeof(progpu_native_analytic_frame)
            ? frame->draw_state
            : nullptr;
    if (!resolve_draw_state(
            requested_draw_state,
            frame->target_view,
            frame->width,
            frame->height,
            frame->dpi_scale,
            draw_state)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The analytic frame draw state is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer must be used from its owner thread.");
    }
    engine->release_semantic_render_bundle();
    reset_layer_metrics(*engine);
    engine->geometry_cache_valid = false;
    engine->geometry_gpu_cache_valid = false;
    if (frame->primitive_count >
            std::numeric_limits<std::uint32_t>::max() / 6U ||
        frame->primitive_count >
            std::numeric_limits<std::size_t>::max() / 6U) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The analytic primitive batch is too large.");
    }
    bool use_group_layer = false;
    bool group_cache_hit = false;
    const auto group_status = prepare_group_layer(
        *engine,
        layer_family::analytic,
        frame->width,
        frame->height,
        frame->dpi_scale,
        reinterpret_cast<WGPUTextureView>(frame->target_view),
        frame->clear_color,
        draw_state,
        use_group_layer,
        group_cache_hit);
    if (group_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        return group_status;
    }
    if (group_cache_hit) {
        if (metrics != nullptr && metrics->struct_size >=
                sizeof(progpu_native_analytic_frame_metrics)) {
            metrics->submission_count = engine->submission_count;
        }
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }

    try {
        engine->vertices.clear();
        engine->indices.clear();
        engine->vertices.reserve(frame->primitive_count * 4U);
        engine->indices.reserve(frame->primitive_count * 6U);
        for (std::size_t index = 0;
             index < frame->primitive_count;
             ++index) {
            float minimum_scale = 0.0F;
            if (!progpu::native::try_get_minimum_scale(
                    frame->primitives[index].transform,
                    minimum_scale)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "An analytic primitive has a non-invertible affine transform.");
            }
            const float local_padding =
                antialias_padding_pixels / minimum_scale;
            if (!progpu::native::append_analytic_primitive(
                    frame->primitives[index],
                    local_padding,
                    engine->vertices,
                    engine->indices)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "An analytic primitive contains invalid geometry, color, or flags.");
            }
        }
        multiply_vertex_alpha(engine->vertices, draw_state.opacity);
    } catch (const std::bad_alloc&) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The native analytic batch could not be allocated.");
    }

    const std::uint64_t vertex_bytes =
        engine->vertices.size() * sizeof(progpu::native::vector_vertex);
    const std::uint64_t index_bytes =
        engine->indices.size() * sizeof(std::uint32_t);
    bool uploaded_uniforms = false;
    if (vertex_bytes != 0U) {
        if (engine->analytic_pipeline == nullptr &&
            !create_analytic_pipeline(*engine)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native analytic WebGPU pipeline could not be created.");
        }
        if (!engine->ensure_vertex_buffer(vertex_bytes) ||
            !engine->ensure_index_buffer(index_bytes)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native analytic WebGPU buffers could not be allocated.");
        }

        const gpu_uniforms uniforms = create_uniforms(
            frame->width,
            frame->height,
            frame->dpi_scale);
        uploaded_uniforms = engine->upload_uniform_if_changed(
            engine->analytic_uniform_buffer,
            uniforms,
            engine->cached_analytic_uniforms,
            engine->analytic_uniform_cache_valid);
        wgpuQueueWriteBuffer(
            engine->queue,
            engine->vertex_buffer,
            0U,
            engine->vertices.data(),
            static_cast<std::size_t>(vertex_bytes));
        wgpuQueueWriteBuffer(
            engine->queue,
            engine->index_buffer,
            0U,
            engine->indices.data(),
            static_cast<std::size_t>(index_bytes));
    }

    const bool owns_encoder = engine->semantic_encoder == nullptr;
    WGPUCommandEncoder encoder = engine->semantic_encoder;
    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic frame encoder");
    if (owns_encoder) {
        encoder = wgpuDeviceCreateCommandEncoder(
            engine->device,
            &encoder_descriptor);
    }
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native analytic command encoder could not be created.");
    }

    WGPURenderPassColorAttachment color_attachment{};
    progpu::native::webgpu::initialize_color_attachment(color_attachment);
    color_attachment.view = use_group_layer
        ? engine->layer_texture_view
        : reinterpret_cast<WGPUTextureView>(frame->target_view);
    color_attachment.loadOp = !use_group_layer &&
            engine->semantic_load_target
        ? WGPULoadOp_Load
        : WGPULoadOp_Clear;
    color_attachment.storeOp = WGPUStoreOp_Store;
    color_attachment.clearValue = use_group_layer
        ? WGPUColor{0.0, 0.0, 0.0, 0.0}
        : WGPUColor{
            frame->clear_color.r,
            frame->clear_color.g,
            frame->clear_color.b,
            frame->clear_color.a};
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = progpu::native::webgpu::string_view("ProGPU native indexed analytic primitive pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &color_attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        if (owns_encoder) {
            wgpuCommandEncoderRelease(encoder);
        }
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native analytic render pass could not be created.");
    }

    if (!engine->indices.empty() && draw_state.opacity != 0.0F &&
        (use_group_layer || draw_state.has_drawable_clip)) {
        if (!use_group_layer) {
            apply_scissor(pass, draw_state);
        }
        wgpuRenderPassEncoderSetPipeline(pass, engine->analytic_pipeline);
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            0U,
            engine->analytic_uniform_bind_group,
            0U,
            nullptr);
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            1U,
            engine->analytic_atlas_bind_group,
            0U,
            nullptr);
        wgpuRenderPassEncoderSetVertexBuffer(
            pass,
            0U,
            engine->vertex_buffer,
            0U,
            vertex_bytes);
        wgpuRenderPassEncoderSetIndexBuffer(
            pass,
            engine->index_buffer,
            WGPUIndexFormat_Uint32,
            0U,
            index_bytes);
        wgpuRenderPassEncoderDrawIndexed(
            pass,
            static_cast<std::uint32_t>(engine->indices.size()),
            1U,
            0U,
            0,
            0U);
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    if (use_group_layer) {
        engine->last_layer_metrics.content_pass_count = 1U;
        if (!encode_group_effect(
                *engine,
                encoder,
                draw_state,
                frame->dpi_scale) ||
            !encode_layer_composite(
                *engine,
                encoder,
                reinterpret_cast<WGPUTextureView>(frame->target_view),
                frame->clear_color,
                draw_state)) {
            if (owns_encoder) {
                wgpuCommandEncoderRelease(encoder);
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The analytic group composite pass could not be created.");
        }
    }

    if (owns_encoder) {
        WGPUCommandBufferDescriptor command_descriptor{};
        command_descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic frame commands");
        WGPUCommandBuffer command = wgpuCommandEncoderFinish(
            encoder,
            &command_descriptor);
        wgpuCommandEncoderRelease(encoder);
        if (command == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native analytic command buffer could not be finished.");
        }

        engine->submit(command);
        wgpuCommandBufferRelease(command);
    }
    if (use_group_layer) {
        retain_group_layer_content(
            *engine,
            layer_family::analytic,
            frame->dpi_scale,
            draw_state);
    }
    engine->last_error.clear();

    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_analytic_frame_metrics)) {
        metrics->draw_call_count = engine->indices.empty() ||
            draw_state.opacity == 0.0F ||
            (!use_group_layer && !draw_state.has_drawable_clip)
            ? 0U
            : 1U;
        metrics->vertex_count =
            static_cast<std::uint32_t>(engine->vertices.size());
        metrics->index_count =
            static_cast<std::uint32_t>(engine->indices.size());
        metrics->vertex_upload_bytes = vertex_bytes;
        metrics->index_upload_bytes = index_bytes;
        metrics->uniform_upload_bytes = uploaded_uniforms
            ? sizeof(gpu_uniforms)
            : 0U;
        metrics->submission_count = engine->submission_count;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status render_geometry(
    progpu_native_engine* engine,
    const progpu_native_geometry_frame* frame,
    progpu_native_geometry_frame_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    clear_metrics(metrics);
    if (engine == nullptr || frame == nullptr ||
        frame->struct_size < offsetof(progpu_native_geometry_frame, draw_state) ||
        frame->width == 0U || frame->height == 0U ||
        !std::isfinite(frame->dpi_scale) || frame->dpi_scale <= 0.0F ||
        frame->target_view == 0U ||
        (frame->primitive_count != 0U && frame->primitives == nullptr) ||
        (frame->point_count != 0U && frame->points == nullptr) ||
        (frame->polyline_count != 0U && frame->polylines == nullptr) ||
        (frame->spline_count != 0U && frame->points == nullptr) ||
        (frame->double_count != 0U && frame->doubles == nullptr) ||
        (frame->dash_style_count != 0U && frame->dash_styles == nullptr) ||
        (frame->spline_count != 0U && frame->splines == nullptr) ||
        (frame->flags &
            ~(PROGPU_NATIVE_GEOMETRY_FRAME_CAPTURE_PAYLOAD_HASH |
              PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD)) != 0U ||
        (((frame->flags &
                PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD) != 0U) !=
            (frame->reserved != 0U)) ||
        !progpu::native::is_finite(frame->clear_color)) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The geometry frame descriptor is invalid.");
    }
    resolved_draw_state draw_state{};
    const auto* requested_draw_state =
        frame->struct_size >= sizeof(progpu_native_geometry_frame)
            ? frame->draw_state
            : nullptr;
    if (!resolve_draw_state(
            requested_draw_state,
            frame->target_view,
            frame->width,
            frame->height,
            frame->dpi_scale,
            draw_state)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The geometry frame draw state is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer must be used from its owner thread.");
    }
    engine->release_semantic_render_bundle();
    reset_layer_metrics(*engine);
    engine->path_gpu_cache_valid = false;
    if (frame->primitive_count > (1U << 24U) ||
        frame->polyline_count > (1U << 24U) ||
        frame->spline_count > (1U << 24U) ||
        frame->dash_style_count > (1U << 24U) ||
        frame->point_count > (1U << 28U) ||
        frame->double_count > (1U << 28U) ||
        frame->primitive_count >
            std::numeric_limits<std::uint32_t>::max() / 6U ||
        frame->primitive_count >
            std::numeric_limits<std::size_t>::max() / 6U ||
        frame->primitive_count >
            std::numeric_limits<std::size_t>::max() / gpu_brush_size ||
        frame->polyline_count >
            std::numeric_limits<std::size_t>::max() / gpu_brush_size ||
        frame->spline_count >
            std::numeric_limits<std::size_t>::max() / gpu_brush_size ||
        frame->primitive_count >
            std::numeric_limits<std::size_t>::max() - frame->polyline_count ||
        frame->primitive_count + frame->polyline_count >
            std::numeric_limits<std::size_t>::max() - frame->spline_count ||
        frame->primitive_count + frame->polyline_count +
            frame->spline_count > (1U << 24U)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The geometry primitive batch is too large.");
    }
    bool use_group_layer = false;
    bool group_cache_hit = false;
    const auto group_status = prepare_group_layer(
        *engine,
        layer_family::geometry,
        frame->width,
        frame->height,
        frame->dpi_scale,
        reinterpret_cast<WGPUTextureView>(frame->target_view),
        frame->clear_color,
        draw_state,
        use_group_layer,
        group_cache_hit);
    if (group_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        return group_status;
    }
    if (group_cache_hit) {
        if (metrics != nullptr && metrics->struct_size >=
                sizeof(progpu_native_geometry_frame_metrics)) {
            metrics->submission_count = engine->submission_count;
        }
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }

    const bool retain_compiled_payload =
        (frame->flags &
            PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD) != 0U;
    const bool compiled_payload_hit = retain_compiled_payload &&
        engine->geometry_cache_valid &&
        engine->geometry_content_revision == frame->reserved;
    if (!compiled_payload_hit) {
        engine->geometry_cache_valid = false;
        engine->geometry_gpu_cache_valid = false;
        try {
        engine->vertices.clear();
        engine->indices.clear();
        engine->primitive_brush_indices.clear();
        engine->polyline_brush_indices.clear();
        engine->spline_brush_indices.clear();
        engine->spline_segment_counts.clear();
        for (std::size_t index = 0U;
             index < frame->dash_style_count;
             ++index) {
            const auto& style = frame->dash_styles[index];
            if (style.interval_count == 0U ||
                style.interval_offset > frame->double_count ||
                style.interval_count >
                    frame->double_count - style.interval_offset ||
                !std::isfinite(style.offset) ||
                style.cap > PROGPU_NATIVE_STROKE_CAP_TRIANGLE ||
                style.reserved != 0U) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A dash style range, offset, cap, or reserved field is invalid.");
            }
            for (std::size_t interval = 0U;
                 interval < style.interval_count;
                 ++interval) {
                const double value =
                    frame->doubles[style.interval_offset + interval];
                if (!std::isfinite(value) || value < 0.0) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A dash interval is negative or not finite.");
                }
            }
        }
        std::size_t vertex_capacity = 0U;
        std::size_t index_capacity = 0U;
        for (std::size_t index = 0; index < frame->primitive_count; ++index) {
            std::size_t vertices_to_add = 0U;
            std::size_t indices_to_add = 0U;
            if (!progpu::native::geometry_primitive_capacity(
                    frame->primitives[index],
                    vertices_to_add,
                    indices_to_add) ||
                vertex_capacity >
                    std::numeric_limits<std::uint32_t>::max() - vertices_to_add ||
                vertex_capacity >
                    std::numeric_limits<std::size_t>::max() - vertices_to_add ||
                index_capacity >
                    std::numeric_limits<std::size_t>::max() - indices_to_add) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "The geometry primitive batch exceeds the indexed upload limits.");
            }
            vertex_capacity += vertices_to_add;
            index_capacity += indices_to_add;
        }
        for (std::size_t index = 0; index < frame->polyline_count; ++index) {
            const auto& polyline = frame->polylines[index];
            std::size_t vertices_to_add = 0U;
            std::size_t indices_to_add = 0U;
            if (polyline.point_offset > frame->point_count ||
                polyline.point_count >
                    frame->point_count - polyline.point_offset ||
                !progpu::native::polyline_capacity(
                    polyline,
                    frame->points + polyline.point_offset,
                    frame->dash_styles,
                    frame->dash_style_count,
                    frame->doubles,
                    frame->double_count,
                    vertices_to_add,
                    indices_to_add) ||
                vertex_capacity >
                    std::numeric_limits<std::uint32_t>::max() -
                        vertices_to_add ||
                vertex_capacity >
                    std::numeric_limits<std::size_t>::max() -
                        vertices_to_add ||
                index_capacity >
                    std::numeric_limits<std::size_t>::max() -
                        indices_to_add) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A connected stroke range exceeds the point arena or indexed upload limits.");
            }
            vertex_capacity += vertices_to_add;
            index_capacity += indices_to_add;
        }
        std::size_t maximum_spline_degree = 0U;
        for (std::size_t index = 0U; index < frame->spline_count; ++index) {
            if (frame->splines[index].degree > (1U << 20U)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A spline degree exceeds the native safety bound.");
            }
            maximum_spline_degree = std::max(
                maximum_spline_degree,
                static_cast<std::size_t>(frame->splines[index].degree));
        }
        engine->spline_work.reserve(maximum_spline_degree + 1U);
        engine->spline_segment_counts.resize(frame->spline_count);
        for (std::size_t index = 0; index < frame->spline_count; ++index) {
            const auto& spline = frame->splines[index];
            const auto& stroke = spline.stroke;
            std::size_t segment_count = 0U;
            std::size_t vertices_to_add = 0U;
            std::size_t indices_to_add = 0U;
            if (stroke.point_offset > frame->point_count ||
                stroke.point_count >
                    frame->point_count - stroke.point_offset ||
                spline.knot_offset > frame->double_count ||
                spline.knot_count >
                    frame->double_count - spline.knot_offset ||
                spline.weight_offset > frame->double_count ||
                spline.weight_count >
                    frame->double_count - spline.weight_offset ||
                !progpu::native::spline_capacity(
                    spline,
                    frame->points + stroke.point_offset,
                    spline.knot_count == 0U
                        ? nullptr
                        : frame->doubles + spline.knot_offset,
                    spline.weight_count == 0U
                        ? nullptr
                        : frame->doubles + spline.weight_offset,
                    frame->dash_styles,
                    frame->dash_style_count,
                    frame->doubles,
                    frame->double_count,
                    segment_count,
                    engine->spline_sampled_points,
                    engine->spline_work,
                    vertices_to_add,
                    indices_to_add) ||
                vertex_capacity >
                    std::numeric_limits<std::uint32_t>::max() -
                        vertices_to_add ||
                vertex_capacity >
                    std::numeric_limits<std::size_t>::max() -
                        vertices_to_add ||
                index_capacity >
                    std::numeric_limits<std::size_t>::max() -
                        indices_to_add) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A spline range, degree, or indexed upload bound is invalid.");
            }
            for (std::size_t knot = 0U; knot < spline.knot_count; ++knot) {
                if (!std::isfinite(frame->doubles[spline.knot_offset + knot])) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A spline knot is not finite.");
                }
            }
            for (std::size_t weight = 0U;
                 weight < spline.weight_count;
                 ++weight) {
                if (!std::isfinite(
                        frame->doubles[spline.weight_offset + weight])) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A spline weight is not finite.");
                }
            }
            engine->spline_segment_counts[index] = segment_count;
            vertex_capacity += vertices_to_add;
            index_capacity += indices_to_add;
        }
        engine->vertices.reserve(vertex_capacity);
        engine->indices.reserve(index_capacity);
        engine->primitive_brush_indices.resize(frame->primitive_count);
        engine->polyline_brush_indices.resize(frame->polyline_count);
        engine->spline_brush_indices.resize(frame->spline_count);
        std::uint32_t brush_count = 1U;
        for (std::size_t index = 0; index < frame->primitive_count; ++index) {
            const std::uint32_t brush_index =
                progpu::native::geometry_uses_payload_brush(
                    frame->primitives[index])
                ? brush_count++
                : 0U;
            engine->primitive_brush_indices[index] = brush_index;
            if (!progpu::native::append_geometry_primitive(
                    frame->primitives[index],
                    static_cast<float>(brush_index),
                    engine->vertices,
                    engine->indices)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A geometry primitive contains invalid points, stroke state, color, transform, or flags.");
            }
        }
        for (std::size_t index = 0; index < frame->polyline_count; ++index) {
            const std::uint32_t brush_index = brush_count++;
            engine->polyline_brush_indices[index] = brush_index;
            const auto& polyline = frame->polylines[index];
            if (!progpu::native::append_polyline(
                    polyline,
                    frame->points + polyline.point_offset,
                    static_cast<float>(brush_index),
                    engine->vertices,
                    engine->indices,
                    frame->dash_styles,
                    frame->dash_style_count,
                    frame->doubles,
                    frame->double_count,
                    true)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A connected stroke contains invalid points, stroke state, transform, join, or flags.");
            }
        }
        for (std::size_t index = 0; index < frame->spline_count; ++index) {
            const auto& spline = frame->splines[index];
            const std::size_t segment_count =
                engine->spline_segment_counts[index];
            const std::uint32_t brush_index = segment_count == 0U
                ? 0U
                : brush_count++;
            engine->spline_brush_indices[index] = brush_index;
            if (!progpu::native::append_spline(
                    spline,
                    frame->points + spline.stroke.point_offset,
                    spline.knot_count == 0U
                        ? nullptr
                        : frame->doubles + spline.knot_offset,
                    spline.weight_count == 0U
                        ? nullptr
                        : frame->doubles + spline.weight_offset,
                    segment_count,
                    static_cast<float>(brush_index),
                    engine->spline_sampled_points,
                    engine->spline_work,
                    engine->vertices,
                    engine->indices,
                    frame->dash_styles,
                    frame->dash_style_count,
                    frame->doubles,
                    frame->double_count)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A spline contains invalid control points, knots, weights, stroke state, or transform.");
            }
        }

        engine->brush_bytes.clear();
        engine->brush_bytes.resize(
            static_cast<std::size_t>(brush_count) * gpu_brush_size);
        set_brush_opacity(engine->brush_bytes, draw_state.opacity);
        for (std::size_t index = 0; index < frame->primitive_count; ++index) {
            const std::uint32_t brush_index =
                engine->primitive_brush_indices[index];
            if (brush_index == 0U) {
                continue;
            }
            std::byte* brush = engine->brush_bytes.data() +
                static_cast<std::size_t>(brush_index) * gpu_brush_size;
            std::memcpy(
                brush + 64U,
                &frame->primitives[index].color,
                sizeof(progpu_native_color));
        }
        for (std::size_t index = 0; index < frame->polyline_count; ++index) {
            const std::uint32_t brush_index =
                engine->polyline_brush_indices[index];
            std::byte* brush = engine->brush_bytes.data() +
                static_cast<std::size_t>(brush_index) * gpu_brush_size;
            std::memcpy(
                brush + 64U,
                &frame->polylines[index].color,
                sizeof(progpu_native_color));
        }
        for (std::size_t index = 0; index < frame->spline_count; ++index) {
            const std::uint32_t brush_index =
                engine->spline_brush_indices[index];
            if (brush_index == 0U) {
                continue;
            }
            std::byte* brush = engine->brush_bytes.data() +
                static_cast<std::size_t>(brush_index) * gpu_brush_size;
            std::memcpy(
                brush + 64U,
                &frame->splines[index].stroke.color,
                sizeof(progpu_native_color));
        }
        if (retain_compiled_payload) {
            engine->geometry_content_revision = frame->reserved;
            engine->geometry_opacity = draw_state.opacity;
            engine->geometry_payload_hash = 14695981039346656037ULL;
            engine->geometry_payload_hash = append_fnv1a64(
                engine->geometry_payload_hash,
                engine->vertices.data(),
                engine->vertices.size() *
                    sizeof(progpu::native::vector_vertex));
            engine->geometry_payload_hash = append_fnv1a64(
                engine->geometry_payload_hash,
                engine->indices.data(),
                engine->indices.size() * sizeof(std::uint32_t));
            engine->geometry_payload_hash = append_fnv1a64(
                engine->geometry_payload_hash,
                engine->brush_bytes.data(),
                engine->brush_bytes.size());
            engine->geometry_cache_valid = true;
        }
        } catch (const std::bad_alloc&) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native geometry batch could not be allocated.");
        }
    }

    const bool opacity_changed = compiled_payload_hit &&
        engine->geometry_opacity != draw_state.opacity;
    if (opacity_changed) {
        set_brush_opacity(engine->brush_bytes, draw_state.opacity);
        engine->geometry_opacity = draw_state.opacity;
        engine->geometry_payload_hash = 14695981039346656037ULL;
        engine->geometry_payload_hash = append_fnv1a64(
            engine->geometry_payload_hash,
            engine->vertices.data(),
            engine->vertices.size() *
                sizeof(progpu::native::vector_vertex));
        engine->geometry_payload_hash = append_fnv1a64(
            engine->geometry_payload_hash,
            engine->indices.data(),
            engine->indices.size() * sizeof(std::uint32_t));
        engine->geometry_payload_hash = append_fnv1a64(
            engine->geometry_payload_hash,
            engine->brush_bytes.data(),
            engine->brush_bytes.size());
    }

    const std::uint64_t vertex_bytes =
        engine->vertices.size() * sizeof(progpu::native::vector_vertex);
    const std::uint64_t index_bytes =
        engine->indices.size() * sizeof(std::uint32_t);
    const std::uint64_t brush_upload_bytes = engine->brush_bytes.size();
    const bool upload_compiled_payload =
        !compiled_payload_hit || !engine->geometry_gpu_cache_valid;
    const bool upload_brush_payload =
        upload_compiled_payload || opacity_changed;
    bool uploaded_uniforms = false;
    std::uint64_t payload_hash = 0U;
    if ((frame->flags &
            PROGPU_NATIVE_GEOMETRY_FRAME_CAPTURE_PAYLOAD_HASH) != 0U &&
        retain_compiled_payload && engine->geometry_cache_valid) {
        payload_hash = engine->geometry_payload_hash;
    } else if ((frame->flags &
            PROGPU_NATIVE_GEOMETRY_FRAME_CAPTURE_PAYLOAD_HASH) != 0U) {
        payload_hash = 14695981039346656037ULL;
        payload_hash = append_fnv1a64(
            payload_hash,
            engine->vertices.data(),
            static_cast<std::size_t>(vertex_bytes));
        payload_hash = append_fnv1a64(
            payload_hash,
            engine->indices.data(),
            static_cast<std::size_t>(index_bytes));
        payload_hash = append_fnv1a64(
            payload_hash,
            engine->brush_bytes.data(),
            engine->brush_bytes.size());
    }
    if (vertex_bytes != 0U) {
        if (engine->analytic_pipeline == nullptr &&
            !create_analytic_pipeline(*engine)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native indexed geometry WebGPU pipeline could not be created.");
        }
        if (!engine->ensure_vertex_buffer(vertex_bytes) ||
            !engine->ensure_index_buffer(index_bytes) ||
            !ensure_analytic_brush_buffer(*engine, brush_upload_bytes)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native indexed geometry WebGPU buffers could not be allocated.");
        }

        const gpu_uniforms uniforms = create_uniforms(
            frame->width,
            frame->height,
            frame->dpi_scale);
        uploaded_uniforms = engine->upload_uniform_if_changed(
            engine->analytic_uniform_buffer,
            uniforms,
            engine->cached_analytic_uniforms,
            engine->analytic_uniform_cache_valid);
        if (upload_compiled_payload) {
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->vertex_buffer,
                0U,
                engine->vertices.data(),
                static_cast<std::size_t>(vertex_bytes));
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->index_buffer,
                0U,
                engine->indices.data(),
                static_cast<std::size_t>(index_bytes));
            engine->geometry_gpu_cache_valid = retain_compiled_payload;
        }
        if (upload_brush_payload) {
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->analytic_brush_buffer,
                0U,
                engine->brush_bytes.data(),
                engine->brush_bytes.size());
        }
    }

    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view("ProGPU native geometry frame encoder");
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
        engine->device,
        &encoder_descriptor);
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native geometry command encoder could not be created.");
    }

    WGPURenderPassColorAttachment color_attachment{};
    progpu::native::webgpu::initialize_color_attachment(color_attachment);
    color_attachment.view = use_group_layer
        ? engine->layer_texture_view
        : reinterpret_cast<WGPUTextureView>(frame->target_view);
    color_attachment.loadOp = !use_group_layer &&
            engine->semantic_load_target
        ? WGPULoadOp_Load
        : WGPULoadOp_Clear;
    color_attachment.storeOp = WGPUStoreOp_Store;
    color_attachment.clearValue = use_group_layer
        ? WGPUColor{0.0, 0.0, 0.0, 0.0}
        : WGPUColor{
            frame->clear_color.r,
            frame->clear_color.g,
            frame->clear_color.b,
            frame->clear_color.a};
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = progpu::native::webgpu::string_view("ProGPU native indexed geometry pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &color_attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        wgpuCommandEncoderRelease(encoder);
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native geometry render pass could not be created.");
    }

    if (!engine->indices.empty() && draw_state.opacity != 0.0F &&
        (use_group_layer || draw_state.has_drawable_clip)) {
        if (!use_group_layer) {
            apply_scissor(pass, draw_state);
        }
        wgpuRenderPassEncoderSetPipeline(pass, engine->analytic_pipeline);
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            0U,
            engine->analytic_uniform_bind_group,
            0U,
            nullptr);
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            1U,
            engine->analytic_atlas_bind_group,
            0U,
            nullptr);
        wgpuRenderPassEncoderSetVertexBuffer(
            pass,
            0U,
            engine->vertex_buffer,
            0U,
            vertex_bytes);
        wgpuRenderPassEncoderSetIndexBuffer(
            pass,
            engine->index_buffer,
            WGPUIndexFormat_Uint32,
            0U,
            index_bytes);
        wgpuRenderPassEncoderDrawIndexed(
            pass,
            static_cast<std::uint32_t>(engine->indices.size()),
            1U,
            0U,
            0,
            0U);
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    if (use_group_layer) {
        engine->last_layer_metrics.content_pass_count = 1U;
        if (!encode_group_effect(
                *engine,
                encoder,
                draw_state,
                frame->dpi_scale) ||
            !encode_layer_composite(
                *engine,
                encoder,
                reinterpret_cast<WGPUTextureView>(frame->target_view),
                frame->clear_color,
                draw_state)) {
            wgpuCommandEncoderRelease(encoder);
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The geometry group composite pass could not be created.");
        }
    }

    WGPUCommandBufferDescriptor command_descriptor{};
    command_descriptor.label = progpu::native::webgpu::string_view("ProGPU native geometry frame commands");
    WGPUCommandBuffer command = wgpuCommandEncoderFinish(
        encoder,
        &command_descriptor);
    wgpuCommandEncoderRelease(encoder);
    if (command == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native geometry command buffer could not be finished.");
    }

    engine->submit(command);
    wgpuCommandBufferRelease(command);
    if (use_group_layer) {
        retain_group_layer_content(
            *engine,
            layer_family::geometry,
            frame->dpi_scale,
            draw_state);
    }
    engine->last_error.clear();

    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_geometry_frame_metrics)) {
        metrics->draw_call_count = engine->indices.empty() ||
            draw_state.opacity == 0.0F ||
            (!use_group_layer && !draw_state.has_drawable_clip)
            ? 0U
            : 1U;
        metrics->vertex_count =
            static_cast<std::uint32_t>(engine->vertices.size());
        metrics->index_count =
            static_cast<std::uint32_t>(engine->indices.size());
        metrics->vertex_upload_bytes = upload_compiled_payload
            ? vertex_bytes
            : 0U;
        metrics->index_upload_bytes = upload_compiled_payload
            ? index_bytes
            : 0U;
        metrics->brush_upload_bytes =
            engine->indices.empty() || !upload_brush_payload
                ? 0U
                : brush_upload_bytes;
        metrics->uniform_upload_bytes = uploaded_uniforms
            ? sizeof(gpu_uniforms)
            : 0U;
        metrics->submission_count = engine->submission_count;
        metrics->payload_hash = payload_hash;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

} // namespace progpu::native::execution
