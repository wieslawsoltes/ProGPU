using System.Runtime.InteropServices;

namespace ProGPU.Backend;

internal sealed unsafe partial class X11NativeWindowPlatform
{
    // Borrow only the actual loaded source window. Never select a server or
    // screen from DISPLAY, WAYLAND_DISPLAY, a default connection or a pointer.
    internal static bool TryGetSourceScreen(nint display, nuint window, out int screen)
    {
        screen = -1;
        if (display == 0 || window == 0 ||
            XGetWindowAttributes(display, window, out var attributes) == 0 ||
            attributes.Screen == 0 || XDisplayOfScreen(attributes.Screen) != display)
            return false;
        screen = XScreenNumberOfScreen(attributes.Screen);
        return screen >= 0;
    }

    [LibraryImport(X11Library)]
    private static partial nint XDisplayOfScreen(nint screen);

    [LibraryImport(X11Library)]
    private static partial int XScreenNumberOfScreen(nint screen);
}
