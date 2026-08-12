#include "progpu_native.h"
#include "progpu_native_geometry.hpp"
#include "GlyphRasterizerWgsl.generated.hpp"
#include "PathRasterizerWgsl.generated.hpp"
#include "TextWgsl.generated.hpp"
#include "TextureWgsl.generated.hpp"
#include "VectorWgsl.generated.hpp"

#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#include <wgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#include "progpu_native_dawn.h"
#endif

#include "progpu_webgpu_compat.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <new>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

namespace {

constexpr std::uint64_t initial_vertex_buffer_size = 64U * 1024U;
constexpr std::uint64_t initial_index_buffer_size = 16U * 1024U;
constexpr std::uint64_t initial_brush_buffer_size = 64U * 256U;
constexpr std::uint64_t gpu_brush_size = 256U;
constexpr float antialias_padding_pixels = 1.5F;
constexpr std::uint32_t native_initial_atlas_size = 1024U;
constexpr std::uint32_t native_max_atlas_size = 4096U;
constexpr std::uint32_t path_padding = 4U;
constexpr std::uint32_t webgpu_copy_row_alignment = 256U;

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

static_assert(sizeof(gpu_uniforms) == 224U);

struct gpu_mask_sampling_uniforms {
    float coordinate0[4];
    float coordinate1[4];
    float bounds[4];
    float corner_radii_x[4];
    float corner_radii_y[4];
    float options[4];
};

static_assert(sizeof(gpu_mask_sampling_uniforms) == 96U);

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

struct native_path_cache_key {
    std::size_t segment_offset;
    std::size_t segment_count;
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
    float pad0;
    float pad1;
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

static_assert(sizeof(gpu_path_uniforms) == 48U);
static_assert(sizeof(gpu_path_record) == 32U);
static_assert(sizeof(gpu_path_record) == sizeof(progpu_native_path_segment) - 16U);
static_assert(sizeof(gpu_glyph_record) == 32U);
static_assert(sizeof(gpu_glyph_uniforms) == 48U);
static_assert(sizeof(gpu_glyph_instance) == 96U);

std::uint32_t align_up(
    std::uint32_t value,
    std::uint32_t alignment) noexcept {
    return (value + alignment - 1U) / alignment * alignment;
}

float quantize_subpixel_phase(float value) noexcept {
    value -= std::floor(value);
    const float quantized = std::round(value * 64.0F) / 64.0F;
    return quantized >= 1.0F ? 0.0F : quantized;
}

struct path_raster_resources {
    WGPUBuffer uniforms = nullptr;
    WGPUBuffer records = nullptr;
    WGPUBuffer segments = nullptr;
    WGPUBuffer coverage = nullptr;
    WGPUBindGroup bind_group = nullptr;

    ~path_raster_resources() {
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
        }
        release_buffer(uniforms);
        release_buffer(records);
        release_buffer(segments);
        release_buffer(coverage);
    }

private:
    static void release_buffer(WGPUBuffer buffer) {
        if (buffer != nullptr) {
            wgpuBufferDestroy(buffer);
            wgpuBufferRelease(buffer);
        }
    }
};

WGPUTextureFormat texture_format(std::uint32_t value) noexcept {
    switch (value) {
        case PROGPU_NATIVE_TEXTURE_FORMAT_RGBA8_UNORM:
            return WGPUTextureFormat_RGBA8Unorm;
        case PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM:
            return WGPUTextureFormat_BGRA8Unorm;
        case PROGPU_NATIVE_TEXTURE_FORMAT_RGBA8_UNORM_SRGB:
            return WGPUTextureFormat_RGBA8UnormSrgb;
        case PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM_SRGB:
            return WGPUTextureFormat_BGRA8UnormSrgb;
        default:
            return WGPUTextureFormat_Undefined;
    }
}

void set_identity(float* matrix) noexcept {
    std::fill_n(matrix, 16U, 0.0F);
    matrix[0] = 1.0F;
    matrix[5] = 1.0F;
    matrix[10] = 1.0F;
    matrix[15] = 1.0F;
}

gpu_uniforms create_uniforms(
    std::uint32_t width,
    std::uint32_t height,
    float dpi_scale) noexcept {
    gpu_uniforms uniforms{};
    set_identity(uniforms.projection);
    set_identity(uniforms.model_view_projection);
    set_identity(uniforms.view);

    const float logical_width = static_cast<float>(width) / dpi_scale;
    const float logical_height = static_cast<float>(height) / dpi_scale;
    uniforms.projection[0] = 2.0F / logical_width;
    uniforms.projection[5] = -2.0F / logical_height;
    uniforms.projection[10] = -1.0F;
    uniforms.projection[12] = -1.0F;
    uniforms.projection[13] = 1.0F;
    uniforms.canvas_size[0] = logical_width;
    uniforms.canvas_size[1] = logical_height;
    uniforms.dpi_scale = dpi_scale;
    return uniforms;
}

std::uint64_t append_fnv1a64(
    std::uint64_t hash,
    const void* data,
    std::size_t size) noexcept {
    const auto* bytes = static_cast<const std::uint8_t*>(data);
    for (std::size_t index = 0; index < size; ++index) {
        hash = (hash ^ bytes[index]) * 1099511628211ULL;
    }
    return hash;
}

struct resolved_draw_state {
    float opacity = 1.0F;
    bool has_clip = false;
    bool has_drawable_clip = true;
    std::uint32_t clip_x = 0U;
    std::uint32_t clip_y = 0U;
    std::uint32_t clip_width = 0U;
    std::uint32_t clip_height = 0U;
};

float snap_scissor_coordinate(float value) noexcept {
    const float rounded = std::round(value);
    return std::abs(value - rounded) < 0.0001F ? rounded : value;
}

bool resolve_draw_state(
    const progpu_native_draw_state* state,
    std::uint32_t target_width,
    std::uint32_t target_height,
    float dpi_scale,
    resolved_draw_state& resolved) noexcept {
    resolved = {};
    if (state == nullptr) {
        return true;
    }
    if (state->struct_size < sizeof(progpu_native_draw_state) ||
        (state->flags & ~PROGPU_NATIVE_DRAW_STATE_CLIP_RECT) != 0U ||
        state->reserved != 0U || !std::isfinite(state->opacity) ||
        state->opacity < 0.0F || state->opacity > 1.0F) {
        return false;
    }
    resolved.opacity = state->opacity;
    if ((state->flags & PROGPU_NATIVE_DRAW_STATE_CLIP_RECT) == 0U) {
        return state->clip_rect.x == 0.0F && state->clip_rect.y == 0.0F &&
            state->clip_rect.width == 0.0F &&
            state->clip_rect.height == 0.0F;
    }
    const auto& clip = state->clip_rect;
    if (!std::isfinite(clip.x) || !std::isfinite(clip.y) ||
        !std::isfinite(clip.width) || !std::isfinite(clip.height) ||
        clip.width < 0.0F || clip.height < 0.0F) {
        return false;
    }

    resolved.has_clip = true;
    const float left = std::clamp(clip.x * dpi_scale, 0.0F,
        static_cast<float>(target_width));
    const float top = std::clamp(clip.y * dpi_scale, 0.0F,
        static_cast<float>(target_height));
    const float right = std::clamp(
        (clip.x + clip.width) * dpi_scale,
        0.0F,
        static_cast<float>(target_width));
    const float bottom = std::clamp(
        (clip.y + clip.height) * dpi_scale,
        0.0F,
        static_cast<float>(target_height));
    const float snapped_left = snap_scissor_coordinate(left);
    const float snapped_top = snap_scissor_coordinate(top);
    const float snapped_right = snap_scissor_coordinate(right);
    const float snapped_bottom = snap_scissor_coordinate(bottom);
    if (snapped_right <= snapped_left || snapped_bottom <= snapped_top) {
        resolved.has_drawable_clip = false;
        return true;
    }
    resolved.clip_x = static_cast<std::uint32_t>(std::floor(snapped_left));
    resolved.clip_y = static_cast<std::uint32_t>(std::floor(snapped_top));
    const std::uint32_t right_pixel = static_cast<std::uint32_t>(
        std::ceil(snapped_right));
    const std::uint32_t bottom_pixel = static_cast<std::uint32_t>(
        std::ceil(snapped_bottom));
    resolved.clip_width = right_pixel - resolved.clip_x;
    resolved.clip_height = bottom_pixel - resolved.clip_y;
    return true;
}

void apply_scissor(
    WGPURenderPassEncoder pass,
    const resolved_draw_state& state) noexcept {
    if (state.has_clip && state.has_drawable_clip) {
        wgpuRenderPassEncoderSetScissorRect(
            pass,
            state.clip_x,
            state.clip_y,
            state.clip_width,
            state.clip_height);
    }
}

void multiply_vertex_alpha(
    std::vector<progpu::native::vector_vertex>& vertices,
    float opacity) noexcept {
    if (opacity == 1.0F) {
        return;
    }
    for (auto& vertex : vertices) {
        vertex.color[3] *= opacity;
    }
}

void set_brush_opacity(
    std::vector<std::byte>& brushes,
    float opacity) noexcept {
    for (std::size_t offset = 4U; offset < brushes.size();
         offset += gpu_brush_size) {
        std::memcpy(brushes.data() + offset, &opacity, sizeof(opacity));
    }
}

} // namespace

