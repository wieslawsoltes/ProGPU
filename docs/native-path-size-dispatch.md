# Native path dispatch sizes

Acceptance application: the original Windows Gallery Clipboard page. Copy Image,
Paste Image and the two original desktop captures must finish within the unchanged
60-second controller budget (57 seconds in the application). The first native
frame and pasted scene contain 61 independent path rasters. The former native
batch launches every Z slice at the maximum 14 by 45 workgroups. The isolated
original Windows replay completes both images, but each submission takes about
11.5 seconds to complete on Microsoft Basic Render Driver. That observation does
not attribute all GPU time to paths or qualify the original window.

## Paired implementation and ownership

The implementation source is the original size-grouping policy in
`ProGPU.Vector/PathAtlas.cs` at `5853d27635ccf50f261b8451716750121633e371`.
Managed path rasterization already groups equal workgroup extents. Both native
providers now apply that policy to independent operation-zero paths and retained
clips. Each original 48-byte shader uniform is copied intact into an aligned
group. Record/segment indices, output offsets/strides, scale, phase, sample grid,
atlas placement and source ownership remain unchanged. Original shaders and lazy
pipeline selection are unchanged; no raster algorithm is copied from elsewhere.

Each binding begins at a 256-byte offset and exposes only its group's actual
uniform extent. Groups retain the original workgroup size and split at the
portable 65,535 Z limit. Additional bind groups remain in the existing native
submission-retirement lease. One original compute pass and encoder/submission sequence contain
the size batches; there is no per-path submission, readback or completion wait.
Signed/Boolean/staged batches keep their original ordering and descriptors.
Clip pass counts stay unchanged; upload counters include the actual padding.

Preparation is O(N log N) comparison work and O(N) temporary storage for N new
rasters. GPU raster work and output pixels are unchanged, while empty invocations
outside each smaller raster disappear. Padding is at most 255 bytes per group.
Retained warm frames do not sort, allocate or upload this preparation again.
No source application, deadline, compiler/adapter policy, API, package identity,
glyph shaping, image, sampling, DPI or input contract changes.

## Research boundary

The existing [cross-engine research](single-path-raster-pipeline.md#architecture-research)
was rechecked against its primary sources on 14 October 2026: Skia/SkParagraph,
Direct2D/DirectWrite, Win2D, WebRender, Vello/Parley and HarfBuzz. Adopt batching
and device-owned resources, preserving the separation of CPU layout and GPU
painting. Reject extra flushes, CPU raster fallback and changes to scene/shaping
identity. Startup remains lazy; retained scenes, visibility, glyph/path/texture
cache keys and eviction, demand uploads, worker preparation, device loss,
subpixel/hinting, fallback/variable fonts and scrolling invalidation keep their
existing contracts. Only the dimensions of independent GPU dispatches change.

## Qualification

The uniform transport test verifies every byte and original occurrence, full
group coverage, alignment/padding, reduced work, a later rejected operation and
the Z boundary. This is not GPU or application qualification. Exact original
scene replays, both providers' native pixel/clip gates, full producer CI,
matched performance/profiling and original Gallery acceptance remain required.
Do not claim that smaller dispatches resolve the deadline until measured.

Local Apple M3 Pro evidence: both providers compile and all 48 configured native
CTest entries pass. The added independent rectangle-membership test checks all
channels at DPI 1/2 through varied path sizes and nested clips, cold and warm.
The original Boolean/mapping cases remain unchanged. A new test initially hit
the helper's old target-size inventory; the helper now also admits its new
256/512 targets while retaining every original target and assertion.

Fixed ABBA replays of both unchanged captured Gallery scenes produce exactly
the same complete RGB bytes. Frame 1 dispatches 2,916 path workgroups instead
of 38,430; frame 2 dispatches 2,917 instead of 39,060. This measures reduced
overdispatch, not Windows speed or original Gallery acceptance. The original
submissions remain 2/3 and draws 59/61. Process/driver cache and OS scheduling
affect the observed timings; final matched profiling and Windows execution
remain outstanding.
