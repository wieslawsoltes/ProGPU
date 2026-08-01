using System.Reflection;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkWebpAndSvgContractTests
{
    [Fact]
    public void WebpFramePreservesBorrowedPixmapAndMutableDuration()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 1, SKColorType.Rgba8888));
        bitmap.SetPixel(0, 0, SKColors.Red);

        var duration = TimeSpan.FromMilliseconds(25);
        var frame = new SKWebpEncoderFrame(bitmap, duration);

        Assert.Equal(duration, frame.Duration);
        Assert.Equal(bitmap.Info, frame.Pixmap.Info);
        Assert.Equal(SKColors.Red, frame.Pixmap.GetPixelColor(0, 0));

        frame.Duration = TimeSpan.FromMilliseconds(40);
        Assert.Equal(TimeSpan.FromMilliseconds(40), frame.Duration);
    }

    [Fact]
    public void WebpEncoderFailsExplicitlyWithoutWritingMislabeledData()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888));
        using var pixmap = bitmap.PeekPixels();
        using var managed = new MemoryStream();
        using var writer = new SKManagedWStream(managed);
        var options = SKWebpEncoderOptions.Default;

        Assert.Null(SKWebpEncoder.Encode(pixmap, options));
        Assert.False(SKWebpEncoder.Encode(managed, pixmap, options));
        Assert.False(SKWebpEncoder.Encode(writer, pixmap, options));
        Assert.Null(SKWebpEncoder.EncodeAnimated([], options));
        Assert.False(SKWebpEncoder.EncodeAnimated(managed, [], options));
        Assert.False(SKWebpEncoder.EncodeAnimated(writer, [], options));
        Assert.Equal(0, managed.Length);
    }

    [Fact]
    public void FrameAndSvgCanvasUseOfficialManagedTypeShapes()
    {
        Assert.True(typeof(SKWebpEncoder).IsAbstract && typeof(SKWebpEncoder).IsSealed);
        Assert.False(typeof(SKWebpEncoderFrame).IsClass);
        Assert.Equal(
            [typeof(SKPixmap), typeof(TimeSpan)],
            typeof(SKWebpEncoderFrame)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .OrderBy(static field => field.MetadataToken)
                .Select(static field => field.FieldType));

        Assert.True(typeof(SKSvgCanvas).IsClass);
        Assert.False(typeof(SKSvgCanvas).IsAbstract);
        Assert.False(typeof(SKSvgCanvas).IsSealed);
        Assert.Empty(typeof(SKSvgCanvas).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
    }
}
