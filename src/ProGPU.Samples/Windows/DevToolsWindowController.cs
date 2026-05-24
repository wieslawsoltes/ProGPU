using System;
using System.Numerics;
using Silk.NET.Windowing;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.WinUI;

namespace ProGPU.Samples;

public static unsafe class DevToolsWindowController
{
    public static void OpenDevToolsWindow()
    {
        if (AppState._devToolsWindow != null) return;

        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(850, 600);
        options.Title = "ProGPU Developer Tools";
        options.API = GraphicsAPI.None;

        AppState._devToolsWindow = Silk.NET.Windowing.Window.Create(options);

        AppState._devToolsWindow.Load += OnDevToolsWindowLoad;
        AppState._devToolsWindow.Render += OnDevToolsWindowRender;
        AppState._devToolsWindow.Resize += OnDevToolsWindowResize;
        AppState._devToolsWindow.Closing += OnDevToolsWindowClosing;

        AppState._devToolsWindow.Initialize();
    }

    private static void OnDevToolsWindowLoad()
    {
        if (AppState._devToolsWindow == null) return;

        AppState._devToolsWgpuContext = new WgpuContext();
        AppState._devToolsWgpuContext.Initialize(AppState._devToolsWindow);

        AppState._devToolsCompositor = new Compositor(AppState._devToolsWgpuContext, AppState._devToolsWgpuContext.SwapChainFormat);

        var inputContext = AppState._devToolsWindow.CreateInput();
        DevToolsInputSystem.Initialize(inputContext, AppState._devToolsPanel!);

        AppState._devToolsPanel?.RefreshVisualTree();
    }

    private static void OnDevToolsWindowRender(double delta)
    {
        if (AppState._devToolsWindow == null || AppState._devToolsWgpuContext == null || AppState._devToolsCompositor == null || AppState._devToolsPanel == null) return;

        AppState._devToolsPanel.Measure(new Vector2(AppState._devToolsWindow.Size.X, AppState._devToolsWindow.Size.Y));
        AppState._devToolsPanel.Arrange(new Rect(0, 0, AppState._devToolsWindow.Size.X, AppState._devToolsWindow.Size.Y));

        if (AppState._screenCompositor != null)
        {
            uint totalVertices = (uint)(AppState._screenCompositor.VectorVertexCount + AppState._screenCompositor.TextVertexCount + AppState._screenCompositor.TextureVertexCount);
            uint totalDrawCalls = (uint)(AppState._screenCompositor.TextureDrawCallCount + 1);
            AppState._devToolsPanel.UpdatePerfPanel((float)AppState._currentFps, (float)AppState._cpuFrameTimeMs, totalVertices, totalDrawCalls);
        }

        TextureView* targetView = null;
        if (AppState._devToolsWgpuContext.Surface != null)
        {
            var surfaceTexture = new SurfaceTexture();
            AppState._devToolsWgpuContext.Wgpu.SurfaceGetCurrentTexture(AppState._devToolsWgpuContext.Surface, &surfaceTexture);
            
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
                targetView = AppState._devToolsWgpuContext.Wgpu.TextureCreateView(surfaceTexture.Texture, &viewDesc);
            }
        }

        if (targetView != null)
        {
            AppState._devToolsCompositor.RenderScene(AppState._devToolsPanel, (uint)AppState._devToolsWindow.Size.X, (uint)AppState._devToolsWindow.Size.Y, targetView);
            
            AppState._devToolsWgpuContext.Wgpu.SurfacePresent(AppState._devToolsWgpuContext.Surface);
            AppState._devToolsWgpuContext.Wgpu.TextureViewRelease(targetView);
        }
    }

    private static void OnDevToolsWindowResize(Vector2D<int> newSize)
    {
        if (AppState._devToolsWgpuContext == null) return;
        AppState._devToolsWgpuContext.ConfigureSwapChain((uint)newSize.X, (uint)newSize.Y);
        AppState._devToolsPanel?.Invalidate();
    }

    private static void OnDevToolsWindowClosing()
    {
        DevToolsService.IsDevToolsActive = false;
        CloseDevToolsWindow();
    }

    public static void CloseDevToolsWindow()
    {
        if (AppState._devToolsWindow == null) return;

        AppState._devToolsWindow.Close();
        AppState._devToolsWindow.Dispose();
        AppState._devToolsWindow = null;

        AppState._devToolsWgpuContext?.Dispose();
        AppState._devToolsWgpuContext = null;

        AppState._devToolsCompositor = null;
    }
}
