# ProGPU.CAD Mesh3D Subobject Selection Research

## Scope and clean-room provenance

This checkpoint adds projected vertex, edge, and face identity and selection for
modern AutoCAD `MESH` objects. It retains authored control-topology identity
through optional Catmull-Clark display refinement, exact face triangulation,
style batching, large-WCS rebasing, and nested block expansion. It does not
make smoothness facets editable, infer topology from render-triangle adjacency,
or claim subobject support for legacy polygon/polyface meshes, `SOLID`,
`3DFACE`, or untessellated ACIS payloads.

The implementation is original ProGPU code. No third-party implementation
text, helper layout, naming, control flow, lookup-table encoding, or source
organization was copied. Approved in-repository implementation provenance is:

- `src/ProGPU.CAD/CadMeshSubdivision.cs`, the original ProGPU-owned bounded
  Catmull-Clark refinement and crease-aware normal implementation;
- `src/ProGPU.CAD/CadMesh3DTopology.cs`, the original ProGPU-owned exact CAD
  face validation and deterministic triangulation implementation;
- `src/ProGPU.CAD/CadMesh3DSceneCompiler.cs`, the immutable managed/native
  Mesh3D scene and consecutive-style batching contract; and
- `src/ProGPU.CAD/CadMesh3DSelection.cs`, the generation-owned Morton BVH,
  WebGPU clip-volume, exact ray/triangle, and caller-buffered projected-query
  implementation.

## AutoCAD behavior and identity contracts

- Autodesk's [subobject selection overview](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-7D35947F-9AFA-4DC1-ADBE-8601C8BCE185.htm)
  defines face, edge, and vertex subobjects for 3D solids, surfaces, and meshes.
  With no filter, Ctrl+click selects a subobject; an active vertex, edge, or face
  filter permits direct click selection.
