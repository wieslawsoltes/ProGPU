# Original source-frame and raw-cache capture port

This implementation-only increment connects original source compilation to actual
GPU captures. It is unvalidated. No compiler, test, lint, shader, browser or
performance execution was run. Source-provider, application and package gates
remain independent and mandatory.

| Slice | Ported contract |
| --- | --- |
| SourceRenderFrame | Independent physical allocation, exact floating projection extent, integer CanvasSize, semantic DPI, compile-space placement and source ClearType suppression |
| Implicit input | Original physical-origin versus legacy branches; negative original RasterBounds/PaddedBounds root translation before geometry planning; original positive-Z projection |
| Raw-cache input | Original complete SourceToRaster matrix, pixel-sized projection and semantic DPI one; actual live device caps even for proven empty input |
| Scene placement | Original source commands placed before path, stroke, text and mask planning; immutable source/resource ownership and recursive child ordering |
| Exact edges | Private near/far witnesses for binary clips, area masks and child bounds avoid an XYWH round trip; ordinary public recording APIs retain their original route |
| Capture lifetime | Fresh output for each explicit request; CapturedSourceScene retains original Arc<Scene>, exact frame, actual texture and preparation/replay metrics |
| CacheSamplerRaster | Independently retained source identity, nonzero revision, complete frame, actual device and ClearType policy; exact reuse comparison and fresh capture publication |
| Matrix work | Independent Matrix4 result columns use f32x4 while preserving each lane's four original products and left-associated additions |

## Entry points

`SourceRenderFrame::effect` consumes an original `ManagedCaptureFrame`;
`SourceRenderFrame::cache` consumes the actual `CacheSamplerRasterFrame`.
`Renderer::prepare_source_scene` applies source placement before raster planning;
`capture_source_scene` owns the original recording through the completed capture.
`capture_cache_sampler` adds original nonzero source revision and explicit identity
without inferring root visual properties from a flat recording. The caller supplies
actual cache content after applying the source root policy; descendant commands
remain intact. No root opacity, transform or scrollable-clip removal is guessed.

Prepared scenes and captures retain the complete source frame in addition to
ordinary physical Frame bookkeeping. Bounded child uniforms remap the original
root projection; local fullscreen storage-copy passes retain their own pixel-copy
frame. Existing semantic-DPI mask/scissor arithmetic remains distinct from the
implicit input projection, following the original managed source. Native source-AA
and full source visual transport retain their independent admission obligations.

Rust ownership adapts managed retained leases to Arc and actual wgpu handles.
Dropping these owners neither flushes/polls/submits nor explicitly destroys a live
shared texture. These public GPU handles still require the caller to respect the
completed-generation contract: a borrowed texture reference is not permission to
mutate or destroy the retained output. Browser thread affinity follows wgpu's
actual types; Arc is not advertised as a cross-thread GPU capability.

## Remaining scope

Frozen final-target shader commands, already realized glyph/AA ranges and inherited
physical clips cannot be reinterpreted as new source commands. They reject during
placement before preparation. The following increment must retain original owned
shader recipes and realize their parameters/samplers only for the actual nested
target, including recursion guards, atomic cache publication/rollback and completed
frame eviction. Full source visual/opacity policy, invalidation/archives/providers,
late camera behavior and all source/runtime/browser/lifetime/performance gates
remain open. This capture foundation does not qualify those contracts.

## Original source provenance

| Original repository source | SHA-256 |
| --- | --- |
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `src/ProGPU.Scene/Compositor.CacheSamplerRaster.cs` | `17b6c4f4a0627ef8761822c03742d0e47f2910fb4401481cbc6afde1bf1990ae` |
| `src/ProGPU.Scene/CacheSamplerRaster.cs` | `588545145814b609ad92781a097f38f7250b84732f69313d8e66695aed53e29c` |
| `src/ProGPU.Scene/CacheSamplerRasterFrame.cs` | `f0dc3f68aab17f17e5bd01f3cd0bbabb4a70901d8e5e4495f8357de5f42d7362` |
| `src/ProGPU.Scene/EffectCaptureFrame.cs` | `557f747e6772917fdbf34c06b838f430734a71e86839e50a03de90c248c5e017` |
| `src/ProGPU.Scene/OwnedShaderEffectSource.cs` | `5ee016cdd04720947b76e5d11eea354978d4653c36bdf4d0fc0f54a63df1918b` |
| `src/ProGPU.Scene/Compositor.OwnedShaderEffects.cs` | `da0c767b6ed25be653888435acb0751499da6282153b25bf9fded30cba77cc71` |

Applicable scopes: `RenderOffscreenCore`, `PrepareAndDrawEffect`,
`CreateMaskRenderUniforms`, `GetBoundedMaskPixelBounds`, explicit raw-cache capture,
original cache generation ownership and source-frame derivation. Owned shader
source/target files are recorded as the next integration boundary, not claimed as
fully connected by this increment. No foreign implementation was used.
