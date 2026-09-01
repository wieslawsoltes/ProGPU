# CAD GPU driver/resource metrics research

## Scope and contracts

The CAD renderer now exposes an explicit diagnostic capture for the complete
native WebGPU backend instance. It reports native registry slot counts and their
control-storage footprint separately from renderer-owned logical buffer and
texture bytes. On supported macOS Metal configurations it also reports the
system-default Metal device's physical allocation and carries an independent
availability bit, so an unsupported query cannot be mistaken for a real zero.

Capture is a bounded diagnostic seam that locks the owning context and invokes
native reporting. It is intentionally excluded from render, scene-update,
upload, editing, and submission hot paths. It neither claims CAD-exclusive
ownership nor treats native registry element sizes as GPU payload residency.

## Primary sources and design decisions

- [Apple `MTLDevice.currentAllocatedSize`](https://developer.apple.com/documentation/metal/mtldevice/currentallocatedsize)
  defines the value as the total bytes used by the GPU device for all resources.
  ProGPU adopts that complete-device scope and rejects presenting it as CAD-only
  memory.
- [wgpu-native `WGPURegistryReport`](https://github.com/gfx-rs/wgpu-native/blob/trunk/ffi/wgpu.h)
  exposes allocated handle slots and element size. ProGPU adapts those values
  into saturating native registry-control bytes and rejects interpreting them as
  buffer/texture payload allocation.
- Skia tracing, Direct2D/DirectWrite and Win2D diagnostics, WebRender's profiler,
  Vello/Parley, and HarfBuzz were rechecked in the earlier plan-frame metrics
  research. None changes the backend ownership boundary here: text layout and
  retained scene work remain reusable CPU state, while driver allocation is an
  infrequent whole-device diagnostic.

## Managed/native applicability

The managed CAD picture and optional native renderer share the same WebGPU
context and therefore the same backend-instance/device diagnostic scope. No C
ABI, shader, C++ frontend, scene semantics, resource lifetime, or draw behavior
changes. The new projection is managed diagnostic API only; native rendering
continues to use the existing shared context counters and benchmark capture.

Cross-platform physical allocation remains explicitly unavailable where the
backend or platform exposes no equivalent authoritative query.
