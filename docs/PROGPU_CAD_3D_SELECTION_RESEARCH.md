# ProGPU.CAD Projected 3D Selection Research

## Scope and clean-room provenance

This checkpoint adds whole-entity click, rectangular region, simple projected
polygon, freehand lasso, and open-fence selection for the retained Flat Mesh3D
view. Point selection unprojects one logical viewport position through the exact
camera matrices already submitted by the managed and native adapters, finds
the nearest visible retained triangle, and returns its ACadSharp semantic root
handle. A caller-buffered companion query returns bounded nearest-first unique
semantic roots, and repeated Alt-clicks cycle that depth order. Exact projected
Window/Crossing, WPolygon/CPolygon, lasso, and Fence queries use the same
retained triangles and camera clip volume.
Point and depth queries preserve an exact center-ray hit first, then use a
configurable projected pick target when that ray misses.
It does not add face/edge/vertex subobject editing, hidden-line policy, or ACIS
payload tessellation.

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
- Autodesk's [object-selection methods](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DidYouKnow/files/GUID-D0D5C0C3-F092-448A-8E81-D38F27094639.htm),
  [selection-area contract](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-33B54E3E-8C03-463E-8CF1-F7D9ACB1E2DB.htm), and
  [lasso settings](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-0510F4AF-B6C7-455C-899F-0AB126EC8154.htm)
  establish Window/Crossing polygon, Fence, and freehand Lasso selection.
  Window requires complete enclosure, Crossing includes intersected objects,
  Fence is an open path, and Space cycles lasso Window/Crossing/Fence modes.
  ProGPU adopts those observable contracts, including initial lasso direction,
  while bounding a gesture at 4,096 logical-space points and reporting rather
  than silently accepting truncation. Explicit WPolygon/CPolygon rejects
  touching or self-crossing boundaries; freehand lasso deliberately accepts
  self-crossing input with a documented even-odd interior.
