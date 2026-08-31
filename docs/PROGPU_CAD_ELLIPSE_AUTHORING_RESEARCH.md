# ProGPU.CAD ELLIPSE Authoring Research and Contract

Date: 2026-08-31

## Scope

This checkpoint adds clean-room, host-neutral plan-view `ELLIPSE` authoring for
full ellipses and elliptical arcs. The construction matrix is deliberately
complete across:

- first-axis endpoints or center plus first-axis endpoint;
- a point-defined other-axis distance or the circle-rotation eccentricity
  option;
- full ellipse, direction-angle arc, explicit-parameter arc, or
  direction-start plus included-angle arc.

The result is one analytic ACadSharp `Ellipse`; it is never approximated by a
polyline, cubic path, sampled point list, or host-owned geometry. Isocircle is
kept separate because it is a drafting-plane circle projection contract tied
to `SNAPSTYL`/`SNAPISOPAIR`, not another independent ellipse construction.

## Primary sources consulted

- Autodesk [`ELLIPSE` command](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-MAC-Core/files/GUID-07303D28-E335-4A90-B136-BF24F875369B.htm):
  axis-endpoint, Center, other-axis distance, Rotation, Arc, start/end angle,
  Parameter, Included Angle, and isocircle behavior.
- Autodesk [`ELLIPSE` DXF entity](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-DXF/files/GUID-107CB04F-AD4D-4D2F-8EC9-AC90888063AB.htm):
  WCS center, center-relative major-axis endpoint, minor/major ratio, extrusion
  direction, and start/end parameters.
- Autodesk [common entity group codes](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-3610039E-27D1-4E23-B6D3-7E60B22BB5BD.htm):
  layer, color, linetype, linetype scale, lineweight, visibility, and ownership
  state inherited by authored entities.
