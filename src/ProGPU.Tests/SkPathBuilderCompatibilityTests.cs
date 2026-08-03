using System.Numerics;
using ProGPU.Vector;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkPathBuilderCompatibilityTests
{
    [Fact]
    public void DetachedPackedBuilderComputesTightBoundsOnlyWhenRequested()
    {
        using var builder = new SKPathBuilder();
        builder.MoveTo(0f, 0f);
        builder.QuadTo(10f, 100f, 20f, 0f);
        using var path = builder.Detach();

        Assert.Equal(new SKRect(0f, 0f, 20f, 100f), path.Bounds);
        Assert.Equal(new SKRect(0f, 0f, 20f, 50f), path.TightBounds);
        Assert.Equal(new SKRect(0f, 0f, 20f, 50f), path.TightBounds);
    }

    [Fact]
    public void PackedDetachAndBoundsStayBoundedIndependentOfSegmentCount()
    {
        static (long AllocatedBytes, SKRect Bounds) Measure(int segmentCount)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            using var builder = new SKPathBuilder();
            builder.MoveTo(1f, 2f);
            for (var index = 0; index < segmentCount; index++)
            {
                builder.LineTo(index + 3f, index + 4f);
                builder.QuadTo(index + 5f, index + 6f, index + 7f, index + 8f);
                builder.CubicTo(
                    index + 9f,
                    index + 10f,
                    index + 11f,
                    index + 12f,
                    index + 13f,
                    index + 14f);
            }

            builder.Close();
            using var path = builder.Detach();
            var bounds = path.Bounds;
            return (GC.GetAllocatedBytesForCurrentThread() - before, bounds);
        }

        _ = Measure(4);
        _ = Measure(256);
        var small = Measure(4);
        var large = Measure(256);

        Assert.InRange(small.AllocatedBytes, 0, 192);
        Assert.InRange(large.AllocatedBytes, 0, 192);
        Assert.InRange(Math.Abs(large.AllocatedBytes - small.AllocatedBytes), 0, 32);
        Assert.Equal(new SKRect(1f, 2f, 16f, 17f), small.Bounds);
        Assert.Equal(new SKRect(1f, 2f, 268f, 269f), large.Bounds);

        using var replacementBuilder = new SKPathBuilder();
        replacementBuilder.MoveTo(-100f, -200f);
        replacementBuilder.MoveTo(5f, 7f);
        replacementBuilder.LineTo(11f, 13f);
        using var replacementPath = replacementBuilder.Detach();
        Assert.Equal(new SKRect(5f, 7f, 11f, 13f), replacementPath.Bounds);
        Assert.Single(replacementPath.Geometry.Figures);
    }

    [Fact]
    public void SnapshotDetachAndResetPreserveNativeOwnershipAndFillRules()
    {
        using var builder = new SKPathBuilder { FillType = SKPathFillType.EvenOdd };
        builder.MoveTo(1, 2);
        builder.LineTo(3, 4);
        using var snapshot = builder.Snapshot();
        builder.LineTo(5, 6);
        using var detached = builder.Detach();
        using var afterDetach = builder.Snapshot();

        Assert.Equal(SKPathFillType.EvenOdd, snapshot.FillType);
        Assert.Equal(SKPathFillType.EvenOdd, detached.FillType);
        Assert.Single(snapshot.Geometry.Figures[0].Segments);
        Assert.Equal(2, detached.Geometry.Figures[0].Segments.Count);
        Assert.True(afterDetach.IsEmpty);
        Assert.Equal(SKPathFillType.Winding, builder.FillType);

        builder.FillType = SKPathFillType.InverseWinding;
        builder.MoveTo(7, 8);
        builder.Reset();
        Assert.Equal(SKPathFillType.Winding, builder.FillType);
        using var afterReset = builder.Snapshot();
        Assert.True(afterReset.IsEmpty);

        using var copiedBuilder = new SKPathBuilder(detached);
        copiedBuilder.LineTo(9, 10);
        using var copied = copiedBuilder.Detach();
        Assert.Equal(2, detached.Geometry.Figures[0].Segments.Count);
        Assert.Equal(3, copied.Geometry.Figures[0].Segments.Count);
    }

    [Fact]
    public void PackedCopyTransformUsesBoundedStorageAndDoesNotMutateSource()
    {
        using var builder = new SKPathBuilder();
        builder.MoveTo(1f, 2f);
        for (var index = 0; index < 32; index++)
        {
            builder.LineTo(index + 3f, index + 4f);
            builder.QuadTo(index + 5f, index + 6f, index + 7f, index + 8f);
            builder.CubicTo(
                index + 9f,
                index + 10f,
                index + 11f,
                index + 12f,
                index + 13f,
                index + 14f);
        }
        using var source = builder.Detach();
        var sourceBounds = source.TightBounds;

        using (var warmup = new SKPath(source))
        {
            warmup.Transform(SKMatrix.CreateTranslation(5f, -7f));
            _ = warmup.TightBounds;
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        using var transformed = new SKPath(source);
        transformed.Transform(SKMatrix.CreateTranslation(5f, -7f));
        var transformedBounds = transformed.TightBounds;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 96);
        Assert.Equal(sourceBounds, source.TightBounds);
        Assert.Equal(sourceBounds.Left + 5f, transformedBounds.Left);
        Assert.Equal(sourceBounds.Top - 7f, transformedBounds.Top);
        Assert.Equal(sourceBounds.Right + 5f, transformedBounds.Right);
        Assert.Equal(sourceBounds.Bottom - 7f, transformedBounds.Bottom);
    }

    [Fact]
    public void RelativeCommandsAndConicsRetainCurrentPointAcrossClose()
    {
        using var builder = new SKPathBuilder();
        builder.RMoveTo(1, 2);
        builder.RLineTo(3, 4);
        builder.RQuadTo(5, 6, 7, 8);
        builder.RConicTo(1, 2, 3, 4, 0.5f);
        builder.RCubicTo(1, 2, 3, 4, 5, 6);
        builder.Close();
        builder.RLineTo(9, 10);
        using var path = builder.Detach();

        Assert.Equal(2, path.Geometry.Figures.Count);
        var first = path.Geometry.Figures[0];
        AssertPoint(first.StartPoint, 1, 2);
        Assert.True(first.IsClosed);
        Assert.Equal(19, first.Segments.Count);
        Assert.IsType<LineSegment>(first.Segments[0]);
        Assert.IsType<QuadraticBezierSegment>(first.Segments[1]);
        var conicStart = new Vector2(11, 14);
        var conicControl = new Vector2(12, 16);
        var conicEnd = new Vector2(14, 18);
        var spanStart = conicStart;
        var conicIndex = 0;
        Assert.All(first.Segments.Skip(2).Take(16), segment =>
        {
            var quadratic = Assert.IsAssignableFrom<QuadraticBezierSegment>(segment);
            Assert.True(float.IsFinite(quadratic.ControlPoint.X));
            Assert.True(float.IsFinite(quadratic.ControlPoint.Y));
            Assert.True(float.IsFinite(quadratic.Point.X));
            Assert.True(float.IsFinite(quadratic.Point.Y));
            for (var sample = 0; sample <= 4; sample++)
            {
                var localT = sample * 0.25f;
                var globalT = (conicIndex + localT) / 16f;
                var expected = EvaluateConic(conicStart, conicControl, conicEnd, 0.5f, globalT);
                var actual = EvaluateQuadratic(spanStart, quadratic.ControlPoint, quadratic.Point, localT);
                Assert.InRange(Vector2.Distance(actual, expected), 0f, 0.001f);
            }

            spanStart = quadratic.Point;
            conicIndex++;
        });
        AssertPoint(((QuadraticBezierSegment)first.Segments[17]).Point, 14, 18);
        var cubic = Assert.IsType<CubicBezierSegment>(first.Segments[18]);
        AssertPoint(cubic.Point, 19, 24);

        var second = path.Geometry.Figures[1];
        AssertPoint(second.StartPoint, 1, 2);
        Assert.False(second.IsClosed);
        AssertPoint(Assert.IsType<LineSegment>(Assert.Single(second.Segments)).Point, 10, 12);
    }

    [Fact]
    public void OvalTangentAndRelativeArcsStayAnalytic()
    {
        using var builder = new SKPathBuilder();
        builder.AddArc(new SKRect(0, 0, 20, 10), 30, 200);
        using var ovalArc = builder.Detach();
        var ovalFigure = Assert.Single(ovalArc.Geometry.Figures);
        AssertPoint(ovalFigure.StartPoint, 18.660254f, 7.5f, 0.0001f);
        Assert.Equal(2, ovalFigure.Segments.Count);
        Assert.All(ovalFigure.Segments, segment => Assert.IsType<ArcSegment>(segment));
        AssertPoint(((ArcSegment)ovalFigure.Segments[^1]).Point, 3.572124f, 1.169777f, 0.0002f);

        builder.MoveTo(0, 0);
        builder.ArcTo(10, 0, 10, 10, 4);
        using var tangentPath = builder.Snapshot();
        var tangent = tangentPath.Geometry.Figures[0];
        Assert.Equal(2, tangent.Segments.Count);
        AssertPoint(Assert.IsType<LineSegment>(tangent.Segments[0]).Point, 6, 0);
        var tangentArc = Assert.IsType<ArcSegment>(tangent.Segments[1]);
        AssertPoint(tangentArc.Point, 10, 4);
        AssertPoint(tangentArc.Size, 4, 4);
        Assert.Equal(SweepDirection.Clockwise, tangentArc.SweepDirection);

        builder.RArcTo(3, 4, 20, SKPathArcSize.Large, SKPathDirection.Clockwise, 5, 6);
        using var relativePath = builder.Snapshot();
        var relativeArc = Assert.IsType<ArcSegment>(relativePath.Geometry.Figures[0].Segments[^1]);
        AssertPoint(relativeArc.Point, 15, 10);
        AssertPoint(relativeArc.Size, 3, 4);
        Assert.True(relativeArc.IsLargeArc);

        builder.Reset();
        builder.MoveTo(1, 2);
        builder.LineTo(3, 4);
        builder.Close();
        builder.ArcTo(new SKRect(0, 0, 20, 10), 0, 90, forceMoveTo: false);
        using var postClosePath = builder.Snapshot();
        var postCloseArc = postClosePath.Geometry.Figures[1];
        AssertPoint(postCloseArc.StartPoint, 1, 2);
        AssertPoint(Assert.IsType<LineSegment>(postCloseArc.Segments[0]).Point, 20, 5);
        Assert.IsType<ArcSegment>(postCloseArc.Segments[1]);
    }

    [Fact]
    public void RectanglesHonorNativeDirectionAndStartIndex()
    {
        using var builder = new SKPathBuilder();
        builder.AddRect(new SKRect(1, 2, 5, 8), SKPathDirection.Clockwise, 1);
        using var clockwise = builder.Detach();
        var clockwiseFigure = Assert.Single(clockwise.Geometry.Figures);
        AssertPoint(clockwiseFigure.StartPoint, 5, 2);
        Assert.True(clockwiseFigure.IsClosed);
        AssertLineEndpoints(
            clockwiseFigure,
            new Vector2(5, 8),
            new Vector2(1, 8),
            new Vector2(1, 2));

        builder.AddRect(new SKRect(1, 2, 5, 8), SKPathDirection.CounterClockwise, 1);
        using var counterClockwise = builder.Detach();
        AssertLineEndpoints(
            counterClockwise.Geometry.Figures[0],
            new Vector2(1, 2),
            new Vector2(1, 8),
            new Vector2(5, 8));

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddRect(
            new SKRect(1, 2, 5, 8),
            SKPathDirection.Clockwise,
            4));
    }

    [Fact]
    public void RoundRectsOvalsCirclesAndPolygonsRetainPrimitiveTopology()
    {
        using var roundRect = new SKRoundRect(new SKRect(0, 0, 20, 10), 2, 3);
        using var builder = new SKPathBuilder();
        builder.AddRoundRect(roundRect, SKPathDirection.Clockwise);
        using var clockwise = builder.Detach();
        var clockwiseFigure = Assert.Single(clockwise.Geometry.Figures);
        AssertPoint(clockwiseFigure.StartPoint, 0, 7);
        Assert.True(clockwiseFigure.IsClosed);
        Assert.Equal(8, clockwiseFigure.Segments.Count);
        Assert.Equal(4, clockwiseFigure.Segments.Count(segment => segment is ArcSegment));

        builder.AddRoundRect(roundRect, SKPathDirection.CounterClockwise);
        using var counterClockwise = builder.Detach();
        AssertPoint(counterClockwise.Geometry.Figures[0].StartPoint, 0, 3);

        builder.AddRoundRect(roundRect, SKPathDirection.Clockwise, 2);
        using var indexed = builder.Detach();
        AssertPoint(indexed.Geometry.Figures[0].StartPoint, 20, 3);

        builder.AddOval(new SKRect(0, 0, 20, 10), SKPathDirection.Clockwise);
        using var oval = builder.Detach();
        var ovalFigure = oval.Geometry.Figures[0];
        AssertPoint(ovalFigure.StartPoint, 20, 5);
        Assert.True(ovalFigure.IsClosed);
        Assert.Equal(2, ovalFigure.Segments.Count);
        Assert.All(ovalFigure.Segments, segment => Assert.IsType<ArcSegment>(segment));

        builder.AddCircle(5, 6, 2, SKPathDirection.CounterClockwise);
        using var circle = builder.Detach();
        var circleBounds = circle.Bounds;
        Assert.InRange(circleBounds.Left, 3f - 0.0001f, 3f + 0.0001f);
        Assert.InRange(circleBounds.Top, 4f - 0.0001f, 4f + 0.0001f);
        Assert.InRange(circleBounds.Right, 7f - 0.0001f, 7f + 0.0001f);
        Assert.InRange(circleBounds.Bottom, 8f - 0.0001f, 8f + 0.0001f);

        builder.AddPoly([new SKPoint(1, 2), new SKPoint(3, 4)], close: false);
        builder.RLineTo(5, 6);
        using var poly = builder.Detach();
        Assert.False(poly.Geometry.Figures[0].IsClosed);
        AssertPoint(((LineSegment)poly.Geometry.Figures[0].Segments[^1]).Point, 8, 10);
    }

    [Fact]
    public void AddPathExtendMatrixAndReversePreserveContourSemantics()
    {
        using var source = SKPath.ParseSvgPathData(
            "M1 2 L3 4 Q5 6 7 8 C9 10 11 12 13 14 Z M20 21 L22 23");
        using var builder = new SKPathBuilder();
        builder.MoveTo(-1, -2);
        builder.AddPath(source, 10, 20, SKPathAddMode.Extend);
        using var extended = builder.Detach();
        Assert.Equal(2, extended.Geometry.Figures.Count);
        var extendedFirst = extended.Geometry.Figures[0];
        AssertPoint(extendedFirst.StartPoint, -1, -2);
        Assert.True(extendedFirst.IsClosed);
        AssertPoint(((LineSegment)extendedFirst.Segments[0]).Point, 11, 22);
        AssertPoint(((CubicBezierSegment)extendedFirst.Segments[^1]).Point, 23, 34);
        AssertPoint(extended.Geometry.Figures[1].StartPoint, 30, 41);

        builder.ReverseAddPath(source);
        using var reversed = builder.Detach();
        Assert.Equal(2, reversed.Geometry.Figures.Count);
        AssertPoint(reversed.Geometry.Figures[0].StartPoint, 22, 23);
        AssertPoint(((LineSegment)reversed.Geometry.Figures[0].Segments[0]).Point, 20, 21);
        var reversedClosed = reversed.Geometry.Figures[1];
        AssertPoint(reversedClosed.StartPoint, 13, 14);
        Assert.True(reversedClosed.IsClosed);
        var reversedCubic = Assert.IsType<CubicBezierSegment>(reversedClosed.Segments[0]);
        AssertPoint(reversedCubic.ControlPoint1, 11, 12);
        AssertPoint(reversedCubic.ControlPoint2, 9, 10);
        AssertPoint(reversedCubic.Point, 7, 8);

        var matrix = SKMatrix.CreateTranslation(100, 200);
        builder.AddPath(source, in matrix);
        using var transformed = builder.Detach();
        AssertPoint(transformed.Geometry.Figures[0].StartPoint, 101, 202);
        AssertPoint(transformed.Geometry.Figures[1].StartPoint, 120, 221);
    }

    private static void AssertLineEndpoints(PathFigure figure, params Vector2[] expected)
    {
        Assert.Equal(expected.Length, figure.Segments.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], Assert.IsType<LineSegment>(figure.Segments[index]).Point);
        }
    }

    private static void AssertPoint(Vector2 actual, float x, float y, float tolerance = 0.00001f)
    {
        Assert.InRange(actual.X, x - tolerance, x + tolerance);
        Assert.InRange(actual.Y, y - tolerance, y + tolerance);
    }

    private static Vector2 EvaluateConic(
        Vector2 start,
        Vector2 control,
        Vector2 end,
        float weight,
        float t)
    {
        var inverse = 1f - t;
        var startFactor = inverse * inverse;
        var controlFactor = 2f * weight * inverse * t;
        var endFactor = t * t;
        return (startFactor * start + controlFactor * control + endFactor * end) /
            (startFactor + controlFactor + endFactor);
    }

    private static Vector2 EvaluateQuadratic(
        Vector2 start,
        Vector2 control,
        Vector2 end,
        float t)
    {
        var inverse = 1f - t;
        return inverse * inverse * start + 2f * inverse * t * control + t * t * end;
    }
}
