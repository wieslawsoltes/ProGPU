using System;
using System.Linq;
using System.Numerics;
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
    public void SkPathTransformUpdatesNativeArcParameters()
    {
        using var path = new SKPath();
        path.AddCircle(10f, 20f, 5f);

        path.Transform(new SKMatrix { ScaleX = 2f, ScaleY = 3f, Persp2 = 1f });

        var figure = Assert.Single(path.Geometry.Figures);
        Assert.Equal(new Vector2(10f, 60f), figure.StartPoint);

        var firstArc = Assert.IsType<ArcSegment>(figure.Segments[0]);
        Assert.Equal(new Vector2(30f, 60f), firstArc.Point);
        AssertNear(10f, firstArc.Size.X);
        AssertNear(15f, firstArc.Size.Y);
        Assert.Equal(SweepDirection.Clockwise, firstArc.SweepDirection);

        var secondArc = Assert.IsType<ArcSegment>(figure.Segments[1]);
        Assert.Equal(new Vector2(10f, 60f), secondArc.Point);
        AssertNear(10f, secondArc.Size.X);
        AssertNear(15f, secondArc.Size.Y);
        Assert.Equal(SweepDirection.Clockwise, secondArc.SweepDirection);
    }

    [Fact]
    public void SkPathAddPathDeepCopiesNativeSegments()
    {
        using var source = new SKPath();
        source.AddCircle(10f, 20f, 5f);

        using var copy = new SKPath();
        copy.AddPath(source);
        copy.Transform(new SKMatrix { ScaleX = 2f, ScaleY = 3f, Persp2 = 1f });

        var sourceFigure = Assert.Single(source.Geometry.Figures);
        var sourceArc = Assert.IsType<ArcSegment>(sourceFigure.Segments[0]);
        Assert.Equal(new Vector2(15f, 20f), sourceArc.Point);
        AssertNear(5f, sourceArc.Size.X);
        AssertNear(5f, sourceArc.Size.Y);

        var copyFigure = Assert.Single(copy.Geometry.Figures);
        var copyArc = Assert.IsType<ArcSegment>(copyFigure.Segments[0]);
        Assert.Equal(new Vector2(30f, 60f), copyArc.Point);
        AssertNear(10f, copyArc.Size.X);
        AssertNear(15f, copyArc.Size.Y);
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
