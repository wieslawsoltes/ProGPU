using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Windowing;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml;

public readonly record struct WindowFrameMetrics(
    double DispatcherTimeMs,
    double RenderingCallbackTimeMs,
    double FrameSetupTimeMs,
    double AnimationTimeMs,
    double LayoutTimeMs,
    double SurfaceAcquireTimeMs,
    double CompositorTimeMs,
    double PresentTimeMs,
    double TotalTimeMs);

public class Window : DependencyObject
{
    private IWindow? _silkWindow;
    private WgpuContext? _wgpuContext;
    private Compositor? _compositor;
    private FrameworkElement? _content;
    private string _title = "ProGPU Window";
    private int _width = 1280;
    private int _height = 800;
    private WindowInputState? _inputState;
    private SilkWindowController? _windowController;
    private NativeWindowDecorations _decorations = NativeWindowDecorations.Full;
    private bool _canResize = true;
    private bool _canMinimize = true;
    private bool _canMaximize = true;
    private bool _topMost;
    private bool _extendsContentIntoTitleBar;
    private double _titleBarHeight = -1d;
    private NativeWindowSize _minimumSize;
    private NativeWindowSize _maximumSize = NativeWindowSize.Unbounded;
    private SystemBackdrop? _systemBackdrop;
    private readonly Grid _renderRoot;
    private readonly Grid _contentRoot;
    private readonly Border _backdropLayer;
    private bool _isEnabled = true;
    private bool _showInTaskbar = true;
    private Window? _owner;
    private bool _isRendering;
    private bool _isExternalHostActive;
    private bool _isClosed;
    private bool _visible;
    private Windows.Foundation.Rect _bounds = new(0, 0, 1280, 800);
    private UIElement? _titleBar;
    private Thickness _safeAreaInsets;
    private Windows.Foundation.Rect _inputPaneOccludedRect;
    private WindowInsets _insets;
    private bool _extendsContentIntoSystemInsets;
    private bool _avoidInputPane = true;

    public IWindow? SilkWindow => _silkWindow;
    public WgpuContext? WgpuContext => _wgpuContext;
    public Compositor? Compositor => _compositor;
    public WindowInputState? InputState => _inputState;
    public SilkWindowController? NativeWindowController => _windowController;
    public NativeWindowHandle NativeHandle => _windowController?.Handle ?? NativeWindowHandle.Empty;
    public NativeWindowCapabilities NativeCapabilities =>
        _windowController?.Capabilities ??
        NativeWindowCapabilities.ForKind(NativeWindowCapabilities.DetectCurrentKind());
    public NativeWindowFrameInsets FrameInsets =>
        _windowController?.FrameInsets ?? NativeWindowFrameInsets.Empty;
    public bool IsUsingSystemBackdropFallback { get; private set; }
    public WindowFrameMetrics FrameMetrics { get; private set; }
    public Windows.Foundation.Rect Bounds => _bounds;
    public bool Visible => _visible;
    public WindowInsets Insets => _insets;
    public Windows.UI.ViewManagement.InputPane InputPane { get; }

    /// <summary>
    /// When false (the default), content is arranged inside platform safe areas.
    /// Backdrops and compositor overlays continue to cover the complete surface.
    /// </summary>
    public bool ExtendsContentIntoSystemInsets
    {
        get => _extendsContentIntoSystemInsets;
        set
        {
            if (_extendsContentIntoSystemInsets == value) return;
            _extendsContentIntoSystemInsets = value;
            ApplyContentInsets();
        }
    }

    /// <summary>Automatically keeps ordinary window content above a docked input pane.</summary>
    public bool AvoidInputPane
    {
        get => _avoidInputPane;
        set
        {
            if (_avoidInputPane == value) return;
            _avoidInputPane = value;
            ApplyContentInsets();
        }
    }

    /// <summary>
    /// Configures the bounded glyph cache before activation. Font-inspection tools
    /// that intentionally traverse thousands of unique glyphs can opt into a larger
    /// atlas without increasing the default footprint of ordinary applications.
    /// </summary>
    public uint GlyphAtlasSize { get; set; } = CompositorOptions.Default.GlyphAtlasSize;

    public NativeWindowDecorations Decorations
    {
        get => _decorations;
        set
        {
            _decorations = value;
            _windowController?.SetDecorations(value);
        }
    }

    public bool CanResize
    {
        get => _canResize;
        set
        {
            _canResize = value;
            _windowController?.SetCanResize(value);
        }
    }

