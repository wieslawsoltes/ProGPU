#include "progpu_native.h"

#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#include <wgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#include "progpu_native_dawn.h"
#endif

#include "progpu_webgpu_compat.hpp"
#include "progpu_native_engine.hpp"
#include "progpu_native_gpu_records.hpp"
#include "progpu_native_semantic_image_resources.hpp"

#include <array>
#include <cstring>

namespace progpu::native::semantic {

bool create_semantic_image_color_matrix_resources(
    progpu_native_engine& engine,
    WGPUTextureView image_view,
    const progpu_native_scene_image_color_matrix& matrix,
    WGPUBuffer& uniform_buffer,
    WGPUBindGroup& bind_group) noexcept {
    uniform_buffer = nullptr;
    bind_group = nullptr;
    if (image_view == nullptr || engine.image_mask_layout == nullptr ||
        engine.image_linear_sampler == nullptr) {
        return false;
    }

    gpu_mask_sampling_uniforms uniforms{};
    std::memcpy(uniforms.coordinate0, matrix.red, sizeof(matrix.red));
    std::memcpy(uniforms.coordinate1, matrix.green, sizeof(matrix.green));
    std::memcpy(uniforms.bounds, matrix.blue, sizeof(matrix.blue));
    std::memcpy(
        uniforms.corner_radii_x,
        matrix.alpha,
        sizeof(matrix.alpha));
    std::memcpy(
        uniforms.corner_radii_y,
        matrix.offset,
        sizeof(matrix.offset));

    WGPUBufferDescriptor buffer_descriptor{};
    buffer_descriptor.label = webgpu::string_view(
        "ProGPU semantic image color-matrix uniforms");
    buffer_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    buffer_descriptor.size = sizeof(uniforms);
    uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &buffer_descriptor);
    if (uniform_buffer == nullptr) {
        return false;
    }
    wgpuQueueWriteBuffer(
        engine.queue,
        uniform_buffer,
        0U,
        &uniforms,
        sizeof(uniforms));

    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.image_linear_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, image_view},
        {nullptr, 2U, uniform_buffer, 0U,
            sizeof(uniforms), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = webgpu::string_view(
        "ProGPU semantic image color-matrix bind group");
    descriptor.layout = engine.image_mask_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    bind_group = wgpuDeviceCreateBindGroup(engine.device, &descriptor);
    if (bind_group == nullptr) {
        wgpuBufferDestroy(uniform_buffer);
        wgpuBufferRelease(uniform_buffer);
        uniform_buffer = nullptr;
        return false;
    }
    return true;
}

} // namespace progpu::native::semantic