struct progpu_native_engine {
    std::thread::id owner_thread;
    progpu::native::webgpu::dispatch webgpu_dispatch{};
    WGPUInstance instance = nullptr;
    WGPUDevice device = nullptr;
    WGPUQueue queue = nullptr;
    WGPUTextureFormat target_format = WGPUTextureFormat_Undefined;
    WGPUShaderModule shader = nullptr;
    WGPURenderPipeline pipeline = nullptr;
    WGPURenderPipeline analytic_pipeline = nullptr;
    WGPUBindGroupLayout uniform_layout = nullptr;
    WGPUBindGroupLayout analytic_uniform_layout = nullptr;
    WGPUBindGroupLayout analytic_atlas_layout = nullptr;
    WGPUBuffer uniform_buffer = nullptr;
    WGPUBindGroup uniform_bind_group = nullptr;
    WGPUBuffer analytic_uniform_buffer = nullptr;
    gpu_uniforms cached_uniforms{};
    gpu_uniforms cached_analytic_uniforms{};
    bool uniform_cache_valid = false;
    bool analytic_uniform_cache_valid = false;
    WGPUBuffer analytic_brush_buffer = nullptr;
    std::uint64_t analytic_brush_buffer_size = 0;
    WGPUBuffer analytic_gradient_buffer = nullptr;
    WGPUBindGroup analytic_uniform_bind_group = nullptr;
    WGPUBindGroup analytic_atlas_bind_group = nullptr;
    WGPUSampler analytic_sentinel_sampler = nullptr;
    WGPUTexture analytic_sentinel_texture = nullptr;
    WGPUTextureView analytic_sentinel_texture_view = nullptr;
    WGPUShaderModule path_raster_shader = nullptr;
    WGPUComputePipeline path_raster_pipeline = nullptr;
    WGPUBindGroupLayout path_raster_layout = nullptr;
    WGPUPipelineLayout path_raster_pipeline_layout = nullptr;
    WGPUSampler path_atlas_sampler = nullptr;
    WGPUTexture path_atlas_texture = nullptr;
    WGPUTextureView path_atlas_texture_view = nullptr;
    WGPUBindGroup path_atlas_bind_group = nullptr;
    std::uint32_t path_atlas_size = native_initial_atlas_size;
    std::uint32_t path_atlas_generation = 0U;
    WGPUShaderModule glyph_raster_shader = nullptr;
    WGPUComputePipeline glyph_raster_pipeline = nullptr;
    WGPUBindGroupLayout glyph_raster_layout = nullptr;
    WGPUPipelineLayout glyph_raster_pipeline_layout = nullptr;
    WGPUShaderModule text_shader = nullptr;
    WGPURenderPipeline text_pipeline = nullptr;
    WGPUBindGroupLayout text_uniform_layout = nullptr;
    WGPUBindGroupLayout text_atlas_layout = nullptr;
    WGPUBuffer text_style_buffer = nullptr;
    WGPUBindGroup text_uniform_bind_group = nullptr;
    WGPUSampler glyph_atlas_sampler = nullptr;
    WGPUTexture glyph_atlas_texture = nullptr;
    WGPUTextureView glyph_atlas_texture_view = nullptr;
    WGPUBindGroup text_atlas_bind_group = nullptr;
    std::uint32_t glyph_atlas_size = native_initial_atlas_size;
    std::uint32_t glyph_atlas_generation = 0U;
    std::uint32_t glyph_atlas_growth_count = 0U;
    WGPUBuffer text_vertex_buffer = nullptr;
    std::uint64_t text_vertex_buffer_size = 0U;
    WGPUBuffer vertex_buffer = nullptr;
    WGPUBuffer index_buffer = nullptr;
    std::uint64_t vertex_buffer_size = 0;
    std::uint64_t index_buffer_size = 0;
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    std::vector<std::uint32_t> primitive_brush_indices;
    std::vector<std::uint32_t> polyline_brush_indices;
    std::vector<std::uint32_t> spline_brush_indices;
    std::vector<std::size_t> spline_segment_counts;
    std::array<progpu_native_point, 101U> spline_sampled_points{};
    std::vector<progpu::native::spline_homogeneous_point> spline_work;
    std::vector<std::byte> brush_bytes;
    std::uint32_t geometry_content_revision = 0U;
    float geometry_opacity = 1.0F;
    std::uint64_t geometry_payload_hash = 0U;
    bool geometry_cache_valid = false;
    bool geometry_gpu_cache_valid = false;
    std::vector<progpu::native::vector_vertex> path_vertices;
    std::vector<std::uint32_t> path_indices;
    std::vector<std::byte> path_brush_bytes;
    std::vector<native_path_raster> path_rasters;
    std::uint32_t path_content_revision = 0U;
    float path_dpi_scale = 0.0F;
    float path_opacity = 1.0F;
    std::uint64_t path_payload_hash = 0U;
    bool path_cache_valid = false;
    bool path_gpu_cache_valid = false;
    std::vector<gpu_glyph_instance> glyph_instances;
    std::vector<float> glyph_source_alphas;
    std::vector<native_glyph_raster> glyph_rasters;
    std::uint32_t glyph_content_revision = 0U;
    float glyph_dpi_scale = 0.0F;
    float glyph_opacity = 1.0F;
    std::uint64_t glyph_payload_hash = 0U;
    bool glyph_cache_valid = false;
    bool glyph_gpu_cache_valid = false;
    WGPUShaderModule image_shader = nullptr;
    WGPURenderPipeline image_pipeline = nullptr;
    WGPURenderPipeline image_mask_pipeline = nullptr;
    WGPUBindGroupLayout image_uniform_layout = nullptr;
    WGPUBindGroupLayout image_texture_layout = nullptr;
    WGPUBindGroupLayout image_mask_layout = nullptr;
    WGPUBuffer image_uniform_buffer = nullptr;
    WGPUBindGroup image_uniform_bind_group = nullptr;
    WGPUSampler image_nearest_sampler = nullptr;
    WGPUSampler image_linear_sampler = nullptr;
    WGPUTexture image_texture = nullptr;
    WGPUTextureView image_texture_view = nullptr;
    WGPUBindGroup image_nearest_bind_group = nullptr;
    WGPUBindGroup image_linear_bind_group = nullptr;
    WGPUTextureView image_mask_view = nullptr;
    WGPUBuffer image_mask_uniform_buffer = nullptr;
    WGPUBindGroup image_mask_nearest_bind_group = nullptr;
    WGPUBindGroup image_mask_linear_bind_group = nullptr;
    WGPUBuffer image_vertex_buffer = nullptr;
    WGPUBuffer image_index_buffer = nullptr;
    std::array<progpu::native::vector_vertex, 4U> image_vertices{};
    gpu_uniforms cached_image_uniforms{};
    std::uint32_t image_revision = 0U;
    std::uint32_t image_content_revision = 0U;
    float image_draw_opacity = 1.0F;
    std::uint32_t image_width = 0U;
    std::uint32_t image_height = 0U;
    std::uint32_t image_texture_generation = 0U;
    std::uint32_t image_mask_revision = 0U;
    std::uint32_t image_mask_width = 0U;
    std::uint32_t image_mask_height = 0U;
    gpu_mask_sampling_uniforms cached_image_mask_uniforms{};
    bool image_mask_uniform_cache_valid = false;
    bool image_source_is_external = false;
    std::uint64_t image_payload_hash = 0U;
    bool image_uniform_cache_valid = false;
    bool image_cache_valid = false;
    bool image_gpu_cache_valid = false;
    std::string last_error;
    std::uint64_t submission_count = 0;
    std::uint64_t last_submission_index = 0U;

    void submit(WGPUCommandBuffer command) noexcept {
        last_submission_index = progpu::native::webgpu::submit(
            queue,
            1U,
            &command);
        ++submission_count;
    }

    bool upload_uniform_if_changed(
        WGPUBuffer buffer,
        const gpu_uniforms& uniforms,
        gpu_uniforms& cached,
        bool& cache_valid) noexcept {
        if (cache_valid &&
            std::memcmp(&cached, &uniforms, sizeof(uniforms)) == 0) {
            return false;
        }
        wgpuQueueWriteBuffer(
            queue,
            buffer,
            0U,
            &uniforms,
            sizeof(uniforms));
        cached = uniforms;
        cache_valid = true;
        return true;
    }

    ~progpu_native_engine() {
        const progpu::native::webgpu::dispatch_scope dispatch_scope(
            &webgpu_dispatch);
        if (image_mask_linear_bind_group != nullptr) {
            wgpuBindGroupRelease(image_mask_linear_bind_group);
        }
        if (image_mask_nearest_bind_group != nullptr) {
            wgpuBindGroupRelease(image_mask_nearest_bind_group);
        }
        if (image_mask_view != nullptr) {
            wgpuTextureViewRelease(image_mask_view);
        }
        if (image_mask_uniform_buffer != nullptr) {
            wgpuBufferDestroy(image_mask_uniform_buffer);
            wgpuBufferRelease(image_mask_uniform_buffer);
        }
        if (image_mask_layout != nullptr) {
            wgpuBindGroupLayoutRelease(image_mask_layout);
        }
        if (image_mask_pipeline != nullptr) {
            wgpuRenderPipelineRelease(image_mask_pipeline);
        }
        if (image_index_buffer != nullptr) {
            wgpuBufferDestroy(image_index_buffer);
            wgpuBufferRelease(image_index_buffer);
        }
        if (image_vertex_buffer != nullptr) {
            wgpuBufferDestroy(image_vertex_buffer);
            wgpuBufferRelease(image_vertex_buffer);
        }
        if (image_linear_bind_group != nullptr) {
            wgpuBindGroupRelease(image_linear_bind_group);
        }
        if (image_nearest_bind_group != nullptr) {
            wgpuBindGroupRelease(image_nearest_bind_group);
        }
        if (image_texture_view != nullptr) {
            wgpuTextureViewRelease(image_texture_view);
        }
        if (image_texture != nullptr) {
            wgpuTextureDestroy(image_texture);
            wgpuTextureRelease(image_texture);
        }
        if (image_linear_sampler != nullptr) {
            wgpuSamplerRelease(image_linear_sampler);
        }
        if (image_nearest_sampler != nullptr) {
            wgpuSamplerRelease(image_nearest_sampler);
        }
        if (image_uniform_bind_group != nullptr) {
            wgpuBindGroupRelease(image_uniform_bind_group);
        }
        if (image_uniform_buffer != nullptr) {
            wgpuBufferDestroy(image_uniform_buffer);
            wgpuBufferRelease(image_uniform_buffer);
        }
        if (image_texture_layout != nullptr) {
            wgpuBindGroupLayoutRelease(image_texture_layout);
        }
        if (image_uniform_layout != nullptr) {
            wgpuBindGroupLayoutRelease(image_uniform_layout);
        }
        if (image_pipeline != nullptr) {
            wgpuRenderPipelineRelease(image_pipeline);
        }
        if (image_shader != nullptr) {
            wgpuShaderModuleRelease(image_shader);
        }
        if (text_vertex_buffer != nullptr) {
            wgpuBufferDestroy(text_vertex_buffer);
            wgpuBufferRelease(text_vertex_buffer);
        }
        if (text_atlas_bind_group != nullptr) {
            wgpuBindGroupRelease(text_atlas_bind_group);
        }
        if (glyph_atlas_texture_view != nullptr) {
            wgpuTextureViewRelease(glyph_atlas_texture_view);
        }
        if (glyph_atlas_texture != nullptr) {
            wgpuTextureDestroy(glyph_atlas_texture);
            wgpuTextureRelease(glyph_atlas_texture);
        }
        if (glyph_atlas_sampler != nullptr) {
            wgpuSamplerRelease(glyph_atlas_sampler);
        }
        if (text_uniform_bind_group != nullptr) {
            wgpuBindGroupRelease(text_uniform_bind_group);
        }
        if (text_style_buffer != nullptr) {
            wgpuBufferDestroy(text_style_buffer);
            wgpuBufferRelease(text_style_buffer);
        }
        if (text_uniform_layout != nullptr) {
            wgpuBindGroupLayoutRelease(text_uniform_layout);
        }
        if (text_atlas_layout != nullptr) {
            wgpuBindGroupLayoutRelease(text_atlas_layout);
        }
        if (text_pipeline != nullptr) {
            wgpuRenderPipelineRelease(text_pipeline);
        }
        if (text_shader != nullptr) {
            wgpuShaderModuleRelease(text_shader);
        }
        if (glyph_raster_pipeline != nullptr) {
            wgpuComputePipelineRelease(glyph_raster_pipeline);
        }
        if (glyph_raster_pipeline_layout != nullptr) {
            wgpuPipelineLayoutRelease(glyph_raster_pipeline_layout);
        }
        if (glyph_raster_layout != nullptr) {
            wgpuBindGroupLayoutRelease(glyph_raster_layout);
        }
        if (glyph_raster_shader != nullptr) {
            wgpuShaderModuleRelease(glyph_raster_shader);
        }
        if (path_atlas_bind_group != nullptr) {
            wgpuBindGroupRelease(path_atlas_bind_group);
        }
        if (path_atlas_texture_view != nullptr) {
            wgpuTextureViewRelease(path_atlas_texture_view);
        }
        if (path_atlas_texture != nullptr) {
            wgpuTextureDestroy(path_atlas_texture);
            wgpuTextureRelease(path_atlas_texture);
        }
        if (path_atlas_sampler != nullptr) {
            wgpuSamplerRelease(path_atlas_sampler);
        }
        if (path_raster_pipeline != nullptr) {
            wgpuComputePipelineRelease(path_raster_pipeline);
        }
        if (path_raster_pipeline_layout != nullptr) {
            wgpuPipelineLayoutRelease(path_raster_pipeline_layout);
        }
        if (path_raster_layout != nullptr) {
            wgpuBindGroupLayoutRelease(path_raster_layout);
        }
        if (path_raster_shader != nullptr) {
            wgpuShaderModuleRelease(path_raster_shader);
        }
        if (index_buffer != nullptr) {
            wgpuBufferDestroy(index_buffer);
            wgpuBufferRelease(index_buffer);
        }
        if (vertex_buffer != nullptr) {
            wgpuBufferDestroy(vertex_buffer);
            wgpuBufferRelease(vertex_buffer);
        }
        if (uniform_bind_group != nullptr) {
            wgpuBindGroupRelease(uniform_bind_group);
        }
        if (analytic_uniform_bind_group != nullptr) {
            wgpuBindGroupRelease(analytic_uniform_bind_group);
        }
        if (analytic_atlas_bind_group != nullptr) {
            wgpuBindGroupRelease(analytic_atlas_bind_group);
        }
        if (analytic_sentinel_texture_view != nullptr) {
            wgpuTextureViewRelease(analytic_sentinel_texture_view);
        }
        if (analytic_sentinel_texture != nullptr) {
            wgpuTextureDestroy(analytic_sentinel_texture);
            wgpuTextureRelease(analytic_sentinel_texture);
        }
        if (analytic_sentinel_sampler != nullptr) {
            wgpuSamplerRelease(analytic_sentinel_sampler);
        }
        if (analytic_gradient_buffer != nullptr) {
            wgpuBufferDestroy(analytic_gradient_buffer);
            wgpuBufferRelease(analytic_gradient_buffer);
        }
        if (analytic_brush_buffer != nullptr) {
            wgpuBufferDestroy(analytic_brush_buffer);
            wgpuBufferRelease(analytic_brush_buffer);
        }
        if (analytic_uniform_buffer != nullptr) {
            wgpuBufferDestroy(analytic_uniform_buffer);
            wgpuBufferRelease(analytic_uniform_buffer);
        }
        if (uniform_buffer != nullptr) {
            wgpuBufferDestroy(uniform_buffer);
            wgpuBufferRelease(uniform_buffer);
        }
        if (uniform_layout != nullptr) {
            wgpuBindGroupLayoutRelease(uniform_layout);
        }
        if (analytic_uniform_layout != nullptr) {
            wgpuBindGroupLayoutRelease(analytic_uniform_layout);
        }
        if (analytic_atlas_layout != nullptr) {
            wgpuBindGroupLayoutRelease(analytic_atlas_layout);
        }
        if (analytic_pipeline != nullptr) {
            wgpuRenderPipelineRelease(analytic_pipeline);
        }
        if (pipeline != nullptr) {
            wgpuRenderPipelineRelease(pipeline);
        }
        if (shader != nullptr) {
            wgpuShaderModuleRelease(shader);
        }
        if (queue != nullptr) {
            wgpuQueueRelease(queue);
        }
        if (device != nullptr) {
            wgpuDeviceRelease(device);
        }
        if (instance != nullptr) {
            progpu::native::webgpu::instance_release(instance);
        }
    }

