# ProGPU sample memory optimization

## Current outcome

This document is updated after every measured optimization step. The current release
baseline is complete for both ProGPU's WinUI gallery and the ProGPU-backed Avalonia
ControlCatalog. No optimization result is accepted unless the same Release workload keeps
frame throughput, compositor timings, explicit GPU residency, and rendered output at least
equivalent within normal run-to-run noise.

Current step: **core atlas, compositor reservations, target-specific pipeline residency,
shared primary/offscreen binding infrastructure, initial monochrome-glyph residency, and
glyph-outline buffer capacity reduced and applied to the Avalonia backend; WinUI
offscreen-effects, glyph, bidi, and rich-document traversal continuation complete**.

| Stage | Workload | Physical footprint | Retained managed | Throughput | Status |
|---|---|---:|---:|---:|---|
| Release baseline | WinUI, average of 54 isolated pages | 450.62 MiB | 34.48 MiB | 414.17 FPS average | Complete |
| Release baseline | WinUI, worst page: Text & Documents | 573.08 MiB | 192.10 MiB | 67.61 FPS | Complete |
| Release baseline | Avalonia ControlCatalog: Buttons | 189.94 MiB | 15.17 MiB | Static after warm-up | Complete |
| Release baseline | Avalonia ControlCatalog: TextBlock | 407.34 MiB | 209.92 MiB | 3.4% sampled process CPU | Complete |
| Release baseline | Avalonia ControlCatalog: Custom Drawing | 1,027.28 MiB | 202.46 MiB | 19.0% sampled process CPU | Complete |
| Release baseline | Avalonia ControlCatalog: Composition | 200.20 MiB | 14.50 MiB | Static after warm-up | Complete |
| Step 1 | Avalonia ControlCatalog: TextBlock | 230.53 MiB (**-43.4%**) | 28.68 MiB (**-86.3%**) | 1.9% sampled process CPU | Complete |
| Step 1 | Avalonia ControlCatalog: Custom Drawing | 398.88 MiB (**-61.2%**) | 17.84 MiB (**-91.2%**) | Animated; frame instrumentation pending | Complete |
| Step 2 | Avalonia ControlCatalog: Custom Drawing | 379 MiB (**-63.1% cumulative**) | 17.87 MiB (**-91.2% cumulative**) | 59.85 FPS; 0.640 ms compositor | Complete |
| Step 3 | WinUI: Text & Documents | 525.17 MiB (**-8.4%**) | 129.47 MiB (**-32.6%**) | 73.11 FPS; 2.175 ms compile | Complete |
| Step 3 | WinUI: Text & Documents, forced live heap | — | 77.99 MiB (**-44.2%**) | Same workload and render counts | Complete |
| Step 3 | WinUI: Typography & Scripts | 446.10 MiB (**-11.7%**) | 30.39 MiB (**-65.1%**) | 433.29 FPS | Complete |
| Step 3 | WinUI: Text Shaping Lab | 385.35 MiB (**-20.9%**) | 43.73 MiB (**-45.1%**) | 434.22 FPS | Complete |
| Step 3 | WinUI, final average of all 54 isolated pages | 438.62 MiB (**-2.7%**) | 27.75 MiB (**-19.5%**) | 431.08 FPS average (**+4.1%**) | Complete |
| Step 12 | WinUI, final average of all 54 isolated pages | 446.14 MiB (driver-noise band) | 27.71 MiB | 430.94 FPS; 7.85 KiB/frame (**-31.3%** from Step 4) | Complete |
| Step 13 | WinUI: Compute FX / Image & Buttons | Driver-noise band | Unchanged after compacting GC | 1.23 / 1.39 KiB/frame median; redundant offscreen hit-test index removed | Complete |
| Step 15 | WinUI: Font Glyph Browser | Driver-noise band | Unchanged after compacting GC | 36.41 KiB/frame median (**-8.3%**); geometric glyph-instance growth | Complete |
| Step 16 | WinUI: Font Glyph Browser | Driver-noise band | Unchanged after compacting GC | 28.05 KiB/frame median (**-17.9%** from fresh baseline); ASCII UAX resolver stacks eliminated | Complete |
| Step 17 | WinUI: Font Glyph Browser | Driver-noise band | Unchanged after compacting GC | 27.66 KiB/frame median (**-1.4%**, **-19.1%** cumulative); identity visual-order arrays eliminated | Complete |
| Step 18 | WinUI: Font Glyph Browser | Driver-noise band | Unchanged after compacting GC | 27.30 KiB/frame median (**-0.3%** paired, **-20.1%** cumulative); bounded line-level workspace | Complete |
| Step 19 | WinUI: Font Glyph Browser | Driver-noise band | Unchanged after compacting GC | 27.24 KiB/frame median (**-1.2%** paired, **-20.3%** cumulative); indexed recursive inline scans | Complete |
| Step 20 | WinUI: Font Glyph Browser | Driver-noise band | Unchanged after compacting GC | 26.80 KiB/frame median (**-1.0%** same-binary, **-21.6%** cumulative); indexed virtualized height scans | Complete |
| Step 3 | Avalonia ControlCatalog: Custom Drawing | 381 MiB (**-62.9% cumulative**) | 15.36 MiB (**-92.4% cumulative**) | 58.66–60.72 FPS under varying GPU contention | Complete |
| Step 4 | Avalonia, four-page allocation average | Driver-noise band | Unchanged after compacting GC | 4.47 KiB/frame (**-82.1%**) | Complete |
| Step 4 | Avalonia ControlCatalog: Custom Drawing | Driver-noise band | 21.79 MiB | 11.14 KiB/frame (**-81.9%**); 60.11 FPS | Complete |
| Step 12 | Avalonia, four-page allocation average | Driver-noise band | Unchanged after compacting GC | 3.05 KiB/frame (**-87.5%** from release baseline) | Complete |
| Step 12 | Avalonia ControlCatalog: Custom Drawing | 367.88 MiB | 21.79 MiB | 5.86 KiB/frame (**-47.4%** from Step 4); 58.41 FPS under desktop contention | Complete |
| Step 21 | Avalonia compositor persistent scene buffers | Exact configured GPU reservation | — | 252.2 KiB, **-7.19 MiB / -96.7%** from the former 7.44 MiB reservation; geometric one-time growth | Complete |
| Step 22 | Avalonia unused core effect resources | Same-binary Custom Drawing | 21.63 MiB retained managed | **12 shader modules + 12 compute pipelines + 4 layouts + 96 B parameters eliminated**; 6,022 B/frame and 56.40 FPS medians unchanged | Complete |
| Step 23 | Avalonia unused core chart resources | Same-binary Custom Drawing | 21.62 MiB retained managed | **2 shader modules + 4 render pipelines + 2 layouts eliminated**; 6,022 B/frame and 56.49 FPS medians unchanged | Complete |
| Step 24 | Core/Avalonia glyph residency | Exact reservation + same-binary Custom Drawing | 21.52 MiB retained managed | **336 KiB persistent GPU/managed reservation eliminated**; 5,997 B/frame and 56.73 FPS medians unchanged | Complete |
| Step 25 | Core/Avalonia path-atlas residency | Same-binary Custom Drawing | 21.53 MiB retained managed | **3 MiB / 75% path-texture residency eliminated**; 5,998 B/frame and 56.28 FPS median with no timing regression | Complete |
| Step 26 | Core/Avalonia target pipelines | Same-binary Custom Drawing | 21.54 MiB retained managed | **10 → 5 resident scene pipelines**; first rendered frame **-12.0 ms median**, steady timings unchanged | Complete |
| Step 27 | Core/Avalonia target bindings | Same-binary Custom Drawing | 21.52 MiB retained managed | **17 duplicate native binding objects eliminated**; allocation and compositor timings unchanged | Complete |
| Step 28 | Core/Avalonia monochrome glyph atlas | Same-binary Custom Drawing | 21.52 MiB retained managed | **262,144 → 67,600 B persistent R8 texture (-74.2%, -190 KiB)**; no growth or timing regression | Complete |
| Step 29 | Core/Avalonia glyph-outline capacity | Same-binary Custom Drawing | **12,800 B direct managed element reservation eliminated** | **51,200 → 44,544 B persistent GPU buffers (-13.0%)**; allocation unchanged and no capacity growth | Complete |

The target is to approach a 50% reduction where the retained data is avoidable. It is a
directional target, not permission to lower raster quality, reduce benchmark work, skip
invalidation, disable animations, or trade memory for worse CPU/GPU frame performance.

## Release baseline

The baseline starts from commit
`cd768dbd53724544ae38adc959fa246414dbb898` (`v0.1.0-preview.27`). Both applications were
built once in Release and the exact binaries were reused for all measurements.

### WinUI sample gallery

`tools/profile-sample-memory.sh` launched every one of the 54 pages in a fresh process. Each
process ran 120 warm-up frames, performed a blocking compacting GC, measured 300 frames,
performed another compacting GC, wrote its metrics, and exited:

```bash
PROGPU_MEMORY_SKIP_BUILD=1 \
PROGPU_MEMORY_KEEP_TRACES=0 \
PROGPU_MEMORY_WARMUP_FRAMES=120 \
PROGPU_MEMORY_MEASURE_FRAMES=300 \
tools/profile-sample-memory.sh artifacts/memory-optimization/baseline-winui
```

| Metric | 54-page average | Worst measured page |
|---|---:|---:|
| macOS physical footprint | 450.62 MiB | 573.08 MiB, Text & Documents |
| Retained managed memory | 34.48 MiB | 192.10 MiB, Text & Documents |
| Allocation per measured frame | 11.31 KiB | 197 KiB, Text & Documents |
| Wall throughput | 414.17 FPS | 67.61 FPS, Text & Documents |
| Compositor compilation | 0.415 ms | 3.811 ms page average, LOL/s |

The highest retained pages are text- or tooling-heavy: Text & Documents (192.10 MiB),
Typography & Scripts (87.07 MiB), Text Shaping Lab (79.58 MiB), GPU Charting (76.07 MiB),
LOL/s (74.94 MiB), Inter Typeface (69.04 MiB), and Visual Designer (64.91 MiB). The ordinary
page floor is approximately 22–26 MiB retained managed memory. Text & Documents also retains
118.8 MiB on the large object heap and 27.7 MiB of fragmentation, so it remains the primary
WinUI follow-up after the Avalonia outlier is corrected.

### Avalonia ControlCatalog

Four representative pages were launched in fresh Release processes and left visible for
15 seconds. Physical footprint came from `/usr/bin/footprint`, process CPU and RSS from
`ps`, and retained managed objects from a forced-GC `dotnet-gcdump`.

| Page | Physical footprint | Lifetime peak | Retained managed | RSS | Sampled CPU |
|---|---:|---:|---:|---:|---:|
| Buttons | 189.94 MiB | 369.75 MiB | 15.17 MiB | 113.20 MiB | 0.9% |
| TextBlock | 407.34 MiB | 594.24 MiB | 209.92 MiB | 119.84 MiB | 3.4% |
| Custom Drawing | 1,027.28 MiB | 1,027.34 MiB | 202.46 MiB | 298.28 MiB | 19.0% |
| Composition | 200.20 MiB | 400.78 MiB | 14.50 MiB | 108.02 MiB | 0.4% |

The forced-GC dumps identify one `System.Byte[]` of 192,123,512 bytes on both outlier pages.
`/System/Library/Fonts/Apple Color Emoji.ttc` is 192,123,488 bytes; the 24-byte difference is
the managed array header. This proves that `FontManagerImpl` reads and permanently caches the
entire color-emoji collection when TextBlock renders emoji or Custom Drawing renders its
plus/minus symbols. Candidate fallback currently loads a complete font before checking its
compact `cmap`, so unrelated fallback candidates can also be read eagerly.

Custom Drawing additionally invalidates itself continuously. That workload is intentional
and will remain enabled: after font residency is fixed, its remaining native/GPU growth must
be measured and corrected without reducing its animation rate or drawing content.

## Performance and quality gates

Every step must pass all applicable gates:

- identical Release build, window size, page, warm-up, and measurement duration;
- retained managed bytes after a compacting full GC and macOS physical footprint;
- exact allocation per frame plus GC collection and pause counts;
- wall FPS, compile/upload/render timings, frame-budget misses, and sampled CPU;
- explicit glyph/path atlas textures, staging buffers, entries, generations, and evictions;
- deterministic screenshots or render-test output for affected text, emoji, paths, and
  animated content;
- focused unit tests followed by the complete ProGPU and Avalonia renderer test suites.

Small sub-millisecond timing changes are reported rather than hidden. A change that lowers
memory by dropping a glyph, changing fallback, reducing physical-DPI rasterization, altering
subpixel placement, disabling an animation, or avoiding required invalidation is a failure.

