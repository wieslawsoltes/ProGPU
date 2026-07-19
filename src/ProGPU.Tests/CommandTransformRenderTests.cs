using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class CommandTransformRenderTests
{
    [Fact]
    public void PrimitiveTransformOverloadsRecordCommandTransform()
    {
        var transform = Matrix4x4.CreateRotationZ(0.5f) * Matrix4x4.CreateTranslation(7f, 9f, 0f);
        var context = new DrawingContext();

        context.DrawRectangle(null, null, new Rect(1f, 2f, 3f, 4f), transform);
        context.DrawEllipse(null, null, new Vector2(4f, 5f), 6f, 7f, transform);
        context.DrawRoundedRectangle(null, null, new Rect(8f, 9f, 10f, 11f), 2f, 3f, transform);

        Assert.Collection(
            context.Commands,
            command =>
            {
                Assert.Equal(RenderCommandType.DrawRect, command.Type);
                Assert.Equal(transform, command.Transform);
            },
            command =>
            {
                Assert.Equal(RenderCommandType.DrawEllipse, command.Type);
                Assert.Equal(transform, command.Transform);
            },
            command =>
            {
                Assert.Equal(RenderCommandType.DrawRoundedRect, command.Type);
                Assert.Equal(transform, command.Transform);
            });
    }

    [Fact]
    public void TransformedPathOverloadRetainsCallerGeometryCache()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(Vector2.Zero);
        figure.Segments.Add(new LineSegment(new Vector2(8f, 0f)));
        path.Figures.Add(figure);
        var cache = RenderCommandGeometryCache.ForPath(path);
        var transform = Matrix4x4.CreateRotationZ(0.25f) *
            Matrix4x4.CreateTranslation(12f, 6f, 0f);
        var context = new DrawingContext();

        context.DrawPath(
            new SolidColorBrush(0xFF0000FF),
            null,
            path,
            transform,
            cache);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.Same(cache, command.GeometryCache);
        Assert.Equal(transform, command.Transform);
    }

    [Fact]
    public void RectangleCommandTransformMovesRenderedPrimitive()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(40, 24);
        window.Content = new TransformedRectangleVisual();

        try
        {
            window.Render();
            var pixels = window.ReadPixels();

            Assert.Equal(new RgbaPixel(0, 0, 0, 255), ReadPixel(pixels, window.Width, 4, 4));
            var transformed = ReadPixel(pixels, window.Width, 24, 12);
            Assert.True(transformed.R > 220 && transformed.G < 30 && transformed.B < 30, $"Expected transformed red rectangle, got {transformed}.");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void TextureCommandTransformMovesRenderedQuad()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(40, 24);
        using var texture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Command Transform Texture Test");
        texture.WritePixels<byte>([0, 255, 0, 255]);
        window.Content = new TransformedTextureVisual(texture);

        try
        {
            window.Render();
            var pixels = window.ReadPixels();

            Assert.Equal(new RgbaPixel(0, 0, 0, 255), ReadPixel(pixels, window.Width, 4, 4));
            var transformed = ReadPixel(pixels, window.Width, 24, 12);
            Assert.True(transformed.G > 220 && transformed.R < 30 && transformed.B < 30, $"Expected transformed green texture, got {transformed}.");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void RoundedRectangleCommandTransformRotatesRenderedPrimitive()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 24);
        window.Content = new RotatedRoundedRectangleVisual();

        try
        {
            window.Render();
            var pixels = window.ReadPixels();

            Assert.Equal(new RgbaPixel(0, 0, 0, 255), ReadPixel(pixels, window.Width, 6, 6));
            var transformed = ReadPixel(pixels, window.Width, 18, 10);
            Assert.True(transformed.R > 220 && transformed.G < 30 && transformed.B < 30, $"Expected rotated red rectangle, got {transformed}.");
        }
        finally
        {
            window.Content = null;
        }
    }

    private static RgbaPixel ReadPixel(byte[] pixels, uint width, int x, int y)
    {
        var index = ((y * (int)width) + x) * 4;
        return new RgbaPixel(pixels[index], pixels[index + 1], pixels[index + 2], pixels[index + 3]);
    }

    private readonly record struct RgbaPixel(byte R, byte G, byte B, byte A);

    private sealed class TransformedRectangleVisual : FrameworkElement
    {
        public TransformedRectangleVisual()
        {
            Width = 40f;
            Height = 24f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(new SolidColorBrush(0x000000FF), null, new Rect(0f, 0f, 40f, 24f));
            context.DrawRectangle(
                new SolidColorBrush(0xFF0000FF),
                null,
                new Rect(0f, 0f, 8f, 8f),
                Matrix4x4.CreateTranslation(20f, 8f, 0f));
        }
    }

    private sealed class TransformedTextureVisual : FrameworkElement
    {
        private readonly GpuTexture _texture;

        public TransformedTextureVisual(GpuTexture texture)
        {
            _texture = texture;
            Width = 40f;
            Height = 24f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(new SolidColorBrush(0x000000FF), null, new Rect(0f, 0f, 40f, 24f));
            context.DrawTexture(
                _texture,
                new Rect(0f, 0f, 8f, 8f),
                default,
                Matrix4x4.CreateTranslation(20f, 8f, 0f),
                TextureSamplingMode.Nearest);
        }
    }

    private sealed class RotatedRoundedRectangleVisual : FrameworkElement
    {
        public RotatedRoundedRectangleVisual()
        {
            Width = 32f;
            Height = 24f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(new SolidColorBrush(0x000000FF), null, new Rect(0f, 0f, 32f, 24f));
            context.DrawRoundedRectangle(
                new SolidColorBrush(0xFF0000FF),
                null,
                new Rect(0f, 0f, 12f, 4f),
                1f,
                1f,
                Matrix4x4.CreateRotationZ(MathF.PI / 2f) * Matrix4x4.CreateTranslation(20f, 4f, 0f));
        }
    }
}
