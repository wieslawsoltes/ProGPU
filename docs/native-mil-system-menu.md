# Native window system-menu connection

## Acceptance and scope

LibreWPF's Showcase exposes Window / Show system menu. An active portable Window must
not hand its opaque presentation-source identity to WPF's Win32 helpers, including
when ProGPU happens to own an HWND. The source calls the optional neutral
`PortableWindowActivationCallbacks.ShowSystemMenu` capability with its activation
and absolute native desktop coordinates. The host resolves its real native window.
Absent/rejected capability fails explicitly. Old callback constructors remain
binary-compatible; replacing registration clears an omitted capability.

The shared provider implements Win32 tracking, X11 window-manager requests and
a Cocoa AppKit native-action menu. Wayland remains missing; unsupported platform
capabilities are not successful source no-ops. Windows SDK package admission
remains guarded. This is not general menu, COM or Direct2D expansion.

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

## Cocoa native-action menu

The same Showcase action now resolves the real NSWindow through the typed Silk.NET
host and uses ProGPU's shared provider. The platform menu has **Minimize, Zoom
and Close**, implemented by AppKit, not by a WPF visual tree or a renderer-specific
menu implementation. Zoom is AppKit's standard/user-size toggle, not Windows
maximize/fullscreen. This is a native macOS adaptation, **not** arbitrary Win32
system-menu customization, Windows Move/Size keyboard modal loops or exact Windows
menu-item parity. The current labels are English, matching the acceptance Showcase;
localized/custom menu labels are not claimed by this fixed native-action surface.

Admission requires the actual AppKit main thread, supported 64-bit macOS ABI,
loaded AppKit classes and a visible window in NSApplication.windows. Identity
lookup happens before messaging a supplied window pointer. Calls do not marshal
synchronously from a worker thread or create a new native window. The source
Window's pre-host/disposed guards remain independent. Window style and actual
standard-button enabled state define the available actions. A window with no
available actions fails explicitly rather than displaying an empty capability.

Retain the window, its content view and its delegate only for this synchronous
operation. A private NSMenu owns three ordinary NSMenuItems and a scoped target.
Manual item enablement uses the opening capability snapshot. The callback records
only the identity of a known selected item for its own target, once: it performs
no P/Invoke, allocation, WPF call or user callback inside the reverse native action.
Thread-local stack context is restored after nested/reentrant tracking. No GCHandle,
dynamic managed delegate, source-window handle or object-shape reflection is used.

After tracking ends, selection is dispatched only if the original window is still
visible and registered, its retained view/delegate identities still match and its
selected native action remains enabled. A callback that closes/rehosts the owner
or disables its button cannot cause a stale action. Opening-disabled and unknown
selections are also rejected. Invoke performMiniaturize:, performZoom: or
performClose: exactly once on that owner. In particular, never call close directly:
performClose preserves AppKit's windowShouldClose: delegate and the existing
GLFW/host/source Closing cancellation path. Dispatch success does not assert that
a delegate accepted the close or that the requested size/state was ultimately applied.

Menu item targets are cleared before menu/target release. Owner leases and an
explicit autorelease pool are released on success, cancellation and managed failure.
Unrelated errors propagate; no generic native-recovery loop is introduced. Only a
private, process-lifetime Objective-C action class is registered, without swizzling
NSWindow/NSObject or adopting another module's class. A collectible provider is
rejected via an anchor Type.IsCollectible lifetime check because a registered native
method must never outlive its managed callback code. This bounded loader-capability
check is not runtime discovery of application objects. Class-name collision fails
closed rather than reusing an unknown callback.

Placement maps ProGPU's top-left native desktop convention to AppKit screen points
using the current NSScreen.screens first entry, **not** mainScreen (which follows
keyboard focus). Read it on each invocation so display reconfiguration is observed.
Then use NSWindow.convertPointFromScreen: and NSView.convertPoint:fromView: to let
AppKit handle view origins/flipping and attach the popup to the real content view.
Do not pass backing pixels or multiply monitor origins by a Retina scale. CGRect
returns use objc_msgSend_stret on x64 and ordinary objc_msgSend on arm64; CGPoint
arguments/returns use the native two-double ABI. Finite screen/point checks precede
tracking. Exact host-to-AppKit anchor placement still needs final mixed-monitor
qualification, including primary-screen changes and one-pixel boundary cases.

