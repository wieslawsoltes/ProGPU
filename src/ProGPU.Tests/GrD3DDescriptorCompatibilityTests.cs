using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class GrD3DDescriptorCompatibilityTests
{
    [Fact]
    public void BackendStateUsesOfficialUnsignedFlagsContract()
    {
        Assert.Equal(typeof(uint), Enum.GetUnderlyingType(typeof(GRBackendState)));
        Assert.Equal(0u, (uint)GRBackendState.None);
        Assert.Equal(uint.MaxValue, (uint)GRBackendState.All);
        Assert.True(typeof(GRBackendState).IsDefined(typeof(FlagsAttribute), inherit: false));
    }

    [Fact]
    public void D3DResourceInfoRetainsBorrowedMetadataAcrossDispose()
    {
        var info = new GRD3DTextureResourceInfo
        {
            Resource = (IntPtr)0x1234,
            ResourceState = 4,
            Format = 28,
            LevelCount = 5,
            SampleCount = 8,
            SampleQualityPattern = 9,
            Protected = true,
        };

        info.Dispose();

        Assert.Equal((IntPtr)0x1234, info.Resource);
        Assert.Equal(4u, info.ResourceState);
        Assert.Equal(28u, info.Format);
        Assert.Equal(5u, info.LevelCount);
        Assert.Equal(8u, info.SampleCount);
        Assert.Equal(9u, info.SampleQualityPattern);
        Assert.True(info.Protected);
    }

    [Fact]
    public void DisposeDispatchesToOverrideForEveryCall()
    {
        var info = new TrackedResourceInfo();

        info.Dispose();
        info.Dispose();

        Assert.Equal(2, info.DisposeCount);
    }

    private sealed class TrackedResourceInfo : GRD3DTextureResourceInfo
    {
        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
    }
}
