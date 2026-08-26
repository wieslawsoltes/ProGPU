# Avalonia.Skia retained command-stream research

## Scope and observed framework contract

This clean-room slice optimizes the SkiaSharp picture and immutable-image APIs
used by Avalonia.Skia. At Avalonia commit
[`fee9c561`](https://github.com/AvaloniaUI/Avalonia/tree/fee9c561ce036e8a3e8cee2397c75ca599b4790d),
[`DrawingContextImpl`](https://github.com/AvaloniaUI/Avalonia/blob/fee9c561ce036e8a3e8cee2397c75ca599b4790d/src/Skia/Avalonia.Skia/DrawingContextImpl.cs)
records ordered canvas state, clip, rectangle, path, glyph-run, and immutable
image operations. `PictureRenderTarget`, `SurfaceRenderTarget`, and
`WriteableBitmapImpl` retain or replay those operations. Those observable
public calls, rather than any foreign implementation text or command encoding,
define the benchmark and regression-test workloads.

The previous ProGPU representation retained one full, wide `RenderCommand`
record per operation, plus parallel side data. A repeated immutable-image
picture consequently retained about 149 managed bytes per draw even though an
ordinary image command needs only texture, source/destination rectangles,
sampling, transform, and presentation state.

## Primary-source comparison

| Engine or specification | Relevant public or architectural contract | ProGPU clean-room decision |
| --- | --- | --- |
| [Skia `SkPicture`](https://api.skia.org/classSkPicture.html) and [`SkPictureRecorder`](https://api.skia.org/classSkPictureRecorder.html) | A picture is an immutable recording of ordered drawing, matrix, and clip operations. Finishing a recording invalidates the recording canvas for further recording. | Preserve exact command order and immutable replay. Use an original ProGPU token and typed-record layout; do not reproduce Skia opcodes, record structures, source organization, or implementation control flow. Clear picture-only image lookup state when recording finishes. |
| [Skia `SkImage`](https://api.skia.org/classSkImage.html) | An image cannot be modified after creation. `kAllow_CachingHint` permits internally caching decoded/copied pixels; `kDisallow_CachingHint` forbids that cache. | Cache one immutable GPU readback only for `Allow`. For `Disallow`, copy a whole native-format texture directly into the caller's rows through the reusable WebGPU staging buffer, preserving row padding and avoiding a persistent copied-pixel cache. |
| [Skia `SkRRect`](https://api.skia.org/classSkRRect.html) | Uniform rounded rectangles standardize bounds, scale oversized radii to fit, collapse invalid radii to rectangular corners, and clamp oval radii to half extents. | Apply the same public normalization directly in the scalar `DrawRoundRect(rect, rx, ry, paint)` fast path and record one analytic rounded-rectangle command. Complex corners and shader geometry continue through the existing retained-path fallback. |
| [Direct2D command lists](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nn-d2d1_1-id2d1commandlist) and [Win2D `CanvasCommandList`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm) | A closed command list is an immutable device resource that can be drawn repeatedly; drawing state and resources remain associated with the device domain. | Retain typed resource references and shared transforms in the owning WebGPU context. Do not materialize a CPU bitmap or cross-device compatibility adapter during recording. |
| [WebGPU command buffers](https://www.w3.org/TR/webgpu/#command-buffers) | Command buffers are immutable encodings submitted in order to a device queue. | Keep the CPU retained stream immutable and allocation-free to index, then expand it into the existing WebGPU compilation and submission path. CPU command packing does not replace GPU rasterization or composition. |
| [WebRender rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html) | Retained display lists preserve ordered scene state while renderer-owned resources are reused across frames. | Separate compact scene intent from device resource ownership. Retain one texture lease per image/context and reuse it for consecutive picture draws instead of searching the retained-resource list per draw. |
| [Vello scene encoding](https://github.com/linebender/vello/blob/main/vello_encoding/src/encoding.rs) and [Vello architecture](https://github.com/linebender/vello) | Compact scene encoding is separate from the `wgpu` device, queue, target, and GPU compute renderer. | Adopt that separation only at the architectural level. ProGPU uses original 32-bit ordered tokens, typed managed arrays, exact fallback records, and its existing WebGPU renderer. |
| [SkParagraph](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/README.md), [DirectWrite glyph runs](https://learn.microsoft.com/en-us/windows/win32/directwrite/glyphs-and-glyph-runs), [HarfBuzz shaping](https://harfbuzz.github.io/harfbuzz-hb-shape.html), and [Parley](https://docs.rs/parley/latest/parley/) | Shaping and layout produce reusable glyph IDs, positions, and runs before rasterization. | Preserve the existing shaped arrays and text records. This slice does not reshape during replay, move Unicode work to the GPU, or alter fallback, DPI, subpixel, atlas, or device-loss contracts. |

Adopted concepts are immutable ordered replay, typed records for frequent
operations, shared transforms, explicit device-resource ownership, and reusable
CPU shaping results. Adapted concepts are implemented through ProGPU's own
reflection-free command types and WebGPU compiler. Rejected alternatives are
copying a foreign opcode layout, placing unmanaged allocations outside managed
measurements, an eager CPU image mirror, a CPU raster fallback, dropping state
operations, and unbounded per-draw resource lookup caches.

## Resulting algorithm and bounds

- Construction performs two linear passes over `C` commands. Transform
  interning is `O(C)` average and `O(C²)` only under adversarial hash
  collisions, with bounded `O(C)` pooled scratch.
- Retained storage is `O(C + T + R)` for ordered 32-bit tokens, `T` unique
  transforms, typed common records, and `R` referenced resources. Uncommon
  commands retain an exact full-record fallback.
- Indexing and replay expansion are allocation-free `O(1)` per command. The
  command token selects a typed array and index; no scan or reflection occurs.
- A consecutive whole-image picture draw reuses the already retained
  image/context texture in `O(1)`. The recording context still owns the one
  actual lease; the three-reference lookup cache is cleared at recording
  completion and canvas disposal.
- A native-format whole-image `ReadPixels` with `Disallow` performs one
  `O(P)` GPU-to-staging transfer and copies mapped rows directly to caller
  storage. `Allow` may reuse one `O(P)` immutable readback array. Both paths
  reuse one staging buffer and preserve the caller's row padding.
- Synchronous map completion performs non-blocking WebGPU device polls and
  cooperative thread yields until completion or the existing 30-second
  timeout. It does not impose a one-millisecond sleep after every incomplete
  poll, so latency remains proportional to actual queue completion.
- The focused storage gate permits at most 64 retained bytes per ordinary
  texture draw, plus one shared transform. Exact clone tests cover texture,
  rectangle, path, rectangle clip, geometry clip, and all pop-state records.

## Validation protocol

The benchmark runner uses matched Release binaries, three fresh ProGPU
processes compared with three alternating official-SkiaSharp baseline
processes, 32 warmup passes, and 24 samples per process. Semantic checksums must
match exactly. Managed allocation is measured with
`GC.GetAllocatedBytesForCurrentThread`; elapsed distributions report the
combined median and p95 rather than a selected process.

Focused tests require exact `RenderCommand` round trips, the 64-byte texture
record bound, retained image lease sharing without a GPU copy, picture state
semantics, and compositor regression coverage. Final integration repeats the
representative Avalonia application workload and matched Xcode Allocations,
Time Profiler, and Metal System Trace captures. Raw `.trace`, `.nettrace`,
`.etlx`, Xcode scratch, and exported XML are temporary and are deleted after
compact summaries are retained.

## First checkpoint measurement

The exact Preview.46 release commit `d55d6657` is the clean baseline. The
candidate differs by the retained token stream, typed common records, and
consecutive picture-image lease lookup cache. Every checksum matched.

| Avalonia-shaped workload | Baseline median | Candidate median | Change | Baseline p95 | Candidate p95 | Baseline allocation | Candidate allocation |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Immutable-image picture recording | 330.771 ns | 314.645 ns | -4.9% | 512.916 ns | 359.209 ns | 149 B/op | 65 B/op (-56.4%) |
| Mixed picture recording | 3,773.844 ns | 3,533.693 ns | -6.4% | 4,660.480 ns | 4,150.555 ns | 1,627 B/op | 1,080 B/op (-33.6%) |

Official SkiaSharp remains faster in both microbenchmarks at this checkpoint;
these results establish a material improvement and memory floor for the next
typed text/glyph and immutable-image slices, not completion of the broader
performance goal.

The next packing checkpoint adds an at-most-96-byte common glyph-run record and
an exact 8-byte scalar state record for opacity and blend pushes. On the same
three-process mixed-picture workload, retained managed allocation falls again
from 1,080 to 424 B/op (-60.7%, or -73.9% from Preview.46), while median time
moves from 3,533.693 to 3,511.312 ns/op (-0.6%). Exact glyph arrays, positions,
font transform, rendering/hinting modes, presentation dependencies, transform,
and pop ordering round-trip through the compact stream.

The SaveLayer checkpoint adds at-most-80-byte analytic rounded-rectangle
records and exact 16-byte retained-visual references. Its type-first classifier
avoids rescanning those common wide commands. The three-process layer workload
improves from 3,412.750 to 3,367.188 ns/op (-1.3%) and from 9,311 to 8,180 B/op
(-12.1%, or -21.4% from Preview.46's 10,411 B/op). A canvas-local layer-context
stack was measured and rejected: it increased allocation by 20 B/op and slowed
the median, so that multi-entry pooling code did not remain.

## 2026-08-26 typed retained-layer checkpoint

This round rechecked the current primary contracts before changing storage.
Skia still defines `saveLayer` as saved matrix/clip state plus restore-time
alpha, filters, and blend, while [`SkPicture`](https://api.skia.org/classSkPicture.html)
remains immutable recorded replay. Direct2D requires a populated command list
to be closed before it becomes an effect input or draw image, and Win2D exposes
the same model as a device-owned image:
[`ID2D1CommandList::Close`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nf-d2d1_1-id2d1commandlist-close),
[`CanvasCommandList`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm),
and the [Win2D effects quickstart](https://microsoft.github.io/Win2D/WinUI3/html/QuickStart.htm).
WebRender continues to transform a retained display list into picture, spatial,
and clip trees before render-task expansion, while Vello keeps scene encoding
separate from later `wgpu` rendering:
[Firefox rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
and the [Vello repository](https://github.com/linebender/vello).

The adopted clean-room design recognizes the exact command shape produced by
ProGPU's own `SKCanvas` analytic rounded-rectangle recorder. A bounded,
single-draw save layer with no inherited clip and no retained side resource now
owns two typed records and their transforms directly in its retained visual;
the third command, `PopClip`, is implicit. `IOwnedRenderCommandCache` expands
the exact `PushClip`, `DrawRoundedRect`, `PopClip` sequence on demand without a
general command collection, transform hash table, or per-layer typed arrays.
The temporary one-command `DrawingContext` is cleared before a single
canvas-local reuse slot can retain it. A nested layer cannot borrow an active
context, and a second returned nested context is released rather than growing
an unbounded pool. Every other command shape uses the existing exact compact
picture path.

Construction and retained storage remain `O(1)` for this three-command shape;
replay is three allocation-free `O(1)` indexed expansions. General layers
remain `O(C + R)` for `C` commands and `R` resources. The canvas retains at most
one cleared transient layer context, independent of the number of sequential
layers. No GPU resource, device, surface, or submission is created while a
picture is recorded.

Rejected measured candidates are preserved as evidence rather than shipped:

- a four-entry context stack reduced the 16-layer allocation from 8,189 to
  6,431 B/op but changed the combined three-process median from 3,847.625 to
  4,208.313 ns/op;
- inserting clip wrappers through canvas-local wide-command scratch reduced
  allocation to 7,147 B/op but did not improve the combined median;
- storing `LayerFrame` as a large value type reduced only 120 B/op and slowed
  the three fresh processes;
- reusing a mutable `LayerFrame` reduced the sustained allocation by another
  152 B/op but changed its median from 7,230.038 to 9,030.557 ns/op, so object
  reuse was removed.

The final Release matrix alternated three official SkiaSharp 4.151.0 processes
with three ProGPU processes, using 32 warmup passes and 24 samples in each
process. All 62 semantic checksums matched. For `avalonia-layer-recording`, the
combined ProGPU median changed from 3,847.625 to 2,673.188 ns/op (-30.5%), p95
from 14,958.313 to 4,333.313 ns/op (-71.0%), and managed allocation from 8,189
to 6,131 B/op (-25.1%). The official-SkiaSharp ratio consequently changed from
6.808 to 4.512. Official allocation counters still exclude native Skia command
storage, so this is not presented as equal cross-engine allocation accounting.

The matched 50,000-operation Xcode Instruments captures all exited zero and
produced the same checksum `17305763102166149771`. Allocation-instrumented
median changed from 13,533.860 to 7,737.439 ns/op, Time Profiler from
10,548.747 to 9,310.386 ns/op, and Metal System Trace from 10,363.022 to
10,073.158 ns/op. Managed allocation in every capture changed from 3,309 to
1,205 B/op (-63.6%). Allocations/VM Tracker reported 113,604,752 versus
113,786,720 persistent heap-plus-anonymous-VM bytes (+0.16%); the difference
includes three additional 64 KiB JIT arena pages and does not support a native
footprint claim. Both Metal captures reported zero resources, submissions,
drawable waits, compiler spills, potential hangs, hang risks, and command
buffer errors, as expected for CPU-only picture recording. Raw traces and
exports were deleted only after their compact summaries and target logs were
retained under `artifacts/performance/skiasharp-round2`.

The managed/native rendering parity audit found no renderer-side delta. This
change is confined to the SkiaSharp compatibility front end; both the managed
and native ProGPU scene compilers receive the same expanded command types,
geometry, transforms, effects, clip order, and resource ownership. Text was
also audited and is deliberately unchanged: current
[HarfBuzz shaping](https://harfbuzz.github.io/harfbuzz-hb-shape.html),
[DirectWrite glyph runs](https://learn.microsoft.com/en-us/windows/win32/directwrite/glyphs-and-glyph-runs),
[SkParagraph's bounded cache](https://skia.googlesource.com/skia/+/main/modules/skparagraph/include/ParagraphCache.h),
and [Parley reusable layout context](https://docs.rs/parley/latest/parley/)
continue to support retaining shaped glyph IDs and positions rather than
reshaping during layer replay.

Release validation passed 110 `SkCanvasStateTests`, 3,809 core tests, 240
headless tests, 104 Avalonia contract tests, 129 current and 104 Avalonia 11
Silk.NET contract tests, the headless-pixel and binary-compatibility tests, and
all 307 XAML tests. The two process-owning XAML watch tests were run in their
own non-competing lane after the other 305 XAML tests because their intentional
one-minute child-process budget becomes timing-sensitive under assembly-wide
parallel load. The official API gate reports `reference=4222`,
`matching=4222`, `missing=0`, and `extra=997` ProGPU extensions.

The positioned-text checkpoint keeps the common one-run builder state in one
typed field instead of adding and clearing a `List<T>` reference slot for every
blob. Multi-run builders promote the first run to the existing list exactly
once; immutable blob snapshots and the bounded pinned run lease are unchanged.
Construction and disposal remain `O(1)` outside the caller's `O(G)` glyph and
position copies. In three fresh matched processes, the Avalonia positioned-run
workload is 268.375 ns/op and 89 B/op versus official SkiaSharp at 289.270
ns/op and 136 B/op: ProGPU is 7.2% faster and allocates 34.6% less managed
memory, with the exact checksum preserved.

## Readback checkpoint measurement

The readback checkpoint uses the same Release runner and three fresh processes
per backend. The previous ProGPU source endpoint and Preview.46 have identical
surface/readback product code; only package versions and documentation changed
between them. Every checksum matched.

| Avalonia-shaped workload | Previous ProGPU median | Candidate median | Change | Previous allocation | Candidate allocation |
| --- | ---: | ---: | ---: | ---: | ---: |
| Immutable-image repeated readback (`Disallow`) | 2,799,313 ns | 230,266 ns | -91.8% | 1,104 B/op | 640 B/op (-42.0%) |
| Direct surface readback | 2,901,513 ns | 431,690 ns | -85.1% | 1,135 B/op | 1,136 B/op |
| Framebuffer conversion readback | 3,169,493 ns | 447,761 ns | -85.9% | 14,088 B/op | 14,089 B/op |
| Reusable surface composition | 658,568 ns | 606,675 ns | -7.9% | 991 B/op | 992 B/op |

The small one-byte allocation differences are integer division artifacts in
the per-operation runner, not retained objects. Official SkiaSharp remains
faster for synchronous GPU-to-CPU reads because its compared surface is a CPU
raster surface. ProGPU intentionally keeps rendering and composition on
WebGPU; this checkpoint removes avoidable waiting and copying without adding a
CPU renderer or violating `Disallow` caching semantics.

## Final broad-distribution and integration validation

The final complete benchmark matrix ran three fresh official and three fresh
ProGPU processes with the same 32/24 warmup/sample protocol. All cases retained
their exact semantic checksums. Representative final ProGPU medians are
155,793 ns for repeated immutable-image `Disallow` readback, 441,181 ns for
direct surface readback, 460,935 ns for conversion readback, 2,854 ns and
424 B/op for mixed picture recording, 3,512 ns and 8,180 B/op for layer
recording, and 254 ns and 89 B/op for positioned text. Complete-distribution
timings vary more than isolated alternating runs, especially for submicrosecond
text construction; therefore the focused paired distributions above remain the
claim evidence and no universal native-performance superiority is asserted.

The exact final source passed these integration gates:

- official SkiaSharp 4.151.0 metadata: 4,222 of 4,222 entries matched, zero
  missing;
- 3,305 `ProGPU.Tests` and 225 headless tests;
- 28 pinned Avalonia retained-compositor tests;
- 274 upstream Avalonia.Skia text tests plus 13 focused text tests, with five
  documented platform/profile skips;
- the full patched Avalonia 12.0.5 ControlCatalog source build against the
  ProGPU SkiaSharp shim, with zero warnings and zero errors.

## Matched macOS Instruments qualification

The same Release `avalonia-surface-compose` workload and checksum
`10859766936728445827` were captured before and after with Xcode Allocations
plus VM Tracker, Time Profiler, and Metal System Trace. Managed allocation
remained exactly 992 B/op. The instrumented medians were 733,859/701,778 ns
before/after under Allocations, 579,267/616,824 ns under Time Profiler, and
713,420/710,813 ns under Metal. The mixed directions are within the
instrumented-run spread and do not support a throughput-regression or
throughput-improvement claim for composition itself.

Allocations reported 159,348,000 B before and 159,378,672 B after for
persistent heap plus anonymous VM, a 30,672 B (0.019%) difference. Both Metal
captures reported zero drawable waits, compiler spills, potential hangs, hang
risks, and command-buffer errors. The first candidate Metal finalization hit an
Instruments rules-engine failure; its incomplete 85 MB trace and 186 MB Xcode
scratch were deleted, and an independent successful capture supplied the final
Metal evidence. All final raw traces, EventPipe traces, `.etlx` files, Xcode
scratch, and XML exports were removed after compact JSON/Markdown summaries and
target logs were retained.
