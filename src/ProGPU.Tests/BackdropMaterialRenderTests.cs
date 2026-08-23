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

            Assert.InRange(center.R, 40, 54);
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
    public void HostBackdropReplacesCoverageWithoutCompositingDestinationTwice()
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
            "Host Backdrop Coverage Target",
            alphaMode: GpuTextureAlphaMode.Premultiplied);

        window.Compositor.RenderOffscreen(
            new NestedHostBackdropVisual(),
            32,
            32,
            target,
            0f,
            1f,
            Vector4.Zero);

        var pixels = target.ReadPixels();
        var center = ReadPixel(pixels, target.Width, 16, 16);
        var outsideClip = ReadPixel(pixels, target.Width, 1, 1);

        // 50%-alpha black under 80%-alpha white luminosity produces a
        // premultiplied (0.8, 0.8, 0.8, 0.9) result. Source-over blending it
        // over the captured 50%-alpha destination again would incorrectly
        // raise alpha to 0.95.
        Assert.InRange(center.R, 201, 207);
        Assert.InRange(center.G, 201, 207);
        Assert.InRange(center.B, 201, 207);
        Assert.InRange(center.A, 227, 232);
        Assert.InRange(outsideClip.R, 0, 2);
        Assert.InRange(outsideClip.G, 0, 2);
        Assert.InRange(outsideClip.B, 0, 2);
        Assert.InRange(outsideClip.A, 126, 129);
    }

    [Fact]
    public void ScaledNestedHostBackdropCoversItsFullHeight()
    {
        var window = HeadlessWindow.Shared;
        using var target = new GpuTexture(
            window.Context,
            600,
            900,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment |
                TextureUsage.TextureBinding |
                TextureUsage.CopySrc,
            "Scaled Host Backdrop Target",
            alphaMode: GpuTextureAlphaMode.Premultiplied);

        var visual = new ScaledNestedHostBackdropVisual();
        for (var frame = 0; frame < 6; frame++)
        {
            window.Compositor.RenderOffscreen(
                visual,
                600,
                900,
                target,
                0f,
                1f,
                Vector4.One);
        }

        var pixels = target.ReadPixels();
        foreach (var y in new[] { 300, 500, 700, 850 })
        {
            var covered = ReadPixel(pixels, target.Width, 450, y);
            Assert.InRange(covered.R, 209, 217);
            Assert.InRange(covered.G, 209, 217);
            Assert.InRange(covered.B, 209, 217);
            Assert.Equal(255, covered.A);
        }
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

    private sealed class NestedHostBackdropVisual : FrameworkElement
    {
        private readonly SolidColorBrush _background =
            new(new Vector4(0f, 0f, 0f, 0.5f));
        private readonly BackdropMaterialBrush _material = new()
        {
            Kind = BackdropMaterialKind.Acrylic,
            Source = BackdropMaterialSource.HostBackdrop,
            TintColor = new Vector4(1f, 1f, 1f, 0f),
            LuminosityColor = new Vector4(1f, 1f, 1f, 0.8f),
            NoiseOpacity = 0f,
            BlurRadius = 0f,
            Saturation = 1f
        };

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                _background,
                null,
                new Rect(0f, 0f, 32f, 32f));
            context.PushGeometryClip(
                PrimitivePathGeometry.CreateRoundedRectangle(
                    2f,
                    2f,
                    28f,
                    28f,
                    4f,
                    4f));
            context.PushGeometryClip(
                PrimitivePathGeometry.CreateEllipse(
                    new Vector2(16f, 16f),
                    12f,
                    12f));
            context.PushGeometryClip(
                PrimitivePathGeometry.CreateRoundedRectangle(
                    6f,
                    6f,
                    20f,
                    20f,
                    3f,
                    3f));
            context.DrawBackdropMaterial(
                _material,
                new Rect(0f, 0f, 32f, 32f));
            context.PopGeometryClip();
            context.PopGeometryClip();
            context.PopGeometryClip();
        }
    }

    private sealed class ScaledNestedHostBackdropVisual : FrameworkElement
    {
        private readonly SolidColorBrush _white =
            new(Vector4.One);
        private readonly SolidColorBrush _black =
            new(new Vector4(0f, 0f, 0f, 1f));
        private readonly BackdropMaterialBrush _material = new()
        {
            Kind = BackdropMaterialKind.Acrylic,
            Source = BackdropMaterialSource.HostBackdrop,
            TintColor = new Vector4(0.988f, 0.988f, 0.988f, 0f),
            LuminosityColor = new Vector4(0.988f, 0.988f, 0.988f, 0.847f),
            NoiseOpacity = 0f,
            BlurRadius = 0f,
            Saturation = 1f
        };
        private readonly Matrix4x4 _outerTransform =
            Matrix4x4.CreateScale(2f, 2f, 1f) *
            Matrix4x4.CreateTranslation(8f, 210f, 0f);
        private readonly Matrix4x4 _innerTransform =
            Matrix4x4.CreateTranslation(1f, 1f, 0f);
        private readonly GpuPicture _popup;

        public ScaledNestedHostBackdropVisual()
        {
            var recorder = new GpuPictureRecorder();
            var popup = recorder.BeginRecording(
                new Rect(0f, 0f, 600f, 900f));
            popup.PushGeometryClip(
                PrimitivePathGeometry.CreateRoundedRectangle(
                    1f,
                    1f,
                    270f,
                    324f,
                    7.5f,
                    7.5f),
                Matrix4x4.Identity);
            popup.PushGeometryClip(
                PrimitivePathGeometry.CreateRoundedRectangle(
                    0f,
                    0f,
                    270f,
                    324f,
                    7.5f,
                    7.5f),
                _innerTransform);
            popup.DrawBackdropMaterial(
                _material,
                new Rect(1f, 1f, 269f, 323f));
            popup.PopGeometryClip();
            popup.PopGeometryClip();
            _popup = recorder.EndRecording();
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                _white,
                null,
                new Rect(0f, 0f, 600f, 900f));
            foreach (var y in new[] { 300f, 500f, 700f, 850f })
            {
                context.DrawRectangle(
                    _black,
                    null,
                    new Rect(400f, y - 4f, 100f, 8f));
            }

            context.PushGeometryClip(
                PrimitivePathGeometry.CreateRoundedRectangle(
                    0f,
                    0f,
                    272f,
                    326f,
                    8.5f,
                    8.5f),
                _outerTransform);
            context.DrawPictureTransformed(_popup, _outerTransform);
            context.PopGeometryClip();
        }
    }
}
