# System.Drawing Graphics Image-Overload Contract

Date: 2026-08-26

## Scope and sources

This slice completes the point, unscaled/clipped, point-anchored source rectangle, and float-source destination rectangle members of the official `Graphics.DrawImage` family. The public contract is defined by the pinned .NET 10.0.11 reference assembly and [Graphics.DrawImage](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.drawimage?view=windowsdesktop-10.0) / [Graphics.DrawImageUnscaled](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.drawimageunscaled?view=windowsdesktop-10.0) documentation.

The destination-point-array overloads are intentionally outside this slice. Three points require affine source-to-parallelogram mapping and four points require perspective mapping; those members remain explicit ApiCompat debt until the retained texture command has a reviewed mapping contract. They are not approximated with an axis-aligned bounding rectangle.

## Typed behavior

Point and integer-coordinate overloads retain the image's pixel dimensions. The four-integer `DrawImageUnscaled` overload follows the unscaled contract and ignores its width/height compatibility parameters. `DrawImageUnscaledAndClipped` intersects source dimensions with the requested destination extent and draws the matching source rectangle at the same size, avoiding accidental stretch.

Point-anchored source-rectangle overloads convert the declared source unit to pixels and use the converted size as the destination extent. Float-source destination-rectangle overloads share the existing typed texture recording path, including sampling mode, retained texture lifetime, remap tables, color matrices, and current graphics transform. Abort callbacks run before resource retention or command recording; a `true` result records nothing.

Only `Bitmap` has a portable retained texture implementation today. Null images fail at the public boundary. This slice does not introduce an HDC, GDI+, screen-capture dependency, runtime reflection, or a platform bitmap wrapper.

## Gates and evidence

Five focused production tests cover:

- `Point` and integer-coordinate placement;
- unscaled size preservation even when compatibility width/height arguments differ;
- clipped source/destination pixels without scaling;
- floating-point and integer point-anchored source cropping;
- remap attributes plus false/true callback paths; and
- null validation before recording.

The complete drawing suite passes 183/183. ApiCompat removes eleven exact member suppressions, reducing measured debt from 48 missing types, 303 missing members, 47 other diagnostics, and 398 total to 48 missing types, 292 missing members, 47 other diagnostics, and 387 total. The gate reports no breaking changes or stale suppressions. LibreWinForms downstream validation rebuilds the ProGPU adapter with 0 warnings and 0 errors, passes 10/10 backend tests, rebuilds canonical `System.Windows.Forms` with 613 known compatibility warnings and 0 errors, and passes 24/24 lifecycle tests.
