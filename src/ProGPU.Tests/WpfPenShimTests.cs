using Xunit;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfDashStyle = System.Windows.Media.DashStyle;
using WpfPen = System.Windows.Media.Pen;
using WpfPenLineCap = System.Windows.Media.PenLineCap;
using WpfPenLineJoin = System.Windows.Media.PenLineJoin;
using VectorPenLineCap = ProGPU.Vector.PenLineCap;
using VectorPenLineJoin = ProGPU.Vector.PenLineJoin;

namespace ProGPU.Tests;

public sealed class WpfPenShimTests
{
    [Fact]
    public void PresentationCorePenToNativePreservesDashAndLineMetadata()
    {
        var pen = new WpfPen(WpfBrushes.Black, 2)
        {
            DashStyle = new WpfDashStyle(new[] { 1.0, 2.0 }, 0.5),
            StartLineCap = WpfPenLineCap.Square,
            EndLineCap = WpfPenLineCap.Triangle,
            DashCap = WpfPenLineCap.Round,
            LineJoin = WpfPenLineJoin.Round,
            MiterLimit = 3.5
        };

        var nativePen = pen.ToNative();
        Assert.NotNull(nativePen);

        Assert.Equal(2, nativePen!.Thickness);
        Assert.True(nativePen.HasDashPattern);
        Assert.Equal(new[] { 2.0, 4.0 }, nativePen.DashArray);
        Assert.Equal(0.5, nativePen.DashOffset);
        Assert.Equal(VectorPenLineCap.Square, nativePen.StartLineCap);
        Assert.Equal(VectorPenLineCap.Triangle, nativePen.EndLineCap);
        Assert.Equal(VectorPenLineCap.Round, nativePen.DashCap);
        Assert.Equal(VectorPenLineJoin.Round, nativePen.LineJoin);
        Assert.Equal(3.5f, nativePen.MiterLimit);
    }

    [Fact]
    public void PresentationCorePenToNativeIgnoresInvalidDashPattern()
    {
        var pen = new WpfPen(WpfBrushes.Black, 2)
        {
            DashStyle = new WpfDashStyle(new[] { 1.0, -1.0 }, 0)
        };

        var nativePen = pen.ToNative();
        Assert.NotNull(nativePen);

        Assert.False(nativePen!.HasDashPattern);
        Assert.Null(nativePen.DashArray);
    }
}
