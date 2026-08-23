# Avalonia Silk.NET platform contract audit

Audit date: 2026-08-24

Supported Avalonia lanes:

- [Avalonia 12.1.1](https://www.nuget.org/packages/Avalonia/12.1.1),
  the latest stable 12.x release at audit time.
- [Avalonia 11.3.20](https://www.nuget.org/packages/Avalonia/11.3.20),
  the latest stable 11.x release at audit time.

## Incident and root cause

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
  follows the render scale. Deferred window positions are converted only after
  the native window and its scaling are known.
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
- IME composition/preedit integration;
- native menus, tray icons, mounted-volume notifications, accessibility
  bridges, and operating-system shutdown/session events;
- touch and pen input.

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

- The shared Silk.NET contract suite passes in Debug and Release against both
  exact lanes: 85 tests on Avalonia 12.1.1 and 72 tests on Avalonia 11.3.20.
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

## Managed/native applicability

The platform-settings, dispatcher, screen, window, input, and clipboard work
belongs to the managed Avalonia/Silk.NET host. Neither the managed ProGPU scene
renderer nor the native C++ renderer implements those operating-system
services, so there is no paired renderer change. The bitmap-encoder update is
also specific to Avalonia 12's managed `IBitmapImpl` contract; the native C++
renderer does not expose or consume that interface. Shared scene, shader,
resource-identity, device-loss, upload, and presentation contracts are
unchanged, so no managed/native differential fixture or canonical shader needs
to move for this fix.

No third-party implementation source was copied into ProGPU. The Avalonia and
GLFW sources were used to identify public contracts and observable platform
behavior; the implementation is original ProGPU code. The source-patch
migration is a permitted port of existing ProGPU-owned changes and records its
official base commit in the preparation script.
