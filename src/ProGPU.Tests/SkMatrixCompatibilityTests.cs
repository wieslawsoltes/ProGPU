using System;
using System.Numerics;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkMatrixCompatibilityTests
{
    [Fact]
    public void ValuesPropertiesConstructorsAndValidationMatchNative()
    {
        var values = new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f };
        var matrix = new SKMatrix(values);
        Assert.Equal(values, matrix.Values);

        var destination = new float[9];
        matrix.GetValues(destination);
        Assert.Equal(values, destination);

        matrix.Values = values.Reverse().ToArray();
        Assert.Equal(values.Reverse(), matrix.Values);
        Assert.Throws<ArgumentNullException>(() => new SKMatrix(null!));
        Assert.Throws<ArgumentException>(() => new SKMatrix(new float[8]));
        Assert.Throws<ArgumentNullException>(() => matrix.Values = null!);
        Assert.Throws<ArgumentException>(() => matrix.GetValues(new float[10]));
    }

    [Fact]
    public void FactoryMethodsMatchNativeMatrices()
    {
        Assert.Equal(SKMatrix.Identity, SKMatrix.CreateIdentity());
        Assert.Equal(SKMatrix.Identity, SKMatrix.CreateTranslation(0f, 0f));
        Assert.Equal(SKMatrix.Identity, SKMatrix.CreateScale(1f, 1f));
        Assert.Equal(SKMatrix.Identity, SKMatrix.CreateSkew(0f, 0f));
        Assert.Equal(SKMatrix.Identity, SKMatrix.CreateScaleTranslation(0f, 0f, 0f, 0f));
        Assert.Equal(new SKMatrix(2f, 0f, -10f, 0f, 3f, -40f, 0f, 0f, 1f),
            SKMatrix.CreateScale(2f, 3f, 10f, 20f));
        Assert.Equal(new SKMatrix(1f, 2f, 0f, 3f, 1f, 0f, 0f, 0f, 1f),
            SKMatrix.CreateSkew(2f, 3f));

        var rotation = SKMatrix.CreateRotationDegrees(90f, 10f, 20f);
        Assert.Equal(-4.371139E-08f, rotation.ScaleX);
        Assert.Equal(-1f, rotation.SkewX);
        Assert.Equal(30f, rotation.TransX);
        Assert.Equal(1f, rotation.SkewY);
        Assert.Equal(10f, rotation.TransY);
    }

    [Fact]
    public void ConcatenationPreservesNativeOrderAndPerspectiveTerms()
    {
        var scale = SKMatrix.CreateScale(2f, 3f);
        var translate = SKMatrix.CreateTranslation(10f, 20f);
        Assert.Equal(new SKMatrix(2f, 0f, 20f, 0f, 3f, 60f, 0f, 0f, 1f),
            SKMatrix.Concat(scale, translate));
        Assert.Equal(SKMatrix.Concat(scale, translate), scale.PreConcat(translate));
        Assert.Equal(new SKMatrix(2f, 0f, 10f, 0f, 3f, 20f, 0f, 0f, 1f),
            scale.PostConcat(translate));

        var perspective = new SKMatrix(2f, 3f, 5f, 7f, 11f, 13f, 0.25f, -0.5f, 2f);
        Assert.Equal(
            new SKMatrix(26.25f, 36.5f, 59f, 94.25f, 135.5f, 204f, -2.5f, -5.75f, -1.25f),
            SKMatrix.Concat(perspective, perspective));

        var target = SKMatrix.Empty;
        SKMatrix.Concat(ref target, scale, translate);
        Assert.Equal(SKMatrix.Concat(scale, translate), target);
    }

    [Fact]
    public void InversionMatchesNativeFailureAndPerspectiveSemantics()
    {
        var perspective = new SKMatrix(2f, 3f, 5f, 7f, 11f, 13f, 0.25f, -0.5f, 2f);
        Assert.True(perspective.IsInvertible);
        Assert.True(perspective.TryInvert(out var inverse));
        AssertNearIdentity(SKMatrix.Concat(perspective, inverse));
        Assert.Equal(inverse, perspective.Invert());

        var singular = new SKMatrix(1f, 2f, 3f, 2f, 4f, 6f, 0f, 0f, 1f);
        Assert.False(singular.IsInvertible);
        Assert.False(singular.TryInvert(out var failed));
        Assert.Equal(SKMatrix.Empty, failed);
        Assert.Equal(SKMatrix.Empty, singular.Invert());

        var tiny = SKMatrix.CreateScale(1e-20f, 1e-20f);
        Assert.True(tiny.TryInvert(out var tinyInverse));
        Assert.Equal(1e20f, tinyInverse.ScaleX);
        Assert.Equal(1e20f, tinyInverse.ScaleY);
    }

    [Fact]
    public void PointAndVectorMappingHandlePerspectiveAndZeroW()
    {
        var perspective = new SKMatrix(2f, 3f, 5f, 7f, 11f, 13f, 0.25f, -0.5f, 2f);
        Assert.Equal(SKPoint.Empty, perspective.MapPoint(4f, 6f));
        Assert.Equal(new SKPoint(-2.5f, -6.5f), perspective.MapVector(4f, 6f));
        Assert.Equal(new SKPoint(10.400001f, 33.600002f), perspective.MapPoint(1f, 2f));

        var affine = SKMatrix.CreateScale(2f, 3f).PostConcat(SKMatrix.CreateTranslation(10f, 20f));
        Assert.Equal(new SKPoint(12f, 23f), affine.MapPoint(new SKPoint(1f, 1f)));
        Assert.Equal(new SKPoint(2f, 3f), affine.MapVector(new SKPoint(1f, 1f)));
    }

    [Fact]
    public void PointAndVectorBuffersValidateAndSupportInPlaceMapping()
    {
        var matrix = SKMatrix.CreateScale(2f, 3f).PreConcat(SKMatrix.CreateTranslation(10f, 20f));
        var points = new[] { new SKPoint(1f, 2f), new SKPoint(3f, 4f) };
        Assert.Equal(
            new[] { new SKPoint(22f, 66f), new SKPoint(26f, 72f) },
            matrix.MapPoints(points));

        matrix.MapPoints(points, points);
        Assert.Equal(new[] { new SKPoint(22f, 66f), new SKPoint(26f, 72f) }, points);

        var vectors = new[] { new SKPoint(1f, 2f), new SKPoint(3f, 4f) };
        matrix.MapVectors(vectors, vectors);
        Assert.Equal(new[] { new SKPoint(2f, 6f), new SKPoint(6f, 12f) }, vectors);

        Assert.Throws<ArgumentException>(() => matrix.MapPoints(new SKPoint[1], new SKPoint[2]));
        Assert.Throws<ArgumentNullException>(() => matrix.MapVectors(null!, vectors));
    }

    [Fact]
    public void RectangleMappingClipsPerspectiveAtNativeNearPlane()
    {
        var perspective = new SKMatrix(2f, 3f, 5f, 7f, 11f, 13f, 0.25f, -0.5f, 2f);
        var bounds = perspective.MapRect(new SKRect(1f, 2f, 5f, 8f));
        Assert.Equal(9.333333f, bounds.Left, precision: 5);
        Assert.Equal(31.11111f, bounds.Top, precision: 4);
        Assert.Equal(565242f, bounds.Right);
        Assert.Equal(1957866f, bounds.Bottom);
        Assert.Equal(bounds, perspective.MapRect(new SKRect(5f, 8f, 1f, 2f)));
        Assert.Equal(SKRect.Empty, perspective.MapRect(new SKRect(1f, 8f, 5f, 12f)));

        Assert.Equal(new SKRect(2f, 6f, 10f, 24f),
            SKMatrix.CreateScale(2f, 3f).MapRect(new SKRect(5f, 8f, 1f, 2f)));
    }

    [Fact]
    public void AffineRectangleMappingPreservesSkewAndNegativeScaleBounds()
    {
        var skewed = new SKMatrix(2f, 0.5f, 10f, -0.25f, 3f, 20f, 0f, 0f, 1f);
        Assert.Equal(
            new SKRect(13f, 24.5f, 26f, 43.75f),
            skewed.MapRect(new SKRect(1f, 2f, 6f, 8f)));

        var reflected = SKMatrix.CreateScale(-2f, -3f).PostConcat(
            SKMatrix.CreateTranslation(10f, 20f));
        Assert.Equal(
            new SKRect(-2f, -4f, 8f, 14f),
            reflected.MapRect(new SKRect(1f, 2f, 6f, 8f)));
    }

    [Fact]
    public void RadiusEqualityHashAndInternalMatrixConversionMatchNativeValues()
    {
        var matrix = SKMatrix.CreateScale(2f, 3f);
        Assert.Equal(4.8989797f, matrix.MapRadius(2f));
        Assert.True(matrix == new SKMatrix(matrix.Values));
        Assert.False(matrix != new SKMatrix(matrix.Values));
        Assert.Equal(matrix.GetHashCode(), new SKMatrix(matrix.Values).GetHashCode());

        var perspective = new SKMatrix(2f, 3f, 5f, 7f, 11f, 13f, 0.25f, -0.5f, 2f);
        var converted = perspective.ToMatrix4x4();
        Assert.Equal(0.25f, converted.M14);
        Assert.Equal(-0.5f, converted.M24);
        Assert.Equal(2f, converted.M44);
        Assert.Equal(perspective, SKMatrix.FromMatrix4x4(converted));
    }

    private static void AssertNearIdentity(SKMatrix matrix)
    {
        Assert.Equal(1f, matrix.ScaleX, precision: 5);
        Assert.Equal(0f, matrix.SkewX, precision: 5);
        Assert.Equal(0f, matrix.TransX, precision: 5);
        Assert.Equal(0f, matrix.SkewY, precision: 5);
        Assert.Equal(1f, matrix.ScaleY, precision: 5);
        Assert.Equal(0f, matrix.TransY, precision: 5);
        Assert.Equal(0f, matrix.Persp0, precision: 5);
        Assert.Equal(0f, matrix.Persp1, precision: 5);
        Assert.Equal(1f, matrix.Persp2, precision: 5);
    }
}
