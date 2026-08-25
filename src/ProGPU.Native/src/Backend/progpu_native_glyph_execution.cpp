#include "progpu_native_frame_execution_common.hpp"

namespace progpu::native::execution {

namespace {

struct cpu_roots {
    std::array<float, 3U> values{};
    std::uint32_t count = 0U;
};

cpu_roots solve_quadratic_cpu(float a, float b, float c) noexcept {
    cpu_roots result{};
    if (std::abs(a) < 0.00001F) {
        if (std::abs(b) > 0.00001F) {
            result.values[0] = -c / b;
            result.count = 1U;
        }
        return result;
    }
    const float discriminant = b * b - 4.0F * a * c;
    if (discriminant == 0.0F) {
        result.values[0] = -b / (2.0F * a);
        result.count = 1U;
    } else if (discriminant > 0.0F) {
        const float root = std::sqrt(discriminant);
        result.values[0] = (-b - root) / (2.0F * a);
        result.values[1] = (-b + root) / (2.0F * a);
        result.count = 2U;
    }
    return result;
}

float shader_cbrt_cpu(float value) noexcept {
    return value < 0.0F
        ? -std::pow(-value, 1.0F / 3.0F)
        : std::pow(value, 1.0F / 3.0F);
}

cpu_roots solve_cubic_cpu(
    float a_in,
    float b_in,
    float c_in,
    float d_in) noexcept {
    if (std::abs(a_in) < 0.00001F) {
        return solve_quadratic_cpu(b_in, c_in, d_in);
    }
    cpu_roots result{};
    const float a = b_in / a_in;
    const float b = c_in / a_in;
    const float c = d_in / a_in;
    const float p = b - a * a / 3.0F;
    const float q = c - a * b / 3.0F +
        2.0F * a * a * a / 27.0F;
    const float discriminant = q * q / 4.0F + p * p * p / 27.0F;
    if (discriminant > 0.0F) {
        const float root = std::sqrt(discriminant);
        const float u = shader_cbrt_cpu(-q / 2.0F + root);
        const float v = shader_cbrt_cpu(-q / 2.0F - root);
        result.values[0] = u + v - a / 3.0F;
        result.count = 1U;
    } else if (p < 0.0F) {
        constexpr float pi = 3.14159265359F;
        const float radius = 2.0F * std::sqrt(-p / 3.0F);
        const float ratio = std::clamp(
            -q / (2.0F * std::sqrt(-p * p * p / 27.0F)),
            -1.0F,
            1.0F);
        const float theta = std::acos(ratio);
        result.values[0] = radius * std::cos(theta / 3.0F) - a / 3.0F;
        result.values[1] = radius *
            std::cos((theta + 2.0F * pi) / 3.0F) - a / 3.0F;
        result.values[2] = radius *
            std::cos((theta + 4.0F * pi) / 3.0F) - a / 3.0F;
        result.count = 3U;
    } else {
        result.values[0] = -a / 3.0F;
        result.count = 1U;
    }
    return result;
}

bool is_winding_root_valid(
    float t,
    float derivative_y,
    float sample_y,
    float start_y,
    float end_y) noexcept {
    if (t < 0.005F) {
        return derivative_y > 0.0F
            ? sample_y >= start_y
            : derivative_y < 0.0F && sample_y < start_y;
    }
    if (t > 0.995F) {
        return derivative_y > 0.0F
            ? sample_y < end_y
            : derivative_y < 0.0F && sample_y >= end_y;
    }
    return true;
}

int glyph_winding_cpu(
    float sample_x,
    float sample_y,
    const gpu_glyph_record& record,
    const progpu_native_path_segment* segments) noexcept {
    int winding = 0;
    const std::uint32_t end = record.start_segment + record.segment_count;
    for (std::uint32_t index = record.start_segment;
         index < end;
         ++index) {
        const auto& segment = segments[index];
        const auto& a = segment.p0;
        const auto& b = segment.p1;
        if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE) {
            if (a.y == b.y) {
                continue;
            }
            if (a.y <= sample_y && b.y > sample_y) {
                const float t = (sample_y - a.y) / (b.y - a.y);
                const float crossing_x = a.x + t * (b.x - a.x);
                winding += sample_x < crossing_x ? 1 : 0;
            } else if (a.y > sample_y && b.y <= sample_y) {
                const float t = (sample_y - a.y) / (b.y - a.y);
                const float crossing_x = a.x + t * (b.x - a.x);
                winding -= sample_x < crossing_x ? 1 : 0;
            }
            continue;
        }

        const auto& c = segment.p2;
        if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC) {
            const float qa = a.y - 2.0F * b.y + c.y;
            const float qb = 2.0F * (b.y - a.y);
            const float qc = a.y - sample_y;
            const cpu_roots roots = solve_quadratic_cpu(qa, qb, qc);
            for (std::uint32_t root_index = 0U;
                 root_index < roots.count;
                 ++root_index) {
                const float t = roots.values[root_index];
                if (t < -0.01F || t > 1.01F) {
                    continue;
                }
                const float evaluated_t = std::clamp(
                    t, 0.00001F, 0.99999F);
                const float evaluated_one_minus_t = 1.0F - evaluated_t;
                const float derivative_y =
                    2.0F * evaluated_one_minus_t * (b.y - a.y) +
                    2.0F * evaluated_t * (c.y - b.y);
                if (!is_winding_root_valid(
                        t, derivative_y, sample_y, a.y, c.y)) {
                    continue;
                }
                const float clamped_t = std::clamp(t, 0.0F, 1.0F);
                const float one_minus_t = 1.0F - clamped_t;
                const float crossing_x =
                    one_minus_t * one_minus_t * a.x +
                    2.0F * one_minus_t * clamped_t * b.x +
                    clamped_t * clamped_t * c.x;
                winding += sample_x < crossing_x
                    ? derivative_y > 0.0F ? 1 : derivative_y < 0.0F ? -1 : 0
                    : 0;
            }
            continue;
        }