## Cross-engine design record

This is a clean-room implementation. No external source is copied, translated, ported, or
structurally reproduced. Primary sources are used only to choose observable behavior and
architecture; the implementation remains ProGPU's typed, reflection-free font, scene,
atlas-generation, invalidation, and GPU ownership design.

| Primary source | Observed architecture | ProGPU decision |
|---|---|---|
| [Skia strike cache](https://github.com/google/skia/blob/main/src/core/SkStrikeCache.cpp), [SkParagraph font collection](https://github.com/google/skia/blob/main/modules/skparagraph/src/FontCollection.cpp) | Memory- and count-budgeted glyph strikes are made most-recently-used on lookup and purged; paragraph font fallback caches selected typefaces, with explicit cache clearing. | Adopt bounded residency and stable face identity. Do not retain an unbounded full-file array merely because one glyph was requested. |
| [DirectWrite custom font sets](https://learn.microsoft.com/en-us/windows/win32/directwrite/custom-font-sets-win10), [glyph-run analysis](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwriteglyphrunanalysis), [Direct2D performance guidance](https://learn.microsoft.com/en-us/windows/win32/direct2d/improving-direct2d-performance) | Lightweight face references are distinct from actual font data; file streams are opened when needed, character/glyph subsets can be requested, and layout/raster results are reusable. | Keep catalog and `cmap` metadata lightweight, instantiate a renderable face only after coverage matches, and make heavy bitmap-glyph payloads demand-resident. |
| [Win2D CanvasTextLayout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm), [Win2D QuickStart](https://microsoft.github.io/Win2D/WinUI2/html/QuickStart.htm) | A text layout is a cached, reusable drawable result; repeatedly drawn resources should be created once and reused. | Preserve Avalonia/HarfBuzz shaped-run reuse and the existing retained glyph commands. Reject per-frame face or layout construction as a memory workaround. |
| [WebRender overview](https://searchfox.org/firefox-main/source/gfx/docs/RenderingOverview.rst), [blob image design](https://searchfox.org/firefox-main/source/gfx/wr/webrender/doc/blob.md) | Visibility narrows a retained scene to a frame; raster work can be prepared off-thread while texture upload remains lazy and visibility-driven. | Retain font metadata and shaping state on the CPU, but make bitmap-glyph payload and GPU-atlas residency demand-driven. |
| [Vello glyph design](https://github.com/linebender/vello/issues/204), [Parley/Fontique architecture](https://github.com/linebender/parley) | Unicode/OpenType shaping remains a CPU concern; Fontique memory-maps font files so pages are loaded lazily and shared by the OS, while glyph rendering is independently cached. | Preserve HarfBuzz shaping, use the glyph-resident `sbix` path for huge bitmap collections, and use read-only OS/assembly-backed storage for ordinary raw SFNT payloads. Keep only selected compact `cmap` subtables managed for hot scalar lookup. |
| [HarfBuzz face table callbacks](https://harfbuzz.github.io/harfbuzz-hb-face.html), [HarfBuzz blob ownership](https://harfbuzz.github.io/harfbuzz-hb-blob.html), [shaping-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html) | HarfBuzz can request individual tables, wraps caller-owned immutable data, and benefits from stable reusable faces/plans. | Supply a compact, immutable standalone shaping face containing complete layout tables while stripping unrequested heavy `sbix` records; keep the face alive and cached for reuse. |
| [Skia `SkCanvas`](https://api.skia.org/classSkCanvas.html), [Skia draw dispatch](https://skia.googlesource.com/skia/+/c6e63919c318/src/core/SkCanvas.cpp), [Skia `SkDraw`](https://skia.googlesource.com/skia/+/f1fab32641ade28345693284437b5295d0749f04/src/core/SkDraw.h) | Rectangles, rounded rectangles, and ovals remain explicit primitive operations rather than requiring a caller-created general path. | Keep Avalonia primitive commands typed through recording and create a clip path only for scene/image brushes that actually require geometry clipping. |
| [Direct2D `DrawRectangle`](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1rendertarget-drawrectangle), [`FillRectangle`](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1rendertarget-fillrectangle), [`FillRoundedRectangle`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-fillroundedrectangle%28constd2d1_rounded_rect_id2d1brush%29) | The render target accepts typed primitives and reusable brush resources directly. | Reuse bounded, value-keyed solid brush and pen conversions while preserving current color, opacity, thickness, cap, join, and miter values. |
| [WebRender renderer bindings](https://searchfox.org/mozilla-central/source/gfx/webrender_bindings/src/bindings.rs), [WebRender rendering overview](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst) | Renderer resources and retained pictures are cached, while shader precompilation is an explicit policy rather than incidental per-draw work. | Reuse stable adapter resources and demand-create independent effect/chart and primary/offscreen vector/text/texture pipeline families. Keep `PrecompileBasePipelines` as an explicit opt-in policy for clients that intentionally trade residency for prewarming. |
| [Vello](https://github.com/linebender/vello) | The scene encoding retains typed fills and strokes for a compute-centric renderer. | Preserve typed ProGPU commands and bounded conversion reuse; do not flatten ordinary Avalonia primitives into transient path object graphs. |
| [Skia `drawLine`](https://skia.googlesource.com/skia/+/620de5ac9f6b/include/core/SkCanvas.h), [Direct2D `DrawLine`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-drawline) | A line remains a typed draw operation; Direct2D represents the ordinary solid stroke with no separate stroke-style object. | Record an ordinary solid Avalonia line directly and retain the general path/stroke expansion only for dashed lines. Keep the same caps, joins, transform, antialiasing, and device-space width policy. |
| [Skia Graphite graphics-pipeline key](https://skia.googlesource.com/skia/+/ca5481ebd0fb/src/gpu/graphite/GraphicsPipeline.h), [WebRender program cache](https://searchfox.org/firefox-main/source/gfx/webrender_bindings/src/program_cache.rs), [WebGPU render-pipeline contract](https://gpuweb.github.io/types/interfaces/GPURenderPipeline.html) | Expensive native pipelines have stable identities and are reused after selection; the frame path should select an existing pipeline rather than rebuild its textual description. | Front the existing owning pipeline cache with a value-keyed pointer selection cache. Ownership and release remain in the original cache, while steady-state selection avoids interpolated names and descriptor reconstruction. |
| [Skia Graphite resource ownership](https://skia.googlesource.com/skia/+/5fdfc51ef3e8/src/gpu/graphite/Resource.h), [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains), [WebRender GPU cache](https://searchfox.org/firefox-main/source/gfx/wr/webrender/src/gpu_cache.rs), [Vello](https://github.com/linebender/vello), and the [WebGPU buffer/bind-group contract](https://gpuweb.github.io/gpuweb/) | GPU resources have explicit ownership and reusable lifetimes; retained renderers allocate/cache resources on demand; a bind group captures a concrete buffer binding and range rather than following a later buffer replacement. | Reserve scene buffers for the ordinary retained workload, grow them geometrically only after measured demand exceeds capacity, recreate the two vector scene-state bind groups exactly when brush/gradient storage changes, and keep the grown buffers stable across retained frames. Text shaping/layout remains at the existing Parley/HarfBuzz-style CPU boundary. |
| [WebGPU pipeline-layout creation](https://gpuweb.github.io/gpuweb/#pipeline-layout-creation), [WebGPU bind-group compatibility](https://gpuweb.github.io/gpuweb/#bind-group-compatibility), [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains), [Win2D QuickStart](https://microsoft.github.io/Win2D/WinUI2/html/QuickStart.htm), and [Skia Graphite resource ownership](https://skia.googlesource.com/skia/+/5fdfc51ef3e8/src/gpu/graphite/Resource.h) | Pipelines created with the same explicit layout have a stable bind-group ABI; compatible resources are reusable across targets on one device, while render-target format and sample count remain pipeline state. Production engines keep device resources cache-owned and shared rather than cloning them per surface. | Give the primary and offscreen base pipelines one explicit resource-binding ABI. Share layouts and immutable bind groups across both targets, retain sample count in each render-pipeline key, atomically replace one shared bind group after buffer/atlas revision changes, and keep one dynamic mask bind group per texture. |
| [Skia Graphite resource ownership](https://skia.googlesource.com/skia/+/5fdfc51ef3e8/src/gpu/graphite/Resource.h), [WebRender texture cache](https://searchfox.org/firefox-main/source/gfx/wr/webrender/src/texture_cache.rs), and the [WebGPU texture descriptor](https://gpuweb.github.io/gpuweb/#dictdef-gputexturedescriptor) | Cached GPU images have explicit two-dimensional extents and residency; width and height are independent texture-allocation inputs, and normalized sampling addresses each axis against its own extent. | Grow the path atlas geometrically per axis from measured raster demand. Preserve existing texel coordinates during growth, normalize X and Y independently, and retain ProGPU's generation/retry contract rather than forcing a square allocation for a tall or wide live set. |
| [WebGPU object labels](https://gpuweb.github.io/gpuweb/#dom-gpuobjectbase-label) | Labels are diagnostic metadata and do not participate in rendering semantics. | Preserve readable encoder and command-buffer labels as static null-terminated UTF-8 data pinned for the synchronous call, avoiding per-frame managed/native string marshalling. |
| [Skia `SkSurface`](https://api.skia.org/classSkSurface.html) and [`SkPath::contains`](https://api.skia.org/classSkPath.html), [Direct2D offscreen interop](https://learn.microsoft.com/en-us/windows/win32/Direct2D/direct2d-and-direct3d-interoperation-overview), [Win2D offscreen drawing](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/offscreen-drawing), [`CanvasGeometry.FillContainsPoint`](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_Geometry_CanvasGeometry_FillContainsPoint_1.htm), and [`IDWriteTextLayout::HitTestPoint`](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextlayout-hittestpoint) | Rendering to a texture/surface and geometry or text hit testing are separate operations. An offscreen target used as an effect input has no implicit input tree or hit-test query. | Disable ProGPU's optional GPU hit-test index only on the sample's private offscreen effects compositor. Preserve the screen compositor policy and every render, atlas, DPI, effect, and texture operation. |
| [WebRender renderer and `hit_test` modules](https://searchfox.org/firefox-main/source/gfx/wr/webrender/src), [Vello render-to-texture architecture](https://github.com/linebender/vello), [Parley text layout](https://github.com/linebender/parley), and [HarfBuzz shaping/rendering boundary](https://harfbuzz.github.io/shaping-and-shape-plans.html) | WebRender keeps hit testing as a distinct scene service; Vello renders a scene directly to a target texture; Parley and HarfBuzz produce reusable text layout/shaping data rather than coupling raster output to hit testing. | Keep text shaping/layout and visible scene compilation unchanged. Do not build or upload a second spatial index for an offscreen texture that is never queried. |
| [Unicode UAX #9 revision 51](https://www.unicode.org/reports/tr9/tr9-51.html), [DirectWrite text analysis](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwrite-idwritetextanalyzer), [Skia Unicode interface](https://skia.googlesource.com/skia/+/a0fd12aac6b3/modules/skunicode/include/SkUnicode.h), [Skia bidi tests](https://skia.googlesource.com/skia/+/7a2127711a40/modules/skunicode/tests/SkUnicodeTest.cpp), [Firefox text-frame layout](https://searchfox.org/firefox-main/source/layout/generic/nsTextFrame.cpp), [Parley](https://github.com/linebender/parley), [Vello](https://github.com/linebender/vello), and [HarfBuzz buffer contract](https://harfbuzz.github.io/harfbuzz-hb-buffer.html) | UAX #9 permits an equivalent implementation that produces the specified result. DirectWrite and Skia resolve bidi before shaping/rendering; Firefox keeps embedding levels in text layout; Parley delegates Unicode analysis to ICU4X; HarfBuzz consumes direction-homogeneous runs and does not perform bidi; Vello begins at the already-laid-out scene boundary. | Recognize only the exhaustively verified level-zero ASCII case before invoking the full UAX resolver. Preserve fresh result ownership, explicit-RTL behavior, every non-ASCII path, run boundaries, shaping inputs, and the renderer boundary. |

Rejected alternatives are disabling color emoji, substituting monochrome symbols, lowering
glyph resolution, recreating typefaces per frame, clearing all caches after every draw, or
moving Unicode/OpenType shaping to the GPU. They either change output, increase CPU work,
destroy retained-scene reuse, or are incomplete for complex scripts and emoji sequences.

## Step log

### Step 0 — release baseline

Status: **complete**.

No source was changed. The measurements above establish the comparison point and identify
two independent Avalonia costs: a deterministic 192.1 MB managed color-font retention issue
and sustained native/GPU growth under the animated Custom Drawing workload.

### Step 1 — demand-resident Avalonia system fonts

Status: **complete**.

Implemented change:

1. Test compact `cmap` coverage before loading any fallback face.
2. Reuse ProGPU's tested glyph-resident `sbix` construction for system font files larger
   than 16 MiB. The compact face retains complete `cmap`, GSUB, GPOS, metrics, and other
   shaping tables while replacing the heavy bitmap table with one resident glyph.
3. Attach a typed file-backed bitmap source to that face. Any glyph index emitted later by
   shaping, including a different emoji or a sequence/ligature result, is read directly from
   the original `sbix` table without loading the whole font.
4. Keep compressed demand-loaded glyph records in a 16 MiB per-face LRU cache. Oversized
   single records remain transient; eviction never invalidates decoded pixels or GPU atlas
   entries.
5. Keep the stable Avalonia typeface and HarfBuzz face cached. No per-frame typeface,
   shaping-face, or layout construction was added.

#### Step 1 comparison

The same four fresh-process, 15-second Release measurements were repeated:

| Page | Physical before | Physical after | Change | Managed before | Managed after | Change |
|---|---:|---:|---:|---:|---:|---:|
| Buttons | 189.94 MiB | 200.14 MiB | +5.4% | 15.17 MiB | 15.18 MiB | +0.1% |
| TextBlock | 407.34 MiB | 230.53 MiB | **-43.4%** | 209.92 MiB | 28.68 MiB | **-86.3%** |
| Custom Drawing | 1,027.28 MiB | 398.88 MiB | **-61.2%** | 202.46 MiB | 17.84 MiB | **-91.2%** |
| Composition | 200.20 MiB | 194.14 MiB | -3.0% | 14.50 MiB | 14.49 MiB | -0.1% |

The unaffected Buttons/Composition retained heaps are unchanged. Their physical movement
(-3.0% to +5.4%) defines the run-to-run native/driver noise band for this short workload.
The affected pages no longer contain the 192,123,512-byte array. Their largest arrays are
the expected 1 MiB renderer buffers and decoded color-glyph pixels.

Focused verification:

- all seven `ProGpuFontManagerImplTests` pass, including a real macOS Apple Color Emoji
  test and a synthetic 32 MiB non-matching fallback candidate;
- the low-level glyph-resident regression test loads a second, initially absent `sbix`
  glyph on demand while the standalone shaping face remains below 1 MiB;
- the full ControlCatalog Release build succeeds;
- TextBlock renders its emoji sequences and Custom Drawing renders its plus/minus symbols
  through the same color-bitmap and GPU-atlas path.

The single 15-second process CPU samples moved from 3.4% to 1.9% for TextBlock, 0.9% to
0.6% for Buttons, and 0.4% to 0.5% for Composition. Custom Drawing moved from 19.0% to
26.8%, which is not accepted as a regression or gain from one sample: the page is an
unthrottled continuous animation, so step 2 adds frame-count and compositor timing
instrumentation and compares equal frame workloads.

### Step 2 — animated Avalonia native/GPU residency

Status: **complete**.

The font fix exposed command recording as the remaining avoidable managed churn. Avalonia
creates a fresh `DrawingContextImpl` for each render and the old path first recorded into
one command array, then appended every command into a second `DrawingVisual` array. The
animated page therefore allocated about 1.18 MiB each frame and repeatedly promoted large
command arrays.

Implemented change:

1. Rent one cleared command-recording context from each offscreen target and return it after
   rendering. Resource leases are released transactionally before reuse and concurrent
   recording still receives an independent context.
2. Compile the recorded command stream directly through a typed
   `IOwnedRenderCommandCache` visual instead of copying it with `Append`.
3. Preserve separate persistent contexts for cached layers. Reuse applies only to ordinary
   frame recording and cannot alias a retained layer.
4. Report recorded command count and capacity alongside compositor, path/glyph atlas, and
   GPU-residency telemetry.

#### Step 2 comparison

The matched 240-warm-up/1,200-frame Custom Drawing run produced:

| Metric | Before direct reuse | After direct reuse | Change |
|---|---:|---:|---:|
| Allocation/frame | 1,179,083 B | 65,179 B | **-94.5%** |
| Gen0 / Gen1 / Gen2 collections | 66 / 44 / 42 | 9 / 0 / 0 | Gen1/Gen2 eliminated |
| Wall throughput | 59.62 FPS | 59.85 FPS | +0.4% |
| Compile / upload / render | — | 0.121 / 0.187 / 0.332 ms | Within 60 Hz budget |
| Compositor | 0.515 ms | 0.640 ms | noisy sub-millisecond movement |
| Recorded commands / capacity | — | 352 / 512 | exact telemetry |
| Draws / vector vertices / path entries | 113 / 528 / 26 | 113 / 528 / 26 | unchanged |

A repeated run measured 0.640 ms compositor time with no wgpu validation errors. The
15-second forced-GC heap was 18.74 MiB and physical footprint was 379 MiB. Relative to the
release baseline, Custom Drawing is cumulatively down 63.1% physical and 91.2% retained
managed memory without changing its continuous animation.

An experimental O(1) list-set swap inside `Compositor.RenderOffscreenCore` was rejected and
fully reverted. A forced-GC dump proved those snapshot arrays were dead LOH history rather
than live retention, the experiment produced no meaningful live-heap gain, and its timing
was not better. The direct recording-context reuse addresses the actual per-frame allocation.

### Step 3 — zero-copy ordinary and bundled font payloads

Status: **complete**.

The WinUI Text & Documents forced-GC graph retained 146.58 MiB. Four complete system-font
payloads accounted for 48.46 MiB, while bundled Noto faces duplicated immutable bytes that
were already present in mapped assembly images. The rest was chiefly the intentional
20,000-paragraph benchmark document and its virtualized editor model; that workload was not
reduced.

Implemented change:

1. Raw SFNT/TTC file constructors retain a read-only memory-mapped view instead of a full
   managed `File.ReadAllBytes` array. Mapping uses delete-sharing, has stable pointer
   ownership, and remains alive with every face and variation instance.
2. Embedded raw fonts retain the runtime's `UnmanagedMemoryStream` over the assembly image.
   Inter and Noto therefore no longer allocate a second copy of the resource.
3. WOFF/WOFF2 continues through the existing normalization path because decompression
   necessarily creates a standalone SFNT payload. CFF fallback and variable-font parsing
   copy only when their existing byte-array parser is actually demanded.
4. `SfntFontFace` and `TtfFont.FontData` use `ReadOnlyMemory<byte>`. Only the selected compact
   `cmap` subtables are copied into managed memory to keep character lookup free of
   `MemoryManager` dispatch. Scalar metrics use the stable read-only address.
5. Avalonia no longer keeps a second per-file byte-array cache. Its typeface stream is a
   bounded read-only stream over the same font memory and is shared by both Avalonia 12 and
   the source-linked Avalonia 11 build.

The final large-face construction checks are:

| Face | Payload | Managed construction allocation | Hot lookup |
|---|---:|---:|---:|
| Hiragino Sans GB TTC | 23.52 MiB | 0.84 MiB selected metadata/`cmap` | 15.65–16.16 ms |
| Same face, whole managed array | 23.52 MiB | 23.52 MiB payload plus metadata | 15.62–16.63 ms |
| Embedded Noto CJK | 16.47 MiB | 0.23 MiB selected metadata/`cmap` | exact pinned payload/hash |

The lookup microcheck performs 1,000,000 repeated character-map and advance operations.
Mapped and array-backed storage are at parity after the compact-hot-table change.

#### Step 3 WinUI comparison

| Workload | Physical before | Physical after | Managed before | Managed after | Throughput before | Throughput after |
|---|---:|---:|---:|---:|---:|---:|
| Text & Documents | 573.08 MiB | 525.17 MiB (**-8.4%**) | 192.10 MiB | 129.47 MiB (**-32.6%**) | 67.61 FPS | 73.11 FPS |
| Typography & Scripts | 505.33 MiB | 446.10 MiB (**-11.7%**) | 87.07 MiB | 30.39 MiB (**-65.1%**) | 433.52 FPS | 433.29 FPS |
| Text Shaping Lab | 486.92 MiB | 385.35 MiB (**-20.9%**) | 79.58 MiB | 43.73 MiB (**-45.1%**) | 367.57 FPS | 434.22 FPS |

Text & Documents compile time is 2.1745 ms versus 2.1828 ms at baseline, and allocation is
199,543 versus 199,751 bytes/frame. Its forced-GC live heap is 81.78 MiB versus 146.58 MiB
(**-44.2%**). The complete 1.2–23.5 MiB font arrays are absent; its largest live object is
the unchanged 3.76 MiB benchmark text string. The final trace reports exactly 63 draws, 676
vector vertices, and 2,782 path entries, matching baseline.

The final fresh-process sweep repeated all 54 pages with the baseline's 120 warm-up and 300
measured frames. Average retained managed memory fell from 34.48 to 27.75 MiB (**-19.5%**)
and average physical footprint from 450.62 to 438.62 MiB (**-2.7%**). Average throughput
rose from 414.17 to 431.08 FPS (**+4.1%**) and average compile time fell from 0.415 to
0.292 ms (**-29.7%**). Allocation moved from 11,581 to 11,705 bytes/frame (+1.1%, 124
bytes/frame), within cross-process sampling noise; Text & Documents itself fell slightly.

#### Step 3 Avalonia comparison

The same source is used by Avalonia. Custom Drawing's forced-GC heap fell from 18.74 MiB to
16.11 MiB (**-14.0%**) while physical footprint remained in the same driver-noise band
(379–381 MiB). Two low-contention 1,200-frame runs measured 60.67 and 60.72 FPS with
0.550 and 0.519 ms compositor time. Later runs during heavy desktop GPU contention measured
56.72–58.66 FPS and 0.897–1.221 ms; the recorded workload and explicit residency remained
identical in all runs: 352 commands/capacity 512, 113 draws, 528 vector vertices, 26 path
entries, and unchanged atlas texture sizes.

### Step 4 — Avalonia platform steady-state allocations

Status: **complete**.

A backend-neutral ControlCatalog harness now runs the same pinned Avalonia source, page,
window size, warm-up, measured animation-frame count, compacting collections, and screenshot
capture against native `Avalonia.Skia` and `Avalonia.ProGpu`. It reports exact
`GC.GetTotalAllocatedBytes` deltas, retained managed/GC state, macOS physical footprint, CPU,
frame time, and ProGPU compositor/atlas residency where available.

The profile found three adapter-owned costs:

1. Solid rectangle, rounded-rectangle, and ellipse calls eagerly built a complete
   `PathGeometry` even when their solid or gradient brush did not need a clip path.
2. Silk.NET event pumping copied the complete window list on every loop iteration.
3. Every draw converted the same Avalonia solid brush and pen into new ProGPU objects.

The final implementation keeps primitive geometry lazy, publishes a copy-on-write immutable
window snapshot only when a window is registered or removed, and reuses solid styles through
a 256-entry value cache. The style key contains current packed color, opacity, thickness,
join, miter, and cap values, so mutating an Avalonia brush or pen selects a new converted
value. Cache overflow clears the bounded cache; retained commands keep their referenced
style alive independently.

The matched baseline is the Step 3 tree before these platform changes. Final values are
medians of three fresh Release processes, each with 180 warm-up and 300 measured frames:

| Page | Allocation/frame before | Allocation/frame after | Change | FPS before | FPS after |
|---|---:|---:|---:|---:|---:|
| Buttons | 6,961 B | 2,160 B | **-69.0%** | 62.47 | 62.68 |
| TextBlock | 18,137 B | 2,161 B | **-88.1%** | 62.56 | 62.69 |
| Custom Drawing | 63,010 B | 11,402 B | **-81.9%** | 58.09 | 60.11 |
| Composition | 11,626 B | 2,158 B | **-81.4%** | 62.69 | 62.66 |
| Four-page average | 24,934 B | 4,470 B | **-82.1%** | — | — |

Native Skia measured 2,179–2,183 B/frame on the three ordinary pages and 7,261 B/frame on
Custom Drawing. ProGPU now matches the ordinary-page allocation floor and reduced the
animated-page gap from 8.7x to 1.6x. The remaining animated allocations are chiefly
Avalonia composition scheduling plus path/stroke caches for changing custom geometry; they
were not hidden by disabling invalidation or animation.

Retained managed memory was unchanged after compacting GC, as expected for a churn
optimization. macOS physical-footprint medians moved in both directions inside the observed
driver/process noise band. Explicit ProGPU residency stayed fixed on Custom Drawing: 352
recorded commands with capacity 512, 113 draws, 528 vector vertices, 226 text vertices, 26
path entries, a 4 MiB path atlas, and 256 KiB each for glyph, color-glyph, and coverage
staging storage. Compile and render medians improved from 0.191/0.545 ms to 0.121/0.350 ms.

An allocation trace independently measured 19,685 B/frame before solid-style reuse and
11,378 B/frame after it. The sampled `ProGPU.Vector.SolidColorBrush` hot spot disappeared.
The earlier full baseline trace was 65,764 B/frame; the final trace is **82.7% lower**.

Quality and behavior checks:

- Buttons, TextBlock, and Composition screenshots are byte-identical to their pre-change
  ProGPU baselines.
- Custom Drawing is time-dependent; visual inspection confirms the same clipped scene,
  strokes, ellipses, text, transforms, and antialiasing at a later animation phase.
- Solid-style mutation tests prove that color changes do not reuse stale converted values.
- Scene/image brush clipping still takes the general geometry path.
- No wgpu validation, uncaptured, or fatal errors occurred in the full measured matrix.
- The rejected macOS Metal-only instance mask and lazy chart-pipeline experiments were
  fully reverted because repeated measurements did not establish a memory win.

A fresh 54/54 WinUI gallery sweep after the Avalonia-only changes completed without a
wgpu error. Relative to the isolated Step 3 sweep, average managed retention was stable
within 0.06%, measured allocation fell 1.76%, and physical footprint moved 1.2% inside
the observed cross-process/driver noise band. Its throughput samples are not used as a
regression comparison because concurrent Roslyn, MSBuild/test, macOS media-analysis, and
WindowServer processes consumed several hundred percent CPU. A targeted four-page repeat
remained similarly contended. The valid isolated WinUI performance comparison is therefore
the Step 3 54-page result above; Step 4 changes only the Avalonia adapter, native ControlCatalog
benchmark, and Silk.NET event pump and do not enter the WinUI rendering path.

### Steps 5–12 — retained geometry and bounded renderer hot paths

Status: **complete**.

The Step 4 trace still showed avoidable work on the continuously invalidated Custom Drawing
page. The continuation keeps the public drawing behavior and recorded workload unchanged
while making the following resources retained or stack-only:

1. `GeometryImpl` owns one lazily built render-command geometry cache. Repeated draws are
   average `O(1)` lookups; every `StreamGeometryImpl` mutation clears the cache before the
   next `O(N)` rebuild for `N` segments.
2. An ordinary solid line remains a typed line command. Dashed lines retain the existing
   general path/stroke cache because their dash expansion depends on path length and style.
3. Stroke-direction selection uses fixed one-, two-, and three-candidate overloads. It
   preserves candidate order and numerical checks with fixed `O(1)` work and no temporary
   `params` array.
4. Each UI thread pools at most four cleared drawing-context state sets. A state whose
   opacity, clip, render-options, or text-options stack ever grows beyond 64 entries is not
   retained, so an unusually deep frame cannot permanently inflate the thread-local pool.
5. Pipeline selection is keyed by draw type, typed vector specialization, blend mode,
   target kind/format, alpha mode, and mask presence. The front cache stores only the
   already-owned native pointer; the existing pipeline cache remains the sole owner and
   releases every native pipeline exactly once. Selection is average `O(1)` and no longer
   formats pipeline names after the first creation.
6. Unequal-radius rounded-rectangle fallback geometry uses a value-keyed cache capped at
   256 entries. Overflow clears this auxiliary lookup; commands and the path atlas retain
   their own valid references independently.
7. Surface-to-context resolution first checks the thread's current context in `O(1)`, then
   scans the locked active registry in `O(C)` for `C` contexts without allocating an array.
   Disposal still removes the context and invalidates all adapter resources.
8. Per-frame encoder and command-buffer labels remain readable, null-terminated UTF-8
   labels, but are backed by static literal data pinned only for the synchronous WebGPU
   call. No label string, unmanaged wrapper, or global tracking-node allocation is needed.

All caches are either mutation-invalidated, compositor-owned, or explicitly bounded. No
mutable drawing data is accepted as reusable without its existing invalidation contract.
No foreign implementation source was copied or adapted.

#### Allocation progression

Values are medians of three fresh Release processes with 180 warm-up and 300 measured
frames. Each row records exactly 352 commands in capacity 512, 113 draws, 528 vector
vertices, 226 text vertices, and 26 resident paths.

| Step | Change | Custom Drawing allocation/frame | Change from Step 4 |
|---|---|---:|---:|
| 4 | Typed primitives and style reuse | 11,402 B | — |
| 5 | Retained geometry-command cache and typed solid lines | 8,599 B | **-24.6%** |
| 6 | Fixed stroke-direction selection | 8,313 B | **-27.1%** |
| 7 | Bounded drawing-state pool | 7,854 B | **-31.1%** |
| 8 | Value-keyed general pipeline selection | 7,174 B | **-37.1%** |
| 9 | Bounded unequal-radius rounded-path reuse | 6,497 B | **-43.0%** |
| 10 | Solid primitive pipeline front cache | 6,284 B | **-44.9%** |
| 11 | Allocation-free surface-context lookup | 6,220 B | **-45.4%** |
| 12 | Static UTF-8 WebGPU command labels | 5,997 B | **-47.4%** |

The final four-page result is:

| Page | Release baseline | Step 4 | Step 12 | Final change |
|---|---:|---:|---:|---:|
| Buttons | 6,961 B/frame | 2,160 B/frame | 2,158 B/frame | **-69.0%** |
| TextBlock | 18,137 B/frame | 2,161 B/frame | 2,160 B/frame | **-88.1%** |
| Custom Drawing | 63,010 B/frame | 11,402 B/frame | 5,997 B/frame | **-90.5%** |
| Composition | 11,626 B/frame | 2,158 B/frame | 2,158 B/frame | **-81.4%** |
| Four-page average | 24,934 B/frame | 4,470 B/frame | 3,118 B/frame | **-87.5%** |

A fresh native Avalonia/Skia run on the same pinned ControlCatalog source measured a
7,015 B/frame Custom Drawing median. ProGPU is 14.5% lower on managed allocation in this
workload. Native Skia and ProGPU use different presentation cadences on this machine, so
their FPS values are deliberately not compared.

The final exact ProGPU median retains 21.79 MiB managed and a 367.88 MiB macOS physical
footprint. The latter remains in the established native-driver noise band. Step 5 was
measured before later desktop contention and improved compile/render/compositor medians
from 0.121/0.350/0.682 ms to 0.103/0.281/0.546 ms. Later steps performed less bounded CPU
work but ran while unrelated desktop processes consumed substantial CPU/GPU time; their
FPS and sub-millisecond timings are recorded in the artifacts but are not treated as an
isolated performance comparison. The final contended median is 58.41 FPS with
0.158/0.292/0.462 ms compile/upload/render and 0.912 ms total compositor time.

The shared compositor changes were also measured across a fresh 54/54 WinUI page sweep.
Compared with the preceding final sweep, average allocation fell from 11,705 to
8,036 B/frame (**-31.3%**), compile time from 0.292 to 0.240 ms (**-17.8%**),
render time from 0.138 to 0.120 ms (**-13.3%**), and total compositor time from
0.475 to 0.401 ms (**-15.5%**). Average FPS is effectively unchanged at 430.94 versus
431.08 (-0.03%). Retained managed memory changed by -0.15%; physical footprint changed
by +1.71% inside the established process/driver noise band. Frame-budget misses fell from
43 to 12, compile-budget misses remained zero, and all pages completed without a wgpu
validation error.

The randomized allocation trace is qualitative rather than a byte-total benchmark. In the
final trace, none of the sampled measurement stacks contain ProGPU or `SilkMarshal`;
remaining samples are Avalonia render-data nodes, dispatcher operations, composition
transport, and task scheduling. Exact allocation comes from
`GC.GetTotalAllocatedBytes(precise: true)` across all 300 measured frames.

Quality and behavior checks:

- all three Buttons, TextBlock, and Composition screenshots are byte-identical to their
  retained Step 5 reference images;
- Custom Drawing remains time-dependent, and visual inspection shows the same clipping,
  transforms, solid/dashed strokes, ellipses, text, antialiasing, and animation;
- command, draw, vertex, path, path-atlas, glyph-atlas, and staging-storage counts are
  unchanged in every measured run;
- focused tests cover solid versus dashed line caching and geometry-cache reuse followed
  by mutation invalidation;
- no wgpu validation, uncaptured, fatal, or other errors occur in any final benchmark log.

### Step 13 — remove the unconsumed offscreen effects hit-test index

Status: **complete**.

The WinUI window compositor already disables the optional GPU hit-test index because
`InputSystem` owns CPU visual-tree hit testing. A fresh allocation trace found a second,
sample-private compositor used only to render the animated gear into an RGBA texture for
blur, shadow, and image-repeat effects. That offscreen target has no pointer/input surface
and no caller reads its `LastHitTestIndex`, but it still used the default option and rebuilt
and uploaded a complete spatial index on every animated frame.

The sample now creates that compositor with `EnableGpuHitTesting = false`. This preserves
the exact visual tree, animation, transforms, clipping, DPI, MSAA, vector/text compilation,
atlas work, texture dimensions, effect dispatches, and final screen composition. It removes
only the unused side product: quadtree construction plus copies/uploads of `P` primitives,
`S` path segments, and `N` nodes. The eliminated CPU work is `O(P * D + S + N)` and the
eliminated transient/storage footprint is `O(P + S + N)`, with the existing maximum tree
depth `D = 8`. Render complexity and output storage are unchanged.

The discovery trace and the first matched after trace use fresh Release processes with 120
warm-up and 300 measured frames. Three additional fresh after processes establish the
post-change median:

| Page | Before | Step 13 result | Change | FPS before / after | Compositor before / after |
|---|---:|---:|---:|---:|---:|
| Compute FX | 23,723 B/frame | 1,257 B/frame | **-94.7%** | 349.52 / 412.42 | 0.140 / 0.130 ms |
| Image & Buttons | 15,235 B/frame | 1,421 B/frame | **-90.7%** | 358.31 / 404.80 | 0.133 / 0.121 ms |
| Font Glyph Browser control | 40,760 B/frame | 40,980 B/frame | +0.5%, noise | 364.52 / 330.78 | Different animated workload timing; allocation control only |

The affected before/after runs retain exactly 37/22 draws, 320/232 vector vertices, and
539/604 text vertices respectively. All after repetitions have zero frame-budget and
compile-budget misses. The Font Glyph Browser does not use the offscreen effects compositor
and its effectively unchanged allocation confirms the scope.

The Compute FX headless test now asserts that neither a CPU nor device hit-test index is
created. Its rendered PNG remains byte-identical to the retained reference:
`9005419572d61c016f4b1b88f19be095956c788982213348c9fe83fb7b9058fb`.
No Step 13 benchmark log contains a WebGPU validation, uncaptured, or fatal error.

Artifacts:

- `artifacts/memory-optimization/step13-current-audit`
- `artifacts/memory-optimization/step13-offscreen-hit-test-disabled`
- `artifacts/memory-optimization/step13-repeat-1`
- `artifacts/memory-optimization/step13-repeat-2`
- `artifacts/memory-optimization/step13-repeat-3`

### Step 14 — static UTF-8 glyph-rasterizer labels

Status: **complete**.

The compositor labels corrected in Step 12 did not cover `GlyphAtlas`. Every raster batch
still allocated and registered unmanaged strings for its encoder and command-buffer labels;
the immediate oversized-glyph fallback did the same. All four fixed labels now use
null-terminated UTF-8 literals pinned only for the synchronous WebGPU creation/finish call.
Label text and native object lifetime are unchanged, while the diagnostic-only managed,
unmanaged, and global memory-registration work is removed.

The glyph browser exact allocation remained neutral at 40,980 versus 41,112 B/frame
(+0.3%, noise), while the after trace contains no sampled `SilkMarshal` stack. This small
cleanup is therefore accepted as allocation-safe, not claimed as a measurable whole-frame
gain. Focused batch wrap/checkpoint/coalescing tests preserve queue submission and atlas
visibility behavior.

Artifact: `artifacts/memory-optimization/step14-glyph-labels`.

### Step 15 — geometric glyph-instance buffer growth

Status: **complete**.

`CompileTextCommand` knows each shaped layout's glyph count and reserves the final
`GlyphInstance` count before appending. The old helper assigned `List.Capacity` to the exact
required count. A scrolling/virtualized page whose visible glyph total increased gradually
therefore copied a nearly full array repeatedly. The helper now uses the list's geometric
`EnsureCapacity` policy. It still guarantees capacity before the same `O(G)` glyph
compilation, but amortizes growth to `O(G)` total copies across increasing scene sizes and
retains at most the normal geometric spare capacity. The buffer stays compositor-owned and
is cleared, not discarded, between compilations.

A tightly paired three-process Release comparison used 120 warm-up and 300 measured
Font Glyph Browser frames:

| Metric | Exact-capacity median | Geometric-growth median | Change |
|---|---:|---:|---:|
| Allocation/frame | 40,651 B | 37,280 B | **-8.3%** |
| Sampled `GlyphInstance[]` bytes | 1,275,327 B in the discovery trace | 230,402 B | **-81.9%** |
| FPS | 354.49 | 334.26 | Ranges overlap under desktop contention |
| Compile time | 1.148 ms | 1.421 ms | Non-repeatable contention |
| Render time | 0.221 ms | 0.232 ms | Within run noise |
| Frame/compile budget misses | 0 / 0 | 0 / 0 | Unchanged |

The timing ranges overlap substantially: exact-capacity runs span 295–373 FPS and optimized
runs span 256–398 FPS, with compile medians moving with the same external contention. The
change affects only rare list growth, not per-glyph compilation or GPU upload, so no
repeatable throughput regression is established. All runs retain 111 draws, 720 vector
vertices, 1,008 text vertices, five populated glyph-browser state samples, the same visible
range (432–1037), and zero atlas clears, evictions, or resets.

Artifacts:

- `artifacts/memory-optimization/step15-text-capacity-1`
- `artifacts/memory-optimization/step15-text-capacity-2`
- `artifacts/memory-optimization/step15-text-capacity-3`
- `artifacts/memory-optimization/step15-paired-baseline-1` through `-3`
- `artifacts/memory-optimization/step15-paired-optimized-1` through `-3`

### Step 16 — level-zero ASCII bidi fast path

Status: **complete**.

The Font Glyph Browser allocation trace showed repeated `Uax9Resolver` construction for
ordinary ASCII labels. UAX #9 rules P2–P3, W1–W7, N0–N2, I1, and L1 resolve every ASCII
code point to level zero when the paragraph base level is zero. `BidiParagraph.Resolve`
now recognizes exactly that narrow case with one linear scan, then creates the same
level-zero UTF-16 projection and single logical run directly. Explicit RTL paragraphs and
every paragraph containing a non-ASCII code unit continue through the complete resolver.

The scan is `O(N)` time and `O(1)` workspace. Its fresh `sbyte[N]` level array and one-run
array are the required retained outputs, so nonempty result ownership is unchanged. Empty
LTR and RTL paragraphs use shared immutable instances containing only `Array.Empty`
storage. Run construction for the full resolver path now uses a two-pass exact-size array
instead of a temporary `List<BidiRun>` followed by `ToArray`.

Correctness is checked against the complete resolver for all 16,384 two-character ASCII
combinations under both explicit LTR and detected base direction: 32,768 paragraph-level,
UTF-16-level, and run comparisons. Focused cases also prove fresh nonempty array ownership,
shared empty results, and that explicit RTL ASCII still takes the full UAX path and retains
nonzero embedding levels. The five affected headless text pages render successfully.

Three fresh Release processes on each side used the same Font Glyph Browser workload:
180 warm-up frames, 600 measured frames, retained traces, visible range 576–1901, 111
draws, 720 vector vertices, 1,009 text vertices, 301 glyph-atlas generation changes and
evictions, and no atlas clear or path reset.

| Metric | Fresh baseline median | Step 16 median | Change |
|---|---:|---:|---:|
| Allocation/frame | 34,998 B | 28,728 B | **-17.9%** |
| Sampled measurement allocation | 20,574,564 B | 17,193,569 B | **-16.4%** |
| Sampled `Uax9Resolver` top-stack bytes | 2,971,016 B | 0 B | **eliminated** |
| Gen0 collections | 2 | 1 | One fewer collection |
| Wall throughput | 358.94 FPS | 407.74 FPS | No regression observed |
| Compile time | 0.710 ms | 0.823 ms | Sub-millisecond run variance |
| Frame / compile budget misses | 0 / 0 | 0 / 0 | Unchanged |

The bidi change does not enter compositor compilation, and the faster wall result does not
justify attributing a throughput gain under varying desktop contention. The small compile
median movement is reported rather than hidden; all six processes retained identical work
and stayed below one millisecond without a budget miss. No benchmark log contains a WebGPU
validation, uncaptured, or fatal error.

Artifacts:

- `artifacts/memory-optimization/step16-bidi-baseline-1` through `-3`
- `artifacts/memory-optimization/step16-bidi-optimized-1` through `-3`

### Step 17 — allocation-free identity visual order

Status: **complete**.

UAX #9 rule L2 is an identity mapping when a broken line contains no odd embedding level.
The compatibility helper still returns a complete `int[N]` map, but WinUI's layout hot path
now requests a map only when reordering is necessary. A null internal map means logical
index equals visual index. Mixed and RTL lines create and retain the same exact map as
before, including its use during justification; LTR lines iterate their existing character
list directly. Work remains `O(N)` and output is unchanged, while identity-map storage
falls from `O(N)` to `O(1)`.

The Step 16 optimized runs are the exact pre-change baseline. Three fresh Step 17 processes
used the same 180 warm-up and 600 measured frames:

| Metric | Step 16 median | Step 17 median | Change |
|---|---:|---:|---:|
| Allocation/frame | 28,728 B | 28,324 B | **-1.4%** |
| Sampled measurement allocation | 17,193,569 B | 16,272,137 B | **-5.4%** |
| Sampled visual-order stacks | 307,280 B | 0 B | **eliminated** |
| Wall throughput | 407.74 FPS | 415.43 FPS | No regression observed |
| Compile time | 0.823 ms | 0.751 ms | No regression observed |
| Frame / compile budget misses | 0 / 0 | 0 / 0 | Unchanged |

The focused bidi suite includes direct identity and mixed-order coverage. All three runs
retain the same visible range, draws, vector vertices, atlas generations/evictions, and
absence of atlas clears and path resets. One of six runs reports 1,008 rather than 1,009
text vertices because the benchmark scroll endpoint crosses one glyph boundary; this is
the same existing run-to-run endpoint variation, not dropped content. No WebGPU error is
present.

Artifacts:

- `artifacts/memory-optimization/step17-identity-order-optimized-1` through `-3`

### Step 18 — bounded per-line bidi-level workspace

Status: **complete**.

Rich-text layout projected retained paragraph levels into a new `sbyte[N]` array for every
broken line, used it synchronously for cluster shaping and visual ordering, then discarded
it. Lines of at most 256 positioned characters now use a bounded stack span; longer lines
retain the existing heap allocation. The helper verifies exact line/storage length, fills
every element, performs the same UAX #9 L1 trailing-whitespace reset, and cannot escape the
layout call. Time remains `O(N)`, temporary heap storage becomes `O(1)` for ordinary short
lines, and stack use is capped at 256 bytes per active layout call.

A temporary process-level switch selected the previous heap path without changing the
binary. Six heap and six bounded-stack processes were run in alternating order, then in
reverse alternating order, each with 180 warm-up and 600 measured frames. The switch was
removed from the retained source after measurement.

| Metric | Same-binary heap median | Same-binary stack median | Change |
|---|---:|---:|---:|
| Allocation/frame | 28,029 B | 27,953 B | **-0.3%** |
| Pairwise allocation delta | — | -64 B/frame median | Four of six pairs lower |
| Sampled measurement allocation | 16,846,563 B | 15,703,711 B | **-6.8%** |
| Wall throughput | 375.12 FPS | 396.04 FPS | No regression observed |
| Compile time | 0.952 ms | 1.009 ms | +0.057 ms, run noise |
| Total compositor time | 1.328 ms | 1.406 ms | +0.078 ms, run noise |
| Frame / compile budget misses | 2 / 0 | 0 / 0 | No stack-mode misses |

The exact gain is deliberately reported as small. Aggregate wall throughput is favorable,
while the sub-tenth-millisecond compile/compositor medians move in the opposite direction
under external desktop load; neither timing movement is claimed as causal. All twelve runs
retain the same visible range, 111 draws, 720 vector vertices, 1,009 text vertices, 301
glyph-atlas generation changes and evictions, and no atlas clear or path reset. Earlier
retained traces show the sampled `GetLineBidiLevels` heap stack falling from 307,256 B to
zero. No WebGPU validation, uncaptured, or fatal error occurs.

The current 27,953 B/frame median is **20.1%** below the fresh 34,998 B/frame pre-Step-16
baseline.

Artifacts:

- `artifacts/memory-optimization/step18-bidi-line-stack-optimized-1` through `-3`
- `artifacts/memory-optimization/step18-same-binary-heap-1` through `-3`
- `artifacts/memory-optimization/step18-same-binary-stack-1` through `-3`
- `artifacts/memory-optimization/step18-same-binary-reverse-heap-1` through `-3`
- `artifacts/memory-optimization/step18-same-binary-reverse-stack-1` through `-3`
- `artifacts/memory-optimization/step18-final-retained`

### Step 19 — indexed rich-inline traversal

Status: **complete**.

The Step 17 trace showed boxed `RichElementCollection<T>` enumerators in recursive inline
length and line-break scans. The retained implementation changes only those recursive
internal methods from interface-based `foreach` traversal to indexed traversal over the
existing `RichElementCollection<Inline>`. Logical tree order, recursion, estimated-height
policy, invalidation, shaping, layout output, and rendering are unchanged. Work remains
`O(N)` for `N` retained inline nodes, but traversal workspace is `O(1)` with no boxed
enumerator per nested collection.

An earlier broader experiment also changed estimated-height loops. It showed the allocation
signal but was fully reverted because unrelated compiler/security load prevented a paired
timing comparison. The accepted narrower change used a temporary process-level switch for
only recursive length/line-break scans. Six enumerable and six indexed processes ran from
the same binary in alternating and reverse-alternating order, each with 180 warm-up and 600
measured frames. The switch and enumerable baseline code were then removed.

| Metric | Same-binary enumerable median | Same-binary indexed median | Change |
|---|---:|---:|---:|
| Allocation/frame | 28,237 B | 27,890 B | **-1.2%** |
| Pairwise allocation delta | — | -447 B/frame median | All six pairs lower |
| Sampled measurement allocation | 17,512,759 B | 17,356,544 B | **-0.9%** |
| Wall throughput | 329.38 FPS | 345.79 FPS | **+5.0%** |
| Compile time | 1.503 ms | 1.215 ms | **-19.2%** |
| Layout time | 0.1060 ms | 0.1068 ms | Stable |
| Total compositor time | 2.016 ms | 1.705 ms | **-15.4%** |
| Frame / compile budget misses | 6 / 0 | 3 / 0 | Improved / unchanged |

Every run retains the same visible range, 111 draws, 720 vector vertices, 1,009 text
vertices, 301 glyph-atlas generation changes and evictions, and no atlas clear or path
reset. No WebGPU validation, uncaptured, or fatal error occurs. The current 27,890 B/frame
median is **20.3%** below the fresh 34,998 B/frame pre-Step-16 baseline.

Artifacts:

- Reverted exploratory run:
  `artifacts/memory-optimization/step19-indexed-rich-inline-1` through `-3`
- `artifacts/memory-optimization/step19-same-binary-enumerable-1` through `-3`
- `artifacts/memory-optimization/step19-same-binary-indexed-1` through `-3`
- `artifacts/memory-optimization/step19-same-binary-reverse-enumerable-1` through `-3`
- `artifacts/memory-optimization/step19-same-binary-reverse-indexed-1` through `-3`
- `artifacts/memory-optimization/step19-final-retained`

### Step 20 — indexed virtualized height estimation

Status: **complete**.

The retained trace still attributed allocation to boxed `RichElementCollection<T>`
enumerators inside `EstimateBlockHeight`. This method supplies provisional heights for
offscreen rich-document paragraphs, lists, and tables so virtualization can assign offsets
without laying out every block. The retained implementation now traverses the existing
inline, list-item, row, and cell collections by index. It preserves the previous
one-level maximum-font scan, line-break and embedded-content policies, formulas, block
order, cache invalidation, and visible layout. Work remains `O(N)` for `N` visited retained
nodes with `O(1)` traversal workspace.

A temporary process-level switch selected the former enumerable implementation from the
same Release binary. Three enumerable-then-indexed pairs and three reverse-order pairs each
ran 180 warm-up and 600 measured frames. The switch and former implementation were removed
after the result was accepted.

| Metric | Same-binary enumerable median | Same-binary indexed median | Change |
|---|---:|---:|---:|
| Allocation/frame | 27,721 B | 27,441 B | **-1.0%** |
| Pairwise allocation delta | — | -294 B/frame median | Five pairs lower; one +12 B tie |
| Sampled measurement allocation | 16,779,435 B | 16,032,211 B | **-4.5%** |
| Wall throughput | 403.21 FPS | 401.58 FPS | -0.4%, noise band |
| Compile time | 0.914 ms | 0.937 ms | +0.023 ms, noise band |
| Layout time | 0.0753 ms | 0.0664 ms | -0.0089 ms |
| Total compositor time | 1.242 ms | 1.277 ms | +0.035 ms, noise band |
| Frame / compile budget misses | 0 / 0 | 1 / 0 | No compile misses |

Every run retains the same visible range, 111 draws, 720 vector vertices, 1,009 text
vertices, 301 glyph-atlas generation changes and evictions, and no atlas clear or path
reset. The retained final trace reports 27,543 B/frame. Its lower wall throughput is
explained by 3.773 ms average surface acquisition under external load; compile, layout,
rendering work, and all workload counters remain in family. No WebGPU validation,
uncaptured, or fatal error occurs. The current same-binary median is **21.6%** below the
fresh 34,998 B/frame pre-Step-16 baseline.

Artifacts:

- `artifacts/memory-optimization/step20-same-binary-enumerable-1` through `-3`
- `artifacts/memory-optimization/step20-same-binary-indexed-1` through `-3`
- `artifacts/memory-optimization/step20-same-binary-reverse-enumerable-1` through `-3`
- `artifacts/memory-optimization/step20-same-binary-reverse-indexed-1` through `-3`
- `artifacts/memory-optimization/step20-final-retained`

### Step 21 — demand-sized core scene buffers for Avalonia

Status: **complete**.

Every compositor previously reserved the maximum brush and gradient-stop storage at
construction: 8,192 `GpuBrush` records at 256 bytes and 65,536 `GpuGradientStop` records at
32 bytes, or exactly 4 MiB before a scene contained a brush. The default core reservation
is now 64 brushes and 512 stops, 16 KiB each. Both limits remain unchanged. When a retained
scene exceeds either initial capacity, the owning buffer grows geometrically to at least
the observed demand and both onscreen/offscreen vector bind groups are recreated against
the new concrete WebGPU buffer ranges. A stable second render retains those buffers and
bind groups; it does not resize again.

The Avalonia adapter also selects 1,024 initial vertices and 1,536 initial indices instead
of the general-purpose 16,384/24,576 profile. Avalonia performs input hit testing through
its own visual tree and no Avalonia backend code consumes ProGPU's optional compiled GPU
hit-test index, so that duplicate index is disabled for Avalonia compositors. Compiled
scene reuse, visible rendering, CPU geometry hit testing, paths, glyphs, DPI, and atlas
policies are unchanged.

Exact per-compositor persistent scene-buffer reservations are:

| Storage | Former reservation | Current Avalonia reservation | Change |
|---|---:|---:|---:|
| Brush + gradient storage | 4,194,304 B | 32,768 B | **-4,161,536 B (-99.2%)** |
| Vector/text/texture mesh buffers | 3,604,480 B | 225,280 B | **-3,379,200 B (-93.7%)** |
| Uniform buffer | 208 B | 208 B | unchanged |
| **Total** | **7,798,992 B (7.44 MiB)** | **258,256 B (252.2 KiB)** | **-7,540,736 B (-96.7%, -7.19 MiB)** |

These are exact requested/allocated WebGPU buffer sizes, not an inference from process
RSS. The existing measured Custom Drawing workload records 352 commands, 113 draws, 528
vector vertices, 226 text vertices, and 26 paths, so its mesh demand remains within the
compact profile. The ControlCatalog benchmark now emits total scene-buffer bytes, separate
brush and gradient buffer bytes, active brush/stop counts, and the GPU-hit-testing flag so
future live captures show any workload-specific growth directly.

The growth regression begins with four brushes, four stops, 16 vertices, and 24 indices,
then renders 300 distinct two-stop gradients. It verifies correct pixels through the
normal headless render path, sufficient grown vector/style capacities, exact active counts,
disabled hit-index compilation, and unchanged buffer sizes on the second retained render.
Core option tests cover exact initial reservations. Avalonia unit coverage pins the compact
profile and confirms that GPU hit testing remains disabled.

The initial restricted pass could not connect GUI processes to WindowServer or open the
diagnostics Unix socket used by `dotnet-trace`, so this step first accepted only exact GPU
reservation and headless retained-render evidence. Step 22 records the subsequent
unrestricted same-binary ControlCatalog comparison using these counters.

### Step 22 — lazy core effect pipelines

Status: **complete**.

`Compositor` owns a `ComputeAccelerator` even when a backend never requests a blur, shadow,
morphology, color filter, blend, displacement, convolution, or lighting pass. Construction
previously compiled and retained twelve WGSL shader modules and twelve compute pipelines,
acquired four blur bind-group layouts, and allocated three persistent parameter buffers
totalling 96 bytes. The Avalonia renderer does not route any operation through these core
effect methods: Avalonia acrylic uses its typed backdrop-material extension, box shadows
remain typed draw commands, and `IDrawingContextImplWithEffects` currently maps only blend
state. The eager resources were therefore dead weight on every Avalonia compositor.

Each effect family is now created on its first actual call:

- Gaussian blur creates only its horizontal/vertical pair, two layouts, and two 16-byte
  parameter buffers.
- sharp shadow creates one pipeline; blurred shadow creates only its two-pass pair, two
  layouts, and one 64-byte retained parameter buffer.
- morphology, image blend, color table, arithmetic composite, displacement, convolution,
  lighting, nonlinear filtering, and magnification each create only their requested
  pipeline.
- the existing equal-radius combined blur/shadow specialization remains independently
  lazy.

The cache retains every created family, so the first-use compile is paid once and stable
frames select the existing pipeline. A GPU regression executes all sixteen possible
effect pipelines, checks the pipeline count after every family, verifies blur output, and
proves a second call does not grow the cache or parameter storage. The existing Compute FX
and Framework Effects headless pages render successfully.

A temporary process-level switch recreated the former eager effect set and full scene
buffers from the same Release binary. It was removed after three legacy and three optimized
fresh-process Custom Drawing runs, each with 240 warm-up and 600 measured frames:

| Metric | Legacy median | Optimized median | Change |
|---|---:|---:|---:|
| Persistent scene buffers | 7,798,992 B | 258,256 B | **-7,540,736 B (-96.7%)** |
| Unused effect shaders / pipelines | 12 / 12 | 0 / 0 | **eliminated** |
| Unused retained effect layouts / parameters | 4 / 96 B | 0 / 0 B | **eliminated** |
| Allocation/frame | 6,022 B | 6,022 B | unchanged |
| Retained managed memory | 21.81 MiB | 21.63 MiB | **-0.18 MiB (-0.8%)** |
| Working set | 236.39 MiB | 235.09 MiB | **-1.30 MiB (-0.5%)** |
| Wall throughput | 56.39 FPS | 56.40 FPS | unchanged |
| Compile time | 0.233 ms | 0.226 ms | no regression |
| Total compositor time | 1.284 ms | 1.260 ms | no regression |

The six physical-footprint samples span 359.91–374.94 MiB optimized and
366.78–368.72 MiB legacy, an incoherent Metal/WindowServer residency band, so no physical
footprint gain is claimed from three pairs. Every run retains exactly 352 commands,
113 draws, 528 vector vertices, 226 text vertices, 26 paths, 11 active brushes, no
gradients, no effect pipeline, and no GPU hit-test index. No wgpu validation, uncaptured,
or fatal error occurs.

Artifacts:

- `artifacts/memory-optimization/step22-lazy-effects/legacy-1.json` through `legacy-3.json`
- `artifacts/memory-optimization/step22-lazy-effects/optimized-1.json` through `optimized-3.json`
- `artifacts/memory-optimization/step22-lazy-effects/custom-drawing.json`
- `artifacts/memory-optimization/step22-lazy-effects/custom-drawing.png`

### Step 23 — lazy core chart pipelines

Status: **complete**.

The core compositor also compiled the line-chart and scatter-chart shaders and both
onscreen/offscreen pipeline variants during construction. Avalonia ControlCatalog does not
record either chart command, yet every Avalonia compositor retained those two shader
modules, four render pipelines, and two pipeline-derived bind-group layouts.

Both chart families are now independently demand-created after a valid chart draw reaches
the render pass. Line charts create their shader, two pipelines, and layout on first line
use; scatter charts do the same only on first scatter use. The owning pipeline cache keeps
them for subsequent frames. A dedicated GPU regression starts with the ordinary
three-shader/eight-pipeline core, renders real line and scatter commands, verifies visible
output and the resulting five-shader/twelve-pipeline cache, then proves a second frame
retains exactly the same counts.

A temporary same-binary switch restored eager chart creation and was removed after three
eager and three lazy Custom Drawing processes, each with 240 warm-up and 600 measured
frames:

| Metric | Eager median | Lazy median | Change |
|---|---:|---:|---:|
| Scene shader modules | 5 | 3 | **2 unused modules eliminated** |
| Scene render pipelines | 14 | 10 | **4 unused pipelines eliminated** |
| Chart bind-group layouts | 2 | 0 | **2 unused layouts eliminated** |
| Allocation/frame | 6,028 B | 6,022 B | effectively unchanged |
| Retained managed memory | 21.632 MiB | 21.622 MiB | -0.010 MiB |
| Working set | 235.28 MiB | 234.14 MiB | -1.14 MiB |
| Wall throughput | 56.45 FPS | 56.49 FPS | unchanged |
| Compile time | 0.2248 ms | 0.2256 ms | +0.0008 ms, noise |
| Total compositor time | 1.282 ms | 1.256 ms | no regression |

Physical-footprint medians are 372.70 MiB eager and 359.42 MiB lazy, but paired deltas
range from +0.11 MiB to -13.28 MiB and overlap the established Metal/WindowServer noise
band, so no physical-memory gain is attributed from three pairs. All six processes retain
the identical 352 commands, 113 draws, 528 vector vertices, 226 text vertices, 26 paths,
11 brushes, zero gradients, zero effect pipelines, and disabled GPU hit testing. No wgpu
validation, uncaptured, or fatal error occurs.

Artifacts:

- `artifacts/memory-optimization/step23-lazy-chart-pipelines/eager-1.json` through `eager-3.json`
- `artifacts/memory-optimization/step23-lazy-chart-pipelines/lazy-1.json` through `lazy-3.json`

### Step 24 — compact core color-glyph residency and Avalonia glyph staging

Status: **complete**.

Every core glyph atlas previously created a 256×256 RGBA bitmap-color texture at
construction, even when the process rendered no `sbix` or CBDT bitmap glyph. The initial
texture is now 64×64 while the existing 512×512 maximum is unchanged. A bitmap larger than
the resident texture grows it geometrically to the required size, copies prior texels,
refreshes normalized coordinates, advances the texture revision, and recreates the
compositor's concrete atlas bind groups through the existing revision contract. A new
96×96 `sbix` regression starts from 16×16, grows once to 128×128, verifies nonzero decoded
pixels, and proves a repeated lookup preserves coordinates and does not grow again.

The core compositor options now also expose the bounded glyph uniform-ring reservation.
Avalonia selects 16 KiB instead of 64 KiB. This still holds 64 independently aligned glyph
uniform records. The representative 13-glyph first-use regression retains one uniform
write, one raster submission, one bind-group creation, and one compute pass with the
compact uniform ring.

A proposed 64 KiB Avalonia coverage ring was rejected: the same first-use workload split
its pending outline uploads into four writes instead of the accepted one-or-two range.
Avalonia therefore retains the 256 KiB coverage ring. Raster quality, physical-DPI sizing,
four-way subpixel policy, atlas maximums, eviction, and steady rendering are unchanged.

Exact persistent reservation changes per Avalonia compositor are:

| Storage | Former | Current | Change |
|---|---:|---:|---:|
| Bitmap-color glyph texture | 262,144 B | 16,384 B | **-245,760 B** |
| Glyph uniform GPU ring | 65,536 B | 16,384 B | **-49,152 B** |
| Managed uniform upload mirror | 65,536 B | 16,384 B | **-49,152 B** |
| Glyph coverage GPU ring | 262,144 B | 262,144 B | unchanged |
| **Total listed residency** | **655,360 B** | **311,296 B** | **-344,064 B (-52.5%, -336 KiB)** |

The color-texture reduction is the core default; the smaller uniform ring is the Avalonia
backend profile. Metrics now report both uniform reservations, coverage staging, and glyph
raster submissions/passes.

A temporary process-level switch restored the former color texture and uniform ring in the
same Release binary. It was removed after three legacy and three compact Custom Drawing
processes, each with 180 warm-up and 300 measured frames:

| Metric | Legacy median | Compact median | Change |
|---|---:|---:|---:|
| Allocation/frame | 5,996.85 B | 5,996.85 B | unchanged |
| Retained managed memory | 21.62 MiB | 21.52 MiB | -0.09 MiB |
| Working set | 233.59 MiB | 233.86 MiB | +0.27 MiB, noise |
| Physical footprint | 358.77 MiB | 358.64 MiB | -0.13 MiB, noise |
| Wall throughput | 56.60 FPS | 56.73 FPS | no regression |
| Compile time | 0.2249 ms | 0.2135 ms | no regression |
| Total compositor time | 1.2412 ms | 1.2391 ms | unchanged |

The Custom Drawing workload does not rasterize a new glyph after attachment, so all six
runs correctly report zero measurement-time glyph raster submissions. They retain exactly
352 commands, 113 draws, 528 vector vertices, and 226 text vertices. Physical-footprint
ranges overlap (legacy 358.72–358.89 MiB, compact 358.44–359.02 MiB), so the exact GPU/CPU
reservation delta—not process footprint noise—is the memory claim. No wgpu validation,
uncaptured, fatal, or other error occurs.

Artifacts:

- `artifacts/memory-optimization/step24-compact-glyph-staging/legacy-1.json` through
  `legacy-3.json`
- `artifacts/memory-optimization/step24-compact-glyph-staging/compact-1.json` through
  `compact-3.json`

Step 25 addresses the measured path-atlas shape mismatch separately, including its
coordinate-stability, recovery-packing, one-shot retry, image-quality, and performance
contracts.

### Step 25 — independent-axis core path-atlas growth

Status: **complete**.

The Custom Drawing workload's tallest raster is 49×1,608 pixels. The former square growth
policy therefore expanded the R8 atlas from 512×512 through 1,024×1,024 to 2,048×2,048,
reserving 4 MiB even though no observed path required comparable width. `PathAtlas` now
tracks width and height independently and grows only the required axis geometrically. If
the current shelf is vertically exhausted while an entry already fits both dimensions,
height grows first; after height reaches its configured maximum, width may grow to expose
the right-hand strip. The per-axis maximum remains 2,048, so this does not expand the
renderer's previous memory bound.

Growth copies the old rectangular texel region without moving it, so existing integer
atlas coordinates and cached coverage remain stable. Both the texture revision and atlas
generation advance: the revision refreshes concrete GPU bindings, while the generation
invalidates vertices whose normalized UVs were compiled against the previous dimensions.
The compositor then uses its bounded reset/recompile path in the same frame, preventing
partially stale path output when a later entry grows the other axis. Normalized
coordinates use the width reciprocal for X and the height reciprocal for Y. The
deterministic MaxRects recovery path likewise uses independent extents, area, coordinate
candidates, free rectangles, compatibility bounds, and placement signatures. A capacity
miss or UV-generation change still aborts compilation, resets once, and retries the same
frame. A live set that cannot fit after that retry still fails explicitly rather than
looping or dropping paths.

The new regressions cover:

- 512×512 to 512×1,024 height-only growth with unchanged texel coordinates and coverage
  for already resident paths, plus an advanced generation that forces same-frame UV
  recompilation;
- separate X/Y UV normalization after rectangular growth;
- rectangular reset/recovery with non-overlapping bounds and visible raster output; and
- a 900-pixel-tall rounded path rendered visibly on the first 64×1,024 frame, followed by
  a stable retained second frame.

The ControlCatalog diagnostics now emit the actual atlas width and height plus peak raster
width and height. A temporary same-binary switch restored square growth and was removed
after three square and three rectangular fresh-process runs, each with 180 warm-up and 300
measured frames:

| Metric | Square median | Rectangular median | Change |
|---|---:|---:|---:|
| Path-atlas extent | 2,048×2,048 | 512×2,048 | width reduced 4× |
| Persistent R8 texture | 4,194,304 B | 1,048,576 B | **-3,145,728 B (-75%, -3 MiB)** |
| Allocation/frame | 5,996.69 B | 5,997.81 B | +1.12 B, noise |
| Retained managed memory | 21.53 MiB | 21.53 MiB | unchanged |
| Working set | 234.02 MiB | 233.75 MiB | -0.27 MiB |
| Physical footprint | 373.69 MiB | 370.17 MiB | **-3.52 MiB median** |
| Wall throughput | 56.21 FPS | 56.28 FPS | unchanged |
| Compile time | 0.2341 ms | 0.2384 ms | +0.0042 ms, noise |
| Upload time | 0.4022 ms | 0.3919 ms | no regression |
| Render time | 0.6786 ms | 0.6607 ms | no regression |
| Total compositor time | 1.3115 ms | 1.2911 ms | no regression |

All six runs retain the identical 26 paths, 352 commands, 113 draws, 528 vector vertices,
and 226 text vertices. Each of the three paired rectangular runs used 2.85–5.13 MiB less
physical footprint than its square counterpart, consistent with the exact 3 MiB texture
reduction. No WebGPU validation, uncaptured, fatal, or other error occurs.

The final rebuilt Avalonia 12 binary was also exercised independently for 60 warm-up and
120 measured frames. It reports a 512×2,048 atlas, 1,048,576 texture bytes, the same
49×1,608 peak raster and workload counts, 57.55 FPS, 0.2219 ms compilation, and 1.1487 ms
total compositor time. Its screenshot preserves the expected Custom Drawing output.

Artifacts:

- `artifacts/memory-optimization/step25-rectangular-path-atlas/square-1.json` through
  `square-3.json`
- `artifacts/memory-optimization/step25-rectangular-path-atlas/rectangular-1.json` through
  `rectangular-3.json`
- `artifacts/memory-optimization/step25-rectangular-path-atlas/final.json`
- `artifacts/memory-optimization/step25-rectangular-path-atlas/final.png`

### Step 26 — demand-created primary/offscreen core pipelines

Status: **complete**.

Every compositor previously compiled all eight base pipelines at construction: vector,
solid rectangle, text, and texture variants for both the multisampled primary target and
the single-sample offscreen target. The three corresponding shader modules were also
created before any scene established which command or target family it needed.

Base shaders and pipelines are now selected through the existing owning
`RenderPipelineCache` on first use. Primary and offscreen identities remain distinct
because their sample counts and pipeline layouts differ, but rendering through one target
does not retain the unused family for the other. The cache keeps every selected pipeline,
so subsequent frames perform the same value-key lookup and reuse the same native object.
An empty compositor now owns zero scene shader modules and zero render pipelines instead
of three and eight. A solid-rectangle primary render creates one vector shader and one
primary pipeline; a later offscreen render creates only its second target pipeline and
reuses the shader.

Clients that require explicit prewarming can set
`CompositorOptions.PrecompileBasePipelines = true`. That policy recreates the former three
base shaders and eight pipelines during construction. It is opt-in rather than an
incidental cost on every compositor.

This particularly benefits Avalonia: its framebuffer and render-to-texture paths call
`RenderOffscreen`, while the previous constructor still retained the unrelated primary
family. Custom Drawing now retains exactly the five pipelines its offscreen workload
selects instead of ten total eager/dynamic pipelines. Its three used shaders are unchanged.

Regressions prove:

- default construction owns no base shader or pipeline;
- the first primary solid-rectangle frame is visible and creates exactly one pipeline;
- a stable second primary frame retains the same object count;
- the first offscreen frame is visible and adds only the corresponding target pipeline;
- a stable second offscreen frame retains the same object count; and
- explicit precompilation still creates the former three-shader/eight-pipeline base set.

A temporary same-binary environment switch selected the eager policy for measurement and
was removed afterward. Six eager and six lazy fresh processes were run in alternating
forward and reverse order, each with 180 warm-up and 300 measured frames:

| Metric | Eager median | Demand-created median | Change |
|---|---:|---:|---:|
| Scene shader modules | 3 | 3 | all three used by this workload |
| Scene render pipelines | 10 | 5 | **5 unused pipelines eliminated (-50%)** |
| First rendered frame | 1,071.13 ms | 1,059.14 ms | **-11.99 ms**; paired median -13.18 ms |
| Allocation/frame | 5,996.85 B | 6,010.00 B | +13.15 B, noise |
| Retained managed memory | 21.535 MiB | 21.535 MiB | unchanged |
| Working set | 233.438 MiB | 233.461 MiB | +0.023 MiB, noise |
| Physical footprint | 369.650 MiB | 370.103 MiB | overlapping native/WindowServer noise |
| Wall throughput | 57.99 FPS | 57.89 FPS | -0.18%, noise |
| Compile time | 0.1996 ms | 0.1992 ms | unchanged |
| Upload time | 0.3189 ms | 0.3234 ms | +0.0045 ms, noise |
| Render time | 0.5285 ms | 0.5404 ms | +0.0119 ms, noise |
| Total compositor time | 1.0561 ms | 1.0707 ms | +0.0147 ms, noise |

All twelve processes retain the identical 26 paths, 352 commands, 113 draws, 528 vector
vertices, and 226 text vertices. Physical-footprint pair deltas range from -14.97 MiB to
+14.06 MiB, so no byte-level process-footprint saving is claimed. The exact memory result
is the five fewer live native pipeline objects. No WebGPU validation, uncaptured, fatal,
or other error occurs.

The final rebuilt Avalonia 12 binary reports five scene pipelines, three shaders, the
unchanged 512×2,048 path atlas and workload counts, 58.15 FPS, 0.1932 ms compilation, and
1.0276 ms total compositor time. Its screenshot preserves the expected output.

Artifacts:

- `artifacts/memory-optimization/step26-lazy-target-pipelines/eager-1.json` through
  `eager-3.json`
- `artifacts/memory-optimization/step26-lazy-target-pipelines/lazy-1.json` through
  `lazy-3.json`
- `artifacts/memory-optimization/step26-lazy-target-pipelines/reverse-eager-1.json`
  through `reverse-eager-3.json`
- `artifacts/memory-optimization/step26-lazy-target-pipelines/reverse-lazy-1.json`
  through `reverse-lazy-3.json`
- `artifacts/memory-optimization/step26-lazy-target-pipelines/final.json`
- `artifacts/memory-optimization/step26-lazy-target-pipelines/final.png`

### Step 27 — shared primary/offscreen core binding infrastructure

Status: **complete**.

The primary and offscreen base pipelines previously cloned the same resource-binding ABI
inside every compositor. Both target families use the same WebGPU device, uniform and
storage buffers, samplers, atlas textures, texture views, and mask textures. Their material
pipeline difference is sample count: the primary family uses the configured multisample
count and the offscreen family uses one sample. Sample count is render-pipeline state and
does not require a second bind-group or pipeline-layout identity.

The compositor now creates one explicit set of vector, text, texture, atlas, path-atlas,
mask, and retained-glyph layouts and aliases it into both target families. The immutable
uniform, atlas, path-atlas, and dummy-mask bind groups are likewise shared. Brush or
gradient-buffer growth replaces one vector scene-state bind group and publishes that same
pointer to both targets. Glyph- or path-atlas texture revision replaces one corresponding
bind group. Dynamic geometry-mask bindings use one texture-keyed cache rather than
separate primary/offscreen caches.

This removes exactly 17 native WebGPU objects from every compositor:

| Native object family | Former | Shared | Change |
|---|---:|---:|---:|
| Base bind-group layouts | 15 | 8 | **-7** |
| Base pipeline layouts | 8 | 4 | **-4** |
| Immutable base bind groups | 12 | 6 | **-6** |
| **Total** | **35** | **18** | **-17 (-48.6%)** |

The dynamic mask cache additionally retains at most one bind group per live mask texture
instead of one per target when the same mask crosses both target paths. The benchmark page
does not create geometry masks during measurement, so no dynamic-mask saving is included
in the exact 17-object result. Native implementations do not expose portable byte sizes
for these opaque handles; no byte-level GPU or driver-memory claim is made.

Regressions prove:

- all seven bind-group-layout, four pipeline-layout, and six immutable-bind-group target
  pairs have identical native identity;
- brush-storage growth replaces the shared vector bind group once and preserves identity;
- glyph- and path-atlas revision refreshes replace one shared bind group each;
- primary and offscreen frames remain visibly rendered;
- primary and offscreen requests for one dynamic mask return one cached bind group, and
  texture disposal removes it; and
- compositor disposal does not release any aliased object twice.

A temporary same-binary switch restored the cloned target infrastructure and was removed
after six cloned and six shared fresh processes in alternating forward and reverse order.
Each process used 180 warm-up and 300 measured Custom Drawing frames:

| Metric | Cloned median | Shared median | Change |
|---|---:|---:|---:|
| Base bind-group layouts | 15 | 8 | **-7** |
| Base pipeline layouts | 8 | 4 | **-4** |
| Immutable base bind groups | 12 | 6 | **-6** |
| Allocation/frame | 5,996.77 B | 5,996.85 B | +0.08 B, unchanged |
| Retained managed memory | 21.52 MiB | 21.52 MiB | unchanged |
| Working set | 233.43 MiB | 232.87 MiB | -0.56 MiB |
| Physical footprint | 362.47 MiB | 354.88 MiB | overlapping native/WindowServer noise |
| Wall throughput | 56.52 FPS | 56.39 FPS | -0.23%, noise |
| Compile time | 0.2376 ms | 0.2373 ms | unchanged |
| Upload time | 0.3957 ms | 0.3924 ms | unchanged |
| Render time | 0.6733 ms | 0.6673 ms | unchanged |
| Total compositor time | 1.3112 ms | 1.2918 ms | unchanged |

The physical-footprint ranges overlap: cloned 354.06–370.33 MiB and shared
352.00–370.52 MiB. Pair deltas alternate between approximately -15 and +15 MiB, confirming
that the process measurement cannot resolve these small opaque objects. First-rendered
frame medians moved from 1,062.24 to 1,050.81 ms, but startup variation is also larger than
the work removed, so this is not claimed as a latency gain.

All twelve processes retain the identical 26 paths, 352 commands, 113 draws, 528 vector
vertices, 226 text vertices, three scene shaders, five scene pipelines, and two persistent
texture bind groups. No WebGPU validation, uncaptured, fatal, or other error occurs.

The final rebuilt Avalonia 12 binary reports 8/4/6 binding objects, the unchanged
512×2,048 path atlas and workload counts, 57.62 FPS, 0.2050 ms compilation, and 1.0787 ms
total compositor time. Its inspected screenshot preserves the expected Custom Drawing
output.

Artifacts:

- `artifacts/memory-optimization/step27-shared-target-bindings/legacy-1.json` through
  `legacy-3.json`
- `artifacts/memory-optimization/step27-shared-target-bindings/shared-1.json` through
  `shared-3.json`
- `artifacts/memory-optimization/step27-shared-target-bindings/reverse-legacy-1.json`
  through `reverse-legacy-3.json`
- `artifacts/memory-optimization/step27-shared-target-bindings/reverse-shared-1.json`
  through `reverse-shared-3.json`
- `artifacts/memory-optimization/step27-shared-target-bindings/final.json`
- `artifacts/memory-optimization/step27-shared-target-bindings/final.png`

### Step 28 — compact demand-grown monochrome glyph atlas

Status: **complete**.

The core monochrome glyph atlas previously reserved a 512×512 R8 texture at compositor
construction. Its shelf packer requires a two-texel perimeter, so a 260×260 texture
provides a complete 256×256 usable starting area. A direct 256×256 experiment was rejected:
the representative Avalonia navigation and Custom Drawing text exhausted it and triggered
an immediate growth to 512×512. The 260×260 configuration retains the full workload
without growth.

`GlyphAtlas.DefaultInitialAtlasSize` is now 260 and
`CompositorOptions.InitialGlyphAtlasSize` exposes the reservation independently from the
unchanged maximum. Direct `GlyphAtlas` callers can likewise specify the initial and maximum
sizes separately. If demand exceeds a non-power-of-two start, geometric growth rounds to
the next power of two: 260 grows to 512 rather than 520. Growth still flushes an active
batch transactionally, copies every resident texel without moving integer coordinates,
refreshes normalized UVs, advances only the texture revision, and lets the compositor
replace its one shared atlas bind group. Generation, eviction, DPI, subpixel phases,
raster quality, and the 2,048×2,048 maximum are unchanged.

The persistent R8 reservation per compositor changes exactly:

| Storage | Former | Current | Change |
|---|---:|---:|---:|
| Monochrome glyph coverage texture | 262,144 B | 67,600 B | **-194,544 B (-74.2%, -190 KiB)** |
| Bitmap-color glyph texture | 16,384 B | 16,384 B | unchanged |
| Combined resident glyph textures | 278,528 B | 83,984 B | **-69.8%** |

Regressions prove that a configured 260×260 atlas starts at the requested extent, grows
to 512×512 rather than 520×520 under additional glyph phases, preserves visible resident
coverage and integer texel coordinates, advances the texture revision without advancing
generation, and keeps the original glyph cache entry reusable. Compositor-option coverage
also verifies independent initial/max sizes, and the Avalonia backend profile inherits the
new core default.

A temporary same-binary initial-size override restored 512×512 and was removed after six
legacy and six compact fresh processes in alternating forward and reverse order. Each
process used 180 warm-up and 300 measured Custom Drawing frames:

| Metric | 512×512 median | 260×260 median | Change |
|---|---:|---:|---:|
| Monochrome glyph texture | 262,144 B | 67,600 B | **-194,544 B (-74.2%)** |
| Allocation/frame | 5,997.33 B | 5,996.85 B | -0.48 B, unchanged |
| Retained managed memory | 21.52 MiB | 21.52 MiB | unchanged |
| Working set | 232.71 MiB | 232.73 MiB | unchanged |
| Physical footprint | 355.42 MiB | 361.34 MiB | overlapping native/WindowServer noise |
| Wall throughput | 56.58 FPS | 56.40 FPS | -0.32%, noise |
| Compile time | 0.2299 ms | 0.2366 ms | +0.0067 ms, noise |
| Upload time | 0.3851 ms | 0.3986 ms | +0.0136 ms, noise |
| Render time | 0.6560 ms | 0.6730 ms | +0.0170 ms, noise |
| Total compositor time | 1.2695 ms | 1.3107 ms | +0.0412 ms, noise |

The physical-footprint ranges overlap: 512×512 353.92–370.13 MiB and 260×260
352.53–370.41 MiB. Pair deltas alternate between approximately -17.6 and +15.9 MiB, so the
exact texture descriptor is the memory claim rather than process noise. First-rendered
frame medians moved from 1,063.49 to 1,051.50 ms, but startup variation is much larger and
no latency gain is claimed.

All twelve processes keep the compact atlas at 260×260—none invokes its growth path—and
retain the identical 26 paths, 352 commands, 113 draws, 528 vector vertices, 226 text
vertices, three scene shaders, and five scene pipelines. No WebGPU validation, uncaptured,
fatal, or other error occurs.

The final rebuilt Avalonia 12 binary reports a 67,600-byte monochrome glyph texture, the
unchanged 512×2,048 path atlas and workload counts, 57.72 FPS, 0.2084 ms compilation, and
1.1087 ms total compositor time. Its inspected screenshot preserves the expected glyph and
Custom Drawing output.

Artifacts:

- `artifacts/memory-optimization/step28-compact-initial-glyph-atlas/legacy-1.json`
  through `legacy-3.json`
- `artifacts/memory-optimization/step28-compact-initial-glyph-atlas/compact-1.json`
  through `compact-3.json`
- `artifacts/memory-optimization/step28-compact-initial-glyph-atlas/reverse-legacy-1.json`
  through `reverse-legacy-3.json`
- `artifacts/memory-optimization/step28-compact-initial-glyph-atlas/reverse-compact-1.json`
  through `reverse-compact-3.json`
- `artifacts/memory-optimization/step28-compact-initial-glyph-atlas/final.json`
- `artifacts/memory-optimization/step28-compact-initial-glyph-atlas/final.png`

### Step 29 — measured compact glyph-outline buffer capacity

Status: **complete**.

The glyph atlas formerly reserved 64 GPU glyph records and 1,024 GPU outline segments,
plus managed scratch and pending-upload lists at the same capacities. The representative
Avalonia navigation and Custom Drawing workload consistently compiles 40 records and 813
segments. The initial capacities are now 48 records and 896 segments, preserving 20% and
10.2% headroom respectively. Existing geometric growth remains unchanged when a larger
font workload crosses either capacity.

The direct per-atlas reservation changes exactly:

| Storage | Former | Current | Change |
|---|---:|---:|---:|
| GPU record and segment buffers | 51,200 B | 44,544 B | **-6,656 B (-13.0%)** |
| Managed segment scratch elements | 49,152 B | 43,008 B | **-6,144 B** |
| Managed pending-segment elements | 49,152 B | 43,008 B | **-6,144 B** |
| Managed pending-record elements | 2,048 B | 1,536 B | **-512 B** |
| Combined direct GPU and managed payload | 151,552 B | 132,096 B | **-19,456 B (-12.8%)** |

Managed figures describe reserved element payload and exclude the unchanged list and array
object headers. GPU allocation is reported directly from the two WebGPU buffer
descriptors.

A temporary same-binary capacity override restored 64/1,024 and was removed after six
legacy and six compact fresh processes. Each process used 180 warm-up and 300 measured
Custom Drawing frames in forward and reverse launch order. Every compact process remained
at exactly 40/48 records and 813/896 segments; no buffer growth, extra glyph-raster
submission, workload change, or WebGPU error occurred.

Across all twelve runs, allocation remains exactly 5,996.85 B/frame at the median. The
unadjusted medians were 57.28 FPS / 1.1804 ms compositor for legacy and 56.60 FPS /
1.2974 ms for compact. Those aggregate values are launch-order biased: when legacy ran
first, the later compact process ranged from -1.04% to -3.10% FPS; reversing the order
reduced compact-versus-legacy to -0.25% through +0.67%, with compositor deltas from
-0.0972 to +0.0080 ms. Since only buffer capacity changes, all compute dispatch counts
and shader-visible record/segment counts are identical, and the reverse-order results
straddle zero, no throughput or compositor regression is attributed to the compact
reservation.

The final rebuilt Avalonia 12 binary reports 44,544 bytes of glyph-outline GPU buffers,
40/48 records, 813/896 segments, the unchanged 352 commands and 113 draws, 56.27 FPS,
0.2662 ms compilation, and 1.3848 ms total compositor time. Its inspected screenshot
preserves the expected glyph, path, and Custom Drawing output.

Regression coverage verifies both the exact compact initial capacity and geometric growth
beyond it while preserving outline compilation, upload, and coverage. The runtime
override used for measurement is absent from the final source.

Artifacts:

- `artifacts/memory-optimization/step29-glyph-outline-capacity/legacy-1.json` through
  `legacy-3.json`
- `artifacts/memory-optimization/step29-glyph-outline-capacity/compact-1.json` through
  `compact-3.json`
- `artifacts/memory-optimization/step29-glyph-outline-capacity/reverse-legacy-1.json`
  through `reverse-legacy-3.json`
- `artifacts/memory-optimization/step29-glyph-outline-capacity/reverse-compact-1.json`
  through `reverse-compact-3.json`
- `artifacts/memory-optimization/step29-glyph-outline-capacity/final.json`
- `artifacts/memory-optimization/step29-glyph-outline-capacity/final.png`

### Final validation

- `ProGPU.Tests`: **2,398 passed**, 0 failed.
- `ProGPU.Tests.Headless`: **198 passed**, 0 failed.
- Focused bidi coverage: **140 passed**, including 32,768 exhaustive ASCII pair
  comparisons; all **5/5** affected headless text pages pass.
- `ProGPU.Xaml.Tests`: **249 passed**, 0 failed.
- Avalonia ProGPU unit tests: **66 passed**, 0 failed.
- Avalonia ProGPU render tests: **1 passed**, 0 failed.
- Avalonia SkiaShim render tests: **2 passed**, 0 failed.
- Avalonia Silk.NET integration tests: **34 passed**, 0 failed.
- Focused font/storage tests: **77 passed**, including exact Inter/Noto hashes, CFF/WOFF,
  variable fonts, TTC faces, mapped raw SFNT, and demand-resident `sbix`.
- Avalonia 11 and Avalonia 12 Rendering/SilkNet Release builds, native-Skia and ProGPU
  ControlCatalog builds, RenderDemo, ProGpuSandbox, SampleControls, and the WinUI desktop
  sample Release build all succeed.
- The unchanged `1_Counter`, `3_FlightBooker`, `4_Timer`, `5_CRUD`, and `6_CircleDrawer`
  examples still fail their pre-existing routed-event/Reactive API conversions; none of
  the optimization changes enter those projects. `2_TempConverter` and `7_Cells` build.
- All **54/54** isolated WinUI sample pages completed the final Release memory/performance
  profile.
- No wgpu validation errors occurred in any measured ControlCatalog or WinUI run.
- Atlas maximums, command/draw/path counts, DPI/raster policy, animation workload, and
  color-font payloads are unchanged. Path-atlas residency now follows measured independent
  X/Y demand. Headless pixel/layout coverage is green.
