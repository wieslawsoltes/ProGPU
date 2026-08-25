# Avalonia Skia source dependency

This non-shipping compatibility project compiles the unmodified
`src/Skia/Avalonia.Skia` project sources from the prepared Avalonia `12.1.1`
source tree. It exists only to validate the ProGPU `SkiaSharp` API surface
against Avalonia's ordinary Skia backend.

The source tree is prepared by `tools/prepare-avalonia-12.1.1-source.sh` from
the official Avalonia release and is not copied into ProGPU. Avalonia is
licensed under the MIT license:

- <https://github.com/AvaloniaUI/Avalonia/tree/12.1.1>
- <https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/licence.md>

ProGPU-specific rendering and windowing code is not added to this dependency
project. The shipping replacement backend remains the original typed
implementation in `ProGPU.Avalonia.Rendering` and
`ProGPU.Avalonia.SilkNet`.
