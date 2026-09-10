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
using WpfFillRule = System.Windows.Media.FillRule;
using WpfGeometry = System.Windows.Media.Geometry;
using WpfPathGeometry = System.Windows.Media.PathGeometry;

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
    public void SkPathTightBoundsUseBezierExtremaInsteadOfControlPoints()
    {
        using var path = new SKPath();
        path.MoveTo(0f, 0f);
        path.QuadTo(50f, 100f, 100f, 0f);
        path.CubicTo(100f, -100f, 200f, -100f, 200f, 0f);

        AssertNear(-100f, path.Bounds.Top);
        AssertNear(100f, path.Bounds.Bottom);

        var tight = path.TightBounds;
        AssertNear(-75f, tight.Top);
        AssertNear(50f, tight.Bottom);
        AssertNear(0f, tight.Left);
        AssertNear(200f, tight.Right);
    }

    [Fact]
    public void SkPathCopyPreservesOpenContourForContinuedSegments()
    {
        using var source = new SKPath();
        source.MoveTo(10f, 100f);

        using var copy = new SKPath(source);
        copy.CubicTo(0f, 0f, 200f, 0f, 300f, 100f);

        var figure = Assert.Single(copy.Geometry.Figures);
        Assert.Equal(new System.Numerics.Vector2(10f, 100f), figure.StartPoint);
        Assert.IsType<ProGPU.Vector.CubicBezierSegment>(Assert.Single(figure.Segments));
    }

    [Fact]
    public void SkPaintWidensFullOvalAnalytically()
    {
        using var source = new SKPath();
        source.AddOval(new SKRect(0f, 0f, 200f, 200f));
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 10f
        };

        using var widened = Assert.IsType<SKPath>(paint.GetFillPath(source));

        AssertNear(-5f, widened.TightBounds.Left);
        AssertNear(-5f, widened.TightBounds.Top);
        AssertNear(205f, widened.TightBounds.Right);
        AssertNear(205f, widened.TightBounds.Bottom);
        Assert.False(widened.Contains(100f, 100f));
        Assert.True(widened.Contains(100f, 2f));
    }

    [Fact]
    public void SkPaintFillPathIncludesMiteredRectangleCorners()
    {
        using var source = new SKPath();
        source.AddRect(new SKRect(35f, 110f, 445f, 150f));
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 20f,
            StrokeJoin = SKStrokeJoin.Miter,
            StrokeMiter = 4f
        };

        using var widened = Assert.IsType<SKPath>(paint.GetFillPath(source));
        Assert.True(widened.Contains(25.5f, 100.5f));
        Assert.True(widened.Contains(454.5f, 159.5f));
        Assert.False(widened.Contains(24.5f, 100.5f));
    }

    [Theory]
    [InlineData(SKStrokeJoin.Miter, 30f, 14f, true)]
    [InlineData(SKStrokeJoin.Round, 30f, 14f, false)]
    [InlineData(SKStrokeJoin.Round, 30f, 15.5f, true)]
    [InlineData(SKStrokeJoin.Bevel, 30f, 15.5f, false)]
    public void SkPaintFillPathPreservesStrokeJoinShape(
        SKStrokeJoin strokeJoin,
        float sampleX,
        float sampleY,
        bool expected)
    {
        using var source = new SKPath();
        source.MoveTo(10f, 40f);
        source.LineTo(30f, 20f);
        source.LineTo(30f, 20f);
        source.LineTo(50f, 40f);
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 10f,
            StrokeJoin = strokeJoin,
            StrokeMiter = 4f
        };

        using var widened = Assert.IsType<SKPath>(paint.GetFillPath(source));
        Assert.Equal(expected, widened.Contains(sampleX, sampleY));
    }

    [Theory]
    [InlineData(SKStrokeCap.Square, 5.5f, 15.5f)]
    [InlineData(SKStrokeCap.Round, 6.5f, 20f)]
    public void SkPaintFillPathIncludesOpenStrokeCaps(
        SKStrokeCap strokeCap,
        float sampleX,
        float sampleY)
    {
        using var source = new SKPath();
        source.MoveTo(10f, 20f);
        source.LineTo(50f, 20f);
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 10f,
            StrokeCap = strokeCap
        };

        using var widened = Assert.IsType<SKPath>(paint.GetFillPath(source));
        Assert.True(widened.Contains(sampleX, sampleY));
        Assert.False(widened.Contains(4.5f, 20f));
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    public void SkPaintFillPathPreservesFractionalStrokeWidth(float strokeWidth)
    {
        using var source = new SKPath();
        source.MoveTo(10f, 20f);
        source.LineTo(50f, 20f);
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
            StrokeCap = SKStrokeCap.Butt
        };

        using var widened = Assert.IsType<SKPath>(paint.GetFillPath(source));
        AssertNear(10f, widened.Bounds.Left);
        AssertNear(20f - strokeWidth * 0.5f, widened.Bounds.Top);
        AssertNear(50f, widened.Bounds.Right);
        AssertNear(20f + strokeWidth * 0.5f, widened.Bounds.Bottom);
    }

    [Fact]
    public void SkPaintFillPathCannotMaterializeDeviceDependentHairline()
    {
        using var source = new SKPath();
        source.MoveTo(10f, 20f);
        source.LineTo(50f, 20f);
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0f
        };

        Assert.Null(paint.GetFillPath(source));
    }

    [Fact]
    public void SkPathTransformUpdatesNativeArcParameters()
    {
        using var path = new SKPath();
        path.AddCircle(10f, 20f, 5f);

        path.Transform(new SKMatrix { ScaleX = 2f, ScaleY = 3f, Persp2 = 1f });

        var figure = Assert.Single(path.Geometry.Figures);
        Assert.Equal(new Vector2(30f, 60f), figure.StartPoint);

        var firstArc = Assert.IsType<ArcSegment>(figure.Segments[0]);
        Assert.Equal(new Vector2(10f, 60f), firstArc.Point);
        AssertNear(10f, firstArc.Size.X);
        AssertNear(15f, firstArc.Size.Y);
        Assert.Equal(SweepDirection.Clockwise, firstArc.SweepDirection);

        var secondArc = Assert.IsType<ArcSegment>(figure.Segments[1]);
        Assert.Equal(new Vector2(30f, 60f), secondArc.Point);
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
        Assert.Equal(new Vector2(5f, 20f), sourceArc.Point);
        AssertNear(5f, sourceArc.Size.X);
        AssertNear(5f, sourceArc.Size.Y);

        var copyFigure = Assert.Single(copy.Geometry.Figures);
        var copyArc = Assert.IsType<ArcSegment>(copyFigure.Segments[0]);
        Assert.Equal(new Vector2(10f, 60f), copyArc.Point);
        AssertNear(10f, copyArc.Size.X);
        AssertNear(15f, copyArc.Size.Y);
    }

    [Fact]
    public void SkPathCounterClockwiseRoundRectWalksPerimeter()
    {
        using var path = new SKPath();

        path.AddRoundRect(new SKRect(0f, 0f, 100f, 50f), 10f, 10f, SKPathDirection.CounterClockwise);

        var figure = Assert.Single(path.Geometry.Figures);
        Assert.Equal(new Vector2(0f, 10f), figure.StartPoint);
        Assert.True(figure.IsClosed);
        Assert.Equal(8, figure.Segments.Count);

        Assert.Equal(new Vector2(0f, 40f), Assert.IsType<LineSegment>(figure.Segments[0]).Point);
        Assert.Equal(new Vector2(10f, 50f), Assert.IsType<ArcSegment>(figure.Segments[1]).Point);
        Assert.Equal(new Vector2(90f, 50f), Assert.IsType<LineSegment>(figure.Segments[2]).Point);
        Assert.Equal(new Vector2(100f, 40f), Assert.IsType<ArcSegment>(figure.Segments[3]).Point);
        Assert.Equal(new Vector2(100f, 10f), Assert.IsType<LineSegment>(figure.Segments[4]).Point);
        Assert.Equal(new Vector2(90f, 0f), Assert.IsType<ArcSegment>(figure.Segments[5]).Point);
        Assert.Equal(new Vector2(10f, 0f), Assert.IsType<LineSegment>(figure.Segments[6]).Point);
        Assert.Equal(new Vector2(0f, 10f), Assert.IsType<ArcSegment>(figure.Segments[7]).Point);
        Assert.All(
            figure.Segments.OfType<ArcSegment>(),
            arc => Assert.Equal(SweepDirection.Counterclockwise, arc.SweepDirection));
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
    public void SkPathResetRestoresWindingFillType()
    {
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        path.AddRect(new SKRect(0f, 0f, 10f, 10f));

        path.Reset();

        Assert.Equal(SKPathFillType.Winding, path.FillType);
        Assert.Equal(FillRule.Nonzero, path.Geometry.FillRule);
        Assert.True(path.IsEmpty);
    }

    [Fact]
    public void SkPathOpPreservesEvenOddFillRuleOnReturnedPath()
    {
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        using var empty = new SKPath();
        path.AddRect(new SKRect(0f, 0f, 10f, 10f));
        path.AddRect(new SKRect(2f, 2f, 8f, 8f));

        using var result = path.Op(empty, SKPathOp.Union);

        Assert.Equal(SKPathFillType.EvenOdd, result.FillType);
        Assert.Equal(FillRule.EvenOdd, result.Geometry.FillRule);
        Assert.Equal(2, result.Geometry.Figures.Count);
    }

    [Fact]
    public void SkPathResultOpPreservesEvenOddFillRuleOnResultPath()
    {
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        using var empty = new SKPath();
        using var result = new SKPath();
        path.AddRect(new SKRect(0f, 0f, 10f, 10f));
        path.AddRect(new SKRect(2f, 2f, 8f, 8f));

        Assert.True(path.Op(empty, SKPathOp.Union, result));

        Assert.Equal(SKPathFillType.EvenOdd, result.FillType);
        Assert.Equal(FillRule.EvenOdd, result.Geometry.FillRule);
        Assert.Equal(2, result.Geometry.Figures.Count);
    }

    [Fact]
    public void SkRegionIntersectRestrictsContainsAndBounds()
    {
        using var region = new SKRegion();

        Assert.True(region.SetRect(new SKRectI(0, 0, 10, 10)));
        Assert.True(region.Op(new SKRectI(5, 5, 15, 15), SKRegionOperation.Intersect));

        Assert.False(region.Contains(4, 5));
        Assert.False(region.Contains(5, 4));
        Assert.True(region.Contains(5, 5));
        Assert.True(region.Contains(9, 9));
        Assert.False(region.Contains(10, 10));
        Assert.Equal(5, region.Bounds.Left);
        Assert.Equal(5, region.Bounds.Top);
        Assert.Equal(10, region.Bounds.Right);
        Assert.Equal(10, region.Bounds.Bottom);
    }

    [Fact]
    public void SkRegionDifferenceRemovesOverlappingRect()
    {
        using var region = new SKRegion();

        Assert.True(region.SetRect(new SKRectI(0, 0, 10, 10)));
        Assert.True(region.Op(new SKRectI(4, 0, 10, 10), SKRegionOperation.Difference));

        Assert.True(region.Contains(3, 5));
        Assert.False(region.Contains(4, 5));
        Assert.False(region.Contains(9, 5));
        Assert.Equal(0, region.Bounds.Left);
        Assert.Equal(0, region.Bounds.Top);
        Assert.Equal(4, region.Bounds.Right);
        Assert.Equal(10, region.Bounds.Bottom);
    }

    [Fact]
    public void SkRegionSetPathAcceptsRectangularPath()
    {
        using var path = new SKPath();
        path.AddRect(new SKRect(1f, 2f, 11f, 12f));
        using var region = new SKRegion();

        Assert.True(region.SetPath(path));

        Assert.True(region.Contains(1, 2));
        Assert.True(region.Contains(10, 11));
        Assert.False(region.Contains(11, 11));
        Assert.Equal(1, region.Bounds.Left);
        Assert.Equal(2, region.Bounds.Top);
        Assert.Equal(11, region.Bounds.Right);
        Assert.Equal(12, region.Bounds.Bottom);
    }

    [Fact]
    public void SkRegionSetPathRejectsNonRectangularPathInsteadOfBoundingBox()
    {
        using var path = new SKPath();
        path.MoveTo(0f, 0f);
        path.LineTo(10f, 0f);
        path.LineTo(0f, 10f);
        path.Close();
        using var region = new SKRegion();

        Assert.False(region.SetPath(path));

        Assert.True(region.IsEmpty);
        Assert.False(region.Contains(8, 8));
        Assert.Equal(SKRectI.Empty, region.Bounds);
    }

    [Fact]
    public void WpfPathGeometryParseHonorsFillRulePrefix()
    {
        var evenOdd = WpfPathGeometry.Parse("F0 M 0 0 L 10 0 L 10 10 Z");
        var nonzero = WpfPathGeometry.Parse("F1 M 0 0 L 10 0 L 10 10 Z");
        var spacedEvenOdd = WpfPathGeometry.Parse("F 0 M 0 0 L 10 0 L 10 10 Z");
        var spacedNonzero = WpfPathGeometry.Parse("F,1 M 0 0 L 10 0 L 10 10 Z");
        var vectorEvenOdd = ProGPU.Vector.PathGeometry.Parse("F0 M 0 0 L 10 0 L 10 10 Z");
        var vectorNonzero = ProGPU.Vector.PathGeometry.Parse("F 1 M 0 0 L 10 0 L 10 10 Z");

        Assert.Equal(WpfFillRule.EvenOdd, evenOdd.FillRule);
        Assert.Equal(WpfFillRule.Nonzero, nonzero.FillRule);
        Assert.Equal(WpfFillRule.EvenOdd, spacedEvenOdd.FillRule);
        Assert.Equal(WpfFillRule.Nonzero, spacedNonzero.FillRule);
        Assert.Single(evenOdd.Figures);
        Assert.Single(nonzero.Figures);
        Assert.Equal(FillRule.EvenOdd, vectorEvenOdd.FillRule);
        Assert.Equal(FillRule.Nonzero, vectorNonzero.FillRule);
        Assert.Single(vectorEvenOdd.Figures);
        Assert.Single(vectorNonzero.Figures);
    }

    [Fact]
    public void WpfGeometryParseUsesPathGeometryParser()
    {
        var geometry = WpfGeometry.Parse("F1 M 0 0 L 10 0 L 10 10 Z");

        var path = Assert.IsType<WpfPathGeometry>(geometry);
        Assert.Equal(WpfFillRule.Nonzero, path.FillRule);
        Assert.Single(path.Figures);
    }

    [Fact]
    public void GraphicsPathFullCircleArcSplitsIntoNativeArcs()
    {
        using var path = new DrawingGraphicsPath();
        path.AddArc(0f, 0f, 20f, 10f, 0f, 360f);

        using var graphics = DrawingGraphics.FromProGpuDrawingContext(new DrawingContext());
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

        using var graphics = DrawingGraphics.FromProGpuDrawingContext(new DrawingContext());
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