    public bool CanMinimize
    {
        get => _canMinimize;
        set
        {
            _canMinimize = value;
            _windowController?.SetCanMinimize(value);
        }
    }

    public bool CanMaximize
    {
        get => _canMaximize;
        set
        {
            _canMaximize = value;
            _windowController?.SetCanMaximize(value);
        }
    }

    public bool TopMost
    {
        get => _topMost;
        set
        {
            _topMost = value;
            _windowController?.SetTopMost(value);
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            _renderRoot.IsEnabled = value;
            _windowController?.SetEnabled(value);
        }
    }

    public bool ShowInTaskbar
    {
        get => _showInTaskbar;
        set
        {
            _showInTaskbar = value;
            _windowController?.SetShowInTaskbar(value);
        }
    }

    public Window? Owner
    {
        get => _owner;
        set
        {
            _owner = value;
            _windowController?.SetParent(value?.NativeHandle ?? NativeWindowHandle.Empty);
        }
    }

    public bool ExtendsContentIntoTitleBar
    {
        get => _extendsContentIntoTitleBar;
        set
        {
            _extendsContentIntoTitleBar = value;
            _windowController?.SetClientAreaExtension(value, _titleBarHeight);
        }
    }

    public double TitleBarHeight
    {
        get => _titleBarHeight;
        set
        {
            _titleBarHeight = double.IsFinite(value) && value >= 0d ? value : -1d;
            _windowController?.SetTitleBarHeight(_titleBarHeight);
        }
    }

    public SystemBackdrop? SystemBackdrop
    {
        get => _systemBackdrop;
        set
        {
            if (ReferenceEquals(_systemBackdrop, value))
            {
                return;
            }
            if (_systemBackdrop != null)
            {
                _systemBackdrop.Changed -= OnSystemBackdropChanged;
            }
            _systemBackdrop = value;
            if (_systemBackdrop != null)
            {
                _systemBackdrop.Changed += OnSystemBackdropChanged;
            }
            ApplySystemBackdrop();
        }
    }

    public FrameworkElement? Content
    {
        get => _content;
        set
        {
            if (ReferenceEquals(_content, value))
            {
                return;
            }
            if (_content != null)
            {
                _contentRoot.RemoveChild(_content);
            }
            _content = value;
            if (value != null)
            {
                _contentRoot.AddChild(value);
            }
            if (_inputState != null)
            {
                _inputState.Root = _renderRoot;
            }
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            if (_silkWindow != null) _silkWindow.Title = value;
        }
    }

    public int Width
    {
        get => _silkWindow?.Size.X ?? _width;
        set
        {
            _width = value;
            _bounds = _bounds with { Width = value };
            if (_silkWindow != null) _silkWindow.Size = new Vector2D<int>(_width, _silkWindow.Size.Y);
        }
    }

    public int Height
    {
        get => _silkWindow?.Size.Y ?? _height;
        set
        {
            _height = value;
            _bounds = _bounds with { Height = value };
            if (_silkWindow != null) _silkWindow.Size = new Vector2D<int>(_silkWindow.Size.X, _height);
        }
    }

    public event Windows.Foundation.TypedEventHandler<object, WindowActivatedEventArgs>? Activated;
    public event Windows.Foundation.TypedEventHandler<object, WindowEventArgs>? Closed;
    public event Windows.Foundation.TypedEventHandler<object, WindowSizeChangedEventArgs>? SizeChanged;
    public event Windows.Foundation.TypedEventHandler<object, WindowVisibilityChangedEventArgs>? VisibilityChanged;
    public event Windows.Foundation.TypedEventHandler<object, WindowInsetsChangedEventArgs>? InsetsChanged;
    public event EventHandler<double>? Rendering;

    public Window()
    {
        _renderRoot = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _backdropLayer = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        _contentRoot = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        InputPane = new Windows.UI.ViewManagement.InputPane(this);
        _renderRoot.AddChild(_backdropLayer);
        _renderRoot.AddChild(_contentRoot);
        ThemeManager.ThemeChanged += HandleThemeChanged;
    }

    private void HandleThemeChanged()
    {
        _windowController?.SetTheme(
            ThemeManager.CurrentTheme == ElementTheme.Dark
                ? NativeWindowTheme.Dark
                : NativeWindowTheme.Light);
        _content?.NotifyThemeChanged();
        ApplySystemBackdrop();
        foreach (var popup in Controls.PopupService.ActivePopups)
        {
            popup.NotifyThemeChanged();
        }
    }

