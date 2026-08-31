# ProGPU.CAD ARC authoring research and conformance record

Status: implemented checkpoint, 2026-08-31

## Scope and clean-room provenance

This checkpoint adds every independent plan-view Autodesk ARC construction
family to the shared desktop/browser shell:

- three circumference points;
- Center/Start/End, Center/Start/Angle, and Center/Start/Chord;
- Start/Center/End, Start/Center/Angle, and Start/Center/Chord;
- Start/End/Angle, Start/End/Direction, and Start/End/Radius.

`Continue` is not approximated. It depends on a separate drawing-wide last-
created entity and command-chaining contract, including the actual terminal
tangent of LINE, ARC, and PLINE. No such implicit global command state is added
to this bounded authoring session.

No third-party source implementation was copied, ported, translated, or used
as a structural template. The implementation was independently derived from
the public command and DXF contracts below. Approved in-repository ProGPU
provenance is:

- `src/ProGPU.CAD/CadCircleAuthoring.cs`, `CadLineAuthoring.cs`, and
  `CadPolylineAuthoring.cs` for original ProGPU transaction, prompt-recovery,
  current-property, and bounded input patterns;
- `src/ProGPU.CAD/CadSnapshotCompiler.cs`, `CadPlanSceneCompiler.cs`, and
  `CadDocumentSnapshot.cs` for the existing managed analytic ARC contract;
- `src/ProGPU.CAD.Native` plus existing managed/native retained-picture tests
  for the established equivalent native ARC replay;
- `src/ProGPU.CAD.Sample/CadSampleCanvas.cs` and `CadSampleView.cs` for the
  existing typed point-acquisition and dynamically themed shared shell.

ACadSharp is consumed only through its reviewed in-repository `Entities/Arc.cs`
public model. No ACadSharp implementation text is included in ProGPU files.

## Authoritative behavior sources

- [Autodesk ARC command](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-30ECFD30-A1D6-4D60-9DD1-B487603F6772.htm)
  defines the point/center/end/angle/chord/direction/radius families, default
  counterclockwise construction, negative-angle clockwise construction,
  positive-chord minor versus negative-chord major selection, positive-radius
  minor versus negative-radius major selection, transient Ctrl-drag clockwise
  construction with counterclockwise restored on release, and tangent Continue.
- [Autodesk ARC DXF records](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-0B14D8F1-0EBA-44BF-9108-57D8CE614BC8.htm)
  define OCS center groups 10/20/30, radius group 40, optional thickness group
  39, start/end angle groups 50/51, and extrusion groups 210/220/230.
