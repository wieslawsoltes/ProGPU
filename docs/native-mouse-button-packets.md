# Original native mouse-button coordinates

The original LibreWPF Windows modal pair reached MessageBox dismissal, restored
owner focus and delivered the enabled guard click. Its next physical click was
at desktop `(190,233)`, but both source button events used the previous guard's
client `(110,438)`. Only afterward did the move arrive at client `(110,153)`.
The diagnostic run is LibreWPF `38957951448`, head
`d9c940cdf4c838f46524772180f4da6ca418f9d2`. Its unchanged 60-second deadline failed;
the original ZIP digest is
`c1125a22d90586d511fd0ebc1b6026291b8a6fc3e0aa1e31173df2c7969c81ea`.

`SilkWindowController.TryGetCurrentMouseButtonPacket` exposes the original signed
client position through ProGPU's existing Win32 window procedure. It borrows
only the current same-window, creating-thread down/up callback. The immutable
value records phase and button identity so consumers can reject mismatched
callbacks before copying its position into their existing event.

The attached Win32 platform retains both the original HWND and HINSTANCE, just
like the pending GLFW identity. The first Windows CI control exposed that the
older platform descriptor discarded HINSTANCE, so the exact live-identity check
rejected its own callback. The fix preserves the complete original descriptor;
it does not relax the identity comparison. That failed run (`38959546960`,
job `117170857182`) remains retained as diagnostic evidence.

The procedure snapshots and restores the prior packet in `finally`. Every nested
message enters its own scope; a keyboard, touch, wheel, chrome or other message
cannot inherit an outer button packet. Left/right/middle and valid X1/X2 down/up
messages retain both original signed 16-bit coordinates. Nonclient, wheel,
double-click messages and invalid X-button identities are not this contract.
Unsupported providers and later, retired or wrong-thread queries return false.

No cursor sampling, extra native callback, event replay, move synthesis, focus
change, source geometry adjustment or added delay is involved. Existing promoted
touch suppression, GLFW capture/button/modifier behavior and all original message
arguments and returns remain unchanged. The scope adds constant work and value
storage per native message; it allocates no object and performs no new P/Invoke.
It is separate from the complete owned Cocoa pointer stream.

The signed coordinate and button contract comes from Microsoft's
[WM_LBUTTONDOWN](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-lbuttondown)
and [WM_XBUTTONDOWN](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-xbuttondown)
documentation. Inspection of the pinned
[Silk.NET 2.23.0 callback forwarding](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/Input/Silk.NET.Input.Glfw/GlfwEvents.cs)
and [mouse event delivery](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/Input/Silk.NET.Input.Glfw/GlfwMouse.cs)
confirms synchronous callbacks. These are behavioral references; the implementation
extends the original ProGPU-owned Win32 procedure and controller.

Deterministic controls cover all ten phase/button combinations, signed boundary
values and rejection of other coordinate frames. The native Windows control
uses an actual hidden GLFW window and Silk input context: original button
messages invoke real callbacks, nested right-button packets restore the outer
left packet, a nested keyboard callback sees no packet, and later/wrong-thread
queries are unavailable. It does not inject desktop input or qualify the original
application. Full producer Build, the rebuilt WPF consumer and unchanged physical
modal acceptance remain required before the issue is closed.
