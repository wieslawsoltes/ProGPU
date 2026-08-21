using System;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class LayerRenderTests
{
    [Fact]
    public void UnchangedSceneReusesCompiledGpuBuffers()
    {
        using var window = new HeadlessWindow(64, 64);
        var visual = new SceneCacheVisual();
        window.Content = visual;

        try
        {
            window.Render();
            Assert.False(window.Compositor.Metrics.SceneCacheHit);

            window.Render();

            Assert.True(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal(1, visual.RenderCount);
            AssertRed(ReadPixel(window.ReadPixels(), window.Width, 20, 20));
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void PlacementOnlyRecompileReusesRetainedVisualCommands()
    {
        using var window = new HeadlessWindow(64, 64);
        var visual = new PlacementCommandVisual();
        window.Content = visual;

        try
        {
            window.Render();
            RgbaPixel background = ReadPixel(window.ReadPixels(), window.Width, 40, 40);

            visual.Offset = new Vector2(12f, 0f);
            window.Render();

            Assert.False(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal(1, visual.RenderCount);
            byte[] pixels = window.ReadPixels();
            AssertColorNear(background, ReadPixel(pixels, window.Width, 8, 10), tolerance: 1);
            AssertRed(ReadPixel(pixels, window.Width, 20, 10));
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void IncrementalPagesRecompileChangedOwnedVisualOnly()
    {
        var options = CompositorOptions.Default with
        {
            EnableGpuHitTesting = false,
            PrimarySampleCount = 1
        };
        using var window = new HeadlessWindow(64, 64, options);
        var first = new OwnedPageVisual(
            new Vector4(1f, 0f, 0f, 1f),
            new Vector2(4f, 4f));
        var second = new OwnedPageVisual(
            new Vector4(0f, 1f, 0f, 1f),
            new Vector2(36f, 4f));
        var host = new IncrementalPageHost(first, second);
        window.Content = host;

        try
        {
            window.Render();
            Assert.Equal(2, window.Compositor.Metrics.IncrementalScenePageCount);

            first.Transform = Matrix4x4.CreateTranslation(8f, 8f, 0f);
            window.Render();

            Assert.False(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal(1, window.Compositor.Metrics.IncrementalScenePageHits);
            Assert.Equal(1, window.Compositor.Metrics.IncrementalScenePageCompilations);
            Assert.Equal(3, window.Compositor.Metrics.IncrementalScenePageCount);
            byte[] pixels = window.ReadPixels();
            AssertRed(ReadPixel(pixels, window.Width, 12, 12));
            AssertGreen(ReadPixel(pixels, window.Width, 40, 8));

            first.Invalidate();
            window.Render();

            Assert.Equal(1, window.Compositor.Metrics.IncrementalScenePageHits);
            Assert.Equal(1, window.Compositor.Metrics.IncrementalScenePageCompilations);
            Assert.Equal(2, window.Compositor.Metrics.IncrementalScenePageCount);
            Assert.True(
                window.Compositor.Metrics.IncrementalScenePageReusedArrays > 0);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void IncrementalPagesReuseExactPresentationVariants()
    {
        var options = CompositorOptions.Default with
        {
            EnableGpuHitTesting = false,
            PrimarySampleCount = 1
        };
        using var window = new HeadlessWindow(64, 64, options);
        var visual = new PresentationOwnedPageVisual(
            new Vector4(1f, 0f, 0f, 1f),
            new Vector2(4f, 4f));
        window.Content = new IncrementalPageHost(visual);

        var grayscale = new IncrementalRenderPresentationState(
            RenderCommandPresentationDependencies.TextRendering,
            default,
            TextRenderingMode.Grayscale,
            default);
        var aliased = grayscale with
        {
            TextRenderingMode = TextRenderingMode.Aliased
        };

        try
        {
            visual.SetPresentationState(grayscale);
            window.Render();
            Assert.Equal(1, window.Compositor.Metrics.IncrementalScenePageCount);

            visual.SetPresentationState(aliased);
            window.Render();
            Assert.Equal(1, window.Compositor.Metrics.IncrementalScenePageCompilations);
            Assert.Equal(2, window.Compositor.Metrics.IncrementalScenePageCount);

            visual.SetPresentationState(grayscale);
            window.Render();
            Assert.Equal(1, window.Compositor.Metrics.IncrementalScenePageHits);
            Assert.Equal(0, window.Compositor.Metrics.IncrementalScenePageCompilations);
            Assert.Equal(2, window.Compositor.Metrics.IncrementalScenePageCount);

            visual.SetPresentationState(aliased);
            window.Render();
            Assert.Equal(1, window.Compositor.Metrics.IncrementalScenePageHits);
            Assert.Equal(0, window.Compositor.Metrics.IncrementalScenePageCompilations);
            Assert.Equal(2, window.Compositor.Metrics.IncrementalScenePageCount);
            AssertRed(ReadPixel(window.ReadPixels(), window.Width, 8, 8));

            visual.Invalidate();
            window.Render();
            Assert.Equal(1, window.Compositor.Metrics.IncrementalScenePageCompilations);
            Assert.Equal(1, window.Compositor.Metrics.IncrementalScenePageCount);
            Assert.True(
                window.Compositor.Metrics.IncrementalScenePageReusedArrays > 0);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void IncrementalPagesBackOffFromContinuouslyChangingPlacement()
    {
        var options = CompositorOptions.Default with
        {
            EnableCompiledSceneCache = false,
            EnableGpuHitTesting = false,
            PrimarySampleCount = 1,
            MaximumIncrementalScenePageVariantsPerVisual = 2,
            IncrementalScenePageVolatilityCooldownFrames = 4
        };
        using var window = new HeadlessWindow(64, 64, options);
        var visual = new OwnedPageVisual(
            new Vector4(1f, 0f, 0f, 1f),
            new Vector2(4f, 4f));
        var host = new IncrementalPageHost(visual);
        window.Content = host;

        try
        {
            window.Render();
            Assert.Equal(1, window.Compositor.Metrics.IncrementalScenePageCount);

            visual.Transform = Matrix4x4.CreateTranslation(8f, 4f, 0f);
            window.Render();
            Assert.Equal(2, window.Compositor.Metrics.IncrementalScenePageCount);

            visual.Transform = Matrix4x4.CreateTranslation(12f, 4f, 0f);
            window.Render();
            Assert.Equal(0, window.Compositor.Metrics.IncrementalScenePageCount);
            Assert.Equal(
                "Composition state is volatile",
                window.Compositor.Metrics.IncrementalScenePageRejectReason);
            Assert.Equal(0, window.Compositor.Metrics.IncrementalScenePageCompilations);

            for (int frame = 0; frame < 3; frame++)
            {
                host.Invalidate();
                window.Render();
                Assert.Equal(
                    0,
                    window.Compositor.Metrics.IncrementalScenePageCompilations);
            }

            host.Invalidate();
            window.Render();
            Assert.Equal(1, window.Compositor.Metrics.IncrementalScenePageCompilations);
            Assert.Equal(1, window.Compositor.Metrics.IncrementalScenePageCount);

            host.Invalidate();
            window.Render();
            Assert.Equal(1, window.Compositor.Metrics.IncrementalScenePageHits);
            Assert.Equal(0, window.Compositor.Metrics.IncrementalScenePageCompilations);
            AssertRed(ReadPixel(window.ReadPixels(), window.Width, 16, 8));
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void VolatileCommandProducerBypassesIncrementalPageCache()
    {
        var options = CompositorOptions.Default with
        {
            EnableCompiledSceneCache = false,
            EnableGpuHitTesting = false,
            PrimarySampleCount = 1
        };
        using var window = new HeadlessWindow(64, 64, options);
        var visual = new VolatileOwnedPageVisual(
            new Vector4(1f, 0f, 0f, 1f),
            new Vector2(4f, 4f));
        window.Content = visual;

        try
        {
            window.Render();

            Assert.Equal(0, window.Compositor.Metrics.IncrementalScenePageCount);
            Assert.Equal(
                "Command producer is volatile",
                window.Compositor.Metrics.IncrementalScenePageRejectReason);
            Assert.Equal(
                0,
                window.Compositor.Metrics.IncrementalScenePageCompilations);
            AssertRed(ReadPixel(window.ReadPixels(), window.Width, 8, 8));

            visual.Invalidate();
            window.Render();

            Assert.Equal(0, window.Compositor.Metrics.IncrementalScenePageCount);
            Assert.Equal(
                0,
                window.Compositor.Metrics.IncrementalScenePageCompilations);
            AssertRed(ReadPixel(window.ReadPixels(), window.Width, 8, 8));
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void IncrementalPagesBackOffWhenGlobalCacheSaturates()
    {
        var options = CompositorOptions.Default with
        {
            EnableCompiledSceneCache = false,
            EnableGpuHitTesting = false,
            PrimarySampleCount = 1,
            MaximumIncrementalScenePages = 2,
            IncrementalScenePageVolatilityCooldownFrames = 4
        };
        using var window = new HeadlessWindow(64, 64, options);
        var host = new IncrementalPageHost(
            new OwnedPageVisual(
                new Vector4(1f, 0f, 0f, 1f),
                new Vector2(4f, 4f)),
            new OwnedPageVisual(
                new Vector4(0f, 1f, 0f, 1f),
                new Vector2(24f, 4f)),
            new OwnedPageVisual(
                new Vector4(0f, 0f, 1f, 1f),
                new Vector2(44f, 4f)));
        window.Content = host;

        try
        {
            window.Render();

            Assert.Equal(0, window.Compositor.Metrics.IncrementalScenePageCount);
            Assert.Equal(
                "Incremental page cache is saturated",
                window.Compositor.Metrics.IncrementalScenePageRejectReason);
            byte[] pixels = window.ReadPixels();
            AssertRed(ReadPixel(pixels, window.Width, 8, 8));
            AssertGreen(ReadPixel(pixels, window.Width, 28, 8));

            host.Invalidate();
            window.Render();
            Assert.Equal(0, window.Compositor.Metrics.IncrementalScenePageCount);
            Assert.Equal(0, window.Compositor.Metrics.IncrementalScenePageCompilations);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void IncrementalPagesUploadOnlyChangedGpuBufferPages()
    {
        var options = CompositorOptions.Default with
        {
            EnableGpuHitTesting = false,
            PrimarySampleCount = 1
        };
        using var window = new HeadlessWindow(64, 64, options);
        var pages = new OwnedPageVisual[32];
        for (int index = 0; index < pages.Length; index++)
        {
            pages[index] = new OwnedPageVisual(
                new Vector4(1f, 0f, 0f, 1f),
                new Vector2(index % 8 * 8f, index / 8 * 8f));
        }

        window.Content = new IncrementalPageHost(pages);

        try
        {
            window.Render();
            long initialUploadBytes =
                window.Compositor.Metrics.IncrementalSceneUploadBytes;
            Assert.True(initialUploadBytes > 4096);

            pages[16].Transform *=
                Matrix4x4.CreateTranslation(1f, 1f, 0f);
            window.Render();

            CompositorMetrics metrics = window.Compositor.Metrics;
            Assert.Equal(31, metrics.IncrementalScenePageHits);
            Assert.Equal(1, metrics.IncrementalScenePageCompilations);
            Assert.True(metrics.IncrementalSceneUploadBytes > 0);
            Assert.True(
                metrics.IncrementalSceneUploadBytes < initialUploadBytes,
                $"Expected a partial upload below {initialUploadBytes} bytes, " +
                $"but uploaded {metrics.IncrementalSceneUploadBytes} bytes.");
            Assert.Equal(1, metrics.SceneUploadBatchCount);
            Assert.Equal(
                metrics.IncrementalSceneUploadPageWrites,
                metrics.SceneUploadCopyCount);
            Assert.True(
                metrics.SceneUploadArenaBytes >= 2UL * 4096UL,
                "Native scene uploads should retain a bounded two-slot " +
                "mapped ring instead of allocating queue staging per write.");

            window.Render();

            metrics = window.Compositor.Metrics;
            Assert.Equal(0, metrics.IncrementalSceneUploadBytes);
            Assert.Equal(0, metrics.SceneUploadCopyCount);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void EmptyOwnedCommandCacheSkipsLookupAndStillRendersChildren()
    {
        using var window = new HeadlessWindow(64, 64);
        var child = new EmbeddedColorVisual();
        var host = new EmptyOwnedCommandCacheHost(child);
        window.Content = host;

        try
        {
            window.Render();

            Assert.Equal(0, host.CommandCacheLookupCount);
            AssertRed(ReadPixel(window.ReadPixels(), window.Width, 12, 12));
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void VisualInvalidationRecompilesSceneAndUpdatesPixels()
    {
        using var window = new HeadlessWindow(64, 64);
        var visual = new SceneCacheVisual();
        window.Content = visual;

        try
        {
            window.Render();
            window.Render();
            Assert.True(window.Compositor.Metrics.SceneCacheHit);

            visual.SetColor(new Vector4(0f, 1f, 0f, 1f));
            window.Render();

            Assert.False(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal("Root version changed", window.Compositor.Metrics.SceneCacheMissReason);
            Assert.Equal(2, visual.RenderCount);
            AssertGreen(ReadPixel(window.ReadPixels(), window.Width, 20, 20));
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void EmbeddedRetainedVisualInvalidationRecompilesOnlyThroughTypedDependency()
    {
        using var window = new HeadlessWindow(64, 64);
        var embedded = new EmbeddedColorVisual();
        var host = new EmbeddedVisualHost(embedded);
        window.Content = host;

        try
        {
            window.Render();
            AssertRed(ReadPixel(window.ReadPixels(), window.Width, 20, 20));

            window.Render();
            Assert.True(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal(1, embedded.RenderCount);

            embedded.SetColor(new Vector4(0f, 1f, 0f, 1f));
            window.Render();

            Assert.False(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal("Embedded visual changed", window.Compositor.Metrics.SceneCacheMissReason);
            Assert.Equal(2, embedded.RenderCount);
            AssertGreen(ReadPixel(window.ReadPixels(), window.Width, 20, 20));

            window.Render();
            Assert.True(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal(2, embedded.RenderCount);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawingContextRetainsVisualIdentityAndPlacementTransform()
    {
        var embedded = new Visual();
        var context = new DrawingContext();
        var transform = Matrix4x4.CreateTranslation(12f, 7f, 0f);

        context.DrawVisual(embedded, transform);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawVisual, command.Type);
        Assert.Same(embedded, command.Visual);
        Assert.Equal(transform, command.Transform);
    }

    [Fact]
    public void EmbeddedCachedLayerRetainsTextureAcrossDirtyFrames()
    {
        using var window = new HeadlessWindow(64, 64);
        var embedded = new ScaledCachedLayerVisual();
        embedded.Measure(new Vector2(20f, 10f));
        embedded.Arrange(new Rect(0f, 0f, 20f, 10f));
        var host = new EmbeddedVisualHost(embedded);
        window.Content = host;

        window.Render();

        GpuTexture layer = Assert.IsType<GpuTexture>(
            embedded.LayerTexture);
        Assert.False(layer.IsDisposed);
        ulong textureId = layer.Id;
        Assert.True(window.Compositor.Metrics.LayerTextureBytes > 0);
        Assert.Equal(
            1,
            window.Compositor.Metrics.PersistentTextureBindGroupCount);

        embedded.Opacity = 0.75f;
        window.Render();

        Assert.Same(layer, embedded.LayerTexture);
        Assert.Equal(textureId, embedded.LayerTexture!.Id);
        Assert.False(layer.IsDisposed);
        Assert.True(window.Compositor.Metrics.LayerTextureBytes > 0);
        Assert.Equal(
            1,
            window.Compositor.Metrics.PersistentTextureBindGroupCount);
    }

    [Fact]
    public void ResizeInvalidatesCompiledSceneTarget()
    {
        using var window = new HeadlessWindow(64, 64);
        var visual = new SceneCacheVisual();
        window.Content = visual;

        try
        {
            window.Render();
            window.Render();
            Assert.True(window.Compositor.Metrics.SceneCacheHit);

            window.Resize(80, 64);
            window.Render();

            Assert.False(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal(2, visual.RenderCount);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void MutableDrawingVisualDisablesCompiledSceneReuse()
    {
        using var window = new HeadlessWindow(64, 64);
        window.Content = new DrawingVisualHost();

        try
        {
            window.Render();
            window.Render();

            Assert.False(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal("Drawing visuals active", window.Compositor.Metrics.SceneCacheMissReason);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void EmptyDrawingVisualAllowsCompiledSceneReuse()
    {
        using var window = new HeadlessWindow(64, 64);
        window.Content = new EmptyDrawingVisualHost();

        try
        {
            window.Render();
            window.Render();

            Assert.True(window.Compositor.Metrics.SceneCacheHit);
            Assert.Null(window.Compositor.Metrics.SceneCacheMissReason);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void CachedLayerCompositeIncludesVisualLocalTransform()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(160, 100);
        window.Content = new LayerHostVisual();

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var background = ReadPixel(pixels, window.Width, x: 10, y: 10);
            var rotatedOnly = ReadPixel(pixels, window.Width, x: 100, y: 25);
            var unrotatedOnly = ReadPixel(pixels, window.Width, x: 85, y: 40);

            AssertRed(rotatedOnly);
            AssertColorNear(background, unrotatedOnly, tolerance: 12);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void CachedLayerRenderScaleControlsTextureResolution()
    {
        using var window = new HeadlessWindow(64, 64);
        var visual = new ScaledCachedLayerVisual
        {
            LayerCacheRenderScale = 2f
        };
        window.Content = visual;

        try
        {
            window.Render();
            Assert.NotNull(visual.LayerTexture);
            Assert.Equal(40u, visual.LayerTexture!.Width);
            Assert.Equal(20u, visual.LayerTexture.Height);
            AssertRed(ReadPixel(window.ReadPixels(), window.Width, 10, 5));

            visual.LayerCacheRenderScale = 0.5f;
            window.Render();
            Assert.Equal(10u, visual.LayerTexture!.Width);
            Assert.Equal(5u, visual.LayerTexture.Height);
            AssertRed(ReadPixel(window.ReadPixels(), window.Width, 10, 5));

            visual.LayerCacheRenderScale = 0f;
            window.Render();
            Assert.Null(visual.LayerTexture);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void CachedLayerCompositeAppliesVisualOpacityAndClip()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(100, 60);
        window.Content = new VisualCompositeScopeHost(new ClippedOpacityLayerVisual());

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var visible = ReadPixel(pixels, window.Width, x: 25, y: 25);
            var clipped = ReadPixel(pixels, window.Width, x: 65, y: 25);

            AssertHalfRed(visible);
            AssertBlack(clipped);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void VisualCompositeScopeAppliesRetainedOpacityMask()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(100, 60);
        window.Content = new VisualCompositeScopeHost(new OpacityMaskedVisual());

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var visible = ReadPixel(pixels, window.Width, x: 25, y: 25);
            var masked = ReadPixel(pixels, window.Width, x: 65, y: 25);

            AssertRed(visible);
            AssertBlack(masked);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void CachedLayerCompositeAppliesRetainedOpacityMask()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(100, 60);
        window.Content = new VisualCompositeScopeHost(new CachedOpacityMaskedVisual());

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var visible = ReadPixel(pixels, window.Width, x: 25, y: 25);
            var masked = ReadPixel(pixels, window.Width, x: 65, y: 25);

            AssertRed(visible);
            AssertBlack(masked);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void VisualCompositeScopeAppliesRetainedPictureOpacityMask()
    {
        var recorder = new GpuPictureRecorder();
        var maskContext = recorder.BeginRecording(
            new Rect(0f, 0f, 80f, 50f));
        maskContext.DrawRectangle(
            new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            null,
            new Rect(0f, 0f, 40f, 50f));
        using var picture = recorder.EndRecording();

        var window = HeadlessWindow.Shared;
        window.Resize(100, 60);
        window.Content = new VisualCompositeScopeHost(
            new PictureOpacityMaskedVisual(picture));

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            AssertRed(ReadPixel(pixels, window.Width, x: 25, y: 25));
            AssertBlack(ReadPixel(pixels, window.Width, x: 65, y: 25));
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void CachedLayerHitTestCachePreservesLayerAndDescendantOwners()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(100, 60);
        window.Content = new VisualCompositeScopeHost(new HitTestCachedLayerVisual());

        try
        {
            window.Render();
            window.Render();

            var index = window.Compositor.LastHitTestIndex;
            Assert.NotNull(index);
            var ownerPrimitives = index!.Primitives.Where(primitive => primitive.Id == 991).ToArray();
            var primitive = Assert.Single(ownerPrimitives);
            Assert.Equal(GpuHitTestPrimitiveKind.AxisAlignedBounds, primitive.Kind);
            Assert.Equal(new Vector2(10f, 5f), primitive.BoundsMin);
            Assert.Equal(new Vector2(90f, 55f), primitive.BoundsMax);

            var childPrimitive = Assert.Single(index.Primitives, primitive => primitive.Id == 993);
            Assert.Equal(GpuHitTestPrimitiveKind.AxisAlignedBounds, childPrimitive.Kind);
            Assert.Equal(new Vector2(20f, 15f), childPrimitive.BoundsMin);
            Assert.Equal(new Vector2(50f, 35f), childPrimitive.BoundsMax);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void PicturePlaybackContributesSubcommandsToHitTestCache()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(100, 60);
        window.Content = new PictureHitTestVisual();

        try
        {
            window.Render();

            var index = window.Compositor.LastHitTestIndex;
            Assert.NotNull(index);
            var primitive = Assert.Single(index!.Primitives, primitive => primitive.Id == 992);
            Assert.Equal(GpuHitTestPrimitiveKind.PathStroke, primitive.Kind);
            Assert.Equal(new Vector2(0f, 0f), primitive.BoundsMin);
            Assert.Equal(new Vector2(12f, 12f), primitive.BoundsMax);
        }
        finally
        {
            window.Content = null;
        }
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

    private static void AssertRed(RgbaPixel pixel)
    {
        Assert.True(pixel.R >= 220, $"Expected cached layer to render red, found {pixel}.");
        Assert.True(pixel.G <= 35, $"Expected cached layer green channel to stay low, found {pixel}.");
        Assert.True(pixel.B <= 35, $"Expected cached layer blue channel to stay low, found {pixel}.");
        Assert.Equal(255, pixel.A);
    }

    private static void AssertHalfRed(RgbaPixel pixel)
    {
        Assert.InRange(pixel.R, 115, 140);
        Assert.InRange(pixel.G, 0, 12);
        Assert.InRange(pixel.B, 0, 12);
        Assert.Equal(255, pixel.A);
    }

    private static void AssertGreen(RgbaPixel pixel)
    {
        Assert.True(pixel.G >= 220, $"Expected scene to render green, found {pixel}.");
        Assert.True(pixel.R <= 35, $"Expected scene red channel to stay low, found {pixel}.");
        Assert.True(pixel.B <= 35, $"Expected scene blue channel to stay low, found {pixel}.");
        Assert.Equal(255, pixel.A);
    }

    private static void AssertBlack(RgbaPixel pixel)
    {
        Assert.InRange(pixel.R, 0, 12);
        Assert.InRange(pixel.G, 0, 12);
        Assert.InRange(pixel.B, 0, 12);
        Assert.Equal(255, pixel.A);
    }

    private static void AssertColorNear(RgbaPixel expected, RgbaPixel actual, int tolerance)
    {
        Assert.InRange(Math.Abs(expected.R - actual.R), 0, tolerance);
        Assert.InRange(Math.Abs(expected.G - actual.G), 0, tolerance);
        Assert.InRange(Math.Abs(expected.B - actual.B), 0, tolerance);
        Assert.InRange(Math.Abs(expected.A - actual.A), 0, tolerance);
    }

    private readonly record struct RgbaPixel(byte R, byte G, byte B, byte A);

    private sealed class SceneCacheVisual : FrameworkElement
    {
        private readonly SolidColorBrush _brush = new(new Vector4(1f, 0f, 0f, 1f));

        public int RenderCount { get; private set; }

        public SceneCacheVisual()
        {
            Width = 64f;
            Height = 64f;
        }

        public void SetColor(Vector4 color)
        {
            _brush.Color = color;
            Invalidate();
        }

        public override void OnRender(DrawingContext context)
        {
            RenderCount++;
            context.DrawRectangle(_brush, null, new Rect(0f, 0f, 64f, 64f));
        }
    }

    private sealed class PlacementCommandVisual : FrameworkElement
    {
        private readonly SolidColorBrush _brush = new(new Vector4(1f, 0f, 0f, 1f));

        public int RenderCount { get; private set; }

        public PlacementCommandVisual()
        {
            Width = 64f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            RenderCount++;
            context.DrawRectangle(_brush, null, new Rect(4f, 4f, 16f, 16f));
        }
    }

    private sealed class DrawingVisualHost : FrameworkElement
    {
        public DrawingVisualHost()
        {
            Width = 64f;
            Height = 64f;
            var drawing = new DrawingVisual { Size = new Vector2(64f, 64f) };
            drawing.Context.DrawRectangle(
                new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 64f, 64f));
            AddChild(drawing);
        }
    }

    private sealed class EmptyDrawingVisualHost : FrameworkElement
    {
        public EmptyDrawingVisualHost()
        {
            Width = 64f;
            Height = 64f;
            AddChild(new DrawingVisual { Size = new Vector2(64f, 64f) });
        }
    }

    private sealed class IncrementalPageHost : FrameworkElement
    {
        public IncrementalPageHost(params Visual[] children)
        {
            Width = 64f;
            Height = 64f;
            foreach (Visual child in children)
            {
                AddChild(child);
            }
        }
    }

    private sealed class OwnedPageVisual : FrameworkElement,
        IIncrementalRenderCommandCache
    {
        private readonly DrawingContext _commands = new();

        public OwnedPageVisual(Vector4 color, Vector2 offset)
        {
            Width = 20f;
            Height = 20f;
            Transform = Matrix4x4.CreateTranslation(
                offset.X,
                offset.Y,
                0f);
            _commands.DrawRectangle(
                new SolidColorBrush(color),
                null,
                new Rect(0f, 0f, 20f, 20f));
        }

        public DrawingContext GetOrUpdateRenderCommandCache() => _commands;
    }

    private sealed class PresentationOwnedPageVisual : FrameworkElement,
        IIncrementalRenderCommandCache
    {
        private readonly DrawingContext _commands = new();
        private IncrementalRenderPresentationState _presentationState;

        public PresentationOwnedPageVisual(Vector4 color, Vector2 offset)
        {
            Width = 20f;
            Height = 20f;
            Transform = Matrix4x4.CreateTranslation(
                offset.X,
                offset.Y,
                0f);
            _commands.DrawRectangle(
                new SolidColorBrush(color),
                null,
                new Rect(0f, 0f, 20f, 20f));
        }

        IncrementalRenderPresentationState
            IIncrementalRenderCommandCache.IncrementalPresentationState =>
                _presentationState;

        public DrawingContext GetOrUpdateRenderCommandCache() => _commands;

        public void SetPresentationState(
            in IncrementalRenderPresentationState presentationState)
        {
            if (_presentationState == presentationState)
                return;

            _presentationState = presentationState;
            InvalidateVisualState();
        }
    }

    private sealed class VolatileOwnedPageVisual : FrameworkElement,
        IIncrementalRenderCommandCache
    {
        private readonly DrawingContext _commands = new();

        public VolatileOwnedPageVisual(Vector4 color, Vector2 offset)
        {
            Width = 20f;
            Height = 20f;
            Transform = Matrix4x4.CreateTranslation(
                offset.X,
                offset.Y,
                0f);
            _commands.DrawRectangle(
                new SolidColorBrush(color),
                null,
                new Rect(0f, 0f, 20f, 20f));
        }

        bool IIncrementalRenderCommandCache.CanCacheIncrementalPage => false;

        public DrawingContext GetOrUpdateRenderCommandCache() => _commands;
    }

    private sealed class EmbeddedVisualHost : FrameworkElement, IOwnedRenderCommandCache
    {
        private readonly DrawingContext _commands = new();

        public EmbeddedVisualHost(Visual embedded)
        {
            Width = 64f;
            Height = 64f;
            _commands.DrawVisual(embedded, Matrix4x4.CreateTranslation(8f, 8f, 0f));
        }

        public DrawingContext GetOrUpdateRenderCommandCache() => _commands;
    }

    private sealed class EmptyOwnedCommandCacheHost : FrameworkElement, IOwnedRenderCommandCache
    {
        private readonly DrawingContext _commands = new();

        public EmptyOwnedCommandCacheHost(Visual child)
        {
            Width = 64f;
            Height = 64f;
            AddChild(child);
        }

        public int CommandCacheLookupCount { get; private set; }

        bool IOwnedRenderCommandCache.HasRenderCommands => false;

        public DrawingContext GetOrUpdateRenderCommandCache()
        {
            CommandCacheLookupCount++;
            return _commands;
        }
    }

    private sealed class EmbeddedColorVisual : Visual
    {
        private Vector4 _color = new(1f, 0f, 0f, 1f);

        public EmbeddedColorVisual()
        {
            Size = new Vector2(24f, 24f);
        }

        public int RenderCount { get; private set; }

        public void SetColor(Vector4 color)
        {
            _color = color;
            Invalidate();
        }

        public override void OnRender(DrawingContext context)
        {
            RenderCount++;
            context.DrawRectangle(
                new SolidColorBrush(_color),
                null,
                new Rect(0f, 0f, 24f, 24f));
        }
    }

    private sealed class VisualCompositeScopeHost : FrameworkElement
    {
        private readonly FrameworkElement _child;
        private readonly SolidColorBrush _background = new(new Vector4(0f, 0f, 0f, 1f));

        public VisualCompositeScopeHost(FrameworkElement child)
        {
            _child = child;
            Width = 100f;
            Height = 60f;
            AddChild(_child);
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            _child.Measure(new Vector2(80f, 50f));
            return availableSize;
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            _child.Arrange(new Rect(10f, 5f, 80f, 50f));
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_background, null, new Rect(0f, 0f, 100f, 60f));
        }
    }

    private sealed class LayerHostVisual : FrameworkElement
    {
        private readonly RotatedCachedLayerVisual _layer = new();

        public LayerHostVisual()
        {
            Width = 160f;
            Height = 100f;
            AddChild(_layer);
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            _layer.Measure(new Vector2(40f, 20f));
            return availableSize;
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            _layer.Arrange(new Rect(80f, 30f, 40f, 20f));
        }
    }

    private sealed class RotatedCachedLayerVisual : FrameworkElement
    {
        public RotatedCachedLayerVisual()
        {
            Width = 40f;
            Height = 20f;
            Rotation = MathF.PI * 0.5f;
            CacheAsLayer = true;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 40f, 20f));
        }
    }

    private sealed class ScaledCachedLayerVisual : FrameworkElement
    {
        private readonly SolidColorBrush _red =
            new(new Vector4(1f, 0f, 0f, 1f));

        public ScaledCachedLayerVisual()
        {
            Width = 20f;
            Height = 10f;
            CacheAsLayer = true;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                _red,
                null,
                new Rect(0f, 0f, 20f, 10f));
        }
    }

    private sealed class ClippedOpacityLayerVisual : FrameworkElement
    {
        private readonly SolidColorBrush _red = new(new Vector4(1f, 0f, 0f, 1f));

        public ClippedOpacityLayerVisual()
        {
            Width = 80f;
            Height = 50f;
            CacheAsLayer = true;
            Opacity = 0.5f;
            ClipBounds = new Rect(0f, 0f, 40f, 50f);
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_red, null, new Rect(0f, 0f, 80f, 50f));
        }
    }

    private class OpacityMaskedVisual : FrameworkElement
    {
        private readonly SolidColorBrush _red = new(new Vector4(1f, 0f, 0f, 1f));

        public OpacityMaskedVisual()
        {
            Width = 80f;
            Height = 50f;
            OpacityMask = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f));
            OpacityMaskBounds = new Rect(0f, 0f, 40f, 50f);
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_red, null, new Rect(0f, 0f, 80f, 50f));
        }
    }

    private sealed class CachedOpacityMaskedVisual : OpacityMaskedVisual
    {
        public CachedOpacityMaskedVisual()
        {
            CacheAsLayer = true;
        }
    }

    private sealed class PictureOpacityMaskedVisual : FrameworkElement
    {
        private readonly SolidColorBrush _red =
            new(new Vector4(1f, 0f, 0f, 1f));

        public PictureOpacityMaskedVisual(GpuPicture picture)
        {
            Width = 80f;
            Height = 50f;
            OpacityMaskPicture = picture;
            OpacityMaskBounds = new Rect(0f, 0f, 80f, 50f);
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                _red,
                null,
                new Rect(0f, 0f, 80f, 50f));
        }
    }

    private sealed class HitTestCachedLayerVisual : FrameworkElement
    {
        private readonly SolidColorBrush _red = new(new Vector4(1f, 0f, 0f, 1f));
        private readonly FrameworkElement _child;

        public HitTestCachedLayerVisual()
        {
            Width = 80f;
            Height = 50f;
            CacheAsLayer = true;
            HitTestId = 991;

            _child = new FrameworkElement
            {
                Width = 30f,
                Height = 20f,
                HitTestId = 993
            };
            AddChild(_child);
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            _child.Measure(new Vector2(30f, 20f));
            return availableSize;
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            _child.Arrange(new Rect(10f, 10f, 30f, 20f));
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_red, null, new Rect(0f, 0f, 80f, 50f));
        }
    }

    private sealed class PictureHitTestVisual : FrameworkElement
    {
        private readonly GpuPicture _picture;

        public PictureHitTestVisual()
        {
            Width = 100f;
            Height = 60f;

            _picture = new GpuPicture(
                [
                    new RenderCommand
                    {
                        Type = RenderCommandType.PushClip,
                        Rect = new Rect(0f, 0f, 12f, 12f)
                    },
                    new RenderCommand
                    {
                        Type = RenderCommandType.DrawPolyline,
                        HitTestId = 992,
                        Pen = new Pen(new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)), 2f),
                        PointBufferOffset = 0,
                        PointBufferCount = 3
                    },
                    new RenderCommand
                    {
                        Type = RenderCommandType.PopClip
                    }
                ],
                [
                    new Vector2(0f, 0f),
                    new Vector2(20f, 0f),
                    new Vector2(20f, 20f)
                ],
                [],
                [],
                []);
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawPicture(_picture);
        }
    }
}
