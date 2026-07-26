# ProGPU voxel ray tracing and WGSL effects

## Scope

This change reuses ProGPU's WebGPU device, texture, buffer, pipeline-cache, retained-scene,
and compositor-extension APIs. It does not add a second graphics API and it does not add
platform-specific rendering code to WinUI. The reusable contracts live in
`ProGPU.Scene`; `VoxelGameView` only owns game state and UI toggles.

The implementation has three GPU layers:

1. Greedy-mesh rasterization, with caller-selected WGSL vertex/material hooks.
2. Portable voxel ray tracing, using a bounded uniform-grid DDA in a fragment shader.
3. Neutral image effects, using a WGSL function that can be applied by a
   `DrawingContext` or retained `Visual.Effect`.

## Primary-source research

This is a clean-room implementation. No engine source was ported or translated.

- The [WGSL specification](https://www.w3.org/TR/WGSL/) defines the uniform and
  read-only storage-buffer contracts used here. It also makes potentially non-terminating
  loops a dynamic error, so the ray traversal has a compile-time bound of 512 iterations
  and a lower runtime quality bound.
- Amanatides and Woo, [A Fast Voxel Traversal Algorithm for Ray
  Tracing](https://physique.cmaisonneuve.qc.ca/svezina/projet/ray_tracer/download/A_Fast_Voxel_Traversal_Algorythm_For_Ray_Tracing.pdf),
  describes incremental grid traversal with a small fixed update per crossed cell. ProGPU
  adopts the incremental side-distance idea, but implements its own signed-bounds,
  material, fog, selection, and WebGPU storage layout.
- [Skia runtime effects](https://docs.skia.org/docs/user/sksl/) contribute shader
  functions to a larger renderer rather than replacing clipping, blending, and color
  management. ProGPU adopts that function-module boundary:
  `progpu_effect_main` is wrapped by the compositor and cannot replace its render pass.
- [Direct2D custom effects](https://learn.microsoft.com/en-us/windows/win32/direct2d/custom-effects)
  separate an effect's properties, transform graph, shader, and cached shader identity.
  ProGPU adapts this as an immutable definition plus mutable parameters and a stable cache
  key. Reflection-based property discovery and COM registration are rejected.
- [Win2D custom effects](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/custom-effects)
  model effects as composable images with device-bound realizations and explicit
  invalidation. ProGPU keeps inputs as GPU textures, reuses the existing device lifecycle,
  and retains the compositor's premultiplied-alpha contract.
- [WebRender's rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  describes a picture tree lowered to render-task dependencies and reused render targets.
  ProGPU adapts this by running voxel output before the existing compositor texture draw
  and fusing rain and camera-motion blur into one pass. A general render-graph rewrite was
  rejected for this scoped change.
- [Vello](https://github.com/linebender/vello) emphasizes GPU-first processing,
  reusable scenes, and avoiding unnecessary intermediate textures. ProGPU keeps the
  raster arena versioned, uploads the ray volume only when its content version changes,
  and fuses the example post effects.
- NVIDIA's [post-process motion-blur chapter](https://developer.nvidia.com/gpugems/gpugems3/part-iv-image-effects/chapter-27-motion-blur-post-processing-effect)
  reconstructs per-pixel velocity from depth and prior transforms. The initial ProGPU
  example adopts bounded screen-space gathers but deliberately limits them to camera
  angular velocity. Depth-reconstructed object blur is deferred because it needs an
  additional sampleable depth contract and previous-object transforms.
- [DirectWrite and Direct2D](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
  keep reusable text layout/glyph positions independent of the renderer, and
  [Skia's shaping design](https://docs.skia.org/docs/dev/design/text_shaper/) similarly
  separates shaping from drawing. The new image effect runs after retained content has
  rendered and never reshapes or relayouts text.
- [HarfBuzz](https://github.com/harfbuzz/harfbuzz) remains ProGPU's shaping boundary.
  Moving shaping or line layout into these shaders was reviewed and rejected.

## Public WGSL image-effect contract

`WgslEffectDefinition` is immutable and supplies a stable key and a WGSL module.
`WgslEffectParameters` supplies a source texture, bounds, 32 `vec4` constants, and up to
16 optional sampled textures. The module implements:

```wgsl
fn progpu_effect_main(input: ProGpuEffectInput) -> vec4<f32>
```

The module may call:

- `progpu_constant(index)` for a `vec4<f32>` constant;
- `progpu_sample_source(uv)` for the primary input;
- `progpu_sample(binding, uv)` for an auxiliary input.

The compositor owns the vertex stage, target format, clipping/mask, blend mode, source
alpha conversion, pipeline verification, failure state, and device resources. Fixed
prefixes and wrappers are embedded `.wgsl` resources; only the caller's structurally
dynamic function module is composed at runtime.

`DrawingContext.DrawWgslEffect` applies an effect to a texture command. `WgslEffect`
implements `EffectBase`, so the same definition can be assigned to any retained visual.
The internal adapter reuses the mature multi-sampler compositor pipeline, but no public
neutral API exposes WPF types or naming.

## Public per-voxel material contract

`VoxelMaterialEffectDefinition` supplies two WGSL functions:

```wgsl
fn progpu_voxel_deform(input: ProGpuVoxelMaterialInput) -> vec3<f32>
fn progpu_voxel_shade(
    input: ProGpuVoxelMaterialInput,
    baseColor: vec3<f32>) -> vec3<f32>
```

The raster pipeline composes those functions with its fixed terrain module and caches the
result by definition key, source hash, and sample count. Deformation therefore happens
per generated voxel-surface vertex, while shading happens per visible fragment. The
built-in dynamic-environment module animates water and foliage and applies rain wetness
and time-of-day tint. Custom hooks must preserve shared face ownership well enough for
their desired silhouette; large independent displacement can expose cracks between
greedy quads.

## Ray-tracing representation

The example ray renderer uses a dense, bounded volume with X as the fastest axis, then Z,
then Y. A radius-three generated world currently requires roughly 1–3 MiB, depending on
its occupied vertical chunk range. The buffer is uploaded once and updated only after
`ContentVersion` changes. Mining and placement patch one CPU cell and advance that
version.

Each pixel first intersects the volume AABB and then traces at most
`RayTracingMaxSteps` in-volume cells, clamped to 512. Rays that miss or leave the world
terminate without marching the remaining fog distance. The implementation does not
claim hardware acceleration-structure ray tracing: WebGPU/WGSL exposes ordinary
render/compute shader resources, so this portable path is shader grid traversal. Raster
mode remains the default and fallback.

## Weather presentation

The weather effect reconstructs a camera ray per fragment and evaluates nine
logarithmically spaced precipitation planes. Drops are seeded from world coordinates,
projected along gravity, moved by wind, and narrowed with distance-aware footprints.
That keeps the pattern attached to the environment while preserving a fixed cost.
Two sparse lens cells contribute refraction and tiny highlights only; visible circular
rim overlays were rejected after browser image review because they read as a UI filter
instead of water on glass. A restrained wet darkening, blue-grey luminance grade, and
horizon mist make rain affect the environment rather than merely drawing white lines.

Rain is currently composited against color without a sampleable terrain depth input.
Consequently it cannot yet be occluded by individual roof voxels. A future depth-aware
quality tier must use an explicit linear-depth attachment rather than sampling the
multisampled `Depth24Plus` render target through an incompatible float-texture binding.

## Performance and quality contract

| Path | GPU work | Persistent GPU storage | CPU hot-path behavior |
| --- | --- | --- | --- |
| Raster | O(V + F), fixed material hook | versioned vertex/index arenas | no geometry upload for stable visible layout |
| Ray trace | O(P × min(D, 512)) after O(1) volume intersection | dense voxel volume | uniform write; volume upload only on version change |
| Weather post effect | 1 or 7 texture samples + 9 rain and 2 sparse lens layers per pixel | existing source texture | fixed 32-float update, no readback |
| Retained custom effect | user-defined per fragment | compositor effect textures and cached pipeline | cache key/source validation; no runtime reflection |

The default remains greedy-mesh rasterization because the ray path's cost scales with
both output pixels and crossed cells. Rain and motion blur are independently toggleable,
but share one pass when either is active. Ray tracing disables MSAA because it produces
one analytic sample per pixel.

Performance claims require Release/AOT measurements from the final binaries. Record the
device, resolution, mode, warmup, workload, frame-time distribution, memory residency,
and captured profiler artifacts; do not compare a debug or JIT build with an AOT build.

### Final Release NativeAOT measurements

Measured on a MacBook Pro (Mac15,6), Apple M3 Pro, 18 GB, macOS 26.4.1, arm64, with the
1280 × 800 sample window, VSync disabled, 180 warmup frames, and 600 measured frames.
The executable was the final `osx-arm64` NativeAOT artifact.

| Mode | Delta FPS | Average frame | Maximum frame | CPU compositor | Allocation/frame | Physical footprint |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Raster, custom material disabled | 231.93 | 4.2840 ms | 17.4065 ms | 0.6100 ms | 1,080 B | 344.7 MiB |
| Ray trace + dynamic material + rain + motion enabled | 167.92 | 5.9268 ms | 27.4386 ms | 0.6444 ms | 1,640 B | 339.0 MiB |

The enhanced path completed the measured interval with zero collections in every GC
generation and a 6.6 KiB managed-heap delta. The later profiler-instrumented runs
measured 169.09–177.34 delta FPS. Instruments Activity Monitor observed a final physical
footprint of 345.69 MiB; its variation is consistent with the app benchmark's
339–355 MiB process-footprint samples.

The final Time Profiler trace contains no repeated WGSL-source fingerprinting after the
source hash was cached. Steady running samples were led by OS wait/worker entry points;
the highest named ProGPU leaf in the top sample group was text feature hashing, while
command submission appeared below it. This supports keeping the raster arena, ray-volume
upload, and WGSL source immutable/versioned rather than rebuilding them per frame.

Profiler artifacts:

- `artifacts/voxel-vfx-final-time-profiler.trace`
- `artifacts/voxel-vfx-final-activity-monitor.trace`
- `artifacts/voxel-vfx-final-time-profile.xml`
- `artifacts/voxel-vfx-final-activity-live.xml`

## Current limitations and follow-up gates

- The example motion blur is camera-motion blur, not depth-reconstructed per-object blur.
- The dense ray volume is appropriate for the sample world's bounded dimensions. Larger
  worlds should use a chunk directory or sparse brick pool after profiling establishes
  the crossover point.
- The ray shader currently traces primary visibility only. Bounded shadow/AO rays should
  be added as separately budgeted quality tiers, not hidden in the base path.
- User WGSL is trusted application code. Compilation failures are contained and exposed
  through `IsFailed`/`LastError`, but execution cost is controlled by the module author.
