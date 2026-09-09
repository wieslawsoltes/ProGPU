# Source-controlled portable dialog lifetime

The X11 dialog path now has an owned advisory EWMH modal-hint connection and
failure-driven source hiding. See the [X11 modal-hint contract](native-mil-x11-modal-hint.md).
This does not close Linux native input suppression or the qualification gaps below.

## Native completion before source input and focus restoration

Acceptance action: Hide or Close the LibreWPF MVP About dialog from a native event
callback, including a nested dialog. Source inspection found that its `using`
scopes restored input/focus when managed ShowDialog unwound, even if AppKit End
was deferred until the outer native event callback returned. This was a source
lifetime blocker, not an observed application failure or new rendering algorithm.

The typed activation contract now includes `ReleaseDialog(activation, completed)`.
The source captures it with RunDialog before Show and rejects a missing callback
before creating a host. A host without a native modal session must implement
explicit synchronous completion. A host with native modality invokes completion
only after native End and identity cleanup, on its owning thread. Release failure
must propagate without pretending that native ownership ended.

The shared `PortableModalInputScope.ReleaseAfterNative` transfers source cleanup
to that completion. It keeps the source gate active while native completion is
pending and drains ready source scopes in reverse entry order, even if an outer
native completion arrives first. Ordinary strict Dispose remains available, but
cannot bypass pending native completion. A normally disposed child also drains a
ready parent. Source cleanup runs once after gate publication; it still runs if
publication throws, so its existing `IsNativeInputPolicySynchronized` check can
reject unsafe focus restoration while clearing owned references. Other ready
scopes are not stranded by an independent cleanup failure. Failures propagate.

WPF captures the actual activation before accepted Close clears it. Both accepted
Hide/Close and the ShowDialog finally block request the same single transfer;
neither directly disposes the input/focus scopes early. The source-owned cleanup
clears only its matching dialog generation and retains the original active-window,
PresentationSource and focused-element checks. Another ShowDialog on that Window
is rejected until the earlier release completes. Cancellation retains modality.
If Show fails before activation exists, no host modality was admitted and source
cleanup completes directly. The host supports release even after activation
disposal because its native window may still have an active retained lease.

The original ProGPU `PortableModalInputScope` and `NativeWindowModalSession`, and
LibreWPF's existing source Window/activation/focus snapshot supplied the code and
ownership model. This is a typed control-flow extension, not copied foreign code.
Native session completion remains the boundary described in the
[Cocoa session contract](native-mil-cocoa-modal-session.md). Both native MIL and
managed portable renderers use this same host/source path; no renderer/shader,
GPU queue, native wire record or CPU fallback algorithm changes here.

Cost: O(D) source stack work to release D ready scopes, plus existing O(W) native
gate publication per scope for W registered surfaces. Normal input tests are
unchanged and allocation-free. Cleanup delegates/ownership allocate only at dialog
entry/release, not per event/frame. Error collection is lazy; lifetime and focus
work are dependency-bound, not SIMD kernels. No speed or runtime-parity claim.

Authored fixtures include real coordinator-to-input-scope sequencing, delayed and
out-of-order native completion, ordinary child disposal, cleanup failure, wrong-
thread completion, terminal request failure, and source Hide/Close with actual
focus snapshots. Existing canceled-result/reopen/missing-capability fixtures are
extended, and host registration/late release are covered. They are compiled, not
executed. Genuine Cocoa native popup admission still blocks automatic AppKit
session activation; Linux modality, package payloads and Windows SDK admission
remain separate. Actual source/host interaction, images, lifetime, VM comparisons
and CI must still qualify after implementation freeze.

Compile-only source-completion checkpoint (2026-09-09): final ProGPU.Tests 0
warnings/0 errors, WPF bridge fixtures 116/0, source PresentationFramework fixtures
6/0, and RealPresentationFrameworkHarness 0/0. Nine new ProGPU lifecycle/contract
fixtures plus host/source coverage are authored. No fixtures, verification scripts,
native applications, VM/GPU workloads, benchmarks or CI checks ran. Latest fetched
main is included; unrelated native semantic-state edits and performance artifacts
are excluded. These builds do not admit package mode or qualify native modality.

