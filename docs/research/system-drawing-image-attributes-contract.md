# System.Drawing image-attributes contract

## Contract sources

The public surface is pinned to the .NET 10.0.11 `System.Drawing.Common` reference assembly. Behavior was checked against Microsoft documentation and the upstream managed wrapper's public category contract; the portable adjustment engine is original managed ProGPU code.

- [`ImageAttributes`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.imageattributes?view=windowsdesktop-10.0) defines color adjustment state used by images, brushes, pens, and text.
- [`ColorAdjustType`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.coloradjusttype?view=windowsdesktop-10.0) identifies the default, bitmap, brush, pen, and text categories. `Count` and `Any` are sentinels rather than adjustment-state slots.
- [`SetColorKey`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.imageattributes.setcolorkey?view=windowsdesktop-10.0) makes the inclusive per-component RGB range transparent.
- [`SetColorMatrices`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.imageattributes.setcolormatrices?view=windowsdesktop-10.0), [`ColorMatrixFlag`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.colormatrixflag?view=windowsdesktop-10.0), [`SetGamma`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.imageattributes.setgamma?view=windowsdesktop-10.0), and [`SetThreshold`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.imageattributes.setthreshold?view=windowsdesktop-10.0) define the managed component transforms.
- [`SetNoOp`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.imageattributes.setnoop?view=windowsdesktop-10.0) temporarily bypasses a category's retained adjustments until `ClearNoOp` restores them.
- [`ColorChannelFlag`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.colorchannelflag?view=windowsdesktop-10.0) and [`SetOutputChannel`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.imageattributes.setoutputchannel?view=windowsdesktop-10.0) select a CMYK separation.
- [`SetOutputChannelColorProfile`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.imageattributes.setoutputchannelcolorprofile?view=windowsdesktop-10.0) consumes a host color-profile file and therefore crosses the local-OS color-management boundary.

## Restored API and state model

The slice restores `ColorChannelFlag` with its official values and removes all 29 `ImageAttributes` missing-member suppressions. The restored group includes brush remapping; color keys; paired color and grayscale matrices; gamma; threshold; no-op; output-channel selection; output-profile methods; and every corresponding clear/category overload.

Each of the five real adjustment categories owns typed state. Bitmap, brush, pen, and text operations use the default state until the first operation explicitly configures that category; an explicit category then starts from an empty state and no longer inherits defaults. Setters snapshot mutable matrices and remap entries. `Clone` deep-copies every category, wrap state remains independently owned, and disposed instances reject subsequent operations.

`SetNoOp` does not destroy retained state. It bypasses all transforms for that category, while `ClearNoOp` exposes the prior settings again. Brush remaps are resolved through the brush category when `TextureBrush` creates its owned adjusted image; `Graphics.DrawImage` resolves the bitmap category; and palette adjustment resolves the caller-selected category.

## Managed adjustment pipeline

The deterministic portable pixel pipeline applies exact-color remapping, inclusive RGB color-key transparency, color/gray matrix selection, gamma correction, component thresholding, and optional CMYK separation in that order. `SkipGrays` bypasses a matrix for equal-RGB pixels. `AltGrays` uses the snapshotted gray matrix for equal-RGB pixels and the color matrix for other pixels. Gamma uses `pow(component, 1 / gamma)`, threshold compares normalized components directly with the caller's 0–1 breakpoint, and alpha is preserved except when a color key makes the pixel transparent.

CMYK channel output is a deterministic managed separation: cyan, magenta, and yellow use the inverse RGB components; black uses the minimum ink component; and the selected ink amount is rendered as black-on-white grayscale. This provides portable output-channel behavior without pretending that an ICC transform occurred.

The existing renderer fast path is preserved. Exact remapping remains one CPU pixel pass, and a default color matrix remains a typed GPU image effect. Color keys, gamma, threshold, gray-specific matrices, and output-channel separation use one explicit CPU snapshot because those effects cannot be represented by the current single-matrix shader contract. No reflection, native-handle probing, or WinForms-shaped compatibility object is introduced.

## Platform boundary and proposed follow-up

ICC profile parsing and host color-management policy are not portable file-only operations. `SetOutputChannelColorProfile` now has the official public shape but fails immediately with `PlatformNotSupportedException` describing the missing typed color-management adapter. It does not silently accept a profile that the renderer would ignore.

The follow-up is a narrow `IColorProfileTransform`-style service owned by the platform/backend layer: resolve and validate a profile path, compile an immutable transform, apply it to caller-owned straight-alpha pixels, and expose deterministic lifetime/error behavior. A Windows adapter can preserve Windows Color System/GDI+ policy; Linux and macOS adapters can bind their explicit local color-management services. A managed ICC engine can be added only if it is measured against the same fixtures. The resulting transform should be cacheable by profile identity and must not leak OS profile handles into `ImageAttributes`.

Exact GDI+ differential fixtures should also be captured on Windows for multi-effect ordering, rounding at component boundaries, and `ColorChannelLast`. Until those fixtures exist, ProGPU rejects `ColorChannelLast` as the non-channel sentinel rather than inventing persistent native state.

## Quality and performance evidence

Six focused tests cover enum identity, category fallback and override, brush remapping, setter snapshots, color-key/gamma/threshold pixels and clear behavior, gray-matrix modes, no-op semantics, CMYK output, validation, explicit ICC failure, disposal, and bounded allocation. The complete drawing suite passes 246/246.

The warmed 64×64 gamma-plus-threshold allocation gate requires 16,384–20,000 bytes per adjusted bitmap. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured `GammaThresholdCpuBackedIcon64x64` at a 120.910 µs median (120.647 µs mean, 0.760 µs standard deviation) with 16.39 KB allocated. One launch, three measured iterations, and denied process-priority elevation make this a coarse local subsystem checkpoint rather than an end-to-end renderer claim.

ApiCompat debt falls from 41 missing types, 173 missing members, 23 other diagnostics, and 237 total to 40 missing types, 144 missing members, 23 other diagnostics, and 207 total. Exact suppressions pass with no new breaking changes or stale entries.
