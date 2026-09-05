# System.Drawing Drawing-Identity Contract

Date: 2026-08-27

## Scope and sources

This slice implements the official [`QualityMode`](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.qualitymode?view=windowsdesktop-10.0), [`StringUnit`](https://learn.microsoft.com/dotnet/api/system.drawing.stringunit?view=windowsdesktop-10.0), [`PenType`](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.pentype?view=windowsdesktop-10.0), and [`Pen.PenType`](https://learn.microsoft.com/dotnet/api/system.drawing.pen.pentype?view=windowsdesktop-10.0) identities from the pinned .NET 10.0.11 contract.

`QualityMode` supplies the canonical invalid/default/low/high values referenced by the specialized drawing-quality enums. `StringUnit` preserves the graphics-unit numeric identities plus the text-specific `Em = 32` value; the official contract currently does not consume this enum in another public API. `PenType` describes the brush that fills a pen stroke, and the read-only `Pen.PenType` value follows the pen's current brush kind.

The public shapes and numeric identities are checked against the reference assembly and Microsoft documentation. The implementation is original ProGPU code.

## Typed implementation and explicit boundary

`Pen.PenType` uses direct type matches for the supported managed brush hierarchy: `SolidBrush`, `HatchBrush`, `TextureBrush`, `PathGradientBrush`, and `LinearGradientBrush`. No native pen query, GDI+ handle, runtime reflection, private-field scan, or fake compatibility object is used. The typed path-gradient renderer is documented in [`system-drawing-path-gradient-contract.md`](system-drawing-path-gradient-contract.md).

This checkpoint left the separate `Pen.Transform` family suppressed because transforming the centerline would have been incorrect. The later [pen-transform slice](system-drawing-pen-transform-contract.md) resolves that debt with a typed inverse-space widening model shared by rendering, widening, bounds, and hit testing.

## Gates and evidence

Five focused tests cover exact enum values, every currently supported brush-to-pen mapping, and zero managed allocation across 4,096 warmed `PenType` reads. The complete drawing suite passes 215/215. ApiCompat removes three missing-type and one missing-member suppressions, reducing measured debt from 45 missing types, 282 missing members, 47 other diagnostics, and 374 total to 42 missing types, 281 missing members, 47 other diagnostics, and 370 total. The gate reports no breaking changes or stale suppressions. LibreWinForms downstream validation rebuilds the ProGPU adapter with 0 warnings and 0 errors, passes 10/10 backend tests, rebuilds canonical `System.Windows.Forms` with 613 known compatibility warnings and 0 errors, and passes 24/24 lifecycle tests.
