#pragma once

#include "progpu_native.h"

struct progpu_native_engine;
struct semantic_image_draw;
struct semantic_image_page;

namespace progpu::native::semantic {

bool create_semantic_image_color_matrix_resources(
    progpu_native_engine& engine,
    WGPUTextureView image_view,
    const progpu_native_scene_image_color_matrix& matrix,
    WGPUBuffer& uniform_buffer,
    WGPUBindGroup& bind_group) noexcept;

bool create_semantic_image_effect_resources(
    progpu_native_engine& engine,
    WGPUTextureView image_view,
    WGPUTextureView chroma_view,
    WGPUTextureView mask_view,
    std::uint32_t mask_width,
    std::uint32_t mask_height,
    WGPUSampler sampler,
    const progpu_native_scene_image_effect& effect,
    WGPUBuffer& uniform_buffer,
    WGPUBuffer& mask_uniform_buffer,
    WGPUBindGroup& uniform_bind_group,
    WGPUBindGroup& texture_bind_group,
    WGPUBindGroup& dummy_mask_bind_group) noexcept;

bool create_semantic_image_blur_resources(
    progpu_native_engine& engine,
    WGPUTextureView image_view,
    WGPUTextureView chroma_view,
    std::uint32_t width,
    std::uint32_t height,
    const progpu_native_scene_image_effect& effect,
    semantic_image_draw& draw) noexcept;

bool encode_semantic_image_blurs(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    semantic_image_page& page) noexcept;

void release_semantic_image_blur_resources(
    semantic_image_draw& draw) noexcept;

} // namespace progpu::native::semantic