    progpu_native_status fail(
        progpu_native_status status,
        const char* message) {
        last_error = message == nullptr ? "Unknown native renderer error." : message;
        return status;
    }

    bool is_owner_thread() const noexcept {
        return owner_thread == std::this_thread::get_id();
    }

    bool ensure_vertex_buffer(std::uint64_t required_size) {
        if (required_size <= vertex_buffer_size && vertex_buffer != nullptr) {
            return true;
        }

        std::uint64_t new_size = std::max(
            initial_vertex_buffer_size,
            vertex_buffer_size);
        while (new_size < required_size) {
            if (new_size > std::numeric_limits<std::uint64_t>::max() / 2U) {
                return false;
            }
            new_size *= 2U;
        }

        WGPUBufferDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view("ProGPU native vector vertex buffer");
        descriptor.usage = WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst;
        descriptor.size = new_size;
        WGPUBuffer replacement = wgpuDeviceCreateBuffer(device, &descriptor);
        if (replacement == nullptr) {
            return false;
        }

        if (vertex_buffer != nullptr) {
            wgpuBufferDestroy(vertex_buffer);
            wgpuBufferRelease(vertex_buffer);
        }
        vertex_buffer = replacement;
        vertex_buffer_size = new_size;
        return true;
    }

    bool ensure_index_buffer(std::uint64_t required_size) {
        if (required_size <= index_buffer_size && index_buffer != nullptr) {
            return true;
        }

        std::uint64_t new_size = std::max(
            initial_index_buffer_size,
            index_buffer_size);
        while (new_size < required_size) {
            if (new_size > std::numeric_limits<std::uint64_t>::max() / 2U) {
                return false;
            }
            new_size *= 2U;
        }

        WGPUBufferDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view("ProGPU native vector index buffer");
        descriptor.usage = WGPUBufferUsage_Index | WGPUBufferUsage_CopyDst;
        descriptor.size = new_size;
        WGPUBuffer replacement = wgpuDeviceCreateBuffer(device, &descriptor);
        if (replacement == nullptr) {
            return false;
        }

        if (index_buffer != nullptr) {
            wgpuBufferDestroy(index_buffer);
            wgpuBufferRelease(index_buffer);
        }
        index_buffer = replacement;
        index_buffer_size = new_size;
        return true;
    }

    bool ensure_text_vertex_buffer(std::uint64_t required_size) {
        if (required_size <= text_vertex_buffer_size &&
            text_vertex_buffer != nullptr) {
            return true;
        }
        std::uint64_t new_size = std::max(
            initial_vertex_buffer_size,
            text_vertex_buffer_size);
        while (new_size < required_size) {
            if (new_size > std::numeric_limits<std::uint64_t>::max() / 2U) {
                return false;
            }
            new_size *= 2U;
        }
        WGPUBufferDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view("ProGPU native positioned glyph instances");
        descriptor.usage = WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst;
        descriptor.size = new_size;
        WGPUBuffer replacement = wgpuDeviceCreateBuffer(device, &descriptor);
        if (replacement == nullptr) {
            return false;
        }
        if (text_vertex_buffer != nullptr) {
            wgpuBufferDestroy(text_vertex_buffer);
            wgpuBufferRelease(text_vertex_buffer);
        }
        text_vertex_buffer = replacement;
        text_vertex_buffer_size = new_size;
        glyph_gpu_cache_valid = false;
        return true;
    }
};

namespace {

bool create_pipeline(progpu_native_engine& engine) {
    progpu::native::webgpu::wgsl_source wgsl(
        progpu::native::generated::vector_wgsl,
        progpu::native::generated::vector_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = progpu::native::webgpu::string_view("ProGPU shared Vector.wgsl");
    engine.shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.shader == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 8U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 48U, 6U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 52U, 7U)
    }};
    WGPUVertexBufferLayout vertex_buffer_layout{};
    vertex_buffer_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_buffer_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_buffer_layout.attributeCount = attributes.size();
    vertex_buffer_layout.attributes = attributes.data();

    WGPUVertexState vertex_state{};
    vertex_state.module = engine.shader;
    vertex_state.entryPoint = progpu::native::webgpu::string_view("vs_solid_rect");
    vertex_state.bufferCount = 1U;
    vertex_state.buffers = &vertex_buffer_layout;

    WGPUBlendState blend{};
    blend.color.srcFactor = WGPUBlendFactor_SrcAlpha;
    blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.srcFactor = WGPUBlendFactor_One;
    blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.alpha.operation = WGPUBlendOperation_Add;

    WGPUColorTargetState color_target{};
    color_target.format = engine.target_format;
    color_target.blend = &blend;
    color_target.writeMask = WGPUColorWriteMask_All;

    WGPUFragmentState fragment_state{};
    fragment_state.module = engine.shader;
    fragment_state.entryPoint = progpu::native::webgpu::string_view("fs_solid_rect_main_unmasked");
    fragment_state.targetCount = 1U;
    fragment_state.targets = &color_target;

    WGPURenderPipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native solid rectangle pipeline");
    pipeline_descriptor.vertex = vertex_state;
    pipeline_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    pipeline_descriptor.multisample.count = 1U;
    pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    pipeline_descriptor.fragment = &fragment_state;
    engine.pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    if (engine.pipeline == nullptr) {
        return false;
    }

    engine.uniform_layout = wgpuRenderPipelineGetBindGroupLayout(
        engine.pipeline,
        0U);
    if (engine.uniform_layout == nullptr) {
        return false;
    }

    WGPUBufferDescriptor uniform_descriptor{};
    uniform_descriptor.label = progpu::native::webgpu::string_view("ProGPU native frame uniforms");
    uniform_descriptor.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    uniform_descriptor.size = sizeof(gpu_uniforms);
    engine.uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &uniform_descriptor);
    if (engine.uniform_buffer == nullptr) {
        return false;
    }

    WGPUBindGroupEntry uniform_entry{};
    uniform_entry.binding = 0U;
    uniform_entry.buffer = engine.uniform_buffer;
    uniform_entry.size = sizeof(gpu_uniforms);
    WGPUBindGroupDescriptor bind_group_descriptor{};
    bind_group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native frame uniform bind group");
    bind_group_descriptor.layout = engine.uniform_layout;
    bind_group_descriptor.entryCount = 1U;
    bind_group_descriptor.entries = &uniform_entry;
    engine.uniform_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &bind_group_descriptor);
    return engine.uniform_bind_group != nullptr;
}

WGPUBindGroup create_analytic_uniform_bind_group(
    progpu_native_engine& engine,
    WGPUBuffer brush_buffer,
    std::uint64_t brush_buffer_size) {
    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, engine.analytic_uniform_buffer, 0U, sizeof(gpu_uniforms),
            nullptr, nullptr},
        {nullptr, 1U, brush_buffer, 0U, brush_buffer_size,
            nullptr, nullptr},
        {nullptr, 2U, engine.analytic_gradient_buffer, 0U, 32U,
            nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic bind group");
    descriptor.layout = engine.analytic_uniform_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

bool ensure_analytic_brush_buffer(
    progpu_native_engine& engine,
    std::uint64_t required_size) {
    if (required_size <= engine.analytic_brush_buffer_size &&
        engine.analytic_brush_buffer != nullptr &&
        engine.analytic_uniform_bind_group != nullptr) {
        return true;
    }

    std::uint64_t new_size = std::max(
        initial_brush_buffer_size,
        engine.analytic_brush_buffer_size);
    while (new_size < required_size) {
        if (new_size > std::numeric_limits<std::uint64_t>::max() / 2U) {
            return false;
        }
        new_size *= 2U;
    }

    WGPUBufferDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view("ProGPU native solid brush table");
    descriptor.usage = WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst;
    descriptor.size = new_size;
    WGPUBuffer replacement = wgpuDeviceCreateBuffer(engine.device, &descriptor);
    if (replacement == nullptr) {
        return false;
    }
    WGPUBindGroup replacement_group = create_analytic_uniform_bind_group(
        engine,
        replacement,
        new_size);
    if (replacement_group == nullptr) {
        wgpuBufferDestroy(replacement);
        wgpuBufferRelease(replacement);
        return false;
    }

    if (engine.analytic_uniform_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.analytic_uniform_bind_group);
    }
    if (engine.analytic_brush_buffer != nullptr) {
        wgpuBufferDestroy(engine.analytic_brush_buffer);
        wgpuBufferRelease(engine.analytic_brush_buffer);
    }
    engine.analytic_brush_buffer = replacement;
    engine.analytic_brush_buffer_size = new_size;
    engine.analytic_uniform_bind_group = replacement_group;
    return true;
}

