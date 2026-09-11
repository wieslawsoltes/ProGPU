using System.Numerics;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public class PathStrokeQueryCompilationTests
{
    [Fact]
    public void ArcEncodingSharesCanonicalParametersAndRetainsDegenerateGap()
    {
        var path = new PathGeometry();
        var figure = new PathFigure { StartPoint = new(2, 0) };
        figure.Segments.Add(new ArcSegment(new(0, 2), new(2, 2), 0, false, SweepDirection.Clockwise));
        path.Figures.Add(figure);
        var (_, expected) = PathAtlas.CompilePath(path, out _, out _, out _, out _);
        var (_, actual, _) = PathAtlas.CompileStrokeQuery(path);
        Assert.Equal(expected[0].SegmentType, actual[0].SegmentType);
        Assert.Equal(expected[0].P2, actual[0].P2); Assert.Equal(expected[0].P3, actual[0].P3);
        Assert.Equal(expected[0].Pad0, actual[0].Pad0); Assert.Equal(expected[0].Pad1, actual[0].Pad1);
        Assert.Equal(expected[0].Pad2, actual[0].Pad2);
        figure.Segments.Add(new ArcSegment(new(0, 2), Vector2.Zero, 0, false, SweepDirection.Clockwise, true, false));
        var (figures, withGap, flags) = PathAtlas.CompileStrokeQuery(path);
        Assert.Equal(2, figures[0].SegmentCount);
        Assert.Equal(0U, withGap[1].SegmentType);
        Assert.Equal(withGap[1].P0, withGap[1].P1);
        Assert.Equal(2, flags[1]);
    }

    [Fact]
    public void CompleteFiguresKeepEmptyHollowGapAndIncomingJoinState()
    {
        var path = new PathGeometry();
        path.Figures.Add(new PathFigure { StartPoint = new(10, 20), IsFilled = false });
        var figure = new PathFigure { StartPoint = new(1, 2), IsFilled = false, IsClosed = true };
        figure.Segments.Add(new LineSegment(new(1, 2), true, false));
        figure.Segments.Add(new QuadraticBezierSegment(new(2, 3), new(3, 4), false, true));
        figure.Segments.Add(new CubicBezierSegment(new(3, 5), new(4, 6), new(5, 6), true, true));
        path.Figures.Add(figure);
        var (figures, segments, flags) = PathAtlas.CompileStrokeQuery(path);
        Assert.Equal(2, figures.Length);
        Assert.Equal(new PathQueryFigure(new(10, 20), 0, 0, false, false), figures[0]);
        Assert.Equal(new PathQueryFigure(new(1, 2), 0, 4, true, false), figures[1]);
        Assert.Equal(new byte[] { 2, 1, 3, 1 }, flags);
        Assert.Equal(new Vector2(1, 2), segments[0].P0);
        Assert.Equal(segments[0].P0, segments[0].P1);
        Assert.Equal(segments[2].P3, segments[3].P0);
        Assert.Equal(figures[1].Start, segments[3].P1);
    }

    [Fact]
    public void OrdinaryCanonicalSegmentsRemainIdenticalToRendererCompilation()
    {
        var path = new PathGeometry();
        var figure = new PathFigure { StartPoint = Vector2.Zero, IsClosed = true };
        figure.Segments.Add(new LineSegment(new(2, 0)));
        figure.Segments.Add(new QuadraticBezierSegment(new(4, 0), new(4, 2)));
        figure.Segments.Add(new CubicBezierSegment(new(4, 4), new(0, 4), new(0, 2)));
        path.Figures.Add(figure);
        var (_, expected) = PathAtlas.CompilePath(path, out _, out _, out _, out _);
        var (_, actual, flags) = PathAtlas.CompileStrokeQuery(path);
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].SegmentType, actual[i].SegmentType);
            Assert.Equal(expected[i].P0, actual[i].P0); Assert.Equal(expected[i].P1, actual[i].P1);
            Assert.Equal(expected[i].P2, actual[i].P2); Assert.Equal(expected[i].P3, actual[i].P3);
            Assert.Equal(1, flags[i]);
        }
    }
}
