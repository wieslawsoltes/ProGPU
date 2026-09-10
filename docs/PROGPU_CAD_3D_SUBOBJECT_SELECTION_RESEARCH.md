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
  separate interaction layer. ProGPU now adopts point/aperture and candidate
  cycling with projected face/edge/vertex highlighting plus exact
  Window/Crossing/Polygon/lasso/Fence selection. Whole-object results are never
  silently applied to subobjects.

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

## Exact region-selection extension design gate

The region extension re-examined the required primary sources before changing
the query contract. Autodesk's current
[mesh-tool documentation](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-5569E695-C667-49BF-AB0B-979C6076CC1F.htm)
explicitly states that an active Face filter supports individual, Window, and
Crossing selection. Its
[Window/Crossing/Polygon/Fence/Lasso contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DidYouKnow/files/GUID-D0D5C0C3-F092-448A-8E81-D38F27094639.htm)
defines Window as complete enclosure, Crossing as enclosure or contact,
Window/Crossing Polygon as the same predicates over a simple polygon, Fence as
contact with an open path, and lasso as Window/Crossing/Fence modes. The
[subobject workflow](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-5E2A9783-090A-452A-913F-0F8A272A547A.htm)
keeps the active vertex/edge/face filter and multiple-selection set semantics.
ProGPU adopts these observable contracts for all three modern-MESH authored
subobject kinds; it does not treat one intersected display facet as complete
containment of its authored face.

