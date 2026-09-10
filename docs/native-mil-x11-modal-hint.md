# X11 dialog modal-hint ownership

Acceptance action: open, hide/close and reopen the LibreWPF Showcase About dialog.
The source input policy already rejects blocked-window input, activation and
native close requests. Native X11 ownership publishes WM_TRANSIENT_FOR, but that
alone does not mark the dialog modal. This change connects the advisory EWMH
state to the existing source dialog lifetime; it is **not full Linux native input
suppression or runtime qualification**.

## Shared ProGPU contract

`SilkWindowController.TryBeginModalHint` returns a typed `NativeWindowModalHint`
lease only for an attached, live X11 window on its creating thread. One live lease
per controller is allowed; different nested dialogs retain independent windows.
The lease borrows the native window. Hosts release it before hide/destruction;
controller disposal releases it before disposing the platform adapter. A failed
release retains ownership and throws instead of reporting successful cleanup.
Removal is an idempotent state request, unlike consuming an AppKit session token.

The X11 adapter checks the actual window's root and advertised modal-state support,
rejects override-redirect windows and bounds native property reads. LP64 atom
records are consumed directly with runtime-intrinsic-capable span searches and
freed on every read path. New native imports are source-generated and blittable.
The existing ProGPU root-message transport is reused with the actual window root.

A preexisting modal bit is preserved. Otherwise entry adds that bit and release
removes only it, never restoring a stale full-state snapshot. Unmapped state
updates merge current atoms in a fixed stack buffer; mapped changes go through
the window manager. Pending mapped requests are tracked so rapid reopen does
not adopt a still-visible, pending-removal bit as externally owned state. An
unmapped direct publication does not mask later externally changed properties.
Rejected/throwing entry rolls back a possibly partial property update. If rollback
fails, the caller/controller retains the lease for explicit cleanup.

No owner/group relationship, window type, event mask, focus, enabled intent,
keyboard/pointer grab, native class or renderer is replaced. Unknown/unsupported
WM state is rejected, not advertised as a successfully applied modal capability.
The 64-state/4096-capability atom bounds fail closed; they do not truncate lists.
Ordered scope handling and bounded protocol compaction are control/serialization
work, not pixel/geometry compute kernels. No GPU fallback or speed claim is added.

## LibreWPF connection

The X11 host requests the hint before entering its dialog event loop. A source
that was synchronously hidden before RunDialog does not acquire one. Admission
failure propagates; source ShowPortableDialog hides through the real Window path
instead of leaving a visible modeless window after throwing. It keeps the source
identity for retry and retains the existing input-scope/focus cleanup protocol.
If hiding also fails, the original and hide errors remain explicit together.

ReleaseDialog removes the hint before source input/focus cleanup. Host Hide,
Close/disposal and loop finally also release idempotently before the native
identity can disappear. The ordinary application loop does not acquire a hint.
Managed portable and C++ MIL renderer modes share this host path; neither scene
implementation or shader pipeline changes. Cocoa automatic session admission,
Wayland modality and Windows package admission remain separate requirements.

## Authored qualification

Portable fixtures cover entry/release/rollback failures, preexisting state,
idempotence, wrong-thread cleanup, deferred WM observation, source gate/focus
ordering and LP64 native field layout. Bridge coverage protects source-completion,
hide and disposal ordering; source Window fixtures require real hiding on premature
loop return and host failure, retaining identity for retry.

A real native X11 fixture creates hidden owner/dialog windows without a graphics
API, exercises actual properties/ownership and repeated leases, preserves unrelated
state and verifies that hint support does not grant native enabled-input support.
Run it in the final X11 desktop lane with an EWMH-capable WM, not merely an empty
DISPLAY or the build-only Colima container:

```sh
PROGPU_NATIVE_X11_MODAL_TEST=1 dotnet test src/ProGPU.Tests/ProGPU.Tests.csproj \
  --filter FullyQualifiedName~NativeWindowModalHintLinuxTests
```

This explicit fixture supplements, not replaces, visible Showcase native/managed
keyboard, pointer, owner-close, nested-dialog/popup, Hide/reopen and focus tests.
Mapped WM behavior, WM restarts, unrelated native windows, other UI threads and
Wayland still require platform implementation/qualification. Submission and
source policy synchronization cannot prove WM input suppression. No fixture,
renderer application, verifier, benchmark or CI gate executed in this batch.

## Provenance and public contracts

Implementation is original ProGPU scope ownership plus its existing
X11NativeWindowPlatform/SilkWindowController transport, and LibreWPF's original
source dialog/host callbacks. No other toolkit implementation was copied or
ported. This is window-state/lifetime integration, not a rendering, text,
scene-compilation, startup or GPU architecture change.

The [EWMH specification](https://specifications.freedesktop.org/wm/latest-single/)
defines advertised support, modal state, transient ownership and root messages.
The [Xlib standard](https://xorg.freedesktop.org/archive/current/doc/libX11/libX11/libX11.html)
defines window/root/mapping attributes, native-long property storage, property
reads and XFree ownership. These protocol concepts were adopted; treating a WM
hint as an enforced input gate, globally grabbing the desktop, and replacing
foreign native window classes were rejected.
