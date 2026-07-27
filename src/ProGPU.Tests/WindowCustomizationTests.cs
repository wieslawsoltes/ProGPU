using ProGPU.Backend;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class WindowCustomizationTests
{
    [Fact]
    public void CapabilityMatrixDoesNotAdvertiseUnsupportedWaylandOperations()
    {
        var windows = NativeWindowCapabilities.ForKind(NativeWindowKind.Win32);
        var macOs = NativeWindowCapabilities.ForKind(NativeWindowKind.Cocoa);
        var x11 = NativeWindowCapabilities.ForKind(NativeWindowKind.X11);
        var wayland = NativeWindowCapabilities.ForKind(NativeWindowKind.Wayland);

        Assert.True(windows.Supports(NativeWindowFeatures.Mica | NativeWindowFeatures.MoveDrag));
        Assert.True(macOs.Supports(NativeWindowFeatures.Mica | NativeWindowFeatures.ResizeDrag));
        Assert.True(x11.Supports(NativeWindowFeatures.Acrylic | NativeWindowFeatures.Taskbar));
        Assert.False(x11.Supports(NativeWindowFeatures.Mica));
        Assert.True(wayland.Supports(NativeWindowFeatures.ClientAreaExtension));
        Assert.False(wayland.Supports(NativeWindowFeatures.MoveDrag));
        Assert.False(wayland.Supports(NativeWindowFeatures.Taskbar));
        Assert.False(wayland.Supports(NativeWindowFeatures.Mica));
    }

    [Fact]
    public void TransparentSurfacePrefersPremultipliedAlpha()
    {
        var modes = new[]
        {
            CompositeAlphaMode.Opaque,
            CompositeAlphaMode.Unpremultiplied,
            CompositeAlphaMode.Premultiplied
        };

        var selected = WgpuContext.ChooseCompositeAlphaMode(true, modes);

        Assert.Equal(CompositeAlphaMode.Premultiplied, selected);
    }

    [Fact]
    public void OpaqueSurfacePrefersOpaqueAlpha()
    {
        var modes = new[]
        {
            CompositeAlphaMode.Premultiplied,
            CompositeAlphaMode.Opaque
        };

        var selected = WgpuContext.ChooseCompositeAlphaMode(false, modes);

        Assert.Equal(CompositeAlphaMode.Opaque, selected);
    }

    [Fact]
    public void AvaloniaSilkWindowPaintsOnlyQueuedInvalidations()
    {
        string source = File.ReadAllText(
            FindRepoFile(
                "src",
                "ProGPU.Avalonia.SilkNet",
                "WindowImpl.cs"));
        int callbackStart = source.IndexOf(
            "private void OnRender(double delta)",
            StringComparison.Ordinal);
        int callbackEnd = source.IndexOf(
            "private void OnResize",
            callbackStart,
            StringComparison.Ordinal);

        Assert.True(callbackStart >= 0);
        Assert.True(callbackEnd > callbackStart);
        string callback = source[callbackStart..callbackEnd];
        Assert.Contains("if (_paintQueued)", callback, StringComparison.Ordinal);
        Assert.Contains("PaintNow();", callback, StringComparison.Ordinal);
    }

    [Fact]
    public void AvaloniaSilkWindowRecreatesLostDeviceBeforePainting()
    {
        string source = File.ReadAllText(
            FindRepoFile(
                "src",
                "ProGPU.Avalonia.SilkNet",
                "WindowImpl.cs"));
        int recoveryStart = source.IndexOf(
            "private bool EnsureWebGpuContextReady()",
            StringComparison.Ordinal);
        int recoveryEnd = source.IndexOf(
            "private void OnWebGpuDeviceLost",
            recoveryStart,
            StringComparison.Ordinal);
        int paintStart = source.IndexOf(
            "private void PaintNow()",
            StringComparison.Ordinal);
        int paintEnd = source.IndexOf(
            "private void OnMove",
            paintStart,
            StringComparison.Ordinal);

        Assert.True(recoveryStart >= 0);
        Assert.True(recoveryEnd > recoveryStart);
        Assert.True(paintStart >= 0);
        Assert.True(paintEnd > paintStart);

        string recovery = source[recoveryStart..recoveryEnd];
        string paint = source[paintStart..paintEnd];
        Assert.Contains(
            "WgpuContext replacement = CreateWebGpuContext();",
            recovery,
            StringComparison.Ordinal);
        Assert.Contains(
            "current?.Dispose();",
            recovery,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!EnsureWebGpuContextReady())",
            paint,
            StringComparison.Ordinal);
        Assert.True(
            paint.IndexOf(
                "if (!EnsureWebGpuContextReady())",
                StringComparison.Ordinal) <
            paint.IndexOf(
                "Paint?.Invoke(",
                StringComparison.Ordinal));
    }

    [Fact]
    public void AvaloniaSilkRenderTimerTracksTheDisplayWithoutWorkTimeDrift()
    {
        string platform = File.ReadAllText(
            FindRepoFile(
                "src",
                "ProGPU.Avalonia.SilkNet",
                "SilkNetPlatform.cs"));
        string timer = File.ReadAllText(
            FindRepoFile(
                "src",
                "ProGPU.Avalonia.SilkNet",
                "SilkNetRenderTimer.cs"));

        Assert.Contains(
            "PROGPU_AVALONIA_RENDER_FPS",
            platform,
            StringComparison.Ordinal);
        Assert.Contains(
            "videoMode->RefreshRate",
            platform,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolveRenderFramesPerSecond()",
            platform,
            StringComparison.Ordinal);
        Assert.Contains(
            "_nextDeadlineTicks - afterTick",
            timer,
            StringComparison.Ordinal);
        Assert.Contains(
            "_nextDeadlineTicks += _periodTicks",
            timer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DispatcherTimer.Run(",
            timer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AvaloniaSilkWindowingRegistersGlfwWithoutAssemblyDiscovery()
    {
        string platform = File.ReadAllText(
            FindRepoFile(
                "src",
                "ProGPU.Avalonia.SilkNet",
                "SilkNetPlatform.cs"));
        string project = File.ReadAllText(
            FindRepoFile(
                "src",
                "ProGPU.Avalonia.SilkNet",
                "Avalonia.SilkNet.csproj"));
        string projectV11 = File.ReadAllText(
            FindRepoFile(
                "src",
                "ProGPU.Avalonia.SilkNet.V11",
                "Avalonia.SilkNet.csproj"));

        int typedRegistration = platform.IndexOf(
            "RegisterNativeBackends();",
            StringComparison.Ordinal);
        int dispatcherInitialization = platform.IndexOf(
            "s_instance._dispatcher = new SilkNetDispatcherImpl();",
            StringComparison.Ordinal);

        Assert.True(typedRegistration >= 0);
        Assert.True(dispatcherInitialization > typedRegistration);
        Assert.Contains(
            "GlfwWindowing.RegisterPlatform();",
            platform,
            StringComparison.Ordinal);
        Assert.Contains(
            "GlfwInput.RegisterPlatform();",
            platform,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Window.TryAdd(",
            platform,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "InputWindowExtensions.TryAdd(",
            platform,
            StringComparison.Ordinal);
        Assert.Contains(
            "<PackageReference Include=\"Silk.NET.Windowing.Glfw\" />",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<PackageReference Include=\"Silk.NET.Input.Glfw\" />",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<PackageReference Include=\"Silk.NET.Windowing.Glfw\" />",
            projectV11,
            StringComparison.Ordinal);
        Assert.Contains(
            "<PackageReference Include=\"Silk.NET.Input.Glfw\" />",
            projectV11,
            StringComparison.Ordinal);
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(
                [directory.FullName, .. pathParts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            string.Join(Path.DirectorySeparatorChar, pathParts));
    }
}
