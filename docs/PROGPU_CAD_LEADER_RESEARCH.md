# ProGPU.CAD classic LEADER retained-geometry research

Date: 2026-08-30

## Scope and clean-room boundary

This checkpoint covers classic `LEADER` entities with straight or spline-fit
paths, default filled arrows, static custom-arrow block expansion, DIMSTYLE and
typed `DSTYLE` overrides, annotation-derived endpoints, affine block placement,
simple/complex linetypes, exact selection, retained managed/native picture
replay, printing, and DXF/DWG save/reopen behavior. Associated `MTEXT`,
`TOLERANCE`, and `INSERT` references can be attached or detached through a typed
ACadSharp API and are now emitted by both dependency writers.

No third-party renderer implementation was copied, ported, translated, or used
as a source template. The implementation was designed from the public contracts
below. ACadSharp is consumed through its public entity model; the small
dependency change only exposes typed association editing and writes the already
modeled group/handle reference. Original ProGPU implementation provenance is
`CadSnapshotCompiler.Leader.cs`, `CadDocumentSnapshot.cs`,
`CadLineTypeLowerer.cs`, `CadPlanSceneCompiler.cs`, and `CadSelection.cs`.

## Primary sources examined

- Autodesk's [LEADER DXF contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-396B2369-F89F-47D7-8223-8B7FB794F9F3.htm)
  defines the vertex stream, straight/spline path flag, annotation type and hard
  reference, hook direction, horizontal direction, normal, and annotation and
  block offsets. Autodesk's [AcDbLeader reference](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-AcDbLeader.html)
  states that the first vertex owns the arrow, suppresses it when the first
  segment is shorter than twice the arrow size, and permits an associated
  annotation to determine the endpoint.
- Autodesk's [leader method contract](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-__MEMBERTYPE_Methods_AcDbLeader.html)
  and [annotationOffset contract](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-AcDbLeader__annotationOffset.html)
  define the block-reference endpoint as insertion point plus annotation offset,
  and text/tolerance endpoints as annotation location plus offset and the signed
  horizontal `DIMGAP` contribution.
- Autodesk's [leader creation guide](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-DevGuide-Managed/files/GUID-785296F3-C81C-41D3-950E-43DF70BDD503.htm)
  identifies `DIMCLRD`, `DIMSCALE`, arrow type/size, and hook length as DIMSTYLE
  inputs. The [DIMLDRBLK reference](https://help.autodesk.com/view/ACD/2026/ENU/?caas=caas%2Fdocumentation%2FACDLT%2F2014%2FENU%2Ffiles%2FGUID-B4374832-C2B4-4555-900C-693625AC58DE-htm.html),
  [arrow-name list](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-6E09DCCA-313F-4FF4-BB1B-F41B512B9CC9.htm),
  and [custom-arrow contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-5D1F8D41-86EC-481F-ACA0-B169F0B91D00.htm)
  define custom block insertion at the tip, rotation along the first segment,
  scale by arrow size and overall dimension scale, and block-base placement.
- Autodesk's [dimension override DXF contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-6A4C31C0-4988-499C-B5A4-15582E433B0F.htm)
  and [style override reference](https://help.autodesk.com/cloudhelp/2018/ENU/OARXMAC-RefGuide/files/OREFMAC-Dimension_Style_Overrides.html)
  define the `ACAD`/`DSTYLE` typed code-value pairs shared by dimensions,
  leaders, and tolerances.
- Skia's [SkPath overview](https://docs.skia.org/docs/user/api/skpath_overview/)
  and [SkPicture API overview](https://skia.org/docs/user/api/), Direct2D's
  [geometry overview](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-geometries-overview),
  and Win2D's [drawing overview](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/)
  support separating reusable path geometry from paint and replaying recorded
  commands. WebRender's [display-list architecture](https://github.com/servo/servo/wiki/Webrender-Overview)
  and Vello's [retained path encoding](https://github.com/linebender/vello/blob/main/vello_encoding/src/path.rs)
  informed the immutable snapshot and picture boundary, not curve construction.
- DirectWrite's [programming guide](https://learn.microsoft.com/en-us/windows/win32/directwrite/programming-guide),
  HarfBuzz's [shaping contract](https://harfbuzz.github.io/harfbuzz-hb-shape.html),
  Skia's [SkParagraph module](https://github.com/google/skia/tree/main/modules/skparagraph),
  and Parley's [layout model](https://github.com/linebender/parley/blob/main/doc/concept.md)
  were checked as required. A classic leader adds no text-layout state; its
  associated annotation remains an independently retained text/entity object.

## Adopted, adapted, and rejected

Adopted:

- retain one immutable path plus explicit arrow contract and use the first
  segment for arrow direction and suppression;
- resolve `DIMSCALE`, `DIMASZ`, `DIMGAP`, `DIMCLRD`, `DIMLWD`, `DIMLTYPE`, and
  `DIMLDRBLK` from validated typed `DSTYLE` records before capture;
- replace the final persisted vertex from a supported associated annotation,
  while retaining the annotation itself as its normal independent entity;
- expand a bounded static custom arrow definition with its block base, local
  axes, scale, parent affine transform, and semantic root identity.

Adapted:

- straight paths use the existing degree-one immutable spline stream. Spline
  leaders use an original chord-parameterized, piecewise cubic C1 interpolant
  through every authored vertex; the last derivative follows the persisted
  horizontal/hook direction. This preserves analytic retained rendering and
  exact spline selection without claiming AutoCAD's private fit construction;
- the default closed-filled arrow is stored as three points and recorded once;
  custom-arrow children reuse ordinary entity capture, style inheritance,
  selection, native-picture compilation, and print replay;
- the dependency's typed attach/detach operation synchronizes annotation type
  and reference, rejects cross-document or unsupported associations, and both
  writers emit the reference already understood by their readers.

Rejected:

- per-frame ACadSharp traversal, polyline tessellation of the retained curve,
  per-segment render commands, reflection-based override or association access,
  and a new shader/native ABI;
- silently drawing malformed leaders, partially publishing exhausted control
  streams, or substituting an arbitrary arrow for an unavailable custom block;
- synthesizing AutoCAD's complete built-in arrow-name catalog without matched
  conformance fixtures.

## Complexity and validation contract

For `V` vertices, straight capture uses `O(V)` time/storage. Spline-fit capture
uses `3(V-1)+1` control points and `O(V)` time/storage. Both are bounded by
`MaxLeaderVerticesPerEntity` and the document-wide
`MaxLeaderControlPoints`; failure rolls all leader path state back before an
unsupported diagnostic is published. Static custom-arrow expansion has the
existing bounded block complexity. Plan recording emits one path plus at most
one default-arrow path; patterned paths use the shared transactional linetype
budgets. Camera-only replay does not revisit ACadSharp. Point and box selection
reuse the exact rational spline evaluator plus an exact filled-triangle test and
allocate nothing after warmup.

Focused tests cover straight/spline paths, terminal derivative, arrow size and
suppression, typed DSTYLE paint/scale, custom arrows, nested affine placement,
patterned paths, exact selection, transactional budgets, annotation endpoints,
DXF/DWG association and geometry round trips, native-picture/print reuse, and
zero-allocation warm selection. Licensed visual differentials for the private
spline-fit curve, the complete built-in arrow catalog, annotative scaling,
paper-space/UCS cases, and malformed-file fuzzing remain required before
declaring classic LEADER fully verified.
