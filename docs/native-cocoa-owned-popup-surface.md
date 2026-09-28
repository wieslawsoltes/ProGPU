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

## Owned native input

The factory now allocates original ProGPU-owned subclasses of `NSPanel` and
`NSView`. These are new objects, never replacement classes for borrowed GLFW
objects. The panel cannot become key or main and the view cannot become first
responder: source keyboard routing remains with the actual dialog owner. The
view is flipped, and AppKit converts event positions into that view's native
point coordinates rather than deriving them from screen or framebuffer ratios.

An owned tracking area follows the visible rectangle, including during drags.
Its pointer callbacks capture move, drag, down/up, enter/leave, scroll and native
cancellation records. Button identity and click count come from the actual
down/up/drag event, not the current global button state (which can be newer than
a queued event). Scroll records preserve native precise-point versus line units,
direction, phase and momentum phase. No second natural-scroll inversion occurs.

Reverse callbacks never call application handlers. A main-thread registration
routes each owned view to a bounded 256-record queue. Host drains copy at most two
contiguous spans into caller storage, outside native callbacks. No motion or button
edge is coalesced/dropped. Invalid input, native callback exceptions or capacity
exhaustion fault the queue before any remaining records are published; caller
tails remain untouched. The failure cannot be cleared by hide/reopen or reenable.

Hidden creation starts with native pointer input disabled. `SetInputAllowed`
verifies the owned panel's actual `ignoresMouseEvents` state before enabling queued
delivery, and disables queued delivery before a blocking native request. This
is an input gate for an owned non-key popup, not an enabled-state claim for an
ordinary Cocoa window. Visibility and input admission are independent. Hide,
blocking and close invalidate pending records and advance a generation; the
host's input adapter must reset its held-button/capture state on a new generation.

Disposal closes input immediately, including while render-view leases remain.
An active reverse callback also retains native lifetime: the host must keep a
closing surface and call `TryCompleteDispose` after native polling returns. The
callback does not destroy the window on its way out. Native callback providers
must be noncollectible; registration never adopts another module's Objective-C
classes. Tracking areas and per-view registrations are removed before releasing
native view ownership.

## Integration still required — do not enable automatic modality

The surface is internal and not selected by either source framework. Allocation
of an NSPanel is not popup admission or application qualification. The same draft
work must still connect:

1. A shared owned-window/input/platform adapter for the host's Silk contracts,
   consuming the new pointer records and generation, preserving typed source
   ownership/capture, and completing deferred retirement after native polling.
   Do not pass an NSPanel pointer to a GLFW operation or duplicate a source host's
   renderer just to create its surface.
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

`CocoaPopupInputQueueTests` covers all native pointer kind mappings, exact full
capacity, ring wrap and partial drains, untouched caller tails, precise scroll
metadata, invalid events, sticky failures, visibility/policy generations,
thread ownership and the prohibition on input delivery inside native callbacks.
The surface tests also exercise input-gate ordering, callback-deferred retirement,
and last-lease release while a native callback remains active.

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
- [Apple precise scroll deltas](https://developer.apple.com/documentation/appkit/nsevent/hasprecisescrollingdeltas)
  and [scrollingDeltaY](https://developer.apple.com/documentation/appkit/nsevent/scrollingdeltay):
  retain the event's native point/line distinction and already-resolved direction.
- Installed Apple SDK public `NSWindow.h`, `NSPanel.h`, `NSView.h`, `NSResponder.h`,
  `NSEvent.h` and `NSTrackingArea.h` declarations were used for
  ABI signatures and enum values, not as implementation source.
- Existing original ProGPU `CocoaNativePopupWindow`, `CocoaPopupConfiguration`,
  `CocoaNativeWindowGeometry`, and `CocoaWindowGeometry` supply ownership and
  coordinate contracts. Original `CocoaNativeSystemMenu` supplies the existing
  process-owned Objective-C class registration/callback ABI pattern. No
  third-party implementation was copied or translated.
