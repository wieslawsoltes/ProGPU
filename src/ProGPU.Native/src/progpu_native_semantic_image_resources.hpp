#pragma once

#include "progpu_native.h"

struct progpu_native_engine;

namespace progpu::native::semantic {

bool create_semantic_image_color_matrix_resources(
    progpu_native_engine& engine,
    WGPUTextureView image_view,
    const progpu_native_scene_image_color_matrix& matrix,
    WGPUBuffer& uniform_buffer,
    WGPUBindGroup& bind_group) noexcept;

} // namespace progpu::native::semantic
