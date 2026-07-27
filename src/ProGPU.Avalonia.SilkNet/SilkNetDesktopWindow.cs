using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform;
#if AVALONIA11
using Avalonia.Controls.Platform.Surfaces;
#else
using Avalonia.Platform.Surfaces;
#endif
using Avalonia.Rendering.Composition;
using ProGPU.Backend;
using Silk.NET.Core;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using AvaloniaWindowState = Avalonia.Controls.WindowState;
using SilkWindowBorder = Silk.NET.Windowing.WindowBorder;
using SilkWindowState = Silk.NET.Windowing.WindowState;

namespace Avalonia.SilkNet;

/// <summary>
/// A typed Avalonia top-level backed by a Silk.NET native window and a
/// ProGPU WebGPU presentation surface.
/// </summary>
public sealed class WindowImpl :
    IWindowImpl,
    IPopupImpl,
    ISilkNetLoopParticipant
{
    private readonly SilkNetWindowingPlatform _platform;
    private readonly WindowImpl? _parent;
    private readonly bool _isPopup;
    private readonly SilkNetFramebufferSurface _framebufferSurface;
#if AVALONIA11
    private readonly object[] _surfaces;
#else
    private readonly IPlatformRenderSurface[] _surfaces;
#endif
    private readonly SilkNetScreenImpl _screens;
    private readonly TaskCompletionSource _disposed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManagedPopupPositioner? _popupPositioner;
    private SilkNetInputRouter? _input;
    private IWindow? _window;
    private WgpuContext? _webGpuContext;
    private SilkNetCursorImpl? _cursor;
    private IWindowIconImpl? _icon;
    private string _title = string.Empty;
    private Size _desiredSize = new(800, 600);
    private PixelPoint? _desiredPosition;
    private Size _minSize;
    private Size _maxSize = new(double.PositiveInfinity, double.PositiveInfinity);
    private AvaloniaWindowState _windowState;
    private SilkWindowBorder _windowBorder = SilkWindowBorder.Resizable;
    private WindowTransparencyLevel _transparencyLevel =
        WindowTransparencyLevel.None;
    private bool _transparentRequested;
    private bool _topmost;
    private bool _enabled = true;
    private bool _visible;
    private bool _paintQueued = true;
    private bool _disposedState;
    private int _closeCallback;
    private long _zOrder;

    internal WindowImpl(
        SilkNetWindowingPlatform platform,
        WindowImpl? parent,
        bool isPopup)
    {
        _platform = platform;
        _parent = parent;
        _isPopup = isPopup;
        if (isPopup)
        {
            _windowBorder = SilkWindowBorder.Hidden;
            _topmost = true;
            _desiredSize = new Size(1, 1);
            if (parent is not null)
            {
                var helper =
                    new ManagedPopupPositionerPopupImplHelper(
                        parent,
                        MoveAndResizePopup);
                _popupPositioner =
                    new ManagedPopupPositioner(helper);
            }
        }

        _framebufferSurface =
            new SilkNetFramebufferSurface(this);
        _surfaces = [_framebufferSurface];
        _screens = new SilkNetScreenImpl(this);
        WgpuContext.OnWebGpuDeviceLost += OnWebGpuDeviceLost;
    }

    public bool HasActiveWebGpuContext =>
        _webGpuContext is
        {
            IsDisposed: false,
            IsDeviceLost: false
        };

    public Task DisposedTask => _disposed.Task;

    public bool SharesWebGpuDeviceWith(WindowImpl other)
    {
        ArgumentNullException.ThrowIfNull(other);
        WgpuContext? left = _webGpuContext;
        WgpuContext? right = other._webGpuContext;
        return left is not null &&
               right is not null &&
               left.SharesDeviceWith(right);
    }

    internal long ZOrder => Volatile.Read(ref _zOrder);
    internal bool AcceptsInput => _enabled && !_disposedState;
    internal IWindow? NativeWindow => _window;

    public double DesktopScaling => RenderScaling;

    public IPlatformHandle? Handle
    {
        get
        {
            IWindow? window = _window;
            if (window?.Native is not { } native)
                return null;
            if (native.Win32 is { } win32)
                return new PlatformHandle(win32.Hwnd, "HWND");
            if (native.Cocoa is { } cocoa)
                return new PlatformHandle(cocoa, "NSWindow");
            if (native.X11 is { } x11)
                return new PlatformHandle(
                    checked((nint)x11.Window),
                    "XID");
            if (native.Wayland is { } wayland)
                return new PlatformHandle(
                    wayland.Surface,
                    "wl_surface");
            return window.Handle == 0
                ? null
                : new PlatformHandle(
                    window.Handle,
                    "GLFW");
        }
    }

    public Size ClientSize
    {
        get
        {
            IWindow? window = _window;
            return window is null
                ? _desiredSize
                : new Size(
                    Math.Max(0, window.Size.X),
                    Math.Max(0, window.Size.Y));
        }
    }

    public double RenderScaling
    {
        get
        {
            IWindow? window = _window;
            if (window is null ||
                window.Size.X <= 0 ||
                window.Size.Y <= 0 ||
                window.FramebufferSize.X <= 0 ||
                window.FramebufferSize.Y <= 0)
            {
                return 1;
            }

            double x =
                window.FramebufferSize.X /
                (double)window.Size.X;
            double y =
                window.FramebufferSize.Y /
                (double)window.Size.Y;
            return Math.Max(1, (x + y) * 0.5);
        }
    }

    internal PixelSize FramebufferPixelSize
    {
        get
        {
            IWindow? window = _window;
            if (window is null)
            {
                double scaling = RenderScaling;
                return new PixelSize(
                    Math.Max(
                        1,
                        checked((int)Math.Ceiling(
                            _desiredSize.Width * scaling))),
                    Math.Max(
                        1,
                        checked((int)Math.Ceiling(
                            _desiredSize.Height * scaling))));
            }

            return new PixelSize(
                Math.Max(1, window.FramebufferSize.X),
                Math.Max(1, window.FramebufferSize.Y));
        }
    }

#if AVALONIA11
    public IEnumerable<object> Surfaces => _surfaces;
#else
    public IPlatformRenderSurface[] Surfaces => _surfaces;
#endif
    public Compositor Compositor => _platform.Compositor;

    public Action<RawInputEventArgs>? Input { get; set; }
    public Action<Rect>? Paint { get; set; }
    public Action<Size, WindowResizeReason>? Resized { get; set; }
    public Action<double>? ScalingChanged { get; set; }
    public Action<WindowTransparencyLevel>? TransparencyLevelChanged { get; set; }
    public Action? Closed { get; set; }
    public Action? LostFocus { get; set; }
    public Action<PixelPoint>? PositionChanged { get; set; }
    public Action? Deactivated { get; set; }
    public Action? Activated { get; set; }
    public Action<AvaloniaWindowState>? WindowStateChanged { get; set; }
    public Action? GotInputWhenDisabled { get; set; }
    public Func<WindowCloseReason, bool>? Closing { get; set; }
    public Action<bool>? ExtendClientAreaToDecorationsChanged { get; set; }
#if !AVALONIA11
    public Action<PlatformAllowedWindowActions>? AllowedWindowActionsChanged
    {
        get;
        set;
    }
#endif

    public Size? FrameSize => ClientSize;

    public PixelPoint Position
    {
        get
        {
            IWindow? window = _window;
            if (window is null)
                return _desiredPosition ?? default;
            double scaling = DesktopScaling;
            return new PixelPoint(
                checked((int)Math.Round(
                    window.Position.X * scaling)),
                checked((int)Math.Round(
                    window.Position.Y * scaling)));
        }
    }

    public Size MaxAutoSizeHint
    {
        get
        {
            Screen? screen = _screens.ScreenFromTopLevel(this);
            return screen is null
                ? new Size(double.PositiveInfinity, double.PositiveInfinity)
                : new Size(
                    screen.WorkingArea.Width / screen.Scaling,
                    screen.WorkingArea.Height / screen.Scaling);
        }
    }

    public AvaloniaWindowState WindowState
    {
        get => _window is null
            ? _windowState
            : FromSilkState(_window.WindowState);
        set
        {
            _windowState = value;
            if (_window is not null)
                _window.WindowState = ToSilkState(value);
        }
    }

#if !AVALONIA11
    public bool WindowStateGetterIsUsable => true;
#endif
    public bool IsClientAreaExtendedToDecorations => false;
    public bool NeedsManagedDecorations => false;
#if !AVALONIA11
    public PlatformRequestedDrawnDecoration RequestedDrawnDecorations =>
        PlatformRequestedDrawnDecoration.None;
#endif
    public Thickness ExtendedMargins => default;
    public Thickness OffScreenMargin => default;
#if !AVALONIA11
    public PlatformAllowedWindowActions AllowedWindowActions =>
        PlatformAllowedWindowActions.All;
#endif
    public WindowTransparencyLevel TransparencyLevel =>
        _transparencyLevel;
    public AcrylicPlatformCompensationLevels AcrylicCompensationLevels =>
        new(1, 1, 1);
    public IPopupPositioner? PopupPositioner =>
        _isPopup ? _popupPositioner : null;

    bool ISilkNetLoopParticipant.IsLoopVisible =>
        _visible && !_disposedState;

    bool ISilkNetLoopParticipant.IsLoopInitialized =>
        _window?.IsInitialized == true &&
        !_disposedState;

    public object? TryGetFeature(Type featureType)
    {
        ArgumentNullException.ThrowIfNull(featureType);
        if (featureType == typeof(IScreenImpl))
            return _screens;
        if (featureType == typeof(Avalonia.Input.Platform.IClipboard))
            return _platform.Clipboard;
        return null;
    }

    public void SetInputRoot(IInputRoot inputRoot)
    {
        ArgumentNullException.ThrowIfNull(inputRoot);
        (_input ??= new SilkNetInputRouter(
            this,
            _platform.Clipboard))
            .SetInputRoot(inputRoot);
    }

    public Point PointToClient(PixelPoint point)
    {
        IWindow? window = _window;
        double scaling = DesktopScaling;
        Vector2D<int> source = new(
            checked((int)Math.Round(point.X / scaling)),
            checked((int)Math.Round(point.Y / scaling)));
        Vector2D<int> result =
            window?.PointToClient(source) ?? source;
        return new Point(result.X, result.Y);
    }

    public PixelPoint PointToScreen(Point point)
    {
        Vector2D<int> source = new(
            checked((int)Math.Round(point.X)),
            checked((int)Math.Round(point.Y)));
        Vector2D<int> result =
            _window?.PointToScreen(source) ?? source;
        double scaling = DesktopScaling;
        return new PixelPoint(
            checked((int)Math.Round(result.X * scaling)),
            checked((int)Math.Round(result.Y * scaling)));
    }

    public void SetCursor(ICursorImpl? cursor)
    {
        _cursor = cursor as SilkNetCursorImpl;
        _input?.ApplyCursor(_cursor);
    }

    public IPopupImpl? CreatePopup() =>
        _platform.CreatePopup(this);

    public void SetTransparencyLevelHint(
        IReadOnlyList<WindowTransparencyLevel> transparencyLevels)
    {
        _transparentRequested = false;
        WindowTransparencyLevel selected =
            WindowTransparencyLevel.None;
        foreach (WindowTransparencyLevel level in transparencyLevels)
        {
            if (level == WindowTransparencyLevel.Transparent ||
                level == WindowTransparencyLevel.Blur ||
                level == WindowTransparencyLevel.AcrylicBlur)
            {
                _transparentRequested = true;
                selected = WindowTransparencyLevel.Transparent;
                break;
            }
        }

        if (_transparencyLevel != selected)
        {
            _transparencyLevel = selected;
            TransparencyLevelChanged?.Invoke(selected);
        }
    }

    public void SetFrameThemeVariant(PlatformThemeVariant themeVariant)
    {
    }

    public void Show(bool activate, bool isDialog)
    {
        ThrowIfDisposed();
        IWindow window = EnsureNativeWindow();
        _visible = true;
        window.IsVisible = true;
        Volatile.Write(
            ref _zOrder,
            _platform.BringToFront());
        if (activate && !_isPopup)
            window.Focus();
        QueuePaint();
    }

    public void Hide()
    {
        _visible = false;
        if (_window is not null)
            _window.IsVisible = false;
    }

    public void Activate()
    {
        IWindow window = EnsureNativeWindow();
        Volatile.Write(
            ref _zOrder,
            _platform.BringToFront());
        window.Focus();
    }

    public void SetTopmost(bool value)
    {
        _topmost = value;
        if (_window is not null)
            _window.TopMost = value;
    }

    public void SetTitle(string? title)
    {
        _title = title ?? string.Empty;
        if (_window is not null)
            _window.Title = _title;
    }

    public void SetParent(IWindowImpl? parent)
    {
        if (parent is not null &&
            !ReferenceEquals(parent, _parent))
        {
            throw new NotSupportedException(
                "Silk.NET window ownership is fixed at construction.");
        }
    }

    public void SetEnabled(bool enable) =>
        _enabled = enable;

#if AVALONIA11
    public void SetSystemDecorations(SystemDecorations enabled)
    {
        _windowBorder =
            enabled == SystemDecorations.None
                ? SilkWindowBorder.Hidden
                : SilkWindowBorder.Resizable;
        if (_window is not null)
            _window.WindowBorder = _windowBorder;
    }
#else
    public void SetWindowDecorations(WindowDecorations enabled)
    {
        _windowBorder =
            enabled == WindowDecorations.None
                ? SilkWindowBorder.Hidden
                : SilkWindowBorder.Resizable;
        if (_window is not null)
            _window.WindowBorder = _windowBorder;
    }
#endif

    public void SetIcon(IWindowIconImpl? icon)
    {
        _icon = icon;
        ApplyIcon();
    }

    public void ShowTaskbarIcon(bool value)
    {
    }

    public void CanResize(bool value)
    {
        _windowBorder = value
            ? SilkWindowBorder.Resizable
            : SilkWindowBorder.Fixed;
        if (_window is not null)
            _window.WindowBorder = _windowBorder;
    }

    public void SetCanMinimize(bool value)
    {
    }

    public void SetCanMaximize(bool value)
    {
    }

    public void BeginMoveDrag(PointerPressedEventArgs e)
    {
    }

    public void BeginResizeDrag(
        WindowEdge edge,
        PointerPressedEventArgs e)
    {
    }

    public void Resize(
        Size clientSize,
        WindowResizeReason reason = WindowResizeReason.Application)
    {
        if (clientSize.Width <= 0 ||
            clientSize.Height <= 0 ||
            !double.IsFinite(clientSize.Width) ||
            !double.IsFinite(clientSize.Height))
        {
            return;
        }

        Size constrained = new(
            Math.Clamp(
                clientSize.Width,
                _minSize.Width,
                _maxSize.Width),
            Math.Clamp(
                clientSize.Height,
                _minSize.Height,
                _maxSize.Height));
        _desiredSize = constrained;
        if (_window is not null)
        {
            _window.Size = new Vector2D<int>(
                Math.Max(
                    1,
                    checked((int)Math.Round(
                        constrained.Width))),
                Math.Max(
                    1,
                    checked((int)Math.Round(
                        constrained.Height))));
        }
    }

    public void Move(PixelPoint point)
    {
        _desiredPosition = point;
        if (_window is null)
            return;
        double scaling = DesktopScaling;
        _window.Position = new Vector2D<int>(
            checked((int)Math.Round(point.X / scaling)),
            checked((int)Math.Round(point.Y / scaling)));
    }

    public void SetMinMaxSize(Size minSize, Size maxSize)
    {
        _minSize = minSize;
        _maxSize = new Size(
            maxSize.Width > 0
                ? maxSize.Width
                : double.PositiveInfinity,
            maxSize.Height > 0
                ? maxSize.Height
                : double.PositiveInfinity);
    }

    public void SetExtendClientAreaToDecorationsHint(
        bool extendIntoClientAreaHint)
    {
        ExtendClientAreaToDecorationsChanged?.Invoke(false);
    }

#if AVALONIA11
    public void SetExtendClientAreaChromeHints(
        ExtendClientAreaChromeHints hints)
    {
    }

    public void GetWindowsZOrder(
        Span<Avalonia.Controls.Window> windows,
        Span<long> zOrder)
    {
        if (windows.Length != zOrder.Length)
        {
            throw new ArgumentException(
                "Window and z-order spans must have equal lengths.");
        }

        for (int index = 0; index < zOrder.Length; index++)
            zOrder[index] = index;
    }
#endif

    public void SetExtendClientAreaTitleBarHeightHint(
        double titleBarHeight)
    {
    }

    public void SetWindowManagerAddShadowHint(bool enabled)
    {
    }

    public void TakeFocus() => Activate();

    public void Dispose()
    {
        if (_disposedState)
            return;
        _disposedState = true;
        _visible = false;
        WgpuContext.OnWebGpuDeviceLost -= OnWebGpuDeviceLost;
        _platform.EventLoop.Unregister(this);
        _input?.Dispose();
        _input = null;
        _framebufferSurface.Dispose();

        WgpuContext? context = _webGpuContext;
        _webGpuContext = null;
        SharedWebGpuDevices.Release(context);

        IWindow? window = _window;
        _window = null;
        if (window is not null)
        {
            window.Load -= OnLoad;
            window.Render -= OnRender;
            window.Resize -= OnResize;
            window.FramebufferResize -= OnFramebufferResize;
            window.Move -= OnMove;
            window.FocusChanged -= OnFocusChanged;
            window.StateChanged -= OnStateChanged;
            window.Closing -= OnClosing;
            window.Dispose();
        }

        Closed?.Invoke();
        _disposed.TrySetResult();
    }

    internal WgpuContext EnsureWebGpuContext()
    {
        ThrowIfDisposed();
        IWindow window = EnsureNativeWindow();
        WgpuContext? current = _webGpuContext;
        if (current is
            {
                IsDisposed: false,
                IsDeviceLost: false
            })
        {
            return current;
        }

        _webGpuContext = null;
        SharedWebGpuDevices.Release(current);
        WgpuContext replacement =
            SharedWebGpuDevices.Create(window);
        _webGpuContext = replacement;
        WgpuContext.Current = replacement;
        return replacement;
    }

    internal void EmitInput(RawInputEventArgs input)
    {
        if (_enabled)
            Input?.Invoke(input);
        else
            GotInputWhenDisabled?.Invoke();
    }

    void ISilkNetLoopParticipant.PollNativeEvents() =>
        _window?.DoEvents();

    void ISilkNetLoopParticipant.UpdateNativeWindow() =>
        _window?.DoUpdate();

    void ISilkNetLoopParticipant.RenderNativeWindow() =>
        _window?.DoRender();

    private IWindow EnsureNativeWindow()
    {
        ThrowIfDisposed();
        if (_window is not null)
            return _window;

        Vector2D<int> size = new(
            Math.Max(
                1,
                checked((int)Math.Round(_desiredSize.Width))),
            Math.Max(
                1,
                checked((int)Math.Round(_desiredSize.Height))));
        WindowOptions options =
            WindowOptions.Default with
            {
                API = GraphicsAPI.None,
                IsVisible = false,
                IsEventDriven = false,
                ShouldSwapAutomatically = false,
                VSync = false,
                // The outer native event loop owns cadence. Zero disables
                // Silk.NET's second, slightly phase-shifted rate limiter.
                FramesPerSecond = 0,
                UpdatesPerSecond = 0,
                Size = size,
                Title = _title,
                WindowState =
                    ToSilkState(_windowState),
                WindowBorder = _windowBorder,
                TransparentFramebuffer =
                    _transparentRequested,
                TopMost = _topmost
            };
        if (_desiredPosition is { } desired)
        {
            options.Position = new Vector2D<int>(
                desired.X,
                desired.Y);
        }

        IWindow window =
            Silk.NET.Windowing.Window.Create(options);
        _window = window;
        window.Load += OnLoad;
        window.Render += OnRender;
        window.Resize += OnResize;
        window.FramebufferResize += OnFramebufferResize;
        window.Move += OnMove;
        window.FocusChanged += OnFocusChanged;
        window.StateChanged += OnStateChanged;
        window.Closing += OnClosing;
        window.Initialize();
        _platform.EventLoop.Register(this);
        return window;
    }

    private void OnLoad()
    {
        if (_window is null)
            return;
        (_input ??= new SilkNetInputRouter(
            this,
            _platform.Clipboard))
            .Attach(_window);
        _input.ApplyCursor(_cursor);
        ApplyIcon();
        EnsureWebGpuContext();
    }

    private void OnRender(double delta)
    {
        _ = delta;
        SilkNetPlatform.RaiseFramePreparing();
        long now = checked(
            (long)(
                Stopwatch.GetTimestamp() *
                (TimeSpan.TicksPerSecond /
                 (double)Stopwatch.Frequency)));
        _platform.RenderTimer.Pulse(now);
        if (!_paintQueued || !_visible || _disposedState)
            return;

        PaintNow();
    }

    private void PaintNow()
    {
        if (!EnsureWebGpuContextReady())
            return;
        _paintQueued = false;
        Paint?.Invoke(new Rect(ClientSize));
    }

    private bool EnsureWebGpuContextReady()
    {
        if (_disposedState)
            return false;
        EnsureWebGpuContext();
        return true;
    }

    private void OnResize(Vector2D<int> size)
    {
        _desiredSize = new Size(
            Math.Max(0, size.X),
            Math.Max(0, size.Y));
        Resized?.Invoke(
            _desiredSize,
            WindowResizeReason.User);
        QueuePaint();
    }

    private void OnFramebufferResize(Vector2D<int> size)
    {
        _screens.Invalidate();
        ScalingChanged?.Invoke(RenderScaling);
        QueuePaint();
    }

    private void OnMove(Vector2D<int> position)
    {
        _screens.Invalidate();
        PositionChanged?.Invoke(Position);
    }

    private void OnFocusChanged(bool focused)
    {
        if (focused)
        {
            Volatile.Write(
                ref _zOrder,
                _platform.BringToFront());
            Activated?.Invoke();
        }
        else
        {
            Deactivated?.Invoke();
            LostFocus?.Invoke();
        }
    }

    private void OnStateChanged(SilkWindowState state)
    {
        _windowState = FromSilkState(state);
        WindowStateChanged?.Invoke(_windowState);
    }

    private void OnClosing()
    {
        if (Interlocked.Exchange(ref _closeCallback, 1) != 0)
            return;
        try
        {
            if (Closing?.Invoke(
                    WindowCloseReason.WindowClosing) == true)
            {
                if (_window is not null)
                    _window.IsClosing = false;
                return;
            }

            Dispose();
        }
        finally
        {
            Volatile.Write(ref _closeCallback, 0);
        }
    }

    private void OnWebGpuDeviceLost(
        Silk.NET.WebGPU.DeviceLostReason reason,
        string message)
    {
        _paintQueued = true;
        _platform.EventLoop.Wake();
    }

    private void QueuePaint()
    {
        _paintQueued = true;
        _platform.EventLoop.Wake();
    }

    private void ApplyIcon()
    {
        if (_window is null)
            return;
        if (_icon is SilkNetWindowIcon icon)
        {
            RawImage? decoded = icon.TryDecode();
            if (decoded is { } image)
                _window.SetWindowIcon([image]);
            else
                _window.SetWindowIcon([]);
        }
        else
        {
            _window.SetWindowIcon([]);
        }
    }

    private void MoveAndResizePopup(
        PixelPoint position,
        Size size,
        double scaling)
    {
        Move(position);
        Resize(size);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            _disposedState,
            this);

    private static AvaloniaWindowState FromSilkState(
        SilkWindowState state) =>
        state switch
        {
            SilkWindowState.Minimized =>
                AvaloniaWindowState.Minimized,
            SilkWindowState.Maximized =>
                AvaloniaWindowState.Maximized,
            SilkWindowState.Fullscreen =>
                AvaloniaWindowState.FullScreen,
            _ => AvaloniaWindowState.Normal
        };

    private static SilkWindowState ToSilkState(
        AvaloniaWindowState state) =>
        state switch
        {
            AvaloniaWindowState.Minimized =>
                SilkWindowState.Minimized,
            AvaloniaWindowState.Maximized =>
                SilkWindowState.Maximized,
            AvaloniaWindowState.FullScreen =>
                SilkWindowState.Fullscreen,
            _ => SilkWindowState.Normal
        };
}
