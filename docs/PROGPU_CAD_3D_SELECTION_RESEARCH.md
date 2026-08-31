# ProGPU.CAD Projected 3D Selection Research

## Scope and clean-room provenance

This checkpoint adds whole-entity click selection for the retained Flat
Mesh3D view. It unprojects one logical viewport position through the exact
camera matrices already submitted by the managed and native adapters, finds
the nearest visible retained triangle, and returns its ACadSharp semantic root
handle. It does not add face/edge/vertex subobject editing, selection cycling,
marquee/frustum selection, hidden-line selection, or ACIS payload
tessellation.

The implementation is original ProGPU code. No third-party implementation
text, type layout, naming, control flow, lookup-table encoding, or source
organization was copied. Approved in-repository behavioral provenance is:

- `src/ProGPU.CAD/CadMesh3DSceneCompiler.cs`, which owns the immutable
  float-rebased triangles and semantic batch handles;
- `src/ProGPU.CAD/CadMesh3DViewport.cs`, which owns the double-WCS camera and
  exact managed/native projection contract;
- `src/ProGPU.CAD/CadSelection.cs`, whose existing plan picker establishes the
  broad-phase-then-exact-test and semantic-root selection policy; and
- `src/ProGPU.CAD.Sample/CadSampleCanvas.cs`, whose selected-handle collection
  remains the single shared desktop/browser edit selection.

## Primary behavior and algorithm sources

- Autodesk's [multiple-object selection contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-531FB60D-833B-4813-927A-42275CF6777D.htm)
  establishes click selection, selection sets, and Window/Crossing behavior.
  Autodesk's [3D subobject selection and cycling contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-89EFC58E-D14E-4B62-87D3-A6E26146D85E.htm)
  establishes that the foreground face is detected first and hidden
  alternatives require an explicit cycling workflow. ProGPU therefore returns
  the nearest whole semantic entity in this slice and records cycling and
  subobjects as separate gates.
