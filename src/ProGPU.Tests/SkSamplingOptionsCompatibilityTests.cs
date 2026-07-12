using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkSamplingOptionsCompatibilityTests
{
    [Fact]
    public void CubicResamplersPreserveNativeCoefficientsAndValueIdentity()
    {
        Assert.Equal(new SKCubicResampler(1f / 3f, 1f / 3f), SKCubicResampler.Mitchell);
        Assert.Equal(new SKCubicResampler(0f, 0.5f), SKCubicResampler.CatmullRom);
        Assert.NotEqual(SKCubicResampler.Mitchell, SKCubicResampler.CatmullRom);
        Assert.Equal(SKCubicResampler.Mitchell.GetHashCode(), new SKCubicResampler(1f / 3f, 1f / 3f).GetHashCode());
    }

    [Fact]
    public void FilterAndCubicConstructorsInitializeOnlyTheirNativeModes()
    {
        Assert.Equal(default, SKSamplingOptions.Default);

        var nearest = new SKSamplingOptions(SKFilterMode.Nearest);
        Assert.Equal(SKFilterMode.Nearest, nearest.Filter);
        Assert.Equal(SKMipmapMode.None, nearest.Mipmap);
        Assert.False(nearest.UseCubic);
        Assert.False(nearest.IsAniso);

        var linearMipmap = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        Assert.Equal(SKFilterMode.Linear, linearMipmap.Filter);
        Assert.Equal(SKMipmapMode.Linear, linearMipmap.Mipmap);
        Assert.False(linearMipmap.UseCubic);

        var cubic = new SKSamplingOptions(new SKCubicResampler(0.2f, 0.4f));
        Assert.True(cubic.UseCubic);
        Assert.Equal(new SKCubicResampler(0.2f, 0.4f), cubic.Cubic);
        Assert.Equal(default, cubic.Filter);
        Assert.Equal(default, cubic.Mipmap);
        Assert.False(cubic.IsAniso);
    }

    [Fact]
    public void AnisotropyConstructorClampsToOneWhileDefaultRemainsDisabled()
    {
        Assert.Equal(0, SKSamplingOptions.Default.MaxAniso);
        Assert.False(SKSamplingOptions.Default.IsAniso);

        var negative = new SKSamplingOptions(-8);
        var zero = new SKSamplingOptions(0);
        var eight = new SKSamplingOptions(8);

        Assert.Equal(1, negative.MaxAniso);
        Assert.Equal(1, zero.MaxAniso);
        Assert.Equal(8, eight.MaxAniso);
        Assert.True(negative.IsAniso);
        Assert.True(zero.IsAniso);
        Assert.True(eight.IsAniso);
        Assert.False(eight.UseCubic);
        Assert.Equal(default, eight.Filter);
        Assert.Equal(default, eight.Mipmap);
        Assert.NotEqual(SKSamplingOptions.Default, zero);
    }
}
