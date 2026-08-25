# System.Drawing retained primitive contract

## Scope

This checkpoint completes the normal managed `Graphics` overload families for retained arcs, Bézier curves, cardinal curves, closed curves, pies, rectangles, rounded rectangles, and polygon fills. It covers `Point`/`PointF`, `Rectangle`/`RectangleF`, coordinate, array, and .NET 10 `ReadOnlySpan<T>` shapes present in the pinned `Microsoft.WindowsDesktop.App.Ref` 10.0.11 contract.

Metafiles, cached bitmaps, screen copies, HDC/HWND entry points, and local-OS handles are deliberately outside this slice. They remain explicit platform-adapter debt and are not represented by fake handles.

## Contract authority

The public surface is checked directly against the pinned official `System.Drawing.Common.dll` reference assembly by `eng/progpu-verify-system-drawing-api.sh`. The exact suppression diff is the signature inventory: this slice removes 56 `CP0002` entries and adds none.

Normal managed behavior is defined by the public geometry contract:

- arcs and pies use the declared ellipse bounds, start angle, and sweep angle;
- Bézier point counts and cardinal-curve ranges are validated before recording;
- closed-curve and polygon overloads preserve `FillMode.Alternate` versus `FillMode.Winding`;
- span overloads consume caller storage synchronously and do not retain or fabricate array-shaped compatibility objects; and
- float rectangle and rounded-rectangle overloads retain their fractional geometry.

## Typed architecture

Every curved primitive is lowered to the existing `System.Drawing.Drawing2D.GraphicsPath` implementation. That implementation owns typed `ProGPU.Vector.PathGeometry`, `PathFigure`, line, cubic-Bézier, and arc segments. `Graphics` records those objects through `ProGPU.Scene.DrawingContext.DrawPath`, retaining the same renderer-neutral geometry used by clipping, hit testing, widening, GPU rendering, and native scene lowering.

Rectangle overloads continue to use the analytic retained rectangle command when the transform permits it. Curves and fills do not add a parallel rasterizer, runtime reflection, private-field probes, native GDI+ handles, or a second compatibility object model.

The span curve path now calls an internal span-aware range overload. `PointF` range recording therefore avoids a temporary point array; integer points require one typed conversion to float geometry. Array overloads route to their span counterparts so validation and recording behavior have one implementation.

## Quality and performance gates

`GraphicsPrimitiveQualityTests` verifies:

- typed retained commands for all primitive groups;
- nonzero/even-odd fill-rule transport;
- fractional and span rectangle recording;
- invalid geometry rejection before any command is retained;
- production pixel output for a filled pie; and
- a 1,024-byte upper bound per warmed four-point span curve recording.

`GraphicsPrimitiveBenchmarks.RecordCurveSpan` measures the same typed four-point curve record/reset workload with BenchmarkDotNet. The 2026-08-22 ARM64/.NET 10.0.11 ShortRun measured a 209.644 ns median (207.170 ns mean, 17.922 ns standard deviation) and 792 B/op. It used one launch, three warmups, and three measured iterations, and process-priority elevation was denied. This is coarse managed command-recording evidence, not an end-to-end renderer throughput claim. Renderer, image-parity, native backend, and LibreWinForms source-first lanes remain the integration authority.

## Remaining work

The next `Graphics` groups should remain complete subsystems: image parallelogram/abort overloads, coordinate/container state, text span overloads and advanced layout semantics, then explicit screen/native-handle adapters. Metafile enumeration belongs with the complete metafile model rather than this managed primitive slice.