## Win32 input gates and source focus restoration

Acceptance path: open/close/hide/reopen the MVP About dialog, including an owned
nested dialog and popups. `PortableModalInputScope.RegisterWindow` binds each
native surface to its source Window identity. The host owns the registration;
the thread index holds weak references so registration does not root abandoned
window graphs. Newly registered surfaces receive current permission immediately.
Scope boundaries snapshot registrations, permit callback-driven surface creation
or disposal, and notify all survivors even after one callback fails. Failed entry
restores the previous scope and republishes it. Failed exit releases the scope
but exposes `IsNativeInputPolicySynchronized == false`, preventing speculative
focus restoration. Failures propagate; no generic renderer recovery is added.
Recursive scope mutation during native publication is explicitly rejected.

The shared Silk controller has an independent input gate. Effective Win32 enabled
state is application-enabled intent AND input admission. Application SetEnabled
and controller Reapply retain this composition; release uses the latest intent,
not a saved IsEnabled assignment. Synchronous callback changes are reconciled,
with explicit failure after eight oscillating attempts. Unsupported platforms
cannot report accepted input gating. The existing checked Win32 enable adapter
remains authoritative for actual native state. Both renderers share this code.

LibreWPF registers root activations and native popups under their actual source
owner identity. Registered Win32 windows initialize hidden and apply the gate
before native Show; inactive windows created inside a dialog stay blocked.
Rendering/geometry/lifetime work continues. Accepted source Hide/Close releases
the gate before native hide/destruction. If an outer dialog closes with an owned
nested dialog still active, source disposal retries after owned windows close.
Canceled closing retains the gate. Normal scope disposal remains idempotent.
Gate-release failures still unwind accepted source-close/host disposal; they do
not strand an already-disposed source with a live native host. The failure remains
reported, and uncertain predecessor gates prevent speculative focus restoration.

Source capture precedes native disabling and records the actually active Window,
original PresentationSource and focused element, not an arbitrary Owner fallback.
After the prior policy is successfully restored, the source requests activation
through the typed host, then restores an element only if it remains in its original
live source. Hidden, disabled, disposed, moved and no-longer-admitted targets do
not regain focus. IsActive is never fabricated; host activation rejection is a
normal unsuccessful request. Capture is not restored as a stale mouse grab.

This is implemented Win32/source integration, **not runtime qualification or
full cross-platform/application-wide modality**. Cocoa and Linux native input
suppression, other UI threads, unrelated native/custom-host windows, native
startup placement and actual focus/close/keyboard/pointer behavior remain explicit
requirements. Registered-policy synchronization does not prove OS qualification.
Windows SDK admission stays guarded. Source/package application gates and exact-head
CI remain mandatory after implementation freeze.

Provenance: original ProGPU PortableModalInputScope, SilkWindowController and
Win32WindowEnabledState; original source-integrated LibreWPF host/dialog/input
seams. No external implementation was copied. Public contracts consulted:
[Win32 enabled input and pre-destruction ordering](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enablewindow)
and [WPF previous-active-window semantics](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.showdialog).
Adopted distinct enabled intent, enable-before-destroy ordering and actual prior
activation; rejected assigning WPF IsEnabled or always activating Window.Owner.
Publication is O(W) time/snapshot storage per boundary for W registered windows;
normal input remains allocation-free. Native callbacks and owner/focus walks are
dependent control flow, not SIMD kernels, GPU work or per-frame scanning.

Authored fixtures cover nested/new/popup admission, failed entry rollback, failed
exit diagnostics, callback removal/reentry, source focus/native admission and
Hide/Close ordering. A Windows-only hidden-window fixture exercises the actual
controller enabled gate and Reapply. These fixtures have not been executed.

