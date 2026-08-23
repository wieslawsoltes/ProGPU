namespace ProGPU.Backend;

public enum NativeWindowKind
{
    Unknown = 0,
    Win32 = 1,
    Cocoa = 2,
    X11 = 3,
    Wayland = 4,
    Glfw = 5
}

public enum NativeWindowDecorations
{
    None = 0,
    BorderOnly = 1,
    Full = 2
}

public enum NativeWindowTheme
{
    Default = 0,
    Light = 1,
    Dark = 2
}

[Flags]
public enum NativeWindowChromeHints
{
    NoChrome = 0,
    SystemChrome = 1 << 0,
    PreferSystemChrome = 1 << 1,
    MacOsThickTitleBar = 1 << 3,
    Default = PreferSystemChrome
}

public enum NativeWindowBackdrop
{
    None = 0,
    Transparent = 1,
    Blur = 2,
    Acrylic = 3,
    Mica = 4,
    MicaAlt = 5
}

public enum NativeResizeEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
    TopLeft = 4,
    TopRight = 5,
    BottomLeft = 6,
    BottomRight = 7
}

public enum NativeWindowZOrder
{
    Front = 0,
    Back = 1
}

[Flags]
public enum NativeWindowFeatures
{
    None = 0,
    Decorations = 1 << 0,
    Resizable = 1 << 1,
    MinimizeButton = 1 << 2,
    MaximizeButton = 1 << 3,
    SizeConstraints = 1 << 4,
    TopMost = 1 << 5,
    ClientAreaExtension = 1 << 6,
    MoveDrag = 1 << 7,
    ResizeDrag = 1 << 8,
    Parent = 1 << 9,
    Taskbar = 1 << 10,
    Theme = 1 << 11,
    Transparent = 1 << 12,
    Blur = 1 << 13,
    Acrylic = 1 << 14,
    Mica = 1 << 15,
    CloseButton = 1 << 16,
    Opacity = 1 << 17,
    ZOrder = 1 << 18
}

[Flags]
public enum NativeDrawnDecorationParts
{
    None = 0,
    TitleBar = 1 << 0,
    Border = 1 << 1,
    ResizeGrips = 1 << 2,
    Shadow = 1 << 3
}

public readonly record struct NativeWindowHandle(
    NativeWindowKind Kind,
    nint Handle,
    nint Display,
    string Descriptor)
{
    public bool IsValid => Handle != 0;
    public static NativeWindowHandle Empty => new(NativeWindowKind.Unknown, 0, 0, "Unknown");
}

public readonly record struct NativeWindowPoint(int X, int Y);

public readonly record struct NativeWindowSize(int Width, int Height)
{
    public static NativeWindowSize Unbounded => new(int.MaxValue, int.MaxValue);
}

public readonly record struct NativeWindowFrameInsets(int Left, int Top, int Right, int Bottom)
{
    public static NativeWindowFrameInsets Empty => default;
}

