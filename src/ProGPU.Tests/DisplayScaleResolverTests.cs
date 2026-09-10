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

    [Theory]
    [InlineData(800, 600, 800, 600, 1.0)]
    [InlineData(800, 600, 1600, 1200, 2.0)]
    [InlineData(800, 600, 1200, 900, 1.5)]
    [InlineData(0, 600, 1600, 1200, 1.0)]
    [InlineData(800, 600, 0, 1200, 1.0)]
    public void ResolveFramebufferScaleSeparatesScreenCoordinatesFromPixels(
        int windowWidth,
        int windowHeight,
        int framebufferWidth,
        int framebufferHeight,
        double expected)
    {
        double scale = DisplayScaleResolver.ResolveFramebufferScale(
            windowWidth,
            windowHeight,
            framebufferWidth,
            framebufferHeight);

        Assert.Equal(expected, scale);
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

    [Fact]
    public void TryResolveNativeWindowDisplayScaleDoesNotThrowWithoutWindow()
    {
        double? dpiScale = DisplayScaleResolver.TryResolveNativeWindowDisplayScale(null);

        if (dpiScale.HasValue)
        {
            Assert.InRange(dpiScale.Value, 1.0, 8.0);
        }
    }

    [Fact]
    public void MacOsNativeDisplayScaleResolverProbesWindowAndViewSelectorShapes()
    {
        string source = File.ReadAllText(FindDisplayScaleResolverSource());

        Assert.Contains("\"backingScaleFactor\"", source, StringComparison.Ordinal);
        Assert.Contains("\"respondsToSelector:\"", source, StringComparison.Ordinal);
        Assert.Contains("TryGetMacOsObjectScreen", source, StringComparison.Ordinal);
        Assert.Contains("TrySendMacOsIntPtr(cocoaObject, \"screen\"", source, StringComparison.Ordinal);
        Assert.Contains("TrySendMacOsIntPtr(cocoaObject, \"window\"", source, StringComparison.Ordinal);
        Assert.Contains("TrySendMacOsIntPtr(cocoaWindow, \"screen\"", source, StringComparison.Ordinal);
        Assert.Contains("TryGetMacOsMainScreen", source, StringComparison.Ordinal);
    }

    private static string FindDisplayScaleResolverSource()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            foreach (string candidate in new[]
                     {
                         Path.Combine(directory.FullName, "ProGPU.Backend", "DisplayScaleResolver.cs"),
                         Path.Combine(directory.FullName, "src", "ProGPU.Backend", "DisplayScaleResolver.cs")
                     })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException("Could not locate ProGPU.Backend DisplayScaleResolver.cs.");
    }
}
