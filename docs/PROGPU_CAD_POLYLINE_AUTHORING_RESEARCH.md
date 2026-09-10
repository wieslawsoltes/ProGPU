# ProGPU.CAD POLYLINE authoring research record

## Scope and primary behavior contracts

This record covers bounded `PLINE` authoring in the shared
desktop/browser editor. It creates one planar ACadSharp `LwPolyline` containing
straight and analytic circular-arc segments, including the interactive Width,
Halfwidth, line-mode Length, arc-mode Angle, Center, Direction, Radius,
and Second point options, and the nested Angle/Center, Angle/Radius/chord-
direction, Center/Angle, and Center/signed-Length workflows. The design is clean-room and uses
public contracts rather than third-party implementation source:

- Autodesk's [PLINE command reference](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-11883C70-6435-4F80-8FB4-F6E933B8FD94.htm)
  defines one 2D polyline object, line/arc modes, tangent continuation, signed
  included angles, fixed-center endpoint projection, tangent directions,
  radius and three-point constructions, nested angle/center/radius/length
  prompts, the Direction-tip Ctrl clockwise override, Width/Halfwidth state, tangent Length,
  `Undo`, `Close`, and `PLINEGEN` behavior.
- Autodesk's [ARC command reference](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-30ECFD30-A1D6-4D60-9DD1-B487603F6772.htm)
  independently confirms positive counterclockwise and negative clockwise
  angles, center/start/end projection, signed center/start chord minor/major
  selection, start/end/direction tangency, positive-radius minor and negative-
  radius major Start/End/Radius selection, the transient Ctrl clockwise drag
  override, and three-point circular construction behavior used by the
  differential tests.
