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

## Shared window and presentation lifetime

`CocoaPopupWindow` implements the existing public Silk `IWindow` contract over
the owned surface. Its only native source is the actual Cocoa panel: `Native.Glfw`
is null. A dedicated `CocoaPopupNativeWindowPlatform` is selected before the
ordinary Cocoa/GLFW adapter. Constructing a GLFW adapter for this window throws
before loading GLFW or using any pointer. The shared controller preserves the
exact `NSPanel` handle descriptor and uses this popup's pointer-only input gate;
ordinary Cocoa windows still do not acquire full native blocking.

Initialization remains hidden. Reentrant close/disposal during creation retains
and retires the unpublished panel, and recursive initialization is rejected.
Geometry comes from the owned native snapshot. Desktop positions stay in points,
while framebuffer extents and point-to-framebuffer conversion use the actual
backing scale. Position/size/backing-size state publishes together before managed
events. A reentrant resize supersedes pending old-size events. Native view/window
identity cannot change within a live adapter.

The source owner still polls native events. Popup `DoEvents` only refreshes
geometry and offers queued input to the managed input adapter; `DoUpdate` also
does this so AppKit modal polling does not starve delivery. Neither starts another
global poll. Managed callbacks have explicit retirement depth, and a closing
adapter can finish disposal after native callbacks or render leases end.

The adapter is intentionally restricted to a nonactivating, untitled, hidden-
created, borderless NoAPI popup. It does not promise fullscreen, independent
topmost/focus, GL/Vulkan contexts, native file drops, a separate run loop, or a
source dispatcher. It reports unsupported mutations instead of applying them to
the owner's GLFW window. Source factories must supply this contract explicitly.

`WgpuContext` now acquires one owned-view lease before the existing Silk native
surface creation path, including shared-device initialization. Failed creation
releases the borrow; failed shared-surface configuration tears down the surface
before the borrow and shared-device reference. Normal disposal unconfigures and
releases the actual GPU surface before releasing its native-view lease. If final
native Hide fails, the window retains retirement ownership; a spent lease must
not prevent a subsequent context cleanup. No alternate renderer/device is created.

## Pointer context and view-owned cursors

`NativeWindowInput.CreateInput` selects the owned provider by actual adapter type
before any Silk/GLFW input selection. An owned window has one live context, one
pointer, and no fabricated keyboard: the dialog owner keeps native keyboard input.
Context attachment is an independent native input gate. Detach blocks native
pointer reception without losing the controller's latest enabled intent; attaching
a replacement context reapplies that intent and an actual default view cursor.

`INativePointerInputContext` provides the original double-coordinate event stream,
scoped event modifiers, native click counts, scroll units/phases and explicit
cancellation. Source adapters must not deliver both this stream and the equivalent
Silk mouse stream. Scroll values are not automatically wheel notches: precise
deltas are native view points, and non-precise deltas are line/row units. Source
scrolling must consume these units using actual source scroll contracts, not an
unconditional multiplication by 120 or an invented fixed row height. Original
phases remain available for source momentum-target ownership.

Managed draining uses bounded caller-owned batches and preserves all 256 queued
records in order. Hide, input blocking and close invalidate already-copied batch
tails as well as pending native records. They clear held state and emit Cancel,
never a synthetic up/click. Unmatched real up is still forwarded. Cancellation,
context disposal and native destruction are deferred across active native/input
callbacks, including source disposal reentered from the cancellation itself.
Input/handler failures block further reception, remain observable on subsequent
drains, and retain the original handler exception when cancellation also fails.
Unknown modifier bits reject before publishing a partial projection.

Standard cursors use public AppKit selectors on this owned NSView's cursor
rectangles. Diagonal frame cursors require the available public macOS API;
unsupported shapes are not reported as another shape. Hidden mode uses an owned
zero-alpha one-pixel bitmap cursor, not a process-global cursor hide counter.
Cursor rectangles are discarded before their retained cursor resources retire.
No owner GLFW cursor, global pointer warp/confinement, private AppKit selectors,
or custom-image support is assumed.

`NativePopupWindow.CreateOwnedCocoaWindow` is the explicit factory. It creates an
uninitialized adapter and rechecks the same live source owner before hidden native
initialization; neither source framework selects owned popups automatically yet.

### Source-scheduled creation before owner assignment