public readonly record struct NativeWindowCapabilities(
    NativeWindowKind Kind,
    NativeWindowFeatures Features)
{
    public bool Supports(NativeWindowFeatures feature) => (Features & feature) == feature;

    public static NativeWindowKind DetectCurrentKind()
    {
        if (OperatingSystem.IsWindows())
        {
            return NativeWindowKind.Win32;
        }
        if (OperatingSystem.IsMacOS())
        {
            return NativeWindowKind.Cocoa;
        }
        if (OperatingSystem.IsLinux())
        {
            var wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ||
                string.Equals(
                    Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
                    "wayland",
                    StringComparison.OrdinalIgnoreCase);
            return wayland ? NativeWindowKind.Wayland : NativeWindowKind.X11;
        }
        return NativeWindowKind.Unknown;
    }

    public static NativeWindowCapabilities ForKind(NativeWindowKind kind)
    {
        const NativeWindowFeatures common =
            NativeWindowFeatures.Decorations |
            NativeWindowFeatures.Resizable |
            NativeWindowFeatures.SizeConstraints |
            NativeWindowFeatures.TopMost |
            NativeWindowFeatures.Transparent;

        return kind switch
        {
            NativeWindowKind.Win32 => new NativeWindowCapabilities(kind,
                common |
                NativeWindowFeatures.ZOrder |
                NativeWindowFeatures.Opacity |
                NativeWindowFeatures.CloseButton |
                NativeWindowFeatures.MinimizeButton |
                NativeWindowFeatures.MaximizeButton |
                NativeWindowFeatures.ClientAreaExtension |
                NativeWindowFeatures.MoveDrag |
                NativeWindowFeatures.ResizeDrag |
                NativeWindowFeatures.Parent |
                NativeWindowFeatures.Taskbar |
                NativeWindowFeatures.Theme |
                NativeWindowFeatures.Blur |
                NativeWindowFeatures.Acrylic |
                NativeWindowFeatures.Mica),
            NativeWindowKind.Cocoa => new NativeWindowCapabilities(kind,
                common |
                NativeWindowFeatures.ZOrder |
                NativeWindowFeatures.Opacity |
                NativeWindowFeatures.CloseButton |
                NativeWindowFeatures.MinimizeButton |
                NativeWindowFeatures.MaximizeButton |
                NativeWindowFeatures.ClientAreaExtension |
                NativeWindowFeatures.MoveDrag |
                NativeWindowFeatures.ResizeDrag |
                NativeWindowFeatures.Parent |
                NativeWindowFeatures.Theme |
                NativeWindowFeatures.Blur |
                NativeWindowFeatures.Acrylic |
                NativeWindowFeatures.Mica),
            NativeWindowKind.X11 => new NativeWindowCapabilities(kind,
                common |
                NativeWindowFeatures.ZOrder |
                NativeWindowFeatures.Opacity |
                NativeWindowFeatures.CloseButton |
                NativeWindowFeatures.MinimizeButton |
                NativeWindowFeatures.MaximizeButton |
                NativeWindowFeatures.ClientAreaExtension |
                NativeWindowFeatures.MoveDrag |
                NativeWindowFeatures.ResizeDrag |
                NativeWindowFeatures.Parent |
                NativeWindowFeatures.Taskbar |
                NativeWindowFeatures.Theme |
                NativeWindowFeatures.Blur |
                NativeWindowFeatures.Acrylic),
            NativeWindowKind.Wayland => new NativeWindowCapabilities(kind,
                NativeWindowFeatures.Decorations |
                NativeWindowFeatures.Resizable |
                NativeWindowFeatures.SizeConstraints |
                NativeWindowFeatures.ClientAreaExtension |
                NativeWindowFeatures.ResizeDrag |
                NativeWindowFeatures.Transparent),
            _ => new NativeWindowCapabilities(kind, common)
        };
    }
}

internal readonly record struct NativeWindowState(
    NativeWindowDecorations Decorations,
    bool CanResize,
    bool CanClose,
    bool CanMinimize,
    bool CanMaximize,
    bool TopMost,
    bool Enabled,
    double Opacity,
    bool ShowInTaskbar,
    bool AddShadow,
    bool ExtendClientArea,
    NativeWindowChromeHints ChromeHints,
    double TitleBarHeight,
    NativeWindowSize MinimumSize,
    NativeWindowSize MaximumSize,
    NativeWindowTheme Theme,
    NativeWindowBackdrop Backdrop,
    NativeWindowHandle Parent,
    bool IsPopup = false)
{
    public static NativeWindowState Default => new(
        NativeWindowDecorations.Full,
        CanResize: true,
        CanClose: true,
        CanMinimize: true,
        CanMaximize: true,
        TopMost: false,
        Enabled: true,
        Opacity: 1d,
        ShowInTaskbar: true,
        AddShadow: true,
        ExtendClientArea: false,
        ChromeHints: NativeWindowChromeHints.Default,
        TitleBarHeight: -1d,
        MinimumSize: new NativeWindowSize(0, 0),
        MaximumSize: NativeWindowSize.Unbounded,
        Theme: NativeWindowTheme.Default,
        Backdrop: NativeWindowBackdrop.None,
        Parent: NativeWindowHandle.Empty,
        IsPopup: false);
}
