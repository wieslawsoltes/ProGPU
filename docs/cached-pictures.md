# Shared cached pictures

`ProGPU.Scene.CachedPicture` is a renderer-owned source resource intended for
repeated cached drawing, including the managed BitmapCacheBrush integration.
It is not itself a WPF brush, and does not yet complete that integration.

```csharp
using var source = new CachedPicture(picture, new Rect(10, 20, 200, 100), renderScale: 2);
context.DrawCachedPicture(source);
context.DrawCachedPicture(source, Matrix4x4.CreateTranslation(220, 0, 0));
// Retain source while commands are live. When content changes:
source.Update(replacementPicture, new Rect(10, 20, 200, 100), renderScale: 2);
```

## Contract and ownership

The source retains an independent `GpuPicture` ownership clone; packed commands
and side buffers are shared, not copied. Disposing the input picture does not
dispose that clone. Updating validates bounds/scale and acquires replacement
leases before changing live state. An unchanged storage identity, bounds and
scale is an allocation-free no-op. Mutable brushes, textures or embedded visuals
referenced by the snapshot still require `source.Invalidate()` after changes;
snapshotting is not a deep freeze of referenced objects.

Each source owns one private retained visual with one picture command. Repeated
draws record references to that same owner, not duplicate visual trees or texture
uploads. Consumer transform, clip and opacity remain outside the cache. Rendering
uses the existing embedded-visual dependency/version tracking, layer texture
allocation, device-domain checks, cold capture and warm composition. This source
requires cached rendering even when optional `Compositor.IsCacheAsLayerEnabled`
optimization is disabled. Ordinary optional visual caches retain their policy.

Bounds specify the exact capture rectangle in picture coordinates, not a culling
hint or stretch destination. The source translates content to cache-local origin
and restores origin when compositing. Fractional logical width/height remain exact
in the offscreen projection; texture dimensions are rounded to physical pixels.
Raster scale changes resolution, not logical size. Zero scale/empty bounds paint
nothing. Negative/non-finite sizes/scales and non-finite rectangle edges fail
before mutation. Resource operations and rendering must be serialized on the
rendering thread. Disposing a source makes existing recorded references empty and
invalidates its owner; recording new references throws. GPU texture retirement is
left to the compositor's active-owner cleanup and disposal, not synchronous
texture destruction by the source object.

The four-argument constructor/update accepts `enableClearType`. Generic
construction defaults to true (preserve recorded text modes); WPF supplies the
cache's false default explicitly. False lowers ClearType text/glyph commands to
grayscale inside capture while preserving aliased text. Each nested explicit
cached source uses its own policy; ordinary nested layers inherit the active
policy. The compositor restores the caller policy after capture, including failed
captures. Picture and incremental-page keys include this policy and the exact
projection, so differently configured captures cannot reuse incompatible compiled
text pages. Precompiled DXF buffers cannot rewrite baked text policy and fail
closed when subpixel suppression is required.

Already-rasterized nested layer/effect textures also record their effective
suppression policy. A policy mismatch forces recapture even when source content
and pixel dimensions are unchanged; failed work cannot qualify stale pixels.
Fixtures include direct text, a nested ordinary layer, and a nested effect source
with policy switches. Execution and performance qualification are deferred.

Construction/update do not initialize a GPU. Changed content retains O(L) leases
for L picture resources and O(1) extra command storage; immutable scene data stays
shared. Recording adds one command in amortized O(1). Cold raster work follows
the existing picture compiler and covered pixels; warm consumers composite the
shared texture. Reference/lifetime control is not independent-lane numeric work,
so no new scalar pixel loop, SIMD fallback, shader, readback or upload is added.
No measured performance or memory improvement is claimed.

## Design provenance and applicability

Implementation reuses original ProGPU `GpuPicture.Clone`, packed picture storage,
`DrawingContext.DrawVisual`, `IOwnedRenderCommandCache`, and compositor
`EnsureLayerTexture`/embedded visual tracking. No external implementation is
copied. Public-contract research informed these choices:

