# ProGPU.CAD POLYLINE authoring research record

## Scope and primary behavior contracts

This checkpoint adds bounded centerline `PLINE` authoring to the shared
desktop/browser editor. It creates one planar ACadSharp `LwPolyline` containing
straight and analytic circular-arc segments. The design is clean-room and uses
public contracts rather than third-party implementation source:

- Autodesk's [PLINE command reference](https://help.autodesk.com/cloudhelp/2027/ENG/AutoCAD-Core/files/GUID-11883C70-6435-4F80-8FB4-F6E933B8FD94.htm)
  defines one 2D polyline object, line/arc modes, tangent continuation, signed
  included angles, `Undo`, `Close`, and `PLINEGEN` behavior.
- Autodesk's [LWPOLYLINE DXF contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-DXF/files/GUID-748FC305-F3F2-4F74-825A-61F04D757A50.htm)
  defines closed and Plinegen flags, OCS vertices, elevation, widths, bulges,
  and extrusion normal.
- The approved in-repository ACadSharp.ProGPU sources
  `external/ACadSharp/src/ACadSharp/Entities/LwPolyLine.cs`,
  `LwPolyline.Vertex.cs`, and `Header/CadHeader.cs` are authoritative for the
  object model and current `PLINEGEN`/`PLINEWID` properties.
- Existing ProGPU-owned `CadPlanSceneCompiler.cs`, `CadSampleCanvas.cs`, and
  `CadEditing.cs` are the authoritative rendering, point-acquisition, and
  generation-safe history provenance. This is a permitted cross-feature port
  and composition of original ProGPU code under the repository clean-room
  policy.

No third-party source text, helper shape, control flow, naming scheme, or data
encoding was copied or translated.

## Adopted behavior and deliberate limits

`CadPolylineAuthoringSession` retains finite WCS points and one DXF bulge per
vertex. The first point fixes one WCS-Z plane. Line mode stores a zero bulge;
tangent-arc mode computes the unique nondegenerate circular arc whose start
tangent continues the actual endpoint tangent of the preceding line or arc.
The reusable explicit-angle API maps signed included angle `a` to the standard
bulge `tan(a / 4)`, with positive angles counterclockwise and negative angles
clockwise. Closure is a flag and terminal bulge, never a duplicate vertex.

The shared shell exposes `PLine`, line/arc mode, `U`, `Close`, and finish. Click
or invariant typed absolute/relative Cartesian or polar input uses the existing
object-snap, grid, Ortho, polar, PolarSnap, and direct-distance acquisition
pipeline. Relative polar tracking and direct distance use the true endpoint
tangent after either a line or arc. Pointer input after a typed nonzero-Z first
point stays on that exact plane.

Completion creates exactly one `LwPolyline` and one history entry. First Apply
captures current CLAYER, CECOLOR, CELTYPE, CELTSCALE, CELWEIGHT, PLINEGEN, and
PLINEWID together. A locked current layer, invalid current linetype scale, or
nonzero PLINEWID fails before document mutation. The width rejection is
intentional: the current retained renderer rejects filled wide-polyline
geometry, so authoring a cosmetic centerline would silently change CAD output.

The following observable PLINE options remain explicit future work: Halfwidth,
Width, Length, interactive Angle, Center, Direction, Radius, Second point,
command chaining from the prior endpoint, temporary overrides, expressions and
drawing units, legacy PLINETYPE output, 3D polyline authoring, and arbitrary
UCS/camera acquisition. `TryAcceptArcPoint` provides the bounded core signed-
angle seam, but the shared prompt does not yet expose the Angle subdialog.

## Retention, quality, and complexity

The default limit is 65,536 segments. Point acceptance is amortized O(1),
segment-local Undo is O(1), tangent and bulge calculations are O(1), and
snapshot creation plus Apply/Undo/Redo are O(S) time and storage for S segments.
No document entity, generation, snapshot, upload, or native call occurs while
points are being acquired.

