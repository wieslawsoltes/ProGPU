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

### Cached LineGeometry and GeometryDrawing consumers

Cached pens now reach the existing shared line-stroke operation from WPF
`DrawGeometry`/`GeometryDrawing`, object and managed entry points, direct native
geometry sinks, and both MIL decoder lanes. This checkpoint recognizes one open
stroked line only: it does not approximate a polyline, closed contour, curve or
combined path as a line. A line's fill has zero area, so even a supplied cached
fill brush does not trigger another source capture. Unsupported cached pens on
other geometries report partial/unsupported output rather than treating a fill
alone as a completely implemented draw.

Source-built `LineGeometry` already publishes `IPortablePrimitiveGeometrySource`.
The new `PortablePrimitiveGeometry.TryGetTransformedLinePoints` uses that descriptor
directly, pairs affine coordinate arithmetic with `Vector128<double>`, and keeps
double precision until the downstream float drawing boundary. It validates kind,
all input coefficients/coordinates and both output points before publishing either
endpoint. No packed path, WPF shape probing, native crossing, device initialization
or buffer allocation is needed for this route. Its work and storage are O(1).
Geometry-local transforms change the spine before the normal-width stroke is
prepared; the outer drawing transform still applies to the finished stroke and
material together. The pen thickness is not multiplied by the geometry transform.

Portable path-only sources use the existing cached native path conversion and
`PrimitivePathGeometry.TryGetOpenLine`, a new O(1) strict classifier. Local media
lines use the existing typed/cached line reader. The classifier requires exactly
one open figure and one stroked line segment, finite endpoints and no per-figure
cap overrides; fill participation does not change its zero fill area. A source
that explicitly cannot publish path data is unsupported, not read through another
object shape. Existing path conversion is O(S) for S source segments on a cache
miss; the primitive-descriptor route avoids it entirely. All stroke/dash bounds,
source leases, raster policy and GPU composition remain in the previously added
ProGPU implementations.

Original ProGPU provenance is `PortablePrimitiveGeometry`, the native MIL affine
line-geometry/spine preparation and sampled-pen path, `WpfPortablePathGeometryConverter`,
`WpfMediaLineGeometryReader`, and `StrokeCoverageGeometry`. The native C++ product
already implements transformed LineGeometry with cached pens; a paired native
fixture now checks the nonuniform geometry-transform/constant-width relative
mapping in direct geometry and GeometryDrawing replay. No C ABI, native product
algorithm or shader change is needed. Generic
live-managed-visual native picture transport is still a separate open gap.

