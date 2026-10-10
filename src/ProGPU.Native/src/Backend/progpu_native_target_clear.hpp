#pragma once

#include "progpu_native.h"
#include "progpu_native_semantic_budget.hpp"

// Internal provider-selected WebGPU handle contract, not a borrowed-view ABI.
struct progpu_native_engine;

namespace progpu::native::execution {

// Caller holds the engine lock and ends its current render pass first. The
// target and dimensions must identify the actual owned current attachment.
// This operation ignores source transforms and replaces only the supplied
// binary scissor. Its enclosing source layers retain all AA/group coverage.
// One real-encoder draw, no submit/wait/readback or unbounded geometry proxy.
// Uniforms use the existing submission-retired raster-resource ownership.
progpu_native_status encode_target_clear(progpu_native_engine& engine,
    WGPUTextureView target, std::uint32_t width, std::uint32_t height,
    const semantic::scissor& scissor, const progpu_native_color& straight_color,
    bool ignores_alpha);

} // namespace progpu::native::execution
