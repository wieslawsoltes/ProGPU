using System;
using System.Collections.Generic;
using System.Linq;
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

public sealed class TextureBlendRenderTests
{
    [Fact]
    public void AdvancedBlendSourceTextureUsesClippedDrawBounds()
    {
        using var window = new HeadlessWindow(128, 96);
        using var target = new GpuTexture(
            window.Context,
            128,
            96,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.CopySrc,
            "Bounded advanced blend test target");
        using var source = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Bounded advanced blend source input",
            alphaMode: GpuTextureAlphaMode.Straight);
        source.WritePixels<byte>([51, 204, 153, 255]);

        var visual = new DrawingVisual { Size = new Vector2(128f, 96f) };
        visual.Context.DrawRectangle(
            new SolidColorBrush(new Vector4(0.8f, 0.4f, 0.2f, 1f)),
            null,
            new Rect(0f, 0f, 128f, 96f));
        visual.Context.PushClip(new Rect(30f, 20f, 20f, 12f));
        visual.Context.PushBlendMode(GpuBlendMode.Difference);
        visual.Context.DrawTexture(
            source,
            new Rect(0f, 0f, 32f, 24f),
            default,
            Matrix4x4.CreateTranslation(24f, 12f, 0f));
        visual.Context.PopBlendMode();
        visual.Context.PopClip();

        window.Compositor.RenderOffscreen(
            visual,
            width: 128,
            height: 96,
            targetTexture: target,
            padding: 0f,
            dpiScale: 1f);

        var pixels = target.ReadPixels();
        var blended = ReadPixel(pixels, target.Width, 40, 26);
        var clipped = ReadPixel(pixels, target.Width, 25, 18);
        Assert.InRange(blended.R, 148, 158);
        Assert.InRange(blended.G, 97, 107);
        Assert.InRange(blended.B, 97, 107);
        Assert.InRange(clipped.R, 199, 209);
        Assert.InRange(clipped.G, 97, 107);
        Assert.InRange(clipped.B, 46, 56);

