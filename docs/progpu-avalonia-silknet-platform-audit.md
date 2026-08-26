# Avalonia Silk.NET platform contract audit

Audit date: 2026-08-25

Supported Avalonia lanes:

- [Avalonia 12.1.1](https://www.nuget.org/packages/Avalonia/12.1.1),
  the latest stable 12.x release at audit time.
- [Avalonia 11.3.20](https://www.nuget.org/packages/Avalonia/11.3.20),
  the latest stable 11.x release at audit time.

## Incident and root cause

### Windows DPI, interactive resize, and custom title bar

On Windows, GLFW reports both the client size and framebuffer size in physical
pixels while its content scale describes the conversion from Avalonia logical
units to those pixels. The Silk.NET host previously exposed the native client
size as logical units and then multiplied it by the display scale again for
framebuffer allocation. It also sent logical resize requests directly to the
native pixel-sized window. This produced oversized layout, incorrect pointer
coordinates, constraints, and frame insets at non-100% Windows display scales.

The corrected Windows boundary now converts exactly once in each direction:
native client pixels and pointer coordinates are divided by `DesktopScaling`
when entering Avalonia, while logical client sizes and constraints are
multiplied when entering GLFW. These conversions are deliberately gated to
Win32. GLFW already reports logical screen coordinates for macOS and scaled
X11/Wayland desktops, so applying the Win32 conversion there would scale
layout, input, and constraints twice. Desktop positions remain physical
`PixelPoint` values, and the actual GLFW framebuffer remains authoritative.
Each conversion is bounded `O(1)` arithmetic with no retained allocation or
renderer-boundary crossing.

Windows enters a modal operating-system sizing loop between
[`WM_ENTERSIZEMOVE`](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-entersizemove)
and `WM_EXITSIZEMOVE`. That loop prevents the normal outer GLFW render cadence
from advancing. The Win32 adapter now keeps its window procedure installed for
both extended-client and ordinary system-chrome windows, tracks the modal
interval, and synchronously pulses Avalonia layout and presentation only while
that interval is active. Normal frame scheduling is unchanged.

Custom title-bar dragging previously sent `WM_NCLBUTTONDOWN` synchronously
with an empty coordinate. The corrected path releases Avalonia pointer capture,
lets the routed press unwind through a send-priority dispatcher post, and sends
the signed screen pointer packed in `lParam`, as required by the official
[`WM_NCLBUTTONDOWN`](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nclbuttondown)
contract. This matches the ordering used by Avalonia's official
[`WindowImpl.BeginMoveDrag`](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Windows/Avalonia.Win32/WindowImpl.cs)
without copying its implementation.

GLFW's official [window guide](https://www.glfw.org/docs/3.4/window_guide.html)
was used as the coordinate-space authority. Its distinction between content
scale, screen coordinates, and framebuffer pixels was adopted. The prior
derived framebuffer enlargement and synchronous zero-coordinate title-bar
message were rejected because they conflict with the public platform
contracts.

### Platform settings

`DataGridColumnHeader.ProcessSort` asks
`KeyboardHelper.GetPlatformCtrlOrCmdKeyModifier` for the target platform's
command modifier. The Silk.NET platform had registered a keyboard device but
had not registered `IPlatformSettings`. The lookup therefore produced no
settings object and sorting a DataGrid column failed with a null reference on
the dispatcher.

The fix registers one `PlatformHotkeyConfiguration`, exposes that exact object
through `DefaultPlatformSettings`, and also registers `KeyGestureFormatInfo`.
macOS uses Meta/Command and Command+Left/Right line navigation, Windows adds
Shift+F10 for context menus, and other desktop platforms use Control plus a
Super meta-key label.

### Complete routed input bridge

Silk.NET's high-level keyboard callback omitted GLFW repeat events and exposed
text as UTF-16 `char` values, which cannot represent supplementary Unicode
scalars. The host now chains the native GLFW key, character, and cursor-enter
callbacks after Silk.NET has installed its input context. The native modifier
bitmask is used while a key callback is active, repeats are emitted as
additional Avalonia key-down events, GLFW Unicode scalar values are converted
without truncation, and key symbols come from `glfwGetKeyName`. Pointer exit is
raised from GLFW's authoritative cursor-enter callback. Left, right, middle,
X1, and X2 buttons and both wheel axes retain their O(1), allocation-free
routing path. This follows GLFW's public
[input contract](https://www.glfw.org/docs/3.4/input_guide.html); the previous
poll-only modifier and BMP-only character assumptions were rejected.

Windows touch uses the documented
[`WM_TOUCH`](https://learn.microsoft.com/en-us/windows/win32/wintouch/wm-touchdown)
contact packet and hundredths-of-a-pixel coordinate contract. The Win32
adapter registers each GLFW HWND, closes every acquired touch handle, converts
screen contacts to client coordinates, and emits stable Avalonia touch IDs.
Compatibility mouse messages carrying Microsoft's touch signature are
suppressed only while their native message is dispatched, preventing one
finger from producing both touch and mouse routed events.

Linux/X11 touch selects XI2.2 touch begin/update/end events on GLFW's existing
display connection. A process-local pump removes only matching generic touch
cookies before GLFW drains the shared Xlib queue, dispatches them to their
owning X11 window, and suppresses XI2 pointer-emulation mouse events for the
same poll. The design follows the X.Org
[XI2 protocol](https://www.x.org/releases/current/doc/inputproto/XI2proto.txt)
and adds no competing display connection or background thread.

macOS desktop hardware exposes indirect trackpad gestures rather than
touchscreen contacts. The backend adds the public AppKit responder methods
[`magnify(with:)`](https://developer.apple.com/documentation/appkit/nsresponder/magnify(with:)),
[`rotate(with:)`](https://developer.apple.com/documentation/appkit/nsresponder/rotate(with:)),
and [`swipe(with:)`](https://developer.apple.com/documentation/appkit/nsresponder/swipe(with:))
to GLFW's process-local content-view class when absent and routes their deltas
as Avalonia magnify, rotate, and swipe events. Handler lookup is bounded and
the unmanaged entry points allocate no delegate per gesture.

## Initialization comparison

The comparison used the official 12.1.1 and 11.3.20 implementations for
[Avalonia.Native](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Avalonia.Native/AvaloniaNativePlatform.cs),
[Win32](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Windows/Avalonia.Win32/Win32Platform.cs), and
[X11](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Avalonia.X11/X11Platform.cs).
The 11.x comparison used the corresponding files at the
[11.3.20 tag](https://github.com/AvaloniaUI/Avalonia/tree/11.3.20).

| Contract | Native | Win32 | X11 | Silk.NET result |
| --- | --- | --- | --- | --- |
| Dispatcher | Native dispatcher | Win32 dispatcher | X11/GLib dispatcher | One UI-thread GLFW event loop; initialized before the compositor |
| Render scheduling | Native timer/render loop | UI or sleep timer | UI or sleep timer | Foreground timer and render loop; monitor changes update cadence unless an explicit diagnostic FPS is configured |
| Windowing and compositor | Registered | Registered | Registered | Both registered; every top level receives the same compositor |
| Keyboard and hotkeys | Command conventions | Windows conventions | Control/Super conventions | Platform-selected configuration plus key-gesture formatting |
| Platform settings | Native live settings | Win32 live settings | DBus live settings | Safe `DefaultPlatformSettings`; no false claim of live theme/accent notifications |
| Clipboard | Platform implementation and facade | Platform implementation and facade | Platform implementation and facade | One text-capable facade registered globally and exposed by every top level |
| Screens | Global native service | Global Win32 service | Platform-owned service | Per-top-level `IScreenImpl` backed by one process monitor provider |
| Cursor and icons | Registered | Registered | Registered | Registered |
| Platform graphics | Optional platform GPU API | Optional platform GPU API | Optional platform GPU API | Deliberately absent; ProGPU supplies `IPlatformRenderInterface` and owns WebGPU presentation |

Avalonia 12 initializes `Dispatcher` and an `IRenderLoop`; Avalonia 11 binds
`IDispatcherImpl` and `IRenderTimer`. The shared implementation follows those
two distinct contracts with compile-time conditionals. It does not use runtime
reflection or assembly probing.

## Required interface inventory

| Surface | Version-specific contract | Silk.NET implementation |
| --- | --- | --- |
| `IWindowingPlatform` | V12 owns platform z-order; V11 requests it from each window | Native windows/popups, explicit unsupported embedding/tray results, stable topmost-aware z-order |
| `ITopLevelImpl` | V12 uses typed render surfaces and nullable frame theme; V11 uses object surfaces and a non-null theme | Physical framebuffer surface, shared compositor, DPI transforms, paint/input/lifetime callbacks, resolved frame theme |
| `IWindowBaseImpl` | Same required lifecycle, position, activation, sizing, and topmost surface | Lazy native creation, show/hide/activate, frame position, max-auto-size, constraints, enabled state, z-order |
| `IWindowImpl` | V12 adds usable-state, requested-decoration, allowed-action, and reasoned resize members; V11 adds chrome hints and per-window z-order | State/action callbacks, reason-preserving resize, native/managed decoration policy, parent/taskbar/icon/chrome/state operations |
| `IPopupImpl` | V11 additionally requires hit-test visibility | Managed popup positioning, focus, shadow hint, and GLFW mouse passthrough in V11 |
| `IScreenImpl` | Same query/detail/change contract | Process monitor inventory, pre-show availability, stable handles, bounds/work area/scaling/primary/change notifications |
| Dispatcher/render timer | V12 dispatcher initialization plus `IRenderLoop`; V11 locator-bound dispatcher and timer | One GLFW UI/native loop and one foreground render timer with bounded refresh-rate updates |
| Keyboard/settings/clipboard | Both require platform conventions; V12 clipboard uses data-transfer ownership | Registered keyboard, settings, hotkeys, gesture formatting, and one ownership-correct clipboard facade |

Compilation against both exact private API surfaces proves that no abstract
member is omitted. The focused behavioral tests cover the members whose
contract cannot be established by compilation alone.

## Window, display, input, and clipboard findings

- `DesktopScaling` follows the native backend coordinate contract: it is 1 on
  macOS while `RenderScaling` may be 2 or greater; Win32/X11 desktop scaling
  follows the render scale. Avalonia client dimensions, input points, frame
  insets, and constraints are logical. GLFW client dimensions and pointer
  coordinates require physical-to-logical conversion only on Win32; macOS and
  scaled X11/Wayland already expose screen-coordinate units. Desktop positions
  remain physical pixels. Deferred window sizes are converted only after the
  native window and its scaling are known.
- Framebuffer storage uses physical framebuffer pixels. `FrameSize` is unknown
  until native frame insets are available and then includes those insets rather
  than incorrectly returning the client size.
- GLFW work areas are not treated as full monitor bounds. Full bounds, working
  areas, stable native handles, primary status, content scale, and refresh rate
  are read from the GLFW monitor API. Screen lookup uses exclusive right/bottom
  edges and maximum intersection, returning null when there is no match.
- The GLFW monitor callback invalidates screen snapshots and updates the render
  cadence. It is attached when the windowing platform is created, after GLFW
  initialization, so `MaxAutoSizeHint` and `Screens` work before the first
  native window is shown. The previous process callback is chained.
- GLFW resize callbacks do not include Avalonia's resize reason. A bounded,
  allocation-free tracker correlates the expected native size with the next
  callback, preserving application, layout, and DPI reasons without allowing
  a later unrelated user resize to inherit stale state.
- Win32 modal move/resize state is tracked from the native message stream for
  both managed custom chrome and ordinary system chrome. Each interactive size
  notification performs one immediate layout/render pulse so content follows
  the window edge; steady-state and programmatic resize scheduling retain the
  normal event-loop path.
- Managed title-bar moves release pointer capture and are deferred until the
  routed press has unwound. The native non-client message carries the actual
  signed screen coordinate, including negative multi-monitor coordinates.
- An unspecified Avalonia 12 frame theme now resolves through registered
  platform settings, as Avalonia.Native and Win32 do. Native backend defaults
  remain correct for direct controller users: Cocoa clears the explicit
  appearance, Win32 reads the current app-theme preference, and X11 removes
  `_GTK_THEME_VARIANT` instead of forcing a light frame.
- Pointer exit is derived from GLFW's authoritative `Hovered` window attribute,
  preventing stale Avalonia pointer-over state. Disabled windows invoke
  `GotInputWhenDisabled` rather than silently discarding the notification.
- Avalonia 11.3.20's popup input-transparency contract is implemented with
  GLFW 3.4 `GLFW_MOUSE_PASSTHROUGH`. The constant is used explicitly because
  Silk.NET 2.23 ships GLFW 3.4 while its generated setter enum predates that
  entry.
- Avalonia 11's legacy extended-client-area chrome hints are preserved in the
  shared native-window state. `NoChrome`, explicit system chrome, managed
  fallback chrome, and macOS's thick toolbar title bar have distinct behavior;
  the default remains managed fallback on Win32/X11 and native chrome on
  Cocoa. Avalonia 12 expresses the same decision through requested drawn
  decoration parts instead of this legacy setter.
- Z-order is stable across the supplied Avalonia windows and reserves a tier
  for topmost windows. Unknown implementations receive the lowest value.
- Clipboard ownership is preserved when the same transfer is assigned twice,
  replaced transfers are disposed once, empty text remains representable, and
  replacing text with a non-text in-process transfer clears the native text
  projection.
- The event loop reports that it cannot query pending input. GLFW offers event
  polling but no non-destructive pending-input query, so claiming otherwise
  would let the dispatcher prioritize background work ahead of queued input.

The GLFW behavior was checked against its public
[window](https://www.glfw.org/docs/3.4/window_guide.html),
[monitor](https://www.glfw.org/docs/3.4/monitor_guide.html), and
[input](https://www.glfw.org/docs/3.4/input_guide.html) contracts.
The V11 macOS thick-title-bar implementation uses AppKit's documented
[`NSWindow.toolbar`](https://developer.apple.com/documentation/appkit/nswindow/toolbar)
and [`NSToolbar`](https://developer.apple.com/documentation/appkit/nstoolbar)
contracts.

## Optional capabilities

The backend implements every required member of `IWindowingPlatform`,
`IWindowImpl`, `ITopLevelImpl`, `IPopupImpl`, `IScreenImpl`, the dispatcher,
and the Avalonia 11/12 render-timer contracts. Features that GLFW/Silk.NET does
not provide are not advertised:

- embeddable top levels;
- native drag sources and drop protocols;
- platform storage pickers and launcher integration;
- IME composition/preedit integration, which GLFW does not expose;
- native menus, tray icons, mounted-volume notifications, accessibility
  bridges, and operating-system shutdown/session events;
- pen-specific pressure, tilt, barrel-button, and eraser input.

Avalonia supplies its documented no-op storage and launcher fallbacks when
those optional features are absent. Embedding throws `NotSupportedException`,
and tray creation returns null, matching their interface contracts. These are
capability differences from Avalonia.Native, Win32, and X11, not partially
implemented services.

## Avalonia version migration

All normal Avalonia package references move together to 12.1.1. The shared
V11 projects pin exactly 11.3.20. Avalonia 12.1.1 changed `IBitmapImpl.Save` to
accept typed PNG/JPEG encoder options; the ProGPU managed Avalonia adapter now
implements that contract and preserves the 11.x overload in the V11 build.
This adapter-only change is not applicable to the native C++ renderer, which
does not implement Avalonia's managed bitmap interface.

The ProGPU-owned exact-source patch lane was cleanly ported from the in-repo
12.0.5 implementation to the official 12.1.1 tag. Upstream's new render-data
stream remains authoritative; ProGPU adds only its retained identity/revision
contract. Upstream ControlCatalog's new typed lazy page factories replace the
older ProGPU deferred-page host. The 12.1.1 patched Avalonia package build
completed with zero warnings and zero errors.

## Validation evidence

- The shared Silk.NET contract suite passes in Release against both exact
  lanes: 115 tests on Avalonia 12.1.1 and 102 tests on Avalonia 11.3.20. The new
  coverage includes Windows logical/native client-size conversion, one-to-one
  Windows framebuffer sizing, scaled frame insets and constraints, scaled
  Windows pointer input, unchanged Linux screen-coordinate layout/input,
  UTF-32 text, GLFW modifier mapping, touch phases, X11 ABI layout, macOS
  gesture mapping, and Windows promoted-mouse suppression.
- Both windowing projects build in Release, and the focused backend windowing
  presenter suite passes 17 tests.
- Package validation produced both
  `ProGPU.Avalonia.SilkNet.12.1.1-preview.59.nupkg` and
  `ProGPU.Avalonia.SilkNet.11.3.20-preview.59.nupkg`; their nuspec dependencies
  pin Avalonia exactly to 12.1.1 and 11.3.20 respectively.
- The runtime-reflection audit passes for both `Avalonia.SilkNet.dll`
  variants.
- A package-backed Release smoke run on macOS rendered the Charting sample
  through `ProGPU/Silk.NET + embedded ProGPU`, presented non-transparent output
  through the same-device WebGPU texture path at 2x DPI, and exited normally.
- A self-contained Windows ARM64 harness ran in the logged-in Parallels
  Windows 11 session at 200% display scaling. Live routed telemetry passed
  keyboard down/up, complete text, Control+Shift+K command routing, pointer
  movement, two-axis-capable wheel routing, and left/right/middle/X1/X2
  buttons. The window reported a 720x480 logical client, 1440x960 physical
  rectangle, and `RenderScaling=2`. A custom-title-bar drag moved the native
  rectangle, and one edge drag produced four client-size notifications and
  three matching Avalonia layout-size observations before release, proving
  layout continued inside the modal resize loop.
- The same Windows VM rendered the ControlCatalog Canvas and text-heavy
  TextBox pages through Avalonia Win32 plus native Dawn/D3D12 at 1024x800
  logical and 2048x1600 physical pixels. The older Silk.NET 2.23
  `wgpu_native.dll` presentation lane loses D3D12 resources under the
  Parallels display adapter during the larger catalog workload. Native
  Win32/Dawn succeeds on the same scene/text workload, while the Skia-rendered
  Silk.NET harness completes all windowing and input checks; this isolates the
  remaining VM-specific failure from the corrected Silk.NET window/input
  boundary rather than hiding it as a DPI or layout failure.
- An Ubuntu X11 VM passed live keyboard, text, pointer, wheel, shortcut, and
  all five mouse-button telemetry. XI2 touch ABI and conversion are covered by
  focused contracts because the VM has no injectable physical multitouch
  device. The rebuilt macOS app passed live keyboard, text, pointer movement,
  and Command+Shift+K telemetry; AppKit gesture entry points are covered by
  native mapping contracts because desktop automation cannot synthesize a
  hardware trackpad gesture at GLFW's responder boundary.
- Native Avalonia windowing with Dawn/Metal on macOS passed Border, Canvas,
  ScrollViewer, TextBlock, Viewbox, and AdornerLayer at 1024x800 logical and
  2048x1600 physical output. Typed clip, inherited drawing-option, and adorner
  synchronization gates all passed without a measured full-scene fallback;
  representative screenshots were visually correct.

## Managed/native applicability

The DPI conversion, modal resize pulse, custom title-bar ordering,
platform-settings, dispatcher, screen, window, input, and clipboard work
belongs to the managed Avalonia/Silk.NET host. Neither renderer implements
those operating-system services, so there is no paired renderer change. The
bitmap-encoder update is likewise specific to Avalonia 12's managed
`IBitmapImpl` contract.

The retained glyph-residency correction found during ControlCatalog
integration is specific to the managed compositor's LRU glyph atlas plus
incremental-page/picture replay. The native C++ renderer builds an immutable
scene-owned positioned-glyph atlas, grows that atlas instead of recycling an
LRU slot behind retained UVs, and has no equivalent Avalonia incremental-page
cache. Its applicability audit therefore found no matching defect or native
code change. Both implementations retain the same shaping, glyph identity,
DPI, and output contracts; no C ABI record or canonical shader changed.

No third-party implementation source was copied into ProGPU. The Avalonia and
GLFW sources were used to identify public contracts and observable platform
behavior; the implementation is original ProGPU code. The source-patch
migration is a permitted port of existing ProGPU-owned changes and records its
official base commit in the preparation script.
