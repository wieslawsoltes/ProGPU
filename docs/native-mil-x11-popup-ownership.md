# Shared X11 popup ownership

## Core application dependency

LibreWPF's MVP and Toolkit menus, ComboBoxes and tooltips select separately
surfaced native popups on X11. Before this connection the WPF-local
`TryConfigureX11PopupOwner` combined transient-owner and override-redirect results
with OR and did not confirm the type property. Partial setup could therefore
be admitted to Show. The shared `NativePopupWindow` provider previously covered
Win32 and Cocoa only.

The existing WPF source placement requires override-redirect: a window-manager
reposition after mapping would separate presented content from source pointer
coordinates. The window's transient owner and type metadata remain independent
required state. This connection preserves all three requirements.

## Shared implementation and ownership

`NativePopupWindow.TryConfigureOwner` now routes X11 handles with matching,
nonzero displays to `X11NativeWindowPlatform.Popup`. Its typed operation sequence
requires every step; one success never hides another failure. Admission requires
live InputOutput windows on the same root and an unmapped, root-child popup.
Owner and override-redirect writes are read back, and final confirmation checks
hidden/top-level/root identity, owner, override-redirect and the complete ordered
two-atom menu type property. A native setter's integer return is not used as proof
that the server applied the requested state.

The original dropdown-menu then popup-menu type preference is retained. Geometry,
event masks and unrelated attributes/properties are not changed. Property buffers
use Xlib's native-long representation and are always freed. The implementation
supports the shipped Linux LP64 RIDs and rejects other layouts before calling Xlib.

These are borrowed live native windows and a display, used on their owning host's
serialized thread. Callers must prevent native destruction during setup. The API
does not make arbitrary or externally destroyed XIDs safe, install a global Xlib
error handler, or take ownership of the display. Out-of-protocol-width XIDs are
rejected before native calls. Rejected/exceptional setup requires
destruction of the still-hidden popup; there is no success-after-partial-rollback
contract. LibreWPF already enforces disposal before propagating configuration
failure and must not fall back to an owner-surface popup after native selection.

## Provenance and implementation parity

Original repository sources reused:

- LibreWPF `def749009`, `SilkNetWpfWindowDecorationService.TryConfigureX11PopupOwner`:
  the existing transient owner, placement policy, type preference and Xlib ABI.
- ProGPU `bc635859`, `X11NativeWindowPlatform.Modal`: the existing LP64 attribute
  layout, property reader ABI, and native buffer release conventions.
- ProGPU's `NativePopupWindow`, `CocoaPopupConfiguration` and
  `Win32PopupConfiguration`: shared provider boundaries and explicit rejection.

This is original platform coordination, not a port of third-party source. The
[Xlib specification](https://www.x.org/releases/current/doc/libX11/libX11/libX11.html)
defines transient-property queries, window attributes and native property storage.
The [EWMH window-type contract](https://specifications.freedesktop.org/wm/latest-single/)
defines pre-map type publication and ordered preferences for override-redirect
surfaces. We use the native metadata contract, not it as proof of WM behavior.

Both C++ MIL and managed portable renderers use the same WPF native popup host
and this ProGPU provider. Neither renderer's scene, GPU device, target, shader,
input index, source geometry or DPI algorithm changes. There is no separate
renderer implementation to port. Complexity is constant per popup initialization;
fixed native records and two type atoms are not a compute-heavy independent-lane
kernel. No per-frame work, CPU pixel fallback or measured speed claim is added.

## Authored qualification

`X11PopupConfigurationTests` covers ordered complete setup, rejection of each
individual step, exception propagation and invalid native identity combinations.
`NativePopupWindowLinuxTests` uses an actual X server to check hidden owner/type/
override state, idempotence, untouched geometry/event masks, and rejection of
mapped or child windows. The explicit `PROGPU_NATIVE_X11_POPUP_TEST=1` lane fails
if DISPLAY cannot be opened. ProGPU's Linux build/test CI runs it under Xvfb and
uploads its TRX; ordinary test runs outside that explicit lane may skip it.
Fixtures are authored/compiled, not executed at this checkpoint.

Final build-only result: ProGPU.Tests Release compiled with 0 warnings / 0 errors
in 26.70 seconds. The WPF bridge graph compiled with 116 warnings / 0 errors in
32.65 seconds. No runtime or CI result is inferred from compilation.

The WPF source contract rejects local Xlib popup implementations and retains
hidden-disposal and no-surface-switch guards. Complete package-mode popup input,
focus, clipping, placement, close/reopen, compositor fidelity and both PRs' exact
delivery-head CI remain required. Xvfb server properties are not WM policy,
rendering or cross-platform/native-Windows comparison evidence.

Linux native dialog input suppression and Cocoa modal panel integration remain
open. Apple's [worksWhenModal contract](https://developer.apple.com/documentation/appkit/nswindow/workswhenmodal)
reserves overriding modal escape for NSPanel subclasses; existing GLFW NSWindows
are not admitted by this X11 change. Do not bypass the AppKit modal poll or
substitute an owner-drawn overlay to claim that dependency complete.
