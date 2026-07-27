# ProGPU Avalonia compositor replacement architecture

## Decision

The target is a source-built, version-pinned Avalonia composition runtime whose
public assembly and API identities remain Avalonia-compatible while ProGPU owns
the retained render scene, resource residency, GPU compilation, render graph,
and presentation.

The first supported lane is Avalonia 12.0.5. Avalonia 11 remains on the current
`IPlatformRenderInterface` backend until the Avalonia 12 composition contract
and ControlCatalog conformance gates are complete.

Avalonia's `ITextShaperImpl` contract is independently replaceable. The direct
ControlCatalog lane uses `ProGpuTextShaper` and the managed
`ProGPU.Text.Shaping` implementation by default; `--harfbuzz` selects the
previous HarfBuzz implementation for differential correctness and performance
measurement.

This design does not use runtime reflection, detours, method replacement,
`ConditionalWeakTable` sidecars, IL weaving, or approximate private-API probes.
The replacement is compiled from the pinned Avalonia source submodule and the
original ProGPU integration sources. Build and package validation fails closed
when the exact Avalonia version, assembly contract, or source revision differs.
The final exact-source renderer and Silk.NET assemblies are also inspected at
the ECMA-335 metadata level; packaging rejects runtime reflection/emit,
`Activator`, `AssemblyLoadContext`, and `UnsafeAccessor` references. The
standalone legacy renderer package remains a separate compatibility artifact
and is not accepted as the replacement compositor binary.

Windowing is independent from compositor selection. The macOS exact-source
lane supports `UseAvaloniaNative().UseProGpu()` by importing Avalonia's
CAMetalLayer drawable into WebGPUSharp/Dawn and exchanging typed Metal
shared-event timeline values. Avalonia retains its existing native windowing
services; ProGPU owns rendering. This lane performs no readback or full-frame
presentation copy and does not load Silk.NET windowing.

## Baseline boundary

The package-compatible `UseSilkNet().UseProGpu()` baseline replaces windowing
and platform drawing, but not Avalonia composition:

```text
Avalonia Visual tree
  -> CompositingRenderer
  -> Avalonia client composition objects
  -> Avalonia batch stream
  -> Avalonia ServerCompositor and server visual tree
  -> IDrawingContextImpl operations for the current frame
  -> ProGPU recorded drawing visual
  -> ProGPU compositor
  -> WebGPU surface
```

`SilkNetPlatform.Initialize` constructs
`Avalonia.Rendering.Composition.Compositor`. Its concrete implementation creates
`ServerCompositor`. `ServerCompositionTarget.Render` walks the server visual
tree and records a flattened platform drawing context. Disposing ProGPU's
`DrawingContextImpl` then wraps that frame's operations in a ProGPU visual and
calls `RenderOffscreen`.

This path already avoids CPU readback for a live Silk surface and benefits from
ProGPU rasterization and compiled-scene reuse. It still retains two composition
layers and prevents ProGPU from using stable Avalonia composition identity as
its scene identity.

Silk.NET now provides a typed `IPopupImpl` rather than forcing every Avalonia
popup into the main-window overlay. Popup windows retain their native parent,
managed Avalonia placement contract, non-activating show behavior, taskbar and
decoration suppression, nested-popup ownership, and synchronous cleanup when
created but never shown. Each popup owns only its presentation surface and
per-target renderer state. The first initialized native `WgpuContext` supplies
the shared instance, adapter, device, queue, and reference-counted device
lifetime; disposing the owner before a popup does not invalidate the popup's
device. `PROGPU_AVALONIA_SHARE_WGPU_DEVICE=0` preserves an exact-binary
differential lane.

This follows WebGPU's presentation model: device creation and presentation
context creation are independent, and one device may render to multiple
canvases. A 60-warm-up/180-frame ControlCatalog Buttons popup comparison kept
120.90 FPS with sharing versus 120.43 FPS with isolated devices, reduced
tracked Metal allocation by 512 KiB, and used zero retained fallback nodes.
Xcode Metal System Trace recorded 418 versus 424 command-buffer submissions and
56.845 versus 72.559 ms aggregate drawable wait in matched rolling windows,
without a new stall pattern. Whole-process footprint differed by less than
1 MiB and is treated as run noise. The compact evidence is in
`artifacts/avalonia-popup-shared-device-instruments-20260726`.

Each surface still owns its mutable compositor/atlas/scene state. The shared
device domain now reference-counts immutable shader modules, ABI-versioned
bind/pipeline layouts, and semantic render/compute pipelines across compatible
contexts while each surface retains its mutable compositor, atlases, targets,
and scene state. The matched two-target resource report reduced layouts from
20 to 8, shader modules from 8 to 4, render pipelines from 9 to 5, and compute
pipelines from 4 to 2. Exact WGSL plus typed descriptor keys reject incompatible
reuse, and disposal-order coverage keeps the domain alive until its last
borrower exits.

## Implemented retained-scene slice

The pinned 12.0.5 source lane now has a compile-time, friend-assembly-only
retained visual-tree seam. It preserves Avalonia's public assemblies, client
composition API, batch stream, render-thread scheduling, animation evaluation,
dirty-region ownership, and fallback behavior while transferring persistent
render-scene ownership to `Avalonia.ProGpu`.

```text
Avalonia ServerCompositionTarget revision transaction
  -> device-context-owned ICompositionServerBackend
  -> AvaloniaCompositionScene stable-ID map
  -> persistent ProGPU ContainerVisual hierarchy
  -> retained per-node commands / GpuPicture resources
  -> DrawVisual at exact host-command z-order
  -> ProGPU compositor and WebGPU surface
```

`ServerCompositor` resolves the typed backend once when Avalonia creates the
platform render-interface context. The backend, rather than a transient
drawing context or offscreen cache, owns one retained scene per composition
target. Target disposal, render-target corruption, graphics-context loss, and
context disposal release that state deterministically. The drawing context is
only the current typed WebGPU command encoder. The earlier
`ICompositionVisualTreeDrawingContextFeature` remains as a differential
fallback in the same exact binary, but the strict source and package lanes
require nonzero `RetainedCompositionServerBackendRenderCount`.

The first synchronization is `O(V)` for `V` attached server visuals. Property
and content changes then provide a deduplicated list of changed server visuals,
so synchronization is `O(C)` before ancestor fallback resolution for `C`
changed nodes. Child collection changes request a transactional full topology
sync. The backend acknowledges a target revision only after synchronization,
so a concurrent revision cannot be accidentally consumed.

Each ProGPU mirror node preserves stable identity, child order, visibility,
opacity, size, local transform, rectangular clipping, and its retained command
cache. Draw-list content is separately keyed by stable render-data identity and
revision. Visual-state and draw-list-content revisions are independent:
transform, opacity, size, and clip deltas update the mirror without re-recording
an unchanged draw list. ProGPU-backed Avalonia geometry implementations map
directly to retained `Visual.GeometryClip` paths, so the clip applies to local
content and descendants without flattening that subtree. Unknown platform
geometry implementations remain conservative fallbacks. Content for invisible
subtrees is materialized only when it becomes renderable. Mirrored nodes also
expose a typed empty-command signal through `IOwnedRenderCommandCache`; the
compiler then skips command-cache lookup and playback for empty nodes while
still applying their transform, opacity, clips, hit-test state, and children. A
persistent host root compares the small supported outer command stream exactly
and invalidates only when that stream changes.

