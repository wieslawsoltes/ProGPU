using System;
using System.Numerics;
using Silk.NET.Windowing;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Scene;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;

namespace ProGPU.Samples;

public static unsafe class DevToolsWindowController
{
    private static IInputContext? _inputContext;

    public static void OpenDevToolsWindow()
    {
        if (AppState._devToolsWindow != null) return;

        if (AppState._window == null)
        {
            // Bypassing native GLFW window creation in embedded contexts (e.g. Avalonia or Uno)
            return;
        }

        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(850, 600);
        options.Title = "ProGPU Developer Tools";
        options.API = GraphicsAPI.None;
        options.VSync = false;

        AppState._devToolsWindow = Silk.NET.Windowing.Window.Create(options);

        AppState._devToolsWindow.Load += OnDevToolsWindowLoad;
        AppState._devToolsWindow.Render += OnDevToolsWindowRender;
        AppState._devToolsWindow.Resize += OnDevToolsWindowResize;
        AppState._devToolsWindow.FramebufferResize += OnDevToolsFramebufferResize;
        AppState._devToolsWindow.Closing += OnDevToolsWindowClosing;

        AppState._devToolsWindow.Initialize();
    }

    private static void OnDevToolsWindowLoad()
    {
        if (AppState._devToolsWindow == null) return;

        AppState._devToolsWgpuContext = new WgpuContext();
        AppState._devToolsWgpuContext.Initialize(AppState._devToolsWindow);
        WgpuContext.Current = AppState._wgpuContext; // Keep main window's context current globally

        AppState._devToolsCompositor = new Compositor(AppState._devToolsWgpuContext, AppState._devToolsWgpuContext.SwapChainFormat);

        _inputContext = AppState._devToolsWindow.CreateInput();
        DevToolsInputSystem.Initialize(_inputContext, AppState._devToolsPanel!, NormalizeDevToolsPointerPosition);

        AppState._devToolsPanel?.RefreshVisualTree();
    }

    private static void OnDevToolsWindowRender(double delta)
    {
        if (AppState._devToolsWindow == null || AppState._devToolsWgpuContext == null || AppState._devToolsCompositor == null || AppState._devToolsPanel == null) return;

        var oldContext = WgpuContext.Current;
        WgpuContext.Current = AppState._devToolsWgpuContext;

        try
        {
            var framebufferSize = GetDevToolsFramebufferSize();
            AppState._devToolsWgpuContext.ReconfigureIfNeeded((uint)framebufferSize.X, (uint)framebufferSize.Y);
            float dpiScale = ResolveDevToolsDpiScale(framebufferSize);
            Vector2 logicalSize = ResolveLogicalClientSize(framebufferSize, dpiScale);

            AppState._devToolsPanel.Measure(logicalSize);
            AppState._devToolsPanel.Arrange(new Rect(0, 0, logicalSize.X, logicalSize.Y));

            if (AppState._screenCompositor != null)
            {
                uint totalVertices = (uint)(AppState._screenCompositor.VectorVertexCount + AppState._screenCompositor.TextVertexCount + AppState._screenCompositor.TextureVertexCount);
                uint totalDrawCalls = (uint)(AppState._screenCompositor.TextureDrawCallCount + 1);
                AppState._devToolsPanel.UpdatePerfPanel((float)AppState._currentFps, (float)AppState._cpuFrameTimeMs, totalVertices, totalDrawCalls);
            }

            TextureView* targetView = null;
            var surfaceTexture = new SurfaceTexture();
            if (AppState._devToolsWgpuContext.Surface != null)
            {
                AppState._devToolsWgpuContext.Api.SurfaceGetCurrentTexture(AppState._devToolsWgpuContext.Surface, &surfaceTexture);
                
                if (surfaceTexture.Status == SurfaceGetCurrentTextureStatus.Success)
                {
                    var viewDesc = new TextureViewDescriptor
                    {
                        Format = AppState._devToolsWgpuContext.SwapChainFormat,
                        Dimension = TextureViewDimension.Dimension2D,
                        BaseMipLevel = 0,
                        MipLevelCount = 1,
                        BaseArrayLayer = 0,
                        ArrayLayerCount = 1,
                        Aspect = TextureAspect.All
                    };
                    targetView = AppState._devToolsWgpuContext.Api.TextureCreateView(surfaceTexture.Texture, &viewDesc);
                }
            }

            try
            {
                if (targetView != null)
                {
                    AppState._devToolsCompositor.RenderScene(
                        AppState._devToolsPanel,
                        (uint)MathF.Ceiling(logicalSize.X),
                        (uint)MathF.Ceiling(logicalSize.Y),
                        (uint)framebufferSize.X,
                        (uint)framebufferSize.Y,
                        dpiScale,
                        targetView);

                    AppState._devToolsWgpuContext.Api.SurfacePresent(AppState._devToolsWgpuContext.Surface);
                }
            }
            finally
            {
                if (targetView != null)
                {
                    AppState._devToolsWgpuContext.Api.TextureViewRelease(targetView);
                }
                if (surfaceTexture.Texture != null)
                {
                    AppState._devToolsWgpuContext.Api.TextureRelease(surfaceTexture.Texture);
                }
            }
        }
        finally
        {
            WgpuContext.Current = oldContext;
        }
    }

