# Native MIL full-ellipse input connection

## Concrete application blocker — 2026-09-09

LibreWPF's full package MVP declares `MvpShapeEllipse`, a 54x54 filled ellipse
with a 3-DIP stroke at Canvas offset (104,16). Source WPF `Ellipse.ArrangeOverride`
insets the defining rectangle by half the pen, giving a 51x51 ellipse at (1.5,1.5).
`Ellipse.OnRender` calls DrawGeometry with that real EllipseGeometry. Native MIL's
primitive-geometry stroke path emits a full elliptical `GEOMETRY_ARC`; complete
input indexing previously accepted only `GEOMETRY_LINE` records in that command.
This rejection prevented complete native input admission for the shape's scene.
The finding is source-backed, not an executed application failure.

## Implementation and paired applicability

The native producer now recognizes the canonical full ellipse emitted by MIL:
positive axis-aligned radii, zero start angle, exactly one positive full sweep,
and ordinary local stroke width with optional edge aliasing. It maps center/radii
to the existing ellipse hit primitive and retains stroke width, owner, inverse
placement and actual clip metadata. Partial sweeps, skewed radius bases and
device-width arcs continue to fail explicitly; they do not become full ellipses.

Both this ingress and existing native analytic ellipse draws call one local
encoder, keeping cached center/inverse-radii fields and padded broad-phase bounds
consistent. The existing intrinsic four-corner transform handles affine bounds;
inverse transforms preserve the local ellipse for canonical GPU point/region
queries. Broad-phase rectangles do not substitute for ellipse containment.

Managed source command capture already routes filled/stroked ellipses to
`GpuHitTestPrimitive.EllipseFill` / `EllipseStroke`. Its implementation requires no
additional ingress: the paired fixtures exercise source traversal and the existing
affine primitive encoder. This is a native representation connection, not a new
ellipse-distance algorithm or a claim of qualified WPF geometric/pixel fidelity.
The canonical shader and C ABI stay unchanged; both native providers consume the
same code. Source WPF and its bridge already export EllipseGeometry correctly.

Complexity remains O(P) fixed-record encoding for P ellipse records with O(1)
additional scratch per record and existing O(P) retained hit storage. Fixed field
setup reuses the prior analytic encoder; batched coordinate placement remains
intrinsic. No CPU pixel readback, extra GPU submission, new raster resource,
WPF-local stroker, generic COM expansion or scalar-buffer fallback is introduced.

## Design/research continuity

Apply the [input ownership research](native-mil-hit-test-ownership.md#design-references-and-decisions):
Skia/SkParagraph and DirectWrite/Win2D preserve geometry versus layout semantics;
WebRender separates retained spatial input from raster output; Vello/Parley and
HarfBuzz retain reusable scene/layout/shaping state. No text or foreign renderer
implementation is copied. The primary
[Direct2D geometry contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1geometry)
and [Skia path API](https://api.skia.org/classSkPath.html), revisited for this
connection, reinforce retaining fill/stroke geometry separately from its bounds.
Adapt representation into ProGPU's existing typed encoder rather than replacing
the shared query algorithm or translating the application to another renderer.

Startup/lazy initialization, text shaping/layout reuse, culling, glyph/path/texture
cache keys/eviction, upload demand, workers, GPU batching, DPI/subpixel/hinting,
fallback/variable-font state and device/atlas invalidation remain unchanged.
No latency, allocation or quality improvement is claimed without final measurement.

## Authored qualification

- Native canonical MIL scene 9838 pairs with
  `MvpEllipseGeometryInputRetainsFillStrokeAndUpdates`: actual MVP dimensions,
  fill/stroke order, owner/placement, widened input bounds, resized radii/center
  and content removal.
- Native builder scene 9837 pairs with
  `NativeFullEllipseArcUsesCanonicalStrokeAndAffinePlacement`: arc versus analytic
  payload equivalence, noncircular ellipse, affine scalar bounds oracle, and
  rejection of partial/skew-basis/device-width records without partial indices.
- The previous drawing-mask managed fixture's axis clip assertion is corrected:
  its existing exact axis clip lives in clipped primitive bounds, not a four-edge
  clip payload. Native retains four edges; this is a storage distinction, not an
  input-policy difference. No test was executed to obtain this source finding.

Compilation is not execution. Exact-head cross-platform provider/module builds,
package consumption, native/managed/Windows point/region and image comparisons,
lifetime/performance and required CI remain final qualification gates. This
checkpoint does not finish the application acceptance queue or general arc input.
