# ProGPU.CAD Projected 3D Selection Research

## Scope and clean-room provenance

This checkpoint adds whole-entity click and rectangular region selection for
the retained Flat Mesh3D view. Point selection unprojects one logical viewport
position through the exact
camera matrices already submitted by the managed and native adapters, finds
the nearest visible retained triangle, and returns its ACadSharp semantic root
handle. A caller-buffered companion query returns bounded nearest-first unique
semantic roots, and repeated Alt-clicks cycle that depth order. Exact projected
Window/Crossing queries use the same retained triangles and camera clip volume.
Point and depth queries preserve an exact center-ray hit first, then use a
configurable projected pick target when that ray misses.
It does not add face/edge/vertex subobject editing, lasso/polygon selection,
hidden-line policy, or ACIS payload tessellation.

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
  Autodesk's [Select Objects contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-243E4DD0-8947-4905-AFE2-BE9B903A8C3F.htm)
  makes direction semantic: left-to-right selects only completely enclosed
  objects, while right-to-left also selects crossed objects. ProGPU adopts
  those whole-object semantics for a rectangular projected clip volume.
  Autodesk's [3D subobject selection and cycling contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-89EFC58E-D14E-4B62-87D3-A6E26146D85E.htm)
  establishes that the foreground face is detected first and hidden
  alternatives require an explicit cycling workflow. ProGPU therefore keeps
  ordinary click on the nearest whole semantic entity and uses explicit
  Alt-click cycling without conflating hidden alternatives with subobjects.
- Autodesk's [PICKBOX contract](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-363698CF-C3DC-4770-81EF-CB09D86B3D3A.htm)
  defines the object-selection target by its complete height in
  device-independent pixels, defaults it to three, and gives zero a disabled
  meaning. ProGPU adopts those units, default, and zero behavior while bounding
  callers to 256 logical pixels.
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

`QueryHits` accepts caller-owned storage for one through 256 results, traverses
the complete clipped ray, keeps the nearest triangle for each semantic root,
and insertion-sorts the bounded nearest set by distance/batch/triangle. It
reports exact triangle intersections and traversal work plus explicit
truncation when more unique roots exist than fit. For destination capacity `K`,
the added semantic collection work is `O(H*K)` worst case and `O(K)` storage;
`K` is contractually bounded at 256. The nearest-only API retains its separate
pruning fast path and unchanged `O(log T + H)` typical contract.

`QueryAperture` and `QueryApertureHits` first execute the exact point contract,
so a surface directly below the pointer wins without target-area ambiguity.
Only a miss constructs a centered square clip volume with the caller's complete
logical-pixel target height. The same six-plane BVH and fixed twelve-vertex
triangle clipper prove candidates exactly; each clipped convex polygon is
fan-triangulated in fixed stack storage to find the nearest camera-space surface
point, from which original-triangle barycentrics and facing are reconstructed.
Single and semantic-depth results keep the existing distance/batch/triangle
ordering and explicit truncation. Typical fallback work is `O(log T + H*K)`
for `H` clipped candidates and result capacity `K`, worst-case `O(T*K)`, with
`O(K)` caller storage and zero warm managed allocation.

`QueryRegion` clamps two logical points to the viewport and converts their
rectangle to WebGPU NDC. It folds the four rectangle inequalities plus
`z >= 0` and `z <= w` into six local-space homogeneous planes once per query.
Each BVH AABB is conservatively rejected by six support-point plane tests.
Candidate triangles are classified against the same planes; Crossing clips a
triangle polygon plane-by-plane in fixed stack storage, so an intersection is
found even when no triangle vertex lies inside the rectangle. Window counts
only fully contained triangles and accepts a semantic root only when that
count equals the root's complete retained triangle count across every batch.
Semantic-root scratch is caller-owned and sized once from `SemanticRootCount`;
output preserves first-root scene order and reports exact truncation. Work is
`O(R + N + C)` and scratch is `O(R)` for `R` roots, `N` visited BVH nodes, and
`C` tested candidates. Warm queries allocate no managed memory.

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
The shared pickbox selector exposes 0/3/5/9/15 logical-pixel targets and the
public query accepts every finite value through 256. The default three-pixel
target applies consistently to ordinary click, Alt depth cycling, and the
object-versus-empty origin decision before a primary drag becomes orbit or
Window/Crossing selection.
Alt-click queries up to 64 nearest unique roots and advances when generation,
camera, and the four-logical-pixel click neighborhood are unchanged; a normal
click, camera change, generation replacement, or displaced click restarts at
the foreground root. Ctrl remains orthogonal and toggles the cycled root in the
shared selection set. A truncated cycle is surfaced in status rather than
silently claiming to enumerate every hidden root.
An ordinary perspective drag remains pending until it crosses the four-pixel
click threshold. The shared CAD host claims an empty-origin drag for region
selection and otherwise gives the gesture to the existing orbit controller,
matching AutoCAD's implied-window behavior without removing direct orbit.
Left-to-right commits Window and right-to-left commits Crossing; Ctrl toggles
the complete returned set atomically. Shift-left and middle/right remain pan.
The overlay uses dynamic theme-resource brushes and pointer motion only updates
bounded control state; no query runs until drag arbitration or completion.

