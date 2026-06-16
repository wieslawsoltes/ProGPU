using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
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

        var textures = GetEffectTextures(window.Compositor);
        var cached = Assert.Single(textures);
        Assert.Same(visual, cached.Key);
        Assert.Equal(24u, cached.Value.Source.Width);
        Assert.Equal(16u, cached.Value.Source.Height);
        Assert.Equal(24u, cached.Value.Temp.Width);
        Assert.Equal(16u, cached.Value.Temp.Height);
        Assert.Equal(24u, cached.Value.Destination.Width);
        Assert.Equal(16u, cached.Value.Destination.Height);
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

    private static Dictionary<Visual, (GpuTexture Source, GpuTexture Temp, GpuTexture Destination)> GetEffectTextures(Compositor compositor)
    {
        var field = typeof(Compositor).GetField("_effectTextures", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<Dictionary<Visual, (GpuTexture Source, GpuTexture Temp, GpuTexture Destination)>>(field.GetValue(compositor));
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
}
