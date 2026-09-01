# ProGPU.CAD Modern-MESH Whole-Object Refinement Research

## Scope and clean-room provenance

This checkpoint adds reversible whole-object modern-MESH refinement equivalent
to the object-selection route of AutoCAD `MESHREFINE`. It bakes the mesh's
currently displayed subdivision into persisted editable topology and resets the
object's subdivision level to zero. Face-local refinement is a separate
topological operation and is not approximated by refining the complete object.

No third-party implementation text, control flow, helper layout, lookup table,
or source organization was copied. The ProGPU-owned sources used directly are:

- `src/ProGPU.CAD/CadMeshSubdivision.cs`, the bounded original Catmull-Clark,
  semi-sharp crease, topology-provenance, UVW refinement, and normal algorithm;
- `src/ProGPU.CAD/CadMesh3DSmoothing.cs`, the persisted smoothness and authored
  crease contracts;
- `src/ProGPU.CAD/CadEditing.cs`, the exact retained history and direct
  model-space ownership contract; and
- `src/ProGPU.CAD/CadSnapshotCompiler.cs` and
  `CadMesh3DSceneCompiler.cs`, the one canonical managed/native render scene.

Autodesk documentation and Pixar subdivision publications were consulted only
for observable behavior, public contracts, and published algorithms. OpenSubdiv
source was not used as implementation input.

## AutoCAD behavior adopted

