# Release sample-page memory profile

## Outcome

The complete desktop gallery was measured page-by-page in fresh Release processes before
and after the managed-memory work. Across all 47 pages, average retained managed memory
fell from 80.51 MiB to 30.00 MiB (**62.7% lower**), average managed allocation fell from
154.10 KiB/frame to 43.29 KiB/frame (**71.9% lower**), and average compositor compilation
fell from 1.223 ms to 0.362 ms (**70.4% lower**). The sum of the 47 isolated retained-heap
measurements fell from 3.70 GiB to 1.38 GiB. This sum is useful for comparing the complete
gallery but is not the memory of one process because every page was deliberately isolated.

The worst retained managed heap changed from Text Shaping Lab at 513.91 MiB to Text &
Documents at 177.46 MiB. Average macOS physical footprint, which also includes the runtime,
native WebGPU/Metal objects, driver caches, and mapped resources, fell from 403.90 MiB to
370.82 MiB (**8.2% lower**). Compile frames over the 16.67 ms budget fell from 428 to 3;
426 of the baseline frames were from Text & Documents.

| Representative page | Managed before | Managed after | Change | Allocation/frame before | Allocation/frame after | Compile before | Compile after |
|---|---:|---:|---:|---:|---:|---:|---:|
| Text Shaping Lab | 513.91 MiB | 70.04 MiB | **-86.4%** | 22.8 KiB | 16.9 KiB | 0.082 ms | 0.052 ms |
| Typography & Scripts | 345.18 MiB | 82.17 MiB | **-76.2%** | 10.2 KiB | 8.4 KiB | 0.036 ms | 0.055 ms |
| Text & Documents | 472.28 MiB | 177.46 MiB | **-62.4%** | 4,128.4 KiB | 197.8 KiB | 41.728 ms | 2.738 ms |
| Font Glyph Browser | 60.85 MiB | 31.22 MiB | **-48.7%** | 104.9 KiB | 53.0 KiB | 2.491 ms | 0.899 ms |

## Measurement method

`tools/profile-sample-memory.sh` discovers the gallery pages, builds the desktop application
and analyzer in Release, and launches a new traced child process for each page. Each process
runs 180 scrolling warm-up frames, performs a blocking compacting full collection, measures
600 scrolling frames, performs another compacting collection, writes its metrics, and exits.
This avoids navigation order, previously loaded fonts, and retained driver resources
contaminating another page's result.

The benchmark and analyzer record:

- exact process-wide managed allocation from `GC.GetTotalAllocatedBytes`, retained managed
  bytes, GC heap/committed/fragmented bytes, generations, collection counts, pause duration,
  pinned objects, and finalization state;
- working set and virtual memory plus macOS `proc_pid_rusage` resident, wired, current physical
  footprint, and lifetime maximum physical footprint;
- explicit glyph, color-glyph, and path texture residency, staging capacity, outline buffers,
  atlas entries/generations/evictions, upload writes, raster batches, and compute passes;
- compositor, upload, render, host-update, layout, animation, acquire, present, frame-budget,
  and scene-cache measurements;
- EventPipe CPU samples and randomized allocation samples separated into startup, warm-up,
  and measurement phases by `ProGPU-SampleBenchmark` EventSource markers.

Randomized allocation events are converted to probability-weighted estimates using the .NET
runtime's documented `size / (1 - q^size)` estimator with `p = 1 / 102400` and `q = 1 - p`.
The exact allocation/frame counter is the comparison metric; sampled types and stacks are
used for attribution because a small number of samples has wide statistical error. The
profiler retains JSON and logs for every page and can optionally retain `.nettrace` files.

Run it with:

```bash
PROGPU_MEMORY_KEEP_TRACES=0 tools/profile-sample-memory.sh artifacts/sample-memory-profile
```

Use `PROGPU_MEMORY_PAGE_FILTER` for a regular-expression page subset and
`PROGPU_MEMORY_WARMUP_FRAMES` / `PROGPU_MEMORY_MEASURE_FRAMES` to change the workload.

## Attribution and implementation

The baseline GC dumps for the three largest pages showed that `OpenFontSharp` whole-font
CFF and OpenType layout object graphs retained glyph objects and decoded arrays for fonts
whose pages had requested only a small set of glyphs. Allocation stacks on Text & Documents
also showed a second shaping/layout pass during rendering and per-realization rich-character
objects. CPU samples attributed its severe frame stalls to quadratic free-rectangle pruning
during a path-atlas recovery containing more than one thousand live entries.

