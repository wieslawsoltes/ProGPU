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
    private WindowImpl? _parent;
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
    private SilkWindowController? _windowController;
    private WgpuContext? _webGpuContext;
    private SilkNetCursorImpl? _cursor;
    private IWindowIconImpl? _icon;
    private string _title = string.Empty;
    private Size _desiredSize = new(800, 600);
    private PixelPoint? _desiredPosition;
    private Size _minSize;
    private Size _maxSize = new(double.PositiveInfinity, double.PositiveInfinity);
    private AvaloniaWindowState _windowState;
    private NativeWindowDecorations _decorations =
        NativeWindowDecorations.Full;
    private NativeWindowTheme _theme = NativeWindowTheme.Light;
    private NativeWindowBackdrop _backdrop = NativeWindowBackdrop.None;
    private SilkWindowBorder _windowBorder = SilkWindowBorder.Resizable;
    private WindowTransparencyLevel _transparencyLevel =
        WindowTransparencyLevel.None;
    private bool _transparentRequested;
    private bool _topmost;
    private bool _enabled = true;
    private bool _showInTaskbar = true;
    private bool _canResize = true;
    private bool _canMinimize = true;
    private bool _canMaximize = true;
    private bool _addShadow = true;
    private bool _extendClientArea;
    private double _titleBarHeight = -1d;
    private bool _visible;
    private bool _paintQueued = true;
    private bool _disposedState;
#if AVALONIA11
    private bool _isHitTestVisible = true;
#endif
    private int _closeCallback;
    private long _zOrder;
    private double _reportedScaling = 1d;
    private double? _nativeDisplayScale;

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
            _decorations = NativeWindowDecorations.None;
            _windowBorder = SilkWindowBorder.Hidden;
            _topmost = true;
            _showInTaskbar = false;
            _canResize = false;
            _canMinimize = false;
            _canMaximize = false;
            _addShadow = false;
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

    internal long ZOrder =>
        SilkNetWindowingPlatform.ResolveZOrder(
            Volatile.Read(ref _zOrder),
            _topmost);
    internal SilkNetWindowingPlatform Platform => _platform;
    internal IWindow? NativeWindow => _window;
    internal NativeWindowHandle NativeParentHandle =>
        _windowController?.Parent ??
        NativeWindowHandle.Empty;

    public double DesktopScaling =>
        SilkNetDisplayMetrics.ResolveDesktopScaling(
            OperatingSystem.IsMacOS(),
            RenderScaling);

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
            if (window is null)
                return 1;

            double reportedScale =
                SilkNetDisplayMetrics.ResolveReportedFramebufferScale(
                    window.Size.X,
                    window.Size.Y,
                    window.FramebufferSize.X,
                    window.FramebufferSize.Y);
            return SilkNetDisplayMetrics.ResolveRenderScaling(
                window.Size.X,
                window.Size.Y,
                window.FramebufferSize.X,
                window.FramebufferSize.Y,
                reportedScale > 1d
                    ? null
                    : _nativeDisplayScale);
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

            return SilkNetDisplayMetrics.ResolveFramebufferPixelSize(
                window.Size.X,
                window.Size.Y,
                window.FramebufferSize.X,
                window.FramebufferSize.Y,
                RenderScaling);
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

    public Size? FrameSize =>
        SilkNetDisplayMetrics.ResolveFrameSize(
            ClientSize,
            _windowController is { IsAttached: true } controller
                ? controller.FrameInsets
                : null);

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
            {
                _windowController?.PrepareForStateTransition();
                _window.WindowState = ToSilkState(value);
            }
        }
    }

#if !AVALONIA11
    public bool WindowStateGetterIsUsable => true;
#endif
    public bool IsClientAreaExtendedToDecorations =>
        _extendClientArea;
    public bool NeedsManagedDecorations =>
        _windowController?.RequiresManagedDecorations ?? false;
#if !AVALONIA11
    public PlatformRequestedDrawnDecoration RequestedDrawnDecorations =>
        SilkNetWindowChrome.MapRequestedDrawnDecorations(
            _windowController?.RequestedDrawnDecorations ??
            NativeDrawnDecorationParts.None);
