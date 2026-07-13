using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkPathMetadataCompatibilityTests
{
    [Fact]
    public void LineMetadataMatchesNativePointAndVerbContract()
    {
        using var path = new SKPath();
        path.MoveTo(1f, 2f);
        path.LineTo(3f, 4f);

        Assert.True(path.IsLine);
        Assert.False(path.IsOval);
        Assert.False(path.IsRect);
        Assert.False(path.IsRoundRect);
        Assert.True(path.IsConvex);
        Assert.False(path.IsConcave);
        Assert.Equal(SKPathConvexity.Convex, path.Convexity);
        Assert.Equal(SKPathSegmentMask.Line, path.SegmentMasks);
        Assert.Equal(2, path.VerbCount);
        Assert.Equal(2, path.PointCount);
        Assert.Equal(new SKPoint(1f, 2f), path[0]);
        Assert.Equal(new SKPoint(3f, 4f), path.GetPoint(1));
        Assert.Equal(new SKPoint(3f, 4f), path.LastPoint);
        Assert.Equal(new[] { new SKPoint(1f, 2f), new SKPoint(3f, 4f) }, path.Points);
        Assert.Equal(path.Points, path.GetLine());
        Assert.Throws<ArgumentOutOfRangeException>(() => path.GetPoint(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => path.GetPoint(2));
    }

    [Fact]
    public void MixedCurveMetadataCopiesOnlyRequestedPointPrefix()
    {
        using var path = new SKPath();
        path.MoveTo(0f, 0f);
        path.QuadTo(1f, 2f, 3f, 4f);
        path.CubicTo(5f, 6f, 7f, 8f, 9f, 10f);

        Assert.Equal(3, path.VerbCount);
        Assert.Equal(6, path.PointCount);
        Assert.Equal(SKPathSegmentMask.Quad | SKPathSegmentMask.Cubic, path.SegmentMasks);
        Assert.Equal(new SKPoint(9f, 10f), path.LastPoint);
        Assert.Equal(
            new[]
            {
                new SKPoint(0f, 0f),
                new SKPoint(1f, 2f),
                new SKPoint(3f, 4f),
                new SKPoint(5f, 6f),
            },
            path.GetPoints(4));

        var destination = new SKPoint[8];
        Assert.Equal(6, path.GetPoints(destination, destination.Length));
        Assert.Equal(new SKPoint(9f, 10f), destination[5]);
        Assert.Equal(SKPoint.Empty, destination[6]);

        var oversized = path.GetPoints(10);
        Assert.Equal(10, oversized.Length);
        Assert.Equal(SKPoint.Empty, oversized[^1]);
    }

    [Theory]
    [InlineData(SKPathDirection.Clockwise)]
    [InlineData(SKPathDirection.CounterClockwise)]
    public void RectangleMetadataPreservesBoundsClosureAndDirection(SKPathDirection direction)
    {
        using var path = new SKPath();
        var expected = new SKRect(10f, 20f, 50f, 70f);
        path.AddRect(expected, direction);

        Assert.True(path.IsRect);
        Assert.True(path.IsConvex);
        Assert.Equal(5, path.VerbCount);
        Assert.Equal(4, path.PointCount);
        Assert.Equal(expected, path.GetRect(out var isClosed, out var actualDirection));
        Assert.True(isClosed);
        Assert.Equal(direction, actualDirection);
        Assert.Equal(path.GetRect(), expected);
        Assert.Equal(
            direction == SKPathDirection.Clockwise
                ? new SKPoint(expected.Left, expected.Bottom)
                : new SKPoint(expected.Right, expected.Top),
            path.LastPoint);
    }

    [Fact]
    public void OvalAndRoundRectMetadataRecoverNativeShapes()
    {
        using var oval = new SKPath();
        var ovalBounds = new SKRect(5f, 10f, 45f, 30f);
        oval.AddOval(ovalBounds);
        Assert.True(oval.IsOval);
        Assert.True(oval.IsConvex);
        Assert.Equal(9, oval.PointCount);
        Assert.Equal(6, oval.VerbCount);
        Assert.Equal(ovalBounds, oval.GetOvalBounds());

        using var source = new SKRoundRect();
        source.SetRectRadii(
            new SKRect(0f, 0f, 100f, 50f),
            [
                new SKPoint(3f, 4f),
                new SKPoint(5f, 6f),
                new SKPoint(7f, 8f),
                new SKPoint(9f, 10f),
            ]);
        using var path = new SKPath();
        path.AddRoundRect(source);

        Assert.True(path.IsRoundRect);
        Assert.True(path.IsConvex);
        using var recovered = path.GetRoundRect();
        Assert.NotNull(recovered);
        Assert.Equal(source.Rect, recovered.Rect);
        Assert.Equal(source.Radii, recovered.Radii);
    }

    [Fact]
    public void ConcavePolygonAndEmptyBoundsMatchNativeClassification()
    {
        using var empty = new SKPath();
        Assert.Equal(SKPathConvexity.Convex, empty.Convexity);
        Assert.True(empty.IsConvex);
        Assert.False(empty.IsConcave);
        Assert.False(empty.GetBounds(out var emptyBounds));
        Assert.False(empty.GetTightBounds(out var emptyTightBounds));
        Assert.Equal(SKRect.Empty, emptyBounds);
        Assert.Equal(SKRect.Empty, emptyTightBounds);

        using var path = new SKPath();
        path.MoveTo(0f, 0f);
        path.LineTo(10f, 0f);
        path.LineTo(5f, 5f);
        path.LineTo(10f, 10f);
        path.LineTo(0f, 10f);
        path.Close();

        Assert.False(path.IsConvex);
        Assert.True(path.IsConcave);
        Assert.Equal(SKPathConvexity.Concave, path.Convexity);
        Assert.True(path.GetBounds(out var bounds));
        Assert.True(path.GetTightBounds(out var tightBounds));
        Assert.Equal(new SKRect(0f, 0f, 10f, 10f), bounds);
        Assert.Equal(bounds, tightBounds);
        Assert.Equal(tightBounds, path.ComputeTightBounds());
    }
}