        const auto& d = segment.p3;
        const float ca = -a.y + 3.0F * b.y - 3.0F * c.y + d.y;
        const float cb = 3.0F * a.y - 6.0F * b.y + 3.0F * c.y;
        const float cc = -3.0F * a.y + 3.0F * b.y;
        const float cd = a.y - sample_y;
        const cpu_roots roots = solve_cubic_cpu(ca, cb, cc, cd);
        for (std::uint32_t root_index = 0U;
             root_index < roots.count;
             ++root_index) {
            const float t = roots.values[root_index];
            if (t < -0.01F || t > 1.01F) {
                continue;
            }
            const float evaluated_t = std::clamp(t, 0.00001F, 0.99999F);
            const float derivative_y = 3.0F * ca * evaluated_t * evaluated_t +
                2.0F * cb * evaluated_t + cc;
            if (!is_winding_root_valid(
                    t, derivative_y, sample_y, a.y, d.y)) {
                continue;
            }
            const float clamped_t = std::clamp(t, 0.0F, 1.0F);
            const float one_minus_t = 1.0F - clamped_t;
            const float crossing_x =
                one_minus_t * one_minus_t * one_minus_t * a.x +
                3.0F * one_minus_t * one_minus_t * clamped_t * b.x +
                3.0F * one_minus_t * clamped_t * clamped_t * c.x +
                clamped_t * clamped_t * clamped_t * d.x;
            winding += sample_x < crossing_x
                ? derivative_y > 0.0F ? 1 : derivative_y < 0.0F ? -1 : 0
                : 0;
        }
    }
    return winding;
}

