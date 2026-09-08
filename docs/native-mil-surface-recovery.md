# Native MIL desktop surface recovery

## Core application blocker — 2026-09-08

The LibreWPF native host harness and package-mode MVP use
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
