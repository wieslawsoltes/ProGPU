#pragma once

// Internal semantic replay records are compiled only after the selected
// WebGPU C header has declared the WGPU handle types.
#include "progpu_native.h"
#include "progpu_native_gpu_records.hpp"
#include "progpu_native_semantic_brush.hpp"
#include "progpu_native_semantic_draw_merge.hpp"
#include "progpu_native_semantic_effect_cache.hpp"

#include <array>
#include <cstdint>
#include <vector>

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

struct semantic_path_page {
    std::uint64_t scene_hash = 0U;
    float dpi_scale = 0.0F;
    std::uint32_t target_width = 0U;
    std::uint32_t target_height = 0U;
    bool cache_valid = false;
    std::vector<progpu_native_scene_path_fill> paths;
    std::vector<progpu_native_path_segment> segments;
    std::vector<progpu_native_scene_path_boolean_node> boolean_nodes;
    std::vector<std::uint32_t> brush_indices;
    std::vector<semantic_path_draw> draws;
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
    WGPUBindGroup texture_bind_group = nullptr;
    WGPUBuffer color_matrix_buffer = nullptr;
    WGPUBindGroup color_matrix_bind_group = nullptr;
    WGPUBuffer effect_uniform_buffer = nullptr;
    WGPUBuffer effect_mask_uniform_buffer = nullptr;
    WGPUBindGroup effect_uniform_bind_group = nullptr;
    WGPUBindGroup effect_texture_bind_group = nullptr;
    WGPUBindGroup effect_dummy_mask_bind_group = nullptr;
    WGPUTexture blur_intermediate_texture = nullptr;
    WGPUTextureView blur_intermediate_view = nullptr;
    WGPUTexture blur_output_texture = nullptr;
    WGPUTextureView blur_output_view = nullptr;
    WGPUBuffer blur_horizontal_uniform_buffer = nullptr;
    WGPUBuffer blur_vertical_uniform_buffer = nullptr;
    WGPUBindGroup blur_horizontal_bind_group = nullptr;
    WGPUBindGroup blur_vertical_bind_group = nullptr;
    std::uint32_t first_vertex = 0U;
    std::uint32_t vertex_count = 4U;
    std::uint32_t sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
    bool has_color_matrix = false;
    bool has_effect = false;
    bool has_effect_mask = false;
    bool has_live_blur = false;
    bool blur_unfilterable_source = false;
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

struct semantic_external_image_binding {
    std::uint64_t resource_id = 0U;
    std::uint64_t generation = 0U;
    std::uint32_t role = PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE_PRIMARY;
    WGPUTextureView view = nullptr;
    std::uint32_t width = 0U;
    std::uint32_t height = 0U;
};

namespace progpu::native::three_d {

struct alignas(16) camera_record {
    progpu_native_matrix_4x4 projection{};
    progpu_native_matrix_4x4 view{};
    float camera_position[4]{};
    float viewport[4]{};
};

struct alignas(16) line_record {
    float start[4]{};
    float end[4]{};
    progpu_native_color color{};
    float thickness = 0.0F;
    float opacity = 0.0F;
    std::uint32_t camera_index = 0U;
    std::uint32_t flags = 0U;
    progpu_native_matrix_4x4 transform{};
};

struct alignas(16) mesh_record {
    std::uint32_t flags = 0U;
    std::uint32_t topology = 0U;
    std::uint32_t render_mode = 0U;
    std::uint32_t camera_index = 0U;
    std::uint32_t vertex_offset = 0U;
    std::uint32_t vertex_count = 0U;
    std::uint32_t index_offset = 0U;
    std::uint32_t index_count = 0U;
    progpu_native_matrix_4x4 model_transform{};
    progpu_native_matrix_4x4 normal_transform{};
    progpu_native_color color{};
    progpu_native_float_4 light_direction{};
    progpu_native_float_4 ambient_color{};
    progpu_native_float_4 specular_color{};
    progpu_native_float_4 material_ambient{};
    float opacity = 0.0F;
    std::uint32_t shading_mode = 0U;
    std::uint32_t reserved0 = 0U;
    std::uint32_t reserved1 = 0U;
};

static_assert(sizeof(camera_record) == 160U);
static_assert(sizeof(line_record) == 128U);
static_assert(sizeof(mesh_record) == 256U);
static_assert(sizeof(progpu_native_scene_mesh_3d_vertex) == 48U);

} // namespace progpu::native::three_d

struct semantic_3d_draw {
    std::uint32_t kind = 0U;
    std::uint32_t first_record = 0U;
    std::uint32_t record_count = 0U;
};

struct semantic_3d_page {
    WGPUBuffer camera_buffer = nullptr;
    WGPUBuffer line_buffer = nullptr;
    WGPUBuffer mesh_buffer = nullptr;
    WGPUBuffer vertex_buffer = nullptr;
    WGPUBuffer index_buffer = nullptr;
    WGPUBindGroup bind_group = nullptr;
    std::uint64_t scene_hash = 0U;
    float dpi_scale = 0.0F;
    std::uint32_t target_width = 0U;
    std::uint32_t target_height = 0U;
    bool cache_valid = false;
    std::vector<semantic_3d_draw> draws;
    std::vector<std::uint32_t> mesh_topologies;
    std::vector<std::uint32_t> mesh_index_counts;
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
    WGPUBuffer mask_chain_uniform_buffer = nullptr;
    WGPUTexture mask_texture = nullptr;
    WGPUTextureView mask_texture_view = nullptr;
    WGPUBindGroup mask_bind_group = nullptr;
    WGPUBindGroup mask_chain_bind_group = nullptr;
    WGPUBindGroup advanced_blend_bind_group = nullptr;
    std::uint32_t advanced_uniform_offset = 0U;
    std::uint32_t target_width = 0U;
    std::uint32_t target_height = 0U;
    std::uint32_t source_width = 0U;
    std::uint32_t source_height = 0U;
    std::uint32_t draw_call_count = 0U;
    std::uint32_t mask_uniform_upload_bytes = 0U;
    std::uint32_t mask_source_x = 0U;
    std::uint32_t mask_source_y = 0U;
    std::uint64_t effect_cache_operation_id = 0U;
    std::uint64_t cache_identity = 0U;
    std::uint64_t cache_content_revision = 0U;
    bool backdrop = false;
    bool can_skip_content_on_effect_cache = false;
    bool cache_content = false;
    bool mask_uses_alpha_channel = false;
};

struct semantic_layer_slot {
    WGPUTexture texture = nullptr;
    WGPUTextureView view = nullptr;
    WGPUTexture depth_texture = nullptr;
    WGPUTextureView depth_view = nullptr;
    std::uint32_t depth_width = 0U;
    std::uint32_t depth_height = 0U;
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