- [`SUBOBJSELECTIONMODE`](https://help.autodesk.com/cloudhelp/2027/ENG/AutoCAD-Core/files/GUID-7C4F6525-41DD-48A2-AE7C-45663EDD6122.htm)
  defines Off, Vertex, Edge, and Face filters. ProGPU adopts those four relevant
  modes as a typed selector and leaves solid-history and drawing-component
  filters until their source geometry exists.
- Autodesk's [overlapping-subobject contract](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-94DAC4CF-C9E9-4DC1-9226-0E905AA12CE8.htm)
  uses Ctrl+Space to cycle visible and hidden candidates before selection.
  ProGPU adopts explicit bounded nearest-first cycling; it never turns a depth
  readback color into semantic identity.
- Autodesk's [mesh modification contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-D9C3BFA6-C6F6-44B8-8BCC-8CB99A7C697B.htm)
  distinguishes editable mesh faces from the denser smoothness facets and says
  facets cannot be individually modified. It also explicitly excludes legacy
  polygon and polyface meshes from these modern mesh capabilities. ProGPU
  therefore propagates each authored control-face ordinal through every
  refined child face and triangulation, propagates each authored edge through
  its refined display-polyline segments, and preserves each authored control
  vertex at its final displayed position. Render triangles and subdivision
  facets never receive public face IDs.
- ObjectARX's [`AcDbSubentId`](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-AcDbSubentId.html)
  pairs a face/edge/vertex type with a graphics-system marker. The
  [subentity-path guide](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-DevGuide/files/GUID-50891C1D-FA31-4611-8BDE-35A054E091CA.htm)
  adds the outer-to-inner object path needed to distinguish nested occurrences.
  Autodesk separately documents persistent associative IDs because ordinary
  subentity markers can change after reevaluation. ProGPU adapts that split:
  one public ID contains content generation, semantic root handle, snapshot
  component index, kind, and authored ordinal. It is exact and stable within
  one immutable generation, distinguishes repeated geometry below one root,
  and is rejected after replacement. Cross-edit persistence is deliberately
  deferred until the editable topology owns persistent IDs.
- Autodesk's [mesh selection-filter workflow](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-5569E695-C667-49BF-AB0B-979C6076CC1F.htm)
  supports individual and Window/Crossing face selection and shows grips as a
  separate interaction layer. The first delivery adopts point/aperture and
  candidate cycling with projected face/edge/vertex highlighting; exact
  subobject Window/Crossing selection remains a separately measured extension
  rather than silently applying whole-object results to subobjects.

## Required cross-engine architecture review

| Engine | Primary source examined | Decision for this checkpoint |
|---|---|---|
| Skia / SkParagraph | [SkPath containment](https://skia.googlesource.com/skia/+/2a8c48be4ff65d873d9d5ba65ecef989d82dd0be/site/user/api/SkPath_Reference.md), [Skia path operations](https://skia.googlesource.com/skia/+/20f3403/include/pathops/SkPathOps.h), [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/) | Skia keeps geometry predicates independent from rendering and keeps shaped text reusable. Adapted: topology identity and projected predicates remain immutable CPU data. Rejected: allocating backend path objects per rollover or deriving IDs from raster coverage. Text shaping, fallback, and glyph caches are unaffected. |
| DirectWrite / Direct2D / Win2D and DirectX | [Direct2D geometry overview](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-geometries-overview), [Direct2D geometry comparison](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1geometry-comparewithgeometry), [DirectXMath triangle tests](https://learn.microsoft.com/en-us/windows/win32/dxmath/ovw-xnamath-triangletests), [DirectXTK picking](https://github.com/microsoft/DirectXTK/wiki/Picking) | These APIs separate device-independent geometry relationships, ray construction, bounds tests, and exact triangle tests. Adapted: one CPU broad phase followed by exact projected primitive tests. Rejected: per-query Direct2D geometry allocation or a platform-only GPU picking path. DirectWrite layout remains unchanged. |
| WebRender / Gecko | [Gecko/WebRender hit-test source](https://searchfox.org/mozilla-central/source/gfx/layers/wr), [WebRender bindings](https://searchfox.org/mozilla-central/source/gfx/webrender_bindings/src/bindings.rs), [rendering architecture](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst) | Gecko retains hit-test metadata, spatial identity, and clip chains separately from pixels and deduplicates repeated hit-test information. Adapted: keep component/subobject identity beside retained geometry and reuse the scene transform at query time. Rejected: framebuffer readback and style-batch identity. |
| Vello / Parley | [Vello retained-scene design](https://github.com/linebender/vello/blob/main/doc/vision.md), [Vello scene source](https://github.com/linebender/vello/blob/main/vello/src/scene.rs), [Parley layout source](https://github.com/linebender/parley/tree/main/parley/src/layout) | Vello separates retained encoding from late transforms; Parley retains reusable line layout. Adapted: source topology is compiled once per immutable CAD generation and camera projection remains late state. No text layout is rebuilt for subobject rollover or selection. |
| HarfBuzz | [shape entry point](https://github.com/harfbuzz/harfbuzz/blob/main/src/hb-shape.cc), [shape-plan cache](https://github.com/harfbuzz/harfbuzz/blob/main/src/hb-shape-plan.cc) | HarfBuzz keeps reusable shaping plans and has no surface-topology role. Existing Unicode/OpenType shaping, fallback fonts, variable-font state, and glyph upload behavior remain unchanged. |
| WebGPU | [coordinate systems](https://gpuweb.github.io/gpuweb/#coordinate-systems), [device loss](https://gpuweb.github.io/gpuweb/#dom-gpudevice-lost) | WebGPU defines the exact top-left viewport and `[0,1]` depth clip contract but no portable semantic subobject query. Adapted: project with the same camera matrices and retain the index outside device ownership. Device replacement rehydrates draw resources without changing a matching CPU topology generation. |

## Adopted retained topology and selection design

For a modern mesh with `V` authored control vertices, `E` unique authored
control edges, `F` authored faces, and `T` rendered triangles, snapshot
compilation retains:

- `V` final displayed control-vertex positions with authored vertex ordinals;
- each authored edge as one ordered final display polyline whose segments are
  produced by subdivision, never by triangulation diagonals;
- each authored face as its ordered authored-edge loop; and
- per-render-triangle face identity plus per-corner vertex identity and
  per-side edge identity, using `-1` where a triangle corner or side is only a
  refinement/triangulation artifact.

Subdivision starts with deterministic first-appearance edge order from the
authored face loops. Each child face inherits its original face ordinal. Each
split source-edge segment inherits its original edge ordinal, and its new edge
point is inserted into the retained ordered edge chain. Original control
vertices remain at the front of the refinement vertex array and retain their
ordinal while their displayed positions follow the existing Catmull-Clark
mask. Triangulation copies only these explicit annotations. It never attempts
to reconstruct authored topology later from equal floating-point positions.

Snapshot work remains `O(V + E + F + T)` in addition to the existing bounded
subdivision and triangulation costs. Storage is `O(V + E*2^L + F + T)` for
subdivision level `L`; it is charged to the existing aggregate topology limit
and published transactionally with the generation. The scene compiler copies
annotations while preserving consecutive-style batching and assigns the
snapshot mesh primitive index as the generation-local component index, so
filtering and material batching cannot renumber identities.

Point selection traverses the existing triangle BVH. A square logical-pixel
aperture is transformed into the same six homogeneous local-space planes used
by existing projected queries. Candidate triangles expose only their annotated
authored face, boundary-edge segments, and authored corner vertices. Exact
ray/triangle or clipped-triangle tests resolve faces; homogeneous segment/point
clipping and projected distance resolve edges and vertices. Results are
deduplicated by full subobject ID and sorted by camera depth, projected pointer
distance, component, kind, and ordinal. The destination is caller-owned and
bounded to 256 entries; truncation is explicit. Typical query work is
`O(log T + H*K)` for `H` candidate triangles and bounded result capacity `K`,
with conservative `O(T*K)` worst case and zero warm managed allocation.

## Interaction, managed/native parity, and invalidation

The shared shell owns a generation-tagged selected-subobject set independently
from the existing whole-entity handle set. Off preserves whole-entity click;
Ctrl+click in Off requests an unfiltered subobject candidate. An active Vertex,
Edge, or Face filter requests that kind without Ctrl. Ctrl+Space cycles the
bounded candidates at the last stable pointer/camera/generation state. Selection
replacement, additive/removal modifiers, empty clicks, camera movement, and
generation replacement invalidate the cycle state explicitly.

Projected highlights and vertex grips are a bounded overlay derived from the
retained component geometry. Brushes use theme resources and re-resolve on
theme changes. Camera-only updates reproject the selected geometry without
recompiling the snapshot, rebuilding GPU mesh buffers, or uploading retained
geometry. A generation replacement rejects stale IDs before indexing or
highlighting and clears their UI state transactionally.

The managed and native renderers already consume the same
`CadRecordedMesh3DScene`. This checkpoint adds CPU-only identity annotations
and queries; it changes no shader, stable C record, generated C# wire layout,
native handle, upload stream, or managed/native crossing. Both renderers draw
the same triangles and a native host may invoke the same managed-independent
CPU selection contract before native submission. A duplicate C++ picker would
add semantic drift without reducing a render-path crossing, so it is not
applicable. Native image parity, stable replay uploads, and device-loss resource
rehydration remain required regression gates.

## Validation and deferred scope

Focused regressions cover authored-face identity across triangulation and style
batching, refined edge chains and final control-vertex positions, nested
component disambiguation, exact front/hidden ordering, filters, cycling,
bounded truncation, zero-allocation warm replay, non-applicable `3DFACE`, and
the shared shell's replace/add/remove interaction. The complete final Release
`ProGPU.CAD.Tests` run passed 1,424/1,424.

The final Release benchmark DLL has SHA-256
`9a639dff17a6c5a48b852de1e2f68ba68b5512c95dc57a3012f64c5d95ffefe1`;
the loaded `ProGPU.CAD` DLL has SHA-256
`188f513ded9b704e04fce2ce590e99d6a658a001eaf6b098cd789dd2d9418a0a`.
On a 128-by-128 modern-MESH grid repeated at four depths (131,072 retained
triangles), 65,536 exact face-subobject queries returned four ordered hits
each, allocated zero managed bytes, and measured 12.0/16.3/19.2 microseconds
p50/p95/p99. A query visited about 97 BVH nodes, tested 65 triangles, and
accepted about six clipped intersections on average. The complete
SHA-identified result is
`artifacts/benchmarks/cad-3d-subobject-selection/final-release.json`. This is
an acceptance baseline, not a before/after speedup claim, because the previous
generation had no subobject query.

Xcode Instruments 16.0 then launched that exact final DLL for the same
128-by-128, four-layer query family. Allocations/VM Tracker retained exports for
20,752,800 persistent and 71,469,584 total heap-plus-anonymous-VM bytes across
process startup, topology construction, index construction, and all query
families; the paired managed hot-query counters remain zero. Time Profiler
retained its samples and reported no potential hang or hang risk. Metal System
Trace confirmed zero target resource allocations, current allocated bytes,
application submissions, waits, compiler spills, hangs, or errors. Its 2,507
completion rows are unrelated system activity because no target application
submission exists. All targets exited zero. Compact summaries, manifests, and
exported tables are retained under
`artifacts/benchmarks/cad-3d-subobject-selection/instruments/`.

Deferred work is explicit: exact subobject Window/Crossing/lasso/fence sets,
persistent IDs across topology-edit generations, subobject transforms and
grips, legacy-mesh compatibility behavior, and ACIS solid/surface face, edge,
and vertex topology. None is approximated by whole-object handles, tessellation
facets, render-batch ordinals, or display-wire indices.
