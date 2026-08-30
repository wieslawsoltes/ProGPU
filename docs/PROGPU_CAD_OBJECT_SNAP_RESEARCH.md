# ProGPU.CAD Object Snap Research

Status: implemented foundation, 2026-08-30

## Scope and clean-room sources

This slice adds running Endpoint, Midpoint, Center, Node, Quadrant, actual
geometric Intersection, Nearest, and reference-aware Perpendicular acquisition
to the shared desktop/browser MOVE and COPY point prompts. It does not copy,
translate, or imitate another engine's source code, helper structure, naming,
tables, or control flow. The implementation was
designed from public behavior contracts and existing ProGPU-owned immutable
geometry:

- Autodesk's current [Object Snaps modifier reference](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-76B81C1A-373E-4BCD-975A-789FB36C89FE.htm)
  documents selecting the precise snap point closest to the cursor, visual
  markers/tooltips, persistent running modes, and cycling available snaps.
- Autodesk's [Drafting Settings object-snap reference](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-50383F73-4F23-4F70-B4FC-52D5748D80AF.htm)
  defines Quadrant on arcs, circles, ellipses, and elliptical arcs and applies
  selected running modes to the closest eligible aperture point.
- Autodesk's current [Web object-snap reference](https://help.autodesk.com/cloudhelp/ENU/AutoCAD-Web-Help/files/Drafting-and-Creating/AutoCAD_Web_Help_Drafting_and_Creating_Osnap_html.html)
  independently lists the same four Quadrant families while listing polyline
  arcs only for other modes; ProGPU therefore does not silently broaden
  Quadrant to bulge segments. It defines Nearest on arcs, circles, ellipses,
  elliptical arcs, lines, points, polylines, rays, splines, and xlines.
- Autodesk's current [Drafting Settings reference for macOS](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-MAC-Core/files/GUID-06D81B23-B171-4F33-920B-4609E22DD9E5.htm)
  independently gives the same Nearest family list and defines Perpendicular
  on arcs, circles, ellipses/elliptical arcs, lines, polylines, rays, splines,
  and xlines, with Deferred Perpendicular for multi-object constructions.
- Autodesk's [OSNAP command reference](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-LT/files/GUID-CF5780AD-D1AB-4526-9608-83D7952749E7.htm)
  confirms that Nearest selects the closest point on one of those entities.
- Autodesk's [AutoLISP object-snap reference](https://help.autodesk.com/view/ACD/2027/ENU/?caas=caas%2Fdocumentation%2FACD%2F2014%2FENU%2Ffiles%2FGUID-4EEE5488-01D8-454F-9386-79E493E55D6E-htm.html)
  documents combined Endpoint/Midpoint/Center modes and an aperture controlling
  which nearby points are eligible.
- Autodesk's [OSMODE reference](https://help.autodesk.com/cloudhelp/2016/ENU/AutoCAD-Core/files/GUID-DD9B3216-A533-4D47-95D8-7585F738FD75.htm)
  assigns stable bit values 1/2/4/8/16/32 to Endpoint, Midpoint, Center, Node,
  Quadrant, and Intersection, 128 to Perpendicular, and 512 to Nearest; the
  ProGPU mode flags preserve those compatible assignments without importing
  unrelated modes.
- QCAD's current [reference manual](https://www.qcad.org/doc/qcad/latest/reference/en/qcad_reference_manual_en.html)
  documents Endpoint on bounded curve ends and vertices, Midpoint on lines and
  arcs (the midpoint lies on the arc, not at its center), Center on circles,
  arcs, and ellipses, automatic snap priority, and On Entity as the closest
  point on an entity.
- QCAD's current [Perpendicular reference](https://www.qcad.org/doc/qcad/latest/reference/en/scripts/Snap/SnapPerpendicular/doc/SnapPerpendicular_en.html)
  confirms that the mode applies while drawing a line and snaps to the
  perpendicular point on a line, arc, circle, or ellipse.
- QCAD's [Intersection Manual reference](https://qcad.org/doc/qcad/latest/reference/en/scripts/Snap/SnapIntersectionManual/doc/SnapIntersectionManual_en.html)
  distinguishes intersections of two entities from a separate manual tool that
  can extend entities beyond their authored limits.
- Autodesk's [Object Snap tracking reference](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-50383F73-4F23-4F70-B4FC-52D5748D80AF.htm)
  likewise distinguishes actual geometric Intersection from Extended and
  Apparent Intersection behavior.
- Autodesk Fusion's [drawing object-snap reference](https://help.autodesk.com/cloudhelp/ENU/Fusion-Drawing/files/GUID-93F2DB4E-A77C-4A40-9CBA-8E267855DD82.htm)
  independently confirms Endpoint, edge Midpoint, circular/arc Center, and
  point-geometry semantics.

