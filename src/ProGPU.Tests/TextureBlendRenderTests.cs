using System;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class TextureBlendRenderTests
{
    [Fact]
    public void StraightAlphaTextureUsesSourceAlphaForColorBlend()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);
        using var texture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Straight Alpha Texture Blend Test",
            alphaMode: GpuTextureAlphaMode.Straight);
        texture.WritePixels<byte>(new byte[] { 200, 80, 20, 128 });
        window.Content = new TextureBlendVisual(texture);

        try
        {
            window.Render();

            var pixel = ReadPixel(window.ReadPixels(), window.Width, x: 16, y: 16);

            Assert.InRange(pixel.R, 95, 105);
            Assert.InRange(pixel.G, 35, 45);
            Assert.InRange(pixel.B, 5, 15);
            Assert.Equal(255, pixel.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void StraightAlphaTextureAppliesOpacityMaskOnce()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);
        using var texture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Straight Alpha Texture Mask Test",
            alphaMode: GpuTextureAlphaMode.Straight);
        texture.WritePixels<byte>(new byte[] { 200, 80, 20, 255 });
        window.Content = new StraightAlphaOpacityMaskTextureVisual(texture);

        try
        {
            window.Render();

            var pixel = ReadPixel(window.ReadPixels(), window.Width, x: 16, y: 16);

            Assert.InRange(pixel.R, 95, 105);
            Assert.InRange(pixel.G, 35, 45);
            Assert.InRange(pixel.B, 5, 15);
            Assert.Equal(255, pixel.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void PremultipliedTextureScalesRgbWhenOpacityIsApplied()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);
        using var texture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Premultiplied Texture Opacity Test",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        texture.WritePixels<byte>(new byte[] { 128, 0, 0, 128 });
        window.Content = new PremultipliedOpacityTextureVisual(texture);

        try
        {
            window.Render();

            var pixel = ReadPixel(window.ReadPixels(), window.Width, x: 16, y: 16);

            Assert.InRange(pixel.R, 58, 70);
            Assert.InRange(pixel.G, 0, 8);
            Assert.InRange(pixel.B, 0, 8);
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

    private sealed class TextureBlendVisual : FrameworkElement
    {
        private readonly GpuTexture _texture;

        public TextureBlendVisual(GpuTexture texture)
        {
            _texture = texture;
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 32f, 32f));
            context.DrawTexture(_texture, new Rect(0f, 0f, 32f, 32f));
        }
    }

    private sealed class StraightAlphaOpacityMaskTextureVisual : FrameworkElement
    {
        private readonly GpuTexture _texture;

        public StraightAlphaOpacityMaskTextureVisual(GpuTexture texture)
        {
            _texture = texture;
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 32f, 32f));
            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(0.5f, 0f, 0f, 1f)),
                new Rect(0f, 0f, 32f, 32f));
            context.DrawTexture(_texture, new Rect(0f, 0f, 32f, 32f));
            context.PopOpacityMask();
        }
    }

    private sealed class PremultipliedOpacityTextureVisual : FrameworkElement
    {
        private readonly GpuTexture _texture;

        public PremultipliedOpacityTextureVisual(GpuTexture texture)
        {
            _texture = texture;
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 32f, 32f));
            context.PushOpacity(0.5f);
            context.DrawTexture(_texture, new Rect(0f, 0f, 32f, 32f));
            context.PopOpacity();
        }
    }
}
