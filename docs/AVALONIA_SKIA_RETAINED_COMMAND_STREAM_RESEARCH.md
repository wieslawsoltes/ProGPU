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