The approved implementation provenance is entirely in this repository:
`CadDocumentSnapshot`, `CadSpatialIndex`, `CadPlanViewport`, the primitive
records in `CadSnapshot.cs`, `CadSnapshotCompiler.GetBulgeArc`, exact selection
angle/bulge predicates in `CadSelection.cs`,
`CadSplineCanonicalizer`, `CadRationalBezier`, and `CadSplineSelection`.
`CadObjectSnapQuery` derives points from those contracts without consulting
third-party implementation text.

## Adopted behavior

The default Standard mode composes Intersection, Endpoint, Midpoint, Center,
Quadrant, and Node. Nearest and Perpendicular remain explicit selector choices:
Nearest's continuous projection would otherwise mask discrete attraction, and
Perpendicular requires an accepted reference point. The shared selector can
instead enable one mode or turn running snaps off. A fixed 10-logical-pixel aperture is evaluated in device
space, so zoom does not change the acquisition radius. The closest point wins.
Exact distance ties use the documented ProGPU order Intersection, Endpoint,
Midpoint, Center, Quadrant, Node, Perpendicular, Nearest, followed by immutable
retained entity order, second entity order, and per-pair point order. This makes replay and tests
independent of hash or tree traversal order.

Supported exact candidates are:

- line endpoints and midpoint;
- circle center;
- bounded arc endpoints, parameter midpoint on the arc, and center;
- bounded ellipse endpoints, parameter midpoint on the ellipse, and center;
- lightweight and legacy 2D-polyline vertices plus exact straight or bulge-arc
  segment midpoints;
- 3D-polyline vertices and segment midpoints;
- exact endpoints of open nonperiodic positive-weight rational splines,
  extracted from canonical rational-Bezier spans without flattening; and
- POINT position as Node.

Quadrant evaluates the exact retained local parameters 0, π/2, π, and 3π/2 on
circles, arcs, ellipses, and elliptical arcs. Bounded sweeps admit only points
on the authored curve. Retained coordinate-system and major/minor-axis vectors
are applied directly, so rotated and tilted geometry is not reconstructed from
an AABB. When an arc endpoint is also a quadrant, Endpoint wins the exact tie.

Nearest means the closest point in the plan viewport's WCS XY projection while
retaining the exact source curve's WCS Z. It covers finite lines, RAYs, XLINEs,
POINTs, circles, arcs, ellipses and elliptical arcs, straight or bulged
lightweight/legacy 2D-polyline segments, 3D-polyline segments, and valid
positive-weight rational splines. Finite segment and sweep extents are clamped;
construction lines retain their authored unbounded domain. Circular and
elliptical curves are represented exactly as positive-weight rational quadratic
spans of at most 90 degrees and share ProGPU's bounded Bernstein
stationary-root solver with canonical rational-Bezier spline spans. No curve is
flattened and the result is not reconstructed from an AABB. Equal projected minima retain
authored parameter/span order, including a projection that collapses an entity;
a numerically unresolved curve is counted as unsupported instead of guessed.

Perpendicular is evaluated only after the MOVE/COPY base point has been
accepted. The base is the reference point and candidates are every authored
curve point whose projected tangent is orthogonal to the projected vector from
that base. Finite lines, rays, arcs, elliptical arcs, and polyline segments do
not extend beyond their authored domains; xlines remain unbounded. Circles,
ellipses, exact bulge conics, and positive-weight rational splines preserve all
real Bernstein stationary roots, so a far-side normal remains selectable near
the cursor instead of being reduced to Nearest. When every point on a span is a
valid normal, cursor proximity selects one exact point on that span. Source WCS
Z is retained. A first prompt with no reference produces no Perpendicular
candidate; Deferred Perpendicular between two source entities, multiline,
REGION/SOLID edges, and arbitrary-camera normals remain explicit later
contracts.

Intersection means a point that lies on both authored entities in the same WCS
XY plane. Exact closed-form solvers cover line segments, RAYs, XLINEs, planar
conformal circles and arcs, planar ellipses or ellipse arcs against linear
entities, straight or bulge-arc lightweight/legacy 2D-polyline segments, and
horizontal 3D-polyline segments. Finite segment and arc parameter intervals are
honored. A unique shared endpoint of collinear linear components is accepted;
an interval of coincident overlap is deliberately reported as unsupported
instead of inventing one answer. Construction lines are added explicitly
because their unbounded geometry has no finite spatial-index bounds.

Circle/ellipse, ellipse/ellipse, spline, tilted/nonconformal curve, nonhorizontal
3D-polyline, coincident-curve, apparent, projected, and extension intersections
remain explicit unsupported or deferred contracts. Actual entities on different
WCS Z planes do not intersect. This avoids silently flattening 3D geometry or
extending bounded geometry to imitate a separate snap mode.

Full circles/ellipses have no synthetic Endpoint. Closed/periodic splines have
no Endpoint. Tangent snaps, grid snap,
extension/tracking, cycling, tooltips, global last-point/UCS behavior, and
arbitrary-camera acquisition remain explicit later contracts. ProGPU rejects
flattening an analytic curve merely to obtain a snap point and rejects silently
treating an entity AABB as geometry.