Supported opacity masks, blur/drop-shadow effects, and clipped adorners are
represented by typed ProGPU scene state. Linear, radial, and conic gradients
use native GPU mask brushes; conic angle is carried as a rotation about the
resolved center so the shared sweep shader keeps its general start/end-angle
contract. Blur and shadow nodes retain explicit uneffected subtree bounds,
Gaussian sigma, raster padding, offset, color, and opacity so offscreen
allocation follows actual content instead of layout-size assumptions.
Unsupported custom effects and non-ProGPU geometry implementations are isolated
at the nearest atomic subtree and rendered by Avalonia's typed traversal into
that node's retained commands.
Render and text options are merged with Avalonia's nearest-node inheritance
order. The pinned server publishes each changed visual's local typed option
values, and ProGPU propagates the resulting presentation generation through
its stable mirror hierarchy. Compact retained commands preserve a typed mask
of fields which inherit presentation state; texture sampling, text rendering,
and text hinting are resolved when the command value is expanded for
compilation. Re-recording therefore updates compact storage in place and does
not advance content identity when only those inherited fields changed. Each
incremental page key includes the exact effective presentation values on which
its commands depend, so alternating states reuse collision-free page variants
without hashing or reflection. Setting
`PROGPU_AVALONIA_RETAINED_SCENE=0` disables the visual-tree seam and exercises
the exact same binary through the typed flattened path for differential pixel,
memory, and timing validation.

The exact-source ControlCatalog enables
`ProGpuOptions.RequireNativeCompositionScene` by default. An unsupported mask,
effect, geometry clip, or clipped-adorner contract therefore fails at the
typed retained-scene boundary instead of silently recording an
Avalonia-flattened subtree. `--allow-composition-fallback` is the explicit
transitional diagnostic lane and is not accepted by the conformance sweep.

Avalonia bitmap caches are native ProGPU cached layers. Their
`RenderAtScale` value controls physical texture resolution, including
non-positive suppression; `SnapsToDevicePixels` aligns the composed origin in
physical coordinates; and `EnableClearType` controls whether inherited
subpixel text is preserved or converted to grayscale within the cached
subtree. Cache-option changes invalidate the layer and request one
transactional synchronization when descendant text options can change.

Current evidence is pixel-correct. Buttons records
six outer host commands and preserves 787 retained scene nodes, compared with
293 flattened outer commands. Four representative pages plus a bounded
elliptical visual-clip fixture and an inherited aliased-text fixture are
byte-identical between retained and flattened modes, including hidden-page
activation. Native blur and offset/color/opacity drop-shadow fixtures also
produce byte-identical retained and flattened output, differ from the
no-effect baseline, and use zero fallback nodes. Both effect lanes execute the
same ProGPU compute kernels through independent Avalonia traversal contracts.
Separating state/content revisions and deferring invisible content reduced the
Buttons retained compile cost from 0.762 ms to 0.439 ms and brought allocation
within nine bytes/frame of the exact-binary flattened lane. The retained lane
still compiled about 0.084 ms more CPU work in that continuously-changing
comparison. A later paired 180-frame run of the typed empty-command fast path
reduced retained compilation from 0.715 ms to 0.591 ms; short desktop runs were
noisy, so this is recorded as a bounded hot-path result rather than a general
speedup claim.

The compiler now also supports bounded incremental scene pages for explicitly
owned immutable command caches. The exact-source Avalonia mirror opts in through
the typed `IIncrementalRenderCommandCache` contract; generic and mutable owned
caches do not. A page is keyed by its content revision and every inherited or
target state which changes compiled output. Unsupported masks, effects,
extensions, embedded visuals, GPU-transform scopes, non-solid page brushes, or
unbalanced composition scopes fail closed to ordinary compilation. Replayed
pages preserve stream order and merge compatible draw calls across boundaries.
An updated content revision removes older revisions for the same visual while
transform/state variants and the global store remain LRU-bounded.

The assembled scene buffers use fixed 4 KiB comparison ranges and aligned
partial WebGPU queue writes. First use, buffer replacement, and growth upload
the full live range; later frames upload only ranges whose bytes changed. The
algorithm performs `O(B)` bounded CPU comparison and `O(D)` queue transfer for
`B` live scene bytes and `D` dirty bytes, with `O(B)` comparison-shadow memory.
`PROGPU_AVALONIA_INCREMENTAL_SCENE_PAGES=0` disables both page reuse and
incremental uploads in the same binary for differential measurement.

The inherited drawing-options stress fixture alternates aliased and grayscale
text presentation while the general benchmark alternates effective opacity.
Exact content classification reduced page compilation from 599 per
600-frame run to zero. A dedicated 32-byte `GpuTextStyle` stream now carries
solid-run color, effective opacity, and text-rendering mode independently from
the immutable 96-byte glyph instances and the global vector-brush table.
Incremental pages normalize style indices to page-local values and append
styles in deterministic tree order, so changing presentation does not
renumber unrelated vector or text data. Non-solid and static text retain the
legacy per-glyph path, and telemetry reports its vertex count explicitly.

Three fresh 120-warmup/600-measured-frame Release processes held exactly
3,780 managed bytes/frame, 119.45-119.96 FPS, 67,088 page hits, zero page
compilations, and zero fallback nodes. Each run transferred 920,064 bytes,
all from the 1,536-byte text-style live range; glyph, vector, brush, index, and
texture uploads were zero. This is 96.7% below the previous 28,138,752-byte
glyph-instance path. The extra persistent GPU allocation is a bounded
1,536-byte style buffer for this scene. Retained and flattened screenshots
remain byte-identical.

On an Apple M3 Pro running macOS 26 and .NET 10 Release, a paired
60-warmup/180-measured-frame Buttons run reduced scene-buffer transfer from
14,930,160 to 733,184 bytes (95.1%), average compilation from 0.6141 to
0.5636 ms, average upload from 0.3014 to 0.1840 ms, and average compositor time
from 1.4617 to 1.3188 ms. The live store stabilized at 235 pages/243,944 bytes
plus a 106,496-byte upload shadow. The exact-source retained/flattened pixel
gate remained byte-identical. BitmapCache default, scale, fractional-snap,
ClearType-on, and ClearType-off fixtures also remain byte-identical with zero
fallback nodes. Canvas's linear-gradient opacity mask is compiled through
ProGPU's native GPU mask pass and is likewise byte-identical with zero fallback
nodes. A 23-degree conic-gradient mask is also byte-identical and differs from
the unmasked baseline, validating Avalonia's above-center angle convention
through the native rotated sweep brush. The default unclipped AdornerLayer page
is also byte-identical with zero fallback nodes because Avalonia has already
materialized the adorner transform. Clipped adorners retain the ordered
transformed ancestor rectangle/geometry clip chain; Clipboard and Notifications
validate that path. Scene and image masks are retained as ProGPU pictures, and
HeaderedContentControl validates the GroupBox `VisualBrush` border-gap mask.
Avalonia blur and drop shadow map to bounded ProGPU offscreen effect nodes and
preserve the same pixels through the flattened effect-scope contract.
The 70-page exact-source census has no observed main-scene fallback
after these targeted validations. Native completion of unobserved semantics
and the full acceptance matrix remain required before replacing the flattened
path without qualification.

## Target boundary

The Avalonia public/client composition API remains the compatibility frontend:

```text
UI thread
  Avalonia CompositionObject API
  -> existing typed serialization and atomic batch contract

Render thread
  Avalonia protocol host
  -> typed ProGPU composition backend
  -> compact retained ProGPU scene
  -> visibility, cache, upload, and render-graph planning
  -> WebGPU queue and Silk.NET presentation target
```

