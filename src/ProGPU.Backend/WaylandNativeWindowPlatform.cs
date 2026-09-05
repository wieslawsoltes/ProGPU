using Silk.NET.Windowing;

namespace ProGPU.Backend;

internal sealed class WaylandNativeWindowPlatform : GlfwNativeWindowPlatform
{
    private readonly NativeWindowHandle _handle;

    public WaylandNativeWindowPlatform(IWindow window, nint display, nint surface)
        : base(window)
    {
        _handle = new NativeWindowHandle(NativeWindowKind.Wayland, surface, display, "wl_surface");
    }

    public override NativeWindowHandle Handle => _handle;
    public override NativeWindowCapabilities Capabilities => NativeWindowCapabilities.ForKind(NativeWindowKind.Wayland);
    public override bool RequiresManagedDecorations => true;
    public override NativeDrawnDecorationParts RequestedDrawnDecorations =>
        NativeDrawnDecorationParts.TitleBar |
        NativeDrawnDecorationParts.Border |
        NativeDrawnDecorationParts.ResizeGrips |
        NativeDrawnDecorationParts.Shadow;
    public override bool SupportsManagedMove => false;
    public override bool SupportsManagedResize => true;
    public override bool SetOpacity(double value) => value == 1d;
    public override bool SetClientAreaExtension(bool enabled, double titleBarHeight) => true;
    public override bool SetBackdrop(NativeWindowBackdrop backdrop) =>
        backdrop is NativeWindowBackdrop.None or NativeWindowBackdrop.Transparent;
}
