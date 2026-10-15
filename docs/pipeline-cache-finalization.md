# Abandoned pipeline-cache ownership

An abandoned `RenderPipelineCache` previously ran an empty finalizer while the
live device domain kept its counted shader, render-pipeline and compute-pipeline
references. Its containing engine could disappear without explicitly disposing
the cache. The domain retained those native handles until device shutdown, even
after the last live cache sharing the same resources had been disposed.

A nonempty abandoned cache now transfers itself to the existing typed context
retirement queue. Finalization takes no rendering lock and calls no native GPU
API. The original context drain disposes the cache under `RenderLock` before
snapshotting native handle queues, so one drain releases newly unowned handles.
The original device-domain reference counts preserve any still-live cache's
handles. Explicit disposal, source/pipeline keys, cache reuse and native teardown
remain unchanged. Empty caches queue nothing. Context shutdown closes admission
before its final drain; its device domain owns final native release afterward.

This reuses original ProGPU's `WgpuContext.QueuePipelineCacheDisposal`,
`CleanupPendingResources`, `RenderPipelineCache.Dispose` and counted device-domain
ownership at `6a12aa195c8301dc0b192215fdddc3bf2be454fd`.
The [existing ownership research](owned-gpu-finalization.md#provenance-and-applicability)
applies; no foreign implementation was copied. Both managed providers use the
same CLR cache and device-domain owner. The C++ renderer owns its original
submission resources directly and has no corresponding CLR cache/finalizer;
its native lifetime contract is unchanged. Transfer is constant work per cache;
the existing drain is linear in its retained cache entries. No rendering,
shader, source callback, upload, per-frame allocation or performance change is
claimed.

Six actual-device cases cover both stock wgpu-native and Dawn. The original
source fails four abandonment/shared-owner cases and passes two explicit-disposal
and context-shutdown controls. Each uses real shader, render-pipeline and
compute-pipeline handles, with an unchanged five-second collector bound while
the creating thread holds `RenderLock`. Shared-owner checks require the live
cache's actual handles to remain identical and final retirement to queue each
handle exactly once. All original failed logs and TRX remain retained.

The repaired source passes all six cases with zero skips and no test-body changes.
The broader source-built assembly also passes all 197 selected lifetime, buffer,
device-cache, context, chart-series and workflow/source controls, zero skips.

Windows explicitly selects this class; Linux and macOS include it in their
ordinary full managed suites. Full producer CI and downstream package/application
qualification remain required. These controls prove a separate cache leak, not
the cause of the historical Linux collector deadline in #371.