The protocol host preserves commit ordering, batch completion, render-thread
jobs, object disposal, readback, and animation-visible behavior. It does not
remain the authoritative render scene. Every server object has a stable compact
ProGPU handle and applies deserialized property deltas directly to a
generation-checked ProGPU scene store.

The intended steady-state cost is proportional to changed plus visible scene
data. An unchanged subtree is not traversed, re-recorded, re-tessellated, or
re-uploaded. Transform, opacity, brush parameter, child topology, content, and
effect topology changes remain distinct invalidation classes.

## Typed backend seam

The pinned Avalonia source gains an internal composition backend contract. It is
not a new public Avalonia promise. `Avalonia.ProGpu` implements it through the
existing signed friend-assembly relationship.

The contract must cover these operations without exposing Avalonia server
implementation objects as an unversioned public API:

- create and destroy one backend per platform graphics device;
- create, resize, suspend, resume, and destroy composition targets;
- allocate generation-checked visual and resource handles;
- apply atomic batches of typed property and topology changes;
- update animation channels at a specified compositor timestamp;
- compile and render a target using logical size, physical pixel size, scaling,
  color/alpha format, damage, and presentation state;
- complete processed and rendered batches in Avalonia order;
- capture a target or subtree without changing its retained state;
- invalidate every device-owned handle as one device-loss generation change.

The factory is resolved once when `Compositor` is created. Target and visual hot
paths hold direct typed references or compact value handles. There is no service
lookup, reflection, string property name, or weak-table access per node or draw.

Avalonia's current server implementation remains the default factory in the
pinned fork. ProGPU registration selects the ProGPU factory before the
compositor is constructed. Unsupported operations fail conformance tests; they
do not silently switch an individual visual back to the old compositor in the
final package.

## Scene representation

The compatibility layer lives in a new `ProGPU.Avalonia.Composition` project.
Avalonia types do not leak into generic `ProGPU.Scene` APIs.

The first representation uses paged, generation-checked stores with bounded
growth:

| Avalonia concept | ProGPU representation |
| --- | --- |
| container visual | group handle plus contiguous or pooled child-handle range |
| draw-list visual | immutable interned display-list handle plus content revision |
| solid-color visual | analytic rectangle instance and brush handle |
| surface visual | texture/image handle with tile, sampling, alpha, and color metadata |
| transform/opacity/visibility | compact node properties and dirty bits |
| rectangular clip | scissor when axis-aligned, otherwise affine analytic clip |
| canonical rounded clip | affine analytic per-corner ellipse coverage |
| general/nested geometric clip | retained texture-mask fallback |
| opacity mask | render-graph mask input |
| effect | typed effect node and parameter block |
| bitmap cache | revision- and target-generation-sensitive cached layer |
| custom visual | typed retained display-list producer and message queue |
| composition target | Silk/WebGPU surface or offscreen texture target |

Handle reuse increments a generation. Stale references fail in diagnostics and
are ignored only after their owning Avalonia object has been disposed through a
successfully applied batch.

No one-to-one managed ProGPU object is required for every visual. CPU hot data
is stored in compact arrays or pages; immutable drawing, geometry, brush, image,
glyph-run, clip, and effect resources are interned separately. GPU buffers grow
geometrically and expose their capacity and active bytes through diagnostics.

## Invalidation and rendering

The backend preserves these independent dirty classes:

- transform;
- opacity;
- visibility;
- clip;
- bounds;
- content revision;
- child topology/order;
- resource binding;
- effect parameters;
- effect topology;
- target size/scaling/format;
- atlas and device generation.

A property delta updates its node once and enqueues it once per backend batch.
Transform- or opacity-only changes update compact scene data without rebuilding
display lists or path/glyph resources. Child topology changes rebuild only the
affected child range and ancestor bounds. Content changes invalidate the
owning display-list revision and dependent cached layers.

The backend computes physical damage from both the previous and new
world-space bounds, expanded by clip/effect requirements. Full redraw remains
available for target recreation, non-retained swapchains, debug overlays, and
operations whose coverage cannot be bounded correctly.

ProGPU compilation and presentation keep the existing correctness contracts:

- every pixel-affecting mutation advances the retained revision;
- framebuffer dimensions are physical pixels and layout remains logical;
- glyph scale and subpixel phase include display scaling;
- atlas generation invalidates moved UV content;
- a compiled-scene hit still performs the required clear/render/present pass;
- device loss invalidates all device-owned resources as one transaction.

## Feature-completion order

### Slice 0: measurement and ABI gates

- Capture a current ProGPU/Silk.NET result for every ControlCatalog page with
  `tools/profile-avalonia-controlcatalog.sh`.
- Keep `tools/profile-sample-memory.sh` as the fresh-process sweep for every
  ProGPU desktop sample page.
- Record public and sibling-assembly `TypeRef`/`MemberRef` contracts for the
  pinned Avalonia package set.
- Reject a package whose patched `Avalonia.Base` contract, assembly identity,
  signing identity, or pinned source revision differs.
- Differentially compare ProGPU and HarfBuzz glyph IDs, visual order, UTF-16
  clusters, advances, offsets, ranged features, fallback faces, variations, and
  break/tab behavior over the Avalonia text corpus.

### Slice 1: target and retained draw-list ownership

- Add the internal backend factory and target lifecycle.
- Give each server visual a stable ProGPU handle.
- Synchronize container topology, transforms, opacity, visibility, bounds, and
  draw-list content revisions.
- Render ControlCatalog through the retained ProGPU target without rebuilding a
  complete root drawing context on an unchanged frame.
- Keep Avalonia's existing batch transport, scheduling, animations, and
  readback semantics.

### Slice 2: composition visual parity

- Complete the remaining extension-effect paths. Standard solid-color,
  surface, rounded/geometric clip, opacity mask, cache, blur/drop-shadow
  effect, adorner, `ICustomDrawOperation`, and `CompositionCustomVisual` paths
  are native.
- Add multi-window owner-disposal-order, offscreen, resize/DPI, target
  suspension, capture, and forced native device-loss tests. The typed
  device-loss state machine is implemented: existing WebGPU and Avalonia
  backend contexts report loss, lost shared devices are excluded, Silk.NET
  recreates its device/surface before painting, and the exact-source Avalonia
  manager recreates backends even when no `IPlatformGraphics` exists. Native
  popup creation,
  parentage, placement, rendering, and memory/performance now have a
  ControlCatalog differential gate.
  The fixture also fails when `Popup.IsUsingOverlayLayer` is true, preventing a
  passing run from silently measuring the old overlay behavior.
  Zero-physical-pixel Silk.NET targets now report
  `PlatformRenderTargetState.NotReadyTryLater` and reject framebuffer locking
  until a positive framebuffer size returns. Window disposal releases an
  already-created WebGPU/input lifetime even when the native window has become
  uninitialized, so suspension and owner disposal cannot retain a shared
  device lease.
  Affined offscreen surfaces now implement capture as one explicit GPU-to-CPU
  boundary copy. The returned bitmap retains context-neutral RGBA storage and
  lazily creates only the device texture required by its consumer; ordinary
  same-context layer blits remain direct texture reuse. Snapshot creation no
  longer returns a texture owned by the source render context.
  Silk.NET framebuffer locks now report DPI from the physical-to-logical
  framebuffer ratio. Avalonia composition commands already carry that target
  scaling in their transforms, so the ProGPU host deliberately normalizes
  those commands to a physical-pixel coordinate space instead of multiplying
  the DPI twice. Resize replaces the old target texture and releases any
  readback capacity rather than retaining both size generations.
  The native loss qualification uses Dawn's typed `wgpuDeviceForceLoss`
  diagnostic entry point in an isolated IOSurface probe, observes the real
  callback, and creates a healthy replacement. The ordinary wgpu-native lane
  retains deterministic loss-state coverage because it exports no equivalent
  safe force-loss function.
