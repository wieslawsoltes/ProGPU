# Native index metadata and host routing

This extends the [desktop completion contract](native-mil-hit-test-completion.md)
for LibreWPF's existing Showcase/Toolkit point and region callbacks. Geometry execution
stays in ProGPU's C++/shared GPU query path; the WPF adapter only handles source
owners and neutral candidate DTOs.

## Shared ProGPU contracts

`NativeGpuHitTestOwnerSnapshot.GetIndexInfo()` queries its exact installed scene
generation. The native `get_hit_test_index` function reuses the engine's validated
resource reader and the existing canonical index record, returning index presence,
primitive/node/index/path-segment counts and actual GPU residency. Residency
requires the retained bind group and matching native hit-index hash. Reading
metadata does not create a pipeline, allocate/upload an index or submit work.
It is owner-thread affine, O(resources) metadata inspection, O(1) workspace and
one boundary call only when requested. WPF does not parse a duplicate scene index.

The snapshot's `CopyOwners` validates compositor/scene token identity before its
immutable map performs O(results) dictionary lookups into the caller's output
span. Unknown IDs and no-hit records consume no output slot. Native order,
repeated IDs and destination capacity are preserved. This is source identity
transport, not CPU hit geometry or an independent SIMD kernel.

Both native providers export the same metadata function. Managed renderer input
already exposes its own index diagnostics and ordered owner mapping; it remains
unchanged. No new wire layout or shader is introduced. The implementation uses
only original ProGPU resource inspection and owner-map code.

## LibreWPF integration

The host's point, all-owner, bounds-owner, bounds-candidate and ellipse-candidate
callbacks select by renderer before touching a managed index. Native mode uses
the last successfully presented native owner snapshot, shared Begin/Wait queries
and original intersection details. It retains source refresh ordering and the
managed adapter's bounded expansion when unresolved IDs fill a result window.
Normal point queries use stack storage; larger result windows rent at most 256
records. Region candidates remain typed `PortableGeometryHitTestCandidate` objects.

Pending readback tokens retain their original owner snapshot when completion
fails. Another query fails explicitly until the owning compositor is disposed;
there is no generic recovery loop, silent miss or managed fallback. Reset follows
native engine disposal. Normal success releases temporary owner references.

`EnableNativeMilHitTesting` is a frozen startup choice. It sets the existing
complete-index compilation flag and is inherited by separately surfaced popups
and source-window activation. It remains explicitly opt-in while core application
coverage is unfinished. Native callbacks without admission throw an explicit
unsupported error; they no longer borrow the managed index. SDK apps can opt in
with `ProGpuWpfNativeMilHitTesting=true` alongside `NativeMilWgpu`.

The native SDK qualification lane and existing real source-built host harness
now require this option. The harness checks source owner results and native
index residency, not just a draw. All execution remains deferred until freeze;
an authored gate is not evidence of successful application input.

## Qualification additions and remaining scope

ProGPU fixtures cover metadata before/after upload, stale snapshot rejection,
bounded ordered owner copies, repeated owners and invalid C arguments on both
providers. WPF fixtures cover all host query families with an absent managed
index, unresolved top-owner expansion, native diagnostics, disposal and explicit
admission. Actual source-built drawing/input checks extend the existing harness.

These fixtures must run against refreshed exact-head native payloads. Older
package feeds do not export the new completion/metadata functions. Complete
application hit coverage (required clip/cache/stroke combinations), default
admission, all platform/compiler/module/browser gates and runtime/image/lifetime/
performance qualification are still required. No parity or speed claim follows
from code compilation alone.
