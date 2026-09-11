using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class DashedPathMetadataTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FigureCapsOverridePenOnlyAtReachedEndpoints(bool endpointsInGaps)
    {
        var source = new PathGeometry();
        var figure = new PathFigure(Vector2.Zero)
        {
            StrokeStartLineCap = PenLineCap.Triangle,
            StrokeEndLineCap = PenLineCap.Flat
        };
        figure.Segments.Add(new LineSegment(new Vector2(endpointsInGaps ? 6 : 10, 0)));
        source.Figures.Add(figure);
        var pen = new Pen(new SolidColorBrush(Vector4.One), 1,
            startLineCap: PenLineCap.Square, endLineCap: PenLineCap.Square,
            dashCap: PenLineCap.Round, dashArray: [2, 2],
            dashOffset: endpointsInGaps ? 2 : 0);
        var cache = RenderCommandGeometryCache.ForStrokePath(source);

        Assert.True(cache.TryGetDashedStrokePath(pen, out var dashed, out var coveragePen));
        Assert.Equal(PenLineCap.Round, coveragePen.StartLineCap);
        Assert.Equal(PenLineCap.Round, coveragePen.EndLineCap);
        Assert.Equal(endpointsInGaps ? (PenLineCap?)null : PenLineCap.Triangle,
            dashed.Figures[0].StrokeStartLineCap);
        Assert.Equal(endpointsInGaps ? (PenLineCap?)null : PenLineCap.Flat,
            dashed.Figures[^1].StrokeEndLineCap);
        Assert.True(cache.TryGetDashedStrokePath(pen, out var reused, out var reusedPen));
        Assert.Same(dashed, reused);
        Assert.Same(coveragePen, reusedPen);
        Assert.Equal(PenLineCap.Triangle, figure.StrokeStartLineCap);
        Assert.Equal(PenLineCap.Flat, figure.StrokeEndLineCap);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InteriorLineJoinPreservesSourceSmoothFlag(bool smooth)
    {
        var source = new PathGeometry();
        var figure = new PathFigure(Vector2.Zero);
        figure.Segments.Add(new LineSegment(new Vector2(10, 0)));
        figure.Segments.Add(new LineSegment(new Vector2(10, 10), isSmoothJoin: smooth));
        source.Figures.Add(figure);
        var pen = new Pen(new SolidColorBrush(Vector4.One), 1, dashArray: [30, 2]);

        Assert.True(Compositor.TryCreateDashedStrokePath(source, pen, out var dashed));
        var run = Assert.Single(dashed.Figures);
        Assert.Equal(2, run.Segments.Count);
        Assert.False(run.Segments[0].IsSmoothJoin);
        Assert.Equal(smooth, run.Segments[1].IsSmoothJoin);
        Assert.NotSame(figure.Segments[1], run.Segments[1]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClosedSeamRestoresSourceJoinAfterDashRunMerge(bool wholeContour, bool smooth)
    {
        // Matches native curve_dashes_match_managed_reference_contracts.
        var source = new PathGeometry();
        var figure = new PathFigure(Vector2.Zero, isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(10, 0), isSmoothJoin: smooth));
        figure.Segments.Add(new LineSegment(new Vector2(10, 10)));
        figure.Segments.Add(new LineSegment(new Vector2(0, 10)));
        source.Figures.Add(figure);
        var pen = new Pen(new SolidColorBrush(Vector4.One), 1,
            dashArray: wholeContour ? [50, 2] : [5, 2]);

        Assert.True(Compositor.TryCreateDashedStrokePath(source, pen, out var dashed));
        Assert.Equal(wholeContour ? 1 : 5, dashed.Figures.Count);
        var run = dashed.Figures[^1];
        Assert.Equal(wholeContour, run.IsClosed);
        Assert.Equal(smooth, run.Segments[wholeContour ? 0 : 1].IsSmoothJoin);
        Assert.Null(run.StrokeStartLineCap);
        Assert.Null(run.StrokeEndLineCap);
        Assert.Equal(smooth, figure.Segments[0].IsSmoothJoin);
    }
}
