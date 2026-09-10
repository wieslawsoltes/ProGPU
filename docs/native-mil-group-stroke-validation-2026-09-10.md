# Native MIL grouped stroke and cached coverage validation

## Acceptance and implementation

The acceptance application is native MIL ShowcaseApp. Viewing or resizing grouped
rounded shapes and cached-brush strokes must preserve their source geometry, pen
width, brush mapping and once-composited opacity. This is a release CI correction
batch, not additional Direct2D/Win2D API expansion or package qualification.

`prepare_degenerate_fixed_geometry` now clamps rectangle radii against their source
extents before constructing the rigid pen frame. Direct draws already performed
that clamp, but group children did not. A rectangle with zero width and positive
declared corner radii therefore produced different cap geometry inside a group.
The shared normalization retains the original pen width and makes both routes
identical. No fill-area admission or source hit policy changes.

Untransformed, undashed ellipses in groups now reuse the direct fixed-shape full
arc emitter. The general closed-path route introduced an unnecessary seam join
from float-angle endpoint reconstruction. Sampled brushes, dashes and transformed
geometry retain their existing preparation; group material domains remain shared.
The complete direct/single-child-group stream matrix now agrees across six
transforms, three shapes, four extent pairs, six brush sources and two dash modes.

These are original ProGPU-native routing corrections using the existing direct
MIL helpers. Managed `StrokeCoverageGeometry.Smooth.cs` already requires both
corner radii for rounded preparation and explicitly rejects collapsed source
rectangles; this change does not claim to qualify that missing managed adapter
contract. Managed ordinary ellipse rendering retains its existing representation.
No public ABI, shader, CPU fallback or additional GPU pass is introduced. The
source-normalization and emitter selection are constant work. Existing intrinsic
mapping, shared stroke compiler and cross-engine decisions documented in
`native-mil-stroke-spine-bounds.md` remain unchanged. No performance claim is made.

## Oracle corrections

- Seven-child solid/dashed group fixtures compare complete prepared geometry and
  stroke records with independent child draws. Only packing offsets are normalized;
  coordinates, caps, joins, widths, dash values and closure are retained.
- DrawingImage capsule bounds use an independent scalar quarter-cubic derivative
  oracle, matching the actual retained round caps rather than an ideal circle.
  The existing transform comparison tolerance is unchanged.
- Primitive-only cached pen coverage is a geometry mask, not a nested picture.
  Its fixture checks both masks, opaque coverage primitives and independently
  retained half-opacity consumers of the same source revision.
- Rotated cached fill and pen consumers have different material frames: fill
  retains the geometry transform after relative brush mapping, while pen geometry
  is mapped before widening. Tests verify each complete affine transform.
- Cached-source opacity applies once to finished coverage. The managed reference
  now renders the ordinary opaque stroke into an independent layer, then applies
  opacity once. `PushOpacity` instead multiplies overlapping pieces. Pixel
  tolerance remains two and every pixel, source capture and warm reuse is checked.
- The Windows native Direct2D aliased-path size query expects the documented
  insufficient-buffer status/HRESULT; serialization still requires success and
  the full one-sample coverage contract.

## Validation and release limits

Local Release renderer: 4,576 passed, seven existing platform-specific skips,
zero failures. Headless: 280 passed, zero failures. Focused cached-picture: 39
passed. Generated MIL/native contract verification passes. Logs are retained in
the prepared worktree's `artifacts/release-hour` with `group-fixes`,
`cached-layer-oracle`, `native-group-contract` and native oracle batch names.

The full local native run is still 18/19: MIL now advances through the group,
DrawingImage cubic bounds, cached coverage and rotated fill/pen cases, then fails
building the gap-separated cached path fixture at `bitmap_cache_brush_linear_paths_preserve_gap_bounds`
(scene 8132). No test is skipped; this is the next native blocker. The latest log
is `native-fill-pen-oracle-tests.log`. Dependency pins are recorded in the LibreWPF
release status report. Hosted Windows previously compiled successfully but exposed a
cached Viewport3D image failure in addition to the repaired size-query assertion.
That GPU failure, SVG inventory review, exact-head CI, complete payload/package
production and real Windows/Linux/application gates remain required. Do not infer
qualification from prior staged payloads or merge while required checks are red.
