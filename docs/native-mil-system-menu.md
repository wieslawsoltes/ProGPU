# Native window system-menu connection

## Acceptance and scope

LibreWPF's MVP exposes Window / Show system menu. An active portable Window must
not hand its opaque presentation-source identity to WPF's Win32 helpers, including
when ProGPU happens to own an HWND. The source calls the optional neutral
`PortableWindowActivationCallbacks.ShowSystemMenu` capability with its activation
and absolute native desktop coordinates. The host resolves its real native window.
Absent/rejected capability fails explicitly. Old callback constructors remain
binary-compatible; replacing registration clears an omitted capability.

This batch implements the Win32 system-menu provider. Cocoa, X11 and Wayland
providers remain missing; an active portable window no longer reports their
previous source no-op as menu display. Windows SDK package admission remains
guarded. This is not general menu, COM or Direct2D expansion.

## Shared native ownership

`ProGPU.Backend.NativeWindowSystemMenu.TryShow` accepts a typed native handle and
integer desktop point. Unsupported kinds return false before native access. The
Win32 implementation admits only a same-process, same-thread top-level owner with
an existing system menu. It neither creates/replaces menu items nor transfers or
destroys the owner/menu handles. Native initialization notifications remain enabled
for standard availability and application-customized entries. Menu alignment uses
the system's handedness setting; either mouse button can select an item.

Tracking returns a command identifier. Reported native errors fail; cancellation
without a reported error posts nothing. A selected command is posted exactly once
as WM_SYSCOMMAND after rechecking the owner and its menu identity. The modal loop
may dispatch callbacks that destroy or replace the owner/menu. Do not retain a
handle across calls, bypass thread admission or directly assign another framework's
Window state from this shared backend. Native window event/close-cancellation
integration remains owned by the existing host.

Portable source PointToScreen and host placement share desktop coordinates. Do not
multiply monitor origins by framebuffer DPI. The platform adapter accepts finite
signed-int-range doubles and rounds once to the nearest native pixel, ties away
from zero. The source's internal physical-coordinate entry uses the same native
desktop frame for portable owners. Native WPF's original logical-to-device path
is unchanged. Mixed-DPI placement still requires final native qualification.

The implementation is original ProGPU platform integration based on public
contracts, not copied WPF or third-party code. It reuses ProGPU's native handle
types, window-thread queries and posted-message boundary. The bounded policy is
O(1) work/storage outside the OS's interactive menu loop; it creates no GPU,
renderer, font context, retained scene or per-frame work. SIMD/GPU compute policy
is inapplicable to this event-driven control flow. Both managed and C++ MIL
renderers use the same window adapter; no paired rendering algorithm is changed.

## Contract references

- [GetSystemMenu](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmenu): obtain the existing window menu without resetting it; retain native initialization and custom items.
- [TrackPopupMenuEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-trackpopupmenuex): desktop positions, alignment, returned selection, cancellation and error behavior. A zero command alone cannot prove that pixels were displayed.
- [WM_SYSCOMMAND](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand): native command delivery, including system and application-owned menu identifiers.

## Qualification pending

Authored policy fixtures cover left/right alignment, negative desktop positions,
one-post delivery, custom identifiers, cancellation, rejection and menu replacement
during tracking. A Windows-only fixture covers real child HWND and foreign-thread
rejection without displaying a menu. Source fixtures cover typed registrar
forwarding, source identity, coordinates, missing/rejected/replaced capabilities,
invalid points and propagation of host exceptions. Host fixtures reject calls
before native window creation and after disposal.

These fixtures are authored, not executed. Final Windows VM qualification must
open the actual MVP menu with each renderer, cancel by Escape/outside click,
select native state/move/size/close commands, exercise Closing cancellation,
and repeat after moving between monitors/scales. Check images and source/native
state; the API result or a mock fixture alone does not prove menu display. Keep
macOS/Linux missing-provider behavior explicit until their real presentation
capabilities are implemented. Package and exact-head CI gates are unchanged.

Implementation-phase compilation: ProGPU.Tests builds with 0 warnings/0 errors;
LibreWPF bridge fixtures with 116 warnings/0 errors; source PresentationFramework
fixtures with 6 warnings/0 errors. These are compilation results only. The initial
test build exposed an internal policy accessibility error; the fixture now links
the same policy source using the existing popup-policy test pattern instead of
expanding product API visibility. Latest fetched ProGPU main is included. No
fixture, verifier, application, VM/GPU, benchmark or CI qualification was run.