- Remove the frame-flattening fallback from ControlCatalog. Missing node kinds
  become explicit test failures.

### Slice 2b: existing Avalonia windowing backends

- macOS is implemented through typed `IMetalPlatformSurface` drawable
  acquisition, IOSurface import into the exact WebGPUSharp/Dawn device, and
  bidirectional `MTLSharedEvent` timeline synchronization.
- Direct-render suitability is independent from previous-frame retention. A
  non-retained drawable renders the retained ProGPU scene directly and
  requests a full redraw through the existing per-session property; it must
  not allocate a full-window preservation layer merely to satisfy dirty-rect
  policy.
- The strict ControlCatalog backend name is `source-progpu-native`; its
  HarfBuzz differential is `source-progpu-native-harfbuzz`.
- The exact native build makes CAMetalLayer storage Dawn-importable at compile
  time. Stock package mode may explicitly allow framebuffer fallback; strict
  performance and release gates never measure that fallback.
- Investigate support for Apple's compressed `&BGA` drawable IOSurfaces in
  Dawn and adopt it only after matched Xcode bandwidth, GPU-residency,
  memory-footprint, frame-time, and pixel-correctness results.
- Windows and Linux now use Dawn's own presentation surface instead of
  importing a texture from a second graphics device. Avalonia Win32 supplies
  its typed `INativePlatformHandleSurface`/`HWND` and Dawn presents through
  D3D12; Avalonia X11 supplies its typed `XID`, ProGPU owns one Xlib display
  connection, and Dawn presents through Vulkan. Adapter selection is made
  against the surface before device creation. Each frame renders directly
  into `GetCurrentTexture` and calls `Present`; there is no dma-buf/DXGI
  cross-device import, CPU readback, or full-frame copy.
- The strict profiler requires `DawnD3D12HWND`, `DawnVulkanXlib`, or
  `DawnMetalIOSurface` according to the host. Linux CI runs the ControlCatalog
  telemetry gate under Xvfb/Mesa Vulkan. WebGPUSharp's Linux Dawn binary links
  to LLVM `libc++.so.1`, so the qualification image installs the distribution
  `libc++1` runtime alongside Vulkan. Windows CI cross-builds the same typed
  lane and its handle-contract tests. Hardware percentile and residency
  baselines remain host-specific and must not be inferred from a software
  Vulkan CI run.

### Slice 3: compact protocol runtime

- Completed foundation: resolve one typed composition backend per graphics
  context, move target-scene ownership out of drawing-context caches, and
  release scenes on target/context loss without changing public Avalonia ABI.
- Completed foundation: replace the ProGPU mirror's per-node dictionary,
  full-sync visited hash, and stale-node scratch list with a 256-slot paged
  state store. Avalonia ABI shells carry only primitive owner and
  index-plus-generation handles; ProGPU validates both generation and retained
  identity before direct `O(1)` access.
- Completed foundation: coalesce each visual into the target change queue with
  one high bit in its compact `ushort` retained-change state. The target no
  longer owns a duplicate `HashSet<ServerCompositionVisual>` alongside the
  ordered change list. Acknowledging the exact target revision clears the
  change bits and queue bit transactionally, so another batch can enqueue the
  visual once without allocating or hashing.
- Completed foundation: each queued delta snapshots the primitive owner,
  generation-bearing handle, retained identity, typed change mask, state and
  content revisions, visibility, opacity, affine transform, and local bounds.
  The same value snapshot now carries layout size and `ClipToBounds`; the
  consumer never rereads those properties from the managed server visual.
  A coalesced mutation replaces the value in its original queue slot,
  preserving first-change ordering without a search or allocation.
  Incremental synchronization addresses the ProGPU page directly, validates
  source identity, and applies these primitive snapshots without rereading
  their managed properties. Effective ancestor visibility is evaluated from
  the ProGPU visual hierarchy. A stale/reused handle aborts the incremental
  transaction and forces the existing full synchronization.
- Completed foundation: when synchronization loses an acknowledgement race
  to a newer target revision, Avalonia refreshes the still-pending immutable
  queue slots in place. This preserves first-change ordering while publishing
  the backend identity assigned during the interrupted synchronization.
  Within one incremental transaction, an earlier parent-topology delta can
  also materialize a newly attached child before that child's original
  unassigned `0/0` snapshot is consumed. ProGPU reconciles only this
  unassigned-to-assigned identity transition and then validates the retained
  ID, source identity, owner, index, and generation in its typed state store.
  All pixel-affecting values continue to come exclusively from the immutable
  delta; foreign and nonzero stale handles still fail closed.
- Completed foundation: opacity and visibility changes use the remaining
  low-bit protocol channel as `PrimitiveAppearance`. Their captured values
  update only the ProGPU visual page and never reread clip, mask, effect,
  cache, brush, render-option, or text-option state. Later direct-channel
  slices moved every resource-bearing field off the former complex path.
  The retained-versus-flattened pixel gate passes on all nine zero-fallback
  pages and the dedicated mask, effect, clip, inherited-text-option, and
  BitmapCache fixtures after this split. The rebuilt Avalonia 12/11 package
  stack passes merged-assembly ABI and runtime-reflection validation; its
  package-only smoke renders 29 frames through one retained scene with zero
  fallback nodes.
- Completed foundation: layout size and `ClipToBounds` use a dedicated compact
  `LayoutClip` channel. ProGPU retains the independent axis-aligned geometry
  clip bounds and intersects them with the captured layout rectangle directly;
  changing layout clipping neither rebuilds geometry nor rereads Avalonia
  properties. A non-rectangular geometry keeps its original typed identity.
  Opacity masks republish their dedicated typed snapshot in the same
  transaction because their bounds/resource realization depends on the layout
  clip. Blur and drop-shadow bounds republish the dedicated typed `Effect`
  snapshot. This path is `O(1)` and allocation-free per changed node.
- Completed foundation: geometry-clip mutations use a dedicated typed
  `GeometryClip` snapshot. Supported Avalonia path adapters publish the
  immutable ProGPU path with the visual revision, so the consumer validates
  the generation-bearing handle and updates the existing retained visual
  directly without rereading the server property. Layout rectangles and
  non-rectangular path identity remain independent and are recomposed in
  constant work. Supported blur and drop-shadow effects republish their typed
  bounds snapshot; clipped adorners refresh their typed mirror dependency,
  while unsupported effects fail closed before partial commit.
- Completed foundation: blur and drop-shadow mutations use a dedicated typed
  `Effect` snapshot containing kind, radius, offset, packed color, opacity,
  and final post-effect bounds. After Avalonia recomputes subtree bounds, the
  target refreshes only its already queued deltas so snapshots cannot contain
  pre-layout bounds. ProGPU normalizes the scalar values, recovers content
  bounds, and reuses an existing matching effect object. Unsupported effect
  kinds abort the incremental transaction and use the established full/fallback
  path. The direct path is reflection-free, `O(1)` per changed node, and does
  not resize the effect texture when the effect extent remains stable.
