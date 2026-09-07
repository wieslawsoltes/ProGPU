using System;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class LinearDashCoverageCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EquivalentStyleReusesCoverageAndRefreshesOnlyPaint(bool terminal)
    {
        var cache = RenderCommandGeometryCache.ForStrokePath(CreatePath());
        var pen = CreatePen(terminal);
        Assert.True(cache.TryGetLinearDashCoverage(pen, 1, out var initial));
        var replay = CreatePen(terminal); // Equal intervals, independent owned storage.
        Assert.True(cache.TryGetLinearDashCoverage(replay, 1, out var updated));
        Assert.Same(initial.Path, updated.Path);
        Assert.Same(initial.GeometryCache, updated.GeometryCache);
        Assert.Equal(terminal, updated.Pen == null);
        if (!terminal)
        {
            Assert.NotSame(initial.Pen, updated.Pen);
            Assert.Same(replay.Brush, updated.Pen!.Brush);
            Assert.False(updated.Pen.HasDashPattern);
        }
        Assert.True(cache.TryGetLinearDashCoverage(replay, 1, out var stable));
        Assert.Same(updated.Path, stable.Path);
        Assert.Same(updated.Pen, stable.Pen);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void EveryOutlineStyleInputInvalidatesCoverage(int change)
    {
        var cache = RenderCommandGeometryCache.ForStrokePath(CreatePath());
        var pen = CreatePen(true);
        Assert.True(cache.TryGetLinearDashCoverage(pen, 1, out var initial));
        float localWidth = 1;
        switch (change)
        {
            case 0: localWidth = 2; break;
            case 1: pen.LineJoin = PenLineJoin.Bevel; break;
            case 2: pen.MiterLimit = 3; break;
            case 3: pen.StartLineCap = PenLineCap.Square; break;
            case 4: pen.EndLineCap = PenLineCap.Square; break;
            case 5: pen.DashCap = PenLineCap.Square; break;
            case 6: pen.DashOffset = 0.5; break;
            case 7: pen.DashArray = [1, 1]; break;
        }
        Assert.True(cache.TryGetLinearDashCoverage(pen, localWidth, out var changed));
        Assert.NotSame(initial.Path, changed.Path);
        Assert.NotSame(initial.GeometryCache, changed.GeometryCache);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmCoverageLookupDoesNotAllocate(bool terminal)
    {
        var cache = RenderCommandGeometryCache.ForStrokePath(CreatePath());
        var pen = CreatePen(terminal);
        for (int i = 0; i < 128; i++)
            Assert.True(cache.TryGetLinearDashCoverage(pen, 1, out _));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            if (!cache.TryGetLinearDashCoverage(pen, 1, out _))
                throw new InvalidOperationException("Stable dash coverage unexpectedly failed.");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void InvalidStyleFailsClosedAndCanRecoverWithoutStaleCoverage()
    {
        var cache = RenderCommandGeometryCache.ForStrokePath(CreatePath());
        var pen = CreatePen(true);
        Assert.True(cache.TryGetLinearDashCoverage(pen, 1, out var initial));
        pen.DashArray = [0, 2];
        Assert.False(cache.TryGetLinearDashCoverage(pen, 1, out _));
        Assert.False(cache.TryGetLinearDashCoverage(pen, 1, out _));
        pen.DashArray = [2, 2];
        Assert.True(cache.TryGetLinearDashCoverage(pen, 1, out var recovered));
        Assert.NotSame(initial.Path, recovered.Path);
        Assert.Null(recovered.Pen);
    }

    [Fact]
    public void RepeatedNonfiniteWidthRetainsFailureWithoutAllocating()
    {
        var cache = RenderCommandGeometryCache.ForStrokePath(CreatePath());
        var pen = CreatePen(true);
        for (int i = 0; i < 128; i++)
            Assert.False(cache.TryGetLinearDashCoverage(pen, float.NaN, out _));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            if (cache.TryGetLinearDashCoverage(pen, float.NaN, out _))
                throw new InvalidOperationException("Invalid width unexpectedly succeeded.");
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void PreparedCommandRetainsMetadataWithoutDuplicatingFillOrTransform()
    {
        var path = CreatePath();
        var pen = CreatePen(true);
        var command = new RenderCommand
        {
            Type = RenderCommandType.DrawPath, Path = path, Pen = pen,
            Brush = new SolidColorBrush(new Vector4(0, 1, 0, 1)),
            GeometryCache = RenderCommandGeometryCache.ForPath(path),
            Transform = Matrix4x4.CreateScale(2, 3, 1), IsEdgeAliased = true,
            IsPenThicknessLocal = true
        };
        Assert.True(Compositor.TryPrepareLinearDashCommand(command, path, 1, out var prepared));
        Assert.NotSame(path, prepared.Path);
        Assert.Null(prepared.Pen);
        Assert.Same(pen.Brush, prepared.Brush);
        Assert.Equal(default, prepared.Transform); // Caller already composed it.
        Assert.True(prepared.IsEdgeAliased);
        Assert.Same(path, command.Path);
        Assert.Same(pen, command.Pen);
        Assert.True(path.Figures[0].IsFilled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HitTestingSharesTerminalOutlineAndDeferredPolylineSource(bool polyline)
    {
        var drawing = new DrawingContext();
        var pen = CreatePen(true);
        if (polyline)
            drawing.DrawPolyline(pen, new Vector2[] { Vector2.Zero, new(2, 0), new(4, 0) }.AsSpan());
        else
            drawing.DrawPath(null, pen, CreatePath());
        var command = Assert.Single(drawing.Commands);
        using var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(command, Matrix4x4.Identity, drawing, id: 915);
        var index = builder.BuildIndex();
        Assert.NotEmpty(index.Primitives);
        Assert.All(index.Primitives, primitive =>
        {
            Assert.Equal(915, primitive.Id);
            Assert.Equal(GpuHitTestPrimitiveKind.PathFill, primitive.Kind);
        });
        var cache = Assert.IsType<RenderCommandGeometryCache>(command.GeometryCache);
        Assert.NotNull(cache.StrokePath);
        Assert.True(cache.TryGetLinearDashCoverage(pen, 1, out var coverage));
        Assert.Null(coverage.Pen);
        Assert.NotEmpty(index.PathSegments);
    }

    [Fact]
    public void GpuHitTestingIncludesTerminalCapAndExcludesHiddenInterval()
    {
        using var gpu = new WgpuContext();
        gpu.Initialize(null);
        var drawing = new DrawingContext();
        drawing.DrawPath(null, CreatePen(true), CreatePath());
        using var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(Assert.Single(drawing.Commands), Matrix4x4.Identity, drawing, id: 916);
        var index = builder.BuildIndex();
        Assert.True(GpuHitTestEngine.TryHitTestPoint(gpu, index, new Vector2(4.25f, 0), out var hit));
        Assert.Equal(916, hit.Id);
        Assert.False(GpuHitTestEngine.TryHitTestPoint(gpu, index, new Vector2(3, 0), out _));
    }

    internal static PathGeometry CreatePath()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(Vector2.Zero);
        figure.Segments.Add(new LineSegment(new(2, 0)));
        figure.Segments.Add(new LineSegment(new(4, 0)));
        path.Figures.Add(figure);
        return path;
    }

    internal static Pen CreatePen(bool terminal) => new(new SolidColorBrush(Vector4.One), 1,
        endLineCap: terminal ? PenLineCap.Triangle : PenLineCap.Flat,
        dashCap: terminal ? PenLineCap.Round : PenLineCap.Flat, dashArray: [2, 2]);
}
