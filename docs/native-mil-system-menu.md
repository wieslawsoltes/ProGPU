# Native window system-menu connection

## Acceptance and scope

LibreWPF's MVP exposes Window / Show system menu. An active portable Window must
not hand its opaque presentation-source identity to WPF's Win32 helpers, including
when ProGPU happens to own an HWND. The source calls the optional neutral
`PortableWindowActivationCallbacks.ShowSystemMenu` capability with its activation
and absolute native desktop coordinates. The host resolves its real native window.
Absent/rejected capability fails explicitly. Old callback constructors remain
binary-compatible; replacing registration clears an omitted capability.

The shared provider implements Win32 tracking and X11 window-manager requests.
Cocoa and Wayland providers remain missing; an active portable window no longer reports their
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

## X11 window-manager request

The Linux adapter resolves the actual Silk.NET X11 Display/XID pair, not a WPF
source identity and not a Wayland surface. The caller must own and keep both live
on the window thread for the synchronous call; this is not a validator for stale
or arbitrary foreign pointers. No process-global Xlib error handler is installed.
The existing Xlib/host error policy remains in force for violated native lifetimes
or server failure. Only missing native libraries/entry points become an unsupported
result; unrelated managed errors propagate.

The provider queries the owner's actual root with XGetGeometry, including a
nondefault X screen. That round trip orders pending owner creation before opening
another connection. It requires the ICCCM WM_STATE property with Normal or Iconic
state, excluding absent/withdrawn clients instead of treating an arbitrary child
or override-redirect surface as a managed top level. It then reads that root's
_NET_SUPPORTED and requires _GTK_SHOW_WINDOW_MENU. Atom existence alone is not
advertised support. The list is bounded at 4,096 atoms; invalid type/format,
oversized/truncated metadata and missing capabilities fail explicitly. Properties
are read without deletion, and every Xlib property allocation is released.

XIQueryVersion negotiates client-specific behavior. To avoid changing GLFW/Silk.NET
input semantics, the menu operation opens one short-lived connection to the same
server using the owner's XDisplayString, checks XInput availability and negotiates
XI 2.0 there. XIGetClientPointer targets the **owner window's client**, not the new
connection's default pointer. An absent pointer is unsupported; never guess device
zero or a hardcoded master-pointer ID. libXi.so.6 is an optional runtime capability;
without it no menu request is sent. The temporary connection is closed in finally,
including device-query or send failure, and the host display remains borrowed.

One format-32 ClientMessage carries the owner XID, menu atom, real device ID and
signed root-relative X/Y, with both reserved slots zero. It is sent to the owner's
root with SubstructureNotify/Redirect delivery and flushed. The Xlib record uses
native-sized C-long slots and a full 24-native-word XEvent buffer on ILP32/LP64;
it is not the packed X11 wire layout. Format-32 property storage also uses native
unsigned-long stride. No second framebuffer DPI conversion is performed.

Success means an advertised request was submitted, **not** that the window manager
displayed or honored it. There is no synchronous acknowledgement. The WM owns
menu items, selection and window-state requests; ProGPU does not synthesize menu
pixels or duplicate state commands. Existing host event and close-cancellation
handling remains authoritative. No grabs are forcibly released, no input events
are consumed, and no window-manager advertisement or owner property is modified.
Invocation during another active input grab requires final interaction coverage.

This is original ProGPU platform glue derived from public protocol/API contracts;
no GTK/KDE/SDL implementation was copied or translated. Capability copying/search
is O(A) for at most 4,096 atoms using span operations, with bounded stack/native
storage. One connection and a bounded number of server round trips occur only on
this user action, never per frame. This is not a compute-heavy scalar fallback or
a rendering optimization; no speed claim is made. Both renderer modes share it.

