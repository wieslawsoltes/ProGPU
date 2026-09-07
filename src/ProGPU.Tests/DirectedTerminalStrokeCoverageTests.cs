using System;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class DirectedTerminalStrokeCoverageTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    public void TerminalCapsProduceOnePositiveWindingCoverage(int x, int y)
    {
        var direction = new Vector2(x, y);
        var normal = new Vector2(-y, x);
        foreach (var dashCap in new[] { PenLineCap.Flat, PenLineCap.Square, PenLineCap.Round, PenLineCap.Triangle })
        foreach (var endCap in new[] { PenLineCap.Flat, PenLineCap.Square, PenLineCap.Round, PenLineCap.Triangle })
        {
            var pen = new Pen(new SolidColorBrush(Vector4.One), 1,
                startLineCap: PenLineCap.Flat, endLineCap: endCap, dashCap: dashCap, dashArray: [2, 2]);
            Assert.True(StrokeCoverageGeometry.TryPrepareLine(Vector2.Zero, direction * 4, pen,
                out var spine, out var coveragePen, out var bounds, out var outline));
            bool hasTerminal = dashCap != PenLineCap.Flat || endCap != PenLineCap.Flat;
            Assert.Equal(hasTerminal, outline != null);
            Assert.False(coveragePen.HasDashPattern);
            float length = endCap != PenLineCap.Flat ? 4.5f : dashCap != PenLineCap.Flat ? 4 : 2;
            var minimum = Vector2.Min(-normal * 0.5f, direction * length - normal * 0.5f);
            minimum = Vector2.Min(minimum, normal * 0.5f);
            minimum = Vector2.Min(minimum, direction * length + normal * 0.5f);
            var maximum = Vector2.Max(-normal * 0.5f, direction * length - normal * 0.5f);
            maximum = Vector2.Max(maximum, normal * 0.5f);
            maximum = Vector2.Max(maximum, direction * length + normal * 0.5f);
            Assert.Equal(new Rect(minimum.X, minimum.Y, maximum.X - minimum.X, maximum.Y - minimum.Y), bounds);
            if (outline == null) continue;
            Assert.Equal(FillRule.Nonzero, outline.FillRule);
            Assert.NotSame(spine, outline);
            Assert.All(outline.Figures, AssertPositive);
            // All source state is preserved and the legacy API cannot silently
            // drop the complete filled-coverage payload.
            Assert.True(pen.HasDashPattern);
            Assert.False(StrokeCoverageGeometry.TryPrepareLine(Vector2.Zero, direction * 4, pen, out _, out _, out _));
        }
    }

    [Theory]
    [InlineData(PenLineJoin.Miter)]
    [InlineData(PenLineJoin.Round)]
    [InlineData(PenLineJoin.Bevel)]
    public void LinearPathTerminalCoverageIncludesInteriorJoins(PenLineJoin join)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(Vector2.Zero);
        figure.Segments.Add(new LineSegment(new(1, 0)));
        figure.Segments.Add(new LineSegment(new(1, 3)));
        path.Figures.Add(figure);
        var pen = new Pen(new SolidColorBrush(Vector4.One), 1, lineJoin: join,
            dashCap: PenLineCap.Round, endLineCap: PenLineCap.Triangle, dashArray: [2, 2]);
        Assert.True(StrokeCoverageGeometry.TryPrepareLinearPath(path, pen, out _, out _, out var bounds, out var outline));
        Assert.NotNull(outline);
        Assert.Equal(new Rect(0, -0.5f, 1.5f, 4), bounds);
        Assert.All(outline.Figures, AssertPositive);
        Assert.True(figure.IsFilled);
    }

    [Fact]
    public void ZeroWidthDashedLineAndPathHaveNoCoverage()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(Vector2.Zero);
        figure.Segments.Add(new LineSegment(new(4, 0))); path.Figures.Add(figure);
        var pen = new Pen(new SolidColorBrush(Vector4.One), 0, dashCap: PenLineCap.Round, dashArray: [2, 2]);
        Assert.True(StrokeCoverageGeometry.TryPrepareLine(Vector2.Zero, new(4, 0), pen,
            out _, out _, out var lineBounds, out var lineFill));
        Assert.True(StrokeCoverageGeometry.TryPrepareLinearPath(path, pen,
            out _, out _, out var pathBounds, out var pathFill));
        Assert.Equal(default, lineBounds); Assert.Equal(default, pathBounds);
        Assert.Null(lineFill); Assert.Null(pathFill);
    }

    private static void AssertPositive(PathFigure figure)
    {
        Assert.True(figure.IsClosed);
        Assert.True(figure.IsFilled);
        double area = 0;
        var previous = figure.StartPoint;
        foreach (var segment in figure.Segments)
        {
            var next = segment is LineSegment line ? line.Point : Assert.IsType<CubicBezierSegment>(segment).Point;
            double ax = previous.X - figure.StartPoint.X, ay = previous.Y - figure.StartPoint.Y;
            double bx = next.X - figure.StartPoint.X, by = next.Y - figure.StartPoint.Y;
            area += ax * by - ay * bx;
            previous = next;
        }
        Assert.True(double.IsFinite(area) && area > 0);
    }
}
