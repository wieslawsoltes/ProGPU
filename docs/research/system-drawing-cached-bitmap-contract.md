# System.Drawing cached-bitmap contract

## Source contract

The public shape and validation order are source-first adaptations of the .NET 10 WinForms managed `System.Drawing.Imaging.CachedBitmap` and `Graphics.DrawCachedBitmap` sources. The public surface is one sealed disposable type with `CachedBitmap(Bitmap, Graphics)` and one `Graphics.DrawCachedBitmap(CachedBitmap, int, int)` member. Null constructor and draw inputs fail before device work. A disposed cache is invalid, and drawing supports translation but rejects scale, rotation, shear, and other non-translation public transforms.

The upstream implementation creates and draws a private GDI+ device-dependent handle. That native handle is not portable and is not copied into ProGPU.

## Portable typed implementation

Construction snapshots the source bitmap, resolves the target `WgpuContext` from the typed `Graphics` recorder, and materializes the snapshot in that exact device domain. The cache never probes private state, uses reflection, exposes a fake GDI+ handle, or mutates the caller bitmap.

Drawing validates the target device identity and transfers a normal typed bitmap texture lease into the retained `DrawingContext`. Repeated draws in one retained command stream share one texture lease. Cache or source disposal after recording cannot invalidate deferred commands because the drawing context owns its own lease. A different WebGPU device fails explicitly instead of silently copying across devices.

Only the public/page/container transform is restricted to translation. The host-provided base transform remains outside that check because it maps logical framework coordinates into the selected device and is not caller `Graphics` state.

## Quality and performance gates

Focused tests cover nulls, immutable source ownership, source disposal, translation, scale/rotation rejection before recording, disposed-cache behavior, deferred lifetime, exact pixels, one retained texture across repeated draws, and a 128-byte-per-record warmed allocation ceiling. `CachedBitmapBenchmarks` compares warmed ordinary and cached 64×64 retained recording. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a 225.040 ns median for ordinary bitmap recording and a 169.996 ns median for cached recording, with 96 B allocated by each record/release cycle. One launch and three measured iterations make this coarse local evidence; it does not replace full renderer profiling.
