using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class GradientRenderTests
{
    [Fact]
    public void TwoPointConicalShaderUsesSkiaCompatibleFocalRootSelection()
    {
        Assert.Contains("let root0Valid = root0Radius >= -0.00001;", Shaders.VectorShader);
        Assert.Contains("let root1Valid = root1Radius >= -0.00001;", Shaders.VectorShader);
        Assert.Contains("return vec2<f32>(max(root0, root1), 1.0);", Shaders.VectorShader);
    }

    [Fact]
    public void TwoPointConicalGradientRendersThroughNativeVectorShader()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);
        window.Content = new TwoPointConicalGradientVisual();

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var center = ReadPixel(pixels, window.Width, x: 16, y: 16);
            var edge = ReadPixel(pixels, window.Width, x: 31, y: 16);

            Assert.InRange(center.R, 220, 255);
            Assert.InRange(center.G, 0, 24);
            Assert.InRange(center.B, 0, 32);
            Assert.Equal(255, center.A);

            Assert.InRange(edge.R, 0, 48);
            Assert.InRange(edge.G, 0, 24);
            Assert.InRange(edge.B, 190, 255);
            Assert.Equal(255, edge.A);
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

    private sealed class TwoPointConicalGradientVisual : FrameworkElement
    {
        public TwoPointConicalGradientVisual()
        {
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            var brush = new TwoPointConicalGradientBrush(
                new Vector2(16f, 16f),
                0f,
                new Vector2(16f, 16f),
                16f,
                new[]
                {
                    new GradientStop(new Vector4(1f, 0f, 0f, 1f), 0f),
                    new GradientStop(new Vector4(0f, 0f, 1f, 1f), 1f)
                });

            context.DrawRectangle(brush, null, new Rect(0f, 0f, 32f, 32f));
        }
    }
}
