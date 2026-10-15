# Image and wavefront owner finalization

An abandoned WinUI `Image` called ordinary `GpuTexture.Dispose` from its
finalizer. An abandoned `WavefrontVectorEngine` similarly disposed its owned
buffers and background texture. Those calls take `RenderLock`. A rendering
thread holding that lock while waiting for finalizers therefore prevented the
owner from retiring. Explicit wavefront disposal also omitted its pipeline cache.

Independent actual-device controls against original source `96209e2c` reproduce
all six lock-held failures with the unchanged five-second join, on stock
wgpu-native and Dawn. The four Image cases cover file and encoded sources. The
two wavefront cases submit an actual path before collection and independently
retain every child buffer and texture, preventing child-finalizer order from
concealing the owner path. Both explicit-disposal controls also retain the shader
and three compute pipelines. Both Image source-replacement/borrowed-texture
controls pass. Original failed logs and TRX remain retained.

These reproduce concrete defects; they do not establish the missing historical
Linux collector stack in [#371](https://github.com/wieslawsoltes/ProGPU/issues/371).
That original scenario and its deadline remain unchanged.

## Retirement

Image finalization now uses the existing internal texture finalizer transfer.
Source replacement continues to dispose on the calling thread, and a `GpuTexture`
supplied as a borrowed Image source is never owned by this finalizer.

`GpuBuffer` shares one atomic retirement claim between explicit disposal, its
own finalizer and an owning engine's finalizer. Finalizer transfer clears the
wrapper and queues its actual native buffer without acquiring `RenderLock`.
Explicit disposal retains its serialized queue operation. A second owner release
or later child finalizer cannot queue the same handle again. Failed construction
with no native handle has nothing to transfer; an already disposed context
retains its established device-teardown responsibility.

Wavefront finalization uses these existing buffer/texture queues and transfers
its typed pipeline-cache owner to the context. The context disposes those cache
references under `RenderLock` **before** taking its native-handle snapshots.
Consequently one cleanup pass, including shutdown's final pass, also releases the
handles that cache retirement enqueues. Failed and unattempted cache owners remain
available for an explicit drain retry. Shutdown closes finalizer admission under
the same queue lock; the real device domain owns its remaining native handles.
Shared cache reference counts and actual submission ownership remain authoritative.

Explicit engine disposal also retires its cache. No worker, source callback,
reflection, new native crossing or finalizer-side GPU operation is introduced.
The empty drain allocates nothing; transfer/storage are bounded by abandoned
cache owners, and draining is linear in those owners and their cached resources.
No shader, path geometry, raster result, public disposal API or provider ABI changes.

## Provenance and applicability

The authoritative implementation is original ProGPU's `GpuTexture.FinalizeResources`,
`GpuBuffer`, `WgpuContext` retirement queues, `RenderPipelineCache.Dispose` and
`WgpuDeviceResourceDomain` at `96209e2c`. The prior
[retained image finalization research](retained-image-finalization.md) applies.
[.NET finalization](https://learn.microsoft.com/en-us/dotnet/api/system.object.finalize?view=net-10.0)
independently establishes unspecified finalizer order and the consequence of a
blocked finalizer. [WebGPU buffer lifetime](https://gpuweb.github.io/types/interfaces/GPUBuffer.html#destroy)
informs retaining the existing backend destruction/retirement contract. No foreign
implementation text or structure was copied.

Both managed provider contexts execute these owners and have paired controls.
The native C++ renderer uses its original submission-owned
`progpu_native_raster_resources` and `submission_resources`; it has neither the
WinUI Image finalizer nor this managed wavefront owner/CLR lock edge. Its provider
retirement remains unchanged. The original Skia, Direct2D/Win2D, WebRender,
Vello, SkParagraph, Parley and HarfBuzz comparisons in the linked ownership
research retain their scope; none supplies replacement implementation code.
GPU/SIMD work, font and shaping policy, atlas identities, upload demand, source
input, cache generations and device-loss behavior are unchanged.

## Validation

The first repaired source passes all ten original independent controls. The final
local run passes 131 lifetime, buffer, pipeline-cache and context controls, plus
all 13 original headless wavefront checks, with zero skips. The final suite
additionally verifies one-pass context shutdown, exact single handle queues,
borrowed ownership, source replacement and shared cache retirement, alongside
the original image and cache-sampler lifetime cases. Windows CI explicitly includes
the new actual-device controls; the existing Linux and macOS suites also include
them. Whole CI, exact packages and downstream release qualification remain required.
The local result is not historical-failure attribution, an allocation improvement
or a general performance claim.
