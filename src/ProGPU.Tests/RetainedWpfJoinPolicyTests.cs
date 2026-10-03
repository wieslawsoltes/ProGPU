using System;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

// Authored controls; execution and original-source comparison remain separate.
public sealed class RetainedWpfJoinPolicyTests
{
    [Theory]
    [InlineData(PenLineJoin.Miter)]
    [InlineData(PenLineJoin.Bevel)]
    [InlineData(PenLineJoin.Round)]
    public void SourceSmoothJoinSelectsRoundAndRetainsReversal(PenLineJoin join)
    {
        var pen = SourcePen(join);
        var actual = new StrokeJoinTriangle[StrokeJoinGeometry.MaxTrianglesPerJoin];
        foreach (bool reversal in new[] { false, true })
        {
            var next = reversal ? Vector2.Zero : Vector2.One;
            var expected = StrokeJoinGeometry.CreateWpfLineJoin(PenLineJoin.Round,
                2, 1, Vector2.Zero, Vector2.UnitX, next);
            int count = StrokeJoinGeometry.WriteLineJoin(actual, pen, 2,
                Vector2.Zero, Vector2.UnitX, next, isSmoothJoin: true);
            Assert.Equal(reversal ? 8 : 4, count);
            Assert.Equal(expected, actual.Take(count));
            Assert.Equal(expected, StrokeJoinGeometry.CreateWpfLineJoin(join,
                2, 1, Vector2.Zero, Vector2.UnitX, next, isSmoothJoin: true));
            Assert.Equal(count, StrokeJoinGeometry.WriteWpfLineJoin(actual, join,
                2, 1, Vector2.Zero, Vector2.UnitX, next, isSmoothJoin: true));
            Assert.Equal(expected, actual.Take(count));

            pen.UseWpfJoinSemantics = false;
            Assert.Equal(0, StrokeJoinGeometry.WriteLineJoin(actual, pen, 2,
                Vector2.Zero, Vector2.UnitX, next, isSmoothJoin: true));
            Assert.Empty(StrokeJoinGeometry.CreateLineJoin(join, 2, 1,
                Vector2.Zero, Vector2.UnitX, next, isSmoothJoin: true));
            pen.UseWpfJoinSemantics = true;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void IncompatiblePolicyRejectsBeforeWriting(int defect)
    {
        var pen = SourcePen(PenLineJoin.Miter);
        if (defect == 0) pen.LineJoin = PenLineJoin.MiterOrBevel;
        if (defect == 1) pen.StrokeTransformMode = PenStrokeTransformMode.Fixed;
        if (defect == 2) pen.Thickness = Pen.HairlineThickness;
        var sentinel = new StrokeJoinTriangle(new(91, 92), new(93, 94), new(95, 96));
        var triangles = Enumerable.Repeat(sentinel, StrokeJoinGeometry.MaxTrianglesPerJoin).ToArray();
        Assert.Throws<NotSupportedException>(() => StrokeJoinGeometry.WriteLineJoin(
            triangles, pen, 2, Vector2.Zero, Vector2.UnitX, Vector2.Zero, isSmoothJoin: true));
        Assert.All(triangles, item => Assert.Equal(sentinel, item));
    }

    [Theory]
    [InlineData(PenLineJoin.Miter, false, false)]
    [InlineData(PenLineJoin.Bevel, false, false)]
    [InlineData(PenLineJoin.Round, false, false)]
    [InlineData(PenLineJoin.Miter, true, false)]
    [InlineData(PenLineJoin.Bevel, true, false)]
    [InlineData(PenLineJoin.Round, true, false)]
    [InlineData(PenLineJoin.Miter, false, true)]
    [InlineData(PenLineJoin.Miter, true, true)]
    public void ActualManagedPaintRetainsSourceReversalCoverage(PenLineJoin join, bool smooth, bool dashed)
    {
        using var window = new HeadlessWindow(64, 64);
        window.Compositor.ClearColor = new(0, 0, 0, 1);
        var pen = SourcePen(join);
        pen.Thickness = 8;
        if (dashed) pen.SetDashPattern([1000, 1]);
        var path = new PathGeometry();
        var figure = new PathFigure(new(8.25f, 32.25f)) { IsFilled = false };
        figure.Segments.Add(new LineSegment(new(32.25f, 32.25f), isSmoothJoin: smooth));
        figure.Segments.Add(new LineSegment(new(8.25f, 32.25f), isSmoothJoin: smooth));
        path.Figures.Add(figure);
        using var actual = Picture(path, pen);
        bool round = smooth || join == PenLineJoin.Round;
        using var independent = Picture(ExpectedOutline(round), null);
        window.Content = new PictureVisual(actual);
        window.Render();
        var cold = window.ReadPixels();
        window.Render();
        Assert.Equal(cold, window.ReadPixels());
        window.Content = new PictureVisual(independent);
        window.Render();
        Assert.Equal(window.ReadPixels(), cold);
        // Explicit overhang and far-corner witnesses supplement the full image.
        Assert.Equal((byte)255, cold[(32 * 64 + 35) * 4]);
        Assert.Equal(round ? (byte)0 : (byte)255, cold[(28 * 64 + 35) * 4]);
        window.Content = null;
    }

    private static Pen SourcePen(PenLineJoin join) => new(
        new SolidColorBrush(new(1, 0, 0, 1)), 2, join, 1)
        { UseWpfJoinSemantics = true };

    private static GpuPicture Picture(PathGeometry path, Pen? pen) => new(
        [new RenderCommand { Type = RenderCommandType.DrawPath, Path = path, Pen = pen,
            Brush = pen == null ? new SolidColorBrush(new(1, 0, 0, 1)) : null,
            Transform = Matrix4x4.Identity, IsEdgeAliased = true, IsPenThicknessLocal = true,
            GeometryCache = RenderCommandGeometryCache.ForPath(path) }], [], [], [], []);

    private static PathGeometry ExpectedOutline(bool round)
    {
        // Independent literal radius-four fan boundary, not a product stroke,
        // bounds helper or another invocation of the join writer under test.
        Vector2[] points = round
            ? [new(8.25f, 28.25f), new(32.25f, 28.25f), new(33.780734f, 28.554482f),
               new(35.078427f, 29.421574f), new(35.94552f, 30.719267f), new(36.25f, 32.25f),
               new(35.94552f, 33.780735f), new(35.078427f, 35.078427f),
               new(33.780735f, 35.94552f), new(32.25f, 36.25f), new(8.25f, 36.25f)]
            : [new(8.25f, 28.25f), new(36.25f, 28.25f), new(36.25f, 36.25f), new(8.25f, 36.25f)];
        var figure = new PathFigure(points[0], isClosed: true) { IsFilled = true };
        for (int i = 1; i < points.Length; ++i) figure.Segments.Add(new LineSegment(points[i]));
        var path = new PathGeometry();
        path.Figures.Add(figure);
        return path;
    }

    private sealed class PictureVisual : FrameworkElement
    {
        private readonly GpuPicture _picture;
        public PictureVisual(GpuPicture picture) { _picture = picture; Width = 64; Height = 64; }
        public override void OnRender(DrawingContext context) => context.DrawPicture(_picture);
    }
}
