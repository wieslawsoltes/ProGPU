using System;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Fonts.Inter;
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
    public void RootQuarterPixelTranslationPatchesOnePlacementWithoutRecompiling()
    {
        using var window = new HeadlessWindow(
            64,
            64,
            new CompositorOptions
            {
                EnableGpuHitTesting = false,
                PrimarySampleCount = 1
            });
        var visual = new TranslatedSceneCacheVisual();
        window.Content = visual;

        try
        {
            window.Render();
            Assert.False(window.Compositor.Metrics.SceneCacheHit);
            RgbaPixel background = ReadPixel(window.ReadPixels(), window.Width, 40, 40);

            visual.Transform = Matrix4x4.CreateTranslation(12f, 0f, 0f);
            window.Render();

            Assert.True(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal(1, visual.RenderCount);
            byte[] pixels = window.ReadPixels();
            AssertColorNear(background, ReadPixel(pixels, window.Width, 8, 10), tolerance: 1);
            AssertRed(ReadPixel(pixels, window.Width, 20, 10));

            visual.Transform = Matrix4x4.CreateTranslation(16f, 0f, 0f);
            window.Render();

            Assert.True(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal(1, visual.RenderCount);
            AssertRed(ReadPixel(window.ReadPixels(), window.Width, 24, 10));
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void RootTextTranslationActivatesRetainedGlyphPlacement()
    {
        using var window = new HeadlessWindow(
            64,
            64,
            new CompositorOptions { EnableGpuHitTesting = false, PrimarySampleCount = 1 });
        var text = new RetainedGlyphVisual();
        window.Content = text;

        try
        {
            window.Render();
            byte[] before = window.ReadPixels();

            text.Transform = Matrix4x4.CreateTranslation(8f, 0f, 0f);
            window.Render();

            Assert.True(window.Compositor.Metrics.SceneCacheHit);
            byte[] after = window.ReadPixels();
            int redPixels = 0;
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 56; x++)
                {
                    RgbaPixel source = ReadPixel(before, window.Width, x, y);
                    if (source.R > source.G + 32 && source.R > source.B + 32 && source.A > 32)
                    {
                        redPixels++;
                        AssertColorNear(
                            source,
                            ReadPixel(after, window.Width, x + 8, y),
                            tolerance: 2);
                    }
                }
            }
            Assert.True(redPixels > 0);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void FractionalRootTranslationFallsBackToFullCompilation()
    {
        using var window = new HeadlessWindow(
            64,
            64,
            new CompositorOptions { EnableGpuHitTesting = false, PrimarySampleCount = 1 });
        var visual = new TranslatedSceneCacheVisual();
        window.Content = visual;

        try
        {
            window.Render();
            visual.Transform = Matrix4x4.CreateTranslation(0.1f, 0f, 0f);
            window.Render();

            Assert.False(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal("Root version changed", window.Compositor.Metrics.SceneCacheMissReason);
            Assert.Equal(2, visual.RenderCount);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DescendantTranslationFallsBackToFullCompilation()
    {
        using var window = new HeadlessWindow(
            64,
            64,
            new CompositorOptions { EnableGpuHitTesting = false, PrimarySampleCount = 1 });
        var visual = new PlacementHostVisual();
        window.Content = visual;

        try
        {
            window.Render();
            visual.Child.Transform = Matrix4x4.CreateTranslation(12f, 0f, 0f);
            window.Render();

            Assert.False(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal("Root version changed", window.Compositor.Metrics.SceneCacheMissReason);
            Assert.Equal(2, visual.Child.RenderCount);
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

    private sealed class TranslatedSceneCacheVisual : FrameworkElement
    {
        private readonly SolidColorBrush _brush = new(new Vector4(1f, 0f, 0f, 1f));

        public int RenderCount { get; private set; }

        public TranslatedSceneCacheVisual()
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

    private sealed class RetainedGlyphVisual : FrameworkElement
    {
        private readonly ushort[] _glyphIndices = { InterFontFamily.Regular.GetGlyphIndex('A') };
        private readonly Vector2[] _glyphPositions = { new(4f, 32f) };
        private readonly SolidColorBrush _brush = new(new Vector4(1f, 0f, 0f, 1f));

        public RetainedGlyphVisual()
        {
            Width = 64f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawGlyphRun(
                _glyphIndices,
                _glyphPositions,
                InterFontFamily.Regular,
                28f,
                _brush,
                Vector2.Zero,
                preferGlyphAtlas: true);
        }
    }

    private sealed class PlacementHostVisual : FrameworkElement
    {
        public TranslatedSceneCacheVisual Child { get; } = new();

        public PlacementHostVisual()
        {
            Width = 64f;
            Height = 64f;
            AddChild(Child);
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            Child.Measure(availableSize);
            return availableSize;
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            Child.Arrange(new Rect(0f, 0f, 64f, 64f));
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
