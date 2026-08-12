using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class DashCapSemanticsTests
{
    [Fact]
    public void LoweredOpenDashUsesDashCapsInternallyAndSourceCapsOnlyAtReachedEndpoints()
    {
        var path = CreateLinePath(Vector2.Zero, new Vector2(34f, 0f));
        var cache = RenderCommandGeometryCache.ForStrokePath(path);
        var pen = CreateDistinctCapPen();

        Assert.True(cache.TryGetDashedStrokePath(pen, out var dashedPath, out var loweredPen));

        Assert.Equal(PenLineCap.Round, loweredPen.StartLineCap);
        Assert.Equal(PenLineCap.Round, loweredPen.EndLineCap);
        Assert.Equal(PenLineCap.Round, loweredPen.DashCap);
        Assert.Collection(
            dashedPath.Figures,
            first =>
            {
                Assert.Equal(PenLineCap.Flat, first.StrokeStartLineCap);
                Assert.Null(first.StrokeEndLineCap);
                AssertLine(first, new Vector2(0f, 0f), new Vector2(8f, 0f));
            },
            middle =>
            {
                Assert.Null(middle.StrokeStartLineCap);
                Assert.Null(middle.StrokeEndLineCap);
                AssertLine(middle, new Vector2(16f, 0f), new Vector2(24f, 0f));
            },
            last =>
            {
                Assert.Null(last.StrokeStartLineCap);
                Assert.Equal(PenLineCap.Square, last.StrokeEndLineCap);
                AssertLine(last, new Vector2(32f, 0f), new Vector2(34f, 0f));
            });

        var transformed = dashedPath.CreateTransformed(Matrix4x4.CreateTranslation(5f, 7f, 0f));
        Assert.Equal(PenLineCap.Flat, transformed.Figures[0].StrokeStartLineCap);
        Assert.Equal(PenLineCap.Square, transformed.Figures[^1].StrokeEndLineCap);

        Assert.True(cache.TryGetDashedStrokePath(pen, out var cachedPath, out var cachedPen));
        Assert.Same(dashedPath, cachedPath);
        Assert.Same(loweredPen, cachedPen);
    }

    [Fact]
    public void DashedGeometryCacheInvalidatesWhenAnySourceCapChanges()
    {
        var path = CreateLinePath(Vector2.Zero, new Vector2(34f, 0f));
        var cache = RenderCommandGeometryCache.ForStrokePath(path);
        var firstPen = CreateDistinctCapPen();

        Assert.True(cache.TryGetDashedStrokePath(firstPen, out var firstPath, out _));

        var secondPen = new Pen(
            firstPen.Brush,
            firstPen.Thickness,
            startLineCap: PenLineCap.Triangle,
            endLineCap: PenLineCap.Flat,
            dashCap: PenLineCap.Square,
            dashArray: firstPen.DashArray,
            dashOffset: firstPen.DashOffset);
        Assert.True(cache.TryGetDashedStrokePath(secondPen, out var secondPath, out var secondLoweredPen));

        Assert.NotSame(firstPath, secondPath);
        Assert.Equal(PenLineCap.Triangle, secondPath.Figures[0].StrokeStartLineCap);
        Assert.Equal(PenLineCap.Flat, secondPath.Figures[^1].StrokeEndLineCap);
        Assert.Equal(PenLineCap.Square, secondLoweredPen.StartLineCap);
        Assert.Equal(PenLineCap.Square, secondLoweredPen.EndLineCap);
    }

    [Fact]
    public void SourceCapsAreNotAppliedWhenTheDashPatternLeavesBothEndpointsInGaps()
    {
        var path = CreateLinePath(Vector2.Zero, new Vector2(6f, 0f));
        var pen = new Pen(
            new SolidColorBrush(Vector4.One),
            thickness: 1f,
            startLineCap: PenLineCap.Triangle,
            endLineCap: PenLineCap.Square,
            dashCap: PenLineCap.Round,
            dashArray: [2.0, 2.0],
            dashOffset: 2.0);

        Assert.True(Compositor.TryCreateDashedStrokePath(path, pen, out var dashedPath));

        var dash = Assert.Single(dashedPath.Figures);
        AssertLine(dash, new Vector2(2f, 0f), new Vector2(4f, 0f));
        Assert.Null(dash.StrokeStartLineCap);
        Assert.Null(dash.StrokeEndLineCap);
    }

    [Fact]
    public void LoweredClosedDashMergesTheCyclicRunAcrossTheSourceSeam()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(Vector2.Zero, isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(4f, 0f)));
        figure.Segments.Add(new LineSegment(new Vector2(4f, 4f)));
        figure.Segments.Add(new LineSegment(new Vector2(0f, 4f)));
        path.Figures.Add(figure);
        var cache = RenderCommandGeometryCache.ForStrokePath(path);
        var pen = new Pen(
            new SolidColorBrush(Vector4.One),
            thickness: 1f,
            startLineCap: PenLineCap.Square,
            endLineCap: PenLineCap.Triangle,
            dashCap: PenLineCap.Round,
            dashArray: [6.0, 4.0]);

        Assert.True(cache.TryGetDashedStrokePath(pen, out var dashedPath, out _));

        var cyclicRun = Assert.Single(dashedPath.Figures);
        Assert.False(cyclicRun.IsClosed);
        Assert.Null(cyclicRun.StrokeStartLineCap);
        Assert.Null(cyclicRun.StrokeEndLineCap);
        Assert.Equal(new Vector2(2f, 4f), cyclicRun.StartPoint);
        Assert.Equal(4, cyclicRun.Segments.Count);
        Assert.Equal(new Vector2(0f, 4f), Assert.IsType<LineSegment>(cyclicRun.Segments[0]).Point);
        Assert.Equal(Vector2.Zero, Assert.IsType<LineSegment>(cyclicRun.Segments[1]).Point);
        Assert.Equal(new Vector2(4f, 0f), Assert.IsType<LineSegment>(cyclicRun.Segments[2]).Point);
        Assert.Equal(new Vector2(4f, 2f), Assert.IsType<LineSegment>(cyclicRun.Segments[3]).Point);
    }

    [Fact]
    public void LoweredClosedDashMarksAContourCoveredByOneRunAsClosed()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(Vector2.Zero, isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(4f, 0f)));
        figure.Segments.Add(new LineSegment(new Vector2(4f, 4f)));
        figure.Segments.Add(new LineSegment(new Vector2(0f, 4f)));
        path.Figures.Add(figure);
        var pen = new Pen(
            new SolidColorBrush(Vector4.One),
            thickness: 1f,
            dashCap: PenLineCap.Round,
            dashArray: [20.0, 1.0]);

        Assert.True(Compositor.TryCreateDashedStrokePath(path, pen, out var dashedPath));

        var closedRun = Assert.Single(dashedPath.Figures);
        Assert.True(closedRun.IsClosed);
        Assert.Null(closedRun.StrokeStartLineCap);
        Assert.Null(closedRun.StrokeEndLineCap);
    }

    [Fact]
    public void DistinctLoweredLineCapsFeedExactGpuHitPrimitives()
    {
        using var gpu = new WgpuContext();
        gpu.Initialize(null);

        var path = CreateLinePath(new Vector2(10f, 16f), new Vector2(44f, 16f));
        var pen = CreateDistinctCapPen(thickness: 4f, dashArray: [2.0, 2.0]);
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            HitTestId = 417,
            Path = path,
            Pen = pen,
            GeometryCache = RenderCommandGeometryCache.ForStrokePath(path)
        }, Matrix4x4.Identity);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        Assert.Collection(
            index.Primitives,
            first =>
            {
                AssertCaps(first, LineGeometryCap.Flat, LineGeometryCap.Round);
                Assert.Equal(new Vector4(10f, 16f, 18f, 16f), first.Data0);
            },
            middle => AssertCaps(middle, LineGeometryCap.Round, LineGeometryCap.Round),
            last => AssertCaps(last, LineGeometryCap.Round, LineGeometryCap.Square));
        Assert.Empty(index.PathSegments);

        Assert.False(TryHit(gpu, index, 9f));
        Assert.True(TryHit(gpu, index, 12f));
        Assert.False(TryHit(gpu, index, 22f));
        Assert.True(TryHit(gpu, index, 45f));
        Assert.False(TryHit(gpu, index, 47f));
    }

    [Fact]
    public void CurvedLoweredDashUsesCapAwarePerFigurePathHitPrimitives()
    {
        using var gpu = new WgpuContext();
        gpu.Initialize(null);

        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(10f, 16f));
        figure.Segments.Add(new QuadraticBezierSegment(
            new Vector2(26f, 4f),
            new Vector2(44f, 16f)));
        path.Figures.Add(figure);
        var pen = CreateDistinctCapPen(thickness: 4f, dashArray: [2.0, 2.0]);
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            HitTestId = 417,
            Path = path,
            Pen = pen,
            GeometryCache = RenderCommandGeometryCache.ForStrokePath(path)
        }, Matrix4x4.Identity);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        Assert.True(index.Primitives.Count >= 2);
        Assert.All(index.Primitives, primitive =>
            Assert.Equal(GpuHitTestPrimitiveKind.PathStroke, primitive.Kind));
        Assert.Equal((float)(uint)LineGeometryCap.Flat, index.Primitives[0].Data2.X);
        Assert.Equal((float)(uint)LineGeometryCap.Round, index.Primitives[0].Data2.Y);
        Assert.Equal(new Vector2(10f, 16f), index.PathSegments[0].P0);
        Assert.True(index.Primitives[0].Data0.Z < index.Primitives[^1].Data0.X);
        Assert.True(index.Primitives[0].BoundsMax.X < index.Primitives[^1].BoundsMin.X);

        var outsideHit = GpuHitTestEngine.TryHitTestPoint(
            gpu,
            index,
            new Vector2(9f, 16f),
            out var outsideResult);
        Assert.False(
            outsideHit,
            $"primitive={outsideResult.PrimitiveIndex}; " +
            string.Join(" | ", index.Primitives.Select(static primitive =>
                $"segments={primitive.Data1.X}:{primitive.Data1.Y} caps={primitive.Data2.X}:{primitive.Data2.Y}")));
        Assert.True(TryHit(gpu, index, 11f));
    }

    [Fact]
    public void FlatDashCapWithoutEndpointOverridesStillUsesFlatHitPrimitives()
    {
        using var gpu = new WgpuContext();
        gpu.Initialize(null);

        var path = CreateLinePath(new Vector2(10f, 16f), new Vector2(44f, 16f));
        var pen = new Pen(
            new SolidColorBrush(Vector4.One),
            thickness: 4f,
            startLineCap: PenLineCap.Flat,
            endLineCap: PenLineCap.Flat,
            dashCap: PenLineCap.Flat,
            dashArray: [2.0, 2.0]);
        var cache = RenderCommandGeometryCache.ForStrokePath(path);
        Assert.True(cache.TryGetDashedStrokePath(pen, out var loweredPath, out _));
        Assert.All(loweredPath.Figures, figure =>
        {
            Assert.Null(figure.StrokeStartLineCap);
            Assert.Null(figure.StrokeEndLineCap);
        });

        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            HitTestId = 417,
            Path = path,
            Pen = pen,
            GeometryCache = cache
        }, Matrix4x4.Identity);
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        Assert.All(index.Primitives, primitive =>
            AssertCaps(primitive, LineGeometryCap.Flat, LineGeometryCap.Flat));
        Assert.False(TryHit(gpu, index, 9f));
        Assert.True(TryHit(gpu, index, 11f));
    }

    [Fact]
    public void PlainFlatQuadraticRejectsTheRoundEndpointHalo()
    {
        using var gpu = new WgpuContext();
        gpu.Initialize(null);

        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(10f, 16f));
        figure.Segments.Add(new QuadraticBezierSegment(
            new Vector2(26f, 4f),
            new Vector2(44f, 16f)));
        path.Figures.Add(figure);
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            HitTestId = 417,
            Path = path,
            Pen = new Pen(
                new SolidColorBrush(Vector4.One),
                thickness: 4f,
                startLineCap: PenLineCap.Flat,
                endLineCap: PenLineCap.Flat),
            GeometryCache = RenderCommandGeometryCache.ForStrokePath(path)
        }, Matrix4x4.Identity);
        var index = builder.BuildIndex();

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(GpuHitTestPrimitiveKind.PathStroke, primitive.Kind);
        Assert.Equal((float)(uint)LineGeometryCap.Flat, primitive.Data2.X);
        Assert.Equal((float)(uint)LineGeometryCap.Flat, primitive.Data2.Y);
        Assert.False(TryHit(gpu, index, 9f));
        Assert.True(TryHit(gpu, index, 11f));
    }

    [Fact]
    public void PlainFlatHairlinePathRejectsTheRoundEndpointHalo()
    {
        using var gpu = new WgpuContext();
        gpu.Initialize(null);

        var path = CreateLinePath(new Vector2(10f, 16f), new Vector2(44f, 16f));
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            HitTestId = 417,
            Path = path,
            Pen = new Pen(
                new SolidColorBrush(Vector4.One),
                thickness: Pen.HairlineThickness,
                startLineCap: PenLineCap.Flat,
                endLineCap: PenLineCap.Flat),
            GeometryCache = RenderCommandGeometryCache.ForStrokePath(path)
        }, Matrix4x4.Identity);
        var index = builder.BuildIndex();

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(GpuHitTestPrimitiveKind.PathStroke, primitive.Kind);
        Assert.Equal((float)(uint)LineGeometryCap.Flat, primitive.Data2.X);
        Assert.Equal((float)(uint)LineGeometryCap.Flat, primitive.Data2.Y);
        Assert.Single(index.PathSegments);
        Assert.False(TryHit(gpu, index, 9.75f));
        Assert.True(TryHit(gpu, index, 10.25f));
    }

    [Fact]
    public void DashedHairlineSplitsCapsPerRunAndKeepsOnlyFramebufferSegments()
    {
        var path = CreateLinePath(new Vector2(10f, 16f), new Vector2(44f, 16f));
        var pen = CreateDistinctCapPen(
            thickness: Pen.HairlineThickness,
            dashArray: [2.0, 2.0]);
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            HitTestId = 417,
            Path = path,
            Pen = pen,
            GeometryCache = RenderCommandGeometryCache.ForStrokePath(path)
        }, Matrix4x4.CreateScale(2f, 1f, 1f));
        var index = builder.BuildIndex(maxDepth: 2, maxPrimitivesPerNode: 1);

        Assert.Equal(9, index.Primitives.Count);
        Assert.Equal(index.Primitives.Count, index.PathSegments.Count);
        Assert.All(index.Primitives, primitive =>
            Assert.Equal(GpuHitTestPrimitiveKind.PathStroke, primitive.Kind));
        Assert.Equal((float)(uint)LineGeometryCap.Flat, index.Primitives[0].Data2.X);
        Assert.Equal((float)(uint)LineGeometryCap.Round, index.Primitives[0].Data2.Y);
        Assert.Equal((float)(uint)LineGeometryCap.Round, index.Primitives[^1].Data2.X);
        Assert.Equal((float)(uint)LineGeometryCap.Square, index.Primitives[^1].Data2.Y);
        Assert.Equal(new Vector2(20f, 16f), index.PathSegments[0].P0);
        Assert.Equal(new Vector2(88f, 16f), index.PathSegments[^1].P1);
    }

    [Theory]
    [InlineData(PenLineCap.Flat, false)]
    [InlineData(PenLineCap.Square, true)]
    [InlineData(PenLineCap.Round, true)]
    [InlineData(PenLineCap.Triangle, true)]
    public void PathStrokeRegionQueriesHonorEndpointCaps(
        PenLineCap cap,
        bool expectedHit)
    {
        using var gpu = new WgpuContext();
        gpu.Initialize(null);

        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(10f, 16f));
        figure.Segments.Add(new QuadraticBezierSegment(
            new Vector2(26f, 16f),
            new Vector2(44f, 16f)));
        path.Figures.Add(figure);
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            HitTestId = 417,
            Path = path,
            Pen = new Pen(
                new SolidColorBrush(Vector4.One),
                thickness: 4f,
                startLineCap: cap,
                endLineCap: cap),
            GeometryCache = RenderCommandGeometryCache.ForStrokePath(path)
        }, Matrix4x4.Identity);
        var index = builder.BuildIndex();
        Assert.Equal(GpuHitTestPrimitiveKind.PathStroke, Assert.Single(index.Primitives).Kind);

        var results = new GpuHitTestResult[2];
        var boundsHit = GpuHitTestEngine.TryQueryBoundsAll(
            gpu,
            index,
            new Vector2(8.8f, 15.8f),
            new Vector2(9.2f, 16.2f),
            results,
            out var boundsHitCount,
            out var boundsSummary);
        Assert.Equal(expectedHit, boundsHit);
        Assert.Equal(expectedHit ? 1 : 0, boundsHitCount);
        Assert.Equal(1u, boundsSummary.PreciseTests);

        Array.Clear(results);
        var ellipseHit = GpuHitTestEngine.TryQueryEllipseAll(
            gpu,
            index,
            new Vector2(8.8f, 15.8f),
            new Vector2(9.2f, 16.2f),
            results,
            out var ellipseHitCount,
            out var ellipseSummary);
        Assert.Equal(expectedHit, ellipseHit);
        Assert.Equal(expectedHit ? 1 : 0, ellipseHitCount);
        Assert.Equal(1u, ellipseSummary.PreciseTests);
    }

    [Theory]
    [InlineData(PenLineCap.Flat, false, false)]
    [InlineData(PenLineCap.Square, true, true)]
    [InlineData(PenLineCap.Round, false, true)]
    [InlineData(PenLineCap.Triangle, false, false)]
    public void PathStrokeRegionQueriesDistinguishCapShapes(
        PenLineCap cap,
        bool expectedSquareCornerHit,
        bool expectedRoundShoulderHit)
    {
        using var gpu = new WgpuContext();
        gpu.Initialize(null);

        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(10f, 16f));
        figure.Segments.Add(new QuadraticBezierSegment(
            new Vector2(26f, 16f),
            new Vector2(44f, 16f)));
        path.Figures.Add(figure);
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            HitTestId = 417,
            Path = path,
            Pen = new Pen(
                new SolidColorBrush(Vector4.One),
                thickness: 4f,
                startLineCap: cap,
                endLineCap: cap),
            GeometryCache = RenderCommandGeometryCache.ForStrokePath(path)
        }, Matrix4x4.Identity);
        var index = builder.BuildIndex();
        var results = new GpuHitTestResult[2];

        var cornerHit = GpuHitTestEngine.TryQueryBoundsAll(
            gpu,
            index,
            new Vector2(8.45f, 17.45f),
            new Vector2(8.55f, 17.55f),
            results,
            out var cornerHitCount,
            out var cornerSummary);
        Assert.Equal(expectedSquareCornerHit, cornerHit);
        Assert.Equal(expectedSquareCornerHit ? 1 : 0, cornerHitCount);
        Assert.Equal(1u, cornerSummary.PreciseTests);

        Array.Clear(results);
        var shoulderHit = GpuHitTestEngine.TryQueryEllipseAll(
            gpu,
            index,
            new Vector2(8.95f, 17.45f),
            new Vector2(9.05f, 17.55f),
            results,
            out var shoulderHitCount,
            out var shoulderSummary);
        Assert.Equal(expectedRoundShoulderHit, shoulderHit);
        Assert.Equal(expectedRoundShoulderHit ? 1 : 0, shoulderHitCount);
        Assert.Equal(1u, shoulderSummary.PreciseTests);
    }

    [Fact]
    public void DistinctDashCapsRenderAtTheirOwnEndpoints()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(56, 32);
        window.Content = new DistinctDashCapVisual();

        try
        {
            window.Render();
            var pixels = window.ReadPixels();

            AssertDark(ReadPixel(pixels, window.Width, 9, 16));
            AssertRed(ReadPixel(pixels, window.Width, 19, 16));
            AssertDark(ReadPixel(pixels, window.Width, 22, 16));
            AssertRed(ReadPixel(pixels, window.Width, 45, 16));
            AssertDark(ReadPixel(pixels, window.Width, 47, 16));
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void HairlineDashUsesUnitPatternBasisAndPreservesTheSentinel()
    {
        var path = CreateLinePath(Vector2.Zero, new Vector2(10f, 0f));
        var pen = CreateDistinctCapPen(thickness: Pen.HairlineThickness, dashArray: [2.0, 2.0]);

        Assert.True(Compositor.TryCreateDashedStrokePath(path, pen, out var dashedPath));
        var loweredPen = Compositor.CreateUndashedPen(pen, localThickness: 1f);

        Assert.True(loweredPen.IsHairline);
        Assert.Equal(Pen.HairlineThickness, loweredPen.Thickness);
        AssertLine(dashedPath.Figures[0], Vector2.Zero, new Vector2(2f, 0f));
        AssertLine(dashedPath.Figures[1], new Vector2(4f, 0f), new Vector2(6f, 0f));
    }

    [Fact]
    public void PictureArchiveRoundTripsPerFigureStrokeCaps()
    {
        var path = CreateLinePath(Vector2.Zero, new Vector2(10f, 0f));
        path.Figures[0].StrokeStartLineCap = PenLineCap.Triangle;
        path.Figures[0].StrokeEndLineCap = PenLineCap.Square;
        var gpuPicture = new GpuPicture(
            [new RenderCommand
            {
                Type = RenderCommandType.DrawPath,
                Path = path,
                Pen = new Pen(new SolidColorBrush(Vector4.One), 2f)
            }],
            [],
            [],
            [],
            []);
        using var picture = new SKPicture(gpuPicture, new SKRect(0f, 0f, 16f, 16f));
        using var data = picture.Serialize();
        using var copy = SKPicture.Deserialize(data);

        var actualFigure = Assert.Single(Assert.Single(copy!.Picture.Commands).Path!.Figures);
        Assert.Equal(PenLineCap.Triangle, actualFigure.StrokeStartLineCap);
        Assert.Equal(PenLineCap.Square, actualFigure.StrokeEndLineCap);
    }

    [Fact]
    public void PictureArchiveStillReadsVersionOnePathsWithoutCapMetadata()
    {
        var path = CreateLinePath(Vector2.Zero, new Vector2(10f, 0f));
        path.Figures[0].StrokeStartLineCap = PenLineCap.Triangle;
        path.Figures[0].StrokeEndLineCap = PenLineCap.Square;
        var legacyTransform = Matrix4x4.CreateScale(3f, 3f, 1f);
        using var gpuPicture = new GpuPicture(
            [new RenderCommand
            {
                Type = RenderCommandType.DrawPath,
                Path = path,
                Pen = new Pen(new SolidColorBrush(Vector4.One), 6f),
                Transform = legacyTransform,
                IsPenThicknessLocal = false
            }],
            [],
            [],
            [],
            []);
        var bytes = PictureArchive.Serialize(
            gpuPicture,
            new SKRect(0f, 0f, 16f, 16f),
            archiveVersion: 1);

        Assert.Equal(1, BitConverter.ToInt32(bytes, sizeof(ulong)));
        using var copy = SKPicture.Deserialize(bytes);

        var actual = Assert.Single(copy!.Picture.Commands);
        Assert.False(actual.IsPenThicknessLocal);
        Assert.Equal(6f, actual.Pen!.Thickness);
        Assert.Equal(legacyTransform, actual.Transform);
        var actualFigure = Assert.Single(actual.Path!.Figures);
        Assert.Null(actualFigure.StrokeStartLineCap);
        Assert.Null(actualFigure.StrokeEndLineCap);
        AssertLine(actualFigure, Vector2.Zero, new Vector2(10f, 0f));
    }

    [Fact]
    public void SkPathOffsetAndReversePreservePerFigureStrokeCaps()
    {
        using var source = new SKPath();
        var sourceFigure = new PathFigure(Vector2.Zero)
        {
            StrokeStartLineCap = PenLineCap.Triangle,
            StrokeEndLineCap = PenLineCap.Square
        };
        sourceFigure.Segments.Add(new LineSegment(new Vector2(10f, 0f)));
        source.Geometry.Figures.Add(sourceFigure);

        using var offset = new SKPath(source);
        offset.Offset(3f, 5f);
        var offsetFigure = Assert.Single(offset.Geometry.Figures);
        Assert.Equal(PenLineCap.Triangle, offsetFigure.StrokeStartLineCap);
        Assert.Equal(PenLineCap.Square, offsetFigure.StrokeEndLineCap);

        using var reversed = new SKPath();
#pragma warning disable CS0618
        reversed.AddPathReverse(source);
#pragma warning restore CS0618
        var reversedFigure = Assert.Single(reversed.Geometry.Figures);
        Assert.Equal(PenLineCap.Square, reversedFigure.StrokeStartLineCap);
        Assert.Equal(PenLineCap.Triangle, reversedFigure.StrokeEndLineCap);
    }

    private static PathGeometry CreateLinePath(Vector2 start, Vector2 end)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(start);
        figure.Segments.Add(new LineSegment(end));
        path.Figures.Add(figure);
        return path;
    }

    private static Pen CreateDistinctCapPen(float thickness = 1f, double[]? dashArray = null) =>
        new(
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            thickness,
            startLineCap: PenLineCap.Flat,
            endLineCap: PenLineCap.Square,
            dashCap: PenLineCap.Round,
            dashArray: dashArray ?? [8.0, 8.0]);

    private static void AssertLine(PathFigure figure, Vector2 start, Vector2 end)
    {
        Assert.False(figure.IsClosed);
        Assert.Equal(start, figure.StartPoint);
        Assert.Equal(end, Assert.IsType<LineSegment>(Assert.Single(figure.Segments)).Point);
    }

    private static void AssertCaps(
        GpuHitTestPrimitive primitive,
        LineGeometryCap startCap,
        LineGeometryCap endCap)
    {
        Assert.Equal(GpuHitTestPrimitiveKind.LineStroke, primitive.Kind);
        Assert.Equal((float)(uint)startCap, primitive.Data1.Z);
        Assert.Equal((float)(uint)endCap, primitive.Data1.W);
    }

    private static bool TryHit(WgpuContext gpu, GpuHitTestIndex index, float x) =>
        GpuHitTestEngine.TryHitTestPoint(
            gpu,
            index,
            new Vector2(x, 16f),
            out GpuHitTestResult result) && result.Id == 417;

    private static RgbaPixel ReadPixel(byte[] pixels, uint width, int x, int y)
    {
        var index = ((y * (int)width) + x) * 4;
        return new RgbaPixel(
            pixels[index],
            pixels[index + 1],
            pixels[index + 2],
            pixels[index + 3]);
    }

    private static void AssertRed(RgbaPixel pixel) =>
        Assert.True(pixel.R >= 160 && pixel.G <= 64 && pixel.B <= 64, $"Expected red, found {pixel}.");

    private static void AssertDark(RgbaPixel pixel) =>
        Assert.True(pixel.R <= 48 && pixel.G <= 48 && pixel.B <= 48, $"Expected dark, found {pixel}.");

    private readonly record struct RgbaPixel(byte R, byte G, byte B, byte A);

    private sealed class DistinctDashCapVisual : FrameworkElement
    {
        public DistinctDashCapVisual()
        {
            Width = 56f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 56f, 32f));
            context.DrawPath(
                null,
                CreateDistinctCapPen(thickness: 4f, dashArray: [2.0, 2.0]),
                CreateLinePath(new Vector2(10f, 16f), new Vector2(44f, 16f)));
        }
    }
}
