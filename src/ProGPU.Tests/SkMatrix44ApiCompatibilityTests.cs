using System.Numerics;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkMatrix44ApiCompatibilityTests
{
    [Fact]
    public void Matrix44IsNativeValueTypeWithZeroAndIdentityConstants()
    {
        Assert.True(typeof(SKMatrix44).IsValueType);
        Assert.Equal(SKMatrix44.Empty, new SKMatrix44());
        Assert.Equal(SKMatrix44.Identity, SKMatrix44.CreateIdentity());
        Assert.Equal(Matrix4x4.Identity, (Matrix4x4)SKMatrix44.Identity);
        Assert.NotEqual(SKMatrix44.Empty, SKMatrix44.Identity);
    }

    [Fact]
    public void Matrix44RoundTripsRowColumnAndIndexerLayouts()
    {
        var rowMajor = Enumerable.Range(1, 16).Select(static value => (float)value).ToArray();
        var matrix = SKMatrix44.FromRowMajor(rowMajor);

        Assert.Equal(rowMajor, matrix.ToRowMajor());
        Assert.Equal(
            new float[]
            {
                1f, 5f, 9f, 13f,
                2f, 6f, 10f, 14f,
                3f, 7f, 11f, 15f,
                4f, 8f, 12f, 16f,
            },
            matrix.ToColumnMajor());
        Assert.Equal(matrix, SKMatrix44.FromColumnMajor(matrix.ToColumnMajor()));

        matrix[2, 3] = 42f;
        Assert.Equal(42f, matrix.M23);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = matrix[4, 0]);
        Assert.Throws<ArgumentException>(() => SKMatrix44.FromRowMajor(new float[15]));
        Assert.Throws<ArgumentException>(() => matrix.ToColumnMajor(new float[17]));
    }

    [Fact]
    public void Matrix44PreservesNativeTwoDimensionalProjection()
    {
        var matrix = new SKMatrix(
            2f,
            0.25f,
            7f,
            -0.5f,
            3f,
            11f,
            0.01f,
            -0.02f,
            1f);
        SKMatrix44 matrix44 = matrix;

        Assert.Equal(matrix, matrix44.Matrix);
        Assert.Equal(2f, matrix44.M00);
        Assert.Equal(-0.5f, matrix44.M01);
        Assert.Equal(0.25f, matrix44.M10);
        Assert.Equal(7f, matrix44.M30);
        Assert.Equal(11f, matrix44.M31);
        Assert.Equal(0.01f, matrix44.M03);
        Assert.Equal(-0.02f, matrix44.M13);
        Assert.Equal(1f, matrix44.M33);
    }

    [Fact]
    public void Matrix44ConcatInvertAndMapMatchSystemNumerics()
    {
        var translation = SKMatrix44.CreateTranslation(7f, 11f, 13f);
        var scale = SKMatrix44.CreateScale(2f, 3f, 4f, 1f, 2f, 3f);
        var rotation = SKMatrix44.CreateRotationDegrees(0f, 0f, 1f, 32f);
        var combined = SKMatrix44.Concat(translation, scale) * rotation;
        var expected = (Matrix4x4)translation * (Matrix4x4)scale * (Matrix4x4)rotation;

        AssertMatrixNear(expected, combined);
        AssertMatrixNear((Matrix4x4)combined * (Matrix4x4)scale, combined.PreConcat(scale));
        AssertMatrixNear((Matrix4x4)scale * (Matrix4x4)combined, combined.PostConcat(scale));
        Assert.True(combined.IsInvertible);
        Assert.True(combined.TryInvert(out var inverse));
        AssertMatrixNear(Matrix4x4.Identity, combined * inverse);
        AssertNear(expected.GetDeterminant(), combined.Determinant());
        AssertMatrixNear(Matrix4x4.Transpose(expected), combined.Transpose());

        var point2 = new SKPoint(5f, 9f);
        var expected2 = Vector2.Transform(new Vector2(point2.X, point2.Y), expected);
        AssertPointNear(new SKPoint(expected2.X, expected2.Y), combined.MapPoint(point2));
        var point3 = new SKPoint3(5f, 9f, 12f);
        var expected3 = Vector3.Transform(new Vector3(point3.X, point3.Y, point3.Z), expected);
        AssertPointNear(new SKPoint3(expected3.X, expected3.Y, expected3.Z), combined.MapPoint(point3));

        var singular = SKMatrix44.CreateScale(0f, 1f, 1f);
        Assert.False(singular.TryInvert(out var singularInverse));
        Assert.Equal(SKMatrix44.Empty, singularInverse);
        Assert.Equal(SKMatrix44.Empty, singular.Invert());
    }

    private static void AssertMatrixNear(Matrix4x4 expected, SKMatrix44 actual)
    {
        var expectedValues = ((SKMatrix44)expected).ToRowMajor();
        var actualValues = actual.ToRowMajor();
        for (var index = 0; index < expectedValues.Length; index++)
        {
            AssertNear(expectedValues[index], actualValues[index]);
        }
    }

    private static void AssertPointNear(SKPoint expected, SKPoint actual)
    {
        AssertNear(expected.X, actual.X);
        AssertNear(expected.Y, actual.Y);
    }

    private static void AssertPointNear(SKPoint3 expected, SKPoint3 actual)
    {
        AssertNear(expected.X, actual.X);
        AssertNear(expected.Y, actual.Y);
        AssertNear(expected.Z, actual.Z);
    }

    private static void AssertNear(float expected, float actual) =>
        Assert.InRange(actual, expected - 0.0001f, expected + 0.0001f);
}
