using Avalonia;
using Avalonia.SilkNet;
using Xunit;

namespace ProGPU.Avalonia.SilkNet.ContractTests;

public sealed class DisplayMetricsContractTests
{
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
    public void PhysicalTargetNeverUsesFewerPixelsThanNativeBackingScale()
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
