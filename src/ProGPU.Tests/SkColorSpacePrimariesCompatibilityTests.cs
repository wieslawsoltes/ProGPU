using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkColorSpacePrimariesCompatibilityTests
{
    [Fact]
    public void ConstructorsAndOwnedValuesMatchTheEightCoordinateContract()
    {
        var source = new[] { 0.64f, 0.33f, 0.30f, 0.60f, 0.15f, 0.06f, 0.3127f, 0.3290f };
        var value = new SKColorSpacePrimaries(source);
        source[0] = 99f;

        Assert.Equal(0.64f, value.RX);
        var values = value.Values;
        values[0] = 42f;
        Assert.Equal(0.64f, value.RX);
        Assert.Equal("values", Assert.Throws<ArgumentNullException>(() => new SKColorSpacePrimaries((float[])null!)).ParamName);
        Assert.Equal("values", Assert.Throws<ArgumentException>(() => new SKColorSpacePrimaries(new float[7])).ParamName);
    }

    [Fact]
    public void MutableCoordinatesEqualityAndHashCoverEveryScalar()
    {
        var value = new SKColorSpacePrimaries(0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f);
        var equal = new SKColorSpacePrimaries(value.Values);
        Assert.True(value == equal);
        Assert.False(value != equal);
        Assert.True(value.Equals((object)equal));
        Assert.Equal(value.GetHashCode(), equal.GetHashCode());

        equal.WY = 0.9f;
        Assert.NotEqual(value, equal);
        Assert.Equal(new float[8], SKColorSpacePrimaries.Empty.Values);
    }

    [Fact]
    public void SrgbAndDisplayP3ChromaticitiesConvertToD50()
    {
        var srgb = new SKColorSpacePrimaries(0.64f, 0.33f, 0.30f, 0.60f, 0.15f, 0.06f, 0.3127f, 0.3290f);
        var p3 = new SKColorSpacePrimaries(0.68f, 0.32f, 0.265f, 0.69f, 0.15f, 0.06f, 0.3127f, 0.3290f);

        Assert.True(srgb.ToColorSpaceXyz(out var srgbXyz));
        AssertMatrixNear(
            [0.43602818f, 0.38510093f, 0.14309105f, 0.22247864f, 0.7168975f, 0.060624108f, 0.013926373f, 0.097092114f, 0.7141915f],
            srgbXyz);
        Assert.True(p3.ToColorSpaceXyz(out var p3Xyz));
        AssertMatrixNear(
            [0.51510215f, 0.29196474f, 0.1571531f, 0.24118185f, 0.6922364f, 0.06658185f, -0.0010494092f, 0.041881755f, 0.7843777f],
            p3Xyz);
        Assert.Equal(srgbXyz, srgb.ToColorSpaceXyz());
    }

    [Fact]
    public void InvalidOrDegenerateCoordinatesFailWithoutPartialMatrix()
    {
        var invalid = new SKColorSpacePrimaries(-0.1f, 0.3f, 0.3f, 0.6f, 0.15f, 0.06f, 0.3127f, 0.3290f);
        Assert.False(invalid.ToColorSpaceXyz(out var invalidMatrix));
        Assert.Equal(SKColorSpaceXyz.Empty, invalidMatrix);

        var degenerate = new SKColorSpacePrimaries(0.64f, 0.33f, 0.64f, 0.33f, 0.15f, 0.06f, 0.3127f, 0.3290f);
        Assert.False(degenerate.ToColorSpaceXyz(out var degenerateMatrix));
        Assert.Equal(SKColorSpaceXyz.Empty, degenerateMatrix);
        Assert.Equal(SKColorSpaceXyz.Empty, degenerate.ToColorSpaceXyz());
    }

    [Fact]
    public void BoundaryPrimaryWithZeroYRemainsConvertible()
    {
        var value = new SKColorSpacePrimaries(0.64f, 0f, 0.30f, 0.60f, 0.15f, 0.06f, 0.3127f, 0.3290f);
        Assert.True(value.ToColorSpaceXyz(out var matrix));
        AssertMatrixNear(
            [0.344415f, 0.50813913f, 0.1116658f, 0.0067466097f, 0.9459435f, 0.047310017f, 0.13975444f, 0.12811267f, 0.5573429f],
            matrix);
    }

    private static void AssertMatrixNear(float[] expected, SKColorSpaceXyz actual)
    {
        Assert.Equal(expected.Length, actual.Values.Length);
        for (var index = 0; index < expected.Length; index++)
            Assert.InRange(actual.Values[index], expected[index] - 0.000001f, expected[index] + 0.000001f);
    }
}