    private void OnSystemBackdropChanged()
    {
        ApplySystemBackdrop();
    }

    public void Activate()
    {
        ObjectDisposedException.ThrowIf(_isClosed, this);
        if (WindowHostServices.Current is { } externalHost)
        {
            if (_isExternalHostActive) return;
            externalHost.Activate(this);
            _isExternalHostActive = true;
            WindowManager.Register(this);
            NotifyHostVisibilityChanged(true);
            NotifyHostActivationChanged(WindowActivationState.CodeActivated);
            return;
        }

        if (_silkWindow != null)
        {
            _silkWindow.IsVisible = true;
            _silkWindow.Focus();
            return;
        }

        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(_width, _height);
        options.Title = _title;
        options.API = GraphicsAPI.None;
        options.VSync = false;
        options.TransparentFramebuffer = true;
        options.TopMost = _topMost;
        options.WindowBorder = _decorations switch
        {
            NativeWindowDecorations.None => WindowBorder.Hidden,
            NativeWindowDecorations.BorderOnly => WindowBorder.Fixed,
            _ => _canResize ? WindowBorder.Resizable : WindowBorder.Fixed
        };

        _silkWindow = Silk.NET.Windowing.Window.Create(options);
        _windowController = new SilkWindowController(_silkWindow);
        _silkWindow.Load += OnLoad;
        _silkWindow.Render += OnRender;
        _silkWindow.Resize += OnResize;
        _silkWindow.FramebufferResize += OnFramebufferResize;
        _silkWindow.Closing += OnClosing;

        _silkWindow.Initialize();
        WindowManager.Register(this);
        UpdateBounds(_silkWindow.Size.X, _silkWindow.Size.Y);
        NotifyHostVisibilityChanged(true);
        NotifyHostActivationChanged(WindowActivationState.CodeActivated);
    }

    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Activate();
        return Task.CompletedTask;
    }

    public void Close()
    {
        if (_isExternalHostActive && WindowHostServices.Current is { } externalHost)
        {
            externalHost.Close(this);
            return;
        }
        if (_silkWindow == null) return;
        _silkWindow.Close();
    }

    public void Hide()
    {
        if (_isExternalHostActive && WindowHostServices.Current is { } externalHost)
        {
            externalHost.Hide(this);
            return;
        }
        if (_silkWindow != null)
        {
            _silkWindow.IsVisible = false;
        }
        NotifyHostVisibilityChanged(false);
        NotifyHostActivationChanged(WindowActivationState.Deactivated);
    }

    /// <summary>
    /// Selects the element used as the app-defined title bar. This mirrors the WinUI API;
    /// native hosts may use the value to initiate platform window dragging.
    /// </summary>
    public void SetTitleBar(UIElement? titleBar)
    {
        _titleBar = titleBar;
    }

    internal UIElement? AppTitleBar => _titleBar;

    private void OnLoad()
    {
        if (_silkWindow == null) return;

        _windowController?.Attach();
        ApplyWindowSettings();

        _wgpuContext = new WgpuContext();
        _wgpuContext.Initialize(_silkWindow);
        var framebufferSize = GetCurrentFramebufferSize();
        var sampleCount = ResolveWindowDpiScale(framebufferSize) >= 1.5f ? 1u : 4u;
        _compositor = new Compositor(
            _wgpuContext,
            _wgpuContext.SwapChainFormat,
            CompositorOptions.Default with
            {
                EnableGpuHitTesting = false,
                GlyphAtlasSize = GlyphAtlasSize,
                PrimarySampleCount = sampleCount
            });
        ApplySystemBackdrop();
        ConfigureCompositorHooks();

        string fontPath = "/System/Library/Fonts/Supplemental/Arial.ttf";
        if (!System.IO.File.Exists(fontPath)) fontPath = "Arial.ttf";

        if (System.IO.File.Exists(fontPath))
        {
            PopupService.DefaultFont = new ProGPU.Text.TtfFont(fontPath);
        }

        var inputContext = _silkWindow.CreateInput();
        _inputState = InputSystem.Initialize(inputContext, _renderRoot, NormalizePointerPosition);
    }

    private void ConfigureCompositorHooks()
    {
        if (_compositor == null) return;
        _compositor.PreRender += (w, h) => PopupService.MeasureAndArrangePopups(new Vector2(w, h));
        _compositor.GetExternalLayers = () => PopupService.ActivePopups;
        _compositor.GetTooltip = () => InputSystem.ActiveToolTip;
        _compositor.GetMousePosition = () => InputSystem.LastMousePosition;
        _compositor.HasDynamicDiagnostics = () => DevToolsService.IsDevToolsActive || DragDropManager.IsDragging;
        _compositor.RenderDiagnostics = (diagContext, w, h) =>
        {
            if (DevToolsService.IsDevToolsActive)
            {
                AdornerLayer.Render(diagContext, w, h);
            }
            DragDropManager.RenderDragVisual(diagContext, w, h);
        };
    }

    public void InitializeExternalRenderer(WgpuContext context, float dpiScale)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_wgpuContext != null || _compositor != null)
            throw new InvalidOperationException("The window renderer is already initialized.");
        _wgpuContext = context;
        var sampleCount = dpiScale >= 1.5f ? 1u : 4u;
        _compositor = new Compositor(
            context,
            context.SwapChainFormat,
            CompositorOptions.Default with
            {
                EnableGpuHitTesting = false,
                GlyphAtlasSize = GlyphAtlasSize,
                PrimarySampleCount = sampleCount
            });
        ApplySystemBackdrop();
        ConfigureCompositorHooks();
        _inputState = InputSystem.CreateExternalState(_renderRoot);
    }

    public void RenderExternalFrame(double delta, uint framebufferWidth, uint framebufferHeight, float dpiScale)
    {
        if (_isRendering || _wgpuContext == null || _compositor == null) return;
        var scale = float.IsFinite(dpiScale) && dpiScale > 0f ? dpiScale : 1f;
        var framebufferSize = new Vector2D<int>(checked((int)Math.Max(1u, framebufferWidth)), checked((int)Math.Max(1u, framebufferHeight)));
        var logicalSize = ResolveLogicalClientSize(framebufferSize, scale);
        UpdateBounds(logicalSize.X, logicalSize.Y);
        _isRendering = true;
        try
        {
            RenderFrameCore(delta, framebufferSize, scale, logicalSize);
        }
        finally
        {
            _isRendering = false;
        }
    }

    public void ShutdownExternalRenderer()
    {
        SuspendExternalRenderer();
        _isExternalHostActive = false;
        NotifyHostVisibilityChanged(false);
        NotifyHostActivationChanged(WindowActivationState.Deactivated);
        WindowManager.Unregister(this);
        RaiseClosed();
        DetachWindowServices();
    }

    /// <summary>
    /// Releases renderer-owned resources while preserving the WinUI window and its
    /// application state. Mobile hosts use this when a native presentation surface is
    /// temporarily destroyed and initialize a replacement renderer when it returns.
    /// </summary>
    public void SuspendExternalRenderer()
    {
        _compositor?.Dispose();
        _compositor = null;
        _wgpuContext = null;
        _inputState = null;
    }

    /// <summary>
    /// Updates activation state from an external platform host.
    /// </summary>
    public void NotifyHostActivationChanged(WindowActivationState state)
    {
        if (_isClosed) return;
        Activated?.Invoke(this, new WindowActivatedEventArgs(state));
    }

    /// <summary>
    /// Updates visibility from an external platform host.
    /// </summary>
    public void NotifyHostVisibilityChanged(bool visible)
    {
        if (_isClosed || _visible == visible) return;
        _visible = visible;
        VisibilityChanged?.Invoke(this, new WindowVisibilityChangedEventArgs(visible));
    }

    /// <summary>Updates safe-area and input-pane geometry from an external native host.</summary>
    public void NotifyHostInsetsChanged(
        Thickness safeArea,
        Windows.Foundation.Rect inputPaneOccludedRect)
    {
        safeArea = NormalizeInsets(safeArea);
        inputPaneOccludedRect = NormalizeOccludedRect(inputPaneOccludedRect);
        if (_safeAreaInsets.Equals(safeArea) && _inputPaneOccludedRect.Equals(inputPaneOccludedRect)) return;

        _safeAreaInsets = safeArea;
        _inputPaneOccludedRect = inputPaneOccludedRect;
        ApplyContentInsets();
        bool ensuredFocusedElement = InputPaneVisible(inputPaneOccludedRect) && EnsureFocusedElementInView();
        InputPane.UpdateOccludedRect(inputPaneOccludedRect, ensuredFocusedElement);
    }

    /// <summary>Installs best-effort platform software-keyboard show/hide callbacks.</summary>
    public void ConfigureInputPane(Func<bool>? tryShow, Func<bool>? tryHide) =>
        InputPane.SetPlatformCallbacks(tryShow, tryHide);

    private void ApplyContentInsets()
    {
        double width = Math.Max(0d, _bounds.Width);
        double height = Math.Max(0d, _bounds.Height);
        Thickness safe = _extendsContentIntoSystemInsets ? default : _safeAreaInsets;
        float keyboardBottom = 0f;

        if (_avoidInputPane && IsDockedBottomOcclusion(_inputPaneOccludedRect, width, height))
        {
            keyboardBottom = (float)Math.Clamp(height - _inputPaneOccludedRect.Y, 0d, height);
        }

        var effective = new Thickness(
            safe.Left,
            safe.Top,
            safe.Right,
            Math.Max(safe.Bottom, keyboardBottom));
        _contentRoot.Margin = effective;

        double visibleX = Math.Clamp(effective.Left, 0f, (float)width);
        double visibleY = Math.Clamp(effective.Top, 0f, (float)height);
        double visibleWidth = Math.Max(0d, width - effective.Left - effective.Right);
        double visibleHeight = Math.Max(0d, height - effective.Top - effective.Bottom);
        var next = new WindowInsets(
            _safeAreaInsets,
            _inputPaneOccludedRect,
            new Windows.Foundation.Rect(visibleX, visibleY, visibleWidth, visibleHeight));
        if (_insets.Equals(next)) return;
        _insets = next;
        _contentRoot.InvalidateMeasure();
        _contentRoot.Invalidate();
        InsetsChanged?.Invoke(this, new WindowInsetsChangedEventArgs(next));
    }

    private static bool IsDockedBottomOcclusion(Windows.Foundation.Rect rect, double width, double height) =>
        rect.Width > 0d && rect.Height > 0d &&
        rect.Width >= width * 0.9d &&
        rect.Y + rect.Height >= height - 1d;

    private static bool InputPaneVisible(Windows.Foundation.Rect rect) =>
        rect.Width > 0d && rect.Height > 0d;

    private bool EnsureFocusedElementInView()
    {
        FrameworkElement? focused = InputSystem.FocusedElement;
        if (focused == null || focused.Size.X <= 0f || focused.Size.Y <= 0f) return false;

        bool belongsToWindow = false;
        for (Visual? ancestor = focused; ancestor != null; ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(ancestor, _renderRoot))
            {
                belongsToWindow = true;
                break;
            }
        }
        if (!belongsToWindow) return false;

        Rect focusedBounds = focused.TransformToVisual(_renderRoot)
            .TransformBounds(new Rect(0f, 0f, focused.Size.X, focused.Size.Y));
        const float revealPadding = 12f;
        float visibleLeft = (float)_insets.VisibleBounds.X + revealPadding;
        float visibleTop = (float)_insets.VisibleBounds.Y + revealPadding;
        float visibleRight = (float)(_insets.VisibleBounds.X + _insets.VisibleBounds.Width) - revealPadding;
        float visibleBottom = (float)(_insets.VisibleBounds.Y + _insets.VisibleBounds.Height) - revealPadding;
        float deltaX = focusedBounds.Right > visibleRight
            ? focusedBounds.Right - visibleRight
            : focusedBounds.X < visibleLeft ? focusedBounds.X - visibleLeft : 0f;
        float deltaY = focusedBounds.Bottom > visibleBottom
            ? focusedBounds.Bottom - visibleBottom
            : focusedBounds.Y < visibleTop ? focusedBounds.Y - visibleTop : 0f;

        // Floating/undocked keyboards create a hole rather than reducing the whole
        // viewport. Prefer revealing the editor immediately above that occlusion.
        var occluded = _inputPaneOccludedRect;
        bool overlapsOcclusion = occluded.Width > 0d && occluded.Height > 0d &&
            focusedBounds.Right > (float)occluded.X &&
            focusedBounds.X < (float)(occluded.X + occluded.Width) &&
            focusedBounds.Bottom > (float)occluded.Y &&
            focusedBounds.Y < (float)(occluded.Y + occluded.Height);
        if (overlapsOcclusion)
        {
            deltaY = focusedBounds.Bottom - ((float)occluded.Y - revealPadding);
        }
        if (deltaX == 0f && deltaY == 0f) return true;

        for (Visual? ancestor = focused.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            if (ancestor is ScrollViewer viewer)
            {
                bool changed = viewer.ChangeView(
                    deltaX == 0f ? null : viewer.HorizontalOffset + deltaX,
                    deltaY == 0f ? null : viewer.VerticalOffset + deltaY,
                    null);
                if (changed) return true;
            }
            else if (ancestor is DataGrid dataGrid && deltaY != 0f)
            {
                float previous = dataGrid.ScrollOffset;
                dataGrid.ScrollOffset += deltaY;
                if (dataGrid.ScrollOffset != previous) return true;
            }
            if (ReferenceEquals(ancestor, _renderRoot)) break;
        }

        return false;
    }

    private static Thickness NormalizeInsets(Thickness value) => new(
        float.IsFinite(value.Left) ? Math.Max(0f, value.Left) : 0f,
        float.IsFinite(value.Top) ? Math.Max(0f, value.Top) : 0f,
        float.IsFinite(value.Right) ? Math.Max(0f, value.Right) : 0f,
        float.IsFinite(value.Bottom) ? Math.Max(0f, value.Bottom) : 0f);

    private Windows.Foundation.Rect NormalizeOccludedRect(Windows.Foundation.Rect value)
    {
        double x = double.IsFinite(value.X) ? Math.Clamp(value.X, 0d, _bounds.Width) : 0d;
        double y = double.IsFinite(value.Y) ? Math.Clamp(value.Y, 0d, _bounds.Height) : 0d;
        double width = double.IsFinite(value.Width) ? Math.Clamp(value.Width, 0d, _bounds.Width - x) : 0d;
        double height = double.IsFinite(value.Height) ? Math.Clamp(value.Height, 0d, _bounds.Height - y) : 0d;
        return new Windows.Foundation.Rect(x, y, width, height);
    }

    private void OnRender(double delta)
    {
        RenderFrame(delta);
    }

    private unsafe void RenderFrame(double delta)
    {
        if (_isRendering ||
            _silkWindow == null ||
            _wgpuContext == null ||
            _compositor == null)
        {
            return;
        }

        _isRendering = true;
        try
        {
            RenderFrameCore(delta);
        }
        finally
        {
            _isRendering = false;
        }
    }

    private unsafe void RenderFrameCore(double delta)
    {
        var framebufferSize = GetCurrentFramebufferSize();
        float dpiScale = ResolveWindowDpiScale(framebufferSize);
        Vector2 logicalSize = ResolveLogicalClientSize(framebufferSize, dpiScale);
        RenderFrameCore(delta, framebufferSize, dpiScale, logicalSize);
    }

    private unsafe void RenderFrameCore(double delta, Vector2D<int> framebufferSize, float dpiScale, Vector2 logicalSize)
    {
        long frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var wgpuContext = _wgpuContext!;
        var compositor = _compositor!;
        var content = _renderRoot;

        long phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        UIThread.RunPending();
        double dispatcherTimeMs = System.Diagnostics.Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;

        if (_inputState != null)
        {
            InputSystem.Current = _inputState;
        }

        // Raise Rendering event
        phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Rendering?.Invoke(this, delta);
        double renderingCallbackTimeMs = System.Diagnostics.Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;

        phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!wgpuContext.TryReconfigureIfNeeded((uint)framebufferSize.X, (uint)framebufferSize.Y))
        {
            return;
        }
        double frameSetupTimeMs = System.Diagnostics.Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;

        // Core animation updates
        phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        content.UpdateAnimations((float)delta);
        double animationTimeMs = System.Diagnostics.Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;

        phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        VisualStateManager.UpdateAdaptiveStates(content, logicalSize);
        content.Measure(logicalSize);
        content.Arrange(new Rect(0, 0, logicalSize.X, logicalSize.Y));
        double layoutTimeMs = System.Diagnostics.Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;

        phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        TextureView* targetView = null;
        var surfaceTexture = new SurfaceTexture();
        if (wgpuContext.Surface != null)
        {
            wgpuContext.Api.SurfaceGetCurrentTexture(wgpuContext.Surface, &surfaceTexture);
            
            if (surfaceTexture.Status == SurfaceGetCurrentTextureStatus.Success)
            {
                var viewDesc = new TextureViewDescriptor
                {
                    Format = wgpuContext.SwapChainFormat,
                    Dimension = TextureViewDimension.Dimension2D,
                    BaseMipLevel = 0,
                    MipLevelCount = 1,
                    BaseArrayLayer = 0,
                    ArrayLayerCount = 1,
                    Aspect = TextureAspect.All
                };
                targetView = wgpuContext.Api.TextureCreateView(surfaceTexture.Texture, &viewDesc);
            }
            else if (surfaceTexture.Status is SurfaceGetCurrentTextureStatus.Outdated or SurfaceGetCurrentTextureStatus.Lost)
            {
                // Resize, display migration, and foreground restoration can invalidate the
                // platform surface without changing its dimensions. Reconfigure now and let
                // the next display-link tick acquire the replacement drawable.
                wgpuContext.TryConfigureSwapChain((uint)framebufferSize.X, (uint)framebufferSize.Y);
            }
            else if (surfaceTexture.Status is SurfaceGetCurrentTextureStatus.OutOfMemory or SurfaceGetCurrentTextureStatus.DeviceLost)
            {
                throw new InvalidOperationException($"WebGPU surface acquisition failed: {surfaceTexture.Status}.");
            }
        }
        double surfaceAcquireTimeMs = System.Diagnostics.Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;
        double compositorTimeMs = 0d;
        double presentTimeMs = 0d;

        try
        {
            if (targetView != null)
            {
                phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                compositor.RenderScene(
                    content,
                    (uint)MathF.Ceiling(logicalSize.X),
                    (uint)MathF.Ceiling(logicalSize.Y),
                    (uint)framebufferSize.X,
                    (uint)framebufferSize.Y,
                    dpiScale,
                    targetView);
                compositorTimeMs = System.Diagnostics.Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;

                phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                wgpuContext.Api.SurfacePresent(wgpuContext.Surface);
                presentTimeMs = System.Diagnostics.Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;
            }
        }
        finally
        {
            if (targetView != null)
            {
                wgpuContext.Api.TextureViewRelease(targetView);
            }
            if (surfaceTexture.Texture != null)
            {
                wgpuContext.Api.TextureRelease(surfaceTexture.Texture);
            }
        }

        FrameMetrics = new WindowFrameMetrics(
            dispatcherTimeMs,
            renderingCallbackTimeMs,
            frameSetupTimeMs,
            animationTimeMs,
            layoutTimeMs,
            surfaceAcquireTimeMs,
            compositorTimeMs,
            presentTimeMs,
            System.Diagnostics.Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds);
    }

    private void OnResize(Vector2D<int> newSize)
    {
        UpdateBounds(newSize.X, newSize.Y);
        _content?.Invalidate();
        _renderRoot.Invalidate();
    }

    private void OnFramebufferResize(Vector2D<int> newSize)
    {
        if (_wgpuContext == null || _silkWindow == null) return;
        var framebufferSize = NormalizeFramebufferSize(newSize);
        _wgpuContext.ConfigureSwapChain((uint)framebufferSize.X, (uint)framebufferSize.Y);
        _content?.Invalidate();
        _renderRoot.Invalidate();
        RenderFrame(0d);
    }

    private Vector2D<int> GetCurrentFramebufferSize()
    {
        return _silkWindow != null
            ? NormalizeFramebufferSize(_silkWindow.FramebufferSize)
            : new Vector2D<int>(1, 1);
    }

    private static Vector2D<int> NormalizeFramebufferSize(Vector2D<int> framebufferSize)
    {
        return new Vector2D<int>(
            Math.Max(1, framebufferSize.X),
            Math.Max(1, framebufferSize.Y));
    }

    private float ResolveWindowDpiScale(Vector2D<int> framebufferSize)
    {
        double monitorScale = 1.0;
        if (_silkWindow != null && _silkWindow.Size.X > 0)
        {
            monitorScale = (double)framebufferSize.X / _silkWindow.Size.X;
        }

        return (float)DisplayScaleResolver.ResolveWindowDisplayScale(_silkWindow, monitorScale);
    }

    private static Vector2 ResolveLogicalClientSize(Vector2D<int> framebufferSize, float dpiScale)
    {
        float scale = float.IsFinite(dpiScale) && dpiScale > 0f ? dpiScale : 1f;
        return new Vector2(
            MathF.Max(1f, framebufferSize.X / scale),
            MathF.Max(1f, framebufferSize.Y / scale));
    }

    private Vector2 NormalizePointerPosition(Vector2 pointerPosition)
    {
        if (!OperatingSystem.IsWindows())
        {
            return pointerPosition;
        }

        return InputSystem.NormalizePointerPositionForDpi(
            pointerPosition,
            ResolveWindowDpiScale(GetCurrentFramebufferSize()));
    }

    private void OnClosing()
    {
        NotifyHostVisibilityChanged(false);
        NotifyHostActivationChanged(WindowActivationState.Deactivated);
        RaiseClosed();
        WindowManager.Unregister(this);

        _compositor?.Dispose();
        _wgpuContext?.Dispose();
        _windowController?.Dispose();
        _windowController = null;
        DetachWindowServices();
        _silkWindow = null;
    }

    private void UpdateBounds(double width, double height)
    {
        double normalizedWidth = double.IsFinite(width) ? Math.Max(0d, width) : 0d;
        double normalizedHeight = double.IsFinite(height) ? Math.Max(0d, height) : 0d;
        if (_bounds.Width.Equals(normalizedWidth) && _bounds.Height.Equals(normalizedHeight)) return;

        _bounds = new Windows.Foundation.Rect(_bounds.X, _bounds.Y, normalizedWidth, normalizedHeight);
        _width = checked((int)Math.Round(normalizedWidth));
        _height = checked((int)Math.Round(normalizedHeight));
        ApplyContentInsets();
        SizeChanged?.Invoke(
            this,
            new WindowSizeChangedEventArgs(new Windows.Foundation.Size(normalizedWidth, normalizedHeight)));
    }

    private void RaiseClosed()
    {
        if (_isClosed) return;
        _isClosed = true;
        Closed?.Invoke(this, new WindowEventArgs());
    }

    private void DetachWindowServices()
    {
        if (_systemBackdrop != null)
        {
            _systemBackdrop.Changed -= OnSystemBackdropChanged;
        }
        ThemeManager.ThemeChanged -= HandleThemeChanged;
    }

    public void SetSizeConstraints(NativeWindowSize minimum, NativeWindowSize maximum)
    {
        _minimumSize = minimum;
        _maximumSize = maximum;
        _windowController?.SetSizeConstraints(minimum, maximum);
    }

    public bool BeginMove(NativeWindowPoint pointer) => _windowController?.BeginMove(pointer) == true;

    public bool BeginResize(NativeResizeEdge edge, NativeWindowPoint pointer) =>
        _windowController?.BeginResize(edge, pointer) == true;

    public bool UpdateWindowDrag(NativeWindowPoint pointer) =>
        _windowController?.UpdateDrag(pointer) == true;

    public void EndWindowDrag()
    {
        _windowController?.EndDrag();
    }

    private void ApplyWindowSettings()
    {
        if (_windowController == null)
        {
            return;
        }

        _windowController.SetDecorations(_decorations);
        _windowController.SetCanResize(_canResize);
        _windowController.SetCanMinimize(_canMinimize);
        _windowController.SetCanMaximize(_canMaximize);
        _windowController.SetTopMost(_topMost);
        _windowController.SetEnabled(_isEnabled);
        _windowController.SetShowInTaskbar(_showInTaskbar);
        _windowController.SetParent(_owner?.NativeHandle ?? NativeWindowHandle.Empty);
        _windowController.SetSizeConstraints(_minimumSize, _maximumSize);
        _windowController.SetClientAreaExtension(_extendsContentIntoTitleBar, _titleBarHeight);
        _windowController.SetTheme(
            ThemeManager.CurrentTheme == ElementTheme.Dark
                ? NativeWindowTheme.Dark
                : NativeWindowTheme.Light);
        _windowController.SetBackdrop(_systemBackdrop?.NativeKind ?? NativeWindowBackdrop.None);
    }

    private void ApplySystemBackdrop()
    {
        var nativeApplied = _windowController?.SetBackdrop(
            _systemBackdrop?.NativeKind ?? NativeWindowBackdrop.None) == true;
        IsUsingSystemBackdropFallback = _systemBackdrop != null && !nativeApplied;
        _backdropLayer.Background = IsUsingSystemBackdropFallback
            ? _systemBackdrop!.FallbackBrush
            : null;
        if (_compositor == null)
        {
            return;
        }

        _compositor.ClearColor = _systemBackdrop == null
            ? ThemeManager.GetColor("PageBackground")
            : nativeApplied
                ? Vector4.Zero
                : _systemBackdrop.FallbackColor;
        _content?.Invalidate();
        _renderRoot.Invalidate();
    }
}

public static class WindowManager
{
    private static readonly List<Window> _windows = new();

    public static IReadOnlyList<Window> ActiveWindows
    {
        get
        {
            lock (_windows)
            {
                return _windows.ToArray();
            }
        }
    }

    public static void Register(Window window)
    {
        lock (_windows)
        {
            if (!_windows.Contains(window))
            {
                _windows.Add(window);
            }
        }
    }

    public static void Unregister(Window window)
    {
        lock (_windows)
        {
            _windows.Remove(window);
        }
    }
}
