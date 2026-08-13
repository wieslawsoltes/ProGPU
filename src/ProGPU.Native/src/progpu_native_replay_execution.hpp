#pragma once

// Internal retained replay execution is compiled only after the selected
// WebGPU C header has declared the WGPU handle types.
#include "progpu_native_draw_state.hpp"
#include "progpu_native_engine.hpp"
#include "progpu_native_semantic_budget.hpp"
#include "progpu_native_semantic_replay.hpp"

#include <cstdint>
#include <vector>

namespace progpu::native::execution {

void apply_scissor(
    WGPURenderPassEncoder pass,
    const resolved_draw_state& state) noexcept;

bool rebuild_vector_clip_chain(
    progpu_native_engine& engine,
    const progpu_native_group_mask& mask,
    std::uint32_t width,
    std::uint32_t height,
    float dpi_scale);

bool prepare_semantic_layer_resources(
    progpu_native_engine& engine,
    const semantic::layer_budget& budget,
    std::uint32_t frame_width,
    std::uint32_t frame_height,
    float dpi_scale,
    std::uint32_t composite_count,
    std::uint64_t& uploaded_uniform_bytes);

void append_semantic_layer_quad(
    std::vector<vector_vertex>& vertices,
    const semantic::scissor& source,
    const semantic::scissor& target,
    std::uint32_t source_texture_width,
    std::uint32_t source_texture_height,
    float dpi_scale,
    float opacity);

bool create_semantic_layer_mask_binding(
    progpu_native_engine& engine,
    const progpu_native_scene_layer_mask& source,
    const semantic::scissor& target_extent,
    float dpi_scale,
    semantic_render_bundle_span& operation);

bool create_gaussian_effect_resources(progpu_native_engine& engine);
bool create_drop_shadow_effect_resources(progpu_native_engine& engine);

bool ensure_semantic_effect_uniform_buffer(
    progpu_native_engine& engine,
    std::uint64_t required_bytes);

bool encode_group_effect(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    const resolved_draw_state& draw_state,
    float dpi_scale);

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

} // namespace progpu::native::execution
