#pragma once

#include "progpu_native.h"

#include <cmath>
#include <cstddef>
#include <cstdint>

namespace progpu::native {

inline constexpr std::uint64_t initial_vertex_buffer_size = 64U * 1024U;
inline constexpr std::uint64_t initial_index_buffer_size = 16U * 1024U;
inline constexpr std::uint64_t initial_brush_buffer_size = 64U * 256U;
inline constexpr std::uint64_t gpu_brush_size = 256U;
inline constexpr float antialias_padding_pixels = 1.5F;
inline constexpr std::uint32_t native_initial_atlas_size = 1024U;
inline constexpr std::uint32_t native_max_atlas_size = 4096U;
inline constexpr std::uint32_t path_padding = 4U;
inline constexpr std::uint32_t webgpu_copy_row_alignment = 256U;

enum class layer_family : std::uint32_t {
    solid = 1U,
    analytic = 2U,
    geometry = 3U,
    path = 4U,
    glyph = 5U,
    image = 6U
};

struct gpu_uniforms {
    float projection[16];
    float model_view_projection[16];
    float view[16];
    float canvas_size[2];
    float dpi_scale;
    float pad0;
    float render_origin[2];
    float pad1[2];
};

struct gpu_mask_sampling_uniforms {
    float coordinate0[4];
    float coordinate1[4];
    float bounds[4];
    float corner_radii_x[4];
    float corner_radii_y[4];
    float options[4];
};

struct gpu_mask_chain_uniforms {
    gpu_mask_sampling_uniforms masks[3];
};

struct gpu_drop_shadow_params {
    float offset[2];
    float padding[2];
    float color[4];
};

struct gpu_group_blend_uniforms {
    float backdrop[4];
    std::uint32_t blend_mode;
    std::uint32_t padding[3];
};

struct gpu_advanced_blend_sampling_uniforms {
    float source_origin[2];
    float source_extent[2];
    std::uint32_t blend_mode;
    std::uint32_t padding[3];
};

static_assert(sizeof(gpu_advanced_blend_sampling_uniforms) == 32U);

struct gpu_gaussian_blur_params {
    float sigma;
    std::uint32_t radius;
    std::uint32_t kernel_type;
    std::uint32_t padding1;
};

struct gpu_path_uniforms {
    float x_start;
    float y_start;
    float scale_x;
    float scale_y;
    std::uint32_t path_index;
    std::uint32_t output_offset_words;
    std::uint32_t output_row_words;
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t sample_grid;
    std::uint32_t path_index_b;
    std::uint32_t path_op_kind;
};

struct gpu_path_coverage_combine_uniforms {
    std::uint32_t source_offset_words;
    std::uint32_t source_stride_words;
    std::uint32_t source_count;
    std::uint32_t program_index;
    std::uint32_t program_count;
    std::uint32_t destination_offset_words;
    std::uint32_t destination_row_words;
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t sample_grid;
};

struct gpu_path_record {
    std::uint32_t start_segment;
    std::uint32_t segment_count;
    float min_x;
    float min_y;
    float max_x;
    float max_y;
    std::uint32_t fill_rule;
    std::uint32_t pad1;
};

struct native_path_raster {
    std::uint32_t atlas_x;
    std::uint32_t atlas_y;
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t output_offset;
    std::uint32_t output_bytes_per_row;
    float scale_x;
    float scale_y;
    float raster_min_x;
    float raster_min_y;
    float subpixel_x;
    float subpixel_y;
};

struct gpu_clip_vertex {
    float position[2];
    float atlas_uv[2];
};

struct gpu_clip_compose_uniforms {
    std::uint32_t operation;
    std::uint32_t first;
    std::uint32_t width;
    std::uint32_t height;
};

struct native_path_cache_key {
    std::size_t segment_offset;
    std::size_t segment_count;
    std::size_t boolean_node_offset;
    std::size_t boolean_node_count;
    std::uint32_t min_x;
    std::uint32_t min_y;
    std::uint32_t max_x;
    std::uint32_t max_y;
    std::uint32_t scale;
    std::uint32_t subpixel_x;
    std::uint32_t subpixel_y;
    std::uint32_t fill_rule;
    std::uint32_t sample_grid;

