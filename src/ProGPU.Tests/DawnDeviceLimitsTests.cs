using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using W = WebGpuSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class DawnDeviceLimitsTests
{
    [Fact]
    public void PreservesEveryQueriedLimitWithoutNarrowingOrRounding()
    {
        var limits = new W.Limits
        {
            MaxStorageBufferBindingSize = (1UL << 34) + 17,
            MaxStorageBuffersPerShaderStage = 19,
            MaxComputeInvocationsPerWorkgroup = 257,
            MaxComputeWorkgroupSizeX = 129,
            MaxComputeWorkgroupsPerDimension = 32769
        };
        Assert.Equal(new WgpuComputeLimits((1UL << 34) + 17, 19, 257, 129, 32769),
            DawnDeviceLimits.ToComputeLimits(limits));
    }

    [Fact]
    public void UnknownSnapshotDoesNotInventStagedQuerySupport()
    {
        WgpuComputeLimits actual = DawnDeviceLimits.ToComputeLimits(default);
        Assert.Equal(default, actual);
        Assert.False(actual.AdmitsOrderedHitQuery(1, 1024));
    }

    [Theory]
    [InlineData("DawnGpuContext.cs")]
    [InlineData("DawnGpuContext.Offscreen.cs")]
    [InlineData("DawnGpuContext.SystemWarp.cs")]
    [InlineData("DawnNativePresentation.cs")]
    public void EveryFactoryCopiesItsActualSuccessfulDeviceQueryBeforeInitialization(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "ProGPU.Backend.Dawn", file)))
            root = root.Parent;
        Assert.NotNull(root);
        string source = File.ReadAllText(Path.Combine(root.FullName, "src", "ProGPU.Backend.Dawn", file));
        int query = source.IndexOf("device.GetLimits(&limits) != W.Status.Success", StringComparison.Ordinal);
        int snapshot = source.IndexOf("ComputeLimits = DawnDeviceLimits.ToComputeLimits(limits)", StringComparison.Ordinal);
        int initialize = source.IndexOf("context.InitializeExternalNativeDevice(", StringComparison.Ordinal);
        Assert.True(query >= 0 && snapshot > query && initialize > snapshot);
        Assert.Contains("throw new InvalidOperationException(", source[query..snapshot]);
    }
}
