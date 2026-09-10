# ProGPU CAD background plan-scene preparation research

## Scope and clean-room boundary

This checkpoint moves immutable CAD plan command recording and picture freezing
off the UI thread for desktop file-open and explicitly asynchronous edit
requests. GPU-domain raster IMAGE
leases are resolved, decoded/uploaded when necessary, and validated on the
owning host thread first; a one-shot prepared resource set then transfers those
leases into worker-owned recording without a worker-side WebGPU call. Browser
Wasm executes the same phases synchronously. The existing session/request/
generation publication gate rejects stale work before any retained-state swap.

The implementation is original ProGPU code. No foreign source text, helper
layout, control flow, or cache representation was copied.

## Primary-source comparison

| Engine | Sources consulted | Decision |
|---|---|---|
| Skia / SkParagraph | [Skia canvas and picture API](https://skia.org/docs/user/api/), [Skia execution tracing](https://docs.skia.org/docs/dev/tools/tracing/), [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/), and [Skia text overview](https://docs.skia.org/docs/dev/design/text_overview/) | Adopted immutable recorded-picture replacement and reusable shaping/layout. Adapted preparation to ProGPU's typed drawing context and explicit texture leases. Rejected rebuilding or reshaping on publication and rejected initializing GPU state merely to record CPU geometry. |
| DirectWrite / Direct2D / Win2D | [Direct2D device contexts](https://learn.microsoft.com/en-us/windows/win32/direct2d/devices-and-device-contexts), [multithreaded Direct2D apps](https://learn.microsoft.com/en-us/windows/win32/direct2d/multi-threaded-direct2d-apps), [Direct2D command lists](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nn-d2d1_1-id2d1commandlist), [Direct2D and DirectWrite](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite), and [Win2D `CanvasCommandList`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm) | Adopted separating reusable text/layout and command recording from device execution. Adapted device ownership to a host-thread prepare phase followed by a device-call-free worker phase. Rejected COM, per-command crossings, and a platform-specific CAD scene. |
| WebRender | [rendering overview](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst), [retained display-list overview](https://github.com/servo/servo/wiki/Webrender-Overview), and [current profiler source](https://github.com/servo/webrender/blob/main/webrender/src/profiler.rs) | Adopted asynchronous scene building with transaction identity and separately measured scene/upload work. Adapted it to one complete ProGPU snapshot/picture and O(1) publication ticket. Rejected partial publication and hidden resource I/O during retained replay. |
| Vello / Parley | [Vello retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md), [Vello scene source](https://github.com/linebender/vello/blob/main/vello/src/scene.rs), and [Parley layout model](https://github.com/linebender/parley/blob/main/doc/concept.md) | Adopted late immutable scene encoding and reusable text layout. Adapted this to a worker-recorded ProGPU picture while retaining exact CAD analytic geometry. Rejected baking viewport transforms into worker geometry or adding a second text stack. |
| HarfBuzz | [shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html), [buffer contract](https://harfbuzz.github.io/harfbuzz-hb-buffer.html), and [glyph rendering boundary](https://harfbuzz.github.io/glyphs-and-rendering.html) | Shaping plans, fallback decisions, variable-font state, glyph indices, and positions stay reusable immutable CPU inputs. Rejected per-open reshaping after snapshot preparation and any GPU font-table work. |

## Architecture, ownership, and complexity

`CadPlanSceneCompiler.PrepareResources` creates a bounded resolver snapshot,
visits immutable headers once to preserve the exact plan layer/visibility
policy, and acquires at most one texture lease per actually drawable raster
resource in the target device domain. This host phase is O(E + R + B + P) when
a resource needs first decode/upload, with O(R + P) owned storage for E headers,
R resource records, B encoded bytes, and P decoded pixels under existing
catalog limits. It performs no file or network I/O. A wrong-device, disposed,
or null lease fails before worker recording.

`CadPreparedPlanSceneResources` is generation-tagged and transfers once. Its
O(R) transfer retains leases directly in the worker drawing context and copies
only an R-entry texture reference table; it performs no GPU operation. A
generation/resource-table mismatch, second transfer, cancellation, or compiler
failure releases all transferred and untransferred leases exactly once.
`CompilePrepared` then has the existing O(N + P) plan-recording complexity and
the same command/resource output as synchronous `Compile`.

Desktop `CadSampleCanvas.LoadAsync` now performs snapshot construction on a
worker, resource preparation on the host continuation using the device context
captured before the first await, and complete CPU plan recording plus picture
freezing on a worker. The previous picture remains drawable throughout.
Browser/Wasm runs the same ordered phases without requiring threads. Final
publication remains O(1) and accepts only the exact session, request, and
content generation.

`ExecuteEditAsync`, `TryUndoAsync`, and `TryRedoAsync` retain the existing
synchronous editing API unchanged while providing an explicit ordered path for
worker-prepared replacements. One bounded semaphore serializes mutation through
publication. Cancellation is accepted while waiting and immediately before
mutation; after a history command commits, preparation runs without caller
cancellation to a generation-checked published-or-superseded result. A
post-commit preparation exception retains the prior drawable picture and exact
history state, is surfaced to the caller, and can be retried without another
mutation through `RefreshEditedSceneAsync`. Two queued edits therefore publish
in request order and never expose a picture tagged with the wrong generation.

Startup stays lazy: no resource is prepared before an actual loaded snapshot.
Visibility/layer policy and exact source order remain compiler-owned; this
checkpoint does not introduce a second culler. Demand-driven GPU upload is
bounded to the explicit host resource phase. DPI/subpixel behavior, fallback
fonts, variable-font state, glyph/path caches, retained batching, and device-
loss rehydration keep their existing ownership. Both managed and native-picture
consumers receive the same final `GpuPicture`, so no shader, native C++
frontend, or stable C ABI change applies.

## Validation and remaining work

The headless regression prepares a real encoded IMAGE texture on the owning
device, records the plan on a worker, verifies texture identity and retained
lease ownership, rejects a second transfer, and proves the texture survives
catalog disposal until the scene releases it. Existing snapshot/scene,
publication-gate, raster effect/clip, native-picture, cancellation, and browser
AOT gates remain applicable.

Focused regressions cover matching async load publication, cancellation before
mutation, ordered concurrent edit publication, and generation-exact
Execute/Undo/Redo replacement. Generation-keyed incremental chunks and shared
block fragments/instances remain separate rendering work.
