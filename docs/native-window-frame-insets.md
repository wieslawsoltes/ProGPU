# Native window frame insets for source-sized applications

WPF `Window.Width` and `Window.Height` specify the outer top-level window. The
ProGPU WPF host presently passes those dimensions to Silk.NET as the client
size, enlarging the native frame. A same-source Windows ARM64 fixture at 200%
DPI demonstrated the discrepancy with `GetWindowRect` and `GetClientRect`:

| Renderer | Outer | Client |
| --- | --- | --- |
| Stock Windows WPF | 430 × 300 | 417 × 264 |
| ProGPU native MIL | 443 × 336 | 430 × 300 |

`SilkWindowController.FrameInsets` already queries the native platform frame;
Win32 obtains exact left/top/right/bottom from the native window and client
rectangles, while Cocoa/X11 use GLFW frame metrics. That existing provider is
the host authority. `PortableWindowActivationCallbacks.GetFrameInsets` makes a
typed, optional report available to source Window consumers, and
`PortableWindowFrameInsets` carries the four values in desktop logical units.
The host converts platform pixels to those logical units once; WPF source
measurement and outer-to-client sizing must not divide by framebuffer scale a
second time. `null` means unavailable (for example before the native window is
initialized), while `Empty` is authoritative for borderless surfaces.

This contract alone does not fix source layout. The LibreWPF consumer must use
the actual insets both when converting outer requested size to native client
size and when measuring/arranging the Window root, then validate startup,
resize, DPI, chrome changes, SizeToContent and popup surfaces. The stock/native
Win32 rectangles above are the initial ordinary-window acceptance oracle;
text line metrics alone cannot qualify outer-window sizing.
