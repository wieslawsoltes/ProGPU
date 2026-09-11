# Native MIL stroke-spine bounds

## Application dependency and failure

The frozen native MIL delivery gate includes source PathGeometry drawings with
tiled image/drawing/visual pens. A mixed curve with an unstroked middle segment
leaves an independent horizontal line run. Applying Geometry.Transform to that
run failed with `InvalidGraph` before mask painting: the preparation path used
the positive-area fill bounds check on a zero-height stroke centerline.
The same failure reproduced in macOS ARM64 and the strict GCC CI lane.

## Repair and implementation parity

The existing native path-extrema helper now accepts an explicit `stroke_spine`
bounds policy only from transformed stroke preparation. Finite zero-width or
zero-height coordinates remain valid there; empty/nonfinite input still fails.
Every existing fill caller retains the default positive-area requirement.
The source coordinates, split runs, endpoint/dash-cap ownership and pen thickness
reach the existing shared stroke compiler unchanged. Geometry-local transforms
precede widening; the world transform still follows it. No epsilon rectangle,
dropped run, mask-bounds input substitute or alternate stroker is introduced.

This is a native MIL preparation-specific repair. Managed `PathGeometry.TryGetBounds`
already accepts finite zero extents, and `StrokeCoverageGeometry.LinearPath.cs`
measures the actual widened runs after geometry-local mapping. Paired managed
tests retain the line endpoints, gap boundary caps and original width under
horizontal, vertical and rank-one maps; no managed production change is needed.

Original implementation provenance: ProGPU's native `try_get_path_segment_bounds`
and `transform_path_stroke_spine`, its shared semantic path stroker, and the
managed geometry/linear-stroke helpers above. The
[existing cross-engine design comparison](native-mil-hit-test-ownership.md#design-references-and-decisions)
remains applicable. Rechecked [Skia path bounds](https://api.skia.org/classSkPath.html)
and [Direct2D widened bounds](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1geometry-getwidenedbounds)
support keeping path coordinates distinct from painted stroke bounds. No foreign
implementation was copied. Shaping, caches, upload, batching and device ownership
are unchanged. Bounds traversal remains O(S) time/O(1) space for S segments;
existing intrinsic point mapping is unchanged. No speed improvement is claimed.

## Validation and remaining gates

- Three native regressions inspect the actual mask's line endpoints, unchanged
  width four and identity post-widen transform for horizontal, vertical and
  rank-one geometry maps.
- All 64 existing mixed-curve/arc/multiple/broken tiled-pen combinations now pass,
  followed by the nested-group brush fixtures. These requests are rendering
  fixtures, not complete native hit-index or pixel qualification.
- Managed linear-stroke coverage tests pass **19/19**, including three new paired
  cases. The native MIL suite reaches a later collapsed-group brush-table count
  failure; the complete suite is not green.
- Native coverage metadata is regenerated and the contract verifier is required.
  Full exact-head provider/platform/package/CI gates remain mandatory.
