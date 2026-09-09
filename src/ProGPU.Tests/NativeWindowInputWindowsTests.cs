using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.Windowing;
using Xunit;

namespace ProGPU.Tests;

[CollectionDefinition("Native window input", DisableParallelization = true)]
public sealed class NativeWindowInputCollection { }

[Collection("Native window input")]
public partial class NativeWindowInputWindowsTests
{
    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires the native Windows window-input qualification lane.";
        }
    }

    [WindowsFact]
    public void NativeInputGatePreservesLatestApplicationEnabledIntentAcrossControllerRefresh()
    {
        var options = WindowOptions.Default;
        options.API = GraphicsAPI.None;
        options.IsVisible = false;
        using var window = Window.Create(options);
        window.Initialize();
        using var controller = new SilkWindowController(window);
        Assert.True(controller.Attach());
        nint handle = controller.Handle.Handle;
        Assert.True(controller.SetEnabled(true));
        Assert.NotEqual(0, IsWindowEnabled(handle));
        Assert.True(controller.SetInputAllowed(false));
        Assert.Equal(0, IsWindowEnabled(handle));
        Assert.True(controller.SetEnabled(true));
        Assert.Equal(0, IsWindowEnabled(handle));
        controller.SetDecorations(NativeWindowDecorations.Full);
        controller.Reapply();
        Assert.Equal(0, IsWindowEnabled(handle));
        Assert.True(controller.SetEnabled(false));
        Assert.True(controller.SetInputAllowed(true));
        Assert.Equal(0, IsWindowEnabled(handle));
        Assert.True(controller.SetEnabled(true));
        Assert.NotEqual(0, IsWindowEnabled(handle));
    }

    [LibraryImport("user32.dll")]
    private static partial int IsWindowEnabled(nint window);
}