- Completed foundation: `BitmapCache` mutations use a dedicated typed value
  snapshot containing presence, render scale, device-pixel snapping, and
  ClearType state. Scale and snapping update the existing ProGPU layer
  directly; a ClearType transition conservatively republishes inherited text
  semantics for the affected subtree. Embedded retained-scene roots now
  participate in layer/effect liveness even though Avalonia intentionally
  embeds them without a ProGPU parent link. Texture bind-group cache identity
  is keyed by native view generation rather than texel-content generation, so
  redrawing a stable cached layer neither disposes its texture nor creates a
  new bind group.
- Completed foundation: mask, adorner dependency, and topology payloads now
  publish direct typed snapshots and resolve into ProGPU-owned mirror state
  pages. Invisible-to-visible deferred subtree
  materialization still traverses the typed server tree until descendant
  topology and content generations are published directly.
- Remaining: publish serialization deltas directly to handles instead of
  waiting for computed-server-property notification.
- Move animation values into compact channels and update scene buffers without
  UI-thread property mutation.
- Preserve composition batch, custom-message, completion, and readback ordering.

The current transitional mirror minimizes its cost while that remaining move
is implemented. Optional ProGPU `Visual` state for animation, clips, masks,
effects, bitmap caching, and layer textures is stored in a typed lazy cold
object. An ordinary Avalonia mirror visual is consequently 328 bytes instead
of 456 bytes. `AvaloniaCompositionScene` also synchronizes child order against
the persistent list in place; it does not retain a second child list as
scratch. Default reads and no-op writes allocate nothing, first optional use
is `O(1)`, and stable child synchronization remains `O(C)` while preserving
node identity and invalidation.

Incremental compiled pages likewise keep only their admitted rendering
contract. Their typed page-local draw call is 56 bytes and contains vector,
text, and texture state; the 256-byte general draw call's chart, extension,
static-buffer, custom-data, brush, pen, and path fields are absent because
page admission rejects those modes. Replay expands the compact value on the
stack before ordinary batching. For 141 Composition pages this reduces live
draw-call arrays from 39,480 to 11,280 bytes without a public ABI change or a
per-frame allocation.

### Slice 4: package replacement — completed for preview.27

- Ship `ProGPU.Avalonia.Compositor` for the exact supported Avalonia lane.
- Supply the source-built `Avalonia.Base` replacement with the same assembly
  name and public API contract, plus explicit ProGPU provenance.
- Require the ordinary `Avalonia` runtime assets to be excluded so two
  `Avalonia.Base` implementations can never reach compile or output together.
- Validate a package-only app, third-party control binaries, trimming, NativeAOT,
  multi-window startup, and ControlCatalog before publishing.

The final isolated package feed contains the exact ABI-validated `Avalonia`
replacement plus the runtime and Avalonia integration packages. A
SHA-512-validated package-only restore/build passed, the macOS arm64 NativeAOT
publish produced a 22,266,520-byte executable, and that executable rendered 40
smoke frames with one retained scene and zero fallback nodes. The ordinary
package smoke rendered 28 frames with zero fallbacks. The two-window gate
rendered 70 aggregate frames across two retained scenes and remained healthy
after both owner-first and borrower-first disposal.

NuGet.org cannot accept a second artifact under Avalonia's existing package ID
and version. The ordinary public ProGPU integration packages therefore keep
their own package IDs. The separately gated private-feed lane intentionally
supplies the exact `Avalonia` package identity and version so an isolated
consumer can perform the requested package-level replacement without changing
its `PackageReference`. That replacement artifact must never be pushed to
NuGet.org.

## Compatibility and performance gates

The package is incomplete until all of these gates pass on the final Release
binaries:

### Behavioral and visual

- all existing Avalonia renderer, Silk.NET, input, dispatcher, render-baseline,
  and package-only tests;
- every ControlCatalog page starts, warms, renders, captures, and exits without
  a ProGPU validation error or unsupported fallback;
- screenshot comparison at 1.0, 1.25, 1.5, and 2.0 scaling with explicit
  tolerances for backend raster differences;
- composition animations, custom visual messages, opacity masks, caches,
  effects, popups, multiple targets, capture, and device loss;
- unchanged scene reuse plus visual, size, DPI, target, atlas, and resource
  invalidation regressions.

The deterministic device-loss gate includes normal-destruction filtering,
existing-versus-replacement WebGPU context generation, Avalonia backend
`IsLost`, exact-source backend disposal/recreation, and Silk.NET
recovery-before-paint ordering. Platform qualification must still induce a
real driver/device reset and verify a subsequent pixel-bearing frame.

### Memory and lifetime

- zero live backend nodes/resources after target and compositor disposal;
- generation-safe late batch/disposal handling;
- bounded atlas, scene store, staging, pipeline, bind-group, and layer caches;
- repeated open/close, page navigation, resize, theme switching, popup, capture,
  and device-loss leak tests using `WeakReference` and exact GPU diagnostics;
- no positive retained-memory slope after warmup across repeated
  ControlCatalog navigation cycles.

### Frame performance

- no ControlCatalog page regresses its median or 99th-percentile frame time by
  more than the declared noise budget against the current ProGPU backend;
- steady unchanged frames allocate zero bytes in the composition backend after
  warmup;
- transform-only and opacity-only subtree animation performs no display-list,
  geometry, glyph, or image rebuild;
- unchanged scene compilation is skipped while the required render and present
  pass still occurs;
- sustained 60, 120, and 144 Hz pacing is reported separately from unpaced
  compositor throughput;
- startup, first rendered frame, mean/median/p95/p99/worst frame, compile,
  upload, render, allocations, GC, retained managed memory, physical footprint,
  GPU buffer/texture bytes, draw calls, and cache hits are captured.

Measurements compare the same final binaries, machine, power state, adapter,
resolution, scaling, warmup, frame count, and VSync/present mode. Results are
reported as distributions across alternating fresh processes; a single FPS
number is not accepted as causal evidence.

The pinned Avalonia repository remains the source of truth for text contract
tests. `tools/test-avalonia-progpu-text.sh` builds
`external/Avalonia/tests/Avalonia.ProGpu.UnitTests` against the local ProGPU
source and runs its text-formatting and glyph-run test bodies with the
ProGPU-specific bootstrap selecting `ProGpuTextShaper`. Backend selection is
confined to the ProGPU test adapters; the behavioral assertions remain the
pinned Avalonia assertions.

## Current implementation and validation record

The first retained-ownership slice and the context-owned backend foundation
are implemented in the pinned Avalonia source lane. `ServerCompositor`
resolves `ICompositionServerBackend` once from the platform graphics context.
`ProGpuCompositionServerBackend` owns the target-to-scene registry and invokes
the retained renderer before Avalonia's ordinary visual traversal.
`ServerCompositionRenderData` exposes a stable internal identity and revision
to typed ProGPU compilation. Each supported render-data revision becomes an
owned `GpuPicture` in a bounded 2,048-entry store. Replacement, target
destruction, context loss, and eviction dispose resources deterministically.
Top-level and offscreen frame recording transfer command-list ownership
directly to ProGPU and reuse recording capacity across frames.

This is an intermediate slice, not the final compact protocol runtime:
Avalonia still owns batch deserialization, server property recomputation,
initial/incremental mirror synchronization, and animation scheduling. The
backend is now the authoritative owner of render-target scene lifetime, but
the server visual objects are not yet ABI shells over compact ProGPU handles.
Retained render-data contents are no longer replayed into the frame recorder
when their revision is unchanged, and eligible local compilation output
resides in bounded ProGPU pages. The combined GPU streams are still assembled
in traversal order each changing frame, but only dirty 4 KiB buffer ranges are
submitted. Avalonia remains authoritative for animation scheduling and the
unsupported isolated semantics listed above.