Compile-only checkpoint (2026-09-09): final ProGPU.Tests and LibreWPF bridge
fixtures both compile with 0 warnings/0 errors; source PresentationFramework
fixtures compile with 2 warnings/0 errors and RealPresentationFrameworkHarness
with 0/0. Callback-created surface admission and active-then-hidden restoration
fixtures are included. Builds use the workspace SDK and `--no-restore -m:1`;
no tests, verifier scripts, application/VM/GPU runs, benchmarks or CI polling ran.
Latest fetched ProGPU main (`102e39e5088b462624da6296ff70a43ed2c5d8b4`) is
already included. The unrelated native semantic-state edits and performance
artifact deletions are excluded from this batch. Fresh package production and
package-mode startup remain separate open gates, not established by these builds.

## Native top-level owner connection

Acceptance action: the existing LibreWPF MVP About dialog assigns its source
`Window.Owner`, then opens through `ShowDialog`. Source inspection found that the
host retained that identity only as an activation hint; source owner assignment
could also enter WPF's hidden-HWND taskbar-owner path. The owner is now resolved
through the registered live ProGPU host, not its opaque presentation-source handle.

`SilkWindowController.TrySetOwner` reuses the original ProGPU native platform
adapters. Both renderer modes share this host operation. Controllers must already
be attached on their creating thread, use the same native kind/display, and not be
disposed, closing or cyclic. The child host initializes hidden when necessary;
the bridge applies ownership before Show and rejects unsupported native admission.
Accepted state is retained separately from a failed request. Clearing removes the
native relation. No renderer implementation or shader is changed.

Win32 checks same-thread/process top-level windows, walks the native owner chain
(bounded at 1024 ancestors for malformed external chains), and checks the write
and resulting owner. Existing ProGPU window-attribute bindings are reused, with
no popup style, nonactivation subclass or child reparenting. Cocoa rejects wrong
kind/self/native ancestor cycles before detaching the old owner. X11 requires the
same display and flushes the existing transient hint; its return is submission
acceptance, not proof of window-manager behavior. Wayland remains unsupported.
The source callback rejects missing support, updates source collections only after
admission, and rejects raw WindowInteropHelper owner handles before HWND access.
Ownership chain walks are dependent control flow, not data-parallel CPU kernels.

