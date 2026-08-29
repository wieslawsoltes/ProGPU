#include "progpu_native_frame_execution_common.hpp"
#include "progpu_native_path_boolean_gpu.hpp"

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
    const bool has_boolean_program_fields =
        frame->struct_size >= sizeof(progpu_native_path_frame);
    const auto* boolean_nodes = has_boolean_program_fields
        ? frame->boolean_nodes
        : nullptr;
    const std::size_t boolean_node_count = has_boolean_program_fields
        ? frame->boolean_node_count
        : 0U;
    if (boolean_node_count > (1U << 22U) ||
        (boolean_node_count != 0U && boolean_nodes == nullptr)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The path boolean-program arena is invalid.");
    }
    resolved_draw_state draw_state{};
    const auto* requested_draw_state =
        frame->struct_size >= offsetof(
            progpu_native_path_frame,
            boolean_nodes)
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
    std::vector<std::vector<gpu_path_uniforms>> split_leaf_uniforms;
    std::vector<std::vector<gpu_path_uniforms>>
        split_signed_leaf_uniforms;
    std::vector<gpu_path_record> path_records;
    std::vector<gpu_path_coverage_combine_uniforms>
        coverage_combine_uniforms;
    std::vector<gpu_path_coverage_combine_uniforms>
        signed_coverage_combine_uniforms;
    if (!compiled_payload_hit) {
        engine->path_cache_valid = false;
        engine->path_gpu_cache_valid = false;
        try {
            engine->path_vertices.clear();
            engine->path_indices.clear();
            engine->path_brush_bytes.clear();
            engine->path_rasters.clear();
            path_uniforms.reserve(frame->path_count);
            coverage_combine_uniforms.reserve(frame->path_count);
            signed_coverage_combine_uniforms.reserve(frame->path_count);
            path_records.reserve(
                frame->path_count + boolean_node_count * 2U);
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
            std::size_t expected_boolean_node_offset = 0U;
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
                    (path.sample_grid != 4U && path.sample_grid != 8U) ||
                    (path.boolean_node_count != 0U &&
                        path.boolean_node_offset !=
                            expected_boolean_node_offset) ||
                    !path_boolean::validate(
                        path,
                        boolean_nodes,
                        boolean_node_count)) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A path range, bound, transform, fill rule, or sample grid is invalid.");
                }
                expected_boolean_node_offset += path.boolean_node_count;
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
                cache_key.boolean_node_offset = path.boolean_node_offset;
                cache_key.boolean_node_count = path.boolean_node_count;
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
                    const std::uint64_t aligned_output_offset = align_up_u64(
                        output_offset,
                        webgpu_copy_offset_alignment);
                    if (aligned_output_offset >
                        std::numeric_limits<std::uint32_t>::max()) {
                        return engine->fail(
                            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                            "The aligned path coverage staging batch exceeds 4 GiB.");
                    }
                    output_offset =
                        static_cast<std::uint32_t>(aligned_output_offset);
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
                    const auto program = path_boolean::append_gpu_records(
                        path,
                        boolean_nodes,
                        path_records,
                        std::span<const progpu_native_path_segment>(
                            frame->segments,
                            frame->segment_count));
                    if (program.split_leaf_count != 0U) {
                        const bool signed_winding_program =
                            (program.operation_kind &
                                path_boolean::gpu_signed_winding_program_flag) !=
                            0U;
                        const std::uint64_t leaf_words_per_pixel =
                            signed_winding_program
                                ? path_maximum_sample_count
                                : path_sample_mask_word_count;
                        const std::uint64_t leaf_words_per_row =
                            static_cast<std::uint64_t>(width) *
                            leaf_words_per_pixel;
                        const std::uint64_t leaf_bytes =
                            leaf_words_per_row * sizeof(std::uint32_t) *
                            height;
                        const std::uint64_t source_offset = align_up_u64(
                            next_output,
                            webgpu_copy_row_alignment);
                        const std::uint64_t signed_result_bytes =
                            signed_winding_program
                                ? static_cast<std::uint64_t>(width) * height *
                                    path_sample_mask_word_count *
                                    sizeof(std::uint32_t)
                                : 0U;
                        const std::uint64_t split_next_output =
                            source_offset + leaf_bytes *
                                program.split_leaf_count +
                            signed_result_bytes;
                        if (split_next_output >
                            std::numeric_limits<std::uint32_t>::max()) {
                            return engine->fail(
                                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                                "The split path coverage staging batch exceeds 4 GiB.");
                        }
                        const auto append_leaf_uniform = [
                            raster_min_x,
                            raster_min_y,
                            subpixel_x,
                            subpixel_y,
                            raster_scale,
                            leaf_words_per_row,
                            signed_winding_program,
                            width,
                            height,
                            &path](
                            std::vector<gpu_path_uniforms>& destination,
                            std::uint32_t record_index,
                            std::uint32_t leaf_output_offset) {
                            destination.push_back({
                                raster_min_x - subpixel_x,
                                raster_min_y - subpixel_y,
                                raster_scale,
                                raster_scale,
                                record_index,
                                leaf_output_offset / 4U,
                                static_cast<std::uint32_t>(
                                    leaf_words_per_row),
                                width,
                                height,
                                path.sample_grid,
                                0U,
                                signed_winding_program
                                    ? path_boolean::
                                        gpu_signed_winding_program_flag
                                    : 0U});
                        };
                        auto& selected_leaf_uniforms = signed_winding_program
                            ? split_signed_leaf_uniforms
                            : split_leaf_uniforms;
                        if (selected_leaf_uniforms.size() <
                            program.split_leaf_count) {
                            selected_leaf_uniforms.resize(
                                program.split_leaf_count);
                        }
                        for (std::uint32_t leaf_index = 0U;
                             leaf_index < program.split_leaf_count;
                             ++leaf_index) {
                            const std::uint64_t leaf_output_offset =
                                source_offset + leaf_bytes * leaf_index;
                            append_leaf_uniform(
                                selected_leaf_uniforms[leaf_index],
                                program.path_record_index + leaf_index,
                                static_cast<std::uint32_t>(
                                    leaf_output_offset));
                        }
                        const gpu_path_coverage_combine_uniforms
                            combine_uniform{
                            static_cast<std::uint32_t>(source_offset) / 4U,
                            static_cast<std::uint32_t>(leaf_bytes) / 4U,
                            program.split_leaf_count,
                            program.program_index,
                            program.operation_kind &
                                ~path_boolean::gpu_program_flag,
                            output_offset / 4U,
                            output_bytes_per_row / 4U,
                            width,
                            height,
                            path.sample_grid};
                        if (signed_winding_program) {
                            signed_coverage_combine_uniforms.push_back(
                                combine_uniform);
                        } else {
                            coverage_combine_uniforms.push_back(
                                combine_uniform);
                        }
                        output_offset =
                            static_cast<std::uint32_t>(split_next_output);
                    } else {
                        path_uniforms.push_back({
                            raster_min_x - subpixel_x,
                            raster_min_y - subpixel_y,
                            raster_scale,
                            raster_scale,
                            program.path_record_index,
                            output_offset / 4U,
                            output_bytes_per_row / 4U,
                            width,
                            height,
                            path.sample_grid,
                            program.program_index,
                            program.operation_kind
                        });
                        output_offset = static_cast<std::uint32_t>(next_output);
                    }
                    retained_tiles.emplace(cache_key, raster_index);
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
            if (expected_boolean_node_offset != boolean_node_count) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "Every path boolean-program node must have one owner.");
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
    if (!create_path_resources(*engine)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native path atlas WebGPU resources could not be created.");
    }
    if (!resize_path_atlas(*engine, required_atlas_size)) {
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
    auto& split_leaf_uniform_buffers = temporary.split_leaf_uniforms;
    auto& split_signed_leaf_uniform_buffers =
        temporary.split_signed_leaf_uniforms;
    WGPUBuffer& path_record_buffer = temporary.records;
    WGPUBuffer& path_segment_buffer = temporary.segments;
    WGPUBuffer& coverage_buffer = temporary.coverage;
    WGPUBuffer& coverage_combine_uniform_buffer =
        temporary.coverage_combine_uniforms;
    WGPUBuffer& signed_coverage_combine_uniform_buffer =
        temporary.signed_coverage_combine_uniforms;
    WGPUBindGroup& raster_bind_group = temporary.bind_group;
    auto& split_leaf_bind_groups = temporary.split_leaf_bind_groups;
    auto& split_signed_leaf_bind_groups =
        temporary.split_signed_leaf_bind_groups;
    WGPUBindGroup& signed_combine_bind_group =
        temporary.signed_combine_bind_group;
    const auto create_buffer = [&](
        const char* label,
        std::uint64_t size,
        progpu::native::webgpu::buffer_usage_flags usage) -> WGPUBuffer {
        WGPUBufferDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(label);
        descriptor.size = std::max<std::uint64_t>(size, 4U);
        if (descriptor.size > engine->max_buffer_size) {
            return nullptr;
        }
        descriptor.usage = usage;
        return wgpuDeviceCreateBuffer(engine->device, &descriptor);
    };
    if (!compiled_payload_hit && frame->path_count != 0U) {
        path_uniform_buffer = create_buffer(
            "ProGPU native path uniforms",
            std::max<std::size_t>(path_uniforms.size(), 1U) *
                sizeof(gpu_path_uniforms),
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        split_leaf_uniform_buffers.resize(
            split_leaf_uniforms.size(),
            nullptr);
        for (std::size_t phase_index = 0U;
             phase_index < split_leaf_uniforms.size();
             ++phase_index) {
            if (!split_leaf_uniforms[phase_index].empty()) {
                split_leaf_uniform_buffers[phase_index] = create_buffer(
                    "ProGPU native split boolean leaf uniforms",
                    split_leaf_uniforms[phase_index].size() *
                        sizeof(gpu_path_uniforms),
                    WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
            }
        }
        split_signed_leaf_uniform_buffers.resize(
            split_signed_leaf_uniforms.size(),
            nullptr);
        for (std::size_t phase_index = 0U;
             phase_index < split_signed_leaf_uniforms.size();
             ++phase_index) {
            if (!split_signed_leaf_uniforms[phase_index].empty()) {
                split_signed_leaf_uniform_buffers[phase_index] =
                    create_buffer(
                        "ProGPU native split signed-winding leaf uniforms",
                        split_signed_leaf_uniforms[phase_index].size() *
                            sizeof(gpu_path_uniforms),
                        WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
            }
        }
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
        coverage_combine_uniform_buffer = create_buffer(
            "ProGPU native path coverage combine uniforms",
            std::max<std::size_t>(
                coverage_combine_uniforms.size(),
                1U) * sizeof(gpu_path_coverage_combine_uniforms),
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        signed_coverage_combine_uniform_buffer = create_buffer(
            "ProGPU native signed-winding combine uniforms",
            std::max<std::size_t>(
                signed_coverage_combine_uniforms.size(),
                1U) * sizeof(gpu_path_coverage_combine_uniforms),
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        bool split_buffer_allocation_failed = false;
        for (std::size_t phase_index = 0U;
             phase_index < split_leaf_uniforms.size();
             ++phase_index) {
            split_buffer_allocation_failed |=
                !split_leaf_uniforms[phase_index].empty() &&
                split_leaf_uniform_buffers[phase_index] == nullptr;
        }
        for (std::size_t phase_index = 0U;
             phase_index < split_signed_leaf_uniforms.size();
             ++phase_index) {
            split_buffer_allocation_failed |=
                !split_signed_leaf_uniforms[phase_index].empty() &&
                split_signed_leaf_uniform_buffers[phase_index] == nullptr;
        }
        if (path_uniform_buffer == nullptr || split_buffer_allocation_failed ||
            path_record_buffer == nullptr ||
            path_segment_buffer == nullptr || coverage_buffer == nullptr ||
            coverage_combine_uniform_buffer == nullptr ||
            signed_coverage_combine_uniform_buffer == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native path raster staging buffers could not be allocated.");
        }
        const std::uint64_t uniform_bytes = path_uniforms.size() *
            sizeof(gpu_path_uniforms);
        std::uint64_t split_leaf_uniform_bytes = 0U;
        const std::uint64_t record_bytes = path_records.size() *
            sizeof(gpu_path_record);
        const std::uint64_t segment_bytes = frame->segment_count *
            sizeof(progpu_native_path_segment);
        if (uniform_bytes != 0U) {
            wgpuQueueWriteBuffer(engine->queue, path_uniform_buffer, 0U,
                path_uniforms.data(), uniform_bytes);
        }
        for (std::size_t phase_index = 0U;
             phase_index < split_leaf_uniforms.size();
             ++phase_index) {
            const std::uint64_t phase_bytes =
                split_leaf_uniforms[phase_index].size() *
                    sizeof(gpu_path_uniforms);
            split_leaf_uniform_bytes += phase_bytes;
            if (phase_bytes != 0U) {
                wgpuQueueWriteBuffer(
                    engine->queue,
                    split_leaf_uniform_buffers[phase_index],
                    0U,
                    split_leaf_uniforms[phase_index].data(),
                    phase_bytes);
            }
        }
        for (std::size_t phase_index = 0U;
             phase_index < split_signed_leaf_uniforms.size();
             ++phase_index) {
            const std::uint64_t phase_bytes =
                split_signed_leaf_uniforms[phase_index].size() *
                    sizeof(gpu_path_uniforms);
            split_leaf_uniform_bytes += phase_bytes;
            if (phase_bytes != 0U) {
                wgpuQueueWriteBuffer(
                    engine->queue,
                    split_signed_leaf_uniform_buffers[phase_index],
                    0U,
                    split_signed_leaf_uniforms[phase_index].data(),
                    phase_bytes);
            }
        }
        wgpuQueueWriteBuffer(engine->queue, path_record_buffer, 0U,
            path_records.data(), record_bytes);
        wgpuQueueWriteBuffer(engine->queue, path_segment_buffer, 0U,
            frame->segments, segment_bytes);
        const std::uint64_t combine_uniform_bytes =
            coverage_combine_uniforms.size() *
                sizeof(gpu_path_coverage_combine_uniforms);
        if (combine_uniform_bytes != 0U) {
            wgpuQueueWriteBuffer(
                engine->queue,
                coverage_combine_uniform_buffer,
                0U,
                coverage_combine_uniforms.data(),
                combine_uniform_bytes);
        }
        const std::uint64_t signed_combine_uniform_bytes =
            signed_coverage_combine_uniforms.size() *
                sizeof(gpu_path_coverage_combine_uniforms);
        if (signed_combine_uniform_bytes != 0U) {
            wgpuQueueWriteBuffer(
                engine->queue,
                signed_coverage_combine_uniform_buffer,
                0U,
                signed_coverage_combine_uniforms.data(),
                signed_combine_uniform_bytes);
        }
        path_upload_bytes = uniform_bytes + split_leaf_uniform_bytes +
            record_bytes + segment_bytes + combine_uniform_bytes +
            signed_combine_uniform_bytes;

        const auto create_raster_bind_group = [
            engine,
            path_record_buffer,
            record_bytes,
            path_segment_buffer,
            segment_bytes,
            coverage_buffer,
            coverage_staging_bytes](
            const char* label,
            WGPUBuffer uniform_buffer,
            std::uint64_t uniform_size,
            WGPUBuffer combine_buffer,
            std::uint64_t combine_size) {
            const std::array<WGPUBindGroupEntry, 5U> entries{{
                {nullptr, 0U, uniform_buffer, 0U,
                    std::max<std::uint64_t>(
                        uniform_size,
                        sizeof(gpu_path_uniforms)),
                    nullptr, nullptr},
                {nullptr, 1U, path_record_buffer, 0U, record_bytes,
                    nullptr, nullptr},
                {nullptr, 2U, path_segment_buffer, 0U, segment_bytes,
                    nullptr, nullptr},
                {nullptr, 3U, coverage_buffer, 0U,
                    coverage_staging_bytes, nullptr, nullptr},
                {nullptr, 4U, combine_buffer, 0U,
                    std::max<std::uint64_t>(
                        combine_size,
                        sizeof(gpu_path_coverage_combine_uniforms)),
                    nullptr, nullptr}
            }};
            WGPUBindGroupDescriptor descriptor{};
            descriptor.label = progpu::native::webgpu::string_view(label);
            descriptor.layout = engine->path_raster_layout;
            descriptor.entryCount = entries.size();
            descriptor.entries = entries.data();
            return wgpuDeviceCreateBindGroup(engine->device, &descriptor);
        };
        raster_bind_group = create_raster_bind_group(
            "ProGPU native path raster bind group",
            path_uniform_buffer,
            uniform_bytes,
            coverage_combine_uniform_buffer,
            combine_uniform_bytes);
        split_leaf_bind_groups.resize(split_leaf_uniforms.size(), nullptr);
        bool split_bind_group_creation_failed = false;
        for (std::size_t phase_index = 0U;
             phase_index < split_leaf_uniforms.size();
             ++phase_index) {
            const std::uint64_t phase_bytes =
                split_leaf_uniforms[phase_index].size() *
                    sizeof(gpu_path_uniforms);
            if (phase_bytes != 0U) {
                split_leaf_bind_groups[phase_index] =
                    create_raster_bind_group(
                        "ProGPU native split boolean leaf bind group",
                        split_leaf_uniform_buffers[phase_index],
                        phase_bytes,
                        coverage_combine_uniform_buffer,
                        combine_uniform_bytes);
                split_bind_group_creation_failed |=
                    split_leaf_bind_groups[phase_index] == nullptr;
            }
        }
        split_signed_leaf_bind_groups.resize(
            split_signed_leaf_uniforms.size(),
            nullptr);
        for (std::size_t phase_index = 0U;
             phase_index < split_signed_leaf_uniforms.size();
             ++phase_index) {
            const std::uint64_t phase_bytes =
                split_signed_leaf_uniforms[phase_index].size() *
                    sizeof(gpu_path_uniforms);
            if (phase_bytes != 0U) {
                split_signed_leaf_bind_groups[phase_index] =
                    create_raster_bind_group(
                        "ProGPU native split signed-winding leaf bind group",
                        split_signed_leaf_uniform_buffers[phase_index],
                        phase_bytes,
                        signed_coverage_combine_uniform_buffer,
                        signed_combine_uniform_bytes);
                split_bind_group_creation_failed |=
                    split_signed_leaf_bind_groups[phase_index] == nullptr;
            }
        }
        if (!signed_coverage_combine_uniforms.empty()) {
            signed_combine_bind_group = create_raster_bind_group(
                "ProGPU native signed-winding combine bind group",
                path_uniform_buffer,
                uniform_bytes,
                signed_coverage_combine_uniform_buffer,
                signed_combine_uniform_bytes);
            split_bind_group_creation_failed |=
                signed_combine_bind_group == nullptr;
        }
        if (raster_bind_group == nullptr ||
            split_bind_group_creation_failed) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native path raster bind group could not be created.");
        }
    }

    const bool split_raster_submissions =
        !coverage_combine_uniforms.empty() ||
        !signed_coverage_combine_uniforms.empty();
    const bool restore_semantic_encoder = split_raster_submissions &&
        engine->semantic_encoder != nullptr;
    if (restore_semantic_encoder) {
        WGPUCommandEncoder semantic_encoder = engine->semantic_encoder;
        engine->semantic_encoder = nullptr;
        WGPUCommandBufferDescriptor command_descriptor{};
        command_descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native pre-XOR semantic commands");
        WGPUCommandBuffer command = wgpuCommandEncoderFinish(
            semantic_encoder,
            &command_descriptor);
        wgpuCommandEncoderRelease(semantic_encoder);
        if (command == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The pre-XOR semantic command buffer could not be finished.");
        }
        engine->submit(command);
        wgpuCommandBufferRelease(command);
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
    const auto submit_raster_phase = [&](const char* label) {
        WGPUCommandBufferDescriptor command_descriptor{};
        command_descriptor.label =
            progpu::native::webgpu::string_view(label);
        WGPUCommandBuffer command = wgpuCommandEncoderFinish(
            encoder,
            &command_descriptor);
        wgpuCommandEncoderRelease(encoder);
        encoder = nullptr;
        if (command == nullptr) {
            return false;
        }
        engine->submit(command);
        wgpuCommandBufferRelease(command);
        encoder = wgpuDeviceCreateCommandEncoder(
            engine->device,
            &encoder_descriptor);
        return encoder != nullptr;
    };

    if (raster_bind_group != nullptr) {
        std::uint32_t workgroups_x = 0U;
        std::uint32_t split_workgroups_x = 0U;
        std::uint32_t workgroups_y = 0U;
        for (const auto& raster : engine->path_rasters) {
            workgroups_x = std::max(
                workgroups_x,
                (raster.width + 63U) / 64U);
            split_workgroups_x = std::max(
                split_workgroups_x,
                (raster.width + 15U) / 16U);
            workgroups_y = std::max(
                workgroups_y,
                (raster.height + 15U) / 16U);
        }
        std::uint32_t signed_leaf_workgroups_x = 0U;
        std::uint32_t signed_leaf_workgroups_y = 0U;
        for (const auto& phase : split_signed_leaf_uniforms) {
            for (const auto& uniform : phase) {
                signed_leaf_workgroups_x = std::max(
                    signed_leaf_workgroups_x,
                    (uniform.width + 15U) / 16U);
                signed_leaf_workgroups_y = std::max(
                    signed_leaf_workgroups_y,
                    (uniform.height * 8U + 15U) / 16U);
            }
        }
        std::uint32_t signed_pack_workgroups_x = 0U;
        std::uint32_t signed_pack_workgroups_y = 0U;
        std::uint32_t signed_sample_workgroups_x = 0U;
        std::uint32_t signed_sample_workgroups_y = 0U;
        for (const auto& uniform : signed_coverage_combine_uniforms) {
            signed_pack_workgroups_x = std::max(
                signed_pack_workgroups_x,
                (uniform.width + 63U) / 64U);
            signed_pack_workgroups_y = std::max(
                signed_pack_workgroups_y,
                (uniform.height + 15U) / 16U);
            signed_sample_workgroups_x = std::max(
                signed_sample_workgroups_x,
                (uniform.width + 15U) / 16U);
            signed_sample_workgroups_y = std::max(
                signed_sample_workgroups_y,
                (uniform.height + 15U) / 16U);
        }
        const auto encode_raster_pass = [&](
            const char* label,
            WGPUBindGroup bind_group,
            std::size_t uniform_count,
            WGPUComputePipeline pipeline,
            std::uint32_t dispatch_x,
            std::uint32_t dispatch_y) {
            if (uniform_count == 0U) {
                return true;
            }
            WGPUComputePassDescriptor compute_descriptor{};
            compute_descriptor.label =
                progpu::native::webgpu::string_view(label);
            WGPUComputePassEncoder compute_pass =
                wgpuCommandEncoderBeginComputePass(
                    encoder,
                    &compute_descriptor);
            if (compute_pass == nullptr) {
                return false;
            }
            wgpuComputePassEncoderSetPipeline(
                compute_pass,
                pipeline);
            wgpuComputePassEncoderSetBindGroup(
                compute_pass,
                0U,
                bind_group,
                0U,
                nullptr);
            wgpuComputePassEncoderDispatchWorkgroups(
                compute_pass,
                dispatch_x,
                dispatch_y,
                static_cast<std::uint32_t>(uniform_count));
            wgpuComputePassEncoderEnd(compute_pass);
            wgpuComputePassEncoderRelease(compute_pass);
            return true;
        };
        if (!encode_raster_pass(
                "ProGPU native path coverage pass",
                raster_bind_group,
                path_uniforms.size(),
                engine->path_raster_pipeline,
                workgroups_x,
                workgroups_y)) {
            if (owns_encoder && encoder != nullptr) {
                wgpuCommandEncoderRelease(encoder);
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "A native path compute pass could not be created.");
        }
        for (std::size_t phase_index = 0U;
             phase_index < split_leaf_uniforms.size();
             ++phase_index) {
            if (!encode_raster_pass(
                    "ProGPU native path split boolean leaf coverage pass",
                    split_leaf_bind_groups[phase_index],
                    split_leaf_uniforms[phase_index].size(),
                    engine->path_split_leaf_pipeline,
                    split_workgroups_x,
                    workgroups_y)) {
                if (owns_encoder && encoder != nullptr) {
                    wgpuCommandEncoderRelease(encoder);
                }
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "A native path split boolean compute pass could not be created.");
            }
            if (split_raster_submissions &&
                !submit_raster_phase(
                    "ProGPU native path split boolean raster phase commands")) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "A native path split boolean raster phase could not be submitted.");
            }
        }
        for (std::size_t phase_index = 0U;
             phase_index < split_signed_leaf_uniforms.size();
             ++phase_index) {
            if (!encode_raster_pass(
                    "ProGPU native path signed-winding leaf coverage pass",
                    split_signed_leaf_bind_groups[phase_index],
                    split_signed_leaf_uniforms[phase_index].size(),
                    engine->path_split_signed_leaf_pipeline,
                    signed_leaf_workgroups_x,
                    signed_leaf_workgroups_y)) {
                if (owns_encoder && encoder != nullptr) {
                    wgpuCommandEncoderRelease(encoder);
                }
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "A native path signed-winding leaf pass could not be created.");
            }
            if (!submit_raster_phase(
                    "ProGPU native path signed-winding leaf commands")) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "A native path signed-winding leaf phase could not be submitted.");
            }
        }

        if (!signed_coverage_combine_uniforms.empty()) {
            WGPUComputePassDescriptor row_descriptor{};
            row_descriptor.label =
                progpu::native::webgpu::string_view(
                    "ProGPU native path signed-winding sample combine pass");
            WGPUComputePassEncoder row_pass =
                wgpuCommandEncoderBeginComputePass(
                    encoder,
                    &row_descriptor);
            if (row_pass == nullptr) {
                if (owns_encoder) {
                    wgpuCommandEncoderRelease(encoder);
                }
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "The native path signed-winding sample pass could not be created.");
            }
            wgpuComputePassEncoderSetPipeline(
                row_pass,
                engine->path_split_signed_rows_pipeline);
            wgpuComputePassEncoderSetBindGroup(
                row_pass,
                0U,
                signed_combine_bind_group,
                0U,
                nullptr);
            wgpuComputePassEncoderDispatchWorkgroups(
                row_pass,
                signed_sample_workgroups_x,
                signed_sample_workgroups_y,
                static_cast<std::uint32_t>(
                    signed_coverage_combine_uniforms.size()));
            wgpuComputePassEncoderEnd(row_pass);
            wgpuComputePassEncoderRelease(row_pass);
        }

        if (!coverage_combine_uniforms.empty()) {
            WGPUComputePassDescriptor combine_descriptor{};
            combine_descriptor.label =
                progpu::native::webgpu::string_view(
                    "ProGPU native path split boolean coverage combine pass");
            WGPUComputePassEncoder combine_pass =
                wgpuCommandEncoderBeginComputePass(
                    encoder,
                    &combine_descriptor);
            if (combine_pass == nullptr) {
                if (owns_encoder) {
                    wgpuCommandEncoderRelease(encoder);
                }
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "The native path split boolean combine pass could not be created.");
            }
            wgpuComputePassEncoderSetPipeline(
                combine_pass,
                engine->path_split_boolean_combine_pipeline);
            wgpuComputePassEncoderSetBindGroup(
                combine_pass,
                0U,
                raster_bind_group,
                0U,
                nullptr);
            wgpuComputePassEncoderDispatchWorkgroups(
                combine_pass,
                workgroups_x,
                workgroups_y,
                static_cast<std::uint32_t>(
                    coverage_combine_uniforms.size()));
            wgpuComputePassEncoderEnd(combine_pass);
            wgpuComputePassEncoderRelease(combine_pass);
        }

        if (!signed_coverage_combine_uniforms.empty()) {
            WGPUComputePassDescriptor combine_descriptor{};
            combine_descriptor.label =
                progpu::native::webgpu::string_view(
                    "ProGPU native path signed-winding coverage pack pass");
            WGPUComputePassEncoder combine_pass =
                wgpuCommandEncoderBeginComputePass(
                    encoder,
                    &combine_descriptor);
            if (combine_pass == nullptr) {
                if (owns_encoder) {
                    wgpuCommandEncoderRelease(encoder);
                }
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "The native path signed-winding pack pass could not be created.");
            }
            wgpuComputePassEncoderSetPipeline(
                combine_pass,
                engine->path_split_signed_coverage_pipeline);
            wgpuComputePassEncoderSetBindGroup(
                combine_pass,
                0U,
                signed_combine_bind_group,
                0U,
                nullptr);
            wgpuComputePassEncoderDispatchWorkgroups(
                combine_pass,
                signed_pack_workgroups_x,
                signed_pack_workgroups_y,
                static_cast<std::uint32_t>(
                    signed_coverage_combine_uniforms.size()));
            wgpuComputePassEncoderEnd(combine_pass);
            wgpuComputePassEncoderRelease(combine_pass);
        }

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

    }

    if (owns_encoder) {
        WGPUCommandBufferDescriptor command_descriptor{};
        command_descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native retained path commands");
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
    if (restore_semantic_encoder) {
        engine->semantic_encoder = wgpuDeviceCreateCommandEncoder(
            engine->device,
            &encoder_descriptor);
        if (engine->semantic_encoder == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The post-XOR semantic command encoder could not be created.");
        }
    }
    if (!engine->semantic_prepare_only && use_group_layer) {
        retain_group_layer_content(
            *engine,
            layer_family::path,
            frame->dpi_scale,
            draw_state);
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

} // namespace progpu::native::execution