bool create_analytic_pipeline(progpu_native_engine& engine) {
    const std::array<WGPUVertexAttribute, 8U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 48U, 6U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 52U, 7U)
    }};
    WGPUVertexBufferLayout vertex_buffer_layout{};
    vertex_buffer_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_buffer_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_buffer_layout.attributeCount = attributes.size();
    vertex_buffer_layout.attributes = attributes.data();

    WGPUVertexState vertex_state{};
    vertex_state.module = engine.shader;
    vertex_state.entryPoint = progpu::native::webgpu::string_view("vs_main");
    vertex_state.bufferCount = 1U;
    vertex_state.buffers = &vertex_buffer_layout;

    WGPUBlendState blend{};
    blend.color.srcFactor = WGPUBlendFactor_SrcAlpha;
    blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.srcFactor = WGPUBlendFactor_One;
    blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.alpha.operation = WGPUBlendOperation_Add;

    WGPUColorTargetState color_target{};
    color_target.format = engine.target_format;
    color_target.blend = &blend;
    color_target.writeMask = WGPUColorWriteMask_All;

    WGPUFragmentState fragment_state{};
    fragment_state.module = engine.shader;
    fragment_state.entryPoint = progpu::native::webgpu::string_view("fs_main_unmasked");
    fragment_state.targetCount = 1U;
    fragment_state.targets = &color_target;

    WGPURenderPipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic primitive pipeline");
    pipeline_descriptor.vertex = vertex_state;
    pipeline_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    pipeline_descriptor.multisample.count = 1U;
    pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    pipeline_descriptor.fragment = &fragment_state;
    engine.analytic_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    if (engine.analytic_pipeline == nullptr) {
        return false;
    }

    engine.analytic_uniform_layout = wgpuRenderPipelineGetBindGroupLayout(
        engine.analytic_pipeline,
        0U);
    engine.analytic_atlas_layout = wgpuRenderPipelineGetBindGroupLayout(
        engine.analytic_pipeline,
        1U);
    if (engine.analytic_uniform_layout == nullptr ||
        engine.analytic_atlas_layout == nullptr) {
        return false;
    }

    WGPUBufferDescriptor uniform_descriptor{};
    uniform_descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic frame uniforms");
    uniform_descriptor.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    uniform_descriptor.size = sizeof(gpu_uniforms);
    engine.analytic_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &uniform_descriptor);

    WGPUBufferDescriptor gradient_descriptor{};
    gradient_descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic gradient sentinel");
    gradient_descriptor.usage = WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst;
    gradient_descriptor.size = 32U;
    engine.analytic_gradient_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &gradient_descriptor);

    if (engine.analytic_uniform_buffer == nullptr ||
        engine.analytic_gradient_buffer == nullptr) {
        return false;
    }

    WGPUTextureDescriptor texture_descriptor{};
    texture_descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic sentinel texture");
    texture_descriptor.usage = WGPUTextureUsage_TextureBinding;
    texture_descriptor.dimension = WGPUTextureDimension_2D;
    texture_descriptor.size = {1U, 1U, 1U};
    texture_descriptor.format = WGPUTextureFormat_RGBA8Unorm;
    texture_descriptor.mipLevelCount = 1U;
    texture_descriptor.sampleCount = 1U;
    engine.analytic_sentinel_texture = wgpuDeviceCreateTexture(
        engine.device,
        &texture_descriptor);
    if (engine.analytic_sentinel_texture == nullptr) {
        return false;
    }
    engine.analytic_sentinel_texture_view = wgpuTextureCreateView(
        engine.analytic_sentinel_texture,
        nullptr);

    WGPUSamplerDescriptor sampler_descriptor{};
    sampler_descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic sentinel sampler");
    sampler_descriptor.addressModeU = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeV = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.magFilter = WGPUFilterMode_Nearest;
    sampler_descriptor.minFilter = WGPUFilterMode_Nearest;
    sampler_descriptor.mipmapFilter = WGPUMipmapFilterMode_Nearest;
    sampler_descriptor.lodMinClamp = 0.0F;
    sampler_descriptor.lodMaxClamp = 0.0F;
    sampler_descriptor.maxAnisotropy = 1U;
    engine.analytic_sentinel_sampler = wgpuDeviceCreateSampler(
        engine.device,
        &sampler_descriptor);
    if (engine.analytic_sentinel_texture_view == nullptr ||
        engine.analytic_sentinel_sampler == nullptr) {
        return false;
    }

    if (!ensure_analytic_brush_buffer(engine, gpu_brush_size)) {
        return false;
    }

    std::array<std::byte, gpu_brush_size> solid_brush{};
    constexpr float opacity = 1.0F;
    std::memcpy(solid_brush.data() + 4U, &opacity, sizeof(opacity));
    wgpuQueueWriteBuffer(
        engine.queue,
        engine.analytic_brush_buffer,
        0U,
        solid_brush.data(),
        solid_brush.size());
    std::array<std::byte, 32U> gradient_sentinel{};
    wgpuQueueWriteBuffer(
        engine.queue,
        engine.analytic_gradient_buffer,
        0U,
        gradient_sentinel.data(),
        gradient_sentinel.size());

    const std::array<WGPUBindGroupEntry, 2U> atlas_entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.analytic_sentinel_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U,
            nullptr, engine.analytic_sentinel_texture_view}
    }};
    WGPUBindGroupDescriptor atlas_bind_group_descriptor{};
    atlas_bind_group_descriptor.label =
        progpu::native::webgpu::string_view(
            "ProGPU native analytic atlas sentinel bind group");
    atlas_bind_group_descriptor.layout = engine.analytic_atlas_layout;
    atlas_bind_group_descriptor.entryCount = atlas_entries.size();
    atlas_bind_group_descriptor.entries = atlas_entries.data();
    engine.analytic_atlas_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &atlas_bind_group_descriptor);

    return engine.analytic_atlas_bind_group != nullptr;
}

bool create_path_resources(progpu_native_engine& engine) {
    if (engine.path_raster_pipeline != nullptr &&
        engine.path_atlas_bind_group != nullptr) {
        return true;
    }
    if (engine.path_raster_shader != nullptr ||
        engine.path_raster_pipeline != nullptr ||
        engine.path_raster_layout != nullptr ||
        engine.path_raster_pipeline_layout != nullptr ||
        engine.path_atlas_sampler != nullptr ||
        engine.path_atlas_texture != nullptr ||
        engine.path_atlas_texture_view != nullptr ||
        engine.path_atlas_bind_group != nullptr) {
        return false;
    }
    if (engine.analytic_pipeline == nullptr &&
        !create_analytic_pipeline(engine)) {
        return false;
    }

    progpu::native::webgpu::wgsl_source wgsl(
        progpu::native::generated::path_rasterizer_wgsl,
        progpu::native::generated::path_rasterizer_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = progpu::native::webgpu::string_view("ProGPU shared PathRasterizer.wgsl");
    engine.path_raster_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.path_raster_shader == nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 4U> layout_entries{};
    for (std::uint32_t index = 0U; index < layout_entries.size(); ++index) {
        layout_entries[index].binding = index;
        layout_entries[index].visibility = WGPUShaderStage_Compute;
        layout_entries[index].buffer.hasDynamicOffset = false;
        layout_entries[index].buffer.type = index == 3U
            ? WGPUBufferBindingType_Storage
            : WGPUBufferBindingType_ReadOnlyStorage;
    }
    layout_entries[0].buffer.minBindingSize = sizeof(gpu_path_uniforms);
    layout_entries[1].buffer.minBindingSize = sizeof(gpu_path_record);
    layout_entries[2].buffer.minBindingSize = sizeof(progpu_native_path_segment);
    layout_entries[3].buffer.minBindingSize = sizeof(std::uint32_t);
    WGPUBindGroupLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path raster bindings");
    layout_descriptor.entryCount = layout_entries.size();
    layout_descriptor.entries = layout_entries.data();
    engine.path_raster_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &layout_descriptor);
    if (engine.path_raster_layout == nullptr) {
        return false;
    }

    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path raster layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    pipeline_layout_descriptor.bindGroupLayouts = &engine.path_raster_layout;
    engine.path_raster_pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (engine.path_raster_pipeline_layout == nullptr) {
        return false;
    }

    WGPUComputePipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path raster pipeline");
    pipeline_descriptor.layout = engine.path_raster_pipeline_layout;
    pipeline_descriptor.compute.module = engine.path_raster_shader;
    pipeline_descriptor.compute.entryPoint = progpu::native::webgpu::string_view("cs_main");
    engine.path_raster_pipeline = wgpuDeviceCreateComputePipeline(
        engine.device,
        &pipeline_descriptor);
    if (engine.path_raster_pipeline == nullptr) {
        return false;
    }

    WGPUTextureDescriptor texture_descriptor{};
    texture_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained path atlas");
    texture_descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_CopyDst;
    texture_descriptor.dimension = WGPUTextureDimension_2D;
    texture_descriptor.size = {
        engine.path_atlas_size,
        engine.path_atlas_size,
        1U
    };
    texture_descriptor.format = WGPUTextureFormat_R8Unorm;
    texture_descriptor.mipLevelCount = 1U;
    texture_descriptor.sampleCount = 1U;
    engine.path_atlas_texture = wgpuDeviceCreateTexture(
        engine.device,
        &texture_descriptor);
    if (engine.path_atlas_texture == nullptr) {
        return false;
    }
    engine.path_atlas_texture_view = wgpuTextureCreateView(
        engine.path_atlas_texture,
        nullptr);

    WGPUSamplerDescriptor sampler_descriptor{};
    sampler_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path atlas sampler");
    sampler_descriptor.addressModeU = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeV = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.magFilter = WGPUFilterMode_Linear;
    sampler_descriptor.minFilter = WGPUFilterMode_Linear;
    sampler_descriptor.mipmapFilter = WGPUMipmapFilterMode_Nearest;
    sampler_descriptor.lodMinClamp = 0.0F;
    sampler_descriptor.lodMaxClamp = 0.0F;
    sampler_descriptor.maxAnisotropy = 1U;
    engine.path_atlas_sampler = wgpuDeviceCreateSampler(
        engine.device,
        &sampler_descriptor);
    if (engine.path_atlas_texture_view == nullptr ||
        engine.path_atlas_sampler == nullptr) {
        return false;
    }

    const std::array<WGPUBindGroupEntry, 2U> atlas_entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.path_atlas_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U,
            nullptr, engine.path_atlas_texture_view}
    }};
    WGPUBindGroupDescriptor atlas_descriptor{};
    atlas_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path atlas bind group");
    atlas_descriptor.layout = engine.analytic_atlas_layout;
    atlas_descriptor.entryCount = atlas_entries.size();
    atlas_descriptor.entries = atlas_entries.data();
    engine.path_atlas_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &atlas_descriptor);
    if (engine.path_atlas_bind_group == nullptr) {
        return false;
    }
    ++engine.path_atlas_generation;
    return true;
}

