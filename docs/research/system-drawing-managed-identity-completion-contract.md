# System.Drawing managed identity completion contract

## Source contract

This slice restores the .NET 10 `System.Drawing.CopyPixelOperation` raster-operation identity and corrects `ToolboxBitmapAttribute` from sealed to inheritable. The enum preserves every official Win32 ROP value, including the signed high-bit `NoMirrorBitmap` value and the independent `CaptureBlt` modifier.

## Portable boundary

Enum identity does not imply that desktop capture is a renderer operation. The four `Graphics.CopyFromScreen` overloads remain explicit debt until a typed local-OS screen-capture service can supply pixels and capability/error semantics without routing ProGPU drawing through an HDC. Similarly, toolbox-image loading remains managed resource behavior; inheritance requires no reflection-based product path or backend discovery.

## Quality gate

Focused tests verify all raster-operation values and prove ordinary subclassing of `ToolboxBitmapAttribute`. This slice adds no hot-path code and therefore needs no performance benchmark.
