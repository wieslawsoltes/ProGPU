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
// and pipeline state while WebGPU handles remain reference-counted.
progpu_native_status create_child_engine(
    const progpu_native_engine& parent,
    WGPUTextureFormat target_format,
    progpu_native_engine** child);

} // namespace progpu::native::execution