bool create_glyph_resources(progpu_native_engine& engine) {
    if (engine.glyph_raster_pipeline != nullptr &&
        engine.text_pipeline != nullptr &&
        engine.text_uniform_bind_group != nullptr &&
        engine.text_atlas_bind_group != nullptr) {
        return true;
    }
    if (engine.glyph_raster_shader != nullptr ||
        engine.glyph_raster_pipeline != nullptr ||
        engine.glyph_raster_layout != nullptr ||
        engine.glyph_raster_pipeline_layout != nullptr ||
        engine.text_shader != nullptr || engine.text_pipeline != nullptr ||
        engine.text_uniform_layout != nullptr ||
        engine.text_atlas_layout != nullptr ||
        engine.text_style_buffer != nullptr ||
        engine.text_uniform_bind_group != nullptr ||
        engine.glyph_atlas_sampler != nullptr ||
        engine.glyph_atlas_texture != nullptr ||
        engine.glyph_atlas_texture_view != nullptr ||
        engine.text_atlas_bind_group != nullptr) {
        return false;
    }
    if (engine.analytic_pipeline == nullptr &&
        !create_analytic_pipeline(engine)) {
        return false;
    }

    progpu::native::webgpu::wgsl_source glyph_wgsl(
        progpu::native::generated::glyph_rasterizer_wgsl,
        progpu::native::generated::glyph_rasterizer_wgsl_size);
    WGPUShaderModuleDescriptor glyph_shader_descriptor{};
    glyph_shader_descriptor.nextInChain = glyph_wgsl.chain();
    glyph_shader_descriptor.label = progpu::native::webgpu::string_view("ProGPU shared GlyphRasterizer.wgsl");
    engine.glyph_raster_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &glyph_shader_descriptor);
    if (engine.glyph_raster_shader == nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 4U> compute_entries{};
    for (std::uint32_t index = 0U; index < compute_entries.size(); ++index) {
        compute_entries[index].binding = index;
        compute_entries[index].visibility = WGPUShaderStage_Compute;
        compute_entries[index].buffer.type = index == 0U
            ? WGPUBufferBindingType_Uniform
            : index == 3U
            ? WGPUBufferBindingType_Storage
            : WGPUBufferBindingType_ReadOnlyStorage;
    }
    compute_entries[0].buffer.hasDynamicOffset = true;
    compute_entries[0].buffer.minBindingSize = sizeof(gpu_glyph_uniforms);
    compute_entries[1].buffer.minBindingSize = sizeof(gpu_glyph_record);
    compute_entries[2].buffer.minBindingSize = sizeof(progpu_native_path_segment);
    compute_entries[3].buffer.minBindingSize = sizeof(std::uint32_t);
    WGPUBindGroupLayoutDescriptor compute_layout_descriptor{};
    compute_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native glyph raster bindings");
    compute_layout_descriptor.entryCount = compute_entries.size();
    compute_layout_descriptor.entries = compute_entries.data();
    engine.glyph_raster_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &compute_layout_descriptor);
    if (engine.glyph_raster_layout == nullptr) {
        return false;
    }
    WGPUPipelineLayoutDescriptor compute_pipeline_layout_descriptor{};
    compute_pipeline_layout_descriptor.label =
        progpu::native::webgpu::string_view(
            "ProGPU native glyph raster layout");
    compute_pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    compute_pipeline_layout_descriptor.bindGroupLayouts =
        &engine.glyph_raster_layout;
    engine.glyph_raster_pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &compute_pipeline_layout_descriptor);
    if (engine.glyph_raster_pipeline_layout == nullptr) {
        return false;
    }
    WGPUComputePipelineDescriptor compute_pipeline_descriptor{};
    compute_pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native glyph raster pipeline");
    compute_pipeline_descriptor.layout = engine.glyph_raster_pipeline_layout;
    compute_pipeline_descriptor.compute.module = engine.glyph_raster_shader;
    compute_pipeline_descriptor.compute.entryPoint = progpu::native::webgpu::string_view("cs_main");
    engine.glyph_raster_pipeline = wgpuDeviceCreateComputePipeline(
        engine.device,
        &compute_pipeline_descriptor);
    if (engine.glyph_raster_pipeline == nullptr) {
        return false;
    }

    progpu::native::webgpu::wgsl_source text_wgsl(
        progpu::native::generated::text_wgsl,
        progpu::native::generated::text_wgsl_size);
    WGPUShaderModuleDescriptor text_shader_descriptor{};
    text_shader_descriptor.nextInChain = text_wgsl.chain();
    text_shader_descriptor.label = progpu::native::webgpu::string_view("ProGPU shared Text.wgsl");
    engine.text_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &text_shader_descriptor);
    if (engine.text_shader == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 8U> text_attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 16U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 24U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 40U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 56U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 72U, 6U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 88U, 7U)
    }};
    WGPUVertexBufferLayout text_vertex_layout{};
    text_vertex_layout.arrayStride = sizeof(gpu_glyph_instance);
    text_vertex_layout.stepMode = WGPUVertexStepMode_Instance;
    text_vertex_layout.attributeCount = text_attributes.size();
    text_vertex_layout.attributes = text_attributes.data();
    WGPUVertexState text_vertex_state{};
    text_vertex_state.module = engine.text_shader;
    text_vertex_state.entryPoint = progpu::native::webgpu::string_view("vs_main");
    text_vertex_state.bufferCount = 1U;
    text_vertex_state.buffers = &text_vertex_layout;
    WGPUBlendState text_blend{};
    text_blend.color.srcFactor = WGPUBlendFactor_SrcAlpha;
    text_blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    text_blend.color.operation = WGPUBlendOperation_Add;
    text_blend.alpha.srcFactor = WGPUBlendFactor_One;
    text_blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    text_blend.alpha.operation = WGPUBlendOperation_Add;
    WGPUColorTargetState text_target{};
    text_target.format = engine.target_format;
    text_target.blend = &text_blend;
    text_target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState text_fragment_state{};
    text_fragment_state.module = engine.text_shader;
    text_fragment_state.entryPoint = progpu::native::webgpu::string_view("fs_main_unmasked");
    text_fragment_state.targetCount = 1U;
    text_fragment_state.targets = &text_target;
    WGPURenderPipelineDescriptor text_pipeline_descriptor{};
    text_pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native positioned glyph pipeline");
    text_pipeline_descriptor.vertex = text_vertex_state;
    text_pipeline_descriptor.primitive.topology =
        WGPUPrimitiveTopology_TriangleList;
    text_pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    text_pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    text_pipeline_descriptor.multisample.count = 1U;
    text_pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    text_pipeline_descriptor.fragment = &text_fragment_state;
    engine.text_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &text_pipeline_descriptor);
    if (engine.text_pipeline == nullptr) {
        return false;
    }
    engine.text_uniform_layout = wgpuRenderPipelineGetBindGroupLayout(
        engine.text_pipeline,
        0U);
    engine.text_atlas_layout = wgpuRenderPipelineGetBindGroupLayout(
        engine.text_pipeline,
        1U);
    if (engine.text_uniform_layout == nullptr ||
        engine.text_atlas_layout == nullptr) {
        return false;
    }

    WGPUBufferDescriptor style_descriptor{};
    style_descriptor.label = progpu::native::webgpu::string_view("ProGPU native text style sentinel");
    style_descriptor.usage = WGPUBufferUsage_Storage |
        WGPUBufferUsage_CopyDst;
    style_descriptor.size = 32U;
    engine.text_style_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &style_descriptor);
    if (engine.text_style_buffer == nullptr) {
        return false;
    }
    std::array<std::byte, 32U> style_bytes{};
    wgpuQueueWriteBuffer(
        engine.queue,
        engine.text_style_buffer,
        0U,
        style_bytes.data(),
        style_bytes.size());
    const std::array<WGPUBindGroupEntry, 2U> uniform_entries{{
        {nullptr, 0U, engine.analytic_uniform_buffer, 0U,
            sizeof(gpu_uniforms), nullptr, nullptr},
        {nullptr, 1U, engine.text_style_buffer, 0U, 32U, nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor uniform_group_descriptor{};
    uniform_group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native text uniform bind group");
    uniform_group_descriptor.layout = engine.text_uniform_layout;
    uniform_group_descriptor.entryCount = uniform_entries.size();
    uniform_group_descriptor.entries = uniform_entries.data();
    engine.text_uniform_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &uniform_group_descriptor);
    if (engine.text_uniform_bind_group == nullptr) {
        return false;
    }

    WGPUTextureDescriptor atlas_descriptor{};
    atlas_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained glyph atlas");
    atlas_descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_CopyDst;
    atlas_descriptor.dimension = WGPUTextureDimension_2D;
    atlas_descriptor.size = {
        engine.glyph_atlas_size,
        engine.glyph_atlas_size,
        1U
    };
    atlas_descriptor.format = WGPUTextureFormat_R8Unorm;
    atlas_descriptor.mipLevelCount = 1U;
    atlas_descriptor.sampleCount = 1U;
    engine.glyph_atlas_texture = wgpuDeviceCreateTexture(
        engine.device,
        &atlas_descriptor);
    if (engine.glyph_atlas_texture == nullptr) {
        return false;
    }
    engine.glyph_atlas_texture_view = wgpuTextureCreateView(
        engine.glyph_atlas_texture,
        nullptr);
    WGPUSamplerDescriptor sampler_descriptor{};
    sampler_descriptor.label = progpu::native::webgpu::string_view("ProGPU native glyph atlas sampler");
    sampler_descriptor.addressModeU = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeV = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.magFilter = WGPUFilterMode_Linear;
    sampler_descriptor.minFilter = WGPUFilterMode_Linear;
    sampler_descriptor.mipmapFilter = WGPUMipmapFilterMode_Nearest;
    sampler_descriptor.lodMinClamp = 0.0F;
    sampler_descriptor.lodMaxClamp = 0.0F;
    sampler_descriptor.maxAnisotropy = 1U;
    engine.glyph_atlas_sampler = wgpuDeviceCreateSampler(
        engine.device,
        &sampler_descriptor);
    if (engine.glyph_atlas_texture_view == nullptr ||
        engine.glyph_atlas_sampler == nullptr) {
        return false;
    }
    const std::array<WGPUBindGroupEntry, 3U> atlas_entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.glyph_atlas_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U,
            nullptr, engine.glyph_atlas_texture_view},
        {nullptr, 2U, nullptr, 0U, 0U,
            nullptr, engine.analytic_sentinel_texture_view}
    }};
    WGPUBindGroupDescriptor atlas_group_descriptor{};
    atlas_group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native text atlas bind group");
    atlas_group_descriptor.layout = engine.text_atlas_layout;
    atlas_group_descriptor.entryCount = atlas_entries.size();
    atlas_group_descriptor.entries = atlas_entries.data();
    engine.text_atlas_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &atlas_group_descriptor);
    if (engine.text_atlas_bind_group == nullptr) {
        return false;
    }
    ++engine.glyph_atlas_generation;
    return true;
}

bool resize_path_atlas(
    progpu_native_engine& engine,
    std::uint32_t requested_size) {
    if (requested_size <= engine.path_atlas_size) {
        return true;
    }
    WGPUTextureDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained path atlas");
    descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_CopyDst;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {requested_size, requested_size, 1U};
    descriptor.format = WGPUTextureFormat_R8Unorm;
    descriptor.mipLevelCount = 1U;
    descriptor.sampleCount = 1U;
    WGPUTexture texture = wgpuDeviceCreateTexture(engine.device, &descriptor);
    if (texture == nullptr) {
        return false;
    }
    WGPUTextureView view = wgpuTextureCreateView(texture, nullptr);
    if (view == nullptr) {
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }
    const std::array<WGPUBindGroupEntry, 2U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.path_atlas_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, view}
    }};
    WGPUBindGroupDescriptor group_descriptor{};
    group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path atlas bind group");
    group_descriptor.layout = engine.analytic_atlas_layout;
    group_descriptor.entryCount = entries.size();
    group_descriptor.entries = entries.data();
    WGPUBindGroup group = wgpuDeviceCreateBindGroup(
        engine.device,
        &group_descriptor);
    if (group == nullptr) {
        wgpuTextureViewRelease(view);
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }

    wgpuBindGroupRelease(engine.path_atlas_bind_group);
    wgpuTextureViewRelease(engine.path_atlas_texture_view);
    wgpuTextureDestroy(engine.path_atlas_texture);
    wgpuTextureRelease(engine.path_atlas_texture);
    engine.path_atlas_bind_group = group;
    engine.path_atlas_texture_view = view;
    engine.path_atlas_texture = texture;
    engine.path_atlas_size = requested_size;
    ++engine.path_atlas_generation;
    return true;
}

bool resize_glyph_atlas(
    progpu_native_engine& engine,
    std::uint32_t requested_size) {
    if (requested_size <= engine.glyph_atlas_size) {
        return true;
    }
    WGPUTextureDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained glyph atlas");
    descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_CopyDst;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {requested_size, requested_size, 1U};
    descriptor.format = WGPUTextureFormat_R8Unorm;
    descriptor.mipLevelCount = 1U;
    descriptor.sampleCount = 1U;
    WGPUTexture texture = wgpuDeviceCreateTexture(engine.device, &descriptor);
    if (texture == nullptr) {
        return false;
    }
    WGPUTextureView view = wgpuTextureCreateView(texture, nullptr);
    if (view == nullptr) {
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }
    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.glyph_atlas_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, view},
        {nullptr, 2U, nullptr, 0U, 0U,
            nullptr, engine.analytic_sentinel_texture_view}
    }};
    WGPUBindGroupDescriptor group_descriptor{};
    group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native text atlas bind group");
    group_descriptor.layout = engine.text_atlas_layout;
    group_descriptor.entryCount = entries.size();
    group_descriptor.entries = entries.data();
    WGPUBindGroup group = wgpuDeviceCreateBindGroup(
        engine.device,
        &group_descriptor);
    if (group == nullptr) {
        wgpuTextureViewRelease(view);
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }

    wgpuBindGroupRelease(engine.text_atlas_bind_group);
    wgpuTextureViewRelease(engine.glyph_atlas_texture_view);
    wgpuTextureDestroy(engine.glyph_atlas_texture);
    wgpuTextureRelease(engine.glyph_atlas_texture);
    engine.text_atlas_bind_group = group;
    engine.glyph_atlas_texture_view = view;
    engine.glyph_atlas_texture = texture;
    engine.glyph_atlas_size = requested_size;
    ++engine.glyph_atlas_generation;
    ++engine.glyph_atlas_growth_count;
    return true;
}

WGPUBindGroup create_image_texture_bind_group(
    progpu_native_engine& engine,
    WGPUSampler sampler,
    WGPUTextureView view,
    const char* label) {
    const std::array<WGPUBindGroupEntry, 2U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, view}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(label);
    descriptor.layout = engine.image_texture_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

