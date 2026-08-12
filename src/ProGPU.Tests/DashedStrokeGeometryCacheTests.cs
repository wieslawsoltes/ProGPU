using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class DashedStrokeGeometryCacheTests
{
    [Fact]
    public void ReconstructedPictureGradientPenReusesGeometryWithoutKeepingStalePaint()
    {
        var cache = CreateLineCache();
        var firstBrush = CreateGradientBrush(Matrix4x4.CreateTranslation(2f, 3f, 0f));
        var firstPen = CreatePen(firstBrush);

        Assert.True(cache.TryGetDashedStrokePath(firstPen, out var firstPath, out var firstUndashedPen));

        // Picture replay reconstructs a gradient and its pen after composing the
        // command transform even when the effective paint is value-identical.
        var replayBrush = CreateGradientBrush(Matrix4x4.CreateTranslation(2f, 3f, 0f));
        var replayPen = CreatePen(replayBrush);
        Assert.True(cache.TryGetDashedStrokePath(replayPen, out var replayPath, out var replayUndashedPen));

        Assert.Same(firstPath, replayPath);
        Assert.NotSame(firstUndashedPen, replayUndashedPen);
        Assert.Same(replayBrush, replayUndashedPen.Brush);
        Assert.NotSame(firstBrush, replayUndashedPen.Brush);

        // A stable replay with the same retained paint also reuses the derived pen.
        Assert.True(cache.TryGetDashedStrokePath(replayPen, out var stablePath, out var stableUndashedPen));
        Assert.Same(replayPath, stablePath);
        Assert.Same(replayUndashedPen, stableUndashedPen);
    }

    [Fact]
    public void DashGeometryCacheInvalidatesEveryPlacementInput()
    {
        var cache = CreateLineCache();
        var pen = CreatePen(new SolidColorBrush(Vector4.One));

        Assert.True(cache.TryGetDashedStrokePath(pen, 2f, out var initialPath, out _));

        Assert.True(cache.TryGetDashedStrokePath(pen, 3f, out var widthPath, out _));
        Assert.NotSame(initialPath, widthPath);

        pen.DashOffset = 0.75;
        Assert.True(cache.TryGetDashedStrokePath(pen, 3f, out var offsetPath, out _));
        Assert.NotSame(widthPath, offsetPath);

        pen.DashArray = [3.0, 1.0];
        Assert.True(cache.TryGetDashedStrokePath(pen, 3f, out var intervalPath, out _));
        Assert.NotSame(offsetPath, intervalPath);

        pen.DashArray = [3.0, 1.0, 2.0, 1.0];
        Assert.True(cache.TryGetDashedStrokePath(pen, 3f, out var intervalCountPath, out _));
        Assert.NotSame(intervalPath, intervalCountPath);
    }

    [Fact]
    public void StrokePaintStyleRefreshesDerivedPenWithoutRebuildingDashGeometry()
    {
        var cache = CreateLineCache();
        var pen = CreatePen(new SolidColorBrush(Vector4.One));
        Assert.True(cache.TryGetDashedStrokePath(pen, out var retainedPath, out var previousPen));

        pen.LineJoin = PenLineJoin.Round;
        AssertPaintOnlyChange(cache, pen, retainedPath, ref previousPen);

        pen.MiterLimit = 4f;
        AssertPaintOnlyChange(cache, pen, retainedPath, ref previousPen);

        pen.Brush = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f));
        AssertPaintOnlyChange(cache, pen, retainedPath, ref previousPen);
        Assert.Same(pen.Brush, previousPen.Brush);
    }

    [Fact]
    public void StrokeCapsRebuildDashGeometryBecauseTheyPlaceRunEndpoints()
    {
        var cache = CreateLineCache();
        var pen = CreatePen(new SolidColorBrush(Vector4.One));
        Assert.True(cache.TryGetDashedStrokePath(pen, out var initialPath, out _));

        pen.StartLineCap = PenLineCap.Square;
        Assert.True(cache.TryGetDashedStrokePath(pen, out var startCapPath, out _));
        Assert.NotSame(initialPath, startCapPath);

        pen.EndLineCap = PenLineCap.Triangle;
        Assert.True(cache.TryGetDashedStrokePath(pen, out var endCapPath, out _));
        Assert.NotSame(startCapPath, endCapPath);

        pen.DashCap = PenLineCap.Round;
        Assert.True(cache.TryGetDashedStrokePath(pen, out var dashCapPath, out _));
        Assert.NotSame(endCapPath, dashCapPath);
    }

    [Fact]
    public void StableDashGeometryCacheHitIsAllocationFree()
    {
        var cache = CreateLineCache();
        var pen = CreatePen(new SolidColorBrush(Vector4.One));
        Assert.True(cache.TryGetDashedStrokePath(pen, out var expectedPath, out var expectedPen));

        for (var index = 0; index < 128; index++)
        {
            if (!cache.TryGetDashedStrokePath(pen, out _, out _))
            {
                throw new InvalidOperationException("The warmed dash cache unexpectedly missed.");
            }
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        PathGeometry? actualPath = null;
        Pen? actualPen = null;
        for (var index = 0; index < 10_000; index++)
        {
            if (!cache.TryGetDashedStrokePath(pen, out actualPath, out actualPen))
            {
                throw new InvalidOperationException("The warmed dash cache unexpectedly missed.");
            }
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(0, allocated);
        Assert.Same(expectedPath, actualPath);
        Assert.Same(expectedPen, actualPen);
    }

    private static void AssertPaintOnlyChange(
        RenderCommandGeometryCache cache,
        Pen sourcePen,
        PathGeometry expectedPath,
        ref Pen previousPen)
    {
        Assert.True(cache.TryGetDashedStrokePath(sourcePen, out var path, out var derivedPen));
        Assert.Same(expectedPath, path);
        Assert.NotSame(previousPen, derivedPen);
        previousPen = derivedPen;
    }

    private static RenderCommandGeometryCache CreateLineCache()
    {
        return RenderCommandGeometryCache.ForStrokePath(
            RenderCommandGeometryCache.CreateLinePath(
                Vector2.Zero,
                new Vector2(40f, 0f)));
    }

    private static Pen CreatePen(Brush brush)
    {
        return new Pen(
            brush,
            thickness: 2f,
            lineJoin: PenLineJoin.Bevel,
            miterLimit: 8f,
            startLineCap: PenLineCap.Flat,
            endLineCap: PenLineCap.Flat,
            dashCap: PenLineCap.Square,
            dashArray: [2.0, 1.0],
            dashOffset: 0.25);
    }

    private static LinearGradientBrush CreateGradientBrush(Matrix4x4 coordinateTransform)
    {
        return new LinearGradientBrush(
            Vector2.Zero,
            new Vector2(40f, 0f),
            [
                new GradientStop(new Vector4(1f, 0f, 0f, 1f), 0f),
                new GradientStop(new Vector4(0f, 0f, 1f, 1f), 1f)
            ])
        {
            CoordinateTransform = coordinateTransform
        };
    }
}
