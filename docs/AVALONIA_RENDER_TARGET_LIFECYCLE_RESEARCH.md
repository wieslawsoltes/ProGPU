# Avalonia render-target bitmap lifecycle research

## Problem

Avalonia permits a `RenderTargetBitmap` to be constructed while a logical tree
is being built, before a native window or graphics device exists. The ProGPU
implementation previously allocated its WebGPU texture in the constructor and
therefore rejected this valid lifecycle.

## Primary-source comparison

| Engine | Relevant contract | ProGPU decision |
| --- | --- | --- |
| [Avalonia Skia render-target implementation](https://github.com/AvaloniaUI/Avalonia/blob/fee9c561ce036e8a3e8cee2397c75ca599b4790d/src/Skia/Avalonia.Skia/RenderTargetBitmapImpl.cs) | Construction establishes bitmap identity and storage without requiring a window presentation context. | Preserve construction before window creation. Do not initialize WebGPU in the constructor. |
| [Skia canvas/surface creation](https://skia.org/docs/user/api/skcanvas_creation/) | GPU surfaces belong to a GPU context, and surfaces on one device should share that context. Raster surfaces remain a separate CPU option. | Bind the texture at first GPU use to the current ProGPU device domain; share it with the later Silk.NET window when possible. |
| [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains) and [render targets](https://learn.microsoft.com/en-us/windows/win32/direct2d/render-targets-overview) | Render targets and their bitmaps are device-dependent; sharing is valid only inside the same underlying device domain. | Treat the WebGPU texture as device-dependent. On a genuinely different device, preserve content through the existing bounded CPU readback/upload migration path. |
| [Win2D `CanvasRenderTarget`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasRenderTarget.htm) and [DPI model](https://microsoft.github.io/Win2D/WinUI3/html/DPI.htm) | An offscreen target is drawable and sampleable, is created from a resource-creator device, and owns explicit pixel dimensions and DPI. | Keep one texture serving as both render attachment and sample source, preserving Avalonia pixel size, DPI, premultiplied alpha, and explicit ownership. |
| [WebRender](https://github.com/servo/webrender) | GPU resources belong to the renderer/device and retained work is rendered to device-owned targets. | Keep retained command compilation separate from texture allocation and allocate only when the target is actually drawn or sampled. |
| [Vello](https://github.com/linebender/vello) | A retained scene is independent of the `wgpu` setup; rendering requires an explicit device, queue, and target texture. | Preserve the same boundary: object construction is CPU-only, while the first drawing context selects the device and texture. |
| [WebGPU specification](https://gpuweb.github.io/gpuweb/#texture-creation) | Newly created texture subresources have a zero-bit representation, avoiding disclosure of uninitialized memory. | Treat a newly allocated, never-written RGBA render target as transparent black without allocating or uploading a CPU zero buffer. |
| [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/direct-write-portal), [HarfBuzz](https://harfbuzz.github.io/shaping-concepts.html), and [Parley](https://github.com/linebender/parley) | Text shaping/layout is reusable CPU state and is independent of the raster target’s device lifecycle. | Leave text shaping, fallback, positioning, and caches unchanged. This correction is confined to the offscreen image resource boundary. |

## Adopted lifecycle

1. Construction validates dimensions and records DPI/format/usage in `O(1)`
   time with no GPU or pixel-sized CPU allocation.
2. The first draw or sample chooses the current healthy WebGPU context, or
   creates the existing shared standalone device only when no window device is
   available.
3. A new texture starts as transparent black under the WebGPU initialization
   contract. No CPU zero buffer or upload is created.
4. Same-device sampling is `O(1)`. Cross-device migration is exceptional and
   remains `O(P)` time and storage for `P` pixels so content is preserved.
5. CPU storage remains demand-only for lock, save, or cross-device migration.

Rejected alternatives were constructor-time device creation (breaks Avalonia
lifecycle and adds startup residency), an always-resident CPU mirror (duplicates
pixel memory), and silently dropping a bitmap when its original device differs
from the destination device.
