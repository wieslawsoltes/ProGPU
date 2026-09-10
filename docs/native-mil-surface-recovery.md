# Native MIL desktop surface recovery

## Device recreation integration — 2026-09-08

This checkpoint supersedes the original fail-closed-only LibreWPF device-loss
behavior described below. Automatic host reconstruction is now implemented,
but runtime recovery is not qualified.

The affected acceptance path is the native host harness / package-mode Showcase:
present an initial frame, lose the device domain, then resume the existing visual
tree in the same native window. The host handles this at its composition-target
loading boundary, never inside an active acquired frame or backend loss callback.

- Backend notifications only enqueue host work when that host's exact context is
  lost. Unrelated domains do not trigger reconstruction. Subscription lifetime
  follows the installed target, including failure cleanup.
- Before rebuilding, the host retires its native MIL session/compositor, external
  image leases, DirectX wrapper, managed compositor, retained GPU caches and old
  surface context. Existing shared-device reference counting keeps sibling
  surfaces alive until each releases its own old-domain references.
- Source-built WPF roots, popup bridges, native windows, placement, renderer mode,
  clear color and platform services are preserved. The new session snapshots the
  authoritative WPF state; it does not replay old-device texture handles. Input
  and window-event subscriptions are reattached to the replacement target.
- Native popup options now retain their typed owner host, not a captured context.
  A popup creation/recreation resolves that owner's current initialized target;
  it defers if the owner is still initializing or unwinding a frame. ProGPU's
  `InitializeSharedDevice` explicitly rejects a lost owner before acquiring a
  shared lifetime or accessing the native window.
- `RenderDeviceRecreated` notifies applications on the host thread before the
  replacement's first frame. Consumers must recreate application-owned GPU
  resources there or through context-aware texture lease sources. The existing
  native external-image domain/lease checks remain authoritative: arbitrary
  D3DImage/media producers cannot reuse old resources or obtain fabricated pixels.
- `RenderDeviceRecoveryCount` counts rebuilt target notifications, separately
  from presentation counters. Reconstruction itself does not count as a frame.
  A callback that closes the host stops publication and disposes the new target.
- `WgpuDeviceLostException` distinguishes a lost-domain submission/shared-owner
  failure from other invalid operations while retaining the previous
  `InvalidOperationException` base contract. Native MIL's typed DeviceLost status
  and managed lost-domain submissions unwind the frame before scheduling recovery.
  Allocation, validation and unrelated application errors still propagate.

Window target construction now cleans up its owned context and any completed
compositor when initialization fails. Reconstruction uses the latest window
geometry and a new full WPF snapshot; it does not assume a replacement has the
old format, target dimensions, atlas contents or external image leases.

The original ProGPU sources at `bf9cc40c` are `WgpuContext`'s device-domain loss,
shared-device lifetime and disposal paths and `NativeCompositor.Recreate` /
`progpu_native_device_recovery.cpp`'s separation of immutable CPU state from GPU
handles. The generic native compositor's transactional snapshot-clone API remains
unchanged. LibreWPF instead reconstructs from its retained source-built WPF root
because its transport session and context-aware external images must be renewed
together. This is O(scene resources) work only on recovery; no extra normal-frame
serialization, pixel readback, shader path or CPU fallback is added. This host
ownership work applies to managed and native MIL modes; standalone C++ consumers
continue to own their replacement device and use the existing native recreate API.

Authored gate coverage: `NativeMilHostDeviceRecoverySmoke` injects loss after the
first real host frame, waits for recovery, and checks a distinct live context,
old-context disposal, preserved root/window identity and a submitted native draw.
`eng/progpu-wpf-native-mil-host-smoke.sh` includes this case on every supported
platform through `--native-mil-device-recovery`. The injection marks an actual
context's domain lost; it is not a hardware-reset simulation or image comparison.
Paired host scheduling fixtures and a backend shared-owner rejection fixture are
also authored. No tests or runtime gates have been executed in this batch.

Compilation checkpoint: Release builds of `ProGPU.Tests`, `ProGPU.Wpf.Tests`
and `ProGPU.Wpf.RealPresentationFrameworkHarness` succeeded with zero errors
and 0, 116 and 4 warnings respectively. The harness build includes the source-built
PresentationCore/PresentationFramework dependencies and the new recovery case;
it is not evidence that the case has run or passed. Source verifiers, VM/GPU
workloads, image comparisons and CI qualification remain deferred.

Final qualification must still cover loss during encoding/submission, visible and
hidden popup siblings, application-owned external-image recreation, callback
reentrancy/close, failed replacement creation, repeated hardware loss, output
quality and resource counts. Creation failure remains explicit; this change does
not promise an indefinitely retrying device-creation loop.

## Core application blocker — 2026-09-08