- Autodesk's [SetWidth API contract](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-ActiveX-Reference/files/GUID-ED45F9D1-AE03-4DF0-9F2D-2019BD42CD52E.htm)
  confirms per-segment start/end width ownership, while the
  [PLINEWID system-variable contract](https://help.autodesk.com/view/ACD/2026/ENU/?caas=caas%2Fdocumentation%2FACDLT%2F2014%2FENU%2Ffiles%2FGUID-35EEB892-15C9-442F-847D-2F4DC52E9690-htm.html)
  defines the drawing-level default retained for later polyline creation.
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
- Existing original ProGPU-owned `CadArcAuthoring.cs` is the exact in-repository
  normalized solver provenance for the new PLINE Angle, Center, Direction,
  Radius, Second point, and nested Angle/Center/Radius/Length calculations.
  PLINE keeps vertex order and a signed bulge while ARC canonicalizes persisted geometry to a positive
  counterclockwise interval; matched differential tests compare center, radius,
  and absolute sweep.

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

The shared shell exposes `PLine`, line/arc mode, a typed arc-option selector,
`Width`, `Halfwidth`, `Length`, `U`, `Close`, and finish. In Arc mode, Angle
stores a signed included angle before accepting an endpoint; Center fixes the
start-center radius and projects the endpoint input onto its ray; Direction
uses the start-to-direction-point vector as the exact starting tangent; Radius
uses radius sign to select the minor or major interval; and Second point
solves the unique oriented circumcircle through start, second, and end. These
explicit options work immediately after the first vertex; only the default
tangent endpoint requires a preceding segment. Click
or invariant typed absolute/relative Cartesian or polar input uses the existing
object-snap, grid, Ortho, polar, PolarSnap, and direct-distance acquisition
pipeline. Relative polar tracking and direct distance use the true endpoint
tangent after either a line or arc. Pointer input after a typed nonzero-Z first
point stays on that exact plane.

At the Angle endpoint prompt, Center rotates the current start about a supplied
fixed center by the already accepted signed angle, while Radius accepts a
positive radius and then a chord-direction point; the chord length is exactly
`2r |sin(a/2)|`. At the Center endpoint prompt, Angle rotates the start about
that fixed center by a signed included angle, while Length accepts a signed
chord: positive selects the minor counterclockwise interval and negative the
major counterclockwise interval. Each final point or scalar solve is O(1),
validates the resolved endpoint and retained float radius before mutation, and
keeps the active prompt unchanged after failure. Shared typed keywords,
selector/button availability, pointer acquisition, viewport validation, and
live preview all use the same state machine.

At point-final Center, Direction, and Radius prompts, holding Ctrl selects the
clockwise circular solution without changing the retained construction inputs.
Radius sign and direction are independent: positive radius selects the minor
interval, negative radius selects the major interval, and Ctrl negates the
selected interval's direction. Center and Direction keep the same solved
circle while Ctrl selects its clockwise counterpart. The host-neutral session
receives this modifier as an explicit boolean; it does not query keyboard state.

Width and Halfwidth use a two-scalar prompt. The prior ending width is the next
starting default; the accepted starting width is the ending default; and the
new ending width becomes the uniform default for following segments and the
drawing's resulting `PLINEWID`. Halfwidth converts each scalar to full DXF
width exactly once. Length accepts a finite positive scalar in line mode and
extends from the current vertex along the actual normalized endpoint tangent
of the preceding line or arc.

Completion creates exactly one `LwPolyline` and one history entry. First Apply
captures current CLAYER, CECOLOR, CELTYPE, CELTSCALE, CELWEIGHT, PLINEGEN, and
PLINEWID together. A uniform authored profile collapses to `ConstantWidth`;
a straight variable profile retains explicit per-vertex start/end widths and
disables PLINEGEN because tapered segments do not support generated pattern
continuation. Undo restores both entity state and the prior PLINEWID; Redo
reuses entity identity and republishes the resulting default. ACadSharp feature
commit `64f4feda` adds `$PLINEWID` to the default DXF header allow-list so the
same state now round-trips in both DXF and DWG. A locked current layer or invalid current linetype scale
fails before document mutation. Nonzero PLINEWID publishes through the exact
filled or FILLMODE-off outline contract in
`PROGPU_CAD_WIDE_POLYLINE_RESEARCH.md`.

The following observable PLINE options remain explicit future work: command
chaining from the prior endpoint, other temporary overrides, expressions and
drawing units, legacy PLINETYPE output, 3D polyline authoring, and arbitrary
UCS/camera acquisition. Variable-width arc publication also remains explicitly
unsupported: exact tapered circular-arc boundaries are spirals, and signed
inner boundaries can cross the arc center. The authoring session therefore
rejects any arc/variable-profile combination before snapshot or document
mutation rather than lowering a cosmetic approximation.

## Retention, quality, and complexity

The default limit is 65,536 segments. Point, scalar/control prompt, width-prompt,
and length acceptance are amortized O(1), segment-local Undo is O(1), every
normalized circular solve and bulge calculation is O(1), and
snapshot creation plus Apply/Undo/Redo are O(S) time and storage for S segments.
Prefix uniform-width metadata and one accepted-arc counter make compatibility
checks O(1); an early benchmark found and eliminated an O(S²) width-option scan.
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

Focused Debug and Release regressions cover line plus tangent-arc topology, exact arc
endpoint tangents, signed-angle bulges, tangent closure, no duplicate closing
vertex, planar and finite rejection, segment bounds, current properties,
Width/Halfwidth default propagation, Length after a line or arc, scalar prompt
recovery, variable-arc fail-closed behavior, uniform-width collapse, explicit
start/end width publication, PLINEGEN suppression, atomic PLINEWID restoration,
locked-layer and FILLMODE-off atomic failure,
all five explicit arc prompt state machines, all four nested arc workflows,
differential center/radius/sweep parity with the authoritative ARC command,
Center endpoint projection, signed chord minor/major selection, large-WCS
center/angle stability, recoverable nested failure, contextual shared controls,
independent signed-radius minor/major and transient Ctrl clockwise selection,
live modifier refresh, shared typed/pointer arc-option interaction, one-entity/one-history
Apply/Undo/Redo, typed and pointer interaction, shared buttons and keys, exact
relative-polar direct distance after an arc, nonzero-Z pointer acquisition, and
DXF/DWG entity-width and PLINEWID round trips. The focused authoring gate passes
38/38 in both configurations; the complete suite passes 1,381/1,381 in both Debug and Release
on macOS arm64. The Release `ACadSharp.ProGPU` and `ProGPU.CAD`
packages build at `0.1.0-preview.62`; an isolated consumer resolves the reviewed
`ACadSharp.ProGPU` identity, builds with zero warnings, and creates an AC1032
document.

The reproducible Release benchmark writes its ignored local report to
`artifacts/benchmarks/cad-polyline-authoring-options.json` and measures the maximum
65,536-segment bound over five warmups and forty iterations on macOS arm64/.NET
10. Inherited-width acquisition plus snapshot completion is p50 4.4878 ms,
p95 17.3738 ms, and p99 21.5204 ms; changing Width on every segment is p50
3.6531 ms, p95 15.6571 ms, and p99 20.9222 ms. An explicit 60-degree Angle
arc at every segment is p50 9.0618 ms, p95 21.6800 ms, and p99 28.4260 ms.
The nested Center/Angle solve at every segment is p50 13.6242 ms, p95
23.7172 ms, and p99 28.3498 ms. A clockwise major-radius solve at every
segment is p50 11.8405 ms, p95 23.7980 ms, and p99 29.6710 ms. All five
allocate about 17.5 MB per
completed maximum-size snapshot, dominated by geometrically grown retained
arrays and the immutable O(S) snapshot copies. The result is complexity and
bounded-throughput evidence, not an interactive rendering-speed claim; the
existing matched managed/native/print wide-polyline measurements remain the
rendering evidence.

Visual goldens and the remaining command options above are still required
before PLINE authoring can be called complete.