Provenance: original ProGPU `SilkWindowController.SetParent`,
`Win32NativeWindowPlatform.Popup.cs` checked attribute access, and Cocoa/X11
`SetParent` adapters. No third-party implementation was copied. Public contracts:
[Microsoft top-level owner attributes](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowlongptrw),
[Apple child-window ordering](https://developer.apple.com/documentation/appkit/nswindow/addchildwindow%28_%3Aordered%3A%29),
and [Xlib transient hints](https://xorg.freedesktop.org/archive/current/doc/libX11/libX11/libX11.html).
Adopted owner-versus-child distinction, explicit native rejection, and cycle
prohibition; rejected treating normal dialogs as nonactivating popups.

Authored policy/source/bridge fixtures cover assign/replace/clear, rejected native
writes, foreign/cyclic handles, disposed source owners, enabled-independent host
routing and source collection preservation. These are not executed evidence.
Native OS ordering/close behavior, cross-monitor startup placement, input
suppression and previous activation/focus restoration still require completion or
qualification. Windows package admission stays guarded. The package-mode MVP
still needs fresh package production; source compilation does not bypass its feed.

Compile-only checkpoint: ProGPU.Tests 0 warnings/0 errors, source
PresentationFramework fixtures 2/0, final bridge fixtures 21/0, and the source
RealPresentationFrameworkHarness 0/0. Corrected four
test-only assertions that treated non-generic WindowCollection as a generic
collection after the initial source-fixture build failure. No fixture, native
window, VM, image/lifetime/performance gate or CI qualification ran.
The additional LibreWPF RealApplicationRunHarness no-restore build stopped with
NETSDK1004 (missing restore assets). This is separate from source compilation;
fresh SDK/package production and consumption remain required.

## Shared source input admission

`PortableModalInputScope` now owns the active dialog identity for a synchronous
host/source UI thread. Enter before Show and dispose after the dialog invocation;
only the innermost dialog is admitted. Unknown ownership fails closed while the
scope is active. Normal input has one thread-local check; active modal checks use
reference identity. There are no per-event allocations, reflection or changes to
application IsEnabled values. Each invocation owns one small scope/delegate;
release clears references. Wrong-thread or out-of-order release fails without
corrupting the stack. This is bounded stateful control flow, not SIMD compute.

LibreWPF host ingress and queued dispatch both consult this shared policy before
activation hooks or source delivery. Blocked drag/drop advertises no accepted
effect, native close notifications are canceled (explicit source Close stays
allowed), and blocked activation/nonclient hooks are not forwarded. Rendering,
geometry and lifecycle updates continue. Source input checks the actual reported
root and the captured-mouse root after redirection; keyboard/text also checks the
focused element. Modal entry deactivates blocked input providers and releases
blocked mouse capture and keyboard focus.

Popup admission follows the live source-owned popup owner presentation source,
including separately surfaced and nested popups. It does not guess from mutable
PlacementTarget, Window.Owner, native handles or OS. The ownership walk rejects
missing/disposed roots and cycles with allocation-free cycle detection. A popup
of an inactive owner cannot inherit the active dialog's permission merely by
receiving a native event. Only original source Window identities are admitted.

This source layer alone is **not full native modality**. Native ownership and
Win32 gates/source restoration are connected in the sections above; Cocoa/Linux
native suppression and application-wide coordination remain open. Other UI
threads are independent. OS
activation may occur before the host rejects its notification; a successful
source filter is not proof that the OS prevented that activation. Custom hosts
must use the source input registrar; arbitrary direct application event injection
is outside this transport contract. Both native and managed renderers share the
same policy. No rendering/compiler/native shader algorithm is duplicated.

Authored fixtures cover nested identity/thread ownership, release ordering and
exception cleanup, host ingress and pre-modal queued events, source admission,
application-enabled-state preservation and capture cleanup. Actual nested popup
interaction, OS keyboard/mouse/capture behavior and owner reactivation remain
required final application gates, not proven by policy fixtures or compilation.

Source-admission compile checkpoint: ProGPU.Tests 0 warnings/0 errors, source
PresentationFramework fixtures 2/0, final bridge fixtures 20/0 (116/0 on initial
dependency rebuild), source application harness 0/0. The initial ProGPU fixture
build selected xUnit's obsolete async overload for a throw-only lambda; explicitly
typing that lambda as Action fixed compilation. No tests, verifiers, native
applications/input, VM/GPU workloads, benchmarks or CI qualification ran. The
package-mode MVP still requires the separately planned fresh package production;
the previously missing local feed is not bypassed by these source builds.

## Dialog pumping

The LibreWPF MVP About dialog uses the same portable host in native C++ MIL and
managed rendering modes. Application run lifetime and modal dialog lifetime are
different: the application may remain alive with hidden windows; a dialog's
source can end its synchronous invocation without destroying the native window.

`PortableWindowActivationCallbacks.RunDialog` is an optional, typed callback
separate from `Run`. It receives the existing activation and a borrowed
`Func<bool>` continuation. The source captures capability admission before Show,
owns dialog/result/visibility state and the modal enter/leave scope, and rejects a
host that returns while the dialog is still open. Missing capability must not
fall through to application Run or a WPF HWND dispatcher loop.

The host pumps native events, dispatcher work and rendering while its normal
native lifetime remains valid and the continuation permits another iteration.
It checks the continuation before polling and after callbacks, outside existing
close/dispose exception filters. Hiding must not close/dispose or recreate the
window. Continuation exceptions propagate; the predicate is borrowed on the host
thread and never stored beyond this synchronous call. A per-dialog delegate is
bounded control flow, not a SIMD-eligible compute kernel or per-frame allocation.

The WPF source consumer and host integration live in LibreWPF. This neutral
ProGPU contract is original API design using object identity and a boolean
continuation, not copied WPF implementation. Public contract references consulted:

- [Window.Hide](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.hide):
  hiding preserves the instance and does not raise Closing/Closed.
- [Window.ShowDialog](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.showdialog):
  synchronous result, application modality and owner/activation semantics.

Scope: this connects loop lifetime, not full modality. The additional owner,
Win32 gate and source restoration connections are described above; their remaining
platform/application qualification requirements are not closed by the loop.
Do not infer them from ComponentDispatcher's
modal notification or the presence of this callback. Windows SDK admission remains
guarded; no native menu, popup or rendering algorithm is replaced here.

Authored regressions cover optional capability separation, continuation identity
and exception propagation; LibreWPF covers Hide/reuse, canceled result closes,
missing callbacks, premature host return and modal-scope cleanup. The MVP existing
dialog gate additionally hides a live dialog, retains it in Application.Windows,
then reuses and closes it. Tests and native application runs are deferred until
the final implementation-freeze qualification phase. Both renderers must pass the
same actual application actions on macOS/Linux and Windows Parallels; compilation
or a callback-only fixture is not runtime/modal parity evidence.

Compile-only checkpoint: ProGPU.Tests builds with 0 warnings/0 errors; LibreWPF
source PresentationFramework fixtures build with 2 warnings/0 errors (6/0 on the
initial dependency rebuild), bridge fixtures with 116/0 and source-built
application harness with 0/0. No fixture, verifier, GPU/native application, VM,
benchmark or CI gate was executed. Latest fetched ProGPU main is included in the
feature branch. The C++ scene/compiler/render algorithms are unchanged: native
and managed modes both consume this same host/source lifetime contract.

## Native enabled-state admission prerequisite

The MVP modal dialog's remaining other-window input blocker requires trustworthy
native enable/disable admission. The shared `SilkWindowController.SetEnabled`
previously combined enabled-state and shadow-refresh results with OR, allowing
an unrelated decoration operation to hide unsupported input-state handling.
It now returns the enabled-state result alone and refreshes shadow only after
that operation was accepted. Desired controller state is still retained for
later attachment/reapplication; a false return is not proof of effective state.

On Windows the platform now checks local same-thread/process ownership before
the call, uses source-generated integer-BOOL bindings, and checks ownership and
actual enabled state after synchronous native callbacks. It deliberately ignores
the native call's previous-state return value. A destroyed window or a callback
that reverses the requested state produces false; unrelated failures propagate.
The caller must still own the live native window lifetime; this is not protection
against arbitrary HWND reuse or a native-window ownership lease.

This original control-flow policy follows the public
[EnableWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enablewindow)
and [IsWindowEnabled](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-iswindowenabled)
contracts: EnableWindow returns the previous disabled state and synchronously
delivers notifications, while IsWindowEnabled reads current input availability.
The implementation introduces no WPF source-copy, pixel/compute algorithm or
SIMD-eligible loop. All host frameworks and both renderers share the same backend.

Authored policy fixtures cover enable/disable/idempotent requests, invalid and
foreign ownership, destruction, state reversal and exception propagation.
They do not qualify actual WM_ENABLE ordering or native input suppression.
The macOS implementation currently updates standard window-button state, not
all keyboard/pointer input; X11/Wayland enabled-state support remains incomplete.
Do not use a successful chrome update as cross-platform modal admission. The
WPF other-window input coordinator, native ownership, nested scopes, activation
restoration and actual Windows/macOS/Linux app validation remain open.

Next core integration must keep application enabled intent separate from modal
blocking. The existing ProGPU WinUI `AppWindow.ApplyModalOwner`/`AddModalChild`/
`ReleaseModalChild` implementation directly assigns the window's IsEnabled value;
that is not a suitable WPF adapter contract for preserving application-owned
values. WPF host ingress, queued input dispatch and source captured-mouse routing
must all use the same effective restriction, including popup surfaces owned by the
active dialog. Restricting just the initial host event does not cover an event
queued before modal entry or redirected afterward by mouse capture.

Enabled-state checkpoint compilation: final ProGPU.Tests 0 warnings/0 errors;
LibreWPF source application harness 5 warnings/0 errors. No policy fixture, native
window/input workload, application, VM, benchmark or CI qualification was run.
Latest fetched ProGPU main remains included. This is shared OS-host plumbing;
managed/native scene and rendering implementations are unchanged and both use it.
