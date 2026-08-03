# Avalonia.Skia paint and retained-effect research

## Scope and clean-room boundary

This slice implements ProGPU behavior from public contracts, specifications,
observable Avalonia.Skia call shapes, and independently measured outputs. No
foreign implementation source was copied, translated, or structurally ported.
The implementation remains inside ProGPU's typed retained scene, resource-lease,
and WebGPU compositor architecture.

The measured Avalonia.Skia 12.0.5 call shapes are:

- record a `SaveLayer`, immediately reset the cached `SKPaint`, dispose its
  `SKImageFilter`, record the layer body, and restore later;
- create isotropic blur and drop-shadow filters for visual effects;
- create one alpha table with three identity color tables for opacity;
- create short, normally four-value dash patterns;
- mutate and reuse one `SKRoundRect` through `SetRectRadii` and `GetRadii`.

## Primary sources examined

- Skia documents `SkPicture` as recorded commands for later playback and
  `saveLayer` as applying paint alpha, color/image filters, and blend mode when
  restored: [SkCanvas reference](https://api.skia.org/classSkCanvas.html),
  [SkImageFilter reference](https://api.skia.org/classSkImageFilter.html), and
  [SkImageFilters factories](https://api.skia.org/classSkImageFilters.html).
- Direct2D records replayable command lists whose referenced resources remain
  external, then closes the list before using it as an effect input:
  [ID2D1CommandList](https://learn.microsoft.com/windows/win32/api/d2d1_1/nn-d2d1_1-id2d1commandlist).
- Win2D explicitly records vector content into a `CanvasCommandList`, uses that
  command list as a Gaussian-blur input, and delays DPI-dependent rasterization
  until drawing to a target: [effects quickstart](https://microsoft.github.io/Win2D/WinUI3/html/QuickStart.htm),
  [CanvasCommandList](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm),
  and [DPI model](https://microsoft.github.io/Win2D/WinUI3/html/DPI.htm).
- Direct2D exposes GPU effect graphs covering blur, transfer tables,
  morphology, displacement, lighting, composition, and transforms:
  [built-in effects](https://learn.microsoft.com/windows/win32/direct2d/built-in-effects).
- WebRender converts a retained display list into a scene and picture tree,
  then expands effects such as shadows and blur into render tasks only for the
  current frame: [Firefox rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html).
- Vello separates scene encoding (`push_layer`, drawing, `pop_layer`) from
  later wgpu rendering and uses GPU compute for parallel raster work:
  [Vello repository and architecture overview](https://github.com/linebender/vello).
- Text architecture was checked even though this slice does not alter shaping.
  DirectWrite keeps reusable layout/glyph runs separate from Direct2D
  rasterization, HarfBuzz remains the OpenType shaping boundary, SkParagraph
  retains staged paragraph state/cache, and Parley shares font/layout contexts
  and reusable layout scratch:
  [DirectWrite/Direct2D text separation](https://learn.microsoft.com/windows/win32/direct2d/direct2d-and-directwrite),
  [HarfBuzz](https://github.com/harfbuzz/harfbuzz),
  [SkParagraph cache](https://chromium.googlesource.com/skia/+/master/modules/skparagraph/include/ParagraphCache.h),
  and [Parley library architecture](https://docs.rs/parley/latest/src/parley/lib.rs.html).

## Architecture decision

Adopted:

- `SKPicture` save layers retain an immutable command subtree and a compact
  snapshot of restore-time paint state. Recording does not create a WebGPU
  device, render target, texture, or compute pass.
- The compositor materializes the isolated texture at replay time. Blur and
  drop shadow use the existing WebGPU effect path and cached effect textures;
  ordinary layers use the existing WebGPU cached-layer path.
- Source clips and save-layer bounds are retained around the subtree before the
  effect runs, while the parent clip also bounds final composition.
- Picture/context resource leases own the retained subtree until the final
  clone or replay context is released. The common no-side-buffer path stores a
  compact command collection rather than a second general 560-byte command
  array.
- Common blur/drop-shadow values are inline in specialized filter objects.
  Four-value dashes use inline storage. Color tables deduplicate identical and
  identity channels while preserving an immutable caller snapshot.
- Text shaping, line breaking, bidi resolution, and glyph positions remain
  reusable CPU results. This slice changes only retained grouping and
  replay-time GPU raster/effect work.

Adapted:

- Skia's logical save-layer contract is represented as a retained ProGPU visual
  because ProGPU already tracks visual invalidation, WebGPU effect texture
  generations, DPI, device loss, and compiled-scene dependencies.
- Direct2D/Win2D command-list resource references become ProGPU reference-counted
  leases so picture clones remain safe without copying textures or mutable
  adapters.
- WebRender's picture/render-task split maps to compact picture recording plus
  compositor-time effect expansion. ProGPU keeps the public SkiaSharp contract
  and its existing WGSL compute effects rather than importing another engine's
  scene types.

Rejected:

- immediate offscreen GPU rendering during `SKPicture` recording, because it
  defeats retained playback, initializes WebGPU on a CPU recording path, and
  cannot adapt raster resolution to the replay target;
- CPU raster fallbacks for blur, shadow, layer isolation, or table filtering;
- borrowing caller arrays, reflection, boxed adapters, and per-frame external
  engine objects;
- moving Unicode/OpenType shaping to the GPU in this slice, because the
  production engines retain reusable shaping/layout results and no measured,
  complete replacement was established.

## Cost model and preliminary evidence

Recording a layer is `O(C + R)` time and storage for `C` retained commands and
`R` retained resources. Common filter and dash creation is `O(1)` with one
owned object; table creation is `O(256)` validation/copy work and stores 0, 256,
512, or 1024 bytes according to channel identity. Replay raster/effect work is
`O(P * K)` for `P` affected pixels and the selected bounded WebGPU kernel
footprint `K`; stable replay reuses compositor effect/layer textures.

Preliminary macOS arm64 smoke measurements, before the final matched profiler
run, show:

- blur plus drop-shadow factories: 216 to 184 managed bytes per operation;
- Avalonia alpha/identity tables: 1256 to 392 managed bytes per operation;
- short dash lifecycle: 344 cold managed bytes to 154 bytes per operation;
- one recorded blur layer: about 609 microseconds before deferral and about
  14 microseconds after deferral in the one-operation smoke, with the same
  semantic checksum;
- 16 retained layers: about 4.75 microseconds and 11,089 managed bytes per
  layer after compact command ownership. Official managed allocation counters
  do not include Skia's native command/filter storage, so final memory claims
  will correlate managed allocation, process footprint, EventPipe, Instruments,
  wgpu-native resources, and Metal allocation data.

Final acceptance requires matched Release binaries, exact semantic checksums,
GPU pixel tests for blur/shadow/clip/opacity, official API metadata comparison,
the full test/package matrix, and macOS Time Profiler, Allocations/VM Tracker,
and Metal System Trace runs. Raw trace bundles are temporary; compact exported
summaries are retained and the raw bundles are removed after analysis.