Skia's current
[`SkPath` contract](https://skia.googlesource.com/skia/+/main/include/core/SkPath.h)
still separates retained path geometry, finite/tight bounds, fill rules, and
containment from raster output. Direct2D's current
[geometry contract](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-geometries-overview)
likewise keeps geometry relationships device-independent. Current
[Vello scene encoding](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
retains scene data for late transforms, while
[Parley layout](https://github.com/linebender/parley/tree/main/parley/src/layout)
and [HarfBuzz shape plans](https://github.com/harfbuzz/harfbuzz/blob/main/src/hb-shape-plan.cc)
confirm that reusable text work is unrelated and must not be invalidated.
WebRender's retained hit-test/spatial metadata and WebGPU's
[coordinate-system contract](https://gpuweb.github.io/gpuweb/#coordinate-systems)
continue to support identity beside retained geometry and exact top-left,
`[0,1]`-depth projection rather than framebuffer readback. Adopted: reuse the
existing immutable BVH, clip volume, projected double-intermediate predicates,
and caller-owned state. Rejected: raster/color picking, per-query path objects,
render-batch identity, runtime topology reconstruction, and text/cache rebuilds.

For each generation, the selection index assigns one dense state slot to every
authored component vertex, edge, and face and records the exact number of
render-primitive annotations contributing to it. Crossing/Fence needs one
exact contributing point, segment, or triangle. Window requires every retained
annotation for that authored subobject to be strictly contained, so a refined
edge chain or authored face cannot pass because only one child segment/facet is
inside. Rectangle work is `O(S + N + C)` and projected-path work is
`O(S + N + C*P)` for `S` authored subobjects, `N` visited BVH nodes, `C`
candidate triangles, and `P` path points. Storage is `O(S)` immutable counts
plus `O(S)` caller-owned integer state; result identity storage is bounded to
256 and truncation is explicit. Warm queries allocate no managed memory.

## Generation-safe subobject transform design gate

The editing extension re-examined the production-engine contracts before
making retained selection identity mutable. Autodesk documents that selected
subobjects can participate in
[`MOVE`, `ROTATE`, `SCALE`, and `ERASE`](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-7D35947F-9AFA-4DC1-ADBE-8601C8BCE185.htm),
that the [3D move gizmo](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-30E05BD1-8D1B-4DA8-BCE0-C91AE859A5C5.htm)
constrains selected objects or subobjects by axis or plane, and that subobject
transforms maintain the containing object's topology. Its
[grip guidance](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-7BD066C9-31BA-4D47-8064-2F9CF268FA15.htm)
places the gizmo at the selection center and supports group modification of
mesh subobjects. ProGPU adopts WCS translation, axis rotation, and uniform
scaling of a mixed vertex/edge/face set: vertices affect themselves, authored
edges affect both endpoints, authored faces affect every referenced control
vertex, and the union transforms each vertex exactly once. The shared shell
routes bounded `+/-X` and `+/-Y` translation plus positive/negative Z-axis
rotation and uniform scale through the same generation-owned command seam. It
defers free-drag gizmos, non-uniform scale, and grip-mode policy; topology-safe
erase is specified separately below.

Rendered IDs identify occurrences, while an editable control vertex belongs to
the authoritative modern-MESH entity. The snapshot and shared Mesh3D scene now
retain the source MESH handle, its source-to-WCS affine mapping, and whether it
is directly owned by model space. A direct source can be resolved under the
document lock and layer-lock rules. A source below an `INSERT` is deliberately
rejected before mutation: editing the block definition would change every
instance, while transforming only one occurrence requires an explicit
reference-editing or make-unique contract. Identity remains generation-local.
The command records its source generation, and history rejects it if the
session has advanced; after a successful topology-preserving move, the shell
remaps the selected authored kind/ordinal through source identity into the new
generation.

The cross-engine review reinforced that separation. Skia's current
[`SkMesh` contract](https://skia.googlesource.com/skia/+/main/include/core/SkMesh.h)
keeps specification/buffer identity explicit and bounds vertex/index buffer
updates to their created size and GPU context. Direct2D's
[geometry overview](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-geometries-overview)
keeps geometry resources and transforms independent of raster output.
WebRender's retained
[hit-test and spatial metadata](https://searchfox.org/mozilla-central/source/gfx/layers/wr)
supports keeping occurrence identity beside geometry rather than deriving it
from pixels. Current
[Vello scene encoding](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
retains geometry for late transforms, while
[Parley layout](https://github.com/linebender/parley/tree/main/parley/src/layout)
and [HarfBuzz shape-plan caching](https://github.com/harfbuzz/harfbuzz/blob/main/src/hb-shape-plan.cc)
confirm that an unrelated mesh edit must not invalidate reusable text shaping
or layout. WebGPU's
[coordinate-system contract](https://gpuweb.github.io/gpuweb/#coordinate-systems)
still has no persistent CAD subentity identity or document-editing primitive.

Adopted: immutable generation ownership, explicit authoritative source
identity, bounded update sets, late scene rebuild, exact stored coordinates for
undo/redo, and validation before publication. Adapted: unlike a mutable GPU
vertex buffer, ProGPU first changes ACadSharp's persisted control vertices and
then recompiles the canonical retained scene so DXF/DWG save output, managed
rendering, native rendering, selection, and bounds all observe one model.
Rejected: editing render triangles or smoothness facets, inferring control
vertices from equal positions, using a batch/component ordinal as persistent
identity, silently editing shared block definitions, partial best-effort
mutation, framebuffer picking, and rebuilding text/cache state.

The first application expands `S` selected subobjects into `A` unique control
vertices, derives exact double-WCS affected bounds, transforms every affected
vertex, and validates `C` authored face corners in expected `O(S + A + C)`
time with `O(A + C)` temporary/retained storage. Translation is one vector
addition per vertex. Rotation normalizes one finite nonzero WCS axis once and
applies the standard Rodrigues axis-angle expression in constant work per
vertex. Uniform scale accepts only a positive, finite, non-unit factor with a
finite reciprocal and performs one pivot-relative multiply per vertex.
Explicit-pivot command overloads preserve caller intent; selection-center
overloads derive their pivot from authoritative ACadSharp control coordinates,
not float-rebased render vertices.

Selection is bounded to 4,096 subobjects and one million affected vertices.
The command materializes exact before and proposed-after coordinate arrays and
validates all results, topology, and every authored edge before the first
write, so a non-finite result, stale topology, locked layer, missing source, or
collapsed edge changes neither the document nor its generation. Undo and redo
assign those retained arrays in `O(A)` time without recomputing an inverse
transform or allocating new command state. Rebuilding the retained scene after
each successful edit regenerates subdivision, bounds, selection data, and both
managed/native render inputs; the shell remaps selected source handle,
component, kind, and ordinal into the replacement generation.

This is CPU document editing and immutable-scene metadata only: no shader,
public C ABI, generated C# wire record, upload stream, render submission, or
managed/native crossing changes. Both renderers continue consuming the same
rebuilt scene, so a second C++ document editor would create ownership drift and
is not applicable.

## Topology-safe subobject deletion design gate

The deletion extension was designed from Autodesk's public behavior rather
than a third-party implementation. Autodesk documents that
[removing a mesh face leaves a gap](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-MAC-Core/files/GUID-DB57828F-4184-414F-8854-1731E877FCCC.htm),
deleting an edge removes every adjacent face, and deleting a vertex removes
every incident face. Its focused
[face-deletion workflow](https://help.autodesk.com/cloudhelp/2016/ENU/AutoCAD-Core/files/GUID-099BCDD4-1F7C-4A28-B350-C981CA5D6D23.htm)
uses subobject selection followed by Delete. The
[modern-MESH DXF contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-4B9ADA67-87C8-4673-A579-6E4C76FF7025.htm)
stores one ordered control-vertex array, face-index stream, and crease data;
ObjectARX's
[`setSubDMesh` contract](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-ManagedRefGuide/files/OREFNET-Autodesk_AutoCAD_DatabaseServices_SubDMesh_SetSubDMesh_Point3dCollection_Int32Collection_int.html)
likewise replaces control vertices and indexed faces, while
[`setVertexTextureArray`](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-AcDbSubDMesh__setVertexTextureArray_AcGePoint3dArray__.html)
requires texture coordinates to correspond to the vertex array.

ProGPU adopts the observable face/edge/vertex deletion rules exactly and
leaves the resulting boundary open. It computes the complete deleted-face set
from authored topology before mutation. Surviving faces retain source order.
Vertices newly made isolated by the deletion are compacted in original order,
as are explicitly selected pre-existing isolated vertices; unrelated
pre-existing isolated vertices are preserved because Autodesk's public
contract does not authorize deleting them. Per-control-vertex texture
coordinates and surviving crease-edge endpoints are remapped through the same
old-to-new index table. A mesh with no surviving face is removed as one whole
model-space entity and retained for exact Undo/Redo. The command neither fills
the gap nor synthesizes faces, triangulation, crease values, or UVs.

The generation-owned resolver shared with transform commands rejects stale,
nested, missing, out-of-range, and duplicate IDs before document mutation.
The command then validates direct model-space ownership, layer authorization,
finite vertices and UVs, every face index, distinct face vertices, collapsed
edges, and unique valid crease records. All candidate topology and every
complete-entity removal are preflighted before partial mesh state changes; a
cancelled removal batch leaves all selected meshes unchanged. Exact before and
after arrays are retained, so Undo/Redo does not infer inverse topology.
Selection is bounded to 4,096 IDs, one million visited control vertices, and
four million visited face corners. Initial deletion is `O(S + V + C + K)`
time and storage for selected IDs `S`, vertices `V`, face corners `C`, and
crease records `K`; retained Undo/Redo is `O(V + C + K)`.

The required engine review was rechecked for this topology-changing path.
Skia/SkParagraph and Direct2D keep device-independent geometry separate from
raster output; WebRender keeps retained identity/metadata separate from GPU
pixels; Vello retains scene encoding for later rendering; Parley and HarfBuzz
retain unrelated text layout and shaping work; WebGPU provides no persistent
CAD topology editor. ProGPU therefore mutates the authoritative ACadSharp
document once, then rebuilds the one canonical immutable scene consumed by
both managed and native renderers. Startup/lazy initialization, text shaping,
fallback fonts, variable-font state, glyph/path/image caches, visibility
culling, worker preparation, upload batching, DPI/subpixel policy, and
device-loss rehydration are unchanged. No shader, public C ABI, generated wire
record, GPU cache key, resource lease, or renderer-specific algorithm changes,
so a parallel C++ document mutation path is not applicable. The shell clears
the old ordinal selection after success because compaction can renumber faces,
edges, and vertices in the replacement generation.

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
bounded truncation, exact rectangular Window/Crossing containment, simple
polygon validation, even-odd self-crossing lasso, open Fence, authored-face
aggregation across subdivision children, zero-allocation warm replay,
non-applicable `3DFACE`, and the shared shell's point and region interaction.
The complete final Release `ProGPU.CAD.Tests` run passed 1,428/1,428.

The final Release benchmark DLL has SHA-256
`f28aaaf55e771bb948e4adc3d5d6b10ec0b9e031d581325db392674d53bf6d35`;
the loaded `ProGPU.CAD` DLL has SHA-256
`80a515ac8a54b24c47c9e0bf4f057c14c61c65d641e1d54d50e6cbfd64a38dd5`.
On a 128-by-128 modern-MESH grid repeated at four depths (131,072 retained
triangles and 264,196 authored subobjects), 65,536 queries per lane allocated
zero managed bytes. Exact point face queries measured 12.4/16.5/19.7
microseconds p50/p95/p99. Exact face Crossing rectangles measured
237.0/264.0/310.5 microseconds, even-odd Crossing lassos measured
249.2/279.7/323.9 microseconds, and open Fences measured
193.1/215.0/233.3 microseconds. Rectangle/lasso/Fence tested about
245/245/154 triangles and returned about 77/53/20 authored faces on average;
the intentional `O(S)` dense-state clear and result scan are included. The
11,870,568-byte index built at 23.47/51.79/51.79 milliseconds. The complete
SHA-identified acceptance result is generated locally at the ignored
`artifacts/benchmarks/cad-3d-subobject-selection/final-release.json` path. This is
an acceptance baseline, not a before/after speedup claim, because the previous
generation had no exact subobject-region query.

Xcode Instruments 16.0 then launched that exact final DLL for the same
128-by-128, four-layer query family. Allocations/VM Tracker retained exports for
22,079,024 persistent and 73,332,128 total heap-plus-anonymous-VM bytes across
process startup, topology construction, index construction, and all query
families; the paired managed hot-query counters remain zero. Time Profiler
retained its samples and reported no potential hang or hang risk. Metal System
Trace confirmed zero target resource allocations, current allocated bytes,
application submissions, waits, compiler spills, hangs, or errors. Its 8,649
completion rows are unrelated system activity because no target application
submission exists. All targets exited zero. Compact summaries, manifests, and
exported tables are generated locally under the ignored
`artifacts/benchmarks/cad-3d-subobject-selection/instruments-region/` path.

The transform extension adds focused regressions for mixed vertex/edge/face
deduplication, normalized-axis and exact-pivot math, invalid parameter rejection,
exact undo/redo coordinates, stale-generation rejection, multi-mesh atomic
failure, collapsed-edge rejection, bounded affected-vertex work, locked layers,
explicit nested-definition rejection, translation/rotation/scale subdivision
rebuild, DXF/DWG persistence, source-identity remapping, and shared-shell
movement/rotation/scale. The final Release suites passed 1,447/1,447
`ProGPU.CAD.Tests` and 3,848/3,848 `ProGPU.Tests`.

The final transform benchmark uses a 128-by-128 modern-MESH grid with 16,641
control vertices and 16,384 authored faces, selecting 1,024 evenly distributed
faces. The SHA-identified Release benchmark and `ProGPU.CAD` binaries are
`c05c51fc1dc53f270c4bfa7c135c00e0019de56406e634c01f8c7fbc5d6de811`
and `8354ee5f175d5793730b7e9f00b32519c1184442347d47a8e2926cb09b15eb8a`.
Across 24 iterations per lane, complete transform plus pre/post snapshot and
Mesh3D scene rebuild measured 408.1063/448.6865/463.4769 milliseconds
p50/p95/p99 for translation, 431.4559/548.9130/604.4687 for rotation, and
410.4458/670.3365/681.9217 for scale. Managed allocation was respectively
209,356,224, 209,047,849, and 209,357,734 bytes per operation. These lanes
deliberately include two full immutable compilations and expose their existing
allocation cost. Exact retained undo+redo measured
0.0245/0.0279/0.0288 milliseconds with 304 bytes for translation,
0.0286/0.0710/0.0736 with 288 bytes for rotation, and
0.0090/0.0113/0.0142 with 288 bytes for scale. Allocation is from history
reason strings and session events; retained coordinate replay creates no new
coordinate storage. The acceptance result is generated locally at the ignored
`artifacts/benchmarks/cad-3d-subobject-transform/final-release.json` path, whose
SHA-256 is
`d2ffaa7eae661831ecc61e973c0f9f2df585ce434e44740171b84d17ab72b3d7`.

Matched Xcode Instruments captures launched the same final binaries and all
three lanes. Allocations/VM Tracker reported 14,034,240 persistent and
1,617,079,344 total heap-plus-anonymous-VM bytes across startup and instrumented
work. Time Profiler retained samples with no potential hang or hang risk. Metal
System Trace found zero target resource allocations, current allocated bytes,
application submissions, drawable waits, compiler spills, hangs, or errors,
as expected for CPU editing and immutable scene compilation. All recordings
exited zero; compact manifests, target logs, summaries, and exported tables are
generated locally under the ignored
`artifacts/benchmarks/cad-3d-subobject-transform/instruments-final/` path.

The topology-deletion acceptance lane uses the same 128-by-128 mesh and 1,024
selected faces. Across 24 Release iterations, deletion plus pre/post snapshot
and Mesh3D scene rebuild measured 451.6070/467.5489/471.1918 milliseconds
p50/p95/p99 and 202,986,371 managed bytes per operation. Exact retained
Undo+Redo measured 0.0255/0.0272/0.0294 milliseconds with 496 bytes from
history/session publication. The SHA-identified result is generated locally at
the ignored `artifacts/benchmarks/cad-3d-subobject-delete/final-release.json`
path with SHA-256
`bc56c4e2b0b38587a3b94f62af6a063ebc19bf5dae905d025dff969826a25618`.
This is an acceptance baseline for new behavior, not a speedup claim.

Matched macOS Allocations and Time Profiler captures of the same four-lane
benchmark retained 18,267,072 persistent and 2,046,108,880 total
heap-plus-anonymous-VM bytes, samples, and no hang finding. A bounded Metal
retry of the same grid/selection workload found zero target allocations,
current allocated bytes, application submissions, waits, spills, hangs, or
errors; the initial target also exited zero but Xcode failed to finalize its
large system-wide trace, which the helper removed. The exact retry rationale,
manifests, logs, summaries, and compact exports are generated locally under the
ignored `artifacts/benchmarks/cad-3d-subobject-delete/` path.

Deletion regressions cover face/edge/vertex incidence, deterministic
vertex/UV/crease remapping, preservation of unrelated isolated vertices,
complete-entity identity across Undo/Redo, stale generations, locked layers,
work bounds, cancelled multi-mesh removal, subdivision rebuild, shared-shell
selection clearing, and DXF/DWG round trips. The final Release suites passed
1,458/1,458 `ProGPU.CAD.Tests` and 3,848/3,848 `ProGPU.Tests`.

Deferred work is explicit: persistent IDs across topology-edit generations,
free-drag gizmos, non-uniform scaling, arbitrary interactive 3D axis
acquisition, explicit nested reference editing, legacy-mesh compatibility
behavior, and ACIS solid/surface face, edge, and vertex topology. None is
approximated by whole-object handles, tessellation facets, render-batch
ordinals, shared-definition mutation, or display-wire indices.