- [Autodesk Arc StartAngle](https://help.autodesk.com/cloudhelp/2024/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Arc_StartAngle.html)
  confirms radians at the API boundary and counterclockwise positive angles
  from the OCS X axis when viewed down the positive normal.

For Axis-Z plan arcs, OCS and WCS XY coincide. The command stores one finite
center, positive radius, normalized start/end angles, `Normal = AxisZ`, and the
accepted first-point WCS-Z elevation. Current CLAYER, CECOLOR, CELTYPE,
CELTSCALE, CELWEIGHT, and THICKNESS are read atomically on first Apply. Nonzero
THICKNESS fails before mutation because existing retained lowering represents
the planar analytic curve and must not silently discard extruded side geometry.

## Cross-engine architecture review

- [Skia `SkPath` reference](https://skia.googlesource.com/skia/+/1321a3d/site/user/api/SkPath_Reference.md)
  documents endpoint/radii arc selection by sweep direction and small/large
  route and retains the result as bounded rational conics. Adopted: retain one
  analytic small/large and sweep interval rather than authoring a point
  polyline. Rejected: converting the CAD entity into Skia-specific conics.
- [Direct2D path geometries](https://learn.microsoft.com/en-us/windows/win32/direct2d/path-geometries-overview)
  retain an arc segment with endpoint, radii, sweep direction, and small/large
  choice inside a reusable path. [Win2D](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/)
  exposes the same Direct2D geometry model. Adopted: the transient point-final
  preview records one ProGPU `ArcSegment`, while the committed entity continues
  through the existing retained CAD ARC path.
- [Vello scene encoding](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  encodes reusable shapes and transforms for GPU processing.
  [WebRender](https://github.com/servo/webrender) consumes retained display
  lists in a GPU renderer. Adapted: pointer motion changes only bounded prompt
  state and one transient analytic command; it does not mutate ACadSharp,
  compile a snapshot, upload a resource, or cross the native boundary.
- [SkParagraph](https://github.com/google/skia/tree/main/modules/skparagraph),
  [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/direct-write-portal),
  [Parley](https://github.com/linebender/parley), and
  [HarfBuzz](https://harfbuzz.github.io/what-is-harfbuzz.html) were examined as
  required. They shape or lay out text and do not participate in ARC
  construction, font fallback, glyph caching, or this geometry-only preview.
  No text-stack change is applicable.

The existing managed and native CAD renderers already consume the same
`CadArcPrimitive`, including analytic bounds, selection, object snaps,
linetypes, printing, persistence, and device-loss replay. This checkpoint adds
host-neutral construction, shared-shell prompts, and one document command. It
changes no shader, canonical shader resource, packed stream, C ABI, GPU cache,
upload, atlas generation, device identity, or native algorithm. A paired native
implementation change is therefore not applicable; existing matched ARC replay
regressions remain authoritative.

## Geometry and state contract

`CadArcAuthoringSession` owns a fixed two-point buffer. Every mode accepts two
construction points, then resolves a final point or scalar without changing the
session. Start/End/Direction accepts either a WCS direction point or a numeric
direction angle. The other angle/chord/radius modes require their exact signed
scalar after the two points. The shared shell parses signed invariant scalars;
included and direction angles are entered in degrees and converted at the host
boundary using current ANGDIR. Direction angles use the captured current-UCS
plus ANGBASE basis. Host-neutral solver angles are radians.

After the two fixed points of Center/Start/End, Start/Center/End, or point-
defined Start/End/Direction, the session exposes an explicit transient
clockwise boolean. Center constructions retain the same center and radius and
exchange their geometric endpoints to store the clockwise complement as one
positive counterclockwise DXF interval. Direction retains an already-clockwise
solve; otherwise it selects the same circle's clockwise route. Three-point ARC
ignores the override because its second circumference point uniquely selects
the route. Scalar angle, chord, direction-angle, and radius inputs retain their
signed contracts and do not query keyboard state.

All constructions are normalized constant-work formulas:

- 3P translates the start to the origin, scales the two remaining vectors by
  their largest absolute component, and solves the normalized perpendicular-
  bisector determinant. The orientation determinant selects the unique stored
  interval that passes through the second point.
- Center/Start/End projects the final point to its ray from center, preserving
  the start radius rather than forcing the arc through the supplied endpoint.
- Center/Start/Angle rotates by the exact signed included angle. A clockwise
  construction is stored as the same geometric counterclockwise DXF interval
  with exchanged geometric endpoints.
- Center/Start/Chord uses `2 asin(|chord| / (2 radius))`; a positive value
  selects that minor counterclockwise sweep and a negative value selects its
  `2π - sweep` major complement.
- Start/End/Angle uses the chord midpoint plus the signed perpendicular offset
  `halfChord * cot(angle / 2)`. This covers clockwise/counterclockwise and
  minor/major intervals with one formula.
- Start/End/Direction intersects the start-tangent normal with the chord
  perpendicular bisector using scaled dot products. Numeric directions remain
  vectors and are never added as a unit point to a large WCS origin.
- Start/End/Radius uses the stable perpendicular height
  `sqrt((radius - halfChord) * (radius + halfChord))`; sign chooses the minor
  or major center side required by the command contract.

Every result must have finite geometry, a positive radius no greater than
`float.MaxValue` for the existing retained lowerer, and a nonzero sweep less
than one turn with numerically distinct endpoints. Duplicate, off-plane,
nonfinite, collinear, coincident-center, tangent-collinear, impossible chord,
impossible radius, zero-angle, and full-turn inputs fail closed. Final failure
does not consume accepted state.

The point pipeline preserves existing precedence: exact object snap, Ortho or
acquired polar path, active grid/PolarSnap, then raw pointer. Explicit typed
absolute/relative Cartesian or polar coordinates bypass pointer constraints.
Bare positive values remain shared direct-distance input during point prompts;
at an ARC scalar prompt the same bounded grammar is interpreted according to
that mode, including negative major-arc values. ARC does not infer a previous
segment for relative polar tracking. The shared host passes current Ctrl state
only through the eligible point-final seam, refreshes an existing pointer
preview on key transitions, and applies the same route to pointer clicks,
typed coordinates, and point-prompt direct distance.

One successful final solve adds one ACadSharp `Arc` as one history operation and
one content generation. Undo removes that same entity; Redo restores the same
identity and the properties captured on first Apply even if header defaults
have since changed.

## Complexity and performance contract

- retained prompt storage: O(1), exactly two `CadPoint3D` slots;
- bounded signed-scalar parsing: O(L) for at most 128 UTF-16 code units;
- point validation and every construction solve: O(1);
- point-final pointer preview: O(1), one analytic `ArcSegment` plus one guide,
  with bounded transient objects and no tessellation, document mutation,
  snapshot compilation, upload, or managed/native call;
- scalar prompts: no speculative model or renderer work before acceptance;
- apply, Undo, and Redo: O(1), one entity and one history record;
- completed replay: the unchanged existing analytic managed/native ARC path.

The checked-in Release benchmark
`artifacts/benchmarks/cad-arc-authoring-clockwise.json` measures batches of
65,536 mixed Center/Start/End, Start/Center/End, and Start/End/Direction
sessions over five warmups and forty iterations on macOS arm64/.NET 10.
Default point-final routes are p50 3.0400 ms, p95 3.4053 ms, and p99 3.6508 ms;
Ctrl-clockwise routes are p50 3.1334 ms, p95 3.3765 ms, and p99 3.6994 ms.
The complete session lifecycle allocates about 6.82 MB per batch, including
65,536 session objects and their fixed two-point arrays. This is bounded solver
throughput evidence, not a rendering-speed claim. Visual goldens, arbitrary
UCS/camera construction, expressions and units, other temporary overrides,
and command chaining/Continue remain future gates.

## Verification

Focused tests cover all ten modes, clockwise interval canonicalization,
positive/negative minor/major rules, normalized large-WCS 3P and direction
solves, bounded scalar parsing, ANGDIR, exact analytic preview, shared point
constraints and direct distance, nonzero elevation, invalid geometry,
recoverable final/preflight failures, property capture, identity through
Undo/Redo, all shared selector entries and Escape, eligible and ineligible Ctrl
state, default-overload compatibility, same-circle endpoint exchange, already-
clockwise Direction retention, live preview refresh, pointer and typed point-
final acceptance, and DXF/DWG round trips. The
publication gates passed on 2026-08-31:

- focused ARC authoring tests: 54/54 in Debug and Release;
- complete .NET 10 CAD suite: 1,391/1,391 in Debug and Release;
- Release desktop build: 0 warnings, 0 errors;
- `ProGPU.CAD` and `ACadSharp.ProGPU` packages built at
  `0.1.0-preview.62`, and the isolated package consumer restored, built with
  0 warnings and 0 errors, and created an AC1032 document.
