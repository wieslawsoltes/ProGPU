# Avalonia Skia source provenance

Every `.cs` file in this directory is copied without modification from:

- repository: <https://github.com/AvaloniaUI/Avalonia>
- release: `12.0.5`
- commit: `fee9c561ce036e8a3e8cee2397c75ca599b4790d`
- source directory: `src/Skia/Avalonia.Skia`

The deterministic SHA-256 over the 54 sorted relative `.cs` paths, a zero byte
after each path, and each file's bytes is:

`b449fb8ed977fcafa9ebc006f0a38f9229d7f78ce4a1986ceccc8fd1cbaf2d2f`

`Avalonia.Skia.csproj` is the out-of-tree ProGPU build adapter and is not an
upstream source file. The live WebGPU presentation bridge is implemented in
`ProGPU.Backend`, `ProGPU.Avalonia.SilkNet`, and the ProGPU SkiaSharp shim, so
the Avalonia backend source remains unchanged.

The upstream MIT license is reproduced in `AVALONIA-LICENSE.md`.
