using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkBitmapOperationsCompatibilityTests
{
    [Fact]
    public void PixelArrayGetterAndSetterMatchSkiaOrdering()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            2,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        var pixels = new[]
        {
            new SKColor(10, 20, 30, 40),
            new SKColor(50, 60, 70, 80),
        };

        bitmap.Pixels = pixels;
        Assert.Equal(
            pixels.Select(static color => color.ToString()),
            bitmap.Pixels.Select(static color => color.ToString()));
        Assert.Throws<ArgumentNullException>(() => bitmap.Pixels = null!);
        Assert.Throws<ArgumentException>(() => bitmap.Pixels = new SKColor[1]);
    }

    [Fact]
    public void CopyConvertsStorageAndLeavesDestinationUntouchedOnFailure()
    {
        using var source = new SKBitmap(new SKImageInfo(
            2,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        source.Pixels = new[]
        {
            new SKColor(10, 20, 30, 40),
            new SKColor(50, 60, 70, 80),
        };

        using var bgra = source.Copy(SKColorType.Bgra8888);
        using var alpha = source.Copy(SKColorType.Alpha8);
        Assert.Equal("1E140A28463C3250", Convert.ToHexString(bgra.GetPixelSpan()));
        Assert.Equal("2850", Convert.ToHexString(alpha.GetPixelSpan()));

        using var destination = new SKBitmap(1, 1);
        destination.SetPixel(0, 0, SKColors.Red);
        Assert.False(source.CopyTo(destination, SKColorType.Unknown));
        Assert.Equal(1, destination.Width);
        Assert.Equal(1, destination.Height);
        Assert.Equal(SKColors.Red.ToString(), destination.GetPixel(0, 0).ToString());
    }

    [Fact]
    public void SubsetSharesPixelsAndAlphaExtractionUsesAlignedMaskRows()
    {
        using var source = new SKBitmap(new SKImageInfo(
            2,
            2,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        source.SetPixel(0, 0, new SKColor(10, 20, 30, 40));
        source.SetPixel(1, 0, new SKColor(50, 60, 70, 80));

        using var subset = new SKBitmap();
        Assert.True(source.ExtractSubset(subset, new SKRectI(-1, 0, 1, 1)));
        Assert.Equal(1, subset.Width);
        Assert.Equal(1, subset.Height);
        Assert.Equal(source.GetPixels(), subset.GetPixels());
        Assert.Equal(source.GetPixel(0, 0).ToString(), subset.GetPixel(0, 0).ToString());

        using var alpha = new SKBitmap();
        Assert.True(source.ExtractAlpha(alpha, out var offset));
        Assert.Equal(0, offset.X);
        Assert.Equal(0, offset.Y);
        Assert.Equal(SKColorType.Alpha8, alpha.ColorType);
        Assert.Equal(4, alpha.RowBytes);
        Assert.Equal(6, alpha.ByteCount);
        Assert.Equal("285000000000", Convert.ToHexString(alpha.GetPixelSpan()));
    }

    [Fact]
    public void SizeResizeAndScaleOverloadsShareSamplingImplementation()
    {
        using var source = new SKBitmap(new SKImageInfo(
            2,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        source.SetPixel(0, 0, SKColors.Red);
        source.SetPixel(1, 0, SKColors.Blue);

        using var resized = source.Resize(new SKSizeI(4, 1), SKSamplingOptions.Default);
        Assert.NotNull(resized);
        Assert.Equal("FF0000FFFF0000FF0000FFFF0000FFFF", Convert.ToHexString(resized.GetPixelSpan()));

        using var destination = new SKBitmap(new SKImageInfo(
            4,
            1,
            SKColorType.Bgra8888,
            SKAlphaType.Unpremul));
        Assert.True(source.ScalePixels(destination, SKSamplingOptions.Default));
        Assert.Equal("0000FFFF0000FFFFFF0000FFFF0000FF", Convert.ToHexString(destination.GetPixelSpan()));
    }
}
