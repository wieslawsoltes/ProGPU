# System.Drawing native font and graphics interop contract

## Scope

This checkpoint restores the .NET 10 public identities for
`Font.FromHdc(IntPtr)`, `Graphics.FromHdcInternal(IntPtr)`,
`Graphics.FromHwndInternal(IntPtr)`, and `Graphics.GetHalftonePalette()`. It
also replaces the previous placeholder behavior of the already-present public
`Graphics.FromHdc` and `Graphics.FromHwnd` overloads. The pinned
`Microsoft.WindowsDesktop.App.Ref` 10.0.11 assembly defines the exact API
surface. Official Microsoft documentation defines the observable native
meaning of [font import from an HDC](https://learn.microsoft.com/dotnet/api/system.drawing.font.fromhdc),
[graphics import from an HDC](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.fromhdc),
[graphics import from an HWND](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.fromhwnd),
and [the Windows halftone palette](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.gethalftonepalette).

These APIs describe Windows GDI objects. ProGPU therefore does not invent a
portable meaning for raw integers, fabricate an empty recorder for a window
handle, call GDI through an unreviewed product path, or infer native state with
reflection, private-field scans, or duck typing.

## Typed adapter boundary

`INativeFontInteropService` imports the font currently selected into an exact
nonzero device-context handle. `INativeGraphicsInteropService` receives the
exact HDC and optional device handle, the exact HWND (including zero, which a
Windows adapter may interpret as the desktop), or a request to create a
halftone-palette handle. Each capability has an independent process-scoped,
single-owner registration so a host can provide graphics interop without
claiming native font selection.

The native graphics adapter returns an owned `Graphics` instance. It can use
the public `Graphics.FromProGpuDrawingContext` overloads to preserve explicit
device bounds, a finite 2D host transform, a target WebGPU context, synchronous
flush handling, and exactly-once completion. The caller owns and disposes the
returned object. Palette handles stay explicitly native and follow the host
adapter's platform ownership rules; ProGPU neither wraps nor fabricates them.

A zero HDC is rejected before capability lookup. A zero HWND is transported
unchanged because it has an official platform interpretation. Missing
capabilities throw `PlatformNotSupportedException`, and an adapter returning a
null managed product fails explicitly. `FromHdcInternal` and
`FromHwndInternal` are canonical aliases over the same typed paths rather than
separate behavior.

## Quality and performance gates

Five focused cases cover exact HDC/device/HWND transport, all public and
internal entries, palette identity, adapter-supplied bounds and transform,
exactly-once completion, independent single-owner registrations, validation
order, missing capabilities, null products, and zero allocation across 10,000
warmed palette dispatches. The legacy curve/path/headless tests now construct
portable recorders explicitly through `FromProGpuDrawingContext`; 32 focused
ProGPU compatibility cases and 61 headless GDI-shim cases pass after removal of
the fake `FromHwnd(IntPtr.Zero)` recorder path.

ApiCompat removes four member suppressions and reaches 0 missing types, 1
missing member, 13 other diagnostics, and 14 total, with no breaking changes or
stale suppressions. `Graphics.AddMetafileComment(byte[])` is the sole remaining
missing member and remains tied to the separate portable metafile-recording
checkpoint; adding an API-only method to ordinary graphics would violate its
documented recording-only behavior.

`NativeDrawingInteropBenchmarks.GetHalftonePaletteDispatch` isolates the typed
registry plus a state-changing, no-inline provider without OS work. The
2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a 0.634 ns median (0.609 ns
mean, 0.074 ns standard deviation) with zero managed allocation. This workload
is close to the timer and harness resolution and has a confidence interval
wider than its mean; it proves the absence of managed allocation but is not a
portable absolute latency claim.
