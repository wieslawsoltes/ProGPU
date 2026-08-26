using Avalonia;
using Avalonia.SilkNet;
using ProGPU.Backend;
using Xunit;

namespace ProGPU.Avalonia.SilkNet.ContractTests;

public sealed class DisplayMetricsContractTests
{
    [Theory]
    [InlineData(true, 2d, 1d)]
    [InlineData(false, 2d, 2d)]
    [InlineData(false, 0d, 1d)]
    public void DesktopCoordinatesFollowBackendConventions(
        bool isMacOS,
        double renderScaling,
        double expected)
    {
        Assert.Equal(
            expected,
            SilkNetDisplayMetrics.ResolveDesktopScaling(
                isMacOS,
                renderScaling));
    }

    [Theory]
    [InlineData(true, 1.5d, 1.5d)]
    [InlineData(false, 1.5d, 1d)]
    [InlineData(true, 0d, 1d)]
    public void OnlyWindowsUsesPhysicalClientAndPointerCoordinates(
        bool isWindows,
        double desktopScaling,
        double expected)
    {
        Assert.Equal(
            expected,
            SilkNetDisplayMetrics.ResolveNativeCoordinateScaling(
                isWindows,
                desktopScaling));
    }

    [Fact]
    public void FrameSizeIncludesNativeChromeInsets()
    {
        Size? frame = SilkNetDisplayMetrics.ResolveFrameSize(
            new Size(800, 600),
            new NativeWindowFrameInsets(8, 30, 8, 8));

        Assert.Equal(new Size(816, 638), frame);
        Assert.Null(
            SilkNetDisplayMetrics.ResolveFrameSize(
                new Size(800, 600),
                frameInsets: null));
    }

    [Fact]
    public void FrameInsetsAreConvertedFromWindowsPixelsToLogicalUnits()
    {
        Size? frame = SilkNetDisplayMetrics.ResolveFrameSize(
            new Size(800, 600),
            new NativeWindowFrameInsets(12, 45, 12, 12),
            desktopScaling: 1.5d);

        Assert.Equal(new Size(816, 638), frame);
    }

    [Theory]
    [InlineData(1200, 900, 1.5d, 800d, 600d)]
    [InlineData(800, 600, 1d, 800d, 600d)]
    public void NativeClientPixelsConvertToAvaloniaLogicalUnits(
        int nativeWidth,
        int nativeHeight,
        double desktopScaling,
        double expectedWidth,
        double expectedHeight)
    {
        Assert.Equal(
            new Size(expectedWidth, expectedHeight),
            SilkNetDisplayMetrics.ResolveLogicalClientSize(
                nativeWidth,
                nativeHeight,
                desktopScaling));
    }

    [Fact]
    public void AvaloniaLogicalSizeConvertsToWindowsClientPixels()
    {
        Assert.Equal(
            new PixelSize(1200, 900),
            SilkNetDisplayMetrics.ResolveNativeClientSize(
                new Size(800, 600),
                desktopScaling: 1.5d));
    }

    [Fact]
    public void NativeScaleRepairsOneToOneSilkFramebufferReports()
    {
        double scaling = SilkNetDisplayMetrics.ResolveRenderScaling(
            windowWidth: 1024,
            windowHeight: 800,
            framebufferWidth: 1024,
            framebufferHeight: 800,
            nativeDisplayScale: 2d);

        Assert.Equal(2d, scaling);
    }

    [Fact]
    public void PhysicalFramebufferRatioWinsWithoutNativeFallback()
    {
        double scaling = SilkNetDisplayMetrics.ResolveRenderScaling(
            windowWidth: 1024,
            windowHeight: 800,
            framebufferWidth: 1536,
            framebufferHeight: 1200,
            nativeDisplayScale: 2d);

        Assert.Equal(1.5d, scaling);
    }

    [Fact]
    public void RetinaTargetUsesLogicalSizeTimesRenderScale()
    {
        PixelSize size =
            SilkNetDisplayMetrics.ResolveFramebufferPixelSize(
                windowWidth: 1024,
                windowHeight: 800,
                framebufferWidth: 1024,
                framebufferHeight: 800,
                renderScaling: 2d);

        Assert.Equal(new PixelSize(2048, 1600), size);
    }

    [Fact]
    public void WindowsOneToOneFramebufferIsNotScaledTwice()
    {
        PixelSize size =
            SilkNetDisplayMetrics.ResolveFramebufferPixelSize(
                windowWidth: 1536,
                windowHeight: 1200,
                framebufferWidth: 1536,
                framebufferHeight: 1200,
                renderScaling: 1.5d,
                desktopScaling: 1.5d);

        Assert.Equal(new PixelSize(1536, 1200), size);
    }

    [Fact]
    public void PhysicalTargetPreservesLargerReportedFramebuffer()
    {
        PixelSize size =
            SilkNetDisplayMetrics.ResolveFramebufferPixelSize(
                windowWidth: 1024,
                windowHeight: 800,
                framebufferWidth: 1537,
                framebufferHeight: 1201,
                renderScaling: 1.5d);

        Assert.Equal(new PixelSize(1537, 1201), size);
    }
}
