using System;
using System.Reflection;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.SilkNet;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Xunit;

namespace Avalonia.IntegrationTests.SilkNet;

public sealed class SilkNetFramebufferManagerTests
{
    [Fact]
    public void ZeroSizedFramebufferSuspendsUntilPhysicalPixelsReturn()
    {
        IWindow window = DispatchProxy.Create<IWindow, TestWindowProxy>();
        var proxy = (TestWindowProxy)(object)window;
        proxy.IsInitialized = true;
        proxy.FramebufferSize = new Vector2D<int>(0, 0);
        proxy.WindowSize = new Vector2D<int>(160, 90);

        using var manager = new SilkNetFramebufferManager(window);
        using IFramebufferRenderTarget target =
            manager.CreateFramebufferRenderTarget();

        Assert.False(manager.IsReady);
        Assert.False(target.State.IsReady);
        Assert.False(target.State.IsCorrupted);
        Assert.Throws<RenderTargetNotReadyException>(
            () => target.Lock(default, out _));

        proxy.FramebufferSize = new Vector2D<int>(320, 180);

        Assert.True(manager.IsReady);
        Assert.True(target.State.IsReady);
        using ILockedFramebuffer framebuffer =
            target.Lock(default, out _);
        Assert.Equal(new PixelSize(320, 180), framebuffer.Size);
        Assert.Equal(320 * 4, framebuffer.RowBytes);
        Assert.Equal(new Vector(192, 192), framebuffer.Dpi);
    }

    private class TestWindowProxy : DispatchProxy
    {
        public bool IsInitialized { get; set; }
        public Vector2D<int> FramebufferSize { get; set; }
        public Vector2D<int> WindowSize { get; set; }

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "get_IsInitialized" => IsInitialized,
                "get_FramebufferSize" => FramebufferSize,
                "get_Size" => WindowSize,
                _ when targetMethod?.ReturnType == typeof(void) => null,
                _ when targetMethod?.ReturnType.IsValueType == true =>
                    Activator.CreateInstance(targetMethod.ReturnType),
                _ => null
            };
        }
    }
}
