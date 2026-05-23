using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Windowing;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Scene;

namespace ProGPU.WinUI;

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
        set => _content = value;
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
    }

    public void Activate()
    {
        if (_silkWindow != null) return;

        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(_width, _height);
        options.Title = _title;
        options.API = GraphicsAPI.None;

        _silkWindow = Silk.NET.Windowing.Window.Create(options);
        _silkWindow.Load += OnLoad;
        _silkWindow.Render += OnRender;
        _silkWindow.Resize += OnResize;
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

        var inputContext = _silkWindow.CreateInput();
        _inputState = InputSystem.Initialize(inputContext, _content);
    }

    private unsafe void OnRender(double delta)
    {
        if (_silkWindow == null || _wgpuContext == null || _compositor == null || _content == null) return;

        if (_inputState != null)
        {
            InputSystem.Current = _inputState;
        }

        // Raise Rendering event
        Rendering?.Invoke(this, delta);

        // Core animation updates
        _content.UpdateAnimations((float)delta);

        _content.Measure(new Vector2(_silkWindow.Size.X, _silkWindow.Size.Y));
        _content.Arrange(new Rect(0, 0, _silkWindow.Size.X, _silkWindow.Size.Y));

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
            _compositor.RenderScene(_content, (uint)_silkWindow.Size.X, (uint)_silkWindow.Size.Y, targetView);
            
            _wgpuContext.Wgpu.SurfacePresent(_wgpuContext.Surface);
            _wgpuContext.Wgpu.TextureViewRelease(targetView);
        }
    }

    private void OnResize(Vector2D<int> newSize)
    {
        if (_wgpuContext == null) return;
        _wgpuContext.ConfigureSwapChain((uint)newSize.X, (uint)newSize.Y);
        _content?.Invalidate();
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
