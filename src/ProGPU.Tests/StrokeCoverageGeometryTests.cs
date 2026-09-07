using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class StrokeCoverageGeometryTests
{
    [Theory]
    [InlineData(PenLineCap.Flat, 10f, 30f)]
    [InlineData(PenLineCap.Square, 8f, 32f)]
    [InlineData(PenLineCap.Round, 8f, 32f)]
    [InlineData(PenLineCap.Triangle, 8f, 32f)]
    public void HorizontalLineBoundsIncludeCapSupport(PenLineCap cap, float left, float right)
    {
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4, startLineCap: cap, endLineCap: cap);
        Assert.True(StrokeCoverageGeometry.TryPrepareLine(new(10, 20), new(30, 20), pen,
            out var path, out var resultPen, out var bounds));
        Assert.Same(pen, resultPen);
        Assert.Single(path.Figures);
        Assert.Equal(new Rect(left, 18, right - left, 4), bounds);
    }

    [Fact]
    public void ShortRoundCapIsAHalfDiscNotAFullEndpointCircle()
    {
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4,
            startLineCap: PenLineCap.Round, endLineCap: PenLineCap.Flat);
        Assert.True(StrokeCoverageGeometry.TryPrepareLine(new(10, 20), new(10.25f, 20), pen,
            out _, out _, out var bounds));
        Assert.Equal(new Rect(8, 18, 2.25f, 4), bounds);
    }

    [Theory]
    [InlineData(10f, 20f, 30f, 50f)]
    [InlineData(-13f, 6f, -13.25f, 6.125f)]
    [InlineData(30f, 40f, -20f, -60f)]
    public void IntrinsicRoundCapBoundsMatchScalarCubicOracle(float x0, float y0, float x1, float y1)
    {
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4,
            startLineCap: PenLineCap.Round, endLineCap: PenLineCap.Round);
        Assert.True(StrokeCoverageGeometry.TryPrepareLine(new(x0, y0), new(x1, y1), pen,
            out _, out _, out var bounds));
        // Independent dense scalar oracle for the same public two-quarter cap
        // contract. It is intentionally not used by production bound queries.
        double dx = (double)x1 - x0, dy = (double)y1 - y0;
        double length = System.Math.Sqrt(dx * dx + dy * dy);
        double ux = dx / length * 2, uy = dy / length * 2;
        double nx = -uy, ny = ux;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        void Include(double x, double y)
        {
            minX = System.Math.Min(minX, x); minY = System.Math.Min(minY, y);
            maxX = System.Math.Max(maxX, x); maxY = System.Math.Max(maxY, y);
        }
        void Cubic(double ax, double ay, double bx, double by, double cx, double cy, double ex, double ey)
        {
            ax = (float)ax; ay = (float)ay; bx = (float)bx; by = (float)by;
            cx = (float)cx; cy = (float)cy; ex = (float)ex; ey = (float)ey;
            for (int i = 0; i <= 4096; i++)
            {
                double t = i / 4096.0, s = 1 - t;
                Include(s * s * s * ax + 3 * s * s * t * bx + 3 * s * t * t * cx + t * t * t * ex,
                    s * s * s * ay + 3 * s * s * t * by + 3 * s * t * t * cy + t * t * t * ey);
            }
        }
        for (int cap = 0; cap < 2; cap++)
        {
            double x = cap == 0 ? x0 : x1, y = cap == 0 ? y0 : y1, sign = cap == 0 ? -1 : 1;
            double ox = ux * sign, oy = uy * sign;
            const double k = 0.5522847498307933984;
            Cubic(x - nx, y - ny, x - nx + ox * k, y - ny + oy * k,
                x + ox - nx * k, y + oy - ny * k, x + ox, y + oy);
            Cubic(x + ox, y + oy, x + ox + nx * k, y + oy + ny * k,
                x + nx + ox * k, y + ny + oy * k, x + nx, y + ny);
        }
        Assert.InRange(System.Math.Abs(bounds.X - minX), 0, 0.0001);
        Assert.InRange(System.Math.Abs(bounds.Y - minY), 0, 0.0001);
        Assert.InRange(System.Math.Abs(bounds.Right - maxX), 0, 0.0001);
        Assert.InRange(System.Math.Abs(bounds.Bottom - maxY), 0, 0.0001);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void DashedLineUsesPreparedFiguresAndEffectiveCaps(int count)
    {
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4,
            startLineCap: PenLineCap.Triangle, endLineCap: PenLineCap.Square, dashCap: PenLineCap.Round,
            dashArray: count == 2 ? [2, 1] : [2, 1, 3], dashOffset: 0.25);
        Assert.True(StrokeCoverageGeometry.TryPrepareLine(new(8, 16), new(56, 48), pen,
            out var path, out var resultPen, out var bounds));
        Assert.True(path.Figures.Count > 1);
        Assert.False(resultPen.HasDashPattern);
        Assert.All(path.Figures, figure => Assert.Single(figure.Segments));
        Assert.True(bounds.Width > 0 && bounds.Height > 0);
    }

    [Fact]
    public void EmptyAndUnsupportedPatternsAreExplicit()
    {
        var pen = new Pen(new SolidColorBrush(Vector4.One), 0);
        Assert.True(StrokeCoverageGeometry.TryPrepareLine(Vector2.Zero, Vector2.One, pen, out _, out _, out var bounds));
        Assert.Equal(default, bounds);
        pen.Thickness = 4;
        pen.SetDashPattern([0, 1]);
        Assert.False(StrokeCoverageGeometry.TryPrepareLine(Vector2.Zero, Vector2.One, pen, out _, out _, out _));
        pen.SetDashPattern([double.PositiveInfinity, 1]);
        Assert.False(StrokeCoverageGeometry.TryPrepareLine(Vector2.Zero, Vector2.One, pen, out _, out _, out _));
        pen.SetDashPattern([1, 1]);
        Assert.False(StrokeCoverageGeometry.TryPrepareLine(Vector2.Zero, new(1e9f, 0), pen, out _, out _, out _));
        pen.StrokeTransformMode = PenStrokeTransformMode.Fixed;
        Assert.False(StrokeCoverageGeometry.TryPrepareLine(Vector2.Zero, Vector2.One, pen, out _, out _, out _));
    }
}
