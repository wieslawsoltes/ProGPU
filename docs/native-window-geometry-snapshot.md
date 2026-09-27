# Native window geometry snapshots

`SilkWindowController.TryGetGeometrySnapshot` reads the actual geometry of its
already-attached, live native window. It does not attach the controller, apply
desired state, show, activate, focus, move, resize, pump events or inject input.
The caller must use the controller's creating thread; Cocoa additionally requires
the AppKit main thread. Unsupported providers return `false` with a default
snapshot. This change adds Cocoa support only, not cross-platform geometry parity.

## Contract

`NativeWindowGeometrySnapshot` carries the exact `NativeWindowHandle`, borrowed
content-view identity, positive AppKit `CocoaWindowNumber`, actual content and
frame rectangles, and the current `backingScaleFactor`. Window/view identity
values grant no ownership or permission to dereference pointers. The snapshot is
an observation, not a lifetime lease or promise that subsequent native geometry
is unchanged.

Content is measured from the live content view's `bounds`, converted by
`convertRect:toView:nil` and then `NSWindow.convertRectToScreen:`. Frame is read
independently from `NSWindow.frame`. The current `NSScreen.screens[0].frame`
defines the global top-left desktop-point origin:

```text
desktopX = screenRect.X - primary.X
desktopY = primary.Y + primary.Height - (screenRect.Y + screenRect.Height)
```

Widths, heights, fractional coordinates and negative desktop origins remain
native points. Backing scale is a separate value: it never multiplies global
origins or extents. There is no inferred titlebar inset, framebuffer ratio,
`AXContents` rectangle, synthetic content origin or focus-dependent `mainScreen`.

The query verifies the pointer belongs to `NSApplication.windows` before
messaging it, temporarily retains the actual window/view, validates the view's
window and positive device number, and revalidates membership, view identity and
device number after the read. The primary screen and its frame are also
rechecked. Retired/rehosted/recreated identities, unavailable metadata and invalid
or overflowing geometry return `false/default` atomically. Managed/native-call
exceptions propagate with default output after releasing acquired temporary
leases; no partially qualified result is published. Hidden materialized owned
windows are eligible, but unopened windows without a device number are not.

`CocoaWindowNumber` is intentionally named for AppKit's `NSInteger` property.
Apple's documentation distinguishes it from a global window-server identifier.
External Core Graphics evidence must retain its own actual PID/window-number/
geometry correlation; the product does not label a cast as a proven CGWindowID.

## Original implementation and primary contracts

The implementation reuses original ProGPU host contracts in
`INativeWindowPlatform`, `SilkWindowController` and `MacOsNativeWindowPlatform`.
The retained Cocoa ownership, local-window membership and typed Objective-C
interop pattern follow `CocoaNativePopupWindow` and `CocoaNativeSystemMenu`;
`CocoaMenuRect` supplies the existing 32-byte native rectangle ABI. No foreign
implementation code, private view scan, reflection or dynamic invocation is used.

Primary contracts inspected for this implementation:

- [NSView rectangle conversion](https://developer.apple.com/documentation/appkit/nsview/convert%28_%3Ato%3A%29-3cqqt?language=objc): a nil destination converts to the view's window.
- [NSWindow screen conversion](https://developer.apple.com/documentation/appkit/nswindow/converttoscreen%28_%3A%29?language=objc): window coordinates to screen coordinates.
- [NSScreen screens](https://developer.apple.com/documentation/appkit/nsscreen/screens?language=objc): index zero is the primary screen, unlike focus-dependent `mainScreen`; the array must not be cached across display changes.
- [Backing scale](https://developer.apple.com/documentation/appkit/nswindow/backingscalefactor?language=objc): backing-store scale is not a substitute for coordinate conversion.
- [AppKit window number](https://developer.apple.com/documentation/appkit/nswindow/windownumber): device-number identity and its documented global-number distinction.
- [Apple Silicon architectural differences](https://developer.apple.com/documentation/apple-silicon/addressing-architectural-differences-in-your-macos-code): correctly typed Objective-C message calls are required.

The installed Apple SDK `NSView.h`, `NSWindow.h` and `objc/message.h` were also
inspected for the exact declarations. Supported 64-bit ARM uses ordinary typed
`objc_msgSend` rectangle returns; x64 uses `objc_msgSend_stret` for the 32-byte
rectangle, including the rectangle-conversion methods. `NSInteger` is pointer
sized and `CGFloat` is double on these supported targets. Other architectures
are unavailable, not guessed.

This is a host metadata query, not a renderer, text, layout, scene-compiler or GPU
architecture change. There is no GPU work, readback, pixel interpretation or new
rendering algorithm to compare with third-party rendering engines. Both renderer
modes consume the existing shared host controller. Managed scratch is fixed-size;
native identity membership is bounded by the application's live window list.

## Validation boundaries

`NativeWindowGeometryTests` adds 42 deterministic cases for independent frame and
content rectangles, scale-independent geometry, fractional/offset primary-screen
mapping, negative origins, invalid values and overflow, borrowed identity,
missing admission, stale view/device generations and exception-safe release with
default output. These tests exercise the original typed policy, not AppKit, a
desktop, external CG matching, package staging or rendered pixels.

Local validation of product/test commit `54b8539c0` on macOS ARM64 used SDK
10.0.201 / runtime 10.0.5. The actual complete `ProGPU.Backend.csproj` compiled
without warnings/errors, and all **42 cases passed, zero failed, zero skipped**.
A signed, isolated xUnit project linked the unchanged
`src/ProGPU.Tests/NativeWindowGeometryTests.cs` and referenced that actual backend
project, using the repository's pinned test packages. It did not substitute
product types or build/run the full `ProGPU.Tests` dependency graph. Compilation
and these deterministic policy cases do not execute AppKit geometry queries.

The scoped command, from the repository root, was:

```sh
NUGET_PACKAGES="$PWD/artifacts/window-geometry-tests/packages" \
TMPDIR="$PWD/artifacts/window-geometry-tests/tmp" \
dotnet test artifacts/window-geometry-tests/GeometryTests.csproj \
  -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false \
  -p:RestoreFallbackFolders=<existing-read-only-package-cache> \
  --logger 'trx;LogFileName=geometry.trx' \
  --results-directory artifacts/window-geometry-tests/results
```

The task-owned project, run log and TRX remain under that local `artifacts`
directory; the authoritative checked-in tests still run through the unchanged
full `ProGPU.Tests` CI gate. Diff checks passed. The repository docs verifier
stopped at the fresh worktree's missing `external/ACadSharp` submodule project;
it did not pass. Actual macOS ARM64 and x64 interop execution, installed-package
consumption and native popup desktop acceptance remain separate required
qualification. Existing independent Windows/X11 native geometry probes are not
replaced or qualified by this Cocoa seam.
