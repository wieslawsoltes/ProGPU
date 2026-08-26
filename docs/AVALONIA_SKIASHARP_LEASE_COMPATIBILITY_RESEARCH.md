# Avalonia SkiaSharp API lease compatibility research

## Scope

This review covers source compatibility for Avalonia custom draw operations
that use `ISkiaSharpApiLeaseFeature`. The implementation is clean-room: it
uses Avalonia's public contract and observable behavior as conformance inputs;
no Avalonia renderer implementation is copied into ProGPU.

The change does not alter shaping, layout, glyph caching, rasterization,
scene compilation, shaders, GPU submission, device loss, or the native C++
renderer. Managed/native rendering parity is therefore not applicable beyond
the unchanged canonical ProGPU scene commands produced by the shim canvas.

## Primary sources and findings

| Source | Finding | Decision |
| --- | --- | --- |
| [Avalonia 12.0.5 lease contract](https://github.com/AvaloniaUI/Avalonia/blob/12.0.5/src/Skia/Avalonia.Skia/ISkiaSharpApiLeaseFeature.cs) | The optional feature returns a bounded lease containing `SKCanvas`, optional `GRContext`/`SKSurface`, current opacity, and optional platform graphics access. | Adopt the public source shape so existing custom draw operation source compiles unchanged. |
| [Avalonia 12.0.5 drawing context](https://github.com/AvaloniaUI/Avalonia/blob/12.0.5/src/Skia/Avalonia.Skia/DrawingContextImpl.cs#L85-L165) | Avalonia makes the backend unavailable while the lease is active, restores ownership on disposal, and returns no platform lease when the renderer has no platform graphics context. | Adapt ownership to ProGPU's existing thread-affine `IProGpuApiLease`; return `null` for unsupported surface and platform objects. |
| [Avalonia custom Skia sample](https://github.com/AvaloniaUI/Avalonia/blob/12.0.5/samples/RenderDemo/Pages/CustomSkiaPage.cs) | Application code probes the feature inside `ICustomDrawOperation.Render`, scopes the lease with `using`, and draws through `lease.SkCanvas`. | Preserve this call shape and lifetime. |
| [Skia canvas overview](https://skia.org/docs/user/api/skcanvas_overview/) and [coordinate spaces](https://skia.org/docs/user/coordinates/) | Canvas state owns the active matrix and clip, and draw calls are transformed through that matrix. | Initialize the ProGPU shim canvas with Avalonia's complete active 2D transform before user drawing. |
| [Skia canvas creation](https://skia.org/docs/user/api/skcanvas_creation/) | GPU contexts are associated with the backing device and are expected to be current for the drawing scope. | Construct the canvas over the leased `WgpuContext`; expose its borrowed `GRContext` wrapper only for the lease lifetime. |

The required wider rendering/text review found no competing public lease
contract to adopt:

- [SkParagraph](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h)
  keeps layout results separate from canvas painting. This change leaves
  ProGPU shaping and layout untouched.
- [DirectWrite `IDWriteTextLayout`](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwrite-idwritetextlayout),
  [Direct2D device contexts](https://learn.microsoft.com/en-us/windows/win32/direct2d/devices-and-device-contexts),
  and [Win2D interop](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/interop)
  expose backend-specific drawing or resource objects, but none can satisfy
  an Avalonia `SKCanvas` contract. Adding a Direct2D bridge was rejected.
- [WebRender's retained display-list pipeline](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  keeps recording and GPU submission separated. The shim follows the same
  ownership principle by recording into the current ProGPU scene rather than
  submitting a second frame.
- [Vello's renderer integration](https://github.com/linebender/vello/blob/main/vello/README.md)
  likewise separates scene construction from rendering to a device texture,
  while [Parley](https://docs.rs/parley/latest/parley/) reuses CPU layout
  state. Neither requires a change to this bounded adapter.
- [HarfBuzz shaping](https://github.com/harfbuzz/harfbuzz/blob/main/src/hb-shape.cc)
  converts Unicode buffers to positioned glyphs independently of canvas
  ownership. The existing ProGPU shaping path remains authoritative.

## 2026-08-26 animated custom-draw checkpoint

The ControlCatalog integration gate now overlays 96 clipped SkiaSharp cells on
the Canvas page. Each cell records rectangle, circle, quadratic/cubic path,
transform, clip, and save/restore operations, and the fixture invalidates on
every benchmark pulse. It therefore exercises the public Avalonia lease shape
with a changing custom visual instead of measuring only construction.

The initial profile found that the wide general `RenderCommand` scratch array
was repeatedly rented, copied to exact size, and returned while the custom
visual changed. The clean-room correction keeps one reusable high-water
recording buffer, compacts analytic circles and ordinary canvas state into
typed retained commands, and reuses one feature object for the drawing-context
lifetime. Recording remains `O(C)` for `C` commands; stable storage is bounded
by the largest observed command count and replay remains `O(C)`. No GPU work,
shader, text shaping, native ABI, or output-quality contract changed.

Three fresh 60-warmup/300-frame Release processes before the command-storage
correction allocated 238.09--307.73 KiB/frame. The final exact candidate,
repeated with 120 warmup and 600 measured frames, allocated 16.02--18.97
KiB/frame: a 14.9x--16.1x reduction relative to the best/worst paired bounds.
Both rendered 282 draws per frame through `SilkNetWebGpuSurface`. Frame-time
distributions were affected by desktop scheduler load, so no throughput
improvement is claimed.

Matched 12-second final-binary Xcode Allocations/VM Tracker, Time Profiler, and
Metal System Trace captures were then run against Preview.60 plus only the
benchmark/type-identity fixture and against the candidate. Persistent native
heap plus anonymous VM was 204,308,320 versus 206,048,400 bytes (+0.85%, within
the one-run launch/driver spread). Metal reported 336/360 submissions and
1,054/1,103 completions, no compiler spills, potential hangs, hang risks, or
command-buffer errors in either capture. Drawable-wait totals were 74.319 ms
before and 6.790 ms after, but the single instrumented runs support only the
absence-of-regression/error gate, not a latency claim. The compact summaries,
manifests, and target logs were retained; raw traces, exports, and task-owned
Xcode scratch were deleted after successful summarization.

Managed/native parity is not applicable to this checkpoint. The optimized
storage is the managed Avalonia retained-recording adapter; both paths still
consume the same expanded ProGPU commands. The native C++ renderer has no
Avalonia `ICustomDrawOperation`, SkiaSharp lease, or managed command-array
ownership, and no corresponding implementation defect was found.

## Package and type-identity decision

The compatibility contract is implemented in
`ProGPU.Avalonia.Rendering`, not `ProGPU.SkiaSharp`. Feature discovery is an
Avalonia renderer responsibility and requires the active draw scope,
transform, opacity, target size, and device. The generic SkiaSharp shim does
not own any of those values.

A mandatory compatibility package remains rejected because it would add an
assembly dependency for applications that compile directly against ProGPU.
An optional binary-identity facade is now available for precompiled consumers;
it is public-signed, contains type forwards only, and is selected at final
build/publish asset resolution by `ProGPU.BinaryCompatibility`.

Source compatibility remains the default. Applications that need an unchanged
precompiled library can opt into the bounded modern-.NET binary profile
documented in
[`PROGPU_BINARY_ASSEMBLY_COMPATIBILITY.md`](PROGPU_BINARY_ASSEMBLY_COMPATIBILITY.md).

## Resulting contract

- Feature discovery is `O(1)` and does not initialize another GPU device.
- One lease creates one bounded `SKCanvas` and one borrowed `GRContext`
  wrapper over the existing recorder/device. Work and storage are `O(1)`
  before application draw calls.
- The canvas target extent uses the active physical `PixelSize`.
- Avalonia's full affine/perspective-compatible 4x4 transform is mapped into
  the shim's 3x3 `SKMatrix` using the same row-vector convention already used
  by `SKMatrix` and ProGPU scene commands.
- Existing ProGPU opacity and clip stacks remain active; opacity is exposed
  for API compatibility and is not multiplied into draw colors a second time.
- `SkSurface` and `TryLeasePlatformGraphicsApi()` return `null` because the
  recorder is not an Avalonia Skia surface and WebGPU is not represented by
  an `IPlatformGraphicsContext` here.
- Disposal removes the canvas command interceptor before releasing the
  thread-affine ProGPU lease.

Focused tests cover feature coexistence, source call shape, target size,
transform mapping, device identity, opacity, unsupported optional objects,
exclusive ownership, command recording, and disposal.
