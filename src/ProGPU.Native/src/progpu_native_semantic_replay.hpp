#pragma once

// Internal semantic replay records are compiled only after the selected
// WebGPU C header has declared the WGPU handle types.
#include "progpu_native.h"
#include "progpu_native_gpu_records.hpp"
#include "progpu_native_semantic_brush.hpp"
#include "progpu_native_semantic_effect_cache.hpp"

#include <array>
#include <cstdint>
#include <vector>

struct semantic_analytic_draw {
    std::uint64_t vertex_offset_bytes = 0U;
    std::uint64_t index_offset_bytes = 0U;
    std::uint32_t vertex_count = 0U;
    std::uint32_t index_count = 0U;
};

struct semantic_analytic_page {
    WGPUBuffer vertex_buffer = nullptr;
    WGPUBuffer index_buffer = nullptr;
    std::uint64_t vertex_buffer_size = 0U;
    std::uint64_t index_buffer_size = 0U;
    std::uint64_t vertex_bytes = 0U;
    std::uint64_t index_bytes = 0U;
    std::uint64_t scene_hash = 0U;
    float dpi_scale = 0.0F;
    std::uint32_t target_width = 0U;
    std::uint32_t target_height = 0U;
    bool cache_valid = false;
    std::vector<semantic_analytic_draw> draws;
};

struct semantic_path_draw {
    std::uint32_t first_index = 0U;
    std::uint32_t index_count = 0U;
};

struct semantic_path_page {
    std::uint64_t scene_hash = 0U;
    float dpi_scale = 0.0F;
    std::uint32_t target_width = 0U;
    std::uint32_t target_height = 0U;
    bool cache_valid = false;
    std::vector<progpu_native_scene_path_fill> paths;
    std::vector<progpu_native_path_segment> segments;
    std::vector<std::uint32_t> brush_indices;
    std::vector<semantic_path_draw> draws;
};

struct semantic_glyph_draw {
    std::uint32_t first_instance = 0U;
    std::uint32_t instance_count = 0U;
};

struct semantic_color_glyph_raster {
    std::uint32_t atlas_x = 0U;
    std::uint32_t atlas_y = 0U;
};

struct semantic_glyph_page {
    std::uint64_t scene_hash = 0U;
    float dpi_scale = 0.0F;
    std::uint32_t target_width = 0U;
    std::uint32_t target_height = 0U;
    bool cache_valid = false;
    // Keep the pointer-free uint64 scene ABI in retained storage. Native
    // 64-bit execution reinterprets it after layout assertions; wasm32
    // performs one checked narrowing translation at execution time.
    std::vector<progpu_native_scene_glyph_outline> outlines;
    std::vector<progpu_native_path_segment> segments;
    std::vector<progpu_native_positioned_glyph> glyphs;
    std::vector<std::uint32_t> style_indices;
    std::vector<progpu_native_scene_color_glyph_bitmap> color_bitmaps;
    std::vector<std::byte> color_pixels;
    std::vector<std::uint32_t> color_bitmap_indices;
    std::vector<semantic_color_glyph_raster> color_rasters;
    std::vector<semantic_glyph_draw> draws;
};

struct semantic_image_draw {
    WGPUTexture texture = nullptr;
    WGPUTextureView view = nullptr;
    WGPUBindGroup nearest_bind_group = nullptr;
    WGPUBindGroup linear_bind_group = nullptr;
    std::uint32_t first_vertex = 0U;
    std::uint32_t sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
};

struct semantic_image_page {
    WGPUBuffer vertex_buffer = nullptr;
    std::uint64_t vertex_bytes = 0U;
    std::uint64_t scene_hash = 0U;
    float dpi_scale = 0.0F;
    std::uint32_t target_width = 0U;
    std::uint32_t target_height = 0U;
    bool cache_valid = false;
    std::vector<semantic_image_draw> draws;
};

enum class semantic_replay_kind : std::uint8_t {
    bundle,
    push_layer,
    pop_layer
};

struct semantic_effect_dispatch {
    std::uint32_t kind = PROGPU_NATIVE_GROUP_EFFECT_NONE;
    std::int32_t source_texture = -1;
    std::uint32_t horizontal_texture = 0U;
    std::uint32_t vertical_texture = 0U;
    std::uint32_t output_texture = 0U;
    std::uint32_t horizontal_uniform_offset = 0U;
    std::uint32_t vertical_uniform_offset = 0U;
    std::uint32_t drop_shadow_uniform_offset = 0U;
};

struct semantic_render_bundle_span {
    semantic_replay_kind kind = semantic_replay_kind::bundle;
    WGPURenderBundle bundle = nullptr;
    std::uint32_t clip_x = 0U;
    std::uint32_t clip_y = 0U;
    std::uint32_t clip_width = 0U;
    std::uint32_t clip_height = 0U;
    std::uint32_t target_layer = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t source_layer = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t parent_layer = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t first_composite_vertex = 0U;
    std::uint32_t first_resolve_vertex = 0U;
    std::uint32_t first_copy_vertex = 0U;
    std::uint32_t first_backdrop_resolve_vertex = 0U;
    std::uint32_t backdrop_source_x = 0U;
    std::uint32_t backdrop_source_y = 0U;
    std::uint32_t blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
    std::uint32_t first_effect_dispatch = 0U;
    std::uint32_t effect_count = 0U;
    std::uint32_t final_effect_texture = 0U;
    std::uint64_t operation_id = 0U;
    WGPUBuffer mask_uniform_buffer = nullptr;
    WGPUBindGroup mask_bind_group = nullptr;
    WGPUBindGroup advanced_blend_bind_group = nullptr;
    std::uint32_t advanced_uniform_offset = 0U;
    std::uint32_t target_width = 0U;
    std::uint32_t target_height = 0U;
    std::uint32_t source_width = 0U;
    std::uint32_t source_height = 0U;
    bool backdrop = false;
};

struct semantic_layer_slot {
    WGPUTexture texture = nullptr;
    WGPUTextureView view = nullptr;
    WGPUBindGroup bind_group = nullptr;
    WGPUBuffer uniform_buffer = nullptr;
    WGPUBindGroup analytic_uniform_bind_group = nullptr;
    WGPUBindGroup text_uniform_bind_group = nullptr;
    WGPUBindGroup image_uniform_bind_group = nullptr;
    WGPUBuffer bound_analytic_brush_buffer = nullptr;
    WGPUBuffer bound_analytic_gradient_buffer = nullptr;
    WGPUBuffer bound_text_style_buffer = nullptr;
    progpu::native::gpu_uniforms cached_uniforms{};
    bool uniform_cache_valid = false;
    std::uint32_t width = 0U;
    std::uint32_t height = 0U;
    std::uint32_t generation = 0U;
    std::array<WGPUTexture, 3U> effect_textures{};
    std::array<WGPUTextureView, 3U> effect_views{};
    std::array<WGPUBindGroup, 3U> effect_output_bind_groups{};
    std::array<WGPUBindGroup, 12U> effect_blur_bind_groups{};
    std::array<WGPUBindGroup, 36U> effect_drop_shadow_bind_groups{};
    std::uint32_t effect_width = 0U;
    std::uint32_t effect_height = 0U;
    std::uint32_t effect_generation = 0U;
    progpu::native::effects::semantic_output_cache effect_output_cache{};
};
