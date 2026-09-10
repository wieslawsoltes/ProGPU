using System.Numerics;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

public sealed class PathWarpGeometryTests
{
    [Fact]
    public void PerspectiveWarpMapsFourCornersAndPreservesFigureMetadata()
    {
        var source = new PathGeometry { FillRule = FillRule.EvenOdd };
        var figure = new PathFigure(new Vector2(0f, 0f), isClosed: true)
        {
            IsFilled = false,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Square,
        };
        figure.Segments.Add(new LineSegment(new Vector2(10f, 0f)));
        figure.Segments.Add(new LineSegment(new Vector2(10f, 10f)));
        figure.Segments.Add(new LineSegment(new Vector2(0f, 10f)));
        source.Figures.Add(figure);
        Vector2[] destination =
        [
            new Vector2(2f, 3f),
            new Vector2(22f, 5f),
            new Vector2(4f, 23f),
            new Vector2(18f, 19f),
        ];

        Assert.True(PathWarpGeometry.TryCreateWarpedPath(
            source,
            destination,
            Vector2.Zero,
            new Vector2(10f, 10f),
            PathWarpMode.Perspective,
            0.25f,
            out PathGeometry warped));

        PathFigure result = Assert.Single(warped.Figures);
        Assert.Equal(FillRule.EvenOdd, warped.FillRule);
        Assert.False(result.IsFilled);
        Assert.True(result.IsClosed);
        Assert.Equal(PenLineCap.Round, result.StrokeStartLineCap);
        Assert.Equal(PenLineCap.Square, result.StrokeEndLineCap);
        Assert.Equal(destination[0], result.StartPoint);
        Assert.Equal(destination[1], Assert.IsType<LineSegment>(result.Segments[0]).Point);
        Assert.Equal(destination[3], Assert.IsType<LineSegment>(result.Segments[1]).Point);
        Assert.Equal(destination[2], Assert.IsType<LineSegment>(result.Segments[2]).Point);
    }

    [Fact]
    public void ThreePointWarpDerivesParallelogramCorner()
    {
        PathGeometry source = CreateLine(new Vector2(0f, 0f), new Vector2(10f, 10f));
        Vector2[] destination = [new Vector2(5f, 7f), new Vector2(25f, 7f), new Vector2(5f, 27f)];

        Assert.True(PathWarpGeometry.TryCreateWarpedPath(
            source,
            destination,
            Vector2.Zero,
            new Vector2(10f, 10f),
            PathWarpMode.Perspective,
            0.25f,
            out PathGeometry warped));

        Assert.Equal(new Vector2(25f, 27f), Assert.IsType<LineSegment>(warped.Figures[0].Segments[0]).Point);
    }

    [Fact]
    public void BilinearWarpSubdividesDiagonalWithinFlatness()
    {
        PathGeometry source = CreateLine(new Vector2(0f, 0f), new Vector2(10f, 10f));
        Vector2[] destination =
        [
            new Vector2(0f, 0f),
            new Vector2(10f, 0f),
            new Vector2(0f, 10f),
            new Vector2(20f, 20f),
        ];

        Assert.True(PathWarpGeometry.TryCreateWarpedPath(
            source,
            destination,
            Vector2.Zero,
            new Vector2(10f, 10f),
            PathWarpMode.Bilinear,
            0.1f,
            out PathGeometry warped));

        PathFigure figure = Assert.Single(warped.Figures);
        Assert.True(figure.Segments.Count > 1);
        Assert.Contains(figure.Segments, segment =>
            segment is LineSegment line && Vector2.DistanceSquared(line.Point, new Vector2(7.5f, 7.5f)) < 0.000001f);
        Assert.Equal(new Vector2(20f, 20f), Assert.IsType<LineSegment>(figure.Segments[^1]).Point);
    }

    [Fact]
    public void WarpRejectsUnsupportedGeometryAndDegenerateContracts()
    {
        var curved = new PathGeometry();
        var figure = new PathFigure(Vector2.Zero);
        figure.Segments.Add(new CubicBezierSegment(Vector2.One, new Vector2(2f), new Vector2(3f)));
        curved.Figures.Add(figure);
        Vector2[] destination = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY];

        Assert.False(PathWarpGeometry.TryCreateWarpedPath(
            curved,
            destination,
            Vector2.Zero,
            Vector2.One,
            PathWarpMode.Perspective,
            0.25f,
            out _));
        Assert.False(PathWarpGeometry.TryCreateWarpedPath(
            CreateLine(Vector2.Zero, Vector2.One),
            destination.AsSpan(0, 2),
            Vector2.Zero,
            Vector2.One,
            PathWarpMode.Perspective,
            0.25f,
            out _));
        Assert.False(PathWarpGeometry.TryCreateWarpedPath(
            CreateLine(Vector2.Zero, Vector2.One),
            destination,
            Vector2.Zero,
            new Vector2(0f, 1f),
            PathWarpMode.Perspective,
            0.25f,
            out _));
    }

    private static PathGeometry CreateLine(Vector2 start, Vector2 end)
    {
        var source = new PathGeometry();
        var figure = new PathFigure(start);
        figure.Segments.Add(new LineSegment(end));
        source.Figures.Add(figure);
        return source;
    }
}
