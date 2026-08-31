# ProGPU.CAD RECTANG Authoring Research and Contract

Date: 2026-08-31

## Scope

This clean-room slice adds the host-neutral analytic core for plan-view
`RECTANG` authoring. It covers diagonal-corner, Dimensions, Area, and Rotation
construction together with mutually exclusive sharp, symmetric chamfer, and
constant-radius fillet corners. The committed entity is one closed ACadSharp
`LwPolyline`; circular corners remain exact DXF bulges rather than sampled
line or Bézier approximations.

## Primary sources consulted

- Autodesk [`RECTANG` command](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-188B2DDA-6CD8-4D37-BF26-E6CF27C34C75.htm):
  closed rectangular-polyline result; diagonal corners; Area, Dimensions, and
  Rotation placement; persistent Chamfer, Elevation, Fillet, Thickness, and
  Width settings; and the requirement that Area includes removed chamfer or
  fillet corner area.
- Autodesk [`LWPOLYLINE` DXF entity](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-DXF/files/GUID-748FC305-F3F2-4F74-825A-61F04D757A50.htm):
  closed and PLINEGEN flags, OCS vertices, elevation, thickness, constant and
  per-vertex width, bulge, and extrusion direction.
- Skia [`SkRRect`](https://api.skia.org/classSkRRect.html) and
  [`SkPath`](https://api.skia.org/classSkPath.html): finite normalized bounds,
  bounded corner radii, explicit contour closure, and retained analytic shape
  data distinct from presentation transforms.
- Microsoft [`ID2D1RoundedRectangleGeometry`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1roundedrectanglegeometry)
  and Win2D [`CanvasGeometry.CreateRoundedRectangle`](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_Geometry_CanvasGeometry_CreateRoundedRectangle_1.htm):
  immutable/device-independent retained geometry and explicit radii.
- Linebender [Vello scene encoding](https://github.com/linebender/vello/blob/main/vello/src/scene.rs),
  [path encoding](https://github.com/linebender/vello/blob/main/vello_encoding/src/path.rs),
  and [retained-transform design](https://github.com/linebender/vello/blob/main/doc/vision.md):
  compact path data, separate affine transforms, and GPU-side transform reuse.
- Mozilla [WebRender display-list border binding](https://searchfox.org/mozilla-central/source/gfx/webrender_bindings/src/bindings.rs):
  rectangle, corner-radius, border, clip, and spatial identity remain typed
  display-list data rather than transient raster output.
- Skia [SkParagraph](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h),
  Microsoft [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/text-formatting-and-layout),
  Linebender [Parley](https://github.com/linebender/parley), and
  [HarfBuzz](https://harfbuzz.github.io/harfbuzz-hb-shape.html): text shaping,
  fallback, line layout, and positioned-glyph reuse are independent of
  rectangle authoring and must not be invalidated or replaced by this work.

No third-party source text, helper structure, naming scheme, control flow, or
lookup data is copied or translated. These sources establish public behavior,
file representation, and retained-rendering constraints only.

## Approved in-repository provenance

The implementation directly reuses original ProGPU-owned contracts from:

- `src/ProGPU.CAD/CadPolylineAuthoring.cs` for immutable planar vertices,
  exact bulges, current-property and PLINEGEN capture, nonzero-PLINEWID
  preflight, and identity-preserving Undo/Redo;
- the plan point-acquisition stack for WCS input, object/grid snap, Ortho,
  polar tracking, direct distance, and active-plane preservation;
- the existing LWPOLYLINE snapshot, selection, managed/native scene, print,
  and DXF/DWG persistence paths.

## Geometry contract

For first corner `p`, normalized rotation `t`, local orthonormal basis
`u=(cos(t),sin(t))`, `v=(-sin(t),cos(t))`, and signed extents `x` and `y`, the
four outer corners are `p`, `p+x*u`, `p+x*u+y*v`, and `p+y*v`. Diagonal input
projects the second-point displacement onto this basis. Dimensions and Area
use positive magnitudes and the placement point selects each extent sign;
zero projection deterministically selects the positive side.

The contour follows the signed corner order. Its orientation is `sign(x*y)`.
Fillet arcs therefore use signed bulge
`sign(x*y) * tan(pi/8)` for each quarter circle. At a radius equal to half one
or both extents, coincident tangent vertices are coalesced while their outgoing
bulges are retained, producing an exact stadium or four-bulge circle without
zero-length segments. Chamfer limits are handled by the same bounded
coalescing rule.

For outer length `L`, width `W`, chamfer distances `a,b`, fillet radius `r`,
and requested enclosed area `A`:

- sharp: `A = L*W`;
- chamfer: `A = L*W - 2*a*b`;
- fillet: `A = L*W - (4-pi)*r^2`.

Area mode adds the applicable constant corner reduction before solving the
unknown dimension. Every scalar, basis projection, corner, bulge, and result
must be finite. Extents are bounded to the retained float-vector domain;
opposite corners and the compacted contour must remain representable as
distinct double-WCS points. Invalid final input never consumes the accepted
first corner.

The first chamfer distance is measured on local-X edges and the second on
local-Y edges. This is ProGPU's explicit typed mapping. Autodesk documents the
two distances but not their internal per-edge storage convention; licensed
behavioral differential fixtures remain a later confirmation gate.

## Retention, performance, and parity audit

Session state, diagonal projection, Dimensions solving, Area solving, and live
vertex expansion are bounded O(1) time and storage. A snapshot has four scalar
corner parameters and never owns a variable vertex collection. Commit expands
at most eight vertices and allocates the one final LWPOLYLINE snapshot.

The final entity follows the existing polyline pipeline, so startup/lazy
initialization, text shaping/layout reuse, visibility culling, cache keys and
eviction, demand-driven upload, worker preparation, GPU batching, DPI and
subpixel rules, font fallback and variable-font state, device-loss generation,
canonical shaders, C ABI records, and native resource ownership do not change.
Managed and native renderers consume the same analytic bulge primitive, and
printing consumes the same retained scene. No renderer-specific approximation
or new managed/native crossing applies.

## Explicitly deferred and fail-closed

- Nonzero rectangle Width uses DXF polyline width, but ProGPU currently rejects
  filled wide-polyline lowering. RECTANG therefore reuses the existing
  nonzero-PLINEWID preflight instead of drawing a cosmetic centerline.
- Nonzero Thickness is not authored because current LWPOLYLINE compilation
  does not yet lower the extrusion side faces. Silently persisting an ignored
  value would violate rendering/printing parity.
- Arbitrary 3D UCS/OCS planes, per-corner radii, expressions and unit suffixes,
  command-line aliases, command chaining, licensed AutoCAD differential
  fixtures, and host controls are separate checkpoints. Plan elevation is the
  accepted first point's exact WCS Z until a typed persistent elevation option
  is integrated.

## Verification contract

Focused tests must cover all construction modes and quadrants, rotated bases,
clockwise/counterclockwise bulge signs, chamfer and fillet area correction,
maximum-radius vertex coalescing, large-WCS behavior, nonfinite and degenerate
failure, recoverable final input, property publication and Undo/Redo, DXF and
DWG round trips, managed/native replay, and bounded randomized invariants.

The analytic-core checkpoint passed 20/20 focused tests in Debug and Release,
including 4,096 deterministic randomized snapshots, both persistence formats,
and native replay. ACadSharp emitted only its pre-existing warning baseline;
the new ProGPU sources compiled without warnings.
