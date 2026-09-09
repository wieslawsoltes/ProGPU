# Source input through retained bitmap caches

## Application dependency and scope

LibreWPF's core acceptance sequence requires cached content to change after its
first frame without stale rendering or input. The existing retained WPF
`PushVisualCache` sink publishes typed `CacheAsLayer` visuals. Its owner fixture
already verifies drawing IDs, but source input traversal rejected that cache;
ordinary compositor replay instead indexed the raster texture's rectangle.
The package MVP remains the acceptance application, with Toolkit/AvalonDock and
the existing third-party gates retained. This is a source-backed integration
dependency, not a reproduced application result or a reduced replacement app.

## Managed connection

`Compositor.TryCaptureSourceCompositeInput` shares the existing pre-effect source
traversal with optional visual caches. Capture occurs before raster admission,
using the original local/parent transform and source clips. Raster preparation
and final texture composition suspend hit writes in a finally-restored scope.
The cache's texture size, pixel snapping and render scale do not become source
input. Zero render scale may suppress raster production without suppressing the
actual typed source geometry. Own content and children are captured once, in
source order, without calling OnRender for input or using Size as hit geometry.

`RequiresLayerCache` sources remain rejected: their prepare/refresh ownership
contract differs from optional caching of already materialized commands. Spatial
opacity masks and effects without declared source mapping also remain explicit.
Generic visuals, disabled GPU input, raster cache keys and offscreen rendering
retain their established paths. No CPU pixels, cache-bypassing drawing replay,
WPF-local algorithm or per-control native call is introduced.

The original implementation provenance is ProGPU's
`GpuRenderCommandHitTestCache.SourceVisual.cs`, `ApplyAndDrawEffect`,
`ApplyAndDrawLayer` and `EnsureLayerTexture` at `d5c98473`. This connects their
existing source traversal rather than importing third-party implementation.
The traversal is O(V + C) for source visuals and retained commands during index
rebuild, not per query. Scope/ownership traversal is dependency-bound; existing
intrinsic transform/geometry encoding and canonical GPU queries are unchanged.
Compiled-scene reuse, raster reuse and final allocation/performance measurements
remain independently required. No speed claim follows from this connection.

## Paired native work: still required, not admitted

This behavior applies to C++ MIL too; the managed connection is not parity
completion. Native `append_visual` still rejects indexed cached visuals.
`add_visual_cache_layer` emits ordinary positive-scale content in cache-local
coordinates, resets state for that raster domain, and gives the final composite
an optionally pixel-snapped transform. Using either raw cache-local geometry or
the snapped composite transform would change source interaction. Scale zero
currently suppresses content emission altogether.

The native connection must carry explicit unsnapped source-frame metadata through
cache scopes into the existing builder hit-index producer. It must map primitive
and clip coordinates through nested caches, retain owner order and point/region
policy, and preserve input when raster scale is zero. Source clip caches must be
qualified by that frame; raster bounds must not become geometric clips. Reuse
the existing builder resources and intrinsic mapping, not a second scene decoder,
duplicate source composer, noncached renderer fallback or texture-bounds input.
Keep complete-index admission closed until this branch is implemented and paired
fixtures are authored. Existing native/managed PRs remain delivery dependencies.

## Design references

The existing [cross-engine design decisions](native-mil-hit-test-ownership.md#design-references-and-decisions)
were revisited on 2026-09-09: Skia/SkParagraph, WebRender, Direct2D/DirectWrite,
Win2D, Vello/Parley and HarfBuzz continue to motivate separate retained geometry,
spatial/clip ownership and reusable text layout. Their primary links are retained
in that record; no foreign implementation is copied. The additional
[WPF BitmapCache contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.bitmapcache?view=windowsdesktop-10.0)
explicitly preserves mouse-over interaction while caching the control's bitmap.
Adopt this separation, not raster alpha/texture bounds as source input.

Startup/lazy initialization, workers, shaping/fallback/variable fonts,
glyph/path keys, eviction and upload batching are unchanged. Raster DPI,
subpixel/hinting and cache invalidation stay in the existing raster path; source
input uses its unsnapped transform independently. Device recovery still rebuilds
the scene/index and renews native ownership. No new execution-policy fallback
or platform-specific input algorithm is added.

## Authored qualification

Managed fixtures cover render scales 0/1/2, fractional source offsets with pixel
snapping, stable texture reuse while rebuilding the index, movement, content
replacement/clear, actual clipping and sibling-state restoration. Existing
Blur/DropShadow fixtures now also cover optional cached effect roots and point-only
children. The WPF retained cache-scope fixture checks actual drawing bounds rather
than the larger cache allocation. Required cached-picture rejection remains.

These fixtures are authored and compiled, not executed. Native cache parity and
matched native fixtures remain unfinished. Final application, shader, module,
platform/package, differential/performance and exact-head CI gates stay mandatory.

## Native positive-scale frame connection

The next implementation now connects ordinary positive-scale native caches.
`source_local_cache` builder metadata carries a copied unsnapped
content-to-parent affine at PushLayer. The raster layer, cache revisions, bounds
and composite transform remain unchanged. MIL selects it only for source-owned
visual caches, never brush-source traversal. Source primitive states, image/point
scopes, glyph bounds and vector clip controls pass through the retained input
frame. Nested layers compose frame mappings; save/layer pops restore their prior
frame and clip identity. Clip resource reuse is qualified by both input frame
and layer scope, not resource ID alone.

Three independent affine rows use NEON/SSE2, retaining multiply/add ordering;
corner/clip-control mapping reuses the existing four-lane producer. No GPU
readback, CPU rasterization or second scene decoding is added. Mapping is O(1)
per state and O(S) per clip's segments; frame storage is O(L) for cached layers.
Stable native index bytes still enter the canonical retained hash/upload path.

Known gaps remain explicit: zero raster scale still requires input-only command
retention; source masks on the cache boundary are rejected; non-axis-preserving
rectangle clips inside a cached frame require exact composed clip topology.
These branches are not successful empty results or permission to expand a clip
to its envelope. Positive-scale admission does not finish cache parity or close
the application gate. The historical blanket rejection above is superseded only
for the newly connected branch.

Native canonical fixtures match managed scales 1/2, fractional placement,
movement, source clipping and replacement/clear. Cached Blur/DropShadow variants
also retain their existing point-only child/update cases. A nested builder fixture
compares intrinsic frame mapping with the scalar affine oracle and verifies
clip-cache separation, sibling restoration, malformed mapping rejection and
reset. Managed source traversal has the matching nested placement fixture; the
module consumer exercises the same public overload. All execution remains
deferred; build results are recorded separately in LibreWPF's delivery report.
