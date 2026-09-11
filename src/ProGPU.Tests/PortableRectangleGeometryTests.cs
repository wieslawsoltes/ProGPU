using System;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableRectangleGeometryTests
{
    [Theory]
    [InlineData(2.0, 0.0, 0.0, 0.5, 3.0, 4.0)]
    [InlineData(0.8, 0.6, -0.6, 0.8, 3.0, -2.0)]
    [InlineData(-1.0, 0.25, 0.5, 2.0, -5.0, 10.0)]
    public void DescriptorCornersMatchScalarDoubleOracleAndPreserveTail(double a, double b, double c,
        double d, double x, double y)
    {
        var rectangle = new PortableRect(16777217.25, -10.125, 0.0625, 20.25);
        var primitive = PortablePrimitiveGeometry.Rectangle(rectangle, 0, 0, new(a, b, c, d, x, y));
        Span<PortablePoint> output = stackalloc PortablePoint[5];
        output[4] = new(91, 92);
        Assert.True(primitive.TryWriteTransformedRectangleCorners(output));
        for (int i = 0; i < 4; i++)
        {
            double px = rectangle.X + (i is 1 or 2 ? rectangle.Width : 0);
            double py = rectangle.Y + (i >= 2 ? rectangle.Height : 0);
            Assert.Equal(px * a + py * c + x, output[i].X);
            Assert.Equal(px * b + py * d + y, output[i].Y);
        }
        Assert.Equal(91, output[4].X); Assert.Equal(92, output[4].Y);
    }

    [Fact]
    public void FailedCornerQueriesAreTransactional()
    {
        Span<PortablePoint> output = stackalloc PortablePoint[4];
        output.Fill(new(91, 92));
        var primitive = PortablePrimitiveGeometry.Rectangle(new(0, 0, 1e308, 1e308), 0, 0, new(1, 0, 1, 1, 0, 0));
        Assert.False(primitive.TryWriteTransformedRectangleCorners(output)); // The third corner overflows.
        primitive = PortablePrimitiveGeometry.Rectangle(new(0, 0, 10, 10), 0, 0, PortableMatrix3x2.Identity);
        Assert.False(primitive.TryWriteTransformedRectangleCorners(output[..3]));
        primitive = PortablePrimitiveGeometry.Rectangle(new(0, 0, 10, 10), 1, 1, PortableMatrix3x2.Identity);
        Assert.False(primitive.TryWriteTransformedRectangleCorners(output));
        primitive = PortablePrimitiveGeometry.Rectangle(new(0, 0, 0, 10), 0, 0, PortableMatrix3x2.Identity);
        Assert.False(primitive.TryWriteTransformedRectangleCorners(output));
        primitive = PortablePrimitiveGeometry.Line(new(1, 2), new(3, 4), PortableMatrix3x2.Identity);
        Assert.False(primitive.TryWriteTransformedRectangleCorners(output));
        foreach (var point in output) { Assert.Equal(91, point.X); Assert.Equal(92, point.Y); }
    }

    [Theory]
    [InlineData(PenLineJoin.Miter)]
    [InlineData(PenLineJoin.Bevel)]
    [InlineData(PenLineJoin.Round)]
    public void PreparedCornersShareRectangleJoinImplementation(PenLineJoin join)
    {
        var rectangle = new Rect(8, 8, 24, 12);
        var matrix = new Matrix3x2(-2, 0.25f, 0.5f, 1, 3, 4);
        var primitive = PortablePrimitiveGeometry.Rectangle(new(8, 8, 24, 12), 0, 0,
            new(matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M31, matrix.M32));
        Span<PortablePoint> mapped = stackalloc PortablePoint[4];
        Span<Vector2> points = stackalloc Vector2[4];
        Assert.True(primitive.TryWriteTransformedRectangleCorners(mapped));
        for (int i = 0; i < 4; i++) points[i] = new((float)mapped[i].X, (float)mapped[i].Y);
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4, lineJoin: join, miterLimit: 1);
        Assert.True(StrokeCoverageGeometry.TryPrepareRectangle(rectangle, matrix, pen, out var expected, out _, out var expectedBounds));
        Assert.True(StrokeCoverageGeometry.TryPrepareConvexQuadrilateral(points, pen, out var actual, out _, out var actualBounds));
        Assert.Equal(expectedBounds, actualBounds);
        Assert.Equal(expected.Figures[0].StartPoint, actual.Figures[0].StartPoint);
        points.Clear(); // The returned path owns its vertices, never the input span.
        Assert.Equal(expected.Figures[0].StartPoint, actual.Figures[0].StartPoint);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(4f)]
    public void ConvexPreparationRejectsConcaveCrossedAndCollapsedInput(float width)
    {
        var pen = new Pen(new SolidColorBrush(Vector4.One), width);
        void Reject(Vector2[] points)
        {
            Assert.False(StrokeCoverageGeometry.TryPrepareConvexQuadrilateral(points, pen, out var path, out var resultPen, out var bounds));
            Assert.Null(path); Assert.Null(resultPen); Assert.Equal(default, bounds);
        }
        Reject([new(0, 0), new(4, 0), new(1, 1), new(4, 4)]);
        Reject([new(0, 0), new(4, 4), new(4, 0), new(0, 4)]);
        Reject([new(0, 0), new(4, 0), new(4, 0), new(0, 4)]);
        Reject([new(0, 0), new(4, 0), new(4, 4)]);
    }
}
