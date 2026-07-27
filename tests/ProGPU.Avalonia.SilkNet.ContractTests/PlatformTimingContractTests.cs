using System;
using Avalonia.SilkNet;
using Xunit;

namespace ProGPU.Avalonia.SilkNet.ContractTests;

public sealed class PlatformTimingContractTests
{
    [Theory]
    [InlineData(24)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(360)]
    public void ExplicitSupportedFrameRateWins(int configured)
    {
        Assert.Equal(
            configured,
            SilkNetPlatform.NormalizeRenderFramesPerSecond(
                configured,
                detectedFramesPerSecond: 75));
    }

    [Theory]
    [InlineData(0, 75, 75)]
    [InlineData(23, 120, 120)]
    [InlineData(361, 24, 24)]
    [InlineData(-1, 0, 60)]
    [InlineData(1000, 1000, 60)]
    public void InvalidConfigurationUsesDisplayRateOrSafeFallback(
        int configured,
        int detected,
        int expected)
    {
        Assert.Equal(
            expected,
            SilkNetPlatform.NormalizeRenderFramesPerSecond(
                configured,
                detected));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(60)]
    [InlineData(240)]
    public void RenderTimerExposesAStableForegroundInterval(int framesPerSecond)
    {
        var timer = new SilkNetRenderTimer(framesPerSecond);

        Assert.False(timer.RunsInBackground);
        Assert.Equal(
            TimeSpan.FromSeconds(1.0 / framesPerSecond),
            timer.Interval);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RenderTimerRejectsNonPositiveRates(int framesPerSecond)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SilkNetRenderTimer(framesPerSecond));
    }
}
