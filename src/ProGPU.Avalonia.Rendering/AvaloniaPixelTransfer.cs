using System;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using SixLabors.ImageSharp.PixelFormats;

namespace Avalonia.ProGpu;

/// <summary>
/// CPU-only conversion boundary between Avalonia framebuffer formats and the
/// renderer's canonical RGBA8 texture representation.
/// </summary>
internal static class AvaloniaPixelTransfer
{
    public static Rgba32[] CopyToRgba(
        PixelSize size,
        int stride,
        PixelFormat format,
        IntPtr source)
    {
        if (size.Width <= 0 || size.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        if (source == IntPtr.Zero)
            throw new ArgumentNullException(nameof(source));

        int bytesPerPixel = format == PixelFormats.Rgb565 ? 2 : 4;
        int compactRowBytes = checked(size.Width * bytesPerPixel);
        if (stride < compactRowBytes)
            throw new ArgumentOutOfRangeException(nameof(stride));

        var pixels = new Rgba32[checked(size.Width * size.Height)];
        var row = new byte[compactRowBytes];
        for (int y = 0; y < size.Height; y++)
        {
            Marshal.Copy(
                IntPtr.Add(source, checked(y * stride)),
                row,
                0,
                row.Length);
            int destinationOffset = y * size.Width;
            if (format == PixelFormats.Rgb565)
            {
                for (int x = 0; x < size.Width; x++)
                {
                    int sourceOffset = x * 2;
                    ushort packed = (ushort)(
                        row[sourceOffset] |
                        row[sourceOffset + 1] << 8);
                    pixels[destinationOffset + x] = new Rgba32(
                        Expand5((packed >> 11) & 0x1f),
                        Expand6((packed >> 5) & 0x3f),
                        Expand5(packed & 0x1f),
                        byte.MaxValue);
                }
            }
            else
            {
                bool bgra = format == PixelFormats.Bgra8888;
                if (!bgra && format != PixelFormats.Rgba8888)
                {
                    throw new NotSupportedException(
                        $"Unsupported Avalonia pixel format {format}.");
                }

                for (int x = 0; x < size.Width; x++)
                {
                    int sourceOffset = x * 4;
                    byte first = row[sourceOffset];
                    byte green = row[sourceOffset + 1];
                    byte third = row[sourceOffset + 2];
                    byte alpha = row[sourceOffset + 3];
                    pixels[destinationOffset + x] = bgra
                        ? new Rgba32(third, green, first, alpha)
                        : new Rgba32(first, green, third, alpha);
                }
            }
        }

        return pixels;
    }

    public static void CopyFromRgba(
        ReadOnlySpan<Rgba32> pixels,
        PixelSize size,
        int stride,
        PixelFormat format,
        IntPtr destination)
    {
        if (pixels.Length != checked(size.Width * size.Height))
            throw new ArgumentException("Pixel count does not match size.", nameof(pixels));
        if (destination == IntPtr.Zero)
            throw new ArgumentNullException(nameof(destination));

        int bytesPerPixel = format == PixelFormats.Rgb565 ? 2 : 4;
        int compactRowBytes = checked(size.Width * bytesPerPixel);
        if (stride < compactRowBytes)
            throw new ArgumentOutOfRangeException(nameof(stride));

        var row = new byte[compactRowBytes];
        for (int y = 0; y < size.Height; y++)
        {
            int sourceOffset = y * size.Width;
            if (format == PixelFormats.Rgb565)
            {
                for (int x = 0; x < size.Width; x++)
                {
                    Rgba32 pixel = pixels[sourceOffset + x];
                    ushort packed = (ushort)(
                        (pixel.R >> 3) << 11 |
                        (pixel.G >> 2) << 5 |
                        pixel.B >> 3);
                    int destinationOffset = x * 2;
                    row[destinationOffset] = (byte)packed;
                    row[destinationOffset + 1] = (byte)(packed >> 8);
                }
            }
            else
            {
                bool bgra = format == PixelFormats.Bgra8888;
                if (!bgra && format != PixelFormats.Rgba8888)
                {
                    throw new NotSupportedException(
                        $"Unsupported Avalonia pixel format {format}.");
                }

                for (int x = 0; x < size.Width; x++)
                {
                    Rgba32 pixel = pixels[sourceOffset + x];
                    int destinationOffset = x * 4;
                    row[destinationOffset] = bgra ? pixel.B : pixel.R;
                    row[destinationOffset + 1] = pixel.G;
                    row[destinationOffset + 2] = bgra ? pixel.R : pixel.B;
                    row[destinationOffset + 3] = pixel.A;
                }
            }

            Marshal.Copy(
                row,
                0,
                IntPtr.Add(destination, checked(y * stride)),
                row.Length);
        }
    }

    private static byte Expand5(int value) =>
        (byte)((value << 3) | (value >> 2));

    private static byte Expand6(int value) =>
        (byte)((value << 2) | (value >> 4));
}
