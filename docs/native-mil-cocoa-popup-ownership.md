# Checked Cocoa popup ownership

## Core acceptance dependency

The LibreWPF Showcase's main menu and ComboBoxes use separately surfaced native Cocoa
popups. Configuration previously called addChildWindow directly in WPF and could
report success without checking the resulting owner. The host ignored rejected
configuration outside Windows and could proceed to Show. The bounded outcome is
checked ProGPU-owned parent setup before showing the real hidden popup, with
rejection disposing that surface rather than showing it unowned.

This is source-backed implementation work. It does not implement AppKit modal
popup admission. The Showcase About view currently contains text and an OK button,
not a popup control; do not claim a modal-popup acceptance case from that view.

## Shared native boundary

`NativePopupWindow.TryConfigureOwner` dispatches matching Win32 or Cocoa identities.
Unsupported/mismatched/invalid handles are rejected. The existing Win32 behavior
is unchanged. Cocoa requires the AppKit main thread, ARM64/x64 ABI, both actual
windows in NSApplication's window list, non-null content views and a hidden popup.
Borrowed pointer identity is checked against the native list before messaging it.

The synchronous operation retains owner/popup windows, content views, delegates
and the previous parent while AppKit can invoke callbacks. It validates their
current host identity before and after mutation. The owner ancestry is checked
for cycles, with a 1024-link malformed-chain bound. It captures the previous
parent and hides-on-deactivate flag, detaches an old parent when necessary, adds
the real child above its owner and disables hiding solely on deactivation. It
checks resulting parent, hidden state, flag and host identity before success.
No coordinate conversion, focus request, Show, event polling or class mutation
occurs in this operation. Existing WPF placement is reapplied before native Show.

Rejected mutation attempts to restore captured ownership/flags only while host
identity remains current and the popup remains hidden. It does not overwrite a
third party's reentrant parent change. Restoration is not a success guarantee:
every failed/throwing setup requires the caller to destroy the rejected popup,
including partial or interrupted restoration. Exceptions are not converted into
an ordinary event-loop or renderer fallback. Native references are released when
the synchronous operation ends; normal host/window ownership remains unchanged.

The generic policy is `CocoaPopupConfiguration`; `CocoaNativePopupWindow` supplies
AppKit ABI calls through source-generated LibraryImport, byte BOOL values and
pointer-sized ordering values. LibreWPF adapts real Silk native handles into this
shared provider and removes its local Cocoa ownership routine/unused imports.
Selected native popup configuration must succeed on every platform before Show;
this also makes existing X11 rejection explicit, without rewriting the X11 adapter.

## Modality is still open

Apple specifies that only NSPanel subclasses should override the default
worksWhenModal behavior. Existing GLFW NSWindows cannot be promoted into valid
modal popup hosts merely by changing that property or their Objective-C class.
This change does neither. Automatic AppKit ShowDialog sessions remain guarded.
Completing that path needs genuine NSPanel surface/input/lifetime integration,
not owner-surface substitution or bypassing runModalSession. Source gate-release
and deferred native End ordering remain independent existing contracts.

## Applicability, cost and provenance

Both managed portable and native MIL renderers use the same host/configuration
path. No C++ scene/render algorithm, shader, GPU resource or wire record changes.
The work belongs in ProGPU's existing native platform adapter; source WPF keeps
its own visual and input policy. Existing original `CocoaNativeModalSession` and
`Win32PopupConfiguration` provide in-repository ABI/policy patterns. No foreign
implementation code is copied.

Admission is O(W + D) for W native windows and owner depth D, with constant managed
workspace and bounded native retains. Reentrant native callbacks have their own
cost. Calls are setup-only, not per frame or input item. Lifetime/identity/ancestry
work is dependent control flow, not an independent-lane SIMD/GPU kernel. There is
no pixel readback, new managed/native rendering crossing or performance claim.

Authored policy fixtures cover success, repeat configuration, initial visibility,
identity/cycle rejection, failed parent/flag publication, restoration, reentrant
identity/visibility/parent changes and callback exception propagation. WPF source
fixtures protect shared Cocoa dispatch and rejection-before-show on all platforms.
These fixtures must execute after feature freeze. Actual Cocoa main-thread native
calls, visible menu/ComboBox placement, click/focus behavior, close/reopen and
native retain release still require package/application qualification.

## Primary contracts consulted

- [NSWindow worksWhenModal](https://developer.apple.com/documentation/appkit/nswindow/workswhenmodal)
  and [modal-session polling](https://developer.apple.com/documentation/appkit/nsapplication/runmodalsession(_:)):
  retain the NSPanel restriction; checked child ownership alone is not admission.
- [Child window ordering](https://developer.apple.com/documentation/appkit/nswindow/addchildwindow(_:ordered:))
  and [deactivation visibility](https://developer.apple.com/documentation/appkit/nswindow/hidesondeactivate):
  preserve native parent/child identity and explicit flag ownership, not fake WPF
  handles or inferred activation. Apple Markdown documentation was used where the
  HTML pages required JavaScript.
