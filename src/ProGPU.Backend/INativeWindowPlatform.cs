using Silk.NET.Windowing;

namespace ProGPU.Backend;

internal interface INativeWindowPlatform : IDisposable
{
    NativeWindowHandle Handle { get; }
    NativeWindowCapabilities Capabilities { get; }
    bool RequiresManagedDecorations { get; }
    NativeDrawnDecorationParts RequestedDrawnDecorations { get; }
    NativeWindowFrameInsets FrameInsets { get; }
    double DefaultTitleBarHeight { get; }
    bool SupportsManagedMove { get; }
    bool SupportsManagedResize { get; }
    bool SupportsSystemChromeExtension { get; }

    bool ApplyChrome(in NativeWindowState state);
    bool SetTopMost(bool value);
    bool SetEnabled(bool value);
    bool SetOpacity(double value);
    bool SetShowInTaskbar(bool value);
    bool SetParent(NativeWindowHandle parent);
    bool SetSizeConstraints(NativeWindowSize minimum, NativeWindowSize maximum);
    bool SetClientAreaExtension(bool enabled, double titleBarHeight);
    bool SetTheme(NativeWindowTheme theme);
    bool SetBackdrop(NativeWindowBackdrop backdrop);
    bool SetWindowShadow(bool enabled);
    bool TryBeginMove(NativeWindowPoint pointer);
    bool TryBeginResize(NativeResizeEdge edge, NativeWindowPoint pointer);
}

internal static class NativeWindowPlatformFactory
{
    public static INativeWindowPlatform Create(IWindow window)
    {
        if (OperatingSystem.IsWindows() && window.Native?.Win32 is { } win32 && win32.Hwnd != 0)
        {
            return new Win32NativeWindowPlatform(window, win32.Hwnd);
        }

        if (OperatingSystem.IsMacOS() && window.Native?.Cocoa is { } cocoa && cocoa != 0)
        {
            return new MacOsNativeWindowPlatform(window, cocoa);
        }

        if (OperatingSystem.IsLinux() && window.Native?.X11 is { } x11 && x11.Window != 0)
        {
            return new X11NativeWindowPlatform(window, x11.Display, x11.Window);
        }

        if (OperatingSystem.IsLinux() && window.Native?.Wayland is { } wayland && wayland.Surface != 0)
        {
            return new WaylandNativeWindowPlatform(window, wayland.Display, wayland.Surface);
        }

        return new GlfwNativeWindowPlatform(window);
    }
}