bool create_image_resources(progpu_native_engine& engine) {
    if (engine.image_pipeline != nullptr) {
        return true;
    }
    if (engine.image_shader != nullptr ||
        engine.image_uniform_layout != nullptr ||
        engine.image_texture_layout != nullptr ||
        engine.image_uniform_buffer != nullptr ||
        engine.image_uniform_bind_group != nullptr ||
        engine.image_nearest_sampler != nullptr ||
        engine.image_linear_sampler != nullptr ||
        engine.image_vertex_buffer != nullptr ||
        engine.image_index_buffer != nullptr) {
        return false;
    }

    progpu::native::webgpu::wgsl_source wgsl(
        progpu::native::generated::texture_wgsl,
        progpu::native::generated::texture_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = progpu::native::webgpu::string_view("ProGPU shared Texture.wgsl");
    engine.image_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.image_shader == nullptr) {
        return false;
    }

    WGPUBindGroupLayoutEntry uniform_layout_entry{};
    uniform_layout_entry.binding = 0U;
    uniform_layout_entry.visibility = WGPUShaderStage_Vertex |
        WGPUShaderStage_Fragment;
    uniform_layout_entry.buffer.type = WGPUBufferBindingType_Uniform;
    uniform_layout_entry.buffer.minBindingSize = sizeof(gpu_uniforms);
    WGPUBindGroupLayoutDescriptor uniform_layout_descriptor{};
    uniform_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native image uniform layout");
    uniform_layout_descriptor.entryCount = 1U;
    uniform_layout_descriptor.entries = &uniform_layout_entry;
    engine.image_uniform_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &uniform_layout_descriptor);

    std::array<WGPUBindGroupLayoutEntry, 2U> texture_layout_entries{};
    texture_layout_entries[0].binding = 0U;
    texture_layout_entries[0].visibility = WGPUShaderStage_Fragment;
    texture_layout_entries[0].sampler.type = WGPUSamplerBindingType_Filtering;
    texture_layout_entries[1].binding = 1U;
    texture_layout_entries[1].visibility = WGPUShaderStage_Fragment;
    texture_layout_entries[1].texture.sampleType =
        WGPUTextureSampleType_Float;
    texture_layout_entries[1].texture.viewDimension =
        WGPUTextureViewDimension_2D;
    texture_layout_entries[1].texture.multisampled = false;
    WGPUBindGroupLayoutDescriptor texture_layout_descriptor{};
    texture_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native image texture layout");
    texture_layout_descriptor.entryCount = texture_layout_entries.size();
    texture_layout_descriptor.entries = texture_layout_entries.data();
    engine.image_texture_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &texture_layout_descriptor);
    if (engine.image_uniform_layout == nullptr ||
        engine.image_texture_layout == nullptr) {
        return false;
    }

    const std::array<WGPUBindGroupLayout, 2U> pipeline_layouts{{
        engine.image_uniform_layout,
        engine.image_texture_layout
    }};
    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native unmasked image layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = pipeline_layouts.size();
    pipeline_layout_descriptor.bindGroupLayouts = pipeline_layouts.data();
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 7U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 48U, 6U)
    }};
    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_layout.attributeCount = attributes.size();
    vertex_layout.attributes = attributes.data();
    WGPUVertexState vertex_state{};
    vertex_state.module = engine.image_shader;
    vertex_state.entryPoint = progpu::native::webgpu::string_view("vs_main");
    vertex_state.bufferCount = 1U;
    vertex_state.buffers = &vertex_layout;
    WGPUBlendState blend{};
    blend.color.srcFactor = WGPUBlendFactor_SrcAlpha;
    blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.srcFactor = WGPUBlendFactor_One;
    blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.alpha.operation = WGPUBlendOperation_Add;
    WGPUColorTargetState target{};
    target.format = engine.target_format;
    target.blend = &blend;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.image_shader;
    fragment.entryPoint = progpu::native::webgpu::string_view("fs_main_unmasked");
    fragment.targetCount = 1U;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained image pipeline");
    pipeline_descriptor.layout = pipeline_layout;
    pipeline_descriptor.vertex = vertex_state;
    pipeline_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    pipeline_descriptor.multisample.count = 1U;
    pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    pipeline_descriptor.fragment = &fragment;
    engine.image_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.image_pipeline == nullptr) {
        return false;
    }

    WGPUBufferDescriptor uniform_descriptor{};
    uniform_descriptor.label = progpu::native::webgpu::string_view("ProGPU native image frame uniforms");
    uniform_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    uniform_descriptor.size = sizeof(gpu_uniforms);
    engine.image_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &uniform_descriptor);
    WGPUBufferDescriptor vertex_descriptor{};
    vertex_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained image vertices");
    vertex_descriptor.usage = WGPUBufferUsage_Vertex |
        WGPUBufferUsage_CopyDst;
    vertex_descriptor.size = sizeof(engine.image_vertices);
    engine.image_vertex_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &vertex_descriptor);
    WGPUBufferDescriptor index_descriptor{};
    index_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained image indices");
    index_descriptor.usage = WGPUBufferUsage_Index |
        WGPUBufferUsage_CopyDst;
    index_descriptor.size = 6U * sizeof(std::uint32_t);
    engine.image_index_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &index_descriptor);
    if (engine.image_uniform_buffer == nullptr ||
        engine.image_vertex_buffer == nullptr ||
        engine.image_index_buffer == nullptr) {
        return false;
    }

    WGPUBindGroupEntry uniform_entry{};
    uniform_entry.binding = 0U;
    uniform_entry.buffer = engine.image_uniform_buffer;
    uniform_entry.size = sizeof(gpu_uniforms);
    WGPUBindGroupDescriptor uniform_group_descriptor{};
    uniform_group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native image uniform bind group");
    uniform_group_descriptor.layout = engine.image_uniform_layout;
    uniform_group_descriptor.entryCount = 1U;
    uniform_group_descriptor.entries = &uniform_entry;
    engine.image_uniform_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &uniform_group_descriptor);
    if (engine.image_uniform_bind_group == nullptr) {
        return false;
    }

    const auto create_sampler = [&](WGPUFilterMode filter) {
        WGPUSamplerDescriptor descriptor{};
        descriptor.addressModeU = WGPUAddressMode_ClampToEdge;
        descriptor.addressModeV = WGPUAddressMode_ClampToEdge;
        descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
        descriptor.magFilter = filter;
        descriptor.minFilter = filter;
        descriptor.mipmapFilter = WGPUMipmapFilterMode_Nearest;
        descriptor.lodMinClamp = 0.0F;
        descriptor.lodMaxClamp = 0.0F;
        descriptor.maxAnisotropy = 1U;
        return wgpuDeviceCreateSampler(engine.device, &descriptor);
    };
    engine.image_nearest_sampler = create_sampler(WGPUFilterMode_Nearest);
    engine.image_linear_sampler = create_sampler(WGPUFilterMode_Linear);
    if (engine.image_nearest_sampler == nullptr ||
        engine.image_linear_sampler == nullptr) {
        return false;
    }

    constexpr std::array<std::uint32_t, 6U> indices{
        0U, 1U, 2U, 0U, 2U, 3U};
    wgpuQueueWriteBuffer(
        engine.queue,
        engine.image_index_buffer,
        0U,
        indices.data(),
        sizeof(indices));
    return true;
}

WGPUBindGroup create_image_mask_bind_group(
    progpu_native_engine& engine,
    WGPUSampler sampler,
    WGPUTextureView view,
    const char* label) {
    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, view},
        {nullptr, 2U, engine.image_mask_uniform_buffer, 0U,
            sizeof(gpu_mask_sampling_uniforms), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(label);
    descriptor.layout = engine.image_mask_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

bool create_image_mask_resources(progpu_native_engine& engine) {
    if (engine.image_mask_pipeline != nullptr) {
        return true;
    }
    if (engine.image_pipeline == nullptr || engine.image_shader == nullptr ||
        engine.image_uniform_layout == nullptr ||
        engine.image_texture_layout == nullptr ||
        engine.image_mask_layout != nullptr ||
        engine.image_mask_uniform_buffer != nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 3U> mask_entries{};
    mask_entries[0].binding = 0U;
    mask_entries[0].visibility = WGPUShaderStage_Fragment;
    mask_entries[0].sampler.type = WGPUSamplerBindingType_Filtering;
    mask_entries[1].binding = 1U;
    mask_entries[1].visibility = WGPUShaderStage_Fragment;
    mask_entries[1].texture.sampleType = WGPUTextureSampleType_Float;
    mask_entries[1].texture.viewDimension = WGPUTextureViewDimension_2D;
    mask_entries[1].texture.multisampled = false;
    mask_entries[2].binding = 2U;
    mask_entries[2].visibility = WGPUShaderStage_Fragment;
    mask_entries[2].buffer.type = WGPUBufferBindingType_Uniform;
    mask_entries[2].buffer.minBindingSize = sizeof(gpu_mask_sampling_uniforms);
    WGPUBindGroupLayoutDescriptor mask_layout_descriptor{};
    mask_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native image mask layout");
    mask_layout_descriptor.entryCount = mask_entries.size();
    mask_layout_descriptor.entries = mask_entries.data();
    engine.image_mask_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &mask_layout_descriptor);
    if (engine.image_mask_layout == nullptr) {
        return false;
    }

    const std::array<WGPUBindGroupLayout, 3U> layouts{{
        engine.image_uniform_layout,
        engine.image_texture_layout,
        engine.image_mask_layout
    }};
    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native masked image layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = layouts.size();
    pipeline_layout_descriptor.bindGroupLayouts = layouts.data();
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 7U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 48U, 6U)
    }};
    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_layout.attributeCount = attributes.size();
    vertex_layout.attributes = attributes.data();
    WGPUVertexState vertex_state{};
    vertex_state.module = engine.image_shader;
    vertex_state.entryPoint = progpu::native::webgpu::string_view("vs_main");
    vertex_state.bufferCount = 1U;
    vertex_state.buffers = &vertex_layout;
    WGPUBlendState blend{};
    blend.color.srcFactor = WGPUBlendFactor_SrcAlpha;
    blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.srcFactor = WGPUBlendFactor_One;
    blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.alpha.operation = WGPUBlendOperation_Add;
    WGPUColorTargetState target{};
    target.format = engine.target_format;
    target.blend = &blend;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.image_shader;
    fragment.entryPoint = progpu::native::webgpu::string_view("fs_main");
    fragment.targetCount = 1U;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained masked image pipeline");
    pipeline_descriptor.layout = pipeline_layout;
    pipeline_descriptor.vertex = vertex_state;
    pipeline_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    pipeline_descriptor.multisample.count = 1U;
    pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    pipeline_descriptor.fragment = &fragment;
    engine.image_mask_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.image_mask_pipeline == nullptr) {
        return false;
    }

    WGPUBufferDescriptor buffer_descriptor{};
    buffer_descriptor.label = progpu::native::webgpu::string_view("ProGPU native image mask sampling uniforms");
    buffer_descriptor.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    buffer_descriptor.size = sizeof(gpu_mask_sampling_uniforms);
    engine.image_mask_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &buffer_descriptor);
    return engine.image_mask_uniform_buffer != nullptr;
}

bool update_image_mask(
    progpu_native_engine& engine,
    const progpu_native_image_frame& frame,
    bool& uploaded_uniforms) {
    WGPUTextureView view = reinterpret_cast<WGPUTextureView>(
        frame.external_mask_view);
    const bool replace = engine.image_mask_view == nullptr ||
        engine.image_mask_view != view ||
        engine.image_mask_width != frame.mask_width ||
        engine.image_mask_height != frame.mask_height;
    if (replace) {
        progpu::native::webgpu::texture_view_add_ref(view);
        WGPUBindGroup nearest = create_image_mask_bind_group(
            engine,
            engine.image_nearest_sampler,
            view,
            "ProGPU native nearest image mask bind group");
        WGPUBindGroup linear = create_image_mask_bind_group(
            engine,
            engine.image_linear_sampler,
            view,
            "ProGPU native linear image mask bind group");
        if (nearest == nullptr || linear == nullptr) {
            if (linear != nullptr) wgpuBindGroupRelease(linear);
            if (nearest != nullptr) wgpuBindGroupRelease(nearest);
            wgpuTextureViewRelease(view);
            return false;
        }
        if (engine.image_mask_linear_bind_group != nullptr) {
            wgpuBindGroupRelease(engine.image_mask_linear_bind_group);
        }
        if (engine.image_mask_nearest_bind_group != nullptr) {
            wgpuBindGroupRelease(engine.image_mask_nearest_bind_group);
        }
        if (engine.image_mask_view != nullptr) {
            wgpuTextureViewRelease(engine.image_mask_view);
        }
        engine.image_mask_view = view;
        engine.image_mask_nearest_bind_group = nearest;
        engine.image_mask_linear_bind_group = linear;
        engine.image_mask_width = frame.mask_width;
        engine.image_mask_height = frame.mask_height;
    }

    gpu_mask_sampling_uniforms uniforms{};
    uniforms.coordinate0[0] = frame.mask_destination_rect.x * frame.dpi_scale;
    uniforms.coordinate0[1] = frame.mask_destination_rect.y * frame.dpi_scale;
    uniforms.coordinate1[0] = 1.0F /
        (frame.mask_destination_rect.width * frame.dpi_scale);
    uniforms.coordinate1[1] = 1.0F /
        (frame.mask_destination_rect.height * frame.dpi_scale);
    uniforms.options[0] = 1.0F;
    if (!engine.image_mask_uniform_cache_valid ||
        std::memcmp(
            &engine.cached_image_mask_uniforms,
            &uniforms,
            sizeof(uniforms)) != 0) {
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.image_mask_uniform_buffer,
            0U,
            &uniforms,
            sizeof(uniforms));
        engine.cached_image_mask_uniforms = uniforms;
        engine.image_mask_uniform_cache_valid = true;
        uploaded_uniforms = true;
    }
    engine.image_mask_revision = frame.mask_revision;
    return true;
}