AppKit's popup result distinguishes selection from cancellation, not cancellation
from every possible display failure. An invoked popup with no selected action
returns without dispatch; successful tracking cannot replace observed-menu evidence.
This event-driven provider creates no WebGPU context, scene or per-frame work and
is shared by managed and C++ MIL. Its own selection/coordinate work is O(1);
AppKit application-window identity lookup is O(W) for W windows, and its screen
snapshot depends on S displays. Native menu storage has three items, plus scoped
window/view/delegate references and AppKit's snapshots. No compute-heavy CPU
fallback is added, and no speed claim is made.

Original ProGPU implementation provenance: reuse the existing
MacOsNativeWindowPlatform's native handle and documented arm64/x64 struct-return
strategy; derive the new menu/ownership policy from Apple API contracts. No Apple,
GLFW, GTK, KDE or other third-party implementation text was copied or translated.

## Contract references

- [GetSystemMenu](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmenu): obtain the existing window menu without resetting it; retain native initialization and custom items.
- [TrackPopupMenuEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-trackpopupmenuex): desktop positions, alignment, returned selection, cancellation and error behavior. A zero command alone cannot prove that pixels were displayed.
- [WM_SYSCOMMAND](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand): native command delivery, including system and application-owned menu identifiers.
- [KDE NETRootInfo public menu API](https://api.kde.org/netrootinfo.html): _GTK_SHOW_WINDOW_MENU target, input device and root-coordinate semantics. Adopt the WM-owned request, not a framework-owned replacement menu.
- [EWMH root messages and _NET_SUPPORTED](https://specifications.freedesktop.org/wm/latest-single/): advertised support and root ClientMessage routing. The GTK menu extension itself is not a universal EWMH requirement.
- [ICCCM WM_STATE](https://xorg.freedesktop.org/archive/current/doc/xorg-docs/icccm/icccm.html): managed-client state and property format; reject withdrawn/missing state rather than guessing a parent.
- [Xlib contracts](https://xorg.freedesktop.org/archive/current/doc/libX11/libX11/libX11.html): root geometry, display identity, native-long property/event storage, SendEvent and allocation ownership.
- [XIQueryVersion](https://xorg.freedesktop.org/archive/X11R7.5/doc/man/man3/XIQueryVersion.3.html) and [XIGetClientPointer](https://xorg.freedesktop.org/archive/X11R7.5/doc/man/man3/XIGetClientPointer.3.html): isolated version negotiation and actual owner-client pointer selection; do not alter the host's input connection.
- [AppKit menu tracking](https://developer.apple.com/documentation/appkit/nsmenu/popup(positioning:at:in:)): synchronous selection/cancellation and positioning in the supplied view.
- [Native close action](https://developer.apple.com/documentation/appkit/nswindow/performclose(_:)) and [native zoom action](https://developer.apple.com/documentation/appkit/nswindow/performzoom(_:)): button-equivalent behavior and delegate cancellation, not direct close or a fabricated Windows maximize operation.
- [Menu enablement](https://developer.apple.com/library/archive/documentation/Cocoa/Conceptual/MenuList/Articles/EnablingMenuItems.html): explicit manual enablement; this provider also checks current owner/button state after tracking.
- [AppKit window inventory](https://developer.apple.com/documentation/appkit/nsapplication/windows) and [primary screen identity](https://developer.apple.com/documentation/appkit/nsscreen/screens): live application ownership and primary-versus-focus-screen distinction.
- [GLFW coordinates](https://www.glfw.org/docs/latest/intro.html#coordinate_systems), [AppKit screen conversion](https://developer.apple.com/documentation/appkit/nswindow/convertpoint(fromscreen:)) and [view conversion](https://developer.apple.com/documentation/appkit/nsview/convert(_:from:)-1dq9l): top-left desktop points, native screen/window/view mapping and backing-pixel separation.
- [Objective-C runtime public contracts](https://developer.apple.com/documentation/objectivec/objective-c-runtime): an original private action target class and typed ABI binding; no foreign class swizzling.

## Qualification pending

Authored policy fixtures cover left/right alignment, negative desktop positions,
one-post delivery, custom identifiers, cancellation, rejection and menu replacement
during tracking. A Windows-only fixture covers real child HWND and foreign-thread
rejection without displaying a menu. Source fixtures cover typed registrar
forwarding, source identity, coordinates, missing/rejected/replaced capabilities,
invalid points and propagation of host exceptions. Host fixtures reject calls
before native window creation and after disposal.

These fixtures are authored, not executed. Final Windows VM qualification must
open the actual Showcase menu with each renderer, cancel by Escape/outside click,
select native state/move/size/close commands, exercise Closing cancellation,
and repeat after moving between monitors/scales. Check images and source/native
state; the API result or a mock fixture alone does not prove menu display. Keep
Wayland missing-provider behavior and unsupported X11/Cocoa environments explicit.
Package and exact-head CI gates are unchanged.

X11 policy fixtures cover owner/display rejection, actual root routing, signed
coordinate extremes, nondefault device selection, unsupported advertisement,
connection/device/send failures, exception cleanup, full Xlib event size/offsets,
native-long property stride and transactional malformed-property rejection. They
link the production policy source without widening public API. Both 32-bit and
64-bit ABI expectations are authored; neither is runtime-qualified by compilation.

Final Linux qualification must run the actual Showcase action in both renderer modes
under a WM advertising the extension (record WM/version, X11 versus XWayland,
screen/root, input device and package/native artifact commits). Observe the menu,
Escape/outside-click cancellation and state/close/move/resize actions, close
cancellation, repeat/reopen and multi-monitor coordinates. Check pointer/keyboard
input before and after repeated invocation and verify no connection/handle leak.
Include a nondefault X screen where available and an unsupported WM/missing-XInput
case; rejection is an explicit unsupported result, never a passed menu-display
gate. Native Wayland is a separate missing provider, not covered by XWayland.

Cocoa policy fixtures cover action ordering, one dispatch after revalidation,
retained lease release, cancellation, absent owner/screen/actions, closed/rehosted
owners, capability removal, unknown/disabled selection, propagated host errors,
primary-screen point mapping and native record sizes. The callback fixture rejects
foreign/nested target identities, unknown items and repeated selection. These are
authored fixtures, not executed AppKit or image evidence. A source-linked scoped
context fixture also covers nested tracking interrupted by an exception, restoration
of the outer selection target, and late actions after scope exit.

Final macOS qualification must invoke the actual Showcase action with both renderers,
observe the native menu and each available action, cancel by Escape/outside click,
exercise source Closing cancellation and repeat close/reopen. Include style/button
restrictions, menu-triggered nested callbacks and owner closure/view/delegate
replacement during tracking. Observe mixed-Retina/non-Retina monitors above/left
of primary, primary-screen changes, placement at screen edges, keyboard/mouse input
after dismissal, and repeated-operation native/managed lifetime. Exercise arm64
and x64 native bindings where available. API results, compilation and policy mocks
alone do not pass this gate or enable Windows SDK package admission.

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

Cocoa implementation-phase compilation: final ProGPU.Tests 0 warnings/0 errors,
LibreWPF bridge fixtures 21/0 (116/0 on the initial dependency rebuild), and the
source-built application harness 0/0 (4/0 initially). The final graph includes the
scoped nested-selection fixture and native view-coordinate binding. No fixture,
verifier, native menu, application/VM/GPU workload, benchmark or CI qualification
was executed. Latest fetched ProGPU main is included. These results do not prove
AppKit runtime linkage, callback ordering, native output or application parity.
