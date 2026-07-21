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
| Worker preparation | WebRender owns a glyph raster worker subsystem; Parley reuses CPU scratch contexts. | Reject adding worker synchronization in this focused change; path compilation remains on the existing compilation boundary. |
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

Adapted:

- DirectWrite's separate color-run baseline becomes ProGPU's residual placement
  transform, while the local 128-phase lattice remains baked for coverage;
- Skia-style byte budgeting is applied to ProGPU's original typed record/segment
  arrays and shares one budget across both compilation consumers.

Rejected:

- caching exact absolute emoji positions;
- lowering coverage precision, shrinking the GPU atlas, or snapping final quads;
- moving Unicode/OpenType shaping to the GPU;
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