The context-owned backend validation on 2026-07-26 used fresh Release
processes with 30 warm-up and 60 measured Buttons frames. Silk.NET rendered
90/90 observed frames through the server backend at 121.47 FPS; Avalonia
Native/Dawn rendered 90/90 at 120.23 FPS. Both retained one 789-node scene,
reported zero fallback nodes and zero tracked intermediate-texture bytes, and
used their expected direct presentation paths. The complete pinned
`Avalonia.Base.UnitTests` run passed 2,842 tests with 12 existing skips. The
replacement package passed strict public ApiCompat after its native-surface
selection feature was made internal; the package metadata audit also rejected
and led to removal of a diagnostic `GetType().Name` call before passing with no
runtime-reflection type references. The package-only multi-window gate rendered
66 backend frames across two scenes and kept both surviving windows healthy
after owner and borrower disposal.

The following compact-delta slice classifies each changed server visual with
an allocation-free compact mask: transform, bounds, appearance, layout clip,
geometry clip, effect, bitmap cache, inherited drawing options, and topology. The
existing monotonic 64-bit
`RetainedId` is the stable handle. Transform-only and bounds-only updates now
touch only their matching ProGPU fields instead of rebuilding unrelated mask,
effect, cache, and drawing-option state. Blur and drop-shadow changes carry
typed scalar snapshots and update the existing ProGPU effect instance without
rereading Avalonia state. Flags coalesce until the target acknowledges
the exact revision. Inherited render/text options conservatively request a
transactional full synchronization so descendants cannot keep stale effective
values; direct descendant generations remain part of the compact-protocol
work still to do. Focused pinned-source coverage passes 4/4, including exact
transform and inherited-text-option masks. Fresh Buttons and animated
Composition checks rendered all 90 expected backend frames at 123.09 and
121.14 FPS respectively, with zero fallback nodes.

Validation on an Apple M3 Pro, macOS 26, .NET 10, Release configuration:

- `tools/test-avalonia-progpu-text.sh`: on the exact official Avalonia 12.0.5
  source, 274 of 279 upstream `Media.TextFormatting` tests passed, with the
  five original platform/profiling skips; all 13 upstream `GlyphRunTests`
  passed. Test bodies and assertions are unchanged. A typed compile-time
  adapter selects the ProGPU services and deterministic embedded test fonts.
  Adobe Blank remains available for explicit parser tests but is excluded
  from fallback because its intentional all-blank mapping is not a fallback
  face.
- `Avalonia.ProGpu.UnitTests`: all 89 package, renderer, shaping, and
  retained-cache ownership/eviction tests passed.
- The pinned server-composition state machine now consumes all delayed
  parent-change flags in its first recomputation. Before the fix,
  `ParentChangeDelayedFlagsAreConsumedByOneRecompute` reproduced a false
  no-op retained revision advance from 1 to 2; after the fix both focused
  retained-revision tests and the complete `Avalonia.Base.UnitTests` suite
  pass (2,841 passed, 12 skipped, 0 failed; 2,853 total).
- The exact official 12.0.5 source-built ControlCatalog completed all 70 pages
  in fresh processes with both text shapers: 140/140 successful processes,
  with no failed page or ProGPU validation error. Requested page selection is
  logged after loaded-priority dispatch, and page-specific draw, glyph, and
  retained-picture counts confirm distinct workloads.
- A later fail-closed 70-page source-ProGPU census completed 70/70 fresh
  processes with `ProGpuOptions.RequireNativeCompositionScene` enabled and
  zero fallback nodes. The profiler now rejects a source result whose fallback
  telemetry is missing or nonzero, independently of the render-thread failure.
- The ProGPU shaper averaged 60.694 FPS, 17.006 ms/frame, and 6,076.18 managed
  bytes/frame; HarfBuzz averaged 60.622 FPS, 17.015 ms/frame, and 6,076.59
  bytes/frame. Maximum observed frame time was 57.859 ms and 55.054 ms
  respectively. These 30-warmup/60-measurement runs establish parity and broad
  compatibility, not a causal performance win.
- Reusing the ProGPU frame recorder reduced the Border opacity workload from
  581,255 to 14,139 managed bytes/frame while preserving 9,256 retained-picture
  hits and 208 initial/revision compilations over the measured run.
- A fresh 54-page native ProGPU desktop preflight completed without failures.
  Static retained pages commonly measured 450–540 uncapped FPS. The existing
  180-warmup/600-measurement profile remains the sustained baseline; the new
  30/60 sweep is a cold-path regression preflight and is not directly compared
  to that longer run.
- The later 60-warm-up/180-measured-frame desktop sweep also completed all
  54 pages. It exposed `Text & Documents` as the sole large transient-vector
  outlier. A shared-compositor PathAtlas fix now discards stale phase variants
  through the existing one-frame retry instead of growing an 8 MiB recovery
  texture. In a matched 30/60 run, its atlas fell to 0.5 MiB, total tracked
  textures/staging to 2.33 MiB, allocation to 331,500 bytes/frame, and
  compositor time to 2.7073 ms. Xcode recorded 209,554,320 bytes of persistent
  native heap plus anonymous VM and no Metal error, spill, or hang signal.
  The final current-binary sweep then completed all 54 pages again: 50 pages
  reached at least 190 FPS, and the longer Text & Documents run shrank to a
  0.25 MiB atlas, 2.09 MiB total tracked textures/staging, and 478.52 MiB
  physical footprint versus the preceding 17.82 MiB and 510.69 MiB. This
  shared fix applies identically to WinUI and the Avalonia renderer.
- The typed device-loss source change passes all 2,854 Avalonia.Base tests
  (2,842 passed, 12 skipped) and the strict 12.0.5 ABI gate. A matched
  600-frame Composition run measured 119.36 FPS and 5,312 managed bytes/frame
  for ProGPU versus 119.90 FPS and 5,634 bytes/frame for Skia, with 738
  retained nodes and zero fallback nodes.
- The post-startup live-memory pair settled at 184.70 MiB physical footprint
  for ProGPU versus 198.70 MiB for Skia. The apparent 119 MiB ProGPU
  IOAccelerator reservation was only 4.70 MiB resident; broader graphics
  residency drained to 59.90 MiB versus Skia's 60.90 MiB. ProGPU retained
  8.94 MiB more working set, primarily from 18.40 MiB more CoreCLR/JIT
  `VM_ALLOCATE`, while native allocator payload was 14.55 MiB lower.

Machine-readable and Markdown results are under:

- `artifacts/avalonia-source-all-opacity-preflight/summary.json`
- `artifacts/avalonia-source-all-opacity-preflight/summary.md`
- `artifacts/avalonia-exact-source-all-preflight/summary.json`
- `artifacts/avalonia-exact-source-all-preflight/summary.md`
- `artifacts/sample-memory-profile-current-preflight/summary.json`
- `artifacts/sample-memory-profile-current-preflight/summary.md`

Prepare the exact official source lane and run its fail-closed ABI gate with:

```bash
./tools/prepare-avalonia-12.0.5-source.sh
./tools/validate-avalonia-source-abi.sh
```

The preparation script creates an isolated detached worktree at official
Avalonia 12.0.5 commit
`fee9c561ce036e8a3e8cee2397c75ca599b4790d`, initializes its pinned
submodules, and applies the reviewed compositor and text-test patches only
when they apply cleanly. Both scripts reject a different revision.