Authored fixtures cover intrinsic transforms against scalar double arithmetic,
overflow/nonfinite/other-kind rejection, strict topology recognition, object/
drawing/MIL routes, path-only versus primitive sources, dashed geometry lines,
zero-area fill avoidance and reporting of unimplemented non-line cached pens.
The primitive-source fixture throws if replay requests a packed path. The
[existing cross-engine research](#retained-cached-source-stroke-coverage) remains
applicable: preserve typed primitive/stroke/material separation and retained
source identity, and derive brush mapping from the widened stroke rather than
fill bounds. Text shaping/layout, font caches, visibility, device loss and GPU
execution policy are unchanged. No speed or rendering-parity claim is made.

General path/closed-shape cached pens, exact zero/tiny dash semantics, all
degenerate/transform/DPI/guideline combinations and the full goal's qualification
remain open. Fixtures are authored only; runtime/image/native/managed/platform/VM,
scalar/SIMD, renderer/Svg.Skia, benchmarks, source audits and CI remain deferred.

Release compilation (2026-09-07): native MIL fixtures succeed; ProGPU.Tests has
0 warnings/0 errors. The final incremental WPF build has 13 warnings/0 errors,
after a broader rebuild with 106 warnings/0 errors. Warnings were not attributed,
and no fixture was run. Incremental warning totals do not imply warning fixes.

### Typed cached-line pens and intrinsic cap bounds

LibreWPF now routes cached-brush line pens before the color/gradient-only pen
adapter. Object calls, managed static/animated calls, native primitive calls,
retained sinks and both raw MIL decoder lanes consume `PortablePenState`, retain
the original brush identity and use ProGPU-owned stroke preparation. The typed
invalidation visitor follows `PortablePenState.Brush`, including cached target
and explicit cache-policy dependencies. Missing/invalid descriptors fail closed.

`StrokeCoverageGeometry.TryPrepareLine` builds the ordinary retained line and,
for dashes, reuses `RenderCommandGeometryCache.TryGetDashedStrokePath` and its
effective per-figure endpoint caps. The prepared undashed coverage is recorded
once, so drawing does not repeat dash preparation. Bounds come from each emitted
line spine and cap, not an inflated fill rectangle. Relative brush mapping uses
these bounds; active command transforms remain outside the source capture.
WPF guideline snapping happens before preparation. Opaque coverage, source alpha
and consumer opacity remain separate. Explicit aliased-edge policy now survives
path-mask recording and both managed compilation paths; old public overloads
remain available and default to antialiased coverage.

Original source provenance: the new paired-coordinate bound implementation ports
ProGPU C++ `Mil/progpu_native_mil.cpp::try_transformed_line_stroke_bounds` and the
cubic portion of `try_get_path_segment_bounds` into `ProGPU.Scene`, restricted
to identity geometry transforms before the outer draw transform. Round caps use
the same two cubic quarters and analytic derivative roots; they are not broadened
to full endpoint circles. `Vector128<double>` handles independent coordinates,
cap controls, bounds reductions and curve evaluation. Scalar root branches are
bounded, data-dependent choices. Generated figure traversal is a bounds reduction.
The new dash preflight pairs interval validation/scaling with intrinsics and a
single scalar tail. `Pen.SetDashPattern(ReadOnlySpan<double>)` takes one owned
snapshot; the existing dash engine still has its own preparation storage.

New preparation is O(D + F) time/storage for D dash intervals and F emitted
figures, plus fixed work per cap. Normal solid lines are O(1). No GPU initialization,
new shader, readback, per-dash submission, native call per primitive or WPF-local
stroker is added. Existing source cache identity, texture reuse, refresh, DPI,
device-loss and eviction policies remain unchanged. Native MIL already has the
paired sampled-pen/widened-bounds path and aliased raster state; its product C++
and wire/shaders are unchanged. A native fixture checks asymmetric-cap relative
mapping against a scalar arithmetic oracle. Managed fixtures cover all cap kinds,
short asymmetric lines, odd/even dashes, a dense scalar cubic oracle for intrinsic
bounds, mask alias metadata, typed replay and pen dependency invalidation.

This is not full stroke parity. The inherited dash engine replaces zero/tiny
scaled intervals with epsilon values, so this new preparation rejects intervals
at or below 0.0001 rather than silently using that approximation. Nonfinite or
overflowing patterns, fixed/hairline width in this bounds helper, and preparations
whose line length divided by minimum interval exceeds one million are explicitly
unsupported. General geometry/primitive pens, LineGeometry/GeometryDrawing
lowering, exact zero/tiny dash semantics, combined/group strokes and complete
transform/DPI/guideline/degenerate-case qualification remain open. General
`DrawCachedPictureStroke` still accepts caller-qualified bounds for fixed/hairline
strokes; the new WPF line preparer does not guess them. Float transport and cubic
round-cap tolerances require native/managed differential qualification; no bitwise
or image-parity claim is made.

The primary contracts and cross-engine decisions in
[retained stroke research](#retained-cached-source-stroke-coverage) remain the
design references: preserve material/stroke separation and command/source reuse;
derive bounds from widened geometry; leave shaping, layout and font caches alone.
This is an original ProGPU cross-language port, not foreign source adaptation.
All authored tests, scalar/SIMD comparisons, image/VM/platform/renderer/Svg.Skia,
performance, source audits and CI gates remain unexecuted pending final validation.

Release compilation (2026-09-07): native `progpu_native_mil_tests` succeeds;
ProGPU.Tests reports 0 warnings/0 errors. The final incremental WPF build reports
11 warnings/0 errors, after a broader rebuild reporting 106 warnings/0 errors.
Warning attribution remains deferred; changing incremental totals are not fixes.

### Retained cached-source stroke coverage

`DrawingContext.DrawCachedPictureStroke` now records a path/pen opacity mask,
one cached-source draw, and balanced optional opacity scopes. It uses the existing
`PushOpacityMask(PathGeometry, Pen, Rect, Matrix4x4)` and ordinary stroke compiler,
not a second stroker or a picture wrapper. Source mapping precedes the outer
transform; the outer transform positions coverage and material together. The
coverage pen is opaque white, independent of the original pen brush and its alpha,
so source alpha and consumer opacity are applied once. Width, asymmetric endpoint
caps, dash cap, join, miter limit, dash offset, and normal/fixed/hairline state are
preserved. Ordinary non-positive widths and zero consumer opacity record nothing.

`Pen.WithBrush` snapshots scalar state with one new pen while sharing private,
immutable dash storage. Both public dash-array reads and assignments still copy,
so mutating either pen cannot change the other's intervals. The replacement brush
is borrowed under the existing retained-brush contract. The stroke helper creates
an independent white brush, rather than exposing a mutable shared singleton.
Path geometry remains caller-owned and immutable during retained use, as with
`DrawPath`; source leases survive caller disposal and parent-picture clones.

The caller supplies stroke coverage bounds before the outer transform. These must
enclose the actual ink, including caps, joins and dashes; fixed/hairline bounds
also depend on target transform/DPI. Bounds are validated for finite positive
extents and finite edges, not inferred from fill bounds. WPF relative material
mapping additionally requires authoritative stroke bounds, not a conservative
allocation envelope. This checkpoint does **not** yet route WPF cached-brush pens:
typed pen preservation and authoritative source bounds across immediate primitives,
geometry drawings and raw MIL replay remain required integration work.

Original ProGPU provenance: `ProGPU.Vector/Brush.cs` owns pen/dash storage;
`ProGPU.Scene/RenderCommand.cs` owns retained path masks and cached-source leases;
`Compositor.PushOpacityMaskValue(PathGeometry,...)` uses
`ResolveStrokeCompileState` and `CompilePathCommand`. No foreign implementation
was copied. Native MIL already lowers sampled pens through its ordinary stroke
coverage and `append_bitmap_cache_brush` shared source pages; no C++ product,
wire or shader change is necessary for this recording helper. The generic
`GpuPictureNativeSceneCompiler` still rejects live managed `DrawVisual` commands;
this API must not be advertised as closing that separate native transport gap.

Research refreshed for this checkpoint:

| Primary contract | Decision |
| --- | --- |
| [Skia paint](https://api.skia.org/classSkPaint.html) | Keep stroke geometry state separate from replacement material; do not copy its implementation. |
| [Direct2D widened bounds](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1geometry-getwidenedbounds) | Require bounds derived from width/style/transform instead of inflating a fill rectangle. |
| [Win2D command lists](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm) and [WebRender overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html) | Retain source identity and commands independently of consumers; compile GPU work lazily. |
| [Vello Scene](https://docs.rs/vello/latest/vello/struct.Scene.html) | Reuse one path with explicit stroke and transform state, not per-dash submissions. |
| [Parley layout](https://docs.rs/parley/latest/parley/layout/struct.Layout.html) and [HarfBuzz caching](https://harfbuzz.github.io/shaping-plans-and-caching.html) | Leave shaping/layout reuse unchanged; this stroke-only addition does not touch font fallback, variable fonts, DPI text policy, or glyph atlas generations. |

Startup, workers, visibility, device-loss handling, source-cache keys/eviction and
demand-driven uploads are unchanged. Recording adds bounded O(1) pen/brush/cache
and lease objects, no dash-count-dependent copy or numerical loop. Existing
geometry compilation keeps its path/dash-dependent work and existing GPU stroke
and mask pipelines; stable replay uses the retained scene. No new CPU fallback,
readback or execution-policy override is introduced. SIMD optimization applies to
the existing shared numerical algorithms, not this fixed-size orchestration.
No speedup or quality/parity claim follows from these structural properties.

Authored managed fixtures cover pen ownership, scalar state, normal/fixed/hairline
metadata, invalid state before recording, source lifetime, and line/rectangle
solid/dashed image oracles with warm source reuse. Matched native MIL fixtures
cover line/rectangle pens with asymmetric caps, dash caps, consumer alpha and
repeated shared source revisions. Fixtures and compilation results are recorded
separately; execution, images, performance, source audits, cross-platform/VM,
renderer/Svg.Skia and CI qualification remain deferred and required.

Release compilation (2026-09-07): native `progpu_native_mil_tests` succeeds;
ProGPU.Tests reports 0 warnings/0 errors; the WPF test graph reports 105
warnings/0 errors. Initial new-fixture compile errors (a line-segment constructor
and a nonexistent layer identity field) were corrected before these builds.
Warning attribution and all fixture execution are deferred; these totals do not
establish clean CI, rendering correctness or performance.

### Cached glyph coverage and authoritative ink bounds

`DrawingContext.DrawCachedPictureWithCoverage` now paints a shared source through
an independently owned coverage-picture clone. Coverage bounds and the outer
transform define the mask; source mapping composes before that outer transform.
Consumer opacity remains outside coverage. The command recording owns both
coverage and source across caller disposal and parent recording clones. This
reuses `PushOwnedOpacityMaskPicture`, existing GPU picture masks and
`DrawCachedPicture`; it is also reusable for future stroke coverage.

Portable native-vector and compatibility glyph DTOs now have additive
`HasInkBounds`/`InkBounds` fields. Bounds include baseline origin but precede the
glyph transform; explicitly empty ink is valid. Source-built WPF calls its
existing `ComputeInkBoundingBox`, adds baseline origin and caches the neutral
result once per initialized GlyphRun, sharing it between both portable exports.
The WPF adapter cache includes bounds availability and value. The native MIL
producer prefers these bounds in its existing ManagedBounds packet field; no
C ABI field was added. Old descriptors retain their existing legacy producer
size-bound fallback, which is not sufficient for cached-glyph coverage.

WPF cached foregrounds now dispatch before media-brush adaptation in direct
object/managed entry points, GlyphRunDrawing replay and raw MIL records. Given
authoritative ink bounds, the sink records one opaque glyph-run coverage command
with original glyph indices/positions, baseline, font and style simulations.
It preserves Aliased mode and lowers other coverage modes to Grayscale because
the mask consumes alpha, not RGB subpixel coverage. Glyph transform and active
outer transform apply to both coverage and material. Missing ink metadata fails
closed for this consumer; local shim/older DTO sources need a real ink-bound
contract, not an estimated box. Empty ink and empty cached sources paint nothing.

Original-source provenance: ProGPU `GpuPictureRecorder`, retained resource
leases, `DrawGlyphRun`, `PushOpacityMaskValue(GpuPicture,...)`, and
`DrawCachedPicture`. Native sampled-glyph coverage already records an owned
glyph mask plus shared cache-source pages in `progpu_native_mil.cpp`; its product
algorithm and wire/shaders are unchanged. New paired native fixtures cover
direct and GlyphRunDrawing consumers with four style-simulation combinations.
Managed fixtures cover coverage/source ownership through clones, composed
transforms and shared glyph arrays; WPF fixtures cover ink-metadata cache changes,
raw/object dispatch, retained coverage and authoritative native packet bounds.

The [WPF ink-bounds contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.glyphrun.computeinkboundingbox?view=windowsdesktop-10.0)
defines origin-relative ink bounds; the bridge adds baseline once. No foreign
implementation is copied into ProGPU. Existing Skia/DirectWrite/Direct2D/Win2D,
WebRender, Vello/Parley and HarfBuzz research remains applicable: retain shaped
glyph data and source identity separately, with lazy GPU masks and unchanged
font, DPI, atlas, upload and device-loss policy. Recording adds O(1) bounded
picture/command/lease allocations per dirty draw, not per glyph. Source WPF
ink calculation runs once per initialized glyph run; no new numerical CPU
buffer loop or pixel fallback is added. GPU glyph work remains O(G) plus mask
raster/composition work over affected pixels, using existing pipelines. No speed
or pixel-parity claim is made.

Release compilation (2026-09-07): native `progpu_native_mil_tests` succeeds,
ProGPU.Tests has 0 warnings/errors and ProGPU.Wpf.Tests has 10 warnings/0 errors.
Fixtures are compiled only. Full glyph images (including style, color-font,
transform, hinting and DPI combinations), lifecycle, native/managed/platform/VM,
renderer/Svg.Skia, performance, source verification and CI gates remain deferred.
Strokes, unbounded masks, legacy glyph producers without ink bounds, retained-owner
mask metadata and remaining source scopes are still implementation work.

### Recording-owned cached opacity masks

`DrawingContext.PushCachedPictureOpacityMask` records a cached source through
the existing retained-picture alpha-mask compositor. Its source transform and
opacity are inside an owned one- or three-command mask picture; the outer
transform applies to both mask placement and the explicit mask bounds. The
parent recording owns that picture through a retained resource lease. Cloned
parent recordings keep the mask/source alive independently of the caller's
source lease and dispose the mask only after the last owner releases it.
Dynamic source capture still runs through `CompileEmbeddedVisual` and
`CachedPicture.Refresh`, so source changes need not rebuild consumer recordings.

LibreWPF now passes raw typed cache masks through bounded object/managed calls,
normal visual-state scopes, drawing-group scopes and both MIL decoder lanes.
Source lookup shares the existing target/cache/device/adapter identity rules;
consumer mapping and opacity do not alter source capture. Empty targets,
zero brush opacity and singular mappings produce transparent masks, not no-op
scopes. Missing descriptors or unusable bounds report unsupported. Direct sink
calls throw when a required cached mask cannot be represented. BitmapCacheBrush
instances bypass brush-only retained-owner mask metadata and use normal typed
command scopes, because that metadata cannot yet own the source lease. The
unbounded object/managed PushOpacityMask overload remains a gap for nonempty
cached sources: deferred painted-content bounds are not guessed from the source.

Original implementation provenance is `GpuPictureRecorder`, `DrawCachedPicture`,
`RetainedResourceLease`, and `Compositor.PushOpacityMaskValue(GpuPicture,...)`.
No new compositor shader, pixel conversion, GPU submission point or P/Invoke is
introduced. Recording uses O(1) bounded wrapper/command storage and one source
lease per mask, plus the existing source-lease lookup/deduplication cost. It is
not allocation-free on dirty recording. Stable replay retains the wrapper.
Mask composition uses the existing GPU mask target/passes; raster work scales
with affected mask pixels and actual source recapture cost. No CPU pixel loop
or scalar fallback is added, and no speed improvement is claimed.

Native applicability: `add_spatial_opacity_mask` already records a sampled
cache-brush fill into an owned child semantic scene and consumes its alpha;
`append_bitmap_cache_brush` supplies shared source pages and consumer placement.
Those C++ algorithms and their wire/shader contracts are unchanged. A paired
native fixture now inspects direct, drawing-group and visual picture masks,
their nested shared source pages, source dimensions, relative mapping and
single application of consumer alpha. Managed fixtures cover mask/source lease
lifetime, parent cloning, independent transforms, rejected parameters, a GPU
alpha oracle, warm source reuse and deferred recapture after zero-scale updates.
WPF fixtures cover all bounded scope producers and transparent empty masks.

The [WPF opacity-mask contract](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/opacity-masks-overview)
defines alpha-only masking combined with content opacity. It informed independent
mask/color and placement assertions, not implementation source. The existing
Skia/Direct2D/Win2D/WebRender/Vello/Parley/HarfBuzz research record above remains
applicable: separate source generation from consumer coverage and preserve lazy
capture, layout/shaping caches, DPI, atlases, uploads, batching and device-loss
ownership. No font or pipeline policy is changed by this adapter.

Release compilation (2026-09-07): native `progpu_native_mil_tests` succeeds,
ProGPU.Tests has 0 warnings/errors, and ProGPU.Wpf.Tests has 1 warning/0 errors.
All fixtures remain unexecuted. Full native/managed images, nested state and
lifetime qualification, platform/VM/sample comparisons, renderer/Svg.Skia,
performance, source verifiers and CI gates are deferred, not waived. Strokes,
glyphs, unbounded mask scopes, retained-owner mask metadata and remaining source
scope combinations still require implementation and final qualification.

### Rounded cache-brush fills and radius normalization

`DrawingContext.PushRoundedRectangleClip` now records one retained clip using
ProGPU's original `PrimitivePathGeometry.CreateRoundedRectangle`: four lines
and four analytic arcs, clamped independently to half the extent. The transform
remains on the command. A zero radius axis is square. Positive radii, including
values below the old rectangle-classification epsilon, retain arcs and cannot
be classified as rectangular clips. Invalid generic clip parameters throw
before recording; this API requires nonnegative finite radii and finite positive
bounds. LibreWPF normalizes negative radii to zero and clamps large finite
double radii before float narrowing. Unrepresentable positive radii fail closed.

Direct object/managed rounded calls and raw MIL rounded/animated records now use
this clip around the shared cached source, retaining opacity, brush mapping,
ordinary pen replay and unsupported-animation accounting. No shim geometry or
pixel copy is introduced. This connects fills, not cache-brush pen materials,
glyph foregrounds or opacity masks.

Paired C++ work normalizes immediate rounded radii in double precision after
animation resolution. Both early validation and resolved-value validation now
accept finite out-of-range radii and clamp them to `[0, extent/2]` before float
conversion. RectangleGeometry resource validation is a separate unchanged
contract. Original-source provenance is `PrimitivePathGeometry.cs`, the existing
retained clip command, and `append_rounded_rectangle_path` plus immediate shape
dispatch in `src/ProGPU.Native/src/Mil/progpu_native_mil.cpp`. Native sampled
rounded fills already use four analytic arcs; other native WPF geometry paths
also use `make_wpf_rounded_rectangle_geometry` with cubic corners. This work
does not establish bit-identical output across those representations.

The [WPF rounded drawing contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.drawingcontext.drawroundedrectangle?view=windowsdesktop-10.0)
supplies radius-clamping and separate fill/pen semantics, not implementation
source. The cross-engine research recorded above remains applicable: retain
source identity and lazy capture independently from consumer coverage; do not
change shaping, font reuse, atlas eviction, DPI, upload, batching or device-loss
policy for a new primitive consumer. Setup is O(1) time and bounded eight-segment
storage; rasterization uses the existing GPU vector clip path. This introduces
no compute-heavy CPU buffer loop, new shader, readback or queue submission.

Authored matched fixtures cover analytic/clamped/tiny/zero radii, native source
layers and curved masks, managed command transforms, transactional invalid input,
WPF object fills and raw MIL animated diagnostics. Release compilation succeeds
for native `progpu_native_mil_tests`, ProGPU.Tests (0 warnings/errors) and
ProGPU.Wpf.Tests (7 warnings, 0 errors). The MIL coverage ledger was regenerated;
its counts are unchanged and are ingress coverage, not parity evidence.
Fixtures were not executed. Full renderer/Svg.Skia, native/managed images,
platform/VM, performance, source verification and CI gates remain deferred and
required before completion. Cached-brush strokes, glyphs, masks and remaining
source scopes are still open.

### Target-anchored aliases and direct ellipse fills

`PortableBitmapCacheBrushCaptureSource` is an immutable typed capture anchor: it
holds target and optional explicit cache identities, with opacity one and no
consumer transforms. LibreWPF lookup now keys those identities plus the device,
viewport cache and adapter context. Distinct brushes using the same target and
same explicit cache (or both selecting target/default policy) share one source.
Changing the first brush's target cannot retarget the shared recording used by
another brush. Consumer brush state stays on the normal retained WPF dependency
graph; the source provider subscribes only to its anchored target/cache graph.
Different explicit cache objects remain distinct even when their current values
match, because either can mutate independently.

`DrawingContext.PushEllipseClip` records the original ProGPU four-arc analytic
ellipse as one retained geometry clip and preserves its command transform. WPF
direct ellipse calls use this reusable API rather than allocating a shim ellipse
or widening its bounds into a rectangle clip. Zero-area fills paint nothing;
negative/nonfinite radii or values outside the float renderer domain fail closed.
Object and managed ellipse calls, including their animation overloads, route to
the source fill path while retaining unsupported-animation diagnostics.

The managed MIL decoder now dispatches raw typed BitmapCacheBrush resources for
rectangle, ellipse and geometry records before attempting media-brush conversion.
Both native-primitive and typed decoder lanes use the same dispatcher, preserve
regular pen replay, and account for unsupported animation handles. Rounded cache
brush records explicitly report unsupported until their exact clip route exists;
they must not be counted as applied after converting their brush to null.

Original-source provenance is ProGPU's `PrimitivePathGeometry.CreateEllipse`,
retained geometry clip commands, neutral bitmap-cache descriptors and the C++
MIL target/cache resource identity and consumer mapping contract. Existing
SkDrawable, Direct2D/Win2D, WebRender, Vello/Parley and HarfBuzz research above
continues to apply: retain capture/consumer separation and lazy rendering;
shaping/font caches, DPI/atlas eviction, uploads, GPU batching and device-loss
algorithms are unchanged. Anchoring, key comparison and primitive setup are O(1),
with bounded four-arc recording storage and no numerical whole-buffer CPU work,
readback or new shader. No speed or pixel-parity claim is made.

Native C++ already clips and maps these cache-brush consumers over shared source
pages; these changes connect the managed path without new wire records or native
callbacks. Authored fixtures cover distinct-brush sharing, target replacement
isolation, exact native arc clips and raw MIL resource dispatch. Full managed/native
image/lifecycle, platform/VM, performance, renderer/Svg.Skia, verifier and CI
qualification remains deferred. Rounded calls, cached-brush strokes, glyphs,
masks and remaining source-scope combinations are still implementation work.

### Recording-owned source lookup and WPF fills

`CachedPictureSourceCache<TKey>.Acquire` creates one live source per capture key
and returns independent `CachedPictureLease` objects. The factory runs only for
a missing entry, recursive acquisition is rejected, and the last lease removes
the entry and disposes the source/subscriptions. Closing the lookup rejects new
acquisitions but leaves outstanding leases valid. The lease's Picture property
is borrowed; callers dispose the lease, never the shared picture.

`DrawingContext.DrawCachedPicture(CachedPictureLease)` retains independent
ownership once per source identity per recording. Existing picture snapshot and
clone leases extend that lifetime; clearing one drawing context cannot invalidate
another retained recording. Lookup is expected O(1); recording uses the existing
O(R) retained-resource identity check for R resources, with O(1) command insertion
and only one durable source lease per recording. No performance claim is made.

LibreWPF uses a rendering-thread lookup keyed by brush reference, device context,
viewport-cache identity and image-adapter delegate identity (target/method equality,
not arbitrary object reflection). Keys are removed with final recording ownership.
This implements reuse of the same brush in a compatible capture context; sharing
across distinct brush objects with equal target/policy is still open. Providers
and command disposal follow the same serialized rendering-thread contract.

Typed GeometryDrawing fills and object/managed render-data geometry/rectangle
calls now clip to exact native geometry, apply opacity and consumer-relative then
absolute brush mapping, and record the leased source. There is no TileBrush
viewbox/stretch/tiling or source-pixel copy. The shared interop mapping follows
the original C++ `append_bitmap_cache_brush` affine contract, evaluated in double
before conversion to the managed float domain. Singular maps paint nothing;
invalid numeric state fails closed. Cached source contents remain independent
of the fill's transform, opacity and shape coverage.

Provenance: original ProGPU `RetainedResourceLease`, DrawingContext picture
snapshot ownership, CachedPicture source lifecycle, and native MIL
`append_bitmap_cache_brush` in `src/ProGPU.Native/src/Mil/progpu_native_mil.cpp`.
The earlier SkDrawable/Direct2D/Win2D/WebRender/Vello/Parley/HarfBuzz public-contract
research applies: adopt explicit ownership and retained source/consumer separation;
do not change shaping, fonts, atlas eviction, device-loss handling or GPU batching.
The mapping is fixed-work O(1) arithmetic, not a whole-buffer CPU kernel. GPU
work and texture residency remain in the existing shared layer compositor.

Authored fixtures cover mapping order, shared acquisitions, recording/clone
ownership, closed-lookup behavior, exact clip/opacity command scopes, and typed
source subscription lifetime in GeometryDrawing/object render-data fills. Tests,
native/managed image differentials, VM/platform comparisons, renderer/Svg.Skia
gates and allocation/performance measurements are deferred. Native C++ already
has this fill/mapping/shared-page behavior and needs paired qualification, not a
new callback or wire record. Direct ellipse-call routing, cached-brush strokes,
glyph foregrounds, masks, target/policy aliasing and broader parity remain open.

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
Managed geometry/rectangle/ellipse fills now have source lookup and recording-owned
lifetime/invalidation integration, including distinct-brush target/cache identity
sharing and raw typed MIL dispatch. Rounded fills now use the shared analytic
clip described above, and bounded opacity masks use recording-owned picture
sources. Glyph foregrounds with authoritative ink metadata now use retained
coverage. Pen, legacy glyph metadata, unbounded mask and retained-owner mask-metadata work
remains open. Root scroll clips and source
content requiring unsupported recorder scopes fail closed. This does not claim
complete BitmapCacheBrush or MIL/DirectX/Direct2D/COM/Win2D parity.
