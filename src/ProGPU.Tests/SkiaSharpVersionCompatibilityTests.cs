using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkiaSharpVersionCompatibilityTests
{
    [Fact]
    public void VersionPropertiesMatchOfficialCompatibilityLevel()
    {
        Assert.Equal(new Version(151, 0), SkiaSharpVersion.Native);
        Assert.Same(SkiaSharpVersion.Native, SkiaSharpVersion.NativeMinimum);
    }

    [Fact]
    public void CompatibilityCheckSucceedsForBothModes()
    {
        Assert.True(SkiaSharpVersion.CheckNativeLibraryCompatible());
        Assert.True(SkiaSharpVersion.CheckNativeLibraryCompatible(false));
        Assert.True(SkiaSharpVersion.CheckNativeLibraryCompatible(true));
    }

    [Fact]
    public void StableVersionQueriesAllocateNothing()
    {
        _ = SkiaSharpVersion.Native;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000_000; index++)
        {
            _ = SkiaSharpVersion.Native;
            _ = SkiaSharpVersion.NativeMinimum;
            _ = SkiaSharpVersion.CheckNativeLibraryCompatible();
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }
}
