# Pure C++ browser gallery

Status: MotionMark and native OpenType text-shaping slices implemented and
locally qualified on 2026-08-21.

## Purpose

`progpu_native_browser_gallery` is the fully native counterpart of
`ProGPU.Samples.Browser`. Clang/Emscripten compiles the ProGPU C++20 scene
builder, renderer, text/font stack, and canonical production shaders directly
to WebAssembly. The published application contains no CLR, Mono runtime,
managed assembly, or managed-to-native boundary.

The first gallery page is a direct cross-language port of the ProGPU-owned
[`MotionMarkShowcaseVisual.cs`](../src/ProGPU.Samples/Views/MotionMarkShowcaseVisual.cs).
Its native implementation records the exact source provenance in
[`progpu_native_motion_mark.hpp`](../src/ProGPU.Native/samples/Views/progpu_native_motion_mark.hpp)
and has a matched native contract test. No third-party MotionMark source was
used.

The second page ports the eight ProGPU-owned feature-wall cases from
[`TextShapingShowcasePage.cs`](../src/ProGPU.Samples/Pages/TextShapingShowcasePage.cs)
to [`progpu_native_text_shaping_showcase.cpp`](../src/ProGPU.Native/samples/Pages/progpu_native_text_shaping_showcase.cpp).
It executes the native Unicode decoder, GSUB/GPOS shaper, TrueType outline
decoder, semantic scene builder, compute glyph rasterizer, and shared
production `Text.wgsl` shader. The page contains no pre-rendered text and uses
no browser or platform text API.

## Shared managed/native browser architecture

The native application is not a separate browser integration fork. Both hosts
consume the canonical
[`progpu-browser-host.js`](../src/ProGPU.Browser/BrowserAssets/progpu-browser-host.js)
module:

| Layer | Managed browser gallery | Native C++ browser gallery | Shared contract |
| --- | --- | --- | --- |
| Viewport and DPI | `ProGPU.Browser` dispatcher | `browser_window_host` | Visual viewport variables, `ResizeObserver`, logical size, and physical backing size |
| Adapter/device | `BrowserGpuRuntime` | Emdawnwebgpu preinitialized device | High-performance adapter, Full/Portable feature selection, device-loss and uncaptured-error policy |
| Frame scheduling | `BrowserWindowHost` | `emscripten_request_animation_frame_loop` | Browser vsync lifecycle and physical framebuffer metrics |
| Canvas presentation | Managed WebGPU protocol | Stable WebGPU C API over Emdawnwebgpu | Preferred format, premultiplied alpha, current swapchain texture, direct render attachment |
| Scene lifecycle | Managed retained compositor | C++ `semantic_scene_builder` | Immutable scene ID/generation, update only on change, render every presented frame |
| GPU implementation | Managed backend | Native renderer | The same production `.wgsl` resources and semantic scene contracts |

The HTML shell provides accessible navigation and controls. MotionMark
topology, animation, scene building, and all pixels inside the WebGPU canvas are
produced by native C++; the DOM is not used as a rendering fallback.

An explicitly destroyed `GPUDevice` is treated as graceful host teardown. Only
an abnormal device-loss reason reaches the engine recovery callback. The real-
browser gate changes MotionMark complexity and requires a later presented
frame, so this lifecycle distinction cannot hide an unusable device.

## MotionMark parity and performance contract

| Managed contract | Native implementation |
| --- | --- |
| 81 by 41 logical grid and four directional offsets | Identical grid and offset set |
| Lines, quadratic Beziers, and cubic Beziers | One native geometry primitive per source segment |
| Split-delimited paths with the style of the group-end element | Identical grouping and style selection |
| Vello, Fluent, spectrum, and monochrome palettes | Identical color values and HSV conversion |
| `pow(random, 5) * 20 + 1` stroke distribution | Identical width distribution |
| Fixed 60 Hz update budget and 0.5% toggles per step | Identical bounded update cadence |
| Retained grouped path recording | One retained geometry resource, one deduplicated brush table, and one semantic draw command |

Changed-scene preparation is `O(N + G)` time and retained storage for `N`
segments and `G` groups. Stable frames do not rebuild or recopy the semantic
stream. One changed generation crosses the internal native engine boundary
once; each displayed frame performs one native render call and one GPU
submission. The 1,000-segment local qualification produced one GPU draw and a
roughly 132 KB retained scene stream.

The geometry command carries an explicit deduplicated brush-index map, matching
the managed native-scene compiler. This is required for exact curve rendering:
quadratic and cubic GPU vertices use their inline color lanes for control-point
metadata, while the shared brush table supplies their material. Lines, curves,
caps, and joins therefore retain one group-end style without per-segment draws.

## Text-shaping parity and quality contract

| Managed feature wall | Native C++ implementation |
| --- | --- |
| Standard ligatures, kerning, fractions, slashed zero, stylistic set, contextual alternates, Romanian localization, and mark-to-mark positioning | The same eight feature tags, strings, language selection, and before/after comparison |
| Unicode/OpenType shaping on the CPU | Native C++ Unicode decode plus GSUB lookups 1-8 and GPOS lookups 1-9 |
| Retained glyph runs and cached outlines | One immutable semantic scene; unique source outlines are decoded once and reused across physical raster sizes |
| Physical-pixel glyph coverage | Raster records are keyed by glyph and physical font size while sharing immutable source segments |
| Canonical WebGPU text rendering | Native and managed backends consume the same `GlyphRasterizer.wgsl` and `Text.wgsl` resources |

