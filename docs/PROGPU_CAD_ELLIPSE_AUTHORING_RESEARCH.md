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
polyline, cubic path, sampled point list, or host-owned geometry. The same
bounded command now includes the separate Isocircle construction, gated by
`SNAPSTYL=1` and projected from the exact captured `SNAPISOPAIR` basis with
Radius or Diameter input.

## Primary sources consulted

- Autodesk [`ELLIPSE` command](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-MAC-Core/files/GUID-07303D28-E335-4A90-B136-BF24F875369B.htm):
  axis-endpoint, Center, other-axis distance, Rotation, Arc, start/end angle,
  Parameter, Included Angle, and isocircle behavior.
- Autodesk [2D isometric drawing](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-Core/files/GUID-37463F74-0B06-46E2-8791-6C5B852A069D.htm):
  exact Left 90/150-degree, Top 30/150-degree, and Right 30/90-degree
  isoplane pairs; true drafting scale along isometric axes; F5/Ctrl+E plane
  cycling; and the requirement to represent plane circles with Isocircle.
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
- Isocircle captures the active unit isometric basis vectors `u` and `v` when
  the command starts. With `d = dot(u, v)` and entered circle radius `r`, its
  canonical major direction is
  `(u + sign(d) * v) / sqrt(3)`, its major radius is
  `r * sqrt(3/2)`, and its minor/major ratio is `1/sqrt(3)`. This is the exact
  singular-axis form of the documented true-scale two-axis projection. It
  works for all three isoplanes and an arbitrary persisted snap rotation,
  without hard-coded screen angles, sampling, or runtime eigensolving.
- Diameter input is normalized once to `r = diameter / 2`. Pointer, coordinate,
  and direct-distance previews use the same solve and publish one full
  ellipse. The Isocircle choices are unavailable in rectangular SNAP style.

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
- Autodesk's ELLIPSE command contract does not define the ARC command's
  transient Ctrl clockwise override. Inventing one was rejected; direction
  remains explicit through the documented Angle, Parameter, and Included
  Angle routes.
- Arbitrary 3D UCS/camera authoring, object-snap tracking, command chaining,
  expressions/units, temporary overrides, and visual goldens remain separate
  gates.

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
by the managed snapshot compiler. Isocircle changes only the CPU-side
authoring solve and produces the existing canonical center/major/minor
primitive; there is no paired native projection algorithm, shader, or ABI to
modify. The matched regression compiles an authored Isocircle through both the
managed scene and native picture compiler.

## Complexity and performance contract

- retained session storage: O(1), bounded points plus one analytic axis state;
- scalar parsing: O(L) for at most 128 UTF-16 code units;
- every construction, parameter solve, preview, Apply, Undo, and Redo: O(1);
- live preview: one analytic ellipse or bounded elliptical-arc path plus at
  most two guides, with no tessellation, document mutation, snapshot compile,
  upload, or managed/native crossing;
- stable completed replay: the unchanged retained ProGPU ellipse path, with
  zero new managed/native crossings or retained uploads.

The reproducible Release lane writes its ignored local report to
`artifacts/benchmarks/cad-isocircle-authoring.json` and solves 65,536 rotated mixed-
isoplane sessions per iteration. Across 48 measured iterations after six
warmups, Radius records p50/p95/p99 6.3054/8.2485/10.3219 ms and Diameter
5.7855/9.2371/10.2220 ms. Each batch allocates about 22.55 MB from the existing
bounded session object and its fixed point array; solving and stable completed
replay add no collections, tessellation, upload, or managed/native crossing.

## Verification

Focused tests cover the complete four-by-four construction matrix, all three
Isocircle planes, persisted snap rotation, Radius and Diameter scalar/pointer
input, rectangular-style gating, exact shared-view commit, axis
canonicalization, large-WCS midpoint and explicit-direction precision,
Rotation edge-on rejection, direction-to-parameter mapping, signed Included
Angle behavior, bounded scalar parsing, current-property capture, identity
through Undo/Redo, locked-layer/invalid-CELTSCALE/nonzero-THICKNESS preflight,
recoverable final failures, shared selectors and Escape, ANGBASE/ANGDIR,
direct distance with nonzero elevation, exact analytic full/arc previews,
DXF/DWG round trips, and matched managed/native replay. The publication gates
passed on 2026-08-31:

- focused ELLIPSE authoring tests: 51/51 in Debug and Release;
- complete .NET 10 CAD suite: 1,398/1,398 in Debug and Release;
- Release desktop build: 0 ProGPU warnings and 0 errors (the separately built
  ACadSharp source retains its existing warning baseline);
- `ProGPU.CAD` and `ACadSharp.ProGPU` packages built at
  `0.1.0-preview.62`, and the isolated package consumer restored, built with
  0 warnings and 0 errors, and created an AC1032 document.

The grouped package wrapper remains blocked by the separately user-deleted
browser sample project. The equivalent direct two-package build and isolated
consumer gate were used without restoring or staging those deletions.
