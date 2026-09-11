using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class DashEndpointTraversalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenLeadingRetraceDoesNotAcquireSourceStartCap(bool curved)
    {
        var source = Create(curved, false, Vector2.Zero, new(1, 0), Vector2.Zero, new(0, 1));
        var pen = CreatePen(); pen.DashOffset = 1;
        Assert.True(Compositor.TryCreateDashedStrokePath(source, pen, out var dashed));
        var run = Assert.Single(dashed.Figures);
        Assert.Equal(Vector2.Zero, run.StartPoint);
        Assert.Null(run.StrokeStartLineCap);
        Assert.Equal(PenLineCap.Square, run.StrokeEndLineCap);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenTrailingRetraceDoesNotAcquireSourceEndCap(bool curved)
    {
        var source = Create(curved, false, Vector2.Zero, new(1, 0), new(2, 0), new(1, 0));
        Assert.True(Compositor.TryCreateDashedStrokePath(source, CreatePen(), out var dashed));
        var run = Assert.Single(dashed.Figures);
        Assert.Equal(PenLineCap.Triangle, run.StrokeStartLineCap);
        Assert.Null(run.StrokeEndLineCap);
        Assert.False(run.IsClosed);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public void HiddenTraversalAcrossClosedSeamDoesNotCloseVisibleRetrace(bool curved, int offset)
    {
        var source = Create(curved, true, Vector2.Zero, new(1, 0), Vector2.Zero, new(-1, 0), Vector2.Zero);
        var pen = CreatePen(); pen.DashArray = [2, 2]; pen.DashOffset = offset;
        Assert.True(Compositor.TryCreateDashedStrokePath(source, pen, out var dashed));
        var run = Assert.Single(dashed.Figures);
        Assert.Equal(Vector2.Zero, run.StartPoint);
        Assert.Equal(2, run.Segments.Count);
        Assert.False(run.IsClosed);
    }

    [Fact]
    public void DirectedTerminalCapDoesNotAlsoExtendEarlierCoincidentRun()
    {
        var source = Create(false, false, Vector2.Zero, new(1, 0), new(2, 0), new(1, 0));
        var pen = CreatePen(); pen.StartLineCap = PenLineCap.Flat;
        Assert.True(StrokeCoverageGeometry.TryPrepareLinearPath(source, pen,
            out var spine, out _, out var bounds, out var fill));
        Assert.NotNull(fill);
        Assert.Null(Assert.Single(spine.Figures).StrokeEndLineCap);
        // Terminal direction is -X. A wrong source-end cap on the earlier +X
        // run would expand the right bound to 1.5 instead of 1.
        Assert.Equal(new Rect(0, -0.5f, 1, 1), bounds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnstrokedEndpointRetraceCannotAcquireSourceCap(bool leading)
    {
        var source = leading
            ? Create(false, false, Vector2.Zero, new(1, 0), Vector2.Zero, new(0, 1))
            : Create(false, false, Vector2.Zero, new(1, 0), new(2, 0), new(1, 0));
        var segments = source.Figures[0].Segments;
        segments[leading ? 0 : 1].IsStroked = false;
        segments[leading ? 1 : 2].IsStroked = false;
        var pen = CreatePen(); pen.DashArray = [10, 2]; pen.DashOffset = 0.5;
        Assert.True(Compositor.TryCreateDashedStrokePath(source, pen, out var dashed));
        var run = Assert.Single(dashed.Figures);
        if (leading) Assert.Null(run.StrokeStartLineCap);
        else Assert.Null(run.StrokeEndLineCap);
    }

    private static Pen CreatePen() => new(new SolidColorBrush(Vector4.One), 1,
        startLineCap: PenLineCap.Triangle, endLineCap: PenLineCap.Square,
        dashCap: PenLineCap.Flat, dashArray: [1, 2]);

    private static PathGeometry Create(bool curved, bool closed, params Vector2[] points)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(points[0], isClosed: closed);
        for (int i = 1; i < points.Length; i++)
            figure.Segments.Add(curved
                ? new QuadraticBezierSegment((points[i - 1] + points[i]) * 0.5f, points[i])
                : new LineSegment(points[i]));
        path.Figures.Add(figure);
        return path;
    }
}
