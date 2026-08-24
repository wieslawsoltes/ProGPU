using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.SilkNet;
using ProGPU.Backend;
using Silk.NET.Windowing;
using Xunit;

namespace ProGPU.Avalonia.SilkNet.ContractTests;

public sealed class WindowChromeContractTests
{
#if !AVALONIA11
    [Theory]
    [InlineData(
        NativeWindowKind.Win32,
        "Mica",
        NativeWindowBackdrop.Mica)]
    [InlineData(
        NativeWindowKind.Cocoa,
        "Mica",
        NativeWindowBackdrop.Mica)]
    [InlineData(
        NativeWindowKind.X11,
        "AcrylicBlur",
        NativeWindowBackdrop.Acrylic)]
    [InlineData(
        NativeWindowKind.Wayland,
        "Transparent",
        NativeWindowBackdrop.Transparent)]
    public void TransparencyUsesFirstSupportedPlatformChoice(
        NativeWindowKind kind,
        string expectedLevel,
        NativeWindowBackdrop expectedBackdrop)
    {
        WindowTransparencyLevel[] requested =
        [
            WindowTransparencyLevel.Mica,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Blur,
            WindowTransparencyLevel.Transparent
        ];

        SilkNetTransparencyChoice result =
            SilkNetWindowChrome.SelectTransparency(
                requested,
                NativeWindowCapabilities.ForKind(kind));

        Assert.Equal(
            expectedLevel,
            result.Level.ToString());
        Assert.Equal(expectedBackdrop, result.Backdrop);
    }

    [Theory]
    [InlineData(
        NativeWindowDecorations.None,
        false,
        true,
        WindowBorder.Hidden)]
    [InlineData(
        NativeWindowDecorations.Full,
        true,
        true,
        WindowBorder.Hidden)]
    [InlineData(
        NativeWindowDecorations.BorderOnly,
        false,
        true,
        WindowBorder.Fixed)]
    [InlineData(
        NativeWindowDecorations.Full,
        false,
        false,
        WindowBorder.Fixed)]
    [InlineData(
        NativeWindowDecorations.Full,
        false,
        true,
        WindowBorder.Resizable)]
    public void InitialSilkBorderPreservesDecorationIntent(
        NativeWindowDecorations decorations,
        bool extendClientArea,
        bool canResize,
        WindowBorder expected)
    {
        Assert.Equal(
            expected,
            SilkNetWindowChrome.GetInitialWindowBorder(
                decorations,
                extendClientArea,
                canResize));
    }

    [Theory]
    [InlineData(
        true,
        true,
        true,
        7)]
    [InlineData(
        false,
        true,
        true,
        6)]
    [InlineData(
        true,
        false,
        false,
        2)]
    public void AllowedActionsFollowWindowCapabilities(
        bool canResize,
        bool canMinimize,
        bool canMaximize,
        int expected)
    {
        Assert.Equal(
            expected,
            (int)SilkNetWindowChrome
                .GetAllowedWindowActions(
                    canResize,
                    canMinimize,
                    canMaximize));
    }

    [Fact]
    public void DrawnDecorationFlagsMapWithoutLoss()
    {
        NativeDrawnDecorationParts native =
            NativeDrawnDecorationParts.TitleBar |
            NativeDrawnDecorationParts.Border |
            NativeDrawnDecorationParts.ResizeGrips |
            NativeDrawnDecorationParts.Shadow;

        Assert.Equal(
            15,
            (int)SilkNetWindowChrome
                .MapRequestedDrawnDecorations(
                    native));
    }
#endif

    [Fact]
    public void TopmostWindowsRemainAboveRecentlyActivatedNormalWindows()
    {
        long oldTopmost =
            SilkNetWindowingPlatform.ResolveZOrder(
                activationOrder: 1,
                topmost: true);
        long newerNormal =
            SilkNetWindowingPlatform.ResolveZOrder(
                activationOrder: 100,
                topmost: false);

        Assert.True(oldTopmost > newerNormal);
    }

    [Theory]
    [InlineData(WindowEdge.West, NativeResizeEdge.Left)]
    [InlineData(WindowEdge.North, NativeResizeEdge.Top)]
    [InlineData(WindowEdge.East, NativeResizeEdge.Right)]
    [InlineData(WindowEdge.South, NativeResizeEdge.Bottom)]
    [InlineData(WindowEdge.NorthWest, NativeResizeEdge.TopLeft)]
    [InlineData(WindowEdge.NorthEast, NativeResizeEdge.TopRight)]
    [InlineData(WindowEdge.SouthWest, NativeResizeEdge.BottomLeft)]
    [InlineData(WindowEdge.SouthEast, NativeResizeEdge.BottomRight)]
    public void ResizeEdgesMapExactly(
        WindowEdge source,
        NativeResizeEdge expected)
    {
        Assert.Equal(
            expected,
            SilkNetWindowChrome.MapResizeEdge(source));
    }

    [Fact]
    public void SizeConstraintsNormalizeForNativeWindowing()
    {
        Assert.Equal(
            new NativeWindowSize(11, 21),
            SilkNetWindowChrome.ToMinimumSize(
                new Size(10.1, 20.01)));
        Assert.Equal(
            new NativeWindowSize(99, 199),
            SilkNetWindowChrome.ToMaximumSize(
                new Size(99.9, 199.8)));
        Assert.Equal(
            NativeWindowSize.Unbounded,
            SilkNetWindowChrome.ToMaximumSize(
                new Size(
                    double.PositiveInfinity,
                    double.NaN)));
    }
}
