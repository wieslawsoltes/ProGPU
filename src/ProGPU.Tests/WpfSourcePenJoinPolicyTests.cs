using Xunit;
using WpfMedia = global::System.Windows.Media;

namespace ProGPU.Tests;

public sealed class WpfSourcePenJoinPolicyTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    public void ShimOverloadsRetainFullSourcePolicyAndStrokeSnapshot(int join, int overload)
    {
        var source = new WpfMedia.Pen(WpfMedia.Brushes.Black, 2)
        {
            LineJoin = (WpfMedia.PenLineJoin)join,
            MiterLimit = 1,
            StartLineCap = WpfMedia.PenLineCap.Square,
            EndLineCap = WpfMedia.PenLineCap.Triangle,
            DashCap = WpfMedia.PenLineCap.Round,
            DashStyle = new WpfMedia.DashStyle([2, 3], 0.5)
        };
        var bounds = new System.Windows.Rect(5, 7, 20, 30);
        var native = overload switch
        {
            0 => source.ToNative(),
            1 => source.ToNative(2),
            2 => source.ToNative(bounds),
            _ => source.ToNative(bounds, 2)
        };

        Assert.NotNull(native);
        Assert.True(native.UseWpfJoinSemantics);
        Assert.False(native.ClipMiterAtLimit); // Full WPF policy is not the independent clip-only flag.
        Assert.Equal((Vector.PenLineJoin)join, native.LineJoin);
        Assert.Equal(Vector.PenStrokeTransformMode.Normal, native.StrokeTransformMode);
        Assert.False(native.IsHairline);
        Assert.Equal(overload is 1 or 3 ? 4f : 2f, native.Thickness);
        Assert.Equal(1f, native.MiterLimit);
        Assert.Equal(Vector.PenLineCap.Square, native.StartLineCap);
        Assert.Equal(Vector.PenLineCap.Triangle, native.EndLineCap);
        Assert.Equal(Vector.PenLineCap.Round, native.DashCap);
        Assert.Equal(new double[] { 2, 3 }, native.DashArray);
        Assert.Equal(0.5, native.DashOffset);

        source.LineJoin = WpfMedia.PenLineJoin.Round;
        source.DashStyle.Dashes = [9];
        source.Thickness = 9;
        Assert.Equal((Vector.PenLineJoin)join, native.LineJoin);
        Assert.Equal(new double[] { 2, 3 }, native.DashArray);
        Assert.Equal(overload is 1 or 3 ? 4f : 2f, native.Thickness);
    }
}