## Algorithm, ownership, and failure contract

`CadObjectSnapQuery` first maps the screen aperture through the existing plan
viewport to a full-depth WCS selection column. The immutable spatial index
returns candidate entity indices into caller-owned scratch. Exact snap points
are then projected through the same viewport and arbitrated in squared device
distance. The immutable result carries snapshot generation, semantic kind,
retained entity index, source handle, exact double-WCS point, device distance,
candidate written/total counts, evaluated-point count, and unsupported count.
An Intersection result also carries the second retained entity and handle,
tested/total entity-pair counts, the tested analytic-component-pair count, and
explicit truncation. Scratch truncation is explicit; the query never allocates
a replacement. The shared shell refuses a possibly incomplete snap result and
falls back to raw input when either entity or intersection work was truncated.

For E retained entities, K broad-phase candidates, P ordinary snap points, S
exact Nearest/Perpendicular segments or rational-Bezier spans, B the
fixed 65,536 entity-pair budget, and C the fixed 262,144 analytic-component-pair
budget, average broad-phase work is `O(log E + K)`, deterministic candidate
sorting is `O(K log K)` in Intersection mode, and exact work is
`O(P + S + min(K^2, B) + C)` for fixed maximum spline degree ten. The broad
phase is `O(E + K)` worst case. Internal
storage is `O(1)` plus caller scratch. A single very large polyline pair cannot
exceed C component tests; a dedicated immutable
snap-point/intersection index is deferred until profiling justifies its build
and residency cost. Warm query tests require zero managed allocation.

Pointer hover never edits the document or republishes a snapshot. Clicking
commits the exact double-WCS snap result directly; it is not projected back
through float screen coordinates. Generation validation prevents acceptance of
a stale point after scene replacement. Typed coordinate input remains exact and
clears pointer snap state. Pan, zoom, cancellation, prompt reset, and mode
changes also clear or recompute state transactionally.

## Rendering and managed/native applicability audit

The marker is a fixed-device shared-shell overlay (square, triangle, circle,
plus, diagonal X, diamond, hourglass, or right angle) recorded after the retained CAD
picture. It adds no shader, GPU resource,
scene-cache key, upload, C ABI, or native document algorithm. The managed and
native renderers continue consuming the same committed retained picture after
one MOVE/COPY edit; therefore a separate native CAD snap implementation is not
applicable.

The mandatory rendering/text architecture gate was rechecked against
[Skia's staged shaped-text model](https://docs.skia.org/docs/dev/design/text_shaper/),
[DirectWrite/Direct2D separation](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite),
[Win2D retained text layout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm),
[WebRender's rendering pipeline](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
[Vello's retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md),
[Parley's reusable layout model](https://github.com/linebender/parley/blob/main/doc/concept.md),
and [HarfBuzz shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html).
Those sources reinforce retained semantic results and drawing lightweight
interaction overlays separately. No startup, shaping/layout, visibility,
glyph/path/image cache, upload, batching, DPI/subpixel, fallback/variation, or
device-loss contract changes in this slice.

## Verification and remaining evidence

Core tests cover each semantic kind; analytic arc/ellipse and bulge midpoints;
all four circle/rotated-ellipse quadrants, bounded circular/elliptical arc
parameters, and Endpoint/Quadrant tie priority;
exact plan Nearest points on finite/unbounded lines, POINTs, circles, bounded
arcs, ellipses, straight/bulged current and legacy 2D polylines, 3D-polyline
segments, and a positive-weight rational spline, including retained Z and
zero-allocation conic replay;
reference-required Perpendicular feet on bounded/unbounded linears, both sides
of circles, bounded arcs, ellipses, straight/bulged polylines, 3D-polyline
segments, and ordinary/rational splines, including retained Z, authored extents,
all-root arbitration, degenerate constant-distance spans, and zero-allocation
conic replay;
line/line, line/circle, line/arc, circle/circle, line/ellipse, polyline/RAY, and
polyline/XLINE intersections; authored extents and WCS planes; a unique
collinear shared endpoint; unsupported overlap; deterministic ties; explicit
caller-scratch, entity-pair-budget, and component-pair-budget truncation;
disabled modes; generation tagging;
and zero-allocation 1,024-query replay. Shared-shell tests cover hover before
the base point, no snapshot publication across 1,024 motions, fixed-device
marker recording, exact one-generation snapped MOVE with two successive
Intersection, Quadrant, or Nearest points or a base-referenced Perpendicular
second point, raw plan input when disabled, and
selector propagation without document edits.

Future mode families require their own exact geometry and ambiguity contracts.
Large-scene p50/p95/p99 measurements, dense coincident-candidate cycling, grid
and dedicated intersection indexes, arbitrary camera planes, and
desktop/browser visual goldens remain before object snapping can be called
complete.
