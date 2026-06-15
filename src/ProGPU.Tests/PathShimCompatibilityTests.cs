using System;
using System.Linq;
using ProGPU.Scene;
using ProGPU.Vector;
using SkiaSharp;
using Xunit;
using DrawingFillMode = System.Drawing.Drawing2D.FillMode;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingGraphicsPath = System.Drawing.Drawing2D.GraphicsPath;
using DrawingPens = System.Drawing.Pens;

namespace ProGPU.Tests;

public sealed class PathShimCompatibilityTests
{
    [Fact]
    public void SkPathBoundsIncludeArcExtrema()
    {
        using var path = new SKPath();

        path.AddCircle(10f, 20f, 5f);

        var bounds = path.Bounds;
        AssertNear(5f, bounds.Left);
        AssertNear(15f, bounds.Top);
        AssertNear(15f, bounds.Right);
        AssertNear(25f, bounds.Bottom);
    }

    [Fact]
    public void SkCanvasDrawPathCarriesEvenOddFillRule()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 64f, 64f);
        using var paint = new SKPaint { Style = SKPaintStyle.Fill };
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        path.AddRect(new SKRect(0f, 0f, 32f, 32f));

        canvas.DrawPath(path, paint);

        var command = Assert.Single(context.Commands);
        Assert.Equal(FillRule.EvenOdd, command.Path!.FillRule);
    }

    [Fact]
    public void GraphicsPathFullCircleArcSplitsIntoNativeArcs()
    {
        using var path = new DrawingGraphicsPath();
        path.AddArc(0f, 0f, 20f, 10f, 0f, 360f);

        using var graphics = DrawingGraphics.FromHwnd(IntPtr.Zero);
        graphics.DrawPath(DrawingPens.Black, path);

        var command = Assert.Single(graphics.DrawingContext.Commands);
        var figure = Assert.Single(command.Path!.Figures);
        Assert.Equal(2, figure.Segments.OfType<ArcSegment>().Count());
        Assert.True(command.Path.TryGetBounds(out var min, out var max));
        AssertNear(0f, min.X);
        AssertNear(0f, min.Y);
        AssertNear(20f, max.X);
        AssertNear(10f, max.Y);
    }

    [Fact]
    public void GraphicsPathFillModeCarriesNativeFillRule()
    {
        using var path = new DrawingGraphicsPath(DrawingFillMode.Alternate);
        path.AddRectangle(new System.Drawing.RectangleF(0f, 0f, 30f, 30f));
        path.AddRectangle(new System.Drawing.RectangleF(10f, 10f, 10f, 10f));

        using var graphics = DrawingGraphics.FromHwnd(IntPtr.Zero);
        graphics.FillPath(System.Drawing.Brushes.Black, path);

        var command = Assert.Single(graphics.DrawingContext.Commands);
        Assert.Equal(FillRule.EvenOdd, command.Path!.FillRule);

        graphics.DrawingContext.Commands.Clear();
        path.FillMode = DrawingFillMode.Winding;
        graphics.FillPath(System.Drawing.Brushes.Black, path);

        command = Assert.Single(graphics.DrawingContext.Commands);
        Assert.Equal(FillRule.Nonzero, command.Path!.FillRule);
    }

    private static void AssertNear(float expected, float actual)
    {
        Assert.InRange(MathF.Abs(actual - expected), 0f, 0.001f);
    }
}
