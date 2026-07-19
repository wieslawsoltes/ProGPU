using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GdiBitmap = System.Drawing.Bitmap;
using GdiGraphics = System.Drawing.Graphics;
using GdiInterpolationMode = System.Drawing.Drawing2D.InterpolationMode;
using GdiRectangle = System.Drawing.Rectangle;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Scene.Extensions;
using ProGPU.Tests.Headless;
using ProGPU.Text;
using ProGPU.Transpiler;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using SkiaSharp;
using WpfDrawingContext = System.Windows.Media.DrawingContext;
using WpfPixelFormats = System.Windows.Media.Imaging.PixelFormats;
using WpfRect = System.Windows.Rect;
using WpfWriteableBitmap = System.Windows.Media.Imaging.WriteableBitmap;
using Xunit;

namespace ProGPU.Tests;

public sealed class CompositorReviewRegressionTests
{
    private const string SolidShaderToySource = """
fn mainImage(fragCoord: vec2<f32>) -> vec4<f32> {
    return vec4<f32>(1.0, 0.0, 0.0, 1.0);
}
""";

    [Fact]
    public void SkRectUnionIncludesEmptyOriginInCoordinateEnvelope()
    {
        var bounds = SKRect.Empty;

        bounds.Union(new SKRect(50f, 60f, 70f, 90f));

        Assert.Equal(0f, bounds.Left);
        Assert.Equal(0f, bounds.Top);
        Assert.Equal(70f, bounds.Right);
        Assert.Equal(90f, bounds.Bottom);

        bounds.Union(SKRect.Empty);

        Assert.Equal(0f, bounds.Left);
        Assert.Equal(0f, bounds.Top);
        Assert.Equal(70f, bounds.Right);
        Assert.Equal(90f, bounds.Bottom);
    }

    [Fact]
    public void CombinedPathAtlasBoundsUseGeometryCoordinatesBeforePadding()
    {
        using var atlas = new PathAtlas(HeadlessWindow.Shared.Context, atlasSize: 256);
        var combined = new PathGeometry
        {
            IsCombined = true,
            Op = 2,
            PathA = PrimitivePathGeometry.CreateRectangle(10f, 15f, 20f, 10f),
            PathB = PrimitivePathGeometry.CreateRectangle(40f, 35f, 20f, 5f)
        };

        var info = atlas.GetOrCreatePath(combined, scale: 2f);

        Assert.Equal(10f, info.UnscaledMinX, precision: 3);
        Assert.Equal(15f, info.UnscaledMinY, precision: 3);
        Assert.Equal(60f, info.UnscaledMaxX, precision: 3);
        Assert.Equal(40f, info.UnscaledMaxY, precision: 3);
        Assert.Equal(16f, info.MinX);
        Assert.Equal(26f, info.MinY);
        Assert.Equal(108u, info.Width);
        Assert.Equal(58u, info.Height);
    }

    [Fact]
    public void PathAtlasCachesAndSamplesSubpixelTranslationPhase()
    {
        using var atlas = new PathAtlas(HeadlessWindow.Shared.Context, atlasSize: 256);
        var path = PrimitivePathGeometry.CreateRectangle(0f, 0f, 8f, 8f);

        var first = atlas.GetOrCreatePath(path, scale: 1f, subpixelX: 0.2f, subpixelY: -0.6f);
        var samePhase = atlas.GetOrCreatePath(path, scale: 1f, subpixelX: 0.202f, subpixelY: 0.402f);
        var differentPhase = atlas.GetOrCreatePath(path, scale: 1f, subpixelX: 0.22f, subpixelY: 0.4f);

        Assert.Equal(13f / 64f, first.Key.SubpixelX);
        Assert.Equal(26f / 64f, first.Key.SubpixelY);
        Assert.Equal(first.X, samePhase.X);
        Assert.Equal(first.Y, samePhase.Y);
        Assert.NotEqual(first.X, differentPhase.X);
        Assert.Equal(2, atlas.CachedPathCount);
        Assert.Equal(first.Key.SubpixelX, first.TexCoordMin.X * 256f - first.X, precision: 5);
        Assert.Equal(first.Key.SubpixelY, first.TexCoordMin.Y * 256f - first.Y, precision: 5);
        Assert.Equal(first.Key.SubpixelX, first.TexCoordMax.X * 256f - first.X - first.Width, precision: 5);
        Assert.Equal(first.Key.SubpixelY, first.TexCoordMax.Y * 256f - first.Y - first.Height, precision: 5);
    }

    [Fact]
    public void PathAtlasCachesRequestedCoveragePrecision()
    {
        using var atlas = new PathAtlas(HeadlessWindow.Shared.Context, atlasSize: 256);
        var path = PrimitivePathGeometry.CreateRectangle(0f, 0f, 10.33f, 16f);

        var standard = atlas.GetOrCreatePath(
            path,
            scale: 1f,
            sampleGrid: PathAtlas.StandardCoverageSampleGrid);
        var precise = atlas.GetOrCreatePath(
            path,
            scale: 1f,
            sampleGrid: PathAtlas.HighPrecisionCoverageSampleGrid);
        atlas.RasterizePendingPaths();

        Assert.Equal(PathAtlas.StandardCoverageSampleGrid, standard.Key.SampleGrid);
        Assert.Equal(PathAtlas.HighPrecisionCoverageSampleGrid, precise.Key.SampleGrid);
        Assert.NotEqual(standard.X, precise.X);
        Assert.Equal(2, atlas.CachedPathCount);

        var pixels = atlas.AtlasTexture.ReadPixels();
        var standardOffset = GetPathAtlasPixelOffset(standard, 10, 8, 256);
        var preciseOffset = GetPathAtlasPixelOffset(precise, 10, 8, 256);
        Assert.InRange(pixels[standardOffset], (byte)60, (byte)68);
        Assert.InRange(pixels[preciseOffset], (byte)90, (byte)102);
    }

    [Fact]
    public void PathAtlasBatchesDistinctGeometryRecords()
    {
        using var atlas = new PathAtlas(HeadlessWindow.Shared.Context, atlasSize: 256);
        var narrow = atlas.GetOrCreatePath(
            PrimitivePathGeometry.CreateRectangle(0f, 0f, 3f, 3f),
            scale: 1f);
        var wide = atlas.GetOrCreatePath(
            PrimitivePathGeometry.CreateRectangle(0f, 0f, 9f, 3f),
            scale: 1f);

        atlas.RasterizePendingPaths();

        var pixels = atlas.AtlasTexture.ReadPixels();
        Assert.Equal((byte)255, pixels[GetPathAtlasPixelOffset(narrow, 1, 1, 256)]);
        Assert.Equal((byte)0, pixels[GetPathAtlasPixelOffset(narrow, 5, 1, 256)]);
        Assert.Equal((byte)255, pixels[GetPathAtlasPixelOffset(wide, 8, 1, 256)]);
    }

    [Fact]
    public void RepeatedVectorGlyphsReuseSinglePathAtlasEntry()
    {
        const int glyphCount = 96;
        var font = new TtfFont(BuildMissingGlyphOutlineFont());
        using var window = new HeadlessWindow(1024, 64);
        window.Content = new RepeatedVectorGlyphVisual(font, glyphCount);

        window.Render();

        var cachedPathCount = window.Compositor.PathAtlas.CachedPathCount;
        Assert.True(
            cachedPathCount <= 1,
            $"Expected one reusable vector-glyph path, found {cachedPathCount}.");
        Assert.Single(
            GetDrawCalls(window.Compositor),
            drawCall => drawCall.Type == Compositor.DrawCallType.Vector && drawCall.IndexCount > 0);

        var pixels = window.ReadPixels();
        for (var glyphIndex = 0; glyphIndex < glyphCount; glyphIndex += 16)
        {
            var redOffset = (33 * 1024 + glyphIndex * 10 + 6) * 4;
            Assert.True(pixels[redOffset] > 200, $"Glyph {glyphIndex} was not rendered at its expected position.");
        }
    }

    [Fact]
    public void FractionalTransformedVectorGlyphsUseBoundedAtlasPhases()
    {
        const int glyphCount = 512;
        var font = new TtfFont(BuildMissingGlyphOutlineFont());
        using var window = new HeadlessWindow(
            1024,
            384,
            CompositorOptions.Default with { PathAtlasSize = 512 });
        window.Content = new FractionalVectorGlyphVisual(font, glyphCount);

        window.Render();

        Assert.False(window.Compositor.PathAtlas.CapacityExceeded);
        Assert.InRange(
            window.Compositor.PathAtlas.CachedPathCount,
            1,
            130);
        Assert.Equal(
            (uint)(glyphCount * 6),
            (uint)GetDrawCalls(window.Compositor)
                .Where(static drawCall => drawCall.Type == Compositor.DrawCallType.Vector)
                .Sum(static drawCall => (long)drawCall.IndexCount));

        var pixels = window.ReadPixels();
        Assert.True(
            pixels.Where((_, index) => index % 4 == 0).Count(static red => red > 200) >= glyphCount * 8,
            "Expected every bounded vector-glyph phase to retain visible coverage.");
    }

    [Fact]
    public void ParentTransformedVectorGlyphsUseQuarterPixelCoveragePhases()
    {
        const int phaseCount = 16;
        const int glyphCount = phaseCount * phaseCount;
        var font = new TtfFont(BuildMissingGlyphOutlineFont());
        using var window = new HeadlessWindow(
            640,
            576,
            CompositorOptions.Default with { PathAtlasSize = 256 });
        window.Content = new ParentPhaseVectorGlyphVisual(font, phaseCount);

        window.Render();

        Assert.False(window.Compositor.PathAtlas.CapacityExceeded);
        Assert.InRange(window.Compositor.PathAtlas.CachedPathCount, 1, 16);
        Assert.Equal(
            (uint)(glyphCount * 6),
            (uint)GetDrawCalls(window.Compositor)
                .Where(static drawCall => drawCall.Type == Compositor.DrawCallType.Vector)
                .Sum(static drawCall => (long)drawCall.IndexCount));

        var pixels = window.ReadPixels();
        Assert.True(
            pixels.Where((_, index) => index % 4 == 0).Count(static red => red > 200) >= glyphCount * 8,
            "Expected every parent-transform phase to retain visible vector-glyph coverage.");
    }

    [Fact]
    public void VectorTextCoverageMatchesDeviceSizeAndTransformPolicy()
    {
        var method = typeof(Compositor).GetMethod(
            "GetTextPathCoverageGamma",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var rasterizationMethod = typeof(Compositor).GetMethod(
            "ResolveTextRasterization",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(rasterizationMethod);

        float GetGamma(
            float fontSize,
            Matrix4x4 transform,
            float dpiScale = 1f,
            float staticZoom = 1f)
        {
            var rasterization = Assert.IsType<ValueTuple<float, float, float>>(rasterizationMethod.Invoke(
                null,
                [fontSize, transform, dpiScale, staticZoom]));
            return Assert.IsType<float>(method.Invoke(
                null,
                [fontSize, transform, TransformMetrics.GetStrokeScale(transform), rasterization.Item1]));
        }

        Assert.Equal(0.72f, GetGamma(18f, Matrix4x4.Identity));
        Assert.Equal(0.5f, GetGamma(32f, Matrix4x4.Identity));
        Assert.Equal(0.5f, GetGamma(18f, Matrix4x4.CreateScale(2f, 3f, 1f)));
        Assert.Equal(0.72f, GetGamma(12f, Matrix4x4.Identity, dpiScale: 1.999f));
        Assert.Equal(0.5f, GetGamma(12f, Matrix4x4.Identity, dpiScale: 2f));
        Assert.Equal(0.72f, GetGamma(12f, Matrix4x4.Identity, staticZoom: 1.999f));
        Assert.Equal(0.5f, GetGamma(12f, Matrix4x4.Identity, staticZoom: 2f));
        Assert.Equal(0.875f, GetGamma(32f, Matrix4x4.CreateRotationZ(MathF.PI / 4f), dpiScale: 2f));
        Assert.Equal(0.875f, GetGamma(32f, Matrix4x4.CreateScale(-1f, 1f, 1f), staticZoom: 2f));

        var shear = Matrix4x4.Identity;
        shear.M21 = 0.25f;
        Assert.Equal(0.875f, GetGamma(32f, shear));
    }

    private static int GetPathAtlasPixelOffset(
        PathAtlas.PathInfo info,
        int worldX,
        int worldY,
        uint atlasWidth)
    {
        var localX = checked((uint)(worldX - (int)info.MinX));
        var localY = checked((uint)(worldY - (int)info.MinY));
        return checked((int)(((info.Y + localY) * atlasWidth + info.X + localX) * 4));
    }

    private static byte ReadGlyphAtlasCoverage(
        byte[] pixels,
        GlyphInfo info,
        uint atlasWidth,
        uint localX,
        uint localY)
    {
        int offset = checked((int)(((info.Y + localY) * atlasWidth + info.X + localX) * 4));
        return pixels[offset];
    }

    [Fact]
    public void PathAtlasFrameAdvancePreservesValidCachedCoordinates()
    {
        using var atlas = new PathAtlas(HeadlessWindow.Shared.Context, atlasSize: 256);

        for (int i = 0; i < 4; i++)
        {
            atlas.GetOrCreatePath(
                PrimitivePathGeometry.CreateRectangle(i * 80f, 0f, 70f, 70f),
                scale: 1f);
        }

        Assert.Equal(4, atlas.CachedPathCount);
        var generation = atlas.Generation;

        atlas.CleanupFrame(anticipatedWidth: 1920, anticipatedHeight: 1080);

        Assert.Equal(4, atlas.CachedPathCount);
        Assert.Equal(generation, atlas.Generation);
    }

    [Fact]
    public void PathCacheKeyQuantizesScaleOnlyWhenExplicitlyRequested()
    {
        const float scale = 1.0003f;
        var ordinaryPath = new PathCacheKey(1, scale, scale, 0f, 0f);
        var vectorText = new PathCacheKey(
            1,
            scale,
            scale,
            0f,
            0f,
            sampleGrid: PathAtlas.StandardCoverageSampleGrid,
            subpixelPhaseGrid: PathAtlas.DefaultSubpixelPhaseGrid,
            quantizeScale: true);

        Assert.Equal(scale, ordinaryPath.ScaleX);
        Assert.Equal(scale, ordinaryPath.ScaleY);
        Assert.NotEqual(scale, vectorText.ScaleX);
        Assert.InRange(MathF.Abs(vectorText.ScaleX - scale) / scale, 0f, 1f / 2048f);

        var subnormal = new PathCacheKey(
            1,
            float.Epsilon,
            float.Epsilon,
            0f,
            0f,
            sampleGrid: PathAtlas.StandardCoverageSampleGrid,
            subpixelPhaseGrid: PathAtlas.DefaultSubpixelPhaseGrid,
            quantizeScale: true);
        Assert.Equal(float.Epsilon, subnormal.ScaleX);
        Assert.Equal(float.Epsilon, subnormal.ScaleY);
    }

    [Fact]
    public void PathAtlasCapacityFailureRecoversAtNextFrameBoundary()
    {
        using var atlas = new PathAtlas(HeadlessWindow.Shared.Context, atlasSize: 64);
        var first = atlas.GetOrCreatePath(
            PrimitivePathGeometry.CreateRectangle(0f, 0f, 44f, 44f),
            scale: 1f);
        atlas.CleanupFrame();
        _ = atlas.GetOrCreatePath(first.Geometry, scale: 1f);
        var missing = atlas.GetOrCreatePath(
            PrimitivePathGeometry.CreateRectangle(0f, 0f, 12f, 12f),
            scale: 1f);
        ulong generation = atlas.Generation;

        Assert.True(atlas.CapacityExceeded);
        Assert.Equal(0u, missing.Width);

        atlas.CleanupFrame();

        Assert.False(atlas.CapacityExceeded);
        Assert.Equal(0, atlas.CachedPathCount);
        Assert.True(atlas.Generation > generation);
    }

    [Fact]
    public void PathAtlasRetryDeterministicallyPacksGalleryShapedLiveSet()
    {
        using var atlas = new PathAtlas(HeadlessWindow.Shared.Context, atlasSize: 2048);
        PathGeometry[] paths =
        [
            PrimitivePathGeometry.CreateRectangle(0f, 0f, 1018f, 756f),
            PrimitivePathGeometry.CreateRectangle(3f, 5f, 1018f, 756f),
            PrimitivePathGeometry.CreateRectangle(7f, 11f, 992f, 592f),
            PrimitivePathGeometry.CreateRectangle(13f, 17f, 992f, 592f)
        ];

        var first = atlas.GetOrCreatePath(paths[0], scale: 1f);
        var second = atlas.GetOrCreatePath(paths[1], scale: 1f);
        var third = atlas.GetOrCreatePath(paths[2], scale: 1f);
        var preservedCoordinates = new[]
        {
            (first.X, first.Y),
            (second.X, second.Y),
            (third.X, third.Y)
        };
        var missing = atlas.GetOrCreatePath(paths[3], scale: 1f);

        Assert.True(atlas.CapacityExceeded);
        Assert.Equal(0u, missing.Width);
        Assert.Equal(preservedCoordinates[0], (first.X, first.Y));
        Assert.Equal(preservedCoordinates[1], (second.X, second.Y));
        Assert.Equal(preservedCoordinates[2], (third.X, third.Y));

        ulong generation = atlas.Generation;
        atlas.ResetForRenderRetry();
        PathAtlas.PathInfo[] recovered = paths
            .Select(path => atlas.GetOrCreatePath(path, scale: 1f))
            .ToArray();

        Assert.False(atlas.CapacityExceeded);
        Assert.True(atlas.Generation > generation);
        Assert.Equal(new uint[] { 1026, 1026, 1000, 1000 }, recovered.Select(static info => info.Width));
        Assert.Equal(new uint[] { 764, 764, 600, 600 }, recovered.Select(static info => info.Height));
        AssertAtlasRectanglesDoNotOverlap(recovered, atlas.AtlasSize);

        var firstPacking = recovered.Select(static info => (info.X, info.Y)).ToArray();
        atlas.ResetForRenderRetry();
        PathAtlas.PathInfo[] repeated = paths
            .Select(path => atlas.GetOrCreatePath(path, scale: 1f))
            .ToArray();
        Assert.Equal(firstPacking, repeated.Select(static info => (info.X, info.Y)).ToArray());
    }

    [Fact]
    public void PathAtlasRetryUsesAlternativeDeterministicStrategyForFeasibleSet()
    {
        using var atlas = new PathAtlas(HeadlessWindow.Shared.Context, atlasSize: 122);
        PathGeometry[] paths =
        [
            PrimitivePathGeometry.CreateRectangle(0f, 0f, 90f, 50f),
            PrimitivePathGeometry.CreateRectangle(3f, 5f, 110f, 30f),
            PrimitivePathGeometry.CreateRectangle(7f, 11f, 10f, 30f),
            PrimitivePathGeometry.CreateRectangle(13f, 17f, 10f, 30f)
        ];

        foreach (PathGeometry path in paths)
        {
            _ = atlas.GetOrCreatePath(path, scale: 1f);
        }

        Assert.True(atlas.CapacityExceeded);
        atlas.ResetForRenderRetry();
        PathAtlas.PathInfo[] recovered = paths
            .Select(path => atlas.GetOrCreatePath(path, scale: 1f))
            .ToArray();

        Assert.False(atlas.CapacityExceeded);
        Assert.Equal(new uint[] { 98, 118, 18, 18 }, recovered.Select(static info => info.Width));
        Assert.Equal(new uint[] { 58, 38, 38, 38 }, recovered.Select(static info => info.Height));
        Assert.Equal((2u, 2u), (recovered[1].X, recovered[1].Y));
        AssertAtlasRectanglesDoNotOverlap(recovered, atlas.AtlasSize);

        var firstPacking = recovered.Select(static info => (info.X, info.Y)).ToArray();
        atlas.ResetForRenderRetry();
        PathAtlas.PathInfo[] repeated = paths
            .Select(path => atlas.GetOrCreatePath(path, scale: 1f))
            .ToArray();
        Assert.Equal(firstPacking, repeated.Select(static info => (info.X, info.Y)).ToArray());
    }

    [Fact]
    public void PathAtlasRetryRejectsMathematicallyUnfitTypographyLiveSet()
    {
        using var atlas = new PathAtlas(HeadlessWindow.Shared.Context, atlasSize: 2048);
        PathGeometry[] paths =
        [
            PrimitivePathGeometry.CreateRectangle(0f, 0f, 1018f, 756f),
            PrimitivePathGeometry.CreateRectangle(3f, 5f, 1016f, 754f),
            PrimitivePathGeometry.CreateRectangle(7f, 11f, 1116f, 491f),
            PrimitivePathGeometry.CreateRectangle(13f, 17f, 1114f, 490f),
            PrimitivePathGeometry.CreateRectangle(19f, 23f, 250f, 1f),
            PrimitivePathGeometry.CreateRectangle(29f, 31f, 2f, 205f),
            PrimitivePathGeometry.CreateRectangle(37f, 41f, 4f, 24f),
            PrimitivePathGeometry.CreateRectangle(43f, 47f, 3f, 16f)
        ];

        PathAtlas.PathInfo[] initial = paths
            .Select(path => atlas.GetOrCreatePath(path, scale: 1f))
            .ToArray();

        Assert.True(atlas.CapacityExceeded);
        // The fourth and sixth insertions are the shelf misses. During retry
        // their geometry resolves the exact captured Gallery raster set:
        // 1026x764, 1024x762, 1124x499, 1122x498, 258x9, 10x213,
        // 12x32, and 11x24 (before each rectangle's 2px atlas gutter).
        Assert.Equal(1026u, initial[0].Width);
        Assert.Equal(1024u, initial[1].Width);
        Assert.Equal(1124u, initial[2].Width);
        Assert.Equal(0u, initial[3].Width);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(atlas.ResetForRenderRetry);
        Assert.Contains("8 live paths", exception.Message, StringComparison.Ordinal);
        Assert.Contains("2048x2048", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialRoundedRectangleBorderFillBypassesPathAtlas()
    {
        using var window = new HeadlessWindow(
            128,
            96,
            CompositorOptions.Default with { PathAtlasSize = 64 });
        window.Content = new PartialRoundedBorderVisual();

        window.Render();

        Assert.False(window.Compositor.PathAtlas.CapacityExceeded);
        Assert.Equal(0, window.Compositor.PathAtlas.CachedPathCount);
        Assert.True(
            window.Compositor.VectorVertices.Count(vertex => MathF.Abs(vertex.ShapeType - 13f) < 0.01f) > 0,
            "Expected direct triangle-SDF vertices for the partial rounded border.");

        byte[] pixels = window.ReadPixels();
        int redPixels = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] > 140 && pixels[offset + 2] < 100)
            {
                redPixels++;
            }
        }

        Assert.True(redPixels > 100, $"Expected a visible red border, found {redPixels} red pixels.");
        int centerOffset = (48 * 128 + 64) * 4;
        Assert.True(pixels[centerOffset + 2] > 180, "The even-odd inner hole should preserve the blue background.");
    }

    [Fact]
    public void NonCanonicalRoundedBoundaryWalkUsesPathAtlas()
    {
        using var window = new HeadlessWindow(
            128,
            64,
            CompositorOptions.Default with { PathAtlasSize = 256 });
        window.Content = new NonCanonicalRoundedPathVisual();

        window.Render();

        Assert.Equal(1, window.Compositor.PathAtlas.CachedPathCount);
        Assert.DoesNotContain(
            window.Compositor.VectorVertices,
            vertex => MathF.Abs(vertex.ShapeType - 13f) < 0.01f);
    }

    [Fact]
    public void LargeScaledPartialRoundedFillUsesDeviceBoundedTessellation()
    {
        using var window = new HeadlessWindow(
            128,
            96,
            CompositorOptions.Default with { PathAtlasSize = 64 });
        window.Content = new LargeScaledPartialRoundedPathVisual();

        window.Render();

        VectorVertex[] directVertices = window.Compositor.VectorVertices
            .Where(vertex => MathF.Abs(vertex.ShapeType - 13f) < 0.01f)
            .ToArray();
        int directTriangleVertexCount = directVertices.Length;
        Assert.True(
            directTriangleVertexCount > 300,
            $"Expected device-error-bounded tessellation beyond the fixed eight-segment contour, found {directTriangleVertexCount} vertices.");
        Assert.Equal(0, directTriangleVertexCount % 4);
        float triangleAabbArea = 0f;
        for (int vertexIndex = 0; vertexIndex < directVertices.Length; vertexIndex += 4)
        {
            VectorVertex vertex = directVertices[vertexIndex];
            Vector2 point0 = new(vertex.Color.X, vertex.Color.Y);
            Vector2 point1 = new(vertex.Color.Z, vertex.Color.W);
            Vector2 point2 = vertex.ShapeSize;
            Vector2 minimum = Vector2.Min(point0, Vector2.Min(point1, point2));
            Vector2 maximum = Vector2.Max(point0, Vector2.Max(point1, point2));
            triangleAabbArea += (maximum.X - minimum.X) * (maximum.Y - minimum.Y);
        }

        const float contourBoundsArea = 1_000f * 1_000f;
        Assert.True(
            triangleAabbArea < contourBoundsArea * 3.1f,
            $"Balanced triangulation should bound aggregate triangle AABB work; measured {triangleAabbArea / contourBoundsArea:F3}x contour area.");
        Assert.Equal(0, window.Compositor.PathAtlas.CachedPathCount);
        byte[] pixels = window.ReadPixels();
        Assert.True(pixels[(64 * 128 + 64) * 4] > 180, "Balanced direct triangles should cover the rounded fill interior.");
    }

    [Fact]
    public void CrossingCanonicalRoundedRingFallsBackToPathAtlas()
    {
        using var window = new HeadlessWindow(
            112,
            112,
            CompositorOptions.Default with { PathAtlasSize = 256 });
        window.Content = new CrossingRoundedRingVisual();

        window.Render();

        Assert.Equal(1, window.Compositor.PathAtlas.CachedPathCount);
        Assert.DoesNotContain(
            window.Compositor.VectorVertices,
            vertex => MathF.Abs(vertex.ShapeType - 13f) < 0.01f);
        byte[] pixels = window.ReadPixels();
        Assert.True(pixels[(5 * 112 + 5) * 4 + 2] > 180, "The excluded rounded outer corner should remain blue.");
        Assert.True(pixels[(50 * 112 + 99) * 4] > 180, "The non-crossing right border should render red.");
        Assert.True(pixels[(50 * 112 + 50) * 4 + 2] > 180, "The even-odd inner hole should remain blue.");
    }

    private static void AssertAtlasRectanglesDoNotOverlap(
        IReadOnlyList<PathAtlas.PathInfo> paths,
        uint atlasSize)
    {
        for (int index = 0; index < paths.Count; index++)
        {
            PathAtlas.PathInfo current = paths[index];
            Assert.True(current.X + current.Width + 2 <= atlasSize);
            Assert.True(current.Y + current.Height + 2 <= atlasSize);
            for (int otherIndex = index + 1; otherIndex < paths.Count; otherIndex++)
            {
                PathAtlas.PathInfo other = paths[otherIndex];
                Assert.True(
                    current.X + current.Width + 2 <= other.X ||
                    other.X + other.Width + 2 <= current.X ||
                    current.Y + current.Height + 2 <= other.Y ||
                    other.Y + other.Height + 2 <= current.Y,
                    $"Atlas rectangles {index} and {otherIndex} overlap.");
            }
        }
    }

    [Fact]
    public void CompositorRetriesFrameAfterRecoverablePathAtlasCapacityFailure()
    {
        const int pathCount = 24;
        using var window = new HeadlessWindow(
            640,
            64,
            CompositorOptions.Default with { PathAtlasSize = 128 });
        window.Content = new PathAtlasPressureVisual(pathCount, variant: 0);
        window.Render();
        var firstGeneration = window.Compositor.PathAtlas.Generation;

        window.Content = new PathAtlasPressureVisual(pathCount, variant: 1);
        window.Render();

        Assert.False(window.Compositor.PathAtlas.CapacityExceeded);
        Assert.True(window.Compositor.PathAtlas.Generation > firstGeneration);
        Assert.Equal(
            (uint)(pathCount * 6),
            (uint)GetDrawCalls(window.Compositor)
                .Where(static drawCall => drawCall.Type == Compositor.DrawCallType.Vector)
                .Sum(static drawCall => (long)drawCall.IndexCount));

        var pixels = window.ReadPixels();
        for (var pathIndex = 0; pathIndex < pathCount; pathIndex++)
        {
            var redOffset = (28 * 640 + pathIndex * 24 + 8) * 4;
            Assert.True(
                pixels[redOffset] > 200,
                $"Path {pathIndex} was missing after the atlas retry.");
        }
    }

    [Fact]
    public void OffscreenCompositorRetriesAfterRecoverablePathAtlasCapacityFailure()
    {
        const int pathCount = 24;
        using var window = new HeadlessWindow(
            640,
            64,
            CompositorOptions.Default with { PathAtlasSize = 128 });
        using var target = new GpuTexture(
            window.Context,
            640,
            64,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.CopySrc,
            "PathAtlas retry offscreen target");
        window.Compositor.RenderOffscreen(
            CreatePathAtlasPressureDrawingVisual(pathCount, variant: 0),
            640,
            64,
            target,
            padding: 0f,
            dpiScale: 1f);
        var firstGeneration = window.Compositor.PathAtlas.Generation;

        window.Compositor.RenderOffscreen(
            CreatePathAtlasPressureDrawingVisual(pathCount, variant: 1),
            640,
            64,
            target,
            padding: 0f,
            dpiScale: 1f);

        Assert.False(window.Compositor.PathAtlas.CapacityExceeded);
        Assert.True(window.Compositor.PathAtlas.Generation > firstGeneration);
        var pixels = target.ReadPixels();
        for (var pathIndex = 0; pathIndex < pathCount; pathIndex++)
        {
            var redOffset = (28 * 640 + pathIndex * 24 + 8) * 4;
            Assert.True(
                pixels[redOffset] > 200,
                $"Offscreen path {pathIndex} was missing after the atlas retry.");
        }
    }

    [Fact]
    public void CompositorFailsExplicitlyWhenSinglePathExceedsAtlas()
    {
        using var window = new HeadlessWindow(
            96,
            96,
            CompositorOptions.Default with { PathAtlasSize = 64 });
        window.Content = new OversizedPathVisual();

        var exception = Assert.IsAssignableFrom<InvalidOperationException>(
            Record.Exception(() => window.Render()));

        Assert.Contains("PathAtlas", exception.Message, StringComparison.Ordinal);
        Assert.Contains("64x64", exception.Message, StringComparison.Ordinal);
    }

    private static DrawingVisual CreatePathAtlasPressureDrawingVisual(int pathCount, int variant)
    {
        var visual = new DrawingVisual { Size = new Vector2(640f, 64f) };
        var brush = new SolidColorBrush(Vector4.One);
        for (var pathIndex = 0; pathIndex < pathCount; pathIndex++)
        {
            var variantOffset = variant * 0.25f;
            var path = PrimitivePathGeometry.CreateRectangle(
                0f,
                0f,
                8f + pathIndex % 4 + variantOffset,
                8f + pathIndex / 4 + variantOffset);
            visual.Context.DrawPath(
                brush,
                null,
                path,
                Matrix4x4.CreateTranslation(pathIndex * 24f + 4f, 24f, 0f));
        }

        return visual;
    }

    [Fact]
    public void AcisSolidPipelineUsesAlreadyComposedTransformOnce()
    {
        var compositor = CreateUninitializedCompositorForExtensionCompile();
        var pipeline = new AcisSolidExtensionPipeline();
        var modelTransform = Matrix4x4.CreateTranslation(10f, 0f, 0f);
        var parentTransform = Matrix4x4.CreateScale(2f, 3f, 1f);
        var composedTransform = modelTransform * parentTransform;
        var oldDoubleAppliedTransform = modelTransform * composedTransform;
        var brush = new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f));
        var cmd = new RenderCommand
        {
            Type = RenderCommandType.DrawExtension,
            ExtensionId = CompositorBuiltInExtensions.AcisSolid,
            Pen = new Pen(brush, 2f),
            Edges3D =
            [
                new Line3D(new Vector3(0f, 0f, 0f), new Vector3(5f, 0f, 0f))
            ],
            Transform = modelTransform
        };