#endif
    public Thickness ExtendedMargins
    {
        get
        {
            SilkWindowController? controller = _windowController;
            if (!_extendClientArea ||
                NeedsManagedDecorations ||
                _decorations != NativeWindowDecorations.Full ||
                WindowState == AvaloniaWindowState.FullScreen ||
                controller is null)
            {
                return default;
            }

            return new Thickness(
                0,
                controller.ExtendedTitleBarHeight,
                0,
                0);
        }
    }
    public Thickness OffScreenMargin => default;
#if !AVALONIA11
    public PlatformAllowedWindowActions AllowedWindowActions =>
        SilkNetWindowChrome.GetAllowedWindowActions(
            _canResize,
            _canMinimize,
            _canMaximize);
#endif
    public WindowTransparencyLevel TransparencyLevel =>
        _transparencyLevel;
    public AcrylicPlatformCompensationLevels AcrylicCompensationLevels =>
        new(1, 0.8, 0);
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
        ArgumentNullException.ThrowIfNull(transparencyLevels);
        NativeWindowCapabilities capabilities =
            _windowController?.Capabilities ??
            NativeWindowCapabilities.ForKind(
                NativeWindowCapabilities.DetectCurrentKind());
        SilkNetTransparencyChoice choice =
            SilkNetWindowChrome.SelectTransparency(
                transparencyLevels,
                capabilities);
        _transparentRequested =
            choice.Level != WindowTransparencyLevel.None;
        _backdrop = choice.Backdrop;
        _windowController?.SetBackdrop(_backdrop);

        if (_transparencyLevel != choice.Level)
        {
            _transparencyLevel = choice.Level;
            TransparencyLevelChanged?.Invoke(choice.Level);
        }
    }

    public void SetFrameThemeVariant(
#if AVALONIA11
        PlatformThemeVariant themeVariant)
#else
        PlatformThemeVariant? themeVariant)
#endif
    {
#if AVALONIA11
        _theme = themeVariant == PlatformThemeVariant.Dark
            ? NativeWindowTheme.Dark
            : NativeWindowTheme.Light;
#else
        _theme = themeVariant switch
        {
            null => NativeWindowTheme.Default,
            { } value when value == PlatformThemeVariant.Dark =>
                NativeWindowTheme.Dark,
            _ => NativeWindowTheme.Light
        };
#endif
        _windowController?.SetTheme(_theme);
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
        _windowController?.SetTopMost(value);
    }

    public void SetTitle(string? title)
    {
        _title = title ?? string.Empty;
        if (_window is not null)
            _window.Title = _title;
    }

    public void SetParent(IWindowImpl? parent)
    {
        if (ReferenceEquals(parent, this))
            throw new ArgumentException(
                "A window cannot own itself.",
                nameof(parent));
        if (parent is not null and not WindowImpl)
            throw new ArgumentException(
                "The owner must use the Silk.NET windowing backend.",
                nameof(parent));

        _parent = (WindowImpl?)parent;
        ApplyNativeParent();
    }

    public void SetEnabled(bool enable)
    {
        _enabled = enable;
        _windowController?.SetEnabled(enable);
    }

#if AVALONIA11
    public void SetSystemDecorations(SystemDecorations enabled)
    {
        SetNativeDecorations(
            enabled switch
            {
                SystemDecorations.None =>
                    NativeWindowDecorations.None,
                SystemDecorations.BorderOnly =>
                    NativeWindowDecorations.BorderOnly,
                _ => NativeWindowDecorations.Full
            });
    }
#else
    public void SetWindowDecorations(WindowDecorations enabled)
    {
        SetNativeDecorations(
            enabled switch
            {
                WindowDecorations.None =>
                    NativeWindowDecorations.None,
                WindowDecorations.BorderOnly =>
                    NativeWindowDecorations.BorderOnly,
                _ => NativeWindowDecorations.Full
            });
    }
