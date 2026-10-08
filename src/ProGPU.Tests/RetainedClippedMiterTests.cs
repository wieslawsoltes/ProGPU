using System;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class RetainedClippedMiterTests
{
    [Theory]
    [InlineData(1f, 3)]
    [InlineData(1.4f, 3)]
    [InlineData(1.5f, 2)]
    public void ClippingHasItsOwnThresholdButNotWpfReversal(float limit, int count)
    {
        var pen = CreatePen(0, limit);
        var triangles = new StrokeJoinTriangle[StrokeJoinGeometry.MaxTrianglesPerJoin];
        Assert.Equal(count, StrokeJoinGeometry.WriteLineJoin(triangles, pen, 2,
            Vector2.Zero, Vector2.UnitX, Vector2.One));
        var wpf = StrokeJoinGeometry.CreateWpfLineJoin(PenLineJoin.Miter, 2, limit,
            Vector2.Zero, Vector2.UnitX, Vector2.One);
        Assert.Equal(wpf, triangles.Take(count));
        // The clipped polygon admits a point past the bevel, but the full
        // square's corner stays out below sqrt(2). No production bounds oracle.
        Assert.True(Contains(triangles.AsSpan(0, count), new Vector2(1.6f, -.6f)));
        Assert.Equal(count == 2, Contains(triangles.AsSpan(0, count), new Vector2(1.999f, -.999f)));
        Assert.Equal(0, StrokeJoinGeometry.WriteLineJoin(triangles, pen, 2,
            Vector2.UnitY, Vector2.Zero, Vector2.UnitY));
        Assert.Equal(3, StrokeJoinGeometry.CreateWpfLineJoin(PenLineJoin.Miter, 2, 1,
            Vector2.UnitY, Vector2.Zero, Vector2.UnitY).Length);
        pen.ClipMiterAtLimit = false;
        Assert.Equal(count == 2 ? 2 : 1, StrokeJoinGeometry.WriteLineJoin(triangles, pen, 2,
            Vector2.Zero, Vector2.UnitX, Vector2.One));
        pen.LineJoin = PenLineJoin.MiterOrBevel;
        pen.ClipMiterAtLimit = true;
        Assert.Equal(count == 2 ? 2 : 1, StrokeJoinGeometry.WriteLineJoin(triangles, pen, 2,
            Vector2.Zero, Vector2.UnitX, Vector2.One));
    }

    [Fact]
    public void InvalidJoinStillRejectsBeforeWritingWithClippingIntent()
    {
        var pen = CreatePen(0, 1);
        pen.LineJoin = (PenLineJoin)4;
        var sentinel = new StrokeJoinTriangle(new(91, 92), new(93, 94), new(95, 96));
        var triangles = Enumerable.Repeat(sentinel, StrokeJoinGeometry.MaxTrianglesPerJoin).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => StrokeJoinGeometry.WriteLineJoin(
            triangles, pen, 0, Vector2.Zero, Vector2.Zero, Vector2.Zero));
        Assert.All(triangles, item => Assert.Equal(sentinel, item));
    }

    [Theory]
    [InlineData(0, 1f)]
    [InlineData(0, 2f)]
    [InlineData(1, 1f)]
    [InlineData(1, 2f)]
    [InlineData(2, 1f)]
    [InlineData(2, 2f)]
    public void ActualPaintAndQueryUseTheSameClippedCoverage(int mode, float limit)
    {
        using var window = new HeadlessWindow(64, 64);
        window.Compositor.ClearColor = new Vector4(0, 0, 0, 1);
        var pen = CreatePen(mode, limit);
        using var actual = Picture(pen, Rectangle());
        float radius = mode == 2 ? .5f : 4;
        using var independent = Picture(null, Ring(radius, limit), new SolidColorBrush(new Vector4(1, 0, 0, 1)));
        if (mode == 0)
        {
            Assert.True(GpuPictureBounds.TryGetBounds(actual, out var actualBounds));
            Assert.Equal(new Rect(6.25f, 6.25f, 48, 48), actualBounds);
        }
        window.Content = new PictureVisual(actual);
        window.Render();
        byte[] cold = window.ReadPixels();
        var corner = new Vector2(50.25f, 10.25f);
        Assert.True(window.Compositor.TryHitTestPoint(corner + new Vector2(radius * .6f, -radius * .6f), out _));
        Assert.Equal(limit == 2, window.Compositor.TryHitTestPoint(
            corner + new Vector2(radius * .8f, -radius * .8f), out _));
        Assert.False(window.Compositor.TryHitTestPoint(new Vector2(30, 30), out _));
        window.Render();
        Assert.Equal(cold, window.ReadPixels());
        window.Content = new PictureVisual(independent);
        window.Render();
        Assert.Equal(window.ReadPixels(), cold);
        for (int y = 0; y < 64; ++y)
            for (int x = 0; x < 64; ++x)
            {
                bool red = Expected(x + .5, y + .5, radius, limit);
                int offset = (y * 64 + x) * 4;
                Assert.Equal(red ? (byte)255 : (byte)0, cold[offset]);
                Assert.Equal(0, cold[offset + 1]);
                Assert.Equal(0, cold[offset + 2]);
                Assert.Equal(255, cold[offset + 3]);
            }
        window.Content = null;
    }

    private static Pen CreatePen(int mode, float limit) => new(
        new SolidColorBrush(new Vector4(1, 0, 0, 1)), mode == 2 ? Pen.HairlineThickness : 8,
        PenLineJoin.Miter, limit, strokeTransformMode: mode == 0 ? PenStrokeTransformMode.Normal : PenStrokeTransformMode.Fixed)
        { ClipMiterAtLimit = true };

    [Theory]
    [InlineData(0, 1f)]
    [InlineData(0, 2f)]
    [InlineData(1, 1f)]
    [InlineData(1, 2f)]
    [InlineData(2, 1f)]
    [InlineData(2, 2f)]
    public void NonuniformPlacementRetainsLocalOrDeviceJoinWidth(int mode, float limit)
    {
        using var window = new HeadlessWindow(64, 64);
        var transform = Matrix4x4.CreateScale(.75f, 1.25f, 1) *
            Matrix4x4.CreateTranslation(6, -2, 0);
        using var picture = Picture(CreatePen(mode, limit), Rectangle(), transform: transform);
        var corner = Vector2.Transform(new Vector2(50.25f, 10.25f), transform);
        var outward = mode == 0 ? new Vector2(3, -5) :
            mode == 1 ? new Vector2(4, -4) : new Vector2(.5f, -.5f);
        window.Content = new PictureVisual(picture);
        for (int replay = 0; replay < 2; replay++)
        {
            window.Render();
            Assert.True(window.Compositor.TryHitTestPoint(corner + outward * .6f, out _));
            Assert.Equal(limit == 2,
                window.Compositor.TryHitTestPoint(corner + outward * .8f, out _));
            Assert.False(window.Compositor.TryHitTestPoint(
                Vector2.Transform(new Vector2(30, 30), transform), out _));
        }
        window.Content = null;
    }

    private static GpuPicture Picture(Pen? pen, PathGeometry path, Brush? brush = null,
        Matrix4x4? transform = null) => new(
        [new RenderCommand { Type = RenderCommandType.DrawPath, Path = path, Pen = pen, Brush = brush,
            Transform = transform ?? Matrix4x4.Identity, IsEdgeAliased = true, IsPenThicknessLocal = pen != null,
            GeometryCache = RenderCommandGeometryCache.ForPath(path) }], [], [], [], []);

    private static PathGeometry Rectangle()
    {
        var path = new PathGeometry();
        AddContour(path, [new(10.25f, 10.25f), new(50.25f, 10.25f), new(50.25f, 50.25f), new(10.25f, 50.25f)], false);
        return path;
    }

    private static PathGeometry Ring(float radius, float limit)
    {
        const float l = 10.25f, r = 50.25f;
        // Independent polygon: clip the outer square with four planes normal
        // to its corner diagonals. No product widening/bounds/join helper.
        float inset = limit == 1 ? (float)(radius * (2 - Math.Sqrt(2))) : 0;
        var path = new PathGeometry { FillRule = FillRule.EvenOdd };
        AddContour(path, [new(l - radius + inset, l - radius), new(r + radius - inset, l - radius),
            new(r + radius, l - radius + inset), new(r + radius, r + radius - inset),
            new(r + radius - inset, r + radius), new(l - radius + inset, r + radius),
            new(l - radius, r + radius - inset), new(l - radius, l - radius + inset)], true);
        AddContour(path, [new(l + radius, l + radius), new(r - radius, l + radius),
            new(r - radius, r - radius), new(l + radius, r - radius)], true);
        return path;
    }

    private static void AddContour(PathGeometry path, Vector2[] points, bool filled)
    {
        var figure = new PathFigure(points[0], isClosed: true) { IsFilled = filled };
        for (int i = 1; i < points.Length; ++i) figure.Segments.Add(new LineSegment(points[i]));
        path.Figures.Add(figure);
    }

    private static bool Expected(double x, double y, double radius, float limit)
    {
        const double l = 10.25, r = 50.25;
        double distance = radius * Math.Sqrt(2);
        return x >= l - radius && x < r + radius && y >= l - radius && y < r + radius &&
            !(x >= l + radius && x < r - radius && y >= l + radius && y < r - radius) &&
            (limit != 1 || l - x + l - y <= distance && x - r + l - y <= distance &&
                x - r + y - r <= distance && l - x + y - r <= distance);
    }

    private static bool Contains(ReadOnlySpan<StrokeJoinTriangle> triangles, Vector2 point)
    {
        static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
        foreach (var triangle in triangles)
        {
            float a = Cross(triangle.P1 - triangle.P0, point - triangle.P0);
            float b = Cross(triangle.P2 - triangle.P1, point - triangle.P1);
            float c = Cross(triangle.P0 - triangle.P2, point - triangle.P2);
            if (a >= 0 && b >= 0 && c >= 0 || a <= 0 && b <= 0 && c <= 0) return true;
        }
        return false;
    }

    private sealed class PictureVisual : FrameworkElement
    {
        private readonly GpuPicture _picture;
        public PictureVisual(GpuPicture picture) { _picture = picture; Width = 64; Height = 64; }
        public override void OnRender(DrawingContext context) => context.DrawPicture(_picture);
    }
}
