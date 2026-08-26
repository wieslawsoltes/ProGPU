# System.Drawing Brush Base Contract

Date: 2026-08-27

## Scope and sources

This slice restores the official [`Brush`](https://learn.microsoft.com/dotnet/api/system.drawing.brush?view=windowsdesktop-10.0) inheritance, [`Brush.Clone`](https://learn.microsoft.com/dotnet/api/system.drawing.brush.clone?view=windowsdesktop-10.0), and [`Brush.Dispose`](https://learn.microsoft.com/dotnet/api/system.drawing.brush.dispose?view=windowsdesktop-10.0) contracts from the pinned .NET 10.0.11 reference assembly. It also restores the protected-internal [`SetNativeBrush`](https://learn.microsoft.com/dotnet/api/system.drawing.brush.setnativebrush?view=windowsdesktop-10.0) shape while making its Windows-native boundary explicit.

`Brush` now derives from `MarshalByRefObject`, implements `ICloneable` and `IDisposable`, requires public derived classes to implement `Clone`, and exposes the official protected virtual disposal hook. Concrete ProGPU brushes provide independent clones and release owned managed state through that hook.

The public shapes and ownership semantics are checked against the reference assembly and Microsoft documentation. The implementation is original ProGPU code.

## Typed implementation

The former public abstract `ToProGpuBrush` member leaked a ProGPU-specific type into the official `System.Drawing` surface and forced third-party brush subclasses to implement a renderer method that does not exist in WinForms. It is now an internal virtual renderer seam. Built-in solid, hatch, texture, and linear-gradient brushes override it; an external brush can derive using only the official contract, and drawing it fails at a clear typed-adapter boundary until a renderer adapter is supplied.

`SolidBrush` now owns explicit disposal state, clones its color independently, and rejects property, clone, and renderer use after disposal. Existing hatch, texture, and gradient ownership paths now override the common protected disposal hook. `SetNativeBrush` does not retain an untyped pointer on the portable path; it throws `PlatformNotSupportedException` naming the required explicit Windows drawing adapter. No GDI+ call, native-handle cache, runtime reflection, private-field scan, or fake compatibility object is introduced.

## Gates and evidence

Four focused tests cover independent solid-brush cloning, idempotent public disposal and post-disposal rejection, official third-party inheritance/clone/dispose hooks, the unsupported typed-renderer boundary, and native-handle rejection. The complete drawing suite passes 219/219, and the drawing benchmark project builds with 0 warnings and 0 errors after the renderer seam becomes internal.

ApiCompat removes three missing-member suppressions and three other-shape suppressions, reducing measured debt from 42 missing types, 281 missing members, 47 other diagnostics, and 370 total to 42 missing types, 278 missing members, 44 other diagnostics, and 364 total. The gate reports no breaking changes or stale suppressions. LibreWinForms downstream validation rebuilds the ProGPU adapter with 0 warnings and 0 errors, passes 10/10 backend tests, rebuilds canonical `System.Windows.Forms` with 613 known compatibility warnings and 0 errors, and passes 24/24 lifecycle tests.

The broad headless build reached the changed drawing assembly, then stopped in the unrelated Fluent theme project because the local `external/microsoft-ui-xaml/.../generic.xaml` checkout is absent. The focused drawing suite and benchmark build are the isolated compile evidence for this seam; hosted CI owns the fully populated repository graph.
