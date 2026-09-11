using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableLineGeometryTests
{
    [Theory]
    [InlineData(2.0, 0.0, 0.0, 0.5, 3.0, 4.0)]
    [InlineData(1.0, 0.5, -0.25, -2.0, -10.0, 7.0)]
    [InlineData(0.0, 0.0, 0.0, 0.0, 3.0, 4.0)]
    public void PrimitiveLineMapsDoubleCoordinatesWithoutPathPacking(double a, double b, double c,
        double d, double x, double y)
    {
        var first = new PortablePoint(16777217.25, -10.125);
        var last = new PortablePoint(-34.125, 70.75);
        var primitive = PortablePrimitiveGeometry.Line(first, last, new(a, b, c, d, x, y));
        Assert.True(primitive.TryGetTransformedLinePoints(out var actualFirst, out var actualLast));
        Assert.Equal(first.X * a + first.Y * c + x, actualFirst.X);
        Assert.Equal(first.X * b + first.Y * d + y, actualFirst.Y);
        Assert.Equal(last.X * a + last.Y * c + x, actualLast.X);
        Assert.Equal(last.X * b + last.Y * d + y, actualLast.Y);
    }

    [Fact]
    public void InvalidAndNonLineDescriptorsLeaveNoPartialEndpoints()
    {
        var primitive = PortablePrimitiveGeometry.Line(new(1, 2), new(3, 4), new(double.PositiveInfinity, 0, 0, 1, 0, 0));
        Assert.False(primitive.TryGetTransformedLinePoints(out var first, out var last));
        Assert.Equal(0, first.X); Assert.Equal(0, last.X);
        primitive = PortablePrimitiveGeometry.Line(new(double.MaxValue, 0), new(1, 2), new(2, 0, 0, 1, 0, 0));
        Assert.False(primitive.TryGetTransformedLinePoints(out first, out last));
        Assert.Equal(0, first.X); Assert.Equal(0, last.X);
        primitive = PortablePrimitiveGeometry.Rectangle(new(0, 0, 10, 10), 0, 0, PortableMatrix3x2.Identity);
        Assert.False(primitive.TryGetTransformedLinePoints(out _, out _));
    }

    [Fact]
    public void OpenLineClassifierRejectsTopologyAndCapChanges()
    {
        var path = RenderCommandGeometryCache.CreateLinePath(new(10, 20), new(30, 20));
        Assert.True(PrimitivePathGeometry.TryGetOpenLine(path, out var start, out var end));
        Assert.Equal(new Vector2(10, 20), start); Assert.Equal(new Vector2(30, 20), end);
        var figure = path.Figures[0];
        figure.IsClosed = true;
        Assert.False(PrimitivePathGeometry.TryGetOpenLine(path, out _, out _));
        figure.IsClosed = false;
        figure.StrokeStartLineCap = PenLineCap.Round;
        Assert.False(PrimitivePathGeometry.TryGetOpenLine(path, out _, out _));
        figure.StrokeStartLineCap = null;
        figure.Segments[0].IsStroked = false;
        Assert.False(PrimitivePathGeometry.TryGetOpenLine(path, out _, out _));
        figure.Segments[0].IsStroked = true;
        figure.Segments.Add(new LineSegment(new(40, 50)));
        Assert.False(PrimitivePathGeometry.TryGetOpenLine(path, out _, out _));
        path.IsCombined = true;
        Assert.False(PrimitivePathGeometry.TryGetOpenLine(path, out _, out _));
    }
}
