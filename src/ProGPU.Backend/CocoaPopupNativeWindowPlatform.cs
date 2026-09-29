namespace ProGPU.Backend;

// Deliberately does not derive from GlfwNativeWindowPlatform. Its generic
// window.Handle fallback would otherwise reinterpret an NSPanel as GLFWwindow*.
internal sealed class CocoaPopupNativeWindowPlatform(CocoaPopupWindow window) : INativeWindowPlatform
{
    public NativeWindowHandle Handle => window.NativeHandle;
    public NativeWindowCapabilities Capabilities => new(NativeWindowKind.Cocoa,
        window.TransparentFramebuffer ? NativeWindowFeatures.Transparent : NativeWindowFeatures.None);
    public bool RequiresManagedDecorations => false;
    public NativeDrawnDecorationParts RequestedDrawnDecorations => NativeDrawnDecorationParts.None;
    public NativeWindowFrameInsets FrameInsets => NativeWindowFrameInsets.Empty;
    public double DefaultTitleBarHeight => 0;
    public bool SupportsManagedMove => false;
    public bool SupportsManagedResize => false;
    public bool SupportsSystemChromeExtension => false;
    public bool IsInteractiveMoveResize => false;
    public bool IsProcessingPromotedTouchMouse => false;
    public Action<NativeTouchEvent>? TouchHandler { get; set; }
    public bool TryGetGeometrySnapshot(out NativeWindowGeometrySnapshot snapshot) => window.TryGetGeometry(out snapshot);
    public bool ApplyChrome(in NativeWindowState state) =>
        state.IsPopup && state.Decorations == NativeWindowDecorations.None &&
        !state.ExtendClientArea && !state.CanResize;
    public bool SetEnabled(bool value) => window.SetInputAllowed(value);
    public bool SetTopMost(bool value) => !value;
    public bool SetOpacity(double value) => false;
    public bool SetZOrder(NativeWindowZOrder value) => false;
    public bool SetShowInTaskbar(bool value) => !value;
    public bool SetParent(NativeWindowHandle parent) => window.BindOwner(parent);
    public bool SetSizeConstraints(NativeWindowSize minimum, NativeWindowSize maximum) => false;
    public bool SetClientAreaExtension(bool enabled, double titleBarHeight) => !enabled;
    public bool SetTheme(NativeWindowTheme theme) => false;
    public bool SetBackdrop(NativeWindowBackdrop backdrop) => window.TransparentFramebuffer
        ? backdrop == NativeWindowBackdrop.Transparent : backdrop == NativeWindowBackdrop.None;
    public bool SetWindowShadow(bool enabled) => false;
    public bool TryBeginMove(NativeWindowPoint pointer) => false;
    public bool TryBeginResize(NativeResizeEdge edge, NativeWindowPoint pointer) => false;
    // Window ownership belongs to IWindow, never to its borrowed controller.
    public void Dispose() { }
}
