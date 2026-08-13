#pragma once

// Emdawnwebgpu exposes the modern WebGPU C API under this include path. The
// browser adapter also needs the global proc resolver declared even though the
// shared Dawn adapter normally asks a native host to provide that declaration.
#if defined(PROGPU_NATIVE_BROWSER)
#undef WGPU_SKIP_DECLARATIONS
#endif
#include <webgpu/webgpu.h>
