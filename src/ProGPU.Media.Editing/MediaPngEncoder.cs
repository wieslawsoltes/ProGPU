using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace ProGPU.Media.Editing;

internal enum MediaPngPixelOrder
{
    Rgba,
    Bgra
}

/// <summary>
/// Dependency-free PNG boundary shared by native thumbnail providers on
/// platforms without a suitable system image encoder. Input rows are consumed
/// directly; one pooled converted row and the required compressed/output byte
/// stores are retained.
/// </summary>
/// <remarks>
/// For P pixels and B encoded bytes, encoding is O(P + B) time and O(W + B)
/// working storage for row width W. CRC lookup is O(1) process-wide storage.
/// This is intentionally an encoded-result boundary, never a frame hot path.
/// </remarks>
internal static class MediaPngEncoder
{
    private static readonly uint[] s_crcTable =
        CreateCrcTable();
    private static ReadOnlySpan<byte> Signature =>
        [137, 80, 78, 71, 13, 10, 26, 10];
    private static ReadOnlySpan<byte> Ihdr =>
        "IHDR"u8;
    private static ReadOnlySpan<byte> Idat =>
        "IDAT"u8;
    private static ReadOnlySpan<byte> Iend =>
        "IEND"u8;

    internal static byte[] Encode(
        ReadOnlySpan<byte> pixels,
        uint width,
        uint height,
        uint rowStride,
        MediaPngPixelOrder pixelOrder)
    {
        if (width == 0 ||
            height == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "PNG dimensions must be non-zero.");
        }
        if (!Enum.IsDefined(pixelOrder))
        {
            throw new ArgumentOutOfRangeException(
                nameof(pixelOrder));
        }

        int rowBytes =
            checked((int)width * 4);
        if (rowStride < rowBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rowStride),
                "PNG input stride is smaller than one RGBA row.");
        }
        int requiredLength =
            checked(
                (int)(
                    (ulong)(height - 1) *
                    rowStride +
                    (uint)rowBytes));
        if (pixels.Length < requiredLength)
        {
            throw new ArgumentException(
                "PNG input does not contain every declared row.",
                nameof(pixels));
        }

        byte[] row =
            ArrayPool<byte>.Shared.Rent(
                checked(rowBytes + 1));
        try
        {
            using var compressed =
                new MemoryStream();
            using (var zlib =
                   new ZLibStream(
                       compressed,
                       CompressionLevel.Fastest,
                       leaveOpen: true))
            {
                row[0] = 0;
                for (uint y = 0;
                     y < height;
                     y++)
                {
                    ReadOnlySpan<byte> source =
                        pixels.Slice(
                            checked((int)(y * rowStride)),
                            rowBytes);
                    Span<byte> destination =
                        row.AsSpan(1, rowBytes);
                    if (pixelOrder ==
                        MediaPngPixelOrder.Rgba)
                    {
                        source.CopyTo(destination);
                    }
                    else
                    {
                        ConvertBgraToRgba(
                            source,
                            destination);
                    }
                    zlib.Write(
                        row.AsSpan(
                            0,
                            rowBytes + 1));
                }
            }

            int capacity =
                checked(
                    (int)Math.Min(
                        int.MaxValue,
                        compressed.Length + 57));
            using var output =
                new MemoryStream(capacity);
            output.Write(Signature);
            Span<byte> header =
                stackalloc byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(
                header,
                width);
            BinaryPrimitives.WriteUInt32BigEndian(
                header[4..],
                height);
            header[8] = 8;
            header[9] = 6;
            WriteChunk(
                output,
                Ihdr,
                header);
            WriteChunk(
                output,
                Idat,
                compressed.GetBuffer().AsSpan(
                    0,
                    checked((int)compressed.Length)));
            WriteChunk(
                output,
                Iend,
                ReadOnlySpan<byte>.Empty);
            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row);
        }
    }

    private static void ConvertBgraToRgba(
        ReadOnlySpan<byte> source,
        Span<byte> destination)
    {
        for (int offset = 0;
             offset < source.Length;
             offset += 4)
        {
            destination[offset] =
                source[offset + 2];
            destination[offset + 1] =
                source[offset + 1];
            destination[offset + 2] =
                source[offset];
            destination[offset + 3] =
                source[offset + 3];
        }
    }

    private static void WriteChunk(
        Stream output,
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        Span<byte> number =
            stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(
            number,
            checked((uint)data.Length));
        output.Write(number);
        output.Write(type);
        output.Write(data);

        uint crc = uint.MaxValue;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(
            number,
            ~crc);
        output.Write(number);
    }

    private static uint UpdateCrc(
        uint crc,
        ReadOnlySpan<byte> bytes)
    {
        for (int index = 0;
             index < bytes.Length;
             index++)
        {
            crc =
                s_crcTable[
                    (crc ^ bytes[index]) & 0xff] ^
                (crc >> 8);
        }
        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table =
            new uint[256];
        for (uint index = 0;
             index < table.Length;
             index++)
        {
            uint value = index;
            for (int bit = 0;
                 bit < 8;
                 bit++)
            {
                value =
                    (value & 1) != 0
                        ? 0xedb8_8320u ^
                          (value >> 1)
                        : value >> 1;
            }
            table[index] = value;
        }
        return table;
    }
}
