using System;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.ProGpu;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ProGPU.Avalonia.ContractTests;

public sealed class AvaloniaBitmapEncodingContractTests
{
    [Fact]
    public void PngOptionsProducePngData()
    {
        using var image = new Image<Rgba32>(1, 1);
        using var stream = new MemoryStream();

        AvaloniaBitmapEncoding.Save(
            image,
            stream,
            PngBitmapEncoderOptions.Default);

        Assert.Equal(
            new byte[] { 0x89, 0x50, 0x4E, 0x47 },
            stream.ToArray()[..4]);
    }

    [Fact]
    public void JpegOptionsProduceJpegData()
    {
        using var image = new Image<Rgba32>(1, 1);
        using var stream = new MemoryStream();

        AvaloniaBitmapEncoding.Save(
            image,
            stream,
            new JpegBitmapEncoderOptions { Quality = 80 });

        Assert.Equal(
            new byte[] { 0xFF, 0xD8 },
            stream.ToArray()[..2]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void JpegQualityOutsideAvaloniaRangeIsRejected(
        int quality)
    {
        using var image = new Image<Rgba32>(1, 1);
        using var stream = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => AvaloniaBitmapEncoding.Save(
                image,
                stream,
                new JpegBitmapEncoderOptions
                {
                    Quality = quality
                }));
    }
}