`NativePopupWindow.CreateCocoaPopupWindow` is the separate source-dispatcher
factory for hosts such as Forms that materialize a hidden dropdown before its
owner is known. Its required wake callback belongs to the actual source dispatcher;
it does not invent a Silk parent, poll AppKit, or adopt a foreign native window.
Initialization requires available AppKit classes and the main-thread application
contract, and allocates the same real hidden NSPanel and owned view. Geometry,
cursor/input-context creation and a render-view lease are available before binding,
but native pointer reception and Show remain blocked. The original fixed-owner
factory keeps its managed parent, wake callback and fixed-owner restriction.

`TryBindCocoaOwner` (also the typed platform's `SetParent`) binds, rebinds or clears
an initialized hidden source-scheduled popup. A retained owner lease checks the
actual application window, content view, delegate and native window number.
The popup must remain hidden, detached and outside native-session retention;
preparation does not attach a child window, since AppKit attachment can show it.
Input is disabled before native owner mutation and resumes only for a valid owner,
the existing source input intent and a live input context. Same-owner verification
is nonmutating and may run while visible. Different-owner binding is rejected
during visibility, initialization, nested transitions and native callbacks.

Changing owners does not replace the panel, view, render lease or GPU device.
A pre-owner standalone rendering context remains standalone; native ownership
does not authorize silently moving resources to the later owner's device. Source
hosts must hide before changing owners and dispose a popup after rejected or
throwing native setup. Disposal during binding cannot publish success or destroy
a renderer-leased view. Cleanup preserves the original binding failure and keeps
failed native retirement available for an explicit retry.

These factories are still explicit, unselected building blocks. Source framework
factory/input integration, real native owner lifetime and rendering qualification
below remain required; this API does not choose a wheel compatibility policy.

### Source-host retirement completion

`NativeWindowLifetime.TryDispose` requests disposal and reports whether the native
ownership has actually ended. For an owned popup it returns false during active
initialization/native/managed callbacks or while the view is renderer-leased.
The source host retains that exact `IWindow` as a retirement participant and
retries after its existing native poll and GPU-surface cleanup, on the creating
thread. It must not unregister the retirement participant merely because Dispose
returned, and must retain ownership on an exception for an explicit retry. The
helper does not poll events, spin, move callbacks, hide exceptions or release a
borrowed renderer lease. Other window providers keep their original Dispose
semantics; no native handle, property or input-provider probe identifies them.

`CocoaPopupRetirementTests` supplies twelve managed cases for both factories,
uninitialized windows, render leases, native/input/initialization callbacks,
failed-hide retry, thread ownership, null rejection and unchanged foreign-provider
behavior. They run in the existing early owned-popup group on every test RID.
Source-host retirement queue integration and actual AppKit/session/render lifetime
qualification remain required before either source factory is enabled.

## Integration still required — do not enable automatic modality

The surface implementation remains internal; the explicit factory is not selected
by either source framework. An NSPanel and passing managed transport tests are not
popup admission or application qualification. The same draft work must still connect:

1. Both source input adapters must select `NativeWindowInput` for the new window,
   consume native pointer metadata/units, and connect Cancel/Leave to actual source
   capture and interaction state. Existing keyboard-polling modifier readers and
   unconditional wheel-notch scaling do not admit native popup input. Source
   cursor services must reuse the attached context, not dispose it via a temporary
   context. Never let GLFW claim an owned panel.
2. The shared window/presentation lifetime path must be exercised with the owner's
   actual device in both renderer modes. The implementation is not native surface
   creation, rendering, device-loss or callback-retirement qualification.
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

`CocoaPopupWindowTests` adds hidden initialization, exact typed controller dispatch,
independent input intent, desktop/backing conversions, backing-scale changes,
reentrant geometry/creation/close, failed bounds and retirement, render-lease and
native-callback lifetime, owner-loop wakeup, creating-thread rejection and explicit
unsupported operations. These tests use original fake native operations and do
not load AppKit or GLFW.

`CocoaPopupInputContextTests` adds single-context admission/replacement, full 256-
record delivery, event modifiers without fake keyboards, exact native scroll
units/phases, copied-tail invalidation, unmatched up, cancellation/disconnection,
callback-retained native lifetime, overflow, persistent handler faults, unknown
modifier rejection, cursor state and deferred explicit-factory ownership checks.
These managed tests do not qualify actual NSCursor/NSPanel behavior, input routing
or application presentation.

`CocoaPopupOwnerBindingTests` adds deferred source-scheduled creation, ownerless
geometry/render leases, late binding/rebinding/clearing, fixed-owner compatibility,
visible same-owner verification, persistent disabled intent, absent/replaced input
contexts, invalid owners, rejected native gates, disposal and failed retirement,
copied input-tail cancellation, nested/thread/native-callback rejection and foreign
provider isolation. These tests use managed native-operation doubles; hosted CI,
not local runtime execution, supplies their execution evidence.
The Build workflow runs the complete managed owned-popup lifecycle/input group
immediately after test compilation on every test RID, including Windows, before
the longer GPU suites. Original full tests and all native/package gates remain.
The owner-binding backend and test-project Release compilations passed locally
with zero warnings/errors; execution and real native qualification are separate.

The backend and test-project Release builds succeeded with zero warnings and errors.
Focused managed input/window tests pass; full validation and native UI qualification
remain deferred while implementation continues. CI must
remain fully green before any eventual merge. An initial no-restore build failed
inside NuGet's stale asset reader; an ordinary forced restore rebuilt successfully
without changing package declarations or versions.

## Typed source popup admission

`NativePopupWindow.TryPrepareOwner` and `TryShowOwned` also accept the actual
`IWindow`. Owned Cocoa panels bind their retained source owner and hold their
dispatch lifetime around the source Show callback. A missing/different owner,
changed view identity, disposal or failure to remain visible cannot publish
success. Owner rebinding and nested display admission are rejected during that
callback; retirement failures cannot replace its original error. Other providers
retain the original native-handle ownership checks.

The source callback prepares rendering and then calls
`NativePopupWindow.ShowWithoutActivation`. Owned panels use their checked native
visibility; GLFW windows temporarily disable FocusOnShow using only an actual
`Native.Glfw` identity. An opaque window Handle is never a GLFW pointer. Restoration
checks the same live provider identity and preserves original callback errors.
Visibility alone is not ownership admission; callers dispose rejected setups.

Eighteen authored managed cases cover hidden binding/reopen, invalid and changed
owners, callback ordering/reentrancy, deferred disposal/render leases, original
failure preservation, changed view identity, foreign-provider rejection and
thread affinity. They join the existing early CocoaPopup CI group on all three
test RIDs and the full test suite. Backend and test-project compilation passed
with zero warnings/errors; runtime execution and real native UI qualification
remain separate. These APIs do not select source factories or scroll policy.

## Provider-owned mouse pass-through

`NativeWindowInput.SetInputTransparent` takes the actual initialized `IWindow`,
never an opaque handle. Owned panels combine independent enabled/transparency
intent with live owner and input-context admission. Enabling a transparent panel,
replacing input, rebinding an owner or reopening cannot make it receive pointers.
Blocking cancels pressed state through the existing input generation; it neither
synthesizes a release/click nor delivers a stale copied batch tail. Native rejection
is an error requiring source cleanup, not successful pass-through.

GLFW providers require an actual `Native.Glfw` identity and read back the requested
[mouse pass-through attribute](https://www.glfw.org/docs/3.4/window_guide.html#GLFW_MOUSE_PASSTHROUGH_attrib).
ProGPU's Avalonia 11 host uses this same API instead of its monitor helper's opaque
handle cast. This confirms provider configuration, not desktop click routing.
Twelve authored managed cases cover combined policies, replacement/reopen/owner
binding, cancellation, rejection, unsupported providers and lifecycle/thread
boundaries in the existing early CocoaPopup group and full test suite. Source
factory selection, precision-scroll compatibility and real native UI checks remain
separate requirements.

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
- Public SDK `NSCursor.h`, `NSBitmapImageRep.h`, `NSImage.h` and `NSView.h`
  declarations supply cursor construction and view-scoped cursor rectangles.
  The device-RGB name comes from AppKit's public exported constant, not a guessed
  private value. Scroll semantics follow the SDK and
  [Apple scrollingDeltaY](https://developer.apple.com/documentation/appkit/nsevent/scrollingdeltay).
- Existing original ProGPU `CocoaNativePopupWindow`, `CocoaPopupConfiguration`,
  `CocoaNativeWindowGeometry`, and `CocoaWindowGeometry` supply ownership and
  coordinate contracts. Original `CocoaNativeSystemMenu` supplies the existing
  process-owned Objective-C class registration/callback ABI pattern. No
  third-party implementation was copied or translated.
- The pinned Silk.NET 2.23 public interface/option declarations supply the window
  adapter contract. No foreign window/input implementation or inaccessible base
  class was copied, invoked through reflection, or used as implementation source.
