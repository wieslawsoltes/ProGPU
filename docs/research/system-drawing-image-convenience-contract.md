# System.Drawing Image-Convenience Contract

Date: 2026-08-26

## Scope and sources

This slice implements the official [`Image.GetThumbnailImage`](https://learn.microsoft.com/dotnet/api/system.drawing.image.getthumbnailimage?view=windowsdesktop-10.0), [`Image.GetThumbnailImageAbort`](https://learn.microsoft.com/dotnet/api/system.drawing.image.getthumbnailimageabort?view=windowsdesktop-10.0), and coordinate [`Graphics.DrawIcon`](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.drawicon?view=windowsdesktop-10.0) members from the pinned .NET 10.0.11 contract.

The callback parameter is retained for source and binary compatibility but is not invoked. This follows the current managed contract after removal of the former GDI+ abort callback. Thumbnail dimensions must be positive. The current portable implementation supports bitmap-backed images; other `Image` subclasses fail explicitly instead of returning a blank or fabricated thumbnail. Coordinate `DrawIcon` uses the icon's native dimensions and validates the icon before recording.

The public signatures and observable behavior are checked against the reference assembly and Microsoft documentation. The retained implementation is original ProGPU code and does not call or copy GDI+ internals.

## Typed implementation

`GetThumbnailImage` creates a `Bitmap` through the existing typed retained-texture resize constructor. That path records one source-rectangle-to-destination-rectangle image command with high-quality bicubic sampling and retains the source texture until the destination bitmap is rendered. It does not read a native handle, create an HDC, scan private fields, or initialize a platform bitmap compatibility object.

`DrawIcon(icon, x, y)` converts the typed managed icon snapshot to a bitmap and records the existing unscaled image command at the requested coordinates. The temporary bitmap is disposed after recording; the drawing command owns its typed texture lease, so deferred rendering remains valid.

The destination-point-array `DrawImage` family was left for a separate renderer contract. It is now implemented with truthful retained affine and perspective texture mapping; see [`system-drawing-destination-point-image-contract.md`](system-drawing-destination-point-image-contract.md).

## Gates and evidence

Ten focused test cases cover:

- requested thumbnail dimensions and scaled nontransparent pixels;
- compatibility callback non-invocation;
- all non-positive dimension combinations;
- explicit rejection of images without typed bitmap pixels;
- native-size icon pixels and coordinate placement;
- null validation before command recording; and
- a 4,608-byte-per-operation upper allocation bound across 32 warmed thumbnail creations.

The complete drawing suite passes 210/210. ApiCompat removes one missing-type and two missing-member suppressions, reducing measured debt from 46 missing types, 284 missing members, 47 other diagnostics, and 377 total to 45 missing types, 282 missing members, 47 other diagnostics, and 374 total. The gate reports no breaking changes or stale suppressions. LibreWinForms downstream validation rebuilds the ProGPU adapter with 0 warnings and 0 errors, passes 10/10 backend tests, rebuilds canonical `System.Windows.Forms` with 613 known compatibility warnings and 0 errors, and passes 24/24 lifecycle tests.

The 2026-08-26 ARM64/.NET 10.0.11 BenchmarkDotNet ShortRun measured 64x64-to-32x32 thumbnail creation and disposal at a 170.455 microsecond median (192.464 microsecond mean, 38.656 microsecond standard deviation) with 7.77 KB allocated. The runner could not acquire high process priority and used three measured iterations, so this is a coarse local subsystem checkpoint. The focused allocation gate is the deterministic regression guard.
