#pragma once

#include "progpu_native.h"

#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#endif

struct progpu_native_engine;

namespace progpu::native::execution {

// Creates one owner-thread-affine engine over the parent's retained device,
// queue, instance, and exact WebGPU dispatch. The child owns independent scene
// state. It borrows the parent's immutable base pipeline and any already prepared
// analytic pipeline/layouts, and must be destroyed before that owning parent.
// Analytic buffers, bind groups and readiness remain independent and lazy.
progpu_native_status create_child_engine(
    const progpu_native_engine& parent,
    WGPUTextureFormat target_format,
    progpu_native_engine** child);

} // namespace progpu::native::execution
