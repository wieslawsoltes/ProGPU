using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.ProGpu;
using ProGPU.Vector;
using Xunit;

namespace Avalonia.ProGpu.ContractTests;

public sealed class AvaloniaStreamPathContractTests
{
    [Fact]
    public void WriterRecordsEveryAvaloniaSegmentKind()
    {
        var geometry = new AvaloniaStreamPath();
        using (var writer = geometry.Open())
        {
            writer.SetFillRule(Avalonia.Media.FillRule.NonZero);
            writer.BeginFigure(new Point(1, 2));
            writer.LineTo(new Point(3, 4));
            writer.QuadraticBezierTo(new Point(5, 6), new Point(7, 8));
            writer.CubicBezierTo(
                new Point(9, 10),
                new Point(11, 12),
                new Point(13, 14));
            writer.ArcTo(
                new Point(20, 30),
                new Size(4, 5),
                Math.PI / 2,
                isLargeArc: true,
                Avalonia.Media.SweepDirection.Clockwise);
            writer.EndFigure(isClosed: true);
        }

        var figure = Assert.Single(geometry.Path.Figures);
        Assert.True(figure.IsClosed);
        Assert.Equal(ProGPU.Vector.FillRule.Nonzero, geometry.Path.FillRule);
        Assert.Collection(
            figure.Segments,
            segment => Assert.IsType<ProGPU.Vector.LineSegment>(segment),
            segment => Assert.IsType<ProGPU.Vector.QuadraticBezierSegment>(segment),
            segment => Assert.IsType<ProGPU.Vector.CubicBezierSegment>(segment),
            segment =>
            {
                var arc = Assert.IsType<ProGPU.Vector.ArcSegment>(segment);
                Assert.Equal(90, arc.RotationAngle, 4);
                Assert.Equal(ProGPU.Vector.SweepDirection.Clockwise, arc.SweepDirection);
            });
    }

    [Fact]
    public void CloneOwnsAnIndependentPathSnapshot()
    {
        var geometry = CreatePolyline();
        var clone = Assert.IsType<AvaloniaStreamPath>(geometry.Clone());

        geometry.Path.Figures.Clear();

        Assert.Single(clone.Path.Figures);
        Assert.Equal(10, clone.ContourLength, 4);
    }

    [Fact]
    public void MeasureQueriesReturnPointTangentAndSubsegment()
    {
        var geometry = CreatePolyline();

        Assert.Equal(10, geometry.ContourLength, 4);
        Assert.True(geometry.TryGetPointAndTangentAtDistance(
            2.5,
            out var point,
            out var tangent));
        Assert.Equal(new Point(2.5, 0), point);
        Assert.Equal(new Point(1, 0), tangent);

        Assert.True(geometry.TryGetSegment(
            2,
            8,
            startOnBeginFigure: true,
            out var segment));
        Assert.Equal(6, segment.ContourLength, 4);
        Assert.False(geometry.TryGetSegment(
            -1,
            3,
            startOnBeginFigure: true,
            out _));
    }

    [Fact]
    public void FillAndStrokeQueriesUseRetainedPathData()
    {
        var rectangle = AvaloniaGeometryFactory.Rectangle(new Rect(0, 0, 20, 10));

        Assert.True(rectangle.FillContains(new Point(5, 5)));
        Assert.False(rectangle.FillContains(new Point(25, 5)));
        Assert.True(rectangle.StrokeContains(
            new Avalonia.Media.Pen(Brushes.Black, 2),
            new Point(0.5, 5)));
        Assert.False(rectangle.StrokeContains(
            new Avalonia.Media.Pen(Brushes.Black, 2),
            new Point(10, 5)));
    }

    [Fact]
    public void BooleanFillQueriesStayCpuOnly()
    {
        var left = AvaloniaGeometryFactory.Rectangle(new Rect(0, 0, 20, 20));
        var right = AvaloniaGeometryFactory.Rectangle(new Rect(10, 0, 20, 20));
        var difference = AvaloniaGeometryFactory.Combine(
            GeometryCombineMode.Exclude,
            left,
            right);

        Assert.True(difference.FillContains(new Point(5, 5)));
        Assert.False(difference.FillContains(new Point(15, 5)));
    }

    [Fact]
    public void InvalidWriterOrderFailsDeterministically()
    {
        var geometry = new AvaloniaStreamPath();
        using var writer = geometry.Open();

        Assert.Throws<InvalidOperationException>(
            () => writer.LineTo(new Point(1, 1)));
        writer.BeginFigure(default);
        Assert.Throws<InvalidOperationException>(
            () => writer.BeginFigure(default));
        writer.EndFigure(isClosed: false);
        Assert.Throws<InvalidOperationException>(
            () => writer.SetFillRule(Avalonia.Media.FillRule.EvenOdd));
    }

    private static AvaloniaStreamPath CreatePolyline()
    {
        var geometry = new AvaloniaStreamPath();
        using var writer = geometry.Open();
        writer.BeginFigure(default, isFilled: false);
        writer.LineTo(new Point(5, 0));
        writer.LineTo(new Point(10, 0));
        writer.EndFigure(isClosed: false);
        return geometry;
    }
}
