# Original canvas and paint production port

Implementation only; no build, test, shader, browser, image or performance validation was run in this code-first increment. No generated artifacts were created.

`paint::{Paint, PaintSnapshot, PaintMaskFilter}` retains original defaults, byte colors, alpha/filter order, hairlines/local pens/dashes, blend ownership, fast bounds, gamma/clip tables and all mask-filter expressions. `canvas::{Canvas, CanvasPicture, CanvasMatrix, SaveLayer, SurfaceCanvas}` records independent matrix/clip/save state and immutable draws. Matrix concatenation/inversion/perspective bounds retain original arithmetic; independent component/table lanes use safe SIMD.

Connected execution includes analytic/path/region and inverse fills, original non-dash path effects, actual text/hinted/native paragraph generations, mesh/Coons patches, source image inventories and normalized texture subsets/mips, image color transfer, pictures, fixed per-draw vector blends, and canonical image destination blends. Clips use original full geometry and finite canvas-difference paths; conservative clip query state remains separate. Surface leases exclusively borrow the actual target and explicit context; flush/snapshot retain immutable source generations and existing queue-completion owners. Drop never submits or polls.

GPU preparation captures actual layer/source recordings, prior target plus prior commands, full transformed bounds, original image-then-color filter order, paint alpha, source clips and output clips. Mask filters use the existing canonical filter graphs; complex shader fills/strokes retain separate real coverage sources and original shader graphs. Geometry/source producers run during preparation, never encode. Pure `to_scene` supports commands without captures. Whole retained source-device visitation is shared by `ImageFilter` and `ImageFilteredImageDraw` before capture creation.

Remaining explicit execution gates: RGBA16 save-layer targets; perspective rendering despite complete stored matrix state/bounds; destination-reading vector/picture blend scopes; original runtime source execution gates; rational/unsolved combined tight-bound producers; source-UV complex shader/mask-filter mesh lowering; and flushing while an isolated layer remains open. Original legacy SKFont/SKTextBlob/DrawString, drawable callbacks, remaining convenience drawing overloads and full Paint.GetFillPath facade are separate adapters over the existing text/vector engines, not invented substitutes. No provider/package/source-pixel or performance qualification follows from this increment.

Original source SHA-256:

| Source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKCanvas.cs` | `08b8d871bccfefdda7021e533c41641eaf22299932d1c32f8e7d9b49a2741e64` |
| `src/SkiaSharp/SKPaint.cs` | `684b87af3f62633200e96265997d40250ddc98d0cf6ddcbb008d8746de6d41ff` |
| `src/SkiaSharp/SKMaskFilter.cs` | `b85daf2f73b77bd826d1d7eaea84ed07cc422f9d324868cd7a805f8f58c46f40` |
| `src/SkiaSharp/SKMatrix.cs` | `a1a0fb70cb138510b7407bf41de5b3d88fb6e3f34bde01dd948d1128268b54e3` |
| `src/SkiaSharp/SKCanvasSaveLayerRec.cs` | `d69fb02174e67ad631c491d245e108d2e9b3bd7967b2e1cacb7fa0610d4e6d96` |
| `src/SkiaSharp/SKPath.cs` | `98f474ac8801e7d7b5ceed338191b979753babe0bb5b0aa826a8d1553f7cd660` |
