using System;
using System.Numerics;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class PenSnapshotTests
{
    [Fact]
    public void MaterialReplacementPreservesStrokeValuesAndIndependentDashOwnership()
    {
        double[] intervals = [2, 1, 3];
        var original = new Pen(new SolidColorBrush(Vector4.One), 3, PenLineJoin.Bevel,
            7, PenLineCap.Square, PenLineCap.Triangle, PenLineCap.Round, intervals, -0.5,
            PenStrokeTransformMode.Fixed);
        var material = new SolidColorBrush(new Vector4(0, 1, 0, 1));
        var copy = original.WithBrush(material);
        intervals[0] = 99;
        var exposed = copy.DashArray!;
        exposed[1] = 99;
        Assert.Equal(new double[] { 2, 1, 3 }, original.DashArray);
        Assert.Equal(new double[] { 2, 1, 3 }, copy.DashArray);
        Assert.Same(material, copy.Brush);
        Assert.Equal(original.Thickness, copy.Thickness);
        Assert.Equal(original.LineJoin, copy.LineJoin);
        Assert.Equal(original.MiterLimit, copy.MiterLimit);
        Assert.Equal(original.StartLineCap, copy.StartLineCap);
        Assert.Equal(original.EndLineCap, copy.EndLineCap);
        Assert.Equal(original.DashCap, copy.DashCap);
        Assert.Equal(original.StrokeTransformMode, copy.StrokeTransformMode);
        Assert.Equal(original.DashOffset, copy.DashOffset);
        original.DashArray = [4, 5];
        Assert.Equal(new double[] { 2, 1, 3 }, copy.DashArray);
        copy.DashArray = [6, 7];
        Assert.Equal(new double[] { 4, 5 }, original.DashArray);
        Assert.Throws<ArgumentNullException>(() => original.WithBrush(null!));
    }
}
