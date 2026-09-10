using System;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class LinearPathStrokeCoverageTests
{
    [Theory]
    [InlineData(3f, 4f)]
    [InlineData(-3f, 4f)]
    [InlineData(3f, -4f)]
    [InlineData(-3f, -4f)]
    [InlineData(0.125f, 11.5f)]
    public void IntrinsicLineSupportMatchesScalarOracle(float dx, float dy)
    {
        var source = new PathGeometry();
        var start = new Vector2(10, 20); var end = start + new Vector2(dx, dy);
        var figure = new PathFigure(start);
        figure.Segments.Add(new LineSegment(end)); source.Figures.Add(figure);
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4);
        Assert.True(StrokeCoverageGeometry.TryPrepareLinearPath(source, pen, out _, out _, out var bounds));
        double length = double.Hypot((double)end.X - start.X, (double)end.Y - start.Y);
        double nx = -((double)end.Y - start.Y) / length * 2, ny = ((double)end.X - start.X) / length * 2;
        // Explicit scalar reference, including native narrowing before reduction.
        float left = float.PositiveInfinity, top = float.PositiveInfinity;
        float right = float.NegativeInfinity, bottom = float.NegativeInfinity;
        foreach (var point in new[] { start, end })
            foreach (int sign in new[] { -1, 1 })
            {
                float x = (float)(point.X + sign * nx), y = (float)(point.Y + sign * ny);
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
        Assert.Equal(new Rect(left, top, right - left, bottom - top), bounds);
    }

    [Theory]
    [InlineData(false, false, PenLineJoin.Miter)]
    [InlineData(false, true, PenLineJoin.Miter)]
    [InlineData(true, false, PenLineJoin.Miter)]
    [InlineData(true, true, PenLineJoin.Miter)]
    [InlineData(true, false, PenLineJoin.Bevel)]
    [InlineData(true, true, PenLineJoin.Bevel)]
    [InlineData(true, false, PenLineJoin.Round)]
    [InlineData(true, true, PenLineJoin.Round)]
    public void LinearRunBoundsAndGapCapsMatchNativeMil(bool closed, bool gap, PenLineJoin join)
    {
        var source = RectangleSpine(closed, gap);
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4, join,
            startLineCap: PenLineCap.Round, endLineCap: PenLineCap.Triangle, dashCap: PenLineCap.Flat);
        Assert.True(StrokeCoverageGeometry.TryPrepareLinearPath(source, pen, out var path, out var coverage, out var bounds));
        // Independent strip/cap oracle for the three-sided spine; the closed
        // form adds the left edge, while the right gap removes x=32 support.
        Assert.Equal(new Rect(8, 18, gap ? 22 : 24, 24), bounds);
        Assert.Same(pen, coverage);
        Assert.NotSame(source, path);
        if (gap)
        {
            Assert.Equal(closed ? 1 : 2, path.Figures.Count);
            Assert.All(path.Figures, figure => Assert.False(figure.IsClosed));
            Assert.Equal(PenLineCap.Flat, path.Figures[0].StrokeEndLineCap);
            if (closed) Assert.Equal(new Vector2(30, 40), path.Figures[0].StartPoint);
        }
        else Assert.Equal(closed, Assert.Single(path.Figures).IsClosed);
        source.Figures.Clear();
        Assert.NotEmpty(path.Figures);
    }

    [Fact]
    public void ConstantsAreCompactedButDoNotRemoveFollowingSmoothJoin()
    {
        var source = new PathGeometry();
        var figure = new PathFigure(new(0, 0));
        figure.Segments.Add(new LineSegment(new(10, 0)));
        figure.Segments.Add(new LineSegment(new(10, 0)));
        figure.Segments.Add(new LineSegment(new(0, 1), isSmoothJoin: true));
        source.Figures.Add(figure);
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4, miterLimit: 100);
        Assert.True(StrokeCoverageGeometry.TryPrepareLinearPath(source, pen, out var path, out _, out var bounds));
        Assert.Equal(2, path.Figures[0].Segments.Count);
        Assert.True(path.Figures[0].Segments[1].IsSmoothJoin);
        Assert.Equal(12, bounds.Right); // Round turn support, not a long miter.
        figure.Segments[1].IsSmoothJoin = true;
        figure.Segments[2].IsSmoothJoin = false;
        Assert.True(StrokeCoverageGeometry.TryPrepareLinearPath(source, pen, out path, out _, out bounds));
        Assert.True(path.Figures[0].Segments[1].IsSmoothJoin);
        Assert.Equal(12, bounds.Right); // Smooth metadata on a compacted constant survives.
    }

    [Fact]
    public void IndependentFlatPointFiguresDoNotCreatePhantomCoverage()
    {
        var source = new PathGeometry();
        foreach (var point in new[] { new Vector2(0, 0), new Vector2(100, 100) })
        {
            var figure = new PathFigure(point);
            figure.Segments.Add(new LineSegment(point));
            source.Figures.Add(figure);
        }
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4);
        Assert.True(StrokeCoverageGeometry.TryPrepareLinearPath(source, pen, out _, out _, out var bounds));
        Assert.Equal(default, bounds);
        source.Figures[0].StrokeStartLineCap = PenLineCap.Round;
        Assert.True(StrokeCoverageGeometry.TryPrepareLinearPath(source, pen, out _, out _, out bounds));
        Assert.Equal(new Rect(-2, -2, 2, 4), bounds);
    }

    [Fact]
    public void UnsupportedTopologyAndStatePublishNoPartialResult()
    {
        var source = RectangleSpine(true, false);
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4);
        source.Figures[0].Segments.Add(new QuadraticBezierSegment(new(1, 2), new(3, 4)));
        Reject();
        source.Figures[0].Segments.RemoveAt(3);
        pen.DashArray = [0, 0]; Reject();
        pen.DashArray = null; pen.StrokeTransformMode = PenStrokeTransformMode.Fixed; Reject();
        pen.StrokeTransformMode = (PenStrokeTransformMode)23; Reject();
        pen.StrokeTransformMode = PenStrokeTransformMode.Normal; pen.Thickness = float.NaN; Reject();
        void Reject()
        {
            Assert.False(StrokeCoverageGeometry.TryPrepareLinearPath(source, pen, out var path, out var resultPen, out var bounds));
            Assert.Null(path); Assert.Null(resultPen); Assert.Equal(default, bounds);
        }
    }

    private static PathGeometry RectangleSpine(bool closed, bool gap)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new(10, 20), closed) { IsFilled = false };
        figure.Segments.Add(new LineSegment(new(30, 20)));
        figure.Segments.Add(new LineSegment(new(30, 40), isStroked: !gap));
        figure.Segments.Add(new LineSegment(new(10, 40)));
        path.Figures.Add(figure);
        return path;
    }
}