Accepted segments are rebuilt only after an accepted point or viewport change
and replay as one retained screen-space picture. Its arcs use ProGPU's analytic
`ArcSegment`; they are not flattened. The live pointer arc remains allocation-
free and draws bounded line segments because mutable retained geometry cannot
be changed behind a recorded command. Its step count targets at most 0.25
physical-pixel circular sagitta from current display scale and is capped at 512
segments. This approximation is transient only: the accepted preview and final
entity both retain analytic arcs and exact DXF bulges.

## Mandatory cross-engine rendering and text audit

The rendering applicability gate was triggered because the transient accepted
PLINE preview changed from separate lines to one retained analytic path. The
following primary sources were examined:

- [Skia `SkPath`](https://api.skia.org/classSkPath.html) retains typed move,
  line, conic/cubic, arc, and close topology and exposes explicit volatility and
  generation behavior. ProGPU adopts retained typed topology and an explicit
  close state, while keeping its own `PathGeometry` and cache ownership.
- [Direct2D `ID2D1GeometrySink::AddArc`](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1geometrysink-addarc)
  and [Win2D `CanvasPathBuilder`](https://microsoft.github.io/Win2D/WinUI2/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasPathBuilder.htm)
  retain lines and arcs in an open or closed path. ProGPU adopts the endpoint,
  radius, large-arc, and sweep representation already native to its vector
  stack; it does not add a platform geometry bridge.
- [Vello's path encoding](https://github.com/linebender/vello/blob/main/vello_encoding/src/path.rs)
  retains compact path tags, tangents, and closed topology for later GPU stroke
  processing. ProGPU adopts the separation between authored topology and later
  rendering, but keeps exact DXF bulges and existing canonical ProGPU lowering.
- [WebRender](https://github.com/servo/webrender) and its
  [display-list architecture overview](https://github.com/servo/servo/wiki/Webrender-Overview)
  informed the decision to keep accepted geometry retained and rebuild it only
  on accepted state or viewport changes rather than pointer frames.
- [SkParagraph](https://github.com/google/skia/tree/main/modules/skparagraph),
  [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/direct-write-portal),
  [Parley](https://github.com/linebender/parley), and
  [HarfBuzz](https://github.com/harfbuzz/harfbuzz) were examined as required.
  Their shaping, bidi, line-breaking, glyph-positioning, fallback, and text-
  layout caches are not applicable because this checkpoint changes no text,
  glyph, font, shaping, or layout path. Existing ProGPU text output is untouched.

Foreign APIs and source structures were rejected. The resulting code uses
ProGPU's typed reflection-free state, ownership, invalidation, and rendering
contracts.

## Managed/native applicability and verification

Completed `LwPolyline` entities already flow through the paired managed/native
scene compiler contract and the canonical analytic polyline representation.
This checkpoint changes no entity lowering, shader, C ABI, generated wire
record, device resource, cache generation, or C++ implementation. The accepted
and live previews are shared-host editor overlays and never cross the native
boundary. The paired-renderer audit therefore finds no native code change
applicable; DXF/DWG round trips and existing managed/native replay cover the
completed entity path.

Focused Release regressions cover line plus tangent-arc topology, exact arc
endpoint tangents, signed-angle bulges, tangent closure, no duplicate closing
vertex, planar and finite rejection, segment bounds, current properties,
PLINEGEN, locked-layer and PLINEWID atomic failure, one-entity/one-history
Apply/Undo/Redo, typed and pointer interaction, shared buttons and keys, exact
relative-polar direct distance after an arc, nonzero-Z pointer acquisition, and
DXF/DWG round trips. Complete-suite and package evidence is recorded after the
final validation run for this checkpoint. The complete macOS arm64 Release
ProGPU.CAD suite passes 1,090/1,090, and the portable `net10.0` desktop host
builds with zero warnings. Focused package-content validation passes for
`ACadSharp.ProGPU.0.1.0-preview.62` and
`ProGPU.CAD.0.1.0-preview.62`; an isolated consumer resolves the reviewed fork
identity, rejects upstream ACadSharp, builds without warnings, and creates an
AC1032 document. The pack itself reports the existing ACadSharp multi-target
and source-warning baseline; no new ProGPU.CAD warning is introduced.

Visual goldens, dense-sequence p50/p95/p99 authoring measurements, and the
remaining command options above are still required before PLINE authoring can
be called complete.
