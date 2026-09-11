# Cocoa modal-session host contract

## Acceptance dependency and admission boundary

The LibreWPF Showcase About dialog needs native suppression of interaction with other
windows while rendering and source dispatcher work continue. Source input filtering
and Cocoa title-bar button state do not provide that contract. This checkpoint
implements the shared native event-session prerequisite, not completed macOS
ShowDialog integration or cross-platform modality.

`NativeWindowModalSession.TryBegin` admits a live, already-visible Cocoa window on
the AppKit main thread. It rejects other platforms, missing/foreign windows and
an AppKit modal session not owned by this coordinator. Visibility is deliberate:
AppKit must not implicitly center a hidden window over the host's chosen placement.
It retains the real window, content view and delegate until native session end.
Each poll checks that the host identity still matches. It does not retain a GPU
device, create a replacement native window, swizzle Cocoa classes or copy GLFW code.

## Event and lifetime ownership

All participating host event polls call `TryPumpEvents` first. False permits the
ordinary platform poll; true forbids a second GLFW/default-mode poll. The native
session uses AppKit `runModalSession:` instead of a blocking `runModalForWindow:`;
dispatcher turns, updates, rendering and bounded idle delays remain host-owned.
WPF's ordinary poll and deferred-activation drain both consume this seam. Both
renderer modes share the same host implementation.

Sessions nest on the creating thread. Begin/end transitions own native dispatch;
reentrant polling of an already-running session does not drain events again, while
a newly nested session may run. Release requested from a native event callback
waits for that poll to return. An outer release also waits for nested sessions.
The matching native End executes once before retained identities are released.
Unexpected native stop/abort responses are reported with their actual code;
they do not silently select the ordinary event loop or a different renderer.

`RetainsWindow` includes begin/end transitions and deferred releases. WPF queues
native host destruction while that lease remains, including externally pumped
hosts, and retries on subsequent deferred-disposal drains. Callers must dispose
every session and continue host cleanup after release; retaining an NSWindow does
not by itself keep GLFW callback data alive. No managed finalizer invokes AppKit.

### Deferred native Hide completion

`TryReleaseWindow(window, onReleased)` requests release of every existing session
for that native identity on the calling thread. An unrelated nested session stays
live; completion waits for it and any active outer poll to unwind. The callback
is attached to the oldest matching session, runs once after End and identity
cleanup, and may run synchronously. False means no session owns the window and
the callback was not retained or called. Callers must recheck current host intent
and any newly entered lease before hiding a window or restoring focus.

Ready callbacks run outside native transitions, with the previous session already
restored. Callback failures do not prevent other ready callbacks or parent release;
errors are propagated, preserving a single exception or aggregating multiple ones.
A native End failure keeps the session and native identities retained and drops
success callbacks. Its completion is uncertain: End might have thrown after the
native call consumed its token. Poll/release attempts therefore rethrow the stored
failure instead of retrying End, using default polling or destroying the host.
This is a terminal coordinator failure, not a generic recovery loop.
Identity-cleanup failure after
successful End is reported without a success callback. `IsReleased` alone is not
a successful-cleanup notification.

LibreWPF Hide now requests this shared release before changing native visibility.
Repeated pending hides coalesce; completion checks disposal and latest Show/Hide
intent and rechecks native leases. A subsequent Show therefore supersedes the
pending hide. Nonmodal and non-Cocoa windows retain their existing immediate path.
This connection is shared by managed portable and native MIL renderers; it changes
neither renderer's scene implementation. It does not yet connect source modal-gate
release/focus restoration or enable automatic Cocoa ShowDialog sessions.

The release lookup is O(D) for D nested sessions; completion is O(C) for C callbacks
on released sessions. Callback storage is allocated only at a release transition,
not in steady event polling. Exceptional error collection is lazy. This is ordered
lifetime control flow, with no data-parallel CPU/GPU kernel or performance claim.
The implementation extends original ProGPU `NativeWindowModalSession` and the
LibreWPF host Hide path. No third-party implementation code is incorporated;
AppKit's [session-end contract](https://developer.apple.com/documentation/appkit/nsapplication/endmodalsession(_:))
is the native completion boundary, while the host callback policy is ProGPU-owned.

Authored additional fixtures cover release after poll/End/identity cleanup, multiple
leases for one window, unrelated nested windows, independent callback failures,
failed End without unsafe retry, failed cleanup without notification, reentrant new
sessions, and unowned identities. The WPF fixture guards release-before-hide and
current-intent checks in the real host source; it is not a native interaction test.
Fixtures are compiled, not executed. Actual Hide/Show/Close/cancellation and focus
ordering in the package Showcase remain final application qualification requirements.

