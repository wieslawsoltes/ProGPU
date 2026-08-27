using System.Buffers.Binary;

namespace System.Drawing.Imaging;

internal readonly record struct MetafileRecord(
    EmfPlusRecordType Type,
    int Flags,
    int Offset,
    int DataOffset,
    int DataLength,
    bool IsEmfPlus);

internal sealed class MetafileDocument
{
    internal MetafileDocument(
        byte[] source,
        MetafileHeader header,
        MetafileRecord[] records,
        ImageFormat rawFormat)
    {
        Source = source;
        Header = header;
        Records = records;
        RawFormat = rawFormat;
    }

    internal byte[] Source { get; }
    internal MetafileHeader Header { get; }
    internal MetafileRecord[] Records { get; }
    internal ImageFormat RawFormat { get; }
}

internal static class MetafileParser
{
    private const int MaxSourceBytes = 256 * 1024 * 1024;
    private const int MaxRecordBytes = 16 * 1024 * 1024;
    private const int MaxRecordCount = 1_000_000;
    private const uint PlaceableWmfKey = 0x9AC6CDD7;
    private const uint EmfSignature = 0x464D4520;
    private const uint EmfPlusSignature = 0x2B464D45;

    internal static MetafileDocument ParseFile(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);
        string fullPath = Path.GetFullPath(filename);
        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ParseStream(stream);
    }

    internal static MetafileDocument ParseStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        byte[] source = ReadOwnedSource(stream);
        try
        {
            return ParseOwnedSource(source);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException or IndexOutOfRangeException)
        {
            throw InvalidMetafile(exception);
        }
    }

    private static byte[] ReadOwnedSource(Stream stream)
    {
        if (stream.CanSeek)
        {
            long remaining = checked(stream.Length - stream.Position);
            if (remaining < 0 || remaining > MaxSourceBytes)
            {
                throw new ArgumentException("The metafile source length is outside the supported range.", nameof(stream));
            }

            byte[] result = GC.AllocateUninitializedArray<byte>((int)remaining);
            stream.ReadExactly(result);
            return result;
        }

        using var owned = new MemoryStream();
        byte[] buffer = new byte[81_920];
        while (true)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return owned.ToArray();
            }

            if (owned.Length + read > MaxSourceBytes)
            {
                throw new ArgumentException("The metafile source length is outside the supported range.", nameof(stream));
            }

            owned.Write(buffer, 0, read);
        }
    }

    internal static MetafileDocument ParseOwnedSource(byte[] source)
    {
        if (source.Length < 6)
        {
            throw InvalidMetafile();
        }

        ReadOnlySpan<byte> bytes = source;
        if (source.Length >= 22 && ReadUInt32(bytes, 0) == PlaceableWmfKey)
        {
            return ParseWmf(source, placeable: true);
        }

        if (source.Length >= 44 && ReadUInt32(bytes, 0) == (uint)EmfPlusRecordType.EmfHeader)
        {
            return ParseEmf(source);
        }

        return ParseWmf(source, placeable: false);
    }

    private static MetafileDocument ParseWmf(byte[] source, bool placeable)
    {
        ReadOnlySpan<byte> bytes = source;
        int headerOffset = placeable ? 22 : 0;
        if (source.Length < checked(headerOffset + 24))
        {
            throw InvalidMetafile();
        }

        Rectangle bounds = Rectangle.Empty;
        float dpi = 96f;
        if (placeable)
        {
            ushort checksum = 0;
            for (int offset = 0; offset < 20; offset += 2)
            {
                checksum ^= ReadUInt16(bytes, offset);
            }

            if (checksum != ReadUInt16(bytes, 20))
            {
                throw InvalidMetafile();
            }

            short left = ReadInt16(bytes, 6);
            short top = ReadInt16(bytes, 8);
            short right = ReadInt16(bytes, 10);
            short bottom = ReadInt16(bytes, 12);
            ushort unitsPerInch = ReadUInt16(bytes, 14);
            if (right < left || bottom < top || unitsPerInch == 0 || ReadUInt32(bytes, 16) != 0)
            {
                throw InvalidMetafile();
            }

            bounds = Rectangle.FromLTRB(left, top, right, bottom);
            dpi = unitsPerInch;
        }

        ushort type = ReadUInt16(bytes, headerOffset);
        ushort headerWords = ReadUInt16(bytes, headerOffset + 2);
        ushort version = ReadUInt16(bytes, headerOffset + 4);
        uint declaredWords = ReadUInt32(bytes, headerOffset + 6);
        ushort objectCount = ReadUInt16(bytes, headerOffset + 10);
        uint maximumRecordWords = ReadUInt32(bytes, headerOffset + 12);
        ushort parameterCount = ReadUInt16(bytes, headerOffset + 16);
        if (type is not 1 and not 2 || headerWords != 9 || declaredWords < 12 || maximumRecordWords < 3)
        {
            throw InvalidMetafile();
        }

        int declaredBytes = CheckedByteCount(declaredWords, 2);
        if (checked(headerOffset + declaredBytes) != source.Length)
        {
            throw InvalidMetafile();
        }

        var records = new List<MetafileRecord>();
        int cursor = checked(headerOffset + 18);
        bool sawEof = false;
        uint largestRecordWords = 0;
        while (cursor < source.Length)
        {
            if (records.Count == MaxRecordCount || source.Length - cursor < 6)
            {
                throw InvalidMetafile();
            }

            uint recordWords = ReadUInt32(bytes, cursor);
            ushort function = ReadUInt16(bytes, cursor + 4);
            if (recordWords < 3 || recordWords > maximumRecordWords)
            {
                throw InvalidMetafile();
            }

            int recordBytes = CheckedByteCount(recordWords, 2);
            ValidateRecordExtent(cursor, recordBytes, source.Length);
            largestRecordWords = Math.Max(largestRecordWords, recordWords);
            records.Add(new MetafileRecord(
                (EmfPlusRecordType)((int)EmfPlusRecordType.WmfRecordBase | function),
                0,
                cursor,
                cursor + 6,
                recordBytes - 6,
                IsEmfPlus: false));
            cursor = checked(cursor + recordBytes);

            if (function == 0)
            {
                if (recordWords != 3 || cursor != source.Length)
                {
                    throw InvalidMetafile();
                }

                sawEof = true;
                break;
            }
        }

        if (!sawEof || largestRecordWords > maximumRecordWords)
        {
            throw InvalidMetafile();
        }

        var metaHeader = new MetaHeader
        {
            Type = unchecked((short)type),
            HeaderSize = unchecked((short)headerWords),
            Version = unchecked((short)version),
            Size = unchecked((int)declaredWords),
            NoObjects = unchecked((short)objectCount),
            MaxRecord = unchecked((int)maximumRecordWords),
            NoParameters = unchecked((short)parameterCount)
        };
        var header = new MetafileHeader(
            placeable ? MetafileType.WmfPlaceable : MetafileType.Wmf,
            source.Length,
            version,
            dpi,
            dpi,
            bounds,
            metaHeader,
            0,
            0,
            0,
            false);
        return new MetafileDocument(source, header, records.ToArray(), ImageFormat.Wmf);
    }

    private static MetafileDocument ParseEmf(byte[] source)
    {
        ReadOnlySpan<byte> bytes = source;
        if (source.Length < 88 || ReadUInt32(bytes, 40) != EmfSignature)
        {
            throw InvalidMetafile();
        }

        uint headerSizeValue = ReadUInt32(bytes, 4);
        uint declaredBytesValue = ReadUInt32(bytes, 48);
        uint declaredRecordCount = ReadUInt32(bytes, 52);
        ushort declaredHandles = ReadUInt16(bytes, 56);
        if (headerSizeValue < 88 || (headerSizeValue & 3) != 0 ||
            declaredBytesValue != source.Length || declaredRecordCount < 2 ||
            declaredRecordCount > MaxRecordCount || declaredHandles == 0)
        {
            throw InvalidMetafile();
        }

        int headerSize = checked((int)headerSizeValue);
        ValidateRecordExtent(0, headerSize, source.Length);
        ValidateDescription(bytes, headerSize);

        int left = ReadInt32(bytes, 8);
        int top = ReadInt32(bytes, 12);
        int right = ReadInt32(bytes, 16);
        int bottom = ReadInt32(bytes, 20);
        if (right < left || bottom < top)
        {
            throw InvalidMetafile();
        }

        var bounds = Rectangle.FromLTRB(left, top, right, bottom);
        int deviceWidth = ReadInt32(bytes, 72);
        int deviceHeight = ReadInt32(bytes, 76);
        int millimeterWidth = ReadInt32(bytes, 80);
        int millimeterHeight = ReadInt32(bytes, 84);
        float dpiX = CalculateDpi(deviceWidth, millimeterWidth);
        float dpiY = CalculateDpi(deviceHeight, millimeterHeight);
        int version = unchecked((int)ReadUInt32(bytes, 44));

        var records = new List<MetafileRecord>(checked((int)declaredRecordCount));
        int cursor = 0;
        bool sawEof = false;
        while (cursor < source.Length)
        {
            if (records.Count == MaxRecordCount || source.Length - cursor < 8)
            {
                throw InvalidMetafile();
            }

            uint type = ReadUInt32(bytes, cursor);
            uint sizeValue = ReadUInt32(bytes, cursor + 4);
            if (sizeValue < 8 || sizeValue > MaxRecordBytes || (sizeValue & 3) != 0)
            {
                throw InvalidMetafile();
            }

            int size = checked((int)sizeValue);
            ValidateRecordExtent(cursor, size, source.Length);
            records.Add(new MetafileRecord(
                (EmfPlusRecordType)type,
                0,
                cursor,
                cursor + 8,
                size - 8,
                IsEmfPlus: false));
            cursor = checked(cursor + size);

            if (type == (uint)EmfPlusRecordType.EmfEof)
            {
                if (cursor != source.Length)
                {
                    throw InvalidMetafile();
                }

                sawEof = true;
                break;
            }
        }

        uint parsedRecordCount = checked((uint)records.Count);
        bool countMatches = parsedRecordCount == declaredRecordCount ||
            parsedRecordCount - 1 == declaredRecordCount;
        if (!sawEof || !countMatches || records[0].Type != EmfPlusRecordType.EmfHeader)
        {
            throw InvalidMetafile();
        }

        MetafileType metafileType = MetafileType.Emf;
        int emfPlusHeaderSize = 0;
        int logicalDpiX = 0;
        int logicalDpiY = 0;
        bool isDisplay = false;
        if (records.Count > 2 && records[1].Type == EmfPlusRecordType.EmfGdiComment &&
            TryParseEmfPlusHeader(
                bytes,
                records[1],
                out MetafileRecord[] emfPlusRecords,
                out bool dual,
                out int emfPlusVersion,
                out emfPlusHeaderSize,
                out logicalDpiX,
                out logicalDpiY,
                out isDisplay))
        {
            metafileType = dual ? MetafileType.EmfPlusDual : MetafileType.EmfPlusOnly;
            version = emfPlusVersion;
            // The EMR_GDICOMMENT is the transport envelope for the contained
            // EMF+ records. Enumeration exposes the decoded records at the
            // envelope's source position rather than appending them after EOF.
            records.RemoveAt(1);
            records.InsertRange(1, emfPlusRecords);
        }

        var header = new MetafileHeader(
            metafileType,
            source.Length,
            version,
            dpiX,
            dpiY,
            bounds,
            null,
            emfPlusHeaderSize,
            logicalDpiX,
            logicalDpiY,
            isDisplay);
        return new MetafileDocument(source, header, records.ToArray(), ImageFormat.Emf);
    }

    private static bool TryParseEmfPlusHeader(
        ReadOnlySpan<byte> bytes,
        MetafileRecord comment,
        out MetafileRecord[] records,
        out bool dual,
        out int version,
        out int headerSize,
        out int logicalDpiX,
        out int logicalDpiY,
        out bool isDisplay)
    {
        records = [];
        dual = false;
        version = 0;
        headerSize = 0;
        logicalDpiX = 0;
        logicalDpiY = 0;
        isDisplay = false;
        if (comment.DataLength < 8)
        {
            return false;
        }

        uint dataSizeValue = ReadUInt32(bytes, comment.DataOffset);
        if (dataSizeValue < 4 || dataSizeValue > comment.DataLength - 4)
        {
            throw InvalidMetafile();
        }

        int dataOffset = comment.DataOffset + 4;
        int dataSize = checked((int)dataSizeValue);
        if (ReadUInt32(bytes, dataOffset) != EmfPlusSignature)
        {
            return false;
        }

        int cursor = checked(dataOffset + 4);
        int end = checked(dataOffset + dataSize);
        var nested = new List<MetafileRecord>();
        bool sawEof = false;
        while (cursor < end)
        {
            if (nested.Count == MaxRecordCount || end - cursor < 12)
            {
                throw InvalidMetafile();
            }

            ushort type = ReadUInt16(bytes, cursor);
            ushort flags = ReadUInt16(bytes, cursor + 2);
            uint sizeValue = ReadUInt32(bytes, cursor + 4);
            uint payloadSizeValue = ReadUInt32(bytes, cursor + 8);
            if (sizeValue < 12 || sizeValue > MaxRecordBytes || (sizeValue & 3) != 0 ||
                payloadSizeValue > sizeValue - 12)
            {
                throw InvalidMetafile();
            }

            int size = checked((int)sizeValue);
            int payloadSize = checked((int)payloadSizeValue);
            ValidateRecordExtent(cursor, size, end);
            nested.Add(new MetafileRecord(
                (EmfPlusRecordType)type,
                flags,
                cursor,
                cursor + 12,
                payloadSize,
                IsEmfPlus: true));

            if (nested.Count == 1)
            {
                if (type != (ushort)EmfPlusRecordType.Header || payloadSize < 16)
                {
                    throw InvalidMetafile();
                }

                dual = (flags & 1) != 0;
                version = ReadInt32(bytes, cursor + 12);
                uint emfPlusFlags = ReadUInt32(bytes, cursor + 16);
                logicalDpiX = ReadInt32(bytes, cursor + 20);
                logicalDpiY = ReadInt32(bytes, cursor + 24);
                if (logicalDpiX <= 0 || logicalDpiY <= 0)
                {
                    throw InvalidMetafile();
                }

                isDisplay = (emfPlusFlags & 1) != 0;
                headerSize = size;
            }

            cursor = checked(cursor + size);
            if (type == (ushort)EmfPlusRecordType.EndOfFile)
            {
                if (cursor != end)
                {
                    throw InvalidMetafile();
                }

                sawEof = true;
                break;
            }
        }

        if (!sawEof)
        {
            throw InvalidMetafile();
        }

        records = nested.ToArray();
        return true;
    }

    private static void ValidateDescription(ReadOnlySpan<byte> bytes, int headerSize)
    {
        uint characterCount = ReadUInt32(bytes, 60);
        uint offset = ReadUInt32(bytes, 64);
        if (characterCount == 0)
        {
            return;
        }

        int byteCount = CheckedByteCount(characterCount, 2);
        if (offset < 88 || offset > headerSize || byteCount > headerSize - offset)
        {
            throw InvalidMetafile();
        }
    }

    private static float CalculateDpi(int pixels, int millimeters) =>
        pixels > 0 && millimeters > 0
            ? pixels * 25.4f / millimeters
            : 96f;

    private static int CheckedByteCount(uint elementCount, int elementSize)
    {
        ulong byteCount = (ulong)elementCount * (uint)elementSize;
        if (byteCount > int.MaxValue || byteCount > MaxSourceBytes)
        {
            throw InvalidMetafile();
        }

        return (int)byteCount;
    }

    private static void ValidateRecordExtent(int offset, int size, int end)
    {
        if (size < 0 || size > MaxRecordBytes || offset < 0 || offset > end || size > end - offset)
        {
            throw InvalidMetafile();
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));

    private static short ReadInt16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(offset, 2));

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));

    private static ArgumentException InvalidMetafile(Exception? inner = null) =>
        new("Parameter is not valid.", inner);
}
