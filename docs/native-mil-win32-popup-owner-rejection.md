# Win32 native popup-owner rejection diagnostics

`NativePopupWindow.TryConfigureOwner` must admit two distinct, live, same-thread
top-level HWNDs while the popup is hidden. A temporary unique subclass identity
on each original HWND spans configuration and rollback, then is removed. Every
attribute/hook/frame mutation rechecks those identities; same-thread numeric HWND
reuse after destruction cannot receive subsequent writes. The attempt rejects
before attribute mutation if either identity hook cannot be installed. It then writes the owner and
nonactivating popup styles, installs its native mouse-activation lifetime hook,
and refreshes the frame. Before success, it reads back the live same-thread pair,
top-level owner, exact requested popup owner/styles (including hidden state), and
the installed nonactivation subclass. Style changes and frame refresh dispatch
synchronous window messages; a callback may replace those values even when every
native operation succeeds. Such a mismatch or unreadable state reports
`ConfigurationChanged`, attempts the same original-attribute restoration, and
returns false. Any failed write/hook/refresh also attempts restoration; the caller
must dispose the rejected popup. Restoration cannot recover a destroyed HWND. A
lifetime and locality check precedes each cleanup mutation so retirement cannot redirect
remaining writes to a reused, invalid or foreign-thread HWND. Rejected reconfiguration
retains a nonactivation hook that was present before this attempt. A
diagnostic must not show an unowned window or turn this rejection into a managed
renderer fallback.

Removing the caption and sizing frame also removes `WS_EX_WINDOWEDGE` from the
requested extended style. An independent Windows STATIC-window observation kept
the original values: style `04CF0000`/extended `00040100` became style `84000000`,
and Windows normalized requested extended `08000180` to `08000080`, both before
and after frame refresh. The target explicitly requests that borderless state;
the final comparison still requires the complete exact style words. Unrelated
extended styles such as layered rendering remain intact, and rejected operations
retain their original restoration values.

The four subclass helpers resolve together from one retained common-controls v6
module. `GetWindowSubclass` is not a named export in the legacy module selected by
an unmanifested application. A library-owned embedded manifest selects v6 only
while loading that module; the original calling activation context is restored
before window work. This does not require an application visual-styles manifest
or change the application's default theme. Single-file/NativeAOT deployment uses
the same embedded manifest through a temporary file removed after acquisition.
Partial acquisitions free their module/context and reject before HWND mutation.
The complete module remains loaded while native subclass callbacks may exist.
The original unmanifested Windows test host remains unchanged and checks calling
activation-context preservation on both successful and rejected configuration.

An exact Windows 11 ARM64 Parallels Toolkit source-overlay run with ProGPU
native semantic-scene checkpoint build `1dfda315e4b82c889de1b0a14eee90a2001204fa`
reached popup validation and rejected the selected native owner at
`WpfPortableNativePopupHost.EnsureInitialized`. That exception alone did not
identify whether the GLFW handle was absent, nonlocal, already visible, a child,
or rejected by a later Win32 operation. The preceding native scene encode was
also slow, but popup admission is a separate correctness gate.

Set `PROGPU_NATIVE_TRACE_POPUP_OWNER=1` only in a diagnostic run. Rejected Win32
attempts then write one line to standard error with a stable reason, the borrowed
HWND values, and current/owner/popup native thread and process IDs. Accepted
attempts and ordinary runs perform no diagnostic reads or logging. The reason
distinguishes invalid identity, locality, each original-attribute read, child
or visible styles, each write, the nonactivation hook, frame refresh, and changed
configuration after synchronous callbacks. This
instrumentation does not bypass any admission condition or retain an HWND.

Qualification requires overlaying the exact built `ProGPU.Backend.dll` in the
same Toolkit output as the exact native runtime, recording both hashes, and
rerunning the actual source-owned popup sequence in the VM. A reason reported by
the fake-operation fixture is only a unit contract; it cannot qualify GLFW
hidden-window creation, Win32 modality, Toolkit input, floating windows, or the
assembled LibreWPF SDK package. Fix the observed native/source integration cause
before declaring the popup gate closed.

The focused controls cover callback changes to the owner, hidden/style state,
nonactivation style/hook, owner kind, locality and read availability. A separate
Windows-only control replaces an actual hidden HWND's owner inside its original
`WM_NCCALCSIZE` dispatch and requires rejection plus restoration. It synthesizes
no pointer or keyboard input and does not qualify the broader popup appearance,
placement, dismissal or package matrix. The native contracts are documented by
[SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)
and [GetWindowSubclass](https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nf-commctrl-getwindowsubclass).