The ABI gate builds the pinned
`Avalonia.Base`, compares its assembly name, version, culture, and public-key
token with the official 12.0.5 runtime assembly, and runs the .NET SDK's
strict ApiCompat rules over the runtime API including attributes and parameter
names. It passes with `Avalonia.Base, Version=12.0.5.0`, public-key token
`C8D484A7012F9A8B`, and no suppression file. Avalonia's NuGet reference facade
is produced by its packaging-time `PrivateApi` stripping pipeline, so a normal
compiler-generated `obj/ref` assembly is deliberately not misrepresented as
that packaged facade. The package lane must additionally validate the actual
packed reference facade before publication. These are build/CI checks and
introduce no reflection or compatibility probing into runtime code.

`tools/pack-avalonia-progpu-replacement.sh` now runs Avalonia's official
merge/reference-assembly pipeline and accepts the private replacement only
after strict ApiCompat checks for every `lib` and `ref` assembly in net8.0 and
net10.0. The final `Avalonia.Base` package payload must also byte-match the
source output validated in that same build. The package-only consumer restores
from an isolated cache and byte-checks the selected Avalonia, renderer, and
Silk.NET payloads before it builds.

The delayed-state fix was repacked through this full lane. The resulting
12.0.5 replacement retained the official assembly versions and
`C8D484A7012F9A8B` public-key token, passed the runtime-reflection audit, and
built successfully in an isolated replacement-only consumer. Alternating
120-warmup/300-measured-frame Composition runs were intentionally treated as a
regression gate rather than a speedup result: fixed runs measured 0.9124 and
0.8170 ms average compositor time around the original-bug run's 0.8515 ms,
with the same 738 nodes, one full plus 419 incremental synchronizations, zero
fallback nodes, and 34,177,024 tracked Metal bytes. That continuously animated
page performs real visual updates every frame, so the observed variation is
process noise; the demonstrated benefit is removal of false no-op revision and
bounds work.

Its replacement-only native smoke also requires rendered ProGPU frames, a
nonzero retained-scene count, and zero fallback nodes. The conditional
compile-time contract keeps the ordinary published-package comparison lane
compatible with package versions that predate `ProGpuOptions`; no runtime
reflection or API probing is used.

The final lifecycle package refresh passed 2,475 ProGPU core tests, all 89
Avalonia renderer/package tests, and all 42 Silk.NET integration tests. The
isolated replacement consumer restored the exact SHA-512-validated package
bytes, rendered 21 frames, observed one retained composition scene, and
reported zero fallback nodes. The packaged `Avalonia.ProGpu.dll` and
`Avalonia.SilkNet.dll` also passed the runtime-reflection metadata audit.

The custom-visual gate uses an explicit 64-by-64 animated
`CompositionCustomVisualHandler` attached through Avalonia's public typed
composition API. The final 30-warmup/60-measured-frame source run retained one
custom-visual node, compiled it 89 times as handler invalidations arrived,
retained 739 total nodes, and used zero flattened fallback nodes. The
ControlCatalog profiler now fails the Composition page if either typed
custom-visual counter is absent, so a page-selection or child-composition
regression cannot silently pass on ordinary visual telemetry.

The explicit retained-scene comparison lane now removes
`ICompositionServerBackend` from the graphics-context feature map when
`PROGPU_AVALONIA_RETAINED_SCENE=0`. This gives the pixel gate a genuine
flattened traversal with zero retained scenes/backend renders while the
default and strict package lanes continue to resolve the backend once from the
typed context. The full retained-pixel matrix passes byte-for-byte across all
nine pages and every clip, mask, effect, inherited-text, and BitmapCache
fixture. Geometry-clip animation targets the clipped root itself so each short
fixture frame changes visible pixels instead of being correctly culled as an
off-ellipse descendant update.

The compact-delta release gate also passed the exact replacement-package
stack, strict official-identity ABI validation, the runtime-reflection audit,
and the package-only shared-device multi-window test. That test rendered 70
typed backend frames across two retained scenes with zero fallback nodes, then
kept the survivor rendering after both owner-first and borrower-first
disposal.

Two alternating 120/600 Composition comparisons measured ProGPU at 120.258
mean FPS and 5,733 managed bytes/frame versus Skia at 120.011 FPS and 5,931
bytes/frame. ProGPU's maximum physical footprint was 346.00 MiB versus
299.70 MiB. Xcode Allocations attributes the difference primarily to a bounded
Composition-specific IOAccelerator reservation (124,534,784 bytes for ProGPU
versus 16,007,168 for Skia), not managed retention or intermediate texture
payload. The same ProGPU binary on Buttons retained only 10,977,280 bytes of
IOAccelerator VM. Upload-transport, custom-visual, and oversized-mask-reuse
A/B captures did not reduce the reservation and were reverted; their
allocation timestamps stop during startup and the final Metal window reports
no compiler spill, drawable wait, hang, or command-buffer error.

## Cross-engine research decisions

Primary sources:

