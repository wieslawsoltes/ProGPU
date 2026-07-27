using System.Numerics;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public class PathAtlasFillCompilerTests
{
    [Fact]
    public void PathAtlasCompilerImplicitlyClosesFilledOpenFigures()
    {
        var path = CreateOpenRectangle();

        var (records, segments) = PathAtlas.CompileFillPath(path, out _, out _, out _, out _);

        AssertImplicitClosure(records, segments);
    }

    [Fact]
    public void GenericCompilerPreservesOpenFiguresForStrokeHitTesting()
    {
        var path = CreateOpenRectangle();

        var (records, segments) = PathAtlas.CompilePath(path, out _, out _, out _, out _);

        var record = Assert.Single(records);
        Assert.Equal(3u, record.SegmentCount);
        Assert.Equal(3, segments.Length);
    }

    [Fact]
    public void PathAtlasCompilerSkipsUnfilledFigures()
    {
        var path = CreatePathWithUnfilledFigure();

        var (records, segments) = PathAtlas.CompileFillPath(
            path,
            out float minX,
            out float minY,
            out float maxX,
            out float maxY);

        AssertOnlyFilledFigure(records, segments, minX, minY, maxX, maxY);
    }

    [Fact]
    public void TransitionMaskedIconPreservesAllContoursAndImplicitFillClosures()
    {
        PathGeometry path = PathGeometry.Parse(
            "M2,5.27L3.28,4L20,20.72L18.73,22L12.73,16H7V19L3,15L7,11V14H9.73L5,9.27V14H3V5.27" +
            "M21,9L17,5V8H14V10H17V13L21,9" +
            "M17,17V14H15V17H17Z");

        var (records, segments) = PathAtlas.CompileFillPath(
            path,
            out float minX,
            out float minY,
            out float maxX,
            out float maxY);

        Assert.Equal(3, path.Figures.Count);
        Assert.False(path.Figures[0].IsClosed);
        Assert.False(path.Figures[1].IsClosed);
        Assert.True(path.Figures[2].IsClosed);
        Assert.Equal(26, segments.Length);
        Assert.Equal(26u, Assert.Single(records).SegmentCount);
        Assert.Equal(new Vector2(3f, 5.27f), segments[14].P0);
        Assert.Equal(new Vector2(2f, 5.27f), segments[14].P1);
        Assert.Equal(0u, segments[14].SegmentType);
        Assert.Equal(new Vector2(21f, 9f), segments[21].P1);
        Assert.Equal(new Vector2(17f, 17f), segments[25].P1);
        Assert.Equal(2f, minX);
        Assert.Equal(4f, minY);
        Assert.Equal(21f, maxX);
        Assert.Equal(22f, maxY);
    }

    [Fact]
    public void ContainedRoundedRectangleDifferencePreservesExactEvenOddContours()
    {
        PathGeometry outer = CreateTopRoundedRectangle(
            0f,
            0f,
            120f,
            24f,
            new Vector2(5f, 6f));
        PathGeometry inner = CreateTopRoundedRectangle(
            1f,
            1f,
            119f,
            24f,
            new Vector2(4f, 5f));

        PathGeometry result = PathOpGeometrySolver.Combine(outer, inner, op: 0);

        Assert.Equal(FillRule.EvenOdd, result.FillRule);
        Assert.Equal(2, result.Figures.Count);
        Assert.Equal(outer.Figures[0].Segments.Count, result.Figures[0].Segments.Count);
        Assert.Equal(inner.Figures[0].Segments.Count, result.Figures[1].Segments.Count);
        Assert.IsType<ArcSegment>(result.Figures[0].Segments[1]);
        Assert.IsType<ArcSegment>(result.Figures[1].Segments[1]);
    }

    private static PathGeometry CreateOpenRectangle()
    {
        var path = new PathGeometry { FillRule = FillRule.EvenOdd };
        var figure = new PathFigure(new Vector2(1f, 2f));
        figure.Segments.Add(new LineSegment(new Vector2(5f, 2f)));
        figure.Segments.Add(new LineSegment(new Vector2(5f, 6f)));
        figure.Segments.Add(new LineSegment(new Vector2(1f, 6f)));
        path.Figures.Add(figure);
        return path;
    }

    private static PathGeometry CreatePathWithUnfilledFigure()
    {
        var path = CreateOpenRectangle();
        var unfilledFigure = new PathFigure(new Vector2(100f, 100f))
        {
            IsFilled = false
        };
        unfilledFigure.Segments.Add(new LineSegment(new Vector2(120f, 100f)));
        unfilledFigure.Segments.Add(new LineSegment(new Vector2(100f, 120f)));
        path.Figures.Add(unfilledFigure);
        return path;
    }

    private static PathGeometry CreateTopRoundedRectangle(
        float left,
        float top,
        float right,
        float bottom,
        Vector2 radius)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(left + radius.X, top), isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(right - radius.X, top)));
        figure.Segments.Add(new ArcSegment(
            new Vector2(right, top + radius.Y),
            radius,
            rotationAngle: 0f,
            isLargeArc: false,
            SweepDirection.Clockwise));
        figure.Segments.Add(new LineSegment(new Vector2(right, bottom)));
        figure.Segments.Add(new LineSegment(new Vector2(left, bottom)));
        figure.Segments.Add(new LineSegment(new Vector2(left, top + radius.Y)));
        figure.Segments.Add(new ArcSegment(
            new Vector2(left + radius.X, top),
            radius,
            rotationAngle: 0f,
            isLargeArc: false,
            SweepDirection.Clockwise));
        path.Figures.Add(figure);
        return path;
    }

    private static void AssertImplicitClosure(GpuPathRecord[] records, GpuPathSegment[] segments)
    {
        var record = Assert.Single(records);
        Assert.Equal(4u, record.SegmentCount);
        Assert.Equal(4, segments.Length);
        Assert.Equal(new Vector2(1f, 6f), segments[3].P0);
        Assert.Equal(new Vector2(1f, 2f), segments[3].P1);
        Assert.Equal(0u, segments[3].SegmentType);
    }

    private static void AssertOnlyFilledFigure(
        GpuPathRecord[] records,
        GpuPathSegment[] segments,
        float minX,
        float minY,
        float maxX,
        float maxY)
    {
        var record = Assert.Single(records);
        Assert.Equal(4u, record.SegmentCount);
        Assert.Equal(4, segments.Length);
        Assert.Equal(1f, minX);
        Assert.Equal(2f, minY);
        Assert.Equal(5f, maxX);
        Assert.Equal(6f, maxY);
    }
}
