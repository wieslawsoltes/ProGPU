using System;
using System.Buffers.Binary;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.ProGpu;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Avalonia.ProGpu.ContractTests;

public sealed class AvaloniaEncodedImageContractTests
{
    [Fact]
    public void UncompressedIconBitmapPreservesBottomUpColorAndMask()
    {
        byte[] icon = CreateTwoPixelIcon();

        Assert.Equal(new Avalonia.PixelSize(2, 1),
            AvaloniaEncodedImage.Identify(icon));
        Rgba32[] pixels = AvaloniaEncodedImage.Decode(icon);

        Assert.Equal(new Rgba32(255, 0, 0, 255), pixels[0]);
        Assert.Equal(new Rgba32(0, 255, 0, 0), pixels[1]);
    }

    [Fact]
    public void TruncatedIconDirectoryIsRejected()
    {
        byte[] icon = { 0, 0, 1, 0, 1, 0 };

        Assert.Throws<InvalidDataException>(
            () => AvaloniaEncodedImage.Identify(icon));
    }

    [Fact]
    public void ImmutableIconBitmapCanBeSavedAsPng()
    {
        using var source = new MemoryStream(CreateTwoPixelIcon());
        using var bitmap = new ImmutableBitmap(source);
        using var encoded = new MemoryStream();

        bitmap.Save(encoded, PngBitmapEncoderOptions.Default);

        using Image<Rgba32> image = Image.Load<Rgba32>(encoded.ToArray());
        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(new Rgba32(255, 0, 0, 255), image[0, 0]);
        Assert.Equal(new Rgba32(0, 255, 0, 0), image[1, 0]);
    }

    [Fact]
    public void CommonHeadersAreIdentifiedWithoutPixelDecode()
    {
        byte[] gif =
        {
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9',
            (byte)'a', 32, 0, 24, 0
        };
        var bitmap = new byte[30];
        bitmap[0] = (byte)'B';
        bitmap[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bitmap.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(18), 48);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(22), -36);

        Assert.Equal(
            new Avalonia.PixelSize(32, 24),
            AvaloniaEncodedImage.Identify(gif));
        Assert.Equal(
            new Avalonia.PixelSize(48, 36),
            AvaloniaEncodedImage.Identify(bitmap));
    }

    private static byte[] CreateTwoPixelIcon()
    {
        const int directoryBytes = 6 + 16;
        const int headerBytes = 40;
        const int colorStride = 8;
        const int maskStride = 4;
        var icon = new byte[
            directoryBytes + headerBytes + colorStride + maskStride];
        Span<byte> bytes = icon;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], 1);
        bytes[6] = 2;
        bytes[7] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[10..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[12..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes[14..],
            headerBytes + colorStride + maskStride);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[18..], directoryBytes);

        Span<byte> dib = bytes[directoryBytes..];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, headerBytes);
        BinaryPrimitives.WriteInt32LittleEndian(dib[4..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(dib[8..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(dib[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib[14..], 32);
        dib[40] = 0;
        dib[41] = 0;
        dib[42] = 255;
        dib[43] = 0;
        dib[44] = 0;
        dib[45] = 255;
        dib[46] = 0;
        dib[47] = 0;
        dib[48] = 0x40;
        return icon;
    }
}
