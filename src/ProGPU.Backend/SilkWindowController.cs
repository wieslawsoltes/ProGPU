using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace ProGPU.Backend;

public sealed class SilkWindowController : IDisposable
{
    private readonly IWindow _window;
    private INativeWindowPlatform? _platform;
    private NativeWindowState _state = NativeWindowState.Default;
    private DragState? _drag;
    private bool _isApplying;
    private bool _disposed;

    public SilkWindowController(IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
    }

    public bool IsAttached => _platform != null;
    public NativeWindowHandle Handle => _platform?.Handle ?? ResolvePendingHandle();
    public NativeWindowCapabilities Capabilities =>
        _platform?.Capabilities ?? NativeWindowCapabilities.ForKind(Handle.Kind);
    public bool IsClientAreaExtended =>
        _state.ExtendClientArea &&
        (!UsesSystemChrome(_state.ChromeHints) ||
         (_platform?.SupportsSystemChromeExtension ?? false));
    public bool RequiresManagedDecorations =>
        IsClientAreaExtended &&
        (_platform?.RequiresManagedDecorations ?? false) &&
        UsesManagedChrome(_state.ChromeHints);
    public NativeDrawnDecorationParts RequestedDrawnDecorations =>
        RequiresManagedDecorations
            ? _platform?.RequestedDrawnDecorations ?? NativeDrawnDecorationParts.None
            : NativeDrawnDecorationParts.None;
    public NativeWindowFrameInsets FrameInsets => _platform?.FrameInsets ?? NativeWindowFrameInsets.Empty;
    public double ExtendedTitleBarHeight => _state.ExtendClientArea
        ? (_state.TitleBarHeight >= 0d
            ? _state.TitleBarHeight
            : _platform?.DefaultTitleBarHeight ?? 0d)
        : 0d;
    public NativeWindowBackdrop Backdrop => _state.Backdrop;
    public NativeWindowTheme Theme => _state.Theme;
    public NativeWindowHandle Parent => _state.Parent;

    public bool Attach()
    {
        ThrowIfDisposed();
        if (_platform != null)
        {
            return true;
        }
        if (!_window.IsInitialized || _window.Native == null)
        {
            return false;
        }

        _platform = NativeWindowPlatformFactory.Create(_window);
        ApplyAll();
        return true;
    }

    public bool SetDecorations(NativeWindowDecorations decorations)
    {
        _state = _state with { Decorations = decorations };
        return Apply(ApplyChromeAndShadow);
    }

    public bool SetIsPopup(bool value)
    {
        _state = _state with { IsPopup = value };
        return Apply(ApplyChromeAndShadow);
    }

    public bool SetCanResize(bool value)
    {
        _state = _state with { CanResize = value };
        return Apply(ApplyChromeAndShadow);
    }

    public bool SetCanClose(bool value)
    {
        _state = _state with { CanClose = value };
        return Apply(ApplyChromeAndShadow);
    }

    public bool SetCanMinimize(bool value)
    {
        _state = _state with { CanMinimize = value };
        return Apply(ApplyChromeAndShadow);
    }

    public bool SetCanMaximize(bool value)
    {
        _state = _state with { CanMaximize = value };
        return Apply(ApplyChromeAndShadow);
    }

    public bool SetTopMost(bool value)
    {
        _state = _state with { TopMost = value };
        return Apply((platform, _) => platform.SetTopMost(value));
    }

    public bool SetEnabled(bool value)
    {
        _state = _state with { Enabled = value };
        return Apply((platform, state) =>
        {
            bool enabled =
                platform.SetEnabled(value);
            bool shadow =
                platform.SetWindowShadow(
                    state.AddShadow);
            return enabled || shadow;
        });
    }

