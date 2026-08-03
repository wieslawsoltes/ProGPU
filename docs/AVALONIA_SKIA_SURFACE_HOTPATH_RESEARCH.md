# Avalonia.Skia surface hot-path research

## Scope and observed framework contract

This clean-room slice optimizes the official SkiaSharp surface/image contracts
that Avalonia.Skia uses for reusable render targets. At Avalonia commit
[`fee9c561`](https://github.com/AvaloniaUI/Avalonia/tree/fee9c561ce036e8a3e8cee2397c75ca599b4790d),
[`SurfaceRenderTarget`](https://github.com/AvaloniaUI/Avalonia/blob/fee9c561ce036e8a3e8cee2397c75ca599b4790d/src/Skia/Avalonia.Skia/SurfaceRenderTarget.cs)
reuses an `SKSurface`, flushes its canvas, draws one surface into another, and
creates immutable snapshots. The
[`FramebufferRenderTarget`](https://github.com/AvaloniaUI/Avalonia/blob/fee9c561ce036e8a3e8cee2397c75ca599b4790d/src/Skia/Avalonia.Skia/FramebufferRenderTarget.cs)
conversion fallback snapshots a pointer-backed surface and reads that image into
the destination framebuffer. `WriteableBitmapImpl` similarly retains immutable
image snapshots. These observed public calls, not foreign implementation text,
define the benchmark and regression-test workloads.

## Primary-source comparison

| Engine or specification | Relevant public/architectural contract | ProGPU clean-room decision |
| --- | --- | --- |
| [Skia `SkSurface`](https://skia.googlesource.com/skia/+/refs/heads/main/include/core/SkSurface.h), [`SkSurface` implementation boundary](https://skia.googlesource.com/skia/+/refs/heads/main/src/image/SkSurface.cpp), and [`SkSurface_Base`](https://skia.googlesource.com/skia/+/refs/heads/main/src/image/SkSurface_Base.h) | A snapshot is immutable; unchanged content may share an image generation, while a later write preserves any externally retained generation. Surface drawing is allowed to use a temporary image representation instead of requiring a permanent full snapshot. | Adopt the observable immutable-generation and copy-on-write contract. A ProGPU-owned surface transfers its existing texture into a reference-counted image generation. A later write reclaims it in `O(1)` when no external lease remains, or performs one GPU copy when an image/draw command still retains the old generation. The ownership state machine, types, and control flow are original ProGPU code. |
| [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains), [render targets](https://learn.microsoft.com/en-us/windows/win32/direct2d/render-targets-overview), and [Direct2D/Direct3D interoperation](https://learn.microsoft.com/en-us/windows/win32/Direct2D/direct2d-and-direct3d-interoperation-overview) | Render targets and bitmaps are device resources; commands are batched and `Flush` submits pending work without changing resource-domain ownership. | Keep surface backing, immutable generations, readback staging, and retained texture leases in one typed `WgpuContext` device domain. Reject CPU round-trips for same-device composition. |
| [Win2D `CanvasRenderTarget`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasRenderTarget.htm) and [drawing-session contract](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasDrawingSession.htm) | An offscreen target is both drawable and sampleable; ending a drawing session submits work, and device resources should be reused. | Preserve one renderable/sampleable texture per mutable surface and one reusable owned command visual. Do not create a second CPU display list or per-flush surface object. |
| [WebGPU object destruction](https://gpuweb.github.io/gpuweb/#dom-gpuobjectbase-destroy), [queue submission](https://gpuweb.github.io/gpuweb/#dom-gpuqueue-submit), and [submitted-work completion](https://gpuweb.github.io/gpuweb/#dom-gpuqueue-onsubmittedworkdone) | Submitted command buffers retain the resources needed for execution. Dropping application references does not cancel submitted work; explicit external storage has a separate lifetime boundary. | Release ordinary native wrapper references without a per-frame queue wait. Route production submissions through `WgpuContext.Submit`, poll every two submissions, and force a drain after eight undrained submissions. Imported external texture owners still force an immediate wait before disposal. This makes queue/resource residency independent of total frame count. |
| [WebRender rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html) and [render-task source](https://searchfox.org/firefox-main/source/gfx/wr/webrender/src/render_task.rs) | Retained scene/resource identity is reused while render tasks and texture-cache residency remain renderer-owned and bounded. | Reuse one typed `IOwnedRenderCommandCache` visual per surface. Compile the current commands directly and clear their retained leases after submission; do not allocate and append into a new `DrawingVisual` every flush. |
| [Vello](https://github.com/linebender/vello) | Scene encoding is separate from the explicit `wgpu` device, queue, and target texture; parallel raster/composition work stays on the GPU. | Keep command recording typed and CPU-retained while rendering, texture copies, sampling, and composition remain WebGPU operations. Reject CPU tessellation or a separate software surface renderer. |
| [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/), [SkParagraph](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/), [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/direct-write-portal), [HarfBuzz shaping](https://harfbuzz.github.io/shaping-and-shape-plans.html), and [Parley](https://github.com/linebender/parley) | Unicode/OpenType shaping and line layout are reusable CPU results, independent of surface generation and queue lifetime. | Leave shaping, fallback, glyph IDs, line layout, DPI/subpixel policy, glyph/path atlases, visibility culling, and device-loss invalidation unchanged. This slice changes only surface/image ownership, submission, and readback staging. |

Rejected alternatives are an unconditional snapshot texture copy, borrowing a
mutable surface texture without copy-on-write, blocking the device queue every
frame, an unbounded asynchronous submission queue, an always-resident duplicate
CPU image, reflection-based Avalonia adapters, and source-derived Skia control
flow.

## Resulting algorithms and bounds

- Stable `Snapshot(bounds)` is `O(1)` time and one compact image-view
  allocation. It does not submit a texture copy.
- The first write after a snapshot is `O(1)` when the generation has no other
  references. With an outstanding snapshot or deferred draw lease it performs
  one `O(P)` GPU texture copy for `P` pixels, preserving immutable pixels.
- Surface flush compilation is `O(C)` for `C` retained commands and reads the
  existing command storage directly. It no longer performs a second `O(C)`
  append or allocates a visual/command array per flush.
- Surface and image readback remain necessarily `O(P)` GPU/CPU bandwidth.
  Each long-lived source owns one staging buffer and one exact-size managed
  pixel array, so repeated reads use `O(P)` retained storage with bounded
  per-call object allocation. Pointer-backed snapshots transfer the already
  completed canonical RGBA generation instead of mapping the GPU twice.
- Queue accounting is `O(1)` per submission. Completed work is polled every two
  submissions; eight undrained submissions is the fixed forced-drain bound.
  External native owners retain the stricter immediate-drain rule.

## Validation protocol

The benchmark runner executes three alternating official-SkiaSharp/ProGPU
Release process pairs, 32 warmups and 24 samples per process, and requires exact
semantic checksums. The surface family covers stable frames, surface-to-surface
composition, pointer-backed snapshot conversion, direct surface readback, and
repeated immutable-image readback. Focused tests additionally cover retained
snapshot immutability, deferred draws followed by source mutation, `O(1)`
backing reclamation, subset-relative reads, BGRA conversion, external-owner
drains, and the bounded submission policy.

macOS validation pairs EventPipe CPU samples with Xcode Allocations, Time
Profiler, and Metal System Trace captures of the same Release composition
workload. Raw `.nettrace`, `.trace`, Xcode scratch, and exported XML data are
temporary; compact benchmark JSON/Markdown, top-function text, profiler logs,
manifests, and summaries are retained.

## Measured result

The uninstrumented Release comparison used Preview.45 (`ab21744b`) as the
before binary and `2d4e725e` as the product-code endpoint. The final runner
executed three alternating official-SkiaSharp/ProGPU process pairs with 32
warmups and 24 samples per process. The working tree differed from that endpoint
only by this research document. All listed checksums matched across every run.

| Avalonia-shaped ProGPU workload | Preview.45 median | Final median | Change | Preview.45 allocation | Final allocation |
| --- | ---: | ---: | ---: | ---: | ---: |
| Reusable surface composition | 1,483,329 ns | 658,568 ns | -55.6% | 16,248 B/op | 991 B/op (-93.9%) |
| Framebuffer conversion readback | 6,220,693 ns | 3,169,493 ns | -49.1% | 17,646 B/op | 14,088 B/op (-20.2%) |
| Stable surface frame enqueue | 85,726 ns | 342,653 ns | +299.7% | 3,241 B/op | 441 B/op (-86.4%) |

The stable-frame workload deliberately runs faster than presentation pacing and
has no terminal readback. It therefore measures the selected completed-work
backpressure as well as command encoding: every eighth undrained submission is
retired before more work is admitted. The CPU enqueue median is slower than the
old per-frame cleanup policy, while allocation is substantially lower and
native residency no longer grows with the total frame count. Composition and
conversion, the framework paths that consume or present the result, retain the
material latency improvements above. A looser 64-submission experiment reached
about 0.267 ms composition but retained roughly 85 MB of IOAccelerator VM; an
unbounded experiment retained roughly 378 MB. Both were rejected.

Matched Xcode Instruments runs used the same 18-second/8-second-window Release
composition workload. Preview.45 measured 1,849,262 ns/op and 16,248 B/op under
instrumentation; the selected bound measured 713,420 ns/op and 992 B/op, a
61.4% timing and 93.9% allocation reduction. Persistent heap plus anonymous VM
was 125,743,408 B before and 159,348,000 B after. IOAccelerator VM was
12,419,072 B before and 37,191,680 B after; the final fixed high-water state had
45 `MTLResourceList` entries totaling 2,211,840 B. This is the measured cost of
retaining a small amount of GPU overlap rather than synchronizing every cleanup.
The final Metal capture reported zero command-buffer errors, potential hangs,
hang risks, drawable waits, and compiler spills. EventPipe attributed 77.1% of
sampled exclusive CPU time to the intentional bounded `PollDevice(true)`
backpressure, confirming the remaining dominant cost rather than hiding it in
managed work.

Timing ratios are evidence from one shared Apple Silicon machine rather than
narrow CI gates. Exact checksums, API metadata, bounded residency, focused
ownership tests, and full regression gates remain hard requirements. Raw
EventPipe and Instruments traces were deleted after retaining compact JSON,
Markdown, logs, manifests, and top-function evidence.
