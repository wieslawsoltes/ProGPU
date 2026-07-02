using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests;

public sealed class CompositorHostFrameTests
{
    [Fact]
    public void FromLogicalSizeRoundsPhysicalTargetUp()
    {
        var frame = CompositorHostFrame.FromLogicalSize(50.25f, 30.25f, 2f);

        Assert.True(frame.IsValid);
        Assert.Equal(51u, frame.LogicalPixelWidth);
        Assert.Equal(31u, frame.LogicalPixelHeight);
        Assert.Equal(101u, frame.RenderTargetWidth);
        Assert.Equal(61u, frame.RenderTargetHeight);
        Assert.Equal(RenderTargetViewport.Full(101, 61), frame.RenderTargetViewport);
        Assert.Equal(2f, frame.DpiScale);
    }

    [Fact]
    public void FromLogicalSizeNormalizesInvalidValues()
    {
        var frame = CompositorHostFrame.FromLogicalSize(
            float.NaN,
            -10f,
            0f,
            float.PositiveInfinity);

        Assert.True(frame.IsValid);
        Assert.Equal(1f, frame.LogicalWidth);
        Assert.Equal(1f, frame.LogicalHeight);
        Assert.Equal(1u, frame.RenderTargetWidth);
        Assert.Equal(1u, frame.RenderTargetHeight);
        Assert.Equal(1f, frame.DpiScaleX);
        Assert.Equal(1f, frame.DpiScaleY);
    }

    [Fact]
    public void FromRenderTargetPreservesTargetAndComputesLogicalSize()
    {
        var frame = CompositorHostFrame.FromRenderTarget(150, 90, 1.5f);

        Assert.True(frame.IsValid);
        Assert.Equal(100f, frame.LogicalWidth);
        Assert.Equal(60f, frame.LogicalHeight);
        Assert.Equal(150u, frame.RenderTargetWidth);
        Assert.Equal(90u, frame.RenderTargetHeight);
        Assert.Equal(1.5f, frame.DpiScale);
    }

    [Fact]
    public void ViewportClampPreservesOffsetAndClampsExtent()
    {
        var viewport = new RenderTargetViewport(10f, 20f, 400f, 10f).Clamp(200, 160);

        Assert.Equal(10f, viewport.X);
        Assert.Equal(20f, viewport.Y);
        Assert.Equal(190f, viewport.Width);
        Assert.Equal(10f, viewport.Height);
    }

    [Fact]
    public void FromLogicalSizeClampsCustomViewportToRenderTarget()
    {
        var frame = CompositorHostFrame.FromLogicalSize(
            100f,
            80f,
            2f,
            2f,
            new RenderTargetViewport(10f, 20f, 400f, 10f));

        Assert.Equal(200u, frame.RenderTargetWidth);
        Assert.Equal(160u, frame.RenderTargetHeight);
        Assert.Equal(new RenderTargetViewport(10f, 20f, 190f, 10f), frame.RenderTargetViewport);
    }
}