- [Skia text shaper design](https://docs.skia.org/docs/dev/design/text_shaper/)
  and [Skia Graphite source](https://github.com/google/skia/tree/main/src/gpu/graphite)
- [Direct2D/DirectWrite text rendering](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
  and [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
- [Win2D device-loss handling](https://microsoft.github.io/Win2D/WinUI3/html/HandlingDeviceLost.htm)
- [WebRender source](https://github.com/servo/webrender)
- [Vello source](https://github.com/linebender/vello) and
  [glyph-rendering design discussion](https://github.com/linebender/vello/issues/204)
- [Parley layout API](https://docs.rs/parley/latest/parley/)
- [HarfBuzz shaping plans](https://harfbuzz.github.io/harfbuzz-hb-shape-plan.html)
- [Avalonia composition source](https://github.com/AvaloniaUI/Avalonia/tree/master/src/Avalonia.Base/Rendering/Composition)

Adopted:

- reusable CPU shaping/layout results and retained glyph indices;
- retained display lists/scenes with stable resource identity;
- visibility- and revision-driven raster/upload work;
- device-domain ownership and generation-wide loss recovery;
- asynchronous or render-thread scene preparation without UI-thread GPU work;
- physical target sizing with logical layout and fractional text placement.

Adapted:

- WebRender interning and retained display-list concepts become typed ProGPU
  resource handles and revisioned display-list nodes;
- Vello's encoded GPU scene concepts become ProGPU's existing compute path
  rasterization and compiled scene buffers;
- Direct2D/Win2D resource-domain rules become `WgpuContext` plus device
  generation ownership;
- Parley/Skia/DirectWrite shaping separation continues through Avalonia and
  reusable managed ProGPU CPU shaping results rather than introducing GPU
  Unicode shaping.

Rejected:

- copying or translating any other engine's implementation;
- using Avalonia private source as ordinary ProGPU implementation files;
- runtime reflection, detours, IL weaving, or version-heuristic patching;
- rebuilding a flattened display list every frame;
- moving Unicode/OpenType shaping to the GPU;
- unbounded exact-position glyph/path cache keys;
- silent software, Skia, or old-compositor fallback in the final package;
- performance claims based on non-comparable binaries or a single FPS run.

The reviewed engines inform contracts, algorithms, test cases, and measured
tradeoffs only. ProGPU implementation source remains original and follows the
repository's typed, reflection-free architecture.

## Package-only multi-window lifecycle result

The package-only multi-window gate now covers both shared-device destruction
orders. It proves typed device identity for the owner/survivor and
survivor/borrower pairs, awaits actual platform disposal, and requires the
survivor to render afterward. The exact package run required 24 initial
frames, 22 frames after disposing the original device owner, and 20 frames
after disposing a borrower. Four frames initialized and qualified the second
borrower, for 70 aggregate backend renders. The final survivor retained one
scene with zero fallback nodes. This checked-in gate exposed and led to the
fix for input-device list mutation during first-window disposal.

Matched Xcode Metal and live-memory captures show no disposed-surface
accumulation. The final small survivor used 32.54 MB of Metal allocated size
and 28.00 MiB of IOSurface, both below the maximized one-window package
baseline of 62.65 MB and 56.80 MiB. The stable macOS AGX high-water region is
recorded separately from explicit live GPU resources and remains flat across
both disposal orders.

## Compact retained-command projection

`AvaloniaCompositionVisual` does not retain the full 560-byte
`RenderCommand` array element for ordinary vector and glyph-run content.
Instead, it owns a typed compact command projection and implements
`IOwnedRenderCommandCache.RenderCommandCount/GetRenderCommand`. Compilation
expands each value on the stack into the existing compositor contract, so
pipeline selection, atlas invalidation, DPI changes, device loss, hit
testing, and render output continue through the single established path.

One scene-owned `DrawingContext` is reused as the ordinary recording scratch
buffer. A visual falls back fail-closed to its own complete context when a
command uses retained resources, GPU transform indirection, or an unsupported
kind. Custom visuals are always complete and volatile. This makes stable
ordinary command retention `O(C)` in compact command count while recording
workspace is `O(max C changed by one visual)` rather than one general backing
array per visual. No glyph array is copied and steady replay allocates no
managed object.

On the Composition contract workload, live command arrays fell from 163,936
to 5,696 bytes. The complete retained/flattened pixel matrix remained
byte-identical, and the corrected steady run measured 5,383 bytes/frame at
120.27 FPS versus Skia's 5,816 bytes/frame at 119.89 FPS.

## Typed opacity-mask channel and analytic solid masks

Avalonia opacity-mask changes now travel through a dedicated immutable
retained delta containing the typed brush reference and finalized subtree
bounds. Mutable server brushes are observed explicitly; replacing a brush
detaches the prior observer, so a stale resource cannot invalidate the visual.
Unsupported brush kinds abort the incremental transaction before changing the
ProGPU scene. There is no reflection, property-name dispatch, or
post-publication source reread.

Visual state and render content now have independent revisions. Transform,
opacity, clip, effect, and mask changes advance the state revision without
forcing custom or ordinary drawing commands to be re-recorded. Actual solid
color, surface, draw-list, acrylic, size-dependent, and custom-handler content
changes advance the content revision. Partial custom-visual invalidation
advances content identity while preserving its bounded dirty rectangle.

A top-level solid opacity mask is compiled as an affine analytic rectangle
plus one scalar alpha in the existing mask uniform. It uses a pooled bind group
and the dummy texture binding required by the stable pipeline layout, but
allocates no mask texture and schedules no mask render pass. Gradient, image,
scene, and nested masks retain the bounded texture fallback. Analytic mask
scopes are not captured into incremental pages because their per-frame
bind-group ownership is outside a page's stable resource contract.

The final 120/600-frame Buttons fixture completed 718 typed mask
synchronizations with two initial full synchronizations, one custom-content
compilation, and zero complex or fallback nodes. Three fresh processes
averaged 119.966 FPS and 4,190 managed bytes/frame. The mask path reported zero
mask texture bytes, render passes, draw calls, or copy bytes; one analytic bind
group remained pooled.

## Incremental visual topology

Topology no longer marks the retained target as requiring a full scene
synchronization. A topology delta carries the ordered typed server-child view
for its source visual. ProGPU applies all topology deltas before ordinary
state/content deltas, preserving mirror identities while adding, removing,
reordering, or reparenting children. After the transaction, one reachability
walk removes detached handles and refreshes scene accounting.

The protocol is allocation-free at the ownership boundary: Avalonia publishes
its existing server list as `IReadOnlyList<ServerCompositionVisual>`, and
ProGPU consumes it on the same render thread. No reflection, copied child
array, runtime patching, or serializer-layout duplication is involved.
Reparenting refreshes inherited drawing options but preserves retained drawing
commands and custom-visual content identity.

The deterministic ControlCatalog reparent fixture reports a dedicated topology
counter and separate measured full-sync counter. Three 120/600 runs each
completed 1,800 measured topology synchronizations, zero measured full syncs,
zero fallbacks, 793 stable nodes, one custom compilation, and 3,716 managed
bytes/frame at 120.20-120.27 FPS. Retained and comparison pixels are
byte-identical. Xcode Time Profiler plus EventPipe showed the final
reachability walk in only three managed CPU samples over 600 measured frames,
with no measurement GC.

The final release gate precreates both parents and the child before warmup,
then reparents the same child on every measured frame. Its eight-frame
qualification completed 21 incremental topology synchronizations with zero
measured full synchronizations and byte-identical retained/flattened pixels.
This specifically covers first-attachment handle activation and prevents a
full-scene fallback from hiding a stale queued identity.

## Typed adorner dependencies

`AdornerIsClipped` and `AdornedVisual` publish a dedicated immutable retained
delta. The backend resolves the captured server visual through its
generation-checked handle and keeps a typed mirror-to-mirror relationship.
Adorner clip reconstruction then reads only mirror-owned transforms, size,
layout clipping, and geometry clipping; it does not walk or reread live
Avalonia state during rendering.

Each dependent visual reuses bounded ancestor-path and composite-clip lists.
Relationship and relevant transform changes refresh only affected adorners,
while topology or clip mutations run one dependency refresh after the
transaction. Invalid cross-tree or fallback relationships fail closed through
the established full-scene qualification path. The deterministic AdornerLayer
fixture completed 600/600 measured relationship changes incrementally in each
of three fresh runs with zero full syncs, zero fallbacks, and zero
complex-appearance synchronizations. It sustained 120.11-120.18 FPS at
0.2469-0.2687 ms average compilation and exactly 7,904 managed bytes/frame.
The dynamic retained/comparison screenshots are byte-identical. Xcode
Allocations, Time Profiler, and Metal reported no drawable waits, compiler
spills, hang risks, hangs, or command-buffer errors; 500.0 MiB of raw
trace/export/scratch data was removed after compact summaries were retained.

The final release fixture discovers the page's existing XAML adorner after
deferred content materializes during warmup, retains that adorner and both
targets, and changes only `AdornedElement` during measurement. The eight-frame
gate reports six typed adorner synchronizations, zero measured full
synchronizations, zero fallback nodes, and byte-identical
retained/flattened output.

## Direct-only incremental state protocol

Every incremental visual-state mutation now uses an owning typed channel:
transform, bounds, primitive appearance, layout clip, geometry clip, bitmap
cache, effect, opacity mask, inherited drawing options, topology, or adorner.
The former bit-two catch-all had no remaining publisher and is removed without
renumbering the later internal flags. Its full-state source reread and
`RequiresFallback` branch are gone from incremental synchronization.

Unsupported captured resource values still fail closed in their own channel,
and initial/full synchronization retains the complete state and fallback
classification path. Consequently no animation or resource delta consults
unrelated live Avalonia properties after publication.

Remaining compositor protocol work is now narrower:

- repeat package ABI, native-windowing, Silk.NET, Dawn, memory, text-shaper,
  and full sample qualification after the compact text-style change.

The clean integration branch has been reconstructed without the three
prohibited imported commits. The enforced audit reports zero imported paths,
zero provenance notices, and zero reachable import commits.