The Win32 implementation is original ProGPU platform integration based on public
contracts, not copied WPF or third-party code. It reuses ProGPU's native handle
types, window-thread queries and posted-message boundary. Its bounded policy is
O(1) work/storage outside the OS's interactive menu loop; it creates no GPU,
renderer, font context, retained scene or per-frame work. SIMD/GPU compute policy
is inapplicable to this event-driven control flow. Both managed and C++ MIL
renderers use the same window adapter; no paired rendering algorithm is changed.

## Contract references

- [GetSystemMenu](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmenu): obtain the existing window menu without resetting it; retain native initialization and custom items.
- [TrackPopupMenuEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-trackpopupmenuex): desktop positions, alignment, returned selection, cancellation and error behavior. A zero command alone cannot prove that pixels were displayed.
- [WM_SYSCOMMAND](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand): native command delivery, including system and application-owned menu identifiers.
- [KDE NETRootInfo public menu API](https://api.kde.org/netrootinfo.html): _GTK_SHOW_WINDOW_MENU target, input device and root-coordinate semantics. Adopt the WM-owned request, not a framework-owned replacement menu.
- [EWMH root messages and _NET_SUPPORTED](https://specifications.freedesktop.org/wm/latest-single/): advertised support and root ClientMessage routing. The GTK menu extension itself is not a universal EWMH requirement.
- [ICCCM WM_STATE](https://xorg.freedesktop.org/archive/current/doc/xorg-docs/icccm/icccm.html): managed-client state and property format; reject withdrawn/missing state rather than guessing a parent.
- [Xlib contracts](https://xorg.freedesktop.org/archive/current/doc/libX11/libX11/libX11.html): root geometry, display identity, native-long property/event storage, SendEvent and allocation ownership.
- [XIQueryVersion](https://xorg.freedesktop.org/archive/X11R7.5/doc/man/man3/XIQueryVersion.3.html) and [XIGetClientPointer](https://xorg.freedesktop.org/archive/X11R7.5/doc/man/man3/XIGetClientPointer.3.html): isolated version negotiation and actual owner-client pointer selection; do not alter the host's input connection.

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
macOS/Wayland missing-provider behavior and unsupported X11 environments explicit.
Package and exact-head CI gates are unchanged.

X11 policy fixtures cover owner/display rejection, actual root routing, signed
coordinate extremes, nondefault device selection, unsupported advertisement,
connection/device/send failures, exception cleanup, full Xlib event size/offsets,
native-long property stride and transactional malformed-property rejection. They
link the production policy source without widening public API. Both 32-bit and
64-bit ABI expectations are authored; neither is runtime-qualified by compilation.

Final Linux qualification must run the actual MVP action in both renderer modes
under a WM advertising the extension (record WM/version, X11 versus XWayland,
screen/root, input device and package/native artifact commits). Observe the menu,
Escape/outside-click cancellation and state/close/move/resize actions, close
cancellation, repeat/reopen and multi-monitor coordinates. Check pointer/keyboard
input before and after repeated invocation and verify no connection/handle leak.
Include a nondefault X screen where available and an unsupported WM/missing-XInput
case; rejection is an explicit unsupported result, never a passed menu-display
gate. Native Wayland is a separate missing provider, not covered by XWayland.

Earlier Win32 implementation-phase compilation: ProGPU.Tests builds with 0 warnings/0 errors;
LibreWPF bridge fixtures with 116 warnings/0 errors; source PresentationFramework
fixtures with 6 warnings/0 errors. These are compilation results only. The initial
test build exposed an internal policy accessibility error; the fixture now links
the same policy source using the existing popup-policy test pattern instead of
expanding product API visibility. Latest fetched ProGPU main is included. No
fixture, verifier, application, VM/GPU, benchmark or CI qualification was run.

X11 implementation-phase compilation: ProGPU.Tests 0 warnings/0 errors,
LibreWPF bridge fixtures 116/0, source-built application harness 4/0. These compile
the shared backend, production host adapter and authored fixtures but do not run
them or qualify Linux native linkage/menu output. Latest fetched ProGPU main is
included. Runtime, VM/GPU, benchmark, source-verifier and CI execution remains
deferred to the core feature freeze; automatic CI has not been disabled.