bool upload_image_texture(
    progpu_native_engine& engine,
    const progpu_native_image_frame& frame) {
    const bool external = (frame.source_flags &
        PROGPU_NATIVE_IMAGE_SOURCE_EXTERNAL_VIEW) != 0U;
    WGPUTextureView external_view = reinterpret_cast<WGPUTextureView>(
        frame.external_source_view);
    const bool source_missing = external
        ? engine.image_texture_view == nullptr
        : engine.image_texture == nullptr;
    const bool replace = source_missing ||
        engine.image_source_is_external != external ||
        engine.image_width != frame.image_width ||
        engine.image_height != frame.image_height ||
        (external && engine.image_texture_view != external_view);
    WGPUTexture texture = engine.image_texture;
    WGPUTextureView view = engine.image_texture_view;
    WGPUBindGroup nearest_group = engine.image_nearest_bind_group;
    WGPUBindGroup linear_group = engine.image_linear_bind_group;
    if (replace && external) {
        texture = nullptr;
        view = external_view;
        progpu::native::webgpu::texture_view_add_ref(view);
        nearest_group = create_image_texture_bind_group(
            engine,
            engine.image_nearest_sampler,
            view,
            "ProGPU native external nearest image bind group");
        linear_group = create_image_texture_bind_group(
            engine,
            engine.image_linear_sampler,
            view,
            "ProGPU native external linear image bind group");
        if (nearest_group == nullptr || linear_group == nullptr) {
            if (linear_group != nullptr) {
                wgpuBindGroupRelease(linear_group);
            }
            if (nearest_group != nullptr) {
                wgpuBindGroupRelease(nearest_group);
            }
            wgpuTextureViewRelease(view);
            return false;
        }
    } else if (replace) {
        WGPUTextureDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained RGBA image");
        descriptor.usage = WGPUTextureUsage_TextureBinding |
            WGPUTextureUsage_CopyDst;
        descriptor.dimension = WGPUTextureDimension_2D;
        descriptor.size = {frame.image_width, frame.image_height, 1U};
        descriptor.format = WGPUTextureFormat_RGBA8Unorm;
        descriptor.mipLevelCount = 1U;
        descriptor.sampleCount = 1U;
        texture = wgpuDeviceCreateTexture(engine.device, &descriptor);
        if (texture == nullptr) {
            return false;
        }
        view = wgpuTextureCreateView(texture, nullptr);
        if (view == nullptr) {
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
            return false;
        }
        nearest_group = create_image_texture_bind_group(
            engine,
            engine.image_nearest_sampler,
            view,
            "ProGPU native nearest image bind group");
        linear_group = create_image_texture_bind_group(
            engine,
            engine.image_linear_sampler,
            view,
            "ProGPU native linear image bind group");
        if (nearest_group == nullptr || linear_group == nullptr) {
            if (linear_group != nullptr) {
                wgpuBindGroupRelease(linear_group);
            }
            if (nearest_group != nullptr) {
                wgpuBindGroupRelease(nearest_group);
            }
            wgpuTextureViewRelease(view);
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
            return false;
        }
    }

    if (!external) {
        progpu::native::webgpu::image_copy_texture destination{};
        destination.texture = texture;
        destination.aspect = WGPUTextureAspect_All;
        progpu::native::webgpu::texture_data_layout layout{};
        layout.bytesPerRow = frame.row_bytes;
        layout.rowsPerImage = frame.image_height;
        const WGPUExtent3D extent{frame.image_width, frame.image_height, 1U};
        const std::size_t upload_bytes =
            static_cast<std::size_t>(frame.row_bytes) *
                (frame.image_height - 1U) +
            static_cast<std::size_t>(frame.image_width) * 4U;
        wgpuQueueWriteTexture(
            engine.queue,
            &destination,
            frame.rgba_pixels,
            upload_bytes,
            &layout,
            &extent);
    }

    if (replace) {
        if (engine.image_linear_bind_group != nullptr) {
            wgpuBindGroupRelease(engine.image_linear_bind_group);
        }
        if (engine.image_nearest_bind_group != nullptr) {
            wgpuBindGroupRelease(engine.image_nearest_bind_group);
        }
        if (engine.image_texture_view != nullptr) {
            wgpuTextureViewRelease(engine.image_texture_view);
        }
        if (engine.image_texture != nullptr) {
            wgpuTextureDestroy(engine.image_texture);
            wgpuTextureRelease(engine.image_texture);
        }
        engine.image_texture = texture;
        engine.image_texture_view = view;
        engine.image_nearest_bind_group = nearest_group;
        engine.image_linear_bind_group = linear_group;
        engine.image_width = frame.image_width;
        engine.image_height = frame.image_height;
        engine.image_source_is_external = external;
    }
    engine.image_revision = frame.image_revision;
    ++engine.image_texture_generation;
    return true;
}

void clear_metrics(progpu_native_frame_metrics* metrics) noexcept {
    if (metrics == nullptr ||
        metrics->struct_size < sizeof(progpu_native_frame_metrics)) {
        return;
    }
    const std::uint32_t struct_size = metrics->struct_size;
    *metrics = {};
    metrics->struct_size = struct_size;
}

void clear_metrics(progpu_native_analytic_frame_metrics* metrics) noexcept {
    if (metrics == nullptr ||
        metrics->struct_size < sizeof(progpu_native_analytic_frame_metrics)) {
        return;
    }
    const std::uint32_t struct_size = metrics->struct_size;
    *metrics = {};
    metrics->struct_size = struct_size;
}

void clear_metrics(progpu_native_geometry_frame_metrics* metrics) noexcept {
    if (metrics == nullptr ||
        metrics->struct_size < sizeof(progpu_native_geometry_frame_metrics)) {
        return;
    }
    const std::uint32_t struct_size = metrics->struct_size;
    *metrics = {};
    metrics->struct_size = struct_size;
}

void clear_metrics(progpu_native_path_frame_metrics* metrics) noexcept {
    if (metrics == nullptr ||
        metrics->struct_size < sizeof(progpu_native_path_frame_metrics)) {
        return;
    }
    const std::uint32_t struct_size = metrics->struct_size;
    *metrics = {};
    metrics->struct_size = struct_size;
}

void clear_metrics(progpu_native_glyph_frame_metrics* metrics) noexcept {
    if (metrics == nullptr ||
        metrics->struct_size < sizeof(progpu_native_glyph_frame_metrics)) {
        return;
    }
    const std::uint32_t struct_size = metrics->struct_size;
    *metrics = {};
    metrics->struct_size = struct_size;
}

void clear_metrics(progpu_native_image_frame_metrics* metrics) noexcept {
    if (metrics == nullptr ||
        metrics->struct_size < sizeof(progpu_native_image_frame_metrics)) {
        return;
    }
    const std::uint32_t struct_size = metrics->struct_size;
    *metrics = {};
    metrics->struct_size = struct_size;
}

