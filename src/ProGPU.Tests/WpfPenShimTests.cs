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
    public void PortablePenStateRetainsTileBrushIdentityAndOwnsDashSnapshot()
    {
        var brush = new DeferredTileBrush();
        var dash = new WpfDashStyle([1.0, 2.0], 0.5);
        var pen = new WpfPen(brush, 2)
        {
            DashStyle = dash,
            StartLineCap = WpfPenLineCap.Square,
            EndLineCap = WpfPenLineCap.Triangle,
            DashCap = WpfPenLineCap.Round,
            LineJoin = WpfPenLineJoin.Bevel,
            MiterLimit = 3.5
        };
        Assert.True(((ProGPU.Wpf.Interop.IPortablePenStateSource)pen).TryGetPortablePenState(out var state));
        Assert.Same(brush, state.Brush);
        Assert.Equal(2, state.Thickness);
        Assert.Equal(ProGPU.Wpf.Interop.PortablePenLineCap.Square, state.StartLineCap);
        Assert.Equal(ProGPU.Wpf.Interop.PortablePenLineCap.Triangle, state.EndLineCap);
        Assert.Equal(ProGPU.Wpf.Interop.PortablePenLineCap.Round, state.DashCap);
        Assert.Equal(ProGPU.Wpf.Interop.PortablePenLineJoin.Bevel, state.LineJoin);
        Assert.Equal(3.5, state.MiterLimit);
        Assert.Equal(0.5, state.DashOffset);
        dash.Dashes = [9.0];
        Assert.Equal(new[] { 1.0, 2.0 }, state.Dashes.ToArray());
        pen.Brush = null;
        Assert.True(((ProGPU.Wpf.Interop.IPortablePenStateSource)pen).TryGetPortablePenState(out var empty));
        Assert.Null(empty.Brush);
        Assert.Same(brush, state.Brush);
    }

    private sealed class DeferredTileBrush : System.Windows.Media.Brush,
        ProGPU.Wpf.Interop.IPortableTileBrushSource
    {
        public bool TryGetPortableTileBrush(out ProGPU.Wpf.Interop.PortableTileBrush brush) =>
            throw new System.InvalidOperationException("Pen snapshot must not eagerly resolve tile content.");
    }

    [Fact]
    public void PresentationCorePenDefaultConstructorUsesWpfThickness()
    {
        var pen = new WpfPen
        {
            Brush = WpfBrushes.Black
        };

        var nativePen = pen.ToNative();
        Assert.NotNull(nativePen);

        Assert.Equal(1, nativePen!.Thickness);
    }

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
        Assert.Equal(new[] { 1.0, 2.0 }, nativePen.DashArray);
        Assert.Equal(0.5, nativePen.DashOffset);
        Assert.Equal(VectorPenLineCap.Square, nativePen.StartLineCap);
        Assert.Equal(VectorPenLineCap.Triangle, nativePen.EndLineCap);
        Assert.Equal(VectorPenLineCap.Round, nativePen.DashCap);
        Assert.Equal(VectorPenLineJoin.Round, nativePen.LineJoin);
        Assert.Equal(3.5f, nativePen.MiterLimit);
    }

    [Fact]
    public void PresentationCorePenToNativeKeepsDashUnitsRelativeWhenThicknessIsScaled()
    {
        var pen = new WpfPen(WpfBrushes.Black, 4)
        {
            DashStyle = new WpfDashStyle(new[] { 1.0, 1.5 }, 0.25)
        };

        var nativePen = pen.ToNative(3f);
        Assert.NotNull(nativePen);

        Assert.Equal(12, nativePen!.Thickness);
        Assert.True(nativePen.HasDashPattern);
        Assert.Equal(new[] { 1.0, 1.5 }, nativePen.DashArray);
        Assert.Equal(0.25, nativePen.DashOffset);
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
