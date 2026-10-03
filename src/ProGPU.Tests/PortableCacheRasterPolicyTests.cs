using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableCacheRasterPolicyTests
{
    [Fact]
    public void DefaultSnapshotsAreUnavailableWithoutManufacturedDefaults()
    {
        Assert.False(default(PortableBitmapCacheRasterPolicy).IsValid);
        Assert.False(default(PortablePrimaryDisplayRasterScale).IsValid);
        Assert.Equal(0u, default(PortableBitmapCacheRasterPolicy).MaximumTextureWidth);
        Assert.Equal(0f, default(PortablePrimaryDisplayRasterScale).ScaleX);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidAxesStayInvalidAndUnmodified(float invalid)
    {
        var cache = new PortableBitmapCacheRasterPolicy(1.25f, 1.5f, 4096, 2048, 7);
        var source = new PortablePrimaryDisplayRasterScale(1.25f, 1.5f,
            PortablePrimaryDisplayRasterPolicy.WindowsSystemDpi, new object(), 7);
        var cacheX = cache with { PrimaryDpiScaleX = invalid };
        var cacheY = cache with { PrimaryDpiScaleY = invalid };
        var sourceX = source with { ScaleX = invalid };
        var sourceY = source with { ScaleY = invalid };
        Assert.False(cacheX.IsValid);
        Assert.False(cacheY.IsValid);
        Assert.False(sourceX.IsValid);
        Assert.False(sourceY.IsValid);
        Assert.Equal(BitConverter.SingleToInt32Bits(invalid), BitConverter.SingleToInt32Bits(cacheX.PrimaryDpiScaleX));
        Assert.Equal(BitConverter.SingleToInt32Bits(invalid), BitConverter.SingleToInt32Bits(sourceY.ScaleY));
        Assert.True(cache.IsValid);
        Assert.True(source.IsValid);
    }

    [Fact]
    public void PolicyRequiresBothActualLimitsAndARevision()
    {
        var policy = new PortableBitmapCacheRasterPolicy(1, 2, 4096, 8192, 1);
        Assert.True(policy.IsValid);
        Assert.False((policy with { MaximumTextureWidth = 0 }).IsValid);
        Assert.False((policy with { MaximumTextureHeight = 0 }).IsValid);
        Assert.False((policy with { SourceRevision = 0 }).IsValid);
        // Numeric validation cannot certify provenance or silently replace a
        // limit. Exact live-device admission remains the backend's contract.
        Assert.Equal(uint.MaxValue, (policy with { MaximumTextureWidth = uint.MaxValue }).MaximumTextureWidth);
    }

    [Theory]
    [InlineData(PortablePrimaryDisplayRasterPolicy.WindowsSystemDpi)]
    [InlineData(PortablePrimaryDisplayRasterPolicy.PrimaryMonitorContentScale)]
    public void SourceSnapshotKeepsRawAxesIdentityAndRevision(PortablePrimaryDisplayRasterPolicy kind)
    {
        float x = BitConverter.Int32BitsToSingle(0x3f800001);
        float y = BitConverter.Int32BitsToSingle(0x3fbfffff);
        object identity = new ThrowingIdentity();
        var source = new PortablePrimaryDisplayRasterScale(x, y, kind, identity, 42);
        Assert.True(source.IsValid);
        Assert.Same(identity, source.SourceIdentity);
        Assert.Equal(0x3f800001, BitConverter.SingleToInt32Bits(source.ScaleX));
        Assert.Equal(0x3fbfffff, BitConverter.SingleToInt32Bits(source.ScaleY));
        Assert.Equal(42UL, source.Revision);
        Assert.True(source.Equals(source with { }));
        Assert.Equal(source.GetHashCode(), (source with { }).GetHashCode());
        Assert.False(source.Equals(source with { SourceIdentity = new ThrowingIdentity() }));
        Assert.False(source.Equals(source with { Revision = 43 }));
        Assert.False(source.Equals(source with { ScaleX = 1 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void UnknownSourcePolicyIsNotAPlatformFallback(int unknown)
    {
        var source = new PortablePrimaryDisplayRasterScale(1, 1,
            (PortablePrimaryDisplayRasterPolicy)unknown, new object(), 1);
        Assert.False(source.IsValid);
        Assert.Equal(unknown, (int)source.Policy);
    }

    [Fact]
    public void MissingSourceOwnershipOrGenerationIsInvalid()
    {
        var source = new PortablePrimaryDisplayRasterScale(1, 1,
            PortablePrimaryDisplayRasterPolicy.WindowsSystemDpi, new object(), 1);
        Assert.False((source with { SourceIdentity = null! }).IsValid);
        Assert.False((source with { Revision = 0 }).IsValid);
    }

    [Fact]
    public void SnapshotsDoNotCollapseDistinctMalformedFloatBits()
    {
        var source = new PortablePrimaryDisplayRasterScale(0, 1,
            PortablePrimaryDisplayRasterPolicy.WindowsSystemDpi, new object(), 1);
        var negativeZero = source with { ScaleX = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)) };
        Assert.False(source.Equals(negativeZero));
        Assert.False(source.IsValid);
        Assert.False(negativeZero.IsValid);
        var nan1 = source with { ScaleX = BitConverter.Int32BitsToSingle(0x7fc00001) };
        var nan2 = source with { ScaleX = BitConverter.Int32BitsToSingle(0x7fc00002) };
        Assert.False(nan1.Equals(nan2));
        Assert.False(nan1.IsValid);
        Assert.False(nan2.IsValid);
    }

    private sealed class ThrowingIdentity
    {
        public override bool Equals(object? value) => throw new InvalidOperationException("Identity callback must not run.");
        public override int GetHashCode() => throw new InvalidOperationException("Identity callback must not run.");
    }
}
