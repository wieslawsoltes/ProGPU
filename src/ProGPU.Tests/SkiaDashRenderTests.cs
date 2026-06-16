using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkiaDashRenderTests
{
    [Fact]
    public void DashedPathEffectRendersStrokeGaps()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);
        window.Content = new DashedSkiaLineVisual();

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var firstDash = ReadPixel(pixels, window.Width, x: 5, y: 16);
            var gap = ReadPixel(pixels, window.Width, x: 14, y: 16);
            var secondDash = ReadPixel(pixels, window.Width, x: 22, y: 16);

            Assert.True(firstDash.R >= 180, $"Expected first dash to render red, found {firstDash}.");
            Assert.True(secondDash.R >= 180, $"Expected second dash to render red, found {secondDash}.");
            Assert.True(gap.R <= 40, $"Expected dash gap to stay near background, found {gap}.");
            Assert.True(gap.G <= 40, $"Expected dash gap to stay near background, found {gap}.");
            Assert.True(gap.B <= 40, $"Expected dash gap to stay near background, found {gap}.");
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

    private sealed class DashedSkiaLineVisual : FrameworkElement
    {
        public DashedSkiaLineVisual()
        {
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new System.Numerics.Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 32f, 32f));

            using var canvas = new SKCanvas(context, 32f, 32f);
            using var path = new SKPath();
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = SKColors.Red,
                StrokeWidth = 4f,
                PathEffect = SKPathEffect.CreateDash(new[] { 8f, 8f }, 0f)
            };

            path.MoveTo(2f, 16f);
            path.LineTo(30f, 16f);
            canvas.DrawPath(path, paint);
        }
    }
}