- [SkPicture](https://api.skia.org/classSkPicture.html): reuse recorded commands,
  but distinguish culling hints from this resource's exact capture rectangle.
- [Direct2D caching and DirectWrite layout reuse](https://learn.microsoft.com/en-us/windows/win32/direct2d/improving-direct2d-performance):
  retain scene resources and cache color output; keep shaping/layout upstream.
- [Win2D CanvasCommandList](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm):
  separate retained commands from consumer placement and image-like use.
- [Vello Scene](https://docs.rs/vello/latest/vello/struct.Scene.html): retain scene
  encoding; use an O(1) shared reference here rather than per-consumer scene append.
- [Parley Layout](https://docs.rs/parley/latest/parley/layout/struct.Layout.html)
  and [HarfBuzz plans](https://harfbuzz.github.io/shaping-plans-and-caching.html):
  preserve reusable CPU text results; no new shaping, font fallback, variable-font,
  subpixel, hinting or text upload algorithm belongs in this wrapper.

The prior native MIL cross-engine research record (including WebRender's
content/placement separation) remains applicable. Startup stays lazy, visibility
and owner cleanup use existing compositor policy, and GPU batching/device-loss
handling remain in that compositor. Worker preparation and dirty rectangles are
not introduced by this resource. The C++ counterpart is the already implemented
shared local cached-layer contract and BitmapCacheBrush source ownership; this
managed API does not add or alter a native wire record. Paired pixel, lifecycle,
DPI and performance qualification is still required.

## Implementation-first status

### Live typed sources

`CachedPicture(ICachedPictureSource source, bool ownsSource = false)` captures
one initial CPU recording and subscribes to typed `Invalidated` events. Further
events mark the retained owner dirty without recording or submitting GPU work.
Layer preparation calls `Refresh` before sizing; multiple consumers and repeated
events share one successful recapture. An unchanged source takes an O(1) branch.
`Refresh` is also available for explicit host preparation. Live sources reject
manual `Update`; their recorder owns content, bounds and raster policy.

`Capture` returns an owned `CachedPictureSnapshot`. ProGPU acquires independent
picture leases then disposes the snapshot. Invalid descriptors, capture exceptions,
reentrant capture and changes during capture fail without replacing prior source
ownership, leave the source dirty, and propagate to rendering rather than
qualifying stale pixels. Disposal unsubscribes exactly once; `ownsSource` also
disposes the provider, including failed construction. Serialized rendering-thread
access is mandatory. Hosts without change events must explicitly invalidate.

LibreWPF `WpfBitmapCacheBrushCapture.CreateLiveCachedPicture` connects its existing
typed dependency tracker and source recorder to this API. Source/cache dependency
subscriptions are refreshed before recording so an event during capture cannot
be hidden by the tracker's dirty-event coalescing. The host wrapper transfers its
new picture directly, without an extra ownership clone. Callers retain one live
source across consumers; automatic brush-consumer lookup/routing remains open.

This is original ProGPU `CachedPicture`/`Visual` invalidation and compositor layer
preparation, with original LibreWPF tracker/capture adaptation. Construction and
changed recapture cost O(C + V + E + L), including existing recording/graph work
and L retained resource leases; unchanged preparation adds O(1) work with no new
allocation. The event handler performs only dirty/version updates. There is no
numeric pixel loop requiring SIMD, CPU readback, per-primitive native crossing,
shader, additional queue submission or eager GPU initialization.

Public-contract research refreshed for this extension:

- [SkDrawable](https://api.skia.org/classSkDrawable.html): adopt explicit change
  notification and independent snapshot ownership, not its implementation.
- [Direct2D resource reuse](https://learn.microsoft.com/en-us/windows/win32/direct2d/improving-direct2d-performance)
  and [Win2D command lists](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm):
  preserve reusable recorded sources and keep submission outside mutation events.
- [WebRender overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  and [Vello scenes](https://docs.rs/vello/latest/vello/struct.Scene.html): keep
  host recording separate from renderer preparation; this change does not add
  worker scheduling or alter existing visibility/device-domain cache ownership.
- [Parley layout](https://docs.rs/parley/latest/parley/layout/struct.Layout.html)
  and [HarfBuzz plans](https://harfbuzz.github.io/shaping-plans-and-caching.html):
  retain upstream layout/shaping reuse. Font fallback, variable fonts, DPI,
  subpixel/hinting, atlas eviction and demand-driven upload algorithms are unchanged.

Native applicability: C++ MIL already receives host-authored immutable updates
and invalidates shared source pages by resource generation. This managed callback
adapter does not cross the C ABI or change native wire semantics. Native/managed
mutation, resize, failure and disposal differentials remain required, as do
startup/scroll/residency and allocation measurements; no speed claim is made.

CPU fixtures cover source identity, coordinate normalization, independent snapshot
ownership, no-op updates, invalid input and disposal. Authored GPU fixtures cover
two consumers, warm reuse, replacement pixels, zero/changed scale and fractional
source extents. Fixtures are not executed under the current implementation-first
sequence. Release builds of ProGPU.Scene and the ProGPU test graph succeed with
zero warnings/errors; the LibreWPF bridge/test graph also builds, with warnings.
Full renderer, Svg.Skia, native differentials, VM/platform images,
Instruments/benchmarks and CI qualification remain deferred.

LibreWPF now exports a root-policy source through `WpfBitmapCacheBrushCapture` and
uses `PortableBitmapCacheBrushPolicy.TryResolve` for explicit/target/default policy.
Its `CreateCachedPicture`/`UpdateCachedPicture` methods transfer independent
picture ownership into this resource and apply render scale/ClearType policy.
Normal managed brush fills/pens/glyphs/masks still need consumer routing, shared
cache lifetime and typed invalidation integration. Root scroll clips and source
content requiring unsupported recorder scopes fail closed. This does not claim
complete BitmapCacheBrush or MIL/DirectX/Direct2D/COM/Win2D parity.
