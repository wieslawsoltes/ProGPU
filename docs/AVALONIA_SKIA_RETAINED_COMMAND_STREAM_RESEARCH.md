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
semantics, and compositor regression coverage. Final integration will repeat
the representative Avalonia application workload and matched Xcode
Allocations, Time Profiler, and Metal System Trace captures. Raw `.trace`,
`.nettrace`, Xcode scratch, and exported XML are temporary and will be deleted
after compact summaries are retained.

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