    public bool SetOpacity(double value)
    {
        if (!double.IsFinite(value) || value is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        _state = _state with { Opacity = value };
        return Apply((platform, _) => platform.SetOpacity(value));
    }

    public bool SetZOrder(NativeWindowZOrder value)
    {
        if (value is not NativeWindowZOrder.Front and not NativeWindowZOrder.Back)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return Apply((platform, _) => platform.SetZOrder(value));
    }

    public bool SetShowInTaskbar(bool value)
    {
        _state = _state with { ShowInTaskbar = value };
        return Apply((platform, _) => platform.SetShowInTaskbar(value));
    }

    public bool SetParent(NativeWindowHandle parent)
    {
        _state = _state with { Parent = parent };
        return Apply((platform, _) => platform.SetParent(parent));
    }

    public bool SetSizeConstraints(NativeWindowSize minimum, NativeWindowSize maximum)
    {
        minimum = NormalizeMinimum(minimum);
        maximum = NormalizeMaximum(minimum, maximum);
        _state = _state with { MinimumSize = minimum, MaximumSize = maximum };
        return Apply((platform, _) => platform.SetSizeConstraints(minimum, maximum));
    }

    public bool SetClientAreaExtension(bool value, double titleBarHeight = -1d)
    {
        _state = _state with
        {
            ExtendClientArea = value,
            TitleBarHeight = NormalizeTitleBarHeight(titleBarHeight)
        };
        return Apply((platform, state) =>
        {
            var extensionApplied = platform.SetClientAreaExtension(value, state.TitleBarHeight);
            var chromeApplied =
                ApplyChromeAndShadow(
                    platform,
                    state);
            return extensionApplied || chromeApplied;
        });
    }

    public bool SetTitleBarHeight(double titleBarHeight)
    {
        return SetClientAreaExtension(_state.ExtendClientArea, titleBarHeight);
    }

    public bool SetChromeHints(NativeWindowChromeHints hints)
    {
        _state = _state with { ChromeHints = hints };
        return Apply((platform, state) =>
        {
            bool extension = platform.SetClientAreaExtension(
                state.ExtendClientArea,
                state.TitleBarHeight);
            bool chrome = ApplyChromeAndShadow(platform, state);
            return extension || chrome;
        });
    }

    public bool SetTheme(NativeWindowTheme theme)
    {
        _state = _state with { Theme = theme };
        return Apply((platform, _) => platform.SetTheme(theme));
    }

    public bool SetBackdrop(NativeWindowBackdrop backdrop)
    {
        _state = _state with { Backdrop = backdrop };
        return Apply((platform, _) => platform.SetBackdrop(backdrop));
    }

    public bool SetWindowShadow(bool enabled)
    {
        _state = _state with { AddShadow = enabled };
        return Apply(
            (platform, _) =>
                platform.SetWindowShadow(enabled));
    }

    public bool PrepareForStateTransition()
    {
        return Apply(static (platform, state) =>
            ApplyChromeAndShadow(
                platform,
                state with { CanResize = true }));
    }

    public bool BeginMove(NativeWindowPoint pointer)
    {
        ThrowIfDisposed();
        if (!EnsureAttached())
        {
            return false;
        }
        if (_platform!.TryBeginMove(pointer))
        {
            _drag = null;
            return true;
        }
        if (!_platform.SupportsManagedMove)
        {
            return false;
        }

        _drag = DragState.CreateMove(pointer, _window.Position, _window.Size);
        return true;
    }

    public bool BeginResize(NativeResizeEdge edge, NativeWindowPoint pointer)
    {
        ThrowIfDisposed();
        if (!_state.CanResize || !EnsureAttached())
        {
            return false;
        }
        if (_platform!.TryBeginResize(edge, pointer))
        {
            _drag = null;
            return true;
        }
        if (!_platform.SupportsManagedResize)
        {
            return false;
        }

        _drag = DragState.CreateResize(edge, pointer, _window.Position, _window.Size);
        return true;
    }

    public bool UpdateDrag(NativeWindowPoint pointer)
    {
        if (_drag is not { } drag)
        {
            return false;
        }

        var deltaX = pointer.X - drag.Pointer.X;
        var deltaY = pointer.Y - drag.Pointer.Y;
        if (drag.IsMove)
        {
            _window.Position = new Vector2D<int>(
                drag.Position.X + deltaX,
                drag.Position.Y + deltaY);
            return true;
        }

        var left = drag.Position.X;
        var top = drag.Position.Y;
        var right = left + drag.Size.X;
        var bottom = top + drag.Size.Y;
        if (UsesLeft(drag.Edge))
        {
            left += deltaX;
        }
        if (UsesRight(drag.Edge))
        {
            right += deltaX;
        }
        if (UsesTop(drag.Edge))
        {
            top += deltaY;
        }
        if (UsesBottom(drag.Edge))
        {
            bottom += deltaY;
        }

        var width = Math.Clamp(right - left, _state.MinimumSize.Width, _state.MaximumSize.Width);
        var height = Math.Clamp(bottom - top, _state.MinimumSize.Height, _state.MaximumSize.Height);
        if (UsesLeft(drag.Edge))
        {
            left = right - width;
        }
        if (UsesTop(drag.Edge))
        {
            top = bottom - height;
        }

        if (Handle.Kind != NativeWindowKind.Wayland)
        {
            _window.Position = new Vector2D<int>(left, top);
        }
        _window.Size = new Vector2D<int>(Math.Max(1, width), Math.Max(1, height));
        return true;
    }

    public void EndDrag()
    {
        _drag = null;
    }

    public void Reapply()
    {
        if (EnsureAttached())
        {
            ApplyAll();
        }
    }

    private bool Apply(Func<INativeWindowPlatform, NativeWindowState, bool> action)
    {
        ThrowIfDisposed();
        if (!EnsureAttached() || _isApplying)
        {
            return false;
        }

        _isApplying = true;
        try
        {
            return action(_platform!, _state);
        }
        finally
        {
            _isApplying = false;
        }
    }

    private bool EnsureAttached()
    {
        return _platform != null || Attach();
    }

    private static bool ApplyChromeAndShadow(
        INativeWindowPlatform platform,
        NativeWindowState state)
    {
        bool chrome =
            platform.ApplyChrome(state);
        bool shadow =
            platform.SetWindowShadow(
                state.AddShadow);
        return chrome || shadow;
    }

    private void ApplyAll()
    {
        if (_isApplying)
        {
            return;
        }

        _isApplying = true;
        try
        {
            ApplyAllCore();
        }
        finally
        {
            _isApplying = false;
        }
    }

    private void ApplyAllCore()
    {
        var platform = _platform!;
        platform.SetClientAreaExtension(_state.ExtendClientArea, _state.TitleBarHeight);
        platform.ApplyChrome(_state);
        platform.SetSizeConstraints(_state.MinimumSize, _state.MaximumSize);
        platform.SetTopMost(_state.TopMost);
        platform.SetEnabled(_state.Enabled);
        platform.SetOpacity(_state.Opacity);
        platform.SetShowInTaskbar(_state.ShowInTaskbar);
        if (_state.Parent.IsValid)
        {
            platform.SetParent(_state.Parent);
        }
        platform.SetTheme(_state.Theme);
        platform.SetBackdrop(_state.Backdrop);
        platform.SetWindowShadow(_state.AddShadow);
    }

    private NativeWindowHandle ResolvePendingHandle()
    {
        if (!_window.IsInitialized || _window.Native == null)
        {
            return NativeWindowHandle.Empty;
        }

        return GlfwNativeWindowPlatform.ResolveWindowHandle(_window);
    }

    private static NativeWindowSize NormalizeMinimum(NativeWindowSize size)
    {
        return new NativeWindowSize(Math.Max(0, size.Width), Math.Max(0, size.Height));
    }

    private static NativeWindowSize NormalizeMaximum(NativeWindowSize minimum, NativeWindowSize maximum)
    {
        var width = maximum.Width <= 0 || maximum.Width == int.MaxValue
            ? int.MaxValue
            : Math.Max(minimum.Width, maximum.Width);
        var height = maximum.Height <= 0 || maximum.Height == int.MaxValue
            ? int.MaxValue
            : Math.Max(minimum.Height, maximum.Height);
        return new NativeWindowSize(width, height);
    }

    private static double NormalizeTitleBarHeight(double value)
    {
        return double.IsFinite(value) && value >= 0d ? value : -1d;
    }

    private static bool UsesLeft(NativeResizeEdge edge) =>
        edge is NativeResizeEdge.Left or NativeResizeEdge.TopLeft or NativeResizeEdge.BottomLeft;
    private static bool UsesRight(NativeResizeEdge edge) =>
        edge is NativeResizeEdge.Right or NativeResizeEdge.TopRight or NativeResizeEdge.BottomRight;
    private static bool UsesTop(NativeResizeEdge edge) =>
        edge is NativeResizeEdge.Top or NativeResizeEdge.TopLeft or NativeResizeEdge.TopRight;
    private static bool UsesBottom(NativeResizeEdge edge) =>
        edge is NativeResizeEdge.Bottom or NativeResizeEdge.BottomLeft or NativeResizeEdge.BottomRight;

    internal static bool UsesSystemChrome(
        NativeWindowChromeHints hints) =>
        (hints & NativeWindowChromeHints.SystemChrome) != 0 &&
        (hints & NativeWindowChromeHints.PreferSystemChrome) == 0;

    internal static bool UsesManagedChrome(
        NativeWindowChromeHints hints) =>
        (hints & NativeWindowChromeHints.PreferSystemChrome) != 0;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _drag = null;
        _platform?.Dispose();
        _platform = null;
    }

    private readonly record struct DragState(
        bool IsMove,
        NativeResizeEdge Edge,
        NativeWindowPoint Pointer,
        Vector2D<int> Position,
        Vector2D<int> Size)
    {
        public static DragState CreateMove(
            NativeWindowPoint pointer,
            Vector2D<int> position,
            Vector2D<int> size) =>
            new(true, NativeResizeEdge.Right, pointer, position, size);

        public static DragState CreateResize(
            NativeResizeEdge edge,
            NativeWindowPoint pointer,
            Vector2D<int> position,
            Vector2D<int> size) =>
            new(false, edge, pointer, position, size);
    }
}
