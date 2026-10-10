using System.Numerics;
using Microsoft.Graphics.Canvas.Geometry;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Win2D.Tests;

public sealed class MiterOrBevelCanvasTests
{
    [Theory]
    [InlineData(CanvasStrokeTransformBehavior.Normal, PenStrokeTransformMode.Normal, false)]
    [InlineData(CanvasStrokeTransformBehavior.Fixed, PenStrokeTransformMode.Fixed, false)]
    [InlineData(CanvasStrokeTransformBehavior.Hairline, PenStrokeTransformMode.Fixed, true)]
    public void DistinctJoinSurvivesEachTransformModeAndWarmCache(
        CanvasStrokeTransformBehavior behavior,
        PenStrokeTransformMode expectedMode,
        bool hairline)
    {
        using var style = new CanvasStrokeStyle
        {
            LineJoin = CanvasLineJoin.MiterOrBevel,
            MiterLimit = 2f,
            TransformBehavior = behavior
        };
        var brush = new SolidColorBrush(Vector4.One);

        Pen first = style.GetOrCreatePen(brush, 8f);
        Pen repeated = style.GetOrCreatePen(brush, 8f);

        Assert.Same(first, repeated);
        Assert.Same(brush, first.Brush);
        Assert.Equal(PenLineJoin.MiterOrBevel, first.LineJoin);
        Assert.Equal(3, (int)first.LineJoin);
        Assert.Equal(2f, first.MiterLimit);
        Assert.Equal(expectedMode, first.StrokeTransformMode);
        Assert.Equal(hairline, first.IsHairline);
        Assert.Equal(behavior == CanvasStrokeTransformBehavior.Fixed, first.IsFixed);
        Assert.Equal(hairline ? Pen.HairlineThickness : 8f, first.Thickness);
    }

    [Fact]
    public void BrushAndWidthRemainPartOfTheDistinctJoinCacheIdentity()
    {
        using var style = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.MiterOrBevel };
        var firstBrush = new SolidColorBrush(Vector4.One);
        var equalColorBrush = new SolidColorBrush(Vector4.One);

        Pen first = style.GetOrCreatePen(firstBrush, 4f);
        Pen newBrush = style.GetOrCreatePen(equalColorBrush, 4f);
        Pen newWidth = style.GetOrCreatePen(equalColorBrush, 6f);

        Assert.NotSame(first, newBrush);
        Assert.NotSame(newBrush, newWidth);
        Assert.Same(newWidth, style.GetOrCreatePen(equalColorBrush, 6f));
        Assert.Same(firstBrush, first.Brush);
        Assert.Same(equalColorBrush, newBrush.Brush);
        Assert.Equal(4f, first.Thickness);
        Assert.Equal(4f, newBrush.Thickness);
        Assert.Equal(6f, newWidth.Thickness);
        Assert.Equal(PenLineJoin.MiterOrBevel, first.LineJoin);
        Assert.Equal(PenLineJoin.MiterOrBevel, newBrush.LineJoin);
        Assert.Equal(PenLineJoin.MiterOrBevel, newWidth.LineJoin);
    }

    [Fact]
    public void StyleMutationReplacesCachedPenWithoutMutatingEarlierGenerations()
    {
        using var style = new CanvasStrokeStyle
        {
            LineJoin = CanvasLineJoin.MiterOrBevel,
            MiterLimit = 1f
        };
        var brush = new SolidColorBrush(Vector4.One);
        Pen first = style.GetOrCreatePen(brush, 8f);

        style.LineJoin = CanvasLineJoin.MiterOrBevel;
        style.MiterLimit = 1f;
        Assert.Same(first, style.GetOrCreatePen(brush, 8f));
        style.MiterLimit = 2f;
        Pen changedLimit = style.GetOrCreatePen(brush, 8f);
        style.LineJoin = CanvasLineJoin.Miter;
        Pen ordinaryMiter = style.GetOrCreatePen(brush, 8f);

        Assert.NotSame(first, changedLimit);
        Assert.NotSame(changedLimit, ordinaryMiter);
        Assert.Equal(PenLineJoin.MiterOrBevel, first.LineJoin);
        Assert.Equal(1f, first.MiterLimit);
        Assert.Equal(PenLineJoin.MiterOrBevel, changedLimit.LineJoin);
        Assert.Equal(2f, changedLimit.MiterLimit);
        Assert.Equal(PenLineJoin.Miter, ordinaryMiter.LineJoin);
        Assert.Equal(2f, ordinaryMiter.MiterLimit);
    }

    [Fact]
    public void InvalidJoinPreservesTheExistingStyleAndCachedPen()
    {
        using var style = new CanvasStrokeStyle
        {
            LineJoin = CanvasLineJoin.MiterOrBevel,
            MiterLimit = 2f
        };
        var brush = new SolidColorBrush(Vector4.One);
        Pen retained = style.GetOrCreatePen(brush, 8f);

        Assert.Throws<ArgumentOutOfRangeException>(() => style.LineJoin = (CanvasLineJoin)4);

        Assert.Equal(CanvasLineJoin.MiterOrBevel, style.LineJoin);
        Assert.Same(retained, style.GetOrCreatePen(brush, 8f));
        Assert.Equal(PenLineJoin.MiterOrBevel, retained.LineJoin);
        Assert.Equal(2f, retained.MiterLimit);
    }
}