Autodesk's
[`MESHREFINE`](https://help.autodesk.com/cloudhelp/2020/ENG/AutoCAD-Core/files/GUID-36077075-B5DF-452E-A9C1-8575A4763864.htm)
command accepts mesh objects or face subobjects. Whole-object refinement turns
the underlying facets at the current smoothness level into editable faces and
resets the object's smoothness level to zero, establishing a new baseline.
Autodesk's
[refinement workflow](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-0382A1C3-02BC-40DA-B2AA-483CEB365DA0.htm)
requires a smoothness level of at least one; a face-local edit instead divides
each selected face into four and does not reset the object's level. ProGPU
therefore filters selected level-zero meshes, rejects an all-ineligible set,
and implements only exact whole-object refinement in this checkpoint.

Autodesk's
[refinement behavior](https://help.autodesk.com/cloudhelp/2014/ENU/AutoCAD-Core/files/GUID-FBC8B54F-C7F0-40E5-93DE-A30EA4B40B2A.htm)
states that whole-object refinement lowers a finite crease by the object's
former smoothness level while an Always crease remains sharp. ProGPU maps each
authored source edge through the existing subdivision provenance chain. A
finite value `s` becomes `max(0, s-L)` after baking level `L`; `-1` remains
`-1`. Each surviving authored crease becomes the ordered `2^L` child records,
including preservation of the original record direction. Zero, exhausted, and
unset crease records are omitted. Boundary edges that were sharp only because
of subdivision boundary rules are not invented as authored crease records.

Whole-object refinement preserves the entity handle, layer and common entity
properties, `BlendCrease`, and the presence or absence of per-control-vertex
texture coordinates. Persisted UVW values are refined in all three components
with double precision. Only the final immutable GPU scene narrows U and V to
its existing float vertex contract; baking never copies that narrowed render
representation back into the ACadSharp document.

## Published subdivision architecture considered

Pixar's
[`TopologyRefiner`](https://graphics.pixar.com/opensubdiv/docs/doxy_html/a01121.html)
documents uniform refinement as an owned hierarchy with per-level vertex,
edge, face, and face-vertex inventories. Its topology-level API exposes child
edges and edge sharpness, supporting the design decision to carry original
edge ancestry explicitly through every uniform level. The
[OpenSubdiv Sdc paper](https://graphics.pixar.com/library/SigAsia2015/paper.pdf)
separates scheme masks, semi-sharp crease computation, and mesh representation.
ProGPU adopts that architectural separation in its existing original
implementation: one representation-neutral subdivision result contains final
topology plus source-edge chains, and the document command separately decides
which authored records persist. ProGPU does not add OpenSubdiv as a dependency
or reproduce its implementation structures.

## Required cross-engine architecture review

| Engine | Primary sources rechecked | Applicability decision |
|---|---|---|
| Skia / SkParagraph | [Skia shaping stages](https://docs.skia.org/docs/dev/design/text_shaper/), [text overview](https://docs.skia.org/docs/dev/design/text_overview/) | Mesh refinement changes no Unicode analysis, shaping, fallback, glyph positions, glyph atlas, DPI, subpixel phase, or text layout. Existing text results remain reusable. Only the changed CAD generation is rebuilt. |
| DirectWrite / Direct2D / Win2D | [Direct2D geometry realizations](https://learn.microsoft.com/en-us/windows/win32/direct2d/geometry-realizations-overview), [Direct2D API overview](https://learn.microsoft.com/en-us/windows/win32/direct2d/the-direct2d-api) | Device-independent source geometry remains separate from device realizations. Adapted: persist complete ACadSharp topology first, then replace the immutable canonical scene once. DirectWrite and Win2D text are unaffected. |
| WebRender / Gecko | [rendering architecture](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html) | WebRender separates a retained Scene from the visible Frame. Adapted: preserve semantic root identity while replacing only the changed generation's mesh primitives; visibility culling and retained upload remain downstream. |
| Vello / Parley | [Vello scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md), [Parley layout model](https://github.com/linebender/parley/blob/main/doc/concept.md) | Late scene encoding and reusable text layout support one canonical scene replacement without invalidating unrelated text. Neither project provides a CAD topology-editing contract to adopt. |
| HarfBuzz | [shape plans](https://harfbuzz.github.io/harfbuzz-hb-shape-plan.html) | Shape plans are keyed by text/font segment properties and have no surface-topology role. Refinement does not change font discovery, variable-font state, fallback, shaping, or glyph caches. |
| WebGPU | [device-loss contract](https://gpuweb.github.io/gpuweb/#device-lost) | WebGPU has no portable document-level subdivision editor. The existing generation replacement, one-resource/one-draw Mesh3D batching, lazy upload, and device-loss rehydration remain unchanged. No shader or wire-ABI change applies. |

Startup/lazy initialization, display-list reuse outside the changed CAD
generation, visibility culling, glyph/texture/path cache ownership, demand
upload, worker preparation, GPU batching, DPI/subpixel/hinting, fallback fonts,
variable-font state, and device-loss recovery are unchanged. Both the managed
and native renderers consume the same rebuilt `CadRecordedMesh3DScene`; a
second native document editor would split ownership and is not applicable.

## Transaction, limits, and complexity

`CadRefineMesh3DCommand` resolves at most 65,536 distinct direct model-space
handles and rejects locked layers, wrong entity families, negative levels,
invalid vertices/faces/crease records/UVW values, non-manifold subdivision,
overflow, and cancellation. It builds and validates every eligible result
before the first mutation. Default aggregate limits are one million topology
corner visits, one million result vertices, one million result faces, and one
million surviving authored crease edges.

For source corner count `C` and level `L`, the exact visited-corner count is
`C*(4^(L+1)-1)/3`, final face-corner count is `C*4^L`, and every original edge
chain contains `2^L+1` vertices. Construction is
`O(sum(V_l + E_l + C_l) + K*2^L)` expected time and bounded retained storage
across levels for vertices `V_l`, edges `E_l`, corners `C_l`, and authored
crease records `K`. Hash tables have the standard collision worst case; the
explicit limits bound allocation before publication. Undo and Redo validate
the entire retained level, blend flag, vertex, face, authored-edge, and UVW
state before replacing it in `O(R)` for retained state `R`.

The shared desktop/browser shell enables Refine for an editable all-MESH whole
selection containing at least one positive level. One edit advances one
document generation, rebuilds the canonical scene, preserves whole-entity
selection, and disables Refine once every selected mesh is level zero.

## Validation and local performance evidence

Focused coverage verifies level-one and level-two topology, mixed level-zero
filtering, exact Undo/Redo, whole-state divergence rejection, locked/wrong-type
and aggregate-limit atomicity, finite/infinite/fractional crease decay and
source-edge direction, omission of implicit boundaries, double-precision UVW,
shared-shell selection retention, canonical scene rebuilding, and DXF/DWG
round trips.

The reproducible Release command
`--mesh3d-refinement-grid 64 --mesh3d-refinement-level 1 --warmup 3
--iterations 12` uses 4,225 source vertices and 4,096 source quads and produces
16,641 vertices, 16,384 editable quads, and 32,768 render triangles in 81,920
topology visits. The 2026-09-01 local run measured 79.9957 ms p50 and 134.7055
ms p95/p99 for refine plus snapshot/scene rebuild; exact retained Undo/Redo
measured 0.4662 ms p50 and 3.5142 ms p95/p99. Generated JSON and Instruments
captures stay under the ignored `artifacts/` tree and are intentionally not
part of the PR; the benchmark command and report schema remain checked in for
reviewer reproduction.

Matched local Xcode Allocations and Time Profiler launches of the same final
Release binary used two warmups and eight iterations. Allocations reported
20,184,384 persistent native-heap plus anonymous-VM bytes, 213,368,192 total
allocated bytes, and 193,183,808 transient bytes. Time Profiler reported no
potential hangs, hang risks, or command-buffer errors. A separate Metal System
Trace launch used one warmup and four iterations, exited successfully, observed
no explicit Metal resources/current allocated size/drawable waits/compiler
spills/errors, and removed its 74,147,829-byte raw trace plus Xcode scratch
after compact local summarization. The Metal template reported system-wide
completion events but no corresponding target submissions, so no target GPU
work is inferred; this is the expected CPU document-edit and scene-compilation
lane.
