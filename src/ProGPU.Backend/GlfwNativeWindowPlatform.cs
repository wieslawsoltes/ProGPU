using Silk.NET.GLFW;
using Silk.NET.Windowing;

namespace ProGPU.Backend;

internal unsafe class GlfwNativeWindowPlatform : INativeWindowPlatform
{
    protected readonly IWindow Window;
    protected readonly Glfw Glfw;
    protected readonly WindowHandle* GlfwWindow;

    public GlfwNativeWindowPlatform(IWindow window)
    {
        Window = window;
        Glfw = Glfw.GetApi();
        GlfwWindow = (WindowHandle*)(window.Native?.Glfw ?? window.Handle);
    }

    public virtual NativeWindowHandle Handle => ResolveHandle(Window);
    public virtual NativeWindowCapabilities Capabilities => NativeWindowCapabilities.ForKind(Handle.Kind);
    public virtual bool RequiresManagedDecorations => false;
    public virtual NativeDrawnDecorationParts RequestedDrawnDecorations => NativeDrawnDecorationParts.None;
    public virtual NativeWindowFrameInsets FrameInsets
    {
        get
        {
            if (GlfwWindow == null)
            {
                return NativeWindowFrameInsets.Empty;
            }

            Glfw.GetWindowFrameSize(
                GlfwWindow,
                out var left,
                out var top,
                out var right,
                out var bottom);
            return new NativeWindowFrameInsets(left, top, right, bottom);
        }
    }
    public virtual double DefaultTitleBarHeight => Math.Max(0, FrameInsets.Top);
    public virtual bool SupportsManagedMove => Handle.Kind is not NativeWindowKind.Wayland;
    public virtual bool SupportsManagedResize => true;

    public virtual bool ApplyChrome(in NativeWindowState state)
    {
        if (GlfwWindow == null)
        {
            return false;
        }

        var decorated = state.Decorations != NativeWindowDecorations.None && !state.ExtendClientArea;
        var nativeResizable = RequiresNativeResizableStyle(state);
        Glfw.SetWindowAttrib(GlfwWindow, WindowAttributeSetter.Decorated, decorated);
        Glfw.SetWindowAttrib(GlfwWindow, WindowAttributeSetter.Resizable, nativeResizable);
        return true;
    }

    public virtual bool SetTopMost(bool value)
    {
        if (GlfwWindow == null)
        {
            return false;
        }

        Glfw.SetWindowAttrib(GlfwWindow, WindowAttributeSetter.Floating, value);
        return true;
    }

    public virtual bool SetEnabled(bool value) => false;
    public virtual bool SetShowInTaskbar(bool value) => false;
    public virtual bool SetParent(NativeWindowHandle parent) => false;

    public virtual bool SetSizeConstraints(NativeWindowSize minimum, NativeWindowSize maximum)
    {
        if (GlfwWindow == null)
        {
            return false;
        }

        Glfw.SetWindowSizeLimits(
            GlfwWindow,
            NormalizeMinimum(minimum.Width),
            NormalizeMinimum(minimum.Height),
            NormalizeMaximum(maximum.Width),
            NormalizeMaximum(maximum.Height));
        return true;
    }

    public virtual bool SetClientAreaExtension(bool enabled, double titleBarHeight) => true;

    public virtual bool SetTheme(NativeWindowTheme theme) => false;
    public virtual bool SetBackdrop(NativeWindowBackdrop backdrop) =>
        backdrop is NativeWindowBackdrop.None or NativeWindowBackdrop.Transparent;
    public virtual bool SetWindowShadow(bool enabled) => false;
    public virtual bool TryBeginMove(NativeWindowPoint pointer) => false;
    public virtual bool TryBeginResize(NativeResizeEdge edge, NativeWindowPoint pointer) => false;
    public virtual void Dispose()
    {
    }

    internal static NativeWindowHandle ResolveWindowHandle(IWindow window) => ResolveHandle(window);

    protected static NativeWindowHandle ResolveHandle(IWindow window)
    {
        var native = window.Native;
        if (native?.Win32 is { } win32 && win32.Hwnd != 0)
        {
            return new NativeWindowHandle(NativeWindowKind.Win32, win32.Hwnd, win32.HInstance, "HWND");
        }
        if (native?.Cocoa is { } cocoa && cocoa != 0)
        {
            return new NativeWindowHandle(NativeWindowKind.Cocoa, cocoa, 0, "NSWindow");
        }
        if (native?.X11 is { } x11 && x11.Window != 0)
        {
            return new NativeWindowHandle(NativeWindowKind.X11, (nint)x11.Window, x11.Display, "XID");
        }
        if (native?.Wayland is { } wayland && wayland.Surface != 0)
        {
            return new NativeWindowHandle(NativeWindowKind.Wayland, wayland.Surface, wayland.Display, "wl_surface");
        }
        if (native?.Glfw is { } glfw && glfw != 0)
        {
            return new NativeWindowHandle(NativeWindowKind.Glfw, glfw, 0, "GLFWwindow");
        }
        return NativeWindowHandle.Empty;
    }

    protected bool RequiresNativeResizableStyle(in NativeWindowState state) =>
        state.CanResize ||
        Window.WindowState is WindowState.Maximized or WindowState.Fullscreen;

    private static int NormalizeMinimum(int value) => value > 0 ? value : -1;
    private static int NormalizeMaximum(int value) => value > 0 && value < int.MaxValue ? value : -1;
}
