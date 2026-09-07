using System;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class SmoothStrokeCoverageTests
{
    [Theory]
    [InlineData(true, 12f, 6f)]
    [InlineData(false, 3f, 2f)]
    [InlineData(false, 12f, 2f)]
    [InlineData(false, 3f, 6f)]
    [InlineData(false, 12f, 6f)]
    [InlineData(false, 100f, 100f)]
    public void AffineSpinePrecedesWidthAndClampedConnectorsRemainSmooth(bool ellipse, float rx, float ry)
    {
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4);
        var matrix = new Matrix3x2(2, 0, 0, 0.5f, 3, 4);
        Assert.True(Prepare(ellipse, rx, ry, matrix, pen, out var path, out var resultPen, out var bounds));
        // Independent axis-extrema oracle: spine [19,8..67,14], width four
        // applied afterwards. Matched native MIL fixture uses the same shape.
        Near(17, bounds.X); Near(6, bounds.Y); Near(69, bounds.Right); Near(16, bounds.Bottom);
        Assert.Same(pen, resultPen);
        var figure = Assert.Single(path.Figures);
        Assert.True(figure.IsClosed);
        Assert.Equal(4, System.Linq.Enumerable.Count(figure.Segments, segment => segment is ArcSegment));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReflectionAndTranslationPreserveBoundsAndReturnedPathOwnership(bool ellipse)
    {
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4);
        Assert.True(Prepare(ellipse, 12, 6, Matrix3x2.Identity, pen, out var original, out _, out var a));
        Assert.True(Prepare(ellipse, 12, 6, new(-1, 0, 0, 1, 80, -5), pen, out var reflected, out _, out var b));
        Near(80 - a.Right, b.X); Near(a.Y - 5, b.Y); Near(a.Width, b.Width); Near(a.Height, b.Height);
        original.Figures.Clear();
        Assert.True(Assert.Single(reflected.Figures).IsClosed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ZeroWidthRetainsSpineAndUnsupportedPensFailTransactionally(bool ellipse)
    {
        var pen = new Pen(new SolidColorBrush(Vector4.One), 0);
        Assert.True(Prepare(ellipse, 12, 6, Matrix3x2.Identity, pen, out var path, out _, out var bounds));
        Assert.Single(path.Figures); Assert.Equal(default, bounds);
        pen.Thickness = 4;
        pen.DashArray = [1, 1];
        Reject(pen, Matrix3x2.Identity);
        pen.DashArray = null;
        pen.StrokeTransformMode = PenStrokeTransformMode.Fixed;
        Reject(pen, Matrix3x2.Identity);
        pen.StrokeTransformMode = PenStrokeTransformMode.Normal;
        pen.Thickness = Pen.HairlineThickness;
        Reject(pen, Matrix3x2.Identity);
        pen.Thickness = 4;
        Reject(pen, new(1, 0, 0, 0, 0, 0));
        Reject(pen, new(float.PositiveInfinity, 0, 0, 1, 0, 0));
        pen.Thickness = float.NaN;
        Reject(pen, Matrix3x2.Identity);

        void Reject(Pen value, Matrix3x2 matrix)
        {
            Assert.False(Prepare(ellipse, 12, 6, matrix, value, out var failedPath, out var failedPen, out var failedBounds));
            Assert.Null(failedPath); Assert.Null(failedPen); Assert.Equal(default, failedBounds);
        }
    }

    private static bool Prepare(bool ellipse, float rx, float ry, Matrix3x2 transform, Pen pen,
        out PathGeometry path, out Pen resultPen, out Rect bounds) => ellipse
        ? StrokeCoverageGeometry.TryPrepareEllipse(new(20, 14), rx, ry, transform, pen, out path, out resultPen, out bounds)
        : StrokeCoverageGeometry.TryPrepareRoundedRectangle(new(8, 8, 24, 12), rx, ry, transform, pen, out path, out resultPen, out bounds);

    private static void Near(float expected, float actual) => Assert.InRange(MathF.Abs(expected - actual), 0, 0.02f);
}