        pipeline.Compile(compositor, provider: null, composedTransform, ref cmd);

        var records = GetPrivateField<List<GpuAcisRecord>>(pipeline, "_dynamicRecords");
        var record = Assert.Single(records);
        Assert.Equal(composedTransform, record.Transform);
        Assert.NotEqual(oldDoubleAppliedTransform, record.Transform);
    }

    [Fact]
    public void ResizeAfterExternalPathAtlasDisposeDoesNotSubmitDestroyedTexture()
    {
        using var window = new HeadlessWindow(1280, 800);
        using (var atlas = new PathAtlas(window.Context, atlasSize: 256))
        {
            var combined = new PathGeometry
            {
                IsCombined = true,
                Op = 2,
                PathA = PrimitivePathGeometry.CreateRectangle(10f, 15f, 20f, 10f),
                PathB = PrimitivePathGeometry.CreateRectangle(40f, 35f, 20f, 5f)
            };

            atlas.GetOrCreatePath(combined, scale: 2f);
        }

        window.Resize(96, 48);
        window.Content = new OpacityMaskUnderClearBlendVisual();

        try
        {
            window.Render();
        }
        finally
        {
            window.Content = null;
        }
    }

    [Theory]
    [InlineData(GdiInterpolationMode.NearestNeighbor, TextureSamplingMode.Nearest)]
    [InlineData(GdiInterpolationMode.Bicubic, TextureSamplingMode.Cubic)]
    [InlineData(GdiInterpolationMode.HighQualityBicubic, TextureSamplingMode.Cubic)]
    [InlineData(GdiInterpolationMode.Default, TextureSamplingMode.Linear)]
    [InlineData(GdiInterpolationMode.Low, TextureSamplingMode.Linear)]
    [InlineData(GdiInterpolationMode.High, TextureSamplingMode.Linear)]
    [InlineData(GdiInterpolationMode.Bilinear, TextureSamplingMode.Linear)]
    [InlineData(GdiInterpolationMode.HighQualityBilinear, TextureSamplingMode.Linear)]
    public void GdiDrawImageMapsInterpolationModeToTextureSampling(
        GdiInterpolationMode interpolationMode,
        TextureSamplingMode expectedSamplingMode)
    {
        var previous = WgpuContext.Current;
        var window = HeadlessWindow.Shared;

        try
        {
            WgpuContext.Current = window.Context;
            using var destination = new GdiBitmap(4, 4);
            using var source = new GdiBitmap(2, 2);
            using var graphics = GdiGraphics.FromImage(destination);

            graphics.InterpolationMode = interpolationMode;
            graphics.DrawImage(source, new GdiRectangle(0, 0, 4, 4));

            var command = Assert.Single(graphics.DrawingContext.Commands);
            Assert.Equal(RenderCommandType.DrawTexture, command.Type);
            Assert.Equal(expectedSamplingMode, command.TextureSamplingMode);
        }
        finally
        {
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void ShimGpuProvidersPreferCurrentContextOverFirstActiveContext()
    {
        var previous = WgpuContext.Current;
        using var firstActiveContext = new WgpuContext();
        firstActiveContext.Initialize(null);
        using var currentContext = new WgpuContext();
        currentContext.Initialize(null);
        WpfWriteableBitmap? wpfBitmap = null;

        try
        {
            WgpuContext.Current = currentContext;

            using var gdiBitmap = new GdiBitmap(1, 1);
            wpfBitmap = new WpfWriteableBitmap(
                1,
                1,
                96d,
                96d,
                WpfPixelFormats.Pbgra32,
                palette: null);

            Assert.Same(currentContext, gdiBitmap.GpuTexture.Context);
            Assert.Same(currentContext, wpfBitmap.GpuTexture.Context);
            Assert.NotSame(firstActiveContext, gdiBitmap.GpuTexture.Context);
            Assert.NotSame(firstActiveContext, wpfBitmap.GpuTexture.Context);
        }
        finally
        {
            wpfBitmap?.GpuTexture.Dispose();
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void SkShaderSingleStopGradientsUseFiniteZeroOffset()
    {
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0f, 0f),
            new SKPoint(10f, 0f),
            [new SKColor(255, 0, 0, 255)],
            colorPos: null,
            SKShaderTileMode.Clamp);

        var brush = Assert.IsType<LinearGradientBrush>(shader.ToBrush());
        var stop = Assert.Single(brush.Stops);

        Assert.Equal(0f, stop.Offset);
        Assert.True(float.IsFinite(stop.Offset));
    }

    [Fact]
    public void SkPaintShaderBrushesApplyPaintAlphaToFillAndStroke()
    {
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0f, 0f),
            new SKPoint(10f, 0f),
            [SKColors.Red, SKColors.Blue],
            colorPos: null,
            SKShaderTileMode.Clamp);

        using var fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Shader = shader,
            Color = new SKColor(255, 255, 255, 128)
        };

        var fillBrush = Assert.IsType<LinearGradientBrush>(fillPaint.ToBrush());
        Assert.Equal(128f / 255f, fillBrush.Opacity, precision: 6);

        using var strokePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            Shader = shader,
            Color = new SKColor(255, 255, 255, 64)
        };

        var pen = strokePaint.ToPen();
        Assert.NotNull(pen);
        var strokeBrush = Assert.IsType<LinearGradientBrush>(pen.Brush);
        Assert.Equal(64f / 255f, strokeBrush.Opacity, precision: 6);
    }

    [Fact]
    public void ShaderToyAcceptsConstIntegerArraySizes()
    {
        var wgsl = ShaderToyTranspiler.Translate(
            """
            const int KernelSize = 2 + 1;

            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                const int LocalSize = KernelSize;
                float weights[LocalSize];
                weights[0] = 0.25;
                weights[1] = 0.5;
                weights[2] = 0.25;
                fragColor = vec4(weights[0] + weights[1] + weights[2], 0.0, 0.0, 1.0);
            }
            """);

        Assert.Contains("var<private> KernelSize: i32;", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("var LocalSize: i32 = KernelSize;", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("var weights: array<f32, 3>;", wgsl, System.StringComparison.Ordinal);
        Assert.DoesNotContain("var weights: f32;", wgsl, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderToyForLoopPreservesMultiDeclarationInitializers()
    {
        var wgsl = ShaderToyTranspiler.Translate(
            """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                int sum = 0;
                for (int i = 0, j = 1; i < 4; i++)
                {
                    sum += i + j;
                }
                fragColor = vec4(float(sum));
            }
            """);

        Assert.Contains("var i: i32 = 0;", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("var j: i32 = 1;", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("for (; (i < 4); i = i + 1) {", wgsl, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderToyForLoopLowersCommaSeparatedUpdates()
    {
        var wgsl = ShaderToyTranspiler.Translate(
            """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                int sum = 0;
                for (int i = 0, j = 0; i < 4; i++, j++)
                {
                    if (i == 2)
                    {
                        continue;
                    }
                    sum += i + j;
                }
                fragColor = vec4(float(sum));
            }
            """);

        Assert.Contains("loop {", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("if (!((i < 4))) { break; }", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("continuing {", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("i = i + 1;", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("j = j + 1;", wgsl, System.StringComparison.Ordinal);
        Assert.DoesNotContain("i++, j++", wgsl, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderToyForLoopLowersCommaSeparatedInitializerExpressions()
    {
        var wgsl = ShaderToyTranspiler.Translate(
            """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                int sum = 0;
                int i = 0;
                int j = 0;
                for (i = 0, j = 1; i < 4; i++, j++)
                {
                    sum += i + j;
                }
                fragColor = vec4(float(sum));
            }
            """);

        Assert.Contains("i = 0;", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("j = 1;", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("loop {", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("if (!((i < 4))) { break; }", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("i = i + 1;", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("j = j + 1;", wgsl, System.StringComparison.Ordinal);
        Assert.DoesNotContain("i = 0, j = 1", wgsl, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderToyModBroadcastsScalarFirstVectorDivisor()
    {
        var wgsl = ShaderToyTranspiler.Translate(
            """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                vec2 wrapped = mod(1.0, fragCoord.xy);
                fragColor = vec4(wrapped, 0.0, 1.0);
            }
            """);

        Assert.Contains("fn wgsl_mod_fv2(x: f32, y: vec2<f32>) -> vec2<f32>", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("wgsl_mod_fv2(1.0", wgsl, System.StringComparison.Ordinal);
        Assert.DoesNotContain("wgsl_mod_ff(1.0", wgsl, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderToyModVectorResultBroadcastsScalarAddition()
    {
        var wgsl = ShaderToyTranspiler.Translate(
            """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                vec2 wrapped = mod(fragCoord.xy, 1.0) + 1.0;
                fragColor = vec4(wrapped, 0.0, 1.0);
            }
            """);

        Assert.Contains("wgsl_mod_v2f(fragCoord.xy, 1.0)", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("wgsl_mod_v2f(fragCoord.xy, 1.0) + vec2<f32>(1.0)", wgsl, System.StringComparison.Ordinal);
        Assert.DoesNotContain("wgsl_mod_v2f(fragCoord.xy, 1.0) + 1.0", wgsl, System.StringComparison.Ordinal);
    }

    [Fact]
    public void GpuPictureRecorderEndRecordingTransfersRetainedResourceLeases()
    {
        var recorder = new GpuPictureRecorder();
        var context = recorder.BeginRecording(new Rect(0f, 0f, 16f, 16f));
        var resource = new CountingDisposable();
        context.RetainResource(resource);

        using var picture = recorder.EndRecording();

        Assert.Equal(0, context.RetainedResourceCount);
        Assert.Equal(1, picture.RetainedResourceCount);
        Assert.Equal(0, resource.DisposeCount);

        picture.Dispose();

        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public void ShaderToyAcceptsBitwiseCompoundAssignments()
    {
        var wgsl = ShaderToyTranspiler.Translate(
            """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                uint x = uint(fragCoord.x);
                uint mask = 0xffffffffu;
                x ^= x >> 16u;
                mask &= x;
                x |= mask << 1u;
                x %= 17u;
                fragColor = vec4(float(x & 255u));
            }
            """);

        Assert.Contains("x ^= (x >> 16u);", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("mask &= x;", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("x |= (mask << 1u);", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("x %= 17u;", wgsl, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderToyParsesOverflowingHexLiteralAsUnsigned()
    {
        var wgsl = ShaderToyTranspiler.Translate(
            """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                uint x = uint(fragCoord.x);
                uint mask = 0xffffffff;
                x &= mask;
                fragColor = vec4(float(x & 255u));
            }
            """);

        Assert.Contains("var mask: u32 = 4294967295u;", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("x &= mask;", wgsl, System.StringComparison.Ordinal);
        Assert.DoesNotContain("var mask: u32 = -1", wgsl, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderToyShiftOperatorsBindBeforeRelationalOperators()
    {
        var wgsl = ShaderToyTranspiler.Translate(
            """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                uint x = uint(fragCoord.x);
                bool folded = 1u < x >> 1u;
                fragColor = folded ? vec4(0.0, 1.0, 0.0, 1.0) : vec4(1.0, 0.0, 0.0, 1.0);
            }
            """);

        Assert.Contains("var folded: bool = (1u < (x >> 1u));", wgsl, System.StringComparison.Ordinal);
        Assert.DoesNotContain("((1u < x) >> 1u)", wgsl, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderToyIFrameUniformUsesIntegerAbi()
    {
        var header = typeof(ShaderToyExtensionPipeline).GetField(
                "VertexAndHeaderShader",
                BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) as string
            ?? throw new System.InvalidOperationException("Expected ShaderToy WGSL header.");

        Assert.Contains("iFrame: i32", header, System.StringComparison.Ordinal);
        Assert.DoesNotContain("iFrame: f32", header, System.StringComparison.Ordinal);
        Assert.Equal(typeof(int), typeof(ShaderToyUniforms).GetField(nameof(ShaderToyUniforms.Frame))?.FieldType);
        Assert.Equal(20, Marshal.OffsetOf<ShaderToyUniforms>(nameof(ShaderToyUniforms.Frame)).ToInt32());
        Assert.Equal(64, Marshal.SizeOf<ShaderToyUniforms>());
    }

    [Fact]
    public void ShaderToyCacheKeysIncludeStableSourceHash()
    {
        var sourceKeyMethod = typeof(ShaderToyExtensionPipeline).GetMethod(
            "GetStableShaderSourceKey",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected ShaderToy source key helper.");
        var shaderKeyMethod = typeof(ShaderToyExtensionPipeline).GetMethod(
            "GetShaderKey",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected ShaderToy shader key helper.");
        var pipelineKeyMethod = typeof(ShaderToyExtensionPipeline).GetMethod(
            "GetPipelineKey",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected ShaderToy pipeline key helper.");

        var firstSourceKey = Assert.IsType<string>(sourceKeyMethod.Invoke(null, ["return vec4<f32>(1.0);"]));
        var secondSourceKey = Assert.IsType<string>(sourceKeyMethod.Invoke(null, ["return vec4<f32>(0.0);"]));
        var firstShaderKey = Assert.IsType<string>(shaderKeyMethod.Invoke(
            null,
            ["chart", firstSourceKey, GpuTextureAlphaMode.Straight]));
        var secondShaderKey = Assert.IsType<string>(shaderKeyMethod.Invoke(
            null,
            ["chart", secondSourceKey, GpuTextureAlphaMode.Straight]));
        var firstPipelineKey = Assert.IsType<string>(pipelineKeyMethod.Invoke(
            null,
            ["chart", firstSourceKey, false, GpuBlendMode.SrcOver, GpuTextureAlphaMode.Straight]));
        var secondPipelineKey = Assert.IsType<string>(pipelineKeyMethod.Invoke(
            null,
            ["chart", secondSourceKey, false, GpuBlendMode.SrcOver, GpuTextureAlphaMode.Straight]));

        Assert.NotEqual(firstSourceKey, secondSourceKey);
        Assert.NotEqual(firstShaderKey, secondShaderKey);
        Assert.NotEqual(firstPipelineKey, secondPipelineKey);
        Assert.Contains(firstSourceKey, firstShaderKey, StringComparison.Ordinal);
        Assert.Contains(firstSourceKey, firstPipelineKey, StringComparison.Ordinal);
    }

    [Fact]
    public void GdiBitmapDeferredFlushUsesConsumptionContextWhenAmbientContextChanges()
    {
        var previous = WgpuContext.Current;
        using var bitmapContext = new WgpuContext();
        bitmapContext.Initialize(null);
        using var ambientContext = new WgpuContext();
        ambientContext.Initialize(null);

        try
        {
            WgpuContext.Current = bitmapContext;
            using var bitmap = new GdiBitmap(4, 4);
            using (var graphics = GdiGraphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.Red);
            }

            WgpuContext.Current = ambientContext;
            bitmap.Flush();

            Assert.Same(ambientContext, bitmap.GpuTexture.Context);
        }
        finally
        {
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void GdiDrawImageMigratesPortableCpuBitmapIntoTargetContext()
    {
        var previous = WgpuContext.Current;
        using var sourceContext = new WgpuContext();
        sourceContext.Initialize(null);
        using var targetContext = new WgpuContext();
        targetContext.Initialize(null);

        try
        {
            WgpuContext.Current = sourceContext;
            using var source = new GdiBitmap(1, 1);
            source.SetPixel(0, 0, System.Drawing.Color.Red);
            var originalSourceTexture = source.GpuTexture;
            Assert.Same(sourceContext, originalSourceTexture.Context);

            WgpuContext.Current = targetContext;
            using var target = new GdiBitmap(2, 2);
            using var graphics = GdiGraphics.FromImage(target);

            graphics.DrawImage(source, new GdiRectangle(0, 0, 1, 1));

            var command = Assert.Single(graphics.DrawingContext.Commands);
            Assert.Same(targetContext, command.Texture!.Context);
            Assert.NotSame(originalSourceTexture, command.Texture);
            Assert.True(originalSourceTexture.IsDisposed);
            Assert.Equal(System.Drawing.Color.Red.ToArgb(), target.GetPixel(0, 0).ToArgb());
        }
        finally
        {
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void GdiBitmapFlushClearsRecordedResourcesWhenRenderFails()
    {
        var previous = WgpuContext.Current;
        using var context = new WgpuContext();
        context.Initialize(null);

        try
        {
            WgpuContext.Current = context;
            using var source = new GdiBitmap(1, 1);
            using var target = new GdiBitmap(2, 2);
            using var graphics = GdiGraphics.FromImage(target);
            graphics.DrawImage(source, new GdiRectangle(0, 0, 1, 1));

            var retainedTexture = Assert.Single(
                target.RecordedContext.Commands,
                static command => command.Texture != null).Texture!;
            Assert.Same(source.GpuTexture, retainedTexture);
            Assert.Equal(1, target.RecordedContext.RetainedResourceCount);

            var compositor = GetGdiBitmapCompositor(target);
            compositor.RegisterExtension(9907, new ThrowingCompileExtension("Synthetic GDI bitmap flush failure."));
            target.RecordedContext.DrawExtension(9907);

            var retainedTextureDisposed = false;
            void OnTextureDisposed(ulong id)
            {
                if (id == retainedTexture.Id)
                {
                    retainedTextureDisposed = true;
                }
            }

            GpuTexture.OnDisposedWithId += OnTextureDisposed;
            try
            {
                var exception = Assert.Throws<System.InvalidOperationException>(() => target.Flush());
                Assert.Contains("Synthetic GDI bitmap flush failure", exception.Message, System.StringComparison.Ordinal);
            }
            finally
            {
                GpuTexture.OnDisposedWithId -= OnTextureDisposed;
            }

            Assert.Empty(target.RecordedContext.Commands);
            Assert.Equal(0, target.RecordedContext.RetainedResourceCount);
            Assert.False(retainedTextureDisposed);
            Assert.False(retainedTexture.IsDisposed);
        }
        finally
        {
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void GdiBitmapDisposeReleasesBackingTextureWhenFlushFails()
    {
        var previous = WgpuContext.Current;
        using var context = new WgpuContext();
        context.Initialize(null);
        GdiBitmap? target = null;

        try
        {
            WgpuContext.Current = context;
            using var source = new GdiBitmap(1, 1);
            target = new GdiBitmap(2, 2);
            using var graphics = GdiGraphics.FromImage(target);
            graphics.DrawImage(source, new GdiRectangle(0, 0, 1, 1));

            var backingTexture = target.GpuTexture;
            var compositor = GetGdiBitmapCompositor(target);
            compositor.RegisterExtension(9908, new ThrowingCompileExtension("Synthetic GDI bitmap dispose failure."));
            target.RecordedContext.DrawExtension(9908);

            var backingTextureDisposed = false;
            void OnTextureDisposed(ulong id)
            {
                if (id == backingTexture.Id)
                {
                    backingTextureDisposed = true;
                }
            }

            GpuTexture.OnDisposedWithId += OnTextureDisposed;
            try
            {
                var exception = Assert.Throws<System.InvalidOperationException>(() => target.Dispose());
                Assert.Contains("Synthetic GDI bitmap dispose failure", exception.Message, System.StringComparison.Ordinal);

                Assert.True(backingTextureDisposed);
                Assert.True(backingTexture.IsDisposed);
                Assert.Empty(target.RecordedContext.Commands);
                Assert.Equal(0, target.RecordedContext.RetainedResourceCount);
            }
            finally
            {
                GpuTexture.OnDisposedWithId -= OnTextureDisposed;
            }
        }
        finally
        {
            target?.Dispose();
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void GdiBitmapFinalizerPathDoesNotDisposeBackingTexture()
    {
        var previous = WgpuContext.Current;
        using var context = new WgpuContext();
        context.Initialize(null);

        try
        {
            WgpuContext.Current = context;
            var bitmap = new GdiBitmap(1, 1);
            var backingTexture = bitmap.GpuTexture;

            var backingTextureDisposed = false;
            void OnTextureDisposed(ulong id)
            {
                if (id == backingTexture.Id)
                {
                    backingTextureDisposed = true;
                }
            }

            GpuTexture.OnDisposedWithId += OnTextureDisposed;
            try
            {
                InvokeGdiBitmapDispose(bitmap, disposing: false);
                GC.SuppressFinalize(bitmap);

                Assert.False(backingTextureDisposed);
                Assert.False(backingTexture.IsDisposed);
            }
            finally
            {
                GpuTexture.OnDisposedWithId -= OnTextureDisposed;
                if (!backingTexture.IsDisposed)
                {
                    backingTexture.Dispose();
                }
            }
        }
        finally
        {
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void WpfDrawImageRejectsCrossContextBitmapSourcesBeforeRecording()
    {
        var previous = WgpuContext.Current;
        using var sourceContext = new WgpuContext();
        sourceContext.Initialize(null);
        using var targetContext = new WgpuContext();
        targetContext.Initialize(null);
        WpfWriteableBitmap? bitmap = null;

        try
        {
            WgpuContext.Current = sourceContext;
            bitmap = new WpfWriteableBitmap(
                1,
                1,
                96d,
                96d,
                WpfPixelFormats.Pbgra32,
                palette: null);

            WgpuContext.Current = targetContext;
            var nativeContext = new DrawingContext();
            using var drawingContext = new WpfDrawingContext(nativeContext);

            var exception = Assert.Throws<System.InvalidOperationException>(
                () => drawingContext.DrawImage(bitmap, new WpfRect(0, 0, 1, 1)));
            Assert.Contains("different WebGPU context", exception.Message, System.StringComparison.Ordinal);
            Assert.Empty(nativeContext.Commands);
        }
        finally
        {
            bitmap?.GpuTexture.Dispose();
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void WpfShaderEffectRejectsCrossContextSamplerTexturesBeforeBinding()
    {
        using var sourceContext = new WgpuContext();
        sourceContext.Initialize(null);
        using var window = new HeadlessWindow(16, 16);
        using var source = new GpuTexture(
            sourceContext,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Cross-context WPF Shader Effect Source");
        source.WritePixels(new byte[] { 255, 0, 0, 255 });

        var effect = new WpfShaderEffectParams
        {
            Texture = source,
            Rect = new Rect(0f, 0f, 16f, 16f),
            ShaderKey = $"review_wpf_shader_effect_cross_context_{System.Guid.NewGuid():N}"
        };
        window.Content = new WpfShaderEffectDisposalVisual(effect);

        try
        {
            window.Render();

            Assert.False(effect.IsFailed);
            Assert.StartsWith(
                "WPF shader effect sampler texture belongs to a different WebGPU context",
                effect.LastError,
                System.StringComparison.Ordinal);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void ImageEffectRejectsCrossContextTexturesBeforeBinding()
    {
        using var sourceContext = new WgpuContext();
        sourceContext.Initialize(null);
        using var window = new HeadlessWindow(16, 16);
        using var source = new GpuTexture(
            sourceContext,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Cross-context Image Effect Source");
        source.WritePixels(new byte[] { 255, 0, 0, 255 });

        var effect = new ImageEffectParams
        {
            Texture = source,
            Rect = new Rect(0f, 0f, 16f, 16f),
            BlurSigma = 1f
        };
        window.Content = new ImageEffectParamsVisual(effect);

        try
        {
            window.Render();

            Assert.StartsWith(
                "Image effect texture belongs to a different WebGPU context",
                effect.LastError,
                System.StringComparison.Ordinal);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void RenderSceneSkipsTextureCommandsFromForeignContext()
    {
        using var sourceContext = new WgpuContext();
        sourceContext.Initialize(null);
        using var window = new HeadlessWindow(16, 16);
        using var source = new GpuTexture(
            sourceContext,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Foreign Context Texture Command Source");
        source.WritePixels(new byte[] { 255, 0, 0, 255 });
        window.Content = new TextureCacheVisual(source);

        try
        {
            window.Render();

            var textureBindGroups = GetPersistentTextureBindGroups(window.Compositor);
            Assert.DoesNotContain(textureBindGroups.Keys, key => key.TextureId == source.Id);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void RenderOffscreenRestoresCompositorStateWhenCompilationFails()
    {
        using var window = new HeadlessWindow(32, 32);
        using var target = new GpuTexture(
            window.Context,
            16,
            16,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Failing Offscreen Restore Target");
        var beforeWidth = GetCompositorField<uint>(window.Compositor, "_currentWidth");
        var beforeHeight = GetCompositorField<uint>(window.Compositor, "_currentHeight");
        var beforeDpiScale = window.Compositor.CurrentDpiScale;
        var beforeProjection = GetCompositorField<Matrix4x4>(window.Compositor, "_currentProjection");

        var exception = Assert.Throws<System.InvalidOperationException>(
            () => window.Compositor.RenderOffscreen(
                new ThrowingRenderVisual(),
                width: 16,
                height: 16,
                targetTexture: target,
                padding: 0f,
                dpiScale: 2f));

        Assert.Equal("Synthetic offscreen render failure.", exception.Message);
        Assert.Equal(beforeWidth, GetCompositorField<uint>(window.Compositor, "_currentWidth"));
        Assert.Equal(beforeHeight, GetCompositorField<uint>(window.Compositor, "_currentHeight"));
        Assert.Equal(beforeDpiScale, window.Compositor.CurrentDpiScale);
        Assert.Equal(beforeProjection, GetCompositorField<Matrix4x4>(window.Compositor, "_currentProjection"));
    }

    [Fact]
    public void DrawGlyphRunFlushesActualTextCountBeforeColorLayerPaths()
    {
        var font = new TtfFont(BuildColorLayerFont());
        Assert.True(font.HasColorGlyphs);
        Assert.False(font.HasBitmapGlyphs);
        var window = HeadlessWindow.Shared;
        window.Resize(96, 48);
        window.Content = new MixedColorGlyphRunVisual(font);

        try
        {
            window.Render();

            AssertMixedColorGlyphDrawCalls(window.Compositor);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawTextFlushesActualTextCountBeforeColorLayerPaths()
    {
        var font = new TtfFont(BuildColorLayerFont());
        var window = HeadlessWindow.Shared;
        window.Resize(96, 48);
        window.Content = new MixedColorTextVisual(font);

        try
        {
            window.Render();

            AssertMixedColorGlyphDrawCalls(window.Compositor);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void GlyphAtlasDoesNotTreatMissingGlyphIdAsWhitespace()
    {
        var font = new TtfFont(BuildMissingGlyphOutlineFont());
        Assert.False(font.HasColorGlyphs);
        var layout = new TextLayout("A", font, 24f);
        Assert.Equal(font.GetGlyphIndex('A'), Assert.Single(layout.Glyphs).GlyphIndex);
        GlyphAtlas atlas = HeadlessWindow.Shared.Compositor.Atlas;

        GlyphInfo tab = atlas.GetOrCreateGlyph(font, '\t', 24f);
        GlyphInfo missing = atlas.GetOrCreateGlyph(font, 0x2603u, 24f);

        Assert.Equal(0u, tab.Width);
        Assert.Equal(0u, tab.Height);
        Assert.True(missing.Width > 0, "Expected missing-glyph ID 0 to keep its outline width.");
        Assert.True(missing.Height > 0, "Expected missing-glyph ID 0 to keep its outline height.");
    }

    [Fact]
    public void GlyphAtlasCapacityExhaustionPreservesExistingCoordinates()
    {
        var font = new TtfFont(BuildMissingGlyphOutlineFont());
        using var atlas = new GlyphAtlas(HeadlessWindow.Shared.Context, atlasSize: 64);
        GlyphInfo first = atlas.GetOrCreateGlyph(font, 'A', 8f, subpixelX: 0);
        ulong generation = atlas.Generation;

        for (int size = 9; size <= 40 && !atlas.CapacityExceeded; size++)
        {
            for (byte subpixel = 0; subpixel < 4; subpixel++)
            {
                atlas.GetOrCreateGlyph(font, 'A', size, subpixel);
            }
        }

        GlyphInfo cached = atlas.GetOrCreateGlyph(font, 'A', 8f, subpixelX: 0);

        Assert.True(atlas.CapacityExceeded);
        Assert.Equal(generation, atlas.Generation);
        Assert.Equal(first.X, cached.X);
        Assert.Equal(first.Y, cached.Y);
        Assert.Equal(first.TexCoordMin, cached.TexCoordMin);
        Assert.Equal(first.TexCoordMax, cached.TexCoordMax);
    }

    [Fact]
    public void GlyphAtlasBatchFlushesBeforeUniformRingWraps()
    {
        const int glyphCount = 12;
        const uint atlasSize = 256;
        const uint ringBufferSize = 8 * 256;
        var font = new TtfFont(BuildMissingGlyphOutlineFont());
        var constructor = typeof(GlyphAtlas).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(WgpuContext), typeof(uint), typeof(uint)],
            modifiers: null);
        Assert.NotNull(constructor);
        using var atlas = Assert.IsType<GlyphAtlas>(constructor.Invoke(
            [HeadlessWindow.Shared.Context, atlasSize, ringBufferSize]));
        GlyphInfo first = default;
        GlyphInfo last = default;

        atlas.BeginBatch();
        try
        {
            for (int index = 0; index < glyphCount; index++)
            {
                GlyphInfo info = atlas.GetOrCreateGlyph(font, 'A', 8f + index * 0.0001f);
                if (index == 0)
                {
                    first = info;
                }
                last = info;
            }
        }
        finally
        {
            atlas.EndBatch();
        }

        Assert.False(atlas.CapacityExceeded);
        var pixels = atlas.AtlasTexture.ReadPixels();
        Assert.True(ReadGlyphAtlasCoverage(pixels, first, atlasSize, 6, 6) > 200);
        Assert.True(ReadGlyphAtlasCoverage(pixels, last, atlasSize, 6, 6) > 200);
    }

    [Fact]
    public void GlyphAtlasCheckpointMakesPendingCoverageVisibleWithoutEndingBatch()
    {
        const uint atlasSize = 128;
        var font = new TtfFont(BuildMissingGlyphOutlineFont());
        using var atlas = new GlyphAtlas(HeadlessWindow.Shared.Context, atlasSize);

        atlas.BeginBatch();
        try
        {
            GlyphInfo first = atlas.GetOrCreateGlyph(font, 'A', 24f);

            atlas.FlushPendingBatchWork();

            var pixels = atlas.AtlasTexture.ReadPixels();
            Assert.True(ReadGlyphAtlasCoverage(pixels, first, atlasSize, 8, 8) > 200);

            GlyphInfo second = atlas.GetOrCreateGlyph(font, 'A', 25f);
            Assert.NotEqual((first.X, first.Y), (second.X, second.Y));
        }
        finally
        {
            atlas.EndBatch();
        }
    }

    [Fact]
    public void SmallFallbackFaceIsSharedAcrossGlyphs()
    {
        string path = Path.Combine(Path.GetTempPath(), $"progpu-fallback-{Guid.NewGuid():N}.ttf");
        File.WriteAllBytes(path, BuildMissingGlyphOutlineFont());
        try
        {
            TtfFont? first = TextLayout.GetOrLoadFallbackFont(path, faceIndex: 0, glyphIndex: 0);
            TtfFont? second = TextLayout.GetOrLoadFallbackFont(path, faceIndex: 0, glyphIndex: 1);

            Assert.NotNull(first);
            Assert.Same(first, second);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ColdCachedLayerIncludesGlyphCoverageOnItsFirstFrame()
    {
        var font = new TtfFont(BuildMissingGlyphOutlineFont());
        using var window = new HeadlessWindow(48, 48);
        var visual = new CachedLayerGlyphVisual(font);
        window.Content = visual;

        window.Render();

        Assert.NotNull(visual.LayerTexture);
        var pixels = window.ReadPixels();
        int coveredPixel = (33 * 48 + 14) * 4;
        Assert.True(
            pixels[coveredPixel] > 200,
            "The first cached-layer frame must sample glyph coverage after rasterization is submitted.");

        int cachedGlyphCount = window.Compositor.Atlas.CachedGlyphCount;
        ulong atlasGeneration = window.Compositor.Atlas.Generation;
        ulong layerTextureId = visual.LayerTexture.Id;

        window.Render();

        Assert.Equal(cachedGlyphCount, window.Compositor.Atlas.CachedGlyphCount);
        Assert.Equal(atlasGeneration, window.Compositor.Atlas.Generation);
        Assert.Equal(layerTextureId, visual.LayerTexture.Id);
    }

    [Fact]
    public void ClippedGlyphDoesNotConsumeGlyphAtlasResidency()
    {
        var font = new TtfFont(BuildMissingGlyphOutlineFont());
        using var window = new HeadlessWindow(64, 48);
        window.Content = new ClippedGlyphRunVisual(font);

        window.Render();

        Assert.Equal(1, window.Compositor.Atlas.CachedGlyphCount);
    }

    [Fact]
    public void GlyphAtlasCapacityExhaustionFallsBackWithoutDroppingGlyphs()
    {
        var font = new TtfFont(BuildMissingGlyphOutlineFont());
        using var window = new HeadlessWindow(
            96,
            64,
            CompositorOptions.Default with
            {
                GlyphAtlasSize = 32,
                PathAtlasSize = 256
            });
        window.Content = new AtlasOverflowGlyphRunVisual(font);

        window.Render();

        Assert.True(window.Compositor.Atlas.CapacityExceeded);
        Assert.Contains(
            GetDrawCalls(window.Compositor),
            drawCall => drawCall.Type == Compositor.DrawCallType.Text && drawCall.IndexCount > 0);
        Assert.Contains(
            GetDrawCalls(window.Compositor),
            drawCall => drawCall.Type == Compositor.DrawCallType.Vector && drawCall.IndexCount > 0);

        var pixels = window.ReadPixels();
        for (int glyphIndex = 0; glyphIndex < 4; glyphIndex++)
        {
            int x = glyphIndex * 20 + 6;
            int redOffset = (33 * 96 + x) * 4;
            Assert.True(
                pixels[redOffset] > 200,
                $"Glyph {glyphIndex} was dropped after glyph-atlas capacity exhaustion.");
        }
    }

    [Fact]
    public void GlyphRunBrushOpacityComposesWithVisualOpacity()
    {
        var font = new TtfFont(BuildMissingGlyphOutlineFont());
        using var window = new HeadlessWindow(64, 48);
        window.Content = new GlyphBrushOpacityVisual(font);

        window.Render();

        var pixels = window.ReadPixels();
        int opaqueRed = (33 * 64 + 6) * 4;
        int composedRed = (33 * 64 + 30) * 4;
        Assert.True(pixels[opaqueRed] > 200);
        Assert.InRange(pixels[composedRed], (byte)45, (byte)85);
    }

    [Fact]
    public void GpuSeriesDrawCallColorsIncludeBrushAndActiveOpacity()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(64, 64);
        window.Content = new GpuSeriesOpacityVisual();

        try
        {
            window.Render();

            Compositor.CompositorDrawCall[] drawCalls = GetDrawCalls(window.Compositor);
            Compositor.CompositorDrawCall line = Assert.Single(drawCalls, drawCall => drawCall.Type == Compositor.DrawCallType.ChartLine);
            Compositor.CompositorDrawCall scatter = Assert.Single(drawCalls, drawCall => drawCall.Type == Compositor.DrawCallType.ChartScatter);

            Assert.Equal(0.15f, line.Color.W, precision: 4);
            Assert.Equal(0.1f, scatter.Color.W, precision: 4);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void CachedLayerRefreshesWhenPhysicalTextureSizeChanges()
    {
        using var window = new HeadlessWindow(64, 64);
        using var target1x = new GpuTexture(
            window.Context,
            32,
            16,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Layer Cache 1x Target");
        using var target2x = new GpuTexture(
            window.Context,
            64,
            32,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Layer Cache 2x Target");
        var visual = new CachedLayerResizeVisual();

        window.Compositor.RenderOffscreen(
            visual,
            width: 32,
            height: 16,
            targetTexture: target1x,
            padding: 0f,
            dpiScale: 1f);

        Assert.NotNull(visual.LayerTexture);
        Assert.Equal(32u, visual.LayerTexture.Width);
        Assert.Equal(16u, visual.LayerTexture.Height);
        Assert.False(visual.IsDirty);

        window.Compositor.RenderOffscreen(
            visual,
            width: 32,
            height: 16,
            targetTexture: target2x,
            padding: 0f,
            dpiScale: 2f);

        Assert.NotNull(visual.LayerTexture);
        Assert.Equal(64u, visual.LayerTexture.Width);
        Assert.Equal(32u, visual.LayerTexture.Height);
        Assert.False(visual.IsDirty);
    }

    [Fact]
    public void CachedLayerUsesCeilingForFractionalPhysicalTextureSize()
    {
        using var window = new HeadlessWindow(128, 64);
        using var target = new GpuTexture(
            window.Context,
            126,
            26,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Fractional Layer Cache Target");
        var visual = new CachedLayerResizeVisual(new Vector2(100.5f, 20.25f));

        window.Compositor.RenderOffscreen(
            visual,
            width: 101,
            height: 21,
            targetTexture: target,
            padding: 0f,
            dpiScale: 1.25f);

        Assert.NotNull(visual.LayerTexture);
        Assert.Equal(126u, visual.LayerTexture.Width);
        Assert.Equal(26u, visual.LayerTexture.Height);
        Assert.False(visual.IsDirty);
    }

    [Fact]
    public unsafe void ExplicitPhysicalRenderTargetScalesLogicalSceneToFramebuffer()
    {
        using var window = new HeadlessWindow(20, 20);
        using var target = new GpuTexture(
            window.Context,
            20,
            20,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "HiDPI Explicit Render Target");
        var visual = new SolidLogicalSceneVisual();

        window.Compositor.RenderScene(
            visual,
            logicalWidth: 10,
            logicalHeight: 10,
            renderTargetWidth: 20,
            renderTargetHeight: 20,
            dpiScale: 2f,
            target.ViewPtr);

        var pixels = target.ReadPixels();
        var lowerRight = ReadPixel(pixels, target.Width, x: 15, y: 15);

        Assert.True(lowerRight.R >= 220, $"Expected logical scene to fill the physical target width, found {lowerRight}.");
        Assert.True(lowerRight.G <= 35, $"Expected logical scene green channel to stay low, found {lowerRight}.");
        Assert.True(lowerRight.B <= 35, $"Expected logical scene blue channel to stay low, found {lowerRight}.");
        Assert.Equal(255, lowerRight.A);
    }

    [Fact]
    public unsafe void ExplicitPhysicalRenderTargetPinsViewportToPhysicalFramebuffer()
    {
        using var window = new HeadlessWindow(24, 24);
        using var target = new GpuTexture(
            window.Context,
            21,
            17,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "HiDPI Explicit Viewport Target");
        var visual = new SolidLogicalSceneVisual(new Vector2(10f, 8f));

        window.Compositor.RenderScene(
            visual,
            logicalWidth: 10,
            logicalHeight: 8,
            renderTargetWidth: 21,
            renderTargetHeight: 17,
            dpiScale: 2f,
            target.ViewPtr);

        var pixels = target.ReadPixels();
        var lowerRight = ReadPixel(pixels, target.Width, x: 19, y: 15);

        Assert.True(lowerRight.R >= 220, $"Expected explicit physical viewport to fill target width, found {lowerRight}.");
        Assert.True(lowerRight.G <= 35, $"Expected explicit physical viewport green channel to stay low, found {lowerRight}.");
        Assert.True(lowerRight.B <= 35, $"Expected explicit physical viewport blue channel to stay low, found {lowerRight}.");
        Assert.Equal(255, lowerRight.A);
    }

    [Fact]
    public unsafe void ExplicitRenderTargetViewportOffsetsLogicalSceneWithinFramebuffer()
    {
        using var window = new HeadlessWindow(24, 24);
        using var target = new GpuTexture(
            window.Context,
            24,
            24,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "Offset Explicit Viewport Target");
        var visual = new SolidLogicalSceneVisual(new Vector2(10f, 8f));

        window.Compositor.RenderScene(
            visual,
            logicalWidth: 10,
            logicalHeight: 8,
            renderTargetWidth: 24,
            renderTargetHeight: 24,
            renderTargetViewport: new RenderTargetViewport(2f, 4f, 20f, 16f),
            dpiScale: 2f,
            target.ViewPtr);

        var pixels = target.ReadPixels();
        var outsideTopLeft = ReadPixel(pixels, target.Width, x: 1, y: 4);
        var insideTopLeft = ReadPixel(pixels, target.Width, x: 2, y: 4);
        var insideLowerRight = ReadPixel(pixels, target.Width, x: 21, y: 19);
        var outsideLowerRight = ReadPixel(pixels, target.Width, x: 22, y: 20);

        Assert.True(outsideTopLeft.R <= 35, $"Expected pixels before viewport X to stay clear, found {outsideTopLeft}.");
        Assert.True(insideTopLeft.R >= 220, $"Expected viewport origin to contain logical scene content, found {insideTopLeft}.");
        Assert.True(insideLowerRight.R >= 220, $"Expected logical scene to fill offset viewport, found {insideLowerRight}.");
        Assert.True(outsideLowerRight.R <= 35, $"Expected pixels after viewport to stay clear, found {outsideLowerRight}.");
    }

    [Fact]
    public unsafe void ExplicitRenderTargetViewportKeepsOpacityMaskSamplingAligned()
    {
        using var window = new HeadlessWindow(32, 24);
        using var target = new GpuTexture(
            window.Context,
            32,
            24,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "Offset Explicit Viewport Mask Target");
        var visual = new OffsetViewportOpacityMaskVisual();

        window.Compositor.RenderScene(
            visual,
            logicalWidth: 8,
            logicalHeight: 8,
            renderTargetWidth: 32,
            renderTargetHeight: 24,
            renderTargetViewport: new RenderTargetViewport(8f, 4f, 16f, 16f),
            dpiScale: 2f,
            target.ViewPtr);

        var pixels = target.ReadPixels();
        var visibleMaskedPixel = ReadPixel(pixels, target.Width, x: 10, y: 6);
        var clippedMaskedPixel = ReadPixel(pixels, target.Width, x: 14, y: 6);
        var maskTexturePool = GetMaskTexturePool(window.Compositor);

        Assert.True(visibleMaskedPixel.R >= 220, $"Expected opacity mask to align with viewport origin, found {visibleMaskedPixel}.");
        Assert.True(clippedMaskedPixel.R <= 35, $"Expected pixels outside the opacity mask bounds to stay clear, found {clippedMaskedPixel}.");
        Assert.Contains(maskTexturePool, texture => texture.Width == 32 && texture.Height == 24);
    }

    [Fact]
    public unsafe void ExplicitRenderTargetViewportKeepsImageEffectMaskSamplingAligned()
    {
        using var window = new HeadlessWindow(32, 24);
        using var target = new GpuTexture(
            window.Context,
            32,
            24,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "Offset Explicit Viewport Image Effect Mask Target");
        using var source = CreateSolidTexture(window.Context, new byte[] { 255, 0, 0, 255 }, "Offset Viewport Image Effect Source");
        var visual = new OffsetViewportImageEffectMaskVisual(source);

        window.Compositor.RenderScene(
            visual,
            logicalWidth: 8,
            logicalHeight: 8,
            renderTargetWidth: 32,
            renderTargetHeight: 24,
            renderTargetViewport: new RenderTargetViewport(8f, 4f, 16f, 16f),
            dpiScale: 2f,
            target.ViewPtr);

        var pixels = target.ReadPixels();
        var visibleMaskedPixel = ReadPixel(pixels, target.Width, x: 10, y: 6);
        var clippedMaskedPixel = ReadPixel(pixels, target.Width, x: 14, y: 6);
        var maskTexturePool = GetMaskTexturePool(window.Compositor);

        Assert.True(visibleMaskedPixel.R >= 220, $"Expected image effect mask to align with viewport origin, found {visibleMaskedPixel}.");
        Assert.True(clippedMaskedPixel.R <= 35, $"Expected image effect pixels outside the opacity mask bounds to stay clear, found {clippedMaskedPixel}.");
        Assert.Contains(maskTexturePool, texture => texture.Width == 32 && texture.Height == 24);
    }

    [Fact]
    public unsafe void ExplicitRenderTargetViewportKeepsWpfShaderEffectMaskSamplingAligned()
    {
        using var window = new HeadlessWindow(32, 24);
        using var target = new GpuTexture(
            window.Context,
            32,
            24,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "Offset Explicit Viewport WPF Shader Effect Mask Target");
        using var source = CreateSolidTexture(window.Context, new byte[] { 255, 0, 0, 255 }, "Offset Viewport WPF Shader Effect Source");
        var visual = new OffsetViewportWpfShaderEffectMaskVisual(source);

        window.Compositor.RenderScene(
            visual,
            logicalWidth: 8,
            logicalHeight: 8,
            renderTargetWidth: 32,
            renderTargetHeight: 24,
            renderTargetViewport: new RenderTargetViewport(8f, 4f, 16f, 16f),
            dpiScale: 2f,
            target.ViewPtr);

        var pixels = target.ReadPixels();
        var visibleMaskedPixel = ReadPixel(pixels, target.Width, x: 10, y: 6);
        var clippedMaskedPixel = ReadPixel(pixels, target.Width, x: 14, y: 6);
        var maskTexturePool = GetMaskTexturePool(window.Compositor);

        Assert.True(visibleMaskedPixel.R >= 220, $"Expected WPF shader-effect mask to align with viewport origin, found {visibleMaskedPixel}.");
        Assert.True(clippedMaskedPixel.R <= 35, $"Expected WPF shader-effect pixels outside the opacity mask bounds to stay clear, found {clippedMaskedPixel}.");
        Assert.Contains(maskTexturePool, texture => texture.Width == 32 && texture.Height == 24);
    }

    [Fact]
    public unsafe void ExplicitPhysicalRenderTargetFeedsFramebufferSizeToCanvasPixelHelpers()
    {
        using var window = new HeadlessWindow(24, 24);
        using var target = new GpuTexture(
            window.Context,
            21,
            17,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "HiDPI Explicit Canvas Size Target");
        var extension = new CanvasSizeRecordingExtension();
        window.Compositor.RegisterExtension(9004, extension);

        var visual = new DrawingVisual
        {
            Size = new Vector2(10f, 8f)
        };
        visual.Context.DrawExtension(9004);

        window.Compositor.RenderScene(
            visual,
            logicalWidth: 10,
            logicalHeight: 8,
            renderTargetWidth: 21,
            renderTargetHeight: 17,
            dpiScale: 2f,
            target.ViewPtr);

        Assert.Equal(1, extension.RenderCount);
        Assert.Equal(21f, extension.CanvasPixelWidth);
        Assert.Equal(17f, extension.CanvasPixelHeight);
    }

    [Fact]
    public unsafe void ExplicitRenderTargetViewportFeedsClientRectToCanvasPixelHelpers()
    {
        using var window = new HeadlessWindow(24, 24);
        using var target = new GpuTexture(
            window.Context,
            24,
            24,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Offset Explicit Canvas Size Target");
        var extension = new CanvasSizeRecordingExtension();
        window.Compositor.RegisterExtension(9006, extension);

        var visual = new DrawingVisual
        {
            Size = new Vector2(10f, 8f)
        };
        visual.Context.DrawExtension(9006);

        window.Compositor.RenderScene(
            visual,
            logicalWidth: 10,
            logicalHeight: 8,
            renderTargetWidth: 24,
            renderTargetHeight: 24,
            renderTargetViewport: new RenderTargetViewport(2f, 4f, 20f, 16f),
            dpiScale: 2f,
            target.ViewPtr);

        Assert.Equal(1, extension.RenderCount);
        Assert.Equal(2f, extension.CanvasPixelX);
        Assert.Equal(4f, extension.CanvasPixelY);
        Assert.Equal(20f, extension.CanvasPixelWidth);
        Assert.Equal(16f, extension.CanvasPixelHeight);
    }

    [Fact]
    public void RenderOffscreenUsesOffscreenTargetSizeWhenExplicitOuterTargetIsActive()
    {
        using var window = new HeadlessWindow(24, 24);
        using var target = new GpuTexture(
            window.Context,
            19,
            13,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Offscreen Explicit Outer Target Regression");
        var extension = new CanvasSizeRecordingExtension();
        window.Compositor.RegisterExtension(9005, extension);

        SetCompositorField(window.Compositor, "_explicitRenderTargetWidth", (uint?)211u);
        SetCompositorField(window.Compositor, "_explicitRenderTargetHeight", (uint?)113u);
        SetCompositorField(window.Compositor, "_explicitRenderTargetViewport", new RenderTargetViewport(7f, 11f, 211f, 113f));
        SetCompositorField(window.Compositor, "_explicitDpiScale", (float?)3f);

        var visual = new DrawingVisual
        {
            Size = new Vector2(10f, 8f)
        };
        visual.Context.DrawExtension(9005);

        window.Compositor.RenderOffscreen(
            visual,
            width: 10,
            height: 8,
            targetTexture: target,
            padding: 0f,
            dpiScale: 2f);

        Assert.Equal(1, extension.RenderCount);
        Assert.Equal(19f, extension.CanvasPixelWidth);
        Assert.Equal(13f, extension.CanvasPixelHeight);
        Assert.Equal(211u, Assert.IsType<uint>(GetRawCompositorField(window.Compositor, "_explicitRenderTargetWidth")));
        Assert.Equal(113u, Assert.IsType<uint>(GetRawCompositorField(window.Compositor, "_explicitRenderTargetHeight")));
        Assert.Equal(
            new RenderTargetViewport(7f, 11f, 211f, 113f),
            Assert.IsType<RenderTargetViewport>(GetRawCompositorField(window.Compositor, "_explicitRenderTargetViewport")));
        Assert.Equal(3f, Assert.IsType<float>(GetRawCompositorField(window.Compositor, "_explicitDpiScale")));
    }

    [Fact]
    public void CachedLayerRecreatesTextureForCurrentWebGpuContext()
    {
        using var firstWindow = new HeadlessWindow(64, 64);
        using var secondWindow = new HeadlessWindow(64, 64);
        using var firstTarget = new GpuTexture(
            firstWindow.Context,
            32,
            16,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "First Context Layer Target");
        using var secondTarget = new GpuTexture(
            secondWindow.Context,
            32,
            16,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Second Context Layer Target");
        var visual = new CachedLayerResizeVisual();

        firstWindow.Compositor.RenderOffscreen(
            visual,
            width: 32,
            height: 16,
            targetTexture: firstTarget,
            padding: 0f,
            dpiScale: 1f);

        GpuTexture? firstLayer = visual.LayerTexture;
        Assert.NotNull(firstLayer);
        Assert.Same(firstWindow.Context, firstLayer.Context);
        Assert.False(firstLayer.IsDisposed);

        secondWindow.Compositor.RenderOffscreen(
            visual,
            width: 32,
            height: 16,
            targetTexture: secondTarget,
            padding: 0f,
            dpiScale: 1f);

        GpuTexture? secondLayer = visual.LayerTexture;
        Assert.NotNull(secondLayer);
        Assert.NotSame(firstLayer, secondLayer);
        Assert.Same(secondWindow.Context, secondLayer.Context);
        Assert.True(firstLayer.IsDisposed);
        Assert.False(secondLayer.IsDisposed);
    }

    [Fact]
    public void CachedLayerTextureIsReleasedWhenVisualLeavesActiveTree()
    {
        using var window = new HeadlessWindow(64, 64);
        var root = new StackPanel
        {
            Width = 64f,
            Height = 64f
        };
        var visual = new CachedLayerResizeVisual();
        root.AddChild(visual);
        window.Content = root;

        window.Render();

        GpuTexture? layer = visual.LayerTexture;
        Assert.NotNull(layer);
        Assert.False(layer.IsDisposed);

        root.RemoveChild(visual);
        window.Render();

        Assert.True(layer.IsDisposed);
        Assert.Null(visual.LayerTexture);
    }

    [Fact]
    public void CachedLayerTextureIsReleasedWhenCacheAsLayerIsDisabled()
    {
        using var window = new HeadlessWindow(64, 64);
        var root = new StackPanel
        {
            Width = 64f,
            Height = 64f
        };
        var visual = new CachedLayerResizeVisual();
        root.AddChild(visual);
        window.Content = root;

        window.Render();

        GpuTexture? layer = visual.LayerTexture;
        Assert.NotNull(layer);
        Assert.False(layer.IsDisposed);

        visual.CacheAsLayer = false;
        window.Render();

        Assert.True(layer.IsDisposed);
        Assert.Null(visual.LayerTexture);
    }

    [Fact]
    public void TransformedEllipticalRoundedRectanglePathFallbackAppliesTransformOnce()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(80, 40);
        window.Content = new TransformedEllipticalRoundedRectangleVisual();

        try
        {
            window.Render();

            byte[] pixels = window.ReadPixels();
            RgbaPixel expected = ReadPixel(pixels, window.Width, x: 32, y: 17);
            RgbaPixel doubleTransformed = ReadPixel(pixels, window.Width, x: 52, y: 22);

            Assert.True(expected.R >= 220, $"Expected once-transformed rounded rectangle center to be red, found {expected}.");
            Assert.True(expected.G <= 35, $"Expected once-transformed rounded rectangle center to keep green low, found {expected}.");
            Assert.True(expected.B <= 35, $"Expected once-transformed rounded rectangle center to keep blue low, found {expected}.");
            Assert.Equal(255, expected.A);

            Assert.True(
                doubleTransformed.R < 80 || doubleTransformed.A < 220,
                $"Expected double-transformed location to remain outside the rounded rectangle fill, found {doubleTransformed}.");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void RoundedRectangleWithExplicitZeroRadiusYRendersAsRectangle()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 24);
        window.Content = new ExplicitZeroRadiusYRoundedRectangleVisual();

        try
        {
            window.Render();

            byte[] pixels = window.ReadPixels();
            RgbaPixel corner = ReadPixel(pixels, window.Width, x: 5, y: 5);

            Assert.True(corner.R >= 220, $"Expected explicit zero RadiusY to keep the rectangle corner red, found {corner}.");
            Assert.True(corner.G <= 35, $"Expected explicit zero RadiusY to keep green low, found {corner}.");
            Assert.True(corner.B <= 35, $"Expected explicit zero RadiusY to keep blue low, found {corner}.");
            Assert.Equal(255, corner.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void RenderOffscreenKeepsPathAtlasBuffersAliveAfterSubmit()
    {
        using var window = new HeadlessWindow(32, 32);
        using var target = new GpuTexture(
            window.Context,
            32,
            32,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "RenderOffscreen PathAtlas Buffer Lifetime Test");
        var visual = new DrawingVisual
        {
            Size = new Vector2(32f, 32f)
        };
        visual.Context.DrawPath(
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            pen: null,
            PrimitivePathGeometry.CreateRoundedRectangle(4f, 4f, 20f, 16f, 4f, 4f));

        window.Compositor.RenderOffscreen(
            visual,
            width: 32,
            height: 32,
            targetTexture: target,
            padding: 0f,
            dpiScale: 1f);

        Assert.NotEmpty(GetPathAtlasTempBuffers(window.Compositor));
    }

    [Fact]
    public void RenderOffscreenAdvancesPathAtlasFrameBetweenTopLevelPasses()
    {
        using var window = new HeadlessWindow(32, 32);
        using var target = new GpuTexture(
            window.Context,
            32,
            32,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "RenderOffscreen PathAtlas Frame Lifecycle Test");
        var visual = new DrawingVisual
        {
            Size = new Vector2(32f, 32f)
        };
        visual.Context.DrawPath(
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            pen: null,
            PrimitivePathGeometry.CreateRoundedRectangle(4f, 4f, 20f, 16f, 4f, 4f));

        uint initialFrame = GetPathAtlasFrameNumber(window.Compositor);

        window.Compositor.RenderOffscreen(visual, 32, 32, target, 0f, 1f);
        window.Compositor.RenderOffscreen(visual, 32, 32, target, 0f, 1f);

        Assert.Equal(initialFrame + 2, GetPathAtlasFrameNumber(window.Compositor));
    }

    [Fact]
    public void RenderOffscreenRunsExtensionFrameScopeForTopLevelPass()
    {
        using var window = new HeadlessWindow(32, 32);
        using var target = new GpuTexture(
            window.Context,
            32,
            32,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "RenderOffscreen Extension Frame Scope Test");
        var extension = new CountingExtension();
        window.Compositor.RegisterExtension(9001, extension);

        var visual = new DrawingVisual
        {
            Size = new Vector2(32f, 32f)
        };
        visual.Context.DrawRectangle(
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            pen: null,
            new Rect(0f, 0f, 16f, 16f));

        window.Compositor.RenderOffscreen(
            visual,
            width: 32,
            height: 32,
            targetTexture: target,
            padding: 0f,
            dpiScale: 1f);

        Assert.Equal(1, extension.BeginFrameCount);
        Assert.Equal(1, extension.EndFrameCount);
    }

    [Fact]
    public async Task ConcurrentRenderOffscreenCallsKeepFrameStateIsolated()
    {
        using var window = new HeadlessWindow(16, 16);
        using var redTarget = new GpuTexture(
            window.Context,
            16,
            16,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "Concurrent Red Target");
        using var greenTarget = new GpuTexture(
            window.Context,
            16,
            16,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "Concurrent Green Target");
        var redVisual = new DrawingVisual { Size = new Vector2(16f, 16f) };
        var greenVisual = new DrawingVisual { Size = new Vector2(16f, 16f) };
        redVisual.Context.DrawRectangle(
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            null,
            new Rect(0f, 0f, 16f, 16f));
        greenVisual.Context.DrawRectangle(
            new SolidColorBrush(new Vector4(0f, 1f, 0f, 1f)),
            null,
            new Rect(0f, 0f, 16f, 16f));

        await Task.WhenAll(
            Task.Run(() => window.Compositor.RenderOffscreen(
                redVisual,
                16,
                16,
                redTarget,
                0f,
                1f)),
            Task.Run(() => window.Compositor.RenderOffscreen(
                greenVisual,
                16,
                16,
                greenTarget,
                0f,
                1f)));

        var red = ReadPixel(redTarget.ReadPixels(), redTarget.Width, 8, 8);
        var green = ReadPixel(greenTarget.ReadPixels(), greenTarget.Width, 8, 8);
        Assert.True(red.R >= 220 && red.G <= 35 && red.B <= 35, $"Expected red target, found {red}.");
        Assert.True(green.G >= 220 && green.R <= 35 && green.B <= 35, $"Expected green target, found {green}.");
    }

    [Fact]
    public void RenderSceneEndsExtensionFrameWhenCompilationThrows()
    {
        using var window = new HeadlessWindow(32, 32);
        var extension = new CountingExtension();
        window.Compositor.RegisterExtension(9002, extension);
        window.Content = new ThrowingVisual();

        Assert.Throws<InvalidOperationException>(() => window.Render());
        Assert.Equal(1, extension.BeginFrameCount);
        Assert.Equal(1, extension.EndFrameCount);
    }

    [Fact]
    public void RenderOffscreenEndsExtensionFrameWhenCompilationThrows()
    {
        using var window = new HeadlessWindow(32, 32);
        using var target = new GpuTexture(
            window.Context,
            32,
            32,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "RenderOffscreen Extension Frame Exception Test");
        var extension = new CountingExtension();
        window.Compositor.RegisterExtension(9003, extension);

        Assert.Throws<InvalidOperationException>(() => window.Compositor.RenderOffscreen(
            new ThrowingVisual(),
            width: 32,
            height: 32,
            targetTexture: target,
            padding: 0f,
            dpiScale: 1f));
        Assert.Equal(1, extension.BeginFrameCount);
        Assert.Equal(1, extension.EndFrameCount);
    }

    [Fact]
    public void RenderOffscreenAllocatesOpacityMaskTextureAtPhysicalSize()
    {
        using var window = new HeadlessWindow(32, 32);
        using var target = new GpuTexture(
            window.Context,
            64,
            64,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "RenderOffscreen Physical Mask Target");
        var visual = new OpacityMaskedVisual();

        window.Compositor.RenderOffscreen(
            visual,
            width: 32,
            height: 32,
            targetTexture: target,
            padding: 0f,
            dpiScale: 2f);

        var maskTexturePool = GetMaskTexturePool(window.Compositor);
        Assert.Contains(maskTexturePool, texture => texture.Width == 64 && texture.Height == 64);
    }

    [Fact]
    public void PbgraTextureUploadBumpsGenerationForWpfShaderEffectCache()
    {
        using var window = new HeadlessWindow(1, 1);
        using var texture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Bgra8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "PBgra Generation Texture");
        var parameters = new WpfShaderEffectParams
        {
            Texture = texture
        };
        var effect = new WpfShaderEffect(parameters);
        var initialGeneration = texture.Generation;
        var initialCacheKey = GetRenderCacheKey(effect);

        texture.WritePbgra32SubRect(
            new Pbgra32PixelBuffer(
                width: 1,
                height: 1,
                stride: 4,
                pixels: new byte[] { 0, 0, 255, 255 }),
            x: 0,
            y: 0);

        Assert.True(texture.Generation > initialGeneration);
        Assert.NotEqual(initialCacheKey, GetRenderCacheKey(effect));
    }

    [Fact]
    public void GpuTextureReadPixelsRejectsTextureWithoutCopySrcUsage()
    {
        using var window = new HeadlessWindow(1, 1);
        using var texture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "ReadPixels Missing CopySrc Texture");

        var exception = Assert.Throws<System.InvalidOperationException>(() => texture.ReadPixels());
        Assert.Equal("Texture was not created with CopySrc usage.", exception.Message);
    }

    [Fact]
    public void GpuTextureReadPixelsReusesCallerOwnedReadbackBuffer()
    {
        using var window = new HeadlessWindow(2, 1);
        using var texture = new GpuTexture(
            window.Context,
            2,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.CopySrc | TextureUsage.CopyDst,
            "Reusable Readback Texture");
        using var readbackBuffer = new GpuTextureReadbackBuffer(window.Context);
        var first = new byte[8];
        var second = new byte[8];

        texture.WritePixels(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 });
        texture.ReadPixels(first, readbackBuffer);
        var allocatedSize = readbackBuffer.BufferSize;
        texture.WritePixels(new byte[] { 0, 0, 255, 255, 255, 255, 255, 255 });
        texture.ReadPixels(second, readbackBuffer);

        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 }, first);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 255, 255, 255 }, second);
        Assert.Equal(allocatedSize, readbackBuffer.BufferSize);
    }

    [Fact]
    public void GpuTextureClearRenderTargetClearsWithoutCpuUpload()
    {
        using var window = new HeadlessWindow(2, 2);
        using var texture = new GpuTexture(
            window.Context,
            2,
            2,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst,
            "GPU Clear Texture");
        texture.WritePixels(Enumerable.Repeat((byte)255, 16).ToArray());
        var generationBeforeClear = texture.Generation;

        texture.ClearRenderTarget();

        Assert.Equal(new byte[16], texture.ReadPixels());
        Assert.Equal(generationBeforeClear + 1, texture.Generation);
    }

    [Fact]
    public void GpuTextureClearRenderTargetRequiresRenderAttachmentUsage()
    {
        using var window = new HeadlessWindow(1, 1);
        using var texture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.CopyDst,
            "GPU Clear Missing RenderAttachment Texture");

        var exception = Assert.Throws<InvalidOperationException>(() => texture.ClearRenderTarget());

        Assert.Equal("GPU texture clear requires RenderAttachment usage.", exception.Message);
    }

    [Fact]
    public unsafe void GpuTextureBlitterPreservesPixelOrientationAndChannels()
    {
        using var window = new HeadlessWindow(2, 2);
        using var source = new GpuTexture(
            window.Context,
            2,
            2,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "GPU Blit Source");
        using var destination = new GpuTexture(
            window.Context,
            2,
            2,
            TextureFormat.Bgra8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "GPU Blit Destination");
        byte[] sourcePixels =
        [
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255
        ];
        source.WritePixels(sourcePixels);

        GpuTextureBlitter.Blit(source, destination.ViewPtr, destination.Format);

        Assert.Equal(
            new byte[]
            {
                0, 0, 255, 255, 0, 255, 0, 255,
                255, 0, 0, 255, 255, 255, 255, 255
            },
            destination.ReadPixels());
    }

    [Fact]
    public void CompositorOptionsControlEagerGpuReservations()
    {
        using var window = new HeadlessWindow(1, 1);
        var options = new CompositorOptions
        {
            GlyphAtlasSize = 256,
            PathAtlasSize = 512,
            InitialVertexCount = 1024,
            InitialIndexCount = 1536
        };
        using var compositor = new Compositor(window.Context, TextureFormat.Rgba8Unorm, options);

        Assert.Same(options, compositor.Options);
        Assert.Equal(256u, compositor.Atlas.AtlasSize);
        Assert.Equal(512u, compositor.PathAtlas.AtlasSize);
        Assert.Equal(
            options.InitialVertexCount * (uint)Marshal.SizeOf<VectorVertex>(),
            GetCompositorField<GpuBuffer>(compositor, "_vectorVertexBuffer").Size);
        Assert.Equal(
            options.InitialIndexCount * sizeof(uint),
            GetCompositorField<GpuBuffer>(compositor, "_vectorIndexBuffer").Size);
    }

    [Fact]
    public unsafe void CompositorCanDisableGpuHitTestIndexCompilation()
    {
        using var window = new HeadlessWindow(32, 32);
        using var target = new GpuTexture(
            window.Context,
            32,
            32,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "GPU Hit Test Disabled Target");
        using var compositor = new Compositor(
            window.Context,
            TextureFormat.Rgba8Unorm,
            CompositorOptions.Default with { EnableGpuHitTesting = false });
        var visual = new DrawingVisual();
        visual.Context.DrawRectangle(
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            null,
            new Rect(0f, 0f, 32f, 32f));

        compositor.RenderScene(visual, 32, 32, target.ViewPtr);

        Assert.Null(compositor.LastHitTestIndex);
        Assert.Null(compositor.LastHitTestDeviceIndex);
    }

    [Fact]
    public void RichTextBlockReusesCommandsUntilContentIsInvalidated()
    {
        var font = new TtfFont(BuildMissingGlyphOutlineFont());
        var block = new RichTextBlock
        {
            Font = font,
            FontSize = 14f,
            Foreground = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f))
        };
        block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run("abc"));
        block.Measure(new Vector2(200f, 50f));
        block.Arrange(new Rect(0f, 0f, 200f, 50f));

        var first = new DrawingContext();
        block.OnRender(first);
        var firstText = Assert.Single(first.Commands, command => command.Type == RenderCommandType.DrawText).Text;

        var second = new DrawingContext();
        block.OnRender(second);
        var secondText = Assert.Single(second.Commands, command => command.Type == RenderCommandType.DrawText).Text;
        Assert.Same(firstText, secondText);

        var green = new SolidColorBrush(new Vector4(0f, 1f, 0f, 1f));
        block.Foreground = green;
        block.Measure(new Vector2(200f, 50f));
        block.Arrange(new Rect(0f, 0f, 200f, 50f));
        var updated = new DrawingContext();
        block.OnRender(updated);

        Assert.Same(
            green,
            Assert.Single(updated.Commands, command => command.Type == RenderCommandType.DrawText).Brush);
    }

    [Fact]
    public void CompatibilityShimsReuseIsolatedCompositorScopes()
    {
        var previous = WgpuContext.Current;
        using var context = new WgpuContext();
        context.Initialize(null);
        try
        {
            WgpuContext.Current = context;
            using var surface = SKSurface.Create(
                new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var bitmap = new GdiBitmap(2, 2);

            var surfaceCompositor = GetSkiaSurfaceCompositor(context);
            var gdiCompositor = GetGdiBitmapCompositor(bitmap);
            var wpfCompositor = GetWpfCompositor();

            Assert.Same(gdiCompositor, GetGdiBitmapCompositor(bitmap));
            Assert.Same(wpfCompositor, GetWpfCompositor());
            Assert.NotSame(surfaceCompositor, gdiCompositor);
            Assert.NotSame(surfaceCompositor, wpfCompositor);
            Assert.NotSame(gdiCompositor, wpfCompositor);
        }
        finally
        {
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void GpuTextureWritePixelsSubRectRejectsOutOfBoundsRectBeforeUpload()
    {
        using var window = new HeadlessWindow(2, 2);
        using var texture = new GpuTexture(
            window.Context,
            2,
            2,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "SubRect Bounds Texture");

        var pixels = new byte[2 * 2 * 4];
        var exception = Assert.Throws<System.ArgumentOutOfRangeException>(
            () => texture.WritePixelsSubRect(pixels, x: 1, y: 1, subWidth: 2, subHeight: 2));

        Assert.Equal("pixels", exception.ParamName);
        Assert.Equal(1u, texture.Generation);
    }

    [Fact]
    public void GpuTextureWritePixelsSubRectTargetsRequestedArrayLayer()
    {
        using var window = new HeadlessWindow(2, 2);
        using var texture = new GpuTexture(
            window.Context,
            2,
            2,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc,
            "SubRect Array Layer Texture",
            depthOrArrayLayers: 2);
        byte[] layer0Pixels =
        [
            10, 20, 30, 255, 40, 50, 60, 255,
            70, 80, 90, 255, 100, 110, 120, 255
        ];
        byte[] layer1Pixels =
        [
            200, 10, 20, 255, 20, 200, 10, 255,
            10, 20, 200, 255, 240, 240, 240, 255
        ];

        texture.WritePixelsSubRect(layer0Pixels, x: 0, y: 0, subWidth: 2, subHeight: 2, arrayLayer: 0);
        texture.WritePixelsSubRect(layer1Pixels, x: 0, y: 0, subWidth: 2, subHeight: 2, arrayLayer: 1);

        var pixels = texture.ReadPixels();
        Assert.Equal(layer0Pixels, pixels[..16]);
        Assert.Equal(layer1Pixels, pixels[16..32]);

        var exception = Assert.Throws<System.ArgumentOutOfRangeException>(
            () => texture.WritePixelsSubRect(layer1Pixels, x: 0, y: 0, subWidth: 2, subHeight: 2, arrayLayer: 2));
        Assert.Equal("arrayLayer", exception.ParamName);
    }

    [Fact]
    public void GpuTextureWritePixelsSubRectTargetsRequestedMipLevel()
    {
        using var window = new HeadlessWindow(4, 4);
        using var texture = new GpuTexture(
            window.Context,
            4,
            4,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc,
            "SubRect Mip Texture",
            mipLevelCount: 2);
        byte[] mipPixels =
        [
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255
        ];

        texture.WritePixelsSubRect(mipPixels, x: 0, y: 0, subWidth: 2, subHeight: 2, mipLevel: 1);

        Assert.Equal(2u, texture.MipLevelCount);
        Assert.Equal(mipPixels, texture.ReadPixels(mipLevel: 1));

        var exception = Assert.Throws<System.ArgumentOutOfRangeException>(
            () => texture.WritePixelsSubRect(mipPixels, x: 0, y: 0, subWidth: 2, subHeight: 2, mipLevel: 2));
        Assert.Equal("mipLevel", exception.ParamName);
    }

    [Fact]
    public void GpuTextureCopyFromCopiesAllMipLevels()
    {
        using var window = new HeadlessWindow(4, 4);
        using var source = new GpuTexture(
            window.Context,
            4,
            4,
            TextureFormat.Rgba8Unorm,
            TextureUsage.CopySrc | TextureUsage.CopyDst,
            "Mipped Copy Source",
            mipLevelCount: 2);
        using var destination = new GpuTexture(
            window.Context,
            4,
            4,
            TextureFormat.Rgba8Unorm,
            TextureUsage.CopySrc | TextureUsage.CopyDst,
            "Mipped Copy Destination",
            mipLevelCount: 2);
        byte[] mip0Pixels =
        [
            1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255,
            13, 14, 15, 255, 16, 17, 18, 255, 19, 20, 21, 255, 22, 23, 24, 255,
            25, 26, 27, 255, 28, 29, 30, 255, 31, 32, 33, 255, 34, 35, 36, 255,
            37, 38, 39, 255, 40, 41, 42, 255, 43, 44, 45, 255, 46, 47, 48, 255
        ];
        byte[] mip1Pixels =
        [
            100, 0, 0, 255, 0, 100, 0, 255,
            0, 0, 100, 255, 100, 100, 100, 255
        ];

        source.WritePixels(mip0Pixels);
        source.WritePixels(mip1Pixels, mipLevel: 1);
        destination.CopyFrom(source);

        Assert.Equal(mip0Pixels, destination.ReadPixels());
        Assert.Equal(mip1Pixels, destination.ReadPixels(mipLevel: 1));
    }

    [Fact]
    public void GpuTextureCopyBaseLevelFromPreservesDestinationMipLevels()
    {
        using var window = new HeadlessWindow(4, 4);
        using var source = new GpuTexture(
            window.Context,
            4,
            4,
            TextureFormat.Rgba8Unorm,
            TextureUsage.CopySrc | TextureUsage.CopyDst,
            "Base-Level Copy Source");
        using var destination = new GpuTexture(
            window.Context,
            4,
            4,
            TextureFormat.Rgba8Unorm,
            TextureUsage.CopySrc | TextureUsage.CopyDst,
            "Base-Level Copy Destination",
            mipLevelCount: 2);
        var sourcePixels = Enumerable.Range(0, 4 * 4)
            .SelectMany(index => new byte[] { (byte)index, 20, 30, 255 })
            .ToArray();
        byte[] preservedMip =
        [
            100, 0, 0, 255, 0, 100, 0, 255,
            0, 0, 100, 255, 100, 100, 100, 255
        ];

        source.WritePixels(sourcePixels);
        destination.WritePixels(preservedMip, mipLevel: 1);
        destination.CopyBaseLevelFrom(source);

        Assert.Equal(sourcePixels, destination.ReadPixels());
        Assert.Equal(preservedMip, destination.ReadPixels(mipLevel: 1));
    }

    [Fact]
    public void GpuTextureGenerateMipmaps2DLinearDownsamplesEachLevel()
    {
        using var window = new HeadlessWindow(4, 4);
        using var texture = new GpuTexture(
            window.Context,
            4,
            4,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.RenderAttachment |
                TextureUsage.CopySrc | TextureUsage.CopyDst,
            "Linear Mipmap Texture",
            mipLevelCount: 3);
        byte[] basePixels =
        [
            255, 0, 0, 255, 255, 0, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255,
            255, 0, 0, 255, 255, 0, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255
        ];

        texture.WritePixels(basePixels);
        texture.GenerateMipmaps2DLinear();

        Assert.Equal(
            new byte[]
            {
                255, 0, 0, 255, 0, 255, 0, 255,
                0, 0, 255, 255, 255, 255, 255, 255
            },
            texture.ReadPixels(mipLevel: 1));
        var finalMip = texture.ReadPixels(mipLevel: 2);
        Assert.InRange(finalMip[0], (byte)127, (byte)128);
        Assert.InRange(finalMip[1], (byte)127, (byte)128);
        Assert.InRange(finalMip[2], (byte)127, (byte)128);
        Assert.Equal(255, finalMip[3]);
    }

    [Fact]
    public void GpuTextureWritePixelsAndReadPixelsUseNativeFormatStride()
    {
        using var window = new HeadlessWindow(2, 1);
        using var texture = new GpuTexture(
            window.Context,
            2,
            1,
            TextureFormat.Rgba32float,
            TextureUsage.CopyDst | TextureUsage.CopySrc,
            "Wide Format Texture");
        byte[] pixels =
        [
            0, 0, 128, 63, 0, 0, 0, 64, 0, 0, 64, 64, 0, 0, 128, 64,
            0, 0, 160, 64, 0, 0, 192, 64, 0, 0, 224, 64, 0, 0, 0, 65
        ];

        texture.WritePixels(pixels);

        Assert.Equal(pixels, texture.ReadPixels());
    }

    [Fact]
    public void DrawCallScissorPreservesNonEmptySubpixelClips()
    {
        var subpixel = InvokeTryComputeScissorRect(
            new Rect(0.2f, 0.2f, 0.4f, 0.4f),
            dpiScale: 1f,
            viewportX: 0,
            viewportY: 0,
            targetWidth: 16,
            targetHeight: 16);

        Assert.True(subpixel.Result);
        Assert.Equal(0u, subpixel.X);
        Assert.Equal(0u, subpixel.Y);
        Assert.Equal(1u, subpixel.Width);
        Assert.Equal(1u, subpixel.Height);

        var offsetAndScaled = InvokeTryComputeScissorRect(
            new Rect(3.2f, 4.25f, 0.4f, 0.4f),
            dpiScale: 2f,
            viewportX: 5,
            viewportY: 7,
            targetWidth: 32,
            targetHeight: 32);

        Assert.True(offsetAndScaled.Result);
        Assert.Equal(11u, offsetAndScaled.X);
        Assert.Equal(15u, offsetAndScaled.Y);
        Assert.Equal(2u, offsetAndScaled.Width);
        Assert.Equal(2u, offsetAndScaled.Height);

        Assert.False(InvokeTryComputeScissorRect(
            new Rect(20f, 0f, 1f, 1f),
            dpiScale: 1f,
            viewportX: 0,
            viewportY: 0,
            targetWidth: 16,
            targetHeight: 16).Result);
        Assert.False(InvokeTryComputeScissorRect(
            new Rect(1f, 1f, 0f, 4f),
            dpiScale: 1f,
            viewportX: 0,
            viewportY: 0,
            targetWidth: 16,
            targetHeight: 16).Result);
    }

    [Fact]
    public void RenderOffscreenBumpsTargetGenerationForWpfShaderEffectCache()
    {
        using var window = new HeadlessWindow(16, 16);
        using var target = new GpuTexture(
            window.Context,
            16,
            16,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Offscreen Generation Texture");
        var visual = new DrawingVisual();
        visual.Context.DrawRectangle(
            new SolidColorBrush(new Vector4(0.2f, 0.4f, 0.8f, 1f)),
            pen: null,
            new Rect(0f, 0f, 16f, 16f));
        var parameters = new WpfShaderEffectParams
        {
            Samplers = new[]
            {
                new WpfShaderEffectSampler(1, target, TextureSamplingMode.Nearest)
            }
        };
        var effect = new WpfShaderEffect(parameters);
        var initialGeneration = target.Generation;
        var initialCacheKey = GetRenderCacheKey(effect);

        window.Compositor.RenderOffscreen(
            visual,
            width: 16,
            height: 16,
            targetTexture: target,
            padding: 0f,
            dpiScale: 1f);

        Assert.True(target.Generation > initialGeneration);
        Assert.NotEqual(initialCacheKey, GetRenderCacheKey(effect));
    }

    [Fact]
    public void MaskTexturePoolSkipsDisposedTextures()
    {
        using var window = new HeadlessWindow(32, 32);
        var maskTexturePool = GetMaskTexturePool(window.Compositor);
        var disposedTexture = new GpuTexture(
            window.Context,
            32,
            32,
            TextureFormat.R8Unorm,
            TextureUsage.TextureBinding | TextureUsage.RenderAttachment | TextureUsage.CopyDst,
            "Disposed Mask Pool Entry");
        disposedTexture.Dispose();
        maskTexturePool.Add(disposedTexture);

        var texture = InvokeGetMaskTexture(window.Compositor, 32, 32);
        try
        {
            Assert.NotSame(disposedTexture, texture);
            Assert.False(texture.IsDisposed);
            Assert.DoesNotContain(maskTexturePool, candidate => candidate.Id == disposedTexture.Id);
        }
        finally
        {
            texture.Dispose();
        }
    }

    [Fact]
    public void OpacityMaskWritesComputedAlphaIntoMaskTarget()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(120, 40);
        window.Content = new OpacityMaskAlphaVisual();

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var blackMask = ReadPixel(pixels, window.Width, x: 16, y: 16);
            var blueMask = ReadPixel(pixels, window.Width, x: 56, y: 16);
            var halfOpacityMask = ReadPixel(pixels, window.Width, x: 96, y: 16);

            Assert.True(blackMask.R >= 220, $"Expected opaque black mask to preserve red draw, found {blackMask}.");
            Assert.True(blackMask.G <= 35, $"Expected opaque black mask green channel to stay low, found {blackMask}.");
            Assert.True(blackMask.B <= 35, $"Expected opaque black mask blue channel to stay low, found {blackMask}.");
            Assert.Equal(255, blackMask.A);

            Assert.True(blueMask.R >= 220, $"Expected opaque blue mask to preserve red draw, found {blueMask}.");
            Assert.True(blueMask.G <= 35, $"Expected opaque blue mask green channel to stay low, found {blueMask}.");
            Assert.True(blueMask.B <= 35, $"Expected opaque blue mask blue channel to stay low, found {blueMask}.");
            Assert.Equal(255, blueMask.A);

            Assert.InRange(halfOpacityMask.R, 110, 150);
            Assert.InRange(halfOpacityMask.G, 0, 16);
            Assert.InRange(halfOpacityMask.B, 0, 16);
            Assert.Equal(255, halfOpacityMask.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void PictureOpacityMaskPreservesTransparentGap()
    {
        var recorder = new GpuPictureRecorder();
        var maskContext = recorder.BeginRecording(new Rect(0f, 0f, 64f, 32f));
        var opaque = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f));
        maskContext.DrawRectangle(opaque, null, new Rect(0f, 0f, 24f, 32f));
        maskContext.DrawRectangle(opaque, null, new Rect(40f, 0f, 24f, 32f));
        using var picture = recorder.EndRecording();

        var window = HeadlessWindow.Shared;
        window.Resize(64, 32);
        window.Content = new PictureOpacityMaskVisual(picture);

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var left = ReadPixel(pixels, window.Width, x: 12, y: 16);
            var gap = ReadPixel(pixels, window.Width, x: 32, y: 16);
            var right = ReadPixel(pixels, window.Width, x: 52, y: 16);

            Assert.True(left.R >= 220 && left.G <= 35 && left.B <= 35, $"Expected red at left, found {left}.");
            Assert.True(gap.R <= 35 && gap.G <= 35 && gap.B <= 35, $"Expected transparent mask gap, found {gap}.");
            Assert.True(right.R >= 220 && right.G <= 35 && right.B <= 35, $"Expected red at right, found {right}.");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void AppendTranslatesOpacityMaskBounds()
    {
        var source = new DrawingContext();
        var maskBrush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f));
        source.PushOpacityMask(maskBrush, new Rect(2f, 3f, 10f, 11f));
        source.PopOpacityMask();

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        Assert.Equal(2, target.Commands.Count);
        Assert.Equal(RenderCommandType.PushOpacityMask, target.Commands[0].Type);
        Assert.Equal(new Rect(22f, 33f, 10f, 11f), target.Commands[0].Rect);
        Assert.Same(maskBrush, target.Commands[0].Brush);
        Assert.Equal(RenderCommandType.PopOpacityMask, target.Commands[1].Type);
    }

    [Fact]
    public void AppendTranslatesPictureOpacityMaskContent()
    {
        var recorder = new GpuPictureRecorder();
        var pictureContext = recorder.BeginRecording(new Rect(0f, 0f, 10f, 10f));
        pictureContext.DrawRectangle(
            new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            null,
            new Rect(0f, 0f, 10f, 10f));
        using var picture = recorder.EndRecording();

        var source = new DrawingContext();
        source.PushOpacityMask(picture, new Rect(2f, 3f, 10f, 11f));
        source.PopOpacityMask();

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        Assert.Equal(2, target.Commands.Count);
        Assert.Same(picture, target.Commands[0].Picture);
        Assert.Equal(20f, target.Commands[0].Transform.M41);
        Assert.Equal(30f, target.Commands[0].Transform.M42);
        Assert.Equal(RenderCommandType.PopOpacityMask, target.Commands[1].Type);
    }

    [Fact]
    public void AppendTranslatesRectangularClipBounds()
    {
        var source = new DrawingContext();
        source.PushClip(new Rect(2f, 3f, 10f, 11f));
        source.PopClip();

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        Assert.Equal(2, target.Commands.Count);
        Assert.Equal(RenderCommandType.PushClip, target.Commands[0].Type);
        Assert.Equal(new Rect(22f, 33f, 10f, 11f), target.Commands[0].Rect);
        Assert.Equal(RenderCommandType.PopClip, target.Commands[1].Type);
    }

    [Fact]
    public void AppendTranslatesGeometryClipTransform()
    {
        var source = new DrawingContext();
        var clip = PrimitivePathGeometry.CreateRectangle(2f, 3f, 10f, 11f);
        source.PushGeometryClip(clip);
        source.PopGeometryClip();

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        Assert.Equal(2, target.Commands.Count);
        Assert.Equal(RenderCommandType.PushGeometryClip, target.Commands[0].Type);
        Assert.Same(clip, target.Commands[0].Path);
        Assert.Equal(Matrix4x4.CreateTranslation(20f, 30f, 0f), target.Commands[0].Transform);
        Assert.Equal(RenderCommandType.PopGeometryClip, target.Commands[1].Type);
    }

    [Fact]
    public void AppendTranslatesIdentityDrawPathByComposingTransform()
    {
        var source = new DrawingContext();
        var path = PrimitivePathGeometry.CreateRectangle(2f, 3f, 10f, 11f);
        var brush = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f));
        var pen = new Pen(new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)), 2f);
        source.DrawPath(brush, pen, path);

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        Assert.Single(target.Commands);
        Assert.Equal(RenderCommandType.DrawPath, target.Commands[0].Type);
        Assert.Same(path, target.Commands[0].Path);
        Assert.Same(brush, target.Commands[0].Brush);
        Assert.Same(pen, target.Commands[0].Pen);
        Assert.Equal(Matrix4x4.CreateTranslation(20f, 30f, 0f), target.Commands[0].Transform);
    }

    [Fact]
    public void AppendComposesGeometryClipTransformBeforeTranslation()
    {
        var source = new DrawingContext();
        var clip = PrimitivePathGeometry.CreateRectangle(2f, 3f, 10f, 11f);
        var clipTransform = Matrix4x4.CreateScale(2f, 3f, 1f);
        source.PushGeometryClip(clip, clipTransform);
        source.PopGeometryClip();

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        Assert.Equal(2, target.Commands.Count);
        Assert.Equal(RenderCommandType.PushGeometryClip, target.Commands[0].Type);
        Assert.Same(clip, target.Commands[0].Path);
        Assert.Equal(clipTransform * Matrix4x4.CreateTranslation(20f, 30f, 0f), target.Commands[0].Transform);
        Assert.Equal(RenderCommandType.PopGeometryClip, target.Commands[1].Type);
    }

    [Fact]
    public void AppendComposesTransformedTextureRectAfterCommandTransform()
    {
        var source = new DrawingContext();
        var textureTransform = Matrix4x4.CreateScale(2f, 3f, 1f);
        source.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawTexture,
            Rect = new Rect(2f, 3f, 10f, 11f),
            Transform = textureTransform
        });

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        var command = Assert.Single(target.Commands);
        Assert.Equal(RenderCommandType.DrawTexture, command.Type);
        Assert.Equal(new Rect(2f, 3f, 10f, 11f), command.Rect);
        Assert.Equal(textureTransform * Matrix4x4.CreateTranslation(20f, 30f, 0f), command.Transform);
    }

    [Fact]
    public void AppendComposesTransformedExtensionRectAfterCommandTransform()
    {
        var source = new DrawingContext();
        var effectTransform = Matrix4x4.CreateScale(2f, 3f, 1f);
        var parameters = new ImageEffectParams
        {
            Texture = null!,
            Rect = new Rect(2f, 3f, 10f, 11f)
        };
        source.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawExtension,
            ExtensionId = CompositorBuiltInExtensions.ImageEffect,
            DataParam = parameters,
            Transform = effectTransform
        });

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        var command = Assert.Single(target.Commands);
        var appended = Assert.IsType<ImageEffectParams>(command.DataParam);
        Assert.Same(parameters, appended);
        Assert.Equal(new Rect(2f, 3f, 10f, 11f), appended.Rect);
        Assert.Equal(effectTransform * Matrix4x4.CreateTranslation(20f, 30f, 0f), command.Transform);
    }

    [Fact]
    public void AppendComposesTransformedPointCommandAfterCommandTransform()
    {
        var source = new DrawingContext();
        var lineTransform = Matrix4x4.CreateScale(2f, 3f, 1f);
        source.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawLine,
            Position = new Vector2(2f, 3f),
            Position2 = new Vector2(10f, 11f),
            Transform = lineTransform
        });

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        var command = Assert.Single(target.Commands);
        Assert.Equal(RenderCommandType.DrawLine, command.Type);
        Assert.Equal(new Vector2(2f, 3f), command.Position);
        Assert.Equal(new Vector2(10f, 11f), command.Position2);
        Assert.Equal(lineTransform * Matrix4x4.CreateTranslation(20f, 30f, 0f), command.Transform);
    }

    [Fact]
    public void AppendComposesTransformedPointBufferAfterCommandTransform()
    {
        var source = new DrawingContext();
        source.DrawPolyline(
            new Pen(new SolidColorBrush(0xFFFFFFFF), 1f),
            new[] { new Vector2(2f, 3f), new Vector2(10f, 11f) });
        var lineTransform = Matrix4x4.CreateScale(2f, 3f, 1f);
        var sourceCommand = source.Commands[0];
        sourceCommand.Transform = lineTransform;
        source.Commands[0] = sourceCommand;

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        var command = Assert.Single(target.Commands);
        Assert.Equal(RenderCommandType.DrawPolyline, command.Type);
        Assert.Equal(lineTransform * Matrix4x4.CreateTranslation(20f, 30f, 0f), command.Transform);
        Assert.Equal(new Vector2(2f, 3f), target.PointBuffer[command.PointBufferOffset]);
        Assert.Equal(new Vector2(10f, 11f), target.PointBuffer[command.PointBufferOffset + 1]);
    }

    [Fact]
    public void AppendTranslatesUntransformedPointBuffer()
    {
        var source = new DrawingContext();
        source.DrawPolyline(
            new Pen(new SolidColorBrush(0xFFFFFFFF), 1f),
            new[] { new Vector2(2f, 3f), new Vector2(10f, 11f) });

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        var command = Assert.Single(target.Commands);
        Assert.Equal(RenderCommandType.DrawPolyline, command.Type);
        Assert.Equal(new Vector2(22f, 33f), target.PointBuffer[command.PointBufferOffset]);
        Assert.Equal(new Vector2(30f, 41f), target.PointBuffer[command.PointBufferOffset + 1]);
    }

    [Fact]
    public void AppendTranslatesGpuSeriesUniformTranslate()
    {
        var lineBuffer = new object();
        var scatterBuffer = new object();
        var source = new DrawingContext();
        source.DrawGpuLineSeries(
            lineBuffer,
            2f,
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            new Vector2(2f, 3f),
            new Vector2(5f, 7f));
        source.DrawGpuScatterSeries(
            scatterBuffer,
            4f,
            new SolidColorBrush(new Vector4(0f, 1f, 0f, 1f)),
            new Vector2(3f, 4f),
            new Vector2(11f, 13f));

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        Assert.Equal(2, target.Commands.Count);
        Assert.Equal(RenderCommandType.DrawExtension, target.Commands[0].Type);
        Assert.Equal(CompositorBuiltInExtensions.GpuLineSeries, target.Commands[0].ExtensionId);
        Assert.Same(lineBuffer, target.Commands[0].StaticBuffer);
        Assert.Equal(new Vector2(25f, 37f), target.Commands[0].Translate);
        Assert.Equal(new Vector2(2f, 3f), target.Commands[0].Scale);

        Assert.Equal(RenderCommandType.DrawExtension, target.Commands[1].Type);
        Assert.Equal(CompositorBuiltInExtensions.GpuScatterSeries, target.Commands[1].ExtensionId);
        Assert.Same(scatterBuffer, target.Commands[1].StaticBuffer);
        Assert.Equal(new Vector2(31f, 43f), target.Commands[1].Translate);
        Assert.Equal(new Vector2(3f, 4f), target.Commands[1].Scale);
    }

    [Fact]
    public void AppendTranslatesImageEffectPayloadRect()
    {
        var source = new DrawingContext();
        var parameters = new ImageEffectParams
        {
            Texture = null!,
            Rect = new Rect(2f, 3f, 10f, 11f),
            Brightness = 0.25f,
            Contrast = 1.25f,
            Saturation = 0.75f,
            Grayscale = 0.5f,
            Sepia = 0.2f,
            Invert = 1f,
            BlurSigma = 4f,
            LastError = "preserve"
        };
        source.DrawExtension(CompositorBuiltInExtensions.ImageEffect, dataParam: parameters);

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        var appended = Assert.IsType<ImageEffectParams>(Assert.Single(target.Commands).DataParam);
        Assert.NotSame(parameters, appended);
        Assert.Equal(new Rect(22f, 33f, 10f, 11f), appended.Rect);
        Assert.Equal(new Rect(2f, 3f, 10f, 11f), parameters.Rect);
        Assert.Equal(parameters.Brightness, appended.Brightness);
        Assert.Equal(parameters.Contrast, appended.Contrast);
        Assert.Equal(parameters.Saturation, appended.Saturation);
        Assert.Equal(parameters.Grayscale, appended.Grayscale);
        Assert.Equal(parameters.Sepia, appended.Sepia);
        Assert.Equal(parameters.Invert, appended.Invert);
        Assert.Equal(parameters.BlurSigma, appended.BlurSigma);
        Assert.Equal(parameters.LastError, appended.LastError);
    }

    [Fact]
    public void AppendTranslatesWpfShaderEffectPayloadRect()
    {
        var source = new DrawingContext();
        var constants = new[] { 1f, 2f, 3f, 4f };
        var samplers = new[]
        {
            new WpfShaderEffectSampler(1, null, TextureSamplingMode.Nearest)
        };
        var parameters = new WpfShaderEffectParams
        {
            Texture = null,
            Rect = new Rect(2f, 3f, 10f, 11f),
            ShaderSource = "fn custom() -> f32 { return 1.0; }",
            ShaderKey = "effect-key",
            Constants = constants,
            Samplers = samplers,
            SamplingMode = TextureSamplingMode.Nearest,
            IsFailed = true,
            LastError = "preserve",
            SourceTextureRegisterIndex = 1
        };
        source.DrawExtension(CompositorBuiltInExtensions.WpfShaderEffect, dataParam: parameters);

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        var appended = Assert.IsType<WpfShaderEffectParams>(Assert.Single(target.Commands).DataParam);
        Assert.NotSame(parameters, appended);
        Assert.Equal(new Rect(22f, 33f, 10f, 11f), appended.Rect);
        Assert.Equal(new Rect(2f, 3f, 10f, 11f), parameters.Rect);
        Assert.Equal(parameters.ShaderSource, appended.ShaderSource);
        Assert.Equal(parameters.ShaderKey, appended.ShaderKey);
        Assert.Same(constants, appended.Constants);
        Assert.Same(samplers, appended.Samplers);
        Assert.Equal(parameters.SamplingMode, appended.SamplingMode);
        Assert.Equal(parameters.IsFailed, appended.IsFailed);
        Assert.Equal(parameters.LastError, appended.LastError);
        Assert.Equal(parameters.SourceTextureRegisterIndex, appended.SourceTextureRegisterIndex);
    }

    [Fact]
    public void AppendTranslatesShaderToyPayloadRect()
    {
        var source = new DrawingContext();
        var parameters = new ShaderToyParams
        {
            Rect = new Rect(2f, 3f, 10f, 11f),
            ShaderSource = SolidShaderToySource,
            ShaderKey = "toy-key",
            OldShaderKey = "old-toy-key",
            IsFailed = true,
            Resolution = new Vector3(10f, 11f, 1f),
            Time = 3f,
            TimeDelta = 0.5f,
            Frame = 12f,
            FrameRate = 60f,
            Mouse = new Vector4(1f, 2f, 3f, 4f),
            Date = new Vector4(2026f, 6f, 24f, 12f)
        };
        source.DrawExtension(CompositorBuiltInExtensions.ShaderToy, dataParam: parameters);

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        var appended = Assert.IsType<ShaderToyParams>(Assert.Single(target.Commands).DataParam);
        Assert.NotSame(parameters, appended);
        Assert.Equal(new Rect(22f, 33f, 10f, 11f), appended.Rect);
        Assert.Equal(new Rect(2f, 3f, 10f, 11f), parameters.Rect);
        Assert.Equal(parameters.ShaderSource, appended.ShaderSource);
        Assert.Equal(parameters.ShaderKey, appended.ShaderKey);
        Assert.Equal(parameters.OldShaderKey, appended.OldShaderKey);
        Assert.Equal(parameters.IsFailed, appended.IsFailed);
        Assert.Equal(parameters.Resolution, appended.Resolution);
        Assert.Equal(parameters.Time, appended.Time);
        Assert.Equal(parameters.TimeDelta, appended.TimeDelta);
        Assert.Equal(parameters.Frame, appended.Frame);
        Assert.Equal(parameters.FrameRate, appended.FrameRate);
        Assert.Equal(parameters.Mouse, appended.Mouse);
        Assert.Equal(parameters.Date, appended.Date);
    }

    [Fact]
    public void OpacityMaskCompilationDoesNotInheritActiveBlendMode()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(96, 48);
        window.Content = new OpacityMaskUnderClearBlendVisual();

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var maskedContent = ReadPixel(pixels, window.Width, x: 24, y: 24);

            Assert.True(maskedContent.R >= 220, $"Expected opacity-masked content to remain red, found {maskedContent}.");
            Assert.True(maskedContent.G <= 35, $"Expected opacity-masked content green channel to stay low, found {maskedContent}.");
            Assert.True(maskedContent.B <= 35, $"Expected opacity-masked content blue channel to stay low, found {maskedContent}.");
            Assert.Equal(255, maskedContent.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void CachedTextureBindGroupsAreQueuedWhenSourceTextureIsDisposed()
    {
        using var window = new HeadlessWindow(16, 16);
        using var texture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Texture BindGroup Disposal Queue Test");
        texture.WritePixels<byte>(new byte[] { 255, 0, 0, 255 });
        window.Content = new TextureCacheVisual(texture);

        window.Render();

        var textureId = texture.Id;
        var textureBindGroups = GetPersistentTextureBindGroups(window.Compositor);
        Assert.Contains(textureBindGroups.Keys, key => key.TextureId == textureId);
        Assert.Empty(window.Context.PendingBindGroups);

        texture.Dispose();
        window.Content = null;

        Assert.DoesNotContain(textureBindGroups.Keys, key => key.TextureId == textureId);
        lock (window.Context.DisposalLock)
        {
            Assert.Contains(window.Context.PendingBindGroups, ptr => ptr != IntPtr.Zero);
        }

        window.Context.CleanupPendingResources();
    }

    [Fact]
    public void GpuSeriesBufferReleaseBindGroupsQueuesCachedChartBindGroups()
    {
        using var window = new HeadlessWindow(16, 16);
        var previous = WgpuContext.Current;
        WgpuContext.Current = window.Context;

        using var seriesBuffer = new GpuSeriesBuffer();
        var lineBindGroup = (nint)0x1010;
        var scatterBindGroup = (nint)0x2020;
        var lineOffscreenBindGroup = (nint)0x3030;
        var scatterOffscreenBindGroup = (nint)0x4040;

        try
        {
            seriesBuffer.Upload(Array.Empty<float>(), pointsCount: 0);
            seriesBuffer.LineBindGroup = lineBindGroup;
            seriesBuffer.ScatterBindGroup = scatterBindGroup;
            seriesBuffer.LineBindGroupOffscreen = lineOffscreenBindGroup;
            seriesBuffer.ScatterBindGroupOffscreen = scatterOffscreenBindGroup;

            Assert.Empty(window.Context.PendingBindGroups);

            seriesBuffer.ReleaseBindGroups();

            Assert.Equal(0, seriesBuffer.LineBindGroup);
            Assert.Equal(0, seriesBuffer.ScatterBindGroup);
            Assert.Equal(0, seriesBuffer.LineBindGroupOffscreen);
            Assert.Equal(0, seriesBuffer.ScatterBindGroupOffscreen);

            lock (window.Context.DisposalLock)
            {
                Assert.Contains((IntPtr)lineBindGroup, window.Context.PendingBindGroups);
                Assert.Contains((IntPtr)scatterBindGroup, window.Context.PendingBindGroups);
                Assert.Contains((IntPtr)lineOffscreenBindGroup, window.Context.PendingBindGroups);
                Assert.Contains((IntPtr)scatterOffscreenBindGroup, window.Context.PendingBindGroups);
            }
        }
        finally
        {
            lock (window.Context.DisposalLock)
            {
                window.Context.PendingBindGroups.RemoveAll(ptr =>
                    ptr == (IntPtr)lineBindGroup ||
                    ptr == (IntPtr)scatterBindGroup ||
                    ptr == (IntPtr)lineOffscreenBindGroup ||
                    ptr == (IntPtr)scatterOffscreenBindGroup);
            }
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void ImageEffectPipelineDisposeQueuesGpuHandles()
    {
        using var window = new HeadlessWindow(16, 16);
        using var source = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Image Effect Disposal Queue Source");
        source.WritePixels(new byte[] { 255, 0, 0, 255 });
        window.Content = new ImageEffectCacheVisual(source);

        try
        {
            window.Render();

            var extension = Assert.IsAssignableFrom<IDisposable>(
                window.Compositor.GetExtension(CompositorBuiltInExtensions.ImageEffect));

            lock (window.Context.DisposalLock)
            {
                Assert.Empty(window.Context.PendingBindGroups);
            }

            extension.Dispose();

            lock (window.Context.DisposalLock)
            {
                Assert.NotEmpty(window.Context.PendingBindGroups);
                Assert.NotEmpty(window.Context.PendingBindGroupLayouts);
                Assert.NotEmpty(window.Context.PendingPipelineLayouts);
            }

            window.Context.CleanupPendingResources();
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void ShaderToyPipelineDisposeQueuesGpuHandles()
    {
        using var window = new HeadlessWindow(16, 16);
        var shader = new ShaderToyParams
        {
            Rect = new Rect(0f, 0f, 16f, 16f),
            ShaderKey = $"review_shadertoy_disposal_{System.Guid.NewGuid():N}",
            ShaderSource = SolidShaderToySource,
            Resolution = new Vector3(16f, 16f, 1f),
            Time = 0f,
            TimeDelta = 0f,
            Frame = 0f,
            FrameRate = 60f,
            Mouse = Vector4.Zero,
            Date = Vector4.Zero
        };
        window.Content = new ShaderToyDisposalVisual(shader);

        try
        {
            window.Render();

            Assert.False(shader.IsFailed);
            var extension = Assert.IsAssignableFrom<IDisposable>(
                window.Compositor.GetExtension(CompositorBuiltInExtensions.ShaderToy));

            lock (window.Context.DisposalLock)
            {
                Assert.Empty(window.Context.PendingBindGroups);
            }

            extension.Dispose();

            lock (window.Context.DisposalLock)
            {
                Assert.NotEmpty(window.Context.PendingBindGroups);
                Assert.NotEmpty(window.Context.PendingBindGroupLayouts);
                Assert.NotEmpty(window.Context.PendingPipelineLayouts);
            }

            window.Context.CleanupPendingResources();
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void WpfShaderEffectPipelineDisposeQueuesGpuHandles()
    {
        using var window = new HeadlessWindow(16, 16);
        using var source = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "WPF Shader Effect Disposal Queue Source");
        source.WritePixels(new byte[] { 255, 0, 0, 255 });
        var effect = new WpfShaderEffectParams
        {
            Texture = source,
            Rect = new Rect(0f, 0f, 16f, 16f),
            ShaderKey = $"review_wpf_shader_effect_disposal_{System.Guid.NewGuid():N}"
        };
        window.Content = new WpfShaderEffectDisposalVisual(effect);

        try
        {
            window.Render();

            Assert.False(effect.IsFailed, effect.LastError);
            var extension = Assert.IsAssignableFrom<IDisposable>(
                window.Compositor.GetExtension(CompositorBuiltInExtensions.WpfShaderEffect));

            lock (window.Context.DisposalLock)
            {
                Assert.Empty(window.Context.PendingBindGroups);
            }

            extension.Dispose();

            lock (window.Context.DisposalLock)
            {
                Assert.NotEmpty(window.Context.PendingBindGroups);
                Assert.NotEmpty(window.Context.PendingBindGroupLayouts);
                Assert.NotEmpty(window.Context.PendingPipelineLayouts);
            }

            window.Context.CleanupPendingResources();
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public unsafe void GpuBufferDisposeQueuesNativeBufferDisposal()
    {
        using var window = new HeadlessWindow(16, 16);
        using var buffer = new GpuBuffer(
            window.Context,
            16,
            BufferUsage.Vertex | BufferUsage.CopyDst,
            "Explicit Buffer Disposal Queue Test");
        var bufferPtr = (IntPtr)buffer.BufferPtr;

        Assert.NotEqual(IntPtr.Zero, bufferPtr);
        Assert.Empty(window.Context.PendingBuffers);

        buffer.Dispose();

        Assert.True(buffer.BufferPtr == null);
        lock (window.Context.DisposalLock)
        {
            Assert.Contains(bufferPtr, window.Context.PendingBuffers);
        }

        window.Context.CleanupPendingResources();
    }

    private static void AssertMixedColorGlyphDrawCalls(Compositor compositor)
    {
        Compositor.CompositorDrawCall[] drawCalls = GetDrawCalls(compositor);
        Assert.Contains(drawCalls, drawCall => drawCall.Type == Compositor.DrawCallType.Vector);

        Compositor.CompositorDrawCall textDraw = Assert.Single(
            drawCalls,
            drawCall => drawCall.Type == Compositor.DrawCallType.Text && drawCall.IndexCount > 0);
        Assert.Equal(1u, textDraw.IndexCount);
    }

    private static Compositor.CompositorDrawCall[] GetDrawCalls(Compositor compositor)
    {
        var field = typeof(Compositor).GetField("_drawCalls", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var drawCalls = Assert.IsAssignableFrom<IEnumerable<Compositor.CompositorDrawCall>>(field.GetValue(compositor));
        return drawCalls.ToArray();
    }

    private static Compositor CreateUninitializedCompositorForExtensionCompile()
    {
        var compositor = (Compositor)RuntimeHelpers.GetUninitializedObject(typeof(Compositor));
        SetCompositorField(compositor, "_vectorVerticesList", new List<VectorVertex>());
        SetCompositorField(compositor, "_vectorIndicesList", new List<uint>());
        SetCompositorField(compositor, "_activeBrushes", new List<GpuBrush>());
        SetCompositorField(compositor, "_activeGradientStops", new List<GpuGradientStop>());
        SetCompositorField(compositor, "_activeOpacity", 1.0f);
        return compositor;
    }

    private static Compositor GetGdiBitmapCompositor(GdiBitmap bitmap)
    {
        var providerType = typeof(GdiBitmap).Assembly.GetType("System.Drawing.GpuProvider", throwOnError: true)!;
        var method = providerType.GetMethod(
            "GetCompositor",
            BindingFlags.Static | BindingFlags.Public)
            ?? throw new MissingMethodException(providerType.FullName, "GetCompositor");
        return (Compositor)method.Invoke(null, [bitmap.GpuTexture.Context])!;
    }

    private static Compositor GetSkiaSurfaceCompositor(WgpuContext context)
    {
        var method = typeof(SKSurface).GetMethod(
            "GetCompositorForContext",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (Compositor)method.Invoke(null, [context, TextureFormat.Rgba8Unorm])!;
    }

    private static Compositor GetWpfCompositor()
    {
        var providerType = typeof(WpfDrawingContext).Assembly.GetType(
            "System.Windows.Media.GpuProvider",
            throwOnError: true)!;
        var property = providerType.GetProperty(
            "Compositor",
            BindingFlags.Static | BindingFlags.Public)
            ?? throw new MissingMemberException(providerType.FullName, "Compositor");
        return (Compositor)property.GetValue(null)!;
    }

    private static void InvokeGdiBitmapDispose(GdiBitmap bitmap, bool disposing)
    {
        var method = typeof(GdiBitmap).GetMethod(
            "Dispose",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(bool)],
            modifiers: null)
            ?? throw new MissingMethodException(typeof(GdiBitmap).FullName, "Dispose(bool)");
        method.Invoke(bitmap, [disposing]);
    }

    private static T GetCompositorField<T>(Compositor compositor, string fieldName)
    {
        var field = typeof(Compositor).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(compositor));
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(target));
    }

    private static object? GetRawCompositorField(Compositor compositor, string fieldName)
    {
        var field = typeof(Compositor).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(compositor);
    }

    private static void SetCompositorField(Compositor compositor, string fieldName, object? value)
    {
        var field = typeof(Compositor).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(compositor, value);
    }

    private static (bool Result, uint X, uint Y, uint Width, uint Height) InvokeTryComputeScissorRect(
        Rect rect,
        float dpiScale,
        uint viewportX,
        uint viewportY,
        uint targetWidth,
        uint targetHeight)
    {
        var method = typeof(Compositor).GetMethod(
            "TryComputeScissorRect",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(Compositor).FullName, "TryComputeScissorRect");
        object?[] args =
        [
            rect,
            dpiScale,
            viewportX,
            viewportY,
            targetWidth,
            targetHeight,
            0u,
            0u,
            0u,
            0u
        ];
        bool result = (bool)method.Invoke(null, args)!;
        return (result, (uint)args[6]!, (uint)args[7]!, (uint)args[8]!, (uint)args[9]!);
    }

    private static IList GetPathAtlasTempBuffers(Compositor compositor)
    {
        var pathAtlasField = typeof(Compositor).GetField("_pathAtlas", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pathAtlasField);
        var pathAtlas = pathAtlasField.GetValue(compositor);
        Assert.NotNull(pathAtlas);

        var tempBuffersField = pathAtlas.GetType().GetField("_tempBuffers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(tempBuffersField);
        return Assert.IsAssignableFrom<IList>(tempBuffersField.GetValue(pathAtlas));
    }

    private static uint GetPathAtlasFrameNumber(Compositor compositor)
    {
        var pathAtlasField = typeof(Compositor).GetField("_pathAtlas", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pathAtlasField);
        var pathAtlas = pathAtlasField.GetValue(compositor);
        Assert.NotNull(pathAtlas);

        var frameNumberField = pathAtlas.GetType().GetField("_frameNumber", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(frameNumberField);
        return Assert.IsType<uint>(frameNumberField.GetValue(pathAtlas));
    }

    private static Dictionary<Compositor.TextureCacheKey, Compositor.CachedBindGroup> GetPersistentTextureBindGroups(Compositor compositor)
    {
        var field = typeof(Compositor).GetField("_persistentTextureBindGroups", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<Dictionary<Compositor.TextureCacheKey, Compositor.CachedBindGroup>>(field.GetValue(compositor));
    }

    private static List<GpuTexture> GetMaskTexturePool(Compositor compositor)
    {
        var field = typeof(Compositor).GetField("_maskTexturePool", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<List<GpuTexture>>(field.GetValue(compositor));
    }

    private static GpuTexture InvokeGetMaskTexture(Compositor compositor, uint width, uint height)
    {
        var method = typeof(Compositor).GetMethod("GetMaskTexture", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<GpuTexture>(method.Invoke(compositor, [width, height]));
    }

    private static GpuTexture CreateSolidTexture(WgpuContext context, byte[] rgba, string label)
    {
        var texture = new GpuTexture(
            context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            label);
        texture.WritePixels(rgba);
        return texture;
    }

    private static int GetRenderCacheKey(WpfShaderEffect effect)
    {
        var method = typeof(WpfShaderEffect).GetMethod(
            "GetRenderCacheKey",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (int)method.Invoke(effect, null)!;
    }

    private static RgbaPixel ReadPixel(byte[] pixels, uint width, int x, int y)
    {
        var index = ((y * (int)width) + x) * 4;
        return new RgbaPixel(
            pixels[index + 0],
            pixels[index + 1],
            pixels[index + 2],
            pixels[index + 3]);
    }

    private static byte[] BuildColorLayerFont()
    {
        byte[][] glyphs =
        {
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            BuildRectangleGlyph(0, 0, 500, 500),
            BuildRectangleGlyph(120, 120, 620, 620),
        };

        byte[] glyf = BuildGlyfTable(glyphs, out uint[] glyphOffsets);
        return BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable(glyphs.Length)),
            ("maxp", BuildMaxpTable(glyphs.Length)),
            ("hmtx", BuildHmtxTable(glyphs.Length)),
            ("cmap", BuildCmapFormat12Table()),
            ("loca", BuildLongLoca(glyphOffsets)),
            ("glyf", glyf),
            ("COLR", BuildColrTable()),
            ("CPAL", BuildCpalTable()));
    }

    private static byte[] BuildMissingGlyphOutlineFont()
    {
        byte[][] glyphs =
        {
            BuildRectangleGlyph(0, 0, 500, 500),
            BuildRectangleGlyph(100, 100, 500, 500),
        };

        byte[] glyf = BuildGlyfTable(glyphs, out uint[] glyphOffsets);
        return BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable(glyphs.Length)),
            ("maxp", BuildMaxpTable(glyphs.Length)),
            ("hmtx", BuildHmtxTable(glyphs.Length)),
            ("cmap", BuildSingleMappedGlyphCmapFormat12Table()),
            ("loca", BuildLongLoca(glyphOffsets)),
            ("glyf", glyf));
    }

    private static byte[] BuildSingleMappedGlyphCmapFormat12Table()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 1);
        WriteUShort(writer, 3);
        WriteUShort(writer, 10);
        WriteUInt(writer, 12);
        WriteUShort(writer, 12);
        WriteUShort(writer, 0);
        WriteUInt(writer, 28);
        WriteUInt(writer, 0);
        WriteUInt(writer, 1);
        WriteUInt(writer, (uint)'A');
        WriteUInt(writer, (uint)'A');
        WriteUInt(writer, 1);
        return stream.ToArray();
    }

    private sealed class TextureCacheVisual : FrameworkElement
    {
        private readonly GpuTexture _texture;

        public TextureCacheVisual(GpuTexture texture)
        {
            _texture = texture;
            Width = 16f;
            Height = 16f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawTexture(_texture, new Rect(0f, 0f, 16f, 16f));
        }
    }

    private sealed class ImageEffectCacheVisual : FrameworkElement
    {
        private readonly GpuTexture _texture;

        public ImageEffectCacheVisual(GpuTexture texture)
        {
            _texture = texture;
            Width = 16f;
            Height = 16f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawImageWithEffect(
                _texture,
                new Rect(0f, 0f, 16f, 16f),
                blurSigma: 1f);
        }
    }

    private sealed class ImageEffectParamsVisual : FrameworkElement
    {
        private readonly ImageEffectParams _parameters;

        public ImageEffectParamsVisual(ImageEffectParams parameters)
        {
            _parameters = parameters;
            Width = parameters.Rect.Width;
            Height = parameters.Rect.Height;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawExtension(
                CompositorBuiltInExtensions.ImageEffect,
                dataParam: _parameters);
        }
    }

    private static byte[] BuildRectangleGlyph(short xMin, short yMin, short xMax, short yMax)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteShort(writer, 1);
        WriteShort(writer, xMin);
        WriteShort(writer, yMin);
        WriteShort(writer, xMax);
        WriteShort(writer, yMax);
        WriteUShort(writer, 3);
        WriteUShort(writer, 0);
        writer.Write(new byte[] { 1, 1, 1, 1 });
        WriteShort(writer, xMin);
        WriteShort(writer, (short)(xMax - xMin));
        WriteShort(writer, 0);
        WriteShort(writer, (short)(xMin - xMax));
        WriteShort(writer, yMin);
        WriteShort(writer, 0);
        WriteShort(writer, (short)(yMax - yMin));
        WriteShort(writer, 0);
        return stream.ToArray();
    }

    private static byte[] BuildGlyfTable(byte[][] glyphs, out uint[] glyphOffsets)
    {
        glyphOffsets = new uint[glyphs.Length + 1];
        using var stream = new MemoryStream();

        for (int i = 0; i < glyphs.Length; i++)
        {
            glyphOffsets[i] = checked((uint)stream.Position);
            stream.Write(glyphs[i]);
            WritePadding(stream);
        }

        glyphOffsets[^1] = checked((uint)stream.Position);
        return stream.ToArray();
    }

    private static byte[] BuildHeadTable()
    {
        byte[] table = new byte[54];
        using var stream = new MemoryStream(table);
        using var writer = new BinaryWriter(stream);

        WriteUInt(writer, 0x00010000);
        WriteUInt(writer, 0x00010000);
        stream.Position = 18;
        WriteUShort(writer, 1000);
        stream.Position = 50;
        WriteShort(writer, 1);
        return table;
    }

    private static byte[] BuildHheaTable(int glyphCount)
    {
        byte[] table = new byte[36];
        using var stream = new MemoryStream(table);
        using var writer = new BinaryWriter(stream);

        stream.Position = 4;
        WriteShort(writer, 800);
        WriteShort(writer, -200);
        WriteShort(writer, 0);
        stream.Position = 34;
        WriteUShort(writer, checked((ushort)glyphCount));
        return table;
    }

    private static byte[] BuildMaxpTable(int glyphCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUInt(writer, 0x00010000);
        WriteUShort(writer, checked((ushort)glyphCount));
        return stream.ToArray();
    }

    private static byte[] BuildHmtxTable(int glyphCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        for (int i = 0; i < glyphCount; i++)
        {
            WriteUShort(writer, 600);
            WriteShort(writer, 0);
        }

        return stream.ToArray();
    }

    private static byte[] BuildCmapFormat12Table()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 1);
        WriteUShort(writer, 3);
        WriteUShort(writer, 10);
        WriteUInt(writer, 12);
        WriteUShort(writer, 12);
        WriteUShort(writer, 0);
        WriteUInt(writer, 28);
        WriteUInt(writer, 0);
        WriteUInt(writer, 1);
        WriteUInt(writer, (uint)'A');
        WriteUInt(writer, (uint)'B');
        WriteUInt(writer, 1);
        return stream.ToArray();
    }

    private static byte[] BuildLongLoca(uint[] glyphOffsets)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        foreach (uint offset in glyphOffsets)
        {
            WriteUInt(writer, offset);
        }

        return stream.ToArray();
    }

    private static byte[] BuildColrTable()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 1);
        WriteUInt(writer, 14);
        WriteUInt(writer, 20);
        WriteUShort(writer, 2);
        WriteUShort(writer, 1);
        WriteUShort(writer, 0);
        WriteUShort(writer, 2);
        WriteUShort(writer, 2);
        WriteUShort(writer, 0);
        WriteUShort(writer, 3);
        WriteUShort(writer, 1);
        return stream.ToArray();
    }

    private static byte[] BuildCpalTable()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 2);
        WriteUShort(writer, 1);
        WriteUShort(writer, 2);
        WriteUInt(writer, 14);
        WriteUShort(writer, 0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)255);
        writer.Write((byte)255);
        writer.Write((byte)255);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)255);
        return stream.ToArray();
    }

    private static byte[] BuildSfntWithTables(params (string Tag, byte[] Data)[] tables)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUInt(writer, 0x00010000);
        WriteUShort(writer, checked((ushort)tables.Length));
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);

        uint tableOffset = (uint)(12 + tables.Length * 16);
        foreach ((string tag, byte[] data) in tables)
        {
            WriteTag(writer, tag);
            WriteUInt(writer, 0);
            WriteUInt(writer, tableOffset);
            WriteUInt(writer, (uint)data.Length);
            tableOffset += (uint)data.Length;
        }

        foreach ((_, byte[] data) in tables)
        {
            writer.Write(data);
        }

        return stream.ToArray();
    }

    private static void WritePadding(Stream stream)
    {
        while ((stream.Position & 3) != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static void WriteTag(BinaryWriter writer, string tag)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(tag);
        Assert.Equal(4, bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteShort(BinaryWriter writer, short value)
    {
        WriteUShort(writer, unchecked((ushort)value));
    }

    private static void WriteUShort(BinaryWriter writer, ushort value)
    {
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private static void WriteUInt(BinaryWriter writer, uint value)
    {
        writer.Write((byte)(value >> 24));
        writer.Write((byte)(value >> 16));
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private readonly record struct RgbaPixel(byte R, byte G, byte B, byte A);

    private sealed class CountingExtension : ICompositorExtension
    {
        public int BeginFrameCount { get; private set; }

        public int EndFrameCount { get; private set; }

        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
        }

        public void BeginFrame(Compositor compositor)
        {
            BeginFrameCount++;
        }

        public void EndFrame(Compositor compositor)
        {
            EndFrameCount++;
        }
    }

    private sealed class ThrowingCompileExtension(string message) : ICompositorExtension
    {
        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
            throw new System.InvalidOperationException(message);
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
        }
    }

    private sealed class CanvasSizeRecordingExtension : ICompositorExtension
    {
        private static readonly PropertyInfo s_canvasPixelXProperty =
            typeof(Compositor).GetProperty("CurrentCanvasPixelX", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(Compositor).FullName, "CurrentCanvasPixelX");

        private static readonly PropertyInfo s_canvasPixelYProperty =
            typeof(Compositor).GetProperty("CurrentCanvasPixelY", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(Compositor).FullName, "CurrentCanvasPixelY");

        private static readonly PropertyInfo s_canvasPixelWidthProperty =
            typeof(Compositor).GetProperty("CurrentCanvasPixelWidth", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(Compositor).FullName, "CurrentCanvasPixelWidth");

        private static readonly PropertyInfo s_canvasPixelHeightProperty =
            typeof(Compositor).GetProperty("CurrentCanvasPixelHeight", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(Compositor).FullName, "CurrentCanvasPixelHeight");

        public int RenderCount { get; private set; }

        public float CanvasPixelX { get; private set; }

        public float CanvasPixelY { get; private set; }

        public float CanvasPixelWidth { get; private set; }

        public float CanvasPixelHeight { get; private set; }

        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
            RenderCount++;
            CanvasPixelX = (float)s_canvasPixelXProperty.GetValue(compositor)!;
            CanvasPixelY = (float)s_canvasPixelYProperty.GetValue(compositor)!;
            CanvasPixelWidth = (float)s_canvasPixelWidthProperty.GetValue(compositor)!;
            CanvasPixelHeight = (float)s_canvasPixelHeightProperty.GetValue(compositor)!;
        }
    }

    private sealed class ThrowingVisual : FrameworkElement
    {
        public ThrowingVisual()
        {
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            throw new InvalidOperationException("Synthetic render failure.");
        }
    }

    private sealed class OpacityMaskedVisual : FrameworkElement
    {
        public OpacityMaskedVisual()
        {
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                new Rect(0f, 0f, 32f, 32f));
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                pen: null,
                new Rect(0f, 0f, 32f, 32f));
            context.PopOpacityMask();
        }
    }

    private sealed class OffsetViewportOpacityMaskVisual : FrameworkElement
    {
        public OffsetViewportOpacityMaskVisual()
        {
            Width = 8f;
            Height = 8f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                new Rect(0f, 0f, 2f, 8f));
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                pen: null,
                new Rect(0f, 0f, 8f, 8f));
            context.PopOpacityMask();
        }
    }

    private sealed class OffsetViewportImageEffectMaskVisual : FrameworkElement
    {
        private readonly GpuTexture _source;

        public OffsetViewportImageEffectMaskVisual(GpuTexture source)
        {
            _source = source;
            Width = 8f;
            Height = 8f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                new Rect(0f, 0f, 2f, 8f));
            context.DrawExtension(
                CompositorBuiltInExtensions.ImageEffect,
                dataParam: new ImageEffectParams
                {
                    Texture = _source,
                    Rect = new Rect(0f, 0f, 8f, 8f)
                });
            context.PopOpacityMask();
        }
    }

    private sealed class OffsetViewportWpfShaderEffectMaskVisual : FrameworkElement
    {
        private readonly GpuTexture _source;

        public OffsetViewportWpfShaderEffectMaskVisual(GpuTexture source)
        {
            _source = source;
            Width = 8f;
            Height = 8f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                new Rect(0f, 0f, 2f, 8f));
            context.DrawWpfShaderEffect(new WpfShaderEffectParams
            {
                Texture = _source,
                Rect = new Rect(0f, 0f, 8f, 8f),
                ShaderKey = "offset_viewport_masked_wpf_shader_effect",
                SamplingMode = TextureSamplingMode.Nearest
            });
            context.PopOpacityMask();
        }
    }

    private sealed class OpacityMaskAlphaVisual : FrameworkElement
    {
        private readonly SolidColorBrush _background = new(new Vector4(0f, 0f, 0f, 1f));
        private readonly SolidColorBrush _red = new(new Vector4(1f, 0f, 0f, 1f));

        public OpacityMaskAlphaVisual()
        {
            Width = 120f;
            Height = 40f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_background, null, new Rect(0f, 0f, 120f, 40f));

            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                new Rect(0f, 0f, 32f, 32f));
            context.DrawRectangle(_red, null, new Rect(0f, 0f, 32f, 32f));
            context.PopOpacityMask();

            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
                new Rect(40f, 0f, 32f, 32f));
            context.DrawRectangle(_red, null, new Rect(40f, 0f, 32f, 32f));
            context.PopOpacityMask();

            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)) { Opacity = 0.5f },
                new Rect(80f, 0f, 32f, 32f));
            context.DrawRectangle(_red, null, new Rect(80f, 0f, 32f, 32f));
            context.PopOpacityMask();
        }
    }

    private sealed class PictureOpacityMaskVisual : FrameworkElement
    {
        private readonly GpuPicture _picture;
        private readonly SolidColorBrush _background = new(new Vector4(0f, 0f, 0f, 1f));
        private readonly SolidColorBrush _red = new(new Vector4(1f, 0f, 0f, 1f));

        public PictureOpacityMaskVisual(GpuPicture picture)
        {
            _picture = picture;
            Width = 64f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            var bounds = new Rect(0f, 0f, 64f, 32f);
            context.DrawRectangle(_background, null, bounds);
            context.PushOpacityMask(_picture, bounds);
            context.DrawRectangle(_red, null, bounds);
            context.PopOpacityMask();
        }
    }

    private sealed class OpacityMaskUnderClearBlendVisual : FrameworkElement
    {
        private readonly SolidColorBrush _background = new(new Vector4(0f, 0f, 0f, 1f));
        private readonly SolidColorBrush _mask = new(new Vector4(1f, 1f, 1f, 1f));
        private readonly SolidColorBrush _red = new(new Vector4(1f, 0f, 0f, 1f));

        public OpacityMaskUnderClearBlendVisual()
        {
            Width = 96f;
            Height = 48f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_background, null, new Rect(0f, 0f, 96f, 48f));
            context.PushBlendMode(GpuBlendMode.Clear);
            context.PushOpacityMask(_mask, new Rect(8f, 8f, 32f, 32f));
            context.PopBlendMode();
            context.DrawRectangle(_red, null, new Rect(8f, 8f, 32f, 32f));
            context.PopOpacityMask();
        }
    }

    private sealed class GpuSeriesOpacityVisual : FrameworkElement
    {
        public GpuSeriesOpacityVisual()
        {
            Width = 64f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            int lineOffset = context.FloatBuffer.Count;
            context.FloatBuffer.AddRange(new[] { 0f, 0f, 20f, 20f });
            int scatterOffset = context.FloatBuffer.Count;
            context.FloatBuffer.AddRange(new[] { 4f, 4f, 6f, 24f, 24f, 6f });

            context.PushOpacity(0.5f);
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawGpuLineSeries,
                FloatBufferOffset = lineOffset,
                FloatBufferCount = 4,
                GpuPointsCount = 2,
                RadiusX = 2f,
                Brush = new SolidColorBrush(new Vector4(0.2f, 0.3f, 0.4f, 0.6f)) { Opacity = 0.5f },
                Scale = Vector2.One
            });
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawGpuScatterSeries,
                FloatBufferOffset = scatterOffset,
                FloatBufferCount = 6,
                GpuPointsCount = 2,
                RadiusX = 6f,
                Brush = new SolidColorBrush(new Vector4(0.8f, 0.7f, 0.6f, 0.8f)) { Opacity = 0.25f },
                Scale = Vector2.One
            });
            context.PopOpacity();
        }
    }

    private sealed class ShaderToyDisposalVisual : FrameworkElement
    {
        private readonly ShaderToyParams _shader;

        public ShaderToyDisposalVisual(ShaderToyParams shader)
        {
            _shader = shader;
            Width = 16f;
            Height = 16f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawExtension(
                CompositorBuiltInExtensions.ShaderToy,
                dataParam: _shader);
        }
    }

    private sealed class WpfShaderEffectDisposalVisual : FrameworkElement
    {
        private readonly WpfShaderEffectParams _effect;

        public WpfShaderEffectDisposalVisual(WpfShaderEffectParams effect)
        {
            _effect = effect;
            Width = 16f;
            Height = 16f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawWpfShaderEffect(_effect);
        }
    }

    private sealed class CachedLayerResizeVisual : DrawingVisual
    {
        public CachedLayerResizeVisual()
            : this(new Vector2(32f, 16f))
        {
        }

        public CachedLayerResizeVisual(Vector2 size)
        {
            Size = size;
            CacheAsLayer = true;
            Context.DrawRectangle(
                new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                pen: null,
                new Rect(Vector2.Zero, size));
        }
    }

    private sealed class SolidLogicalSceneVisual : DrawingVisual
    {
        public SolidLogicalSceneVisual()
            : this(new Vector2(10f, 10f))
        {
        }

        public SolidLogicalSceneVisual(Vector2 size)
        {
            Size = size;
            Context.DrawRectangle(
                new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                pen: null,
                new Rect(Vector2.Zero, size));
        }
    }

    private sealed class ThrowingRenderVisual : FrameworkElement
    {
        public ThrowingRenderVisual()
        {
            Width = 16f;
            Height = 16f;
        }

        public override void OnRender(DrawingContext context)
        {
            throw new System.InvalidOperationException("Synthetic offscreen render failure.");
        }
    }

    private sealed class MixedColorGlyphRunVisual : FrameworkElement
    {
        private readonly TtfFont _font;

        public MixedColorGlyphRunVisual(TtfFont font)
        {
            _font = font;
            Width = 96f;
            Height = 48f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawGlyphRun(
                new ushort[] { 1, 2 },
                new[] { new Vector2(6f, 30f), new Vector2(36f, 30f) },
                _font,
                24f,
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                Vector2.Zero);
        }
    }

    private sealed class RepeatedVectorGlyphVisual : FrameworkElement
    {
        private readonly TtfFont _font;
        private readonly int _glyphCount;

        public RepeatedVectorGlyphVisual(TtfFont font, int glyphCount)
        {
            _font = font;
            _glyphCount = glyphCount;
            Width = 1024f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            var glyphIndex = _font.GetGlyphIndex('A');
            for (var index = 0; index < _glyphCount; index++)
            {
                context.DrawGlyphRun(
                    new[] { glyphIndex },
                    new[] { new Vector2(index * 10f, 40f) },
                    _font,
                    24f,
                    new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                    Vector2.Zero,
                    useVectorGlyphRendering: true);
            }
        }
    }

    private sealed class FractionalVectorGlyphVisual : FrameworkElement
    {
        private readonly TtfFont _font;
        private readonly int _glyphCount;

        public FractionalVectorGlyphVisual(TtfFont font, int glyphCount)
        {
            _font = font;
            _glyphCount = glyphCount;
            Width = 1024f;
            Height = 384f;
        }

        public override void OnRender(DrawingContext context)
        {
            var glyphIndex = _font.GetGlyphIndex('A');
            var glyphIndices = new[] { glyphIndex };
            var brush = new SolidColorBrush(Vector4.One);
            for (var index = 0; index < _glyphCount; index++)
            {
                var fractionalPosition = new Vector2(
                    (index * 37 % 4093) / 4093f,
                    0.25f);
                var transform = Matrix4x4.CreateRotationZ(index * 0.017f) *
                    Matrix4x4.CreateTranslation(
                        index % 64 * 14f + 8f,
                        index / 64 * 44f + 36f,
                        0f);
                context.DrawGlyphRun(
                    glyphIndices,
                    new[] { fractionalPosition },
                    _font,
                    24f,
                    brush,
                    Vector2.Zero,
                    transform,
                    useVectorGlyphRendering: true);
            }
        }
    }

    private sealed class PathAtlasPressureVisual : FrameworkElement
    {
        private readonly int _pathCount;
        private readonly int _variant;

        public PathAtlasPressureVisual(int pathCount, int variant)
        {
            _pathCount = pathCount;
            _variant = variant;
            Width = 640f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            var brush = new SolidColorBrush(Vector4.One);
            for (var pathIndex = 0; pathIndex < _pathCount; pathIndex++)
            {
                var variantOffset = _variant * 0.25f;
                var path = PrimitivePathGeometry.CreateRectangle(
                    0f,
                    0f,
                    8f + pathIndex % 4 + variantOffset,
                    8f + pathIndex / 4 + variantOffset);
                context.DrawPath(
                    brush,
                    null,
                    path,
                    Matrix4x4.CreateTranslation(pathIndex * 24f + 4f, 24f, 0f));
            }
        }
    }

    private sealed class PartialRoundedBorderVisual : FrameworkElement
    {
        public PartialRoundedBorderVisual()
        {
            Width = 128f;
            Height = 96f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
                null,
                new Rect(0f, 0f, 128f, 96f));
            context.DrawPath(
                new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                null,
                CreatePartialRoundedBorderPath());
        }

        private static PathGeometry CreatePartialRoundedBorderPath()
        {
            var path = new PathGeometry { FillRule = FillRule.EvenOdd };
            path.Figures.Add(CreateContour(8f, 8f, 120f, 88f, 9f, 9f));
            path.Figures.Add(CreateContour(9f, 9f, 119f, 88f, 8f, 8f));
            return path;
        }

        private static PathFigure CreateContour(
            float left,
            float top,
            float right,
            float bottom,
            float topLeftRadius,
            float topRightRadius)
        {
            var figure = new PathFigure(new Vector2(left + topLeftRadius, top), isClosed: true);
            figure.Segments.Add(new LineSegment(new Vector2(right - topRightRadius, top)));
            figure.Segments.Add(new ArcSegment(
                new Vector2(right, top + topRightRadius),
                new Vector2(topRightRadius),
                rotationAngle: 0f,
                isLargeArc: false,
                SweepDirection.Clockwise));
            figure.Segments.Add(new LineSegment(new Vector2(right, bottom)));
            figure.Segments.Add(new LineSegment(new Vector2(left, bottom)));
            figure.Segments.Add(new LineSegment(new Vector2(left, top + topLeftRadius)));
            figure.Segments.Add(new ArcSegment(
                new Vector2(left + topLeftRadius, top),
                new Vector2(topLeftRadius),
                rotationAngle: 0f,
                isLargeArc: false,
                SweepDirection.Clockwise));
            return figure;
        }
    }

    private sealed class NonCanonicalRoundedPathVisual : FrameworkElement
    {
        public override void OnRender(DrawingContext context)
        {
            var figure = new PathFigure(new Vector2(10f, 0f), isClosed: true);
            figure.Segments.Add(new LineSegment(new Vector2(100f, 0f)));
            figure.Segments.Add(new LineSegment(new Vector2(10f, 0f)));
            figure.Segments.Add(new LineSegment(new Vector2(0f, 0f)));
            figure.Segments.Add(new LineSegment(new Vector2(0f, 10f)));
            figure.Segments.Add(new ArcSegment(
                new Vector2(10f, 0f),
                new Vector2(10f),
                rotationAngle: 0f,
                isLargeArc: false,
                SweepDirection.Clockwise));
            var path = new PathGeometry();
            path.Figures.Add(figure);
            context.DrawPath(new SolidColorBrush(Vector4.One), null, path);
        }
    }

    private sealed class LargeScaledPartialRoundedPathVisual : FrameworkElement
    {
        public override void OnRender(DrawingContext context)
        {
            var figure = new PathFigure(new Vector2(500f, 0f), isClosed: true);
            figure.Segments.Add(new LineSegment(new Vector2(500f, 0f)));
            figure.Segments.Add(new ArcSegment(
                new Vector2(1_000f, 500f),
                new Vector2(500f),
                rotationAngle: 0f,
                isLargeArc: false,
                SweepDirection.Clockwise));
            figure.Segments.Add(new LineSegment(new Vector2(1_000f, 500f)));
            figure.Segments.Add(new ArcSegment(
                new Vector2(500f, 1_000f),
                new Vector2(500f),
                rotationAngle: 0f,
                isLargeArc: false,
                SweepDirection.Clockwise));
            figure.Segments.Add(new LineSegment(new Vector2(0f, 1_000f)));
            figure.Segments.Add(new LineSegment(new Vector2(0f, 500f)));
            figure.Segments.Add(new ArcSegment(
                new Vector2(500f, 0f),
                new Vector2(500f),
                rotationAngle: 0f,
                isLargeArc: false,
                SweepDirection.Clockwise));
            var path = new PathGeometry();
            path.Figures.Add(figure);
            context.DrawPath(
                new SolidColorBrush(Vector4.One),
                null,
                path,
                Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(-900f, -900f, 0f));
        }
    }

    private sealed class CrossingRoundedRingVisual : FrameworkElement
    {
        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
                null,
                new Rect(0f, 0f, 112f, 112f));
            var path = new PathGeometry { FillRule = FillRule.EvenOdd };
            path.Figures.Add(CreateContour(6f, 6f, 106f, 106f, 40f));
            path.Figures.Add(CreateContour(7f, 7f, 95f, 95f, 0f));
            context.DrawPath(new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)), null, path);
        }

        private static PathFigure CreateContour(
            float left,
            float top,
            float right,
            float bottom,
            float topLeftRadius)
        {
            var figure = new PathFigure(new Vector2(left + topLeftRadius, top), isClosed: true);
            figure.Segments.Add(new LineSegment(new Vector2(right, top)));
            figure.Segments.Add(new LineSegment(new Vector2(right, bottom)));
            figure.Segments.Add(new LineSegment(new Vector2(left, bottom)));
            figure.Segments.Add(new LineSegment(new Vector2(left, top + topLeftRadius)));
            if (topLeftRadius > 0f)
            {
                figure.Segments.Add(new ArcSegment(
                    new Vector2(left + topLeftRadius, top),
                    new Vector2(topLeftRadius),
                    rotationAngle: 0f,
                    isLargeArc: false,
                    SweepDirection.Clockwise));
            }

            return figure;
        }
    }

    private sealed class ParentPhaseVectorGlyphVisual : FrameworkElement
    {
        private readonly TtfFont _font;
        private readonly int _phaseCount;

        public ParentPhaseVectorGlyphVisual(TtfFont font, int phaseCount)
        {
            _font = font;
            _phaseCount = phaseCount;
            Width = 640f;
            Height = 576f;
        }

        public override void OnRender(DrawingContext context)
        {
            var glyphIndices = new[] { _font.GetGlyphIndex('A') };
            var glyphPositions = new[] { Vector2.Zero };
            var brush = new SolidColorBrush(Vector4.One);
            for (var yPhase = 0; yPhase < _phaseCount; yPhase++)
            {
                for (var xPhase = 0; xPhase < _phaseCount; xPhase++)
                {
                    var transform = Matrix4x4.CreateTranslation(
                        xPhase * 40f + xPhase / (float)_phaseCount + 8f,
                        yPhase * 34f + yPhase / (float)_phaseCount + 30f,
                        0f);
                    context.DrawGlyphRun(
                        glyphIndices,
                        glyphPositions,
                        _font,
                        24f,
                        brush,
                        Vector2.Zero,
                        transform,
                        useVectorGlyphRendering: true);
                }
            }
        }
    }

    private sealed class OversizedPathVisual : FrameworkElement
    {
        public OversizedPathVisual()
        {
            Width = 96f;
            Height = 96f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawPath(
                new SolidColorBrush(Vector4.One),
                null,
                PrimitivePathGeometry.CreateRectangle(0f, 0f, 80f, 80f),
                Matrix4x4.Identity);
        }
    }

    private sealed class AtlasOverflowGlyphRunVisual : FrameworkElement
    {
        private readonly TtfFont _font;

        public AtlasOverflowGlyphRunVisual(TtfFont font)
        {
            _font = font;
            Width = 96f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            ushort glyphIndex = _font.GetGlyphIndex('A');
            context.DrawGlyphRun(
                new[] { glyphIndex, glyphIndex, glyphIndex, glyphIndex },
                new[]
                {
                    new Vector2(0f, 40f),
                    new Vector2(20.25f, 40f),
                    new Vector2(40.5f, 40f),
                    new Vector2(60.75f, 40f)
                },
                _font,
                24f,
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                Vector2.Zero);
        }
    }

    private sealed class ClippedGlyphRunVisual : FrameworkElement
    {
        private readonly TtfFont _font;

        public ClippedGlyphRunVisual(TtfFont font)
        {
            _font = font;
            Width = 64f;
            Height = 48f;
        }

        public override void OnRender(DrawingContext context)
        {
            ushort glyphIndex = _font.GetGlyphIndex('A');
            context.PushClip(new Rect(0f, 0f, 64f, 48f));
            context.DrawGlyphRun(
                new[] { glyphIndex, glyphIndex },
                new[]
                {
                    new Vector2(4f, 36f),
                    new Vector2(400.25f, 36f)
                },
                _font,
                24f,
                new SolidColorBrush(Vector4.One),
                Vector2.Zero);
            context.PopClip();
        }
    }

    private sealed class GlyphBrushOpacityVisual : FrameworkElement
    {
        private readonly TtfFont _font;

        public GlyphBrushOpacityVisual(TtfFont font)
        {
            _font = font;
            Width = 64f;
            Height = 48f;
        }

        public override void OnRender(DrawingContext context)
        {
            ushort glyphIndex = _font.GetGlyphIndex('A');
            var opaque = new SolidColorBrush(Vector4.One);
            var translucent = new SolidColorBrush(Vector4.One) { Opacity = 0.5f };
            context.DrawGlyphRun(
                new[] { glyphIndex },
                new[] { new Vector2(0f, 40f) },
                _font,
                24f,
                opaque,
                Vector2.Zero);
            context.PushOpacity(0.5f);
            context.DrawGlyphRun(
                new[] { glyphIndex },
                new[] { new Vector2(24f, 40f) },
                _font,
                24f,
                translucent,
                Vector2.Zero);
            context.PopOpacity();
        }
    }

    private sealed class CachedLayerGlyphVisual : FrameworkElement
    {
        private readonly TtfFont _font;

        public CachedLayerGlyphVisual(TtfFont font)
        {
            _font = font;
            Width = 48f;
            Height = 48f;
            CacheAsLayer = true;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawGlyphRun(
                new[] { _font.GetGlyphIndex('A') },
                new[] { new Vector2(8f, 40f) },
                _font,
                24f,
                new SolidColorBrush(Vector4.One),
                Vector2.Zero);
        }
    }

    private sealed class MixedColorTextVisual : FrameworkElement
    {
        private readonly TtfFont _font;

        public MixedColorTextVisual(TtfFont font)
        {
            _font = font;
            Width = 96f;
            Height = 48f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawText(
                "AB",
                _font,
                24f,
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                new Vector2(6f, 30f));
        }
    }

    private sealed class TransformedEllipticalRoundedRectangleVisual : FrameworkElement
    {
        public TransformedEllipticalRoundedRectangleVisual()
        {
            Width = 80f;
            Height = 40f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawRoundedRect,
                Brush = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                Rect = new Rect(0f, 0f, 24f, 24f),
                RadiusX = 4f,
                RadiusY = 8f,
                Transform = Matrix4x4.CreateTranslation(20f, 5f, 0f)
            });
        }
    }

    private sealed class ExplicitZeroRadiusYRoundedRectangleVisual : FrameworkElement
    {
        public ExplicitZeroRadiusYRoundedRectangleVisual()
        {
            Width = 32f;
            Height = 24f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawRoundedRect,
                Brush = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                Rect = new Rect(4f, 4f, 20f, 12f),
                RadiusX = 8f,
                RadiusY = 0f
            });
        }
    }

    private sealed class CountingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
