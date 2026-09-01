# ProGPU.CAD Modern-MESH Smoothing and Crease Research

## Scope and clean-room provenance

This checkpoint adds reversible authoring of the persisted modern-MESH
subdivision level and edge-crease table. It does not import implementation
text, control flow, helper types, tables, or source organization from another
engine. The original ProGPU-owned in-repository sources used directly are:

- `src/ProGPU.CAD/CadMeshSubdivision.cs`, the bounded Catmull-Clark,
  semi-sharp crease-decay, UV refinement, and normal implementation;
- `src/ProGPU.CAD/CadMesh3DSceneCompiler.cs`, the canonical immutable managed
  and native Mesh3D scene contract;
- `src/ProGPU.CAD/CadMesh3DEditing.cs`, the generation-owned direct-model-space
  subobject resolver; and
- `src/ProGPU.CAD/CadEditing.cs`, the bounded edit-history and exact retained
  Undo/Redo ownership contract.

## AutoCAD behavior adopted

Autodesk defines
[`MESHSMOOTHMORE`](https://help.autodesk.com/cloudhelp/2020/ENG/AutoCAD-Core/files/GUID-FE5AAF13-EC5D-4EDF-A888-A1CF36486B95.htm)
as increasing each selected mesh by one level and
[`MESHSMOOTHLESS`](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-D8F075B5-3714-4C45-9564-4F2716BE3815.htm)
as decreasing each eligible selected mesh by one. Selection sets can contain
different starting levels, and boundary-level meshes are filtered. Autodesk's
[`SMOOTHMESHMAXLEV`](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-70592674-3BBF-45FF-8E4C-DAEFD78D1812.htm)
range is 1-255 with a default of four and a recommended range of one through
five. ProGPU accepts a caller ceiling in that persisted range but the shared
shell uses the existing renderer ceiling of six and the existing one-million
aggregate refinement-visit limit. It deliberately rejects non-MESH entities
instead of invoking AutoCAD's optional object-to-mesh conversion because no
clean-room conversion contract exists yet.

Autodesk's
[crease behavior](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-1860BD69-AE39-43B1-85D3-1DCF9E57D064.htm)
states that an edge sharpens itself, a face sharpens every boundary edge, and a
vertex sharpens every incident edge. [`MESHCREASE`](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-F176266D-C615-4A0B-95ED-E8FBE1D4E392.htm)
defines zero as removal, `-1` as Always, and a positive value as the highest
smoothing level at which the crease is retained;
[`MESHUNCREASE`](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-CD7FDDA6-DC23-4E1C-9607-45CB2DDC8434.htm)
removes creases through the same face/edge/vertex selection. ObjectARX's
[`AcDbSubDMesh::setCrease`](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-RefGuide/files/OREF-AcDbSubDMesh__setCrease_AcDbFullSubentPathArray__double.html)
confirms those numeric meanings. ProGPU maps selected authored subobjects to
one deduplicated edge set, preserves unrelated crease-record order, updates
existing records in place, appends newly creased edges in deterministic source
order, and removes selected records for zero. It neither creases subdivision
facets nor triangulation diagonals.

The existing ProGPU fractional `BlendCrease` behavior remains based on the
published semi-sharp Catmull-Clark model. Pixar's
[feature-adaptive subdivision paper](https://graphics.pixar.com/library/GPUSubdivRenderingA/paper.pdf)
describes infinitely sharp, semi-sharp, and fractional creases as modeling
features. A fractional value is accepted only when the persisted ACadSharp
`BlendCrease` flag already authorizes interpolation; the command never changes
that independent property implicitly.

## Required cross-engine architecture review

| Engine | Primary sources rechecked | Decision |
|---|---|---|
| Skia / SkParagraph | [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/), [Skia text overview](https://docs.skia.org/docs/dev/design/text_overview/) | Text shaping and rendering are staged independently from mutable geometry. Adopted: a mesh edit invalidates only the CAD generation and canonical mesh scene. Existing shaping, fallback, glyph arrays, and caches remain reusable. |
| DirectWrite / Direct2D / Win2D | [Direct2D API overview](https://learn.microsoft.com/en-us/windows/win32/direct2d/the-direct2d-api), [geometry realizations](https://learn.microsoft.com/en-us/windows/win32/direct2d/geometry-realizations-overview) | Device-independent geometry is separated from device resources, and realizations are rebuilt when geometry changes. Adapted: persist the authored ACadSharp level/crease first, then rebuild immutable scene geometry once. No platform-only geometry object enters the document model. DirectWrite is unaffected. |
| WebRender / Gecko | [rendering architecture](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst), [retained hit-test source](https://searchfox.org/mozilla-central/source/gfx/layers/wr) | Retained spatial/identity metadata remains separate from GPU pixels. Adapted: generation-owned authored subobject IDs select persisted edges; render triangles remain output only. |
| Vello / Parley | [Vello retained-scene design](https://github.com/linebender/vello/blob/main/doc/vision.md), [Vello scene source](https://github.com/linebender/vello/blob/main/vello/src/scene.rs), [Parley layout](https://github.com/linebender/parley/tree/main/parley/src/layout) | Late scene encoding and reusable layout reinforce one canonical scene replacement without invalidating unrelated text layout. No Vello mesh-editing primitive is adopted. |
| HarfBuzz | [shape entry point](https://github.com/harfbuzz/harfbuzz/blob/main/src/hb-shape.cc), [shape-plan cache](https://github.com/harfbuzz/harfbuzz/blob/main/src/hb-shape-plan.cc) | HarfBuzz has no surface-topology role. Its shaping plans, variable-font state, fallback decisions, and glyph positions remain unchanged across mesh edits. |
| WebGPU | [coordinate systems](https://gpuweb.github.io/gpuweb/#coordinate-systems), [device loss](https://gpuweb.github.io/gpuweb/#dom-gpudevice-lost) | WebGPU has no portable subdivision-authoring or CAD crease contract. Existing canonical scene upload, batching, visibility culling, and device-loss rehydration remain authoritative. |

Startup and lazy initialization, font discovery, shaping/layout reuse,
display-list reuse outside the changed CAD generation, visibility culling,
glyph/texture/path caches, demand upload, worker preparation, GPU batching,
DPI/subpixel/hinting, fallback fonts, variable fonts, and device-loss policy are
unchanged. The edit uses no runtime reflection and adds no shader, public C
record, generated C# wire record, managed/native crossing, resource lease, or
backend-specific cache key. Both renderers consume the same rebuilt
`CadRecordedMesh3DScene`; a second native document editor would create model
ownership drift and is not applicable.

## Algorithms, bounds, and failure contract

Smooth More/Less resolves at most 65,536 distinct direct model-space MESH
handles. It filters only meshes already at the requested boundary, computes
each proposed level, and preflights the existing exact corner-visit expression
`C*(4^(L+1)-1)/3` for source corners `C` and positive level `L` across the
document. The default ceiling is six levels and one million visits. Overflow,
locked layers, a wrong entity family, invalid levels, or an exhausted budget
fails before the first assignment. Initial application is `O(M + C)` for
visited model-space meshes and corners; Undo/Redo is `O(A)` for affected meshes
and retains exact before/after levels.

Crease editing accepts at most 4,096 generation-owned subobjects, four million
visited face corners, and one million affected authored edges. It validates
direct model-space ownership, locked layers, generation and topology counts,
finite control vertices, face indices and distinctness, collapsed edges,
unique persisted crease records, and crease numeric state. It builds every
mesh's complete proposed edge table before mutation, so a failure in one mesh
changes none. Initial application is `O(S + V + C + K)` expected time and
storage for selected subobjects `S`, vertices `V`, corners `C`, and crease
records `K`; retained Undo/Redo replaces exact edge arrays in `O(K)`. Hash
tables have the standard theoretical collision worst case, while explicit
work bounds cap input size.

The shared desktop/browser shell exposes Smooth +/− for an unlocked all-MESH
whole-entity selection and invariant crease input plus Set crease/Uncrease for
modern-MESH subobjects. Crease edits preserve authored topology, so source
handle, kind, and ordinal are remapped into the replacement generation.
Smoothness retains whole-entity handles. All controls use existing dynamic
theme resources.

## Validation and measured evidence

Focused coverage includes mixed starting levels, boundary filtering, exact
Undo/Redo, locked and wrong-family rejection, aggregate refinement bounds,
face/edge/vertex crease expansion and deduplication, unrelated crease
preservation, uncrease removal, fractional Blend Crease authorization including
multi-mesh failure atomicity, stale generation rejection, shared-shell selection
remapping, canonical subdivision rebuild, and DXF/DWG round trips. The focused
set passes 11 tests; the complete Release ProGPU.CAD and core suites pass 1,469
and 3,848 tests respectively with no failures or skips.

The checked-in 64 by 64 grid benchmark contains 4,225 control vertices, 4,096
authored faces, and 512 selected faces. Across three warmups and twelve measured
iterations, Smooth More plus canonical snapshot/scene rebuild measured
110.8749 ms p50 and 128.4133 ms p95/p99; face crease plus rebuild measured
179.8041 ms p50 and 244.7409 ms p95/p99. Exact retained Smooth More Undo/Redo
measured 0.0002/0.0023/0.0023 ms p50/p95/p99 with 288 managed bytes per pair;
crease Undo/Redo measured 0.1730/4.0163/4.0163 ms with 420,272 bytes per pair.
The JSON records the final benchmark, CAD, backend, scene, headless-test, and
WinUI binary hashes.

Matched Xcode Allocations and Time Profiler captures of the same Release binary
reported 20,651,072 persistent native-heap plus anonymous-VM bytes,
631,442,656 total allocated bytes, 610,791,584 transient bytes, and no potential
hangs, hang risks, or command-buffer errors. The CPU compile workload submitted
no Metal commands and allocated no observed Metal resources. A separate Metal
System Trace attempt did not finalize within the profiler's bounded cleanup
window; the incomplete 53,248-byte trace and 293,952,056 bytes of Xcode scratch
data were removed, its logs were retained, and no Metal result is claimed.
Exact commands, exports, hashes, and cleanup accounting are in
`artifacts/benchmarks/cad-3d-mesh-smoothing/`.
