using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
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

    public IWindow? SilkWindow => _silkWindow;
    public WgpuContext? WgpuContext => _wgpuContext;
    public Compositor? Compositor => _compositor;
    public WindowInputState? InputState => _inputState;

    public FrameworkElement? Content
    {
        get => _content;
        set
        {
            _content = value;
            if (_inputState != null)
            {
                _inputState.Root = value;
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
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        _content?.NotifyThemeChanged();
        foreach (var popup in Controls.PopupService.ActivePopups)
        {
            popup.NotifyThemeChanged();
        }
    }

    public void Activate()
    {
        if (_silkWindow != null) return;

        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(_width, _height);
        options.Title = _title;
        options.API = GraphicsAPI.None;
        options.VSync = false;

        _silkWindow = Silk.NET.Windowing.Window.Create(options);
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

    private void OnLoad()
    {
        if (_silkWindow == null) return;

        _wgpuContext = new WgpuContext();
        _wgpuContext.Initialize(_silkWindow);
        _compositor = new Compositor(_wgpuContext, _wgpuContext.SwapChainFormat);
        
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
        _inputState = InputSystem.Initialize(inputContext, _content, NormalizePointerPosition);
    }

    private unsafe void OnRender(double delta)
    {
        if (_silkWindow == null || _wgpuContext == null || _compositor == null || _content == null) return;

        UIThread.RunPending();

        if (_inputState != null)
        {
            InputSystem.Current = _inputState;
        }

        // Raise Rendering event
        Rendering?.Invoke(this, delta);

        var framebufferSize = GetCurrentFramebufferSize();
        _wgpuContext.ReconfigureIfNeeded((uint)framebufferSize.X, (uint)framebufferSize.Y);
        float dpiScale = ResolveWindowDpiScale(framebufferSize);
        Vector2 logicalSize = ResolveLogicalClientSize(framebufferSize, dpiScale);

        // Core animation updates
        _content.UpdateAnimations((float)delta);

        _content.Measure(logicalSize);
        _content.Arrange(new Rect(0, 0, logicalSize.X, logicalSize.Y));

        TextureView* targetView = null;
        if (_wgpuContext.Surface != null)
        {
            var surfaceTexture = new SurfaceTexture();
            _wgpuContext.Wgpu.SurfaceGetCurrentTexture(_wgpuContext.Surface, &surfaceTexture);
            
            if (surfaceTexture.Status == SurfaceGetCurrentTextureStatus.Success)
            {
                var viewDesc = new TextureViewDescriptor
                {
                    Format = _wgpuContext.SwapChainFormat,
                    Dimension = TextureViewDimension.Dimension2D,
                    BaseMipLevel = 0,
                    MipLevelCount = 1,
                    BaseArrayLayer = 0,
                    ArrayLayerCount = 1,
                    Aspect = TextureAspect.All
                };
                targetView = _wgpuContext.Wgpu.TextureCreateView(surfaceTexture.Texture, &viewDesc);
            }
        }

        if (targetView != null)
        {
            _compositor.RenderScene(
                _content,
                (uint)MathF.Ceiling(logicalSize.X),
                (uint)MathF.Ceiling(logicalSize.Y),
                (uint)framebufferSize.X,
                (uint)framebufferSize.Y,
                dpiScale,
                targetView);
            
            _wgpuContext.Wgpu.SurfacePresent(_wgpuContext.Surface);
            _wgpuContext.Wgpu.TextureViewRelease(targetView);
        }
    }

    private void OnResize(Vector2D<int> newSize)
    {
        if (_wgpuContext == null || _silkWindow == null) return;
        var framebufferSize = GetCurrentFramebufferSize();
        _wgpuContext.ConfigureSwapChain((uint)framebufferSize.X, (uint)framebufferSize.Y);
        _content?.Invalidate();
    }

    private void OnFramebufferResize(Vector2D<int> newSize)
    {
        if (_wgpuContext == null || _silkWindow == null) return;
        var framebufferSize = NormalizeFramebufferSize(newSize);
        _wgpuContext.ConfigureSwapChain((uint)framebufferSize.X, (uint)framebufferSize.Y);
        _content?.Invalidate();
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
        _silkWindow = null;
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
