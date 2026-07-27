using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using ProGPU.Text;

namespace Avalonia.ProGpu;

/// <summary>
/// Immutable placement data for one embedded bitmap-glyph strike.
/// </summary>
internal readonly struct ColorGlyphMetrics
{
    public ColorGlyphMetrics(
        ushort pixelsPerEm,
        ushort pixelsPerInch,
        short originOffsetX,
        short originOffsetY,
        int pixelWidth,
        int pixelHeight)
        : this(
            pixelsPerEm,
            pixelsPerInch,
            originOffsetX,
            originOffsetY,
            pixelWidth,
            pixelHeight,
            usesHorizontalMetrics: false)
    {
    }

    private ColorGlyphMetrics(
        ushort pixelsPerEm,
        ushort pixelsPerInch,
        short horizontalOffset,
        short verticalOffset,
        int pixelWidth,
        int pixelHeight,
        bool usesHorizontalMetrics)
    {
        PixelsPerEm = pixelsPerEm;
        PixelsPerInch = pixelsPerInch;
        HorizontalOffset = horizontalOffset;
        VerticalOffset = verticalOffset;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        UsesHorizontalMetrics = usesHorizontalMetrics;
    }

    public ushort PixelsPerEm { get; }
    public ushort PixelsPerInch { get; }
    public short HorizontalOffset { get; }
    public short VerticalOffset { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public bool UsesHorizontalMetrics { get; }

    public Rect GetBounds(Point baseline, double emSize)
    {
        double scale = PixelsPerEm == 0 ? 1d : emSize / PixelsPerEm;
        double x = UsesHorizontalMetrics
            ? baseline.X + HorizontalOffset * scale
            : baseline.X - HorizontalOffset * scale;
        double y = UsesHorizontalMetrics
            ? baseline.Y - VerticalOffset * scale
            : baseline.Y + (VerticalOffset - PixelHeight) * scale;
        return new Rect(
            x,
            y,
            PixelWidth * scale,
            PixelHeight * scale);
    }

    public static ColorGlyphMetrics FromBitmap(
        in BitmapGlyphData bitmap,
        int pixelWidth,
        int pixelHeight)
    {
        return bitmap.UsesHorizontalMetrics
            ? new ColorGlyphMetrics(
                bitmap.PixelsPerEm,
                bitmap.PixelsPerInch,
                bitmap.BearingX,
                bitmap.BearingY,
                pixelWidth,
                pixelHeight,
                usesHorizontalMetrics: true)
            : new ColorGlyphMetrics(
                bitmap.PixelsPerEm,
                bitmap.PixelsPerInch,
                bitmap.OriginOffsetX,
                bitmap.OriginOffsetY,
                pixelWidth,
                pixelHeight,
                usesHorizontalMetrics: false);
    }
}

/// <summary>
/// Process-wide bounded cache of encoded color-glyph dimensions and placement.
/// The cache owns no decoded pixels and performs no GPU work.
/// </summary>
internal static class BoundedColorGlyphMetrics
{
    public const int MaximumCachedMetricCount = 2048;

    private static readonly object Gate = new();
    private static readonly Dictionary<CacheKey, int> IndexByKey =
        new(MaximumCachedMetricCount);
    private static readonly CacheEntry[] Entries =
        new CacheEntry[MaximumCachedMetricCount];
    private static int _entryCount;
    private static int _nextReplacement;
    private static ulong _evictionCount;

    public static int CachedMetricCount
    {
        get
        {
            lock (Gate)
                return _entryCount;
        }
    }

    public static long CachedDecodedPixelBytes => 0;

    public static ulong MetricEvictionCount
    {
        get
        {
            lock (Gate)
                return _evictionCount;
        }
    }

    public static bool TryGetMetrics(
        TtfFont font,
        ushort glyphIndex,
        double emSize,
        out ColorGlyphMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (!double.IsFinite(emSize) || emSize <= 0d)
        {
            metrics = default;
            return false;
        }

        float rasterSize = (float)emSize;
        var key = new CacheKey(
            font,
            glyphIndex,
            BitConverter.SingleToInt32Bits(rasterSize));
        lock (Gate)
        {
            if (IndexByKey.TryGetValue(key, out int slot))
            {
                metrics = Entries[slot].Metrics;
                return true;
            }
        }

        if (!font.TryGetBitmapGlyph(glyphIndex, rasterSize, out var bitmap) ||
            !EncodedImageDimensions.TryRead(
                bitmap.Data.Span,
                out int width,
                out int height))
        {
            metrics = default;
            return false;
        }

        metrics = ColorGlyphMetrics.FromBitmap(bitmap, width, height);
        lock (Gate)
        {
            if (IndexByKey.TryGetValue(key, out int existingSlot))
            {
                metrics = Entries[existingSlot].Metrics;
                return true;
            }

            int slot;
            if (_entryCount < MaximumCachedMetricCount)
            {
                slot = _entryCount++;
            }
            else
            {
                slot = _nextReplacement;
                _nextReplacement =
                    (_nextReplacement + 1) % MaximumCachedMetricCount;
                IndexByKey.Remove(Entries[slot].Key);
                _evictionCount++;
            }

            Entries[slot] = new CacheEntry(key, metrics);
            IndexByKey.Add(key, slot);
        }

        return true;
    }

    private readonly struct CacheKey : IEquatable<CacheKey>
    {
        public CacheKey(TtfFont font, ushort glyphIndex, int rasterSizeBits)
        {
            Font = font;
            GlyphIndex = glyphIndex;
            RasterSizeBits = rasterSizeBits;
        }

        public TtfFont Font { get; }
        public ushort GlyphIndex { get; }
        public int RasterSizeBits { get; }

        public bool Equals(CacheKey other)
        {
            return ReferenceEquals(Font, other.Font) &&
                GlyphIndex == other.GlyphIndex &&
                RasterSizeBits == other.RasterSizeBits;
        }

        public override bool Equals(object? obj) =>
            obj is CacheKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(
                RuntimeHelpers.GetHashCode(Font),
                GlyphIndex,
                RasterSizeBits);
    }

    private readonly record struct CacheEntry(
        CacheKey Key,
        ColorGlyphMetrics Metrics);
}

/// <summary>
/// Allocation-free dimension reader for encoded glyph payloads.
/// </summary>
internal static class EncodedImageDimensions
{
    public static bool TryRead(
        ReadOnlySpan<byte> encoded,
        out int width,
        out int height)
    {
        return TryReadPng(encoded, out width, out height) ||
            TryReadJpeg(encoded, out width, out height) ||
            TryReadGif(encoded, out width, out height) ||
            TryReadBitmap(encoded, out width, out height) ||
            TryReadTiff(encoded, out width, out height);
    }

    private static bool TryReadPng(
        ReadOnlySpan<byte> encoded,
        out int width,
        out int height)
    {
        ReadOnlySpan<byte> signature =
            [137, 80, 78, 71, 13, 10, 26, 10];
        if (encoded.Length >= 24 &&
            encoded[..8].SequenceEqual(signature) &&
            encoded[12] == (byte)'I' &&
            encoded[13] == (byte)'H' &&
            encoded[14] == (byte)'D' &&
            encoded[15] == (byte)'R')
        {
            uint parsedWidth =
                BinaryPrimitives.ReadUInt32BigEndian(encoded[16..20]);
            uint parsedHeight =
                BinaryPrimitives.ReadUInt32BigEndian(encoded[20..24]);
            if (parsedWidth is > 0 and <= int.MaxValue &&
                parsedHeight is > 0 and <= int.MaxValue)
            {
                width = (int)parsedWidth;
                height = (int)parsedHeight;
                return true;
            }
        }

        width = 0;
        height = 0;
        return false;
    }

    private static bool TryReadJpeg(
        ReadOnlySpan<byte> encoded,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if (encoded.Length < 4 ||
            encoded[0] != 0xff ||
            encoded[1] != 0xd8)
        {
            return false;
        }

        int offset = 2;
        while (offset + 3 < encoded.Length)
        {
            while (offset < encoded.Length && encoded[offset] == 0xff)
                offset++;
            if (offset >= encoded.Length)
                return false;

            byte marker = encoded[offset++];
            if (marker is 0xd8 or 0xd9)
                continue;
            if (marker is >= 0xd0 and <= 0xd7)
                continue;
            if (offset + 2 > encoded.Length)
                return false;

            int segmentLength =
                BinaryPrimitives.ReadUInt16BigEndian(encoded[offset..]);
            if (segmentLength < 2 ||
                offset > encoded.Length - segmentLength)
            {
                return false;
            }

            if (IsStartOfFrame(marker) && segmentLength >= 7)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(
                    encoded[(offset + 3)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(
                    encoded[(offset + 5)..]);
                return width > 0 && height > 0;
            }

            offset += segmentLength;
        }

        return false;
    }

    private static bool IsStartOfFrame(byte marker)
    {
        return marker is >= 0xc0 and <= 0xcf &&
            marker is not 0xc4 and not 0xc8 and not 0xcc;
    }

    private static bool TryReadGif(
        ReadOnlySpan<byte> encoded,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if (encoded.Length < 10 ||
            encoded[0] != (byte)'G' ||
            encoded[1] != (byte)'I' ||
            encoded[2] != (byte)'F' ||
            encoded[3] != (byte)'8' ||
            (encoded[4] != (byte)'7' && encoded[4] != (byte)'9') ||
            encoded[5] != (byte)'a')
        {
            return false;
        }

        width = BinaryPrimitives.ReadUInt16LittleEndian(encoded[6..]);
        height = BinaryPrimitives.ReadUInt16LittleEndian(encoded[8..]);
        return width > 0 && height > 0;
    }

    private static bool TryReadBitmap(
        ReadOnlySpan<byte> encoded,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if (encoded.Length < 26 ||
            encoded[0] != (byte)'B' ||
            encoded[1] != (byte)'M')
        {
            return false;
        }

        uint headerSize =
            BinaryPrimitives.ReadUInt32LittleEndian(encoded[14..]);
        if (headerSize == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(encoded[18..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(encoded[20..]);
            return width > 0 && height > 0;
        }

        if (headerSize < 40 || encoded.Length < 30)
            return false;
        int parsedWidth =
            BinaryPrimitives.ReadInt32LittleEndian(encoded[18..]);
        int parsedHeight =
            BinaryPrimitives.ReadInt32LittleEndian(encoded[22..]);
        if (parsedWidth <= 0 ||
            parsedHeight == 0 ||
            parsedHeight == int.MinValue)
        {
            return false;
        }

        width = parsedWidth;
        height = Math.Abs(parsedHeight);
        return true;
    }

    private static bool TryReadTiff(
        ReadOnlySpan<byte> encoded,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if (encoded.Length < 8)
            return false;

        bool littleEndian;
        if (encoded[0] == (byte)'I' && encoded[1] == (byte)'I')
            littleEndian = true;
        else if (encoded[0] == (byte)'M' && encoded[1] == (byte)'M')
            littleEndian = false;
        else
            return false;

        if (ReadUInt16(encoded[2..], littleEndian) != 42)
            return false;

        uint directoryOffset = ReadUInt32(encoded[4..], littleEndian);
        if (directoryOffset > int.MaxValue ||
            (int)directoryOffset > encoded.Length - 2)
        {
            return false;
        }

        int offset = (int)directoryOffset;
        int entryCount = ReadUInt16(encoded[offset..], littleEndian);
        offset += 2;
        if (entryCount > (encoded.Length - offset) / 12)
            return false;

        for (int index = 0; index < entryCount; index++, offset += 12)
        {
            ReadOnlySpan<byte> entry = encoded.Slice(offset, 12);
            ushort tag = ReadUInt16(entry, littleEndian);
            if (tag is not 256 and not 257)
                continue;

            ushort type = ReadUInt16(entry[2..], littleEndian);
            uint count = ReadUInt32(entry[4..], littleEndian);
            if (count != 1 || (type is not 3 and not 4))
                continue;

            uint value = type == 3
                ? ReadUInt16(entry[8..], littleEndian)
                : ReadUInt32(entry[8..], littleEndian);
            if (value is 0 or > int.MaxValue)
                return false;

            if (tag == 256)
                width = (int)value;
            else
                height = (int)value;
        }

        return width > 0 && height > 0;
    }

    private static ushort ReadUInt16(
        ReadOnlySpan<byte> value,
        bool littleEndian)
    {
        return littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(value)
            : BinaryPrimitives.ReadUInt16BigEndian(value);
    }

    private static uint ReadUInt32(
        ReadOnlySpan<byte> value,
        bool littleEndian)
    {
        return littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(value)
            : BinaryPrimitives.ReadUInt32BigEndian(value);
    }
}
