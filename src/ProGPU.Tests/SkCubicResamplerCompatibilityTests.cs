using System.Reflection;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkCubicResamplerCompatibilityTests
{
    [Fact]
    public void PublicContractMatchesNativeReadonlyValueShape()
    {
        var type = typeof(SKCubicResampler);

        Assert.True(type.IsValueType);
        Assert.True(type.IsAssignableTo(typeof(IEquatable<SKCubicResampler>)));
        Assert.NotNull(type.GetCustomAttribute<System.Runtime.CompilerServices.IsReadOnlyAttribute>());
        Assert.Equal(0f, SKCubicResampler.CatmullRom.B);
        Assert.Equal(0.5f, SKCubicResampler.CatmullRom.C);
        Assert.Equal(1f / 3f, SKCubicResampler.Mitchell.B);
        Assert.Equal(1f / 3f, SKCubicResampler.Mitchell.C);
    }

    [Fact]
    public void ConstructionPreservesCoefficientsWithoutValidation()
    {
        var resampler = new SKCubicResampler(float.NegativeInfinity, float.NaN);

        Assert.Equal(float.NegativeInfinity, resampler.B);
        Assert.True(float.IsNaN(resampler.C));
    }

    [Fact]
    public void EqualityUsesNativeFloatOperatorSemantics()
    {
        var left = new SKCubicResampler(-0f, 0.75f);
        var right = new SKCubicResampler(0f, 0.75f);
        var nan = new SKCubicResampler(float.NaN, 0.75f);
        var otherNan = new SKCubicResampler(float.NaN, 0.75f);

        Assert.True(left.Equals(right));
        Assert.True(left == right);
        Assert.False(left != right);
        Assert.True(left.Equals((object)right));
        Assert.False(left.Equals(null));
        Assert.False(nan.Equals(otherNan));
        Assert.True(nan != otherNan);
    }

    [Fact]
    public void EqualValuesHaveEqualHashes()
    {
        var left = new SKCubicResampler(-0f, 0.75f);
        var right = new SKCubicResampler(0f, 0.75f);

        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }
}
