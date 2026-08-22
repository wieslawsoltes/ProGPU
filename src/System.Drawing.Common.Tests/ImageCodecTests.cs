using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

public sealed class ImageCodecTests
{
    [Fact]
    public void CodecDiscoveryReportsOnlyFunctionalManagedCodecsAndReturnsDefensiveSnapshots()
    {
        ImageCodecInfo[] decoders = ImageCodecInfo.GetImageDecoders();
        ImageCodecInfo[] encoders = ImageCodecInfo.GetImageEncoders();

        Assert.Equal(
            [ImageFormat.Bmp.Guid, ImageFormat.Jpeg.Guid, ImageFormat.Gif.Guid, ImageFormat.Png.Guid, ImageFormat.Icon.Guid],
            decoders.Select(codec => codec.FormatID));
        Assert.Equal(
            [ImageFormat.Bmp.Guid, ImageFormat.Jpeg.Guid, ImageFormat.Png.Guid],
            encoders.Select(codec => codec.FormatID));
        Assert.All(decoders, codec => Assert.True(codec.Flags.HasFlag(ImageCodecFlags.Decoder | ImageCodecFlags.SupportBitmap | ImageCodecFlags.Builtin)));
        Assert.All(encoders, codec => Assert.True(codec.Flags.HasFlag(ImageCodecFlags.Encoder | ImageCodecFlags.SupportBitmap | ImageCodecFlags.Builtin)));

        decoders[0].CodecName = "mutated";
        Assert.NotNull(decoders[0].SignaturePatterns);
        decoders[0].SignaturePatterns![0][0] = 0;

        ImageCodecInfo fresh = ImageCodecInfo.GetImageDecoders()[0];
        Assert.Equal("ProGPU BMP Codec", fresh.CodecName);
        Assert.Equal((byte)'B', fresh.SignaturePatterns![0][0]);
    }

    [Fact]
    public void ImageFormatUsesOfficialIdentitiesAndValueSemantics()
    {
        Assert.Equal(new Guid("b96b3caa-0728-11d3-9d7b-0000f81ef32e"), ImageFormat.MemoryBmp.Guid);
        Assert.Equal(new Guid("b96b3cb6-0728-11d3-9d7b-0000f81ef32e"), ImageFormat.Heif.Guid);
        Assert.Equal(new Guid("b96b3cb7-0728-11d3-9d7b-0000f81ef32e"), ImageFormat.Webp.Guid);
        Assert.Equal(ImageFormat.Png, new ImageFormat(ImageFormat.Png.Guid));
        Assert.Equal(ImageFormat.Png.GetHashCode(), new ImageFormat(ImageFormat.Png.Guid).GetHashCode());
        Assert.Equal("MemoryBMP", ImageFormat.MemoryBmp.ToString());
        Assert.Equal("Jpeg", ImageFormat.Jpeg.ToString());
    }

    [Fact]
    public void EncoderParametersOwnValuesAndExposeOfficialShapes()
    {
        using var quality = new EncoderParameter(Encoder.Quality, 80L);
        Assert.Equal(Encoder.Quality.Guid, quality.Encoder.Guid);
        Assert.Equal(EncoderParameterValueType.ValueTypeLong, quality.Type);
        Assert.Equal(quality.Type, quality.ValueType);
        Assert.Equal(1, quality.NumberOfValues);

        quality.Encoder = Encoder.Compression;
        Assert.Equal(Encoder.Compression.Guid, quality.Encoder.Guid);

        using var parameters = new EncoderParameters(1) { Param = [quality] };
        Assert.Same(quality, Assert.Single(parameters.Param));
    }

    [Fact]
    public void CodecSelectedPngAndBmpUseManagedEncoders()
    {
        using var bitmap = CreateFixtureBitmap();
        ImageCodecInfo[] encoders = ImageCodecInfo.GetImageEncoders();

        using var png = new MemoryStream();
        bitmap.Save(png, FindEncoder(encoders, ImageFormat.Png), null);
        Assert.Equal([0x89, 0x50, 0x4e, 0x47], png.ToArray()[..4]);

        using var bmp = new MemoryStream();
        bitmap.Save(bmp, FindEncoder(encoders, ImageFormat.Bmp), new EncoderParameters(0));
        Assert.Equal([(byte)'B', (byte)'M'], bmp.ToArray()[..2]);
    }

    [Fact]
    public void JpegQualityEncodingProducesDecodableManagedOutput()
    {
        using var bitmap = CreateFixtureBitmap();
        ImageCodecInfo jpeg = FindEncoder(ImageCodecInfo.GetImageEncoders(), ImageFormat.Jpeg);
        using var parameters = new EncoderParameters(1)
        {
            Param = [new EncoderParameter(Encoder.Quality, 82L)]
        };
        using var stream = new MemoryStream();

        bitmap.Save(stream, jpeg, parameters);

        byte[] encoded = stream.ToArray();
        Assert.True(encoded.Length > 100);
        Assert.Equal([0xff, 0xd8, 0xff], encoded[..3]);
        stream.Position = 0;
        using Image decoded = Image.FromStream(stream);
        Assert.Equal(bitmap.Size, decoded.Size);
    }

    [Fact]
    public void ManagedJpegEncodingHasBoundedWarmAllocation()
    {
        using var bitmap = CreateFixtureBitmap(64, 64);
        ImageCodecInfo jpeg = FindEncoder(ImageCodecInfo.GetImageEncoders(), ImageFormat.Jpeg);
        using var parameters = new EncoderParameters(1)
        {
            Param = [new EncoderParameter(Encoder.Quality, 82L)]
        };
        using var stream = new MemoryStream(capacity: 64 * 64 * 4);
        bitmap.Save(stream, jpeg, parameters);
        stream.Position = 0;
        stream.SetLength(0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bitmap.Save(stream, jpeg, parameters);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(stream.Length > 100);
        Assert.InRange(allocated, 16_384, 30_000);
    }

    [Fact]
    public void EncoderParameterDiscoveryAndUnsupportedMultiframeAreExplicit()
    {
        using var bitmap = CreateFixtureBitmap();
        ImageCodecInfo jpeg = FindEncoder(ImageCodecInfo.GetImageEncoders(), ImageFormat.Jpeg);
        using EncoderParameters? parameters = bitmap.GetEncoderParameterList(jpeg.Clsid);
        EncoderParameter quality = Assert.Single(parameters!.Param);
        Assert.Equal(Encoder.Quality.Guid, quality.Encoder.Guid);
        Assert.Equal(EncoderParameterValueType.ValueTypeLongRange, quality.Type);

        Assert.Throws<NotSupportedException>(() => bitmap.SaveAdd((EncoderParameters?)null));
        Assert.Throws<NotSupportedException>(() => bitmap.SaveAdd(bitmap, null));
        Assert.Throws<ArgumentException>(() => bitmap.GetEncoderParameterList(Guid.NewGuid()));
    }

    private static Bitmap CreateFixtureBitmap(int width = 16, int height = 16)
    {
        var bitmap = new Bitmap(width, height);
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(
                    x,
                    y,
                    Color.FromArgb(
                        255,
                        x * 255 / Math.Max(1, width - 1),
                        y * 255 / Math.Max(1, height - 1),
                        (x + y) * 255 / Math.Max(1, width + height - 2)));
            }
        }

        return bitmap;
    }

    private static ImageCodecInfo FindEncoder(ImageCodecInfo[] encoders, ImageFormat format) =>
        Assert.Single(encoders, codec => codec.FormatID == format.Guid);
}