    bool operator==(const native_path_cache_key&) const = default;
};

struct native_path_cache_key_hash {
    std::size_t operator()(const native_path_cache_key& key) const noexcept {
        std::uint64_t hash = 14695981039346656037ULL;
        const auto mix = [&hash](std::uint64_t value) {
            for (std::uint32_t byte = 0U; byte < 8U; ++byte) {
                hash = (hash ^ static_cast<std::uint8_t>(value)) *
                    1099511628211ULL;
                value >>= 8U;
            }
        };
        mix(key.segment_offset);
        mix(key.segment_count);
        mix(key.boolean_node_offset);
        mix(key.boolean_node_count);
        mix(key.min_x);
        mix(key.min_y);
        mix(key.max_x);
        mix(key.max_y);
        mix(key.scale);
        mix(key.subpixel_x);
        mix(key.subpixel_y);
        mix(key.fill_rule);
        mix(key.sample_grid);
        return static_cast<std::size_t>(hash);
    }
};

struct gpu_glyph_record {
    std::uint32_t start_segment;
    std::uint32_t segment_count;
    float min_x;
    float min_y;
    float max_x;
    float max_y;
    std::uint32_t pad0;
    std::uint32_t pad1;
};

struct gpu_glyph_uniforms {
    float x_start;
    float y_start;
    float scale;
    std::uint32_t glyph_index;
    std::uint32_t output_offset_words;
    std::uint32_t output_row_words;
    std::uint32_t width;
    std::uint32_t height;
    float subpixel_x;
    float atlas_x;
    float atlas_y;
    float pad2;
};

struct gpu_glyph_instance {
    float snapped_logical_position[2];
    float basis_x[2];
    float basis_y[2];
    float bear_size[4];
    float texture_coordinates[4];
    float color[4];
    float scale_bold_italic_flags[4];
    float brush_index;
    float padding;
};

struct native_glyph_raster {
    std::uint32_t atlas_x;
    std::uint32_t atlas_y;
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t output_offset;
    std::uint32_t output_bytes_per_row;
    float x_start;
    float y_start;
};

static_assert(sizeof(gpu_uniforms) == 224U);
static_assert(sizeof(gpu_mask_sampling_uniforms) == 96U);
static_assert(sizeof(gpu_mask_chain_uniforms) == 288U);
static_assert(sizeof(gpu_drop_shadow_params) == 32U);
static_assert(sizeof(gpu_group_blend_uniforms) == 32U);
static_assert(sizeof(gpu_gaussian_blur_params) == 16U);
static_assert(sizeof(gpu_path_uniforms) == 48U);
static_assert(sizeof(gpu_path_coverage_combine_uniforms) == 40U);
static_assert(sizeof(gpu_path_record) == 32U);
static_assert(sizeof(gpu_path_record) ==
    sizeof(progpu_native_path_segment) - 16U);
static_assert(sizeof(gpu_clip_vertex) == 16U);
static_assert(sizeof(gpu_clip_compose_uniforms) == 16U);
static_assert(sizeof(gpu_glyph_record) == 32U);
static_assert(sizeof(gpu_glyph_uniforms) == 48U);
static_assert(sizeof(gpu_glyph_instance) == 96U);

[[nodiscard]] constexpr std::uint32_t align_up(
    std::uint32_t value,
    std::uint32_t alignment) noexcept {
    return (value + alignment - 1U) / alignment * alignment;
}

[[nodiscard]] constexpr std::uint64_t align_up_u64(
    std::uint64_t value,
    std::uint64_t alignment) noexcept {
    return (value + alignment - 1U) / alignment * alignment;
}

[[nodiscard]] inline float quantize_subpixel_phase(float value) noexcept {
    value -= std::floor(value);
    const float quantized = std::round(value * 64.0F) / 64.0F;
    return quantized >= 1.0F ? 0.0F : quantized;
}

} // namespace progpu::native