The implementation therefore:

1. Reads GSUB/GPOS feature records and uses the existing typed raw-table shaping pipeline,
   removing the eager whole-font layout typeface.
2. Adds an original, clean-room CFF 1 INDEX/DICT and Type 2 evaluator. It retains compact
   offsets and evaluates only a requested glyph, including local/global subroutines and CID
   FDSelect/FDArray data. Deprecated `seac` and unsupported malformed programs fall back to
   the existing parser lazily, so compatibility is preserved without paying its whole-font
   cost on normal fonts.
3. Reuses the already-shaped cluster advances for rich-text decoration widths instead of
   constructing another `TextLayout`; caches immutable common shaping options; and recycles
   positioned rich characters through a presenter-local pool capped at 4,096 objects.
4. Keeps MaxRects recovery deterministic but compacts unaffected free regions linearly and
   compares only newly split regions with the pruned set. Worst-case complexity remains
   `O(F^2)`, while the measured bounded-intersection case is `O(F)` and uses one pooled
   temporary array.
5. Preserves the earlier page-switch solution: newly visible glyphs are demand-driven and
   coalesced into bounded outline/uniform writes, one compute pass, and one submission per
   batch. Coverage atlases survive navigation and grow geometrically on demand, avoiding
   both hidden-page preloading and repeated uploads.

## Cross-engine design record

This is a clean-room implementation. No external implementation source was copied, ported,
translated, or structurally reproduced. Primary sources were used to select behavior and
architecture; the implementation follows ProGPU's own typed retained-scene, invalidation,
atlas-generation, and GPU ownership contracts.

