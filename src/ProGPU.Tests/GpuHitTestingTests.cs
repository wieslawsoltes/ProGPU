using System.Collections.Generic;
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
        Assert.Equal(112, Marshal.SizeOf<GpuHitTestPrimitive>());
        Assert.Equal(32, Marshal.SizeOf<GpuHitTestNode>());
        Assert.Equal(40, Marshal.SizeOf<GpuHitTestQuery>());
        Assert.Equal(32, Marshal.SizeOf<GpuHitTestResult>());
        Assert.Equal(48, Marshal.SizeOf<GpuPathSegment>());
    }

    [Fact]
    public void LineStrokeCachesDirectionAndLengthForGpuHitTesting()
    {
        var primitive = GpuHitTestPrimitive.LineStroke(
            10,
            new Vector2(1f, 2f),
            new Vector2(4f, 6f),
            2f,
            LineGeometryCap.Flat,
            LineGeometryCap.Round);

        Assert.Equal(0.6f, primitive.Data2.X, 6);
        Assert.Equal(0.8f, primitive.Data2.Y, 6);
        Assert.Equal(5f, primitive.Data2.Z, 6);
        Assert.Equal(0f, primitive.Data2.W);
    }

    [Fact]
    public void EllipseCachesCenterAndInverseRadiiForGpuHitTesting()
    {
        var primitive = GpuHitTestPrimitive.EllipseFill(
            11,
            new Vector2(2f, 4f),
            new Vector2(10f, 20f));

        Assert.Equal(6f, primitive.Data2.X);
        Assert.Equal(12f, primitive.Data2.Y);
        Assert.Equal(0.25f, primitive.Data2.Z, 6);
        Assert.Equal(0.125f, primitive.Data2.W, 6);
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
    public void RenderCommandCacheCachesLineStrokeHelperDataForGpuHitTesting()
    {
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawLine,
            Pen = new Pen(
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                thickness: 2f,
                startLineCap: PenLineCap.Flat,
                endLineCap: PenLineCap.Round),
            Position = new Vector2(1f, 2f),
            Position2 = new Vector2(4f, 6f)
        }, Matrix4x4.Identity, id: 24);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(24, primitive.Id);
        Assert.Equal(GpuHitTestPrimitiveKind.LineStroke, primitive.Kind);
        Assert.Equal(0.6f, primitive.Data2.X, 6);
        Assert.Equal(0.8f, primitive.Data2.Y, 6);
        Assert.Equal(5f, primitive.Data2.Z, 6);
        Assert.Equal(0f, primitive.Data2.W);
    }

    [Fact]
    public void RenderCommandCacheCachesEllipseHelperDataForGpuHitTesting()
    {
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawEllipse,
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            Pen = new Pen(new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)), thickness: 2f),
            Position2 = new Vector2(6f, 12f),
            RadiusX = 4f,
            RadiusY = 8f
        }, Matrix4x4.Identity, id: 25);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        Assert.Collection(
            index.Primitives,
            fill =>
            {
                Assert.Equal(25, fill.Id);
                Assert.Equal(GpuHitTestPrimitiveKind.EllipseFill, fill.Kind);
                Assert.Equal(6f, fill.Data2.X);
                Assert.Equal(12f, fill.Data2.Y);
                Assert.Equal(0.25f, fill.Data2.Z, 6);
                Assert.Equal(0.125f, fill.Data2.W, 6);
            },
            stroke =>
            {
                Assert.Equal(25, stroke.Id);
                Assert.Equal(GpuHitTestPrimitiveKind.EllipseStroke, stroke.Kind);
                Assert.Equal(6f, stroke.Data2.X);
                Assert.Equal(12f, stroke.Data2.Y);
                Assert.Equal(0.25f, stroke.Data2.Z, 6);
                Assert.Equal(0.125f, stroke.Data2.W, 6);
            });
    }

    [Fact]
    public void RenderCommandCacheCachesCircleAsEllipseHelperDataForGpuHitTesting()
    {
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawCircle,
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            Pen = new Pen(new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)), thickness: 2f),
            Position2 = new Vector2(6f, 12f),
            RadiusX = 4f
        }, Matrix4x4.Identity, id: 26);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        Assert.Collection(
            index.Primitives,
            fill =>
            {
                Assert.Equal(26, fill.Id);
                Assert.Equal(GpuHitTestPrimitiveKind.EllipseFill, fill.Kind);
                Assert.Equal(new Vector2(2f, 8f), fill.BoundsMin);
                Assert.Equal(new Vector2(10f, 16f), fill.BoundsMax);
                Assert.Equal(6f, fill.Data2.X);
                Assert.Equal(12f, fill.Data2.Y);
                Assert.Equal(0.25f, fill.Data2.Z, 6);
                Assert.Equal(0.25f, fill.Data2.W, 6);
            },
            stroke =>
            {
                Assert.Equal(26, stroke.Id);
                Assert.Equal(GpuHitTestPrimitiveKind.EllipseStroke, stroke.Kind);
                Assert.Equal(new Vector2(1f, 7f), stroke.BoundsMin);
                Assert.Equal(new Vector2(11f, 17f), stroke.BoundsMax);
                Assert.Equal(6f, stroke.Data2.X);
                Assert.Equal(12f, stroke.Data2.Y);
                Assert.Equal(0.25f, stroke.Data2.Z, 6);
                Assert.Equal(0.25f, stroke.Data2.W, 6);
            });
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
    public void RenderCommandCacheUsesSharedPathCompilationForGpuHitTesting()
    {
        var compiler = new CountingPathHitTestCompilationCache();
        var builder = new GpuRenderCommandHitTestCacheBuilder(compiler);
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            HitTestId = 78,
            Path = CreateTrianglePath(),
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.Identity);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        Assert.Equal(1, compiler.CallCount);
        Assert.Equal(1, compiler.CompileCount);
        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(GpuHitTestPrimitiveKind.PathFill, primitive.Kind);
        Assert.Equal(78, primitive.Id);
        Assert.Equal(3f, primitive.Data1.Y);
        Assert.Equal(3, index.PathSegments.Count);
    }

    [Fact]
    public void RenderCommandCacheUsesPrecompiledPathMetadataWithoutRecompiling()
    {
        var compiler = new CountingPathHitTestCompilationCache();
        var path = CreateTrianglePath();
        compiler.Prewarm(path);
        var builder = new GpuRenderCommandHitTestCacheBuilder(compiler);
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            HitTestId = 79,
            Path = path,
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.Identity);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        Assert.Equal(1, compiler.CallCount);
        Assert.Equal(0, compiler.CompileCount);
        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(GpuHitTestPrimitiveKind.PathFill, primitive.Kind);
        Assert.Equal(79, primitive.Id);
        Assert.Equal(3, index.PathSegments.Count);
    }

    [Fact]
    public void RenderCommandCacheBuildsDashedLineAsGpuPathStroke()
    {
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawLine,
            HitTestId = 80,
            Position = new Vector2(0f, 0f),
            Position2 = new Vector2(10f, 0f),
            Pen = new Pen(
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                thickness: 2f,
                dashArray: [2.0, 2.0])
        }, Matrix4x4.Identity);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(GpuHitTestPrimitiveKind.PathStroke, primitive.Kind);
        Assert.Equal(80, primitive.Id);
        Assert.NotEmpty(index.PathSegments);
    }

    [Fact]
    public void RenderCommandCacheReusesRecordedDashedLineGeometryCache()
    {
        var context = new DrawingContext();
        var pen = new Pen(
            new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            thickness: 2f,
            dashArray: [2.0, 2.0]);
        context.DrawLine(pen, new Vector2(0f, 0f), new Vector2(10f, 0f));
        var command = Assert.Single(context.Commands);
        Assert.True(command.GeometryCache!.TryGetDashedStrokePath(pen, out var dashedPath, out _));

        var compiler = new CountingPathHitTestCompilationCache();
        compiler.Prewarm(dashedPath);
        var builder = new GpuRenderCommandHitTestCacheBuilder(compiler);

        builder.AddCommand(command, Matrix4x4.Identity, id: 88);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(GpuHitTestPrimitiveKind.PathStroke, primitive.Kind);
        Assert.Equal(88, primitive.Id);
        Assert.Equal(1, compiler.CallCount);
        Assert.Equal(0, compiler.CompileCount);
    }

    [Fact]
    public void RenderCommandCacheBuildsPolylineAsGpuPathStroke()
    {
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPolyline,
            HitTestId = 81,
            PolylinePoints =
            [
                new Vector2(0f, 0f),
                new Vector2(10f, 0f),
                new Vector2(10f, 10f)
            ],
            Pen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), 2f)
        }, Matrix4x4.Identity);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(GpuHitTestPrimitiveKind.PathStroke, primitive.Kind);
        Assert.Equal(81, primitive.Id);
        Assert.Equal(2, index.PathSegments.Count);
    }

    [Fact]
    public void RenderCommandCacheBuildsProviderPolylineAsGpuPathStroke()
    {
        var context = new DrawingContext();
        context.DrawPolyline(
            new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), 2f),
            [
                new Vector2(0f, 0f),
                new Vector2(10f, 0f),
                new Vector2(10f, 10f)
            ]);

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(context.Commands[0], Matrix4x4.Identity, context, id: 82);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(GpuHitTestPrimitiveKind.PathStroke, primitive.Kind);
        Assert.Equal(82, primitive.Id);
        Assert.Equal(2, index.PathSegments.Count);
    }

    [Fact]
    public void RenderCommandCacheBuildsQuadraticBezierAsGpuPathStroke()
    {
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawBezier,
            HitTestId = 85,
            Position = new Vector2(0f, 0f),
            Position2 = new Vector2(10f, 0f),
            Position3 = new Vector2(10f, 10f),
            Pen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), 2f)
        }, Matrix4x4.Identity);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(GpuHitTestPrimitiveKind.PathStroke, primitive.Kind);
        Assert.Equal(85, primitive.Id);
        var segment = Assert.Single(index.PathSegments);
        Assert.Equal(1u, segment.SegmentType);
    }

    [Fact]
    public void RenderCommandCacheBuildsCubicBezierAsGpuPathStroke()
    {
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawCubicBezier,
            HitTestId = 86,
            Position = new Vector2(0f, 0f),
            Position2 = new Vector2(10f, 0f),
            Position3 = new Vector2(10f, 10f),
            Position4 = new Vector2(0f, 10f),
            Pen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), 2f)
        }, Matrix4x4.Identity);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(GpuHitTestPrimitiveKind.PathStroke, primitive.Kind);
        Assert.Equal(86, primitive.Id);
        var segment = Assert.Single(index.PathSegments);
        Assert.Equal(2u, segment.SegmentType);
    }

    [Fact]
    public void RenderCommandCacheBuildsDashedBezierAsGpuPathStroke()
    {
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawBezier,
            HitTestId = 87,
            Position = new Vector2(0f, 0f),
            Position2 = new Vector2(10f, 0f),
            Position3 = new Vector2(10f, 10f),
            Pen = new Pen(
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                thickness: 2f,
                dashArray: [2.0, 2.0])
        }, Matrix4x4.Identity);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(GpuHitTestPrimitiveKind.PathStroke, primitive.Kind);
        Assert.Equal(87, primitive.Id);
        Assert.NotEmpty(index.PathSegments);
    }

    [Fact]
    public void RenderCommandCacheReusesRecordedQuadraticBezierGeometryCache()
    {
        var context = new DrawingContext();
        context.DrawQuadraticBezier(
            new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), 2f),
            new Vector2(0f, 0f),
            new Vector2(10f, 0f),
            new Vector2(10f, 10f));
        var command = Assert.Single(context.Commands);
        var cachedPath = command.GeometryCache!.StrokePath;
        Assert.NotNull(cachedPath);

        var compiler = new CountingPathHitTestCompilationCache();
        compiler.Prewarm(cachedPath);
        var builder = new GpuRenderCommandHitTestCacheBuilder(compiler);

        builder.AddCommand(command, Matrix4x4.Identity, id: 89);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(GpuHitTestPrimitiveKind.PathStroke, primitive.Kind);
        Assert.Equal(89, primitive.Id);
        Assert.Equal(1, compiler.CallCount);
        Assert.Equal(0, compiler.CompileCount);
    }

    [Fact]
    public void RenderCommandCacheBuildsTriangleAsGpuPathFill()
    {
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.FillTriangle,
            HitTestId = 83,
            Position = new Vector2(0f, 0f),
            Position2 = new Vector2(10f, 0f),
            Position3 = new Vector2(0f, 10f),
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.Identity);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(GpuHitTestPrimitiveKind.PathFill, primitive.Kind);
        Assert.Equal(83, primitive.Id);
        Assert.Equal(3, index.PathSegments.Count);
    }

    [Fact]
    public void RenderCommandCacheBuildsQuadAsRenderedTrianglePathFills()
    {
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.FillQuad,
            HitTestId = 84,
            Position = new Vector2(0f, 0f),
            Position2 = new Vector2(10f, 0f),
            Position3 = new Vector2(2f, 2f),
            Position4 = new Vector2(0f, 10f),
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.Identity);

        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        Assert.Equal(2, index.Primitives.Count);
        Assert.All(index.Primitives, primitive =>
        {
            Assert.Equal(GpuHitTestPrimitiveKind.PathFill, primitive.Kind);
            Assert.Equal(84, primitive.Id);
        });
        Assert.Equal(6, index.PathSegments.Count);
    }

    [Fact]
    public void DrawingContextAppendTranslationInvalidatesRecordedGeometryCache()
    {
        var source = new DrawingContext();
        source.DrawQuadraticBezier(
            new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), 2f),
            new Vector2(0f, 0f),
            new Vector2(10f, 0f),
            new Vector2(10f, 10f));
        Assert.NotNull(source.Commands[0].GeometryCache);

        var target = new DrawingContext();
        target.Append(source, new Vector2(5f, 6f));

        var translated = Assert.Single(target.Commands);
        Assert.Null(translated.GeometryCache);
        Assert.Equal(new Vector2(5f, 6f), translated.Position);
        Assert.Equal(new Vector2(15f, 6f), translated.Position2);
        Assert.Equal(new Vector2(15f, 16f), translated.Position3);
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
    public void RenderCommandCacheFeedsGpuCombinedPathFillHitTesting()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        var combined = new PathGeometry
        {
            IsCombined = true,
            PathA = PrimitivePathGeometry.CreateRectangle(0f, 0f, 20f, 20f),
            PathB = PrimitivePathGeometry.CreateRectangle(5f, 5f, 10f, 10f),
            Op = 0
        };
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = combined,
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.Identity, id: 92);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(GpuHitTestPrimitiveKind.PathFill, primitive.Kind);
        Assert.True(index.PathSegments.Count > 0);

        bool outerHit = GpuHitTestEngine.TryHitTestPoint(context, index, new Vector2(2f, 2f), out GpuHitTestResult outerResult);
        bool holeHit = GpuHitTestEngine.TryHitTestPoint(context, index, new Vector2(10f, 10f), out GpuHitTestResult holeResult);

        Assert.True(outerHit);
        Assert.Equal(92, outerResult.Id);
        Assert.False(holeHit);
        Assert.False(holeResult.HasHit);
        Assert.True(holeResult.CandidateCount > 0);
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
    public void RenderCommandCacheFeedsGpuProviderPolylineStrokeHitTesting()
    {
        using var gpu = new WgpuContext();
        gpu.Initialize(null);

        var context = new DrawingContext();
        context.DrawPolyline(
            new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), 2f),
            [
                new Vector2(0f, 0f),
                new Vector2(10f, 0f),
                new Vector2(10f, 10f)
            ]);

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(context.Commands[0], Matrix4x4.Identity, context, id: 93);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        bool strokeHit = GpuHitTestEngine.TryHitTestPoint(gpu, index, new Vector2(5f, 0.5f), out GpuHitTestResult result);
        bool boundsOnlyMiss = GpuHitTestEngine.TryHitTestPoint(gpu, index, new Vector2(5f, 5f), out GpuHitTestResult missResult);

        Assert.True(strokeHit);
        Assert.Equal(93, result.Id);
        Assert.False(boundsOnlyMiss);
        Assert.False(missResult.HasHit);
        Assert.True(missResult.CandidateCount > 0);
    }

    [Fact]
    public void RenderCommandCacheFeedsGpuTriangleFillHitTesting()
    {
        using var gpu = new WgpuContext();
        gpu.Initialize(null);

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.FillTriangle,
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            Position = new Vector2(0f, 0f),
            Position2 = new Vector2(10f, 0f),
            Position3 = new Vector2(0f, 10f)
        }, Matrix4x4.Identity, id: 94);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        bool fillHit = GpuHitTestEngine.TryHitTestPoint(gpu, index, new Vector2(2f, 2f), out GpuHitTestResult result);
        bool boundsOnlyMiss = GpuHitTestEngine.TryHitTestPoint(gpu, index, new Vector2(8f, 8f), out GpuHitTestResult missResult);

        Assert.True(fillHit);
        Assert.Equal(94, result.Id);
        Assert.False(boundsOnlyMiss);
        Assert.False(missResult.HasHit);
        Assert.True(missResult.CandidateCount > 0);
    }

    [Fact]
    public void RenderCommandCacheFeedsGpuQuadFillHitTesting()
    {
        using var gpu = new WgpuContext();
        gpu.Initialize(null);

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.FillQuad,
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            Position = new Vector2(0f, 0f),
            Position2 = new Vector2(10f, 0f),
            Position3 = new Vector2(2f, 2f),
            Position4 = new Vector2(0f, 10f)
        }, Matrix4x4.Identity, id: 95);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        bool fillHit = GpuHitTestEngine.TryHitTestPoint(gpu, index, new Vector2(1f, 1f), out GpuHitTestResult result);
        bool boundsOnlyMiss = GpuHitTestEngine.TryHitTestPoint(gpu, index, new Vector2(9f, 1.5f), out GpuHitTestResult missResult);

        Assert.True(fillHit);
        Assert.Equal(95, result.Id);
        Assert.False(boundsOnlyMiss);
        Assert.False(missResult.HasHit);
        Assert.True(missResult.CandidateCount > 0);
    }

    [Fact]
    public void RenderCommandCacheFeedsGpuQuadraticBezierStrokeHitTesting()
    {
        using var gpu = new WgpuContext();
        gpu.Initialize(null);

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawBezier,
            Pen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), 2f),
            Position = new Vector2(0f, 0f),
            Position2 = new Vector2(10f, 0f),
            Position3 = new Vector2(10f, 10f)
        }, Matrix4x4.Identity, id: 96);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        bool strokeHit = GpuHitTestEngine.TryHitTestPoint(gpu, index, new Vector2(7.5f, 2.5f), out GpuHitTestResult result);
        bool boundsOnlyMiss = GpuHitTestEngine.TryHitTestPoint(gpu, index, new Vector2(2f, 8f), out GpuHitTestResult missResult);

        Assert.True(strokeHit);
        Assert.Equal(96, result.Id);
        Assert.False(boundsOnlyMiss);
        Assert.False(missResult.HasHit);
        Assert.True(missResult.CandidateCount > 0);
    }

    [Fact]
    public void RenderCommandCacheFeedsGpuCubicBezierStrokeHitTesting()
    {
        using var gpu = new WgpuContext();
        gpu.Initialize(null);

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawCubicBezier,
            Pen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), 2f),
            Position = new Vector2(0f, 0f),
            Position2 = new Vector2(10f, 0f),
            Position3 = new Vector2(10f, 10f),
            Position4 = new Vector2(0f, 10f)
        }, Matrix4x4.Identity, id: 97);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        bool strokeHit = GpuHitTestEngine.TryHitTestPoint(gpu, index, new Vector2(7.5f, 5f), out GpuHitTestResult result);
        bool boundsOnlyMiss = GpuHitTestEngine.TryHitTestPoint(gpu, index, new Vector2(0.5f, 5f), out GpuHitTestResult missResult);

        Assert.True(strokeHit);
        Assert.Equal(97, result.Id);
        Assert.False(boundsOnlyMiss);
        Assert.False(missResult.HasHit);
        Assert.True(missResult.CandidateCount > 0);
    }

    [Fact]
    public void RenderCommandCacheFeedsGpuCircleHitTesting()
    {
        using var gpu = new WgpuContext();
        gpu.Initialize(null);

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawCircle,
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            Position2 = new Vector2(10f, 10f),
            RadiusX = 5f
        }, Matrix4x4.Identity, id: 98);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        bool hit = GpuHitTestEngine.TryHitTestPoint(gpu, index, new Vector2(10f, 10f), out GpuHitTestResult result);
        bool cornerMiss = GpuHitTestEngine.TryHitTestPoint(gpu, index, new Vector2(5f, 5f), out GpuHitTestResult missResult);

        Assert.True(hit);
        Assert.Equal(98, result.Id);
        Assert.False(cornerMiss);
        Assert.False(missResult.HasHit);
        Assert.True(missResult.CandidateCount > 0);
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
    public void TryHitTestPointAllReportsTotalHitCountWhenCallerCapacityTruncates()
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
        Assert.Equal(3u, summary.Hit);
        Assert.Equal([30, 20], results.Take(hitCount).Select(result => result.Id).ToArray());
    }

    [Fact]
    public void TryHitTestPointAllStoresDistinctIdsAndKeepsHighestZDuplicate()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleFill(20, new Vector2(0f, 0f), new Vector2(100f, 100f), Vector2.Zero, zIndex: 0f),
            GpuHitTestPrimitive.RectangleFill(10, new Vector2(0f, 0f), new Vector2(100f, 100f), Vector2.Zero, zIndex: 1f),
            GpuHitTestPrimitive.RectangleFill(20, new Vector2(0f, 0f), new Vector2(100f, 100f), Vector2.Zero, zIndex: 2f)
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
        Assert.Equal(3u, summary.Hit);
        Assert.Equal([20, 10], results.Take(hitCount).Select(result => result.Id).ToArray());
        Assert.Equal(2f, results[0].ZIndex);
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
        Assert.Equal(2u, summary.PreciseTests);
        Assert.True(summary.NodesVisited > 0);
    }

    [Fact]
    public void TryQueryBoundsAllStoresDistinctIdsAndKeepsHighestZDuplicate()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleFill(20, new Vector2(0f, 0f), new Vector2(100f, 100f), Vector2.Zero, zIndex: 0f),
            GpuHitTestPrimitive.RectangleFill(10, new Vector2(0f, 0f), new Vector2(100f, 100f), Vector2.Zero, zIndex: 1f),
            GpuHitTestPrimitive.RectangleFill(20, new Vector2(0f, 0f), new Vector2(100f, 100f), Vector2.Zero, zIndex: 2f)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 4, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[2];

        bool hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(25f, 25f),
            new Vector2(75f, 75f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.True(hit);
        Assert.Equal(2, hitCount);
        Assert.Equal(3u, summary.Hit);
        Assert.Equal([20, 10], results.Take(hitCount).Select(result => result.Id).ToArray());
        Assert.Equal(2f, results[0].ZIndex);
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
    public void TryQueryBoundsAllRejectsRoundedRectangleCornerFalsePositiveOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleFill(39, new Vector2(0f, 0f), new Vector2(10f, 10f), new Vector2(4f, 4f))
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(0f, 0f),
            new Vector2(1f, 1f),
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
            new Vector2(0f, 4f),
            new Vector2(1f, 5f),
            results,
            out hitCount,
            out summary);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal(39, results[0].Id);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.FullyContains, results[0].IntersectionDetail);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryBoundsAllRejectsRectangleStrokeHoleOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleStroke(
                40,
                new Vector2(0f, 0f),
                new Vector2(10f, 10f),
                Vector2.Zero,
                2f)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(4f, 4f),
            new Vector2(6f, 6f),
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
            new Vector2(0f, -0.5f),
            new Vector2(1f, 0.5f),
            results,
            out hitCount,
            out summary);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal(40, results[0].Id);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.Intersects, results[0].IntersectionDetail);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryEllipseAllRejectsQueryBoundsCornerFalsePositiveOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleFill(41, new Vector2(9f, 9f), new Vector2(10f, 10f), Vector2.Zero)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryEllipseAll(
            context,
            index,
            new Vector2(0f, 0f),
            new Vector2(10f, 10f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.False(hit);
        Assert.Equal(0, hitCount);
        Assert.Equal(0u, summary.CandidateCount);
    }

    [Fact]
    public void TryQueryEllipseAllClassifiesRectangleFillIntersectionDetailOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleFill(42, new Vector2(4f, 4f), new Vector2(6f, 6f), Vector2.Zero, zIndex: 1f),
            GpuHitTestPrimitive.RectangleFill(43, new Vector2(0f, 0f), new Vector2(10f, 10f), Vector2.Zero, zIndex: 0f)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryEllipseAll(
            context,
            index,
            new Vector2(0f, 0f),
            new Vector2(10f, 10f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.True(hit);
        Assert.Equal(2, hitCount);
        Assert.Equal([42, 43], results.Take(hitCount).Select(result => result.Id).ToArray());
        Assert.Equal(
            [(uint)GpuHitTestIntersectionDetail.FullyInside, (uint)GpuHitTestIntersectionDetail.FullyContains],
            results.Take(hitCount).Select(result => result.IntersectionDetail).ToArray());
        Assert.Equal(2u, summary.CandidateCount);

        Array.Clear(results);
        hit = GpuHitTestEngine.TryQueryEllipseAll(
            context,
            index,
            new Vector2(3f, 3f),
            new Vector2(7f, 7f),
            results,
            out hitCount,
            out summary);

        Assert.True(hit);
        Assert.Equal(2, hitCount);
        Assert.Equal([42, 43], results.Take(hitCount).Select(result => result.Id).ToArray());
        Assert.Equal(
            [(uint)GpuHitTestIntersectionDetail.FullyInside, (uint)GpuHitTestIntersectionDetail.FullyContains],
            results.Take(hitCount).Select(result => result.IntersectionDetail).ToArray());
        Assert.Equal(2u, summary.CandidateCount);
    }

    [Fact]
    public void TryQueryEllipseAllRejectsEllipseFillQueryBoundsFalsePositiveOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.EllipseFill(44, new Vector2(8.5f, 8.5f), new Vector2(9.5f, 9.5f))
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryEllipseAll(
            context,
            index,
            new Vector2(0f, 0f),
            new Vector2(10f, 10f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.False(hit);
        Assert.Equal(0, hitCount);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryEllipseAllClassifiesSameCenterEllipseFillDetailOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.EllipseFill(45, new Vector2(2f, 2f), new Vector2(8f, 8f), zIndex: 1f),
            GpuHitTestPrimitive.EllipseFill(46, new Vector2(-1f, -1f), new Vector2(11f, 11f), zIndex: 0f)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryEllipseAll(
            context,
            index,
            new Vector2(0f, 0f),
            new Vector2(10f, 10f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.True(hit);
        Assert.Equal(2, hitCount);
        Assert.Equal([45, 46], results.Take(hitCount).Select(result => result.Id).ToArray());
        Assert.Equal(
            [(uint)GpuHitTestIntersectionDetail.FullyInside, (uint)GpuHitTestIntersectionDetail.FullyContains],
            results.Take(hitCount).Select(result => result.IntersectionDetail).ToArray());
        Assert.Equal(2u, summary.CandidateCount);
        Assert.Equal(2u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryEllipseAllRejectsEllipseStrokeHoleOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.EllipseStroke(47, new Vector2(0f, 0f), new Vector2(10f, 10f), strokeThickness: 2f)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryEllipseAll(
            context,
            index,
            new Vector2(4.25f, 4.25f),
            new Vector2(5.75f, 5.75f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.False(hit);
        Assert.Equal(0, hitCount);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryEllipseAllRejectsRoundedRectangleCornerFalsePositiveOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleFill(54, new Vector2(0f, 0f), new Vector2(10f, 10f), new Vector2(4f, 4f))
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryEllipseAll(
            context,
            index,
            new Vector2(0f, 0f),
            new Vector2(1f, 1f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.False(hit);
        Assert.Equal(0, hitCount);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryEllipseAllKeepsRoundedRectangleFillOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleFill(55, new Vector2(0f, 0f), new Vector2(10f, 10f), new Vector2(4f, 4f))
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryEllipseAll(
            context,
            index,
            new Vector2(4f, 4f),
            new Vector2(6f, 6f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal(55, results[0].Id);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.FullyContains, results[0].IntersectionDetail);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryEllipseAllRejectsRectangleStrokeHoleOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleStroke(
                56,
                new Vector2(0f, 0f),
                new Vector2(10f, 10f),
                Vector2.Zero,
                2f)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryEllipseAll(
            context,
            index,
            new Vector2(4f, 4f),
            new Vector2(6f, 6f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.False(hit);
        Assert.Equal(0, hitCount);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryEllipseAllKeepsRectangleStrokeOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleStroke(
                57,
                new Vector2(0f, 0f),
                new Vector2(10f, 10f),
                Vector2.Zero,
                2f)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryEllipseAll(
            context,
            index,
            new Vector2(-0.5f, 4f),
            new Vector2(1.5f, 6f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal(57, results[0].Id);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.Intersects, results[0].IntersectionDetail);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryEllipseAllRejectsLineStrokeBoundsFalsePositiveOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.LineStroke(
                48,
                new Vector2(9.2f, 9.2f),
                new Vector2(9.8f, 9.8f),
                strokeThickness: 1.5f,
                LineGeometryCap.Flat,
                LineGeometryCap.Flat)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryEllipseAll(
            context,
            index,
            new Vector2(0f, 0f),
            new Vector2(10f, 10f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.False(hit);
        Assert.Equal(0, hitCount);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryEllipseAllKeepsIntersectingLineStrokeOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.LineStroke(
                49,
                new Vector2(5f, -1f),
                new Vector2(5f, 11f),
                strokeThickness: 1f,
                LineGeometryCap.Flat,
                LineGeometryCap.Flat)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryEllipseAll(
            context,
            index,
            new Vector2(0f, 0f),
            new Vector2(10f, 10f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal(49, results[0].Id);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.Intersects, results[0].IntersectionDetail);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryEllipseAllRejectsPathFillDifferenceHoleOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        var combined = new PathGeometry
        {
            IsCombined = true,
            PathA = PrimitivePathGeometry.CreateRectangle(0f, 0f, 20f, 20f),
            PathB = PrimitivePathGeometry.CreateRectangle(5f, 5f, 10f, 10f),
            Op = 0
        };
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = combined,
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.Identity, id: 50);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryEllipseAll(
            context,
            index,
            new Vector2(8f, 8f),
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
    public void TryQueryEllipseAllKeepsIntersectingPathFillOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = CreateTrianglePath(),
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.Identity, id: 51);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryEllipseAll(
            context,
            index,
            new Vector2(1f, 1f),
            new Vector2(2f, 2f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal(51, results[0].Id);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.Intersects, results[0].IntersectionDetail);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryEllipseAllRejectsPathStrokeBoundsFalsePositiveOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = CreateDiagonalLinePath(),
            Pen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), 1f)
        }, Matrix4x4.Identity, id: 52);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryEllipseAll(
            context,
            index,
            new Vector2(0f, 9f),
            new Vector2(2f, 11f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.False(hit);
        Assert.Equal(0, hitCount);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryEllipseAllKeepsIntersectingPathStrokeOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = CreateDiagonalLinePath(),
            Pen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), 1f)
        }, Matrix4x4.Identity, id: 53);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryEllipseAll(
            context,
            index,
            new Vector2(4.5f, 4.5f),
            new Vector2(5.5f, 5.5f),
            results,
            out int hitCount,
            out GpuHitTestResult summary);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal(53, results[0].Id);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.Intersects, results[0].IntersectionDetail);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
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
    public void TryQueryBoundsAllRejectsLineStrokeBoundsFalsePositiveOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.LineStroke(
                37,
                new Vector2(0f, 0f),
                new Vector2(10f, 10f),
                1f,
                LineGeometryCap.Flat,
                LineGeometryCap.Flat)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
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
        Assert.Equal(37, results[0].Id);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.Intersects, results[0].IntersectionDetail);
        Assert.Equal(1u, summary.CandidateCount);
        Assert.Equal(1u, summary.PreciseTests);
    }

    [Fact]
    public void TryQueryBoundsAllRejectsLineStrokeFlatCapFalsePositiveOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.LineStroke(
                38,
                new Vector2(0f, 0f),
                new Vector2(10f, 0f),
                2f,
                LineGeometryCap.Flat,
                LineGeometryCap.Flat)
        ];
        var index = GpuHitTestIndex.Build(primitives, maxDepth: 2, maxPrimitivesPerNode: 1);
        var results = new GpuHitTestResult[4];

        bool hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(-0.9f, -0.2f),
            new Vector2(-0.8f, 0.2f),
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
            new Vector2(0.1f, -0.2f),
            new Vector2(0.2f, 0.2f),
            results,
            out hitCount,
            out summary);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal(38, results[0].Id);
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
    public void TryQueryBoundsAllRejectsCombinedPathDifferenceHoleOnGpu()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        var combined = new PathGeometry
        {
            IsCombined = true,
            PathA = PrimitivePathGeometry.CreateRectangle(0f, 0f, 20f, 20f),
            PathB = PrimitivePathGeometry.CreateRectangle(5f, 5f, 10f, 10f),
            Op = 0
        };
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = combined,
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        }, Matrix4x4.Identity, id: 93);
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

        Array.Clear(results);
        hit = GpuHitTestEngine.TryQueryBoundsAll(
            context,
            index,
            new Vector2(1f, 1f),
            new Vector2(2f, 2f),
            results,
            out hitCount,
            out summary);

        Assert.True(hit);
        Assert.Equal(1, hitCount);
        Assert.Equal(93, results[0].Id);
        Assert.Equal((uint)GpuHitTestIntersectionDetail.FullyContains, results[0].IntersectionDetail);
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

    private sealed class CountingPathHitTestCompilationCache : IPathHitTestCompilationCache
    {
        private readonly Dictionary<PathGeometry, CachedPathData> _cache = new();

        public int CallCount { get; private set; }
        public int CompileCount { get; private set; }

        public void Prewarm(PathGeometry path)
        {
            _cache[path] = Compile(path);
        }

        public bool TryGetCompiledHitTestPath(
            PathGeometry path,
            out GpuPathRecord[] records,
            out GpuPathSegment[] segments,
            out float localMinX,
            out float localMinY,
            out float localMaxX,
            out float localMaxY)
        {
            CallCount++;
            if (!_cache.TryGetValue(path, out var cached))
            {
                CompileCount++;
                cached = Compile(path);
                _cache[path] = cached;
            }

            records = cached.Records;
            segments = cached.Segments;
            localMinX = cached.LocalMinX;
            localMinY = cached.LocalMinY;
            localMaxX = cached.LocalMaxX;
            localMaxY = cached.LocalMaxY;
            return segments.Length != 0;
        }

        private static CachedPathData Compile(PathGeometry path)
        {
            var (records, segments) = PathAtlas.CompilePath(
                path,
                out float localMinX,
                out float localMinY,
                out float localMaxX,
                out float localMaxY);
            return new CachedPathData(records, segments, localMinX, localMinY, localMaxX, localMaxY);
        }

        private readonly record struct CachedPathData(
            GpuPathRecord[] Records,
            GpuPathSegment[] Segments,
            float LocalMinX,
            float LocalMinY,
            float LocalMaxX,
            float LocalMaxY);
    }
}
