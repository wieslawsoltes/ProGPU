using System;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class MiterOrBevelStrokeTests
{
    [Theory]
    [InlineData(1f, 1)]
    [InlineData(1.4f, 1)]
    [InlineData(1.5f, 2)]
    [InlineData(2f, 2)]
    public void ExplicitJoinKeepsThresholdGeometryInBothWriters(float limit, int expectedCount)
    {
        // A right angle has miter ratio sqrt(2). The inputs bracket that
        // threshold without assuming exact sqrt or epsilon equality.
        var normal = StrokeJoinGeometry.CreateLineJoin(PenLineJoin.MiterOrBevel,
            2, limit, Vector2.Zero, Vector2.UnitX, Vector2.One);
        var wpf = StrokeJoinGeometry.CreateWpfLineJoin(PenLineJoin.MiterOrBevel,
            2, limit, Vector2.Zero, Vector2.UnitX, Vector2.One);
        Assert.Equal(expectedCount, normal.Length);
        AssertTrianglesEqual(normal, wpf);
        var expected = StrokeJoinGeometry.CreateLineJoin(expectedCount == 1
            ? PenLineJoin.Bevel : PenLineJoin.Miter, 2, limit,
            Vector2.Zero, Vector2.UnitX, Vector2.One);
        AssertTrianglesEqual(expected, normal);
        Assert.Contains(normal, triangle => triangle.P0 == new Vector2(1, -1) ||
            triangle.P1 == new Vector2(1, -1) || triangle.P2 == new Vector2(1, -1));
        if (expectedCount == 2)
            Assert.Contains(normal, triangle => triangle.P0 == new Vector2(2, -1) ||
                triangle.P1 == new Vector2(2, -1) || triangle.P2 == new Vector2(2, -1));
    }

    [Fact]
    public void ExplicitReversalDoesNotAcquireLegacyWpfSquare()
    {
        Assert.Empty(StrokeJoinGeometry.CreateLineJoin(PenLineJoin.MiterOrBevel,
            2, 1, Vector2.UnitY, Vector2.Zero, Vector2.UnitY));
        Assert.Empty(StrokeJoinGeometry.CreateWpfLineJoin(PenLineJoin.MiterOrBevel,
            2, 1, Vector2.UnitY, Vector2.Zero, Vector2.UnitY));
        Assert.Equal(3, StrokeJoinGeometry.CreateWpfLineJoin(PenLineJoin.Miter,
            2, 1, Vector2.UnitY, Vector2.Zero, Vector2.UnitY).Length);
        Assert.Equal(3, StrokeJoinGeometry.CreateWpfLineJoin(PenLineJoin.Bevel,
            2, 1, Vector2.UnitY, Vector2.Zero, Vector2.UnitY).Length);
        Assert.Equal(3, StrokeJoinGeometry.CreateWpfLineJoin(PenLineJoin.Miter,
            2, 1, Vector2.Zero, Vector2.UnitX, Vector2.One).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ClosedSeamRetainsBevelCoverageAndLiteralBounds(int firstCorner)
    {
        var path = Rectangle(firstCorner);
        var pen = CreatePen(PenLineJoin.MiterOrBevel, 1, 0);
        Assert.True(StrokeCoverageGeometry.TryPrepareLinearPath(path, pen,
            out var retained, out var coverage, out var bounds));
        Assert.Equal(new Rect(6, 6, 28, 28), bounds);
        Assert.Equal(PenLineJoin.MiterOrBevel, coverage.LineJoin);
        Assert.True(Assert.Single(retained.Figures).IsClosed);
        Assert.True(StrokePathGeometry.TryContains(path, pen, new Vector2(31, 9), out bool inside));
        Assert.True(inside);
        Assert.True(StrokePathGeometry.TryContains(path, pen, new Vector2(33, 7), out bool cutOff));
        Assert.False(cutOff);
        pen.MiterLimit = 2;
        Assert.True(StrokePathGeometry.TryContains(path, pen, new Vector2(33, 7), out bool tip));
        Assert.True(tip);
    }

    [Fact]
    public void DashCacheAndMaterialSnapshotsRetainDistinctJoinGeneration()
    {
        var pen = CreatePen(PenLineJoin.MiterOrBevel, 1, 0);
        pen.DashArray = [100, 1];
        var copy = pen.WithBrush(new SolidColorBrush(new Vector4(0, 1, 0, 1)));
        var cache = RenderCommandGeometryCache.ForStrokePath(Rectangle(0));
        Assert.True(cache.TryGetDashedStrokePath(pen, out var first, out var firstPen));
        Assert.True(cache.TryGetDashedStrokePath(pen, out var warm, out var warmPen));
        Assert.Same(first, warm);
        Assert.Same(firstPen, warmPen);
        Assert.Equal(PenLineJoin.MiterOrBevel, firstPen.LineJoin);
        pen.LineJoin = PenLineJoin.Miter;
        Assert.True(cache.TryGetDashedStrokePath(pen, out var ordinary, out var ordinaryPen));
        Assert.Same(first, ordinary); // Dash positions do not depend on join paint.
        Assert.NotSame(firstPen, ordinaryPen);
        Assert.Equal(PenLineJoin.Miter, ordinaryPen.LineJoin);
        Assert.Equal(PenLineJoin.MiterOrBevel, firstPen.LineJoin);
        Assert.Equal(PenLineJoin.MiterOrBevel, copy.LineJoin);
        pen.DashArray = [2, 2];
        Assert.Equal(new double[] { 100, 1 }, copy.DashArray);
        var recorder = new GpuPictureRecorder();
        recorder.BeginRecording(new Rect(0, 0, 64, 64)).DrawPath(null, copy, Rectangle(0));
        using var picture = recorder.EndRecording();
        using var clone = picture.Clone();
        Assert.True(picture.SharesRetainedCommandStorageWith(clone));
        Assert.Equal(PenLineJoin.MiterOrBevel, clone.GetCommand(0).Pen!.LineJoin);
    }

    [Fact]
    public void InvalidFourRejectsBeforeJoinDestinationWrites()
    {
        var sentinel = new StrokeJoinTriangle(new(91, 92), new(93, 94), new(95, 96));
        var triangles = Enumerable.Repeat(sentinel, StrokeJoinGeometry.MaxTrianglesPerJoin).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => StrokeJoinGeometry.WriteLineJoin(
            triangles, (PenLineJoin)4, 0, 1, Vector2.Zero, Vector2.Zero, Vector2.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => StrokeJoinGeometry.WriteWpfLineJoin(
            triangles, (PenLineJoin)4, 2, 1, Vector2.Zero, Vector2.UnitX, Vector2.One));
        Assert.All(triangles, triangle => Assert.Equal(sentinel, triangle));
        var invalid = CreatePen((PenLineJoin)4, 1, 0);
        Assert.False(StrokeCoverageGeometry.TryPrepareLinearPath(Rectangle(0), invalid,
            out var path, out var pen, out var bounds));
        Assert.Null(path);
        Assert.Null(pen);
        Assert.Equal(default, bounds);
    }

    [Theory]
    [InlineData(0, 1f)]
    [InlineData(0, 2f)]
    [InlineData(1, 1f)]
    [InlineData(1, 2f)]
    [InlineData(2, 1f)]
    [InlineData(2, 2f)]
    public void ActualNormalFixedHairlineReplayMatchesSelectedJoinEveryPixel(int mode, float limit)
    {
        // This compares the actual retained compositor routes, including cold
        // and warm encodings; it is not a Microsoft raster equivalence claim.
        using var window = new HeadlessWindow(64, 64);
        using var actual = Record(PenLineJoin.MiterOrBevel, limit, mode);
        using var expected = Record(limit == 1 ? PenLineJoin.Bevel : PenLineJoin.Miter, limit, mode);
        Assert.True(GpuPictureBounds.TryGetBounds(actual, out var actualBounds));
        Assert.True(GpuPictureBounds.TryGetBounds(expected, out var expectedBounds));
        Assert.Equal(expectedBounds, actualBounds);
        window.Content = new PictureVisual(actual);
        window.Render();
        byte[] cold = window.ReadPixels();
        float radius = mode == 0 ? 6f : mode == 1 ? 4f : .5f;
        var corner = new Vector2(45, 15);
        Assert.True(window.Compositor.TryHitTestPoint(corner + new Vector2(radius * .25f, -radius * .25f), out _));
        Assert.Equal(limit == 2, window.Compositor.TryHitTestPoint(
            corner + new Vector2(radius * .75f, -radius * .75f), out _));
        window.Render();
        Assert.Equal(cold, window.ReadPixels());
        window.Content = new PictureVisual(expected);
        window.Render();
        Assert.Equal(window.ReadPixels(), cold);
        Assert.True(Enumerable.Range(0, cold.Length / 4).Any(pixel =>
            cold[pixel * 4] != 0 && cold[pixel * 4 + 1] == 0 && cold[pixel * 4 + 2] == 0));
        window.Content = null;
    }

    private static GpuPicture Record(PenLineJoin join, float limit, int mode)
    {
        var recorder = new GpuPictureRecorder();
        recorder.BeginRecording(new Rect(0, 0, 64, 64)).DrawPath(null, CreatePen(join, limit, mode),
            Rectangle(0), Matrix4x4.CreateScale(1.5f, 1.5f, 1));
        return recorder.EndRecording();
    }

    private static Pen CreatePen(PenLineJoin join, float limit, int mode) => new(
        new SolidColorBrush(new Vector4(1, 0, 0, 1)), mode == 2 ? Pen.HairlineThickness : 8,
        join, limit, strokeTransformMode: mode == 0 ? PenStrokeTransformMode.Normal : PenStrokeTransformMode.Fixed);

    private static PathGeometry Rectangle(int firstCorner)
    {
        Vector2[] corners = [new(10, 10), new(30, 10), new(30, 30), new(10, 30)];
        var path = new PathGeometry();
        var figure = new PathFigure(corners[firstCorner], isClosed: true) { IsFilled = false };
        for (int i = 1; i < 4; ++i) figure.Segments.Add(new LineSegment(corners[(firstCorner + i) % 4]));
        path.Figures.Add(figure);
        return path;
    }

    private static void AssertTrianglesEqual(StrokeJoinTriangle[] expected, StrokeJoinTriangle[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; ++i) Assert.Equal(expected[i], actual[i]);
    }

    private sealed class PictureVisual : FrameworkElement
    {
        private readonly GpuPicture _picture;

        public PictureVisual(GpuPicture picture)
        {
            _picture = picture;
            Width = 64;
            Height = 64;
        }

        public override void OnRender(DrawingContext context) => context.DrawPicture(_picture);
    }
}
