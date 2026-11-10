# Surface flushing with active layer recordings

Implementation only; no compilation, tests, browser, benchmark or new fixtures.

The original SKSurface flush operates on its root drawing context even while SKCanvas is recording into an independent SaveLayer context. SurfaceCanvas now follows that ownership boundary: it snapshots and submits the root commands held by the outermost active layer, leaving every child context, saved matrix/clip state, actual paint/filter owner and frozen previous-context recipe intact. Snapshot while a layer is open returns the actual surface generation; it does not implicitly restore or paint the active child.

Whole-root preparation completes before transfer to RenderSurface. The accepted root prefix is retired once, before the asynchronous surface flush, so a failed submission cannot append the same source commands again on retry. Clip continuation storage is reserved before transfer; the existing Rust surface live-clip continuation policy remains unchanged. Active child clip indices and nested parent commands are not rebuilt or cleared. Inherited root clip indices alone move with their actual replacement prefix.

This removes the prior blanket open-layer execution rejection. It does not change explicit finish's restore-all behavior, RenderSurface submission/readback ownership, or queue-only abandoned-owner teardown. Exact original source clip-reset behavior across repeated flushes, prior/backdrop replay, source pixels and all native/browser/performance/package controls still require their original qualification; no claim of those controls follows from this implementation.

| Original source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKSurface.cs` | `c52ae704abdd8ffbd044386efcb439d4c67adb4b3b08a895d6ab4018f709a9b6` |
| `src/SkiaSharp/SKCanvas.cs` | `08b8d871bccfefdda7021e533c41641eaf22299932d1c32f8e7d9b49a2741e64` |
