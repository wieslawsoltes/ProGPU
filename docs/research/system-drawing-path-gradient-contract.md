# System.Drawing path-gradient contract

## Source and API contract

This slice restores the complete .NET 10
`System.Drawing.Drawing2D.PathGradientBrush` public surface. The pinned
`System.Drawing.Common` 10.0.11 reference assembly defines binary shape, while
constructor defaults, validation, defensive array ownership, cloning,
transforms, blend curves, preset colors, focus scales, wrap modes, and disposed
state were checked against the source-reused upstream WinForms
[`PathGradientBrushTests`](https://github.com/dotnet/winforms/blob/main/src/System.Drawing.Common/tests/System/Drawing/Drawing2D/PathGradientBrushTests.cs).
The cross-platform material, packing, validation, shader, and rendering
algorithms are original ProGPU code.

Point and span constructors snapshot their input and accept the official
two-point minimum. The path constructor clones and adaptively flattens its
source before retaining a boundary contour; its public rectangle and default
center preserve the source path's analytic bounds. `SurroundColors`, `Blend`,
`InterpolationColors`, and `Transform` return independent snapshots. Cloning
deep-copies all managed state. `Pen.PenType` now identifies this brush directly
as `PenType.PathGradient` without a native query or object-shape probe.

The managed state deliberately retains an upstream quirk: a multi-sample
`Blend` requires positions zero and one at its endpoints but can preserve an
unusual intermediate position. Renderer lowering clamps such intermediate
positions into one monotonic portable curve, so malformed-but-storable state
cannot create an invalid native brush page. The default one-sample public blend
is lowered to the implicit center-to-boundary `[1, 0]` falloff used for actual
painting.

## Typed retained material

`ProGPU.Vector.PathGradientBrush` is a typed renderer contract shared by fills,
ordinary pen brushes, text, retained pictures, and native scene compilation.
It carries boundary points, one surround color per point, center point and
color, focus scales, either a scalar blend curve or preset color curve, spread
and interpolation modes, opacity, and an inverse coordinate transform.
Arrays are snapshotted by the typed material; no runtime reflection, GDI/GDI+
handle, HDC, private-field scan, readback, or compatibility-shaped fake object
is involved.

The existing 32-byte auxiliary record page stores two records per boundary
vertex—position and surround color—followed by the active curve. The existing
256-byte brush record carries the bounded counts and scalar state. Both managed
and standalone C++ scene validators require two through 128 boundary vertices,
an exact `2 * boundaryCount + curveCount` record count, finite payloads,
canonical reserved fields, and a monotonic curve. Native scene snapshots and
portable picture archives preserve the same typed state.

For each fragment the shared WGSL program transforms the paint coordinate,
intersects its center ray with every retained polygon edge, selects the nearest
positive boundary, and interpolates the two edge surround colors. This yields
the normalized center-to-boundary coordinate for arbitrary convex and
star-shaped contours instead of substituting the bounding ellipse. A second
intersection against the anisotropically focus-scaled contour creates the
zero-distance focus region before the blend or preset curve is sampled.
Clamp, repeat, reflect, and decal behavior then use the common gradient policy.
The fixed 128-edge maximum bounds shader work and storage; larger managed
contours are deterministically decimated before retention.

## Quality and performance gates

Focused managed tests cover array/span/path constructors, canonical defaults,
defensive ownership, same-color compaction, deep cloning, transforms,
triangular and 256/511-sample sigma curves, preset colors, validation,
`PenType`, typed graphics/pen command retention, shader source, native record
validation, bounded lowering allocation, and disposed behavior. A headless GPU
test samples a square diagonal where an ellipse approximation would already be
clamped to blue; the retained polygon material remains the expected purple.
The native C++ internal suite separately accepts a canonical path-gradient page
and rejects a boundary count beyond 128.

`GraphicsPathBenchmarks.LowerMaximumBoundaryPathGradient` measures lowering a
128-point elliptical contour with three alternating surround colors, an
anisotropic focus region, and a triangular blend curve. Local benchmark numbers
on the 2026-08-27 ARM64/.NET 10.0.11 ShortRun are a 3.807 microsecond median
(3.706 microsecond mean, 0.366 microsecond standard deviation) with 6.34 KB
allocated. One launch, three warmups, three measured iterations, and denied
process-priority elevation make this coarse subsystem evidence; hosted CI
captures the same benchmark with every pull request.

ApiCompat removes the final ordinary managed renderer-type suppression.
Measured debt falls from 9 missing types, 98 missing members, 15 other
diagnostics, and 122 total to 8 missing types, 98 missing members, 15 other
diagnostics, and 121 total, with no new incompatibility or stale suppression.
The remaining types and most remaining members are explicitly native-only
Metafile, HDC/HWND, resource-handle, and screen-copy boundaries.

## Remaining differential work

This slice establishes a real portable path-gradient renderer but does not yet
claim pixel-exact GDI+ equivalence. Windows image differentials are still
needed for concave/self-intersecting contours, paths containing several
figures, focus scales outside the usual `[0, 1]` range, duplicate curve stops,
and the precise color-interpolation transfer function. A path containing
several figures currently retains its longest flattened contour. Contours over
128 points use deterministic perimeter-order decimation rather than an
error-minimizing simplifier. These are explicit quality follow-ups, not reasons
to restore the API suppression or route portable drawing through an opaque
native handle.
