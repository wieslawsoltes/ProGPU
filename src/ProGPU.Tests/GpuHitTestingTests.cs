using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class GpuHitTestingTests
{
    [Fact]
    public void StructLayoutsMatchShaderStorageLayout()
    {
        Assert.Equal(96, Marshal.SizeOf<GpuHitTestPrimitive>());
        Assert.Equal(32, Marshal.SizeOf<GpuHitTestNode>());
        Assert.Equal(40, Marshal.SizeOf<GpuHitTestQuery>());
        Assert.Equal(32, Marshal.SizeOf<GpuHitTestResult>());
        Assert.Equal(48, Marshal.SizeOf<GpuPathSegment>());
    }

    [Fact]
    public void BuildCreatesQuadtreeBroadPhaseNodes()
    {
        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleFill(10, new Vector2(0f, 0f), new Vector2(10f, 10f), Vector2.Zero),
            GpuHitTestPrimitive.RectangleFill(20, new Vector2(90f, 90f), new Vector2(100f, 100f), Vector2.Zero),
            GpuHitTestPrimitive.RectangleFill(30, new Vector2(0f, 90f), new Vector2(10f, 100f), Vector2.Zero),
            GpuHitTestPrimitive.RectangleFill(40, new Vector2(90f, 0f), new Vector2(100f, 10f), Vector2.Zero)
        ];

        var index = GpuHitTestIndex.Build(primitives, maxDepth: 4, maxPrimitivesPerNode: 1);

        Assert.True(index.Nodes.Count > 1);
        Assert.Equal((uint)4, index.Nodes[0].ChildCount);
        Assert.Equal(4, index.PrimitiveIndices.Count);
    }

    [Fact]
    public void TryHitTestPointAppliesPrimitiveInverseTransformOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        var transform = Matrix4x4.CreateScale(2f, 2f, 1f) * Matrix4x4.CreateTranslation(10f, 20f, 0f);
        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleFill(42, new Vector2(0f, 0f), new Vector2(10f, 10f), Vector2.Zero, transform)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);

        bool hit = GpuHitTestEngine.TryHitTestPoint(context, index, new Vector2(20f, 30f), out GpuHitTestResult result);

        Assert.True(hit);
        Assert.Equal(42, result.Id);

        bool outside = GpuHitTestEngine.TryHitTestPoint(context, index, new Vector2(32f, 30f), out GpuHitTestResult outsideResult);
        Assert.False(outside);
        Assert.False(outsideResult.HasHit);
    }

    [Fact]
    public void RenderCommandCacheClipsBroadPhaseBounds()
    {
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.PushClip,
            Rect = new Rect(2f, 2f, 3f, 3f)
        }, Matrix4x4.Identity);
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            Rect = new Rect(0f, 0f, 10f, 10f),
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.Identity, id: 7);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(new Vector2(2f, 2f), primitive.BoundsMin);
        Assert.Equal(new Vector2(5f, 5f), primitive.BoundsMax);
    }

    [Fact]
    public void RenderCommandCacheAppliesVisualClipScopes()
    {
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.PushClip(new Rect(10f, 10f, 20f, 20f), Matrix4x4.CreateTranslation(5f, 0f, 0f));
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            Rect = new Rect(0f, 0f, 100f, 100f),
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.Identity, id: 7);
        builder.PopClip();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            Rect = new Rect(50f, 50f, 10f, 10f),
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.Identity, id: 8);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        Assert.Collection(
            index.Primitives,
            clipped =>
            {
                Assert.Equal(7, clipped.Id);
                Assert.Equal(new Vector2(15f, 10f), clipped.BoundsMin);
                Assert.Equal(new Vector2(35f, 30f), clipped.BoundsMax);
            },
            unclipped =>
            {
                Assert.Equal(8, unclipped.Id);
                Assert.Equal(new Vector2(50f, 50f), unclipped.BoundsMin);
                Assert.Equal(new Vector2(60f, 60f), unclipped.BoundsMax);
            });
    }

    [Fact]
    public void RenderCommandCacheUsesCommandHitTestId()
    {
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            HitTestId = 1234,
            Rect = new Rect(0f, 0f, 10f, 10f),
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.Identity);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(1234, primitive.Id);
    }

    [Fact]
    public void RenderCommandCacheUsesExplicitTextureHitTestId()
    {
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawTexture,
            Rect = new Rect(10f, 20f, 30f, 40f)
        }, Matrix4x4.CreateTranslation(5f, 6f, 0f), id: 4321);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(4321, primitive.Id);
        Assert.Equal(GpuHitTestPrimitiveKind.AxisAlignedBounds, primitive.Kind);
        Assert.Equal(new Vector2(15f, 26f), primitive.BoundsMin);
        Assert.Equal(new Vector2(45f, 66f), primitive.BoundsMax);
    }

    [Fact]
    public void RenderCommandCacheBuildsPathPrimitiveSegments()
    {
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            HitTestId = 77,
            Path = CreateTrianglePath(),
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.Identity);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(GpuHitTestPrimitiveKind.PathFill, primitive.Kind);
        Assert.Equal(77, primitive.Id);
        Assert.Equal(3, index.PathSegments.Count);
    }

    [Fact]
    public void RenderCommandCacheSkipsUncompiledPathsInsteadOfAddingBoundsHit()
    {
        var combined = new PathGeometry
        {
            IsCombined = true,
            PathB = PrimitivePathGeometry.CreateRectangle(0f, 0f, 20f, 20f),
            Op = 2
        };

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            HitTestId = 77,
            Path = combined,
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.Identity);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        Assert.Empty(index.Primitives);
        Assert.Empty(index.PathSegments);
    }

    [Fact]
    public void RenderCommandCacheFeedsGpuHitTesting()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            Rect = new Rect(0f, 0f, 10f, 10f),
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.CreateTranslation(20f, 10f, 0f), id: 99);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        bool hit = GpuHitTestEngine.TryHitTestPoint(context, index, new Vector2(25f, 15f), out GpuHitTestResult result);

        Assert.True(hit);
        Assert.Equal(99, result.Id);

        bool outside = GpuHitTestEngine.TryHitTestPoint(context, index, new Vector2(15f, 15f), out _);
        Assert.False(outside);
    }

    [Fact]
    public void RenderCommandCacheFeedsGpuPathFillHitTesting()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = CreateTrianglePath(),
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.CreateTranslation(10f, 20f, 0f), id: 88);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        bool hit = GpuHitTestEngine.TryHitTestPoint(context, index, new Vector2(15f, 24f), out GpuHitTestResult result);

        Assert.True(hit);
        Assert.Equal(88, result.Id);

        bool outside = GpuHitTestEngine.TryHitTestPoint(context, index, new Vector2(19f, 29f), out GpuHitTestResult outsideResult);
        Assert.False(outside);
        Assert.False(outsideResult.HasHit);
        Assert.True(outsideResult.CandidateCount > 0);
    }

    [Fact]
    public void RenderCommandCacheFeedsGpuPathStrokeHitTesting()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(0f, 0f));
        figure.Segments.Add(new LineSegment(new Vector2(20f, 0f)));
        path.Figures.Add(figure);

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path,
            Pen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), 4f)
        }, Matrix4x4.Identity, id: 89);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        bool hit = GpuHitTestEngine.TryHitTestPoint(context, index, new Vector2(10f, 1.5f), out GpuHitTestResult result);

        Assert.True(hit);
        Assert.Equal(89, result.Id);

        bool outside = GpuHitTestEngine.TryHitTestPoint(context, index, new Vector2(10f, 4f), out GpuHitTestResult outsideResult);
        Assert.False(outside);
        Assert.False(outsideResult.HasHit);
    }

    [Fact]
    public void RenderCommandCacheUsesDashedPathSegmentsForStrokeHitTesting()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(0f, 0f));
        figure.Segments.Add(new LineSegment(new Vector2(20f, 0f)));
        path.Figures.Add(figure);

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            HitTestId = 91,
            Path = path,
            Pen = new Pen(
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                thickness: 2f,
                dashArray: [2.0, 2.0])
        }, Matrix4x4.Identity);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(GpuHitTestPrimitiveKind.PathStroke, primitive.Kind);
        Assert.True(index.PathSegments.Count > 1);

        bool dashHit = GpuHitTestEngine.TryHitTestPoint(context, index, new Vector2(2f, 0f), out GpuHitTestResult dashResult);
        bool gapHit = GpuHitTestEngine.TryHitTestPoint(context, index, new Vector2(6f, 0f), out GpuHitTestResult gapResult);

        Assert.True(dashHit);
        Assert.Equal(91, dashResult.Id);
        Assert.False(gapHit);
        Assert.False(gapResult.HasHit);
    }

    [Fact]
    public void TryHitTestPointReusesUploadedDeviceIndex()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        using var pipelineCache = new RenderPipelineCache(context);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleFill(11, new Vector2(0f, 0f), new Vector2(20f, 20f), Vector2.Zero)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        using var deviceIndex = new GpuHitTestDeviceIndex(context, index);

        bool first = GpuHitTestEngine.TryHitTestPoint(context, pipelineCache, deviceIndex, new Vector2(10f, 10f), out GpuHitTestResult firstResult);
        bool second = GpuHitTestEngine.TryHitTestPoint(context, pipelineCache, deviceIndex, new Vector2(30f, 30f), out GpuHitTestResult secondResult);

        Assert.True(first);
        Assert.Equal(11, firstResult.Id);
        Assert.False(second);
        Assert.False(secondResult.HasHit);
    }

    [Fact]
    public void TryHitTestPointUsesGpuQuadtreeAndPreciseTesting()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleFill(10, new Vector2(0f, 0f), new Vector2(100f, 100f), Vector2.Zero, zIndex: 0f),
            GpuHitTestPrimitive.EllipseFill(20, new Vector2(25f, 25f), new Vector2(75f, 75f), zIndex: 1f),
            GpuHitTestPrimitive.LineStroke(30, new Vector2(10f, 50f), new Vector2(90f, 50f), 8f, LineGeometryCap.Round, LineGeometryCap.Round, zIndex: 2f)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 4, maxPrimitivesPerNode: 1);

        bool hit = GpuHitTestEngine.TryHitTestPoint(context, index, new Vector2(50f, 50f), out GpuHitTestResult result);

        Assert.True(hit);
        Assert.True(result.HasHit);
        Assert.Equal(30, result.Id);
        Assert.Equal(2f, result.ZIndex);
        Assert.True(result.NodesVisited > 0);
        Assert.True(result.CandidateCount > 0);
        Assert.True(result.PreciseTests > 0);
    }

    [Fact]
    public void TryHitTestPointAllReturnsHitsInDescendingZOrder()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        using var pipelineCache = new RenderPipelineCache(context);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleFill(10, new Vector2(0f, 0f), new Vector2(100f, 100f), Vector2.Zero, zIndex: 0f),
            GpuHitTestPrimitive.RectangleFill(20, new Vector2(0f, 0f), new Vector2(100f, 100f), Vector2.Zero, zIndex: 1f),
            GpuHitTestPrimitive.RectangleFill(30, new Vector2(0f, 0f), new Vector2(100f, 100f), Vector2.Zero, zIndex: 2f)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 4, maxPrimitivesPerNode: 1);
        using var deviceIndex = new GpuHitTestDeviceIndex(context, index);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryHitTestPointAll(
            context,
            pipelineCache,
            deviceIndex,
            new Vector2(50f, 50f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.True(hit);
        Assert.Equal(3, hitCount);
        Assert.Equal(3u, summary.Hit);
        Assert.Equal([30, 20, 10], results.Take(hitCount).Select(result => result.Id).ToArray());
    }

    [Fact]
    public void TryHitTestPointAllHonorsCallerCapacity()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleFill(10, new Vector2(0f, 0f), new Vector2(100f, 100f), Vector2.Zero, zIndex: 0f),
            GpuHitTestPrimitive.RectangleFill(20, new Vector2(0f, 0f), new Vector2(100f, 100f), Vector2.Zero, zIndex: 1f),
            GpuHitTestPrimitive.RectangleFill(30, new Vector2(0f, 0f), new Vector2(100f, 100f), Vector2.Zero, zIndex: 2f)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 4, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[2];

        bool hit = GpuHitTestEngine.TryHitTestPointAll(
            context,
            index,
            new Vector2(50f, 50f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.True(hit);
        Assert.Equal(2, hitCount);
        Assert.Equal(2u, summary.Hit);
        Assert.Equal([30, 20], results.Take(hitCount).Select(result => result.Id).ToArray());
    }

    [Fact]
    public void TryQueryBoundsAllReturnsIntersectingBroadPhaseHitsInDescendingZOrder()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        using var pipelineCache = new RenderPipelineCache(context);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleFill(10, new Vector2(0f, 0f), new Vector2(10f, 10f), Vector2.Zero, zIndex: 0f),
            GpuHitTestPrimitive.RectangleFill(20, new Vector2(20f, 20f), new Vector2(30f, 30f), Vector2.Zero, zIndex: 2f),
            GpuHitTestPrimitive.RectangleFill(30, new Vector2(40f, 40f), new Vector2(50f, 50f), Vector2.Zero, zIndex: 1f)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 4, maxPrimitivesPerNode: 1);
        using var deviceIndex = new GpuHitTestDeviceIndex(context, index);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            pipelineCache,
            deviceIndex,
            new Vector2(5f, 5f),
            new Vector2(25f, 25f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.True(hit);
        Assert.Equal(2, hitCount);
        Assert.Equal(2u, summary.Hit);
        Assert.Equal([20, 10], results.Take(hitCount).Select(result => result.Id).ToArray());
        Assert.Equal(
            [(uint)GpuHitTestIntersectionDetail.Intersects, (uint)GpuHitTestIntersectionDetail.Intersects],
            results.Take(hitCount).Select(result => result.IntersectionDetail).ToArray());
        Assert.Equal(2u, summary.CandidateCount);
        Assert.Equal(0u, summary.PreciseTests);
        Assert.True(summary.NodesVisited > 0);
    }

    [Fact]
    public void TryQueryBoundsAllClassifiesRectBoundsIntersectionDetailOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleFill(10, new Vector2(0f, 0f), new Vector2(10f, 10f), Vector2.Zero, zIndex: 0f),
            GpuHitTestPrimitive.RectangleFill(20, new Vector2(20f, 20f), new Vector2(30f, 30f), Vector2.Zero, zIndex: 1f),
            GpuHitTestPrimitive.RectangleFill(30, new Vector2(40f, 40f), new Vector2(50f, 50f), Vector2.Zero, zIndex: 2f)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 4, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(-5f, -5f),
            new Vector2(45f, 45f),
            results,
            out int hitCount,
            out _);

        Assert.True(hit);
        Assert.Equal(3, hitCount);
        Assert.Equal([30, 20, 10], results.Take(hitCount).Select(result => result.Id).ToArray());
        Assert.Equal(
            [(uint)GpuHitTestIntersectionDetail.Intersects, (uint)GpuHitTestIntersectionDetail.FullyInside, (uint)GpuHitTestIntersectionDetail.FullyInside],
            results.Take(hitCount).Select(result => result.IntersectionDetail).ToArray());

        Array.Clear(results);
        hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(2f, 2f),
            new Vector2(4f, 4f),
            results,
            out hitCount,
            out _);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal(10, results[0].Id);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.FullyContains, results[0].IntersectionDetail);
    }

    [Fact]
    public void TryQueryBoundsAllRejectsEllipseCornerFalsePositiveOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.EllipseFill(20, new Vector2(0f, 0f), new Vector2(10f, 10f))
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(9f, 9f),
            new Vector2(12f, 12f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.False(hit);
        Assert.Equal(0, hitCount);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryBoundsAllClassifiesEllipseRectRegionIntersectionDetailOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.EllipseFill(20, new Vector2(0f, 0f), new Vector2(10f, 10f))
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(7f, 4f),
            new Vector2(12f, 6f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal(20, results[0].Id);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.Intersects, results[0].IntersectionDetail);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);

        Array.Clear(results);
        hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(4f, 4f),
            new Vector2(6f, 6f),
            results,
            out hitCount,
            out summary);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.FullyContains, results[0].IntersectionDetail);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);

        Array.Clear(results);
        hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(-1f, -1f),
            new Vector2(11f, 11f),
            results,
            out hitCount,
            out summary);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.FullyInside, results[0].IntersectionDetail);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryBoundsAllRejectsEllipseStrokeHoleOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.EllipseStroke(30, new Vector2(0f, 0f), new Vector2(10f, 10f), strokeThickness: 2f)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(4.5f, 4.5f),
            new Vector2(5.5f, 5.5f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.False(hit);
        Assert.Equal(0, hitCount);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);

        Array.Clear(results);
        hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(9.5f, 4.5f),
            new Vector2(10.5f, 5.5f),
            results,
            out hitCount,
            out summary);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal(30, results[0].Id);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.Intersects, results[0].IntersectionDetail);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryBoundsAllRejectsPathFillBoundsFalsePositiveOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = CreateTrianglePath(),
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.Identity, id: 40);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(8f, 8f),
            new Vector2(9f, 9f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.False(hit);
        Assert.Equal(0, hitCount);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryBoundsAllClassifiesPathFillRectRegionIntersectionDetailOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = CreateTrianglePath(),
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.Identity, id: 41);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(1f, 1f),
            new Vector2(2f, 2f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal(41, results[0].Id);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.FullyContains, results[0].IntersectionDetail);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);

        Array.Clear(results);
        hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(9f, 0.25f),
            new Vector2(11f, 0.75f),
            results,
            out hitCount,
            out summary);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal(41, results[0].Id);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.Intersects, results[0].IntersectionDetail);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryBoundsAllRejectsPathStrokeBoundsFalsePositiveOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        var path = CreateDiagonalLinePath();
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path,
            Pen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), 1f)
        }, Matrix4x4.Identity, id: 42);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(0f, 9f),
            new Vector2(1f, 10f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.False(hit);
        Assert.Equal(0, hitCount);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);

        Array.Clear(results);
        hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(4.9f, 5.2f),
            new Vector2(5.1f, 5.4f),
            results,
            out hitCount,
            out summary);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal(42, results[0].Id);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.Intersects, results[0].IntersectionDetail);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryHitTestPointRejectsEllipseCornerAfterBroadPhaseCandidate()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.EllipseFill(20, new Vector2(0f, 0f), new Vector2(10f, 10f))
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);

        bool hit = GpuHitTestEngine.TryHitTestPoint(context, index, new Vector2(9f, 9f), out GpuHitTestResult result);

        Assert.False(hit);
        Assert.False(result.HasHit);
        Assert.Equal(1u, result.CandidateCount);
        Assert.Equal(1u, result.PreciseTests);
    }

    private static PathGeometry CreateTrianglePath()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(0f, 0f), isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(10f, 0f)));
        figure.Segments.Add(new LineSegment(new Vector2(0f, 10f)));
        path.Figures.Add(figure);
        return path;
    }

    private static PathGeometry CreateDiagonalLinePath()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(0f, 0f));
        figure.Segments.Add(new LineSegment(new Vector2(10f, 10f)));
        path.Figures.Add(figure);
        return path;
    }
}