progpu_native_status create_engine(
    WGPUInstance instance,
    WGPUDevice device,
    WGPUQueue queue,
    WGPUTextureFormat target_format,
    const progpu::native::webgpu::dispatch& webgpu_dispatch,
    progpu_native_engine** engine) {
    try {
        auto result = std::make_unique<progpu_native_engine>();
        result->owner_thread = std::this_thread::get_id();
        result->webgpu_dispatch = webgpu_dispatch;
        result->instance = instance;
        result->device = device;
        result->queue = queue;
        result->target_format = target_format;
        const progpu::native::webgpu::dispatch_scope dispatch_scope(
            &result->webgpu_dispatch);
        if (result->instance != nullptr) {
            progpu::native::webgpu::instance_add_ref(result->instance);
        }
        progpu::native::webgpu::device_add_ref(result->device);
        progpu::native::webgpu::queue_add_ref(result->queue);
        if (!create_pipeline(*result) ||
            !result->ensure_vertex_buffer(initial_vertex_buffer_size)) {
            result->last_error =
                "The shared vector shader or native WebGPU pipeline could not be created.";
            return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        }
        *engine = result.release();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

} // namespace

extern "C" {

uint32_t progpu_native_get_abi_version(void) {
    return PROGPU_NATIVE_ABI_VERSION;
}

uint8_t progpu_native_get_info(progpu_native_engine_info* info) {
    if (info == nullptr || info->struct_size < sizeof(progpu_native_engine_info)) {
        return 0U;
    }
    *info = {};
    info->struct_size = sizeof(progpu_native_engine_info);
    info->abi_version = PROGPU_NATIVE_ABI_VERSION;
#if defined(PROGPU_NATIVE_DAWN_ABI)
    info->backend_abi = PROGPU_NATIVE_BACKEND_ABI_DAWN_WEBSCENE_2026_07;
#else
    info->backend_abi = PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05;
#endif
    info->capabilities =
        PROGPU_NATIVE_CAPABILITY_SOLID_RECT_BATCH |
        PROGPU_NATIVE_CAPABILITY_SHARED_VECTOR_SHADER |
        PROGPU_NATIVE_CAPABILITY_EXTERNAL_TARGET |
        PROGPU_NATIVE_CAPABILITY_INDEXED_ANALYTIC_BATCH |
        PROGPU_NATIVE_CAPABILITY_AFFINE_2D |
        PROGPU_NATIVE_CAPABILITY_INDEXED_GEOMETRY_BATCH |
        PROGPU_NATIVE_CAPABILITY_DEVICE_STROKES |
        PROGPU_NATIVE_CAPABILITY_BEZIER_STROKES |
        PROGPU_NATIVE_CAPABILITY_STROKE_CAPS |
        PROGPU_NATIVE_CAPABILITY_CONNECTED_STROKES |
        PROGPU_NATIVE_CAPABILITY_SPLINE_STROKES |
        PROGPU_NATIVE_CAPABILITY_DASHED_STROKES |
        PROGPU_NATIVE_CAPABILITY_RETAINED_GEOMETRY_REPLAY |
        PROGPU_NATIVE_CAPABILITY_PATH_FILL_ATLAS |
        PROGPU_NATIVE_CAPABILITY_POSITIONED_GLYPH_ATLAS |
        PROGPU_NATIVE_CAPABILITY_RESIZABLE_ATLASES |
        PROGPU_NATIVE_CAPABILITY_RETAINED_RGBA_IMAGE |
        PROGPU_NATIVE_CAPABILITY_EXTERNAL_RGBA_VIEW |
        PROGPU_NATIVE_CAPABILITY_EXTERNAL_IMAGE_MASK |
        PROGPU_NATIVE_CAPABILITY_EXPLICIT_QUEUE_TIMELINE |
        PROGPU_NATIVE_CAPABILITY_FRAME_DRAW_STATE;
#if defined(PROGPU_NATIVE_DAWN_ABI)
    constexpr char name[] = "ProGPU C++ core renderer / Dawn provider";
#else
    constexpr char name[] = "ProGPU C++ core renderer / wgpu-native";
#endif
    std::memcpy(info->name, name, sizeof(name));
    return 1U;
}

progpu_native_status progpu_native_engine_create(
    const progpu_native_engine_options* options,
    progpu_native_engine** engine) {
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *engine = nullptr;
#if defined(PROGPU_NATIVE_DAWN_ABI)
    (void)options;
    return PROGPU_NATIVE_STATUS_UNSUPPORTED;
#else
    if (options == nullptr ||
        options->struct_size < sizeof(progpu_native_engine_options) ||
        options->abi_version != PROGPU_NATIVE_ABI_VERSION ||
        options->backend_abi !=
            PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05 ||
        options->device == 0U || options->queue == 0U ||
        texture_format(options->target_format) == WGPUTextureFormat_Undefined) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    const progpu::native::webgpu::dispatch webgpu_dispatch{};
    return create_engine(
        nullptr,
        reinterpret_cast<WGPUDevice>(options->device),
        reinterpret_cast<WGPUQueue>(options->queue),
        texture_format(options->target_format),
        webgpu_dispatch,
        engine);
#endif
}

#if defined(PROGPU_NATIVE_DAWN_ABI)
static_assert(sizeof(progpu_native_dawn_engine_options) == 72U);

uint32_t progpu_native_dawn_get_adapter_abi_version(void) {
    return PROGPU_NATIVE_DAWN_ADAPTER_ABI_VERSION;
}

progpu_native_status progpu_native_dawn_engine_create(
    const progpu_native_dawn_engine_options* options,
    progpu_native_engine** engine) {
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *engine = nullptr;
    if (options == nullptr ||
        options->struct_size < sizeof(progpu_native_dawn_engine_options) ||
        options->native_abi_version != PROGPU_NATIVE_ABI_VERSION ||
        options->adapter_abi_version !=
            PROGPU_NATIVE_DAWN_ADAPTER_ABI_VERSION ||
        options->provider_abi_version !=
            PROGPU_NATIVE_DAWN_REQUIRED_PROVIDER_ABI_VERSION ||
        options->reserved != 0U || options->flags != 0U ||
        options->resolver_context == nullptr ||
        options->resolve_proc == nullptr ||
        options->instance == 0U || options->device == 0U ||
        options->queue == 0U ||
        texture_format(options->target_format) ==
            WGPUTextureFormat_Undefined) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }

    progpu::native::webgpu::dispatch webgpu_dispatch{};
    if (!webgpu_dispatch.load(
            options->resolver_context,
            options->resolve_proc)) {
        return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    }
    return create_engine(
        reinterpret_cast<WGPUInstance>(options->instance),
        reinterpret_cast<WGPUDevice>(options->device),
        reinterpret_cast<WGPUQueue>(options->queue),
        texture_format(options->target_format),
        webgpu_dispatch,
        engine);
}
#endif

void progpu_native_engine_destroy(progpu_native_engine* engine) {
    delete engine;
}

progpu_native_status progpu_native_engine_render(
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
    engine->path_gpu_cache_valid = false;
    engine->geometry_gpu_cache_valid = false;
    if (frame->rect_count >
            std::numeric_limits<std::size_t>::max() / 6U ||
        frame->rect_count >
            std::numeric_limits<std::uint32_t>::max() / 6U) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The rectangle batch is too large.");
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
    color_attachment.view = reinterpret_cast<WGPUTextureView>(frame->target_view);
    color_attachment.loadOp = WGPULoadOp_Clear;
    color_attachment.storeOp = WGPUStoreOp_Store;
    color_attachment.clearValue = {
        frame->clear_color.r,
        frame->clear_color.g,
        frame->clear_color.b,
        frame->clear_color.a
    };
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
        draw_state.has_drawable_clip) {
        apply_scissor(pass, draw_state);
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
    engine->last_error.clear();

    if (metrics != nullptr &&
        metrics->struct_size >= sizeof(progpu_native_frame_metrics)) {
        metrics->draw_call_count = engine->vertices.empty() ||
            draw_state.opacity == 0.0F || !draw_state.has_drawable_clip
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

progpu_native_status progpu_native_engine_render_analytic(
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
    engine->path_gpu_cache_valid = false;
    engine->geometry_gpu_cache_valid = false;
    if (frame->primitive_count >
            std::numeric_limits<std::uint32_t>::max() / 6U ||
        frame->primitive_count >
            std::numeric_limits<std::size_t>::max() / 6U) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The analytic primitive batch is too large.");
    }

    try {
        engine->vertices.clear();
        engine->indices.clear();
        engine->vertices.reserve(frame->primitive_count * 4U);
        engine->indices.reserve(frame->primitive_count * 6U);
        for (std::size_t index = 0; index < frame->primitive_count; ++index) {
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

    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic frame encoder");
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
        engine->device,
        &encoder_descriptor);
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native analytic command encoder could not be created.");
    }

    WGPURenderPassColorAttachment color_attachment{};
    progpu::native::webgpu::initialize_color_attachment(color_attachment);
    color_attachment.view = reinterpret_cast<WGPUTextureView>(frame->target_view);
    color_attachment.loadOp = WGPULoadOp_Clear;
    color_attachment.storeOp = WGPUStoreOp_Store;
    color_attachment.clearValue = {
        frame->clear_color.r,
        frame->clear_color.g,
        frame->clear_color.b,
        frame->clear_color.a
    };
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = progpu::native::webgpu::string_view("ProGPU native indexed analytic primitive pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &color_attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        wgpuCommandEncoderRelease(encoder);
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native analytic render pass could not be created.");
    }

    if (!engine->indices.empty() && draw_state.opacity != 0.0F &&
        draw_state.has_drawable_clip) {
        apply_scissor(pass, draw_state);
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
    engine->last_error.clear();

    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_analytic_frame_metrics)) {
        metrics->draw_call_count = engine->indices.empty() ||
            draw_state.opacity == 0.0F || !draw_state.has_drawable_clip
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

progpu_native_status progpu_native_engine_render_geometry(
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
    color_attachment.view = reinterpret_cast<WGPUTextureView>(frame->target_view);
    color_attachment.loadOp = WGPULoadOp_Clear;
    color_attachment.storeOp = WGPUStoreOp_Store;
    color_attachment.clearValue = {
        frame->clear_color.r,
        frame->clear_color.g,
        frame->clear_color.b,
        frame->clear_color.a
    };
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
        draw_state.has_drawable_clip) {
        apply_scissor(pass, draw_state);
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
    engine->last_error.clear();

    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_geometry_frame_metrics)) {
        metrics->draw_call_count = engine->indices.empty() ||
            draw_state.opacity == 0.0F || !draw_state.has_drawable_clip
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

progpu_native_status progpu_native_engine_render_paths(
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
    if (frame->path_count > (1U << 20U) ||
        frame->segment_count > (1U << 24U) ||
        frame->path_count >
            std::numeric_limits<std::uint32_t>::max() / 4U) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The path batch exceeds the native safety bound.");
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
            engine->path_brush_bytes.resize(
                (frame->path_count + 1U) * gpu_brush_size);

            set_brush_opacity(
                engine->path_brush_bytes,
                draw_state.opacity);

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
                    vertex.brush_index = static_cast<float>(index + 1U);
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
                std::memcpy(
                    engine->path_brush_bytes.data() +
                        (index + 1U) * gpu_brush_size + 64U,
                    &path.color,
                    sizeof(path.color));

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
                engine->path_payload_hash = append_fnv1a64(
                    engine->path_payload_hash,
                    engine->path_brush_bytes.data(),
                    engine->path_brush_bytes.size());
                engine->path_cache_valid = true;
            }
        } catch (const std::bad_alloc&) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native path batch could not be allocated.");
        }
    }

    const bool opacity_changed = compiled_payload_hit &&
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
    const bool upload_brush_payload = upload_draw_payload || opacity_changed;
    bool uploaded_uniforms = false;
    if (vertex_bytes != 0U &&
        (!engine->ensure_vertex_buffer(vertex_bytes) ||
         !engine->ensure_index_buffer(index_bytes) ||
         !ensure_analytic_brush_buffer(*engine, brush_bytes))) {
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
                engine->vertex_buffer,
                0U,
                engine->path_vertices.data(),
                vertex_bytes);
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->index_buffer,
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

    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained path frame encoder");
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
        engine->device,
        &encoder_descriptor);
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
            wgpuCommandEncoderRelease(encoder);
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

    WGPURenderPassColorAttachment color_attachment{};
    progpu::native::webgpu::initialize_color_attachment(color_attachment);
    color_attachment.view = reinterpret_cast<WGPUTextureView>(frame->target_view);
    color_attachment.loadOp = WGPULoadOp_Clear;
    color_attachment.storeOp = WGPUStoreOp_Store;
    color_attachment.clearValue = {
        frame->clear_color.r,
        frame->clear_color.g,
        frame->clear_color.b,
        frame->clear_color.a
    };
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained path pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &color_attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        wgpuCommandEncoderRelease(encoder);
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native path render pass could not be created.");
    }
    if (!engine->path_indices.empty() && draw_state.opacity != 0.0F &&
        draw_state.has_drawable_clip) {
        apply_scissor(pass, draw_state);
        wgpuRenderPassEncoderSetPipeline(pass, engine->analytic_pipeline);
        wgpuRenderPassEncoderSetBindGroup(
            pass, 0U, engine->analytic_uniform_bind_group, 0U, nullptr);
        wgpuRenderPassEncoderSetBindGroup(
            pass, 1U, engine->path_atlas_bind_group, 0U, nullptr);
        wgpuRenderPassEncoderSetVertexBuffer(
            pass, 0U, engine->vertex_buffer, 0U, vertex_bytes);
        wgpuRenderPassEncoderSetIndexBuffer(
            pass, engine->index_buffer, WGPUIndexFormat_Uint32, 0U, index_bytes);
        wgpuRenderPassEncoderDrawIndexed(
            pass,
            static_cast<std::uint32_t>(engine->path_indices.size()),
            1U,
            0U,
            0,
            0U);
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);

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
        metrics->draw_call_count = engine->path_indices.empty() ||
            draw_state.opacity == 0.0F || !draw_state.has_drawable_clip
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

progpu_native_status progpu_native_engine_render_glyphs(
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
    if (frame->outline_count > (1U << 20U) ||
        frame->segment_count > (1U << 24U) ||
        frame->glyph_count > (1U << 24U)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The positioned glyph batch exceeds the native safety bound.");
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

    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view("ProGPU native positioned glyph frame encoder");
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
        engine->device,
        &encoder_descriptor);
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
            wgpuCommandEncoderRelease(encoder);
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

    WGPURenderPassColorAttachment color_attachment{};
    progpu::native::webgpu::initialize_color_attachment(color_attachment);
    color_attachment.view = reinterpret_cast<WGPUTextureView>(
        frame->target_view);
    color_attachment.loadOp = WGPULoadOp_Clear;
    color_attachment.storeOp = WGPUStoreOp_Store;
    color_attachment.clearValue = {
        frame->clear_color.r,
        frame->clear_color.g,
        frame->clear_color.b,
        frame->clear_color.a
    };
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = progpu::native::webgpu::string_view("ProGPU native positioned glyph pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &color_attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        wgpuCommandEncoderRelease(encoder);
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native positioned glyph render pass could not be created.");
    }
    if (!engine->glyph_instances.empty() && draw_state.opacity != 0.0F &&
        draw_state.has_drawable_clip) {
        apply_scissor(pass, draw_state);
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
            static_cast<std::uint32_t>(engine->glyph_instances.size()),
            0U,
            0U);
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
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
        metrics->draw_call_count = engine->glyph_instances.empty() ||
            draw_state.opacity == 0.0F || !draw_state.has_drawable_clip
            ? 0U
            : 1U;
        metrics->glyph_count = static_cast<std::uint32_t>(
            engine->glyph_instances.size());
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

progpu_native_status progpu_native_engine_render_image(
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

    const bool created_resources = engine->image_pipeline == nullptr;
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

    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained RGBA image encoder");
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
        engine->device,
        &encoder_descriptor);
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained RGBA image command encoder could not be created.");
    }
    WGPURenderPassColorAttachment attachment{};
    progpu::native::webgpu::initialize_color_attachment(attachment);
    attachment.view = reinterpret_cast<WGPUTextureView>(frame->target_view);
    attachment.loadOp = WGPULoadOp_Clear;
    attachment.storeOp = WGPUStoreOp_Store;
    attachment.clearValue = {
        frame->clear_color.r,
        frame->clear_color.g,
        frame->clear_color.b,
        frame->clear_color.a
    };
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained RGBA image pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        wgpuCommandEncoderRelease(encoder);
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained RGBA image render pass could not be created.");
    }
    if (frame->opacity != 0.0F && draw_state.opacity != 0.0F &&
        draw_state.has_drawable_clip) {
        apply_scissor(pass, draw_state);
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
            draw_state.opacity == 0.0F || !draw_state.has_drawable_clip
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

progpu_native_status progpu_native_engine_get_last_submission(
    progpu_native_engine* engine,
    std::uint64_t* submission_index) {
    if (engine == nullptr || submission_index == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer submission timeline must be queried from its owner thread.");
    }
    *submission_index = engine->last_submission_index;
    engine->last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_engine_poll_submission(
    progpu_native_engine* engine,
    std::uint64_t submission_index,
    std::uint8_t wait,
    std::uint8_t* complete) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    if (engine == nullptr || complete == nullptr || wait > 1U ||
        submission_index == 0U ||
        submission_index > engine->last_submission_index) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The native renderer submission token is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer submission timeline must be polled from its owner thread.");
    }
    *complete = progpu::native::webgpu::poll_submission(
        engine->instance,
        engine->device,
        engine->queue,
        submission_index,
        wait != 0U)
        ? 1U
        : 0U;
    engine->last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

size_t progpu_native_engine_get_last_error(
    const progpu_native_engine* engine,
    char* destination,
    size_t destination_size) {
    if (engine == nullptr) {
        return 0U;
    }
    const std::size_t required = engine->last_error.size() + 1U;
    if (destination != nullptr && destination_size != 0U) {
        const std::size_t copy_size = std::min(
            engine->last_error.size(),
            destination_size - 1U);
        std::memcpy(destination, engine->last_error.data(), copy_size);
        destination[copy_size] = '\0';
    }
    return required;
}

} // extern "C"
