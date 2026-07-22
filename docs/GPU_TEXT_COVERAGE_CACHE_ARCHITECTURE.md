# GPU text coverage-cache architecture

Date: 2026-07-22

## Objective and constraints

This design reduces persistent GPU memory for ProGPU text and vector coverage without changing the retained-scene, shaping, fallback-font, variable-font, DPI, subpixel, hinting, or winding contracts. Rasterization remains GPU-only: no coverage image is rasterized on, or read back through, the CPU.

The implementation is clean-room. The sources below informed public behavior, architecture, and tradeoffs only; no foreign source code, helper organization, names, or encoded tables were copied or translated.

## Primary-source research

| Engine | Relevant architecture | ProGPU decision |
| --- | --- | --- |
| [Skia strike cache](https://skia.googlesource.com/skia/+/main/src/core/SkStrikeCache.h), [SkParagraph API](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h), and [SkParagraph LRU cache](https://skia.googlesource.com/skia/+/a1f873e79a50/modules/skparagraph/include/ParagraphCache.h) | Separates reusable font/strike data, bounds caches by bytes/count, retains shaped paragraph state, and treats glyph representations as cacheable resources rather than one unbounded global image. | Adopt byte-accounted resource limits, representation separation, and retained layout reuse. Keep ProGPU's typed font/outline ownership and GPU rasterizer instead of adopting Skia's CPU strike implementation. |
| [Skia `GrDrawOpAtlas`](https://skia.googlesource.com/skia/+/main/src/gpu/ganesh/GrDrawOpAtlas.cpp) | Activates backing pages lazily and manages independently reusable plots with generation and LRU state. | Adapt lazy physical residency and separate texture revision from content generation. Keep ProGPU's single-texture-per-representation contract and original shelf/recovery allocators rather than copying Skia's page/plot organization. |
| [DirectWrite glyph-run analysis](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwriteglyphrunanalysis) and [Direct2D text performance guidance](https://learn.microsoft.com/en-us/windows/win32/direct2d/improving-direct2d-performance) | Exposes bounded glyph-run alpha bounds and uses one byte per pixel for grayscale alpha, three for ClearType; encourages reuse of text layouts and rendering resources. | Adopt compact grayscale coverage and retained shaped-run reuse. Preserve ProGPU's existing shader reconstruction of grayscale/ClearType output and four-way physical-pixel snapping. |
| [DirectWrite color fonts](https://learn.microsoft.com/en-us/windows/win32/directwrite/color-fonts), [Win2D overview](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/), and [Win2D `CanvasTextLayout`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm) | Color glyph layers are a distinct representation, while reusable text layouts retain shaping and line-layout work. Win2D exposes Direct2D/DirectWrite through a GPU-accelerated API. | Split RGBA bitmap/color glyphs from monochrome coverage. Keep shaping/layout above the atlas and preserve fallback/variable-font state in existing typed cache keys. |
| [WebRender rendering overview](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst) and [glyph rasterizer](https://searchfox.org/mozilla-central/source/gfx/wr/wr_glyph_rasterizer/src/lib.rs) | Culls a retained scene before preparing visible resources, distinguishes alpha and color bitmap glyph formats, and manages them through a texture cache. Glyph raster preparation can run on workers. | Adopt alpha/color format separation and demand-driven population after visibility/scene compilation. Retain ProGPU's GPU compute preparation rather than adding CPU raster workers. |
| [glyphon text atlas](https://github.com/grovesNL/glyphon/blob/main/src/text_atlas.rs) | Starts mask/color atlases small, grows them geometrically, and applies per-frame usage/LRU policy. | Adapt small initial mask/color textures and bounded geometric growth. ProGPU preserves resident texels by GPU copy and keeps texel-space vertices, an original solution required by its retained-scene contract. |
| [Vello](https://github.com/linebender/vello) and its [glyph rendering design discussion](https://github.com/linebender/vello/issues/204) | Uses GPU compute for vector rendering and evaluates dynamic outlines versus cached glyph images; transform-specific hinting makes one universal cached representation unsuitable. | Keep analytic GPU rasterization and bounded cached coverage. Reject an all-vector-per-frame design because stable UI text benefits from retained coverage, and reject SDF/MSDF substitution because it would change small-text hinting and coverage quality. |
| [Parley](https://docs.rs/parley/latest/parley/) | Shares font and layout contexts and keeps shaping/layout reusable and independent of the final renderer. | Preserve ProGPU's reusable CPU shaping/layout results and glyph indices; do not move Unicode/OpenType shaping into the coverage cache. |
| [HarfBuzz shaping-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html) | Reuses shaping plans for matching face, segment properties, and features. | Preserve current shaped results, OpenType feature state, fallback selection, and variable-font instance identity; atlas changes start only after glyph identity and position are known. |
| [WebGPU specification](https://gpuweb.github.io/gpuweb/) | Defines `r8unorm`, storage buffers, and buffer-to-texture copies with aligned row layout. Storage-texture format capabilities vary by implementation tier. | Use a universally writable storage buffer as compute output, pack four coverage bytes per `u32`, then issue GPU buffer-to-`r8unorm` copies. This avoids depending on optional R8 storage-texture support. |
| [wgpu `StagingBelt`](https://wgpu.rs/doc/wgpu/util/belt/struct.StagingBelt.html) | Suballocates reusable chunks, recommends sizing below a whole submission, and allocates an exceptional buffer when one operation exceeds a normal chunk. | Adapt bounded 64 KiB uniform and 256 KiB coverage rings, exact temporary storage for a single oversized glyph, and 256 KiB path dispatch chunks. No wgpu implementation source was ported. |

The required comparison areas map as follows:

- Startup and lazy initialization: maximum atlas dimensions are capacity limits, not startup allocations. Coverage/path begin at 512 square and color at 256 square; no font enumeration, outline upload, or raster work is added at startup.
- Shaping and layout reuse: unchanged. Positioned glyph indices and advances remain reusable CPU results, following the separation used by DirectWrite, Parley, HarfBuzz, and SkParagraph-style stacks.
- Retained scene and visibility: unchanged. Only glyphs and paths demanded by compiled visible commands enter the atlas, comparable to WebRender's retained-scene resource preparation.
- Cache keys and eviction: all existing glyph style, physical size, DPI/subpixel, local-transform, variable-font, path-phase, and generation keys remain intact. Alpha and color entries cannot reuse one another's storage.
- Demand-driven upload: compute output is copied only for pending glyph/path rectangles. There is no full-atlas upload or CPU readback.
- Worker preparation: font parsing/shaping remains reusable CPU work. Coverage preparation is GPU compute, batched before rendering; adding CPU raster workers was rejected.
- GPU batching: glyphs share small persistent rings and use an exact exceptional buffer only for one oversized glyph. Paths reuse a buffer per bounded dispatch chunk rather than allocating the sum of all pending output. One invocation produces four adjacent coverage texels.
- DPI, subpixel, and hinting: raster dimensions, physical-space phase keys, final unsnapped placement, sample grids, gamma, and ClearType reconstruction are unchanged.
- Fallback and variable fonts: existing font identity, shaped glyph index, and outline selection remain the authoritative inputs. The storage format does not merge font instances.
- Device loss and atlas generations: resources remain owned by the compositor/context; atlas `Generation` changes only on real clear/repack. A format split does not weaken retained-scene invalidation or capacity-retry behavior.

## Implemented design

### Compact GPU coverage pipeline

The glyph and path compute shaders keep their existing analytic winding and sample grids. Each invocation now rasterizes four adjacent output pixels and packs the four normalized coverage bytes into one `u32` in a storage buffer. A command-encoder buffer-to-texture copy places the result in an `R8Unorm` atlas. Row pitch is aligned to WebGPU's 256-byte copy requirement.

For a raster rectangle of width `W`, height `H`, segment count `S`, and sample grid `A`, raster work remains `O(W * H * A * S)` in the worst case. Output storage is `O(align256(W) * H)` bytes rather than `O(4 * atlasWidth * atlasHeight)` writable RGBA storage. The output never crosses the CPU boundary.

### Representation-separated atlases

- Monochrome glyph coverage: starts at `512 x 512 x 1` byte R8 and doubles on demand up to the configured maximum (`2560` in the sample).
- Bitmap/color glyphs: starts at `256 x 256 x 4` byte RGBA and grows independently up to `512`.
- Paths and vector-glyph fallback: starts at `512 x 512 x 1` byte R8 and grows up to `2048`.
- Glyph compute staging: 64 KiB uniform ring plus 256 KiB coverage ring. A single glyph larger than the normal ring is processed with exact temporary GPU storage rather than failing or permanently enlarging the ring.
- Path compute staging: one buffer bounded to a 256 KiB dispatch chunk in the ordinary case. A single larger path receives the exact space it requires, preserving coverage quality.

Growth clears a new texture, copies the old top-left texels entirely on the GPU, swaps texture ownership, and increments a texture revision. Atlas `Generation` does not change because no entry moved and no content became invalid. Retained text/path vertices carry texel coordinates; the shaders normalize against the currently bound texture dimensions. Consequently, a texture can grow during compilation without invalidating already compiled vertices or normalized public metadata. The compositor rebuilds only the affected bind groups when a texture revision changes.

The text shader selects the R8 or RGBA binding from the retained glyph representation flag. This avoids scaling the color allocation to the dimensions required by Latin/CJK/vector coverage while preserving premultiplied bitmap-glyph sampling.

### Bounded outline storage

GPU glyph outline records and segments now use one global pair of buffers across all fonts. Per-font state contains only the glyph-to-global-slot map, eliminating per-font minimum allocations and slack. Capacity grows by 1.5x through GPU-to-GPU copy instead of doubling. A reusable CPU scratch list is used only while parsing a newly demanded outline. Allocated GPU outline capacity is measured and bounded to 4 MiB by default; the cache is rebuilt lazily after a frame-boundary trim. Coverage already resident in the atlas remains valid.

### Managed retained-command ownership

Managed heap profiling found that `RichTextBlock`, `MarkdownTextBlock`, and `FlowDocument` already owned immutable-until-invalidated `DrawingContext` command caches, while the compositor copied the same large value-type command arrays into a pooled recording context and then into a generic retained cache. These controls now implement the typed `IOwnedRenderCommandCache` contract. The compositor requests and compiles their existing cache directly; it neither calls the copying `OnRender` adapter nor creates a second retained stream. Public/manual `OnRender` behavior is unchanged.

### Protocol and diagnostics

The typed native and browser WebGPU APIs expose command-encoder buffer-to-texture and texture-to-texture copies. Compositor metrics report current and maximum atlas dimensions, coverage/color/path texture bytes, glyph staging bytes, outline count/capacity/GPU bytes, and current/peak path staging. The sample benchmark additionally reports managed heap and fragmented bytes.

## Memory and performance evidence

The sample configuration previously reserved two RGBA atlases:

| Persistent/bounded GPU allocation | Before | After |
| --- | ---: | ---: |
| Glyph coverage/color textures | 26,214,400 B | 7,602,176 B |
| Path coverage texture | 16,777,216 B | 4,194,304 B |
| Bounded glyph staging | 0 B | 2,097,152 B |
| **Fixed atlas + glyph staging total** | **42,991,616 B** | **13,893,632 B** |

This is a 67.7% reduction (29,097,984 bytes) in the exact fixed/bounded allocation represented by those resources. The new system additionally reports, and bounds, outline and transient path staging rather than hiding them in process-level memory figures.

The second pass changes configured dimensions into maxima. For the same sample configuration, exact startup residency is:

| GPU allocation | First compact pass | Demand-grown pass at startup |
| --- | ---: | ---: |
| Glyph coverage texture | 6,553,600 B | 262,144 B |
| Color glyph texture | 1,048,576 B | 262,144 B |
| Path coverage texture | 4,194,304 B | 262,144 B |
| Glyph coverage staging | 2,097,152 B | 262,144 B |
| Glyph uniform staging | 262,144 B | 65,536 B |
| Initial global outline buffers | per-font | 51,200 B total |
| **Known startup total above** | **14,155,776 B plus per-font outlines** | **1,165,312 B** |

The new known startup total is 91.8% below the first compact pass and 97.3% below the original 42,991,616-byte RGBA-atlas allocation, while retaining the same configured maximum coverage capacity. At maximum texture size, the smaller rings still reduce the fixed atlas/staging portion from 13,893,632 to 12,124,160 bytes. Path staging is no longer proportional to the total pending raster area: the ordinary peak is 256 KiB, with only a single quality-preserving oversized path allowed to exceed it.

A release headless profile of all 195 sample-page tests used induced-GC heap dumps plus root analysis. The dominant necessary residency was font payload/parser data and visible rich-document layout. The actionable duplicate was a 2,228,248-byte `RenderCommand[]` for the active Markdown workload: before direct replay, the same-sized array appeared twice (owned cache plus copied compositor context); after direct replay, only the owned array remained. The full induced-GC checkpoint reported 87,062,289 live managed bytes and 711,902 objects after the change. Whole-heap totals are workload-phase sensitive, so the precise claim is the eliminated duplicate command array and ownership path, not a process-RSS percentage.

A release-build sweep visited all text/font sample pages (Text & Documents, Rich Document Editor, Markdown Playground, Glyph Run Showcase, Text Shaping Lab, Typography & Scripts, Inter Typeface, Interactive Input, Font Glyph Browser, WPF Shim Showcase, and SkiaSharp Shim) before measuring the same Data Virtualization scene for 60 warm-up and 180 measured frames:

| Metric | Baseline | New design | Change |
| --- | ---: | ---: | ---: |
| Compiled-scene CPU time | 0.9577 ms | 0.5308 ms | -44.6% |
| Compositor CPU time | 1.2040 ms | 0.7687 ms | -36.2% |
| Managed allocation/frame | 26,953 B | 25,964 B | -3.7% |
| Wall throughput | 480.26 FPS | 472.54 FPS | -1.6% |
| Glyph/path capacity failures | 0 / 0 | 0 / 0 | unchanged |
| Glyph evictions / atlas clears / path resets | 0 / 0 / 0 | 0 / 0 / 0 | unchanged |

Wall throughput varied by 1.6%, so it is treated as neutral noise rather than an improvement claim. Process RSS and OS memory-footprint counters were also noisy and contradictory between isolated runs; the memory claim therefore uses exact resource sizes exposed by compositor metrics. The populated new run held 2,712 glyph entries and 141 path entries, allocated 1,775,616 bytes of GPU outline capacity, used 196,080 bytes of path CPU cache, and peaked at 1,178,624 bytes of transient path staging.

An isolated Font Glyph Browser run also reduced compilation from 3.5679 to 1.7468 ms and compositor CPU time from 3.9711 to 2.0189 ms, while allocations fell from 117,561 to 102,670 bytes/frame. These timings support the architectural result but are not substituted for the all-feature sweep.

## Quality and regression evidence

- The glyph shader retains 8x8 high-precision coverage and the direction-aware half-open quadratic/cubic winding rules.
- The path shader retains each command's selected sample grid, fill rule, transform phase, scale quantization, and recovery behavior.
- Color bitmap glyphs retain RGBA data and filtered sampling in a dedicated texture.
- The text shader retains aliased, grayscale, gamma/contrast, mask, and ClearType paths; only the sampled texture binding changes.
- Atlas generations still change only when cached UV contents are cleared, moved, or repacked.
- Texture growth is covered by a GPU readback regression proving that resident coverage survives growth at unchanged texel coordinates while `Generation` remains stable and texture revision advances.
- Owned rich-text/Markdown/flow command caches are compiled directly and remain compatible with explicit `OnRender` replay.
- Regression tests cover exact configured residency, R8 readback, color/coverage separation, shader resource contracts, atlas recovery, phase bounds, and existing rendering behavior.

Validation commands:

- `dotnet test src/ProGPU.Tests/ProGPU.Tests.csproj -c Release --no-restore`: 2,259 passed, 0 failed.
- `dotnet build src/ProGPU.slnx -c Release --no-restore`: succeeded with 0 errors.
- `dotnet publish src/ProGPU.Samples.Browser/ProGPU.Samples.Browser.csproj -c Release --no-restore`: AOT-compiled all 68 eligible assemblies and produced the native WebAssembly artifact.

## Rejected alternatives

- RGBA storage textures for coverage: portable and simple, but waste three channels for every monochrome texel.
- R8 storage-texture writes: direct, but not a sufficiently portable baseline across the native and browser targets.
- CPU rasterization or CPU repacking: lowers GPU requirements but violates the GPU-only goal and adds transfer/synchronization cost.
- SDF/MSDF atlases: attractive for scale reuse, but change small-text coverage, hinting, stroke, and transformed vector quality.
- Per-frame uncached vector text: bounds residency but repeats analytic raster work for stable UI text.
- One universal color/coverage atlas: recreates the RGBA tax and couples unrelated capacity/eviction pressure.
