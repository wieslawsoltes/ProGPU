# Native desktop hit-test completion

## Core application dependency

The LibreWPF MVP and Toolkit/AvalonDock need synchronous source callbacks for
clicking and selection. Their native MIL hosts cannot select native owner queries
by repeatedly polling from managed code, resolving through a mutable owner map,
or silently consulting the managed compositor index. This checkpoint supplies the
shared native completion primitive; host admission and remaining index coverage
are still separate application blockers.

## Contract and implementation

`progpu_native_engine_wait_hit_test` completes an existing request on its engine's
owner thread. It shares argument validation, readback consumption, ordered result
copying, summary semantics and retirement with `poll_hit_test`. The existing
one-pending-request restriction remains unchanged. An empty destination explicitly
discards the list, not its summary. A zero-list query retains the topmost result
in the summary; a list query retains total hit count and traversal diagnostics.

Dawn retains the map operation's future and waits for that future through the
existing WebGPU `InstanceWaitAny` adapter. Waiting only for queue submission does
not establish map completion. As with the existing submission wait API, the Dawn
instance must support timed waits. wgpu-native drives its blocking device poll.
Neither path pumps the application's dispatcher, spins in managed code, adds a
submission or reruns the hit algorithm. Browser builds reject blocking completion
without consuming the token: their existing asynchronous polling path remains
mandatory. A provider unable to finish a wait returns an explicit error and keeps
the pending token; callers must retain it for polling or release the compositor.
Terminal map failures retire the request, matching the existing poll contract.

Dawn polling now observes the callback's acquire/release completion state, not
just `BufferGetMapState`. The latter can report mapped while a callback can still
publish into the reused state. After completion publication the callback only
releases its retained state reference, so a following query can safely reuse the
storage. Teardown keeps the existing reference-counted callback lifetime.

`NativeCompositor.WaitGpuHitTest` consumes caller-owned spans under the existing
render lock and validates compositor identity. `NativeGpuHitTestOwnerSnapshot.Wait`
also rejects tokens with different scene IDs/generations and resolves through the
same immutable map as asynchronous queries. A failed wait must not be converted
to a miss or managed fallback by a host adapter.

## Provenance, complexity and parity

This is an original extension of ProGPU's own
`HitTesting/progpu_native_hit_testing_execution.cpp`, WebGPU synchronization
adapter and `NativeCompositor`/owner-snapshot APIs. No third-party implementation
was copied. The existing shared GPU query algorithm and native/managed wire
records are unchanged. Completion adds O(1) state and one managed/native crossing,
with O(R) bounded ordered result transport (R <= 256). Existing sentinel-driven
record copying is transport, not a numeric CPU fallback; no new CPU geometry
algorithm, pixel readback, repacking or SIMD kernel is introduced.

Both native providers use the same consumer. The managed renderer already has
synchronous GPU owner queries; its geometry algorithm needs no change for this
native scheduling seam. Shared native results are compared against asynchronous
results rather than replacing the shader with a CPU oracle.

## Authored qualification coverage

- Package consumer: polling versus 16 sequential waited requests, exact records
  and diagnostic summaries, owner resolution, consumed-token rejection,
  wrong-generation/compositor rejection, insufficient-capacity retry, explicit
  list discard and zero-list topmost results.
- Dawn provider fixture: polling versus sequential waits and request retirement.
- Native include consumers for both providers: null-engine argument rejection.
- Managed unit fixture: uninitialized owner snapshots reject waits.

These fixtures are authored for final qualification, not executed evidence.
Public changes are additive C functions, not a new C++ module interface or wire
layout. Module/header, GCC/MSVC, browser compilation and exact-head package/runtime
gates remain required. No host query option is enabled by this checkpoint.