        Assert.Equal(128UL * 96UL * 4UL, window.Compositor.Metrics.AdvancedBlendScratchTextureBytes);
        Assert.Equal(20UL * 12UL * 4UL, window.Compositor.Metrics.AdvancedBlendSourceTextureBytes);
        Assert.Equal(
            (128UL * 96UL + 20UL * 12UL) * 4UL,
            window.Compositor.Metrics.AdvancedBlendTextureBytes);
    }

    [Fact]
    public void BoundedAdvancedBlendPreservesGlobalOpacityMaskCoordinates()
    {
        using var window = new HeadlessWindow(96, 64);
        using var target = new GpuTexture(
            window.Context,
            96,
            64,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.CopySrc,
            "Masked bounded advanced blend target");
        using var source = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Masked bounded advanced blend source",
            alphaMode: GpuTextureAlphaMode.Straight);
        source.WritePixels<byte>([255, 255, 255, 255]);

        var visual = new DrawingVisual { Size = new Vector2(96f, 64f) };
        visual.Context.DrawRectangle(
            new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
            null,
            new Rect(0f, 0f, 96f, 64f));
        visual.Context.PushOpacityMask(
            new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)) { Opacity = 0.5f },
            new Rect(24f, 16f, 32f, 24f));
        visual.Context.PushBlendMode(GpuBlendMode.Screen);
        visual.Context.DrawTexture(source, new Rect(24f, 16f, 32f, 24f));
        visual.Context.PopBlendMode();
        visual.Context.PopOpacityMask();

        window.Compositor.RenderOffscreen(
            visual,
            width: 96,
            height: 64,
            targetTexture: target,
            padding: 0f,
            dpiScale: 1f);

        var pixels = target.ReadPixels();
        var masked = ReadPixel(pixels, target.Width, 40, 28);
        var outside = ReadPixel(pixels, target.Width, 12, 12);
        Assert.InRange(masked.R, 122, 133);
        Assert.InRange(masked.G, 122, 133);
        Assert.InRange(masked.B, 122, 133);
        Assert.Equal(new RgbaPixel(0, 0, 0, 255), outside);
        Assert.Equal(32UL * 24UL * 4UL, window.Compositor.Metrics.AdvancedBlendSourceTextureBytes);
    }

    [Fact]
    public void BoundedAdvancedBlendUsesPhysicalSourceBoundsAtRetinaScale()
    {
        using var window = new HeadlessWindow(128, 96);
        using var target = new GpuTexture(
            window.Context,
            128,
            96,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.CopySrc,
            "Retina bounded advanced blend target");
        using var source = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Retina bounded advanced blend source",
            alphaMode: GpuTextureAlphaMode.Straight);
        source.WritePixels<byte>([255, 255, 255, 255]);

        var visual = new DrawingVisual { Size = new Vector2(64f, 48f) };
        visual.Context.DrawRectangle(
            new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
            null,
            new Rect(0f, 0f, 64f, 48f));
        visual.Context.PushBlendMode(GpuBlendMode.Screen);
        visual.Context.DrawTexture(source, new Rect(10f, 8f, 12f, 10f));
        visual.Context.PopBlendMode();

        window.Compositor.RenderOffscreen(
            visual,
            width: 64,
            height: 48,
            targetTexture: target,
            padding: 0f,
            dpiScale: 2f);

        var pixels = target.ReadPixels();
        Assert.Equal(new RgbaPixel(255, 255, 255, 255), ReadPixel(pixels, target.Width, 32, 26));
        Assert.Equal(new RgbaPixel(0, 0, 0, 255), ReadPixel(pixels, target.Width, 12, 12));
        Assert.Equal(24UL * 20UL * 4UL, window.Compositor.Metrics.AdvancedBlendSourceTextureBytes);
    }

    [Fact]
    public void TextureShaderEvaluatesMitchellNetravaliCoefficients()
    {
        Assert.Contains("fn cubic_weight(x: f32, b: f32, c: f32)", Shaders.TextureShader);
        Assert.Contains("fn address_texture_coordinates(", Shaders.TextureShader);
        Assert.Contains(
            "sample_bicubic(\n            addressedTexCoord,",
            Shaders.TextureShader);
        Assert.Contains("12.0 - 9.0 * b - 6.0 * c", Shaders.TextureShader);
        Assert.Contains("if (b == 0.0 && c == 0.5)", Shaders.TextureShader);

        var window = HeadlessWindow.Shared;
        window.Resize(64, 32);
        using var texture = new GpuTexture(
            window.Context,
            4,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Cubic Resampler Coefficient Test",
            alphaMode: GpuTextureAlphaMode.Straight);
        texture.WritePixels<byte>(
        [
            0, 0, 0, 255,
            255, 255, 255, 255,
            0, 0, 0, 255,
            255, 255, 255, 255
        ]);
        window.Content = new CubicResamplerVisual(texture);

        try
        {
            window.Render();

            var vertices = GetTextureVertices(window.Compositor);
            var drawVertices = vertices.Skip(vertices.Count - 8).Take(8).ToArray();
            Assert.All(drawVertices[..4], vertex => Assert.Equal(new Vector2(1f / 3f), vertex.ShapeSize));
            Assert.All(drawVertices[4..], vertex => Assert.Equal(new Vector2(0f, 0.5f), vertex.ShapeSize));

            var pixels = window.ReadPixels();
            var totalDifference = 0;
            for (var x = 0; x < 32; x++)
            {
                var mitchell = ReadPixel(pixels, window.Width, x, 16);
                var catmullRom = ReadPixel(pixels, window.Width, x + 32, 16);
                totalDifference += Math.Abs(mitchell.R - catmullRom.R);
            }

            Assert.True(totalDifference > 100, $"Expected distinct cubic kernels, total channel difference was {totalDifference}.");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DefaultUploadedTextureUsesSourceAlphaForColorBlend()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);
        using var texture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Default Straight Alpha Texture Blend Test");
        Assert.Equal(GpuTextureAlphaMode.Straight, texture.AlphaMode);

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

    [Fact]
    public void ClippedTextureCommandUploadsTrimmedQuadWithPreservedSourceRectUv()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(40, 20);
        using var texture = new GpuTexture(
            window.Context,
            4,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Clipped Texture Source Rect Test",
            alphaMode: GpuTextureAlphaMode.Straight);
        texture.WritePixels<byte>(
        [
            0, 0, 255, 255,
            255, 0, 0, 255,
            0, 255, 0, 255,
            255, 255, 0, 255
        ]);
        window.Content = new ClippedSourceRectTextureVisual(texture);

        try
        {
            window.Render();

            var textureVertices = GetTextureVertices(window.Compositor);
            var drawVertices = textureVertices.Skip(textureVertices.Count - 4).Take(4).ToArray();

            Assert.All(drawVertices, vertex => Assert.InRange(vertex.Position.X, 20f, 40f));
            Assert.All(drawVertices, vertex => Assert.InRange(vertex.Position.Y, 0f, 20f));
            Assert.Equal(0.5f, drawVertices.Min(static vertex => vertex.TexCoord.X), 3);
            Assert.Equal(0.75f, drawVertices.Max(static vertex => vertex.TexCoord.X), 3);

            var pixels = window.ReadPixels();
            var outside = ReadPixel(pixels, window.Width, x: 10, y: 10);
            var inside = ReadPixel(pixels, window.Width, x: 30, y: 10);

            Assert.Equal(new RgbaPixel(0, 0, 0, 255), outside);
            Assert.True(
                inside.G > 180 && inside.R < 80 && inside.B < 80,
                $"Expected preserved source-rect UVs to sample the green texel, got RGBA({inside.R}, {inside.G}, {inside.B}, {inside.A}).");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void MirroredClippedTextureCommandUploadsTrimmedQuadWithPreservedUv()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(40, 20);
        using var texture = new GpuTexture(
            window.Context,
            2,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Mirrored Clipped Texture Test",
            alphaMode: GpuTextureAlphaMode.Straight);
        texture.WritePixels<byte>(
        [
            255, 0, 0, 255,
            0, 255, 0, 255
        ]);
        window.Content = new MirroredClippedTextureVisual(texture);

        try
        {
            window.Render();

            var textureVertices = GetTextureVertices(window.Compositor);
            var drawVertices = textureVertices.Skip(textureVertices.Count - 4).Take(4).ToArray();

            Assert.All(drawVertices, vertex => Assert.InRange(vertex.Position.X, 20f, 40f));
            Assert.All(drawVertices, vertex => Assert.InRange(vertex.Position.Y, 0f, 20f));
            Assert.Equal(0f, drawVertices.Min(static vertex => vertex.TexCoord.X), 3);
            Assert.Equal(0.5f, drawVertices.Max(static vertex => vertex.TexCoord.X), 3);

            var pixels = window.ReadPixels();
            var outside = ReadPixel(pixels, window.Width, x: 10, y: 10);
            var inside = ReadPixel(pixels, window.Width, x: 30, y: 10);

            Assert.Equal(new RgbaPixel(0, 0, 0, 255), outside);
            Assert.True(
                inside.R > 180 && inside.G < 80 && inside.B < 80,
                $"Expected mirrored clipped UVs to sample the red texel, got RGBA({inside.R}, {inside.G}, {inside.B}, {inside.A}).");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void VerticallyMirroredClippedTextureCommandUploadsTrimmedQuadWithPreservedUv()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(40, 20);
        using var texture = new GpuTexture(
            window.Context,
            1,
            2,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Vertically Mirrored Clipped Texture Test",
            alphaMode: GpuTextureAlphaMode.Straight);
        texture.WritePixels<byte>(
        [
            255, 0, 0, 255,
            0, 255, 0, 255
        ]);
        window.Content = new VerticallyMirroredClippedTextureVisual(texture);

        try
        {
            window.Render();

            var textureVertices = GetTextureVertices(window.Compositor);
            var drawVertices = textureVertices.Skip(textureVertices.Count - 4).Take(4).ToArray();

            Assert.All(drawVertices, vertex => Assert.InRange(vertex.Position.X, 0f, 40f));
            Assert.All(drawVertices, vertex => Assert.InRange(vertex.Position.Y, 0f, 10f));
            Assert.Equal(0.5f, drawVertices.Min(static vertex => vertex.TexCoord.Y), 3);
            Assert.Equal(1f, drawVertices.Max(static vertex => vertex.TexCoord.Y), 3);

            var pixels = window.ReadPixels();
            var inside = ReadPixel(pixels, window.Width, x: 20, y: 5);
            var outside = ReadPixel(pixels, window.Width, x: 20, y: 15);

            Assert.True(
                inside.G > 180 && inside.R < 80 && inside.B < 80,
                $"Expected vertically mirrored clipped UVs to sample the green texel, got RGBA({inside.R}, {inside.G}, {inside.B}, {inside.A}).");
            Assert.Equal(new RgbaPixel(0, 0, 0, 255), outside);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void ConsecutiveCompatibleTextureCommandsShareOneDrawCall()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(48, 16);
        using var texture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Compatible texture batch input",
            alphaMode: GpuTextureAlphaMode.Straight);
        texture.WritePixels<byte>([37, 149, 223, 255]);
        window.Content = new ConsecutiveTextureVisual(texture);

        try
        {
            window.Render();

            Assert.Equal(1, window.Compositor.TextureDrawCallCount);
            Assert.Equal(18, window.Compositor.TextureIndexCount);
            byte[] pixels = window.ReadPixels();
            Assert.Equal(new RgbaPixel(37, 149, 223, 255), ReadPixel(pixels, window.Width, 8, 8));
            Assert.Equal(new RgbaPixel(37, 149, 223, 255), ReadPixel(pixels, window.Width, 24, 8));
            Assert.Equal(new RgbaPixel(37, 149, 223, 255), ReadPixel(pixels, window.Width, 40, 8));
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void TextureBatchingPreservesSamplingTextureAndClipBoundaries()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(64, 16);
        using var first = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Texture batch state input A",
            alphaMode: GpuTextureAlphaMode.Straight);
        using var second = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Texture batch state input B",
            alphaMode: GpuTextureAlphaMode.Straight);
        first.WritePixels<byte>([255, 0, 0, 255]);
        second.WritePixels<byte>([0, 255, 0, 255]);
        window.Content = new TextureStateBoundaryVisual(first, second);

        try
        {
            window.Render();

            Assert.Equal(4, window.Compositor.TextureDrawCallCount);
            byte[] pixels = window.ReadPixels();
            Assert.Equal(new RgbaPixel(255, 0, 0, 255), ReadPixel(pixels, window.Width, 8, 8));
            Assert.Equal(new RgbaPixel(255, 0, 0, 255), ReadPixel(pixels, window.Width, 24, 8));
            Assert.Equal(new RgbaPixel(0, 255, 0, 255), ReadPixel(pixels, window.Width, 40, 8));
            Assert.Equal(new RgbaPixel(0, 255, 0, 255), ReadPixel(pixels, window.Width, 52, 8));
            Assert.Equal(new RgbaPixel(20, 20, 31, 255), ReadPixel(pixels, window.Width, 60, 8));
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

    private static List<VectorVertex> GetTextureVertices(Compositor compositor)
    {
        var field = typeof(Compositor).GetField("_textureVerticesList", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<List<VectorVertex>>(field.GetValue(compositor));
    }

    private readonly record struct RgbaPixel(byte R, byte G, byte B, byte A);

    private sealed class ConsecutiveTextureVisual : FrameworkElement
    {
        private readonly GpuTexture _texture;

        public ConsecutiveTextureVisual(GpuTexture texture)
        {
            _texture = texture;
            Width = 48f;
            Height = 16f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawTexture(_texture, new Rect(0f, 0f, 16f, 16f));
            context.DrawTexture(_texture, new Rect(16f, 0f, 16f, 16f));
            context.DrawTexture(_texture, new Rect(32f, 0f, 16f, 16f));
        }
    }

    private sealed class TextureStateBoundaryVisual : FrameworkElement
    {
        private readonly GpuTexture _first;
        private readonly GpuTexture _second;

        public TextureStateBoundaryVisual(GpuTexture first, GpuTexture second)
        {
            _first = first;
            _second = second;
            Width = 64f;
            Height = 16f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawTexture(_first, new Rect(0f, 0f, 16f, 16f));
            context.DrawTexture(
                _first,
                new Rect(16f, 0f, 16f, 16f),
                default,
                Matrix4x4.Identity,
                TextureSamplingMode.Nearest);
            context.DrawTexture(_second, new Rect(32f, 0f, 16f, 16f));
            context.PushClip(new Rect(48f, 0f, 8f, 16f));
            context.DrawTexture(_second, new Rect(48f, 0f, 16f, 16f));
            context.PopClip();
        }
    }

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

    private sealed class CubicResamplerVisual : FrameworkElement
    {
        private readonly GpuTexture _texture;

        public CubicResamplerVisual(GpuTexture texture)
        {
            _texture = texture;
            Width = 64f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            var source = new Rect(0f, 0f, 4f, 1f);
            context.DrawTexture(
                _texture,
                new Rect(0f, 0f, 32f, 32f),
                source,
                Matrix4x4.Identity,
                TextureSamplingMode.Cubic,
                new Vector2(1f / 3f));
            context.DrawTexture(
                _texture,
                new Rect(32f, 0f, 32f, 32f),
                source,
                Matrix4x4.Identity,
                TextureSamplingMode.Cubic,
                new Vector2(0f, 0.5f));
        }
    }

    private sealed class ClippedSourceRectTextureVisual : FrameworkElement
    {
        private readonly GpuTexture _texture;

        public ClippedSourceRectTextureVisual(GpuTexture texture)
        {
            _texture = texture;
            Width = 40f;
            Height = 20f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 40f, 20f));
            context.PushClip(new Rect(20f, 0f, 20f, 20f));
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawTexture,
                Texture = _texture,
                Rect = new Rect(0f, 0f, 40f, 20f),
                SrcRect = new Rect(1f, 0f, 2f, 1f),
                TextureSamplingMode = TextureSamplingMode.Nearest
            });
            context.PopClip();
        }
    }

    private sealed class MirroredClippedTextureVisual : FrameworkElement
    {
        private readonly GpuTexture _texture;

        public MirroredClippedTextureVisual(GpuTexture texture)
        {
            _texture = texture;
            Width = 40f;
            Height = 20f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 40f, 20f));
            context.PushClip(new Rect(20f, 0f, 20f, 20f));
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawTexture,
                Texture = _texture,
                Rect = new Rect(0f, 0f, 40f, 20f),
                Transform = Matrix4x4.CreateScale(-1f, 1f, 1f) * Matrix4x4.CreateTranslation(40f, 0f, 0f),
                TextureSamplingMode = TextureSamplingMode.Nearest
            });
            context.PopClip();
        }
    }

    private sealed class VerticallyMirroredClippedTextureVisual : FrameworkElement
    {
        private readonly GpuTexture _texture;

        public VerticallyMirroredClippedTextureVisual(GpuTexture texture)
        {
            _texture = texture;
            Width = 40f;
            Height = 20f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 40f, 20f));
            context.PushClip(new Rect(0f, 0f, 40f, 10f));
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawTexture,
                Texture = _texture,
                Rect = new Rect(0f, 0f, 40f, 20f),
                Transform = Matrix4x4.CreateScale(1f, -1f, 1f) * Matrix4x4.CreateTranslation(0f, 20f, 0f),
                TextureSamplingMode = TextureSamplingMode.Nearest
            });
            context.PopClip();
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
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)) { Opacity = 0.5f },
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
