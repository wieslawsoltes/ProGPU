using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class DashedLinearStrokeCoverageTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DashedGapRunsMatchNativeCachedMaterialBounds(bool closed, bool gap)
    {
        var source = Rectangle(closed, gap);
        foreach (var join in new[] { PenLineJoin.Miter, PenLineJoin.Bevel, PenLineJoin.Round })
        {
            var pen = new Pen(new SolidColorBrush(Vector4.One), 4, lineJoin: join,
                startLineCap: PenLineCap.Round, endLineCap: PenLineCap.Triangle,
                dashCap: PenLineCap.Flat, dashArray: [2, 1], dashOffset: 0.25);
            Assert.True(StrokeCoverageGeometry.TryPrepareLinearPath(source, pen, out var path, out var coveragePen, out var bounds));
            Assert.Equal(new Rect(8, 18, gap ? 22 : 24, 24), bounds);
            Assert.False(coveragePen.HasDashPattern);
            Assert.Same(pen.Brush, coveragePen.Brush);
            Assert.NotSame(source, path);
            Assert.All(path.Figures, figure => Assert.False(figure.IsFilled));
            Assert.True(source.Figures[0].IsFilled);
            Assert.True(pen.HasDashPattern);
            Assert.True(StrokeCoverageGeometry.TryMeasurePreparedLinearStrokeOutline(path, coveragePen, out var measured));
            Assert.Equal(bounds, measured);
        }
    }

    [Theory]
    [InlineData(3f, 4f)]
    [InlineData(-3f, 4f)]
    [InlineData(3f, -4f)]
    [InlineData(-3f, -4f)]
    [InlineData(0.125f, 11.5f)]
    public void EmittedRoundCapsStayWithinCubicErrorOfCapsuleOracle(float dx, float dy)
    {
        var source = Line(new(10, 20), new(10 + dx, 20 + dy));
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4,
            startLineCap: PenLineCap.Round, endLineCap: PenLineCap.Round);
        Assert.True(StrokeCoverageGeometry.TryMeasurePreparedLinearStrokeOutline(source, pen, out var bounds));
        // Independent ideal-circle oracle; the emitted quarter cubics (not the
        // ideal circle) are authoritative. Their radius-2 error is below .001.
        Assert.InRange(bounds.X, MathF.Min(10, 10 + dx) - 2.001f, MathF.Min(10, 10 + dx) - 1.999f);
        Assert.InRange(bounds.Y, MathF.Min(20, 20 + dy) - 2.001f, MathF.Min(20, 20 + dy) - 1.999f);
        Assert.InRange(bounds.Right, MathF.Max(10, 10 + dx) + 1.999f, MathF.Max(10, 10 + dx) + 2.001f);
        Assert.InRange(bounds.Bottom, MathF.Max(20, 20 + dy) + 1.999f, MathF.Max(20, 20 + dy) + 2.001f);
    }

    [Fact]
    public void UnsupportedTerminalCapsTinyEdgesAndDashDensityPublishNoOutputs()
    {
        var source = Line(Vector2.Zero, new(4, 0));
        var pen = new Pen(new SolidColorBrush(Vector4.One), 1,
            dashCap: PenLineCap.Round, dashArray: [2, 2]);
        Reject(); // Visible point at distance four needs an oriented terminal cap.
        pen.DashCap = PenLineCap.Flat;
        Assert.True(StrokeCoverageGeometry.TryPrepareLinearPath(source, pen, out _, out _, out var bounds));
        Assert.Equal(new Rect(0, -0.5f, 2, 1), bounds);
        source.Figures[0].Segments[0] = new LineSegment(new(0.00001f, 0)); Reject();
        source.Figures[0].Segments[0] = new LineSegment(new(1_000_000, 0));
        pen.DashArray = [0.001, 0.001]; Reject();
        pen.DashArray = [0, 1]; Reject();
        void Reject()
        {
            Assert.False(StrokeCoverageGeometry.TryPrepareLinearPath(source, pen, out var path, out var resultPen, out var failed));
            Assert.Null(path); Assert.Null(resultPen); Assert.Equal(default, failed);
        }
    }

    [Fact]
    public void OutlineMeasurementRejectsUnpreparedInputTransactionally()
    {
        var source = Line(Vector2.Zero, new(10, 0));
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4);
        source.Figures[0].Segments.Add(new LineSegment(new(10, 0)));
        Reject();
        source.Figures[0].Segments.RemoveAt(1);
        source.Figures[0].IsClosed = true; Reject(); // Missing explicit closing edge.
        source.Figures[0].IsClosed = false;
        source.Figures[0].Segments[0].IsStroked = false; Reject();
        source.Figures[0].Segments[0] = new LineSegment(new(float.NaN, 0)); Reject();
        void Reject()
        {
            Assert.False(StrokeCoverageGeometry.TryMeasurePreparedLinearStrokeOutline(source, pen, out var bounds));
            Assert.Equal(default, bounds);
        }
    }

    [Fact]
    public void HiddenRetraceDoesNotJoinDistinctDashIntervalsAtCoincidentPositions()
    {
        var source = Line(Vector2.Zero, new(1, 0));
        source.Figures[0].Segments.Add(new LineSegment(new(2, 0)));
        source.Figures[0].Segments.Add(new LineSegment(new(1, 0)));
        source.Figures[0].Segments.Add(new LineSegment(new(1, 1), isSmoothJoin: true));
        var pen = new Pen(new SolidColorBrush(Vector4.One), 1, dashArray: [1, 2]);
        Assert.True(StrokeCoverageGeometry.TryPrepareLinearPath(source, pen, out var path, out _, out _));
        Assert.Equal(2, path.Figures.Count);
        Assert.All(path.Figures, figure => Assert.Single(figure.Segments));
        Assert.False(path.Figures[1].Segments[0].IsSmoothJoin);
    }

    private static PathGeometry Rectangle(bool closed, bool gap)
    {
        var source = Line(new(10, 20), new(30, 20));
        var figure = source.Figures[0];
        figure.IsClosed = closed; figure.IsFilled = true;
        figure.Segments.Add(new LineSegment(new(30, 40), isStroked: !gap));
        figure.Segments.Add(new LineSegment(new(10, 40)));
        return source;
    }

    private static PathGeometry Line(Vector2 first, Vector2 last)
    {
        var source = new PathGeometry();
        var figure = new PathFigure(first) { IsFilled = false };
        figure.Segments.Add(new LineSegment(last)); source.Figures.Add(figure);
        return source;
    }
}
