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
