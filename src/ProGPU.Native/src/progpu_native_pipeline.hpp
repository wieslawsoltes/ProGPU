#pragma once

// Internal pipeline construction is compiled only after the selected WebGPU C
// header and ProGPU dispatch compatibility layer have declared WGPU types.
#include "progpu_native.h"
#include "progpu_native_gpu_records.hpp"
#include "progpu_webgpu_compat.hpp"

#include <cstdint>

struct progpu_native_engine;

WGPUTextureFormat texture_format(std::uint32_t value) noexcept;

progpu::native::gpu_uniforms create_uniforms(
    std::uint32_t width,
    std::uint32_t height,
    float dpi_scale) noexcept;

bool create_pipeline(progpu_native_engine& engine);

WGPUBindGroup create_analytic_uniform_bind_group_for_buffer(
    progpu_native_engine& engine,
    WGPUBuffer uniform_buffer,
    WGPUBuffer brush_buffer,
    std::uint64_t brush_buffer_size,
    WGPUBuffer gradient_buffer,
    std::uint64_t gradient_buffer_size,
    const char* label);

bool ensure_analytic_material_buffers(
    progpu_native_engine& engine,
    std::uint64_t required_brush_size,
    std::uint64_t required_gradient_size);

bool ensure_analytic_brush_buffer(
    progpu_native_engine& engine,
    std::uint64_t required_size);

bool create_analytic_pipeline(progpu_native_engine& engine);
bool create_path_resources(progpu_native_engine& engine);
bool create_glyph_resources(progpu_native_engine& engine);
bool ensure_text_style_buffer(
    progpu_native_engine& engine,
    std::uint64_t required_size);

bool resize_path_atlas(
    progpu_native_engine& engine,
    std::uint32_t requested_size);

bool resize_glyph_atlas(
    progpu_native_engine& engine,
    std::uint32_t requested_size);

WGPUBindGroup create_image_texture_bind_group(
    progpu_native_engine& engine,
    WGPUSampler sampler,
    WGPUTextureView view,
    const char* label);

bool create_image_resources(progpu_native_engine& engine);
bool create_layer_resources(progpu_native_engine& engine);

WGPUBindGroup create_layer_mask_bind_group(
    progpu_native_engine& engine,
    WGPUSampler sampler,
    WGPUTextureView view,
    const char* label,
    WGPUBuffer uniform_buffer = nullptr);

bool create_layer_mask_resources(progpu_native_engine& engine);
bool is_advanced_group_blend(std::uint32_t blend_mode) noexcept;

WGPURenderPipeline get_or_create_fixed_group_blend_pipeline(
    progpu_native_engine& engine,
    std::uint32_t blend_mode,
    bool masked,
    bool& cache_hit);

bool ensure_advanced_group_blend_source(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height);

bool create_clip_chain_resources(progpu_native_engine& engine);

bool ensure_clip_buffer(
    progpu_native_engine& engine,
    WGPUBuffer& buffer,
    std::uint64_t& capacity,
    std::uint64_t required,
    progpu::native::webgpu::buffer_usage_flags usage,
    const char* label);

bool rebuild_clip_bind_groups(progpu_native_engine& engine);

bool ensure_clip_textures(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t atlas_size);
