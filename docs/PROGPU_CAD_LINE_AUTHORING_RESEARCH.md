# ProGPU.CAD LINE authoring research record

## Scope and primary sources

This checkpoint adds a bounded, shared desktop/browser `LINE` command and the
reusable point-acquisition state needed by later drawing commands. The work was
designed clean-room from public behavior and contracts:

- Autodesk's [LINE command reference](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-MAC-Core/files/GUID-9421191D-F461-41BE-AC14-5D4FFB07178D.htm)
  specifies a sequence of separate line segments, repeated next-point prompts,
  Undo, Close after at least two segments, and Enter or Escape completion.
- Autodesk's [line drawing workflow](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-57CDDB6C-B12B-46CE-B9C5-22EFC17258FF.htm)
  confirms contiguous endpoint reuse and independent resulting LINE objects.
- Autodesk's [direct-distance entry contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-MAC-Core/files/GUID-BF4A06D8-2D66-427D-9460-B62A479B22B4.htm)
  defines a typed distance along the live cursor direction.
- Autodesk's [Polar Tracking settings](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-D7CBB7B0-9140-4C53-88EF-08EAA09FA9D7.htm)
  and [POLARMODE contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-D91628CC-9975-4DBF-8D02-10B23A6F3ED5.htm)
  define absolute versus last-segment-relative angle measurement.
- Autodesk's [layer workflow](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-GettingStarted/files/GUID-FA005756-B8F5-4A78-988F-31335A68D77C.htm)
  establishes current-layer and ByLayer defaults for newly drawn objects.

No third-party implementation source was copied or translated. Approved source
provenance is the existing ProGPU-owned coordinate parser, direct-distance,
object-snap, grid, Ortho, polar, history, immutable snapshot, retained picture,
and ACadSharp entity-property paths in this repository.

## Adopted and adapted behavior

`CadLineAuthoringSession` owns only finite accepted WCS points. The first point
establishes the base; every later distinct point adds one contiguous segment.
`U` removes only the latest in-command segment in O(1), retaining its start as
the next base. Close requires two accepted segments and appends the first point
once. Enter, Escape, or Finish ends the sequence without adding a closing edge.
A sequence with no accepted segment changes no document generation.

Each finished edge becomes a separate ACadSharp `Line`. Current layer, color,
linetype, linetype scale, and lineweight are captured together on first command
application, and a locked current layer fails before insertion. This matches the
observable entity and current-property contract.

ProGPU adapts completion to its generation-safe editor architecture: accepted
segments remain a bounded retained transient picture, then the complete
sequence is published as one `CadAddLineSequenceCommand`. Consequently global
Undo removes or restores the finished sequence atomically, while `U` remains
segment-local during the active command. This avoids partial document
generations, repeated complete scene compilation, and half-published batches.

## Shared point acquisition and relative polar tracking

LINE reuses the same precedence as MOVE/COPY: typed coordinates bypass pointer
constraints; pointer input resolves object snap first, followed by the active
grid/Ortho/polar path and raw WCS input. A positive scalar after the first point
uses the live post-constraint cursor direction and preserves exact distance.

The polar query now accepts an explicit finite nonzero previous-segment vector.
When the profile selects last-segment-relative measurement, incremental angles
are offset from that actual authored direction. The first and second LINE point
cannot invent such a basis, so relative incremental tracking fails closed until
a real segment exists. Additional angles remain absolute and non-incremental,
as Autodesk documents. MOVE/COPY continue using the absolute overload and do
not infer a segment from selection or prior displacement state.

## Complexity, ownership, and rendering applicability

The default command limit is 65,536 segments. Point acceptance is amortized
O(1), in-command Undo is O(1), completion and Apply/Undo/Redo are O(S), and
retained storage is O(S) for S segments. Accepted-segment preview is rebuilt
only when a point is accepted or the viewport changes; steady pointer motion
replays one retained picture and draws one rubber band. No entity, snapshot,
upload, or native call occurs during acquisition.

The completed ordinary LINE entities reuse the existing canonical retained
line lowering and managed/native scene compilers. This checkpoint changes no
shader, GPU resource, native ABI, C++ renderer, text, glyph, image, or device-
loss contract. The managed/native parity audit therefore finds no paired native
implementation change applicable; output parity remains covered by the existing
line replay path.

The mandatory cross-engine rendering/text architecture gate is not triggered:
this is host-neutral authoring state over an unchanged renderer, not a rendering,
scene-compilation, text, cache, startup, or GPU-pipeline change.

## Verification and remaining work

Focused Release regressions cover bounded state, degenerate and non-finite
rejection, Close, current-property inheritance, locked-layer atomic failure,
one-entry Undo/Redo, DXF and DWG round trips, typed and pointer interaction,
object-snap precedence, exact relative-polar direct distance, shared controls,
`U`, Close, Enter/Escape completion, and profile-only relative-mode changes.
The complete macOS arm64 Release ProGPU.CAD suite passes 1,090/1,090.
Focused package-content validation passes for the paired
`ACadSharp.ProGPU.0.1.0-preview.62` and `ProGPU.CAD.0.1.0-preview.62` packages;
an isolated consumer resolves the fork identity, rejects upstream ACadSharp,
builds without warnings, and creates an AC1032 document.

POLYLINE authoring is now covered separately by
`PROGPU_CAD_POLYLINE_AUTHORING_RESEARCH.md`. Command chaining from a previous
command endpoint, temporary overrides, expressions and drawing units, 3D
UCS/arbitrary-camera acquisition, object-snap tracking, visual goldens, and
dense-sequence p50/p95/p99 evidence remain later LINE editor gates.
