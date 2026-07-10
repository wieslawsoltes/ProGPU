using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class WinUiWindowCustomizationTests
{
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
        Assert.Null(window.NativeWindowController);
    }
}