The page transfers the fetched font into native ownership once rather than
copying it again inside the sample. A changed preset compiles once and publishes
its scene generation; stable frames only render and submit. The Romanian case
contains 267 positioned glyphs, 107 retained outline records, 11 draw commands,
and a 73,992-byte semantic stream. Sharing source segments across raster sizes
reduced that stream from 131,496 bytes by 43.7% while retaining full-resolution
coverage.

Linear filtering previously allowed a glyph quad near a packed-atlas tile edge
to sample a neighboring tile. The canonical text shader now carries flat
half-texel tile bounds and clamps every grayscale, ClearType, color, masked, and
premultiplied atlas sample to its glyph tile. This preserves filtering inside
the glyph while removing faint horizontal or vertical outlines around glyph
quads. The corresponding source-contract and native retained-resource tests
cover both backends; the algorithm and research record are in
[`GPU_TEXT_COVERAGE_CACHE_ARCHITECTURE.md`](GPU_TEXT_COVERAGE_CACHE_ARCHITECTURE.md).

## WebGPU presentation and pixel quality

The shared host measures the CSS canvas and assigns a backing width and height
rounded from `logical extent * devicePixelRatio` (clamped to the engine's 1-4
DPI contract). C++ configures the corresponding WebGPU surface, acquires its
current texture once per animation frame, and passes that texture view directly
to `progpu_native_engine_render_scene`. There is no texture mapping, CPU
readback, BGRA/RGBA conversion, or 2D canvas copy.

This follows the [WebGPU canvas presentation
contract](https://gpuweb.github.io/gpuweb/#canvas-context) and Emscripten's
[Emdawnwebgpu integration](https://emscripten.org/docs/porting/multimedia_and_graphics/WebGPU-support.html).
The frame loop uses the documented
[`emscripten_request_animation_frame_loop`](https://emscripten.org/docs/porting/emscripten-runtime-environment.html#implementing-an-asynchronous-main-loop-in-c-c)
browser lifecycle. These sources confirmed the adopted swapchain and scheduling
boundary; they did not supply implementation source.

## Release AOT publish

Run:

```bash
./eng/publish-progpu-native-browser-gallery.sh
python3 -m http.server 8091 \
  --directory artifacts/progpu-native/browser-gallery-aot
```

Then open
`http://127.0.0.1:8091/progpu_native_browser_gallery.html`.

On `main`, the Browser Pages workflow publishes the same four-file payload
beside the managed AOT gallery under `/native/`. Both gallery variants therefore
share one deployment origin and the same canonical browser host module while
remaining independently compiled runtimes.

The browser executable uses C++20, `Release`, `-O3`, and link-time optimization
for its compile and link phases. This matches Emscripten's [optimized build
guidance](https://emscripten.org/docs/compiling/Building-Projects.html#building-projects-with-optimizations).
It emits a native `.wasm` ahead of time rather than performing .NET AOT.

Local optimized artifact sizes after the text-shaping slice on 2026-08-21:

| Asset | Raw bytes | gzip -9 | Brotli -11 |
| --- | ---: | ---: | ---: |
| `progpu_native_browser_gallery.wasm` | 1,537,121 | 508,537 | 357,394 |
| `progpu_native_browser_gallery.js` | 62,791 | 14,951 | 13,219 |
| `progpu_native_browser_gallery.html` | 17,896 | 5,737 | 4,933 |
| shared `progpu-browser-host.js` | 4,153 | 1,432 | 1,176 |
| external `Inter-Regular.ttf` | 411,640 | 196,987 | 157,517 |
| Total | 2,033,601 | 727,644 | 534,239 |

Compressed columns are deterministic local compression measurements; actual
HTTP transfer sizes depend on the server. The executable Wasm is 1.47 MiB raw
and contains the native renderer, semantic scene compiler, Unicode/OpenType
text stack, outline decoder, and both gallery samples. Inter remains a separately
cacheable font asset.

## Automated gates

- `progpu_native_motion_mark_tests` verifies the managed sample's retained
  topology/group/update contracts with deterministic native data.
- `progpu_native_text_shaping_showcase_tests` verifies the eight ported presets,
  shaped glyph/outline metrics, retained scene reuse, and physical-size outline
  records through the native C++20 text stack.
- `eng/progpu-test-native-browser.sh` builds the Release Emscripten target and
  drives a real Chromium WebGPU context.
- The browser gate asserts the pure-C++ renderer identity, actual canvas
  swapchain presentation, MotionMark and text-page control round-trips, exact
  physical-pixel backing dimensions, preset generation publication, and the
  expected glyph/outline counts, then uploads screenshots and JSON contracts.
- The existing Clang, GCC, and MSVC C++20 lanes compile the shared native sample
  model through the normal CMake graph.
