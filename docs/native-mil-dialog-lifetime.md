# Source-controlled portable dialog lifetime

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

This connects source/host input filtering on each participating UI thread, **not
full native modality**. Other UI threads are independent. Native nonclient input
suppression, real window owner configuration, application-wide coordination where
required and restoration of previous native activation/focus remain open. OS
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

Scope: this connects loop lifetime, not full modality. Native owner-window
configuration, disabling other application windows (including newly created
windows), nested-modal input restriction and previous activation restoration
remain required integration work. Do not infer them from ComponentDispatcher's
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