Deferred-Hide checkpoint (2026-09-09): the final ProGPU.Tests graph compiles with
0 warnings/0 errors. Eight additional backend lifecycle fixtures and one WPF
source-contract fixture are authored, not executed. Latest fetched ProGPU main
is included. The separate source gate-release/focus ordering and genuine native
popup admission are still required before automatic Cocoa session activation.

## Required next integration — not enabled automatically

Source gate/focus ordering is now connected through the typed `ReleaseDialog`
callback and ProGPU `ReleaseAfterNative`; see the
[source completion connection](native-mil-dialog-lifetime.md#native-completion-before-source-input-and-focus-restoration).
That is implementation evidence only. The native-popup admission dependency below
still prevents automatic Cocoa session activation, and all runtime qualification
remains open. Earlier checkpoints above record their historical state.

WPF `RunDialog` does **not** automatically create an AppKit session yet. The
following application dependencies must be connected before doing so:

- Separately surfaced dialog popups currently use GLFW NSWindow surfaces. Apple's
  modal escape contract belongs to NSPanel subclasses. Preserve actual popup
  ownership, placement and input; do not silently switch to owner-surface replay,
  bypass the modal poll or change a third-party class globally.
- Source modal entry/exit must own the native session, with release-before-hide
  and source activation/focus restoration ordered after actual native End,
  including an End deferred out of a callback. The existing Win32 publication
  success flag alone cannot prove that an AppKit session ended.
- Preserve nested dialogs, cancellation, Hide/reopen, native pointer/capture and
  multi-monitor startup placement in the actual acceptance applications.

Linux native input admission and Windows package admission remain independent
open requirements. No new partial modality setting is selected by default.

## Provenance, cost and qualification

This is original ProGPU host code. Existing ProGPU `CocoaNativeSystemMenu` supplied
the in-repository pattern for checked native identity, source-generated Objective-C
ABI calls, main-thread admission and scoped autorelease ownership. No foreign
implementation text was copied. Public contracts consulted:

- [AppKit session setup](https://developer.apple.com/documentation/appkit/nsapplication/beginmodalsession(for:)),
  [incremental polling](https://developer.apple.com/documentation/appkit/nsapplication/runmodalsession(_:))
  and [session end](https://developer.apple.com/documentation/appkit/nsapplication/endmodalsession(_:)).
- [Modal-window event admission](https://developer.apple.com/documentation/appkit/nswindow/workswhenmodal).
- [Local-event-monitor limits](https://developer.apple.com/documentation/appkit/nsevent/addlocalmonitorforevents(matching:handler:)).
- [GLFW event processing](https://www.glfw.org/docs/latest/input_guide.html#events).

Adopted incremental native polling and scoped ownership. Rejected local monitors
as a complete replacement (they miss native tracking loops), mouse transparency
as full modality, and unconditional modal admission for existing GLFW popups.
Session state work is O(1) per poll and O(D) for nested release/window-lease queries,
for nesting depth D. Native AppKit event dispatch has its own workload-dependent
cost. Initial window identity admission scans the AppKit window list once. No
managed allocation occurs in a successful steady session poll; AppKit autorelease
scopes remain native-owned. Lifetime/dispatch work is dependency-bound control flow,
not an independent-lane SIMD kernel. No performance improvement is claimed.

Authored lifecycle fixtures cover nested polling, callback/deferred release,
retained host identity, rejected begin, unexpected native responses, transition
reentry and wrong-thread release. They bind the actual backend assembly through
a signed friend contract rather than compiling duplicate backend policy sources.
They have not run. Native AppKit/popups, package applications, VM comparisons,
renderer/lifetime/performance gates and exact-head CI remain final qualifications.

Compile-only checkpoint (2026-09-09): ProGPU.Tests 0 warnings/0 errors, LibreWPF
bridge fixtures 0/0 on the final build (116/0 on the first rebuild), and source
RealPresentationFrameworkHarness 4/0. The first fixture build failed on internal
access; a signed friend declaration fixed it. Six existing linked backend policy
sources now bind the actual assembly instead; no test fixtures were removed.
No tests, verifier scripts, native applications, VM/GPU workloads, benchmarks or
CI polling ran. Latest fetched ProGPU main is included; unrelated native
semantic-state edits and performance artifacts are excluded from this batch.
