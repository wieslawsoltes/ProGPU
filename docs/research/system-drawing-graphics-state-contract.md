# System.Drawing Graphics State and Transform Contract

Date: 2026-08-26

## Contract sources

This is an original portable implementation based on the public .NET contracts and the pinned .NET 10.0.11 reference assembly used by ApiCompat:

- [Graphics.CompositingMode](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.compositingmode?view=windowsdesktop-10.0)
- [CompositingMode](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.compositingmode?view=windowsdesktop-10.0)
- [Graphics.RenderingOrigin](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.renderingorigin?view=windowsdesktop-10.0)
- [Graphics.TextContrast](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.textcontrast?view=windowsdesktop-10.0)
- [Graphics.TransformElements](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.transformelements?view=windowsdesktop-10.0)
- [Graphics transform methods](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.transform?view=windowsdesktop-10.0)
- [Graphics.IsVisible](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.isvisible?view=windowsdesktop-10.0)

The official state defaults are source-over compositing, an empty rendering origin, text contrast 4, and an identity world transform. Text contrast accepts 0 through 12. `TransformElements` rejects a non-invertible matrix. The explicit transform overloads honor `MatrixOrder.Prepend` and `MatrixOrder.Append`, and the scalar rectangle visibility overloads use the same effective clip as the rectangle-value overloads.

## Typed portable behavior

`CompositingMode.SourceCopy` lowers to a retained `GpuBlendMode.Src` scope. The scope is closed before bitmap materialization or host batch handoff and restored afterward, so each flush is balanced and later drawing preserves the selected mode. Disposal closes the scope exactly once. `SourceOver` uses the renderer default.

`RenderingOrigin` is applied to `HatchBrush` through a typed two-float origin on `TilePatternBrush`; compositor compilation derives the coordinate transform that shifts the 8×8 tile. Keeping two floats instead of a retained 4×4 matrix preserves the existing bounded allocation per hatch lowering. The same origin is applied to hatch brushes used by pens.

ProGPU text is shaped and rendered through vector/glyph-atlas coverage rather than the GDI rasterizer. `TextContrast` is therefore validated and retained across save/restore, but does not distort the renderer's coverage values to emulate a platform rasterizer that is not present.

`TransformElements` reads and writes the existing `Matrix3x2` world transform directly, without allocating a disposable `Matrix`. Ordered translate, scale, rotate, and multiply overloads all use the shared typed affine implementation. Rectangle visibility delegates to the existing `Region`/visible-device-bounds geometry.

No HDC, GDI+, runtime reflection, private-field scan, or WinForms-shaped compatibility object is introduced.

## Gates and evidence

The focused suite covers:

- exact enum identity, defaults, validation, and disposed behavior;
- save/restore of compositing mode, rendering origin, text contrast, and world transform;
- append/prepend transform composition and the one-argument multiply overload;
- integer and floating-point rectangle visibility against the effective clip;
- production bitmap readback proving `SourceCopy` replaces destination alpha;
- production hatch pixels proving rendering-origin phase changes; and
- the pre-existing 32–96-byte hatch-lowering allocation bound.

The complete drawing suite passes 178/178. ApiCompat removes one missing-type and fourteen missing-member suppressions, reducing measured debt from 49 missing types, 317 missing members, 47 other diagnostics, and 413 total to 48 missing types, 303 missing members, 47 other diagnostics, and 398 total. The gate reports no breaking changes or stale suppressions. LibreWinForms downstream validation rebuilds the ProGPU adapter with 0 warnings and 0 errors, passes its 10/10 backend tests, rebuilds canonical `System.Windows.Forms` with 613 known compatibility warnings and 0 errors, and passes 24/24 canonical lifecycle tests.
