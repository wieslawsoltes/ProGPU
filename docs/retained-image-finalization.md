# Retained image finalization

## Portable context cache and raster ownership, 2026-10-28

The optional `SKImage` context cache had a separate ownership gap. Direct image
finalization skipped `DisposeManaged`, leaving an independently retained cached
texture alive. A finalizing image shader instead called its retained image's
ordinary `Dispose`, whose cache cleanup took the cached texture's `RenderLock`.
The earlier primary `TextureStorage` correction did not cover either path.

Ten actual-device controls against unchanged `SKImage.cs` at `98acaf8170`
(source SHA-256
`bb3bdec4f6fd6df3dd4faf58b8c763d4ce11cce01244ee2386e7851cd11f78c9`)
produced six failures and four explicit-disposal passes. On each of stock
wgpu-native and Dawn, a directly abandoned image retained its cache texture;
an image shader and nested matrix shader missed the original five-second
lock-held join and completed after unlocking. The tests retain the actual cache
texture and, for nested disposal, the actual retained image wrapper. They do not
rely on finalizer ordering to exercise the owner being checked.

Cache release now covers both image disposal paths, preserving the original
explicit `DisposeNative`/`DisposeManaged` hook order. Each entry uses its
own actual context through `GpuTexture.DisposeFromFinalizer` during direct or
nested finalization; ordinary disposal still calls `Dispose`. Shared primary
storage counts, borrowed textures, context-cache keys, pixel bytes and source
callbacks remain unchanged. Selection is O(1) per entry and cleanup is O(C) for
C materialized contexts, matching existing explicit cleanup. There is no new
worker, readback, allocation threshold, collection mode or timeout.

This uses the original `SKNativeObject` finalizer-context scope and
`GpuTexture` atomic queue-only retirement. Both managed providers share the
same optional image cache and have paired controls. The C++ renderer's owned
resource batches in `progpu_native_submission_resources.hpp` retire against
their recorded completion token; they do not own this cross-`WgpuContext`
`SKImage` cache or invoke CLR finalization. Its submission and native raster
resource contracts are unaffected. The independent .NET finalization contract
linked below was checked again; no external implementation was copied.

The same `DisposeManaged` boundary also excluded direct finalization from
freeing the image's raster `GCHandle` and invoking its original raster release
callback. A separate ten-case original control set produced six failures and
four explicit-disposal passes across both providers. Test-only reflection
observes the actual private pin owner rather than inferring a leak from a
process-wide pin count. Live borrowed pixmaps correctly keep their image and
pointer alive; after pixmap disposal, the abandoned image previously left its
pin allocated. Source callbacks were also missing after image finalization.
The failing pin controls release their observed handle after assertions, avoiding
a leak from the diagnostic itself. The initial six-case inventory, complete
ten-case original run and all failures are preserved separately.

Raster cleanup now runs at the common disposal boundary, releases the actual
pin and clears its pixel array, then invokes the original callback once outside
the raster lock. Its pointer/context are unchanged. Exceptions from callbacks
are contained only during actual direct or nested finalization; explicit
disposal still propagates them. This is O(1) CPU ownership work. The CLR pin and
callback belong to this managed image wrapper, with no counterpart in the
native submission-retention implementation.

The final combined run passes all 38 image-finalizer cases, 13 original
cache-sampler cases and 47 selected existing image/API/canvas ownership cases
on macOS ARM64: 98 passes, zero skips. It includes four exception-context
controls and preserves the original five-second bounds. Original failed and
successful logs/TRX, source snapshots and hashes are retained in the task's
`2026/10/28/progpu-portable-image-371` evidence directory. Hosted whole-Build,
package and application qualification remain required. These additional
ownership defects do not attribute the original Linux collector miss in #371;
that still requires its own stack/timing evidence.

## Surface-owned resources, 2026-10-28

