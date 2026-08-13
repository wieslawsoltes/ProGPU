#include "progpu_native_frame_execution_common.hpp"

namespace progpu::native::execution {

progpu_native_status render_paths(
    progpu_native_engine* engine,
    const progpu_native_path_frame* frame,
    progpu_native_path_frame_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    clear_metrics(metrics);
    if (engine == nullptr || frame == nullptr ||
        frame->struct_size < offsetof(progpu_native_path_frame, draw_state) ||
        frame->width == 0U || frame->height == 0U ||
        !std::isfinite(frame->dpi_scale) || frame->dpi_scale <= 0.0F ||
        frame->target_view == 0U ||
        (frame->path_count != 0U && frame->paths == nullptr) ||
        (frame->segment_count != 0U && frame->segments == nullptr) ||
        (frame->flags &
            ~(PROGPU_NATIVE_GEOMETRY_FRAME_CAPTURE_PAYLOAD_HASH |
              PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD)) != 0U ||
        (((frame->flags &
                PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD) != 0U) !=
            (frame->content_revision != 0U)) ||
        !progpu::native::is_finite(frame->clear_color)) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The path frame descriptor is invalid.");
    }
    resolved_draw_state draw_state{};
    const auto* requested_draw_state =
        frame->struct_size >= sizeof(progpu_native_path_frame)
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
            "The path frame draw state is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer must be used from its owner thread.");
    }
    if (!engine->semantic_path_draw_active) {
        engine->release_semantic_render_bundle();
        engine->semantic_path_gpu_scene_hash = 0U;
    }
    reset_layer_metrics(*engine);
    if (frame->path_count > (1U << 20U) ||
        frame->segment_count > (1U << 24U) ||
        frame->path_count >
            std::numeric_limits<std::uint32_t>::max() / 6U) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The path batch exceeds the native safety bound.");
    }
    const bool semantic_materials =
        engine->semantic_path_materials_active;
    if (semantic_materials &&
        engine->semantic_path_cache.brush_indices.size() !=
            frame->path_count) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic path brush map does not match the retained path page.");
    }
    bool use_group_layer = false;
    bool group_cache_hit = false;
    const auto group_status = prepare_group_layer(
        *engine,
        layer_family::path,
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
                sizeof(progpu_native_path_frame_metrics)) {
            metrics->submission_count = engine->submission_count;
        }
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }

    const bool retain_compiled_payload =
        (frame->flags &
            PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD) != 0U;
    const bool compiled_payload_hit = retain_compiled_payload &&
        engine->path_cache_valid &&
        engine->path_content_revision == frame->content_revision &&
        engine->path_dpi_scale == frame->dpi_scale;
    std::uint64_t coverage_staging_bytes = 0U;
    std::uint64_t path_upload_bytes = 0U;
    std::uint32_t rasterized_path_count = 0U;
    std::uint32_t required_atlas_size = engine->path_atlas_size;

    std::vector<gpu_path_uniforms> path_uniforms;
    std::vector<gpu_path_record> path_records;
    if (!compiled_payload_hit) {
        engine->path_cache_valid = false;
        engine->path_gpu_cache_valid = false;
        try {
            engine->path_vertices.clear();
            engine->path_indices.clear();
            engine->path_brush_bytes.clear();
            engine->path_rasters.clear();
            path_uniforms.reserve(frame->path_count);
            path_records.reserve(frame->path_count);
            engine->path_rasters.reserve(frame->path_count);
            engine->path_vertices.reserve(frame->path_count * 4U);
            engine->path_indices.reserve(frame->path_count * 6U);
            if (!semantic_materials) {
                engine->path_brush_bytes.resize(
                    (frame->path_count + 1U) * gpu_brush_size);
                set_brush_opacity(
                    engine->path_brush_bytes,
                    draw_state.opacity);
            }

            std::uint32_t atlas_x = 2U;
            std::uint32_t atlas_y = 2U;
            std::uint32_t row_height = 0U;
            std::uint32_t output_offset = 0U;
            std::unordered_map<
                native_path_cache_key,
                std::size_t,
                native_path_cache_key_hash> retained_tiles;
            retained_tiles.reserve(frame->path_count);
            for (std::size_t segment_index = 0U;
                 segment_index < frame->segment_count;
                 ++segment_index) {
                const auto& segment = frame->segments[segment_index];
                const bool is_arc =
                    segment.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC;
                if (segment.kind > PROGPU_NATIVE_PATH_SEGMENT_ARC ||
                    !progpu::native::is_finite(segment.p0) ||
                    !progpu::native::is_finite(segment.p1) ||
                    !progpu::native::is_finite(segment.p2) ||
                    !progpu::native::is_finite(segment.p3) ||
                    (is_arc &&
                        (segment.p3.x <= 0.0F || segment.p3.y <= 0.0F ||
                         !std::isfinite(std::bit_cast<float>(segment.pad0)) ||
                         !std::isfinite(std::bit_cast<float>(segment.pad1)) ||
                         !std::isfinite(std::bit_cast<float>(segment.pad2)))) ||
                    (!is_arc &&
                        (segment.pad0 != 0U || segment.pad1 != 0U ||
                         segment.pad2 != 0U))) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A path segment kind, point, arc, or reserved field is invalid.");
                }
            }
            for (std::size_t index = 0U;
                 index < frame->path_count;
                 ++index) {
                const auto& path = frame->paths[index];
                if (path.segment_count == 0U ||
                    path.segment_offset > frame->segment_count ||
                    path.segment_count >
                        frame->segment_count - path.segment_offset ||
                    !std::isfinite(path.min_x) ||
                    !std::isfinite(path.min_y) ||
                    !std::isfinite(path.max_x) ||
                    !std::isfinite(path.max_y) ||
                    path.max_x <= path.min_x ||
                    path.max_y <= path.min_y ||
                    !progpu::native::is_finite(path.color) ||
                    !progpu::native::is_finite(path.transform) ||
                    path.fill_rule > PROGPU_NATIVE_FILL_RULE_EVEN_ODD ||
                    (path.sample_grid != 4U && path.sample_grid != 8U)) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A path range, bound, transform, fill rule, or sample grid is invalid.");
                }
                float maximum_scale = 0.0F;
                float minimum_scale = 0.0F;
                if (!progpu::native::try_get_stroke_scales(
                        path.transform,
                        maximum_scale,
                        minimum_scale)) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A path transform is singular.");
                }
                (void)minimum_scale;
                const float raster_scale = maximum_scale;
                const float subpixel_x = quantize_subpixel_phase(
                    path.transform.m31);
                const float subpixel_y = quantize_subpixel_phase(
                    path.transform.m32);
                native_path_cache_key cache_key{};
                cache_key.segment_offset = path.segment_offset;
                cache_key.segment_count = path.segment_count;
                cache_key.min_x = std::bit_cast<std::uint32_t>(path.min_x);
                cache_key.min_y = std::bit_cast<std::uint32_t>(path.min_y);
                cache_key.max_x = std::bit_cast<std::uint32_t>(path.max_x);
                cache_key.max_y = std::bit_cast<std::uint32_t>(path.max_y);
                cache_key.scale = std::bit_cast<std::uint32_t>(raster_scale);
                cache_key.subpixel_x =
                    std::bit_cast<std::uint32_t>(subpixel_x);
                cache_key.subpixel_y =
                    std::bit_cast<std::uint32_t>(subpixel_y);
                cache_key.fill_rule = path.fill_rule;
                cache_key.sample_grid = path.sample_grid;
                const float raster_min_x =
                    std::floor(path.min_x * raster_scale) - path_padding;
                const float raster_min_y =
                    std::floor(path.min_y * raster_scale) - path_padding;
                const float raster_max_x =
                    std::ceil(path.max_x * raster_scale) + path_padding;
                const float raster_max_y =
                    std::ceil(path.max_y * raster_scale) + path_padding;
                const double raster_width = raster_max_x - raster_min_x;
                const double raster_height = raster_max_y - raster_min_y;
                if (!std::isfinite(raster_width) ||
                    !std::isfinite(raster_height) ||
                    raster_width <= 0.0 || raster_height <= 0.0 ||
                    raster_width > native_max_atlas_size - 4U ||
                    raster_height > native_max_atlas_size - 4U) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_UNSUPPORTED,
                        "A transformed path exceeds the bounded native atlas tile size.");
                }
                std::size_t raster_index = 0U;
                const auto retained_tile = retained_tiles.find(cache_key);
                if (retained_tile != retained_tiles.end()) {
                    raster_index = retained_tile->second;
                } else {
                    const auto width =
                        static_cast<std::uint32_t>(raster_width);
                    const auto height =
                        static_cast<std::uint32_t>(raster_height);
                    while (width + 4U > required_atlas_size &&
                           required_atlas_size < native_max_atlas_size) {
                        required_atlas_size *= 2U;
                    }
                    if (atlas_x + width + 2U > required_atlas_size) {
                        atlas_x = 2U;
                        atlas_y += row_height + 2U;
                        row_height = 0U;
                    }
                    while (atlas_y + height + 2U > required_atlas_size &&
                           required_atlas_size < native_max_atlas_size) {
                        required_atlas_size *= 2U;
                    }
                    if (atlas_y + height + 2U > required_atlas_size) {
                        return engine->fail(
                            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                            "The retained native path set does not fit the bounded atlas.");
                    }
                    const std::uint32_t output_bytes_per_row = align_up(
                        width,
                        webgpu_copy_row_alignment);
                    output_offset = align_up(
                        output_offset,
                        webgpu_copy_row_alignment);
                    const std::uint64_t next_output =
                        static_cast<std::uint64_t>(output_offset) +
                        static_cast<std::uint64_t>(output_bytes_per_row) * height;
                    if (next_output >
                        std::numeric_limits<std::uint32_t>::max()) {
                        return engine->fail(
                            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                            "The path coverage staging batch exceeds 4 GiB.");
                    }
                    raster_index = engine->path_rasters.size();
                    engine->path_rasters.push_back({
                        atlas_x,
                        atlas_y,
                        width,
                        height,
                        output_offset,
                        output_bytes_per_row,
                        raster_scale,
                        raster_scale,
                        raster_min_x,
                        raster_min_y,
                        subpixel_x,
                        subpixel_y
                    });
                    path_uniforms.push_back({
                        raster_min_x - subpixel_x,
                        raster_min_y - subpixel_y,
                        raster_scale,
                        raster_scale,
                        static_cast<std::uint32_t>(raster_index),
                        output_offset / 4U,
                        output_bytes_per_row / 4U,
                        width,
                        height,
                        path.sample_grid,
                        0U,
                        0U
                    });
                    path_records.push_back({
                        static_cast<std::uint32_t>(path.segment_offset),
                        static_cast<std::uint32_t>(path.segment_count),
                        path.min_x,
                        path.min_y,
                        path.max_x,
                        path.max_y,
                        path.fill_rule,
                        0U
                    });
                    retained_tiles.emplace(cache_key, raster_index);
                    output_offset = static_cast<std::uint32_t>(next_output);
                    atlas_x += width + 2U;
                    row_height = std::max(row_height, height);
                }
                const auto& raster = engine->path_rasters[raster_index];

                const float local_min_x = raster_min_x / raster_scale;
                const float local_min_y = raster_min_y / raster_scale;
                const float local_max_x = raster_max_x / raster_scale;
                const float local_max_y = raster_max_y / raster_scale;
                const std::array<progpu_native_point, 4U> local_points{{
                    {local_min_x, local_min_y},
                    {local_max_x, local_min_y},
                    {local_max_x, local_max_y},
                    {local_min_x, local_max_y}
                }};
                const std::array<progpu_native_point, 4U> atlas_points{{
                    {raster.atlas_x + subpixel_x, raster.atlas_y + subpixel_y},
                    {raster.atlas_x + raster.width + subpixel_x, raster.atlas_y + subpixel_y},
                    {raster.atlas_x + raster.width + subpixel_x, raster.atlas_y + raster.height + subpixel_y},
                    {raster.atlas_x + subpixel_x, raster.atlas_y + raster.height + subpixel_y}
                }};
                const std::uint32_t vertex_start = static_cast<std::uint32_t>(
                    engine->path_vertices.size());
                const std::uint32_t brush_index = semantic_materials
                    ? engine->semantic_path_cache.brush_indices[index]
                    : static_cast<std::uint32_t>(index + 1U);
                for (std::size_t corner = 0U; corner < 4U; ++corner) {
                    progpu::native::vector_vertex vertex{};
                    progpu::native::transform_point(
                        path.transform,
                        local_points[corner].x,
                        local_points[corner].y,
                        vertex.position[0],
                        vertex.position[1]);
                    std::memcpy(
                        vertex.color,
                        &path.color,
                        sizeof(path.color));
                    vertex.texture_coordinate[0] = atlas_points[corner].x;
                    vertex.texture_coordinate[1] = atlas_points[corner].y;
                    vertex.brush_index = static_cast<float>(brush_index);
                    vertex.shape_size[0] = local_points[corner].x;
                    vertex.shape_size[1] = local_points[corner].y;
                    vertex.corner_radius = 1.0F;
                    vertex.shape_type = 4.0F;
                    engine->path_vertices.push_back(vertex);
                }
                engine->path_indices.insert(
                    engine->path_indices.end(),
                    {vertex_start, vertex_start + 1U, vertex_start + 2U,
                     vertex_start, vertex_start + 2U, vertex_start + 3U});
                if (!semantic_materials) {
                    std::memcpy(
                        engine->path_brush_bytes.data() +
                            (index + 1U) * gpu_brush_size + 64U,
                        &path.color,
                        sizeof(path.color));
                }

            }
            coverage_staging_bytes = output_offset;
            rasterized_path_count = static_cast<std::uint32_t>(
                engine->path_rasters.size());
            if (retain_compiled_payload) {
                engine->path_content_revision = frame->content_revision;
                engine->path_dpi_scale = frame->dpi_scale;
                engine->path_opacity = draw_state.opacity;
                engine->path_payload_hash = 14695981039346656037ULL;
                engine->path_payload_hash = append_fnv1a64(
                    engine->path_payload_hash,
                    engine->path_vertices.data(),
                    engine->path_vertices.size() *
                        sizeof(progpu::native::vector_vertex));
                engine->path_payload_hash = append_fnv1a64(
                    engine->path_payload_hash,
                    engine->path_indices.data(),
                    engine->path_indices.size() * sizeof(std::uint32_t));
                if (!semantic_materials) {
                    engine->path_payload_hash = append_fnv1a64(
                        engine->path_payload_hash,
                        engine->path_brush_bytes.data(),
                        engine->path_brush_bytes.size());
                }
                engine->path_cache_valid = true;
            }
        } catch (const std::bad_alloc&) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native path batch could not be allocated.");
        }
    }

    const bool opacity_changed = !semantic_materials &&
        compiled_payload_hit &&
        engine->path_opacity != draw_state.opacity;
    if (opacity_changed) {
        set_brush_opacity(engine->path_brush_bytes, draw_state.opacity);
        engine->path_opacity = draw_state.opacity;
        engine->path_payload_hash = 14695981039346656037ULL;
        engine->path_payload_hash = append_fnv1a64(
            engine->path_payload_hash,
            engine->path_vertices.data(),
            engine->path_vertices.size() *
                sizeof(progpu::native::vector_vertex));
        engine->path_payload_hash = append_fnv1a64(
            engine->path_payload_hash,
            engine->path_indices.data(),
            engine->path_indices.size() * sizeof(std::uint32_t));
        engine->path_payload_hash = append_fnv1a64(
            engine->path_payload_hash,
            engine->path_brush_bytes.data(),
            engine->path_brush_bytes.size());
    }

    const std::uint32_t atlas_generation_before =
        engine->path_atlas_generation;
    if (engine->path_atlas_texture == nullptr) {
        engine->path_atlas_size = required_atlas_size;
    }
    if (!create_path_resources(*engine) ||
        !resize_path_atlas(*engine, required_atlas_size)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native path atlas WebGPU resources could not be created.");
    }
    if (!compiled_payload_hit && frame->path_count != 0U &&
        engine->path_atlas_generation == atlas_generation_before) {
        ++engine->path_atlas_generation;
    }

    const std::uint64_t vertex_bytes = engine->path_vertices.size() *
        sizeof(progpu::native::vector_vertex);
    const std::uint64_t index_bytes = engine->path_indices.size() *
        sizeof(std::uint32_t);
    const std::uint64_t brush_bytes = engine->path_brush_bytes.size();
    const bool upload_draw_payload =
        !compiled_payload_hit || !engine->path_gpu_cache_valid;
    const bool upload_brush_payload = !semantic_materials &&
        (upload_draw_payload || opacity_changed ||
            engine->analytic_material_owner_hash != 0U);
    bool uploaded_uniforms = false;
    if (vertex_bytes != 0U &&
        (!engine->ensure_path_vertex_buffer(vertex_bytes) ||
         !engine->ensure_path_index_buffer(index_bytes) ||
         (!semantic_materials &&
            !ensure_analytic_brush_buffer(*engine, brush_bytes)))) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The native path draw buffers could not be allocated.");
    }
    const gpu_uniforms uniforms = create_uniforms(
        frame->width,
        frame->height,
        frame->dpi_scale);
    if (vertex_bytes != 0U) {
        uploaded_uniforms = engine->upload_uniform_if_changed(
            engine->analytic_uniform_buffer,
            uniforms,
            engine->cached_analytic_uniforms,
            engine->analytic_uniform_cache_valid);
        if (upload_draw_payload) {
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->path_vertex_buffer,
                0U,
                engine->path_vertices.data(),
                vertex_bytes);
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->path_index_buffer,
                0U,
                engine->path_indices.data(),
                index_bytes);
            engine->path_gpu_cache_valid = retain_compiled_payload;
            engine->geometry_gpu_cache_valid = false;
        }
        if (upload_brush_payload) {
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->analytic_brush_buffer,
                0U,
                engine->path_brush_bytes.data(),
                brush_bytes);
            engine->analytic_material_owner_hash = 0U;
        }
    }
    path_raster_resources temporary;
    WGPUBuffer& path_uniform_buffer = temporary.uniforms;
    WGPUBuffer& path_record_buffer = temporary.records;
    WGPUBuffer& path_segment_buffer = temporary.segments;
    WGPUBuffer& coverage_buffer = temporary.coverage;
    WGPUBindGroup& raster_bind_group = temporary.bind_group;
    const auto create_buffer = [&](
        const char* label,
        std::uint64_t size,
        progpu::native::webgpu::buffer_usage_flags usage) -> WGPUBuffer {
        WGPUBufferDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(label);
        descriptor.size = std::max<std::uint64_t>(size, 4U);
        descriptor.usage = usage;
        return wgpuDeviceCreateBuffer(engine->device, &descriptor);
    };
    if (!compiled_payload_hit && frame->path_count != 0U) {
        path_uniform_buffer = create_buffer(
            "ProGPU native path uniforms",
            path_uniforms.size() * sizeof(gpu_path_uniforms),
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        path_record_buffer = create_buffer(
            "ProGPU native path records",
            path_records.size() * sizeof(gpu_path_record),
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        path_segment_buffer = create_buffer(
            "ProGPU native path segments",
            frame->segment_count * sizeof(progpu_native_path_segment),
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        coverage_buffer = create_buffer(
            "ProGPU native path coverage staging",
            coverage_staging_bytes,
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopySrc);
        if (path_uniform_buffer == nullptr || path_record_buffer == nullptr ||
            path_segment_buffer == nullptr || coverage_buffer == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native path raster staging buffers could not be allocated.");
        }
        const std::uint64_t uniform_bytes = path_uniforms.size() *
            sizeof(gpu_path_uniforms);
        const std::uint64_t record_bytes = path_records.size() *
            sizeof(gpu_path_record);
        const std::uint64_t segment_bytes = frame->segment_count *
            sizeof(progpu_native_path_segment);
        wgpuQueueWriteBuffer(engine->queue, path_uniform_buffer, 0U,
            path_uniforms.data(), uniform_bytes);
        wgpuQueueWriteBuffer(engine->queue, path_record_buffer, 0U,
            path_records.data(), record_bytes);
        wgpuQueueWriteBuffer(engine->queue, path_segment_buffer, 0U,
            frame->segments, segment_bytes);
        path_upload_bytes = uniform_bytes + record_bytes + segment_bytes;

        const std::array<WGPUBindGroupEntry, 4U> entries{{
            {nullptr, 0U, path_uniform_buffer, 0U, uniform_bytes,
                nullptr, nullptr},
            {nullptr, 1U, path_record_buffer, 0U, record_bytes,
                nullptr, nullptr},
            {nullptr, 2U, path_segment_buffer, 0U, segment_bytes,
                nullptr, nullptr},
            {nullptr, 3U, coverage_buffer, 0U, coverage_staging_bytes,
                nullptr, nullptr}
        }};
        WGPUBindGroupDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view("ProGPU native path raster bind group");
        descriptor.layout = engine->path_raster_layout;
        descriptor.entryCount = entries.size();
        descriptor.entries = entries.data();
        raster_bind_group = wgpuDeviceCreateBindGroup(
            engine->device,
            &descriptor);
        if (raster_bind_group == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native path raster bind group could not be created.");
        }
    }

    const bool owns_encoder = engine->semantic_encoder == nullptr;
    WGPUCommandEncoder encoder = engine->semantic_encoder;
    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained path frame encoder");
    if (owns_encoder) {
        encoder = wgpuDeviceCreateCommandEncoder(
            engine->device,
            &encoder_descriptor);
    }
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native path command encoder could not be created.");
    }

    if (raster_bind_group != nullptr) {
        std::uint32_t workgroups_x = 0U;
        std::uint32_t workgroups_y = 0U;
        for (const auto& raster : engine->path_rasters) {
            workgroups_x = std::max(
                workgroups_x,
                (raster.width + 63U) / 64U);
            workgroups_y = std::max(
                workgroups_y,
                (raster.height + 15U) / 16U);
        }
        WGPUComputePassDescriptor compute_descriptor{};
        compute_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path coverage pass");
        WGPUComputePassEncoder compute_pass =
            wgpuCommandEncoderBeginComputePass(encoder, &compute_descriptor);
        if (compute_pass == nullptr) {
            if (owns_encoder) {
                wgpuCommandEncoderRelease(encoder);
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native path compute pass could not be created.");
        }
        wgpuComputePassEncoderSetPipeline(
            compute_pass,
            engine->path_raster_pipeline);
        wgpuComputePassEncoderSetBindGroup(
            compute_pass,
            0U,
            raster_bind_group,
            0U,
            nullptr);
        wgpuComputePassEncoderDispatchWorkgroups(
            compute_pass,
            workgroups_x,
            workgroups_y,
            static_cast<std::uint32_t>(engine->path_rasters.size()));
        wgpuComputePassEncoderEnd(compute_pass);
        wgpuComputePassEncoderRelease(compute_pass);

        for (const auto& raster : engine->path_rasters) {
            progpu::native::webgpu::image_copy_buffer source{};
            source.buffer = coverage_buffer;
            source.layout.offset = raster.output_offset;
            source.layout.bytesPerRow = raster.output_bytes_per_row;
            source.layout.rowsPerImage = raster.height;
            progpu::native::webgpu::image_copy_texture destination{};
            destination.texture = engine->path_atlas_texture;
            destination.origin = {raster.atlas_x, raster.atlas_y, 0U};
            destination.aspect = WGPUTextureAspect_All;
            const WGPUExtent3D extent{raster.width, raster.height, 1U};
            wgpuCommandEncoderCopyBufferToTexture(
                encoder,
                &source,
                &destination,
                &extent);
        }
    }

    const std::uint32_t selected_first_index =
        engine->semantic_path_draw_active
        ? engine->semantic_path_first_index
        : 0U;
    const std::uint32_t selected_index_count =
        engine->semantic_path_draw_active
        ? engine->semantic_path_index_count
        : static_cast<std::uint32_t>(engine->path_indices.size());
    if (!engine->semantic_prepare_only) {
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
    pass_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained path pass");
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
            "The native path render pass could not be created.");
    }
    if (selected_first_index > engine->path_indices.size() ||
        selected_index_count >
            engine->path_indices.size() - selected_first_index) {
        wgpuRenderPassEncoderEnd(pass);
        wgpuRenderPassEncoderRelease(pass);
        if (owns_encoder) {
            wgpuCommandEncoderRelease(encoder);
        }
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic path packed-page draw range is invalid.");
    }
    if (selected_index_count != 0U && draw_state.opacity != 0.0F &&
        (use_group_layer || draw_state.has_drawable_clip)) {
        if (!use_group_layer) {
            apply_scissor(pass, draw_state);
        }
        wgpuRenderPassEncoderSetPipeline(pass, engine->analytic_pipeline);
        wgpuRenderPassEncoderSetBindGroup(
            pass, 0U, engine->analytic_uniform_bind_group, 0U, nullptr);
        wgpuRenderPassEncoderSetBindGroup(
            pass, 1U, engine->path_atlas_bind_group, 0U, nullptr);
        wgpuRenderPassEncoderSetVertexBuffer(
            pass, 0U, engine->path_vertex_buffer, 0U, vertex_bytes);
        wgpuRenderPassEncoderSetIndexBuffer(
            pass,
            engine->path_index_buffer,
            WGPUIndexFormat_Uint32,
            0U,
            index_bytes);
        wgpuRenderPassEncoderDrawIndexed(
            pass,
            selected_index_count,
            1U,
            selected_first_index,
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
                "The path group composite pass could not be created.");
        }
    }

    if (owns_encoder) {
        WGPUCommandBufferDescriptor command_descriptor{};
        command_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained path commands");
        WGPUCommandBuffer command = wgpuCommandEncoderFinish(
            encoder,
            &command_descriptor);
        wgpuCommandEncoderRelease(encoder);
        if (command == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native path command buffer could not be finished.");
        }
        engine->submit(command);
        wgpuCommandBufferRelease(command);
    }
    if (use_group_layer) {
        retain_group_layer_content(
            *engine,
            layer_family::path,
            frame->dpi_scale,
            draw_state);
    }
    }

    std::uint64_t payload_hash = 0U;
    if ((frame->flags &
            PROGPU_NATIVE_GEOMETRY_FRAME_CAPTURE_PAYLOAD_HASH) != 0U) {
        payload_hash = retain_compiled_payload
            ? engine->path_payload_hash
            : append_fnv1a64(
                append_fnv1a64(
                    append_fnv1a64(
                        14695981039346656037ULL,
                        engine->path_vertices.data(),
                        vertex_bytes),
                    engine->path_indices.data(),
                    index_bytes),
                engine->path_brush_bytes.data(),
                engine->path_brush_bytes.size());
    }
    engine->last_error.clear();
    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_path_frame_metrics)) {
        metrics->draw_call_count = engine->semantic_prepare_only ||
            selected_index_count == 0U ||
            draw_state.opacity == 0.0F ||
            (!use_group_layer && !draw_state.has_drawable_clip)
            ? 0U
            : 1U;
        metrics->vertex_count = static_cast<std::uint32_t>(
            engine->path_vertices.size());
        metrics->index_count = static_cast<std::uint32_t>(
            engine->path_indices.size());
        metrics->rasterized_path_count = rasterized_path_count;
        metrics->atlas_width = engine->path_atlas_size;
        metrics->atlas_height = engine->path_atlas_size;
        metrics->atlas_generation = engine->path_atlas_generation;
        metrics->vertex_upload_bytes = upload_draw_payload ? vertex_bytes : 0U;
        metrics->index_upload_bytes = upload_draw_payload ? index_bytes : 0U;
        metrics->brush_upload_bytes = upload_brush_payload ? brush_bytes : 0U;
        metrics->path_upload_bytes = path_upload_bytes;
        metrics->coverage_staging_bytes = coverage_staging_bytes;
        metrics->uniform_upload_bytes = uploaded_uniforms
            ? sizeof(gpu_uniforms)
            : 0U;
        metrics->submission_count = engine->submission_count;
        metrics->payload_hash = payload_hash;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status render_glyphs(
    progpu_native_engine* engine,
    const progpu_native_glyph_frame* frame,
    progpu_native_glyph_frame_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    clear_metrics(metrics);
    if (engine == nullptr || frame == nullptr ||
        frame->struct_size < offsetof(progpu_native_glyph_frame, draw_state) ||
        frame->width == 0U || frame->height == 0U ||
        !std::isfinite(frame->dpi_scale) || frame->dpi_scale <= 0.0F ||
        frame->target_view == 0U ||
        (frame->outline_count != 0U && frame->outlines == nullptr) ||
        (frame->segment_count != 0U && frame->segments == nullptr) ||
        (frame->glyph_count != 0U && frame->glyphs == nullptr) ||
        (frame->flags &
            ~(PROGPU_NATIVE_GEOMETRY_FRAME_CAPTURE_PAYLOAD_HASH |
              PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD)) != 0U ||
        (((frame->flags &
                PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD) != 0U) !=
            (frame->content_revision != 0U)) ||
        !progpu::native::is_finite(frame->clear_color)) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The positioned glyph frame descriptor is invalid.");
    }
    resolved_draw_state draw_state{};
    const auto* requested_draw_state =
        frame->struct_size >= sizeof(progpu_native_glyph_frame)
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
            "The positioned glyph frame draw state is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer must be used from its owner thread.");
    }
    if (!engine->semantic_glyph_draw_active) {
        engine->release_semantic_render_bundle();
        engine->semantic_glyph_gpu_scene_hash = 0U;
    }
    reset_layer_metrics(*engine);
    if (frame->outline_count > (1U << 20U) ||
        frame->segment_count > (1U << 24U) ||
        frame->glyph_count > (1U << 24U)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The positioned glyph batch exceeds the native safety bound.");
    }
    bool use_group_layer = false;
    bool group_cache_hit = false;
    const auto group_status = prepare_group_layer(
        *engine,
        layer_family::glyph,
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
                sizeof(progpu_native_glyph_frame_metrics)) {
            metrics->submission_count = engine->submission_count;
        }
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }

    const bool retain_compiled_payload =
        (frame->flags &
            PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD) != 0U;
    const bool compiled_payload_hit = retain_compiled_payload &&
        engine->glyph_cache_valid &&
        engine->glyph_content_revision == frame->content_revision &&
        engine->glyph_dpi_scale == frame->dpi_scale;
    std::vector<gpu_glyph_record> records;
    std::vector<gpu_glyph_uniforms> uniforms;
    std::uint64_t coverage_staging_bytes = 0U;
    std::uint64_t outline_upload_bytes = 0U;
    std::uint32_t rasterized_glyph_count = 0U;
    std::uint32_t required_atlas_size = engine->glyph_atlas_size;

    if (!compiled_payload_hit) {
        engine->glyph_cache_valid = false;
        engine->glyph_gpu_cache_valid = false;
        try {
            records.reserve(frame->outline_count);
            uniforms.reserve(frame->outline_count);
            engine->glyph_rasters.clear();
            engine->glyph_rasters.reserve(frame->outline_count);
            engine->glyph_instances.clear();
            engine->glyph_instances.reserve(frame->glyph_count);
            engine->glyph_source_alphas.clear();
            engine->glyph_source_alphas.reserve(frame->glyph_count);

            for (std::size_t index = 0U;
                 index < frame->segment_count;
                 ++index) {
                const auto& segment = frame->segments[index];
                if (segment.kind > PROGPU_NATIVE_PATH_SEGMENT_CUBIC ||
                    !progpu::native::is_finite(segment.p0) ||
                    !progpu::native::is_finite(segment.p1) ||
                    !progpu::native::is_finite(segment.p2) ||
                    !progpu::native::is_finite(segment.p3) ||
                    segment.pad0 != 0U || segment.pad1 != 0U ||
                    segment.pad2 != 0U) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A glyph segment kind, point, or reserved field is invalid.");
                }
            }

            std::uint32_t atlas_x = 2U;
            std::uint32_t atlas_y = 2U;
            std::uint32_t row_height = 0U;
            std::uint32_t output_offset = 0U;
            for (std::size_t index = 0U;
                 index < frame->outline_count;
                 ++index) {
                const auto& outline = frame->outlines[index];
                if (outline.segment_count == 0U ||
                    outline.segment_offset > frame->segment_count ||
                    outline.segment_count >
                        frame->segment_count - outline.segment_offset ||
                    !std::isfinite(outline.min_x) ||
                    !std::isfinite(outline.min_y) ||
                    !std::isfinite(outline.max_x) ||
                    !std::isfinite(outline.max_y) ||
                    outline.max_x <= outline.min_x ||
                    outline.max_y <= outline.min_y ||
                    !std::isfinite(outline.raster_scale) ||
                    outline.raster_scale <= 0.0F ||
                    !std::isfinite(outline.subpixel_x) ||
                    outline.subpixel_x < 0.0F ||
                    outline.subpixel_x > 0.75F ||
                    std::abs(
                        outline.subpixel_x * 4.0F -
                        std::round(outline.subpixel_x * 4.0F)) > 0.0001F) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A glyph outline range, bound, scale, or phase is invalid.");
                }
                const float scaled_min_x =
                    outline.min_x * outline.raster_scale;
                const float scaled_min_y =
                    -outline.max_y * outline.raster_scale;
                const float scaled_max_x =
                    outline.max_x * outline.raster_scale;
                const float scaled_max_y =
                    -outline.min_y * outline.raster_scale;
                const float x_start = std::floor(scaled_min_x) - path_padding;
                const float y_start = std::floor(scaled_min_y) - path_padding;
                const double width_value =
                    std::ceil(scaled_max_x) + path_padding - x_start;
                const double height_value =
                    std::ceil(scaled_max_y) + path_padding - y_start;
                if (!std::isfinite(width_value) ||
                    !std::isfinite(height_value) ||
                    width_value <= 0.0 || height_value <= 0.0 ||
                    width_value > native_max_atlas_size - 4U ||
                    height_value > native_max_atlas_size - 4U) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_UNSUPPORTED,
                        "A glyph exceeds the bounded native atlas tile size.");
                }
                const auto width = static_cast<std::uint32_t>(width_value);
                const auto height = static_cast<std::uint32_t>(height_value);
                while (width + 4U > required_atlas_size &&
                       required_atlas_size < native_max_atlas_size) {
                    required_atlas_size *= 2U;
                }
                if (atlas_x + width + 2U > required_atlas_size) {
                    atlas_x = 2U;
                    atlas_y += row_height + 2U;
                    row_height = 0U;
                }
                while (atlas_y + height + 2U > required_atlas_size &&
                       required_atlas_size < native_max_atlas_size) {
                    required_atlas_size *= 2U;
                }
                if (atlas_y + height + 2U > required_atlas_size) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "The retained native glyph set does not fit the bounded atlas.");
                }
                const std::uint32_t output_bytes_per_row = align_up(
                    width,
                    webgpu_copy_row_alignment);
                output_offset = align_up(
                    output_offset,
                    webgpu_copy_row_alignment);
                const std::uint64_t next_output =
                    static_cast<std::uint64_t>(output_offset) +
                    static_cast<std::uint64_t>(output_bytes_per_row) * height;
                if (next_output >
                    std::numeric_limits<std::uint32_t>::max()) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "The glyph coverage staging batch exceeds 4 GiB.");
                }
                engine->glyph_rasters.push_back({
                    atlas_x,
                    atlas_y,
                    width,
                    height,
                    output_offset,
                    output_bytes_per_row,
                    x_start,
                    y_start
                });
                records.push_back({
                    static_cast<std::uint32_t>(outline.segment_offset),
                    static_cast<std::uint32_t>(outline.segment_count),
                    outline.min_x,
                    outline.min_y,
                    outline.max_x,
                    outline.max_y,
                    0U,
                    0U
                });
                uniforms.push_back({
                    x_start,
                    y_start,
                    outline.raster_scale,
                    static_cast<std::uint32_t>(index),
                    output_offset / 4U,
                    output_bytes_per_row / 4U,
                    width,
                    height,
                    outline.subpixel_x,
                    0.0F,
                    0.0F,
                    0.0F
                });
                output_offset = static_cast<std::uint32_t>(next_output);
                atlas_x += width + 2U;
                row_height = std::max(row_height, height);
            }

            for (std::size_t index = 0U;
                 index < frame->glyph_count;
                 ++index) {
                const auto& glyph = frame->glyphs[index];
                if (glyph.outline_index >= frame->outline_count ||
                    glyph.reserved != 0U || glyph.reserved2 != 0.0F ||
                    !progpu::native::is_finite(glyph.position) ||
                    !progpu::native::is_finite(glyph.basis_x) ||
                    !progpu::native::is_finite(glyph.basis_y) ||
                    !progpu::native::is_finite(glyph.color) ||
                    !std::isfinite(glyph.atlas_to_logical_scale) ||
                    glyph.atlas_to_logical_scale <= 0.0F ||
                    !std::isfinite(glyph.bold_offset) ||
                    !std::isfinite(glyph.italic_skew)) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A positioned glyph reference or presentation value is invalid.");
                }
                const auto& raster =
                    engine->glyph_rasters[glyph.outline_index];
                gpu_glyph_instance instance{};
                std::memcpy(
                    instance.snapped_logical_position,
                    &glyph.position,
                    sizeof(glyph.position));
                std::memcpy(
                    instance.basis_x,
                    &glyph.basis_x,
                    sizeof(glyph.basis_x));
                std::memcpy(
                    instance.basis_y,
                    &glyph.basis_y,
                    sizeof(glyph.basis_y));
                instance.bear_size[0] = raster.x_start;
                instance.bear_size[1] = raster.y_start;
                instance.bear_size[2] = static_cast<float>(raster.width);
                instance.bear_size[3] = static_cast<float>(raster.height);
                instance.texture_coordinates[0] =
                    static_cast<float>(raster.atlas_x);
                instance.texture_coordinates[1] =
                    static_cast<float>(raster.atlas_y);
                instance.texture_coordinates[2] =
                    static_cast<float>(raster.atlas_x + raster.width);
                instance.texture_coordinates[3] =
                    static_cast<float>(raster.atlas_y + raster.height);
                std::memcpy(
                    instance.color,
                    &glyph.color,
                    sizeof(glyph.color));
                instance.color[3] *= draw_state.opacity;
                instance.scale_bold_italic_flags[0] =
                    glyph.atlas_to_logical_scale;
                instance.scale_bold_italic_flags[1] = glyph.bold_offset;
                instance.scale_bold_italic_flags[2] = glyph.italic_skew;
                instance.scale_bold_italic_flags[3] = 0.0F;
                instance.brush_index = -1.0F;
                engine->glyph_instances.push_back(instance);
                engine->glyph_source_alphas.push_back(glyph.color.a);
            }

            coverage_staging_bytes = output_offset;
            rasterized_glyph_count = static_cast<std::uint32_t>(
                engine->glyph_rasters.size());
            if (retain_compiled_payload) {
                engine->glyph_content_revision = frame->content_revision;
                engine->glyph_dpi_scale = frame->dpi_scale;
                engine->glyph_opacity = draw_state.opacity;
                engine->glyph_payload_hash = append_fnv1a64(
                    14695981039346656037ULL,
                    engine->glyph_instances.data(),
                    engine->glyph_instances.size() *
                        sizeof(gpu_glyph_instance));
                engine->glyph_payload_hash = append_fnv1a64(
                    engine->glyph_payload_hash,
                    frame->outlines,
                    frame->outline_count *
                        sizeof(progpu_native_glyph_outline));
                engine->glyph_payload_hash = append_fnv1a64(
                    engine->glyph_payload_hash,
                    frame->segments,
                    frame->segment_count *
                        sizeof(progpu_native_path_segment));
                engine->glyph_cache_valid = true;
            }
        } catch (const std::bad_alloc&) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native positioned glyph batch could not be allocated.");
        }
    }

    const bool opacity_changed = compiled_payload_hit &&
        engine->glyph_opacity != draw_state.opacity;
    if (opacity_changed) {
        if (engine->glyph_source_alphas.size() !=
            engine->glyph_instances.size()) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The retained glyph opacity cache is inconsistent.");
        }
        for (std::size_t index = 0U;
             index < engine->glyph_instances.size();
             ++index) {
            engine->glyph_instances[index].color[3] =
                engine->glyph_source_alphas[index] * draw_state.opacity;
        }
        engine->glyph_opacity = draw_state.opacity;
        engine->glyph_payload_hash = append_fnv1a64(
            14695981039346656037ULL,
            engine->glyph_instances.data(),
            engine->glyph_instances.size() * sizeof(gpu_glyph_instance));
        engine->glyph_payload_hash = append_fnv1a64(
            engine->glyph_payload_hash,
            frame->outlines,
            frame->outline_count * sizeof(progpu_native_glyph_outline));
        engine->glyph_payload_hash = append_fnv1a64(
            engine->glyph_payload_hash,
            frame->segments,
            frame->segment_count * sizeof(progpu_native_path_segment));
    }

    const std::uint32_t atlas_generation_before =
        engine->glyph_atlas_generation;
    if (engine->glyph_atlas_texture == nullptr) {
        while (engine->glyph_atlas_size < required_atlas_size) {
            engine->glyph_atlas_size *= 2U;
            ++engine->glyph_atlas_growth_count;
        }
    }
    if (!create_glyph_resources(*engine) ||
        !resize_glyph_atlas(*engine, required_atlas_size)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native glyph atlas WebGPU resources could not be created.");
    }
    if (!compiled_payload_hit && frame->outline_count != 0U &&
        engine->glyph_atlas_generation == atlas_generation_before) {
        ++engine->glyph_atlas_generation;
    }
    const std::uint64_t instance_bytes = engine->glyph_instances.size() *
        sizeof(gpu_glyph_instance);
    const bool upload_instances =
        !compiled_payload_hit || !engine->glyph_gpu_cache_valid ||
        opacity_changed;
    if (instance_bytes != 0U &&
        !engine->ensure_text_vertex_buffer(instance_bytes)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The native positioned glyph instance buffer could not be allocated.");
    }
    bool uploaded_uniforms = false;
    if (instance_bytes != 0U) {
        const gpu_uniforms frame_uniforms = create_uniforms(
            frame->width,
            frame->height,
            frame->dpi_scale);
        uploaded_uniforms = engine->upload_uniform_if_changed(
            engine->analytic_uniform_buffer,
            frame_uniforms,
            engine->cached_analytic_uniforms,
            engine->analytic_uniform_cache_valid);
        if (upload_instances) {
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->text_vertex_buffer,
                0U,
                engine->glyph_instances.data(),
                instance_bytes);
            engine->glyph_gpu_cache_valid = retain_compiled_payload;
        }
    }
    path_raster_resources temporary;
    std::vector<std::byte> uniform_bytes;
    if (!compiled_payload_hit && frame->outline_count != 0U) {
        try {
            uniform_bytes.resize(frame->outline_count * 256U);
        } catch (const std::bad_alloc&) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The glyph uniform staging arena could not be allocated.");
        }
        for (std::size_t index = 0U; index < uniforms.size(); ++index) {
            std::memcpy(
                uniform_bytes.data() + index * 256U,
                &uniforms[index],
                sizeof(gpu_glyph_uniforms));
        }
        const auto create_buffer = [&engine](
            const char* label,
            std::uint64_t size,
            progpu::native::webgpu::buffer_usage_flags usage) -> WGPUBuffer {
            WGPUBufferDescriptor descriptor{};
            descriptor.label = progpu::native::webgpu::string_view(label);
            descriptor.size = std::max<std::uint64_t>(size, 4U);
            descriptor.usage = usage;
            return wgpuDeviceCreateBuffer(engine->device, &descriptor);
        };
        temporary.uniforms = create_buffer(
            "ProGPU native glyph uniform ring",
            uniform_bytes.size(),
            WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst);
        temporary.records = create_buffer(
            "ProGPU native glyph records",
            records.size() * sizeof(gpu_glyph_record),
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        temporary.segments = create_buffer(
            "ProGPU native glyph segments",
            frame->segment_count * sizeof(progpu_native_path_segment),
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        temporary.coverage = create_buffer(
            "ProGPU native glyph coverage staging",
            coverage_staging_bytes,
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopySrc);
        if (temporary.uniforms == nullptr || temporary.records == nullptr ||
            temporary.segments == nullptr || temporary.coverage == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native glyph raster staging buffers could not be allocated.");
        }
        const std::uint64_t record_bytes = records.size() *
            sizeof(gpu_glyph_record);
        const std::uint64_t segment_bytes = frame->segment_count *
            sizeof(progpu_native_path_segment);
        wgpuQueueWriteBuffer(
            engine->queue,
            temporary.uniforms,
            0U,
            uniform_bytes.data(),
            uniform_bytes.size());
        wgpuQueueWriteBuffer(
            engine->queue,
            temporary.records,
            0U,
            records.data(),
            record_bytes);
        wgpuQueueWriteBuffer(
            engine->queue,
            temporary.segments,
            0U,
            frame->segments,
            segment_bytes);
        outline_upload_bytes = uniform_bytes.size() +
            record_bytes + segment_bytes;
        const std::array<WGPUBindGroupEntry, 4U> entries{{
            {nullptr, 0U, temporary.uniforms, 0U,
                sizeof(gpu_glyph_uniforms), nullptr, nullptr},
            {nullptr, 1U, temporary.records, 0U,
                record_bytes, nullptr, nullptr},
            {nullptr, 2U, temporary.segments, 0U,
                segment_bytes, nullptr, nullptr},
            {nullptr, 3U, temporary.coverage, 0U,
                coverage_staging_bytes, nullptr, nullptr}
        }};
        WGPUBindGroupDescriptor bind_group_descriptor{};
        bind_group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native glyph raster bind group");
        bind_group_descriptor.layout = engine->glyph_raster_layout;
        bind_group_descriptor.entryCount = entries.size();
        bind_group_descriptor.entries = entries.data();
        temporary.bind_group = wgpuDeviceCreateBindGroup(
            engine->device,
            &bind_group_descriptor);
        if (temporary.bind_group == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native glyph raster bind group could not be created.");
        }
    }

    const bool owns_encoder = engine->semantic_encoder == nullptr;
    WGPUCommandEncoder encoder = engine->semantic_encoder;
    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view("ProGPU native positioned glyph frame encoder");
    if (owns_encoder) {
        encoder = wgpuDeviceCreateCommandEncoder(
            engine->device,
            &encoder_descriptor);
    }
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native positioned glyph command encoder could not be created.");
    }
    if (temporary.bind_group != nullptr) {
        WGPUComputePassDescriptor compute_descriptor{};
        compute_descriptor.label = progpu::native::webgpu::string_view("ProGPU native glyph coverage pass");
        WGPUComputePassEncoder compute_pass =
            wgpuCommandEncoderBeginComputePass(encoder, &compute_descriptor);
        if (compute_pass == nullptr) {
            if (owns_encoder) {
                wgpuCommandEncoderRelease(encoder);
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native glyph compute pass could not be created.");
        }
        wgpuComputePassEncoderSetPipeline(
            compute_pass,
            engine->glyph_raster_pipeline);
        for (std::uint32_t index = 0U;
             index < engine->glyph_rasters.size();
             ++index) {
            const std::uint32_t dynamic_offset = index * 256U;
            wgpuComputePassEncoderSetBindGroup(
                compute_pass,
                0U,
                temporary.bind_group,
                1U,
                &dynamic_offset);
            const auto& raster = engine->glyph_rasters[index];
            wgpuComputePassEncoderDispatchWorkgroups(
                compute_pass,
                (raster.width + 63U) / 64U,
                (raster.height + 15U) / 16U,
                1U);
        }
        wgpuComputePassEncoderEnd(compute_pass);
        wgpuComputePassEncoderRelease(compute_pass);
        for (const auto& raster : engine->glyph_rasters) {
            progpu::native::webgpu::image_copy_buffer source{};
            source.buffer = temporary.coverage;
            source.layout.offset = raster.output_offset;
            source.layout.bytesPerRow = raster.output_bytes_per_row;
            source.layout.rowsPerImage = raster.height;
            progpu::native::webgpu::image_copy_texture destination{};
            destination.texture = engine->glyph_atlas_texture;
            destination.origin = {raster.atlas_x, raster.atlas_y, 0U};
            destination.aspect = WGPUTextureAspect_All;
            const WGPUExtent3D extent{raster.width, raster.height, 1U};
            wgpuCommandEncoderCopyBufferToTexture(
                encoder,
                &source,
                &destination,
                &extent);
        }
    }

    const std::uint32_t selected_first_instance =
        engine->semantic_glyph_draw_active
        ? engine->semantic_glyph_first_instance
        : 0U;
    const std::uint32_t selected_instance_count =
        engine->semantic_glyph_draw_active
        ? engine->semantic_glyph_instance_count
        : static_cast<std::uint32_t>(engine->glyph_instances.size());
    if (!engine->semantic_prepare_only) {
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
    pass_descriptor.label = progpu::native::webgpu::string_view("ProGPU native positioned glyph pass");
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
            "The native positioned glyph render pass could not be created.");
    }
    if (selected_first_instance > engine->glyph_instances.size() ||
        selected_instance_count >
            engine->glyph_instances.size() - selected_first_instance) {
        wgpuRenderPassEncoderEnd(pass);
        wgpuRenderPassEncoderRelease(pass);
        if (owns_encoder) {
            wgpuCommandEncoderRelease(encoder);
        }
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic glyph packed-page draw range is invalid.");
    }
    if (selected_instance_count != 0U && draw_state.opacity != 0.0F &&
        (use_group_layer || draw_state.has_drawable_clip)) {
        if (!use_group_layer) {
            apply_scissor(pass, draw_state);
        }
        wgpuRenderPassEncoderSetPipeline(pass, engine->text_pipeline);
        wgpuRenderPassEncoderSetBindGroup(
            pass, 0U, engine->text_uniform_bind_group, 0U, nullptr);
        wgpuRenderPassEncoderSetBindGroup(
            pass, 1U, engine->text_atlas_bind_group, 0U, nullptr);
        wgpuRenderPassEncoderSetVertexBuffer(
            pass,
            0U,
            engine->text_vertex_buffer,
            0U,
            instance_bytes);
        wgpuRenderPassEncoderDraw(
            pass,
            6U,
            selected_instance_count,
            0U,
            selected_first_instance);
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
                "The glyph group composite pass could not be created.");
        }
    }
    if (owns_encoder) {
        WGPUCommandBufferDescriptor command_descriptor{};
        command_descriptor.label = progpu::native::webgpu::string_view("ProGPU native positioned glyph commands");
        WGPUCommandBuffer command = wgpuCommandEncoderFinish(
            encoder,
            &command_descriptor);
        wgpuCommandEncoderRelease(encoder);
        if (command == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native positioned glyph command buffer could not be finished.");
        }
        engine->submit(command);
        wgpuCommandBufferRelease(command);
    }
    if (use_group_layer) {
        retain_group_layer_content(
            *engine,
            layer_family::glyph,
            frame->dpi_scale,
            draw_state);
    }
    }

    std::uint64_t payload_hash = 0U;
    if ((frame->flags &
            PROGPU_NATIVE_GEOMETRY_FRAME_CAPTURE_PAYLOAD_HASH) != 0U) {
        payload_hash = retain_compiled_payload
            ? engine->glyph_payload_hash
            : append_fnv1a64(
                14695981039346656037ULL,
                engine->glyph_instances.data(),
                instance_bytes);
    }
    engine->last_error.clear();
    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_glyph_frame_metrics)) {
        metrics->draw_call_count = engine->semantic_prepare_only ||
            selected_instance_count == 0U ||
            draw_state.opacity == 0.0F ||
            (!use_group_layer && !draw_state.has_drawable_clip)
            ? 0U
            : 1U;
        metrics->glyph_count = selected_instance_count;
        metrics->rasterized_glyph_count = rasterized_glyph_count;
        metrics->atlas_width = engine->glyph_atlas_size;
        metrics->atlas_height = engine->glyph_atlas_size;
        metrics->atlas_generation = engine->glyph_atlas_generation;
        metrics->atlas_growth_count = engine->glyph_atlas_growth_count;
        metrics->instance_upload_bytes = upload_instances
            ? instance_bytes
            : 0U;
        metrics->outline_upload_bytes = outline_upload_bytes;
        metrics->coverage_staging_bytes = coverage_staging_bytes;
        metrics->uniform_upload_bytes = uploaded_uniforms
            ? sizeof(gpu_uniforms)
            : 0U;
        metrics->submission_count = engine->submission_count;
        metrics->payload_hash = payload_hash;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status render_image(
    progpu_native_engine* engine,
    const progpu_native_image_frame* frame,
    progpu_native_image_frame_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    clear_metrics(metrics);
    const auto valid_rect = [](const progpu_native_image_rect& rect) {
        return std::isfinite(rect.x) && std::isfinite(rect.y) &&
            std::isfinite(rect.width) && std::isfinite(rect.height) &&
            rect.width > 0.0F && rect.height > 0.0F;
    };
    const bool has_mask = frame != nullptr &&
        frame->external_mask_view != 0U;
    const bool empty_mask_descriptor = frame != nullptr &&
        frame->mask_width == 0U && frame->mask_height == 0U &&
        frame->mask_revision == 0U && frame->mask_sampling == 0U &&
        frame->mask_destination_rect.x == 0.0F &&
        frame->mask_destination_rect.y == 0.0F &&
        frame->mask_destination_rect.width == 0.0F &&
        frame->mask_destination_rect.height == 0.0F;
    if (engine == nullptr || frame == nullptr ||
        frame->struct_size < offsetof(progpu_native_image_frame, draw_state) ||
        frame->width == 0U || frame->height == 0U ||
        !std::isfinite(frame->dpi_scale) || frame->dpi_scale <= 0.0F ||
        frame->target_view == 0U ||
        frame->image_width == 0U || frame->image_height == 0U ||
        frame->image_width > 16384U || frame->image_height > 16384U ||
        (frame->source_flags &
            ~PROGPU_NATIVE_IMAGE_SOURCE_EXTERNAL_VIEW) != 0U ||
        (((frame->source_flags &
                PROGPU_NATIVE_IMAGE_SOURCE_EXTERNAL_VIEW) == 0U) &&
            frame->row_bytes < frame->image_width * 4U) ||
        (((frame->source_flags &
                PROGPU_NATIVE_IMAGE_SOURCE_EXTERNAL_VIEW) != 0U) &&
            (frame->external_source_view == 0U ||
             frame->rgba_pixels != nullptr || frame->pixel_bytes != 0U)) ||
        frame->sampling > PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR ||
        (has_mask &&
            (frame->mask_width == 0U || frame->mask_height == 0U ||
             frame->mask_width > 16384U || frame->mask_height > 16384U ||
             frame->mask_revision == 0U ||
             frame->mask_sampling > PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR ||
             !valid_rect(frame->mask_destination_rect))) ||
        (!has_mask && !empty_mask_descriptor) ||
        frame->image_revision == 0U || frame->content_revision == 0U ||
        !valid_rect(frame->source_rect) ||
        !valid_rect(frame->destination_rect) ||
        frame->source_rect.x < 0.0F || frame->source_rect.y < 0.0F ||
        frame->source_rect.x + frame->source_rect.width >
            static_cast<float>(frame->image_width) ||
        frame->source_rect.y + frame->source_rect.height >
            static_cast<float>(frame->image_height) ||
        !progpu::native::is_finite(frame->transform) ||
        !std::isfinite(frame->opacity) ||
        frame->opacity < 0.0F || frame->opacity > 1.0F ||
        frame->reserved != 0U || frame->reserved2 != 0U ||
        !progpu::native::is_finite(frame->clear_color)) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The retained RGBA image frame descriptor is invalid.");
    }
    resolved_draw_state draw_state{};
    const auto* requested_draw_state =
        frame->struct_size >= sizeof(progpu_native_image_frame)
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
            "The retained image frame draw state is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer must be used from its owner thread.");
    }
    engine->release_semantic_render_bundle();
    reset_layer_metrics(*engine);

    const bool created_resources = engine->image_pipeline == nullptr;
    bool use_group_layer = false;
    bool group_cache_hit = false;
    const auto group_status = prepare_group_layer(
        *engine,
        layer_family::image,
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
                sizeof(progpu_native_image_frame_metrics)) {
            metrics->submission_count = engine->submission_count;
        }
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }

    const bool external = (frame->source_flags &
        PROGPU_NATIVE_IMAGE_SOURCE_EXTERNAL_VIEW) != 0U;
    const std::uint64_t required_upload_bytes = external
        ? 0U
        : static_cast<std::uint64_t>(frame->row_bytes) *
                (frame->image_height - 1U) +
            static_cast<std::uint64_t>(frame->image_width) * 4U;
    const bool dimensions_changed = engine->image_texture_view != nullptr &&
        (engine->image_width != frame->image_width ||
         engine->image_height != frame->image_height);
    const bool upload_texture = engine->image_texture_view == nullptr ||
        engine->image_revision != frame->image_revision ||
        engine->image_source_is_external != external ||
        dimensions_changed ||
        (external && engine->image_texture_view !=
            reinterpret_cast<WGPUTextureView>(frame->external_source_view));
    if ((!upload_texture && dimensions_changed) ||
        (!external && upload_texture &&
            (frame->rgba_pixels == nullptr ||
             frame->pixel_bytes < required_upload_bytes))) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The retained RGBA image revision or pixel payload is invalid.");
    }

    if (!create_image_resources(*engine)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained RGBA image GPU resources could not be created.");
    }
    if (upload_texture && !upload_image_texture(*engine, *frame)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained RGBA image texture could not be uploaded.");
    }
    bool uploaded_mask_uniforms = false;
    if (has_mask &&
        (!create_image_mask_resources(*engine) ||
         !update_image_mask(*engine, *frame, uploaded_mask_uniforms))) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained image mask resources could not be prepared.");
    }

    const bool compiled_payload_hit = engine->image_cache_valid &&
        engine->image_content_revision == frame->content_revision &&
        engine->image_draw_opacity == draw_state.opacity &&
        !dimensions_changed;
    if (!compiled_payload_hit) {
        const float x0 = frame->destination_rect.x;
        const float y0 = frame->destination_rect.y;
        const float x1 = x0 + frame->destination_rect.width;
        const float y1 = y0 + frame->destination_rect.height;
        const float u0 = frame->source_rect.x /
            static_cast<float>(frame->image_width);
        const float v0 = frame->source_rect.y /
            static_cast<float>(frame->image_height);
        const float u1 = (frame->source_rect.x + frame->source_rect.width) /
            static_cast<float>(frame->image_width);
        const float v1 = (frame->source_rect.y + frame->source_rect.height) /
            static_cast<float>(frame->image_height);
        constexpr std::array<std::array<std::uint32_t, 2U>, 4U> corners{{
            {0U, 0U}, {1U, 0U}, {1U, 1U}, {0U, 1U}
        }};
        for (std::size_t index = 0U; index < corners.size(); ++index) {
            const float x = corners[index][0] == 0U ? x0 : x1;
            const float y = corners[index][1] == 0U ? y0 : y1;
            auto& vertex = engine->image_vertices[index];
            progpu::native::transform_point(
                frame->transform,
                x,
                y,
                vertex.position[0],
                vertex.position[1]);
            vertex.color[0] = 1.0F;
            vertex.color[1] = 0.0F;
            vertex.color[2] = 1.0F;
            vertex.color[3] = frame->opacity * draw_state.opacity;
            vertex.texture_coordinate[0] = corners[index][0] == 0U ? u0 : u1;
            vertex.texture_coordinate[1] = corners[index][1] == 0U ? v0 : v1;
            vertex.brush_index = 0.0F;
            vertex.shape_size[0] = 0.0F;
            vertex.shape_size[1] = 0.5F;
            vertex.corner_radius = 0.0F;
            vertex.stroke_thickness = 1.0F;
            vertex.shape_type = 0.0F;
        }
        engine->image_payload_hash = append_fnv1a64(
            14695981039346656037ULL,
            engine->image_vertices.data(),
            sizeof(engine->image_vertices));
        engine->image_content_revision = frame->content_revision;
        engine->image_draw_opacity = draw_state.opacity;
        engine->image_cache_valid = true;
        engine->image_gpu_cache_valid = false;
    }

    const bool upload_vertices = !engine->image_gpu_cache_valid;
    if (upload_vertices) {
        wgpuQueueWriteBuffer(
            engine->queue,
            engine->image_vertex_buffer,
            0U,
            engine->image_vertices.data(),
            sizeof(engine->image_vertices));
        engine->image_gpu_cache_valid = true;
    }
    const gpu_uniforms uniforms = create_uniforms(
        frame->width,
        frame->height,
        frame->dpi_scale);
    const bool uploaded_uniforms = engine->upload_uniform_if_changed(
        engine->image_uniform_buffer,
        uniforms,
        engine->cached_image_uniforms,
        engine->image_uniform_cache_valid);

    const bool owns_encoder = engine->semantic_encoder == nullptr;
    WGPUCommandEncoder encoder = engine->semantic_encoder;
    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained RGBA image encoder");
    if (owns_encoder) {
        encoder = wgpuDeviceCreateCommandEncoder(
            engine->device,
            &encoder_descriptor);
    }
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained RGBA image command encoder could not be created.");
    }
    WGPURenderPassColorAttachment attachment{};
    progpu::native::webgpu::initialize_color_attachment(attachment);
    attachment.view = use_group_layer
        ? engine->layer_texture_view
        : reinterpret_cast<WGPUTextureView>(frame->target_view);
    attachment.loadOp = !use_group_layer && engine->semantic_load_target
        ? WGPULoadOp_Load
        : WGPULoadOp_Clear;
    attachment.storeOp = WGPUStoreOp_Store;
    attachment.clearValue = use_group_layer
        ? WGPUColor{0.0, 0.0, 0.0, 0.0}
        : WGPUColor{
            frame->clear_color.r,
            frame->clear_color.g,
            frame->clear_color.b,
            frame->clear_color.a};
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained RGBA image pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        if (owns_encoder) {
            wgpuCommandEncoderRelease(encoder);
        }
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained RGBA image render pass could not be created.");
    }
    if (frame->opacity != 0.0F && draw_state.opacity != 0.0F &&
        (use_group_layer || draw_state.has_drawable_clip)) {
        if (!use_group_layer) {
            apply_scissor(pass, draw_state);
        }
        wgpuRenderPassEncoderSetPipeline(
            pass,
            has_mask ? engine->image_mask_pipeline : engine->image_pipeline);
        wgpuRenderPassEncoderSetBindGroup(
            pass, 0U, engine->image_uniform_bind_group, 0U, nullptr);
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            1U,
            frame->sampling == PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST
                ? engine->image_nearest_bind_group
                : engine->image_linear_bind_group,
            0U,
            nullptr);
        if (has_mask) {
            wgpuRenderPassEncoderSetBindGroup(
                pass,
                2U,
                frame->mask_sampling == PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST
                    ? engine->image_mask_nearest_bind_group
                    : engine->image_mask_linear_bind_group,
                0U,
                nullptr);
        }
        wgpuRenderPassEncoderSetVertexBuffer(
            pass,
            0U,
            engine->image_vertex_buffer,
            0U,
            sizeof(engine->image_vertices));
        wgpuRenderPassEncoderSetIndexBuffer(
            pass,
            engine->image_index_buffer,
            WGPUIndexFormat_Uint32,
            0U,
            6U * sizeof(std::uint32_t));
        wgpuRenderPassEncoderDrawIndexed(pass, 6U, 1U, 0U, 0, 0U);
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
                "The image group composite pass could not be created.");
        }
    }

    if (owns_encoder) {
        WGPUCommandBufferDescriptor command_descriptor{};
        command_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained RGBA image commands");
        WGPUCommandBuffer command = wgpuCommandEncoderFinish(
            encoder,
            &command_descriptor);
        wgpuCommandEncoderRelease(encoder);
        if (command == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The retained RGBA image command buffer could not be finished.");
        }
        engine->submit(command);
        wgpuCommandBufferRelease(command);
    }
    if (use_group_layer) {
        retain_group_layer_content(
            *engine,
            layer_family::image,
            frame->dpi_scale,
            draw_state);
    }

    std::uint64_t payload_hash = engine->image_payload_hash;
    payload_hash = append_fnv1a64(
        payload_hash,
        &frame->image_revision,
        sizeof(frame->image_revision));
    payload_hash = append_fnv1a64(
        payload_hash,
        &frame->sampling,
        sizeof(frame->sampling));
    payload_hash = append_fnv1a64(
        payload_hash,
        &frame->mask_revision,
        sizeof(frame->mask_revision));
    engine->last_error.clear();
    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_image_frame_metrics)) {
        metrics->draw_call_count = frame->opacity == 0.0F ||
            draw_state.opacity == 0.0F ||
            (!use_group_layer && !draw_state.has_drawable_clip)
            ? 0U
            : 1U;
        metrics->vertex_count = 4U;
        metrics->index_count = 6U;
        metrics->texture_generation = engine->image_texture_generation;
        metrics->vertex_upload_bytes = upload_vertices
            ? sizeof(engine->image_vertices)
            : 0U;
        metrics->index_upload_bytes = created_resources
            ? 6U * sizeof(std::uint32_t)
            : 0U;
        metrics->texture_upload_bytes = upload_texture && !external
            ? required_upload_bytes
            : 0U;
        metrics->uniform_upload_bytes =
            (uploaded_uniforms ? sizeof(gpu_uniforms) : 0U) +
            (uploaded_mask_uniforms
                ? sizeof(gpu_mask_sampling_uniforms)
                : 0U);
        metrics->submission_count = engine->submission_count;
        metrics->payload_hash = payload_hash;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

} // namespace progpu::native::execution
