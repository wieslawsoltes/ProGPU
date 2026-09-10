# System.Drawing Hatch-Brush Contract and Backend Applicability

Date: 2026-08-22

## Contract sources

This is a clean-room implementation based on Microsoft public documentation and the pinned .NET 10.0.11 reference assembly used by ApiCompat. Framework implementation source and private pattern tables were not used.

- [HatchBrush class](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.hatchbrush?view=windowsdesktop-10.0)
- [HatchBrush constructors](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.hatchbrush.-ctor?view=windowsdesktop-10.0)
- [HatchStyle enum](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.hatchstyle?view=windowsdesktop-10.0)
- [GDI+ hatch-style contract](https://learn.microsoft.com/windows/win32/api/gdiplusenums/ne-gdiplusenums-hatchstyle)
- the pinned `Microsoft.WindowsDesktop.App.Ref` 10.0.11 `System.Drawing.Common.dll`

The official surface has two constructors, three read-only properties, and `Clone`. The two-argument constructor initializes the background to black. The enum retains the historical aliases `Min = Horizontal = 0` and `Max = LargeGrid = Cross = 4`, while concrete valid styles continue through `SolidDiamond = 52`; constructor validation therefore uses the concrete 0–52 interval rather than the misleading `Max` alias.

## Original managed pattern policy

`HatchBrush` stores immutable style and color values and lowers to a typed `ProGPU.Vector.TilePatternBrush`. One `ulong` represents a repeating 8×8 foreground mask; cleared bits select the independent background color. Basic line, diagonal, grid, checker, and declared-density styles are generated from original coordinate rules. Named decorative styles use original fixed 8-row motifs chosen to preserve their documented visual category. No bitmap allocation, reflection, native GDI/GDI+ handle, or private-field scan occurs during lowering.

Percentage patterns use an 8×8 ordered threshold distribution with 3, 6, 13, 16, 19, 26, 32, 38, 45, 48, 51, and 58 foreground samples for the documented 5% through 90% styles. This keeps density monotonic and spatially distributed. The tile sampler uses signed modulo before converting to an unsigned bit index, preserving phase at negative coordinates.

## Renderer and native applicability

The existing 256-byte GPU/native brush record gains material kind `8`; its `stopCount` and `stopOffset` fields carry the low and high 32-bit mask words for this non-gradient kind, while `Color0` and `Color1` carry foreground and background. The record size, alignment, bind groups, command stream layout, and gradient auxiliary record remain unchanged.

Both production vector WGSL and the hatch-extension WGSL evaluate the same O(1) tile rule and preserve brush opacity. The C# native stream validator, standalone C++ scene builder, semantic validator, brush-page compiler, and layer/composite-mask paths distinguish tile mask words from gradient stop offsets. SKPicture archive version 4 appends a tile-pattern brush kind and serializes its complete immutable value payload.

## Gates and evidence

The focused managed suite covers:

- exact public enum values and aliases;
- constructor validation, default background, immutable properties, clone ownership, and disposal;
- exact basic masks, monotonic percentage populations, and visible bounded tiles for all 53 concrete styles;
- exact two-color lowering and negative-coordinate sampling;
- production shader structure and native brush-table acceptance of both mask words; and
- one bounded allocation per lowering after warmup.

The pinned ApiCompat report removes both formerly missing-type diagnostics with no new unsuppressed or breaking diagnostics, reducing measured debt from 533 to 531. The changed C++ translation units compile cleanly under GCC 13 C++20 with `-Wall -Wextra -Wpedantic -Werror`; the full CMake/CTest lane remains the hosted native CI authority because this development image does not contain CMake.
