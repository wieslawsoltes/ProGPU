# Retained GPU submission optimization

Status: implemented and correctness-gated on managed and native C++ renderers,
2026-08-24

## Purpose

This work reduces stable-frame CPU and synchronization overhead without
weakening completion, resource lifetime, or pixel-correctness guarantees. It
combines three independent changes:

1. bounded retirement polling instead of polling after every submission;
2. compatible consecutive texture-draw batching;
3. retained WebGPU render-bundle replay for immutable compiled scenes.

The changes are renderer features rather than host-specific shortcuts. The
managed compositor, the Dawn and Silk.NET providers, and the native C++ engine
all have an explicit applicability and validation result.

## Invariants

- A reported completion wait still drains all work submitted before the wait.
- Deferred releases remain bounded even if the host never requests an explicit
  completion wait.
- A render bundle never outlives the exact pipelines, buffers, bind groups,
  textures, samplers, masks, or effect resources captured while recording it.
- Scene, target format, target extent, sample count, and DPI changes invalidate
  bundle replay before captured resources are released.
- Batching preserves draw order, blend semantics, sampling, texture identity,
  effect identity, clip/mask ownership, and page ownership.
- Unsupported or mutable scenes fail closed to ordinary render-pass encoding.
- Every managed optimization is either present in native C++ or has a concrete
  ownership reason proving that it is not applicable.

## Bounded retirement polling

The managed WebGPU context now performs non-blocking retirement polling every
eight queue submissions instead of after each submission. The deferred-release
queue remains capped at 64 submissions, so a burst that reaches the cap forces
progress rather than allowing unbounded resource retention. Explicit
completion waits continue to provide the strong drain boundary.

The native C++ engine uses the same interval and bound through its submission
tracker. Managed native interop no longer adds a second unconditional polling
layer around the C++ engine. Tests cover the interval boundary, the forced
retirement bound, explicit waits, teardown, and concurrent submission/lifetime
serialization.

An experiment that attempted to wait for an exact per-submission token was
reverted. The available provider contract did not establish a portable token
lifetime and ordering improvement over the existing explicit completion
boundary. Retaining the experiment would have increased contract complexity
without measured benefit.

## Compatible texture-draw batching

The managed compiler merges consecutive texture draws only when the complete
GPU state is compatible. The native scene compiler applies the equivalent
patch merge to ordinary and external-image records. Both paths preserve the
first vertex/index ownership floor used by incremental pages; this prevents a
later patch from incorrectly claiming storage owned by an earlier retained
page.

The native C++ scene builder implements the same semantic merge rather than
depending on managed preprocessing. Focused tests cover compatible merges,
state mismatches, external images, incremental-page ownership, draw order, and
pixel output.

## Retained render bundles

`IWebGpuRenderBundleApi` is an optional provider capability. When enabled, the
managed compositor records a strictly eligible immutable compiled scene once
and replays it with `RenderPassEncoderExecuteBundles` on subsequent frames.
Eligibility rejects work that requires mutable uploads, target-dependent
copies, generated masks, intermediate effects, or other commands whose
resources or ordering cannot be retained safely.

The compositor stores the bundle beside the compiled scene generation. During
invalidation and disposal, it releases the bundle before releasing any
resource that the bundle may reference. Metrics distinguish scene-cache hits,
bundle-cache hits, newly recorded bundles, and the draw count represented by a
bundle.

Provider coverage is:

| Provider/engine | Result | Reason |
|---|---|---|
| Silk.NET WebGPU | supported | Direct WebGPU render-bundle entry points |
| Dawn managed provider | supported | Descriptor translation and Dawn handle ownership are explicit |
| Native C++ engine | supported | Retained semantic spans already record and replay native render bundles |
| Browser command-packet provider | intentionally not applicable | The packet/event-loop boundary owns transient encoder commands and cannot retain a native encoder object across host frames |

The browser path therefore uses ordinary pass encoding. This is a documented
ownership mismatch, not an untracked managed/native parity gap.

## Managed/native C++ parity

| Optimization | Managed implementation | Native C++ implementation | Gate |
|---|---|---|---|
| Bounded retirement polling | interval 8, hard bound 64 | equivalent submission tracker and bound | interval, drain, teardown, concurrency tests |
| Texture batching | compatible consecutive draw merge | semantic compiler and pure C++ builder merge | state mismatch, external image, incremental ownership, pixels |
| Retained bundle replay | optional provider interface and scene-generation lifetime | retained semantic bundle spans and native WebGPU dispatch | cache hit/miss, invalidation, lifetime, native internal tests |
| Dawn provider adapter | managed ABI/descriptor translation | native engine already calls provider-resolved WebGPU bundle ABI | real Metal device replay and red-pixel readback |

## Evidence

The focused Dawn/Metal test creates a real device, records a retained scene on
the first frame, replays its bundle on the second frame, and verifies both
bundle metrics and output pixels. The complete managed renderer suite and the
headless pixel suite remain the release gates. Native C++ qualification builds
the library and consumers, runs internal CTest coverage, verifies the export
allowlist, and executes the pure C++ sample on the physical adapter.

A managed-host, forced-redraw Metal matrix used three alternating fresh-process
pairs and 15 representative workloads. Both lanes used the same Apple M3 Pro,
1280 by 720 BGRA8 GPU targets, explicit GPU-completion boundaries, stable
semantic hashes, zero unsupported operations, and final-target readback. The
optimized renderer won synchronized total latency and completed bounded-batch
time in all 15 workloads. Bundle-ineligible sparse, layer, clip, and effect
scenes also won, which prevents bundle replay from hiding a fallback
regression. Bundle-enabled output was byte-identical to the same renderer
revision before bundle support.

A matched Metal System Trace exposed a tradeoff worth optimizing next. The
managed WebGPU provider completed the bounded shadow workload faster, but its
whole-process trace contained more Metal command-buffer and resource-allocation
events than the direct reference lane. Startup-inclusive peak Metal allocated
size was approximately 25.4 MiB versus 7.84 MiB. These are process-level
Instruments counters, not steady-frame renderer residency, but they identify
provider-side command-buffer/resource reuse as the next evidence-backed area.

## Primary references

- [WebGPU render bundles and queue completion](https://www.w3.org/TR/webgpu/)
- [Apple Metal command structure](https://developer.apple.com/documentation/metal/setting-up-a-command-structure)
- [Metal command-buffer completion wait](https://developer.apple.com/documentation/metal/mtlcommandbuffer/waituntilcompleted%28%29)
- [Skia `GrDirectContext` submission API](https://api.skia.org/classGrDirectContext.html)

These sources define the public synchronization and command-recording
contracts. The implementation and measurements above are independent
engineering conclusions derived from those contracts and local runtime
evidence.
