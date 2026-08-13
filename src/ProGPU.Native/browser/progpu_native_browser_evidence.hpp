#pragma once

#include <webgpu/webgpu.h>

#include <cstdint>

namespace progpu::native::browser {

using evidence_completion = void (*)(bool success);

bool begin_evidence_readback(
    WGPUDevice device,
    WGPUQueue queue,
    WGPUTexture texture,
    std::uint32_t width,
    std::uint32_t height,
    evidence_completion completion);

} // namespace progpu::native::browser
