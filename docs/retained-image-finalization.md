# Retained image finalization

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