## Verification and remaining gates

Required regressions cover frontmost ordering, two-sided triangles, misses,
near/far clipping, deterministic shared-edge ties, large-WCS rebasing,
spanning-triangle crossing with no contained vertex, whole-root Window
containment across separated triangles, generation/rebase validation,
dense-scene pruning, zero-allocation warm point/depth/pick-target/region queries,
coordinator replacement, empty-origin selection versus object-origin orbit,
Ctrl set toggling, selection clearing, semantic handle continuity,
theme-dynamic highlighting, and retained camera/upload counters. The
SHA-identified Release 256-by-256 grid lane contains
131,072 triangles. Its 2,359,276-byte, depth-15 index built at
15.4651/47.7765/47.7765 ms p50/p95/p99. Across 65,536 exact point queries it
visited 15 nodes, tested eight triangles, used zero managed bytes, and measured
2.6/6.2/17.1 microseconds p50/p95/p99. Near-edge three-pixel projected-target
queries used zero managed bytes, visited about 19 nodes, tested about 17
triangles, found about five clipped intersections and one semantic hit, and
measured 3.6/12.9/34.2 microseconds p50/p95/p99. Exact projected Crossing queries used
zero managed bytes, visited about 77 nodes, tested about 161 triangles, found
about 101 triangle intersections, and measured 29.2/75.9/129.3 microseconds
p50/p95/p99. The checked-in JSON is
`artifacts/benchmarks/cad-3d-selection-grid-256.json`.
There is no matched pre-change selection latency because the prior Flat 3D
viewer had no projected query path; these figures are an acceptance baseline,
not a claimed before/after speedup.

The final eight-layer lane contains 262,144 triangles and eight unique roots
along each ray. Across 65,536 queries, bounded semantic collection visited
about 91 nodes, tested 64 triangles, returned all eight roots, allocated zero
managed bytes, and measured 10.5/31.9/54.5 microseconds p50/p95/p99. Its
near-edge three-pixel projected-target companion visited 91 nodes, tested 64
triangles, found 24 clipped intersections, returned all eight roots, allocated
zero managed bytes, and measured 12.0/37.6/54.8 microseconds p50/p95/p99. Exact
projected Crossing visited about 314 nodes, tested about 488 triangles, found
about 254 triangle intersections, allocated zero managed bytes, and measured
96.3/242.4/329.2 microseconds p50/p95/p99. Its 4,718,712-byte index built at
31.6633/40.2788/40.2788 ms p50/p95/p99. The SHA-identified JSON is
`artifacts/benchmarks/cad-3d-selection-depth-8.json`. The point/depth query
implementation is unchanged by this slice; an attempted historical-commit
rebuild could not resolve that revision's dependency layout, so the new
depth-query observation is retained as an acceptance measurement rather than
presented as a matched regression claim.

Final-binary Allocations and Time Profiler captures use a 128-by-128,
eight-layer fixture and include exact point, semantic depth, projected-target,
and projected-region queries. Metal uses the same CPU algorithm and binaries at
16-by-16 scale so the target exits naturally within the capture duration.
Allocations report 19,140,992 persistent heap-plus-anonymous-VM bytes and
71,071,568 total bytes for startup, fixtures, builds, and all four query
families. The paired benchmark accounting reports zero managed bytes in every
warm query family. Metal reports no target resource allocation, current
allocated size, application submission, drawable wait, compiler spill, hang,
or error; the exported system trace contains unrelated completion events but no
target submissions. Every final target exited with code zero. Compact evidence
and SHA-identified capture notes are in the three
`cad-3d-selection-pickbox-*-natural/` directories.

Still required for full 3D selection fidelity are lasso/polygon/fence
selection, transparent/hidden-line policy,
face/edge/vertex subobjects, ACIS analytic topology, material/texture alpha
semantics, arbitrary non-Mesh3D projected entity selection, matched
managed/native rendered highlight images, browser interaction/performance
smoke, and licensed AutoCAD differentials.