    private static void OnDevToolsWindowResize(Vector2D<int> newSize)
    {
        if (AppState._devToolsWgpuContext == null || AppState._devToolsWindow == null) return;

        var oldContext = WgpuContext.Current;
        WgpuContext.Current = AppState._devToolsWgpuContext;
        try
        {
            var framebufferSize = GetDevToolsFramebufferSize();
            AppState._devToolsWgpuContext.ConfigureSwapChain((uint)framebufferSize.X, (uint)framebufferSize.Y);
            AppState._devToolsPanel?.Invalidate();
        }
        finally
        {
            WgpuContext.Current = oldContext;
        }
    }

    private static void OnDevToolsFramebufferResize(Vector2D<int> newSize)
    {
        if (AppState._devToolsWgpuContext == null || AppState._devToolsWindow == null) return;

        var oldContext = WgpuContext.Current;
        WgpuContext.Current = AppState._devToolsWgpuContext;
        try
        {
            var framebufferSize = NormalizeFramebufferSize(newSize);
            AppState._devToolsWgpuContext.ConfigureSwapChain((uint)framebufferSize.X, (uint)framebufferSize.Y);
            AppState._devToolsPanel?.Invalidate();
        }
        finally
        {
            WgpuContext.Current = oldContext;
        }
    }

    private static Vector2D<int> GetDevToolsFramebufferSize()
    {
        return AppState._devToolsWindow != null
            ? NormalizeFramebufferSize(AppState._devToolsWindow.FramebufferSize)
            : new Vector2D<int>(1, 1);
    }

    private static Vector2D<int> NormalizeFramebufferSize(Vector2D<int> framebufferSize)
    {
        return new Vector2D<int>(
            Math.Max(1, framebufferSize.X),
            Math.Max(1, framebufferSize.Y));
    }

    private static float ResolveDevToolsDpiScale(Vector2D<int> framebufferSize)
    {
        double monitorScale = 1.0;
        if (AppState._devToolsWindow != null && AppState._devToolsWindow.Size.X > 0)
        {
            monitorScale = (double)framebufferSize.X / AppState._devToolsWindow.Size.X;
        }

        return (float)DisplayScaleResolver.ResolveWindowDisplayScale(AppState._devToolsWindow, monitorScale);
    }

    private static Vector2 ResolveLogicalClientSize(Vector2D<int> framebufferSize, float dpiScale)
    {
        float scale = float.IsFinite(dpiScale) && dpiScale > 0f ? dpiScale : 1f;
        return new Vector2(
            MathF.Max(1f, framebufferSize.X / scale),
            MathF.Max(1f, framebufferSize.Y / scale));
    }

    private static Vector2 NormalizeDevToolsPointerPosition(Vector2 pointerPosition)
    {
        if (!OperatingSystem.IsWindows())
        {
            return pointerPosition;
        }

        return InputSystem.NormalizePointerPositionForDpi(
            pointerPosition,
            ResolveDevToolsDpiScale(GetDevToolsFramebufferSize()));
    }

    private static void OnDevToolsWindowClosing()
    {
        DevToolsService.IsDevToolsActive = false;
    }

    public static void CloseDevToolsWindow()
    {
        if (AppState._devToolsWindow == null) return;

        _inputContext?.Dispose();
        _inputContext = null;

        AppState._devToolsWindow.Close();
        AppState._devToolsWindow.Dispose();
        AppState._devToolsWindow = null;

        AppState._devToolsWgpuContext?.Dispose();
        AppState._devToolsWgpuContext = null;

        AppState._devToolsCompositor = null;
    }
}
