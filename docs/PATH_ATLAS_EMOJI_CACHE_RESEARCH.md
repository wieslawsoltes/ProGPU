# Path atlas and color-emoji cache research

## Scope

This change reduces CPU memory retained by path-atlas compilation and prevents
color-font layers from creating position-dependent path entries. It does not
change shaping, line layout, fallback selection, atlas texture dimensions,
coverage sampling, subpixel phase bounds, or final unsnapped quad placement.
The implementation is clean-room: the sources below informed cache contracts
and observable architecture only; no foreign implementation text, naming, data
layout, or control flow was copied.

## Primary sources

- [Skia `SkStrikeCache`](https://github.com/google/skia/blob/main/src/core/SkStrikeCache.cpp)
  and [Skia graphics cache controls](https://github.com/google/skia/blob/main/include/core/SkGraphics.h)
  treat glyph resources as reusable strike data with explicit byte/count limits
  and purge controls. [SkParagraph's cache contract](https://skia.googlesource.com/skia/+/main/modules/skparagraph/src/ParagraphCache.h)
  separately retains reusable paragraph results.
- [DirectWrite color-font support](https://learn.microsoft.com/en-us/windows/win32/directwrite/color-fonts)
  keeps layout glyph IDs and positions monochrome/position-independent, then
  expands them into ordered color glyph runs at rendering time. The
  [`DWRITE_COLOR_GLYPH_RUN` contract](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_2/ns-dwrite_2-dwrite_color_glyph_run)
  carries baseline origin separately from the layer glyph run.
- [DirectWrite glyph-run analysis](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwrite-idwriteglyphrunanalysis)
  derives bounded device-pixel alpha texture extents from a reusable glyph run.
  [Direct2D/DirectWrite text rendering](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
  preserves reusable layout independently of device-dependent rendering.
- [Win2D `CanvasTextLayout`](https://microsoft.github.io/Win2D/WinUI2/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm)
  exposes reusable layout/cluster results; Win2D color fonts opt into the same
  DirectWrite color-run model rather than embedding destination positions into
  outline identity.
- [WebRender glyph rasterizer source](https://searchfox.org/mozilla-central/source/gfx/wr/wr_glyph_rasterizer/src/lib.rs)
  separates requested glyph identity, worker rasterization, texture upload, and
  platform font backends. [WebRender's rendering overview](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst)
  keeps retained display lists and visibility decisions ahead of resource upload.
- [Vello's renderer architecture](https://github.com/linebender/vello) retains
  scenes and moves parallel path work to GPU compute. Its maintainer-authored
  [glyph-rendering plan](https://github.com/linebender/vello/issues/204) treats
  glyph caching, hinting, and transform policy as distinct concerns.
- [Parley](https://docs.rs/parley/latest/parley/) shares font/layout contexts and
  retains shaped layout, leaving glyph rendering and raster residency to the
  renderer.
- [HarfBuzz shape-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html)
  keys reusable shaping work by face, segment properties, features, and shaper.
  It does not make destination position part of shaping identity.

## Cross-engine comparison and ProGPU decisions

| Concern | Observed architecture | ProGPU decision |
| --- | --- | --- |
| Startup and lazy initialization | Skia, DirectWrite, WebRender, and Parley initialize reusable font/raster contexts independently of each draw. | Keep the existing compositor-owned atlas and lazy per-path compilation. No new startup work or font enumeration is introduced. |
| Shaping and layout reuse | HarfBuzz caches shape plans; SkParagraph, DirectWrite/Win2D, and Parley retain positioned layout. | Leave `TextLayout`, glyph arrays, clusters, fallback faces, and variation selection unchanged. |
| Retained scene and visibility | WebRender and Vello retain scenes and request raster resources only for visible work. | Preserve existing pre-atlas vector-glyph culling and compiled-scene generation checks. |
| Cache identity and eviction | DirectWrite separates layer baseline origin from glyph identity; Skia exposes byte budgets; WebRender separates glyph keys from texture placement. | Adapt color layers to cache only the bounded local fractional phase and carry the remaining translation in the draw transform. Replace count-only compiled-path caches with one shared byte-budgeted LRU. |
| Demand-driven upload | WebRender requests/rasterizes visible glyphs; DirectWrite analysis produces only the requested texture bounds. | Keep `RasterizePendingPaths` demand-driven and batched; repeated emoji layers reuse atlas coordinates instead of scheduling duplicate rasterizations. |
| Worker preparation | WebRender owns a glyph raster worker subsystem; Parley reuses CPU scratch contexts. | Keep path compilation on the existing compilation boundary, but allow fixed-size `TextVisual` shaping to prepare on one CPU worker and publish by revision. GPU atlas allocation stays demand-driven on the compositor thread. |
| GPU batching/compute | Vello performs parallel path work on GPU; WebRender batches resource upload; Direct2D submits glyph runs/layers. | Preserve ProGPU's batched compute rasterizer and vector draw batching. |
| DPI, subpixel, and hinting | Platform engines include rendering mode/transform state in raster identity; Vello distinguishes dynamic vector text from cache-friendly hinted UI text. | Preserve the 128 local phases, four device phases per axis, ten-bit scale quantization, 8x8 high-precision coverage, DPI scaling, and unsnapped final placement. |
| Fallback and color formats | DirectWrite expands a base run into COLR, SVG, or bitmap color runs after layout. | Preserve existing font fallback, COLR/SVG layer order, brush coordinates, bitmap-glyph path, and monochrome fallback. |
| Variable fonts | HarfBuzz and platform engines include variation state in face/shape identity. | No key is broadened across font instances or variation-specific source outlines; outline object identity remains part of the transformed-path key. |
| Device loss and atlas generation | Direct2D separates device-independent layout from device resources; WebRender rebuilds GPU resources after device reset. | Preserve atlas ownership, reset/retry, and `Generation` invalidation. CPU cache eviction never moves atlas UVs and therefore does not increment `Generation`. |

## Adopted, adapted, and rejected

Adopted:

- a strict byte budget for variable-size compiled path records and segments;
- least-recently-used eviction across fill and hit-test compilation data;
- position-independent color-layer outline identity with translation carried by
  the draw transform;
- explicit byte/count diagnostics and focused bounded-memory regressions.
- sequential background preparation for deferred fixed-size text layouts on
  threaded hosts, with atomic revision-checked publication.

Adapted:

- DirectWrite's separate color-run baseline becomes ProGPU's residual placement
  transform, while the local 128-phase lattice remains baked for coverage;
- Skia-style byte budgeting is applied to ProGPU's original typed record/segment
  arrays and shares one budget across both compilation consumers.

Rejected:

- caching exact absolute emoji positions;
- lowering coverage precision, shrinking the GPU atlas, or snapping final quads;
- moving Unicode/OpenType shaping to the GPU;
- creating path-atlas entries or GPU textures from the text-layout worker;
- copying an external atlas, strike-cache, or eviction implementation;
- evicting atlas coordinates independently of `Generation`, which would make
  retained UVs stale.

## Complexity and validation contract

Compiled-path lookup and recency update are average `O(1)`. Insertion evicts `E`
old entries in `O(E)` time and retains at most the configured byte budget plus no
oversize entry; storage is `O(B)` for budget `B`. Color-layer lookup remains
average `O(1)`, path construction is `O(S)` only on a cache miss for `S` outline
segments, and repeated integer-position emoji add no new path-atlas entries.

Validation covers repeated COLR layers at many positions, a deliberately tiny
shared CPU budget, existing fractional/parent-transform phase bounds, atlas
capacity recovery, mixed color/monochrome batching, and rendered pixel presence.

The focused headless measurement renders 48 instances of a two-layer COLR glyph:
96 positioned layer uses collapse to 2 retained path-atlas entries, 97.9% fewer
than one entry per use for that representative integer-position run. A separate
stress case holds combined fill and hit-test compilation payload at or below a
configured 1,024-byte budget throughout 24 distinct paths.

## Measured comparison with `main`

The complex color-emoji comparison used a clean worktree at `origin/main`
(`fc4d27a`) and this branch, .NET SDK 10.0.201, and the same macOS arm64 host.
Each isolated process rendered 256 instances of one synthetic two-layer COLR
glyph whose layers each contain 128 outline points. A full collection immediately
before and after the first render measured retained managed memory. Three runs
produced the following medians:

| Measurement | `main` | This branch | Change |
| --- | ---: | ---: | ---: |
| Retained path-atlas entries | 512 | 2 | -99.6% |
| Retained managed bytes | 19,946,608 | 546,752 | -97.3% |
| First-render time | 55.040 ms | 43.243 ms | -21.4% |

The retained-byte result was stable within 0.05% on `main` and 1.5% on this
branch. One `main` timing sample was an outlier, so the table reports medians and
the regression asserts deterministic atlas residency rather than an absolute
time or process-memory threshold.

The broader `Font Glyph Browser` benchmark was also run for 180 warmup and 600
measured scrolling frames with presentation throttling disabled. Warmed matched
runs were effectively neutral (372.07 versus 372.02 wall FPS, 1.3912 versus
1.3976 ms compositor time, and 192,353 versus 191,440 allocated bytes per frame
for `main` and this branch respectively). Separate process runs showed substantial
system-level timing variance, so these figures are treated as a regression
screen rather than a precise speedup claim. The branch reported 172 resident
path entries and 353,232 compiled-cache bytes in that workload, well below its
8 MiB default budget, with no path-atlas reset.

## Text-page navigation and DXF architecture follow-up

A navigation-sequence benchmark now opens and scrolls `Font Glyph Browser`,
`Text & Documents`, `Markdown Playground`, and `Inter Typeface` before switching
to `Data Virtualization`. The sequence exposed a separate amplification path:
the earlier pages filled the glyph atlas, while ordinary `DrawText` commands did
not opt into its existing LRU region reuse. Cached capacity misses therefore
kept visible DataGrid glyphs on vector fallback indefinitely. That fallback
expanded the final frame from about 70 to more than 600 draw calls and routed
hundreds of glyphs through `PathAtlas`.

The fix marks ordinary `DrawText` as a preferred glyph-atlas consumer. When a
new visible glyph needs space, the atlas may reuse a compatible region that was
not used in the current frame. `Generation` advances so retained UV consumers
recompile, while current-frame regions remain protected. Explicit vector text,
CFF outlines, transformed large text, and color-glyph paths keep their existing
quality-specific branches.

The same sequence was measured before and after the fix:

| Measurement | Before | After | Change |
| --- | ---: | ---: | ---: |
| Draw calls | 618 | 70 | -88.7% |
| Path-atlas entries | 1,556 | 257 | -83.5% |
| Visual-tree compile time | 0.8035 ms | 0.5383 ms | -33.0% |
| Compositor frame time | 1.3424 ms | 0.7827 ms | -41.7% |
| Allocated bytes per frame | 23,436 | 18,944 | -19.2% |

The pre-fix result on current `main` was materially identical (615 draw calls,
1,557 path entries, 0.8205 ms compile time, and 1.3851 ms compositor time), so
the root saturation behavior predates this branch. This PR fixes it because the
bounded path cache makes the fallback amplification and its memory cost directly
observable.

ProGPU's DXF lane was evaluated as a possible atlas replacement. It compiles an
immutable document once into owned vector, text, retained-glyph record/segment,
and instance buffers; camera changes update only a viewport uniform. Its retained
glyph shader evaluates analytic winding directly and draws all instances in one
call, so it correctly avoids per-position path-atlas residency for static CAD
content.

That lane is not a drop-in replacement for ordinary UI text. Direct per-fragment
segment evaluation has a different cost and antialiasing model from the glyph
atlas, and moving the explicit CFF/vector fallback to it would violate the
existing 128 local phases, 16 device phases, and 8x8 coverage contract unless
the shader and batching model were extended. The adopted hybrid is therefore:

- use glyph-atlas LRU residency for ordinary small UI text;
- keep the byte-bounded path atlas for general filled paths and quality-sensitive
  vector/color glyph fallbacks;
- keep DXF-style immutable GPU buffers for static documents and retained scenes;
- consider a future dynamic retained-glyph batch only after it preserves command
  order, clips, masks, blend modes, subpixel coverage, and device-loss behavior.

## Inter specimen cold-scroll follow-up

The Inter specimen exposed a CPU preparation issue independent of `PathAtlas`.
It contains 112 fixed-height retained `TextVisual` specimens. The original page
deferred those layouts for fast activation, then shaped them in four-millisecond
chunks posted to the UI dispatcher. A per-layout trace found ordinary specimens
at roughly 0.3–3 ms, the first italic variable-font shaping plan at 34.7 ms, and
later complex specimens at 7.5–9.8 ms. Those chunks ran in the host update that
also advances scrolling. The completed Inter sweep retained only 162 path-atlas
entries and 157,264 bytes of compiled path data, so replacing or enlarging the
path atlas could not address this hitch.

`TextVisual.WarmDeferredLayout` now constructs the CPU-only layout and publishes
the completed reference atomically only when its text/font/width revision is
still current. Inter starts one sequential worker after the first presentation;
it stops when navigation detaches the page. The render thread still performs
visible glyph-atlas allocation and upload. Single-threaded browser hosts retain
the bounded dispatcher slices instead of pretending `Task.Run` provides a
worker.

A matched unrestricted-presentation run with two warmup and 220 scrolling frames
reduced the worst host-update interval from 17.6304 ms to 0.6410 ms (-96.4%).
The worker shifts the same eventual retained-layout allocations off the UI
thread, so short-process total-allocation and unrestricted-throughput figures
remain sensitive to scheduling and are not used as correctness thresholds.

The full same-process sequence then scrolled Inter, Text & Documents, and Font
Glyph Browser for 180 frames each before Data Virtualization. Its 600 measured
Data Virtualization frames completed at 514.18 wall FPS with 0.5176 ms average
compile time, 2.0647 ms maximum compile time, 0.7577 ms compositor time, 69 draw
calls, 117 path entries, and no glyph-atlas eviction or clear. This verifies that
the Inter worker stops on detach and does not transfer contention or cache
pressure to the virtualized page.
