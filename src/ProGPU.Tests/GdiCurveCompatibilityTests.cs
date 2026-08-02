using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class GdiCurveCompatibilityTests
{
    [Fact]
    public void DrawCurveRecordsCardinalSplineAsOneNativePath()
    {
        using var graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        System.Drawing.PointF[] points =
        [
            new(0f, 0f),
            new(30f, 60f),
            new(90f, 30f),
            new(120f, 0f)
        ];

        graphics.DrawCurve(System.Drawing.Pens.Black, points, tension: 0.6f);

        var command = Assert.Single(graphics.DrawingContext.Commands);
        var figure = Assert.Single(command.Path!.Figures);
        var segments = figure.Segments.Cast<CubicBezierSegment>().ToArray();
        Assert.Equal(3, segments.Length);
        Assert.Equal(new System.Numerics.Vector2(0f, 0f), figure.StartPoint);
        AssertNear(new System.Numerics.Vector2(6f, 12f), segments[0].ControlPoint1);
        AssertNear(new System.Numerics.Vector2(12f, 54f), segments[0].ControlPoint2);
        AssertNear(new System.Numerics.Vector2(30f, 60f), segments[0].Point);
        AssertNear(new System.Numerics.Vector2(108f, 18f), segments[2].ControlPoint1);
        AssertNear(new System.Numerics.Vector2(114f, 6f), segments[2].ControlPoint2);
        AssertNear(new System.Numerics.Vector2(120f, 0f), segments[2].Point);
    }

    [Fact]
    public void DrawCurveRangeUsesAdjacentPointsForEndpointTangents()
    {
        using var graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        System.Drawing.Point[] points =
        [
            new(0, 0),
            new(20, 20),
            new(40, 0),
            new(80, 20)
        ];

        graphics.DrawCurve(System.Drawing.Pens.Black, points, offset: 1, numberOfSegments: 2, tension: 0.75f);

        var command = Assert.Single(graphics.DrawingContext.Commands);
        var figure = Assert.Single(command.Path!.Figures);
        var segments = figure.Segments.Cast<CubicBezierSegment>().ToArray();
        Assert.Equal(2, segments.Length);
        AssertNear(new System.Numerics.Vector2(30f, 20f), segments[0].ControlPoint1);
        AssertNear(new System.Numerics.Vector2(25f, 0f), segments[0].ControlPoint2);
        AssertNear(new System.Numerics.Vector2(40f, 0f), segments[0].Point);
    }

    [Fact]
    public void DrawCurveRejectsInvalidPointRanges()
    {
        using var graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        System.Drawing.PointF[] points = [new(0f, 0f), new(1f, 1f)];

        Assert.Throws<ArgumentNullException>(() => graphics.DrawCurve(null!, points));
        Assert.Throws<ArgumentNullException>(() => graphics.DrawCurve(System.Drawing.Pens.Black, (System.Drawing.PointF[])null!));
        Assert.Throws<ArgumentException>(() => graphics.DrawCurve(
            System.Drawing.Pens.Black,
            new System.Drawing.PointF[] { new(0f, 0f) }));
        Assert.Throws<ArgumentException>(() => graphics.DrawCurve(System.Drawing.Pens.Black, points, -1, 1));
        Assert.Throws<ArgumentException>(() => graphics.DrawCurve(System.Drawing.Pens.Black, points, 0, 2));
    }

    private static void AssertNear(System.Numerics.Vector2 expected, System.Numerics.Vector2 actual)
    {
        Assert.InRange(System.Numerics.Vector2.Distance(expected, actual), 0f, 0.001f);
    }
}
