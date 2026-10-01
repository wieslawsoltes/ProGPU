# Dawn startup request ownership

Ordinary Metal, native-presentation and offscreen adapter requests previously
freed their callback GCHandle in `finally`, even when WaitAny failed before the
callback. The shared device request also freed device-loss userdata immediately
on failure. Returning from a wait is not evidence that native callback ownership
has ended, as the independently executed WARP abandonment control demonstrated.

All adapter routes now use one owned request, preserving their exact backend,
power preference, compatible surface and explicit fallback choice. Device requests
reuse the same owner already exercised by WARP. Normal waits and default policies
are unchanged. A startup failure drains the real completed request with one
zero-timeout WaitAny, then releases any unpublished result and its userdata.
If the drain fails, pending callback state remains retained; device-loss userdata
transfers with it rather than being freed by outer factory cleanup. Original
failure diagnostics stay on the offscreen failure path and do not load a provider
or retry adapter selection.

The isolated `--adapter-abandonment` consumer exercises the ordinary typed Dawn
RequestAdapter entry point, not the LUID companion request. It requires one actual
native completion, one unpublished adapter release and one userdata retirement.
Both Windows RIDs run the same JIT and NativeAOT source/NuGet controls. The existing
device-abandonment control now traverses the shared device-request entry point.
These additions await hosted execution; prior WARP results do not qualify this
follow-up or all ordinary backend/platform choices.

This change does not claim to complete ordinary factory ownership transfer after
device creation, queue-work callback retirement, native presentation teardown or
application qualification. Those retain separate requirements. It introduces no
public API or automatic WARP selection, and never treats an incomplete callback
as a successful request or release.

## Renderer applicability and validation

Both managed rendering and the native renderer's Dawn host consume the device
created by this managed factory. The C++ engine accepts and retains an existing
device/queue through `progpu_native_engine_options` in `progpu_native.h`; it does
not issue these adapter/device requests or own their managed callback handles.
Its engine resource and submission retirement contracts are unchanged. There is
therefore no duplicate C++ request implementation to patch for this defect.

Local checks built the backend and the conformance consumer with zero warnings
and errors. An isolated test project linked the unchanged bodies of
`DawnStartupRequestOwnershipTests`, `DawnSystemWarpContractTests` and
`DawnAdapterRequestFailureDiagnosticsTests`: all 25 device-free cases passed.
PowerShell parsing and `git diff --check` also passed. These checks do not execute
native requests; complete exact-head Build and Windows source/package controls
remain required before merge.
