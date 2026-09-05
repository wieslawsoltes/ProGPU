# ProGPU.CAD POLYGON Authoring Research and Contract

Date: 2026-08-31

## Scope

This checkpoint adds clean-room, host-neutral regular `POLYGON` authoring with
the documented 3-through-1024 side bound and the complete Inscribed,
Circumscribed, and Edge construction matrix. The committed result is exactly
one closed zero-bulge ACadSharp `LwPolyline`, reusing ProGPU's existing
polyline persistence, selection, print, snapshot, and managed/native replay.

## Primary sources consulted

- Autodesk [`POLYGON` command](https://help.autodesk.com/cloudhelp/2027/ENG/AutoCAD-Core/files/GUID-E5CD464D-C0DC-4464-BFDF-50C4ABEC8B91.htm):
  equilateral closed-polyline result, 3-through-1024 sides, Inscribed
  center-to-vertex radius, Circumscribed center-to-edge-midpoint radius,
  pointer-controlled rotation, numeric-radius bottom-edge snap alignment,
  Edge endpoints, and `PLINETYPE` interaction.
- Autodesk [`LWPOLYLINE` DXF entity](https://help.autodesk.com/cloudhelp/2016/ENU/AutoCAD-DXF/files/GUID-748FC305-F3F2-4F74-825A-61F04D757A50.htm):
  closed flag, planar elevation/extrusion, vertices, bulges, widths, and
  `PLINEGEN` flag representation.
- Skia [`SkPath::addPoly` contract](https://skia.googlesource.com/skia/+/2a8c48be4ff65d873d9d5ba65ecef989d82dd0be/site/user/api/SkPath_Reference.md):
  explicit ordered vertices and closed-contour semantics.
- Microsoft [`ID2D1PathGeometry`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1pathgeometry)
  and Win2D [`CanvasPathBuilder`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasPathBuilder.htm):
  retained device-independent line contours remain separate from draw-time
  transforms and device resources.
- Mozilla/Servo [WebRender](https://github.com/servo/webrender) and Linebender
  [Vello scene encoding](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  plus [path encoding](https://github.com/linebender/vello/blob/main/vello_encoding/src/path.rs):
  retain scene/path data and apply affine transforms without rebuilding source
  geometry for each presentation.
- Skia [SkParagraph](https://github.com/google/skia/blob/main/modules/skparagraph/src/ParagraphImpl.h),
  Microsoft [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/introducing-directwrite),
  Linebender [Parley](https://github.com/linebender/parley), and
  [HarfBuzz](https://harfbuzz.github.io/what-is-harfbuzz.html): text shaping,
  fallback, layout, glyph positioning, and rendering stay reusable and
  independent of polygon authoring.

No third-party implementation source was copied, translated, adapted, or used
as a source-text template. The sources establish public behavior,
representation, and architectural constraints only.

## Approved in-repository provenance

The implementation directly reuses original ProGPU-owned contracts from:

- `src/ProGPU.CAD/CadPolylineAuthoring.cs` for exact LWPOLYLINE publication,
  current `CLAYER`/`CECOLOR`/`CELTYPE`/`CELTSCALE`/`CELWEIGHT`/`PLINEGEN`
  capture, preflight, and identity-preserving Undo/Redo;
- `src/ProGPU.CAD/CadPlanGridSnap.cs` for current rectangular or isometric
  snap-basis state and `SNAPANG` orientation;
- the shared sample point-acquisition pipeline for object snap, grid, Ortho,
  polar tracking, PolarSnap, direct distance, WCS-Z preservation, and
  desktop/browser-neutral interaction;
- the existing polyline snapshot/scene/print/native paths for the committed
  result.

## Adopted geometry and interaction

For `N` sides, `h = pi/N`, circumradius `R`, apothem `a`, and first vertex
angle `t`, vertex `i` is

`center + R * (cos(t + i*2*pi/N), sin(t + i*2*pi/N))`.

- Inscribed pointer input uses the center-to-pointer distance as `R`; the
  pointer is the first vertex.
- Circumscribed pointer input uses the center-to-pointer distance as `a`, then
  `R = a/cos(h)`; the pointer direction is the first edge midpoint and the
  first vertex angle is offset by `-h`.
- Numeric Inscribed or Circumscribed radius uses the negative rectangular
  snap-Y direction as the bottom-edge midpoint direction. This preserves the
  documented bottom-edge alignment for current `SNAPANG`, including when an
  isometric display pair is active.
- Edge mode uses the entered points as the first directed edge. The remaining
  counterclockwise vertices lie to its left. This is ProGPU's deterministic
  point-order contract; licensed differential confirmation of undocumented
  cursor-side behavior remains a separate gate.
- Edge center uses the overflow-resistant midpoint `p0/2 + p1/2` plus the
  left unit normal times `edgeLength/(2*tan(h))`.
- Every final solve is non-mutating. A document-property or publication
  preflight failure retains the accepted first point and current prompt.

All solving uses finite `double` WCS coordinates and preserves the first
point's exact Z plane. Circumradius is bounded to the retained float-vector
domain before publication. Vertex arrays and zero bulges are allocated only
for the final command.

## Retention, performance, and parity audit

The session and every live solve are O(1). Side parsing is O(L) for at most
four UTF-16 code units. One canonical unit polygon is recorded once when the
command starts in O(N) time/storage; pointer motion replays it with one affine
transform plus one guide in O(1), without document mutation, snapshot compile,
upload, or managed/native crossing. Commit materializes exactly N vertices in
O(N), bounded by 1024.

The result follows the existing LWPOLYLINE path, so startup/lazy initialization,
layout/text reuse, visibility culling, cache keys/eviction, demand upload,
worker preparation, GPU batching, DPI/subpixel rules, font fallback,
variable-font state, device-loss generations, shaders, packed streams, public
C ABI, and native resource ownership are unchanged. The managed and native
renderers already consume the same compiled polyline primitive; no one-sided
renderer algorithm or shader change applies. Matched replay remains covered.

## Explicitly deferred

- Legacy `PLINETYPE=0` heavyweight `POLYLINE` authoring is not approximated;
  this checkpoint deliberately publishes the established ProGPU LWPOLYLINE
  form. A legacy entity path needs its own persistence and parity contract.
- Fill-on constant `PLINEWID` now uses the exact retained contract in
  `PROGPU_CAD_WIDE_POLYLINE_RESEARCH.md`; tapered widths and FILLMODE-off
  outlines remain fail-closed rather than becoming a cosmetic centerline.
- Arbitrary 3D UCS/camera authoring, expressions/units, command chaining,
  temporary overrides, object-snap tracking, cursor-side Edge alternatives,
  visual goldens, licensed cross-engine differential fixtures, and sustained
  percentile measurements remain later gates.

## Verification contract

Focused regressions cover all modes, pointer and numeric orientation, current
snap rotation, large-WCS midpoint behavior, invariant side bounds, invalid and
off-plane input, recoverable final failure, maximum-side materialization,
current-property publication, identity Undo/Redo, shared selectors and Escape,
direct distance, retained transformed preview, DXF/DWG round trips, and
managed/native replay. The publication gates passed on 2026-08-31:

- focused POLYGON authoring tests: 27/27;
- all CAD authoring tests: 166/166;
- complete .NET 10 CAD suite: 1,228/1,228 in Debug and Release;
- Release ProGPU build: 0 ProGPU warnings and 0 errors (the separately built
  ACadSharp source retains its existing warning baseline);
- `ProGPU.CAD` and `ACadSharp.ProGPU` packages built at
  `0.1.0-preview.62`; the package-content/dependency audit passed, and an
  isolated package-only consumer restored and built with 0 warnings and 0
  errors before creating an AC1032 document.

The grouped package wrapper remains blocked by the separately user-deleted
browser sample project. The equivalent direct two-package build, audit, and
isolated consumer gate passed without restoring or staging those deletions.
