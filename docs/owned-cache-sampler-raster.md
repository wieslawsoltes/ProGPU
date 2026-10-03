# Owned raw cache shader samplers

`CacheSamplerRasterFrame` and `Compositor.CaptureCacheSampler` add a shared Scene
producer for a bitmap-cache shader sampler. Ordinary `CachedPicture` realization
and brush painting are unchanged. WPF must supply a previously validated complete
owned picture, original source bounds, selected cache scale, actual primary-display
policy, and the exact live device's texture limits. No receiving-effect size or
brush opacity/transform is an input.

The independently authored frame calculation retains original double metadata,
narrows four bounds endpoints to float, subtracts those endpoints in float, then
multiplies each extent by the selected double scale and promoted primary float
scale. Integer round-out uses the original strict relative binary32 near-integer
contract, not unconditional ceiling. Per-axis device clamping adjusts that axis's
effective primary scale before narrowing the content transform. The actual source
origin is removed once. A zero scale, collapsed realization rectangle or zero
rounded dimension owns one transparent texel, never a null texture or fabricated
positive source bounds. Missing ownership/bounds remains a caller error.

Behavioral provenance is immutable original WPF `381194e`,
`VisualCache.cpp` 270–341 and `real.h` 141/1410–1458, and the dedicated
`ShaderEffect.cpp` cache-sampler routes 419–479/662–735. These are source-contract
observations, not copied implementation. Runtime numerical and full-pixel
qualification remains deferred. Matching authored equations is not a Microsoft
hardware rendering claim.

The Scene producer clones existing ProGPU retained picture ownership, uses the
existing offscreen compositor at DPI1 with the explicit physical transform, and
returns a disposable typed texture owner. It verifies actual device limits and
identity before/after rendering. Texture retirement stays with `GpuTexture` and
the context submission queues. Source identity/revision, retained recording,
frame and device identity belong to one returned generation. This first producer
creates a fresh raster; cross-consumer cache reuse/performance is not claimed.

`WpfShaderEffectSampler.FromCacheRaster` gives each parameter generation an
explicit lease. Recorded pictures (including clones) and compiled frames acquire
their own leases, so replacing a source cache cannot mutate or retire an earlier
consumer's texture. Ordinary sampler constructors retain borrowed ownership.
Abandoned owned parameter holders only transfer their lease to the owning context
retirement queue, never invoke source callbacks on the finalizer thread. A scoped
context registry also retires every surviving raster on the creating thread before
context teardown, without waiting for finalization. It invalidates acquisition
before callbacks, attempts every registered generation, and preserves the first
cleanup failure; failure is not successful retirement. Successfully retired
generations are removed, not retained as an unbounded source history. Registry
entries are resurrection-aware weak references: neither the registry nor a
weak-key source cache globally roots discarded original brushes. An abandoned
raster also only transfers its owned payload to the context drain. Shutdown
rejects reentrant new captures before recording or GPU allocation.
Uncertain failed retirement is the deliberate exception to weak tracking: its
exact owner remains strongly pending, so GC cannot erase a latched source failure
or make a subsequent shutdown appear successful. Wrong-thread rejection retains
the owner for an explicit creating-thread retry; successful retirement removes it.
Finalizer transfer uses a short dedicated queue/shutdown handshake, not RenderLock.
No source callback runs under that handshake; a source callback waiting for
finalizers while it owns RenderLock cannot form a lock cycle with this path.

Managed shader applicability: `WpfShaderEffectExtensionPipeline` generates one
typed texture binding per sampler and samples normalized UVs independently. It
has no equal-sampler/implicit-input-size gate. The three native `WpfBytecode*`
templates and their uniform layout are not managed pipeline inputs; the native
peer separately adds actual sampler extent there. Implicit input ownership and
the managed effect's normalized output quad stay unchanged.

The new pure arithmetic controls cover primary axes, selected scale, independent
limit clamps, near-integer boundaries, endpoint collapse, underflow/empty and
atomic invalid inputs. They are authored only. No tests, build, syntax check,
verifier, GPU, native, VM or CI workload was executed. The final coordinated
producer/source rebuild, full providers/packages and original Windows gates are
still required; no source pin or default changes are made here.

## Authored managed GPU controls

`CacheSamplerRasterRenderTests` adds fourteen cases (seven scenarios on each owned
provider), without executing them in this implementation batch. Positive captures
query the actual context's limits and retain the exact device/source generation.
Three explicitly selected primary-axis/scale combinations produce 16x4, 8x16 and
4x4 cache textures independently of the 16x8 implicit input and 64x32 effect quad.
The real `WpfShaderEffectExtensionPipeline` samples these through register1 using
nearest filtering; independently literal opaque quadrant colors are compared at
every output byte on cold/warm replays, with one actual effect draw per replay.

The other scenarios retain and replay the original texture across same-owner
source replacement and replacement disposal, reject each deliberately incorrect
device-limit axis without damaging an earlier raster, and observe genuine1x1
transparent-black textures for a childless empty recording and a zero-scale
nonempty recording. The transparent control exposes sampled alpha in RGB, so
opaque black or a missing sampler replaced with the yellow implicit input cannot
pass. Caller recordings are disposed before retained-generation replay. These
controls are authored only, not provider or original Windows qualification.

Additional controls retain parameters after owner disposal, reject failed
candidates without damaging earlier pixels, retain cloned recordings after the
parameter/cache/source owners end, and retire two still-leased source generations
on actual owned context shutdown. The shutdown control uses separate contexts,
counts real source-retirement callbacks, and does not force GC or substitute
finalizer execution for explicit lifecycle completion.

## Authored retirement ownership controls

`CacheSamplerRetirementTests` adds ten actual-device configurations across both
providers for direct and parameter-held finalizer transfer, weak source-key
collection, finalizer completion while the render lock is held, and late
finalizers after explicit context shutdown. These retain the existing fourteen
render controls unchanged and use bounded waits; they have not been executed.

Three additional pure controls exercise the same internal retirement coordinator
used by the production raster: exact failed owner/payload retention across GC and
shutdown retries, attempting every owner while preserving the first failure, and
the admission/reentrant/late-queue handshake. Non-GPU participants isolate that
ownership and exception-ordering contract without deliberately stranding a live
device. They do not qualify `CacheSamplerRaster`'s separate source-callback failure
latch or replace actual-device lifetime qualification. No validation was run for
this follow-up.
