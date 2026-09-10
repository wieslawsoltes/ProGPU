# System.Drawing Graphics Coordinate-Space Contract

Date: 2026-08-26

## Scope and sources

This slice implements the official [`CoordinateSpace`](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.coordinatespace?view=windowsdesktop-10.0) identity and all array/span [`Graphics.TransformPoints`](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.transformpoints?view=windowsdesktop-10.0) entry points from the pinned .NET 10.0.11 contract.

`World` identifies coordinates before the graphics world transform. `Page` identifies coordinates after the world transform and before the page-unit/page-scale plus host transform. `Device` identifies final surface coordinates. The operation converts from the declared source space to device space and then applies the inverse destination-to-device matrix.

## Typed implementation

The conversion matrices are the same values used by retained drawing:

- world to device: world transform × page transform × typed host base transform;
- page to device: page transform × typed host base transform; and
- device to device: identity.

This keeps coordinate queries consistent with rendered output for bitmap graphics and framework-hosted graphics, including explicit outer transforms supplied by LibreWinForms. No HDC, GDI+, platform coordinate probe, runtime reflection, or private-state scan is required.

Array overloads validate null before forwarding to their span counterparts. The .NET 10 `ReadOnlySpan<T>` signatures update the caller-owned backing storage in place, matching the established `System.Drawing` span contract without allocating a copy. Integer points use the same `Point.Round` policy as the portable `Drawing2D.Matrix` implementation.

Invalid coordinate values, empty point sets, disposed graphics, and a non-invertible destination coordinate space fail at the public boundary. Same-space conversion still performs validation and then leaves storage unchanged.

## Gates and evidence

Five focused tests cover:

- exact enum names and values;
- all six directed conversions among world, page, and device spaces;
- simultaneous world, page-scale, page-unit, and host base transforms;
- array and `ReadOnlySpan<T>` mutation for integer and floating-point points;
- null, empty, invalid-space, and disposed validation; and
- zero managed allocation across 1,024 warmed span conversions.

The complete drawing suite passes 188/188. ApiCompat removes one missing-type and four missing-member suppressions, reducing measured debt from 48 missing types, 292 missing members, 47 other diagnostics, and 387 total to 47 missing types, 288 missing members, 47 other diagnostics, and 382 total. The gate reports no breaking changes or stale suppressions. LibreWinForms downstream validation rebuilds the ProGPU adapter with 0 warnings and 0 errors, passes 10/10 backend tests, rebuilds canonical `System.Windows.Forms` with 613 known compatibility warnings and 0 errors, and passes 24/24 lifecycle tests.
