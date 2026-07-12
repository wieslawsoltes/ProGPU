using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkPixmapOperationsCompatibilityTests
{
    [Fact]
    public void EncoderOptionDefaultsAndEqualityMatchSkia()
    {
        Assert.Equal(100, SKJpegEncoderOptions.Default.Quality);
        Assert.Equal(SKJpegEncoderDownsample.Downsample420, SKJpegEncoderOptions.Default.Downsample);
        Assert.Equal(SKJpegEncoderAlphaOption.Ignore, SKJpegEncoderOptions.Default.AlphaOption);
        Assert.Equal(
            new SKJpegEncoderOptions(
                100,
                SKJpegEncoderDownsample.Downsample420,
                SKJpegEncoderAlphaOption.Ignore),
            SKJpegEncoderOptions.Default);

        Assert.Equal(SKPngEncoderFilterFlags.AllFilters, SKPngEncoderOptions.Default.FilterFlags);
        Assert.Equal(6, SKPngEncoderOptions.Default.ZLibLevel);
        Assert.Equal(
            new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6),
            SKPngEncoderOptions.Default);

        Assert.Equal(SKWebpEncoderCompression.Lossy, SKWebpEncoderOptions.Default.Compression);
        Assert.Equal(100f, SKWebpEncoderOptions.Default.Quality);
        Assert.NotEqual(
            SKWebpEncoderOptions.Default,
            new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100f));
    }

    [Fact]
    public void PixelReadsSubsetAndEraseMatchNativeClipping()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            2,
            2,
            SKColorType.Bgra8888,
            SKAlphaType.Premul));
        bitmap.SetPixel(0, 0, new SKColor(100, 150, 200, 128));
        bitmap.SetPixel(1, 0, new SKColor(50, 60, 70, 80));
        using var pixmap = bitmap.PeekPixels();

        Assert.Equal(new SKColor(100, 149, 199, 128).ToString(), pixmap.GetPixelColor(0, 0).ToString());
        Assert.Equal(128f / 255f, pixmap.GetPixelAlpha(0, 0), 4);
        var colorF = pixmap.GetPixelColorF(0, 0);
        Assert.Equal(pixmap.GetPixelColor(0, 0).R / 255f, colorF.R, 4);

        using var subset = pixmap.ExtractSubset(new SKRectI(-1, 0, 1, 1));
        Assert.NotNull(subset);
        Assert.Equal(1, subset.Width);
        Assert.Equal(1, subset.Height);
        Assert.Equal(pixmap.GetPixels(), subset.GetPixels());
        Assert.Null(pixmap.ExtractSubset(SKRectI.Empty));

        Assert.True(pixmap.Erase(new SKColor(1, 2, 3, 4), new SKRectI(-1, 0, 1, 1)));
        Assert.Equal(new SKColor(0, 0, 0, 4).ToString(), pixmap.GetPixelColor(0, 0).ToString());
        Assert.False(pixmap.Erase(SKColors.White, new SKRectI(3, 3, 4, 4)));
    }

    [Fact]
    public void ReadPixelsConvertsColorOrderAndScalePixelsMatchesNearestMapping()
    {
        using var sourceBitmap = new SKBitmap(new SKImageInfo(
            2,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        sourceBitmap.SetPixel(0, 0, new SKColor(10, 20, 30, 40));
        sourceBitmap.SetPixel(1, 0, new SKColor(50, 60, 70, 80));
        using var source = sourceBitmap.PeekPixels();

        using var convertedBitmap = new SKBitmap(new SKImageInfo(
            2,
            1,
            SKColorType.Bgra8888,
            SKAlphaType.Unpremul));
        using var converted = convertedBitmap.PeekPixels();
        Assert.True(source.ReadPixels(converted));
        Assert.Equal("1E140A28463C3250", Convert.ToHexString(converted.GetPixelSpan()));

        using var scaledBitmap = new SKBitmap(new SKImageInfo(
            4,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        using var scaled = scaledBitmap.PeekPixels();
        Assert.True(source.ScalePixels(scaled, SKSamplingOptions.Default));
        Assert.Equal(
            "0A141E280A141E28323C4650323C4650",
            Convert.ToHexString(scaled.GetPixelSpan()));
    }

    [Fact]
    public void PngAndJpegEncodingStayOnCpuAndRoundTripPixels()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            1,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        bitmap.SetPixel(0, 0, new SKColor(10, 20, 30, 40));
        using var pixmap = bitmap.PeekPixels();

        using var bitmapPng = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        Assert.Equal("89504E470D0A1A0A", Convert.ToHexString(bitmapPng.AsSpan()[..8]));
        using var bitmapStream = new MemoryStream();
        Assert.True(bitmap.Encode(bitmapStream, SKEncodedImageFormat.Png, 100));
        Assert.Equal("89504E470D0A1A0A", Convert.ToHexString(bitmapStream.ToArray().AsSpan(0, 8)));

        using var png = pixmap.Encode(SKPngEncoderOptions.Default);
        Assert.NotNull(png);
        Assert.Equal("89504E470D0A1A0A", Convert.ToHexString(png.AsSpan()[..8]));
        using var decodedPng = SKBitmap.Decode(png);
        Assert.Equal(new SKColor(13, 19, 32, 40).ToString(), decodedPng.GetPixel(0, 0).ToString());

        using var jpeg = pixmap.Encode(new SKJpegEncoderOptions(95));
        Assert.NotNull(jpeg);
        Assert.Equal("FFD8FF", Convert.ToHexString(jpeg.AsSpan()[..3]));
        using var decodedJpeg = SKBitmap.Decode(jpeg);
        var jpegColor = decodedJpeg.GetPixel(0, 0);
        Assert.InRange(jpegColor.R, (byte)5, (byte)20);
        Assert.InRange(jpegColor.G, (byte)10, (byte)30);
        Assert.InRange(jpegColor.B, (byte)20, (byte)45);
        Assert.Equal(byte.MaxValue, jpegColor.A);
    }

    [Fact]
    public void ViewsAndUnsupportedWebpPreserveExplicitContracts()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            1,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        bitmap.SetPixel(0, 0, new SKColor(1, 2, 3, 255));
        using var pixmap = bitmap.PeekPixels();
        Assert.True(pixmap.ComputeIsOpaque());

        using var opaque = pixmap.WithAlphaType(SKAlphaType.Opaque);
        using var bgra = pixmap.WithColorType(SKColorType.Bgra8888);
        Assert.Equal(pixmap.GetPixels(), opaque.GetPixels());
        Assert.Equal(pixmap.GetPixels(), bgra.GetPixels());
        Assert.Equal(SKAlphaType.Opaque, opaque.AlphaType);
        Assert.Equal(SKColorType.Bgra8888, bgra.ColorType);

        Assert.Null(pixmap.Encode(SKWebpEncoderOptions.Default));
        using var output = new MemoryStream();
        Assert.False(pixmap.Encode(output, SKWebpEncoderOptions.Default));
        Assert.Equal(0, output.Length);
    }
}
