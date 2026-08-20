#pragma once

// Internal engine ownership is compiled only after the selected WebGPU C
// header and ProGPU dispatch compatibility layer have declared WGPU handles.
#include "progpu_native.h"
#include "progpu_native_geometry_base.hpp"
#include "progpu_native_geometry_spline.hpp"
#include "progpu_native_gpu_records.hpp"
#include "progpu_native_semantic_effect_cache.hpp"
#include "progpu_native_semantic_identity.hpp"
#include "progpu_native_semantic_text_style.hpp"
#include "progpu_webgpu_compat.hpp"
#include "progpu_native_semantic_replay.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string>
#include <thread>
#include <unordered_map>
#include <utility>
#include <vector>

using progpu::native::gpu_drop_shadow_params;
using progpu::native::gpu_advanced_blend_sampling_uniforms;
using progpu::native::gpu_gaussian_blur_params;
using progpu::native::gpu_glyph_instance;
using progpu::native::gpu_group_blend_uniforms;
using progpu::native::gpu_mask_sampling_uniforms;
using progpu::native::gpu_mask_chain_uniforms;
using progpu::native::gpu_uniforms;
using progpu::native::initial_index_buffer_size;
using progpu::native::initial_vertex_buffer_size;
using progpu::native::native_glyph_raster;
using progpu::native::native_initial_atlas_size;
using progpu::native::native_path_raster;
using progpu::native::vector_vertex;

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
    WGPURenderPipeline analytic_masked_pipeline = nullptr;
    WGPURenderPipeline analytic_mask_chain_pipeline = nullptr;
    WGPURenderPipeline analytic_brush_mask_pipeline = nullptr;
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
    std::uint64_t analytic_gradient_buffer_size = 0U;
    std::uint64_t analytic_material_owner_hash = 0U;
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
    WGPURenderPipeline text_masked_pipeline = nullptr;
    WGPURenderPipeline text_mask_chain_pipeline = nullptr;
    WGPUBindGroupLayout text_uniform_layout = nullptr;
    WGPUBindGroupLayout text_atlas_layout = nullptr;
    WGPUBuffer text_style_buffer = nullptr;
    std::uint64_t text_style_buffer_size = 0U;
    std::uint64_t text_style_owner_hash = 0U;
    WGPUBindGroup text_uniform_bind_group = nullptr;
    WGPUSampler glyph_atlas_sampler = nullptr;
    WGPUTexture glyph_atlas_texture = nullptr;
    WGPUTextureView glyph_atlas_texture_view = nullptr;
    WGPUTexture color_glyph_atlas_texture = nullptr;
    WGPUTextureView color_glyph_atlas_texture_view = nullptr;
    WGPUBindGroup text_atlas_bind_group = nullptr;
    std::uint32_t glyph_atlas_size = native_initial_atlas_size;
    std::uint32_t color_glyph_atlas_size = 0U;
    std::uint64_t color_glyph_atlas_owner_hash = 0U;
    std::uint32_t glyph_atlas_generation = 0U;
    std::uint32_t glyph_atlas_growth_count = 0U;
    WGPUBuffer text_vertex_buffer = nullptr;
    std::uint64_t text_vertex_buffer_size = 0U;
    WGPUBuffer vertex_buffer = nullptr;
    WGPUBuffer index_buffer = nullptr;
    std::uint64_t vertex_buffer_size = 0;
    std::uint64_t index_buffer_size = 0;
    WGPUBuffer path_vertex_buffer = nullptr;
    WGPUBuffer path_index_buffer = nullptr;
    std::uint64_t path_vertex_buffer_size = 0U;
    std::uint64_t path_index_buffer_size = 0U;
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
    bool semantic_path_materials_active = false;
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
    WGPURenderPipeline image_color_matrix_pipeline = nullptr;
    WGPURenderPipeline image_masked_color_matrix_pipeline = nullptr;
    WGPURenderPipeline image_mask_chain_pipeline = nullptr;
    WGPURenderPipeline image_mask_chain_color_matrix_pipeline = nullptr;
    WGPUShaderModule image_effect_shader = nullptr;
    WGPURenderPipeline image_effect_pipeline = nullptr;
    WGPURenderPipeline image_effect_mask_chain_pipeline = nullptr;
    WGPUBindGroupLayout image_uniform_layout = nullptr;
    WGPUBindGroupLayout image_texture_layout = nullptr;
    WGPUBindGroupLayout image_mask_layout = nullptr;
    WGPUBindGroupLayout image_effect_uniform_layout = nullptr;
    WGPUBindGroupLayout image_effect_texture_layout = nullptr;
    WGPUShaderModule semantic_image_blur_shader = nullptr;
    WGPURenderPipeline semantic_image_blur_pipeline = nullptr;
    WGPUBindGroupLayout semantic_image_blur_layout = nullptr;
    WGPUShaderModule semantic_image_blur_unfilterable_shader = nullptr;
    WGPURenderPipeline semantic_image_blur_unfilterable_pipeline = nullptr;
    WGPUBindGroupLayout semantic_image_blur_unfilterable_layout = nullptr;
    WGPUBindGroupLayout semantic_mask_chain_layout = nullptr;
    WGPUBuffer image_uniform_buffer = nullptr;
    WGPUBindGroup image_uniform_bind_group = nullptr;
    WGPUSampler image_nearest_sampler = nullptr;
    WGPUSampler image_linear_sampler = nullptr;
    WGPUSampler image_mipmap_sampler = nullptr;
    std::array<WGPUSampler, 6U> image_filtered_samplers{};
    std::array<WGPUSampler, 15U> image_anisotropic_samplers{};
    WGPUTexture image_texture = nullptr;
    WGPUTextureView image_texture_view = nullptr;
    WGPUBindGroup image_texture_bind_group = nullptr;
    std::uint32_t image_binding_sampling =
        std::numeric_limits<std::uint32_t>::max();
    std::uint32_t image_binding_max_anisotropy = 0U;
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
    std::uint32_t image_compiled_sampling =
        std::numeric_limits<std::uint32_t>::max();
    float image_compiled_cubic_b = 0.0F;
    float image_compiled_cubic_c = 0.5F;
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
    WGPURenderPipeline layer_composite_pipeline = nullptr;
    WGPURenderPipeline layer_mask_pipeline = nullptr;
    std::array<WGPURenderPipeline, PROGPU_NATIVE_BLEND_MODULATE + 1U>
        layer_blend_pipelines{};
    std::array<WGPURenderPipeline, PROGPU_NATIVE_BLEND_MODULATE + 1U>
        layer_mask_blend_pipelines{};
    WGPUBindGroupLayout layer_mask_layout = nullptr;
    WGPUBuffer layer_mask_uniform_buffer = nullptr;
    WGPUTexture layer_mask_dummy_texture = nullptr;
    WGPUTextureView layer_mask_dummy_view = nullptr;
    WGPUBindGroup layer_analytic_mask_bind_group = nullptr;
    WGPUTextureView layer_external_mask_view = nullptr;
    WGPUBindGroup layer_external_mask_nearest_bind_group = nullptr;
    WGPUBindGroup layer_external_mask_linear_bind_group = nullptr;
    std::uint32_t layer_external_mask_width = 0U;
    std::uint32_t layer_external_mask_height = 0U;
    std::uint32_t layer_mask_bind_group_generation = 0U;
    gpu_mask_sampling_uniforms cached_layer_mask_uniforms{};
    bool layer_mask_uniform_cache_valid = false;
    WGPUShaderModule group_blend_shader = nullptr;
    WGPURenderPipeline group_blend_pipeline = nullptr;
    WGPUBindGroupLayout group_blend_layout = nullptr;
    WGPUBuffer group_blend_uniform_buffer = nullptr;
    WGPUTexture group_blend_source_texture = nullptr;
    WGPUTextureView group_blend_source_view = nullptr;
    WGPUBindGroup group_blend_bind_group = nullptr;
    gpu_group_blend_uniforms cached_group_blend_uniforms{};
    std::uint32_t group_blend_source_width = 0U;
    std::uint32_t group_blend_source_height = 0U;
    std::uint32_t group_blend_source_texture_generation = 0U;
    std::uint32_t group_blend_source_allocation_count = 0U;
    std::uint64_t group_blend_source_signature = 0U;
    bool group_blend_uniform_cache_valid = false;
    bool group_blend_source_cache_valid = false;
    WGPUShaderModule clip_compose_shader = nullptr;
    WGPURenderPipeline clip_path_pipeline = nullptr;
    WGPURenderPipeline clip_compose_pipeline = nullptr;
    WGPUBindGroupLayout clip_compose_layout = nullptr;
    WGPUSampler clip_sampler = nullptr;
    WGPUTexture clip_atlas_texture = nullptr;
    WGPUTextureView clip_atlas_view = nullptr;
    WGPUBindGroup clip_path_bind_group = nullptr;
    std::uint32_t clip_atlas_size = native_initial_atlas_size;
    std::uint32_t clip_atlas_generation = 0U;
    WGPUTexture clip_node_texture = nullptr;
    WGPUTextureView clip_node_view = nullptr;
    std::array<WGPUTexture, 2U> clip_accumulation_textures{};
    std::array<WGPUTextureView, 2U> clip_accumulation_views{};
    std::array<WGPUBindGroup, 2U> clip_compose_bind_groups{};
    std::array<WGPUBindGroup, 2U> layer_clip_mask_bind_groups{};
    WGPUBuffer clip_compose_uniform_buffer = nullptr;
    std::uint64_t clip_compose_uniform_buffer_size = 0U;
    WGPUBuffer clip_vertex_buffer = nullptr;
    WGPUBuffer clip_index_buffer = nullptr;
    std::uint64_t clip_vertex_buffer_size = 0U;
    std::uint64_t clip_index_buffer_size = 0U;
    std::uint32_t clip_width = 0U;
    std::uint32_t clip_height = 0U;
    std::uint32_t clip_texture_generation = 0U;
    std::uint32_t clip_cached_revision = 0U;
    float clip_cached_dpi_scale = 0.0F;
    std::uint32_t clip_final_index = 0U;
    bool clip_cache_valid = false;
    WGPUShaderModule effect_blur_horizontal_shader = nullptr;
    WGPUShaderModule effect_blur_vertical_shader = nullptr;
    WGPUComputePipeline effect_blur_horizontal_pipeline = nullptr;
    WGPUComputePipeline effect_blur_vertical_pipeline = nullptr;
    WGPUBindGroupLayout effect_blur_layout = nullptr;
    WGPUBuffer effect_blur_horizontal_uniform_buffer = nullptr;
    WGPUBuffer effect_blur_vertical_uniform_buffer = nullptr;
    std::array<WGPUTexture, 2U> effect_textures{};
    std::array<WGPUTextureView, 2U> effect_texture_views{};
    WGPUBindGroup effect_blur_horizontal_bind_group = nullptr;
    WGPUBindGroup effect_blur_vertical_bind_group = nullptr;
    WGPUBindGroup effect_output_bind_group = nullptr;
    WGPUShaderModule effect_drop_shadow_shader = nullptr;
    WGPUComputePipeline effect_drop_shadow_pipeline = nullptr;
    WGPUBindGroupLayout effect_drop_shadow_layout = nullptr;
    WGPUBuffer effect_drop_shadow_uniform_buffer = nullptr;
    WGPUBindGroup effect_drop_shadow_bind_group = nullptr;
    WGPUBindGroup effect_drop_shadow_output_bind_group = nullptr;
    std::array<WGPUTexture, 3U> effect_chain_textures{};
    std::array<WGPUTextureView, 3U> effect_chain_texture_views{};
    std::array<WGPUBindGroup, 3U> effect_chain_output_bind_groups{};
    std::array<WGPUBuffer, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_blur_horizontal_uniform_buffers{};
    std::array<WGPUBuffer, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_blur_vertical_uniform_buffers{};
    std::array<WGPUBuffer, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_drop_shadow_uniform_buffers{};
    std::array<WGPUBindGroup, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_blur_horizontal_bind_groups{};
    std::array<WGPUBindGroup, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_blur_vertical_bind_groups{};
    std::array<WGPUBindGroup, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_drop_shadow_bind_groups{};
    std::array<gpu_gaussian_blur_params,
        PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        cached_effect_chain_blur_horizontal{};
    std::array<gpu_gaussian_blur_params,
        PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        cached_effect_chain_blur_vertical{};
    std::array<gpu_drop_shadow_params,
        PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        cached_effect_chain_drop_shadow{};
    std::array<bool, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_blur_horizontal_uniform_cache_valid{};
    std::array<bool, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_blur_vertical_uniform_cache_valid{};
    std::array<bool, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_drop_shadow_uniform_cache_valid{};
    std::array<std::uint32_t, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_cached_kinds{};
    std::uint32_t effect_chain_cached_count = 0U;
    std::uint32_t effect_chain_final_texture_index = 0U;
    std::uint32_t effect_chain_width = 0U;
    std::uint32_t effect_chain_height = 0U;
    std::uint32_t effect_chain_texture_generation = 0U;
    std::uint32_t effect_chain_allocation_count = 0U;
    bool effect_chain_bindings_valid = false;
    gpu_gaussian_blur_params cached_effect_blur_horizontal{};
    gpu_gaussian_blur_params cached_effect_blur_vertical{};
    gpu_drop_shadow_params cached_effect_drop_shadow{};
    std::uint32_t effect_width = 0U;
    std::uint32_t effect_height = 0U;
    std::uint32_t effect_texture_generation = 0U;
    std::uint32_t effect_allocation_count = 0U;
    std::uint32_t effect_cached_revision = 0U;
    std::uint32_t effect_cached_content_revision = 0U;
    std::uint32_t effect_cached_kind = PROGPU_NATIVE_GROUP_EFFECT_NONE;
    float effect_cached_dpi_scale = 0.0F;
    bool effect_blur_horizontal_uniform_cache_valid = false;
    bool effect_blur_vertical_uniform_cache_valid = false;
    bool effect_drop_shadow_uniform_cache_valid = false;
    bool effect_cache_valid = false;
    WGPUBuffer layer_uniform_buffer = nullptr;
    WGPUBindGroup layer_uniform_bind_group = nullptr;
    WGPUBuffer layer_vertex_buffer = nullptr;
    WGPUBuffer layer_index_buffer = nullptr;
    WGPUTexture layer_texture = nullptr;
    WGPUTextureView layer_texture_view = nullptr;
    WGPUBindGroup layer_texture_bind_group = nullptr;
    std::array<progpu::native::vector_vertex, 4U> layer_vertices{};
    gpu_uniforms cached_layer_uniforms{};
    bool layer_uniform_cache_valid = false;
    bool layer_vertex_cache_valid = false;
    std::uint32_t layer_width = 0U;
    std::uint32_t layer_height = 0U;
    std::uint32_t layer_texture_generation = 0U;
    std::uint32_t layer_allocation_count = 0U;
    std::uint32_t layer_cached_family = 0U;
    std::uint32_t layer_cached_revision = 0U;
    float layer_cached_dpi_scale = 0.0F;
    float layer_cached_primitive_opacity = 1.0F;
    bool layer_content_cache_valid = false;
    progpu_native_layer_metrics last_layer_metrics{};
    std::vector<std::byte> semantic_scene_snapshot;
    std::uint64_t semantic_scene_id = 0U;
    std::uint64_t semantic_scene_generation = 0U;
    std::uint64_t semantic_scene_hash = 0U;
    progpu::native::semantic::semantic_content_hashes
        semantic_hashes{};
    progpu_native_scene_header semantic_scene_header{};
    progpu_native_scene_metrics semantic_scene_metrics{};
    progpu::native::semantic::semantic_brush_page semantic_brush_cache;
    progpu::native::semantic::semantic_text_style_page
        semantic_text_style_cache;
    semantic_analytic_page semantic_analytic_cache;
    semantic_path_page semantic_path_cache;
    semantic_glyph_page semantic_glyph_cache;
    semantic_image_page semantic_image_cache;
    std::vector<semantic_external_image_binding>
        semantic_external_image_bindings;
    semantic_3d_page semantic_3d_cache;
    WGPUShaderModule semantic_3d_shader = nullptr;
    WGPURenderPipeline semantic_line_3d_pipeline = nullptr;
    WGPURenderPipeline semantic_mesh_3d_pipeline = nullptr;
    WGPURenderPipeline semantic_mesh_strip_3d_pipeline = nullptr;
    WGPUBindGroupLayout semantic_3d_layout = nullptr;
    WGPUPipelineLayout semantic_3d_pipeline_layout = nullptr;
    WGPUShaderModule semantic_hit_test_shader = nullptr;
    WGPUComputePipeline semantic_hit_test_pipeline = nullptr;
    WGPUBindGroupLayout semantic_hit_test_layout = nullptr;
    WGPUPipelineLayout semantic_hit_test_pipeline_layout = nullptr;
    WGPUBindGroup semantic_hit_test_bind_group = nullptr;
    WGPUBuffer semantic_hit_test_query_buffer = nullptr;
    WGPUBuffer semantic_hit_test_node_buffer = nullptr;
    WGPUBuffer semantic_hit_test_primitive_index_buffer = nullptr;
    WGPUBuffer semantic_hit_test_primitive_buffer = nullptr;
    WGPUBuffer semantic_hit_test_result_buffer = nullptr;
    WGPUBuffer semantic_hit_test_readback_buffer = nullptr;
    progpu::native::webgpu::buffer_map_read_state
        semantic_hit_test_map_state{};
    WGPUBuffer semantic_hit_test_path_segment_buffer = nullptr;
    std::uint64_t semantic_hit_test_gpu_hash = 0U;
    std::uint64_t semantic_hit_test_next_token = 0U;
    std::uint64_t semantic_hit_test_pending_token = 0U;
    std::uint64_t semantic_hit_test_pending_bytes = 0U;
    std::uint32_t semantic_hit_test_primitive_count = 0U;
    std::uint32_t semantic_hit_test_node_count = 0U;
    std::uint32_t semantic_hit_test_primitive_index_count = 0U;
    std::uint32_t semantic_hit_test_path_segment_count = 0U;
    std::uint32_t semantic_hit_test_requested_result_count = 0U;
    std::vector<semantic_render_bundle_span> semantic_render_bundle_spans;
    std::vector<semantic_effect_dispatch> semantic_effect_dispatches;
    std::array<semantic_layer_slot,
        PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS> semantic_layer_slots{};
    semantic_layer_slot semantic_root_slot{};
    semantic_layer_slot semantic_advanced_source_slot{};
    semantic_layer_slot semantic_advanced_output_slot{};
    WGPUBuffer semantic_layer_vertex_buffer = nullptr;
    std::uint64_t semantic_layer_vertex_buffer_size = 0U;
    std::uint64_t semantic_layer_vertex_content_hash = 0U;
    std::uint64_t semantic_layer_vertex_content_bytes = 0U;
    WGPUBuffer semantic_effect_uniform_buffer = nullptr;
    std::uint64_t semantic_effect_uniform_buffer_size = 0U;
    std::uint32_t semantic_layer_allocation_count = 0U;
    std::uint32_t semantic_effect_allocation_count = 0U;
    WGPUShaderModule semantic_advanced_blend_shader = nullptr;
    WGPURenderPipeline semantic_advanced_blend_pipeline = nullptr;
    WGPUBindGroupLayout semantic_advanced_blend_layout = nullptr;
    WGPUBuffer semantic_advanced_blend_uniform_buffer = nullptr;
    std::uint64_t semantic_advanced_blend_uniform_buffer_size = 0U;
    bool semantic_destination_sampling_active = false;
    std::uint32_t semantic_root_copy_vertex = 0U;
    bool semantic_render_bundle_valid = false;
    std::uint64_t semantic_render_bundle_scene_hash = 0U;
    float semantic_render_bundle_dpi_scale = 0.0F;
    std::uint32_t semantic_render_bundle_width = 0U;
    std::uint32_t semantic_render_bundle_height = 0U;
    std::uint32_t semantic_render_bundle_draw_call_count = 0U;
    std::uint32_t semantic_render_bundle_family_switch_count = 0U;
    WGPUCommandEncoder semantic_encoder = nullptr;
    bool semantic_load_target = false;
    bool semantic_prepare_only = false;
    bool semantic_path_draw_active = false;
    std::uint32_t semantic_path_first_index = 0U;
    std::uint32_t semantic_path_index_count = 0U;
    std::uint64_t semantic_path_gpu_scene_hash = 0U;
    bool semantic_glyph_draw_active = false;
    std::uint32_t semantic_glyph_first_instance = 0U;
    std::uint32_t semantic_glyph_instance_count = 0U;
    std::uint64_t semantic_glyph_gpu_scene_hash = 0U;
    std::string last_error;
    std::uint64_t submission_count = 0;
    std::uint64_t last_submission_index = 0U;
    std::uint64_t device_loss_generation = 0U;
    bool device_lost = false;

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

    void release_clip_resources() noexcept {
        for (auto& bind_group : layer_clip_mask_bind_groups) {
            if (bind_group != nullptr) {
                wgpuBindGroupRelease(bind_group);
                bind_group = nullptr;
            }
        }
        for (auto& bind_group : clip_compose_bind_groups) {
            if (bind_group != nullptr) {
                wgpuBindGroupRelease(bind_group);
                bind_group = nullptr;
            }
        }
        if (clip_path_bind_group != nullptr) {
            wgpuBindGroupRelease(clip_path_bind_group);
            clip_path_bind_group = nullptr;
        }
        if (clip_index_buffer != nullptr) {
            wgpuBufferDestroy(clip_index_buffer);
            wgpuBufferRelease(clip_index_buffer);
            clip_index_buffer = nullptr;
        }
        if (clip_vertex_buffer != nullptr) {
            wgpuBufferDestroy(clip_vertex_buffer);
            wgpuBufferRelease(clip_vertex_buffer);
            clip_vertex_buffer = nullptr;
        }
        if (clip_compose_uniform_buffer != nullptr) {
            wgpuBufferDestroy(clip_compose_uniform_buffer);
            wgpuBufferRelease(clip_compose_uniform_buffer);
            clip_compose_uniform_buffer = nullptr;
        }
        for (auto& view : clip_accumulation_views) {
            if (view != nullptr) {
                wgpuTextureViewRelease(view);
                view = nullptr;
            }
        }
        for (auto& texture : clip_accumulation_textures) {
            if (texture != nullptr) {
                wgpuTextureDestroy(texture);
                wgpuTextureRelease(texture);
                texture = nullptr;
            }
        }
        if (clip_node_view != nullptr) {
            wgpuTextureViewRelease(clip_node_view);
            clip_node_view = nullptr;
        }
        if (clip_node_texture != nullptr) {
            wgpuTextureDestroy(clip_node_texture);
            wgpuTextureRelease(clip_node_texture);
            clip_node_texture = nullptr;
        }
        if (clip_atlas_view != nullptr) {
            wgpuTextureViewRelease(clip_atlas_view);
            clip_atlas_view = nullptr;
        }
        if (clip_atlas_texture != nullptr) {
            wgpuTextureDestroy(clip_atlas_texture);
            wgpuTextureRelease(clip_atlas_texture);
            clip_atlas_texture = nullptr;
        }
        if (clip_sampler != nullptr) {
            wgpuSamplerRelease(clip_sampler);
            clip_sampler = nullptr;
        }
        if (clip_compose_layout != nullptr) {
            wgpuBindGroupLayoutRelease(clip_compose_layout);
            clip_compose_layout = nullptr;
        }
        if (clip_compose_pipeline != nullptr) {
            wgpuRenderPipelineRelease(clip_compose_pipeline);
            clip_compose_pipeline = nullptr;
        }
        if (clip_path_pipeline != nullptr) {
            wgpuRenderPipelineRelease(clip_path_pipeline);
            clip_path_pipeline = nullptr;
        }
        if (clip_compose_shader != nullptr) {
            wgpuShaderModuleRelease(clip_compose_shader);
            clip_compose_shader = nullptr;
        }
    }

    void release_effect_resources() noexcept {
        for (auto& slot : semantic_layer_slots) {
            for (auto& bind_group : slot.effect_drop_shadow_bind_groups) {
                if (bind_group != nullptr) {
                    wgpuBindGroupRelease(bind_group);
                    bind_group = nullptr;
                }
            }
            for (auto& bind_group : slot.effect_blur_bind_groups) {
                if (bind_group != nullptr) {
                    wgpuBindGroupRelease(bind_group);
                    bind_group = nullptr;
                }
            }
        }
        for (auto& bind_group : effect_chain_drop_shadow_bind_groups) {
            if (bind_group != nullptr) {
                wgpuBindGroupRelease(bind_group);
                bind_group = nullptr;
            }
        }
        for (auto& bind_group : effect_chain_blur_vertical_bind_groups) {
            if (bind_group != nullptr) {
                wgpuBindGroupRelease(bind_group);
                bind_group = nullptr;
            }
        }
        for (auto& bind_group : effect_chain_blur_horizontal_bind_groups) {
            if (bind_group != nullptr) {
                wgpuBindGroupRelease(bind_group);
                bind_group = nullptr;
            }
        }
        for (auto& bind_group : effect_chain_output_bind_groups) {
            if (bind_group != nullptr) {
                wgpuBindGroupRelease(bind_group);
                bind_group = nullptr;
            }
        }
        for (auto& view : effect_chain_texture_views) {
            if (view != nullptr) {
                wgpuTextureViewRelease(view);
                view = nullptr;
            }
        }
        for (auto& texture : effect_chain_textures) {
            if (texture != nullptr) {
                wgpuTextureDestroy(texture);
                wgpuTextureRelease(texture);
                texture = nullptr;
            }
        }
        const auto release_buffers = [](auto& buffers) {
            for (auto& buffer : buffers) {
                if (buffer != nullptr) {
                    wgpuBufferDestroy(buffer);
                    wgpuBufferRelease(buffer);
                    buffer = nullptr;
                }
            }
        };
        release_buffers(effect_chain_drop_shadow_uniform_buffers);
        release_buffers(effect_chain_blur_vertical_uniform_buffers);
        release_buffers(effect_chain_blur_horizontal_uniform_buffers);
        if (effect_drop_shadow_output_bind_group != nullptr) {
            wgpuBindGroupRelease(effect_drop_shadow_output_bind_group);
            effect_drop_shadow_output_bind_group = nullptr;
        }
        if (effect_drop_shadow_bind_group != nullptr) {
            wgpuBindGroupRelease(effect_drop_shadow_bind_group);
            effect_drop_shadow_bind_group = nullptr;
        }
        if (effect_output_bind_group != nullptr) {
            wgpuBindGroupRelease(effect_output_bind_group);
            effect_output_bind_group = nullptr;
        }
        if (effect_blur_vertical_bind_group != nullptr) {
            wgpuBindGroupRelease(effect_blur_vertical_bind_group);
            effect_blur_vertical_bind_group = nullptr;
        }
        if (effect_blur_horizontal_bind_group != nullptr) {
            wgpuBindGroupRelease(effect_blur_horizontal_bind_group);
            effect_blur_horizontal_bind_group = nullptr;
        }
        for (auto& view : effect_texture_views) {
            if (view != nullptr) {
                wgpuTextureViewRelease(view);
                view = nullptr;
            }
        }
        for (auto& texture : effect_textures) {
            if (texture != nullptr) {
                wgpuTextureDestroy(texture);
                wgpuTextureRelease(texture);
                texture = nullptr;
            }
        }
        if (effect_blur_vertical_uniform_buffer != nullptr) {
            wgpuBufferDestroy(effect_blur_vertical_uniform_buffer);
            wgpuBufferRelease(effect_blur_vertical_uniform_buffer);
            effect_blur_vertical_uniform_buffer = nullptr;
        }
        if (effect_blur_horizontal_uniform_buffer != nullptr) {
            wgpuBufferDestroy(effect_blur_horizontal_uniform_buffer);
            wgpuBufferRelease(effect_blur_horizontal_uniform_buffer);
            effect_blur_horizontal_uniform_buffer = nullptr;
        }
        if (effect_drop_shadow_uniform_buffer != nullptr) {
            wgpuBufferDestroy(effect_drop_shadow_uniform_buffer);
            wgpuBufferRelease(effect_drop_shadow_uniform_buffer);
            effect_drop_shadow_uniform_buffer = nullptr;
        }
        if (effect_drop_shadow_pipeline != nullptr) {
            wgpuComputePipelineRelease(effect_drop_shadow_pipeline);
            effect_drop_shadow_pipeline = nullptr;
        }
        if (effect_drop_shadow_layout != nullptr) {
            wgpuBindGroupLayoutRelease(effect_drop_shadow_layout);
            effect_drop_shadow_layout = nullptr;
        }
        if (effect_drop_shadow_shader != nullptr) {
            wgpuShaderModuleRelease(effect_drop_shadow_shader);
            effect_drop_shadow_shader = nullptr;
        }
        if (effect_blur_vertical_pipeline != nullptr) {
            wgpuComputePipelineRelease(effect_blur_vertical_pipeline);
            effect_blur_vertical_pipeline = nullptr;
        }
        if (effect_blur_horizontal_pipeline != nullptr) {
            wgpuComputePipelineRelease(effect_blur_horizontal_pipeline);
            effect_blur_horizontal_pipeline = nullptr;
        }
        if (effect_blur_layout != nullptr) {
            wgpuBindGroupLayoutRelease(effect_blur_layout);
            effect_blur_layout = nullptr;
        }
        if (effect_blur_vertical_shader != nullptr) {
            wgpuShaderModuleRelease(effect_blur_vertical_shader);
            effect_blur_vertical_shader = nullptr;
        }
        if (effect_blur_horizontal_shader != nullptr) {
            wgpuShaderModuleRelease(effect_blur_horizontal_shader);
            effect_blur_horizontal_shader = nullptr;
        }
        effect_width = 0U;
        effect_height = 0U;
        effect_chain_width = 0U;
        effect_chain_height = 0U;
        effect_chain_cached_count = 0U;
        effect_chain_bindings_valid = false;
        effect_cache_valid = false;
        effect_drop_shadow_uniform_cache_valid = false;
        effect_chain_blur_horizontal_uniform_cache_valid.fill(false);
        effect_chain_blur_vertical_uniform_cache_valid.fill(false);
        effect_chain_drop_shadow_uniform_cache_valid.fill(false);
    }

    void release_semantic_analytic_page() noexcept {
        auto& page = semantic_analytic_cache;
        if (page.index_buffer != nullptr) {
            wgpuBufferDestroy(page.index_buffer);
            wgpuBufferRelease(page.index_buffer);
            page.index_buffer = nullptr;
        }
        if (page.vertex_buffer != nullptr) {
            wgpuBufferDestroy(page.vertex_buffer);
            wgpuBufferRelease(page.vertex_buffer);
            page.vertex_buffer = nullptr;
        }
        page.index_buffer_size = 0U;
        page.vertex_buffer_size = 0U;
        page.index_bytes = 0U;
        page.vertex_bytes = 0U;
        page.scene_hash = 0U;
        page.dpi_scale = 0.0F;
        page.target_width = 0U;
        page.target_height = 0U;
        page.cache_valid = false;
        page.draws.clear();
    }

    void release_semantic_3d_resources() noexcept {
        auto& page = semantic_3d_cache;
        if (page.bind_group != nullptr) {
            wgpuBindGroupRelease(page.bind_group);
            page.bind_group = nullptr;
        }
        const auto release_buffer = [](WGPUBuffer& buffer) noexcept {
            if (buffer != nullptr) {
                wgpuBufferDestroy(buffer);
                wgpuBufferRelease(buffer);
                buffer = nullptr;
            }
        };
        release_buffer(page.camera_buffer);
        release_buffer(page.line_buffer);
        release_buffer(page.mesh_buffer);
        release_buffer(page.vertex_buffer);
        release_buffer(page.index_buffer);
        page.draws.clear();
        page.mesh_topologies.clear();
        page.mesh_index_counts.clear();
        page.cache_valid = false;
        if (semantic_mesh_strip_3d_pipeline != nullptr) {
            wgpuRenderPipelineRelease(semantic_mesh_strip_3d_pipeline);
            semantic_mesh_strip_3d_pipeline = nullptr;
        }
        if (semantic_mesh_3d_pipeline != nullptr) {
            wgpuRenderPipelineRelease(semantic_mesh_3d_pipeline);
            semantic_mesh_3d_pipeline = nullptr;
        }
        if (semantic_line_3d_pipeline != nullptr) {
            wgpuRenderPipelineRelease(semantic_line_3d_pipeline);
            semantic_line_3d_pipeline = nullptr;
        }
        if (semantic_3d_pipeline_layout != nullptr) {
            wgpuPipelineLayoutRelease(semantic_3d_pipeline_layout);
            semantic_3d_pipeline_layout = nullptr;
        }
        if (semantic_3d_layout != nullptr) {
            wgpuBindGroupLayoutRelease(semantic_3d_layout);
            semantic_3d_layout = nullptr;
        }
        if (semantic_3d_shader != nullptr) {
            wgpuShaderModuleRelease(semantic_3d_shader);
            semantic_3d_shader = nullptr;
        }
    }

    void release_semantic_hit_test_index() noexcept {
        if (semantic_hit_test_bind_group != nullptr) {
            wgpuBindGroupRelease(semantic_hit_test_bind_group);
            semantic_hit_test_bind_group = nullptr;
        }
        const auto release = [](WGPUBuffer& buffer) noexcept {
            if (buffer != nullptr) {
                wgpuBufferDestroy(buffer);
                wgpuBufferRelease(buffer);
                buffer = nullptr;
            }
        };
        release(semantic_hit_test_node_buffer);
        release(semantic_hit_test_primitive_index_buffer);
        release(semantic_hit_test_primitive_buffer);
        release(semantic_hit_test_path_segment_buffer);
        semantic_hit_test_gpu_hash = 0U;
        semantic_hit_test_primitive_count = 0U;
        semantic_hit_test_node_count = 0U;
        semantic_hit_test_primitive_index_count = 0U;
        semantic_hit_test_path_segment_count = 0U;
    }

    void release_semantic_hit_test_resources() noexcept {
#if !defined(PROGPU_NATIVE_DAWN_ABI)
        if (semantic_hit_test_readback_buffer != nullptr &&
            semantic_hit_test_pending_token != 0U &&
            semantic_hit_test_map_state.completion.load(
                std::memory_order_acquire) ==
                progpu::native::webgpu::buffer_map_pending) {
            wgpuBufferUnmap(semantic_hit_test_readback_buffer);
            (void)wgpuDevicePoll(device, true, nullptr);
        }
#endif
        if (semantic_hit_test_readback_buffer != nullptr &&
#if defined(PROGPU_NATIVE_DAWN_ABI)
            wgpuBufferGetMapState(semantic_hit_test_readback_buffer) ==
                WGPUBufferMapState_Mapped
#else
            semantic_hit_test_map_state.completion.load(
                std::memory_order_acquire) ==
                progpu::native::webgpu::buffer_map_succeeded
#endif
            ) {
            progpu::native::webgpu::buffer_unmap(
                semantic_hit_test_readback_buffer);
        }
        semantic_hit_test_pending_token = 0U;
        semantic_hit_test_pending_bytes = 0U;
        semantic_hit_test_requested_result_count = 0U;
        semantic_hit_test_map_state.completion.store(
            progpu::native::webgpu::buffer_map_pending,
            std::memory_order_relaxed);
        release_semantic_hit_test_index();
        const auto release = [](WGPUBuffer& buffer) noexcept {
            if (buffer != nullptr) {
                wgpuBufferDestroy(buffer);
                wgpuBufferRelease(buffer);
                buffer = nullptr;
            }
        };
        release(semantic_hit_test_readback_buffer);
        release(semantic_hit_test_result_buffer);
        release(semantic_hit_test_query_buffer);
        if (semantic_hit_test_pipeline != nullptr) {
            wgpuComputePipelineRelease(semantic_hit_test_pipeline);
            semantic_hit_test_pipeline = nullptr;
        }
        if (semantic_hit_test_pipeline_layout != nullptr) {
            wgpuPipelineLayoutRelease(semantic_hit_test_pipeline_layout);
            semantic_hit_test_pipeline_layout = nullptr;
        }
        if (semantic_hit_test_layout != nullptr) {
            wgpuBindGroupLayoutRelease(semantic_hit_test_layout);
            semantic_hit_test_layout = nullptr;
        }
        if (semantic_hit_test_shader != nullptr) {
            wgpuShaderModuleRelease(semantic_hit_test_shader);
            semantic_hit_test_shader = nullptr;
        }
    }

    void release_semantic_render_bundle() noexcept {
        for (auto& span : semantic_render_bundle_spans) {
            if (span.advanced_blend_bind_group != nullptr) {
                wgpuBindGroupRelease(span.advanced_blend_bind_group);
                span.advanced_blend_bind_group = nullptr;
            }
            if (span.mask_bind_group != nullptr) {
                wgpuBindGroupRelease(span.mask_bind_group);
                span.mask_bind_group = nullptr;
            }
            if (span.mask_chain_bind_group != nullptr) {
                wgpuBindGroupRelease(span.mask_chain_bind_group);
                span.mask_chain_bind_group = nullptr;
            }
            if (span.mask_uniform_buffer != nullptr) {
                wgpuBufferDestroy(span.mask_uniform_buffer);
                wgpuBufferRelease(span.mask_uniform_buffer);
                span.mask_uniform_buffer = nullptr;
            }
            if (span.mask_chain_uniform_buffer != nullptr) {
                wgpuBufferDestroy(span.mask_chain_uniform_buffer);
                wgpuBufferRelease(span.mask_chain_uniform_buffer);
                span.mask_chain_uniform_buffer = nullptr;
            }
            if (span.mask_texture_view != nullptr) {
                wgpuTextureViewRelease(span.mask_texture_view);
                span.mask_texture_view = nullptr;
            }
            if (span.mask_texture != nullptr) {
                wgpuTextureDestroy(span.mask_texture);
                wgpuTextureRelease(span.mask_texture);
                span.mask_texture = nullptr;
            }
            if (span.bundle != nullptr) {
                wgpuRenderBundleRelease(span.bundle);
                span.bundle = nullptr;
            }
        }
        semantic_render_bundle_spans.clear();
        semantic_effect_dispatches.clear();
        semantic_render_bundle_valid = false;
        semantic_render_bundle_scene_hash = 0U;
        semantic_render_bundle_dpi_scale = 0.0F;
        semantic_render_bundle_width = 0U;
        semantic_render_bundle_height = 0U;
        semantic_render_bundle_draw_call_count = 0U;
        semantic_render_bundle_family_switch_count = 0U;
        semantic_destination_sampling_active = false;
        semantic_root_copy_vertex = 0U;
    }

    void release_semantic_layer_resources() noexcept {
        if (semantic_layer_vertex_buffer != nullptr) {
            wgpuBufferDestroy(semantic_layer_vertex_buffer);
            wgpuBufferRelease(semantic_layer_vertex_buffer);
            semantic_layer_vertex_buffer = nullptr;
        }
        const auto release_slot = [](semantic_layer_slot& slot) noexcept {
            for (auto& bind_group : slot.effect_drop_shadow_bind_groups) {
                if (bind_group != nullptr) {
                    wgpuBindGroupRelease(bind_group);
                    bind_group = nullptr;
                }
            }
            for (auto& bind_group : slot.effect_blur_bind_groups) {
                if (bind_group != nullptr) {
                    wgpuBindGroupRelease(bind_group);
                    bind_group = nullptr;
                }
            }
            for (auto& bind_group : slot.effect_output_bind_groups) {
                if (bind_group != nullptr) {
                    wgpuBindGroupRelease(bind_group);
                    bind_group = nullptr;
                }
            }
            for (auto& view : slot.effect_views) {
                if (view != nullptr) {
                    wgpuTextureViewRelease(view);
                    view = nullptr;
                }
            }
            for (auto& texture : slot.effect_textures) {
                if (texture != nullptr) {
                    wgpuTextureDestroy(texture);
                    wgpuTextureRelease(texture);
                    texture = nullptr;
                }
            }
            if (slot.analytic_uniform_bind_group != nullptr) {
                wgpuBindGroupRelease(slot.analytic_uniform_bind_group);
                slot.analytic_uniform_bind_group = nullptr;
            }
            if (slot.text_uniform_bind_group != nullptr) {
                wgpuBindGroupRelease(slot.text_uniform_bind_group);
                slot.text_uniform_bind_group = nullptr;
            }
            if (slot.image_uniform_bind_group != nullptr) {
                wgpuBindGroupRelease(slot.image_uniform_bind_group);
                slot.image_uniform_bind_group = nullptr;
            }
            if (slot.bind_group != nullptr) {
                wgpuBindGroupRelease(slot.bind_group);
                slot.bind_group = nullptr;
            }
            if (slot.view != nullptr) {
                wgpuTextureViewRelease(slot.view);
                slot.view = nullptr;
            }
            if (slot.texture != nullptr) {
                wgpuTextureDestroy(slot.texture);
                wgpuTextureRelease(slot.texture);
                slot.texture = nullptr;
            }
            if (slot.depth_view != nullptr) {
                wgpuTextureViewRelease(slot.depth_view);
                slot.depth_view = nullptr;
            }
            if (slot.depth_texture != nullptr) {
                wgpuTextureDestroy(slot.depth_texture);
                wgpuTextureRelease(slot.depth_texture);
                slot.depth_texture = nullptr;
            }
            if (slot.uniform_buffer != nullptr) {
                wgpuBufferDestroy(slot.uniform_buffer);
                wgpuBufferRelease(slot.uniform_buffer);
                slot.uniform_buffer = nullptr;
            }
            slot.bound_analytic_brush_buffer = nullptr;
            slot.bound_analytic_gradient_buffer = nullptr;
            slot.bound_text_style_buffer = nullptr;
            slot.uniform_cache_valid = false;
            slot.width = 0U;
            slot.height = 0U;
            slot.depth_width = 0U;
            slot.depth_height = 0U;
            slot.effect_width = 0U;
            slot.effect_height = 0U;
            progpu::native::effects::invalidate_semantic_output_cache(
                slot.effect_output_cache);
        };
        for (auto& slot : semantic_layer_slots) {
            release_slot(slot);
        }
        release_slot(semantic_root_slot);
        release_slot(semantic_advanced_source_slot);
        release_slot(semantic_advanced_output_slot);
        if (semantic_effect_uniform_buffer != nullptr) {
            wgpuBufferDestroy(semantic_effect_uniform_buffer);
            wgpuBufferRelease(semantic_effect_uniform_buffer);
            semantic_effect_uniform_buffer = nullptr;
        }
        semantic_layer_vertex_buffer_size = 0U;
        semantic_layer_vertex_content_hash = 0U;
        semantic_layer_vertex_content_bytes = 0U;
        semantic_effect_uniform_buffer_size = 0U;
        if (semantic_advanced_blend_uniform_buffer != nullptr) {
            wgpuBufferDestroy(semantic_advanced_blend_uniform_buffer);
            wgpuBufferRelease(semantic_advanced_blend_uniform_buffer);
            semantic_advanced_blend_uniform_buffer = nullptr;
        }
        semantic_advanced_blend_uniform_buffer_size = 0U;
    }

    void release_semantic_image_page() noexcept {
        auto& page = semantic_image_cache;
        for (auto& draw : page.draws) {
            if (draw.blur_vertical_bind_group != nullptr) {
                wgpuBindGroupRelease(draw.blur_vertical_bind_group);
            }
            if (draw.blur_horizontal_bind_group != nullptr) {
                wgpuBindGroupRelease(draw.blur_horizontal_bind_group);
            }
            if (draw.blur_vertical_uniform_buffer != nullptr) {
                wgpuBufferDestroy(draw.blur_vertical_uniform_buffer);
                wgpuBufferRelease(draw.blur_vertical_uniform_buffer);
            }
            if (draw.blur_horizontal_uniform_buffer != nullptr) {
                wgpuBufferDestroy(draw.blur_horizontal_uniform_buffer);
                wgpuBufferRelease(draw.blur_horizontal_uniform_buffer);
            }
            if (draw.blur_output_view != nullptr) {
                wgpuTextureViewRelease(draw.blur_output_view);
            }
            if (draw.blur_output_texture != nullptr) {
                wgpuTextureDestroy(draw.blur_output_texture);
                wgpuTextureRelease(draw.blur_output_texture);
            }
            if (draw.blur_intermediate_view != nullptr) {
                wgpuTextureViewRelease(draw.blur_intermediate_view);
            }
            if (draw.blur_intermediate_texture != nullptr) {
                wgpuTextureDestroy(draw.blur_intermediate_texture);
                wgpuTextureRelease(draw.blur_intermediate_texture);
            }
            if (draw.effect_dummy_mask_bind_group != nullptr) {
                wgpuBindGroupRelease(draw.effect_dummy_mask_bind_group);
            }
            if (draw.effect_texture_bind_group != nullptr) {
                wgpuBindGroupRelease(draw.effect_texture_bind_group);
            }
            if (draw.effect_uniform_bind_group != nullptr) {
                wgpuBindGroupRelease(draw.effect_uniform_bind_group);
            }
            if (draw.effect_uniform_buffer != nullptr) {
                wgpuBufferDestroy(draw.effect_uniform_buffer);
                wgpuBufferRelease(draw.effect_uniform_buffer);
            }
            if (draw.effect_mask_uniform_buffer != nullptr) {
                wgpuBufferDestroy(draw.effect_mask_uniform_buffer);
                wgpuBufferRelease(draw.effect_mask_uniform_buffer);
            }
            if (draw.color_matrix_bind_group != nullptr) {
                wgpuBindGroupRelease(draw.color_matrix_bind_group);
            }
            if (draw.color_matrix_buffer != nullptr) {
                wgpuBufferDestroy(draw.color_matrix_buffer);
                wgpuBufferRelease(draw.color_matrix_buffer);
            }
            if (draw.texture_bind_group != nullptr) {
                wgpuBindGroupRelease(draw.texture_bind_group);
            }
            if (draw.view != nullptr) {
                wgpuTextureViewRelease(draw.view);
            }
            if (draw.texture != nullptr) {
                wgpuTextureDestroy(draw.texture);
                wgpuTextureRelease(draw.texture);
            }
        }
        page.draws.clear();
        if (page.vertex_buffer != nullptr) {
            wgpuBufferDestroy(page.vertex_buffer);
            wgpuBufferRelease(page.vertex_buffer);
            page.vertex_buffer = nullptr;
        }
        page.vertex_bytes = 0U;
        page.scene_hash = 0U;
        page.dpi_scale = 0.0F;
        page.target_width = 0U;
        page.target_height = 0U;
        page.cache_valid = false;
    }

    void release_semantic_external_image_bindings() noexcept {
        for (auto& binding : semantic_external_image_bindings) {
            if (binding.view != nullptr) {
                wgpuTextureViewRelease(binding.view);
            }
        }
        semantic_external_image_bindings.clear();
    }

    const semantic_external_image_binding*
    find_semantic_external_image_binding(
        std::uint64_t resource_id,
        std::uint64_t generation,
        std::uint32_t role =
            PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE_PRIMARY) const noexcept {
        const auto iterator = std::lower_bound(
            semantic_external_image_bindings.begin(),
            semantic_external_image_bindings.end(),
            std::pair{resource_id, role},
            [](const semantic_external_image_binding& binding,
                const std::pair<std::uint64_t, std::uint32_t>& key) noexcept {
                return binding.resource_id < key.first ||
                    (binding.resource_id == key.first &&
                        binding.role < key.second);
            });
        return iterator != semantic_external_image_bindings.end() &&
                iterator->resource_id == resource_id &&
                iterator->generation == generation &&
                iterator->role == role
            ? &*iterator
            : nullptr;
    }

    void release_semantic_layer_analytic_bindings() noexcept {
        release_semantic_render_bundle();
        for (auto& slot : semantic_layer_slots) {
            if (slot.analytic_uniform_bind_group != nullptr) {
                wgpuBindGroupRelease(slot.analytic_uniform_bind_group);
                slot.analytic_uniform_bind_group = nullptr;
            }
            slot.bound_analytic_brush_buffer = nullptr;
            slot.bound_analytic_gradient_buffer = nullptr;
        }
        const auto release_slot = [](semantic_layer_slot& slot) noexcept {
            if (slot.analytic_uniform_bind_group != nullptr) {
                wgpuBindGroupRelease(slot.analytic_uniform_bind_group);
                slot.analytic_uniform_bind_group = nullptr;
            }
            slot.bound_analytic_brush_buffer = nullptr;
            slot.bound_analytic_gradient_buffer = nullptr;
        };
        release_slot(semantic_root_slot);
        release_slot(semantic_advanced_source_slot);
        release_slot(semantic_advanced_output_slot);
    }

    void release_semantic_layer_text_bindings() noexcept {
        release_semantic_render_bundle();
        const auto release_slot = [](semantic_layer_slot& slot) noexcept {
            if (slot.text_uniform_bind_group != nullptr) {
                wgpuBindGroupRelease(slot.text_uniform_bind_group);
                slot.text_uniform_bind_group = nullptr;
            }
            slot.bound_text_style_buffer = nullptr;
        };
        for (auto& slot : semantic_layer_slots) {
            release_slot(slot);
        }
        release_slot(semantic_root_slot);
        release_slot(semantic_advanced_source_slot);
        release_slot(semantic_advanced_output_slot);
    }

    bool ensure_semantic_analytic_page_buffers(
        std::uint64_t required_vertex_bytes,
        std::uint64_t required_index_bytes) noexcept {
        auto& page = semantic_analytic_cache;
        const auto required_capacity = [](std::uint64_t current,
                                          std::uint64_t required,
                                          std::uint64_t& capacity) noexcept {
            capacity = std::max<std::uint64_t>(256U, current);
            while (capacity < required) {
                if (capacity >
                    std::numeric_limits<std::uint64_t>::max() / 2U) {
                    return false;
                }
                capacity *= 2U;
            }
            return true;
        };
        const bool grow_vertex = page.vertex_buffer == nullptr ||
            required_vertex_bytes > page.vertex_buffer_size;
        const bool grow_index = page.index_buffer == nullptr ||
            required_index_bytes > page.index_buffer_size;
        std::uint64_t vertex_capacity = page.vertex_buffer_size;
        std::uint64_t index_capacity = page.index_buffer_size;
        if ((grow_vertex && !required_capacity(
                page.vertex_buffer_size,
                required_vertex_bytes,
                vertex_capacity)) ||
            (grow_index && !required_capacity(
                page.index_buffer_size,
                required_index_bytes,
                index_capacity))) {
            return false;
        }

        WGPUBuffer replacement_vertex = nullptr;
        WGPUBuffer replacement_index = nullptr;
        if (grow_vertex) {
            WGPUBufferDescriptor descriptor{};
            descriptor.label = progpu::native::webgpu::string_view(
                "ProGPU semantic analytic packed vertex page");
            descriptor.usage =
                WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst;
            descriptor.size = vertex_capacity;
            replacement_vertex = wgpuDeviceCreateBuffer(device, &descriptor);
            if (replacement_vertex == nullptr) {
                return false;
            }
        }
        if (grow_index) {
            WGPUBufferDescriptor descriptor{};
            descriptor.label = progpu::native::webgpu::string_view(
                "ProGPU semantic analytic packed index page");
            descriptor.usage =
                WGPUBufferUsage_Index | WGPUBufferUsage_CopyDst;
            descriptor.size = index_capacity;
            replacement_index = wgpuDeviceCreateBuffer(device, &descriptor);
            if (replacement_index == nullptr) {
                if (replacement_vertex != nullptr) {
                    wgpuBufferDestroy(replacement_vertex);
                    wgpuBufferRelease(replacement_vertex);
                }
                return false;
            }
        }

        if (replacement_vertex != nullptr) {
            if (page.vertex_buffer != nullptr) {
                wgpuBufferDestroy(page.vertex_buffer);
                wgpuBufferRelease(page.vertex_buffer);
            }
            page.vertex_buffer = replacement_vertex;
            page.vertex_buffer_size = vertex_capacity;
        }
        if (replacement_index != nullptr) {
            if (page.index_buffer != nullptr) {
                wgpuBufferDestroy(page.index_buffer);
                wgpuBufferRelease(page.index_buffer);
            }
            page.index_buffer = replacement_index;
            page.index_buffer_size = index_capacity;
        }
        return true;
    }

    ~progpu_native_engine() {
        const progpu::native::webgpu::dispatch_scope dispatch_scope(
            &webgpu_dispatch);
        if (semantic_encoder != nullptr) {
            wgpuCommandEncoderRelease(semantic_encoder);
            semantic_encoder = nullptr;
        }
        release_semantic_render_bundle();
        release_semantic_layer_resources();
        release_semantic_image_page();
        release_semantic_external_image_bindings();
        release_semantic_analytic_page();
        release_semantic_3d_resources();
        release_semantic_hit_test_resources();
        release_effect_resources();
        release_clip_resources();
        if (semantic_advanced_blend_layout != nullptr) {
            wgpuBindGroupLayoutRelease(semantic_advanced_blend_layout);
        }
        if (semantic_advanced_blend_pipeline != nullptr) {
            wgpuRenderPipelineRelease(semantic_advanced_blend_pipeline);
        }
        if (semantic_advanced_blend_shader != nullptr) {
            wgpuShaderModuleRelease(semantic_advanced_blend_shader);
        }
        if (group_blend_bind_group != nullptr) {
            wgpuBindGroupRelease(group_blend_bind_group);
        }
        if (group_blend_source_view != nullptr) {
            wgpuTextureViewRelease(group_blend_source_view);
        }
        if (group_blend_source_texture != nullptr) {
            wgpuTextureDestroy(group_blend_source_texture);
            wgpuTextureRelease(group_blend_source_texture);
        }
        if (group_blend_uniform_buffer != nullptr) {
            wgpuBufferDestroy(group_blend_uniform_buffer);
            wgpuBufferRelease(group_blend_uniform_buffer);
        }
        if (group_blend_layout != nullptr) {
            wgpuBindGroupLayoutRelease(group_blend_layout);
        }
        if (group_blend_pipeline != nullptr) {
            wgpuRenderPipelineRelease(group_blend_pipeline);
        }
        if (group_blend_shader != nullptr) {
            wgpuShaderModuleRelease(group_blend_shader);
        }
        if (layer_external_mask_linear_bind_group != nullptr) {
            wgpuBindGroupRelease(layer_external_mask_linear_bind_group);
        }
        if (layer_external_mask_nearest_bind_group != nullptr) {
            wgpuBindGroupRelease(layer_external_mask_nearest_bind_group);
        }
        if (layer_external_mask_view != nullptr) {
            wgpuTextureViewRelease(layer_external_mask_view);
        }
        if (layer_analytic_mask_bind_group != nullptr) {
            wgpuBindGroupRelease(layer_analytic_mask_bind_group);
        }
        if (layer_mask_dummy_view != nullptr) {
            wgpuTextureViewRelease(layer_mask_dummy_view);
        }
        if (layer_mask_dummy_texture != nullptr) {
            wgpuTextureDestroy(layer_mask_dummy_texture);
            wgpuTextureRelease(layer_mask_dummy_texture);
        }
        if (layer_mask_uniform_buffer != nullptr) {
            wgpuBufferDestroy(layer_mask_uniform_buffer);
            wgpuBufferRelease(layer_mask_uniform_buffer);
        }
        if (layer_mask_layout != nullptr) {
            wgpuBindGroupLayoutRelease(layer_mask_layout);
        }
        if (layer_mask_pipeline != nullptr) {
            wgpuRenderPipelineRelease(layer_mask_pipeline);
        }
        for (auto mask_blend_pipeline : layer_mask_blend_pipelines) {
            if (mask_blend_pipeline != nullptr) {
                wgpuRenderPipelineRelease(mask_blend_pipeline);
            }
        }
        for (auto blend_pipeline : layer_blend_pipelines) {
            if (blend_pipeline != nullptr) {
                wgpuRenderPipelineRelease(blend_pipeline);
            }
        }
        if (layer_texture_bind_group != nullptr) {
            wgpuBindGroupRelease(layer_texture_bind_group);
        }
        if (layer_texture_view != nullptr) {
            wgpuTextureViewRelease(layer_texture_view);
        }
        if (layer_texture != nullptr) {
            wgpuTextureDestroy(layer_texture);
            wgpuTextureRelease(layer_texture);
        }
        if (layer_index_buffer != nullptr) {
            wgpuBufferDestroy(layer_index_buffer);
            wgpuBufferRelease(layer_index_buffer);
        }
        if (layer_vertex_buffer != nullptr) {
            wgpuBufferDestroy(layer_vertex_buffer);
            wgpuBufferRelease(layer_vertex_buffer);
        }
        if (layer_uniform_bind_group != nullptr) {
            wgpuBindGroupRelease(layer_uniform_bind_group);
        }
        if (layer_uniform_buffer != nullptr) {
            wgpuBufferDestroy(layer_uniform_buffer);
            wgpuBufferRelease(layer_uniform_buffer);
        }
        if (layer_composite_pipeline != nullptr) {
            wgpuRenderPipelineRelease(layer_composite_pipeline);
        }
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
        if (image_masked_color_matrix_pipeline != nullptr) {
            wgpuRenderPipelineRelease(image_masked_color_matrix_pipeline);
        }
        if (image_effect_pipeline != nullptr) {
            wgpuRenderPipelineRelease(image_effect_pipeline);
        }
        if (image_effect_mask_chain_pipeline != nullptr) {
            wgpuRenderPipelineRelease(image_effect_mask_chain_pipeline);
        }
        if (semantic_image_blur_pipeline != nullptr) {
            wgpuRenderPipelineRelease(semantic_image_blur_pipeline);
        }
        if (semantic_image_blur_unfilterable_pipeline != nullptr) {
            wgpuRenderPipelineRelease(
                semantic_image_blur_unfilterable_pipeline);
        }
        if (semantic_image_blur_unfilterable_layout != nullptr) {
            wgpuBindGroupLayoutRelease(
                semantic_image_blur_unfilterable_layout);
        }
        if (semantic_image_blur_unfilterable_shader != nullptr) {
            wgpuShaderModuleRelease(
                semantic_image_blur_unfilterable_shader);
        }
        if (semantic_image_blur_layout != nullptr) {
            wgpuBindGroupLayoutRelease(semantic_image_blur_layout);
        }
        if (semantic_image_blur_shader != nullptr) {
            wgpuShaderModuleRelease(semantic_image_blur_shader);
        }
        if (image_effect_texture_layout != nullptr) {
            wgpuBindGroupLayoutRelease(image_effect_texture_layout);
        }
        if (image_effect_uniform_layout != nullptr) {
            wgpuBindGroupLayoutRelease(image_effect_uniform_layout);
        }
        if (image_effect_shader != nullptr) {
            wgpuShaderModuleRelease(image_effect_shader);
        }
        if (image_mask_chain_color_matrix_pipeline != nullptr) {
            wgpuRenderPipelineRelease(image_mask_chain_color_matrix_pipeline);
        }
        if (image_mask_chain_pipeline != nullptr) {
            wgpuRenderPipelineRelease(image_mask_chain_pipeline);
        }
        if (image_color_matrix_pipeline != nullptr) {
            wgpuRenderPipelineRelease(image_color_matrix_pipeline);
        }
        if (image_mask_pipeline != nullptr) {
            wgpuRenderPipelineRelease(image_mask_pipeline);
        }
        if (image_mask_layout != nullptr) {
            wgpuBindGroupLayoutRelease(image_mask_layout);
        }
        if (image_index_buffer != nullptr) {
            wgpuBufferDestroy(image_index_buffer);
            wgpuBufferRelease(image_index_buffer);
        }
        if (image_vertex_buffer != nullptr) {
            wgpuBufferDestroy(image_vertex_buffer);
            wgpuBufferRelease(image_vertex_buffer);
        }
        if (image_texture_bind_group != nullptr) {
            wgpuBindGroupRelease(image_texture_bind_group);
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
        if (image_mipmap_sampler != nullptr) {
            wgpuSamplerRelease(image_mipmap_sampler);
        }
        for (auto sampler : image_filtered_samplers) {
            if (sampler != nullptr) {
                wgpuSamplerRelease(sampler);
            }
        }
        for (auto sampler : image_anisotropic_samplers) {
            if (sampler != nullptr) {
                wgpuSamplerRelease(sampler);
            }
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
        if (color_glyph_atlas_texture_view != nullptr) {
            wgpuTextureViewRelease(color_glyph_atlas_texture_view);
        }
        if (color_glyph_atlas_texture != nullptr) {
            wgpuTextureDestroy(color_glyph_atlas_texture);
            wgpuTextureRelease(color_glyph_atlas_texture);
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
        if (text_masked_pipeline != nullptr) {
            wgpuRenderPipelineRelease(text_masked_pipeline);
        }
        if (text_mask_chain_pipeline != nullptr) {
            wgpuRenderPipelineRelease(text_mask_chain_pipeline);
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
        if (path_index_buffer != nullptr) {
            wgpuBufferDestroy(path_index_buffer);
            wgpuBufferRelease(path_index_buffer);
        }
        if (path_vertex_buffer != nullptr) {
            wgpuBufferDestroy(path_vertex_buffer);
            wgpuBufferRelease(path_vertex_buffer);
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
        if (analytic_masked_pipeline != nullptr) {
            wgpuRenderPipelineRelease(analytic_masked_pipeline);
        }
        if (analytic_mask_chain_pipeline != nullptr) {
            wgpuRenderPipelineRelease(analytic_mask_chain_pipeline);
        }
        if (analytic_brush_mask_pipeline != nullptr) {
            wgpuRenderPipelineRelease(analytic_brush_mask_pipeline);
        }
        if (semantic_mask_chain_layout != nullptr) {
            wgpuBindGroupLayoutRelease(semantic_mask_chain_layout);
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
        geometry_gpu_cache_valid = false;
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
        geometry_gpu_cache_valid = false;
        return true;
    }

    bool ensure_path_vertex_buffer(std::uint64_t required_size) {
        if (required_size <= path_vertex_buffer_size &&
            path_vertex_buffer != nullptr) {
            return true;
        }
        std::uint64_t new_size = std::max(
            initial_vertex_buffer_size,
            path_vertex_buffer_size);
        while (new_size < required_size) {
            if (new_size > std::numeric_limits<std::uint64_t>::max() / 2U) {
                return false;
            }
            new_size *= 2U;
        }
        WGPUBufferDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native retained path vertex buffer");
        descriptor.usage = WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst;
        descriptor.size = new_size;
        WGPUBuffer replacement = wgpuDeviceCreateBuffer(device, &descriptor);
        if (replacement == nullptr) {
            return false;
        }
        if (path_vertex_buffer != nullptr) {
            wgpuBufferDestroy(path_vertex_buffer);
            wgpuBufferRelease(path_vertex_buffer);
        }
        path_vertex_buffer = replacement;
        path_vertex_buffer_size = new_size;
        path_gpu_cache_valid = false;
        return true;
    }

    bool ensure_path_index_buffer(std::uint64_t required_size) {
        if (required_size <= path_index_buffer_size &&
            path_index_buffer != nullptr) {
            return true;
        }
        std::uint64_t new_size = std::max(
            initial_index_buffer_size,
            path_index_buffer_size);
        while (new_size < required_size) {
            if (new_size > std::numeric_limits<std::uint64_t>::max() / 2U) {
                return false;
            }
            new_size *= 2U;
        }
        WGPUBufferDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native retained path index buffer");
        descriptor.usage = WGPUBufferUsage_Index | WGPUBufferUsage_CopyDst;
        descriptor.size = new_size;
        WGPUBuffer replacement = wgpuDeviceCreateBuffer(device, &descriptor);
        if (replacement == nullptr) {
            return false;
        }
        if (path_index_buffer != nullptr) {
            wgpuBufferDestroy(path_index_buffer);
            wgpuBufferRelease(path_index_buffer);
        }
        path_index_buffer = replacement;
        path_index_buffer_size = new_size;
        path_gpu_cache_valid = false;
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