#endif

    public void SetIcon(IWindowIconImpl? icon)
    {
        _icon = icon;
        ApplyIcon();
    }

    public void ShowTaskbarIcon(bool value)
    {
        _showInTaskbar = value;
        _windowController?.SetShowInTaskbar(value);
    }

    public void CanResize(bool value)
    {
        if (_canResize == value)
            return;
        _canResize = value;
        UpdateSilkWindowBorder();
        if (_window is not null)
            _window.WindowBorder = _windowBorder;
        _windowController?.SetCanResize(value);
        RaiseAllowedWindowActionsChanged();
    }

    public void SetCanMinimize(bool value)
    {
        if (_canMinimize == value)
            return;
        _canMinimize = value;
        _windowController?.SetCanMinimize(value);
        RaiseAllowedWindowActionsChanged();
    }

    public void SetCanMaximize(bool value)
    {
        if (_canMaximize == value)
            return;
        _canMaximize = value;
        _windowController?.SetCanMaximize(value);
        RaiseAllowedWindowActionsChanged();
    }

    public void BeginMoveDrag(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_input is not null)
            _windowController?.BeginMove(
                _input.CurrentNativePointer);
    }

    public void BeginResizeDrag(
        WindowEdge edge,
        PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_input is not null)
        {
            _windowController?.BeginResize(
                SilkNetWindowChrome.MapResizeEdge(edge),
                _input.CurrentNativePointer);
        }
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
        _windowController?.SetSizeConstraints(
            SilkNetWindowChrome.ToMinimumSize(_minSize),
            SilkNetWindowChrome.ToMaximumSize(_maxSize));
    }

    public void SetExtendClientAreaToDecorationsHint(
        bool extendIntoClientAreaHint)
    {
        if (_extendClientArea == extendIntoClientAreaHint)
            return;
        _extendClientArea = extendIntoClientAreaHint;
        UpdateSilkWindowBorder();
        if (_window is not null)
            _window.WindowBorder = _windowBorder;
        _windowController?.SetClientAreaExtension(
            _extendClientArea,
            _titleBarHeight);
        ExtendClientAreaToDecorationsChanged?.Invoke(
            _extendClientArea);
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
        {
            zOrder[index] =
                windows[index].PlatformImpl is WindowImpl window
                    ? window.ZOrder
                    : long.MinValue;
        }
    }
#endif

    public void SetExtendClientAreaTitleBarHeightHint(
        double titleBarHeight)
    {
        _titleBarHeight =
            double.IsFinite(titleBarHeight) &&
            titleBarHeight >= 0
                ? titleBarHeight
                : -1;
        _windowController?.SetTitleBarHeight(
            _titleBarHeight);
        if (_extendClientArea)
        {
            ExtendClientAreaToDecorationsChanged?.Invoke(
                true);
        }
    }

    public void SetWindowManagerAddShadowHint(bool enabled)
    {
        _addShadow = enabled;
        _windowController?.SetWindowShadow(enabled);
    }

#if AVALONIA11
    public void SetHitTestVisible(bool isHitTestVisible)
    {
        _isHitTestVisible = isHitTestVisible;
        IWindow? window = _window;
        if (window is null || !window.IsInitialized)
            return;

        _platform.Monitors.SetMousePassthrough(
            window.Handle,
            !isHitTestVisible);
    }
