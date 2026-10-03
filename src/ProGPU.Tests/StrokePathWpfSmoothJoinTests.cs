using System.Numerics;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

// Metadata-transport controls, not original-source or rendered-pixel qualification.
public sealed class StrokePathWpfSmoothJoinTests
{
    [Theory]
    [InlineData(PenLineJoin.Miter, false, false)]
    [InlineData(PenLineJoin.Bevel, false, false)]
    [InlineData(PenLineJoin.Round, false, false)]
    [InlineData(PenLineJoin.Miter, true, false)]
    [InlineData(PenLineJoin.Bevel, true, false)]
    [InlineData(PenLineJoin.Round, true, false)]
    [InlineData(PenLineJoin.Miter, false, true)]
    [InlineData(PenLineJoin.Bevel, false, true)]
    [InlineData(PenLineJoin.Round, false, true)]
    [InlineData(PenLineJoin.Miter, true, true)]
    [InlineData(PenLineJoin.Bevel, true, true)]
    [InlineData(PenLineJoin.Round, true, true)]
    public void SolidAndDashedSourceSmoothCornersUseRoundWithoutChangingGenericWidening(
        PenLineJoin join, bool reversal, bool dashed)
    {
        PathGeometry smooth = OpenPath(reversal, true);
        PathGeometry ordinary = OpenPath(reversal, false);
        Pen pen = SourcePen(join);
        if (dashed) pen.DashArray = [1000, 1];
        Pen round = pen.WithBrush(pen.Brush);
        round.LineJoin = PenLineJoin.Round;
        Assert.True(StrokePathGeometry.TryCreateWidenedPath(smooth, pen, out var actual));
        Assert.True(StrokePathGeometry.TryCreateWidenedPath(ordinary, round, out var expected));
        AssertSameGeometry(expected, actual);
        // Literal witnesses distinguish the round fan from a clipped miter or
        // reversal square without deriving expected points from the join writer.
        Vector2 inside = reversal ? new(35, 32) : new(34.6f, 34.6f);
        Vector2 outside = reversal ? new(35.5f, 35.5f) : new(35.8f, 33.8f);
        AssertHit(smooth, pen, inside, true);
        AssertHit(smooth, pen, outside, false);
        if (join == PenLineJoin.Miter)
            AssertHit(ordinary, pen, outside, true);
        if (join == PenLineJoin.Bevel && !reversal)
            AssertHit(ordinary, pen, inside, false);

        // Legacy widening ignored this metadata, rather than suppressing joins.
        pen.UseWpfJoinSemantics = false;
        Assert.True(StrokePathGeometry.TryCreateWidenedPath(smooth, pen, out var genericSmooth));
        Assert.True(StrokePathGeometry.TryCreateWidenedPath(ordinary, pen, out var genericOrdinary));
        AssertSameGeometry(genericOrdinary, genericSmooth);
        Assert.True(smooth.Figures[0].Segments[1].IsSmoothJoin);
        Assert.False(ordinary.Figures[0].Segments[1].IsSmoothJoin);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void ClosedSeamRetainsFirstOutgoingSmoothFlagAcrossDelayedDashStart(
        int dashMode, bool explicitClosingLine)
    {
        PathGeometry smooth = Rectangle(true, explicitClosingLine);
        PathGeometry ordinary = Rectangle(false, explicitClosingLine);
        Pen pen = SourcePen(PenLineJoin.Miter);
        if (dashMode == 1) pen.DashArray = [1000, 1];
        // Perimeter 80: on [0,40), off [40,60), on [60,80]. This
        // joins the final run to a delayed first run at the figure's start.
        if (dashMode == 2) pen.DashArray = [5, 2.5];
        Assert.True(StrokePathGeometry.TryCreateWidenedPath(smooth, pen, out var widened));
        Assert.NotEmpty(widened.Figures);
        AssertHit(smooth, pen, new(9.4f, 9.4f), true);
        AssertHit(smooth, pen, new(8.2f, 10.2f), false);
        AssertHit(ordinary, pen, new(8.2f, 10.2f), true);
        pen.UseWpfJoinSemantics = false;
        Assert.True(StrokePathGeometry.TryCreateWidenedPath(smooth, pen, out var genericSmooth));
        Assert.True(StrokePathGeometry.TryCreateWidenedPath(ordinary, pen, out var genericOrdinary));
        AssertSameGeometry(genericOrdinary, genericSmooth);
    }

    [Fact]
    public void SourceSmoothFlagDoesNotBridgeAStrokeOrDashGap()
    {
        Pen pen = SourcePen(PenLineJoin.Miter);
        PathGeometry smooth = OpenPath(false, true);
        smooth.Figures[0].Segments[0].IsStroked = false;
        AssertHit(smooth, pen, new(34.6f, 34.6f), false);

        smooth = OpenPath(false, true);
        // The corner lies in the off interval [16,32), not in either run.
        pen.DashArray = [2, 2];
        AssertHit(smooth, pen, new(34.6f, 34.6f), false);
        Assert.True(StrokePathGeometry.TryCreateWidenedPath(smooth, pen, out var actual));
        Assert.True(StrokePathGeometry.TryCreateWidenedPath(OpenPath(false, false), pen, out var ordinary));
        AssertSameGeometry(ordinary, actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InvalidSourcePolicyRejectsBeforeEmptyGeometryOrInvalidPointOutputs(int defect)
    {
        Pen pen = SourcePen(PenLineJoin.Miter);
        if (defect == 0) { pen.StrokeTransformMode = PenStrokeTransformMode.Fixed; pen.Thickness = 0; }
        if (defect == 1) pen.Thickness = Pen.HairlineThickness;
        if (defect == 2) { pen.LineJoin = PenLineJoin.MiterOrBevel; pen.Thickness = 0; }
        var empty = new PathGeometry();
        var sentinel = new PathGeometry();
        PathGeometry output = sentinel;
        Assert.Throws<NotSupportedException>(() => StrokePathGeometry.TryCreateWidenedPath(empty, pen, out output));
        Assert.Same(sentinel, output);
        bool contains = true;
        Assert.Throws<NotSupportedException>(() => StrokePathGeometry.TryContains(
            empty, pen, new(float.NaN, 0), out contains));
        Assert.True(contains);
        Assert.Empty(empty.Figures);
    }

    private static Pen SourcePen(PenLineJoin join) => new(
        new SolidColorBrush(Vector4.One), 8, join, miterLimit: 1)
        { UseWpfJoinSemantics = true };

    private static PathGeometry OpenPath(bool reversal, bool smooth)
    {
        var figure = new PathFigure(new(12, 32)) { IsFilled = false };
        figure.Segments.Add(new LineSegment(new(32, 32)));
        figure.Segments.Add(new LineSegment(reversal ? new(12, 32) : new(32, 12), smooth));
        var path = new PathGeometry();
        path.Figures.Add(figure);
        return path;
    }

    private static PathGeometry Rectangle(bool smooth, bool explicitClosingLine)
    {
        var figure = new PathFigure(new(12, 12), isClosed: true) { IsFilled = false };
        figure.Segments.Add(new LineSegment(new(32, 12), smooth));
        figure.Segments.Add(new LineSegment(new(32, 32)));
        figure.Segments.Add(new LineSegment(new(12, 32)));
        if (explicitClosingLine) figure.Segments.Add(new LineSegment(figure.StartPoint));
        var path = new PathGeometry();
        path.Figures.Add(figure);
        return path;
    }

    private static void AssertHit(PathGeometry path, Pen pen, Vector2 point, bool expected)
    {
        Assert.True(StrokePathGeometry.TryContains(path, pen, point, out bool contains));
        Assert.Equal(expected, contains);
    }

    private static void AssertSameGeometry(PathGeometry expected, PathGeometry actual)
    {
        Assert.Equal(expected.FillRule, actual.FillRule);
        Assert.Equal(expected.Figures.Count, actual.Figures.Count);
        for (int f = 0; f < expected.Figures.Count; f++)
        {
            PathFigure a = expected.Figures[f], b = actual.Figures[f];
            Assert.Equal(a.StartPoint, b.StartPoint);
            Assert.Equal(a.IsClosed, b.IsClosed);
            Assert.Equal(a.IsFilled, b.IsFilled);
            Assert.Equal(a.Segments.Count, b.Segments.Count);
            for (int s = 0; s < a.Segments.Count; s++)
                Assert.Equal(((LineSegment)a.Segments[s]).Point, ((LineSegment)b.Segments[s]).Point);
        }
    }
}
