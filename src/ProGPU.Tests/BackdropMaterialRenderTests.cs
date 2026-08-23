using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class BackdropMaterialRenderTests
{
    [Fact]
    public void AcrylicMaterialComposesLuminosityTintAndRoundedCoverageOnGpu()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(64, 64);
        window.Content = new AcrylicMaterialVisual();

        try
        {
            window.Render();
            var pixels = window.ReadPixels();
            var center = ReadPixel(pixels, window.Width, 32, 32);
            var roundedCorner = ReadPixel(pixels, window.Width, 9, 9);

            Assert.InRange(center.R, 120, 136);
            Assert.InRange(center.G, 0, 8);
            Assert.InRange(center.B, 120, 136);
            Assert.Equal(255, center.A);

            Assert.InRange(roundedCorner.R, 0, 8);
            Assert.InRange(roundedCorner.G, 0, 8);
            Assert.InRange(roundedCorner.B, 0, 8);
            Assert.Equal(255, roundedCorner.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void TextureBackdropUsesGpuBlurSamplingPath()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);
        using var source = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Backdrop Material Source");
        source.WritePixels<byte>(new byte[] { 0, 255, 0, 255 });
        window.Content = new TextureBackdropVisual(source);

        try
        {
            window.Render();
            var center = ReadPixel(window.ReadPixels(), window.Width, 16, 16);

            Assert.InRange(center.R, 0, 8);
            Assert.InRange(center.G, 247, 255);
            Assert.InRange(center.B, 0, 8);
            Assert.Equal(255, center.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void HostBackdropCapturesPreviouslyRenderedTargetContent()
    {
        var window = HeadlessWindow.Shared;
        using var target = new GpuTexture(
            window.Context,
            32,
            32,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment |
                TextureUsage.TextureBinding |
                TextureUsage.CopySrc,
            "Host Backdrop Target",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        var visual = new HostBackdropVisual();

        window.Compositor.RenderOffscreen(
            visual,
            32,
            32,
            target,
            0f,
            1f,
            Vector4.Zero);

        var pixels = target.ReadPixels();
        var outsideLeft = ReadPixel(pixels, target.Width, 4, 16);
        var blurredBoundary = ReadPixel(pixels, target.Width, 16, 12);
        var foreground = ReadPixel(pixels, target.Width, 15, 15);

        Assert.InRange(outsideLeft.R, 247, 255);
        Assert.InRange(outsideLeft.B, 0, 8);
        Assert.InRange(blurredBoundary.R, 24, 224);
        Assert.InRange(blurredBoundary.B, 24, 224);
        Assert.InRange(foreground.R, 0, 8);
        Assert.InRange(foreground.G, 247, 255);
        Assert.InRange(foreground.B, 0, 8);
    }

    [Fact]
    public void AppendTranslatesBackdropMaterialWithoutChangingSourceRect()
    {
        var parameters = new BackdropMaterialParams
        {
            Rect = new Rect(5f, 7f, 20f, 30f),
            SourceRect = new Rect(2f, 3f, 10f, 12f)
        };
        var source = new DrawingContext();
        source.DrawBackdropMaterial(parameters);
        var target = new DrawingContext();

        target.Append(source, new Vector2(11f, 13f));

        var command = Assert.Single(target.Commands);
        Assert.Equal(CompositorBuiltInExtensions.BackdropMaterial, command.ExtensionId);
        var translated = Assert.IsType<BackdropMaterialParams>(command.DataParam);
        Assert.Equal(new Rect(16f, 20f, 20f, 30f), translated.Rect);
        Assert.Equal(parameters.SourceRect, translated.SourceRect);
    }

    [Fact]
    public void WinUiBorderBackgroundUsesSharedBackdropMaterialExtension()
    {
        var border = new Border
        {
            Width = 80f,
            Height = 40f,
            CornerRadius = 6f,
            Background = new AcrylicBrush
            {
                TintColor = new Vector4(0.2f, 0.4f, 0.8f, 0.6f),
                NoiseOpacity = 0.01f
            }
        };
        border.Measure(new Vector2(80f, 40f));
        border.Arrange(new Rect(0f, 0f, 80f, 40f));
        var context = new DrawingContext();

        border.OnRender(context);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawExtension, command.Type);
        Assert.Equal(CompositorBuiltInExtensions.BackdropMaterial, command.ExtensionId);
        var parameters = Assert.IsType<BackdropMaterialParams>(command.DataParam);
        Assert.Equal(BackdropMaterialKind.Acrylic, parameters.Kind);
        Assert.Equal(BackdropMaterialSource.HostBackdrop, parameters.Source);
        Assert.Equal(new Vector4(6f), parameters.CornerRadiiX);
    }

    private static Pixel ReadPixel(byte[] pixels, uint width, int x, int y)
    {
        var index = ((y * (int)width) + x) * 4;
        return new Pixel(
            pixels[index],
            pixels[index + 1],
            pixels[index + 2],
            pixels[index + 3]);
    }

    private readonly record struct Pixel(byte R, byte G, byte B, byte A);

    private sealed class AcrylicMaterialVisual : FrameworkElement
    {
        private readonly SolidColorBrush _background = new(new Vector4(0f, 0f, 0f, 1f));
        private readonly BackdropMaterialBrush _material = new()
        {
            Source = BackdropMaterialSource.None,
            TintColor = new Vector4(1f, 0f, 0f, 0.5f),
            LuminosityColor = new Vector4(0f, 0f, 1f, 1f),
            NoiseOpacity = 0f,
            BlurRadius = 0f,
            Saturation = 1f
        };

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_background, null, new Rect(0f, 0f, 64f, 64f));
            context.DrawRoundedRectangle(_material, null, new Rect(8f, 8f, 48f, 48f), 10f);
        }
    }

    private sealed class TextureBackdropVisual : FrameworkElement
    {
        private readonly GpuTexture _source;
        private readonly SolidColorBrush _background = new(new Vector4(0f, 0f, 0f, 1f));
        private readonly BackdropMaterialBrush _material = new()
        {
            Kind = BackdropMaterialKind.Blur,
            Source = BackdropMaterialSource.Texture,
            NoiseOpacity = 0f,
            BlurRadius = 24f,
            Saturation = 1f
        };

        public TextureBackdropVisual(GpuTexture source)
        {
            _source = source;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_background, null, new Rect(0f, 0f, 32f, 32f));
            context.DrawBackdropMaterial(
                _material,
                new Rect(0f, 0f, 32f, 32f),
                sourceTexture: _source);
        }
    }

    private sealed class HostBackdropVisual : FrameworkElement
    {
        private readonly SolidColorBrush _red = new(new Vector4(1f, 0f, 0f, 1f));
        private readonly SolidColorBrush _blue = new(new Vector4(0f, 0f, 1f, 1f));
        private readonly SolidColorBrush _green = new(new Vector4(0f, 1f, 0f, 1f));
        private readonly BackdropMaterialBrush _material = new()
        {
            Kind = BackdropMaterialKind.Blur,
            Source = BackdropMaterialSource.HostBackdrop,
            NoiseOpacity = 0f,
            BlurRadius = 12f,
            Saturation = 1f
        };

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_red, null, new Rect(0f, 0f, 16f, 32f));
            context.DrawRectangle(_blue, null, new Rect(16f, 0f, 16f, 32f));
            context.DrawBackdropMaterial(_material, new Rect(8f, 8f, 16f, 16f));
            context.DrawRectangle(_green, null, new Rect(14f, 14f, 4f, 4f));
        }
    }
}
