using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class ImageEffectRenderTests
{
    [Fact]
    public void DrawImageWithEffectHonorsExplicitMaskTexture()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(160, 90);

        using var source = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Image Effect Source");
        source.WritePixels(new byte[] { 255, 0, 0, 255 });

        using var blackMask = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Image Effect Explicit Black Mask");
        blackMask.WritePixels(new byte[] { 0, 0, 0, 255 });

        window.Content = new ExplicitMaskImageEffectVisual(source, blackMask);

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var unmasked = ReadPixel(pixels, window.Width, x: 40, y: 45);

            Assert.True(unmasked.R >= 220, $"Expected unmasked image effect to render red, found {unmasked}.");
            Assert.True(unmasked.G <= 35, $"Expected unmasked image effect to keep green low, found {unmasked}.");
            Assert.True(unmasked.B <= 35, $"Expected unmasked image effect to keep blue low, found {unmasked}.");
            Assert.Equal(255, unmasked.A);

            window.Content = new ExplicitMaskImageEffectVisual(source, blackMask, useExplicitMask: true);
            window.Render();

            pixels = window.ReadPixels();
            var masked = ReadPixel(pixels, window.Width, x: 40, y: 45);

            Assert.InRange(masked.R, 10, 35);
            Assert.InRange(masked.G, 10, 35);
            Assert.InRange(masked.B, 20, 45);
            Assert.Equal(255, masked.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawImageWithEffectPreservesTextureCoordinatesWhenClipped()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(160, 90);

        using var source = new GpuTexture(
            window.Context,
            2,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Image Effect Clip Source");
        source.WritePixels(new byte[]
        {
            255, 0, 0, 255,
            0, 255, 0, 255
        });

        window.Content = new ClippedImageEffectVisual(source);

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var clippedMiddle = ReadPixel(pixels, window.Width, x: 80, y: 45);

            Assert.True(clippedMiddle.G >= 180, $"Expected clipped image effect to preserve right-half green UVs, found {clippedMiddle}.");
            Assert.True(clippedMiddle.R <= 80, $"Expected clipped image effect not to stretch left red UVs, found {clippedMiddle}.");
            Assert.True(clippedMiddle.B <= 35, $"Expected clipped image effect to keep blue low, found {clippedMiddle}.");
            Assert.Equal(255, clippedMiddle.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawImageWithEffectPreservesPremultipliedSourceAlpha()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);

        using var source = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Image Effect Premultiplied Source",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        source.WritePixels<byte>(new byte[] { 128, 0, 0, 128 });

        window.Content = new PremultipliedImageEffectVisual(source);

        try
        {
            window.Render();

            var pixel = ReadPixel(window.ReadPixels(), window.Width, x: 16, y: 16);

            Assert.InRange(pixel.R, 120, 136);
            Assert.InRange(pixel.G, 0, 8);
            Assert.InRange(pixel.B, 0, 8);
            Assert.Equal(255, pixel.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawImageWithEffectHonorsActiveBlendMode()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);

        using var source = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Image Effect Blend Source");
        source.WritePixels<byte>(new byte[] { 255, 0, 0, 255 });

        window.Content = new ClearBlendImageEffectVisual(source);

        try
        {
            window.Render();

            var pixel = ReadPixel(window.ReadPixels(), window.Width, x: 16, y: 16);

            Assert.InRange(pixel.R, 0, 8);
            Assert.InRange(pixel.G, 0, 8);
            Assert.InRange(pixel.B, 0, 8);
            Assert.InRange(pixel.A, 0, 8);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawImageWithEffectPremultipliesStraightSourceForScreenBlend()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);

        using var source = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Image Effect Screen Straight Source",
            alphaMode: GpuTextureAlphaMode.Straight);
        source.WritePixels<byte>(new byte[] { 255, 0, 0, 128 });

        window.Content = new ScreenBlendImageEffectVisual(source);

        try
        {
            window.Render();

            var pixel = ReadPixel(window.ReadPixels(), window.Width, x: 16, y: 16);

            Assert.InRange(pixel.R, 120, 136);
            Assert.InRange(pixel.G, 0, 8);
            Assert.InRange(pixel.B, 0, 8);
            Assert.Equal(255, pixel.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawImageWithEffectAppliesOpacityOnceForStraightAlphaSource()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);

        using var source = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Image Effect Straight Opacity Source",
            alphaMode: GpuTextureAlphaMode.Straight);
        source.WritePixels<byte>(new byte[] { 255, 0, 0, 255 });

        window.Content = new StraightOpacityImageEffectVisual(source);

        try
        {
            window.Render();

            var pixel = ReadPixel(window.ReadPixels(), window.Width, x: 16, y: 16);

            Assert.InRange(pixel.R, 120, 136);
            Assert.InRange(pixel.G, 0, 8);
            Assert.InRange(pixel.B, 0, 8);
            Assert.Equal(255, pixel.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawImageWithEffectAppliesColorEffectsInStraightColorSpace()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);

        using var source = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Image Effect Premultiplied Invert Source",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        source.WritePixels<byte>(new byte[] { 128, 0, 0, 128 });

        window.Content = new PremultipliedInvertImageEffectVisual(source);

        try
        {
            window.Render();

            var pixel = ReadPixel(window.ReadPixels(), window.Width, x: 16, y: 16);

            Assert.InRange(pixel.R, 0, 8);
            Assert.InRange(pixel.G, 120, 136);
            Assert.InRange(pixel.B, 120, 136);
            Assert.Equal(255, pixel.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawImageWithEffectAppliesColorMatrixToSelectedSourceRect()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);

        using var source = new GpuTexture(
            window.Context,
            2,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Image Effect Color Matrix Source");
        source.WritePixels<byte>(new byte[]
        {
            255, 0, 0, 255,
            0, 255, 0, 255
        });

        window.Content = new ColorMatrixSourceRectImageEffectVisual(source);

        try
        {
            window.Render();

            var pixel = ReadPixel(window.ReadPixels(), window.Width, x: 16, y: 16);

            Assert.InRange(pixel.R, 0, 8);
            Assert.InRange(pixel.G, 0, 8);
            Assert.InRange(pixel.B, 220, 255);
            Assert.Equal(255, pixel.A);
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

    private readonly record struct RgbaPixel(byte R, byte G, byte B, byte A);

    private sealed class ExplicitMaskImageEffectVisual : FrameworkElement
    {
        private readonly GpuTexture _source;
        private readonly GpuTexture _mask;
        private readonly bool _useExplicitMask;

        public ExplicitMaskImageEffectVisual(GpuTexture source, GpuTexture mask, bool useExplicitMask = false)
        {
            _source = source;
            _mask = mask;
            _useExplicitMask = useExplicitMask;
            Width = 160f;
            Height = 90f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawImageWithEffect(
                _source,
                new Rect(20f, 25f, 40f, 40f),
                maskTexture: _useExplicitMask ? _mask : null);
        }
    }

    private sealed class ClippedImageEffectVisual : FrameworkElement
    {
        private readonly GpuTexture _source;

        public ClippedImageEffectVisual(GpuTexture source)
        {
            _source = source;
            Width = 160f;
            Height = 90f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.PushClip(new Rect(60f, 25f, 40f, 40f));
            context.DrawImageWithEffect(
                _source,
                new Rect(20f, 25f, 80f, 40f));
            context.PopClip();
        }
    }

    private sealed class PremultipliedImageEffectVisual : FrameworkElement
    {
        private readonly GpuTexture _source;

        public PremultipliedImageEffectVisual(GpuTexture source)
        {
            _source = source;
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new System.Numerics.Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 32f, 32f));
            context.DrawImageWithEffect(_source, new Rect(0f, 0f, 32f, 32f));
        }
    }

    private sealed class ClearBlendImageEffectVisual : FrameworkElement
    {
        private readonly GpuTexture _source;

        public ClearBlendImageEffectVisual(GpuTexture source)
        {
            _source = source;
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new System.Numerics.Vector4(0f, 0f, 1f, 1f)),
                null,
                new Rect(0f, 0f, 32f, 32f));
            context.PushBlendMode(GpuBlendMode.Clear);
            context.DrawImageWithEffect(_source, new Rect(0f, 0f, 32f, 32f));
            context.PopBlendMode();
        }
    }

    private sealed class ScreenBlendImageEffectVisual : FrameworkElement
    {
        private readonly GpuTexture _source;

        public ScreenBlendImageEffectVisual(GpuTexture source)
        {
            _source = source;
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new System.Numerics.Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 32f, 32f));
            context.PushBlendMode(GpuBlendMode.Screen);
            context.DrawImageWithEffect(_source, new Rect(0f, 0f, 32f, 32f));
            context.PopBlendMode();
        }
    }

    private sealed class StraightOpacityImageEffectVisual : FrameworkElement
    {
        private readonly GpuTexture _source;

        public StraightOpacityImageEffectVisual(GpuTexture source)
        {
            _source = source;
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new System.Numerics.Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 32f, 32f));
            context.PushOpacity(0.5f);
            context.DrawImageWithEffect(_source, new Rect(0f, 0f, 32f, 32f));
            context.PopOpacity();
        }
    }

    private sealed class PremultipliedInvertImageEffectVisual : FrameworkElement
    {
        private readonly GpuTexture _source;

        public PremultipliedInvertImageEffectVisual(GpuTexture source)
        {
            _source = source;
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new System.Numerics.Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 32f, 32f));
            context.DrawImageWithEffect(_source, new Rect(0f, 0f, 32f, 32f), invert: 1f);
        }
    }

    private sealed class ColorMatrixSourceRectImageEffectVisual : FrameworkElement
    {
        private static readonly ImageEffectColorMatrix s_greenToBlue = new(
            red: new System.Numerics.Vector4(0f, 0f, 0f, 0f),
            green: new System.Numerics.Vector4(0f, 0f, 0f, 0f),
            blue: new System.Numerics.Vector4(0f, 1f, 0f, 0f),
            alpha: new System.Numerics.Vector4(0f, 0f, 0f, 1f),
            offset: System.Numerics.Vector4.Zero);

        private readonly GpuTexture _source;

        public ColorMatrixSourceRectImageEffectVisual(GpuTexture source)
        {
            _source = source;
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new System.Numerics.Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 32f, 32f));
            context.DrawImageWithEffect(
                _source,
                new Rect(0f, 0f, 32f, 32f),
                sourceRect: new Rect(1f, 0f, 1f, 1f),
                colorMatrix: s_greenToBlue);
        }
    }
}
