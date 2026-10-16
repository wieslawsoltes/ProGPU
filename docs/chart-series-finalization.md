# Abandoned chart-series resource retirement

`GpuSeriesBuffer` owns a data buffer, two uniform buffers and four cached native
bind groups. Dynamic chart paths retain it in the compositor's weak-key cache.
Its original empty finalizer left the four bind-group references unreleased when
the entry disappeared while its context remained live. Independently retained
child buffers also escaped the owner's cleanup; a child finalizer cannot prove
that its owning cache retired correctly.

The finalizer now uses the cache's captured context to queue the four bind groups
and transfers all three buffers through `GpuBuffer.DisposeFromFinalizer`. Neither
operation acquires `RenderLock` or calls a native GPU API. The actual context and
existing submission retirement still decide when native handles are released.
It never substitutes `WgpuContext.Current` when the cache has no captured owner.
Explicit disposal, repeated disposal, buffer growth and migration to another
context keep their existing behavior. Live chart data, uniform contents, cache
identity, draw order and shaders are unchanged.

This is original ProGPU's own `GpuSeriesBuffer.ReleaseBindGroups`, atomic buffer
retirement and context queues at `f35a9a2692c207055a560845a40b4d7c960a073f`.
The existing [owned-resource finalization research](owned-gpu-finalization.md)
applies; no foreign implementation was copied. Both managed providers use the
same chart cache and retirement path. The native C++ renderer has neither this
CLR weak-table entry nor its chart bind-group cache; its submission-owned native
resources retain their existing lifetime. Transfer is constant work per cache,
with no render-path allocation, callback, pixel change or performance claim.

Six actual-device cases use stock wgpu-native and Dawn, with four real bind
groups referring to real owned buffers. The unchanged original source fails both
abandonment cases and passes the four explicit-disposal/migration controls. The
repair passes all six, with zero skips and the original five-second collection
bound while holding `RenderLock`. The child buffers are independently retained,
each native handle must enter exactly one retirement queue, and failed controls
clean up their observed real handles without altering their failed result.
All original failure logs and TRX files are retained.

The full test assembly also passes the existing series-retirement control and
all 54 workflow/source diagnostics. An initial external-output invocation could
not locate repository source files for those 54 diagnostics; its failures are
retained, and only the source-location setup was repaired before rerunning them.
The six device cases and the existing retirement control passed in that initial
invocation without changing their assertions or deadlines.

Windows CI explicitly includes this class; Linux and macOS run it in their full
managed suites. Whole Build and package qualification remain required. This
demonstrates an independent resource leak, not the cause of the historical Linux
collector deadline in [#371](https://github.com/wieslawsoltes/ProGPU/issues/371).
