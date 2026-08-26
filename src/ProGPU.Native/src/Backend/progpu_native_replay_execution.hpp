#pragma once

// Internal retained replay execution is compiled only after the selected
// WebGPU C header has declared the WGPU handle types.
#include "progpu_native_draw_state.hpp"
#include "progpu_native_engine.hpp"
#include "progpu_native_semantic_budget.hpp"
#include "progpu_native_semantic_replay.hpp"

#include <cstdint>
#include <vector>

namespace progpu::native::semantic {
class semantic_state_cursor;
}

namespace progpu::native::execution {

void apply_scissor(
    WGPURenderPassEncoder pass,
    const resolved_draw_state& state) noexcept;

bool update_layer_group_mask(
    progpu_native_engine& engine,
    const resolved_draw_state& draw_state,
    float dpi_scale,
    bool& uploaded_uniforms);

bool ensure_layer_texture(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height);

bool rebuild_vector_clip_chain(
    progpu_native_engine& engine,
    const progpu_native_group_mask& mask,
    std::uint32_t width,
    std::uint32_t height,
    float dpi_scale);

bool prepare_semantic_layer_resources(
    progpu_native_engine& engine,
    const semantic::layer_budget& budget,
    const semantic::cache_budget& cache_budget,
    std::uint32_t frame_width,
    std::uint32_t frame_height,
    float dpi_scale,
    std::uint32_t composite_count,
    std::uint64_t& uploaded_uniform_bytes);

bool prepare_semantic_depth_resources(
    progpu_native_engine& engine,
    const semantic::layer_budget& budget,
    std::uint32_t frame_width,
    std::uint32_t frame_height);

bool ensure_semantic_texture_slot(
    progpu_native_engine& engine,
    semantic_layer_slot& slot,
    std::uint32_t width,
    std::uint32_t height,
    const char* label);

bool prepare_semantic_advanced_blend_resources(
    progpu_native_engine& engine,
    std::uint32_t frame_width,
    std::uint32_t frame_height,
    std::uint32_t source_width,
    std::uint32_t source_height,
    std::uint32_t operation_count,
    float dpi_scale,
    std::uint64_t& uploaded_uniform_bytes);

bool prepare_semantic_backdrop_resources(
    progpu_native_engine& engine,
    std::uint32_t frame_width,
    std::uint32_t frame_height,
    std::uint32_t operation_count,
    float dpi_scale,
    std::uint64_t& uploaded_uniform_bytes);

bool create_semantic_advanced_blend_binding(
    progpu_native_engine& engine,
    WGPUTextureView destination_view,
    const gpu_advanced_blend_sampling_uniforms& uniforms,
    semantic_render_bundle_span& operation);

bool encode_semantic_advanced_blend(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    WGPUTextureView parent_view,
    WGPUBindGroup parent_uniform_group,
    const semantic_render_bundle_span& operation);

bool encode_semantic_root_copy(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    WGPUTextureView target_view,
    std::uint32_t first_vertex);

bool encode_semantic_backdrop_capture(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    const semantic_render_bundle_span& operation,
    std::uint32_t& effect_pass_count);

void append_semantic_layer_quad(
    std::vector<vector_vertex>& vertices,
    const semantic::scissor& source,
    const semantic::scissor& target,
    std::uint32_t source_texture_width,
    std::uint32_t source_texture_height,
    float dpi_scale,
    float opacity);

void append_semantic_transformed_layer_quad(
    std::vector<vector_vertex>& vertices,
    const semantic::scissor& source,
    const semantic::scissor& target,
    std::uint32_t source_texture_width,
    std::uint32_t source_texture_height,
    float dpi_scale,
    float opacity,
    const progpu_native_affine_2d& transform);

bool create_semantic_layer_mask_binding(
    progpu_native_engine& engine,
    const std::byte* bytes,
    const progpu_native_scene_resource& resource,
    const semantic::scissor& target_extent,
    float dpi_scale,
    const semantic::semantic_state_cursor* composite_state_cursor,
    const progpu_native_scene_state* composite_state,
    semantic_render_bundle_span& operation,
    std::uint64_t& texture_upload_bytes);

bool create_gaussian_effect_resources(progpu_native_engine& engine);
bool create_drop_shadow_effect_resources(progpu_native_engine& engine);

bool ensure_semantic_effect_uniform_buffer(
    progpu_native_engine& engine,
    std::uint64_t required_bytes);

WGPUBindGroup get_or_create_semantic_effect_blur_binding(
    progpu_native_engine& engine,
    semantic_layer_slot& slot,
    std::int32_t source,
    std::uint32_t output);

WGPUBindGroup get_or_create_semantic_effect_drop_shadow_binding(
    progpu_native_engine& engine,
    semantic_layer_slot& slot,
    std::int32_t source,
    std::uint32_t blurred,
    std::uint32_t output);

bool encode_group_effect(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    const resolved_draw_state& draw_state,
    float dpi_scale);

bool prepare_group_effect(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height,
    const resolved_draw_state& draw_state);

void retain_group_effect(
    progpu_native_engine& engine,
    float dpi_scale,
    const resolved_draw_state& draw_state) noexcept;

void reset_layer_metrics(progpu_native_engine& engine) noexcept;

bool encode_semantic_layer_composite(
    progpu_native_engine& engine,
    WGPURenderPassEncoder pass,
    const semantic_render_bundle_span& operation);

bool encode_semantic_effect_chain(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    const semantic_render_bundle_span& operation,
    std::uint32_t& pass_count);

bool encode_layer_composite(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    WGPUTextureView target_view,
    const progpu_native_color& clear_color,
    const resolved_draw_state& draw_state);

progpu_native_status prepare_group_layer(
    progpu_native_engine& engine,
    layer_family family,
    std::uint32_t width,
    std::uint32_t height,
    float dpi_scale,
    WGPUTextureView target_view,
    const progpu_native_color& clear_color,
    const resolved_draw_state& draw_state,
    bool& use_group_layer,
    bool& submitted_cache_hit);

void retain_group_layer_content(
    progpu_native_engine& engine,
    layer_family family,
    float dpi_scale,
    const resolved_draw_state& draw_state) noexcept;

bool create_image_mask_resources(progpu_native_engine& engine);

bool update_image_mask(
    progpu_native_engine& engine,
    const progpu_native_image_frame& frame,
    bool& uploaded_uniforms);

bool upload_image_texture(
    progpu_native_engine& engine,
    const progpu_native_image_frame& frame);

bool update_image_texture_binding(
    progpu_native_engine& engine,
    WGPUSampler sampler,
    std::uint32_t sampling,
    std::uint32_t max_anisotropy);

} // namespace progpu::native::execution
