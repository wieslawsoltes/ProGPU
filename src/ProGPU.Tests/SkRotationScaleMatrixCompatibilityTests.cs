using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkRotationScaleMatrixCompatibilityTests
{
    [Fact]
    public void ConvenienceFactoriesMatchNativeValues()
    {
        Assert.Equal(new SKRotationScaleMatrix(1f, 0f, 0f, 0f), SKRotationScaleMatrix.CreateIdentity());
        Assert.Equal(new SKRotationScaleMatrix(1f, 0f, 7.25f, -3.5f), SKRotationScaleMatrix.CreateTranslation(7.25f, -3.5f));
        Assert.Equal(new SKRotationScaleMatrix(-2.75f, 0f, 0f, 0f), SKRotationScaleMatrix.CreateScale(-2.75f));

        AssertBits(
            SKRotationScaleMatrix.CreateRotation(0.37f, 12.5f, -4.25f),
            0x3f6ead01,
            0x3eb925a9,
            unchecked((int)0xc1530e2a),
            unchecked((int)0xbf0ecc1c));
        AssertBits(
            SKRotationScaleMatrix.CreateRotationDegrees(37.25f, 12.5f, -4.25f),
            0x3f4bc6c9,
            0x3f1af48c,
            unchecked((int)0xc1485c42),
            unchecked((int)0xc085dc80));
    }

    [Fact]
    public void ScaleRotationTranslationFactoriesMatchNativeValues()
    {
        AssertBits(
            SKRotationScaleMatrix.Create(1.75f, 0.37f, 8.5f, -2.25f, 12.5f, -4.25f),
            0x3fd0d761,
            0x3f2200f4,
            unchecked((int)0xc16958c9),
            unchecked((int)0xc04e794a));
        AssertBits(
            SKRotationScaleMatrix.CreateDegrees(1.75f, 37.25f, 8.5f, -2.25f, 12.5f, -4.25f),
            0x3fb24df0,
            0x3f8795fa,
            unchecked((int)0xc156a175),
            unchecked((int)0xc11920f0));
    }

    [Fact]
    public void MatrixConversionUsesNativeRotationScaleLayout()
    {
        var value = new SKRotationScaleMatrix(2f, 3f, 5f, 7f);

        Assert.Equal(new SKMatrix(2f, -3f, 5f, 3f, 2f, 7f, 0f, 0f, 1f), value.ToMatrix());
    }

    [Fact]
    public void EqualityAndHashingUseNativeFloatSemantics()
    {
        var left = new SKRotationScaleMatrix(-0f, 1f, 2f, 3f);
        var right = new SKRotationScaleMatrix(0f, 1f, 2f, 3f);
        var nan = new SKRotationScaleMatrix(float.NaN, 1f, 2f, 3f);
        var otherNan = new SKRotationScaleMatrix(float.NaN, 1f, 2f, 3f);

        Assert.True(left == right);
        Assert.False(left != right);
        Assert.True(left.Equals((object)right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.False(nan.Equals(otherNan));
    }

    private static void AssertBits(
        SKRotationScaleMatrix actual,
        int scos,
        int ssin,
        int tx,
        int ty)
    {
        Assert.Equal(scos, BitConverter.SingleToInt32Bits(actual.SCos));
        Assert.Equal(ssin, BitConverter.SingleToInt32Bits(actual.SSin));
        Assert.Equal(tx, BitConverter.SingleToInt32Bits(actual.TX));
        Assert.Equal(ty, BitConverter.SingleToInt32Bits(actual.TY));
    }
}
