# ProGPU CAD plan GPU metrics research

## Scope

This checkpoint exposes one typed, generation-correlated snapshot of the
existing ProGPU compositor counters for a frame containing a retained CAD plan
picture. It does not add a renderer, shader, managed/native crossing, query
heap, readback, or per-command instrumentation. The sample is explicitly a
pipeline-frame total because host chrome and transient overlays can share the
same compositor submission; it is not falsely attributed to CAD alone.

The implementation is original ProGPU code. The sources below informed only
the counter taxonomy, ownership boundary, and validation contract.

## Primary-source comparison

| Engine | Sources consulted | Adopted, adapted, or rejected |
|---|---|---|
| Skia / SkParagraph | [Skia execution tracing](https://docs.skia.org/docs/dev/tools/tracing/), [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/), and [Skia text overview](https://docs.skia.org/docs/dev/design/text_overview/) | Adopted separating CPU scene/text preparation from render tracing. Existing shaped glyph arrays, fallback decisions, and atlas state stay reusable. Rejected adding trace events or forcing GPU work merely to read counters. |
| DirectWrite / Direct2D / Win2D | [Direct2D device contexts](https://learn.microsoft.com/en-us/windows/win32/direct2d/devices-and-device-contexts), [Direct2D command lists](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nn-d2d1_1-id2d1commandlist), [Direct2D and DirectWrite](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite), and [Win2D `CanvasCommandList`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm) | Adopted the distinction between reusable recorded commands and device-context execution. Adapted it to immutable ProGPU pictures plus compositor-owned frame counters. Rejected a platform diagnostics bridge and per-frame COM/P/Invoke. |
| WebRender | [current profiler source](https://github.com/servo/webrender/blob/main/webrender/src/profiler.rs) and [retained rendering overview](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst) | Adopted separate display-list/scene-build, upload, batching, draw, target, and logical-memory counters. Adapted them to the metrics ProGPU already publishes and retained explicit frame scope. Rejected claiming logical buffer/texture bytes are physical driver residency. |
| Vello / Parley | [Vello retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md), [Vello scene API](https://github.com/linebender/vello/blob/main/vello/src/scene.rs), and [Parley layout model](https://github.com/linebender/parley/blob/main/doc/concept.md) | Adopted retained scene fragments and reusable text-layout results as independent state from frame execution metrics. Rejected rebuilding paths or layout to collect telemetry. |
| HarfBuzz | [shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html), [buffer contract](https://harfbuzz.github.io/harfbuzz-hb-buffer.html), and [glyph rendering boundary](https://harfbuzz.github.io/glyphs-and-rendering.html) | HarfBuzz has no GPU-residency contract. Its reusable shaping results remain outside the frame counter projection. Rejected reshaping, per-glyph counters, and coupling cache telemetry to font-table parsing. |

## Architecture and complexity contract

`CadPlanGpuFrameMetrics.Capture` copies fixed fields from one completed
`CompositorMetrics` value and correlates them with the immutable CAD content
generation and plan command count. Capture is O(1), uses O(1) value storage,
allocates zero managed memory after warmup, performs zero GPU calls, and retains
no compositor, picture, session, texture, font, or atlas ownership.

The projection covers frame/compile/upload/render timing, target and DPI,
draw/command counts, scene and render-bundle reuse, incremental-page reuse and
uploads, geometry counts, known renderer buffer allocations, known atlas and
intermediate texture allocations, and a saturating four-byte logical target
proxy. The latter is named `LogicalRgbaTargetBytes`; it is not an assertion
about swapchain format or physical allocation. All sums saturate rather than
wrap on malformed or synthetic maximum counters.

Startup and lazy initialization remain unchanged: capture does not initialize
WebGPU, create pipelines, enumerate fonts, shape text, build a display list, or
touch a cache. Visibility culling, demand-driven upload, worker preparation,
DPI/subpixel behavior, fallback fonts, variable-font state, and device-loss
rehydration remain owned by their existing ProGPU components. The same
compositor counters observe managed and native-picture plan replay, so no
one-sided native algorithm or stable C ABI change applies.

## Validation and remaining evidence

Focused tests map every counter family, verify saturating accounting, and prove
1,024 warm captures allocate zero managed bytes. Stable replay acceptance uses
the current-frame page compilation/upload and scene/render-bundle reuse values;
cumulative or logical allocation values are not misread as per-frame uploads or
physical residency.

Physical driver-residency telemetry remains separate. On macOS it still
requires matched Instruments Allocations/VM Tracker, Time Profiler, and Metal
System Trace captures correlated with backend/Metal allocation values. The
full managed/native CAD pixel differential and p50/p95/p99 suite also remains a
release gate rather than an inference from telemetry alone.
