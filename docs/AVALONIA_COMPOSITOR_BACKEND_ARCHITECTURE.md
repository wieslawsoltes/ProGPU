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
  -> ICompositionVisualTreeDrawingContextFeature
  -> AvaloniaCompositionScene stable-ID map
  -> persistent ProGPU ContainerVisual hierarchy
  -> retained per-node commands / GpuPicture resources
  -> DrawVisual at exact host-command z-order
  -> ProGPU compositor and WebGPU surface
```

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
Render and text options are merged along the typed server parent chain with
Avalonia's own inheritance order and become part of each node's materialized
command state. An option change requests one transactional full synchronization
so affected descendant commands are rebuilt without stale inheritance. This is
an explicit transitional completeness mechanism, not reflection or a runtime
API probe. Setting
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
| rectangular clip | scissor when axis-aligned, otherwise analytic clip |
| rounded/geometric clip | retained path clip or cached mask |
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
  telemetry gate under Xvfb/Mesa Vulkan; Windows CI cross-builds the same
  typed lane and its handle-contract tests. Hardware percentile and residency
  baselines remain host-specific and must not be inferred from a software
  Vulkan CI run.

### Slice 3: compact protocol runtime

- Replace managed server visual render state with paged ProGPU state stores
  while retaining Avalonia ABI shells.
- Apply serialization deltas directly to handles.
- Move animation values into compact channels and update scene buffers without
  UI-thread property mutation.
- Preserve composition batch, custom-message, completion, and readback ordering.

### Slice 4: package replacement

- Ship `ProGPU.Avalonia.Compositor` for the exact supported Avalonia lane.
- Supply the source-built `Avalonia.Base` replacement with the same assembly
  name and public API contract, plus explicit ProGPU provenance.
- Require the ordinary `Avalonia` runtime assets to be excluded so two
  `Avalonia.Base` implementations can never reach compile or output together.
- Validate a package-only app, third-party control binaries, trimming, NativeAOT,
  multi-window startup, and ControlCatalog before publishing.

NuGet cannot publish a second artifact under Avalonia's existing package ID and
version. “Bait and switch” therefore means assembly/API-compatible consumption,
not impersonating the upstream NuGet package. The ProGPU package owns the
replacement binary and a fail-closed build contract.

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

The first retained-ownership slice is implemented in the pinned Avalonia
source lane. `ServerCompositionRenderData` exposes a stable internal identity
and revision to a typed drawing-context feature. `Avalonia.ProGpu` compiles
each supported render-data revision into an owned `GpuPicture`, caches it in a
bounded 2,048-entry store, and emits the retained picture on later frames.
Replacement and eviction dispose picture resources deterministically.
Top-level and offscreen frame recording transfer command-list ownership
directly to ProGPU and reuse recording capacity across frames.

This is an intermediate slice, not the final compact protocol runtime:
Avalonia still owns server visual traversal and animation scheduling. The
retained render-data contents are no longer replayed into the frame recorder
when their revision is unchanged, and eligible local compilation output now
resides in bounded ProGPU pages. The combined GPU streams are still assembled
in traversal order each changing frame, but only dirty 4 KiB buffer ranges are
submitted. Avalonia remains authoritative for animation scheduling and the
unsupported isolated semantics listed above.

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
survivor to render afterward. The exact package run produced 24 initial
frames, 22 frames after disposing the original device owner, and 20 frames
after disposing a borrower, with one retained scene and zero fallback nodes.

Matched Xcode Metal and live-memory captures show no disposed-surface
accumulation. The final small survivor used 32.54 MB of Metal allocated size
and 28.00 MiB of IOSurface, both below the maximized one-window package
baseline of 62.65 MB and 56.80 MiB. The stable macOS AGX high-water region is
recorded separately from explicit live GPU resources and remains flat across
both disposal orders.
