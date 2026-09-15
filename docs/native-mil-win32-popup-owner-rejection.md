# Win32 native popup-owner rejection diagnostics

`NativePopupWindow.TryConfigureOwner` must admit two distinct, live, same-thread
top-level HWNDs while the popup is hidden. It then writes the owner and
nonactivating popup styles, installs its native mouse-activation lifetime hook,
and refreshes the frame. Any failed write/hook/refresh restores the original
attributes and returns false; the caller must dispose the rejected popup. A
diagnostic must not show an unowned window or turn this rejection into a managed
renderer fallback.

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
or visible styles, each write, the nonactivation hook, and frame refresh. This
instrumentation does not bypass any admission condition or retain an HWND.

Qualification requires overlaying the exact built `ProGPU.Backend.dll` in the
same Toolkit output as the exact native runtime, recording both hashes, and
rerunning the actual source-owned popup sequence in the VM. A reason reported by
the fake-operation fixture is only a unit contract; it cannot qualify GLFW
hidden-window creation, Win32 modality, Toolkit input, floating windows, or the
assembled LibreWPF SDK package. Fix the observed native/source integration cause
before declaring the popup gate closed.
