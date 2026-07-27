# ProGPU GPU texture and intermediate-memory audit

Date: 2026-07-25

## Scope and method

This audit covers production texture creation, retained texture ownership,
texture copies, offscreen rendering, atlas growth, presentation readback, and
effect intermediates under `src/`, `samples/`, and `integration/`. Test-only
render targets are excluded from the allocation conclusions. The source
inventory was cross-checked against the following primary designs:

- [Skia Graphite `ResourceProvider`](https://chromium.googlesource.com/skia/+/d2b9e48baf1697760afc1dc8ea3ad40110b8cacc/src/gpu/graphite/ResourceProvider.cpp)
  keys reusable scratch textures by dimensions and texture information and
  keeps them budgeted.
- [Skia Graphite resource lifecycle](https://chromium.googlesource.com/skia/+/688688efd488c3779a10c66a8fa3c2bed76e57f3/src/gpu/graphite/Resource.h)
  distinguishes live command-buffer references, reusable resources, and
  purgeable cache residency.
- [Direct2D effects](https://learn.microsoft.com/en-us/windows/win32/direct2d/effects-overview)
  models effects as an input graph with a single output and can link compatible
  pixel-shader stages.
- [Direct2D Gaussian blur](https://learn.microsoft.com/en-us/windows/win32/direct2d/gaussian-blur)
  uses a three-sigma kernel extent and treats zero standard deviation as a
  disabled effect.
- [WebRender rendering overview](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst)
  represents intermediate work as render tasks and persists selected results
  in its texture cache.
- [Vello](https://github.com/linebender/vello) avoids many clip intermediates
  through compute-oriented scene processing, while explicitly listing blur,
  filter, and GPU-memory allocation work as areas still under development.
- [wgpu `Device::poll`](https://docs.rs/wgpu/latest/wgpu/struct.Device.html#method.poll)
  explicitly performs device maintenance, including cleanup of completed
  resources and mapping callbacks. ProGPU now invokes the corresponding
  non-blocking wgpu-native poll after each submitted frame.
- [wgpu-native 0.19.4.1 `wgpuGenerateReport`](https://raw.githubusercontent.com/gfx-rs/wgpu-native/v0.19.4.1/ffi/wgpu.h)
  exposes per-registry allocated, user-kept, released, and error slot counts.
  ProGPU binds this public diagnostic ABI with typed sequential structs.
- [wgpu-native surface conversion](https://raw.githubusercontent.com/gfx-rs/wgpu-native/v0.19.4.1/src/conv.rs)
  documents a default desired maximum frame latency of two.
- [Metal `MTLDevice.currentAllocatedSize`](https://developer.apple.com/documentation/metal/mtldevice/currentallocatedsize)
  reports the bytes Metal currently allocates for resources on a device. It is
  a narrower quantity than macOS process physical footprint or the AGX
  `owned unmapped (graphics)` VM category.
- [wgpu queue upload documentation](https://docs.rs/wgpu/latest/wgpu/struct.Queue.html)
  states that native `write_buffer` calls allocate temporary staging memory
  which is released after the next submission, and recommends an explicit
  staging strategy for avoiding many short-lived allocations.
- [wgpu buffer upload guidance](https://docs.rs/wgpu/latest/wgpu/struct.Buffer.html)
  identifies a caller-managed staging buffer plus encoded
  `copy_buffer_to_buffer` operations as the reusable alternative.
- [wgpu `StagingBelt`](https://docs.rs/wgpu/latest/wgpu/util/struct.StagingBelt.html)
  uses bounded reusable `MAP_WRITE | COPY_SRC` chunks, closes them before
  submission, and asynchronously remaps them after the GPU has consumed the
  copy.
- [WebGPU buffer usage and mapping](https://gpuweb.github.io/gpuweb/#buffer-usage)
  permits `MAP_WRITE` only with `COPY_SRC` and transfers mapped-buffer
  ownership to the GPU on `unmap`.
- [Apple Metal performance analysis](https://developer.apple.com/documentation/xcode/analyzing-the-performance-of-your-metal-app/)
  documents Metal System Trace resource-allocation recording and informed the
  mandatory Instruments capture lane used below.
- [Apple's shader-optimization guidance](https://developer.apple.com/videos/play/tech-talks/10580/)
  identifies dynamically indexed private arrays as a likely source of register
  spilling and recommends reducing private-array and register pressure before
  changing thread-group occupancy.
- [Skia `SkStrikeCache`](https://skia.googlesource.com/skia/+/main/src/core/SkStrikeCache.h)
  applies explicit byte and entry limits, while its
  [GPU `GlyphVector`](https://skia.googlesource.com/skia/+/5934f0e64066/src/text/gpu/GlyphVector.h)
  retains an atlas generation and regenerates atlas residency when required.
- [WebRender's LRU cache](https://searchfox.org/firefox-main/source/gfx/wr/webrender/src/lru_cache.rs)
  is the texture cache's bounded residency index; its weak handles are checked
  against a per-entry epoch so a freed-and-reused slot cannot resolve as stale
  content.
- [DirectWrite color-font support](https://learn.microsoft.com/en-us/windows/win32/directwrite/color-fonts)
  keeps character-to-glyph mapping and positioning independent from the
  rendering-time choice between monochrome outlines, color layers, SVG, and
  embedded bitmap glyphs.
- [HarfBuzz shape-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html)
  likewise scopes reusable shaping decisions to shaping rather than making
  them owners of raster textures.
- [.NET ReadyToRun deployment guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run)
  documents the startup-versus-binary-size tradeoff of precompiled application
  assemblies.
- [.NET compilation configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation)
  documents tiered compilation, Quick JIT, and Quick JIT for loops, including
  their startup and steady-code-quality tradeoffs.

These sources informed resource lifetime, bounds, and representation choices
only. The ProGPU implementation is original and retains ProGPU's typed,
reflection-free ownership and invalidation contracts.

## Allocation classification

| Allocation family | Current ownership | Classification | Result or next action |
| --- | --- | --- | --- |
| Window/surface target | Platform or host | Required final output | Do not pool with scratch resources. Direct Silk.NET presentation is already the preferred desktop path. |
| Framebuffer fallback target | `OffscreenTextureCache` | Required only for CPU framebuffer/readback; avoidable when a native GPU surface exists | Completed for Silk.NET windows: the default path renders directly to the acquired surface texture view. `PROGPU_AVALONIA_DIRECT_PRESENTATION=0` retains the offscreen path for comparison and compatibility, but its GPU-surface blit lane now allocates only the texture; the row-aligned readback buffer is created lazily only when the final target is a CPU framebuffer. |
| `ProGpuHostControl` presentation target | Avalonia host control | One required image in supported exact-source/shared-memory lanes; transitional duplication only in compatibility fallback | Completed for the exact-source same-device lane and macOS shared-texture-memory lane: one renderable/sampleable texture is imported with typed ownership and queue/timeline ordering. The custom-visual compatibility fallback retains the GPU target, staging readback, CPU bitmap, and imported-image copies only when neither typed path is available. |
| MSAA color target | `Compositor` | Quality-dependent transient | Required when `PrimarySampleCount` is four; absent in the measured offscreen ControlCatalog lane. Do not reduce sample count without pixel evidence. |
| Path coverage atlas | `PathAtlas` | Required bounded cache | Completed: R8 demand growth now has a 240-frame shrink hysteresis. A deterministic bounded MaxRects probe must fit the preceding frame's active set and reduce area by at least 25%; a 256-texel rectangular refinement avoids retaining a power-of-two step when an asymmetric live set fits a materially smaller texture. Shrinking advances atlas generation, discards stale UV entries, and rerasterizes live demand through the normal retry-safe path. |
| Alpha and color glyph atlases | `GlyphAtlas` | Required bounded cache | R8 alpha and RGBA color storage are already separated and demand-grown. |
| Avalonia bitmap-glyph atlas pages | Formerly `BitmapGlyphCache` | Duplicate unbounded cache | Completed: removed. Solid bitmap runs and allocation-free slices from mixed runs now record `DrawGlyphRun` commands into ProGPU's bounded, generation-tracked RGBA color atlas. Avalonia retains only identified dimensions in a 2,048-entry LRU and zero decoded pixel bytes. |
| Geometry/opacity masks | `Compositor` | Avoidable full-target intermediates | Completed: bounded local R8 masks, one reusable construction scratch texture, 16-pixel reuse classes, and adaptive recent-demand retention within the hard 128-texture safety cap. A mask that exactly covers the target now renders directly into its final R8 texture, eliminating the duplicate full-target scratch texture and copy. |
| Blur effect source | Per affected visual | Required cached input | Retained only while its visual is attached and the effect is active. |
| Blur temporary and destination | Per affected visual | Algorithm-required for separable color blur | Allocated only for non-zero blur. Zero-radius blur now retains source only. |
| Drop-shadow temporary | Per affected visual | Reducible scalar intermediate | Completed: four horizontal alpha coverages are packed into one RGBA8 texel and tinting occurs in the vertical pass. The temporary uses approximately one byte per source pixel instead of four. |
| Drop-shadow destination | Per affected visual | Required cached shadow output | Sharp shadow retains source plus destination and no blur temporary. |
| WPF shader-effect intermediates | Per affected visual | Previously unnecessary | Completed: WPF shader effects retain only their rendered source; the former unused temporary and destination are not allocated. |
| Sharp-shadow parameter buffer and bind group | `ComputeAccelerator` | Previously recreated per update | Completed: one typed persistent uniform buffer and cached binding replace per-update native resources. |
| Explicit `CacheAsLayer` texture | Owning visual | Required opt-in cache | Released when inactive, detached, hidden, disabled, or context-invalid. |
| Advanced-blend scratch and source | `Compositor` | One lazy full-color ping-pong target plus one bounded source | Completed: the source target is sized to the largest clipped/transformed advanced-blend draw in the frame instead of the full render target. A typed per-pass uniform maps global geometry and mask coordinates into the bounded texture. Capacity grows on demand, shrinks after 240 substantially underutilized frames, and both color targets are released after 240 unused frames. |
| Wavefront color texture | `Compositor` | Required only by wavefront vector mode | Zero residency in the measured direct renderer. |
| Backdrop/image/WPF extension inputs | Extension pipeline | Borrowed source textures plus bind groups | No additional persistent compositor-owned texture in the measured lane. |
| Images, immutable/writeable bitmaps, `SKImage`, and `SKSurface` | API object owner | Required content or explicit surface | Preserve API ownership. Snapshots intentionally allocate independent textures; ordinary draws reuse uploads. Immutable Avalonia bitmaps release encoded/decoded payloads after upload, and writeable CPU locks upload back into their owning texture context rather than whichever window is thread-current. |
| Avalonia `RenderTargetBitmap` | API object owner | Previously four copies for one logical image | Completed: one texture is both render attachment and sample source. CPU storage and temporary readback exist only at explicit `Lock`/`Save` boundaries. |
| Avalonia drawing-context layer surface | API object owner | One retained texture plus commands until first consumption | No duplicate steady storage. `Save` now flushes into and reads the existing texture instead of encoding a blank placeholder. Same-context blits reuse that texture; an explicit non-affined snapshot creates one portable CPU copy and lazily uploads at most one destination-device texture. |
| Skia `saveLayer` and image-filter surfaces | `SKCanvas`/recorded command owner | Required by explicit layer semantics | Completed for command-ordered transients: successful filter-graph intermediates, consumed filter sources, previous-layer/backdrop inputs, and eligible next layer outputs use the same exact-match pool. Final outputs leave the pool while a deferred drawing command owns them. Failed or unconsumed surfaces are disposed rather than pooled. |
| Skia blur/morphology temporary | `SKCanvas` transient pool | Algorithm-required separable intermediate | Completed: an exact context/format/size/usage match is reused after its consuming queue submission has been encoded. The shared per-canvas pool is capped at four textures/64 MiB, never receives a failed pass, and is disposed with the canvas. Repeated same-size filters stop creating and retiring Metal textures after warmup. |
| Readback staging | `GpuTextureReadbackBuffer` | Required only at CPU ownership boundaries | Capacity is lazy and reused; direct GPU presentation does not allocate it. |

`IDrawingContextLayerWithRenderContextAffinityImpl` capture follows the same
boundary rule. A same-context layer blit reuses its renderable texture. An
explicit non-affined snapshot performs one GPU-to-CPU copy and retains that
CPU storage because it is the portable ownership representation. It does not
eagerly allocate a second GPU texture; the destination render context uploads
one only when it consumes the snapshot. Moving the snapshot to another device
disposes the prior device copy and reuses the retained CPU pixels, keeping
residency to one CPU copy plus at most one GPU copy rather than one texture per
device.

## Implemented effect-memory changes

`Compositor.EffectTextureSet` now owns a mandatory source and optional typed
temporary/destination resources. Resource requirements are derived directly
from the effect:

- zero-radius blur: source only, four bytes per pixel;
- non-zero color blur: source, RGBA temporary, RGBA destination, twelve bytes
  per pixel;
- sharp shadow: source and RGBA destination, eight bytes per pixel;
- blurred shadow: source and destination plus a four-coverages-per-RGBA8 packed
  temporary, approximately nine bytes per pixel;
- WPF shader effect: source only.

Effect type/radius changes dispose resources that are no longer required. The
horizontal shadow shader dispatches one invocation per four source pixels and
computes four independent Gaussian sums. The vertical shader selects the
packed channel, completes the separable convolution, then applies the shadow
color and premultiplied alpha. Work remains `O(R)` per source pixel for radius
`R`; temporary bandwidth and residency decrease by about 75%.

Full-target geometry and opacity masks now use their final pooled R8 mask
texture as the render attachment. This retains the same analytic mask shader
and `O(P)` coverage work for `P` pixels while removing an `O(P)` texture copy
and one target-sized R8 scratch allocation. At 2048x1600, the avoided scratch
residency and copy are each 3.125 MiB. Bounded masks continue to share the
single full-target construction scratch because their final texture is smaller
than the render viewport. The full-target and bounded paths are covered by
pixel/readback and residency regression tests.

## Matched ControlCatalog evidence

The exact source-built ControlCatalog was re-run in four fresh Release
processes for Buttons, Composition, Custom Drawing, and TextBlock. Each process
warmed 60 frames and measured 180 frames.

| Metric | Four-page mean |
| --- | ---: |
| FPS | 58.117 |
| Frame time | 17.415 ms |
| Compile time | 0.524 ms |
| Compositor time | 1.274 ms |
| Allocation/frame | 23.62 KiB |
| Managed retained | 96.36 MiB |
| Physical footprint | 490.05 MiB |
| Tracked compositor intermediates | 4.81 MiB |
| Path atlas | 16.00 MiB |
| Glyph/color atlases | 0.27 MiB |

Across all four pages, effect, layer, advanced-blend, wavefront, and MSAA
texture bytes were zero. Mean mask residency was 1.68 MiB plus a 3.125 MiB
scratch texture. The tracked compositor intermediates and atlases therefore
account for roughly 21 MiB, not the approximately 490 MiB process footprint.
The remainder includes the WebGPU/Metal device, shader/pipeline caches,
backend command infrastructure, framework assemblies/heaps, presentation
resources outside the compositor, and driver-accounted memory. Process
physical footprint must not be described as texture memory without lower-level
backend counters.

Compared with the immediately preceding bounded-mask run, FPS changed from
58.336 to 58.117 (-0.4%), compositor time from 1.237 to 1.274 ms (+3.0%), and
mean physical footprint from 496.64 to 490.05 MiB (-1.3%). Those changes are
within cross-process noise for pages that allocate no effects, so they are not
evidence of a ControlCatalog speed improvement from the effect work. Focused
effect tests provide the deterministic residency evidence.

Artifacts:

- `artifacts/avalonia-current-three-lane-20260725-texture-audit/summary.md`
- `artifacts/avalonia-current-three-lane-20260725-texture-audit/source-progpu/*.json`

## Native, GPU, and runtime decomposition

A process footprint is not a texture-allocation total. Fresh source-built
ControlCatalog processes were inspected with `vmmap -summary`, `heap -s`,
typed wgpu-native registry reports, Metal `currentAllocatedSize`, and .NET
runtime counters. Before adaptive mask retention, a representative active
Composition process contained:

| Accounting view | Resident or live amount | Interpretation |
| --- | ---: | --- |
| Physical footprint | ~510 MiB | macOS process-wide pressure estimate; categories below overlap with API-level views and must not be added to it |
| `owned unmapped (graphics)` | ~216.7 MiB | Metal/AGX active working and submission set |
| Metal allocated resources | ~68.3 MiB | live resource bytes reported by `MTLDevice`; included in the broader graphics working set |
| `VM_ALLOCATE` | ~155.2 MiB | CoreCLR/JIT/GC virtual allocations |
| IOSurface | 37.5 MiB | three 2048x1600 BGRA presentation drawables |
| IOAccelerator graphics | ~14.6 MiB | driver mappings |
| malloc resident | ~59 MiB | native allocator pages; live allocator payload was ~38.8 MiB |
| tracked ProGPU intermediates and atlases | ~21 MiB | application-side textures counted by the compositor |

The ~155 MiB `VM_ALLOCATE` region was split by permissions. Approximately
42.91 MiB of dirty `rwx/rwx` pages was executable JIT code and 111.67 MiB of
dirty `rw-/rwx` pages was writable runtime/GC storage. The benchmark reported
about 82.6 MB of GC committed memory, leaving roughly 33 MiB for other
CoreCLR/JIT metadata and arenas. Reserved no-access pages had zero residency.
This is not evidence of a compositor leak; NativeAOT, ReadyToRun, and trimming
are the relevant future experiments for reducing this runtime category.

The wgpu report found zero user-kept command buffers at capture time. Metal
reported 68–77 MiB of live resources while the broader active AGX bucket was
about 202–217 MiB. The difference is a stable driver working/submission
high-water pool, not retained ProGPU texture payload. In a 12-second active
capture, graphics residency and IOSurface residency changed by 0 bytes,
physical footprint changed by +0.40 MiB, working set by +0.61 MiB, and
`VM_ALLOCATE` by +0.30 MiB. That observation rejects monotonic leakage over
the sampled interval; it does not claim the driver baseline is irreducible.

Xcode Instruments then provided a time-resolved Metal resource audit. The
initial Metal System Trace observed 4,259 resource allocations in 7.8 seconds.
At trace end, live resources totaled 74.58 MiB: 37.50 MiB of presentation
drawables, 15.50 MiB of path coverage, a 12.625 MiB Avalonia offscreen target,
a 3.375 MiB mask scratch texture, and 5.14 MiB of buffers. All 7,861
application command buffers completed and Core Animation recorded no drawable
waits. `MTLDevice.currentAllocatedSize` peaked at 85.55 MiB during startup,
settled at 72.09 MiB after 2.3 seconds, and stayed flat. This independently
rejects a growing live Metal-resource or command-buffer leak.

The same trace exposed a performance defect hidden by end-of-run live counts:
steady rendering created about six wgpu-native staging allocations per frame.
The Composition metrics reported 895 logical incremental writes over 180
frames, or 4.97 writes per frame, plus one compositor-uniform write. Because
native `Queue::write_buffer` allocates temporary staging storage per call,
Instruments measured 361.30 staging allocations and 45.16 MiB of transient
staging allocation per second.

The compositor now packs all aligned scene-page, brush, gradient, and uniform
updates into one retained upload arena. One queue write fills that arena and
the existing render command encoder copies typed ranges to their unchanged
destination buffers before masks and draw passes consume them. CPU packing is
`O(B + C)` for `B` changed bytes and `C` destination copies, retained CPU/GPU
storage is `O(B_peak)`, and the number of native queue staging allocations is
one per changed frame rather than one per destination/page.

Matched post-change Metal System Trace results:

| Steady-state metric | Before | After | Change |
| --- | ---: | ---: | ---: |
| All Metal allocations/s | 362.44 | 60.04 | -83.4% |
| wgpu staging allocations/s | 361.30 | 60.04 | -83.4% |
| transient staging allocation/s | 45.16 MiB | 7.51 MiB | -83.4% |
| ControlCatalog upload CPU time | 0.1110 ms | 0.0179 ms | -83.9% |

Logical scene upload bytes, mask passes, mask draws, and mask-copy bytes were
unchanged. Focused layer/incremental-page/mask tests remained 23/23 passing.
The post-change trace ended with two presentation drawables while the initial
trace had three; because Instruments can perturb presentation buffering, the
corresponding live-resource/current-allocation difference is not attributed
to upload batching. An uninstrumented 12-second capture still converged to a
stable ~201 MiB AGX working set, close to the earlier ~202 MiB result. The
batch removes real resource churn and CPU upload overhead, but does not falsely
claim that Apple driver high-water residency disappeared.

The post-change Time Profiler capture contained 3,426 one-millisecond running
samples. In the steady interval, wgpu-native accounted for 0.74% of sampled
leaf CPU and Metal/AGX for 1.99%; runtime JIT/tiered-profile work, CoreCLR, and
kernel calls dominated the still-cold process. Managed JIT frames are not
symbolized by this native trace, so EventPipe remains the authoritative
managed-code view. The upload path is no longer a native CPU hotspot, and the
instrumented sample distribution is not used as an FPS measurement.

Artifact:

- `artifacts/memory/controlcatalog-composition-latency1.json`
- `artifacts/memory/controlcatalog-composition-latency1.md`
- `artifacts/instruments/metal-resource-allocations.xml`
- `artifacts/instruments/metal-current-allocated-size.xml`
- `artifacts/avalonia-upload-batch-20260725/after/metal-resource-allocations.xml`
- `artifacts/avalonia-upload-batch-20260725/after/time-profile.xml`
- `artifacts/avalonia-upload-batch-20260725/after/allocations-profile/allocations-toc.xml`
- `artifacts/avalonia-upload-batch-20260725/after/memory.json`

The multi-hundred-megabyte raw Instruments bundles were deleted after export;
the compact XML, JSON, logs, and summaries retain the evidence used here.

## Fixed resource-retention defects

The fixed mask-pool cap retained every animated historical size until the pool
reached 128 entries. Composition had accumulated 105 mask bind groups even
though the current frame required one mask pass and one mask draw. The pool
now retains the peak demand observed over the last eight returns plus bounded
slack, with a minimum warm set of eight and the existing caller-selected hard
cap.

On the same Composition workload this changed the wgpu user-kept inventory:

| Resource | Before | After |
| --- | ---: | ---: |
| Mask bind groups | 105 | 1 |
| Buffers | 118 | 14 |
| Textures | 134 | 14 |
| Texture views | 134 | 14 |
| Bind groups | 111 | 7 |
| Mask-pool textures | historical variants up to the cap | 7 retained, limit 8 |
| Mask-pool texture bytes | ~3.05 MiB | 1,792 bytes |
| Tracked intermediates | ~6.33 MiB | ~3.28 MiB |

The current frame still executes one mask pass, one draw, and a 91,136-byte
local R8 copy, so the reduction does not bypass clipping or mask output.

On Apple platforms the surface now requests one maximum in-flight frame
instead of wgpu-native's default of two. At 2048x1600 BGRA this reduced
IOSurface residency from three drawables/37.5 MiB to two drawables/25.0 MiB.
Matched clean Composition runs measured 57.16 versus 56.94 FPS and 1.526
versus 1.587 ms compositor time for latency one versus two. This is a
deterministic 12.5 MiB presentation-residency reduction with no measured
frame-time regression in that run. `vmmap` itself suspends the target during
sampling, so profiler-attached FPS results are intentionally excluded from
the comparison.

The Avalonia Silk.NET render callback also no longer calls `PaintNow` on every
host refresh. It paints only when Avalonia has queued an invalidation. A clean,
retained window therefore stops submitting command buffers, allowing Metal to
purge its active submission working set. This preserves normal invalidation,
animation, resize, and expose behavior while removing a permanent 60 Hz idle
render loop.

## Minimal Metal baseline

`tools/ProGPU.GpuMemoryBaseline` isolates window creation, wgpu device
creation, surface creation, a clear-only wgpu surface, and a direct Metal
clear. Matched captures at a 2048x1600 physical framebuffer produced:

| Mode | Physical footprint | AGX graphics | IOSurface |
| --- | ---: | ---: | ---: |
| Window only | 43.6 MiB | 0.0 MiB | 0.0 MiB |
| wgpu device only | 46.6 MiB | 0.0 MiB | 0.0 MiB |
| wgpu surface, no presentation | 45.8 MiB | 0.0 MiB | 0.0 MiB |
| wgpu clear and present | 168.7 MiB | 89.4 MiB | 25.0 MiB |
| direct Metal clear | 155.1 MiB | 90.2 MiB | 0.0 MiB |
| direct Metal clear, 512x400 | 139.3 MiB | 88.5 MiB | 0.0 MiB |

The first real render activates an approximately 88.5 MiB Apple AGX working
set even for a 512x400 direct-Metal clear. wgpu and direct Metal agree within
measurement variance. The fixed active AGX set is consequently not retained
ProGPU texture content or a wgpu leak. Instruments Metal System Trace confirms
that command buffers complete and resources do not grow during the retained
steady window. The correct application optimization is to stop submitting
when clean, after which the driver can make this working set purgeable.

Exported reports are under
`artifacts/memory-baseline-instruments-20260725/`. Raw trace bundles are
deleted after their exported evidence is checked; the remaining XML, JSON, and
Markdown reports cover steady wgpu clear, initialization, direct Metal, and
native allocations.

## Deferred ControlCatalog page ownership

ControlCatalog previously constructed every page at startup. That retained 70
page trees and 28 one-megabyte ImageSharp pixel arrays before the selected
page was used. A forced-GC snapshot still contained 74.8 MiB and 269,000
objects, including 56.9 MiB in the large-object heap. This was application
eagerness, not compositor ownership.

The pinned Avalonia sample patch now uses a reflection-free
`DeferredCatalogPage` with an exhaustive typed enum/switch. Each tab retains a
small host and constructs its concrete page only on first attachment. Pages
already visited remain retained, preserving sample state. The tab declarations
are source-ordered to keep navigation identity stable and avoid startup
reparenting.

After the idle-render and deferred-page changes, a fresh Buttons page measured:

| Idle metric | ProGPU | original Skia |
| --- | ---: | ---: |
| Physical footprint | 149.4 MiB | 153.8 MiB |
| Working set | 227.3 MiB | 199.4 MiB |
| AGX graphics | 6.4 MiB | 23.9 MiB |
| IOSurface | 12.5 MiB | 25.2 MiB |
| `VM_ALLOCATE` | 83.9 MiB | 51.4 MiB |
| Native allocator payload | 28.5 MiB | 36.6 MiB |
| Forced-GC live managed heap | 19.2 MiB | 19.8 MiB |

Against the prior eager ProGPU idle run, physical footprint fell by 129.2 MiB
and `VM_ALLOCATE` fell by 76.6 MiB. Against the original continuously
submitting static run, physical footprint fell by about 300 MiB. The final
ProGPU idle physical footprint is 4.4 MiB below the matched Skia process,
although its runtime virtual region and ordinary working set remain larger.

The original cross-backend screenshot rendered the attached whole window
through Avalonia's immediate renderer. Its nested clipped ScrollViewer omitted
the Skia page subtree, while the ProGPU path accumulated an incorrect window
transform. The benchmark now finds the selected `TabItem` through typed
controls and renders the selected page root. Both backends capture the same
788x1710 Buttons content without runtime reflection or reparenting.

Silk.NET also no longer assumes a 60 Hz display. Its render timer uses the
primary GLFW video mode, accepts a bounded `PROGPU_AVALONIA_RENDER_FPS`
override, and schedules against an absolute next deadline. Rendering time is
subtracted from the following delay, so a 0.8 ms frame no longer turns an
8.33 ms display period into a 9.13 ms timer period.

Matched final Buttons runs warmed 120 frames and measured 300:

| Active metric | ProGPU | original Skia | Difference |
| --- | ---: | ---: | ---: |
| FPS | 119.31 | 119.84 | -0.44% |
| Mean frame interval | 8.415 ms | 8.375 ms | +0.041 ms |
| ProGPU compositor work | 1.118 ms | n/a | n/a |
| Managed allocation/frame | 8,366 B | 5,848 B | +2,518 B |
| Live managed heap | 29.58 MiB | 16.49 MiB | +13.10 MiB |
| Physical footprint | 323.77 MiB | 262.95 MiB | +60.81 MiB |
| Working set | 223.00 MiB | 202.45 MiB | +20.55 MiB |
| Lifetime peak physical footprint | 338.13 MiB | 320.66 MiB | +17.47 MiB |

ProGPU retained four native textures: a 1 MiB path atlas, 256 KiB alpha-glyph
atlas, 16 KiB color-glyph atlas, and the presentation surface view. Tracked
effect, mask, layer, MSAA, advanced-blend, and wavefront intermediates were all
zero. After introducing the mapped upload ring, live Metal resource allocation
was 28.86 MiB in the final matched run.

Pre-ring rolling Metal System Trace captures retained only the last three
seconds. The ProGPU trace shows the same constant 28.86 MiB and completed command
buffers. Its remaining resource churn is one 128 KiB
`(wgpu internal) Staging` buffer allocation/deallocation pair per changed
frame, produced by the single batched queue write. The Skia run exported
submission/completion and drawable-wait events but no Metal resource or
`currentAllocatedSize` rows, so Instruments cannot be used to infer Skia
resource bytes in this configuration. The uninstrumented process and idle
captures remain the comparable memory evidence.

The remaining queue staging allocation is now removed on native WebGPU by an
original ProGPU two-slot mapped upload ring. Each 128 KiB slot has only
`MAP_WRITE | COPY_SRC` usage. CPU bytes are copied into one mapped slot, it is
unmapped before encoded copies, and its allocation-free callback remaps it
after submission. If both slots initially appear busy, the ring performs one
non-blocking device-maintenance poll and retries the completed map callbacks
before using the correctness-preserving queue-write fallback. Browser WebGPU
keeps its existing command serialization path.

The design adapts the public WebGPU ownership rules and wgpu staging-belt
lifecycle, without copying upstream implementation structure. It changes only
CPU-to-GPU transport: retained scenes, visibility, shaping/layout, atlas keys,
fallback fonts, variable-font state, DPI behavior, and device-loss generation
contracts remain unchanged as reviewed in
`docs/progpu-avalonia-rendering-research.md`.

The final two-slot Buttons process retained 256 KiB of upload-ring storage and
15 native buffers, versus 128 KiB and 14 native buffers for the previous
queue-write arena. The live Metal increase was 96-224 KiB across fresh runs,
within the expected one-extra-slot bound. A full eight-second Xcode Metal
System Trace initially exposed two rare 32 KiB fallback staging allocations
and one retained 32 KiB fallback arena. After the non-blocking retry, the same
full capture contained zero fallback arenas and zero post-startup staging
allocations. Both lanes peaked at 31.13 MiB of Metal allocation. Startup still
contains twenty short-lived queue staging allocations totaling 2.42 MiB while
initial glyph/path/buffer resources are created; those are a separate cold
initialization target. The steady optimization therefore trades one bounded
128 KiB retained slot for zero allocation/deallocation churn.

Artifact:

- `artifacts/avalonia-submission-pool-instruments-20260726`

Forcing device completion after every submit was also measured and rejected.
Changing both compositor maintenance polls from non-blocking to blocking
reduced graphics residency by only 1.20 MiB, increased physical footprint by
1.00 MiB, and reduced throughput from 119.31 to 99.13 FPS. Render-pass time
rose to 4.652 ms and compositor time to 5.121 ms. Serializing the queue is
therefore not a valid submission-pool memory optimization.

Artifact:

- `artifacts/avalonia-submit-wait-experiment-20260726`

The latest warm active-process capture separates the remaining footprint:

| Steady active view | ProGPU | original Skia | Difference |
| --- | ---: | ---: | ---: |
| Physical footprint | 337.20 MiB | 274.50 MiB | +62.70 MiB |
| Working set | 228.31 MiB | 211.84 MiB | +16.47 MiB |
| AGX graphics resident | 166.70 MiB | 111.10 MiB | +55.60 MiB |
| `VM_ALLOCATE` resident | 83.00 MiB | 62.70 MiB | +20.30 MiB |
| IOSurface resident | 25.00 MiB | 37.90 MiB | -12.90 MiB |
| Native allocator payload | 17.26 MiB | 25.39 MiB | -8.13 MiB |

These accounting views overlap and must not be summed. They show that the
remaining gap is not an untracked ProGPU texture pool: it is primarily the
wgpu/AGX working set plus the larger active CoreCLR/JIT/GC region, partly
offset by two fewer IOSurface drawable allocations. ProGPU's live native
allocator payload is 8.13 MiB lower than Skia's, so ordinary native heap
objects do not explain the process gap.

Across the six-second warm window, AGX graphics and IOSurface residency were
flat in both lanes. ProGPU physical footprint changed by +2.90 MiB and
`VM_ALLOCATE` by +1.70 MiB; Skia changed by +6.00 MiB and +3.70 MiB
respectively. This is stable high-water behavior, not evidence of a monotonic
compositor, Metal-resource, or command-buffer leak. ProGPU retained 36
`MTLResourceList` objects and 13 native Metal command buffers versus 8 and 2
for Skia, but blocking completion proved that attempting to collapse that
driver pool costs substantial throughput without materially reducing the
working set.

Artifact:

- `artifacts/avalonia-active-memory-20260726`

## Managed image and icon residency

Matched induced-GC captures separated reachable managed objects from the
runtime's uncollected heap counter. The initial ProGPU lane retained
19,385,735 bytes, the same renderer with HarfBuzz retained 20,108,495 bytes,
and Skia retained 9,230,602 bytes. HarfBuzz was therefore 722,760 bytes larger
than the ProGPU shaper in this workload; the managed gap was not caused by the
replacement shaping engine.

A temporary full dump traced three avoidable roots. An undrawn 500x500 native
menu bitmap retained a 1,000,024-byte decoded `Rgba32[]`, while the window and
tray icon objects independently retained two 1,048,600-byte raw RGBA buffers.
Stream-backed immutable bitmaps now use
[ImageSharp identification](https://docs.sixlabors.com/articles/imagesharp/identify.html)
to obtain dimensions without decoding. They keep compact encoded bytes until
a draw, resize, or save requests pixels, discard CPU storage after successful
upload, and permit explicit save/resize readback through `CopySrc` texture
usage.

Silk icon frames likewise retain their encoded representation. Raw pixels are
materialized only while applying or saving an icon. GLFW's public
[`glfwSetWindowIcon` lifetime contract](https://www.glfw.org/docs/latest/group__window.html)
states that image data is copied before the call returns, so no raw pixel
owner is required afterward. The final forced-GC ProGPU heap is 16,366,140
bytes: 3,019,595 bytes below the original ProGPU capture and 7,135,538 bytes
above Skia.

The final matched Release benchmark remains refresh-rate bound:

| Active metric | ProGPU | original Skia | Difference |
| --- | ---: | ---: | ---: |
| FPS | 118.905 | 119.707 | -0.67% |
| Mean frame interval | 8.450 ms | 8.389 ms | +0.061 ms |
| ProGPU compositor work | 0.819 ms | n/a | n/a |
| Managed allocation/frame | 8,383 B | 5,947 B | +2,436 B |
| Benchmark retained managed memory | 26.38 MiB | 16.48 MiB | +9.90 MiB |
| Physical footprint | 321.27 MiB | 263.03 MiB | +58.24 MiB |

The standard retained-versus-flattened pixel contract passed for nine
zero-fallback pages plus geometry clips, inherited text options, conic and
picture masks, blur/drop-shadow effects, transformed adorner clips, and all
BitmapCache scale/snap/ClearType variants.

Artifacts:

- `artifacts/avalonia-active-gcdump-20260726`
- `artifacts/avalonia-active-final-20260726`
- `artifacts/avalonia-retained-pixel-final-normal-20260726`

## Native popup and multi-window device ownership

Silk.NET previously returned `null` from `CreatePopup`, so menus, combo boxes,
tooltips, and flyouts were forced into Avalonia's overlay fallback. A typed
native popup now preserves parent ownership, managed popup positioning,
non-activating show semantics, nested popup chains, taskbar/decorations state,
and never-shown synchronous disposal.
The ControlCatalog fixture fails if `Popup.IsUsingOverlayLayer` is true, so a
successful profile proves that it exercised the native host rather than the
old overlay behavior.

The first popup run exposed a second issue: every `WindowImpl` initialized an
independent WebGPU instance, adapter, device, and queue. WebGPU deliberately
decouples device creation from canvas/presentation context creation and allows
one device to drive any number of canvases. ProGPU now reuses the first native
device lifetime while creating one independently configured surface per
window. The lifetime is reference counted, so surface disposal order does not
control device validity. `PROGPU_AVALONIA_SHARE_WGPU_DEVICE=0` is the
same-binary differential switch.

The matched 60-warm-up/180-frame native-popup results were:

| Metric | Isolated devices | Shared device |
| --- | ---: | ---: |
| FPS | 120.43 | 120.90 |
| Compile | 0.3552 ms | 0.3110 ms |
| Render | 0.3474 ms | 0.3092 ms |
| Compositor | 0.7185 ms | 0.6343 ms |
| Allocated per frame | 8,450 B | 8,452 B |
| Tracked Metal allocation | 32,391,168 B | 31,866,880 B |
| Physical footprint | 344,507,592 B | 345,212,104 B |
| Retained fallback nodes | 0 | 0 |

The 512 KiB tracked Metal reduction is deterministic. The less-than-1 MiB
whole-process difference is fresh-process noise and is not claimed as a
footprint improvement. Matched Xcode Instruments Metal System Trace runs
recorded 424 versus 418 command-buffer submissions and 72.559 versus 56.845 ms
aggregate drawable wait for isolated versus shared devices, with no new stall
pattern. Four raw traces (approximately 285 MiB) were deleted after export;
the 1 MiB XML/TOC/log/manifest evidence remains at
`artifacts/avalonia-popup-shared-device-instruments-20260726`.

Primary contracts:

- <https://gpuweb.github.io/gpuweb/explainer/#canvas-output>
- <https://gpuweb.github.io/types/interfaces/GPUCanvasConfiguration.html>
- <https://developer.apple.com/documentation/metal/MTLCommandQueue>

The shared-device resource report also makes the next target explicit. A
second surface currently owns a second per-target compositor and therefore
adds 15 buffers, four textures/views, seven bind groups, ten bind-group
layouts, four shader modules, four render/compute pipelines, and its own
retained-scene caches. Some of that state is target-specific, while shader
modules and compatible pipelines are candidates for a per-device cache.
That immutable-state coalescing is now implemented by a typed, reference-counted
device resource domain. Mutable buffers, textures, views, bind groups, atlases,
retained scenes, and surfaces remain context-local. Sharing a `Compositor`
object was rejected because its texture-context validation and disposal
callbacks are target-local even when the native device pointer is shared.

### Zero-area suspension and disposal ownership

The [GLFW framebuffer-size contract](https://www.glfw.org/docs/latest/window.html#window_fbsize)
keeps physical framebuffer pixels distinct from logical window coordinates,
particularly on Retina displays. Avalonia's pinned
[`ServerCompositionTarget`](https://github.com/AvaloniaUI/Avalonia/blob/fee9c561ce036e8a3e8cee2397c75ca599b4790d/src/Avalonia.Base/Rendering/Composition/Server/ServerCompositionTarget.cs)
already defers rendering when a platform target reports that it is not ready.
The Silk.NET surface now connects those contracts directly: an initialized
window with a zero width or height reports
`PlatformRenderTargetState.NotReadyTryLater`, and framebuffer locking throws
`RenderTargetNotReadyException` before allocating or presenting a synthetic
1-by-1 target. A later positive physical size resumes normally.

Window teardown also releases its WebGPU context and input context when the
native window has already transitioned to the uninitialized state. This closes
an owner-order edge where a shared-device reference could otherwise survive
the surface. Readiness checks and teardown remain fixed `O(1)` operations with
no steady-frame allocation. A focused integration regression covers
zero-sized suspension, lock rejection, positive-size resume, physical stride,
and target state; the Silk.NET integration suite now passes 42 tests.

### Immutable device-domain cache

The device domain keys shader modules by logical name and exact WGSL source,
bind-group and pipeline layouts by an explicit versioned ABI key, render
pipelines by their complete semantic descriptor, and compute pipelines by
shader, entry point, and explicit layout. Local render-pipeline hits compare
against the retained descriptor without constructing another vertex-layout
graph, so steady lookup remains allocation-free. ABI/source/logical-key
collisions fail instead of silently returning an incompatible object.

The matched native-popup result after this change is:

| Metric | Shared device before cache | Shared device and immutable cache |
| --- | ---: | ---: |
| FPS | 120.90 | 119.39 |
| Compile | 0.3110 ms | 0.3289 ms |
| Render | 0.3092 ms | 0.2933 ms |
| Compositor | 0.6343 ms | 0.6358 ms |
| Allocated per frame | 8,452 B | 8,992 B |
| Native bind-group layouts | 20 | 8 |
| Native shader modules | 8 | 4 |
| Native render pipelines | 9 | 5 |
| Native compute pipelines | 4 | 2 |
| Tracked Metal allocation | 31,866,880 B | 31,866,880 B |

The timing and whole-frame allocation differences are within fresh-process
variance and are not claimed as a speed improvement. A focused 10,000-lookup
test independently verifies that the repeated local pipeline-cache path
allocates zero managed bytes. The exact native immutable object counts decreased by
12 layouts, four shaders, four render pipelines, and two compute pipelines,
with unchanged tracked Metal bytes. That result is important: shader and
pipeline duplication was real native-state duplication, but it was not the
remaining Metal/AGX residency source. The next Metal-memory work must stay
focused on context-local buffers, textures, atlases, and target surfaces.

Primary contracts consulted:

- [WebGPU specification](https://gpuweb.github.io/gpuweb/) defines explicit
  pipeline-layout compatibility and device ownership for shader, layout, and
  pipeline objects.
- [WebGPU canvas output](https://gpuweb.github.io/gpuweb/explainer/#canvas-output)
  permits one device to serve multiple canvases.
- [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
  allow device contexts created from the same device to share device-dependent
  resources.
- [Skia Graphite `ResourceProvider`](https://skia.googlesource.com/skia/+/263308ea4386/src/gpu/graphite/ResourceProvider.cpp)
  centralizes device-owned immutable resource creation and reuse.
- [WebRender renderer source](https://searchfox.org/mozilla-central/source/gfx/wr/webrender)
  keeps renderer/device caches outside individual display lists.
- [Vello](https://github.com/linebender/vello) similarly associates prepared
  GPU pipelines with its renderer/device boundary.
- [HarfBuzz](https://github.com/harfbuzz/harfbuzz) remains a CPU shaping
  reference; ProGPU deliberately did not couple reusable shaped text to the
  device cache.

The adopted concept is a cache at the native device lifetime boundary. ProGPU
adapts it through exact typed keys, reference-counted leases, deterministic
device-before-resource disposal ordering, and no reflection. It rejects shared
mutable surface/compositor state and rejects moving Unicode/OpenType shaping
results into device-owned storage.

### Startup-inclusive Metal attribution

The rolling-window trace intentionally omitted allocations made before its
final three seconds. A second Metal System Trace therefore retained the full
startup interval and exported both allocation and Time Profiler tables before
deleting its 136,544,055-byte raw bundle. The reusable profiler now resolves
Xcode's cross-row XML references and writes compact JSON/Markdown attribution
for resource sizes, owners, live residency, waits, spills, hangs, and command
buffers.

The full-startup trace reached 32,292,864 bytes of
`MTLDevice.currentAllocatedSize`, consistent with the application's steady
31,866,880-byte reading. The largest resources were:

| Resource | Count | Bytes each | Total |
| --- | ---: | ---: | ---: |
| Main 2048x1600 BGRA surface drawable | 2 | 13,107,200 | 26,214,400 |
| Native popup surface drawable | 1 | 393,216 | 393,216 |
| Largest ProGPU atlas allocation | 1 | 1,081,344 | 1,081,344 |
| Alpha-glyph-sized texture allocation | 4 observed | 278,528 | 1,114,112 observed |

The two main drawable textures exactly equal
`2048 * 1600 * 4` bytes each. They are Core Animation's minimum double-buffered
physical-pixel presentation pool, not leaked compositor intermediates.
[Apple's CPU/GPU synchronization guidance](https://developer.apple.com/documentation/metal/synchronizing-cpu-and-gpu-work)
states that Core Animation drawables are expensive display resources and that
two and three are the only supported pool sizes. The
[CAMetalLayer drawable-size contract](https://developer.apple.com/documentation/quartzcore/cametallayer/1478174-drawablesize)
defines those textures in physical pixels, and
[framebuffer-only guidance](https://developer.apple.com/documentation/quartzcore/cametallayer/framebufferonly)
allows display-specific optimization when they are render-target-only.
ProGPU already requests render-attachment-only surface usage and desired frame
latency one; wgpu/Metal still correctly materializes the minimum two drawables.
Reducing these 25 MiB would require rendering the main window below Retina
resolution or abandoning direct presentation, both rejected as quality or
performance regressions.

The popup adds only 384 KiB. The remaining live allocation records comprise
approximately 1.9 MiB of non-surface textures, 1.3 MiB of explicitly attributed
wgpu buffers, 1.8 MiB of Metal driver pools, and shared/internal buffer
allocations. Xcode's per-resource `resource-size` fields include shared/system
backing and therefore do not sum to the same accounting domain as
`currentAllocatedSize`; they are used for attribution, not double-counted as
additional residency.

The trace reported no command-buffer errors or hang risks. It initially found
seven 32-byte compiler-spill events confined to first-use glyph and path
rasterizer command buffers (128 and 96 bytes respectively). Both compute
shaders used dynamically indexed private arrays for roots, sample positions,
and winding accumulators. They now use fixed `vec2`/`vec3` root records and two
explicit `vec4` sample/winding lanes. The original scalar sample-position
expressions remain explicit per lane so the compiler optimization does not
change floating-point evaluation or coverage at boundary pixels. Directional
half-open winding intervals, the 8x8 sample grid, DPI/subpixel behavior, and
packed R8 output are unchanged.

A matched startup-inclusive Metal System Trace after this change again reached
exactly 32,292,864 bytes of `MTLDevice.currentAllocatedSize`, but reported zero
graphics-compiler spills. The full retained-versus-flattened pixel contract
also passed: nine ControlCatalog pages plus native opacity masks, transformed
clips, blur/drop shadow, inherited text options, and BitmapCache
scale/snap/ClearType fixtures. The shader change therefore removes the measured
startup spill without trading away output quality or changing Metal residency.

The two startup-only hang intervals remained dominated by .NET JIT register
allocation and file `fstat`/`pread`/`open`; neither involved steady cache
locking or drawable starvation. They remain CPU/startup optimization targets,
but they are not a GPU memory leak.

A three-process startup experiment tested the runtime-supported alternatives
without changing rendering code. Enabling Quick JIT for loops did not improve
the median first rendered frame (1,004.43 ms versus the 999.63 ms baseline).
A framework-dependent ReadyToRun publish reduced the cached-launch median to
545.57 ms, but its first cold launch took 1,869.58 ms and steady physical
footprint averaged 411,916,941 bytes versus 346,260,704 bytes. Resident memory
grew by the same approximately 65.6 MiB; Metal stayed exactly 31,866,880 bytes.
ReadyToRun and Quick JIT for loops are therefore rejected as integration
defaults. The temporary 111 MiB publish and 152 MiB RID build output were
deleted after measurement.

## Reusable capture tool

`tools/ProGPU.SampleMemoryProfiler` now ships as the packable
`ProGPU.MemoryProfiler` .NET tool (`progpu-memory`). Its `capture` command
combines:

- process working set, virtual/private memory, threads, and CPU time;
- EventPipe `System.Runtime` counters for GC heap, GC commitment,
fragmentation, allocation rate, collections, JIT, and working set;
- macOS physical footprint and VM-region classifications, including
  Metal/AGX, IOSurface, IOAccelerator, malloc zones, stacks, and
  `VM_ALLOCATE`;
- optional native heap/object-class summaries;
- optional application-side ProGPU, wgpu-native, and Metal counters from a
  benchmark JSON result.

It writes a machine-readable JSON time series and a Markdown first/last growth
report. The package was packed, installed into an isolated tool directory, and
invoked through its `progpu-memory` command. VM-region growth now reports
resident and dirty bytes as separate rows; dirty graphics pages are no longer
mislabelled as newly resident memory.

Its macOS-only `instruments` command also orchestrates fresh-process
Allocations, Time Profiler, and Metal System Trace runs. It retains each
`.trace` bundle by default, console log, table of contents, exported Time
Profiler samples/hang diagnostics, Metal resource/current-allocation,
submission/completion/error, compiler-spill, and drawable-wait tables, and a
machine-readable manifest. `--cleanup-traces` instead deletes each exact trace
only after exports succeed and records the reclaimed byte count. A matched
popup capture reclaimed 177,617,589 bytes while preserving its XML/TOC/log
evidence. `--cleanup-exports` can then delete the exact TOC and exported XML
tables after the resolved JSON/Markdown summary is written, recording a second
reclaimed-byte total while retaining the summaries, logs, and manifest.
`--window` limits retained rolling trace history and repeatable
`--env NAME=value` arguments configure a fresh launch without recording values
in the manifest. This makes the repository's macOS Instruments requirement
repeatable rather than dependent on a hand-configured Instruments UI session,
while keeping trace size bounded.

The cleanup audit also found that Xcode can leave daemon-created
`instruments*.ktrace` files in the system temporary directory even after the
requested `.trace` bundle has been exported and deleted. Thirteen completed
captures had accumulated 1,842,629,976 bytes there. The capture tool now gives
each record/export sequence a unique task-owned temporary directory and, for
Xcode services that ignore that override, snapshots pre-existing system
temporary identities and deletes only new `.ktrace` identities created during
the sequential capture. A real two-second Time Profiler validation reclaimed
12,228,352 temporary bytes plus the 4,785,999-byte trace and 12,123-byte XML
exports; its JSON/Markdown summaries and manifest survived, while zero raw
trace, XML, task-scratch, or system `.ktrace` files remained.

The final spill-validation capture deleted another 139,305,294-byte raw trace,
and the path-atlas validation later deleted 226,399,989 bytes. Before the
system-temporary audit, the tool and explicit cleanup had reclaimed
1,085,177,024 bytes of raw traces. Including the orphan `.ktrace` files and
the functional cleanup probe, total raw profiler storage reclaimed is
2,940,035,352 bytes, plus the temporary 111 MiB ReadyToRun publish, 152 MiB of
RID-specific build output, and superseded screenshot runs. No `.trace` bundle
remains under `artifacts/`. Compact summaries needed to reproduce the
before/after attribution remain.

## Hysteretic path-atlas shrink

The retained Composition page demonstrated the remaining historical-peak
problem: animated path identities filled and grew the R8 atlas to 4096x4096
(16 MiB), while only five paths and 183,592 coverage bytes were active in the
final frame. Frame cleanup now performs only an `O(1)` counter check in steady
state. Once 240 frames have elapsed since the last resize, it collects the
preceding frame's active path rectangles in `O(P)` work and tries deterministic
bounded MaxRects candidates, halving independent dimensions. A shrink is
accepted only if a candidate fits and removes at least half the texture area.

The new texture retains the active placements, advances `Generation` and
`TextureRevision`, and drops stale UV entries. That generation change forces
retained and incremental scenes to rebuild before submitting any moved
coordinates. If the following complete live set needs more space, ordinary
demand growth happens in the same frame; the resize counter then imposes a new
240-frame cooldown. The infrequent shrink probe has the same bounded
multi-strategy `O(S * (P log P + P * F^2))` adversarial cost as capacity
recovery for `S=12` strategies and `F` free rectangles, but deliberately skips
the exponential exact-search fallback. Retained storage is `O(P + F)` only
during the probe.

On the 600-frame retained Composition workload:

| Metric | Historical peak behavior | Hysteretic shrink |
| --- | ---: | ---: |
| Final PathAtlas dimensions | 4096x4096 | 4096x2048 |
| ProGPU PathAtlas bytes | 16,777,216 B | 8,388,608 B |
| Final cached entries | about 600 stale/dynamic entries | 6 live/recent entries |
| Final cached padded coverage | about 15.7 MiB | 198,216 B |
| Final app Metal allocation | about 50.2 MiB | 41,517,056 B |
| Average FPS | approximately 120 | 120.02 |

The post-change Xcode Metal System Trace recorded the padded 4096x4096 Metal
texture as 17,301,504 bytes and confirmed it was deallocated. Its replacement
4096x2048 allocation was 8,650,752 bytes and live at trace end.
`MTLDevice.currentAllocatedSize` peaked at 59,801,600 bytes during growth and
replacement, then ended at 41,943,040 bytes. All 13,853 recorded command-buffer
completions reported zero errors, zero hang risks, and zero compiler spills.
The raw 226,399,989-byte trace was deleted after export.

The retained and flattened lanes were each driven for 600 measured frames,
past the resize hysteresis, and produced the same SHA-256 PNG
`961177fe1cddd635485b43ceee31ce1ce0798bfbf417129da7d4c8d0c23f2692`.
Focused tests also cover delayed shrink, generation invalidation, stale-entry
removal, deterministic non-overlapping repopulation, normalized UVs, and
visible rerasterized coverage.

## Duplicate Avalonia bitmap-glyph atlas removal

The Avalonia adapter previously decoded every encountered embedded bitmap
glyph to an `Rgba32[]`, retained that array indefinitely, copied it into a
padded temporary array, and uploaded it into an unbounded list of 1024x1024
RGBA textures. Each page reserved 4,194,304 logical texture bytes in addition
to the compositor's own color glyph atlas. Retained draw commands held raw page
textures and UV rectangles, so page reuse could not be made safe by deleting a
dictionary entry.

The duplicate GPU representation is now gone. A solid Avalonia glyph run is
recorded once as the existing shaped `DrawGlyphRun` contract even when its font
contains `sbix` or `CBDT` bitmaps. For a mixed run with a non-solid foreground,
intrinsic bitmap glyphs use a range command referencing the original shaped
index and position arrays; no one-element arrays are allocated. Scene
compilation demand-decodes those glyphs into `GlyphAtlas.ColorAtlasTexture`.
That atlas starts at 64x64 for Avalonia, grows only on demand to its configured
maximum, and advances `Generation` whenever an LRU region is reused, forcing
retained and incremental scene consumers to rebuild before stale texels can be
submitted.

Avalonia still needs bitmap dimensions while constructing `GlyphRun.Bounds`.
`BitmapGlyphCache` therefore identifies the encoded image through a
zero-copy `ReadOnlyMemory<byte>` stream and retains only six scalar metrics.
Its true LRU is capped at 2,048 successful entries and 256 failed keys. It
retains no decoded pixels and owns no `GpuTexture`; diagnostics report the
entry count, zero decoded-pixel bytes, and eviction count on every benchmark
frame.

The first forced-GC color-emoji capture then isolated another copy outside the
Avalonia adapter. A glyph-resident fallback font retained 77 separately
extracted `sbix` byte arrays averaging about 198 KiB, approximately 15.2 MiB of
encoded glyph payload. The fallback source now maps the immutable original font
once and returns borrowed table/image slices. The compact resident shaping face
is unchanged; only touched file pages become resident, and the exceptional
non-mappable path retains at most one demand-loaded glyph table.

This follows the lifetime models used by
[DirectWrite font-file fragments](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontfilestream-readfilefragment),
[HarfBuzz read-only blobs](https://harfbuzz.github.io/harfbuzz-hb-blob.html),
and [Skia file-mapped `SkData` with parent-owned subsets](https://skia.googlesource.com/skia/+/9a3f5541542/src/core/SkData.cpp).
The implementation is original and uses ProGPU's existing typed mapped-memory
owner; no foreign source structure was ported.

Matched 30-warm-up/120-frame Apple Color Emoji runs produced:

| Metric | Before mapped `sbix` slices | Mapped `sbix` slices | Change |
| --- | ---: | ---: | ---: |
| FPS | 120.91 | 120.75 | -0.13% |
| Compositor | 0.619 ms | 0.536 ms | -0.083 ms |
| Managed allocation/frame | 9,072 B | 9,112 B | +40 B |
| Benchmark retained managed memory | 45,197,696 B | 31,680,720 B | -13,516,976 B |
| Working set | 251,953,152 B | 244,023,296 B | -7,929,856 B |
| Physical footprint | 367,232,248 B | 359,679,320 B | -7,552,928 B |
| Tracked Metal allocation | 31,293,440 B | 31,293,440 B | unchanged |

The induced-GC heap fell from 31,724,286 to 17,863,091 bytes
(-13,861,195 bytes), and the greater-than-100-KiB byte-array bucket fell from
77 arrays to nine. The metric cache itself retained only a 2,872-byte table and
67 small LRU nodes. The post-change image is byte-identical to the pre-change
image. A direct retained-versus-flattened color-glyph capture also produced the
same SHA-256,
`f5b71661d0b32ea7867261ac389cc8e06581bb74e03ebbdf49f3926c4b9b9f0b`.

Against the matched 120-frame original Skia/HarfBuzz process, current ProGPU is
refresh-rate equivalent (120.75 versus 121.34 FPS), with 12.50 MiB more
retained managed memory, 14.34 MiB more working set, and 59.95 MiB more physical
footprint. The remaining footprint gap is therefore not the removed bitmap
pages or encoded glyph arrays; it remains in the previously attributed
wgpu/AGX and CoreCLR/JIT regions.

### Retained PathAtlas replay hysteresis

A 5,000-frame diagnostic run found 23 atlas growths and 17 shrinks despite only
five cached paths. Incremental-page replay continued submitting compiled UVs
without calling `GetOrCreatePath`, so the shrink probe misclassified the live
set as empty every 240 frames, shrank to the initial texture, invalidated the
scene, and immediately grew again.

Compiled-scene and incremental-page hits now mark retained path replay. When a
shrink probe sees no CPU-touched paths but retained vertices were submitted, it
packs the most recently compiled path set instead of an empty set. An
independent tall-path regression verifies that a 512x2048 live set cannot be
shrunk below its replay requirement. A 1,000-frame ControlCatalog run now stays
at one initial growth and zero shrinks. The final five-second rolling Xcode
Metal System Trace observed 738 submissions and 1,358 completions, zero
resource allocations/deallocations, zero command-buffer errors, zero spills,
and zero hang signals. Its 66,480,784-byte raw trace was deleted after compact
exports were written.

Artifacts:

- `artifacts/avalonia-bitmap-glyph-visible-panel-20260726`
- `artifacts/avalonia-bitmap-glyph-visible-panel-flattened-20260726`
- `artifacts/avalonia-bitmap-glyph-final-20260726`
- `artifacts/instruments/avalonia-color-emoji-final-20260726`

## Avalonia host presentation attribution

The host-control experiment exposed two separate issues. Avalonia's macOS Skia
composition backend advertises timeline-semaphore synchronization, not
automatic imported-image synchronization. Calling the automatic
`UpdateAsync(image)` overload on that backend produced about 303 exceptions in
the stable capture. `ProGpuHostControl` now enables shared-image readback only
when the importer explicitly advertises automatic synchronization; the same
command-line experiment consequently selects the safe custom-visual fallback
and records zero exceptions.

The retained fallback baseline averaged about 335 MiB working set and 503 MiB
physical footprint. Its stable VM breakdown contained 134.0-134.9 MiB
`VM_ALLOCATE`, a fixed 47.6 MiB IOSurface region, and 212.1-228.4 MiB graphics
residency. The post-gate run averaged about 325 MiB working set and 490 MiB
physical footprint, with 133.8-134.6 MiB `VM_ALLOCATE`, the same 47.6 MiB
IOSurface region, and 216.2-224.3 MiB graphics residency. These are separate
fresh processes on the same fallback lane, so the difference is normal
process/cache variation rather than a claimed memory optimization. The
important validation is that the unsupported path can no longer enter its
exception loop.

The final four-second rolling Xcode Metal System Trace measured
`MTLDevice.currentAllocatedSize` between a 133,005,312-byte maximum and a
122,257,408-byte last sample. It recorded 559 submissions, 913 completions,
zero command-buffer errors, zero compiler spills, and zero hang signals. The
50 drawable waits totalled 129.242 ms, with a 6.917 ms maximum.

The profiler summary originally misclassified allocation rows whose duration
used Instruments' live sentinel and counted the paired deallocation rows as
additional resources. The corrected parser first indexes explicit
`Deallocation` rows by resource ID and then counts only explicit `Allocation`
rows. The corrected trace contains 74 allocations totalling 234,127,360 bytes,
but only four resources totalling 13,926,400 bytes had no matching
deallocation at capture end.

Of the transient allocations, 69 were 3,145,728-byte Metal buffers allocated
from `libSkiaSharp.dylib` on `RenderTimerLoop`; all but one were deallocated,
usually after approximately 3-17 ms. This is the per-frame CPU bitmap upload
used by the Avalonia custom-visual fallback, not a monotonically growing
ProGPU texture leak. The roughly 500 MiB process footprint is therefore a
composition of stable CoreCLR/native virtual regions, IOSurface and
Skia/Metal cache residency, plus repeated presentation bandwidth. Eliminating
that upload requires a renderable shared IOSurface/DXGI texture and explicit
timeline-fence ownership transfer; relabelling the existing
texture-to-buffer-to-IOSurface copy cannot provide it.

Compact evidence retained:

- `artifacts/avalonia-host-custom-visual-20260726`
- `artifacts/avalonia-host-shared-gated-20260726`
- `artifacts/instruments/avalonia-host-shared-gated-20260726`
- `artifacts/avalonia-host-dawn-single-instruments-20260726`
- `artifacts/avalonia-host-dawn-memory-single-20260726`

The final trace cleanup reclaimed 65,109,461 bytes. Together with the two
superseded host traces cleaned earlier in this experiment, 237,383,908 bytes
of raw trace data were reclaimed. The broken pre-gate compact capture and
temporary package/source inspection trees were deleted after their metrics and
runtime-ABI conclusions were recorded here and in the rendering research
record.

## Bounded advanced-blend source

Advanced image blending still requires a full-size ping-pong output because
WebGPU does not allow a render pass to sample and write the same attachment.
The second full-size color target was not required: ProGPU now scans only the
indexed vertices of advanced texture draws, intersects their physical-pixel
bounds with the target and active clip, and allocates one reusable source
texture at the maximum affected width and height for that frame.

Each advanced draw uses a retained typed uniform buffer slot. Its projection
maps global logical vertices into the bounded physical target, and its physical
origin preserves global opacity-mask sampling. The blend shader subtracts the
same origin before loading the bounded source and treats fragments outside the
draw extent as transparent. This keeps compilation linear in the affected
texture indices, adds no per-frame managed texture allocation after warmup,
and preserves chained normal/advanced ordering.

The 128x96 regression case uses a translated 32x24 image clipped to 20x12.
Advanced-blend texture residency falls from two 128x96 RGBA8 textures
(98,304 bytes) to one 128x96 scratch plus one 20x12 source (50,112 bytes), a
49.0% reduction. GPU tests cover every destination-sampling blend mode with a
bounded transparent source, translated and clipped quads, bounded opacity
masks, chained advanced draws, and mixed normal/advanced sequences.

The matched Release ControlCatalog Composition run remains presentation
limited: ProGPU measured 119.778 FPS/8.383 ms versus Skia's
119.665 FPS/8.390 ms (+0.094% FPS). That page retained zero advanced-blend
bytes, so the bounded-source change is correctly neutral there. Its fresh
process snapshot still shows the broader work that remains: ProGPU retained
25.06 MiB managed versus 14.92 MiB, allocated 11.61 versus 5.50 KiB/frame,
and reported a 397.44 versus 306.92 MiB physical footprint. ProGPU's own
tracked intermediates were 3.21 MiB and Metal `currentAllocatedSize` was
52.09 MiB at benchmark completion, so neither advanced blending nor tracked
effect/layer textures explain the 90.52 MiB process-footprint gap.

A full-window eight-second Xcode Metal System Trace recorded
3,474 `currentAllocatedSize` samples with a 72,335,360-byte maximum and
67,059,712-byte final value. It observed 1,010 allocation events totalling
187,858,944 bytes and 129 resources/74,645,504 bytes live at capture end,
including two live 13,107,200-byte wgpu-native surface textures. There were
zero drawable waits, compiler spills, command-buffer errors, or hang-risk
signals. The two startup responsiveness intervals were attributed primarily
to CoreCLR JIT work and initial shader/resource creation, not a Metal
command-buffer failure. The 152,007,441-byte raw trace was deleted after
compact export; a second 57,771,109-byte tail trace with no allocation rows
was also deleted, and its redundant 217,570-byte compact directory was moved
to Trash.

## Command-ordered Skia filter and layer transients

The blur/morphology scratch pool now also owns only success-proven,
last-consumed filter-graph and `saveLayer` intermediates. A texture may enter
the pool after the GPU operation that consumes it has been submitted. Later
uses are submitted to the same WebGPU queue, so queue order prevents the new
writer from overtaking the earlier reader. Exact WebGPU context, dimensions,
format, and usage must match. Failure paths still dispose immediately.

The final layer/filter output is never simultaneously pooled and referenced
by a deferred drawing command. It may be rented from the pool for a new layer,
but ownership transfers out of the pool before recording that command and
remains with the `DrawingContext` until the command is cleared or flushed.
The existing four-texture/64 MiB cap covers all pooled transients together,
including RGBA16F byte accounting.

The reusable `ProGPU.GpuMemoryBaseline --mode filter` workload exercises a
blurred `saveLayer` at a fixed size without the `dotnet test` child-process
attribution problem. At 320x240/120 Hz, its native snapshot was unchanged
from frame 1 through frame 311: seven live WebGPU textures and 7.062 MiB of
Metal `currentAllocatedSize`. A direct Xcode Instruments capture of the
1024x800 workload retained the final five seconds. It observed 206 Metal
command-buffer completions and zero Metal resource-allocation rows in that
steady-state window, confirming that the workload had stopped creating
native textures after warmup. No drawable waits, compiler spills,
command-buffer errors, or hang signals were present.

Compact evidence is retained in
`artifacts/skcanvas-filter-pool-instruments-20260726`. The Allocations and
Metal raw traces were deleted after export, reclaiming 68,410,036 and
40,507,273 bytes respectively (108,917,309 bytes total).

## Retained command-array compaction

A post-collection full heap showed that the 1,048,600-byte array reported by
the compact `gcdump` was not live and had no GC root; it is diagnostic-capture
overhead rather than a fixed application allocation. The actual largest
ProGPU-specific managed categories were full font payloads required by the
typeface stream/table contract and the backing arrays for the parallel
retained command scene.

`RenderCommand` is a large value type. The default `List<T>` growth policy
therefore made a one-command visual retain capacity for four commands.
Retained Avalonia recording now compacts to the completed command count once.
The unchanged visual retains and reuses that exact capacity; ordinary mutable
or pooled drawing contexts keep their existing growth behavior.

On the source-built ControlCatalog `Buttons` workload, the forced-GC heap fell
from 16,366,140 to 15,129,427 bytes (-1,236,713 bytes). Live
`RenderCommand[]` storage fell from 731,496 to 45,136 bytes (-686,360 bytes),
and the measured heap gap to the original Skia control fell from 7,135,538 to
5,898,825 bytes. Evidence is retained in
`artifacts/avalonia-retained-command-compaction-gcdump-20260726`.

The matched Release Composition check after this change measured ProGPU at
119.933 FPS/8.3655 ms and Skia at 119.960 FPS/8.3665 ms. End-of-run managed
retention was 24.20 versus 14.92 MiB, and allocation was 11.51 versus
5.50 KiB/frame. Physical footprint was 396.17 versus 306.70 MiB
(+89.47 MiB); this is within the variation of the preceding fresh-process
pair and is not presented as a native-memory reduction. The compaction target
is the forced-GC managed heap, where the before/after result is direct.

The temporary full dumps used to resolve ownership totalled approximately
1.6 GiB and were deleted immediately. Two superseded direct-presentation
`gcdump` files were also removed while their JSON/Markdown summaries were
retained, reducing that artifact directory from about 10 MiB to 900 KiB.

## Incremental-page and retained draw-list allocation

The Composition workload's next sampled allocation stack was incremental-page
snapshot creation. When a visual's content revision changes, the compositor
now detaches the exact invalid page, or an obsolete revision of the same
visual, and reuses each exact-sized vertex, index, draw-call, and brush array.
Transform variants for unchanged content remain independently cached. Empty
arrays are shared and are not counted as reuse.

Across 600 measured frames this reused 1,797 arrays over 675 page
compilations. Managed allocation fell from 11,465 to 9,132 bytes/frame
(-20.35%) while FPS stayed at 120.04 versus 120.03. The sampled measurement
estimate fell from 6,549,590 to 5,419,050 bytes and the former
`CaptureIncrementalScenePage` stack disappeared from the leading allocators.

The following profile showed that immutable Avalonia draw lists were still
being copied into nested `GpuPicture` arrays while ProGPU was already
recording their owning retained visual. Retained-scene recording now expands
those typed nodes directly into the owning command list. Standalone/fallback
drawing keeps the stable-ID/revision picture cache. Transforms and retained
resource leases remain owned by the outer recording scope.

In the next matched run, retained-picture compilations fell from 991 to zero
and allocation fell again from 9,132 to 5,437 bytes/frame (-40.47%), or
52.58% below the initial 11,465 bytes/frame. FPS remained refresh-rate
limited at 119.89. End-of-run managed retention fell by 1,873,336 bytes
(1.79 MiB) compared with the immediately preceding run.

A fresh original-Skia run measured 119.969 FPS and 5,632.88 allocated
bytes/frame versus ProGPU's 119.892 FPS and 5,436.76 bytes/frame. ProGPU is
therefore within 0.064% FPS and now allocates 196.12 fewer bytes/frame
(-3.48%) on this workload. ProGPU still retained 23.21 versus 14.94 MiB
managed (+8.28 MiB), 256.78 versus 222.84 MiB working set (+33.94 MiB), and
392.28 versus 307.53 MiB physical footprint (+84.75 MiB). Its own Metal
counter was 52.09 MiB; Skia does not expose an equivalent value through this
benchmark, so Skia's JSON zero is unavailable telemetry rather than zero GPU
memory. Fresh-process footprint remains a residency snapshot, not leak proof.

Retained and flattened pixels matched for nine ControlCatalog pages and the
geometry-clip, inherited text-option, conic-mask, blur, drop-shadow, and
BitmapCache scale/snap/ClearType fixtures. The temporary multi-run screenshot
tree was deleted after the contract passed. Compact evidence remains in
`artifacts/avalonia-composition-allocation-profile-20260726` and
`artifacts/avalonia-inline-render-data-controlcatalog-20260726`; both raw
allocation traces were removed after JSON extraction.

A final Xcode Instruments capture of this exact binary retained the last five
seconds of each eight-second run. Metal System Trace observed 903 submissions
and 1,408 completions, zero resource-allocation rows, drawable waits,
compiler spills, command-buffer errors, potential hangs, or hang risks. The
rolling `currentAllocatedSize` table was unavailable in this capture, so the
benchmark's 52.09 MiB end value is retained without inventing a trace value.
The Allocations template also exposed no exportable allocation table; managed
allocation conclusions therefore remain based on the exact runtime counter
and earlier EventPipe samples. All three raw trace bundles were deleted after
export, reclaiming 189,786,015 bytes. Compact evidence remains in
`artifacts/avalonia-inline-render-data-instruments-20260726`.

## Embedded-font assembly-slice ownership

The next full-dump root audit separated capture artifacts from live data. The
1,048,600-byte and 262,168-byte arrays had no roots. Six 309-316 KiB arrays,
totalling approximately 1.88 MiB, were live font payloads rooted through
Avalonia `GlyphTypeface` objects and the cached `InterFontCollection`.
System-font files already used mapped storage; only Avalonia's packed assembly
resources were copied.

The source integration now returns a typed assembly/resource/offset/length
stream for those resource descriptors. ProGPU parses the corresponding
immutable manifest-resource slice in place and holds the resource stream as
its lifetime owner. Bounds are checked before slicing. Raw SFNT data is not
copied; WOFF/WOFF2 still takes the required normalization copy. The ordinary
stream path and package-mode Avalonia remain unchanged.

On the same source-built Composition workload:

- forced-GC heap: 14,198,298 -> 12,491,558 bytes (-1,706,740; -12.02%);
- large font payload arrays: six -> zero;
- ProGPU: 119.989 FPS, 8.3474 ms/frame, 5,283 bytes allocated/frame;
- Skia: 119.982 FPS, 8.3470 ms/frame, 5,634 bytes allocated/frame;
- end-of-run managed retention: 21.26 vs 14.92 MiB (+6.34 MiB);
- working set: 251.47 vs 222.55 MiB (+28.92 MiB);
- physical footprint: 387.69 vs 307.61 MiB (+80.08 MiB).

The improvement is deliberately claimed only for managed font retention. The
physical-footprint delta varies independently with Metal/AGX and CoreCLR
residency and remains under investigation. All retained/flattened rendering
and effect/cache pixel fixtures passed.

The post-change Xcode Instruments run exported 558 Metal submissions and 860
completions with no allocation, drawable-wait, compiler-spill,
command-buffer-error, or hang rows in its steady window. Its rolling
`currentAllocatedSize` table was unavailable. Allocations, Time Profiler, and
Metal raw traces were removed after compact export, reclaiming 192,414,221
bytes; the compact evidence directory is 5.8 MiB.

## Rectangular path-atlas residency refinement

The first hysteretic shrink reduced the retained Composition atlas from
4096x4096 to 4096x2048, but its power-of-two candidates could not reduce
either remaining axis independently: the active set contained a roughly
2408-wide path and a roughly 1608-tall path. Their combined padded coverage
was only about 188 KiB, while the rectangular R8 texture still reserved
8,388,608 bytes.

After the existing power-of-two probe, the shrink pass now greedily trims each
axis in 256-texel steps and evaluates both width-first and height-first orders.
It selects the lowest-area successful candidate and repacks only when the
candidate removes at least 25% of current area. Each trial uses the existing
bounded deterministic packer without exact branch-and-bound fallback. For
atlas dimensions `W` and `H`, step `A`, `P` live paths, and `F` free regions,
the refinement performs at most `2 * ((W + H) / A)` packing probes per order;
each probe is `O(P log P + P * F^2)` worst-case time and `O(P + F)` temporary
storage. The work occurs only after the 240-frame resize hysteresis.

On the same 600-frame source-built Composition workload, the final atlas is
2560x1792, or 4,587,520 bytes:

- PathAtlas residency: 8,388,608 -> 4,587,520 bytes
  (-3,801,088; -45.31%).
- ProGPU's Metal allocation counter: 54,624,256 -> 50,823,168 bytes,
  exactly the same 3,801,088-byte reduction.
- Process physical footprint: 406,521,104 -> 402,818,320 bytes
  (-3,702,784; -3.53 MiB).
- Average throughput remained refresh-rate limited at 119.98 FPS.
- Managed allocation was 5,453 bytes/frame; the one hysteretic repack occurs
  inside the 600-frame window and remains below the matched Skia result of
  5,634 bytes/frame.

The shrink preserves the normal texture-revision and generation contract:
moved UVs invalidate retained scenes and all live coverage is rerasterized
before submission. A focused asymmetric-live-set regression verifies the
2560x1792 result. The full retained/flattened pixel matrix passed for nine
pages plus geometry clips, aliased text, conic masks, blur, drop shadow, and
BitmapCache scale/snap/ClearType variants.

The exact post-change binary was also captured with Xcode Allocations, Time
Profiler, and Metal System Trace over the final three seconds of separate
eight-second launches. Metal recorded 531 submissions and 852 completions,
with zero drawable waits, graphics-compiler spills, potential hangs, hang
risks, or command-buffer errors. No resource allocation/deallocation appeared
in the steady window, confirming that the delayed atlas policy does not create
ongoing GPU-resource churn. The rolling `currentAllocatedSize` table was
unavailable, so the app's explicit 50,823,168-byte Metal counter remains the
end-of-run residency evidence. All three raw traces were deleted after compact
export, reclaiming 186,817,517 bytes; the retained evidence is 5.9 MiB.

## Bounded direct mask rendering

The launch capture attributed another avoidable live texture: every bounded
geometry or opacity mask already owned its final R8 coverage texture, but
non-full-target passes also retained a full 2560x1280-pixel
3,276,800-byte logical scratch texture. Metal padded that resource to
3,538,944 bytes. Each pass rendered in full-target coordinates and then copied
only the bounded region to its final mask.

Mask passes now render directly into the bounded texture. A per-mask reusable
uniform resource remaps the full-target projection into local attachment
coordinates, carries the world-space fragment origin for nested-mask sampling,
and translates intersected scissors to the attachment origin. The resources
are keyed and disposed with their typed `GpuTexture`; stable frames create no
mask resource or managed object churn. This removes the extra render target
and the texture-to-texture copy while keeping bounded mask residency and the
existing sampled-texture lifetime contract.

On the same 600-frame source-built Composition workload:

- mask scratch: 3,276,800 -> 0 logical bytes;
- tracked intermediate textures: 3,369,728 -> 92,928 bytes;
- mask copy traffic: removed;
- ProGPU Metal counter: 50,823,168 -> 47,415,296 bytes
  (-3,407,872);
- physical footprint: 402,818,320 -> 394,577,168 bytes
  (-8,241,152, including fresh-process variance);
- managed allocation: 5,453 -> 5,322 bytes/frame;
- FPS: 119.98 -> 119.96, refresh-rate equivalent.

The full-launch Xcode Metal trace confirmed that the 3,538,944-byte live
texture disappeared: live non-wgpu textures fell from 21,954,560 to
18,415,616 bytes. `MTLDevice.currentAllocatedSize` ended at 47,415,296 bytes
versus 51,249,152 in the immediately preceding launch capture
(-3,833,856). The trace recorded no drawable waits, graphics compiler spills,
hang risks, or command-buffer errors. The two reported startup responsiveness
intervals were CoreCLR JIT/device-initialization work, not GPU hangs.

All retained/flattened pixel fixtures passed, including nested/conic/picture
masks and transformed clipping. The 239,479,998-byte raw launch trace and
2,679,548 bytes of generated comparison PNGs were deleted after compact
evidence extraction. The full 2,462-test core suite also covers three offset
explicit-render-target viewport variants for ordinary, image-effect, and WPF
shader-effect masks.

### Mask-uniform upload staging cleanup

The direct-mask resource itself was reusable, but its per-frame uniform update
initially bypassed the compositor's mapped upload ring. A matched five-second
steady Metal capture attributed 51 transient 128 KiB buffers
(6,684,672 bytes) to wgpu-native's `queue_write_buffer` staging path. Across
wgpu, Metal-driver, and other attributed rows, the capture observed 58 buffer
allocations totaling 7,602,176 bytes; only three small buffers remained live,
so this was allocation/deallocation churn rather than a leak.

Every mask uniform now joins the existing CPU upload arena before
`EncodePendingSceneUploads`. The arena performs one bounded write into a
two-slot `MAP_WRITE | COPY_SRC` ring and encodes a distinct buffer copy for
each mask destination. The algorithm is `O(M)` time and upload bytes with
`O(M)` frame-arena storage for `M` mask passes; destination uniform resources
and bind groups remain keyed to their typed mask textures. Browser WebGPU
keeps the existing queue-write fallback because its callback and mapping
constraints differ.

On matched 600-frame Composition runs:

- FPS: 119.01 -> 120.11 (both refresh-rate limited);
- compositor time: 1.2366 -> 0.7682 ms in these fresh processes;
- managed allocation: 5,410 -> 5,437 bytes/frame (equivalent);
- Metal counter: 34,308,096 -> 34,177,024 bytes (-131,072);
- native staging allocations in the steady Metal window: 51 -> 0;
- all Metal resource allocations in that window: 58 / 7,602,176 bytes -> 0.

The after-change trace recorded 720 submissions and 1,426 completions with
zero compiler spills, potential hangs, hang risks, or command-buffer errors.
The rolling `currentAllocatedSize` table was unavailable in that capture, so
the after value above is the application's explicit Metal counter rather than
an inferred Instruments value. The focused offset-viewport mask pixel test
also verifies that every mask pass contributes one copy to the mapped upload
batch. Compact evidence remains in
`artifacts/avalonia-package-stream-current-20260726`,
`artifacts/avalonia-package-stream-instruments-20260726`,
`artifacts/avalonia-mask-uniform-upload-controlcatalog-20260726`, and
`artifacts/avalonia-mask-uniform-upload-instruments-20260726`.
All 284,603,016 bytes of raw before/after trace bundles were deleted after
export.

### Retained-scene cold-state ownership cleanup

The next induced-GC ownership review identified eager empty collections on
hundreds of Avalonia mirror nodes: animation dictionaries, child lists on
leaf nodes, adorner-path/clip scratch lists, specialized point/double/3D/float
drawing buffers, and retained-resource lease lists. These are optional state,
not scene topology. They now allocate on first mutation or first public buffer
access while preserving the existing typed ownership and invalidation
contracts.

This changes no rendering algorithm or cache identity. Visual traversal and
synchronization remain `O(V)` for `V` nodes; optional collection creation is
amortized `O(1)` and stable frames avoid these cold allocations. Empty child
collections still expose the same read-only behavior, clearing an unused
container still invalidates it, and specialized drawing buffers preserve
their identity after first access.

Matched 120-warmup/600-frame Composition results:

- forced-GC managed retention: 22,324,280 -> 21,970,776 bytes
  (-353,504 bytes, -0.337 MiB);
- remaining managed gap to Skia: 6.344 -> 6.007 MiB;
- FPS: 120.11 -> 120.15, refresh-rate equivalent;
- managed allocation: 5,437 -> 5,469 bytes/frame, equivalent;
- explicit Metal counter: 34,177,024 -> 34,177,024 bytes;
- retained scene: 738 nodes, one scene, zero fallback nodes.

Working set and physical footprint were lower in the fresh after-process, but
that full delta is not attributed to the managed change because those
process-level counters vary between launches. The forced-GC delta is the
direct ownership result.

The mandatory exact-binary Xcode pass observed 848 Metal submissions and
1,463 completions, zero steady-window Metal resource allocations, zero
graphics-compiler spills, zero hangs or hang risks, and zero command-buffer
errors. Four drawable waits totaled 1.993 ms, with a 1.844 ms maximum. The
Allocations template exposed no exportable allocation table, so no native
allocation claim is inferred from it. All 2,463 core tests and the full
retained/flattened pixel matrix passed.

Compact evidence remains in
`artifacts/avalonia-retained-lazy-state-controlcatalog-20260726`,
`artifacts/avalonia-retained-lazy-state-instruments-20260726`, and
`artifacts/avalonia-retained-lazy-state-pixels-20260726`. The profiler deleted
all three raw traces after compact export, reclaiming 209,235,746 bytes
(about 199.5 MiB). Once the resolved summary was written, its new
`--cleanup-exports` path also removed 9,291,069 bytes of TOC/XML
intermediates, for 218,526,815 bytes reclaimed in total. The repository
contains no retained `.trace`, `.nettrace`, `.gcdump`, `.dmp`, `.gputrace`,
`.heap`, or `.memgraph` capture.

The remaining live Metal set is now presentation-dominated. Instruments
observed two 13,107,200-byte wgpu surface textures and one 13,107,200-byte
CAMetalLayer drawable (39,321,600 bytes total). The Apple surface is already
configured through wgpu-native's typed surface extension for desired maximum
frame latency one. No private CAMetalLayer mutation or unsupported native
object inspection was added merely to force a smaller chain; doing so would
violate the reflection-free platform boundary and could introduce drawable
waits. This presentation residency is therefore retained pending a supported
backend contract, while compositor-owned textures continue to be optimized.

## Next implementation order

1. Extend the completed macOS same-device shared-memory path to typed
   Windows DXGI/D3D12 shared heaps and fences. The macOS host now renders
   directly into one timeline-serialized IOSurface, passes its
   MTLSharedEvent/value through Avalonia, and waits for the consumer value
   before reuse. The 64x64 pixel probe and full host are both operational.
2. Continue reducing the remaining forced-GC gap and per-frame allocation
   gap. The required font payload representation and parallel scene objects
   must be evaluated by ownership and reuse; neither should be moved to native
   memory merely to relabel process footprint.

## Avalonia-hosted sample footprint and chart allocation cleanup

The reusable profiler separated the launch peak from settled ownership by
holding the exact Release process after its 120-frame warm-up and 300-frame
measurement, sampling `proc_pid_rusage`, `vmmap`, .NET counters, and native
heap classes for ten seconds. The roughly 500 MiB launch/immediate reading was
not the steady working set. Before the final chart cleanup the process settled
at 259.50 MiB physical footprint and 318.72 MiB working set; the capture changed
by only +0.30 and +0.56 MiB respectively during the hold.

The managed heap census then identified one 40,000,024-byte
`DataPoint?[]` and another 4,000,024 bytes of inactive chart arrays.
`ChartShowcasePage` constructed all eighteen pivot tabs at page creation,
including the one-million-point and 100,000-point comparisons. Tabs now
materialize on first selection and retain their content after activation, so
navigation state and benchmark data are unchanged while the baseline pays only
for the visible chart.

The next EventPipe trace found eager path construction behind every
`FillQuad`, `FillTriangle`, Bezier, spline, and polyline command even though the
WinUI lane uses CPU visual-tree hit testing. Primitive commands now keep their
compact coordinates only. `GpuRenderCommandHitTestCacheBuilder` creates the
equivalent paths if and only if the independent GPU hit-test option is
requested. The ordinary compositor already compiles these primitives directly
from coordinates, so pixels, DPI/subpixel behavior, and GPU work are unchanged.
Chart line point scratch storage also moved from a new list per series/frame to
the bounded shared `ArrayPool<Vector2>` and is returned after command recording.

Matched Charting results:

| Metric | Before | Final | Change |
| --- | ---: | ---: | ---: |
| Managed retained at measurement | 87,447,040 B | 32,415,920 B | -55,031,120 B |
| Settled physical footprint | 259.50 MiB | 207.10 MiB | -52.40 MiB |
| Settled working set | 318.72 MiB | 264.94 MiB | -53.78 MiB |
| Managed allocation/frame | 302,035 B | 20,141 B | -93.3% |
| Native allocator payload | 29.38 MiB | 28.05 MiB | -1.33 MiB |
| Explicit Metal allocation | 49,676,288 B | 49,676,288 B | unchanged |
| FPS | 119.38 | 119.09 | refresh-rate equivalent |

The final hold was stable after transient graphics residency drained:
physical footprint fell from 393.80 to 207.10 MiB during the capture, while
`VM_ALLOCATE` changed only +0.30 MiB, IOSurface remained 31.20 MiB, and native
allocator payload was 28.05 MiB. The final four-second Xcode Metal window
observed 579 command-buffer completions, zero resource allocations, drawable
waits, compiler spills, hang risks, and command-buffer errors.

Compact evidence remains under
`artifacts/avalonia-sample-charting-final-20260726` and
`artifacts/avalonia-samples-final-20260726`. The current captures retained no
`.trace`, `.nettrace`, `.gcdump`, or XML exports. In addition to the capture
tool's automatic cleanup, 332 obsolete Instruments XML intermediates and the
temporary heap census were removed, reclaiming 164,651,138 bytes while keeping
JSON, Markdown, text, and log summaries.

## Paired Xcode Allocations baseline and reusable native attribution

The reusable profiler now exports the Allocations instrument's Statistics
detail before deleting a trace. Its schema-2 summary separates persistent heap
payload, anonymous VM, all VM regions, and the largest native/VM categories.
Export failure is fail-closed: the raw trace is retained rather than silently
discarding the only attribution evidence. Managed-object attribution remains
the paired EventPipe capture's responsibility.

Fresh eight-second exact-source Buttons captures used the same machine and
benchmark settings:

| Persistent category | ProGPU | Skia | ProGPU - Skia |
| --- | ---: | ---: | ---: |
| Heap plus anonymous VM | 189,989,840 B | 193,783,568 B | -3,793,728 B |
| Heap allocator payload | 29,885,392 B | 28,600,080 B | +1,285,312 B |
| Anonymous VM | 160,104,448 B | 165,183,488 B | -5,079,040 B |
| Dispatch continuations | 92,274,688 B | 92,274,688 B | 0 B |
| IOSurface | 26,214,400 B | 39,714,816 B | -13,500,416 B |
| IOAccelerator | 14,254,080 B | 4,030,464 B | +10,223,616 B |
| 64 KiB malloc class | 13,893,632 B | 4,980,736 B | +8,912,896 B |

The identical 92.275 MB dispatch-continuation reservation was first triggered
from a Foundation one-time cache-initialization stack, but is owned by
libdispatch as a private per-process virtual mapping. Of the
ProGPU capture's 212 live 64 KiB allocations, 203 were CoreCLR JIT
`ArenaAllocator` pages. These categories explain most of the previously
suspected 155 MiB native/runtime block and are not compositor texture
retention. ProGPU's aggregate heap-plus-anonymous-VM total was 3.79 MB below
Skia in this pair. Its higher IOAccelerator and JIT-arena categories are real
backend/runtime costs to monitor, but the paired result does not support a
210 MiB active Metal leak.

`All VM regions` is intentionally not used as a resident-memory comparison:
Skia reserved 546,111,488 bytes in `VM: Memory Tag 255`, versus 11,190,272
bytes for ProGPU, and both captures included hundreds of megabytes of mapped
files. Reservations and mappings overlap other views and are not equivalent
to process physical footprint.

A later allocator qualification measured only 540,672 resident and 524,288
dirty bytes inside the 92,274,688-byte dispatch mapping. Its size is the
libdispatch macOS policy of one 512-page magazine for each of this machine's
11 logical CPUs at a 16 KiB page size. The unsupported
`LIBDISPATCH_CONTINUATION_ALLOCATOR=0` diagnostic switch removed the virtual
mapping but produced no repeatable UI working-set reduction and was 2.28%
slower in an alternating five-million-work-item dispatch stress test. ProGPU
therefore retains the system default and excludes this virtual-only
reservation from physical-memory optimization claims.

Compact evidence is retained in
`artifacts/avalonia-native-memory-baseline-20260726/{progpu,skia}`. Automatic
cleanup removed 163,742,665 bytes of raw traces, 430,294 bytes of allocation
exports, and 12,981,621 bytes of Xcode temporary ktrace data. Each retained
lane is 20 KiB. A subsequent temporary 118+ MiB allocation-list audit was also
deleted, and no raw `.trace`, `.ktrace`, `.nettrace`, `.gcdump`, or
`.instrdst` files remained in the checked artifact and temporary locations.

### Active Composition residency versus virtual driver mappings

The Buttons allocation baseline does not exercise Composition's full path,
mask, and pipeline set. A second exact-binary pair therefore held Composition
active for 5,000 measurement frames. Allocations initially showed
126,894,080 bytes of persistent ProGPU `VM: IOAccelerator`, versus 16,138,240
bytes for Skia. The exported allocation list attributed 121,192,448 bytes
across 985 mappings to `IOGPUMetalResource` remote storage and 3,342,336 bytes
across 204 mappings to device shared memory. Every mapping appeared in one
startup burst between 1.951 and 2.504 seconds; none appeared during the
remaining capture.

The paired `vmmap` time series is the required residency check. At the last
sample:

| Settled signal | ProGPU | Skia | ProGPU - Skia |
| --- | ---: | ---: | ---: |
| Physical footprint | 184.70 MiB | 198.70 MiB | -14.00 MiB |
| Process working set | 231.16 MiB | 222.22 MiB | +8.94 MiB |
| IOAccelerator resident | 4.70 MiB | 12.50 MiB | -7.80 MiB |
| `owned unmapped (graphics)` resident | 59.90 MiB | 60.90 MiB | -1.00 MiB |
| `owned unmapped (graphics)` dirty | 31.90 MiB | 32.90 MiB | -1.00 MiB |
| IOSurface resident | 25.00 MiB | 37.90 MiB | -12.90 MiB |
| `VM_ALLOCATE` resident | 68.20 MiB | 49.80 MiB | +18.40 MiB |
| Native allocator payload | 33.11 MiB | 47.66 MiB | -14.55 MiB |

ProGPU's 119.0 MiB IOAccelerator mapping was therefore only 4.70 MiB
resident. Its broader graphics residency drained from 149.90 to 59.90 MiB
during the hold, while physical footprint fell from a transient 478 MiB peak
to 184.70 MiB and stayed flat. Skia's graphics residency settled at 60.90 MiB.
The suspected 210 MiB Metal/AGX leak is not present in these final binaries:
the active sets converge, and ProGPU's settled physical footprint is lower.
The remaining ProGPU working-set cost is primarily CoreCLR/JIT residency, not
GPU texture or driver residency.

The uninstrumented 600-frame pair remained refresh-rate equivalent: 119.36
FPS for ProGPU and 119.90 FPS for Skia. ProGPU allocated 5,312 versus 5,634
managed bytes/frame, retained 738 composition nodes with zero fallback nodes,
and used 34,177,024 explicit Metal bytes. Compact evidence remains under
`artifacts/avalonia-device-loss-{controlcatalog,instruments,live-memory}-20260726`.
The profiler removed 134,304,992 bytes of raw traces, 431,782 bytes of XML,
and 12,670,976 bytes of Xcode ktrace scratch from the final pair. The
additional 87 MiB full allocation-list diagnostic was deleted after the
category/timestamp audit; no raw profiling bundles remain.

## Package-only shared-device disposal-order residency

The replacement-package consumer now exercises both shared-device destruction
orders without project references or runtime inspection. It creates an owner
and borrower on one typed `WgpuContext` device domain, disposes the original
owner, verifies that the borrower still has an active context and renders new
frames, opens another borrower, disposes it, and again verifies frames from the
survivor. The final ordinary run produced 66 frames: 24 before owner disposal,
22 after owner disposal, and 20 after borrower disposal. It observed both typed
shared-device pairs, one retained scene, and zero fallback nodes.

The same exact package-built binary was held after the lifecycle transition
for matched native and GPU captures. A one-window maximized run is included to
separate surface size from disposal-order retention:

| Settled signal | One maximized window | Multi-window survivor | Multi - one |
| --- | ---: | ---: | ---: |
| Process working set | 189.11 MiB | 193.14 MiB | +4.03 MiB |
| Physical footprint | 329.00 MiB | 295.90 MiB | -33.10 MiB |
| `owned unmapped (graphics)` resident | 166.00 MiB | 163.40 MiB | -2.60 MiB |
| IOSurface resident | 56.80 MiB | 28.00 MiB | -28.80 MiB |
| `VM_ALLOCATE` resident | 55.70 MiB | 57.30 MiB | +1.60 MiB |
| Native allocator payload | 23.89 MiB | 18.79 MiB | -5.10 MiB |
| Metal `currentAllocatedSize`, last | 62.65 MiB | 31.03 MiB | -31.62 MiB |
| Explicit Metal resources live at end | 61.61 MiB | 32.89 MiB | -28.72 MiB |

The five-sample live intervals were stable: graphics residency changed by
zero in both runs, while physical footprint grew only 1.20 MiB for the
single-window process and 1.30 MiB for the post-disposal survivor. Xcode
reported zero command-buffer errors and graphics compiler spills. The
multi-window run had two drawable waits totaling 0.888 ms. Its largest live
resources were the expected current survivor surface/drawable textures, not
textures from the disposed windows.

This matched result rejects a shared-device destruction leak. The roughly
163–166 MiB `owned unmapped (graphics)` region is a stable AGX
high-water/residency category in this small package app; it is not the live
Metal resource total and does not grow across owner or borrower disposal.
Presentation dimensions explain the explicit GPU difference: the one-window
lane is maximized, while the final survivor is 560 by 420 logical units. The
post-disposal process therefore has less IOSurface and explicit Metal
residency than the one-window baseline.

The short-lived Allocations template failed to finalize twice even after the
application was held beyond the requested capture. The profiler's hard bound
stopped only those process trees and removed both incomplete traces and their
private Xcode scratch. Native allocator and VM attribution therefore comes
from the paired `heap`/`vmmap`/.NET sampler; no Allocations result is inferred.
The sampler now excludes failed `vmmap` samples from first/last growth instead
of reporting a false zero when a target exits during collection, and records
successful versus failed VM-map sample counts.

Compact evidence remains in
`artifacts/instruments/avalonia-package-multiwindow-time-metal-20260726`,
`artifacts/instruments/avalonia-package-singlewindow-20260726`, and
`artifacts/instruments/avalonia-package-singlewindow-metal-20260726` (136 KiB
combined). Automatic and explicit cleanup reclaimed 1,809,751,576 bytes
(about 1.69 GiB) of raw traces, XML exports, and Xcode `ktrace` scratch, plus
the 320 MiB isolated package-build staging directory. Both failed capture
directories were removed. No `.trace`, `.ktrace`, `.nettrace`, `.gcdump`,
`.instrdst`, or exported XML files remain under `artifacts/instruments`.

## GPU-native Avalonia render-target bitmaps

The remaining framebuffer-shaped offscreen path was not merely a compatibility
abstraction. `RenderTargetBitmapImpl` inherited an eagerly allocated CPU
buffer and sampleable bitmap texture, while `FramebufferRenderTarget` retained
a second render-attachment texture and a row-aligned readback buffer. Every
drawing-context disposal rendered into the intermediate texture, synchronously
read it into the CPU buffer, and immediately uploaded the same pixels into the
bitmap texture.

The implementation now gives the bitmap's single texture all four required
typed usages: `RenderAttachment`, `TextureBinding`, `CopySrc`, and `CopyDst`.
`DrawingContextImpl` renders directly into that texture and advances its
content generation once per submitted frame. The bitmap allocates no CPU pixel
storage during ordinary render/sample use. `Save` and `Lock` are explicit CPU
boundaries: they allocate the CPU storage and perform one WebGPU
`copyTextureToBuffer` readback only when the GPU copy is newer. A writable lock
uploads once when released. No runtime reflection or platform-handle escape is
involved.

GPU rendering, CPU readback/upload, and bitmap version invalidation use the
same fixed owner-then-device lock order. Version/CPU invalidation occurs only
after both locks are held and before submission, so another same-device
consumer cannot observe a new bitmap version backed by the previous pixels,
and a concurrent `Lock`/`Save` cannot return a stale CPU copy.

For a `W` by `H` RGBA target, the old steady backing cost was
`12WH + align256(4W)H` bytes: CPU pixels, two textures, and the retained
readback buffer. The new ordinary GPU path is `4WH` bytes. At a row width
already aligned to 256 bytes this reduces per-bitmap backing storage from four
full copies to one (-75%); a 1920 by 1080 bitmap drops from 31.64 MiB to
7.91 MiB. After an explicit save/lock, CPU pixels remain available and the
steady cost is two full copies, while the temporary readback staging buffer is
released.

Focused regressions validate correct opaque pixels, stable texture identity,
generation advancement, zero CPU allocation before an explicit CPU boundary,
and absence of the former intermediate texture. Both Avalonia 12 and the
shared-source Avalonia 11 renderer build the same path.

Avalonia layer surfaces keep the same one-texture steady path. Their texture
now advertises `CopySrc`, but no readback buffer is retained. `Save` flushes
pending retained commands into that existing texture, performs the explicit
temporary readback, preserves RGBA/BGRA channel order, and encodes the actual
pixels; the previous placeholder implementation encoded a blank image.

The exact-source Buttons screenshot gate exercised `RenderTargetBitmap.Render`
and `Save` against the final Release binary. It produced a valid 788 by 1710
RGBA PNG, retained 789 composition nodes with zero fallback nodes, reported
zero compositor intermediate-texture bytes, and sustained 120.05 FPS over
180 uninstrumented frames at 3,958 managed bytes per frame.

The required final Xcode qualification launched that exact rebuilt state for
Time Profiler and Metal System Trace. Its rolling Metal window completed 392
command buffers and recorded 22 drawable waits totaling 50.019 ms, with zero
graphics-compiler spills, potential hangs, hang risks, or command-buffer
errors. The launched benchmark reported 119.84 FPS, 3,951 managed bytes/frame,
30,261,248 explicit Metal bytes, and zero tracked intermediate texture bytes.
No Metal resource allocation occurred in the steady rolling window, so the
per-bitmap storage reduction is established by typed resource ownership and
focused live-state regressions rather than inferred from an absent
Instruments allocation event.

Compact evidence is retained in
`artifacts/avalonia-render-target-direct-20260726` and
`artifacts/instruments/avalonia-render-target-direct-20260726`. Automatic
cleanup across the iterative and final qualification runs deleted all six raw
traces, their supported XML exports, and task-owned plus Xcode `ktrace`
scratch, reclaiming 638,654,800 bytes (609.07 MiB). No
raw trace, XML, nettrace, gcdump, or Instruments bundle remains in the capture
directory.

## Affined capture and resize qualification

`SurfaceRenderTarget.CreateNonAffinedSnapshot` previously returned the same
device-owned layer object. Avalonia can dispose that source target immediately
after snapshot creation and consume the returned bitmap from another render
context, so the result was neither an independent snapshot nor non-affined.
The implementation now flushes the existing texture, performs one explicit
GPU-to-CPU copy, and returns immutable context-neutral RGBA storage. It creates
no destination texture until first consumption, retains the CPU representation
for later device migration, and keeps at most one destination-device texture.
Same-context layer blits remain direct texture reuse and do not enter this
boundary.

Silk.NET framebuffer metadata now reports DPI from the physical framebuffer to
logical-window ratio. Avalonia has already applied that scale to composition
command transforms, so the ProGPU host continues to compile those commands in
normalized physical-pixel space rather than multiplying DPI twice. The
screenshot gate caught and rejected that double-scaling variant before
integration. A resize regression verifies that the old offscreen texture and
its optional readback capacity are both disposed before the new size becomes
resident.

The corrected exact-source Buttons image again rendered as a valid 788 by 1710
RGBA capture with 789 retained nodes, zero fallback nodes, and zero tracked
intermediate texture bytes. The final Xcode Time Profiler plus Metal System
Trace run measured 720 frames at 113.15 FPS and 4,267 managed bytes/frame while
instrumented, with a 342,279,344-byte physical footprint, 30,261,248 explicit
Metal bytes, 215 completed command buffers, 14 drawable waits totalling
51.636 ms, and zero compiler spills, potential hangs, hang risks, or
command-buffer errors. The fresh process retained only 93,810 bytes of compact
evidence. Automatic cleanup deleted 322,877,916 bytes of raw traces, XML
exports, and Xcode `ktrace` scratch; no raw profiling artifact remains.

## Final embedded-sample Instruments attribution

The final allocation correction was qualified with the reusable profiler
against the Charting embedded Avalonia sample for 600 measured frames. The
instrumented application reported 119.395 FPS, 5,895 B/frame,
32,497,208 managed bytes, 273,203,200 resident bytes, a 401,393,032-byte
physical footprint, and a 457,082,248-byte peak. Explicit Metal allocation was
49,676,288 bytes, the path and glyph atlases were 256 KiB each, and tracked
intermediate texture storage was zero.

Xcode Allocations attributed 231,565,824 persistent bytes to heap plus
anonymous VM: 24,209,920 bytes of allocator payload and 207,355,904 bytes of
anonymous VM. The largest live attributed regions were one 92,274,688-byte
libdispatch virtual reservation triggered from Foundation, 41,598,976 bytes of
IOAccelerator storage, two IOSurfaces totaling 32,768,000 bytes,
18,153,472 bytes of CoreCLR stacks, 13,729,792 bytes of CoreServices storage,
4,866,048 bytes of Metal resource-list storage, and 1,900,544 bytes of JIT
pages. Their first/last timestamps place the large regions in startup rather
than in a per-frame growth sequence.

The rolling four-second Metal window completed 729 command buffers and
recorded zero resource-allocation rows, drawable waits, compiler spills,
potential hangs, hang risks, or command-buffer errors. This directly rejects
the suspected steady Metal/AGX allocation churn: the roughly 400 MiB process
footprint is a combination of runtime/native reservations, driver state, and
the expected presentation surfaces, not 400 MiB of live ProGPU textures.

Compact evidence is retained in
`artifacts/avalonia-sample-device-domain-final-instruments-20260726`. The
profiler deleted 190,233,742 bytes of raw `.trace` files, 38,374,466 bytes of
XML exports, and 173,009,514 bytes of task-owned/Xcode scratch after extracting
the summaries. Four earlier EventPipe traces totaling about 138 MiB were moved
to Trash after their allocation stacks were converted to compact JSON; they
remain recoverable until Trash is emptied.

## Context-owned Avalonia backend Instruments check

Moving retained target scenes from leased drawing contexts to the
graphics-context-owned `ProGpuCompositionServerBackend` did not introduce a
new texture or target copy. Matched short Buttons runs reported zero tracked
intermediate-texture bytes in both Silk.NET and Avalonia Native/Dawn, with one
789-node scene and 90 typed backend renders in each fresh process.

The post-change Xcode Allocations capture attributed 199,317,360 persistent
bytes to native heap plus anonymous VM: 36,083,568 bytes of heap payload and
163,233,792 bytes of anonymous VM. The largest live GPU/window categories were
two QuartzCore IOSurfaces totaling 26,214,400 bytes and 14,745,600 bytes of
IOAccelerator VM. These are smaller than the prior four-surface Native/Dawn
capture but are not treated as a causal improvement because capture windows
and drawable-pool occupancy differ. The important invariant is unchanged:
there is no roughly 210 MiB live Metal/AGX allocation attributable to ProGPU
textures.

The final Metal window reported zero compiler spills, potential hangs, hang
risks, or command-buffer errors. It observed 458 completions but no
allocation/submission rows in the retained window, so it is evidence about
error absence, not throughput or resource-allocation rate. Automatic cleanup
removed 124,035,712 bytes of raw traces, 115,637,504 bytes of Xcode scratch, and
29,716,025 bytes of XML exports after compact summaries were written. The
temporary compact summaries were inspected and then removed rather than added
to the repository artifact set.

## Clean-room bitmap boundary and JIT-native attribution

The clean-room Avalonia bitmap replacement initially exposed a startup failure
for the ControlCatalog tray-icon ICO. The replacement now parses the ICO
directory directly, selects a supported embedded PNG or uncompressed 24/32-bit
DIB, and preserves the ICO transparency mask. PNG, JPEG, TIFF, GIF, and BMP
dimensions are read from bounded encoded headers without decoding pixels.
ImageSharp identification remains a no-inline fallback for uncommon formats;
ordinary pixel decode and resize remain lazy.

Three matched 15-second Xcode Allocations captures were collected before and
after moving that fallback behind the lazy boundary. Each launched the rebuilt
Release source ControlCatalog Composition page for 120 warm-up plus 5,000
measurement frames; allocation details were exported, summarized, and deleted
along with the raw traces and Xcode scratch.

| Median persistent category | Before lazy boundary | After lazy boundary | Delta |
| --- | ---: | ---: | ---: |
| Heap plus anonymous VM | 308,030,816 B | 302,643,744 B | -5,387,072 B |
| Heap allocations | 35,728,736 B | 30,018,992 B | -5,709,744 B |
| Anonymous VM | 272,302,080 B | 272,416,768 B | +114,688 B |
| 64 KiB JIT arena pages | 13,959,168 B | 8,388,608 B | -5,570,560 B |
| IOAccelerator VM | 122,896,384 B | 122,978,304 B | +81,920 B |
| IOSurface VM | 26,214,400 B | 26,214,400 B | 0 B |

### Bounded Avalonia font projection follow-up

The imported Avalonia font manager/typeface/glyph-run cluster was replaced by
a thin projection over the process-wide `ProGPU.Text.FontManager`. The new
projection caps retained typeface wrappers at 256, shares parsed system fonts,
keeps font-table and font-stream ownership zero-copy where the Avalonia
contract permits it, and stores each retained glyph run as one glyph-ID array
plus one packed `Vector2` position array. Decoration intersection queries are
rare and recompute conservative glyph bounds instead of retaining duplicate
`Point` and bounds arrays per run.

The exact rebuilt source ControlCatalog Composition page completed 300 measured
frames at 120.49 FPS, 8.3328 ms mean frame time, and 4,914 managed bytes/frame.
It retained one 739-node scene, zero fallback nodes, 29,605,888 explicit Metal
bytes, and 92,928 tracked intermediate-texture bytes. Relative to the preceding
clean-room bitmap run, FPS and explicit GPU ownership are unchanged. Managed
heap and physical-footprint snapshots moved upward by about 0.60 MiB and
1.97 MiB respectively; a single process high-water value is insufficient to
attribute that small movement to the font change.

A required 15-second Xcode Instruments follow-up attributed 301,915,584
persistent bytes to heap plus anonymous VM: 29,449,664 bytes of allocator
payload and 272,465,920 bytes of anonymous VM. Relative to the previous
three-run post-lazy median, those values changed by -728,160 bytes, -569,328
bytes, and +49,152 bytes respectively. This is within startup variance and is
not presented as a causal memory win. The persistent 64 KiB JIT arena was
7,405,568 bytes, IOAccelerator VM was 123,027,456 bytes, and the two IOSurfaces
remained 26,214,400 bytes.

Allocation timestamps again place the large GPU reservations entirely in the
startup interval: 119,652,352 bytes of attributed IOAccelerator storage were
created between 2.414 and 2.989 seconds, and the two IOSurfaces between 2.778
and 3.001 seconds. The retained Metal window completed 1,072 command buffers
from 560 observed submissions with zero compiler spills, potential hangs, hang
risks, or command-buffer errors. It contained no Metal allocation rows, so it
supports steady-state allocation absence but does not measure startup resource
bytes.

Compact evidence is retained in
`artifacts/native-avalonia-font-catalog-cleanroom-20260726`. Automated cleanup
removed 300,092,211 bytes of raw traces, 37,498,511 bytes of XML exports, and
251,405,847 bytes of task/Xcode scratch, leaving only the summaries, manifest,
and logs.

## Clean-room Avalonia backend-context ownership follow-up

The imported renderer backend context was replaced with an independently
authored, typed surface selector. Construction performs no GPU initialization.
Metal/Dawn creation is demand-driven, one Dawn context is owned by the Avalonia
renderer context, and every nested or offscreen `SurfaceRenderTarget` receives
the already selected `WgpuContext` explicitly. This prevents a layer from
creating a second native WebGPU device when thread-global context state changes.
Surface discovery remains O(S) time for S candidates with O(1) temporary
storage. Three new contracts verify lazy framebuffer selection, readiness, and
same-device offscreen-layer ownership; the clean-room suite now passes 51
tests.

The deterministic uninstrumented workload used the rebuilt Release source
ControlCatalog Composition page, 120 warm-up frames, and 300 measured frames in
each of three fresh processes. Median FPS was 120.57, median reported physical
footprint was 348,390,648 bytes, managed allocation was about 4.80 KiB/frame,
and the application still reported only 29,605,888 explicit Metal bytes and
92,928 tracked intermediate-texture bytes. Frame-time p99 ranged from 10.195 to
10.628 ms. The ownership change therefore did not exchange memory for lower
throughput or broader scene invalidation.

Native validation repeated the preceding exact 15-second Instruments protocol:
three fresh Allocations captures, each running 120 warm-up plus 5,000 requested
measurement frames, with allocation attribution enabled. The table compares
three-run medians:

| Persistent category | Preceding baseline | Context-owned layers | Delta |
| --- | ---: | ---: | ---: |
| Heap plus anonymous VM | 302,643,744 B | 301,021,088 B | -1,622,656 B (-0.54%) |
| Heap allocations | 29,997,600 B | 29,316,624 B | -680,976 B (-2.27%) |
| Anonymous VM | 272,416,768 B | 271,630,336 B | -786,432 B (-0.29%) |
| IOAccelerator VM | 122,978,304 B | 122,191,872 B | -786,432 B (-0.64%) |
| IOSurface VM | 26,214,400 B | 26,214,400 B | 0 B |
| 64 KiB JIT arena pages | 8,388,608 B | 7,995,392 B | -393,216 B |
| All VM regions | 973,602,816 B | 976,863,232 B | +3,260,416 B |

The aggregate reduction is repeatable across the matched captures, but remains
small relative to driver reservations and includes JIT and mapped-region
variance. It is therefore a bounded ownership improvement, not evidence that
the remaining native footprint was a leak. The two dominant non-file regions
remain approximately 122 MiB of startup-created IOAccelerator VM and the same
92,274,688-byte per-process libdispatch virtual reservation observed in the
Skia baseline. IOSurface ownership is unchanged.

A final matched Allocations, Time Profiler, and Metal System Trace qualification
recorded 560 submissions, 1,076 completions, and ten drawable waits totaling
15.401 ms. It reported zero Metal allocation rows in the final rolling window,
zero compiler spills, zero potential hangs or hang risks, and zero command
buffer errors. That proves absence of steady-window resource creation and
driver errors, not absence of the bounded startup driver working set.

Compact evidence is retained under
`artifacts/avalonia-backend-context-sharing-20260726`,
`artifacts/native-avalonia-backend-context-sharing-long-20260726-run-{1,2,3}`,
and `artifacts/native-avalonia-backend-context-sharing-full-20260726`.
Automatic cleanup reclaimed 691,770,396 bytes of raw traces, 140,907,765 bytes
of XML exports, and 299,158,878 bytes of Xcode/task scratch from the matched
captures. Three protocol-mismatched short native summaries were moved to Trash
and remain recoverable until Trash is emptied.

Two of the three post-change processes retained about 8.3 MiB of JIT arena
pages; the third retained 13.9 MiB. The result is therefore a 5.14 MiB median
heap-plus-anonymous-VM reduction with material startup/JIT variance, not a leak
claim. GPU-owned categories were unchanged within run-to-run variation.

A paired EventPipe JIT trace recorded 20,196 compilations in the same
fresh-process workload. The largest generated-code namespace groups were
`ProGPU.Scene` (786,588 bytes), `Avalonia.Rendering` (621,588 bytes),
`System.Collections` (541,128 bytes), `Avalonia.Controls` (529,916 bytes), and
`Avalonia.Media` (353,064 bytes); `SixLabors.ImageSharp` contributed 135,468
bytes. The reusable profiler's `inspect` command now prints this namespace
ledger from JIT allocation events, keeping JIT code ownership separate from
native allocator high-water marks.

The final Xcode Metal capture reported 647 submissions, 1,301 completions,
six drawable waits totaling 16.175 ms, and zero compiler spills, potential
hangs, hang risks, or command-buffer errors. The roughly 123 MiB
IOAccelerator value remains a startup driver reservation: the prior matched
warm `vmmap` series showed only about 4.7 MiB resident for the larger
119 MiB attributed allocation group. It must not be added to explicit
29.6 MiB ProGPU texture/buffer counters or described as a 123 MiB live texture
leak.

Compact capture evidence is retained under
`artifacts/performance/native-avalonia-cleanroom-cache-valid-20260726`,
`artifacts/performance/native-avalonia-header-identify-20260726`, and their
`run2`/`run3` peers. Raw trace/export cleanup reclaimed approximately 1.0 GiB
across the seven allocation/Metal captures.

## Canonical rounded geometry and matched native-memory ledger

The clean-room Avalonia recorder previously converted every rounded rectangle
to a generic path. Three large ControlCatalog background/card shapes then
missed the compositor's direct rounded-rectangle classifier because their
quadratic representation contained redundant zero-length corner segments.
Those three entries forced the path atlas from 512x512 to 2048x2048 and
retained 2,763,792 bytes of CPU coverage.

The recorder now emits the repository's typed `ArcSegment` representation and
omits zero-length square-corner segments. The compositor accepts a canonical
four-corner circular rounded path as direct bounded triangle/SDF geometry.
Arbitrary and non-canonical paths still use the atlas. The unchanged Buttons
page now retains only its three small icon paths: 3,768 coverage bytes and a
512x512, 262,144-byte atlas. Focused path-atlas lifecycle tests use deliberately
non-canonical pressure paths so reset/retry coverage is not weakened.

Three fresh 120-warm-up/5,000-measured-frame Buttons processes produced the
following medians:

| Metric | Before canonical direct path | After | Delta |
| --- | ---: | ---: | ---: |
| FPS | 120.60 | 120.60 | 0.00 |
| Managed allocation/frame | 3,817 B | 3,775 B | -42 B (-1.1%) |
| Physical footprint | 335,561,904 B | 329,565,360 B | -5,996,544 B (-1.79%) |
| Explicit Metal allocation | 33,832,960 B | 29,458,432 B | -4,374,528 B (-12.93%) |
| Path-atlas texture | 4,194,304 B | 262,144 B | -3,932,160 B |
| Cached path coverage | 2,763,792 B | 3,768 B | -2,760,024 B |

The exact retained/flattened pixel gate subsequently passed all nine catalog
pages plus linear/conic/picture masks, transformed clips, blur, drop shadow,
and BitmapCache scale, snap, and ClearType variants with zero retained fallback
nodes. This is therefore a representation/resource optimization, not a
quality-reduction shortcut.

### Avalonia ProGPU versus original Skia

The final active Buttons comparison alternated three fresh processes per
backend. ProGPU's median physical footprint remained 329,794,736 bytes versus
276,661,472 bytes for Skia (+53,133,264 bytes, +19.21%). It also delivered
about 120.50 versus 119.81 FPS and allocated approximately 3,776 versus 5,966
managed bytes/frame. Physical footprint alone does not identify native heap or
texture ownership, so six matched 15-second Xcode Allocations captures were
collected from those exact Release products.

| Instruments persistent median | ProGPU | Skia | ProGPU - Skia |
| --- | ---: | ---: | ---: |
| Native heap plus anonymous VM | 183,288,752 B | 189,714,048 B | -6,425,296 B (-3.39%) |
| Native allocator payload | 21,988,272 B | 24,923,776 B | -2,935,504 B (-11.78%) |
| Anonymous VM | 160,890,880 B | 164,806,656 B | -3,915,776 B (-2.38%) |
| All VM reservations | 848,805,888 B | 1,392,967,680 B | -544,161,792 B (-39.06%) |
| IOAccelerator VM | 10,911,744 B | 4,030,464 B | +6,881,280 B |
| IOSurface VM | 26,214,400 B | 39,321,600 B | -13,107,200 B |
| `MALLOC_SMALL` VM | 58,720,256 B | 71,303,168 B | -12,582,912 B |

This rejects the hypothesis that Buttons' extra physical footprint is a native
heap leak: ProGPU retained less native heap and anonymous VM than Skia in the
matched Instruments protocol. A paired 20-second live ledger showed the first
physical-footprint samples at 333,656,883 and 279,445,504 bytes. Repeated
`vmmap` sampling then pressured purgeable graphics residency: ProGPU's owned
unmapped graphics region fell from 144,598,630 to 6,225,920 bytes and Skia's
from 133,274,010 to 24,222,106 bytes. In the same interval ProGPU
`VM_ALLOCATE` residency changed by only 419,430 bytes and Skia by 314,573
bytes. The higher active snapshot is therefore dominated by reclaimable
driver high-water state plus runtime/JIT code pages, not monotonic retained
textures.

The end-of-capture native allocator payload was 19,950,064 bytes for ProGPU
and 38,520,672 bytes for Skia. Forced-GC live managed objects were 14,218,141
and 10,279,981 bytes respectively; this separate 3,938,160-byte managed gap is
not counted as native ownership. The reusable
`tools/profile-avalonia-managed-memory.sh` wrapper now captures benchmark
telemetry, sampled process/`vmmap` regions, native heap attribution, EventPipe
counters, and a forced-GC dump in one matched ledger. It records and removes
task-specific trace, XML-export, and Xcode scratch sizes after compact
summaries are written.

Compact evidence is retained under
`artifacts/avalonia-cleanroom-direct-rounded-profile-20260726`,
`artifacts/instruments-avalonia-buttons-progpu-direct-rounded-20260726-run-{1,2,3}`,
`artifacts/instruments-avalonia-buttons-skia-20260726-run-{1,2,3}`, and
`artifacts/avalonia-buttons-full-memory-ledger-20260726`.

### WinUI gallery native follow-up

The same Xcode Allocations protocol was repeated three times against the
current Release WinUI `Basic Input` workload. Persistent native heap plus
anonymous VM was 211,109,920 bytes at the median, down 2,741,568 bytes
(-1.28%) from the earlier 213,851,488-byte capture. Native allocator payload
was 33,162,832 bytes (-399,120 bytes), anonymous VM was 177,930,240 bytes
(-2,359,296 bytes), and all VM reservations were 1,492,598,784 bytes
(-6,340,608 bytes). IOSurface VM fell by 524,288 bytes; median IOAccelerator
VM remained 17,989,632 bytes.

The current Metal trace recorded 2,083 submissions, 3,984 completions, 415
drawable waits totaling 1.657 seconds, zero compiler spills, zero hangs or hang
risks, and zero command-buffer errors. Xcode emitted no resource-allocation or
`currentAllocatedSize` rows even with the full 15-second export window, so this
capture establishes completion/error/churn behavior but cannot replace the
application's explicit resource ledger with an Instruments byte count. A zero
row count is not reported as zero GPU memory.

Compact current WinUI evidence is retained under
`artifacts/instruments-winui-basic-input-current-20260726-run-{1,2,3}` and
`artifacts/instruments-winui-basic-input-current-metal-full-window-20260726`.
The raw traces, XML exports, and Xcode scratch were removed automatically after
the summaries were generated.

### Paged retained state and bounded-mask resource ownership

The generation-checked Avalonia retained-state store removes the compositor
mirror's visual dictionary, full-sync visited hash set, and stale-node list.
Matched 120-warm-up/600-frame runs remained refresh-limited. Buttons retained
22.95 MiB managed and allocated 3.68 KiB/frame; Composition retained
20.76--20.94 MiB and allocated 5.56--5.57 KiB/frame. This change deliberately
does not own native GPU resources, so the matching Xcode capture is the causal
gate: ProGPU retained 307,192,096 bytes of native heap plus anonymous VM versus
Skia's 223,512,608 bytes. ProGPU's 119,095,296-byte remote-storage
IOAccelerator group was still the dominant difference.

The next mask-resource pass removed eager creation of text and texture uniform
bind groups for vector-only mask passes. On the animated Composition page this
reduced live WebGPU bind groups from 32 to 16; only eight vector-mask render
bind groups remain. Tightening the sliding mask-pool floor after the
eight-frame demand window then reduced the stable pool from eight to three
entries and the final live object counts from 24 to 19 buffers, 12 to 7
textures/views, and 16 to 11 bind groups. The complete retained/flattened
pixel matrix still matches byte-for-byte.

These object-lifetime improvements did not reduce the Metal driver high-water:
the final Allocations A/B reported 122,519,552 bytes of IOAccelerator VM and
307,312,064 bytes of heap plus anonymous VM, within fresh-process variance of
the pre-change capture. A short diagnostic run explains why. The first
Composition frames compile as many as 60 simultaneously addressable mask
surfaces before the retained scene settles to one active mask and a
three-entry pool. Releasing those WebGPU objects is correct and bounds the
steady state, but AGX retains its startup-created remote-storage pool.
Therefore the next causal optimization is to avoid creating those surfaces:
encode common rounded clips analytically in the fragment mask contract and/or
pack independent bounded masks into a GPU mask atlas. Merely shrinking,
disposing, or reusing the same standalone textures cannot erase the already
established driver high-water and is not presented as a footprint win.

Compact evidence is retained under
`artifacts/avalonia-paged-state-profile-20260727`,
`artifacts/avalonia-paged-state-instruments-progpu-20260727`,
`artifacts/avalonia-paged-state-instruments-skia-20260727`,
`artifacts/avalonia-mask-bindgroup-lazy-instruments-20260727`,
`artifacts/avalonia-mask-pool-tight-instruments-20260727`, and
`artifacts/avalonia-paged-state-mask-pixels-20260727`. All raw traces,
allocation-list exports, and Xcode scratch bundles were removed after compact
summaries were written.

### Affine analytic clips remove the AGX resource high-water

Lifetime telemetry refined the startup demand from a texture count into its
semantic source. The animated Composition page requested 48 simultaneously
addressable masks, all of which were canonical sharp rectangles under affine
transforms. Their off-axis matrix magnitude ranged from approximately
0.000217 to 2.0. None was a general path, opacity mask, or rounded geometry.
The old path created a standalone texture, view, render-pass resources, and
driver-side resource metadata for every one of those clips before returning
the textures to the bounded pool. Disposing and pooling correctly bounded
steady state but could not undo the AGX startup high-water.

The compositor now carries an original 96-byte typed mask record containing
the physical-to-local affine mapping, local bounds, per-corner elliptical
radii, and mask mode. Canonical rectangles and rounded rectangles are tested
analytically in each affected fragment shader. The implicit rectangle or
corner-ellipse boundary uses WGSL derivatives for transform-aware
antialiasing. This is fixed `O(1)` work and storage per fragment and performs
no mask-texture sample in the analytic branch. A dummy white texture preserves
the existing bind layout; one pooled uniform buffer and bind group represent
each simultaneously live analytic mask. Nested/general geometry, opacity
masks, and nested offscreen mask composition retain the texture fallback. The
outermost `RenderOffscreen` transaction is also the direct presentation
transaction for an imported Dawn drawable and uses analytic clips; nested
layer/effect transactions do not. A nested fallback can consume an analytic
parent through the same typed bind-group contract.

The design follows the derivative semantics in the
[WGSL specification](https://www.w3.org/TR/WGSL/#derivative-builtin-functions)
and WebGPU's explicit
[resource-binding model](https://www.w3.org/TR/webgpu/). It adapts the
already-recorded retained-scene and device-resource separation from WebRender,
Vello, DirectComposition, and Skia Graphite. It rejects CPU tessellation,
runtime shader generation, reflection, unbounded exact-transform cache keys,
and applying the analytic shortcut to non-canonical geometry. No foreign
implementation source or test source was copied.

Two alternating fresh Release processes per backend, each with 120 warm-up and
600 measured Composition frames, produced:

| Metric | ProGPU analytic clip | Skia reference | ProGPU - Skia |
| --- | ---: | ---: | ---: |
| Mean FPS | 120.230 | 119.916 | +0.314 (+0.26%) |
| Mean frame time | 8.334 ms | 8.348 ms | -0.014 ms |
| Managed allocation/frame | 5.00 KiB | 5.81 KiB | -0.81 KiB (-14.0%) |
| Managed retained | 20.99 MiB | 15.20 MiB | +5.79 MiB |
| Maximum physical footprint | 319.88 MiB | 300.33 MiB | +19.55 MiB |

Physical footprint includes reclaimable graphics residency and runtime state,
so a matched Xcode Allocations capture remains the ownership gate. Against the
same Composition workload, the analytic ProGPU binary retained 188,949,648
bytes of native heap plus anonymous VM. The preceding texture-mask binary
retained 307,312,064 bytes, a reduction of 118,362,416 bytes (38.5%). The
matched Skia capture retained 223,512,608 bytes, so ProGPU is 34,562,960 bytes
(15.5%) lower on this allocation-attributed native ledger.

| Instruments persistent category | Texture masks | Analytic clips | Delta |
| --- | ---: | ---: | ---: |
| Native heap plus anonymous VM | 307,312,064 B | 188,949,648 B | -118,362,416 B |
| IOAccelerator VM | 122,519,552 B | 10,158,080 B | -112,361,472 B |
| IOGPU remote storage | 119,177,216 B / 929 live | 9,633,792 B / 143 live | -109,543,424 B / -786 |
| MTL resource lists | 5,062,656 B / 103 live | 835,584 B / 17 live | -4,227,072 B / -86 |

The Metal trace recorded 827 completions with zero compiler spills, drawable
waits, potential hangs, hang risks, or command-buffer errors. Application
telemetry recorded zero mask textures and zero mask render passes, four total
textures/views, 17 buffers, nine bind groups, and two retained analytic mask
bind groups at the end of the run. The 48-mask semantic peak is retained in
telemetry without materializing 48 GPU surfaces.

Both focused affine sharp-rectangle and affine elliptical-rounded-rectangle
pixel tests pass. The complete retained/flattened matrix also passes all nine
catalog pages plus geometry/inherited-text clips, opacity masks, blur,
drop-shadow, and BitmapCache variants byte-for-byte. This is a resource
representation change rather than reduced render quality.

Compact final evidence is retained under
`artifacts/avalonia-affine-mask-profile-20260727`,
`artifacts/avalonia-affine-mask-instruments-20260727`, and
`artifacts/avalonia-affine-mask-pixels-20260727`. Raw Instruments traces,
allocation exports, and task-specific Xcode scratch were removed after compact
summaries were written. A mask atlas is now only a potential measured
follow-up for non-canonical or independently composable masks; it is not
justified for canonical affine clips.

The first Avalonia Native/Dawn qualification exposed that direct imported
drawables enter the compositor through its outermost `RenderOffscreen`
transaction. Treating every nonzero offscreen depth as a nested effect had
therefore kept Dawn on the 48-texture path even though Silk.NET was analytic.
The corrected contract enables analytic clips at depth one and retains the
fallback only for deeper layer/effect transactions. A focused offscreen
affine-clip test validates both zero mask passes and output pixels.

The final corrected Native/Dawn Composition run produced 119.63 FPS, 4.88 KiB
allocated per frame, zero mask textures/passes, zero tracked intermediate
texture bytes, and the required `DawnMetalIOSurface` presentation path.
Matched Xcode Instruments attributed 197,850,976 bytes of native heap plus
anonymous VM, 13,615,104 bytes of IOAccelerator VM, and 589,824 bytes across
12 Metal resource lists. The trace recorded 330 submissions, 863 completions,
and zero spills, hangs, hang risks, or command-buffer errors. Compact evidence
is retained in `artifacts/avalonia-affine-mask-dawn-fixed-profile-20260727`
and `artifacts/avalonia-affine-mask-dawn-instruments-20260727`.

The shared WinUI compositor was also requalified on Basic Input. It delivered
209.75 wall FPS with vsync disabled and 820 bytes allocated per frame.
Instruments attributed 212,557,232 bytes of heap plus anonymous VM versus the
prior 211,109,920-byte capture (+1,447,312 bytes, +0.69%), with the identical
92,274,688-byte per-process libdispatch virtual reservation and
32,768,000-byte two-IOSurface pool. This workload does not create the 48
affine masks, so the small
fresh-process variation is a no-regression result rather than an expected
memory reduction. The Metal trace completed 1,331 command buffers with no
spills, hangs, hang risks, or errors. Evidence is retained in
`artifacts/winui-basic-input-affine-mask-20260727` and
`artifacts/instruments-winui-basic-input-affine-mask-20260727`.

### Dense vector-text atlas compaction

The post-mask 54-page desktop sweep completed every page, but isolated one
secondary ownership regression on `Text & Documents`. Changing PathAtlas from
an eager 2048-square texture to a 512-square starter correctly reduced common
startup residency, while the later 4096 maximum allowed scrolling phase
variants from preceding frames to accumulate. The 497-entry terminal snapshot
therefore retained an 8 MiB R8 texture. This was not live coverage demand:
the final frame used 86 paths with about 96 KiB of coverage.

PathAtlas growth now alternates the shorter axis while copying the old texel
rectangle, keeping incremental textures at most 2:1 and preserving resident
coordinates. More importantly, exhaustion of a recovery-packed free-space map
performs an allocation-free `O(C)` live-area scan. When the current frame uses
at most half the available area, stale variants cause one compilation
transaction to abort. The existing bounded deterministic packer then keeps
only the complete current-frame set, rerasterizes before submission, advances
the generation, and retries the same frame. After the first miss, later paths
are recorded without additional probes or doomed raster work. This preserves
the one-retry/no-stale-UV correctness contract.

The internal retry exception also stopped eagerly formatting every rectangle
on successful recovery. A constant sentinel drives the normal transaction;
the detailed live-set message is built only if the bounded retry actually
fails. This removed diagnostic `String`, `Char[]`, and `StringBuilder`
allocations from the scrolling hot path without reducing terminal diagnostics.
The reusable sample profiler now logs exact atlas width/height, live and cached
coverage, growth, avoided-growth, shrink, and resize-age counters.

Matched 30-warm-up/60-measured-frame Release results are:

| Metric | Pre-compaction current binary | Final compaction | Delta |
| --- | ---: | ---: | ---: |
| PathAtlas texture | 8,388,608 B | 524,288 B | -7,864,320 B (-93.75%) |
| Total tracked GPU textures/staging | 9.83 MiB | 2.33 MiB | -7.50 MiB |
| Managed allocation/frame | 357,316 B | 331,500 B | -25,816 B (-7.2%) |
| Managed retained | 130,533,264 B | 129,689,104 B | -844,160 B |
| Physical footprint | 509,084,968 B | 496,289,088 B | -12,795,880 B |
| Compile time | 3.1847 ms | 1.9130 ms | -39.9% |
| Compositor time | 4.0873 ms | 2.7073 ms | -33.8% |
| Wall FPS | 58.44 | 58.40 | -0.07% |

Against the older eager-2048 baseline, the final atlas is 3,670,016 bytes
smaller (-87.5%), allocation falls from 340,870 to 331,500 bytes/frame,
managed retention falls by 461,312 bytes, physical footprint falls by
16,515,048 bytes, and compositor time falls from 3.7586 to 2.7073 ms. The
single-process wall-FPS difference from that older binary is not used as a
causal claim because other shared-compositor changes intervened; the
immediate before/after pair above is the path-atlas gate.

Xcode Allocations, Time Profiler, and Metal System Trace then captured a
15-second continuously scrolling run. Persistent native heap plus anonymous
VM was 209,554,320 bytes: 24,284,048 allocator bytes and 185,270,272
anonymous-VM bytes. IOAccelerator VM was 21,839,872 bytes and 54 live Metal
resource lists used 2,654,208 bytes. Metal `currentAllocatedSize` peaked at
51,838,976 bytes and ended at 51,249,152 bytes. The explicit resource table
had only four live buffers totaling 655,360 bytes at capture end. The trace
recorded 761 submissions, 1,269 completions, two drawable waits totaling
2.820 ms, and zero compiler spills, hangs, hang risks, or command-buffer
errors. The 2.58 GiB cumulative native-allocation traffic was transient;
207,932,304 bytes remained attributable at capture end, so it is not reported
as a retained leak.

The final 60-warm-up/180-measured-frame sweep reran all 54 desktop pages from
fresh processes and completed 54/54. Fifty pages reached at least 190 wall
FPS and 44 reached at least 200 FPS; the mean was 205.62 FPS. Relative to the
pre-compaction sweep, `Text & Documents` improved from 62.96 to 64.49 FPS,
compile time from 2.496 to 1.462 ms, managed retention from 129.77 to
127.84 MiB, physical footprint from 510.69 to 478.52 MiB, and tracked GPU
textures/staging from 17.82 to 2.09 MiB. Its terminal PathAtlas was 512-square
(262,144 bytes) after the stable-delay shrink. No page failed or reported a
PathAtlas capacity error. The complete report is
`artifacts/sample-memory-final-compacted-atlas-all-20260727`.

Three focused PathAtlas growth/compaction contracts and the combined 250-test
sample-performance/compositor/layer gate pass. Compact evidence is retained in
`artifacts/text-documents-compact-sentinel-matched-20260727` and
`artifacts/instruments-winui-text-documents-compacted-atlas-20260727`.
Instruments raw traces, XML exports, and task-specific Xcode scratch were
removed automatically after summary generation.

### Inline retained-change queue coalescing

The compact protocol no longer keeps both an ordered changed-visual list and a
hash set containing the same managed object references. One high bit in the
existing byte-sized per-visual retained-change state marks list membership;
exact target-revision acknowledgement clears the marker and accumulated
typed-delta bits together. Repeated changes before acknowledgement therefore
coalesce without hashing, allocating, or changing list order.

Three alternating 120-warm-up/600-measured-frame Composition runs completed
for each backend. ProGPU averaged 120.288 FPS and 4.99--5.01 KiB/frame; Skia
averaged 119.917 FPS and 5.81--5.82 KiB/frame. ProGPU's median managed
retention was 20.96 MiB. Xcode Allocations reported 188,714,864 bytes of
persistent native heap plus anonymous VM, versus 188,949,648 bytes before the
queue change. The 234,784-byte difference is only 0.12% and is not claimed as
a whole-process improvement. The causal memory result is deletion of the
duplicate hash buckets and entries. IOAccelerator remained 10,158,080 bytes,
IOGPU remote storage 9,633,792 bytes, and Metal resource lists 835,584 bytes
across 17 lists, confirming that the managed queue change did not perturb GPU
residency.

The focused pinned-source compositor gate passes 5/5 and the full ProGPU core
suite passes 2,487/2,487. Evidence is retained under
`artifacts/avalonia-inline-change-queue-profile-20260727` and
`artifacts/avalonia-inline-change-queue-instruments-20260727`. The Instruments
capture deleted 57,503,553 bytes of raw trace, 28,723,514 bytes of XML
exports, and 10,898,557 bytes of Xcode/task scratch after producing the compact
summary.

The final integration gate also found a quality boundary in the sharp-path
rectangle specialization. A primary-target rectangle can use one analytic
quad without reserving atlas coverage. A rectangle recorded into an offscreen
picture or layer cannot: its coverage is materialized and then filtered again
when the texture is composed. Native SkiaSharp 3.119.4 reproduced the existing
picture-shader expected pixels exactly, while applying the direct SDF before
that second filter changed fractional edge coverage. Offscreen recorded paths
therefore retain PathAtlas coverage and final-target paths keep the
zero-residency specialization. The native-Skia parity test, all 2,487 core
tests, and the final retained/flattened Avalonia matrix pass. Final pixel
evidence is retained in
`artifacts/avalonia-final-skia-parity-pixels-20260727`.

### Captured retained-delta handles

The compact protocol now queues a typed value containing the primitive
backend owner, index-plus-generation handle, and retained identity captured at
insertion time. The direct ProGPU page lookup uses those captured values and
validates the transitional Avalonia source shell. A released/reused slot
therefore cannot make an older queued change address a new visual; the
incremental transaction fails closed into the existing full synchronization.
This removes no Metal texture by itself, but it establishes the ownership
boundary needed to apply future serialization deltas directly to paged state
without re-reading mutable object identity.

Matched Xcode Allocations captures measured 189,713,984 bytes of persistent
native heap plus anonymous VM for ProGPU and 223,791,088 bytes for original
Skia, a 34,077,104-byte (15.2%) ProGPU advantage. ProGPU used 29,216,320 bytes
of native heap payload and 160,497,664 bytes of anonymous VM versus Skia's
46,843,888 and 176,947,200 bytes. IOAccelerator VM was 11,108,352 versus
16,203,776 bytes; IOSurface residency was 26,214,400 versus 39,321,600 bytes.
The retained managed heap remains higher in the ordinary benchmark (about
21.0 MiB versus 15.2 MiB), so the next compact-protocol task remains moving
render-property payload out of managed server objects rather than attempting
to reduce an already-lower matched native/AGX ledger.

The managed follow-up also corrected the profiler's retention terminology.
`dotnet-gcdump report` showed 13,264,627 bytes for ProGPU and 9,528,901 bytes
for Skia, but a root-aware `dotnet-dump` walk found only 11,552,234 and
8,882,780 live bytes. The apparent unique 1,048,576-byte ProGPU array had zero
GC roots and was excluded from the live set. The exact live managed gap is
therefore 2,669,454 bytes, not 3,735,726 bytes. Its largest concrete
ProGPU-only owners are the 739 typed compositor mirror visuals (336,984
bytes), retained render-command/vector/text arrays, and the managed system
font catalog; it is not a native allocation or GPU texture leak. The reusable
managed-memory wrapper now writes the root-filtered report and deletes its
roughly 800 MiB temporary dump plus raw `.gcdump` files after compact evidence
has been produced.

All raw `.trace` files, allocation exports, and task-specific Xcode scratch
were deleted automatically after the compact summaries were generated.
Evidence is retained in
`artifacts/avalonia-typed-handle-delta-instruments-20260727` and
`artifacts/avalonia-typed-handle-delta-profile-20260727`. Root-filtered managed
reports are retained under
`artifacts/avalonia-typed-handle-delta-managed-20260727`.

### Root-filtered Avalonia visual cold state

The root-aware report identified two avoidable retained owners rather than a
native or GPU leak. Every ProGPU `Visual` eagerly carried fields for animation,
outer and composite clips, geometry clips, opacity masks, effects, content
bounds, raster padding, bitmap-cache policy, and layer-texture ownership even
when the visual used none of them. `AvaloniaCompositionScene` also retained a
second child list solely as synchronization scratch. Those optional fields now
live in a lazily allocated typed `VisualColdState`; child synchronization
compares and edits the persistent child list in place.

Default property reads and writes remain allocation-free and preserve their
previous values and invalidation behavior. First use of optional state is
`O(1)`. Child synchronization remains `O(C)` for `C` children, preserves
stable identity, and allocates no duplicate `List<AvaloniaCompositionVisual>`.
The cold-state object remains owned after first use so repeated clip, effect,
mask, and layer mutations do not churn the managed heap.

In the root-filtered matched capture, 739 Avalonia mirror visuals fell from
456 to 328 bytes each: 336,984 to 242,392 bytes, a 94,592-byte or 28.1%
reduction. The duplicate Avalonia visual-list type disappeared. Total live
ProGPU managed memory was 11,641,229 bytes versus 9,135,365 bytes for Skia, a
2,505,864-byte gap; the preceding root-aware gap was 2,669,454 bytes, so this
slice removed 163,590 bytes after accounting for run-to-run object variation.
The ordinary forced-GC benchmark also fell by 234,443 bytes on average across
three fresh ProGPU processes.

The three-run alternating Composition gate measured:

| Backend | Mean FPS | Allocated bytes/frame | Mean managed bytes | Mean physical footprint |
|---|---:|---:|---:|---:|
| ProGPU | 120.228 | 5,114.28 | 21,778,955 | 331,394,915 |
| Skia | 119.980 | 5,951.69 | 15,954,749 | 313,733,019 |

The ProGPU FPS change relative to its immediately preceding three-run set was
-0.024%, and allocation changed by 0.093 byte/frame. The change is therefore
performance-neutral within measurement noise while reducing retained
ownership.

Matched Xcode Allocations attributed 190,884,224 bytes of persistent native
heap plus anonymous VM to ProGPU and 223,217,424 bytes to Skia. ProGPU was
32,333,200 bytes (14.48%) lower. Its allocator payload/anonymous VM split was
29,387,136/161,497,088 bytes versus Skia's
46,302,992/176,914,432 bytes. IOAccelerator VM was
12,091,392 versus 16,269,312 bytes, and IOSurface residency was
26,214,400 versus 39,321,600 bytes. This independently reconfirms that the
remaining Avalonia difference is managed retained state, not a larger ProGPU
native/AGX working set.

The same current binary's WinUI `Basic Input` capture retained
188,786,048 bytes of native heap plus anonymous VM
(27,633,024 heap and 161,153,024 anonymous VM), 32,768,000 bytes of
IOSurface, and 1,212,416 bytes of end-of-capture IOAccelerator VM. This is
22,323,872 bytes below the previous 211,109,920-byte three-run median, but it
is intentionally reported as a single-run no-growth/leak check rather than a
causal improvement: drawable and IOAccelerator high-water marks vary
materially across launches. The WinUI workload allocated 683 bytes/frame and
retained 22,237,088 managed bytes across 300 frames.

All 2,489 core tests, 40 focused visual/layer/clip/effect tests, the complete
retained/flattened ControlCatalog pixel matrix, package-only ordinary and
two-window runtime smokes, and the macOS arm64 NativeAOT runtime smoke pass.
Evidence is retained under `artifacts/avalonia-cold-visual-*20260727` and
`artifacts/instruments-winui-basic-input-cold-visual-20260727`. The profiler
deleted the two temporary full dumps (851,644,728 and 882,823,432 bytes), both
raw GC dumps, and every raw Xcode trace/XML/scratch artifact after emitting
the compact reports.

### Compact incremental-page draw calls

The next root-attributed owner was the retained incremental-scene page
draw-call array. Page admission permits only vector, text, and texture draws,
but each page retained the complete 256-byte `CompositorDrawCall`, including
unused chart matrices, static-buffer/extension objects, custom parameters,
brushes, pens, paths, and series ranges. Pages now store an original typed
56-byte `IncrementalScenePageDrawCall` projection and expand it into the
general hot-path value during replay. Admission remains fail-closed for every
unsupported draw kind and mask texture.

This is `O(D)` capture and replay for `D` page draw calls, as before. It adds no
per-frame allocation, changes no public field or ABI, retains typed texture
ownership, and records the actual projection size through
`Unsafe.SizeOf<T>()` instead of the previous inaccurate 160-byte metric
constant.

The matched root-filtered Composition capture retained 141 one-draw pages.
Their arrays fell from 39,480 to 11,280 bytes: 256 to 56 payload bytes per
entry and 28,200 fewer live bytes including unchanged array headers
(-71.4%). Total live ProGPU memory fell from 11,641,229 to 11,609,007 bytes;
Skia measured 9,135,381 bytes in the new matched process. The exact page
payload is the causal result; the additional 4,022-byte whole-heap difference
is ordinary process variation.

The same-binary 600-frame run measured 120.226 FPS and 5,107.27 allocated
bytes/frame, versus the preceding 120.264 FPS and 5,112.25 bytes/frame.
Incremental page telemetry fell from 133,648 to 118,984 bytes because the
metric now uses the actual 56-byte representation. The complete 2,490-test
core suite and retained/flattened ControlCatalog pixel matrix pass.

The mandatory exact-binary Xcode Allocations capture retained
188,709,520 bytes of native heap plus anonymous VM
(29,063,824 heap and 159,645,696 anonymous VM), 10,125,312 bytes of
IOAccelerator VM, 26,214,400 bytes of IOSurface, and 16 Metal resource lists
using 786,432 bytes. Compared with the recent matched Skia allocation ledger
of 223,217,424 bytes, current ProGPU remains 34,507,904 bytes (15.46%) lower.
The managed-only projection is not credited with that process-level native
difference.

The rebuilt exact replacement stack passed public API/assembly identity,
runtime-reflection, package-only runtime, shared-device two-window lifecycle,
and macOS arm64 NativeAOT execution. Ordinary/multi-window/AOT runs rendered
28/65/40 ProGPU frames with zero fallback nodes. The AOT executable was
22,845,744 bytes. Evidence remains under
`artifacts/avalonia-compact-page-drawcall-*20260727`; temporary full dumps,
raw GC dumps, the 57,631,696-byte Instruments trace, 28,854,496 bytes of XML
exports, and 10,572,837 bytes of Xcode/task scratch were deleted after compact
reports were written.

### Retained-command and final native attribution

The subsequent managed-owner optimization changed no GPU texture, staging, or
pipeline policy: it replaced ordinary Avalonia mirror
`RenderCommand[]` residency with typed vector/glyph projections and one
reusable recording context. A fresh matched Instruments capture is therefore
a native regression gate, not a claim that managed compaction caused driver
memory to fall.

ProGPU retained 176,094,176 bytes of native allocator payload plus anonymous
VM, compared with 213,472,032 bytes for the same Skia Composition workload
(-37,377,856 bytes, -17.5%). The split was
19,037,152/157,057,024 bytes for ProGPU and
34,394,912/179,077,120 bytes for Skia. Both retained the identical
92,274,688-byte dispatch-continuation reservation. ProGPU retained two
QuartzCore IOSurfaces totaling 26,214,400 bytes versus Skia's three totaling
39,321,600 bytes, and 11,173,888 bytes of IOAccelerator VM versus
18,317,312 bytes.

These results reject the hypothesis that the current ProGPU Avalonia backend
has a larger native/AGX live set than Skia on the matched workload. The
largest common anonymous allocation is a one-time per-process libdispatch
virtual reservation, not a growing texture or managed/native leak. Both Metal traces
reported zero compiler spills, potential hangs, hang risks, and
command-buffer errors. Raw traces, allocation tables, and Xcode scratch were
removed after compact summaries were emitted.

### Native SwiftUI/Metal control workload

A clean-room native control now separates the Apple window/Metal floor from
.NET, Avalonia, ProGPU, Dawn, and wgpu-native. It embeds an `MTKView` in
SwiftUI through `NSViewRepresentable`, owns one long-lived
`MTLCommandQueue`, uses `framebufferOnly = true`, acquires the drawable
immediately before encoding, schedules presentation on the command buffer,
and commits without a CPU wait. These choices follow Apple's
[MTKView lifecycle](https://developer.apple.com/documentation/metalkit/mtkview/),
[display-only texture](https://developer.apple.com/documentation/metalkit/mtkview/framebufferonly),
[drawable lifetime](https://developer.apple.com/library/archive/documentation/3DDrawing/Conceptual/MTLBestPracticesGuide/Drawables.html),
[command queue](https://developer.apple.com/documentation/metal/MTLCommandQueue),
and [SwiftUI AppKit integration](https://developer.apple.com/documentation/swiftui/nsviewrepresentable)
contracts.

The 800 × 600-point window produced a 1600 × 1200 drawable on the 2× display.
The default lane measured 142,082,048 bytes of initial physical footprint,
94,162,125 bytes of resident `owned unmapped (graphics)`, 23,592,960 bytes
of IOSurface, 3,211,264 bytes of IOAccelerator, and 6,015,960 bytes of native
allocator payload. The graphics, IOSurface, and IOAccelerator rows were stable
across the sampled interval; physical footprint changed by only 209,715
bytes. Three 1600 × 1200 BGRA drawables contain 23,040,000 payload bytes,
which explains the IOSurface result without a leak.

The same native application independently reproduced the exact
92,274,688-byte dispatch virtual reservation with only 196,608 resident and
dirty bytes in the initial sample. The unsupported malloc diagnostic removed
the virtual row, but ended 1,782,579 bytes above the default physical
footprint and retained 329,584 bytes more allocator payload. This strengthens
the earlier rejection: ProGPU must not ship a private libdispatch override.

The attached default/diagnostic Metal traces recorded 121/120 submissions,
929/924 completions, and zero compiler spills, hangs, hang risks, or
command-buffer errors. Attachment occurs after the first frame, so startup
Metal resource-creation rows are not misrepresented as zero allocations; the
paired `vmmap` regions provide the physical resource ledger.

Applied to ProGPU is the reusable profiling capability, not a speculative
renderer rewrite. `progpu-memory instruments` now supports a typed
`--attach <pid-or-name>` lane for GUI applications and `capture` supports
`--no-runtime-counters` for native targets. The wrapper records physical
`vmmap`, native heap, Allocations, Time Profiler, and Metal System Trace data,
then removes raw Xcode traces, exports, scratch, and the signed probe.
ProGPU's direct presentation already meets the relevant control properties:
render-attachment-only surface usage, late acquire/present/release, a
one-frame wgpu-native Apple latency hint, and two retained QuartzCore
IOSurfaces rather than the control's three. No production rendering change is
supported by the measurement. Compact evidence is under
`artifacts/swiftui-metal-memory-20260727`.

### Compact text-presentation upload stream

Per-buffer upload telemetry identified a genuine bandwidth cost rather than a
texture leak: alternating inherited text mode and effective opacity changed
all 603 96-byte glyph instances. The previous path transferred 28,138,752
bytes over 600 measured frames. Encoding text mode in the global 256-byte
brush record was also rejected after it increased transfer to 32,528,352
bytes by renumbering unrelated vector and text brush indices.

The shipping candidate instead adds one typed 32-byte text-style record per
solid text command. Glyph placement, atlas coordinates, and transforms remain
stable; color, effective opacity, and rendering mode update in the independent
storage stream. For the Buttons fixture this adds only a 1,536-byte GPU buffer.
Three equivalent Release runs transferred 920,064 bytes each, entirely from
that style buffer, with zero glyph/vector/brush/index/texture transfer. This is
96.7% less upload traffic while preserving byte-identical retained/flattened
screenshots, 119.45-119.96 FPS, 3,780 managed bytes/frame, and zero fallback
nodes.

The Xcode qualification retained compact summaries and removed 197.8 MB of
raw trace bundles, 34.3 MB of exported XML, and 265.6 MB of Xcode/task scratch.
It found zero Metal spills, hangs, hang risks, command-buffer errors, or
drawable waits. The Allocations process observed three QuartzCore IOSurfaces
and the known one-time 92,274,688-byte dispatch virtual reservation; neither is
attributed to the 1,536-byte ProGPU text-style buffer. Compact evidence is
under `artifacts/avalonia-text-style-stream-20260727`.

### Appearance fallback-classification memory gate

The retained Avalonia delta consumer now calls `RequiresFallback` only for a
complex `Appearance` change. Transform and primitive opacity/visibility
snapshots already carry every field they require and no longer reread managed
clip, mask, effect, cache, adorner, and render-option state. `AdornedVisual`
was added to Avalonia's complex-appearance publication mask so the gate remains
conservative. This change removes managed hot-path work; it does not add or
remove a GPU texture.

This section records an intermediate gate. After layout, geometry, cache,
effect, mask, inherited-option, topology, and adorner channels were completed,
the catch-all had no publisher and both it and `RequiresFallback` were removed
from incremental synchronization. Full synchronization keeps conservative
fallback classification.

Three alternating 600-frame runs retained one 739-node scene and zero tracked
intermediate textures. ProGPU averaged about 5,385 managed bytes/frame versus
Skia's 5,947.3 bytes/frame while sustaining 120.182 versus 120.002 FPS. The
full retained-versus-flattened pixel matrix was byte-identical with zero
fallback nodes.

Matched Xcode Instruments runs attributed 179,059,920 bytes of persistent
heap plus anonymous VM to ProGPU and 211,502,192 bytes to Skia. ProGPU
retained 26,214,400 bytes of IOSurface and 11,173,888 bytes of IOAccelerator
VM versus Skia's 39,321,600 and 16,269,312 bytes. Both processes retained the
same 92,274,688-byte dispatch virtual reservation, and both Metal traces
reported zero spills, hangs, hang risks, or errors. The exact replacement
packages then passed ABI/reflection audits and a package-only 29-frame smoke
with one retained scene and zero fallback nodes. Compact evidence is under
`artifacts/avalonia-appearance-fallback-gating-*20260727`; raw profiling
artifacts were removed.

### Retained-protocol ownership and final managed/native split

The next root-aware pass found two 49,176-byte startup high-water delta arrays
in both ProGPU and Skia processes. Active ProGPU targets now retain burst
capacity until 60 successful low-delta synchronizations, then reduce to a
16-entry steady capacity. The two arrays settle at 1,584 bytes without
grow/shrink churn. Delta tracking is activated only by a typed retained
backend consumer, so Skia now retains only the 24-byte empty-array singleton
instead of 98,352 bytes of unused ProGPU protocol storage.

The same pass canonicalized equal system-font strings in a local collectible
table and removed a second default-family snapshot. This removed 615 live
strings (30,190 bytes) and 6,832 bytes of `FontInfo[]` storage. No global
string intern pool, reflection, GPU upload, or new texture was introduced.
Final rooted heaps were 11,458,433 bytes for ProGPU and 9,108,725 for Skia;
the remaining ProGPU delta is 2,349,708 bytes while it retains a typed
739-node scene and completes 719 incremental updates. Per-frame managed
allocation is lower at 5,386.31 versus 5,955.85 bytes.

Final-binary Xcode Allocations measured 191,141,536 bytes of persistent native
heap plus anonymous VM for ProGPU and 210,844,192 bytes for Skia. ProGPU was
19,702,656 bytes lower overall, with 18,776,064 fewer anonymous-VM bytes,
13,107,200 fewer IOSurface bytes, and 4,538,368 fewer IOAccelerator bytes.
ProGPU's stable explicit Metal allocation was 29,589,504 bytes. Skia does not
surface an equivalent `currentAllocatedSize` value in this trace, so its zero
counter is not a valid GPU-memory comparison. Both processes retained the same
92,274,688-byte virtual dispatch region and reported zero Metal errors,
compiler spills, hangs, or hang risks.

The full pixel matrix, 11 focused protocol tests, exact-package ABI/reflection
checks, and a 30-frame package-only retained-scene smoke all pass. Compact
evidence is in
`artifacts/avalonia-retained-protocol-ownership-20260727` and the paired
`artifacts/instruments/avalonia-retained-protocol-ownership-*20260727`
directories; raw traces, exports, managed dumps, and Xcode scratch were
deleted.

### Final active-residency audit

The final Composition workload keeps one ProGPU custom composition visual
active for the entire measurement. It reports zero fallback nodes, zero
tracked intermediate-texture bytes, a 262,144-byte path atlas, a
262,144-byte monochrome glyph atlas, a 16,384-byte color-glyph atlas, and
9,797,632 bytes from the backend's explicit Metal descriptor ledger. The
same source-built binary completed 600 frames at 119.962 FPS while allocating
6,716.68 managed bytes/frame.

The paired live `vmmap` and native-heap capture gives the following
domain-specific result:

| Domain | ProGPU | Skia | ProGPU difference |
|---|---:|---:|---:|
| Root-filtered managed heap | 10,974,455 B | 8,419,453 B | +2,555,002 B |
| Native allocator payload | 23,159,856 B | 28,184,000 B | -5,024,144 B |
| Active physical footprint | 307,266,712 B | 279,758,024 B | +27,508,688 B |
| IOSurface residency | 6,553,600 B | 39,321,600 B | -32,768,000 B |
| Initial owned-unmapped graphics residency | 141,557,760 B | 133,274,010 B | +8,283,750 B |
| Final owned-unmapped graphics residency | 1,802,240 B | 107,793,613 B | -105,991,373 B |

The initial physical-footprint difference is therefore not evidence of a
texture leak. During the diagnostic hold, ProGPU's footprint fell by
172,595,610 bytes and its owned-unmapped graphics residency fell by
139,755,520 bytes. IOAccelerator residency also fell from 5,292,032 to
2,506,752 bytes. IOSurface stayed constant. This is bounded driver
high-water state being decommitted; no domain grows monotonically.

Both processes reserve the same 92,274,688-byte libdispatch continuation
address range. Only 589,824 bytes were resident for ProGPU in this capture;
the region is not a 92 MiB physical allocation and has no supported size
control. The separate native SwiftUI/Metal control reproduces the same
reservation and shows that private allocator diagnostics can increase
physical memory, so ProGPU does not override libdispatch.

Final Xcode Metal System Trace captures reported zero spills, hangs, hang
risks, or command-buffer errors for both renderers. ProGPU had zero drawable
waits in its five-second rolling window; Skia had 92 waits totaling
715.545 ms. Because attachment occurred after startup, the exported resource
tables contain no creation rows and are not interpreted as zero GPU memory.
The descriptor ledger and VM regions above remain the authoritative resource
accounting. The profiler removed 740,861,595 bytes of raw traces, XML exports,
and scratch after producing the compact summaries in
`artifacts/controlcatalog-final-instruments-20260727`.