The bounded adjacent surface audit demonstrated another skipped
`DisposeManaged` ownership boundary. Against unchanged `SKSurface.cs` SHA-256
`4bd7e1f1596af93334edb097355b533c97de8f4ad9b9698f8048f20feb38c964`,
20 paired stock/Dawn controls produced ten failures and ten passes. Abandoned
surfaces left their actual owned texture alive, retained an owned HGlobal, kept
a 512-byte readback buffer allocated, and omitted original release callbacks.
Explicit disposal, borrowed textures and retained snapshot pixels already
passed. Negative controls retain actual child owners; they release observed
HGlobals/readback buffers after assertions rather than leaking test artifacts.

Direct finalization now retires the actual owned texture and readback buffer
through existing queue-only paths, releases the surface's counted immutable
generation and frees its own CPU storage. It does not flush pending drawing,
dispose the canvas, poll, unmap or enter RenderLock. Command unsubscription and
canvas detachment only remove managed references. The original explicit
`DisposeManaged` flush and caller-thread teardown order remain intact. Borrowed
textures and CPU pointers remain caller-owned, and a live snapshot retains its
own generation lease. Original release callbacks run once; only actual
finalizer callback exceptions are contained. This reuses original surface,
image-generation and backend retirement code, with O(1) owner work and no new
worker, resource registry, rendering algorithm or native ABI.

Review additionally found that a finalizing surface's short weak registry entry
was already cleared before `Unregister`. Two actual surface controls failed
while the live replacement-owner control passed. Unregistration now removes a
dead entry just as `TryPresent` already did, preserving every live replacement
at the same framebuffer address. The [documented weak-reference lifetime](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/weak-references)
was consulted; no external implementation was copied.

The final focused run passes all 179 selected image, surface, cache-retirement
and compatibility controls with zero skips, including all 23 surface controls.
The original five-second collector bound remains unchanged.

Both managed providers share these owners and their paired controls. The native
C++ submission-retention implementation neither owns this `SKSurface` HGlobal/
callback/weak-registration state nor participates in CLR finalization; its GPU
submission/resource-completion contract is unchanged. Original failures, final
results, source hashes and control inventories are retained in the task's
`2026/10/28/progpu-surface-finalizer-371` evidence directory. This fixes the
demonstrated owner defects; it does not attribute the historical Linux deadline
miss or replace hosted, package or application qualification.

## Observed lock cycle

An abandoned `SKImage` releases its last counted `TextureStorage` reference from
`SKNativeObject`'s finalizer. Previously that path called ordinary
`GpuTexture.Dispose`, which takes `RenderLock`. A creating thread holding that
lock while waiting for finalizers therefore prevented the image from retiring.
Nested image shaders can reach the same path through synchronous `Dispose` calls.

An independent probe against `c254ada12050e934ec7954a8a6d27a0fb76df07b`
retained the actual GPU texture, abandoned its owning image, and collected while
the creating thread held the render lock. It missed the original five-second
join and completed after the lock was released. Its explicit-disposal control
passed. With the fix, the unchanged abandoned-image probe completed in 8.25 ms
while the lock remained held. Retaining the texture prevents its own finalizer
from running first and hiding the image-owner path; no finalizer order is assumed.

