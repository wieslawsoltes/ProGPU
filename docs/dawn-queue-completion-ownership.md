# Dawn queue completion ownership

The managed Dawn queue wait used to free callback userdata in `finally`, including
when WaitAny failed before delivering the callback. It now owns managed and native
uses separately. A returning or failing wait ends only managed use; the actual
completion/cancellation callback ends native use. Either order, including a native
callback during registration, retires the GCHandle exactly once. An import that
fails before registration cancels only its unqueued native use.

Queue callbacks use AllowSpontaneous and publish only private status/message state.
They do not call application handlers, log, submit work, release GPU resources or
invoke native APIs. Message-decoding failures are retained as errors; no managed
exception crosses the callback ABI. Native progress or shutdown can therefore
retire an abandoned notification without another wait on its future. The normal
WaitAny timeout and the explicit successful queue-status requirement are unchanged.
Pending, error and canceled notifications never become successful GPU work.

This follows the pinned original Dawn implementation:

- [Queue.cpp at the packaged revision](https://dawn.googlesource.com/dawn/+/01249a97332468dbdd6cf5edb8dd7bae77875de5/src/dawn/native/Queue.cpp): WorkDoneEvent owns the callback metadata, reports CallbackCancelled on shutdown, and can complete during registration.
- [EventManager.cpp at the same revision](https://dawn.googlesource.com/dawn/+/01249a97332468dbdd6cf5edb8dd7bae77875de5/src/dawn/native/EventManager.cpp): AllowSpontaneous callbacks execute for ready events and remain compatible with WaitAny.

## Applicability

Both renderer modes use the managed Dawn context's queue wait when its lifetime
is polled. The C++ renderer's independent submission future in
`Backend/progpu_webgpu_compat.hpp` uses an empty `queue_work_done` callback with
null userdata. It has no callback allocation to prematurely free and needs no
paired ownership change for this defect. Its future polling, map ownership and
submission semantics are not changed or newly qualified by this work.

## Evidence and remaining gates

The focused device-free tests cover synchronous and late callback order, concurrent
managed/native retirement, native error/cancellation, decoder failure and failed
imports. All 38 linked Dawn ownership/diagnostic contract cases pass. These use the
actual managed state owner, not fabricated GPU handles or an inferred GPU counter.

The isolated `--queue-abandonment` Windows control registers a real notification,
abandons its managed wait and requires one successful native callback and one
GCHandle retirement after a real queue wait. Native completion can be synchronous;
the control does not claim to force a pending hardware submission. Deterministic
late-callback order is tested separately. Existing full-pixel/readback and loss
controls remain independent, with unchanged child deadlines.

Both Windows architectures and JIT/NativeAOT source and NuGet consumers include
the new control. Hosted execution and complete exact-head Build remain pending;
neither source inspection nor device-free tests qualify package, presentation,
application performance, or all platform teardown behavior.
