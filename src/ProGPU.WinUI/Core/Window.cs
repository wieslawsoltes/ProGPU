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

public class Window
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
    private readonly Border _backdropLayer;
    private bool _isEnabled = true;
    private bool _showInTaskbar = true;
    private Window? _owner;
    private bool _isRendering;

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
                _renderRoot.RemoveChild(_content);
            }
            _content = value;
            if (value != null)
            {
                _renderRoot.AddChild(value);
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
            if (_silkWindow != null) _silkWindow.Size = new Vector2D<int>(_width, _silkWindow.Size.Y);
        }
    }

    public int Height
    {
        get => _silkWindow?.Size.Y ?? _height;
        set
        {
            _height = value;
            if (_silkWindow != null) _silkWindow.Size = new Vector2D<int>(_silkWindow.Size.X, _height);
        }
    }

    public event EventHandler? Closed;
    public event EventHandler? Activated;
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
        _renderRoot.AddChild(_backdropLayer);
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged()
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
        Activated?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        if (_silkWindow == null) return;
        _silkWindow.Close();
    }

    public void Hide()
    {
        if (_silkWindow != null)
        {
            _silkWindow.IsVisible = false;
        }
    }

    private void OnLoad()
    {
        if (_silkWindow == null) return;

        _windowController?.Attach();
        ApplyWindowSettings();

        _wgpuContext = new WgpuContext();
        _wgpuContext.Initialize(_silkWindow);
        _compositor = new Compositor(_wgpuContext, _wgpuContext.SwapChainFormat);
        ApplySystemBackdrop();
        
        // Decoupled Compositor Rendering Hooks Setup
        _compositor.PreRender += (w, h) => PopupService.MeasureAndArrangePopups(new Vector2(w, h));
        _compositor.GetExternalLayers = () => PopupService.ActivePopups;
        _compositor.GetTooltip = () => InputSystem.ActiveToolTip;
        _compositor.GetMousePosition = () => InputSystem.LastMousePosition;
        _compositor.RenderDiagnostics = (diagContext, w, h) =>
        {
            if (DevToolsService.IsDevToolsActive)
            {
                AdornerLayer.Render(diagContext, w, h);
            }
            DragDropManager.RenderDragVisual(diagContext, w, h);
        };

        string fontPath = "/System/Library/Fonts/Supplemental/Arial.ttf";
        if (!System.IO.File.Exists(fontPath)) fontPath = "Arial.ttf";

        if (System.IO.File.Exists(fontPath))
        {
            PopupService.DefaultFont = new ProGPU.Text.TtfFont(fontPath);
        }

        var inputContext = _silkWindow.CreateInput();
        _inputState = InputSystem.Initialize(inputContext, _renderRoot, NormalizePointerPosition);
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
        var wgpuContext = _wgpuContext!;
        var compositor = _compositor!;
        var content = _renderRoot;

        UIThread.RunPending();

        if (_inputState != null)
        {
            InputSystem.Current = _inputState;
        }

        // Raise Rendering event
        Rendering?.Invoke(this, delta);

        var framebufferSize = GetCurrentFramebufferSize();
        wgpuContext.ReconfigureIfNeeded((uint)framebufferSize.X, (uint)framebufferSize.Y);
        float dpiScale = ResolveWindowDpiScale(framebufferSize);
        Vector2 logicalSize = ResolveLogicalClientSize(framebufferSize, dpiScale);

        // Core animation updates
        content.UpdateAnimations((float)delta);

        content.Measure(logicalSize);
        content.Arrange(new Rect(0, 0, logicalSize.X, logicalSize.Y));

        TextureView* targetView = null;
        if (wgpuContext.Surface != null)
        {
            var surfaceTexture = new SurfaceTexture();
            wgpuContext.Wgpu.SurfaceGetCurrentTexture(wgpuContext.Surface, &surfaceTexture);
            
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
                targetView = wgpuContext.Wgpu.TextureCreateView(surfaceTexture.Texture, &viewDesc);
            }
        }

        if (targetView != null)
        {
            compositor.RenderScene(
                content,
                (uint)MathF.Ceiling(logicalSize.X),
                (uint)MathF.Ceiling(logicalSize.Y),
                (uint)framebufferSize.X,
                (uint)framebufferSize.Y,
                dpiScale,
                targetView);
            
            wgpuContext.Wgpu.SurfacePresent(wgpuContext.Surface);
            wgpuContext.Wgpu.TextureViewRelease(targetView);
        }
    }

    private void OnResize(Vector2D<int> _)
    {
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
        Closed?.Invoke(this, EventArgs.Empty);
        WindowManager.Unregister(this);

        _compositor?.Dispose();
        _wgpuContext?.Dispose();
        _windowController?.Dispose();
        _windowController = null;
        if (_systemBackdrop != null)
        {
            _systemBackdrop.Changed -= OnSystemBackdropChanged;
        }
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _silkWindow = null;
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
