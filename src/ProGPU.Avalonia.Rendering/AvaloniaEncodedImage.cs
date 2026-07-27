using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Avalonia.ProGpu;

/// <summary>
/// Identifies and decodes the encoded image formats used at Avalonia's bitmap
/// boundary. ICO containers are handled explicitly because ImageSharp 2 does
/// not expose an ICO decoder.
/// </summary>
internal static class AvaloniaEncodedImage
{
    private const int IconDirectorySize = 6;
    private const int IconEntrySize = 16;
    private const int BitmapInfoHeaderSize = 40;
    private const uint PngSignatureHigh = 0x89504e47;
    private const uint PngSignatureLow = 0x0d0a1a0a;

    public static PixelSize Identify(byte[] encoded)
    {
        if (TrySelectIconImage(encoded, out IconImage image))
            return new PixelSize(image.Width, image.Height);
        if (EncodedImageDimensions.TryRead(
                encoded,
                out int width,
                out int height))
        {
            return new PixelSize(width, height);
        }

        return IdentifyOrdinary(encoded);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PixelSize IdentifyOrdinary(byte[] encoded)
    {
        IImageInfo? info = Image.Identify(encoded);
        if (info is null || info.Width <= 0 || info.Height <= 0)
            throw new InvalidDataException("The stream is not a supported image.");
        return new PixelSize(info.Width, info.Height);
    }

    public static Rgba32[] Decode(byte[] encoded)
    {
        if (TrySelectIconImage(encoded, out IconImage image))
        {
            ReadOnlySpan<byte> payload =
                encoded.AsSpan(image.Offset, image.Length);
            if (IsPng(payload))
                return DecodeOrdinary(payload);
            return DecodeIconBitmap(payload, image.Width, image.Height);
        }

        return DecodeOrdinary(encoded);
    }

    private static Rgba32[] DecodeOrdinary(ReadOnlySpan<byte> encoded)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(encoded);
        var pixels = new Rgba32[checked(image.Width * image.Height)];
        image.CopyPixelDataTo(pixels);
        return pixels;
    }

    private static bool TrySelectIconImage(
        ReadOnlySpan<byte> encoded,
        out IconImage selected)
    {
        selected = default;
        if (encoded.Length < IconDirectorySize ||
            BinaryPrimitives.ReadUInt16LittleEndian(encoded) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(encoded[2..]) != 1)
        {
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(encoded[4..]);
        if (count <= 0 ||
            count > (encoded.Length - IconDirectorySize) / IconEntrySize)
        {
            throw new InvalidDataException("The ICO directory is truncated.");
        }

        long bestArea = -1;
        for (int index = 0; index < count; index++)
        {
            int entryOffset = IconDirectorySize + index * IconEntrySize;
            ReadOnlySpan<byte> entry =
                encoded.Slice(entryOffset, IconEntrySize);
            int width = entry[0] == 0 ? 256 : entry[0];
            int height = entry[1] == 0 ? 256 : entry[1];
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (length == 0 ||
                offset > int.MaxValue ||
                length > int.MaxValue ||
                (ulong)offset + length > (ulong)encoded.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> payload =
                encoded.Slice((int)offset, (int)length);
            bool supported =
                IsPng(payload) ||
                IsSupportedIconBitmap(payload, width, height);
            long area = (long)width * height;
            if (supported && area > bestArea)
            {
                bestArea = area;
                selected = new IconImage(
                    width,
                    height,
                    (int)offset,
                    (int)length);
            }
        }

        if (bestArea < 0)
        {
            throw new InvalidDataException(
                "The ICO container has no supported image entry.");
        }

        return true;
    }

    private static bool IsPng(ReadOnlySpan<byte> payload)
        => payload.Length >= 8 &&
           BinaryPrimitives.ReadUInt32BigEndian(payload) == PngSignatureHigh &&
           BinaryPrimitives.ReadUInt32BigEndian(payload[4..]) == PngSignatureLow;

    private static bool IsSupportedIconBitmap(
        ReadOnlySpan<byte> payload,
        int width,
        int height)
    {
        if (payload.Length < BitmapInfoHeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload) <
                BitmapInfoHeaderSize)
        {
            return false;
        }

        int storedWidth = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        int storedHeight =
            BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);
        ushort planes =
            BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]);
        ushort bitsPerPixel =
            BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]);
        uint compression =
            BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]);
        return storedWidth == width &&
               Math.Abs((long)storedHeight) == (long)height * 2 &&
               planes == 1 &&
               (bitsPerPixel == 24 || bitsPerPixel == 32) &&
               compression == 0;
    }

    private static Rgba32[] DecodeIconBitmap(
        ReadOnlySpan<byte> payload,
        int width,
        int height)
    {
        if (!IsSupportedIconBitmap(payload, width, height))
            throw new InvalidDataException("The ICO bitmap entry is invalid.");

        int headerSize =
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload));
        int storedHeight =
            BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);
        bool bottomUp = storedHeight > 0;
        int bitsPerPixel =
            BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]);
        int colorStride =
            checked(((width * bitsPerPixel + 31) / 32) * 4);
        int maskStride = checked(((width + 31) / 32) * 4);
        int colorBytes = checked(colorStride * height);
        int maskOffset = checked(headerSize + colorBytes);
        int requiredBytes = checked(maskOffset + maskStride * height);
        if (requiredBytes > payload.Length)
            throw new InvalidDataException("The ICO bitmap pixels are truncated.");

        var pixels = new Rgba32[checked(width * height)];
        bool hasExplicitAlpha = false;
        for (int destinationY = 0; destinationY < height; destinationY++)
        {
            int sourceY = bottomUp
                ? height - 1 - destinationY
                : destinationY;
            int rowOffset = checked(headerSize + sourceY * colorStride);
            for (int x = 0; x < width; x++)
            {
                int pixelOffset =
                    checked(rowOffset + x * (bitsPerPixel / 8));
                byte blue = payload[pixelOffset];
                byte green = payload[pixelOffset + 1];
                byte red = payload[pixelOffset + 2];
                byte alpha = bitsPerPixel == 32
                    ? payload[pixelOffset + 3]
                    : (byte)255;
                hasExplicitAlpha |= alpha != 0;
                pixels[destinationY * width + x] =
                    new Rgba32(red, green, blue, alpha);
            }
        }

        if (bitsPerPixel == 24 || !hasExplicitAlpha)
        {
            for (int destinationY = 0; destinationY < height; destinationY++)
            {
                int sourceY = bottomUp
                    ? height - 1 - destinationY
                    : destinationY;
                int rowOffset =
                    checked(maskOffset + sourceY * maskStride);
                for (int x = 0; x < width; x++)
                {
                    bool transparent =
                        (payload[rowOffset + x / 8] &
                         (0x80 >> (x & 7))) != 0;
                    ref Rgba32 pixel =
                        ref pixels[destinationY * width + x];
                    pixel.A = transparent ? (byte)0 : (byte)255;
                }
            }
        }

        return pixels;
    }

    private readonly record struct IconImage(
        int Width,
        int Height,
        int Offset,
        int Length);
}
