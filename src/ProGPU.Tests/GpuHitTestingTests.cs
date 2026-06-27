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
        Assert.Equal(32, Marshal.SizeOf<GpuHitTestQuery>());
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
}
