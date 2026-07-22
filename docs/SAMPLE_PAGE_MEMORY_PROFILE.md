# Release sample-page memory profile

## Outcome

The complete desktop gallery was measured page-by-page in fresh Release processes before
and after the managed-memory work. Across all 47 pages, average retained managed memory
fell from 80.51 MiB to 30.29 MiB (**62.4% lower**), average managed allocation fell from
154.10 KiB/frame to 12.04 KiB/frame (**92.2% lower**), and average compositor compilation
fell from 1.223 ms to 0.250 ms (**79.6% lower**). The sum of the 47 isolated retained-heap
measurements fell from 3.70 GiB to 1.39 GiB. This sum is useful for comparing the complete
gallery but is not the memory of one process because every page was deliberately isolated.

The worst retained managed heap changed from Text Shaping Lab at 513.91 MiB to Text &
Documents at 189.27 MiB. Average macOS physical footprint, which also includes the runtime,
native WebGPU/Metal objects, driver caches, and mapped resources, fell from 403.90 MiB to
369.69 MiB (**8.5% lower**). Compile frames over the 16.67 ms budget fell from 428 to zero;
426 of the baseline frames were from Text & Documents. Across the isolated runs, Gen0
collections fell from 503 to 33 and summed GC pause time fell from 868.3 ms to 34.3 ms.

Relative to the previous optimized checkpoint, this final pass reduced allocation by a
further **72.2%**, compilation by **31.0%**, physical footprint by **0.3%**, and summed GC
pause time by **89.5%**. Average retained managed memory moved by +0.29 MiB (+1.0%) between
the two fresh-process sweeps; this is reported rather than hidden, while the physical and
collection measurements improved.

| Representative page | Managed before | Managed after | Change | Allocation/frame before | Allocation/frame after | Compile before | Compile after |
|---|---:|---:|---:|---:|---:|---:|---:|
| Text Shaping Lab | 513.91 MiB | 70.07 MiB | **-86.4%** | 22.8 KiB | 0.9 KiB | 0.082 ms | 0.036 ms |
| Typography & Scripts | 345.18 MiB | 82.16 MiB | **-76.2%** | 10.2 KiB | 0.9 KiB | 0.036 ms | 0.017 ms |
| Text & Documents | 472.28 MiB | 189.27 MiB | **-59.9%** | 4,128.4 KiB | 189.3 KiB | 41.728 ms | 2.037 ms |
| Font Glyph Browser | 60.85 MiB | 31.26 MiB | **-48.6%** | 104.9 KiB | 37.4 KiB | 2.491 ms | 0.732 ms |
| Visual Designer | 81.26 MiB | 46.57 MiB | **-42.7%** | 129.5 KiB | 95.3 KiB | 1.906 ms | 1.872 ms |
| LOL/s Benchmark | 65.83 MiB | 36.80 MiB | **-44.1%** | 1,994.6 KiB | 28.9 KiB | 2.999 ms | 1.881 ms |

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
6. Keeps rich-text table, selection, and text ordering in one retained command list. A
   selection-only scratch list is created lazily on the first selection repaint, then only
   the previous overlay range is spliced; shaped text commands and their strings retain
   identity, while ordinary `RichTextBlock` instances pay no extra cache-object cost.
7. Reuses the registered inheritable-property set, replaces capturing visual-state trigger
   queries with indexed loops, and indexes rich-document block/child lists. These remove
   closure, interface-enumerator, and repeated property-list allocations from steady layout
   without bypassing inheritance, state evaluation, or invalidation.
8. Makes the LOL/s stress page retain a bounded 512-element pool whose controls keep one
   `Run` and one mutable brush. At the 500-element steady-state limit it rotates child order
   in place instead of detaching, reparenting, rebuilding inlines, and allocating brushes.
   The workload still updates 119,600+ labels per measured run at the same active limit.
