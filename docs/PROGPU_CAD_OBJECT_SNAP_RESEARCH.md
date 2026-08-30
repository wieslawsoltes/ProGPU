# ProGPU.CAD Object Snap Research

Status: implemented foundation, 2026-08-30

## Scope and clean-room sources

This slice adds running Endpoint, Midpoint, Center, and Node acquisition to the
shared desktop/browser MOVE and COPY point prompts. It does not copy, translate,
or imitate another engine's source code, helper structure, naming, tables, or
control flow. The implementation was designed from public behavior contracts
and existing ProGPU-owned immutable geometry:

- Autodesk's current [Object Snaps modifier reference](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-76B81C1A-373E-4BCD-975A-789FB36C89FE.htm)
  documents selecting the precise snap point closest to the cursor, visual
  markers/tooltips, persistent running modes, and cycling available snaps.
- Autodesk's [AutoLISP object-snap reference](https://help.autodesk.com/view/ACD/2027/ENU/?caas=caas%2Fdocumentation%2FACD%2F2014%2FENU%2Ffiles%2FGUID-4EEE5488-01D8-454F-9386-79E493E55D6E-htm.html)
  documents combined Endpoint/Midpoint/Center modes and an aperture controlling
  which nearby points are eligible.
- QCAD's current [reference manual](https://www.qcad.org/doc/qcad/latest/reference/en/qcad_reference_manual_en.html)
  documents Endpoint on bounded curve ends and vertices, Midpoint on lines and
  arcs (the midpoint lies on the arc, not at its center), Center on circles,
  arcs, and ellipses, and automatic snap priority.
- Autodesk Fusion's [drawing object-snap reference](https://help.autodesk.com/cloudhelp/ENU/Fusion-Drawing/files/GUID-93F2DB4E-A77C-4A40-9CBA-8E267855DD82.htm)
  independently confirms Endpoint, edge Midpoint, circular/arc Center, and
  point-geometry semantics.

The approved implementation provenance is entirely in this repository:
`CadDocumentSnapshot`, `CadSpatialIndex`, `CadPlanViewport`, the primitive
records in `CadSnapshot.cs`, `CadSnapshotCompiler.GetBulgeArc`,
`CadSplineCanonicalizer`, `CadRationalBezier`, and `CadSplineSelection`.
`CadObjectSnapQuery` derives points from those contracts without consulting
third-party implementation text.

## Adopted behavior

The default Standard mode composes Endpoint, Midpoint, Center, and Node. The
shared selector can instead enable one mode or turn running snaps off. A fixed
10-logical-pixel aperture is evaluated in device space, so zoom does not change
the acquisition radius. The closest point wins. Exact distance ties use the
documented ProGPU order Endpoint, Midpoint, Center, Node, followed by immutable
retained entity order and per-entity point order. This makes replay and tests
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

Full circles/ellipses have no synthetic Endpoint. Closed/periodic splines have
no Endpoint. Intersections, perpendicular/tangent/quadrant/nearest snaps,
construction-line snaps, grid snap, extension/tracking, cycling, tooltips,
global last-point/UCS behavior, and arbitrary-camera acquisition remain
explicit later contracts. ProGPU rejects flattening an analytic curve merely
to obtain a snap point and rejects silently treating an entity AABB as geometry.

## Algorithm, ownership, and failure contract

`CadObjectSnapQuery` first maps the screen aperture through the existing plan
viewport to a full-depth WCS selection column. The immutable spatial index
returns candidate entity indices into caller-owned scratch. Exact snap points
are then projected through the same viewport and arbitrated in squared device
distance. The immutable result carries snapshot generation, semantic kind,
retained entity index, source handle, exact double-WCS point, device distance,
candidate written/total counts, evaluated-point count, and unsupported count.
Scratch truncation is explicit; the query never allocates a replacement.

For E retained entities, K broad-phase candidates, and P snap points belonging
to those candidates, average work is `O(log E + K + P)`, worst-case work is
`O(E + P)`, and internal storage is `O(1)` plus caller scratch. A single very
large polyline still requires `O(P)` point evaluation; a dedicated immutable
snap-point index is deferred until profiling justifies its additional build and
residency cost. Warm query tests require zero managed allocation.

Pointer hover never edits the document or republishes a snapshot. Clicking
commits the exact double-WCS snap result directly; it is not projected back
through float screen coordinates. Generation validation prevents acceptance of
a stale point after scene replacement. Typed coordinate input remains exact and
clears pointer snap state. Pan, zoom, cancellation, prompt reset, and mode
changes also clear or recompute state transactionally.

## Rendering and managed/native applicability audit

The marker is a fixed-device shared-shell overlay (square, triangle, circle/cross,
or X) recorded after the retained CAD picture. It adds no shader, GPU resource,
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

Core tests cover each initial semantic kind, analytic arc/ellipse and bulge
midpoints, deterministic ties, explicit caller-scratch truncation, disabled
modes, generation tagging, and zero-allocation 1,024-query replay. Shared-shell
tests cover hover before the base point, no snapshot publication across 1,024
motions, fixed-device marker recording, exact one-generation snapped MOVE,
raw plan input when disabled, and selector propagation without document edits.

Future mode families require their own exact geometry and ambiguity contracts.
Large-scene p50/p95/p99 measurements, dense coincident-candidate cycling, grid
and intersection indexes, arbitrary camera planes, and desktop/browser visual
goldens remain before object snapping can be called complete.
