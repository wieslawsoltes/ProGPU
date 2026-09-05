# System.Drawing bitmap pixel-memory contract

## Scope

This record covers the managed pixel-memory surface used by LibreWinForms and image-processing callers:

- the complete `PixelFormat` value/flag identity set;
- `Bitmap(int, int, int, PixelFormat, IntPtr)` row decoding;
- both `LockBits` overloads, including `ImageLockMode.UserInputBuffer`;
- `BitmapData` layout and validation;
- packed, indexed, straight-alpha, premultiplied-alpha, and high-depth row conversion; and
- rectangle/rectangle-float crop clones with an observable destination format.
- in-place `Bitmap.ConvertFormat` with fixed/custom/optimal palette selection, alpha thresholds, and all public dithering modes.

The public contract was checked against the .NET 10.0.11 reference assembly and the official [`PixelFormat`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.pixelformat?view=windowsdesktop-10.0), [`BitmapData`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.bitmapdata?view=windowsdesktop-10.0), [`Bitmap.LockBits`](https://learn.microsoft.com/dotnet/api/system.drawing.bitmap.lockbits?view=windowsdesktop-10.0), [`Bitmap` scan0 constructor](https://learn.microsoft.com/dotnet/api/system.drawing.bitmap.-ctor?view=windowsdesktop-10.0#system-drawing-bitmap-ctor(system-int32-system-int32-system-int32-system-drawing-imaging-pixelformat-system-intptr)), and [`Bitmap.Clone`](https://learn.microsoft.com/dotnet/api/system.drawing.bitmap.clone?view=windowsdesktop-10.0) documentation. Official dotnet/winforms source was used only to confirm public signature, validation, enum-value, and layout facts. The implementation is original ProGPU managed code.

## Architecture

`Bitmap` retains one canonical row-major RGBA buffer plus an alpha-mode tag when it is CPU-backed. `LockBits` is a typed conversion boundary around that buffer:

1. pending retained drawing commands are flushed;
2. GPU-backed content is read once when CPU access is required;
3. the requested rectangle is encoded into official BGR/BGRA, packed indexed, 16-bit, 48-bit, or 64-bit row layout;
4. caller-owned buffers remain caller-owned and are never pinned or freed by `Bitmap`;
5. write-capable locks decode the same buffer back into canonical RGBA on `UnlockBits`; and
6. the updated rectangle is uploaded through the existing typed texture sub-rectangle seam when a texture exists.

No renderer command, native GDI+ handle, reflection probe, Skia path, shader ABI, or native command wire is involved. Packed/high-depth formats are an API memory representation; ProGPU's renderer continues to consume the existing RGBA texture contract.

The scan0 constructor snapshots the supplied rows into managed RGBA storage. This intentionally does not retain an unowned pointer after construction; callers may release their source allocation once construction returns. Long-lived zero-copy external-memory aliasing would require a separate typed lifetime owner rather than an unverifiable raw-pointer dependency.

## Row contracts

- 1-bit and 4-bit indexed rows use most-significant pixel packing; 8-bit rows use one palette index per pixel.
- Indexed writes choose the nearest palette entry using deterministic squared ARGB distance.
- 16-bit RGB 5:5:5, RGB 5:6:5, ARGB 1:5:5:5, and grayscale conversions use the full declared channel range.
- 24/32-bit rows use the official BGR/BGRA byte order.
- 48/64-bit rows use little-endian 16-bit B, G, R, and optional A channels.
- PArgb formats preserve premultiplied channel storage across the CPU/GPU alpha-mode boundary.
- Positive and negative caller strides are supported; the pointer addresses the first logical row and the signed stride locates subsequent rows.

## Validation and remaining debt

`ConvertFormat` preserves the canonical RGBA renderer boundary while replacing the bitmap's public memory format. Non-indexed formats round-trip through the same row codecs as `LockBits`. Indexed conversion selects a caller palette, a fixed `PaletteType`, or a deterministic optimal palette; applies the documented alpha threshold when a transparent entry exists; and materializes palette colors in the canonical store. `None`/`Solid`, ordered Bayer 4×4/8×8/16×16, spiral/dual-spiral 4×4/8×8, and fixed-point Floyd-Steinberg error diffusion are deterministic CPU algorithms. Reduced direct-color 5:5:5, 5:6:5, and 1:5:5:5 conversion also applies ordered or error-diffusion quantization instead of silently ignoring the requested mode.

Focused tests cover enum identities/classifiers, scan0 decoding, caller-buffer ownership, read/write round trips, packed/high-depth quantization, crop cloning, stale/unrelated unlock rejection, zero-allocation warmed read-only caller locks, palette/alpha-threshold behavior, every dithering mode, direct-color dithering, deterministic results, validation, and bounded clone-and-convert allocation. `BitmapPixelMemoryBenchmarks.CopyRgbaToCallerOwnedLockBuffer` measures a fixed 256×256 BGRA export without GPU initialization. `ConvertRgbaToErrorDiffusedIndexedClone` measures a fixed 256×256 RGBA clone converted to a 4-bit custom palette with error diffusion.

Codec loading/saving beyond the existing PNG/BMP paths, effect processing, encoder parameterization, and native HBITMAP/HICON adapters remain separate reviewed imaging debt.
