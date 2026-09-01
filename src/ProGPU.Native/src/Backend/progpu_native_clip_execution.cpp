#include "progpu_native.h"

#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#include <wgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#include "progpu_native_dawn.h"
#endif

#include "progpu_webgpu_compat.hpp"
#include "progpu_native_engine.hpp"
#include "progpu_native_path_boolean_gpu.hpp"
#include "progpu_native_pipeline.hpp"
#include "progpu_native_replay_execution.hpp"
#include "progpu_native_webgpu_resources.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <new>
#include <unordered_map>
#include <vector>

namespace progpu::native::execution {

using semantic_scissor = semantic::scissor;
using semantic_layer_budget = semantic::layer_budget;
using semantic_compilation_budget = semantic::compilation_budget;
inline constexpr std::uint32_t semantic_effect_uniform_alignment =
    semantic::effect_uniform_alignment;

bool validate_native_path_segment(
    const progpu_native_path_segment& segment) noexcept {
    const bool is_arc =
        segment.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC;
    return segment.kind <= PROGPU_NATIVE_PATH_SEGMENT_ARC &&
        ::progpu::native::is_finite(segment.p0) &&
        ::progpu::native::is_finite(segment.p1) &&
        ::progpu::native::is_finite(segment.p2) &&
        ::progpu::native::is_finite(segment.p3) &&
        (!is_arc ||
            (segment.p3.x > 0.0F && segment.p3.y > 0.0F &&
             std::isfinite(std::bit_cast<float>(segment.pad0)) &&
             std::isfinite(std::bit_cast<float>(segment.pad1)) &&
             std::isfinite(std::bit_cast<float>(segment.pad2)))) &&
        (is_arc ||
            (segment.pad0 == 0U && segment.pad1 == 0U &&
             segment.pad2 == 0U));
}

bool rebuild_vector_clip_chain(
    progpu_native_engine& engine,
    const progpu_native_group_mask& mask,
    std::uint32_t width,
    std::uint32_t height,
    float dpi_scale) {
    const auto& chain = *mask.clip_chain;
    if (chain.struct_size < sizeof(chain) ||
        (chain.flags &
            ~PROGPU_NATIVE_CLIP_CHAIN_STAGED_SIGNED_WINDING) != 0U ||
        chain.paths == nullptr || chain.path_count == 0U ||
        chain.segments == nullptr || chain.segment_count == 0U ||
        chain.path_count > (1U << 16U) ||
        chain.segment_count > (1U << 24U) ||
        chain.boolean_node_count > (1U << 22U) ||
        (chain.boolean_node_count != 0U && chain.boolean_nodes == nullptr)) {
        return false;
    }
    engine.last_layer_metrics.clip_path_count =
        static_cast<std::uint32_t>(chain.path_count);
    const bool cache_hit = engine.clip_cache_valid &&
        engine.clip_cached_revision == mask.revision &&
        engine.clip_cached_dpi_scale == dpi_scale &&
        engine.clip_width == width && engine.clip_height == height;
    engine.last_layer_metrics.clip_cache_hit = cache_hit ? 1U : 0U;
    if (cache_hit) {
        engine.last_layer_metrics.clip_texture_bytes =
            static_cast<std::uint64_t>(engine.clip_atlas_size) *
                engine.clip_atlas_size +
            static_cast<std::uint64_t>(width) * height * 3U;
        return true;
    }
    if (!create_clip_chain_resources(engine)) {
        return false;
    }

    try {
        for (std::size_t index = 0U; index < chain.segment_count; ++index) {
            if (!validate_native_path_segment(chain.segments[index])) {
                return false;
            }
        }

        std::vector<gpu_path_uniforms> path_uniforms;
        std::vector<std::vector<gpu_path_uniforms>> split_leaf_uniforms;
        std::vector<std::vector<gpu_path_uniforms>>
            split_signed_leaf_uniforms;
        std::vector<gpu_path_record> path_records;
        std::vector<gpu_path_coverage_combine_uniforms>
            coverage_combine_uniforms;
        std::vector<gpu_path_coverage_combine_uniforms>
            signed_coverage_combine_uniforms;
        bool has_inline_signed_winding = false;
        std::vector<native_path_raster> rasters;
        std::vector<gpu_clip_vertex> vertices;
        std::vector<std::uint32_t> indices;
        std::vector<std::byte> compose_uniform_bytes;
        path_uniforms.reserve(chain.path_count);
        coverage_combine_uniforms.reserve(chain.path_count);
        signed_coverage_combine_uniforms.reserve(chain.path_count);
        path_records.reserve(
            chain.path_count + chain.boolean_node_count * 2U);
        rasters.reserve(chain.path_count);
        vertices.reserve(chain.path_count * 4U);
        indices.reserve(chain.path_count * 6U);
        compose_uniform_bytes.resize(chain.path_count * 256U);

        std::uint32_t required_atlas_size = engine.clip_atlas_size;
        std::uint32_t atlas_x = 2U;
        std::uint32_t atlas_y = 2U;
        std::uint32_t row_height = 0U;
        std::uint32_t output_offset = 0U;
        std::unordered_map<
            native_path_cache_key,
            std::size_t,
            native_path_cache_key_hash> retained_tiles;
        retained_tiles.reserve(chain.path_count);
        std::size_t expected_boolean_node_offset = 0U;

        for (std::size_t index = 0U; index < chain.path_count; ++index) {
            const auto& path = chain.paths[index];
            if (path.segment_count == 0U ||
                path.segment_offset > chain.segment_count ||
                path.segment_count >
                    chain.segment_count - path.segment_offset ||
                !std::isfinite(path.min_x) ||
                !std::isfinite(path.min_y) ||
                !std::isfinite(path.max_x) ||
                !std::isfinite(path.max_y) ||
                path.max_x <= path.min_x || path.max_y <= path.min_y ||
                !::progpu::native::is_finite(path.transform) ||
                path.fill_rule > PROGPU_NATIVE_FILL_RULE_EVEN_ODD ||
                (path.sample_grid != 1U && path.sample_grid != 4U &&
                    path.sample_grid != 8U) ||
                path.operation > PROGPU_NATIVE_CLIP_DIFFERENCE ||
                path.reserved != 0U ||
                (path.boolean_node_count != 0U &&
                    path.boolean_node_offset !=
                        expected_boolean_node_offset) ||
                !path_boolean::validate(
                    path,
                    chain.boolean_nodes,
                    chain.boolean_node_count)) {
                return false;
            }
            expected_boolean_node_offset += path.boolean_node_count;
            float maximum_scale = 0.0F;
            float minimum_scale = 0.0F;
            if (!::progpu::native::try_get_stroke_scales(
                    path.transform,
                    maximum_scale,
                    minimum_scale)) {
                return false;
            }
            (void)minimum_scale;
            const float raster_scale = maximum_scale * dpi_scale;
            if (!std::isfinite(raster_scale) || raster_scale <= 0.0F) {
                return false;
            }
            const float subpixel_x = quantize_subpixel_phase(
                path.transform.m31 * dpi_scale);
            const float subpixel_y = quantize_subpixel_phase(
                path.transform.m32 * dpi_scale);
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
                return false;
            }

            std::size_t raster_index = 0U;
            const auto retained = retained_tiles.find(cache_key);
            if (retained != retained_tiles.end()) {
                raster_index = retained->second;
            } else {
                const auto raster_width_u =
                    static_cast<std::uint32_t>(raster_width);
                const auto raster_height_u =
                    static_cast<std::uint32_t>(raster_height);
                while (raster_width_u + 4U > required_atlas_size &&
                       required_atlas_size < native_max_atlas_size) {
                    required_atlas_size *= 2U;
                }
                if (atlas_x + raster_width_u + 2U > required_atlas_size) {
                    atlas_x = 2U;
                    atlas_y += row_height + 2U;
                    row_height = 0U;
                }
                while (atlas_y + raster_height_u + 2U >
                           required_atlas_size &&
                       required_atlas_size < native_max_atlas_size) {
                    required_atlas_size *= 2U;
                }
                if (atlas_y + raster_height_u + 2U >
                    required_atlas_size) {
                    return false;
                }
                const std::uint32_t output_bytes_per_row = align_up(
                    raster_width_u,
                    webgpu_copy_row_alignment);
                const std::uint64_t aligned_output_offset = align_up_u64(
                    output_offset,
                    webgpu_copy_offset_alignment);
                if (aligned_output_offset >
                    std::numeric_limits<std::uint32_t>::max()) {
                    return false;
                }
                output_offset =
                    static_cast<std::uint32_t>(aligned_output_offset);
                const std::uint64_t next_output =
                    static_cast<std::uint64_t>(output_offset) +
                    static_cast<std::uint64_t>(output_bytes_per_row) *
                        raster_height_u;
                if (next_output >
                    std::numeric_limits<std::uint32_t>::max()) {
                    return false;
                }
                raster_index = rasters.size();
                rasters.push_back({
                    atlas_x,
                    atlas_y,
                    raster_width_u,
                    raster_height_u,
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
                    chain.boolean_nodes,
                    path_records,
                    std::span<const progpu_native_path_segment>(
                        chain.segments,
                        chain.segment_count));
                const bool signed_winding_program =
                    (program.operation_kind &
                        path_boolean::gpu_signed_winding_program_flag) != 0U;
                const bool staged_signed_winding =
                    (chain.flags &
                        PROGPU_NATIVE_CLIP_CHAIN_STAGED_SIGNED_WINDING) != 0U;
                if (program.split_leaf_count != 0U &&
                    (!signed_winding_program || staged_signed_winding)) {
                    const std::uint64_t leaf_words_per_pixel =
                        signed_winding_program
                            ? path_maximum_sample_count
                            : path_sample_mask_word_count;
                    const std::uint64_t leaf_words_per_row =
                        static_cast<std::uint64_t>(raster_width_u) *
                        leaf_words_per_pixel;
                    const std::uint64_t leaf_bytes =
                        leaf_words_per_row * sizeof(std::uint32_t) *
                        raster_height_u;
                    const std::uint64_t source_offset = align_up_u64(
                        next_output,
                        webgpu_copy_row_alignment);
                    const std::uint64_t signed_result_bytes =
                        signed_winding_program
                            ? static_cast<std::uint64_t>(raster_width_u) *
                                raster_height_u *
                                path_sample_mask_word_count *
                                sizeof(std::uint32_t)
                            : 0U;
                    const std::uint64_t split_next_output =
                        source_offset + leaf_bytes *
                            program.split_leaf_count + signed_result_bytes;
                    if (split_next_output >
                        std::numeric_limits<std::uint32_t>::max()) {
                        return false;
                    }
                    const auto append_leaf_uniform = [
                        raster_min_x,
                        raster_min_y,
                        subpixel_x,
                        subpixel_y,
                        raster_scale,
                        leaf_words_per_row,
                        signed_winding_program,
                        raster_width_u,
                        raster_height_u,
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
                            raster_width_u,
                            raster_height_u,
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
                    const gpu_path_coverage_combine_uniforms combine_uniform{
                        static_cast<std::uint32_t>(source_offset) / 4U,
                        static_cast<std::uint32_t>(leaf_bytes) / 4U,
                        program.split_leaf_count,
                        program.program_index,
                        program.operation_kind &
                            ~path_boolean::gpu_program_flag,
                        output_offset / 4U,
                        output_bytes_per_row / 4U,
                        raster_width_u,
                        raster_height_u,
                        path.sample_grid};
                    if (signed_winding_program) {
                        signed_coverage_combine_uniforms.push_back(
                            combine_uniform);
                    } else {
                        coverage_combine_uniforms.push_back(combine_uniform);
                    }
                    output_offset =
                        static_cast<std::uint32_t>(split_next_output);
                } else {
                    has_inline_signed_winding =
                        has_inline_signed_winding ||
                        signed_winding_program;
                    path_uniforms.push_back({
                        raster_min_x - subpixel_x,
                        raster_min_y - subpixel_y,
                        raster_scale,
                        raster_scale,
                        program.path_record_index,
                        output_offset / 4U,
                        output_bytes_per_row / 4U,
                        raster_width_u,
                        raster_height_u,
                        path.sample_grid,
                        program.program_index,
                        program.operation_kind
                    });
                    output_offset = static_cast<std::uint32_t>(next_output);
                }
                retained_tiles.emplace(cache_key, raster_index);
                atlas_x += raster_width_u + 2U;
                row_height = std::max(row_height, raster_height_u);
            }
            const auto& raster = rasters[raster_index];
            const float local_min_x = raster.raster_min_x / raster.scale_x;
            const float local_min_y = raster.raster_min_y / raster.scale_y;
            const float local_max_x =
                (raster.raster_min_x + raster.width) / raster.scale_x;
            const float local_max_y =
                (raster.raster_min_y + raster.height) / raster.scale_y;
            const std::array<progpu_native_point, 4U> local_points{{
                {local_min_x, local_min_y},
                {local_max_x, local_min_y},
                {local_max_x, local_max_y},
                {local_min_x, local_max_y}
            }};
            const std::array<progpu_native_point, 4U> atlas_points{{
                {raster.atlas_x + raster.subpixel_x,
                    raster.atlas_y + raster.subpixel_y},
                {raster.atlas_x + raster.width + raster.subpixel_x,
                    raster.atlas_y + raster.subpixel_y},
                {raster.atlas_x + raster.width + raster.subpixel_x,
                    raster.atlas_y + raster.height + raster.subpixel_y},
                {raster.atlas_x + raster.subpixel_x,
                    raster.atlas_y + raster.height + raster.subpixel_y}
            }};
            const std::uint32_t vertex_start =
                static_cast<std::uint32_t>(vertices.size());
            for (std::size_t corner = 0U; corner < 4U; ++corner) {
                float logical_x = 0.0F;
                float logical_y = 0.0F;
                ::progpu::native::transform_point(
                    path.transform,
                    local_points[corner].x,
                    local_points[corner].y,
                    logical_x,
                    logical_y);
                gpu_clip_vertex vertex{};
                vertex.position[0] =
                    2.0F * logical_x * dpi_scale /
                        static_cast<float>(width) -
                    1.0F;
                vertex.position[1] =
                    1.0F - 2.0F * logical_y * dpi_scale /
                        static_cast<float>(height);
                // Keep pixel coordinates until every retained tile has been
                // packed. A later path can grow the shared atlas, so
                // normalizing here would leave earlier nodes using the old
                // denominator and sample an unrelated tile region.
                vertex.atlas_uv[0] = atlas_points[corner].x;
                vertex.atlas_uv[1] = atlas_points[corner].y;
                vertices.push_back(vertex);
            }
            indices.insert(
                indices.end(),
                {vertex_start,
                 vertex_start + 1U,
                 vertex_start + 2U,
                 vertex_start,
                 vertex_start + 2U,
                 vertex_start + 3U});
            const gpu_clip_compose_uniforms compose{
                path.operation,
                index == 0U ? 1U : 0U,
                width,
                height
            };
            std::memcpy(
                compose_uniform_bytes.data() + index * 256U,
                &compose,
                sizeof(compose));
        }
        if (expected_boolean_node_offset != chain.boolean_node_count) {
            return false;
        }

        const float inverse_atlas_size =
            1.0F / static_cast<float>(required_atlas_size);
        for (auto& vertex : vertices) {
            vertex.atlas_uv[0] *= inverse_atlas_size;
            vertex.atlas_uv[1] *= inverse_atlas_size;
        }

        const std::uint64_t vertex_bytes =
            vertices.size() * sizeof(gpu_clip_vertex);
        const std::uint64_t index_bytes =
            indices.size() * sizeof(std::uint32_t);
        const std::uint64_t compose_bytes =
            compose_uniform_bytes.size();
        WGPUBuffer old_vertex = engine.clip_vertex_buffer;
        WGPUBuffer old_index = engine.clip_index_buffer;
        WGPUBuffer old_uniform = engine.clip_compose_uniform_buffer;
        if (!ensure_clip_buffer(
                engine,
                engine.clip_vertex_buffer,
                engine.clip_vertex_buffer_size,
                vertex_bytes,
                WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst,
                "ProGPU native retained clip vertices") ||
            !ensure_clip_buffer(
                engine,
                engine.clip_index_buffer,
                engine.clip_index_buffer_size,
                index_bytes,
                WGPUBufferUsage_Index | WGPUBufferUsage_CopyDst,
                "ProGPU native retained clip indices") ||
            !ensure_clip_buffer(
                engine,
                engine.clip_compose_uniform_buffer,
                engine.clip_compose_uniform_buffer_size,
                compose_bytes,
                WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst,
                "ProGPU native retained clip composition uniforms") ||
            !ensure_clip_textures(
                engine,
                width,
                height,
                required_atlas_size)) {
            return false;
        }
        const bool binding_resources_changed =
            old_vertex != engine.clip_vertex_buffer ||
            old_index != engine.clip_index_buffer ||
            old_uniform != engine.clip_compose_uniform_buffer ||
            engine.clip_path_bind_group == nullptr;
        if (binding_resources_changed &&
            !rebuild_clip_bind_groups(engine)) {
            return false;
        }
        if (engine.clip_path_bind_group == nullptr &&
            !rebuild_clip_bind_groups(engine)) {
            return false;
        }

        wgpuQueueWriteBuffer(
            engine.queue,
            engine.clip_vertex_buffer,
            0U,
            vertices.data(),
            vertex_bytes);
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.clip_index_buffer,
            0U,
            indices.data(),
            index_bytes);
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.clip_compose_uniform_buffer,
            0U,
            compose_uniform_bytes.data(),
            compose_bytes);

        path_raster_resources temporary;
        const auto create_buffer = [&engine](
            const char* label,
            std::uint64_t size,
            ::progpu::native::webgpu::buffer_usage_flags usage) {
            WGPUBufferDescriptor descriptor{};
            descriptor.label = ::progpu::native::webgpu::string_view(label);
            descriptor.size = std::max<std::uint64_t>(size, 4U);
            descriptor.usage = usage;
            return wgpuDeviceCreateBuffer(engine.device, &descriptor);
        };
        const std::uint64_t path_uniform_bytes =
            path_uniforms.size() * sizeof(gpu_path_uniforms);
        std::uint64_t split_leaf_uniform_bytes = 0U;
        const std::uint64_t path_record_bytes =
            path_records.size() * sizeof(gpu_path_record);
        const std::uint64_t path_segment_bytes =
            chain.segment_count * sizeof(progpu_native_path_segment);
        const std::uint64_t combine_uniform_bytes =
            coverage_combine_uniforms.size() *
                sizeof(gpu_path_coverage_combine_uniforms);
        const std::uint64_t signed_combine_uniform_bytes =
            signed_coverage_combine_uniforms.size() *
                sizeof(gpu_path_coverage_combine_uniforms);
        temporary.uniforms = create_buffer(
            "ProGPU native clip path uniforms",
            std::max<std::uint64_t>(
                path_uniform_bytes,
                sizeof(gpu_path_uniforms)),
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        temporary.split_leaf_uniforms.resize(
            split_leaf_uniforms.size(),
            nullptr);
        for (std::size_t phase_index = 0U;
             phase_index < split_leaf_uniforms.size();
             ++phase_index) {
            const std::uint64_t phase_bytes =
                split_leaf_uniforms[phase_index].size() *
                    sizeof(gpu_path_uniforms);
            split_leaf_uniform_bytes += phase_bytes;
            if (phase_bytes != 0U) {
                temporary.split_leaf_uniforms[phase_index] = create_buffer(
                    "ProGPU native clip split boolean leaf uniforms",
                    phase_bytes,
                    WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
            }
        }
        temporary.split_signed_leaf_uniforms.resize(
            split_signed_leaf_uniforms.size(),
            nullptr);
        for (std::size_t phase_index = 0U;
             phase_index < split_signed_leaf_uniforms.size();
             ++phase_index) {
            const std::uint64_t phase_bytes =
                split_signed_leaf_uniforms[phase_index].size() *
                    sizeof(gpu_path_uniforms);
            split_leaf_uniform_bytes += phase_bytes;
            if (phase_bytes != 0U) {
                temporary.split_signed_leaf_uniforms[phase_index] =
                    create_buffer(
                        "ProGPU native clip signed-winding leaf uniforms",
                        phase_bytes,
                        WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
            }
        }
        temporary.records = create_buffer(
            "ProGPU native clip path records",
            path_record_bytes,
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        temporary.segments = create_buffer(
            "ProGPU native clip path segments",
            path_segment_bytes,
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        temporary.coverage = create_buffer(
            "ProGPU native clip coverage staging",
            output_offset,
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopySrc);
        temporary.coverage_combine_uniforms = create_buffer(
            "ProGPU native clip coverage combine uniforms",
            std::max<std::uint64_t>(
                combine_uniform_bytes,
                sizeof(gpu_path_coverage_combine_uniforms)),
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        temporary.signed_coverage_combine_uniforms = create_buffer(
            "ProGPU native clip signed-winding combine uniforms",
            std::max<std::uint64_t>(
                signed_combine_uniform_bytes,
                sizeof(gpu_path_coverage_combine_uniforms)),
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        bool split_buffer_allocation_failed = false;
        for (std::size_t phase_index = 0U;
             phase_index < split_leaf_uniforms.size();
             ++phase_index) {
            split_buffer_allocation_failed |=
                !split_leaf_uniforms[phase_index].empty() &&
                temporary.split_leaf_uniforms[phase_index] == nullptr;
        }
        for (std::size_t phase_index = 0U;
             phase_index < split_signed_leaf_uniforms.size();
             ++phase_index) {
            split_buffer_allocation_failed |=
                !split_signed_leaf_uniforms[phase_index].empty() &&
                temporary.split_signed_leaf_uniforms[phase_index] == nullptr;
        }
        if (temporary.uniforms == nullptr || split_buffer_allocation_failed ||
            temporary.records == nullptr ||
            temporary.segments == nullptr || temporary.coverage == nullptr ||
            temporary.coverage_combine_uniforms == nullptr ||
            temporary.signed_coverage_combine_uniforms == nullptr) {
            return false;
        }
        if (path_uniform_bytes != 0U) {
            wgpuQueueWriteBuffer(engine.queue, temporary.uniforms, 0U,
                path_uniforms.data(), path_uniform_bytes);
        }
        for (std::size_t phase_index = 0U;
             phase_index < split_leaf_uniforms.size();
             ++phase_index) {
            const std::uint64_t phase_bytes =
                split_leaf_uniforms[phase_index].size() *
                    sizeof(gpu_path_uniforms);
            if (phase_bytes != 0U) {
                wgpuQueueWriteBuffer(
                    engine.queue,
                    temporary.split_leaf_uniforms[phase_index],
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
            if (phase_bytes != 0U) {
                wgpuQueueWriteBuffer(
                    engine.queue,
                    temporary.split_signed_leaf_uniforms[phase_index],
                    0U,
                    split_signed_leaf_uniforms[phase_index].data(),
                    phase_bytes);
            }
        }
        wgpuQueueWriteBuffer(engine.queue, temporary.records, 0U,
            path_records.data(), path_record_bytes);
        wgpuQueueWriteBuffer(engine.queue, temporary.segments, 0U,
            chain.segments, path_segment_bytes);
        if (combine_uniform_bytes != 0U) {
            wgpuQueueWriteBuffer(
                engine.queue,
                temporary.coverage_combine_uniforms,
                0U,
                coverage_combine_uniforms.data(),
                combine_uniform_bytes);
        }
        if (signed_combine_uniform_bytes != 0U) {
            wgpuQueueWriteBuffer(
                engine.queue,
                temporary.signed_coverage_combine_uniforms,
                0U,
                signed_coverage_combine_uniforms.data(),
                signed_combine_uniform_bytes);
        }
        const auto create_raster_bind_group = [
            &engine,
            &temporary,
            path_record_bytes,
            path_segment_bytes,
            output_offset](
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
                {nullptr, 1U, temporary.records, 0U,
                    path_record_bytes, nullptr, nullptr},
                {nullptr, 2U, temporary.segments, 0U,
                    path_segment_bytes, nullptr, nullptr},
                {nullptr, 3U, temporary.coverage, 0U,
                    output_offset, nullptr, nullptr},
                {nullptr, 4U, combine_buffer, 0U,
                    std::max<std::uint64_t>(
                        combine_size,
                        sizeof(gpu_path_coverage_combine_uniforms)),
                    nullptr, nullptr}
            }};
            WGPUBindGroupDescriptor descriptor{};
            descriptor.label =
                ::progpu::native::webgpu::string_view(label);
            descriptor.layout = engine.path_raster_layout;
            descriptor.entryCount = entries.size();
            descriptor.entries = entries.data();
            return wgpuDeviceCreateBindGroup(
                engine.device,
                &descriptor);
        };
        temporary.bind_group = create_raster_bind_group(
            "ProGPU native retained clip raster bind group",
            temporary.uniforms,
            path_uniform_bytes,
            temporary.coverage_combine_uniforms,
            combine_uniform_bytes);
        temporary.split_leaf_bind_groups.resize(
            split_leaf_uniforms.size(),
            nullptr);
        bool split_bind_group_creation_failed = false;
        for (std::size_t phase_index = 0U;
             phase_index < split_leaf_uniforms.size();
             ++phase_index) {
            const std::uint64_t phase_bytes =
                split_leaf_uniforms[phase_index].size() *
                    sizeof(gpu_path_uniforms);
            if (phase_bytes != 0U) {
                temporary.split_leaf_bind_groups[phase_index] =
                    create_raster_bind_group(
                        "ProGPU native clip split boolean leaf bind group",
                        temporary.split_leaf_uniforms[phase_index],
                        phase_bytes,
                        temporary.coverage_combine_uniforms,
                        combine_uniform_bytes);
                split_bind_group_creation_failed |=
                    temporary.split_leaf_bind_groups[phase_index] == nullptr;
            }
        }
        temporary.split_signed_leaf_bind_groups.resize(
            split_signed_leaf_uniforms.size(),
            nullptr);
        for (std::size_t phase_index = 0U;
             phase_index < split_signed_leaf_uniforms.size();
             ++phase_index) {
            const std::uint64_t phase_bytes =
                split_signed_leaf_uniforms[phase_index].size() *
                    sizeof(gpu_path_uniforms);
            if (phase_bytes != 0U) {
                temporary.split_signed_leaf_bind_groups[phase_index] =
                    create_raster_bind_group(
                        "ProGPU native clip signed-winding leaf bind group",
                        temporary.split_signed_leaf_uniforms[phase_index],
                        phase_bytes,
                        temporary.signed_coverage_combine_uniforms,
                        signed_combine_uniform_bytes);
                split_bind_group_creation_failed |=
                    temporary.split_signed_leaf_bind_groups[phase_index] ==
                        nullptr;
            }
        }
        if (!signed_coverage_combine_uniforms.empty()) {
            temporary.signed_combine_bind_group = create_raster_bind_group(
                "ProGPU native clip signed-winding combine bind group",
                temporary.uniforms,
                path_uniform_bytes,
                temporary.signed_coverage_combine_uniforms,
                signed_combine_uniform_bytes);
            split_bind_group_creation_failed |=
                temporary.signed_combine_bind_group == nullptr;
        }
        if (temporary.bind_group == nullptr ||
            split_bind_group_creation_failed) {
            return false;
        }

        const bool split_raster_submissions =
            !coverage_combine_uniforms.empty() ||
            !signed_coverage_combine_uniforms.empty();
        const bool restore_semantic_encoder = split_raster_submissions &&
            engine.semantic_encoder != nullptr;
        if (restore_semantic_encoder) {
            WGPUCommandEncoder semantic_encoder = engine.semantic_encoder;
            engine.semantic_encoder = nullptr;
            engine.semantic_vector_mask_uses_shared_clip_resources = false;
            WGPUCommandBufferDescriptor command_descriptor{};
            command_descriptor.label =
                ::progpu::native::webgpu::string_view(
                    "ProGPU native pre-clip semantic commands");
            WGPUCommandBuffer command = wgpuCommandEncoderFinish(
                semantic_encoder,
                &command_descriptor);
            wgpuCommandEncoderRelease(semantic_encoder);
            if (command == nullptr) {
                return false;
            }
            engine.submit(command);
            wgpuCommandBufferRelease(command);
        }
        const bool owns_encoder = engine.semantic_encoder == nullptr;
        WGPUCommandEncoder encoder = engine.semantic_encoder;
        WGPUCommandEncoderDescriptor encoder_descriptor{};
        encoder_descriptor.label = ::progpu::native::webgpu::string_view(
            "ProGPU native retained clip encoder");
        if (owns_encoder) {
            encoder = wgpuDeviceCreateCommandEncoder(
                engine.device,
                &encoder_descriptor);
        }
        if (encoder == nullptr) {
            return false;
        }
        const auto submit_raster_phase = [&](const char* label) {
            WGPUCommandBufferDescriptor command_descriptor{};
            command_descriptor.label =
                ::progpu::native::webgpu::string_view(label);
            WGPUCommandBuffer command = wgpuCommandEncoderFinish(
                encoder,
                &command_descriptor);
            wgpuCommandEncoderRelease(encoder);
            encoder = nullptr;
            if (command == nullptr) {
                return false;
            }
            engine.submit(command);
            wgpuCommandBufferRelease(command);
            encoder = wgpuDeviceCreateCommandEncoder(
                engine.device,
                &encoder_descriptor);
            return encoder != nullptr;
        };
        std::uint32_t workgroups_x = 0U;
        std::uint32_t split_workgroups_x = 0U;
        std::uint32_t workgroups_y = 0U;
        for (const auto& raster : rasters) {
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
                ::progpu::native::webgpu::string_view(label);
            WGPUComputePassEncoder compute =
                wgpuCommandEncoderBeginComputePass(
                    encoder,
                    &compute_descriptor);
            if (compute == nullptr) {
                return false;
            }
            wgpuComputePassEncoderSetPipeline(
                compute,
                pipeline);
            wgpuComputePassEncoderSetBindGroup(
                compute,
                0U,
                bind_group,
                0U,
                nullptr);
            wgpuComputePassEncoderDispatchWorkgroups(
                compute,
                dispatch_x,
                dispatch_y,
                static_cast<std::uint32_t>(uniform_count));
            wgpuComputePassEncoderEnd(compute);
            wgpuComputePassEncoderRelease(compute);
            return true;
        };
        if (!encode_raster_pass(
                "ProGPU native retained clip coverage pass",
                temporary.bind_group,
                path_uniforms.size(),
                has_inline_signed_winding
                    ? engine.path_raster_pipeline
                    : engine.path_raster_ordinary_pipeline,
                workgroups_x,
                workgroups_y)) {
            if (owns_encoder && encoder != nullptr) {
                wgpuCommandEncoderRelease(encoder);
            }
            return false;
        }
        for (std::size_t phase_index = 0U;
             phase_index < split_leaf_uniforms.size();
             ++phase_index) {
            if (!encode_raster_pass(
                    "ProGPU native clip split boolean leaf coverage pass",
                    temporary.split_leaf_bind_groups[phase_index],
                    split_leaf_uniforms[phase_index].size(),
                    engine.path_split_leaf_pipeline,
                    split_workgroups_x,
                    workgroups_y)) {
                if (owns_encoder && encoder != nullptr) {
                    wgpuCommandEncoderRelease(encoder);
                }
                return false;
            }
            if (split_raster_submissions &&
                !submit_raster_phase(
                    "ProGPU native clip split boolean raster phase commands")) {
                return false;
            }
        }
        for (std::size_t phase_index = 0U;
             phase_index < split_signed_leaf_uniforms.size();
             ++phase_index) {
            if (!encode_raster_pass(
                    "ProGPU native clip signed-winding leaf coverage pass",
                    temporary.split_signed_leaf_bind_groups[phase_index],
                    split_signed_leaf_uniforms[phase_index].size(),
                    engine.path_split_signed_leaf_pipeline,
                    signed_leaf_workgroups_x,
                    signed_leaf_workgroups_y)) {
                if (owns_encoder && encoder != nullptr) {
                    wgpuCommandEncoderRelease(encoder);
                }
                return false;
            }
            if (!submit_raster_phase(
                    "ProGPU native clip signed-winding leaf commands")) {
                return false;
            }
        }
        if (!signed_coverage_combine_uniforms.empty()) {
            WGPUComputePassDescriptor row_descriptor{};
            row_descriptor.label =
                ::progpu::native::webgpu::string_view(
                    "ProGPU native clip signed-winding sample combine pass");
            WGPUComputePassEncoder row_pass =
                wgpuCommandEncoderBeginComputePass(
                    encoder,
                    &row_descriptor);
            if (row_pass == nullptr) {
                if (owns_encoder) {
                    wgpuCommandEncoderRelease(encoder);
                }
                return false;
            }
            wgpuComputePassEncoderSetPipeline(
                row_pass,
                engine.path_split_signed_rows_pipeline);
            wgpuComputePassEncoderSetBindGroup(
                row_pass,
                0U,
                temporary.signed_combine_bind_group,
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
                ::progpu::native::webgpu::string_view(
                    "ProGPU native clip split boolean coverage combine pass");
            WGPUComputePassEncoder combine =
                wgpuCommandEncoderBeginComputePass(
                    encoder,
                    &combine_descriptor);
            if (combine == nullptr) {
                if (owns_encoder) {
                    wgpuCommandEncoderRelease(encoder);
                }
                return false;
            }
            wgpuComputePassEncoderSetPipeline(
                combine,
                engine.path_split_boolean_combine_pipeline);
            wgpuComputePassEncoderSetBindGroup(
                combine,
                0U,
                temporary.bind_group,
                0U,
                nullptr);
            wgpuComputePassEncoderDispatchWorkgroups(
                combine,
                workgroups_x,
                workgroups_y,
                static_cast<std::uint32_t>(
                    coverage_combine_uniforms.size()));
            wgpuComputePassEncoderEnd(combine);
            wgpuComputePassEncoderRelease(combine);
        }
        if (!signed_coverage_combine_uniforms.empty()) {
            WGPUComputePassDescriptor combine_descriptor{};
            combine_descriptor.label =
                ::progpu::native::webgpu::string_view(
                    "ProGPU native clip signed-winding coverage pack pass");
            WGPUComputePassEncoder combine =
                wgpuCommandEncoderBeginComputePass(
                    encoder,
                    &combine_descriptor);
            if (combine == nullptr) {
                if (owns_encoder) {
                    wgpuCommandEncoderRelease(encoder);
                }
                return false;
            }
            wgpuComputePassEncoderSetPipeline(
                combine,
                engine.path_split_signed_coverage_pipeline);
            wgpuComputePassEncoderSetBindGroup(
                combine,
                0U,
                temporary.signed_combine_bind_group,
                0U,
                nullptr);
            wgpuComputePassEncoderDispatchWorkgroups(
                combine,
                signed_pack_workgroups_x,
                signed_pack_workgroups_y,
                static_cast<std::uint32_t>(
                    signed_coverage_combine_uniforms.size()));
            wgpuComputePassEncoderEnd(combine);
            wgpuComputePassEncoderRelease(combine);
        }
        for (const auto& raster : rasters) {
            ::progpu::native::webgpu::image_copy_buffer source{};
            source.buffer = temporary.coverage;
            source.layout.offset = raster.output_offset;
            source.layout.bytesPerRow = raster.output_bytes_per_row;
            source.layout.rowsPerImage = raster.height;
            ::progpu::native::webgpu::image_copy_texture destination{};
            destination.texture = engine.clip_atlas_texture;
            destination.origin = {raster.atlas_x, raster.atlas_y, 0U};
            destination.aspect = WGPUTextureAspect_All;
            const WGPUExtent3D extent{raster.width, raster.height, 1U};
            wgpuCommandEncoderCopyBufferToTexture(
                encoder,
                &source,
                &destination,
                &extent);
        }

        for (std::uint32_t index = 0U;
             index < static_cast<std::uint32_t>(chain.path_count);
             ++index) {
            WGPURenderPassColorAttachment node_attachment{};
            ::progpu::native::webgpu::initialize_color_attachment(
                node_attachment);
            node_attachment.view = engine.clip_node_view;
            node_attachment.loadOp = WGPULoadOp_Clear;
            node_attachment.storeOp = WGPUStoreOp_Store;
            node_attachment.clearValue = WGPUColor{0.0, 0.0, 0.0, 0.0};
            WGPURenderPassDescriptor node_descriptor{};
            node_descriptor.label = ::progpu::native::webgpu::string_view(
                "ProGPU native retained clip node pass");
            node_descriptor.colorAttachmentCount = 1U;
            node_descriptor.colorAttachments = &node_attachment;
            WGPURenderPassEncoder node_pass =
                wgpuCommandEncoderBeginRenderPass(
                    encoder,
                    &node_descriptor);
            if (node_pass == nullptr) {
                if (owns_encoder) {
                    wgpuCommandEncoderRelease(encoder);
                }
                return false;
            }
            const std::uint32_t zero_offset = 0U;
            wgpuRenderPassEncoderSetPipeline(
                node_pass,
                engine.clip_path_pipeline);
            wgpuRenderPassEncoderSetBindGroup(
                node_pass,
                0U,
                engine.clip_path_bind_group,
                1U,
                &zero_offset);
            wgpuRenderPassEncoderSetVertexBuffer(
                node_pass,
                0U,
                engine.clip_vertex_buffer,
                0U,
                vertex_bytes);
            wgpuRenderPassEncoderSetIndexBuffer(
                node_pass,
                engine.clip_index_buffer,
                WGPUIndexFormat_Uint32,
                0U,
                index_bytes);
            wgpuRenderPassEncoderDrawIndexed(
                node_pass,
                6U,
                1U,
                index * 6U,
                0,
                0U);
            wgpuRenderPassEncoderEnd(node_pass);
            wgpuRenderPassEncoderRelease(node_pass);

            const std::uint32_t destination_index = index % 2U;
            const std::uint32_t previous_index = 1U - destination_index;
            WGPURenderPassColorAttachment compose_attachment{};
            ::progpu::native::webgpu::initialize_color_attachment(
                compose_attachment);
            compose_attachment.view =
                engine.clip_accumulation_views[destination_index];
            compose_attachment.loadOp = WGPULoadOp_Clear;
            compose_attachment.storeOp = WGPUStoreOp_Store;
            compose_attachment.clearValue = WGPUColor{0.0, 0.0, 0.0, 0.0};
            WGPURenderPassDescriptor compose_descriptor{};
            compose_descriptor.label = ::progpu::native::webgpu::string_view(
                "ProGPU native retained clip composition pass");
            compose_descriptor.colorAttachmentCount = 1U;
            compose_descriptor.colorAttachments = &compose_attachment;
            WGPURenderPassEncoder compose_pass =
                wgpuCommandEncoderBeginRenderPass(
                    encoder,
                    &compose_descriptor);
            if (compose_pass == nullptr) {
                if (owns_encoder) {
                    wgpuCommandEncoderRelease(encoder);
                }
                return false;
            }
            const std::uint32_t dynamic_offset = index * 256U;
            wgpuRenderPassEncoderSetPipeline(
                compose_pass,
                engine.clip_compose_pipeline);
            wgpuRenderPassEncoderSetBindGroup(
                compose_pass,
                0U,
                engine.clip_compose_bind_groups[previous_index],
                1U,
                &dynamic_offset);
            wgpuRenderPassEncoderDraw(
                compose_pass,
                3U,
                1U,
                0U,
                0U);
            wgpuRenderPassEncoderEnd(compose_pass);
            wgpuRenderPassEncoderRelease(compose_pass);
        }

        if (owns_encoder) {
            WGPUCommandBufferDescriptor command_descriptor{};
            command_descriptor.label = ::progpu::native::webgpu::string_view(
                "ProGPU native retained clip commands");
            WGPUCommandBuffer command = wgpuCommandEncoderFinish(
                encoder,
                &command_descriptor);
            wgpuCommandEncoderRelease(encoder);
            if (command == nullptr) {
                return false;
            }
            engine.submit(command);
            wgpuCommandBufferRelease(command);
        }
        if (restore_semantic_encoder) {
            engine.semantic_encoder = wgpuDeviceCreateCommandEncoder(
                engine.device,
                &encoder_descriptor);
            if (engine.semantic_encoder == nullptr) {
                return false;
            }
        }

        engine.clip_cached_revision = mask.revision;
        engine.clip_cached_dpi_scale = dpi_scale;
        engine.clip_final_index = static_cast<std::uint32_t>(
            (chain.path_count - 1U) % 2U);
        engine.clip_cache_valid = owns_encoder;
        engine.last_layer_metrics.clip_rasterized_path_count =
            static_cast<std::uint32_t>(rasters.size());
        engine.last_layer_metrics.clip_pass_count =
            static_cast<std::uint32_t>(!path_uniforms.empty()) +
            static_cast<std::uint32_t>(split_leaf_uniforms.size()) +
            static_cast<std::uint32_t>(!coverage_combine_uniforms.empty()) +
            static_cast<std::uint32_t>(split_signed_leaf_uniforms.size()) +
            static_cast<std::uint32_t>(
                !signed_coverage_combine_uniforms.empty()) * 2U +
            static_cast<std::uint32_t>(chain.path_count) * 2U;
        engine.last_layer_metrics.clip_path_upload_bytes =
            path_uniform_bytes + split_leaf_uniform_bytes + path_record_bytes +
            path_segment_bytes + combine_uniform_bytes +
            signed_combine_uniform_bytes + vertex_bytes + index_bytes +
            compose_bytes;
        engine.last_layer_metrics.clip_coverage_staging_bytes = output_offset;
        engine.last_layer_metrics.clip_texture_bytes =
            static_cast<std::uint64_t>(required_atlas_size) *
                required_atlas_size +
            static_cast<std::uint64_t>(width) * height * 3U;
        return true;
    } catch (const std::bad_alloc&) {
        return false;
    }
}


} // namespace progpu::native::execution
