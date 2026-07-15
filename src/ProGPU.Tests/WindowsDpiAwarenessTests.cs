using System.Numerics;
using Microsoft.UI.Xaml.Input;
using Xunit;

namespace ProGPU.Tests;

public sealed class WindowsDpiAwarenessTests
{
    [Fact]
    public void SampleAppManifestDeclaresPerMonitorV2DpiAwareness()
    {
        string manifest = File.ReadAllText(FindRepoFile("src", "ProGPU.Samples", "app.manifest"));
        string project = File.ReadAllText(FindRepoFile("src", "ProGPU.Samples", "ProGPU.Samples.csproj"));

        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", project, StringComparison.Ordinal);
        Assert.Contains("<dpiAware xmlns=\"http://schemas.microsoft.com/SMI/2005/WindowsSettings\">true/PM</dpiAware>", manifest, StringComparison.Ordinal);
        Assert.Contains("<dpiAwareness xmlns=\"http://schemas.microsoft.com/SMI/2016/WindowsSettings\">PerMonitorV2, PerMonitor</dpiAwareness>", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void AppRunnerEnablesWindowsDpiAwarenessBeforeLaunchingApp()
    {
        string appBuilder = File.ReadAllText(FindRepoFile("src", "ProGPU.WinUI", "Core", "AppBuilder.cs"));
        string dpiAwareness = File.ReadAllText(FindRepoFile("src", "ProGPU.WinUI", "Core", "WindowsDpiAwareness.cs"));

        int dpiIndex = appBuilder.IndexOf("WindowsDpiAwareness.TryEnablePerMonitorV2();", StringComparison.Ordinal);
        int launchIndex = appBuilder.IndexOf("app.Launch(", StringComparison.Ordinal);

        Assert.True(dpiIndex >= 0, "AppRunner should opt into Windows DPI awareness.");
        Assert.True(launchIndex >= 0, "AppRunner launch call was not found.");
        Assert.True(dpiIndex < launchIndex, "DPI awareness must be set before app launch can create windows.");
        Assert.Contains("SetProcessDpiAwarenessContext", dpiAwareness, StringComparison.Ordinal);
        Assert.Contains("DpiAwarenessContextPerMonitorAwareV2", dpiAwareness, StringComparison.Ordinal);
    }

    [Fact]
    public void SilkWindowsReconfigureSwapchainOnFramebufferResize()
    {
        string window = File.ReadAllText(FindRepoFile("src", "ProGPU.WinUI", "Core", "Window.cs"));
        string devTools = File.ReadAllText(FindRepoFile("src", "ProGPU.Samples", "Windows", "DevToolsWindowController.cs"));

        Assert.Contains(".FramebufferResize += OnFramebufferResize", window, StringComparison.Ordinal);
        Assert.Contains("!wgpuContext.TryReconfigureIfNeeded((uint)framebufferSize.X, (uint)framebufferSize.Y)", window, StringComparison.Ordinal);
        Assert.Contains("return;", window, StringComparison.Ordinal);
        Assert.Contains("DisplayScaleResolver.ResolveWindowDisplayScale(_silkWindow, monitorScale)", window, StringComparison.Ordinal);
        Assert.Contains("(uint)framebufferSize.X", window, StringComparison.Ordinal);
        Assert.Contains("dpiScale,", window, StringComparison.Ordinal);
        Assert.Contains(".FramebufferResize += OnDevToolsFramebufferResize", devTools, StringComparison.Ordinal);
        Assert.Contains("_devToolsWgpuContext.ReconfigureIfNeeded((uint)framebufferSize.X, (uint)framebufferSize.Y)", devTools, StringComparison.Ordinal);
        Assert.Contains("DisplayScaleResolver.ResolveWindowDisplayScale(AppState._devToolsWindow, monitorScale)", devTools, StringComparison.Ordinal);
        Assert.Contains("(uint)framebufferSize.X", devTools, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayScaleResolverCanQueryWin32WindowDpi()
    {
        string displayScaleResolver = File.ReadAllText(FindRepoFile("src", "ProGPU.Backend", "DisplayScaleResolver.cs"));

        Assert.Contains("ResolveWindowDisplayScale(IWindow? window)", displayScaleResolver, StringComparison.Ordinal);
        Assert.Contains("TryResolveWindowsWindowDisplayScale", displayScaleResolver, StringComparison.Ordinal);
        Assert.Contains("GetDpiForWindow", displayScaleResolver, StringComparison.Ordinal);
        Assert.Contains("nativeWindowSource.Native?.Win32", displayScaleResolver, StringComparison.Ordinal);
    }

    [Fact]
    public void FeatureRenderSurfacesUseResolvedWindowDpiScale()
    {
        string shaderToy = File.ReadAllText(FindRepoFile("src", "ProGPU.Samples", "Controls", "ShaderToyControl.cs"));
        string viewport3D = File.ReadAllText(FindRepoFile("src", "ProGPU.WinUI", "Controls", "Viewport3D.cs"));
        string designerCanvas = File.ReadAllText(FindRepoFile("src", "ProGPU.WinUI.Designer", "DesignerCanvas.cs"));
        string visualDesignerPage = File.ReadAllText(FindRepoFile("src", "ProGPU.Samples", "Pages", "VisualDesignerPage.cs"));
        string compositor = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Compositor.cs"));

        foreach (string source in new[] { shaderToy, viewport3D, designerCanvas, visualDesignerPage, compositor })
        {
            Assert.Contains("DisplayScaleResolver.ResolveWindowDisplayScale", source, StringComparison.Ordinal);
            Assert.DoesNotContain("FramebufferSize.X /", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PointerPositionsCanBeNormalizedFromPhysicalPixelsToLogicalPixels()
    {
        Vector2 normalized = InputSystem.NormalizePointerPositionForDpi(new Vector2(320f, 160f), 2f);

        Assert.Equal(new Vector2(160f, 80f), normalized);
    }

    [Fact]
    public void SilkInputPathsNormalizePointerCoordinatesForDpiAwareWindows()
    {
        string inputSystem = File.ReadAllText(FindRepoFile("src", "ProGPU.WinUI", "Input", "InputSystem.cs"));
        string devToolsInputSystem = File.ReadAllText(FindRepoFile("src", "ProGPU.WinUI", "Input", "DevToolsInputSystem.cs"));
        string window = File.ReadAllText(FindRepoFile("src", "ProGPU.WinUI", "Core", "Window.cs"));
        string devTools = File.ReadAllText(FindRepoFile("src", "ProGPU.Samples", "Windows", "DevToolsWindowController.cs"));

        Assert.Contains("PointerPositionTransform", inputSystem, StringComparison.Ordinal);
        Assert.Contains("OnMouseMove(NormalizeInputPosition(state, new Vector2(pos.X, pos.Y)))", inputSystem, StringComparison.Ordinal);
        Assert.Contains("InputSystem.Initialize(inputContext, _renderRoot, NormalizePointerPosition)", window, StringComparison.Ordinal);
        Assert.Contains("OperatingSystem.IsWindows()", window, StringComparison.Ordinal);
        Assert.Contains("InputSystem.NormalizePointerPositionForDpi", window, StringComparison.Ordinal);

        Assert.Contains("pointerPositionTransform", devToolsInputSystem, StringComparison.Ordinal);
        Assert.Contains("OnMouseMove(NormalizeInputPosition(new Vector2(pos.X, pos.Y)))", devToolsInputSystem, StringComparison.Ordinal);
        Assert.Contains("DevToolsInputSystem.Initialize(_inputContext, AppState._devToolsPanel!, NormalizeDevToolsPointerPosition)", devTools, StringComparison.Ordinal);
        Assert.Contains("InputSystem.NormalizePointerPositionForDpi", devTools, StringComparison.Ordinal);
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(pathParts)}.");
    }
}