| Source | Concept considered | ProGPU decision |
|---|---|---|
| [Skia strike cache](https://skia.googlesource.com/skia/+/main/src/core/SkStrikeCache.h), [SkParagraph](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h), [GrDrawOpAtlas](https://skia.googlesource.com/skia/+/main/src/gpu/ganesh/GrDrawOpAtlas.h) | Bounded caches, retained layout, lazy atlas residency and eviction | Adopt bounded residency and retained CPU layout; adapt it to analytic R8 WebGPU coverage and ProGPU generation-based invalidation. |
| [DirectWrite glyph-run analysis](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwriteglyphrunanalysis), [Direct2D performance guidance](https://learn.microsoft.com/en-us/windows/win32/direct2d/improving-direct2d-performance), [Win2D CanvasTextLayout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm) | Reusable glyph runs/layout and alpha coverage | Keep shaping/layout as reusable CPU results and upload compact coverage only when visible. |
| [WebRender overview](https://searchfox.org/firefox-main/source/gfx/docs/RenderingOverview.rst), [blob images](https://searchfox.org/firefox-main/source/gfx/wr/webrender/doc/blob.md) | Visible-resource preparation and delayed texture-cache upload | Batch only current-scene misses before drawing; retain valid atlas entries across page switches. |
| [Vello glyph design](https://github.com/linebender/vello/issues/204), [Parley](https://docs.rs/parley/latest/parley/) | GPU vector work paired with reusable CPU shaping/layout | Preserve CPU Unicode/OpenType shaping and GPU raster/composition; reject incomplete GPU-only shaping. |
| [HarfBuzz shaping-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html) | Cache stable face/script/language/feature decisions | Reuse immutable common shaping options and raw table plans; avoid rebuilding parsed font graphs. |
| [FreeType glyph retrieval](https://freetype.org/freetype2/docs/reference/ft2-glyph_retrieval.html) | Load one requested glyph into a replaceable slot | Adopt the observable demand-loading behavior, implemented independently from the [CFF table specification](https://learn.microsoft.com/en-us/typography/opentype/spec/cff) and [Adobe Type 2 Charstring specification](https://adobe-type-tools.github.io/font-tech-notes/pdfs/5177.Type2.pdf). |
| [.NET randomized allocation sampling](https://github.com/dotnet/runtime/blob/main/docs/design/features/RandomizedAllocationSampling.md), [dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace), [`GC.GetGCMemoryInfo`](https://learn.microsoft.com/en-us/dotnet/api/system.gc.getgcmemoryinfo?view=net-10.0) | Phase markers, randomized allocation attribution, and GC/process counters | Combine exact counters with sampled types/stacks; never treat a low-sample allocation estimate as an exact total. |

Rejected alternatives were eager parsing of every glyph and layout table, prewarming every
hidden sample page, an unbounded exact-position atlas key, lowering raster quality, or moving
Unicode/OpenType shaping to an incomplete GPU implementation. Those options either increase
retained memory, recreate page-switch upload spikes, break the bounded cache contract, or
reduce correctness and quality.

## Complete 47-page comparison

All values below are from the same 180-frame warm-up and 600-frame measured Release workload.
Allocation is exact process-wide managed allocation per measured frame. Small differences
below roughly 0.05 ms are generally timer/driver noise; the frame-budget count and full-suite
average are the regression gates.

| Page | Managed before | Managed after | Change | Alloc KiB/frame before | Alloc KiB/frame after | Compile ms before | Compile ms after |
|---|---:|---:|---:|---:|---:|---:|---:|
| 3D Mesh Viewer | 52.30 | 22.50 | -57.0% | 19.1 | 13.5 | 0.056 | 0.041 |
| Advanced Controls | 50.61 | 20.79 | -58.9% | 12.1 | 7.3 | 0.042 | 0.025 |
| Basic Input | 50.03 | 20.23 | -59.6% | 10.9 | 6.9 | 0.038 | 0.020 |
| Color Picker | 50.50 | 20.55 | -59.3% | 7.8 | 7.0 | 0.010 | 0.013 |
| Compositor API | 50.85 | 20.84 | -59.0% | 14.6 | 9.1 | 0.744 | 0.694 |
| Compute FX | 50.88 | 20.84 | -59.1% | 41.4 | 35.8 | 0.019 | 0.026 |
| Data Virtualization | 54.10 | 24.06 | -55.5% | 37.8 | 32.0 | 1.146 | 0.919 |
| Dock Panel | 49.39 | 19.50 | -60.5% | 6.8 | 6.5 | 0.008 | 0.023 |
| Drawing Context | 49.45 | 19.38 | -60.8% | 11.0 | 5.3 | 0.038 | 0.027 |
| DXF CAD Viewer | 51.97 | 21.00 | -59.6% | 16.3 | 10.8 | 0.028 | 0.027 |
| File Storage | 49.61 | 19.75 | -60.2% | 11.9 | 6.5 | 0.042 | 0.032 |
| Font Glyph Browser | 60.85 | 31.22 | -48.7% | 104.9 | 53.0 | 2.491 | 0.899 |
| Framework Effects | 51.05 | 18.78 | -63.2% | 10.2 | 9.8 | 0.351 | 0.324 |
| GDI Shim Showcase | 51.06 | 21.11 | -58.7% | 10.8 | 9.2 | 0.034 | 0.043 |
| Glyph Run Showcase | 50.40 | 20.60 | -59.1% | 10.3 | 6.8 | 0.299 | 0.439 |
| GPU Charting | 97.22 | 68.70 | -29.3% | 18.2 | 22.2 | 0.021 | 0.058 |
| Grid Splitter | 49.48 | 19.56 | -60.5% | 7.2 | 6.0 | 0.012 | 0.030 |
| Image & Buttons | 50.87 | 21.02 | -58.7% | 40.8 | 33.6 | 0.023 | 0.024 |
| Image Effects | 50.70 | 20.67 | -59.2% | 37.6 | 35.8 | 0.013 | 0.016 |
| Inter Typeface | 99.93 | 53.02 | -46.9% | 36.3 | 35.1 | 0.500 | 0.898 |
| Interactive Input | 60.09 | 21.79 | -63.7% | 7.9 | 7.6 | 0.012 | 0.039 |
| Keyboard & Focus | 52.60 | 23.03 | -56.2% | 15.5 | 10.5 | 0.037 | 0.036 |
| Layout Panels | 50.51 | 20.03 | -60.4% | 12.4 | 8.1 | 0.026 | 0.026 |
| LOL/s Benchmark | 65.83 | 36.98 | -43.8% | 1,994.6 | 1,073.1 | 2.999 | 2.831 |
| Markdown Playground | 89.72 | 34.78 | -61.2% | 26.6 | 12.2 | 1.131 | 1.351 |
| Motion & Animations | 50.40 | 18.13 | -64.0% | 20.4 | 13.8 | 0.603 | 0.516 |
| MotionMark Showcase | 59.40 | 30.58 | -48.5% | 81.8 | 44.3 | 1.123 | 0.503 |
| Password Box | 50.42 | 20.63 | -59.1% | 8.1 | 6.1 | 0.016 | 0.021 |
| Path Operations | 49.96 | 19.99 | -60.0% | 8.1 | 6.5 | 0.021 | 0.043 |
| Picture Caching | 51.18 | 21.29 | -58.4% | 21.7 | 13.9 | 0.802 | 0.592 |
| Radio Button | 50.43 | 20.63 | -59.1% | 12.0 | 7.7 | 0.036 | 0.029 |
| Rating Control | 50.28 | 20.50 | -59.2% | 8.0 | 6.4 | 0.020 | 0.029 |
| Rich Document Editor | 50.32 | 20.38 | -59.5% | 12.8 | 7.7 | 0.032 | 0.023 |
| ShaderToy Playground | 51.48 | 21.47 | -58.3% | 148.0 | 32.6 | 0.707 | 0.780 |
| SkiaSharp Shim | 51.80 | 21.77 | -58.0% | 5.9 | 5.4 | 0.009 | 0.028 |
| SplitView Layout | 50.14 | 20.32 | -59.5% | 13.0 | 7.5 | 0.033 | 0.037 |
| Styles Showcase | 49.41 | 19.50 | -60.5% | 7.3 | 5.7 | 0.028 | 0.016 |
| Text & Documents | 472.28 | 177.46 | -62.4% | 4,128.4 | 197.8 | 41.728 | 2.738 |
| Text Shaping Lab | 513.91 | 70.04 | -86.4% | 22.8 | 16.9 | 0.082 | 0.052 |
| Theme Showcase | 52.77 | 22.96 | -56.5% | 15.1 | 16.8 | 0.043 | 0.041 |
| Touch & Gestures | 50.47 | 21.00 | -58.4% | 12.7 | 7.1 | 0.042 | 0.036 |
| Typography & Scripts | 345.18 | 82.17 | -76.2% | 10.2 | 8.4 | 0.036 | 0.055 |
| Vector Shapes | 50.67 | 20.73 | -59.1% | 7.5 | 6.2 | 0.013 | 0.018 |
| Virtualization Controls | 55.09 | 26.66 | -51.6% | 22.9 | 20.6 | 0.016 | 0.014 |
| Visual Designer | 81.26 | 51.92 | -36.1% | 129.5 | 124.0 | 1.906 | 2.537 |
| WPF Shim Showcase | 57.22 | 21.26 | -62.9% | 13.9 | 8.3 | 0.031 | 0.020 |
| Wrap Panel | 49.78 | 19.82 | -60.2% | 9.2 | 7.5 | 0.014 | 0.023 |

GPU Charting and Theme Showcase had small allocation increases in their isolated optimized
runs (about 4 KiB/frame and 1.7 KiB/frame respectively), while their retained managed heaps
still fell by 29.3% and 56.5%. Their absolute compile costs stayed below 0.06 ms. Visual
Designer rose by 0.63 ms but remained at 2.54 ms with no over-budget frames. These small
single-run movements are retained in the table rather than hidden by aggregate reporting.

## Verification

- `ProGPU.Tests` Release: **2,274 passed**, 0 failed.
- `ProGPU.Tests.Headless` Release: **196 passed**, 0 failed.
- Path-atlas focused tests: **29 passed**, including recovery, generation, and small-atlas
  stress coverage.
- CFF differential tests compare independently decoded `A`, `日`, `か`, and `ナ` geometry
  against the established fallback to 0.001 font units; on-demand tests confirm the fallback
  object graph is not created.
- All 47 pages completed the fresh-process Release desktop profile.
- Headless screenshots for Text Shaping Lab, Typography & Scripts, and Text & Documents were
  visually inspected with unchanged text coverage and quality.
- Browser Release publish AOT-compiled 69 assemblies; the WebGPU gallery opened under a real
  browser with zero console errors or warnings beyond expected system-font fallback messages.
- Quality-sensitive 8x8 analytic coverage, physical-DPI raster size, quarter-physical-pixel
  snapping, direction-aware half-open winding, color glyphs, fallback/variation identity,
  and same-frame atlas recovery were not reduced or bypassed.