The LibreWPF native host harness and package-mode Showcase use
`ProGpuWpfWindowHost` for first presentation, updates and resize. Source inspection
found that both native MIL and managed portable presentation returned false after
every failed surface acquisition, without invalidating configuration or preserving
the consumed presentation request. With an event-driven host, a temporary failure
could therefore leave the last frame visible until unrelated input. This is a
source-backed failure path, not a reproduced runtime result.

The bounded implementation outcome is recovery from transient acquisition failure
in both host modes without changing scene compilation, drawing or viewport policy.

## Shared ProGPU policy

`WgpuContext.HandleSurfaceAcquisitionFailure` owns the backend decision. Call it
only for a failed acquisition, after releasing any returned texture:

| Status | Backend action | Caller action |
| --- | --- | --- |
| Timeout | Keep configuration; return retry if the device is live. | Schedule another presentation attempt. |
| Outdated / Lost | Invalidate cached surface capabilities and configuration; return retry if the device is live. | Reconfigure at the next normal render tick using the latest physical size. |
| DeviceLost | Report loss to the exact existing device domain; return no retry. | Stop using that domain. Recreating the device and dependent resources is a separate operation. |
| OutOfMemory | Throw `OutOfMemoryException`. | Propagate the terminal failure, without a retry loop. |
| Success / unknown | Reject misuse or an unrecognized status. | Do not silently treat contract errors as recoverable. |

The method does not acquire a texture, submit GPU work, configure a surface,
recreate a device, select a fallback renderer or schedule a host callback.
The operation is fixed O(1) control flow with no buffer, pixel or independent-lane
work; GPU/SIMD execution policies are not applicable to this state transition.

`GpuTextureSurfacePresenter` now consumes the same policy, preserving its existing
caller-owned scheduling and terminal device-loss return behavior. Its failed
texture reference is released before recovery notification, with the pointer
cleared so its final cleanup cannot release it twice.

## LibreWPF integration and ownership

Both managed portable and native MIL host acquisition paths release failed
textures, call the shared policy, and schedule a presentation-bearing request
for recoverable failures. That request upgrades a pending wake-only MediaContext
tick, even when retained scene state is unchanged. Delayed schedulers receive a
16 ms retry request; compatibility schedulers retain their own cadence. There is
no blocking sleep or recursive acquisition. Successful frame accounting remains
after `SurfacePresent`, not after scheduling or failed acquisition.

Configuration failures also preserve a presentation request. Already-lost device
domains fail explicitly before reconfiguration; a loss reported by acquisition
also fails explicitly instead of repeatedly scheduling the dead device. Automatic
device recreation is **not implemented by this change** and remains a core
lifetime requirement. A successful acquisition without a texture is a contract
error. A null texture view schedules a replacement frame and never enters either
renderer; the managed host previously lacked this null-view guard.

## Provenance and applicability

The authoritative original ProGPU sources at `d993c88a` are
`src/ProGPU.Backend/GpuTextureSurfacePresenter.cs:Present` and
`src/ProGPU.Backend/WgpuContext.cs:InvalidateSurfaceConfiguration` /
`ReportDeviceLost`. This change shares their existing recovery semantics and
adds explicit rejection of unknown/success status misuse. No external engine
implementation is copied, and no rendering/shader algorithm is changed.

Managed and C++ MIL rendering both receive acquired targets from the same
LibreWPF-managed `WgpuContext`; their paired host paths are updated together.
The C++ semantic renderer consumes an external target view and does not own this
host surface's acquisition/configuration, so it needs no second recovery policy.
Existing Avalonia and WinUI status handling remains in its host-specific paths;
this change does not claim their device-recreation or scheduling behavior is
qualified or equivalent. Other hosts can reuse the backend policy without using
LibreWPF scheduling or scene types.

## Authored coverage and remaining qualification

`WgpuContextTests` covers retry decisions, terminal device-domain loss, independent
context isolation, and rejection of memory/contract failures.
`ProGpuWpfWindowHostTests` supplies matched managed/native-mode scheduling cases
for Timeout, Outdated and Lost, wake-only upgrade, unchanged presentation counters,
terminal errors and disposal. These are authored regressions, not executed results.

Compilation checkpoint: Release builds of `ProGPU.Tests` and LibreWPF's
`ProGPU.Wpf.Tests` succeeded with zero errors (zero and 116 warnings respectively).
No test, runtime workload, source verifier, VM validation or CI qualification was
run in this implementation batch.

At final qualification, inject failures around first frame and resize in both
renderer modes. Check that failed textures/views are released exactly once,
Outdated/Lost forces same-size reconfiguration, a transient failure does not need
unrelated input to resume, no failed attempt increments presentation counters,
and closing a window cancels pending work. Run the full application flows on
macOS/Linux and Windows Parallels, then required PR CI at the exact delivery head.
Compilation alone does not establish these runtime outcomes.
