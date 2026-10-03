using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeCacheRasterContractTests
{
    [Fact]
    public void DeviceLimitResultRetainsItsVersionedSixteenByteLayout()
    {
        Assert.Equal(16, Unsafe.SizeOf<NativeCacheRasterLimits>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<NativeCacheRasterLimits>());
        Assert.Equal(0, Marshal.OffsetOf<NativeCacheRasterLimits>(nameof(NativeCacheRasterLimits.StructSize)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<NativeCacheRasterLimits>(nameof(NativeCacheRasterLimits.Version)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<NativeCacheRasterLimits>(nameof(NativeCacheRasterLimits.MaximumTextureWidth)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<NativeCacheRasterLimits>(nameof(NativeCacheRasterLimits.MaximumTextureHeight)).ToInt32());
    }

    [Fact]
    public void SourceValueKeepsPerAxisFloatBitsLimitsAndRevisionWithoutNormalization()
    {
        float scale = BitConverter.Int32BitsToSingle(0x3fa00001);
        var source = new NativeMilBitmapCacheRasterPolicy(scale, 1.5f, 4096, 8192, ulong.MaxValue);
        Assert.Equal(0x3fa00001, BitConverter.SingleToInt32Bits(source.PrimaryDpiScaleX));
        Assert.Equal(1.5f, source.PrimaryDpiScaleY);
        Assert.Equal(4096U, source.MaximumTextureWidth);
        Assert.Equal(8192U, source.MaximumTextureHeight);
        Assert.Equal(ulong.MaxValue, source.SourceRevision);
        Assert.NotEqual(source, source with { SourceRevision = 1 });
    }
}
