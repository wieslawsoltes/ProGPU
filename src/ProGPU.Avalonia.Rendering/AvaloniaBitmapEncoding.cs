#if !AVALONIA11
using System;
using System.IO;
using System.IO.Compression;
using Avalonia.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Avalonia.ProGpu;

internal static class AvaloniaBitmapEncoding
{
    internal static void Save(
        Image<Rgba32> image,
        Stream stream,
        BitmapEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        switch (options)
        {
            case PngBitmapEncoderOptions png:
                image.Save(
                    stream,
                    new PngEncoder
                    {
                        CompressionLevel =
                            MapCompressionLevel(
                                png.CompressionLevel)
                    });
                break;
            case JpegBitmapEncoderOptions jpeg
                when jpeg.Quality is >= 0 and <= 100:
                image.Save(
                    stream,
                    new JpegEncoder
                    {
                        // ImageSharp's encoder range begins at one; quality
                        // zero is Avalonia's lowest-quality endpoint.
                        Quality = Math.Max(1, jpeg.Quality)
                    });
                break;
            case JpegBitmapEncoderOptions jpeg:
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    jpeg.Quality,
                    "JPEG quality must be between 0 and 100.");
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options,
                    "Unsupported bitmap encoder options type.");
        }
    }

    private static PngCompressionLevel MapCompressionLevel(
        CompressionLevel level) =>
        level switch
        {
            CompressionLevel.NoCompression =>
                PngCompressionLevel.NoCompression,
            CompressionLevel.Fastest =>
                PngCompressionLevel.BestSpeed,
            CompressionLevel.Optimal =>
                PngCompressionLevel.DefaultCompression,
            CompressionLevel.SmallestSize =>
                PngCompressionLevel.BestCompression,
            _ => throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                "Unsupported PNG compression level.")
        };
}
#endif
