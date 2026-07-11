using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkiaSharpZeroLengthStrokeTests
{
    [Theory]
    [InlineData(SKStrokeCap.Butt, 0, 0)]
    [InlineData(SKStrokeCap.Round, 255, 0)]
    [InlineData(SKStrokeCap.Square, 255, 255)]
    public void DrawPathRendersZeroLengthStrokeCaps(SKStrokeCap cap, byte centerAlpha, byte cornerAlpha)
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var paint = new SKPaint
        {
            Color = SKColors.Blue,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 16f,
            StrokeCap = cap,
            IsAntialias = false
        };
        using var path = new SKPath();
        path.MoveTo(16f, 16f);
        path.LineTo(16f, 16f);

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawPath(path, paint);
        surface.Flush();

        using var image = surface.Snapshot();
        byte[] pixels = image.Texture.ReadPixels();
        Assert.Equal(centerAlpha, GetAlpha(pixels, width: 32, x: 16, y: 16));
        Assert.Equal(cornerAlpha, GetAlpha(pixels, width: 32, x: 9, y: 9));
        Assert.Equal((byte)0, GetAlpha(pixels, width: 32, x: 5, y: 16));
    }

    [Theory]
    [InlineData(SKStrokeCap.Butt, false)]
    [InlineData(SKStrokeCap.Round, true)]
    [InlineData(SKStrokeCap.Square, true)]
    public void GetFillPathHonorsZeroLengthStrokeCaps(SKStrokeCap cap, bool hasFill)
    {
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 8f,
            StrokeCap = cap
        };
        using var source = new SKPath();
        using var destination = new SKPath();
        source.MoveTo(10f, 10f);
        source.LineTo(10f, 10f);

        Assert.Equal(hasFill, paint.GetFillPath(source, destination));
        Assert.Equal(hasFill, !destination.IsEmpty);
        if (hasFill)
        {
            Assert.Equal(new SKRect(6f, 6f, 14f, 14f), destination.Bounds);
        }
    }

    [Fact]
    public void ClosedPathWithLeadingZeroLengthSegmentKeepsStrokeBudgetAligned()
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var paint = new SKPaint
        {
            Color = SKColors.Green,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4f,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = false
        };
        using var path = new SKPath();
        path.MoveTo(8f, 8f);
        path.LineTo(8f, 8f);
        path.LineTo(24f, 8f);
        path.LineTo(24f, 24f);
        path.LineTo(8f, 24f);
        path.Close();

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawPath(path, paint);
        surface.Flush();

        using var image = surface.Snapshot();
        byte[] pixels = image.Texture.ReadPixels();
        Assert.Equal((byte)255, GetAlpha(pixels, width: 32, x: 8, y: 8));
        Assert.Equal((byte)0, GetAlpha(pixels, width: 32, x: 16, y: 16));
    }

    private static byte GetAlpha(byte[] pixels, int width, int x, int y) =>
        pixels[(y * width + x) * 4 + 3];
}
