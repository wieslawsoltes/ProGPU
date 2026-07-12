using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkRoundRectCompatibilityTests
{
    [Fact]
    public void ConstructorsNormalizeBoundsAndClassifyNativeShapes()
    {
        using var empty = new SKRoundRect();
        Assert.Equal(SKRoundRectType.Empty, empty.Type);
        Assert.Equal(SKRect.Empty, empty.Rect);
        Assert.True(empty.IsValid);

        using var rect = new SKRoundRect(new SKRect(30f, 20f, 10f, 5f));
        Assert.Equal(new SKRect(10f, 5f, 30f, 20f), rect.Rect);
        Assert.Equal(SKRoundRectType.Rect, rect.Type);

        using var simple = new SKRoundRect(new SKRect(0f, 0f, 20f, 10f), 100f, 30f);
        Assert.Equal(SKRoundRectType.Simple, simple.Type);
        Assert.All(simple.Radii, radius => Assert.Equal(new SKPoint(10f, 3f), radius));

        using var circular = new SKRoundRect(new SKRect(0f, 0f, 20f, 10f), 3f);
        Assert.True(circular.AllCornersCircular);
        Assert.Equal(SKRoundRectType.Simple, circular.Type);

        using var copy = new SKRoundRect(simple);
        simple.Offset(7f, -3f);
        Assert.Equal(new SKRect(0f, 0f, 20f, 10f), copy.Rect);
        Assert.Equal(new SKRect(7f, -3f, 27f, 7f), simple.Rect);
    }

    [Fact]
    public void SettersClampScaleAndOwnCornerRadiiLikeNativeSkia()
    {
        using var roundRect = new SKRoundRect();
        roundRect.SetOval(new SKRect(0f, 0f, 20f, 10f));
        Assert.Equal(SKRoundRectType.Oval, roundRect.Type);
        Assert.All(roundRect.Radii, radius => Assert.Equal(new SKPoint(10f, 5f), radius));

        roundRect.SetNinePatch(new SKRect(0f, 0f, 20f, 10f), 8f, 3f, 4f, 2f);
        Assert.Equal(SKRoundRectType.NinePatch, roundRect.Type);
        Assert.Equal(
            new[] { new SKPoint(8f, 3f), new SKPoint(4f, 3f), new SKPoint(4f, 2f), new SKPoint(8f, 2f) },
            roundRect.Radii);

        var source = new[]
        {
            new SKPoint(10f, 2f),
            new SKPoint(4f, 6f),
            new SKPoint(7f, 8f),
            new SKPoint(9f, 5f),
        };
        roundRect.SetRectRadii(new SKRect(0f, 0f, 20f, 10f), source.AsSpan());
        source[0] = default;

        Assert.Equal(SKRoundRectType.Complex, roundRect.Type);
        AssertPointNear(new SKPoint(7.142857f, 1.4285715f), roundRect.GetRadii(SKRoundRectCorner.UpperLeft));
        AssertPointNear(new SKPoint(2.857143f, 4.285714f), roundRect.GetRadii(SKRoundRectCorner.UpperRight));
        var publicRadii = roundRect.Radii;
        publicRadii[0] = default;
        Assert.NotEqual(default, roundRect.GetRadii(SKRoundRectCorner.UpperLeft));

        Assert.Throws<ArgumentNullException>(() => roundRect.SetRectRadii(SKRect.Empty, null!));
        Assert.Throws<ArgumentException>(() => roundRect.SetRectRadii(SKRect.Empty, new SKPoint[3]));

        roundRect.SetRect(new SKRect(0f, 0f, 20f, 10f), float.NaN, 3f);
        Assert.Equal(SKRoundRectType.Rect, roundRect.Type);
        Assert.All(roundRect.Radii, radius => Assert.Equal(default, radius));
    }

    [Fact]
    public void ContainmentUsesEachEllipticalCornerAndRejectsEmptyRects()
    {
        using var roundRect = CreateComplex();

        Assert.True(roundRect.Contains(new SKRect(8f, 4f, 12f, 6f)));
        Assert.False(roundRect.Contains(new SKRect(0f, 0f, 1f, 1f)));
        Assert.False(roundRect.Contains(SKRect.Empty));
        Assert.False(roundRect.Contains(new SKRect(-1f, 4f, 12f, 6f)));
        Assert.False(roundRect.AllCornersCircular);
        Assert.False(roundRect.CheckAllCornersCircular(float.NaN));
    }

    [Fact]
    public void InsetOutsetAndOffsetPreserveSquareCornersAndNativeClassification()
    {
        using var roundRect = CreateComplex();

        roundRect.Deflate(new SKSize(3f, 2f));
        Assert.Equal(new SKRect(3f, 2f, 17f, 8f), roundRect.Rect);
        Assert.Equal(default, roundRect.GetRadii(SKRoundRectCorner.UpperLeft));
        AssertPointNear(new SKPoint(2f, 3.7142859f), roundRect.GetRadii(SKRoundRectCorner.LowerRight));

        roundRect.Inflate(5f, 4f);
        Assert.Equal(new SKRect(-2f, -2f, 22f, 12f), roundRect.Rect);
        Assert.Equal(default, roundRect.GetRadii(SKRoundRectCorner.UpperRight));
        AssertPointNear(new SKPoint(8.428572f, 5.5714283f), roundRect.GetRadii(SKRoundRectCorner.LowerLeft));

        roundRect.Offset(new SKPoint(2f, 3f));
        Assert.Equal(new SKRect(0f, 1f, 24f, 15f), roundRect.Rect);

        roundRect.Deflate(20f, 20f);
        Assert.Equal(SKRoundRectType.Empty, roundRect.Type);
        Assert.True(roundRect.IsValid);
    }

    [Fact]
    public void AxisPreservingTransformsReconstructMappedCornerContours()
    {
        using var roundRect = CreateComplex();

        Assert.True(roundRect.TryTransform(SKMatrix.CreateTranslation(7f, -3f), out var translated));
        using (translated)
        {
            Assert.NotNull(translated);
            Assert.Equal(new SKRect(7f, -3f, 27f, 7f), translated.Rect);
            AssertPointNear(new SKPoint(7.1428566f, 1.4285715f), translated.GetRadii(SKRoundRectCorner.UpperLeft));
        }

        var rotate90 = new SKMatrix(0f, -2f, 20f, 3f, 0f, 0f, 0f, 0f, 1f);
        using var rotated = roundRect.Transform(rotate90);
        Assert.NotNull(rotated);
        Assert.Equal(new SKRect(0f, 0f, 20f, 60f), rotated.Rect);
        AssertPointNear(new SKPoint(7.1428566f, 19.285713f), rotated.GetRadii(SKRoundRectCorner.UpperLeft));

        var rotate30 = SKMatrix.CreateRotationDegrees(30f);
        Assert.False(roundRect.TryTransform(rotate30, out var unsupported));
        Assert.Null(unsupported);

        var perspective = SKMatrix.Identity;
        perspective.Persp0 = 0.01f;
        Assert.Null(roundRect.Transform(perspective));
    }

    [Fact]
    public void InternalQueriesDoNotAllocateOnRetainedGeometryHotPath()
    {
        using var roundRect = CreateComplex();
        _ = roundRect.IsValid;
        _ = roundRect.Type;
        _ = roundRect.CornerRadii[0];

        var before = GC.GetAllocatedBytesForCurrentThread();
        var valid = true;
        for (var index = 0; index < 10_000; index++)
        {
            valid &= roundRect.IsValid;
            valid &= roundRect.Type == SKRoundRectType.Complex;
            valid &= roundRect.CornerRadii[0].X > 0f;
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(before, after);
        Assert.True(valid);
    }

    private static SKRoundRect CreateComplex()
    {
        var roundRect = new SKRoundRect();
        roundRect.SetRectRadii(
            new SKRect(0f, 0f, 20f, 10f),
            new[]
            {
                new SKPoint(10f, 2f),
                new SKPoint(4f, 6f),
                new SKPoint(7f, 8f),
                new SKPoint(9f, 5f),
            });
        return roundRect;
    }

    private static void AssertPointNear(SKPoint expected, SKPoint actual, float tolerance = 0.000001f)
    {
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
    }
}
