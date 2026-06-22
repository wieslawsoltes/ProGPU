using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class DisplayScaleResolverTests
{
    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(-2.0, 1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(8.0, 8.0)]
    [InlineData(8.5, 1.0)]
    public void NormalizeDisplayScaleKeepsOnlyFinitePositiveScales(double input, double expected)
    {
        Assert.Equal(expected, DisplayScaleResolver.NormalizeDisplayScale(input));
    }

    [Fact]
    public void ResolveDisplayScaleWithPlatformFallbackUsesNativeScaleWhenMonitorScaleIsUnavailable()
    {
        double dpiScale = DisplayScaleResolver.ResolveDisplayScaleWithPlatformFallback(
            monitorDpiScale: 1.0,
            platformDpiScaleProvider: () => 2.0);

        Assert.Equal(2.0, dpiScale);
    }

    [Fact]
    public void ResolveDisplayScaleWithPlatformFallbackKeepsUsableMonitorScale()
    {
        double dpiScale = DisplayScaleResolver.ResolveDisplayScaleWithPlatformFallback(
            monitorDpiScale: 1.5,
            platformDpiScaleProvider: () => 2.0);

        Assert.Equal(1.5, dpiScale);
    }

    [Fact]
    public void ResolveDisplayScaleWithPlatformFallbackIgnoresInvalidNativeScale()
    {
        double dpiScale = DisplayScaleResolver.ResolveDisplayScaleWithPlatformFallback(
            monitorDpiScale: 1.0,
            platformDpiScaleProvider: () => 0.0);

        Assert.Equal(1.0, dpiScale);
    }

    [Fact]
    public void ResolveDisplayScaleWithPlatformFallbackUsesNormalizedMonitorScaleWhenNativeScaleIsUnavailable()
    {
        double dpiScale = DisplayScaleResolver.ResolveDisplayScaleWithPlatformFallback(
            monitorDpiScale: 0.0,
            platformDpiScaleProvider: () => null);

        Assert.Equal(1.0, dpiScale);
    }
}