9. Re-sorts the live path-recovery list in place for the same four deterministic orderings
   and reuses one placement/free-rectangle trial pair across the three heuristics. This
   removes a `PathInfo`-heavy array copy per ordering and repeated trial buffers while
   preserving exact fallback, same-frame retry, UV generation, and failure behavior.

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
| 3D Mesh Viewer | 52.30 | 22.51 | -57.0% | 19.1 | 1.1 | 0.056 | 0.017 |
| Advanced Controls | 50.61 | 20.82 | -58.9% | 12.1 | 0.9 | 0.042 | 0.013 |
| Basic Input | 50.03 | 20.25 | -59.5% | 10.9 | 0.9 | 0.038 | 0.016 |
| Color Picker | 50.50 | 20.55 | -59.3% | 7.8 | 0.9 | 0.010 | 0.011 |
| Compositor API | 50.85 | 21.15 | -58.4% | 14.6 | 2.8 | 0.744 | 0.378 |
| Compute FX | 50.88 | 20.86 | -59.0% | 41.4 | 29.7 | 0.019 | 0.008 |
| DXF CAD Viewer | 51.97 | 22.04 | -57.6% | 16.3 | 1.1 | 0.028 | 0.013 |
| Data Virtualization | 54.10 | 24.07 | -55.5% | 37.8 | 27.9 | 1.146 | 1.018 |
| Dock Panel | 49.39 | 19.47 | -60.6% | 6.8 | 0.9 | 0.008 | 0.010 |
| Drawing Context | 49.45 | 19.43 | -60.7% | 11.0 | 0.9 | 0.038 | 0.010 |
| File Storage | 49.61 | 19.69 | -60.3% | 11.9 | 0.9 | 0.042 | 0.011 |
| Font Glyph Browser | 60.85 | 31.26 | -48.6% | 104.9 | 37.4 | 2.491 | 0.732 |
| Framework Effects | 51.05 | 21.30 | -58.3% | 10.2 | 1.6 | 0.351 | 0.235 |
| GDI Shim Showcase | 51.06 | 21.10 | -58.7% | 10.8 | 1.2 | 0.034 | 0.017 |
| GPU Charting | 97.22 | 68.72 | -29.3% | 18.2 | 3.6 | 0.021 | 0.022 |
| Glyph Run Showcase | 50.40 | 20.44 | -59.5% | 10.3 | 1.9 | 0.299 | 0.309 |
| Grid Splitter | 49.48 | 19.51 | -60.6% | 7.2 | 0.9 | 0.012 | 0.010 |
| Image & Buttons | 50.87 | 21.04 | -58.6% | 40.8 | 29.7 | 0.023 | 0.007 |
| Image Effects | 50.70 | 20.69 | -59.2% | 37.6 | 29.8 | 0.013 | 0.009 |
| Inter Typeface | 99.93 | 53.09 | -46.9% | 36.3 | 2.9 | 0.500 | 0.548 |
| Interactive Input | 60.09 | 21.80 | -63.7% | 7.9 | 0.9 | 0.011 | 0.013 |
| Keyboard & Focus | 52.60 | 23.09 | -56.1% | 15.5 | 1.0 | 0.037 | 0.017 |
| LOL/s Benchmark | 65.83 | 36.80 | -44.1% | 1994.6 | 28.9 | 2.999 | 1.881 |
| Layout Panels | 50.51 | 20.87 | -58.7% | 12.4 | 1.0 | 0.026 | 0.014 |
| Markdown Playground | 89.72 | 35.08 | -60.9% | 26.6 | 2.2 | 1.131 | 0.929 |
| Motion & Animations | 50.40 | 20.66 | -59.0% | 20.4 | 7.8 | 0.603 | 0.342 |
| MotionMark Showcase | 59.40 | 30.55 | -48.6% | 81.8 | 14.8 | 1.123 | 0.149 |
| Password Box | 50.42 | 20.63 | -59.1% | 8.1 | 1.0 | 0.016 | 0.012 |
| Path Operations | 49.96 | 19.99 | -60.0% | 8.1 | 0.9 | 0.021 | 0.013 |
| Picture Caching | 51.18 | 21.26 | -58.5% | 21.7 | 6.0 | 0.802 | 0.352 |
| Radio Button | 50.43 | 20.65 | -59.1% | 12.0 | 0.9 | 0.036 | 0.011 |
| Rating Control | 50.28 | 20.49 | -59.2% | 8.0 | 1.0 | 0.020 | 0.013 |
| Rich Document Editor | 50.32 | 20.35 | -59.6% | 12.8 | 0.9 | 0.032 | 0.013 |
| ShaderToy Playground | 51.48 | 21.48 | -58.3% | 148.0 | 25.6 | 0.707 | 0.510 |
| SkiaSharp Shim | 51.80 | 21.71 | -58.1% | 5.9 | 0.9 | 0.009 | 0.010 |
| SplitView Layout | 50.14 | 20.32 | -59.5% | 13.0 | 0.9 | 0.033 | 0.011 |
| Styles Showcase | 49.41 | 19.52 | -60.5% | 7.3 | 1.0 | 0.028 | 0.009 |
| Text & Documents | 472.28 | 189.27 | -59.9% | 4128.4 | 189.3 | 41.728 | 2.037 |
| Text Shaping Lab | 513.91 | 70.07 | -86.4% | 22.8 | 0.9 | 0.082 | 0.036 |
| Theme Showcase | 52.77 | 23.00 | -56.4% | 15.1 | 1.1 | 0.043 | 0.013 |
| Touch & Gestures | 50.47 | 20.95 | -58.5% | 12.7 | 1.0 | 0.042 | 0.014 |
| Typography & Scripts | 345.18 | 82.16 | -76.2% | 10.2 | 0.9 | 0.036 | 0.017 |
| Vector Shapes | 50.67 | 20.74 | -59.1% | 7.5 | 1.0 | 0.013 | 0.011 |
| Virtualization Controls | 55.09 | 26.69 | -51.5% | 22.9 | 1.1 | 0.016 | 0.018 |
| Visual Designer | 81.26 | 46.57 | -42.7% | 129.5 | 95.3 | 1.906 | 1.872 |
| WPF Shim Showcase | 57.22 | 21.27 | -62.8% | 13.9 | 1.0 | 0.031 | 0.011 |
| Wrap Panel | 49.78 | 19.79 | -60.2% | 9.2 | 0.9 | 0.014 | 0.012 |

Every page allocated less than its original isolated baseline. A few sub-millisecond compile
measurements moved upward: Inter Typeface by 0.048 ms, Glyph Run Showcase by 0.010 ms, and
GPU Charting by 0.001 ms. They remained below 0.55 ms with no over-budget compile frames;
these small single-run movements are retained in the table rather than hidden by aggregate
reporting. Compute FX, Image Effects, and Image & Buttons continue to allocate about
30 KiB/frame for their intentionally changing effect/image workloads.

## Verification

- `ProGPU.Tests` Release: **2,277 passed**, 0 failed.
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
  browser, rendered the 3D and rich-text pages, and reported zero console errors or warnings
  beyond expected system-font fallback messages.
- Quality-sensitive 8x8 analytic coverage, physical-DPI raster size, quarter-physical-pixel
  snapping, direction-aware half-open winding, color glyphs, fallback/variation identity,
  and same-frame atlas recovery were not reduced or bypassed.
