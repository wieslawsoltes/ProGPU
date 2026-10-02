# Session-owned input for owned Cocoa popups

This connects actual `CocoaPopupWindow` providers to the native modal-session
stack. A real non-key panel may receive AppKit modal events, but must not accept
pointer input merely because it has `worksWhenModal`. While a session owns the
thread, only a popup bound to that session's exact Cocoa owner is admitted.
Source enabled intent, mouse transparency, live owner and live input context
remain independent prerequisites. Ordinary Cocoa windows and GLFW input are not
reclassified or routed through this gate.

The creating-thread coordinator weakly indexes actual provider instances. Each
session-stack transition publishes a monotonically increasing revision before
native Begin or after successful End and identity cleanup. Two nested frames for
one owner are still distinct transitions. Out-of-order release cannot remove a
newer frame. Failed Begin restores admission from the actual previous stack,
without restoring stale source policy booleans. New providers, input contexts,
owner bindings and reopened windows consult authoritative state. Completed
callbacks may enter new sessions; older wake snapshots do not overwrite them.

Blocking immediately invalidates queued and already-copied native event tails and
clears managed held-button state. Cancel remains a typed event, never an invented
up or click. Native transitions cannot dispatch source cancellation/disconnection
handlers, including reentrant disposal. Original source wake callbacks run after
the transition, and the existing queue-only owner drain delivers cancellation or
completes retirement. No extra global event poll, timer or GPU operation is added.

Failed End, failed identity cleanup or rejected native input publication latches
a fail-closed thread policy. The stack may no longer report a retained native
window after identity cleanup throws; absence of that query is not successful
release proof. No later registration/context/Show can re-enable an owned popup,
and Begin/poll report the original fault instead of guessing recovery. Existing
source completion callbacks still require real successful End and cleanup.

State work is O(P) at a stack transition for P live registered popups and O(D)
for the existing depth-D release lookup. Weak snapshots allocate only at
transitions/wakes, not each event poll or input record. Native pointer delivery
retains the original bounded batch storage and owner generation.

`CocoaPopupModalInputTests` authors actual backend/provider controls for nested
and same-owner frames, out-of-order and callback-deferred release, rollback,
reentrant new frames/registrations, context/owner/reopen state, native rejection,
uncertain cleanup, copied tails, held-state cancellation and deferred retirement.
The preexisting identity-cleanup failure fixture now isolates its intentionally
terminal thread state, as the failed-End fixture already does.

Implementation-only checkpoint: no tests, builds, native execution, UI/VM work,
CI dispatch or runtime staging were performed. Both source stacks have authored
owned popup factories, typed input and native-session completion paths; final
ordinary provider-qualified source selection and exact final-tip qualification
remain mandatory. This shared gate does not itself select automatic modality,
invent wheel compatibility or claim application parity. No dependency pin,
renderer policy, native ABI or public factory default changes here.

This is original ProGPU coordinator/provider code using existing owned-surface,
native input generation and session lifetime contracts. No foreign implementation
was copied or translated.
