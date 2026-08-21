#pragma once

#include <webgpu/webgpu.h>

#include <cstdint>

namespace progpu::native::browser {

using evidence_completion = void (*)(bool success);

bool create_evidence_target(
    WGPUDevice device,
    WGPUTextureFormat format,
    std::uint32_t width,
    std::uint32_t height,
    WGPUTexture* texture,
    WGPUTextureView* view);

bool begin_evidence_readback(
    WGPUDevice device,
    WGPUQueue queue,
    WGPUTexture source_texture,
    std::uint32_t width,
    std::uint32_t height,
    evidence_completion completion);

} // namespace progpu::native::browser
