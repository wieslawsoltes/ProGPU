using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class WinUiWindowCustomizationTests
{
    [Fact]
    public void UnchangedPresentationGatePreservesEveryInvalidationSource()
    {
        static bool CanSkip(
            bool allowSkip = true,
            bool hasPresentedFrame = true,
            long rootVersion = 7,
            long lastRootVersion = 7,
            int framebufferWidth = 1280,
            int framebufferHeight = 800,
            int lastFramebufferWidth = 1280,
            int lastFramebufferHeight = 800,
            float dpiScale = 2f,
            float lastDpiScale = 2f,
            bool continuousRendering = false,
            bool hasDynamicExternalContent = false) =>
            Window.CanSkipUnchangedPresentation(
                allowSkip,
                hasPresentedFrame,
                rootVersion,
                lastRootVersion,
                framebufferWidth,
                framebufferHeight,
                lastFramebufferWidth,
                lastFramebufferHeight,
                dpiScale,
                lastDpiScale,
                continuousRendering,
                hasDynamicExternalContent);

        Assert.True(CanSkip());
        Assert.False(CanSkip(allowSkip: false));
        Assert.False(CanSkip(hasPresentedFrame: false));
        Assert.False(CanSkip(rootVersion: 8));
        Assert.False(CanSkip(framebufferWidth: 1279));
        Assert.False(CanSkip(framebufferHeight: 799));
        Assert.False(CanSkip(dpiScale: 1.5f));
        Assert.False(CanSkip(continuousRendering: true));
        Assert.False(CanSkip(hasDynamicExternalContent: true));
    }

    [Fact]
    public void WindowRetainsCustomizationBeforeNativeActivation()
    {
        var backdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt, DarkTheme = true };
        var window = new Window
        {
            Decorations = NativeWindowDecorations.BorderOnly,
            CanResize = false,
            CanMinimize = false,
            TopMost = true,
            IsEnabled = false,
            ShowInTaskbar = false,
            ExtendsContentIntoTitleBar = true,
            TitleBarHeight = 42d,
            SystemBackdrop = backdrop
        };

        Assert.Equal(NativeWindowDecorations.BorderOnly, window.Decorations);
        Assert.False(window.CanResize);
        Assert.True(window.CanMaximize);
        Assert.False(window.CanMinimize);
        Assert.True(window.TopMost);
        Assert.False(window.IsEnabled);
        Assert.False(window.ShowInTaskbar);
        Assert.True(window.ExtendsContentIntoTitleBar);
        Assert.Equal(42d, window.TitleBarHeight);
        Assert.Same(backdrop, window.SystemBackdrop);
        Assert.True(window.IsUsingSystemBackdropFallback);
        Assert.False(window.IsContinuousRenderingEnabled);
        Assert.Null(window.NativeWindowController);
    }
}
