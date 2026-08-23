using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class VisualEffectRenderTests
{
    [Fact]
    public void VisualEffectCompositeAppliesVisualOpacityAndClip()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(100, 60);
        window.Content = new VisualCompositeScopeHost(new ClippedOpacityEffectVisual());

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
    public void VisualEffectCacheUsesPhysicalTextureSizeForDpiScale()
    {
        using var window = new HeadlessWindow(24, 16);
        using var target = new GpuTexture(
            window.Context,
            24,
            16,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Visual Effect DPI Cache Test Target");
        var visual = new DrawingVisual
        {
            Size = new Vector2(12f, 8f),
            Effect = new BlurEffect(0f)
        };
        visual.Context.DrawRectangle(
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            pen: null,
            new Rect(0f, 0f, 12f, 8f));

        window.Compositor.RenderOffscreen(
            visual,
            width: 12,
            height: 8,
            targetTexture: target,
            padding: 0f,
            dpiScale: 2f);

        Assert.Equal(24u * 16u * 4u, window.Compositor.Metrics.EffectTextureBytes);
    }

    [Fact]
    public void BlurredVisualRetainsOnlyRequiredThreeColorSurfaces()
    {
        using var window = new HeadlessWindow(40, 32);
        using var target = new GpuTexture(
            window.Context,
            40,
            32,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Blur Effect Residency Test Target");
        var visual = CreateEffectVisual(new BlurEffect(4f));

        window.Compositor.RenderOffscreen(
            visual,
            width: 40,
            height: 32,
            targetTexture: target,
            padding: 0f,
            dpiScale: 1f);

        const ulong paddedPixels = 28u * 24u;
        Assert.Equal(paddedPixels * 4u * 3u, window.Compositor.Metrics.EffectTextureBytes);
    }

    [Fact]
    public void SharpShadowDoesNotRetainBlurTemporary()
    {
        using var window = new HeadlessWindow(24, 16);
        using var target = new GpuTexture(
            window.Context,
            24,
            16,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Sharp Shadow Residency Test Target");
        var visual = CreateEffectVisual(new DropShadowEffect(0f));

        window.Compositor.RenderOffscreen(
            visual,
            width: 12,
            height: 8,
            targetTexture: target,
            padding: 0f,
            dpiScale: 1f);

        const ulong sourcePixels = 12u * 8u;
        Assert.Equal(sourcePixels * 4u * 2u, window.Compositor.Metrics.EffectTextureBytes);
    }

    [Fact]
    public void BlurredShadowPacksFourIntermediateCoveragesPerTexel()
    {
        using var window = new HeadlessWindow(40, 32);
        using var target = new GpuTexture(
            window.Context,
            40,
            32,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Blurred Shadow Residency Test Target");
        var visual = CreateEffectVisual(new DropShadowEffect(4f));

        window.Compositor.RenderOffscreen(
            visual,
            width: 40,
            height: 32,
            targetTexture: target,
            padding: 0f,
            dpiScale: 1f);

        const ulong paddedWidth = 28u;
        const ulong paddedHeight = 24u;
        const ulong sourceAndDestinationBytes = paddedWidth * paddedHeight * 8u;
        const ulong packedTemporaryBytes = ((paddedWidth + 3u) / 4u) * paddedHeight * 4u;
        Assert.Equal(
            sourceAndDestinationBytes + packedTemporaryBytes,
            window.Compositor.Metrics.EffectTextureBytes);
    }

    [Fact]
    public void VisualEffectHitTestCachePreservesDescendantOwners()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(100, 60);
        window.Content = new VisualCompositeScopeHost(new HitTestEffectVisual());

        try
        {
            window.Render();

            var index = window.Compositor.LastHitTestIndex;
            Assert.NotNull(index);
            var childPrimitive = Assert.Single(index!.Primitives, primitive => primitive.Id == 994);
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
    public void VisualEffectCapturesExplicitTranslatedContentBounds()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(100, 60);
        window.Content = new ExplicitEffectBoundsVisual();

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var content = ReadPixel(pixels, window.Width, x: 35, y: 25);
            var outside = ReadPixel(pixels, window.Width, x: 15, y: 25);

            Assert.InRange(content.R, 245, 255);
            Assert.InRange(content.G, 0, 10);
            Assert.InRange(content.B, 0, 10);
            Assert.InRange(outside.R, 0, 40);
            Assert.InRange(outside.G, 0, 40);
            Assert.InRange(outside.B, 0, 40);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DropShadowPreservesExplicitContentBoundsPosition()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(100, 60);
        window.Content = new ExplicitShadowBoundsVisual();

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var content = ReadPixel(pixels, window.Width, x: 44, y: 24);
            var shadow = ReadPixel(pixels, window.Width, x: 56, y: 24);
            var staleOrigin = ReadPixel(pixels, window.Width, x: 12, y: 4);

            Assert.InRange(content.R, 245, 255);
            Assert.InRange(content.G, 0, 10);
            Assert.InRange(content.B, 0, 10);
            Assert.InRange(shadow.R, 0, 10);
            Assert.InRange(shadow.G, 0, 10);
            Assert.InRange(shadow.B, 245, 255);
            Assert.InRange(staleOrigin.R, 0, 50);
            Assert.InRange(staleOrigin.G, 0, 50);
            Assert.InRange(staleOrigin.B, 0, 50);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DropShadowCanRenderWithoutCompositingItsSource()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(100, 60);
        window.Content = new ExplicitShadowBoundsVisual(drawSource: false);

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var omittedSource = ReadPixel(pixels, window.Width, x: 44, y: 24);
            var shadow = ReadPixel(pixels, window.Width, x: 56, y: 24);

            Assert.InRange(omittedSource.R, 0, 50);
            Assert.InRange(omittedSource.G, 0, 50);
            Assert.InRange(omittedSource.B, 0, 50);
            Assert.InRange(shadow.R, 0, 10);
            Assert.InRange(shadow.G, 0, 10);
            Assert.InRange(shadow.B, 245, 255);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DetachedEffectTexturesDoNotBlockTheNextSceneCache()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(100, 60);
        window.Content = new ExplicitShadowBoundsVisual();

        try
        {
            window.Render();
            Assert.True(window.Compositor.Metrics.EffectTextureBytes > 0);

            window.Content = new PlainBoundsVisual();
            window.Render();
            Assert.False(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal(0UL, window.Compositor.Metrics.EffectTextureBytes);

            window.Render();
            Assert.True(window.Compositor.Metrics.SceneCacheHit);
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

    private static void AssertHalfRed(RgbaPixel pixel)
    {
        Assert.InRange(pixel.R, 115, 140);
        Assert.InRange(pixel.G, 0, 12);
        Assert.InRange(pixel.B, 0, 12);
        Assert.Equal(255, pixel.A);
    }

    private static void AssertBlack(RgbaPixel pixel)
    {
        Assert.InRange(pixel.R, 0, 12);
        Assert.InRange(pixel.G, 0, 12);
        Assert.InRange(pixel.B, 0, 12);
        Assert.Equal(255, pixel.A);
    }

    private static DrawingVisual CreateEffectVisual(EffectBase effect)
    {
        var visual = new DrawingVisual
        {
            Size = new Vector2(12f, 8f),
            Effect = effect
        };
        visual.Context.DrawRectangle(
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            pen: null,
            new Rect(0f, 0f, 12f, 8f));
        return visual;
    }

    private readonly record struct RgbaPixel(byte R, byte G, byte B, byte A);

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

    private sealed class ClippedOpacityEffectVisual : FrameworkElement
    {
        private readonly SolidColorBrush _red = new(new Vector4(1f, 0f, 0f, 1f));

        public ClippedOpacityEffectVisual()
        {
            Width = 80f;
            Height = 50f;
            Effect = new BlurEffect(0f);
            Opacity = 0.5f;
            ClipBounds = new Rect(0f, 0f, 40f, 50f);
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_red, null, new Rect(0f, 0f, 80f, 50f));
        }
    }

    private sealed class HitTestEffectVisual : FrameworkElement
    {
        private readonly FrameworkElement _child;

        public HitTestEffectVisual()
        {
            Width = 80f;
            Height = 50f;
            Effect = new DropShadowEffect(4f);
            HitTestId = 993;

            _child = new FrameworkElement
            {
                Width = 30f,
                Height = 20f,
                HitTestId = 994
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
    }

    private sealed class ExplicitEffectBoundsVisual : FrameworkElement
    {
        private readonly SolidColorBrush _red =
            new(new Vector4(1f, 0f, 0f, 1f));

        public ExplicitEffectBoundsVisual()
        {
            Effect = new BlurEffect(0f);
            EffectContentBounds = new Rect(25f, 15f, 30f, 20f);
            EffectRasterPadding = 0f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                _red,
                null,
                new Rect(25f, 15f, 30f, 20f));
        }
    }

    private sealed class ExplicitShadowBoundsVisual : FrameworkElement
    {
        private readonly SolidColorBrush _red =
            new(new Vector4(1f, 0f, 0f, 1f));

        public ExplicitShadowBoundsVisual(bool drawSource = true)
        {
            Effect = new DropShadowEffect(0f)
            {
                Offset = new Vector2(8f, 0f),
                Color = new Vector4(0f, 0f, 1f, 1f),
                DrawSource = drawSource
            };
            EffectContentBounds = new Rect(40f, 20f, 12f, 8f);
            EffectRasterPadding = 0f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                _red,
                null,
                new Rect(40f, 20f, 12f, 8f));
        }
    }

    private sealed class PlainBoundsVisual : FrameworkElement
    {
        private readonly SolidColorBrush _green =
            new(new Vector4(0f, 1f, 0f, 1f));

        public PlainBoundsVisual()
        {
            Width = 100f;
            Height = 60f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                _green,
                null,
                new Rect(20f, 10f, 60f, 40f));
        }
    }
}