This proves a concrete lifetime defect, not the cause of the historical Linux
failure in [#371](https://github.com/wieslawsoltes/ProGPU/issues/371).
The subsequent unchanged complete Linux diagnostic passed 6,080 renderer cases
with nine existing skips, 230 headless cases and all 13 original retirement
controls. Its stack observer was positively verified, but no original collector
stayed alive long enough to capture a blocked stack. That result cannot explain
or erase the historical failure.

## Ownership and retirement

The existing `SKNativeObject` finalizer carries an internal thread-local context
through its synchronous disposal chain and restores it in `finally`. This is
actual finalizer entry, not a thread-name guess or a mutable application option.
Only the last counted image-storage release chooses the queue-only backend path.
Live leases keep their original count, and borrowed textures remain borrowed.
Explicit disposal retains its original notification and release behavior.

`GpuTexture.DisposeFromFinalizer` reuses the original atomic
`GpuTexture.FinalizeResources` implementation. Texture/view handles and external
owners go to the existing context retirement queues; device identity,
notifications, submission completion and disposed-context behavior remain intact.
The internal method is available to the exact strong-named SkiaSharp assembly;
no public API or native ABI is added.

Image readback storage also queues its buffer without invoking a lock-taking
native unmap on the finalizer thread. The original context drain calls
`BufferDestroy` and `BufferRelease`, ending any mapping. Normal readback unmapping
and explicit disposal remain unchanged. This does not add a new readback,
submission, finalizable owner, resource registry or worker.

Selection is O(1) per finalizer/last storage release. Retirement retains the
existing queue and disposal complexity; no pixel, shader, scene or cache-key
algorithm changes. This is a deadlock correction, not a throughput, allocation
or GPU residency optimization. No Instruments or general startup speedup is
claimed from the bounded join measurements.

## Applicability and validation

Both managed provider contexts, stock wgpu-native and Dawn, execute the affected
SkiaSharp ownership chain and have paired actual-device controls. The native C++
renderer instead retains `progpu_native_raster_resources` through
`submission_resources` and releases native handles after the recorded submission.
Its source is `src/ProGPU.Native/src/Backend/progpu_native_webgpu_resources.hpp`
and `progpu_native_submission_resources.hpp`; it has no `SKNativeObject` finalizer
or managed render-lock/GC wait edge to replace. Those leases and both native
provider implementations remain unchanged.

`SkImageFinalizerRetirementTests` covers direct images, rendered/readback images,
image shaders and nested matrix shaders, independently retained GPU textures,
explicit notification ownership, live leases and borrowed textures on both
managed providers. Original cache-sampler controls and their five-second bound
are unchanged. On macOS ARM64, the 14 new cases and 54 existing image/retirement
controls passed; another 181 existing canvas, shader, ownership and device-loss
cases passed, all with zero skips. Whole Build, Svg.Skia pixel parity, downstream producer pins and
application qualification remain separate integration gates.

## Contract research

The implementation reuses original ProGPU image counts, finalizer entry and
backend retirement. No external implementation text or structure was copied.

- [.NET finalization](https://learn.microsoft.com/en-us/dotnet/api/system.object.finalize?view=net-10.0)
  supplies the independent contract: referencing objects have no guaranteed
  finalization order, and a blocked finalizer can prevent other finalizers from
  completing. The regression deliberately avoids relying on that order.
- [Skia reference ownership](https://api.skia.org/classSkRefCntBase.html)
  distinguishes shared references from final ownership release. Preserve the
  existing ProGPU counts; do not replace them with reachability guesses.
- [WebGPU buffer destruction](https://gpuweb.github.io/types/interfaces/GPUBuffer.html#destroy)
  and the [buffer lifetime design](https://github.com/gpuweb/gpuweb/blob/main/design/BufferOperations.md)
  support deferring mapped-buffer destruction to the owning context drain.
- [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
  and [Win2D device loss](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/handling-device-lost)
  remain references for device-scoped ownership. Preserve the real context and
  original loss behavior, rather than selecting an ambient live device.
- [WebRender](https://github.com/servo/webrender) and
  [Vello](https://github.com/linebender/vello) are retained-rendering comparisons;
  their batching or ownership architectures are not imported for this CLR edge.
- [SkParagraph's public layout contract](https://github.com/google/skia/blob/main/modules/skparagraph/include/Paragraph.h),
  [DirectWrite layout](https://learn.microsoft.com/en-us/windows/win32/directwrite/text-formatting-and-layout),
  [Parley](https://github.com/linebender/parley) and
  [HarfBuzz clusters](https://harfbuzz.github.io/working-with-harfbuzz-clusters.html)
  remain independent text contracts. Shaping/layout reuse, fallback and variable
  fonts, original clusters, DPI and hinting are unchanged.

Startup/lazy initialization, retained display lists, visibility, glyph/texture/path
keys and eviction, demand upload, worker preparation, GPU batching and atlas
generations retain their existing policies. None needs a new implementation to
remove this demonstrated lock cycle.
