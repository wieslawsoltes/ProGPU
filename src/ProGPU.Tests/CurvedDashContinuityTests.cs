using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class CurvedDashContinuityTests
{
    [Fact]
    public void HiddenReturningCurveDoesNotJoinDistinctVisibleRuns()
    {
        var path = Path(
            new QuadraticBezierSegment(new(0.5f, 0), new(1, 0)),
            new QuadraticBezierSegment(new(3, 0), new(1, 0)),
            new QuadraticBezierSegment(new(1, 0.5f), new(1, 1), isSmoothJoin: true));
        Assert.True(Compositor.TryCreateDashedStrokePath(path, Pen(1, 2), out var dashed));
        Assert.Equal(2, dashed.Figures.Count);
        Assert.All(dashed.Figures, figure =>
        {
            var segment = Assert.Single(figure.Segments);
            Assert.IsType<QuadraticBezierSegment>(segment);
            Assert.False(segment.IsSmoothJoin);
        });
    }

    [Fact]
    public void SeparateIntervalsOnOneReturningCurveRemainSeparateFigures()
    {
        var path = Path(new QuadraticBezierSegment(new(4, 0), Vector2.Zero, isSmoothJoin: true));
        Assert.True(Compositor.TryCreateDashedStrokePath(path, Pen(1, 2), out var dashed));
        Assert.Equal(2, dashed.Figures.Count);
        Assert.All(dashed.Figures, figure => Assert.False(Assert.Single(figure.Segments).IsSmoothJoin));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonconstantReturningBezierRetainsItsAnalyticSegment(bool cubic)
    {
        PathSegment source = cubic
            ? new CubicBezierSegment(new(2, 3), new(-2, 3), Vector2.Zero)
            : new QuadraticBezierSegment(new(4, 0), Vector2.Zero);
        var path = Path(source);
        Assert.True(Compositor.TryCreateDashedStrokePath(path, Pen(20, 1), out var dashed));
        var figure = Assert.Single(dashed.Figures);
        var segment = Assert.Single(figure.Segments);
        Assert.Equal(source.GetType(), segment.GetType());
        Assert.NotSame(source, segment);
        Assert.False(figure.IsClosed); // Endpoint equality does not close the source.
        Assert.False(segment.IsSmoothJoin);
        Assert.Equal(Vector2.Zero, figure.StartPoint);
        if (cubic)
            Assert.Equal(Vector2.Zero, Assert.IsType<CubicBezierSegment>(segment).Point);
        else
            Assert.Equal(Vector2.Zero, Assert.IsType<QuadraticBezierSegment>(segment).Point);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyContinuousSourceCurveJoinInheritsSmoothFlag(bool smooth)
    {
        var path = Path(
            new QuadraticBezierSegment(new(0.5f, 0), new(1, 0)),
            new QuadraticBezierSegment(new(1, 0), new(1, 0)), // Exact constant.
            new ArcSegment(new(1, 0), Vector2.One, 0, false, SweepDirection.Clockwise), // Empty endpoint arc.
            new CubicBezierSegment(new(1, 0.25f), new(1, 0.75f), new(1, 1), isSmoothJoin: smooth));
        Assert.True(Compositor.TryCreateDashedStrokePath(path, Pen(10, 1), out var dashed));
        var run = Assert.Single(dashed.Figures);
        Assert.Equal(2, run.Segments.Count);
        Assert.Equal(smooth, run.Segments[1].IsSmoothJoin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonfiniteOrOverflowedCurveMetricRejectsWholePreparation(bool overflow)
    {
        var control = overflow ? new Vector2(float.MaxValue, float.MaxValue) : new Vector2(float.NaN, 0);
        var path = Path(new LineSegment(new(1, 0)), new QuadraticBezierSegment(control, new(2, 0)));
        Assert.False(Compositor.TryCreateDashedStrokePath(path, Pen(10, 1), out _));
    }

    [Fact]
    public void InvalidArcIsNotSilentlyOmittedAfterAValidSegment()
    {
        var path = Path(new LineSegment(new(1, 0)),
            new ArcSegment(new(2, 0), new(float.NaN, 1), 0, false, SweepDirection.Clockwise));
        Assert.False(Compositor.TryCreateDashedStrokePath(path, Pen(10, 1), out _));
    }

    private static PathGeometry Path(params PathSegment[] segments)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(Vector2.Zero);
        figure.Segments.AddRange(segments);
        path.Figures.Add(figure);
        return path;
    }

    private static Pen Pen(double visible, double hidden) =>
        new(new SolidColorBrush(Vector4.One), 1, dashArray: [visible, hidden]);
}