- The [WebGPU coordinate-system specification](https://gpuweb.github.io/gpuweb/#coordinate-systems)
  defines top-left framebuffer coordinates, X right, Y down, NDC X/Y in
  `[-1, 1]`, and NDC depth in `[0, 1]`. The picker uses those exact endpoints
  and the inverse of the same `view * projection` matrices used for rendering.
- Microsoft's [DirectXMath triangle-test contract](https://learn.microsoft.com/en-us/windows/win32/dxmath/ovw-xnamath-triangletests)
  identifies ray/triangle intersection as the exact triangle primitive. The
  ProGPU implementation independently uses a double-intermediate,
  two-sided barycentric solve over retained float-local triangle coordinates.
- wgpu's experimental [ray-tracing API specification](https://github.com/gfx-rs/wgpu/blob/trunk/docs/api-specs/ray_tracing.md)
  requires opt-in ray-query extensions and acceleration structures whose
  triangle input is copied into backend-owned structures. It is not WebGPU
  core and is not uniformly available to current desktop/mobile/browser
  targets. ProGPU therefore rejects it for the portable baseline while keeping
  the immutable index replaceable by a future measured GPU implementation.

## Required cross-engine architecture review

| Engine | Primary source examined | Decision for this checkpoint |
|---|---|---|
| Skia / SkParagraph | [SkCanvas quick rejection](https://skia.googlesource.com/skia/+/refs/heads/main/src/core/SkCanvas.cpp), [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/) | Skia conservatively rejects by transformed bounds before exact drawing, while shaping is reusable state. Adapted: reject BVH nodes by ray bounds before exact triangles. Existing CAD glyph runs are neither rebuilt nor made 3D-pickable here. |
| Direct2D / DirectWrite / Win2D | [Direct2D `StrokeContainsPoint`](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1geometry-strokecontainspoint), [DirectWrite/Direct2D separation](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite), [Win2D retained text layout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm) | Direct2D separates transformed exact containment from conservative geometry bounds, and DirectWrite/Win2D retain positioned text. Adapted: exact surface testing follows immutable bounds traversal. Text layout, fallback, and device resources are unaffected. |
| WebRender | [rendering pipeline and spatial trees](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst), [display-list hit-test contract](https://searchfox.org/mozilla-central/source/layout/painting/nsDisplayList.h) | WebRender/Gecko retain semantic hit-test information separately from GPU pixels and apply bounds/clips before exact item behavior. Adapted: semantic batch handles remain next to retained triangles; rejected: framebuffer readback as the source of object identity. |
| Vello / Parley | [Vello retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md), [current Vello scene API](https://github.com/linebender/vello/blob/main/vello/src/scene.rs), [Parley text stack](https://github.com/linebender/parley) | Vello separates immutable scene encoding from late transforms; Parley retains Unicode analysis, shaping, fallback, and layout. Adopted: the index belongs to a geometry generation while camera rays are late state. No text cache or layout changes apply. |
| HarfBuzz | [shape plans and caching](https://harfbuzz.github.io/shaping-plans-and-caching.html), [buffer contract](https://harfbuzz.github.io/harfbuzz-hb-buffer.html) | HarfBuzz maps Unicode buffers to positioned glyphs and permits cached plans; it has no 3D surface-selection role. Existing font fallback, variable-font state, glyph caches, and device-loss invalidation remain unchanged. |
| WebGPU | [coordinate systems](https://gpuweb.github.io/gpuweb/#coordinate-systems), [device-loss contract](https://gpuweb.github.io/gpuweb/#dom-gpudevice-lost) | Core WebGPU supplies raster/depth semantics but no portable object-identity query. Adopted: match clip coordinates on the CPU and keep the index device-independent. A WebGPU device replacement rehydrates render resources but does not invalidate a matching CPU geometry generation. |

The broader required concerns are unchanged: index preparation is bounded CPU
work attached to the already-compiled immutable Mesh3D generation and does not
initialize WebGPU; shaping and line layout results are reused; retained plan
and mesh scenes remain independent;
visibility is already resolved before Mesh3D batches; geometry cache keys and
device eviction are unchanged; no upload is demanded by a query; worker-thread
index preparation remains possible because inputs and outputs are immutable;
GPU batching and camera uniform replay are unchanged; DPI affects only the
logical viewport-to-framebuffer mapping already owned by the host; font
fallback and variable-font state do not participate; and device loss replaces
GPU objects without rebuilding the CPU index.

## Adopted retained accelerator and query contract

For `T` retained triangles, the index computes each finite local triangle AABB
and centroid, quantizes centroids into a 30-bit Morton key (ten bits per axis),
sorts once by `(Morton, batch, triangle)`, and builds a balanced binary AABB
tree whose leaves contain at most eight contiguous references. Equal Morton
keys retain deterministic semantic order. Build time is `O(T log T)`, retained
storage is `O(T)`, and depth is `O(log T)` because the sorted interval is split
at its midpoint regardless of key distribution.

A query validates the indexed scene generation and matching viewport rebase,
unprojects NDC depth zero and one, and traverses nearer child bounds first. It
tests the exact indexed triangles with double intermediates, accepts both face
orientations because the shared viewer supplies a back material, and retains
the closest clipped hit. Equal-depth hits resolve by batch then triangle order.
Typical work is `O(log T + H)` for `H` candidate triangles; the conservative
worst case is `O(T)`. Query storage is a fixed stack bounded above the maximum
balanced-tree depth and performs zero managed allocation after index creation.

The result carries content generation, semantic handle, batch and triangle
indices, WCS hit point, camera distance, barycentrics, front-face state, and
visited-node/tested-triangle counters. Empty or out-of-viewport clicks return a
typed miss. Invalid matrices, dimensions, generations, indices, or finite
contracts fail before traversal rather than silently selecting approximate
geometry.

## Managed/native and interaction applicability

The managed and native renderers consume the same `CadRecordedMesh3DScene`,
semantic handles, indices, positions, rebase origin, and camera matrices. This
checkpoint adds no shader, public C record, C# wire declaration, native handle,
or managed/native crossing. A native host can call the same CPU picker before
compiling or submitting its native scene, so a duplicate C++ implementation
would create semantic drift without reducing a crossing. Native image parity
and stable zero-upload camera replay remain unchanged.

The shared sample shell recognizes a stationary non-Shift left click after
the generic viewport's drag threshold. A hit replaces the current semantic
selection; an empty click clears it. Orbit and Shift/right/middle pan retain
their existing behavior and never commit selection. Selected Mesh3D batches
use one dynamic `SystemAccentColor` theme brush while retaining their authored
geometry and restoring their authored material on deselection. Selection-only
material invalidation may rebuild bounded viewport records, but it never
recompiles the ACadSharp snapshot or Mesh3D topology.

## Verification and remaining gates

Required regressions cover frontmost ordering, two-sided triangles, misses,
near/far clipping, deterministic shared-edge ties, large-WCS rebasing,
generation/rebase validation, dense-scene pruning, zero-allocation warm
queries, coordinator replacement, click-versus-drag interaction, selection
clearing, semantic handle continuity, theme-dynamic highlighting, and retained
camera/upload counters. The SHA-identified Release 256-by-256 grid lane contains
131,072 triangles. Its 2,359,256-byte, depth-15 index built at
23.2322/41.6313/41.6313 ms p50/p95/p99. Across 65,536 exact queries it visited
15 nodes, tested eight triangles, used zero managed bytes, and measured
1.5/9.1/19.4 microseconds p50/p95/p99. The checked-in JSON is
`artifacts/benchmarks/cad-3d-selection-grid-256.json`.
There is no matched pre-change selection latency because the prior Flat 3D
viewer had no projected query path; these figures are an acceptance baseline,
not a claimed before/after speedup.

Matched macOS Allocations, Time Profiler, and Metal System Trace captures use a
larger 524,288-triangle workload from the same final binaries. Allocations
reported 19,788,704 persistent heap-plus-anonymous-VM bytes and 60,382,960
total bytes while repeatedly constructing the bounded index. Metal observed no
target resource allocation, current allocated size, application command
submission, drawable wait, compiler spill, hang, or error, confirming that a
query neither initializes WebGPU nor retains GPU state. Raw traces were removed
after compact exports; the manifest, notes, tables, and summary remain under
`artifacts/benchmarks/cad-3d-selection-instruments/`.

Still required for full 3D selection fidelity are configurable pick aperture,
Window/Crossing frustum selection, transparent/hidden-line policy, selection
cycling, face/edge/vertex subobjects, ACIS analytic topology, material/texture
alpha semantics, arbitrary non-Mesh3D projected entity selection, matched
managed/native rendered highlight images, browser interaction/performance
smoke, and licensed AutoCAD differentials.