#endif

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
        _screens.Dispose();
        _framebufferSurface.Dispose();

        WgpuContext? context = _webGpuContext;
        _webGpuContext = null;
        SharedWebGpuDevices.Release(context);

        SilkWindowController? controller =
            _windowController;
        _windowController = null;
        controller?.Dispose();

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
        ArgumentNullException.ThrowIfNull(input);
        Input?.Invoke(input);
    }

    internal bool TryAcceptInput()
    {
        if (_disposedState)
            return false;
        if (_enabled)
            return true;

        GotInputWhenDisabled?.Invoke();
        return false;
    }

    internal NativeWindowPoint ToNativeScreenPoint(
        float clientX,
        float clientY)
    {
        IWindow? window = _window;
        if (window is null)
            return default;
        return new NativeWindowPoint(
            checked(
                window.Position.X +
                (int)MathF.Round(clientX)),
            checked(
                window.Position.Y +
                (int)MathF.Round(clientY)));
    }

    internal void UpdateNativeDrag(
        NativeWindowPoint pointer) =>
        _windowController?.UpdateDrag(pointer);

    internal void EndNativeDrag() =>
        _windowController?.EndDrag();

    void ISilkNetLoopParticipant.PollNativeEvents() =>
        _window?.DoEvents();

    void ISilkNetLoopParticipant.UpdateNativeWindow()
    {
        IWindow? window = _window;
        window?.DoUpdate();
        if (window is not null)
            _input?.ProcessNativeState(window);
    }

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
        IWindow window =
            Silk.NET.Windowing.Window.Create(options);
        _window = window;
        _windowController =
            new SilkWindowController(window);
        ApplyNativeWindowState();
        window.Load += OnLoad;
        window.Render += OnRender;
        window.Resize += OnResize;
        window.FramebufferResize += OnFramebufferResize;
        window.Move += OnMove;
        window.FocusChanged += OnFocusChanged;
        window.StateChanged += OnStateChanged;
        window.Closing += OnClosing;
        window.Initialize();
        NotifyScalingChanged();
        if (_desiredPosition is { } desired)
            Move(desired);
        _platform.EventLoop.Register(this);
        return window;
    }

    private void OnLoad()
    {
        if (_window is null)
            return;
        _platform.Monitors.Attach();
        _windowController?.Attach();
#if AVALONIA11
        if (!_isHitTestVisible)
            SetHitTestVisible(false);
#endif
        ApplyNativeParent();
        if (_extendClientArea)
        {
            ExtendClientAreaToDecorationsChanged?.Invoke(
                true);
        }
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
        _ = size;
        NotifyScalingChanged();
        QueuePaint();
    }

    private void OnMove(Vector2D<int> position)
    {
        _ = position;
        NotifyScalingChanged();
        PositionChanged?.Invoke(Position);
    }

    private void NotifyScalingChanged()
    {
        IWindow? window = _window;
        if (window is not null)
        {
            double reportedScale =
                SilkNetDisplayMetrics.ResolveReportedFramebufferScale(
                    window.Size.X,
                    window.Size.Y,
                    window.FramebufferSize.X,
                    window.FramebufferSize.Y);
            _nativeDisplayScale = reportedScale > 1d
                ? null
                : DisplayScaleResolver
                    .TryResolveNativeWindowDisplayScale(window);
        }

        double scaling = RenderScaling;
        if (Math.Abs(scaling - _reportedScaling) <= 0.0001d)
            return;

        _reportedScaling = scaling;
        _screens.Invalidate();
        ScalingChanged?.Invoke(scaling);
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
        _windowController?.Reapply();
        WindowStateChanged?.Invoke(_windowState);
        if (_extendClientArea)
        {
            ExtendClientAreaToDecorationsChanged?.Invoke(
                true);
        }
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

    private void SetNativeDecorations(
        NativeWindowDecorations decorations)
    {
        if (_decorations == decorations)
            return;
        _decorations = decorations;
        UpdateSilkWindowBorder();
        if (_window is not null)
            _window.WindowBorder = _windowBorder;
        _windowController?.SetDecorations(decorations);
        if (_extendClientArea)
        {
            ExtendClientAreaToDecorationsChanged?.Invoke(
                true);
        }
    }

    private void UpdateSilkWindowBorder()
    {
        _windowBorder =
            SilkNetWindowChrome.GetInitialWindowBorder(
                _decorations,
                _extendClientArea,
                _canResize);
    }

    private void ApplyNativeWindowState()
    {
        SilkWindowController? controller =
            _windowController;
        if (controller is null)
            return;

        controller.SetDecorations(_decorations);
        controller.SetCanResize(_canResize);
        controller.SetCanMinimize(_canMinimize);
        controller.SetCanMaximize(_canMaximize);
        controller.SetTopMost(_topmost);
        controller.SetEnabled(_enabled);
        controller.SetShowInTaskbar(_showInTaskbar);
        controller.SetSizeConstraints(
            SilkNetWindowChrome.ToMinimumSize(_minSize),
            SilkNetWindowChrome.ToMaximumSize(_maxSize));
        controller.SetClientAreaExtension(
            _extendClientArea,
            _titleBarHeight);
        controller.SetTheme(_theme);
        controller.SetBackdrop(_backdrop);
        controller.SetWindowShadow(_addShadow);
    }

    private void ApplyNativeParent()
    {
        SilkWindowController? controller =
            _windowController;
        if (controller is null)
            return;

        NativeWindowHandle handle =
            NativeWindowHandle.Empty;
        if (_parent is { _disposedState: false } parent)
        {
            parent.EnsureNativeWindow();
            handle =
                parent._windowController?.Handle ??
                NativeWindowHandle.Empty;
        }
        controller.SetParent(handle);
    }

    private void RaiseAllowedWindowActionsChanged()
    {
#if !AVALONIA11
        AllowedWindowActionsChanged?.Invoke(
            AllowedWindowActions);
#endif
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
