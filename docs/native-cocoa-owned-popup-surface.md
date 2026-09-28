# Owned Cocoa popup surface

Application target: a LibreWPF or LibreWinForms dialog opening its own dropdown
while AppKit is running a native modal session (ProGPU #197 / LibreWPF #113).

## Implementation boundary

The existing GLFW popup is an `NSWindow`. Child-window ownership alone does not
make it eligible for AppKit modal event dispatch. `CocoaOwnedPopupSurface` instead
owns a newly allocated, nonactivating `NSPanel` and its content view. It never
changes a GLFW object's class, delegate, content view, or native pointer.

The factory stays hidden, validates the actual source owner, sets and verifies
`worksWhenModal`, and uses the existing `NativePopupWindow` preparation/show
contract. Attachment happens only at Show. The primary screen defines desktop
coordinates; backing scale is returned separately by the existing authoritative
geometry query. Negative monitor origins and fractional point coordinates remain
double precision. Invalid or overflowed rectangles are rejected.

Panel/view ownership is thread-bound. A retained renderer borrows them through one
render lease, not one lease per frame. It must dispose its presentation surface
before releasing the lease. Disposal hides immediately after any active native
transition, but native destruction waits for every lease. Reentrant disposal
during identity reads or Show cannot publish a new lease or visible success.
Failed Hide retains explicit ownership. A panel retained by a native modal session
cannot be retired before that session ends.

This is shared window lifetime code, applicable to both managed and C++ renderer
hosts. Neither renderer's algorithms, shader resources, scene contracts, device
selection, or defaults change.

## Integration still required — do not enable automatic modality

The surface is internal and not selected by either source framework. Allocation
of an NSPanel is not popup admission or application qualification. The same draft
work must still connect:

1. An owned native content-view input adapter, including pointer coordinates,
   tracking/capture, typed source ownership, safe callback retirement, and input
   suppression when a different modal source owns the active scope.
2. A presentation adapter using the owner's actual shared device and both renderer
   modes. Surface teardown must precede release of the view lease. No alternate
   renderer or independent device is an acceptable substitute.
3. The LibreWPF and LibreWinForms native-popup factories, preserving placement,
   raster DPI, transparency, keyboard routing, hide/reopen, and source lifecycle.
4. Native modal-session entry only after the source popup/release/focus contracts
   are complete, retaining the existing native-session polling and cleanup guards.

Actual Cocoa tests must demonstrate hidden creation, native panel class and flags,
same-owner attachment, geometry, nested dialog dropdown interaction, blocked-owner
input, owner move/minimize/close, disposal during callbacks, and focus restoration.
Windows-reference and Linux application gates remain independent. No screenshot,
native modal-input, rendering, package, or application parity is claimed here.

## Tests and build evidence

`CocoaOwnedPopupSurfaceTests` covers thread ownership, render leases, reentrant
close, failure-preserved ownership, geometry publication, negative/fractional
desktop mapping, and nonfinite/overflow rejection without loading AppKit. These
are lifecycle/coordinate tests, not native panel or UI tests.

The backend and test-project Release builds succeeded with zero warnings and errors. Test execution
and native UI qualification are deferred while implementation continues; CI must
remain fully green before any eventual merge. An initial no-restore build failed
inside NuGet's stale asset reader; an ordinary forced restore rebuilt successfully
without changing package declarations or versions.

## Primary contracts and provenance

- [Apple NSPanel worksWhenModal](https://developer.apple.com/documentation/appkit/nspanel/workswhenmodal):
  an actual panel can opt into modal-session input; ordinary child ownership does
  not supply this contract.
- [Apple nonactivatingPanel style](https://developer.apple.com/documentation/appkit/nswindow/stylemask-swift.struct/nonactivatingpanel):
  the style is specific to NSPanel, not a flag for reclassifying foreign windows.
- [Apple releasedWhenClosed](https://developer.apple.com/documentation/appkit/nswindow/isreleasedwhenclosed):
  explicit retain/release ownership must not be duplicated by close.
- Installed Apple SDK public `NSWindow.h` / `NSPanel.h` declarations were used for
  ABI signatures and enum values, not as implementation source.
- Existing original ProGPU `CocoaNativePopupWindow`, `CocoaPopupConfiguration`,
  `CocoaNativeWindowGeometry`, and `CocoaWindowGeometry` supply ownership and
  coordinate contracts. No third-party implementation was copied or translated.
