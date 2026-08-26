# System.Drawing pen ownership and known-color immutability contract

## Contract sources

This is a clean-room managed implementation based on the pinned .NET 10.0.11 reference assembly and Microsoft public documentation. No framework implementation source was copied.

- [`Pen`](https://learn.microsoft.com/dotnet/api/system.drawing.pen?view=windowsdesktop-10.0) is a sealed `MarshalByRefObject` implementing `ICloneable` and `IDisposable`.
- [`Pen.Clone`](https://learn.microsoft.com/dotnet/api/system.drawing.pen.clone?view=windowsdesktop-10.0) creates an exact copy, while [`Pen.Dispose`](https://learn.microsoft.com/dotnet/api/system.drawing.pen.dispose?view=windowsdesktop-10.0) leaves the pen unusable.
- [`Pen.Brush`](https://learn.microsoft.com/dotnet/api/system.drawing.pen.brush?view=windowsdesktop-10.0) defines the brush used by the pen and documents `ArgumentException` when mutation is attempted on a system pen.
- [`Pens`](https://learn.microsoft.com/dotnet/api/system.drawing.pens?view=windowsdesktop-10.0) explicitly documents that returned pens are immutable.
- [`SolidBrush.Color`](https://learn.microsoft.com/dotnet/api/system.drawing.solidbrush.color?view=windowsdesktop-10.0) documents the immutable-brush mutation failure.
- Native GDI+ exposes pen brush state through typed [`Pen::GetBrush`](https://learn.microsoft.com/windows/win32/api/gdipluspen/nf-gdipluspen-pen-getbrush), not through shared managed-object identity.

## Managed ownership model

`Pen` now owns its brush state. A brush passed to either constructor or the `Brush` setter is cloned before it becomes pen state. Reading `Brush` returns another clone, so mutating or disposing the caller's brush or the returned brush cannot change the pen. `Clone` duplicates the owned brush and custom dash array; disposing either pen does not invalidate the other.

The known-color cache publishes immutable `SolidBrush` and `Pen` instances. Attempts to change or dispose `Brushes.*`, `SystemBrushes.*`, `Pens.*`, or `SystemPens.*` resources fail with `ArgumentException`; cloning a cached resource produces an ordinary mutable, independently disposable object. This prevents a process-wide resource from being corrupted by one consumer.

Normal pens are idempotently disposable and reject subsequent property, clone, or renderer use with `ObjectDisposedException`. `Pen` now has the official sealed/base/interface shape. The ProGPU lowering method is internal rather than an extra public API.

## Renderer and performance boundary

The renderer consumes the pen's owned brush through an internal typed seam. It does not call the public defensive `Brush` getter, inspect private state through reflection, expose ProGPU types in the public contract, or allocate a managed brush clone per draw. Scalar `Color`, `PenType`, and `Width` reads use the owned state directly.

The focused allocation gate warms `Pens.CornflowerBlue` and requires exactly zero managed bytes across 100,000 `Color`, `PenType`, and `Width` read groups. `KnownColorResourceBenchmarks.ReadCachedPenStateBatch` measures the same three scalar reads. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured 2.271 ns per operation and 0 B allocated. One launch and three measured iterations make this a coarse local regression checkpoint, not an end-to-end renderer claim.

## Validation

Six focused tests cover constructor and setter snapshots, defensive getters, non-solid brush ownership, independent pen/dash cloning, post-disposal rejection, cached resource immutability, mutable clones, and zero-allocation warmed scalar reads. The complete drawing suite passes 225/225.

ApiCompat removes the remaining `Pen` shape suppression, reducing measured debt from 42 missing types, 278 missing members, 44 other diagnostics, and 364 total to 42 missing types, 278 missing members, 43 other diagnostics, and 363 total. The gate reports no breaking changes or stale suppressions.

The complete LibreWinForms source-first shadow gate passes default canonical build at 0 warnings/0 errors, source-built ProGPU canonical build at the established 613 warnings/0 errors baseline, typed platform tests at 22/22, ProGPU adapter tests at 10/10, canonical lifecycle tests at 24/24, and the frozen portable comparison build at 30 warnings/0 errors. NuGet support remains the normal development mode; this plan continues to use the source submodule only as its coordinated development graph.

## Remaining pen work

This ownership slice does not pretend that storage equals rendering. Compound arrays, custom caps, and pen transforms remain explicit ApiCompat debt until their state can be carried through typed ProGPU stroke contracts and verified with geometry/pixel and performance gates. In particular, `Pen.Transform` changes the pen tip rather than translating the stroke centerline, so it must not be approximated with a graphics transform.