bool rasterize_glyph_coverage_cpu(
    const progpu_native_glyph_frame& frame,
    const std::vector<gpu_glyph_record>& records,
    const std::vector<gpu_glyph_uniforms>& uniforms,
    const std::vector<native_glyph_raster>& rasters,
    std::uint64_t coverage_size,
    std::vector<std::byte>& coverage) {
    try {
        coverage.assign(static_cast<std::size_t>(coverage_size), std::byte{});
    } catch (const std::bad_alloc&) {
        return false;
    }
    for (std::size_t glyph_index = 0U;
         glyph_index < rasters.size();
         ++glyph_index) {
        const auto& uniform = uniforms[glyph_index];
        const auto& raster = rasters[glyph_index];
        const auto& record = records[uniform.glyph_index];
        for (std::uint32_t y = 0U; y < raster.height; ++y) {
            for (std::uint32_t x = 0U; x < raster.width; ++x) {
                std::uint32_t covered_samples = 0U;
                const float pixel_x = uniform.x_start +
                    static_cast<float>(x);
                const float pixel_y = uniform.y_start +
                    static_cast<float>(y);
                for (std::uint32_t sample_y = 0U;
                     sample_y < 8U;
                     ++sample_y) {
                    const float glyph_y = -(
                        pixel_y + 0.0625F +
                        static_cast<float>(sample_y) * 0.125F) /
                        uniform.scale;
                    for (std::uint32_t sample_x = 0U;
                         sample_x < 8U;
                         ++sample_x) {
                        const float glyph_x = (
                            pixel_x + 0.0625F +
                            static_cast<float>(sample_x) * 0.125F -
                            uniform.subpixel_x) / uniform.scale;
                        covered_samples += glyph_winding_cpu(
                            glyph_x,
                            glyph_y,
                            record,
                            frame.segments) != 0
                            ? 1U
                            : 0U;
                    }
                }
                const auto value = static_cast<std::uint32_t>(std::round(
                    static_cast<float>(covered_samples) * 3.984375F));
                const std::size_t offset = raster.output_offset +
                    static_cast<std::size_t>(y) *
                        raster.output_bytes_per_row + x;
                coverage[offset] = static_cast<std::byte>(
                    std::min(value, 255U));
            }
        }
    }
    return true;
}

} // namespace

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
                const bool has_color_bitmap =
                    engine->semantic_glyph_draw_active &&
                    engine->semantic_glyph_cache.color_bitmap_indices.size() ==
                        frame->glyph_count &&
                    engine->semantic_glyph_cache.color_bitmap_indices[index] !=
                        PROGPU_NATIVE_SCENE_NO_INDEX;
                const std::uint32_t color_bitmap_index = has_color_bitmap
                    ? engine->semantic_glyph_cache.color_bitmap_indices[index]
                    : PROGPU_NATIVE_SCENE_NO_INDEX;
                if ((!has_color_bitmap &&
                        glyph.outline_index >= frame->outline_count) ||
                    (has_color_bitmap &&
                        (color_bitmap_index >= engine->semantic_glyph_cache
                                .color_bitmaps.size() ||
                            color_bitmap_index >= engine->semantic_glyph_cache
                                .color_rasters.size())) ||
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
                if (has_color_bitmap) {
                    const auto& bitmap = engine->semantic_glyph_cache
                        .color_bitmaps[color_bitmap_index];
                    const auto& raster = engine->semantic_glyph_cache
                        .color_rasters[color_bitmap_index];
                    instance.bear_size[0] = bitmap.bear_x;
                    instance.bear_size[1] = bitmap.bear_y;
                    instance.bear_size[2] = bitmap.render_width > 0.0F
                        ? bitmap.render_width
                        : static_cast<float>(bitmap.width);
                    instance.bear_size[3] = bitmap.render_height > 0.0F
                        ? bitmap.render_height
                        : static_cast<float>(bitmap.height);
                    instance.texture_coordinates[0] =
                        static_cast<float>(raster.atlas_x);
                    instance.texture_coordinates[1] =
                        static_cast<float>(raster.atlas_y);
                    instance.texture_coordinates[2] =
                        static_cast<float>(raster.atlas_x + bitmap.width);
                    instance.texture_coordinates[3] =
                        static_cast<float>(raster.atlas_y + bitmap.height);
                } else {
                    const auto& raster =
                        engine->glyph_rasters[glyph.outline_index];
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
                }
                std::memcpy(
                    instance.color,
                    &glyph.color,
                    sizeof(glyph.color));
                instance.color[3] *= draw_state.opacity;
                instance.scale_bold_italic_flags[0] =
                    glyph.atlas_to_logical_scale;
                instance.scale_bold_italic_flags[1] = glyph.bold_offset;
                instance.scale_bold_italic_flags[2] = glyph.italic_skew;
                instance.scale_bold_italic_flags[3] =
                    has_color_bitmap ? 8.0F : 0.0F;
                const bool has_semantic_style =
                    engine->semantic_glyph_draw_active &&
                    engine->semantic_glyph_cache.style_indices.size() ==
                        frame->glyph_count &&
                    engine->semantic_glyph_cache.style_indices[index] !=
                        PROGPU_NATIVE_SCENE_NO_INDEX;
                if (has_semantic_style) {
                    const auto style_index =
                        engine->semantic_glyph_cache.style_indices[index];
                    if (style_index >=
                        engine->semantic_text_style_cache.styles.size()) {
                        return engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "A retained semantic text style index is invalid.");
                    }
                    instance.brush_index = static_cast<float>(style_index);
                } else {
                    instance.brush_index = -1.0F;
                }
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
    std::vector<std::byte> cpu_coverage;
    const bool glyph_compute_fallback =
        (engine->engine_flags &
            PROGPU_NATIVE_ENGINE_GLYPH_COMPUTE_FALLBACK) != 0U;
    if (!compiled_payload_hit && frame->outline_count != 0U) {
        if (glyph_compute_fallback) {
            if (!rasterize_glyph_coverage_cpu(
                    *frame,
                    records,
                    uniforms,
                    engine->glyph_rasters,
                    coverage_staging_bytes,
                    cpu_coverage)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                    "The native CPU glyph coverage arena could not be allocated.");
            }
            for (const auto& raster : engine->glyph_rasters) {
                progpu::native::webgpu::image_copy_texture destination{};
                destination.texture = engine->glyph_atlas_texture;
                destination.origin = {raster.atlas_x, raster.atlas_y, 0U};
                destination.aspect = WGPUTextureAspect_All;
                progpu::native::webgpu::texture_data_layout layout{};
                layout.bytesPerRow = raster.output_bytes_per_row;
                layout.rowsPerImage = raster.height;
                const WGPUExtent3D extent{
                    raster.width,
                    raster.height,
                    1U};
                const std::size_t source_bytes =
                    static_cast<std::size_t>(raster.output_bytes_per_row) *
                        (raster.height - 1U) + raster.width;
                wgpuQueueWriteTexture(
                    engine->queue,
                    &destination,
                    cpu_coverage.data() + raster.output_offset,
                    source_bytes,
                    &layout,
                    &extent);
            }
            outline_upload_bytes = coverage_staging_bytes;
        } else {
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
            if (descriptor.size > engine->max_buffer_size) {
                return nullptr;
            }
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

} // namespace progpu::native::execution