- Skia [`SkPath` public header](https://skia.googlesource.com/skia/+/refs/heads/chrome/m142/include/core/SkPath.h)
  and [`SkPath` reference](https://skia.googlesource.com/skia/+/2a8c48be4ff65d873d9d5ba65ecef989d82dd0be/site/user/api/SkPath_Reference.md):
  ellipses remain explicit ovals or bounded rational-conic contours rather
  than point-sampled geometry.
- Microsoft [Direct2D API overview](https://learn.microsoft.com/en-us/windows/win32/direct2d/the-direct2d-api)
  and [`ID2D1Geometry`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1geometry):
  immutable device-independent ellipse/path geometry is retained separately
  from device-dependent drawing resources.
- Microsoft [Win2D `CanvasGeometry`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasGeometry.htm)
  and [`CanvasPathBuilder`](https://microsoft.github.io/Win2D/WinUI3/html/Methods_T_Microsoft_Graphics_Canvas_Geometry_CanvasPathBuilder.htm):
  direct ellipse primitives and explicit elliptical-arc path segments remain
  distinct retained geometry forms; expensive derived geometry may be cached.
- Mozilla/Servo [WebRender repository](https://github.com/servo/webrender) and
  [current retained-pipeline counters](https://github.com/servo/webrender/blob/main/webrender/src/profiler.rs):
  display-list/scene building, visibility, preparation, batching, upload, and
  GPU-cache work are separately measured and retained.
- Linebender [Vello scene encoding](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  and [ellipse example](https://github.com/linebender/vello/blob/main/examples/simple_sdl2/src/main.rs):
  shapes enter a retained scene with an affine transform; GPU-side stroking
  remains a renderer concern rather than an authoring approximation.
- Skia [SkParagraph implementation state](https://github.com/google/skia/blob/main/modules/skparagraph/src/ParagraphImpl.h),
  Microsoft [DirectWrite architecture](https://learn.microsoft.com/en-us/windows/win32/directwrite/introducing-directwrite),
  Linebender [Parley architecture](https://github.com/linebender/parley), and
  [HarfBuzz shaping contract](https://harfbuzz.github.io/what-is-harfbuzz.html):
  shaping, font fallback, line layout, glyph positioning, and rendering are
  separate reusable stages. ELLIPSE authoring must not invalidate or replace
  any text result.

No third-party implementation source was copied, translated, adapted, or used
as a source-text template. These sources establish public behavior,
representation, and architecture only.

## Approved in-repository provenance

The implementation may directly reuse original ProGPU-owned patterns from:

- `src/ProGPU.CAD/CadCircleAuthoring.cs` for bounded point state, current
  entity-property capture, preflight, and identity-preserving Undo/Redo;
- `src/ProGPU.CAD/CadArcAuthoring.cs` for signed angle conversion,
  non-mutating final solves, and analytic preview ownership;
- `src/ProGPU.CAD/CadSnapshotCompiler.cs` for the authoritative ACadSharp
  ellipse-to-`CadEllipsePrimitive` contract;
- `src/ProGPU.CAD/CadPlanSceneCompiler.cs` for the existing shared analytic
  managed/native replay path;
- the existing shared sample point-acquisition pipeline for object snap, grid,
  Ortho, polar tracking, PolarSnap, direct distance, WCS-Z preservation, and
  desktop/browser-neutral interaction.

## Adopted design

Let the first semi-axis be vector `a`, its perpendicular candidate be `b`, and
the center be `c`.

- Axis-endpoint construction uses
  `c = p0 / 2 + p1 / 2` and `a = (p1 - p0) / 2` to avoid an avoidable sum
  overflow at large WCS origins.
- Center construction uses `c = p0` and `a = p1 - p0`.
- Distance construction uses the finite Euclidean distance from `c` to the
  other-axis input and an exactly perpendicular plan vector.
- Rotation construction follows the observable circle-rotation contract with
  `ratio = abs(cos(rotation))`; the documented near-edge-on invalid interval is
  rejected rather than producing an effectively linear ellipse.
- If the point-defined other axis is longer than the first axis, the axes are
  exchanged and the parameter basis is rotated so ACadSharp always receives a
  true major-axis vector and a ratio in `(0, 1]` without changing geometry.
- An elliptical parameter is recovered from a direction `d` without
  tessellation:

  `t = atan2(dot(d, minor) / dot(minor, minor),
             dot(d, major) / dot(major, major))`.

- Full ellipses persist exactly as start `0` and end `2*pi`.
- Direction-angle arcs resolve start and end rays through the equation above.
- Parameter arcs accept the DXF parametric values directly.
- Included-angle arcs rotate the accepted start direction analytically.
  Negative input is canonicalized to the same positive-counterclockwise DXF
  locus by exchanging the persisted interval endpoints.
- Every final solve is non-mutating. Document preflight failure leaves the
  exact prompt and accepted construction state recoverable.

All model calculations use finite `double` WCS values. The retained renderer’s
existing float-axis limit is validated before publication. Point construction
is confined to the first accepted point’s WCS-Z plane, and authored entities
use `Normal = AxisZ`.

## Rejected and deferred alternatives

- Polyline, cubic, sampled, or shader-generated authoring substitutes are
  rejected because they change DXF/DWG semantics, object snaps, bounds,
  linetypes, selection, print quality, and zoom behavior.
- Runtime parsing beyond one bounded invariant scalar is rejected. Expressions,
  unit suffixes, and temporary command overrides need a shared typed input
  language.
- Isocircle is deferred to a dedicated current-isoplane circle-projection
  contract so `SNAPSTYL`, `SNAPISOPAIR`, radius/diameter, and future arbitrary
  UCS behavior remain explicit.
- Arbitrary 3D UCS/camera authoring, Ctrl-drag clockwise endpoint override,
  object-snap tracking, command chaining, visual goldens, and dense-input
  percentile measurements remain separate gates.

## Cross-engine and managed/native applicability

The adopted architecture retains one immutable analytic entity and reuses the
existing ProGPU snapshot, culling, line-style, print, and managed/native scene
paths. It does not change startup/lazy initialization, worker preparation,
visibility rules, cache keys or eviction, uploads, GPU batching, DPI/subpixel
behavior, fallback fonts, variable-font state, glyph caches, atlas generations,
device-loss invalidation, shaders, packed streams, the public C ABI, or native
resource ownership. SkParagraph, DirectWrite, Parley, and HarfBuzz therefore
inform the separation audit but require no text-side edit.

The native renderer already consumes the same `CadEllipsePrimitive` generated
by the managed snapshot compiler. The checkpoint adds CPU-side document and
host authoring only; there is no paired native algorithm to modify. Matched
managed/native replay remains a required regression gate.

## Complexity and performance contract

- retained session storage: O(1), bounded points plus one analytic axis state;
- scalar parsing: O(L) for at most 128 UTF-16 code units;
- every construction, parameter solve, preview, Apply, Undo, and Redo: O(1);
- live preview: one analytic ellipse or bounded elliptical-arc path plus at
  most two guides, with no tessellation, document mutation, snapshot compile,
  upload, or managed/native crossing;
- stable completed replay: the unchanged retained ProGPU ellipse path, with
  zero new managed/native crossings or retained uploads.

This is a capability checkpoint, not a rendering-speed claim.

## Verification

Focused tests cover the complete four-by-four construction matrix, axis
canonicalization, large-WCS midpoint and explicit-direction precision,
Rotation edge-on rejection, direction-to-parameter mapping, signed Included
Angle behavior, bounded scalar parsing, current-property capture, identity
through Undo/Redo, locked-layer/invalid-CELTSCALE/nonzero-THICKNESS preflight,
recoverable final failures, shared selectors and Escape, ANGBASE/ANGDIR,
direct distance with nonzero elevation, exact analytic full/arc previews,
DXF/DWG round trips, and matched managed/native replay. The publication gates
passed on 2026-08-31:

- focused ELLIPSE authoring tests: 44/44;
- all CAD authoring tests: 139/139;
- complete .NET 10 CAD suite: 1,201/1,201 in Debug and Release;
- Release desktop build: 0 ProGPU warnings and 0 errors (the separately built
  ACadSharp source retains its existing warning baseline);
- `ProGPU.CAD` and `ACadSharp.ProGPU` packages built at
  `0.1.0-preview.62`, and the isolated package consumer restored, built with
  0 warnings and 0 errors, and created an AC1032 document.

The grouped package wrapper remains blocked by the separately user-deleted
browser sample project. The equivalent direct two-package build and isolated
consumer gate were used without restoring or staging those deletions.
