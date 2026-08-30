# ProGPU.CAD retained raster output research

Date: 2026-08-30

## Scope and clean-room provenance

This design adds bounded PDF and PNG stream output for an already compiled
`CadPrintJob`. It does not introduce a CAD renderer, text shaper, display-list
format, or GPU/native ABI. The implementation was written from the public
contracts below and from these original ProGPU-owned sources in this repository:

- `src/ProGPU.CAD/CadPrintPlan.cs` for physical media, output-pixel, clip,
  lineweight, retained-picture, and ownership contracts;
- `src/ProGPU.CAD/CadPrintJob.cs` for bounded source pages, O(1) collated or
  uncollated output mapping, and independent retained-picture leases;
- `src/ProGPU.CAD.Sample/CadSampleCanvas.cs` for the plotting snapshot's white
  background and adaptive ACI 7 policy;
- `src/SkiaSharp/SKDocument.cs`, `SKCanvas.cs`, `SKPicture.cs`, and `SKImage.cs`
  for ProGPU's existing CPU raster document, retained-picture replay, and PNG
  encoding contracts.

The PDF 1.4 catalog/page/image/content/xref framing and RGB Flate embedding in
`CadPrintOutputWriter.WritePdfDocument` are an original-ProGPU cross-project
refactor/port specifically from the in-repository `SKDocument.WritePdf` and
`CompressRgb` implementation. CAD changes the ownership and execution structure
to stream one exact already-rasterized page at a time, adds independent output
budgets/cancellation, and preserves per-page physical metadata. The matched
PDF-image/PNG RGB differential test verifies the shared raster contract. No
third-party implementation source was consulted or used for that code.

The new implementation does not use source text, helper structure, naming, or
lookup data from a third-party engine. `SkiaSharp` grants only a strong-named
internal friend seam to `ProGPU.CAD`; the official-compatible public surface is
unchanged.

## Primary sources consulted