- Hormann and Agathos's [point-in-polygon survey](https://www.inf.usi.ch/hormann/papers/Hormann.2001.TPI.pdf)
  describes crossing-number classification and the need to handle boundary
  cases explicitly. ProGPU independently uses double-intermediate orientation,
  inclusive segment intersection, explicit boundary classification, and an
  even-odd crossing test; no implementation text or lookup structure was used.
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
| Skia / SkParagraph | [SkPath containment](https://skia.googlesource.com/skia/+/2a8c48be4ff65d873d9d5ba65ecef989d82dd0be/site/user/api/SkPath_Reference.md), [Skia path operations](https://skia.googlesource.com/skia/+/20f3403/include/pathops/SkPathOps.h), [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/) | Skia exposes fill-rule containment and explicit path operations while keeping shaped text reusable. Adapted: classify retained projected triangle polygons against a caller path after conservative bounds traversal. Rejected: materializing general path-op objects per query. Existing CAD glyph runs are unaffected. |
| Direct2D / DirectWrite / Win2D | [Direct2D geometry overview](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-geometries-overview), [Direct2D geometry comparison](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1geometry-comparewithgeometry), [Win2D geometry combine](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasGeometryCombine.htm) | Direct2D/Win2D separate device-independent geometry relations from drawing and expose exact containment/combination contracts. Adapted: keep selection predicates on the device-independent retained CPU generation. Rejected: allocating backend geometry or initializing graphics merely to select. DirectWrite layout and fallback remain unaffected. |
| WebRender | [WebRender source](https://searchfox.org/mozilla-central/source/gfx/layers/wr), [Gecko painting and hit-test source](https://searchfox.org/firefox-main/source/layout/painting) | WebRender/Gecko retain semantic hit-test and clip-chain information separately from GPU pixels and cull before detailed behavior. Adapted: semantic batch handles remain next to retained triangles and the path AABB drives BVH broad phase; rejected: framebuffer readback as object identity. |
| Vello / Parley | [Vello scene source](https://github.com/linebender/vello/tree/main/vello/src), [Parley line-layout source](https://github.com/linebender/parley/tree/main/parley/src/layout) | Vello separates retained scene encoding from late transforms; Parley retains reusable layout. Adopted: the index belongs to one immutable geometry generation while projection and selection paths are late state. No text cache or layout changes apply. |
| HarfBuzz | [shape API source](https://github.com/harfbuzz/harfbuzz/blob/main/src/hb-shape.cc), [shape-plan source](https://github.com/harfbuzz/harfbuzz/blob/main/src/hb-shape-plan.cc) | HarfBuzz retains reusable shaping plans and has no projected surface-selection role. Existing font fallback, variable-font state, glyph caches, and device-loss invalidation remain unchanged. |
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

`QueryPolygon`, `QueryLasso`, and `QueryFence` derive a conservative six-plane
clip volume from the projected path bounds and reuse the same BVH. Candidate
triangles are clipped to that volume and then projected through the exact
retained view-projection matrix. Double-intermediate point/boundary and segment
predicates classify the resulting convex polygon against the caller path.
Window accepts a semantic root only when every retained root triangle is
strictly inside the closed path; Crossing accepts any boundary or interior
overlap. A freehand lasso uses even-odd fill and may cross itself. WPolygon and
CPolygon first prove a simple non-touching polygon in `O(P^2)`. Fence remains
an open, potentially self-crossing, zero-width path; a one-logical-pixel bounds
expansion is conservative broad phase only, and exact segment/polygon contact
decides every hit. For `P` path points, `R` roots, `N` visited nodes, and `C`
candidate triangles, query work is `O(R + N + C*P)`, with the additional
`O(P^2)` simple-polygon validation when requested. Storage is fixed clipping
and traversal stack plus caller-owned `O(R)` root scratch and output. The public
path bound is 4,096 points; output remains deterministic in first-root scene
order with explicit destination truncation and zero warm managed allocation.

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
The shared Box/Lasso selector switches the same empty-origin gesture to a
one-logical-pixel-sampled freehand path. Its initial direction selects Window
or Crossing, and Space cycles Window, Fence, and Crossing while captured.
Window/Crossing closes the overlay and query implicitly; Fence leaves it open.
The control reuses one 4,096-point buffer, exposes its span only to the
synchronous completion callback, and explicitly rejects an over-capacity
gesture without changing selection.

## Verification and remaining gates

Required regressions cover frontmost ordering, two-sided triangles, misses,
near/far clipping, deterministic shared-edge ties, large-WCS rebasing,
spanning-triangle crossing with no contained vertex, whole-root Window
containment across separated triangles, generation/rebase validation,
dense-scene pruning, zero-allocation warm point/depth/pick-target/region/lasso/
fence queries, concave simple polygons, self-crossing even-odd lassos, open
collinear fences, strict whole-root Window and exact Crossing behavior,
coordinator replacement, empty-origin selection versus object-origin orbit,
Ctrl set toggling, selection clearing, semantic handle continuity,
theme-dynamic highlighting, and retained camera/upload counters. The
SHA-identified Release 256-by-256 grid lane contains
131,072 triangles. Its 2,359,276-byte, depth-15 index built at
15.1625/45.4200/45.4200 ms p50/p95/p99. Across 65,536 exact point queries it
visited 15 nodes, tested eight triangles, used zero managed bytes, and measured
3.4/9.0/15.7 microseconds p50/p95/p99. Near-edge three-pixel projected-target
queries used zero managed bytes, visited about 19 nodes, tested about 17
triangles, found about five clipped intersections and one semantic hit, and
measured 3.6/17.4/54.6 microseconds p50/p95/p99. Exact projected Crossing queries used
zero managed bytes, visited about 77 nodes, tested about 161 triangles, found
about 101 triangle intersections, and measured 20.9/54.0/76.8 microseconds
p50/p95/p99. The three-point lasso reused the same nodes/candidates, found
about 59 intersections and one root, and measured 34.5/97.8/139.1
microseconds. The two-point Fence visited about 54 nodes, tested about 86
triangles, found about 13 intersections and one root, and measured
17.4/47.2/74.7 microseconds. Both new lanes allocated zero managed bytes. The
checked-in JSON is
`artifacts/benchmarks/cad-3d-selection-grid-256.json`.
There is no matched pre-change selection latency because the prior Flat 3D
viewer had no projected query path; these figures are an acceptance baseline,
not a claimed before/after speedup.

The final eight-layer lane contains 262,144 triangles and eight unique roots
along each ray. Across 65,536 queries, bounded semantic collection visited
about 91 nodes, tested 64 triangles, returned all eight roots, allocated zero
managed bytes, and measured 10.4/33.7/60.7 microseconds p50/p95/p99. Its
near-edge three-pixel projected-target companion visited 91 nodes, tested 64
triangles, found 24 clipped intersections, returned all eight roots, allocated
zero managed bytes, and measured 11.4/39.5/64.7 microseconds p50/p95/p99. Exact
projected Crossing visited about 314 nodes, tested about 488 triangles, found
about 254 triangle intersections, allocated zero managed bytes, and measured
92.2/241.9/330.6 microseconds p50/p95/p99. Lasso reused the same broad phase,
found about 169 intersections and all eight roots, and measured
125.8/332.4/473.0 microseconds. Fence visited about 252 nodes, tested about 322
triangles, found about 62 intersections and all eight roots, and measured
80.6/219.8/300.4 microseconds. Both allocated zero managed bytes. Its
4,718,712-byte index built at 22.4764/36.5025/36.5025 ms p50/p95/p99. The
SHA-identified JSON is
`artifacts/benchmarks/cad-3d-selection-depth-8.json`. The point/depth query
implementation is unchanged by this slice; an attempted historical-commit
rebuild could not resolve that revision's dependency layout, so the new
depth-query observation is retained as an acceptance measurement rather than
presented as a matched regression claim.

Final-binary Allocations and Time Profiler captures use a 128-by-128,
eight-layer fixture and include exact point, semantic depth, projected-target,
projected-region, lasso, and Fence queries. Metal uses the same CPU algorithm
and binaries at 16-by-16 scale so the target exits naturally within the capture
duration. Allocations report 20,850,448 persistent heap-plus-anonymous-VM bytes
and 71,332,080 total bytes for startup, fixtures, builds, and all six query
families. The paired benchmark accounting reports zero managed bytes in every
warm query family. Metal reports no target resource allocation, current
allocated size, application submission, drawable wait, compiler spill, hang,
or error; the exported system trace contains unrelated completion events but no
target submissions. Every final target exited with code zero. Compact evidence
and SHA-identified capture notes are in the three
`cad-3d-selection-lasso-*-natural/` directories.

Still required for full 3D selection fidelity are transparent/hidden-line policy,
face/edge/vertex subobjects, ACIS analytic topology, material/texture alpha
semantics, arbitrary non-Mesh3D projected entity selection, matched
managed/native rendered highlight images, browser interaction/performance
smoke, and licensed AutoCAD differentials.