- Skia's [PDF backend guide](https://skia.org/docs/user/sample/pdf/),
  [`SkDocument` contract](https://api.skia.org/classSkDocument.html),
  [canvas creation guide](https://skia.org/docs/user/api/skcanvas_creation/), and
  [`SkSurface::readPixels`](https://api.skia.org/classSkSurface.html) establish a
  page-oriented document API, explicit page sizes, reusable recorded pictures,
  and caller-owned raster readback. Skia also documents that some PDF features
  require raster expansion. ProGPU adapts that explicitly as an all-raster PDF
  adapter rather than implying vector text or path retention.
- Skia's upstream
  [SkParagraph overview](https://skia.googlesource.com/skia/+/main/modules/skparagraph/README.md)
  keeps paragraph shaping/layout upstream of painting. ProGPU likewise consumes
  the glyph IDs and positions already retained by the CAD snapshot and never
  reshapes during export.
- Direct2D's
  [`ID2D1PrintControl::AddPage`](https://learn.microsoft.com/windows/win32/api/d2d1_1/nf-d2d1_1-id2d1printcontrol-addpage)
  converts one retained command list plus a physical page size into a fixed page.
  DirectWrite's
  [glyph-run model](https://learn.microsoft.com/windows/win32/directwrite/glyphs-and-glyph-runs)
  and [Direct2D/DirectWrite integration](https://learn.microsoft.com/windows/win32/direct2d/direct2d-and-directwrite)
  preserve positioned glyph runs and allow their layout to be reused across draw
  calls. ProGPU adopts the command-list/page separation and keeps retained shaped
  glyph runs intact until raster replay.
- Win2D's
  [`CanvasRenderTarget`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasRenderTarget.htm),
  [`CanvasImage.SaveAsync`](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_CanvasImage_SaveAsync_1.htm),
  and [DPI guidance](https://microsoft.github.io/Win2D/WinUI3/html/DPI.htm)
  separate an offscreen target, an explicit output DPI, and stream encoding.
  ProGPU adopts explicit DPI and stream ownership, while staging synchronously
  because its current CPU raster canvas is synchronous and browser-safe.
- Mozilla's
  [rendering architecture](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst)
  keeps a reusable display list/scene, culls it into frames, and prepares GPU
  caches only at render time. Its compositor
  [asynchronous readback contract](https://searchfox.org/mozilla-central/source/gfx/layers/Compositor.h)
  avoids blocking a GPU compositor. ProGPU retains the scene and page picture,
  but deliberately uses the existing CPU output path; a future GPU adapter must
  use asynchronous readback rather than make this synchronous API block a GPU.
- Vello's [renderer contract](https://github.com/linebender/vello) records a scene
  once and renders it into an explicitly sized `wgpu` texture. ProGPU adopts the
  scene/target separation but rejects a Vello-specific scene translation or GPU
  dependency for this portable first adapter.
- Parley's [layout concept](https://github.com/linebender/parley/blob/main/doc/concept.md)
  produces final glyph IDs and positions before painting. HarfBuzz's
  [shaping contract](https://harfbuzz.github.io/shaping-and-shape-plans.html) and
  [cluster guidance](https://harfbuzz.github.io/clusters.html) likewise make
  positioned glyphs and character-to-glyph clusters reusable output, including
  for PDF extraction. ProGPU preserves its existing positioned glyph run;
  raster PDF intentionally does not claim selectable/extractable PDF text.

## Comparison and decisions

| Concern | Production/research pattern | ProGPU decision |
|---|---|---|
| Startup and lazy initialization | Skia/Win2D create output targets on demand; WebRender/Vello retain scenes independently of a target | Construct the CPU bitmap/document only when export is requested; no WebGPU device or pipeline initialization |
| Shaping and layout reuse | SkParagraph, DirectWrite, Parley, and HarfBuzz shape before paint and retain glyph positions | Replay the existing CAD `GpuPicture`; never shape, enumerate fonts, or rebuild text in the writer |
| Display-list reuse | SkPicture, D2D command lists, WebRender scenes, and Vello scenes are replayable | Clone one immutable page lease per output occurrence; copies share job source storage and do not recompile CAD entities |
| Visibility and clipping | WebRender culls a scene into a frame; print systems pass a page command list | Reuse the print plan's exact printable-area clip and page transform; do not repeat CAD spatial queries |
| Resource/cache keys and eviction | Renderers keep device/domain-specific image and glyph caches | Reuse the picture's existing retained resources for one replay. The CPU adapter owns no persistent device cache and therefore has no device-loss generation |
| Demand upload and workers | WebRender/Vello upload for visible frames and can prepare asynchronously | Reject GPU upload for this adapter. CPU replay is synchronous; future GPU output is a separate asynchronous adapter |
| GPU batching | WebRender/Vello batch a retained scene into a GPU target | Not applicable to the CPU adapter. The same page picture remains native-compilable, verified separately, but no native ABI or renderer changes are made |
| DPI/subpixel/hinting | Win2D requires explicit offscreen DPI; Skia documents 72 PDF points per inch | Preserve each compiled page's explicit DPI and integer raster extent, then map it to exact physical PDF points. PNG uses that same page DPI. Mixed-DPI PDF pages remain independent because fixed physical strokes are already baked in device pixels |
| Fallback fonts and variable fonts | SkParagraph/DirectWrite/Parley choose fonts and shape before painting | Preserve the snapshot's resolved font, glyph IDs, variation state, and positions. Export has no fallback discovery |
| Device loss and atlas generation | WebRender/Vello regenerate device resources after loss | CPU output has no GPU device. Existing managed/native page compilation remains unchanged and independently tested |

Adopted:

- page-at-a-time replay from one immutable retained command stream;
- explicit physical media and DPI, with output dimensions preflighted before
  consuming a picture;
- caller-owned streams and deterministic page order;
- an opaque white paper background, using the existing white-background plotting
  snapshot so adaptive ACI 7 becomes black while explicit true white remains
  white;
- encoded-byte, per-page pixel, total-pixel, and dimension budgets;
- complete bounded staging before destination commit, so validation, rendering,
  encoding, and pre-commit cancellation do not alter the destination.

Adapted:

- PDF is intentionally raster-only. It maps every retained source page to exact
  physical points and rasterizes it at the DPI used to compile that page.
  Different source DPIs may coexist in one PDF without rescaling fixed
  device-space lineweights after compilation.
- PNG emits one explicitly selected output occurrence. Multipage order belongs to
  PDF and the existing `CadPrintJob`; no nonstandard multipage PNG is invented.
- The shared desktop/browser shell uses its existing `StorageFile` picker and
  byte-write seam. Platform-specific path or JavaScript APIs do not enter CAD.

Rejected for this slice:

- copying or wrapping a third-party PDF implementation;
- serializing a `GpuPicture` merely to cross the ProGPU assembly boundary;
- re-running the CAD snapshot, text shaper, spatial query, or print compiler in
  the output writer;
- synchronous GPU readback, per-command native crossings, or a desktop-only WIC,
  CoreGraphics, or printer API;
- claiming vector PDF, searchable text, ICC output profiles, CTB/STB application,
  printer submission, or matched GPU/native pixels.

## Complexity, ownership, and validation contract

Preflight is `O(P)` time and storage for `P` output pages. Replay and encoding are
`O(C + X)` for retained commands `C` and raster pixels `X`. Raster storage is
bounded by per-page and total pixel limits; encoded staging is independently
bounded. PDF copy/collation mapping remains the print job's O(1) arithmetic and
does not allocate an additional source/copy map.

The job retains its pages throughout output. Each replay gets an independent
picture lease, transferred to the internal Skia picture wrapper and released
after the page. The writer never disposes the job or source plans. Rendering and
encoding happen entirely in staging. Cancellation is observed during preflight,
between pages, after rendering, and immediately before commit. A destination
write failure may leave that caller-owned stream partially written; atomic file
replacement remains the responsibility of the platform storage adapter.

Tests cover requested DPI and decoded PNG dimensions/pixels, mixed-media PDF
page order and physical boxes, copies, byte/page/total/dimension guards,
pre-commit cancellation and destination preservation, disposed ownership,
post-output managed and native-ready page reuse, DXF/DWG round trips, and shared
desktop/browser control availability. SVG/vector PDF, GPU/native pixel matching,
browser performance smoke, printer spooling, and Instruments measurements remain
explicit future gates.

## Release baseline

The opt-in benchmark lane is reproducible with:

```text
dotnet run --project src/ProGPU.CAD.Benchmarks/ProGPU.CAD.Benchmarks.csproj \
  -c Release --no-build -- --entities 1000 --raster-output \
  --raster-output-dpi 96 --warmup 5 --iterations 24 --queries 100
```

On 2026-08-30, macOS 26.6 / .NET 10.0.5 produced the following first-feature
baseline for one A4 page: raster PDF p50/p95/p99
`29.3000/116.8343/134.6967 ms`, mean `41.3089 ms`, and
`7,169,511 B/op`; PNG p50/p95/p99 `106.0207/177.7532/252.2247 ms`, mean
`122.0418 ms`, and `10,944,880 B/op`. Process working set after the complete
benchmark was `93,519,872` bytes. This is a reproducible capacity baseline,
not a before/after performance-improvement claim. It measures caller-owned
encoded output allocation, including the bounded raster and staging buffers.
No GPU work occurred, so Metal System Trace and device-residency measurements
are not applicable to this CPU adapter. A future optimization claim requires
matched final binaries and the repository's Instruments/EventPipe evidence.
