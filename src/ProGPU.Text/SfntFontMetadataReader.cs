using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ProGPU.Text;

internal static class SfntFontMetadataReader
{
    private const int SfntHeaderSize = 12;
    private const int TableRecordSize = 16;
    private const int NameHeaderSize = 6;
    private const int NameRecordSize = 12;
    private const int MaxFaceCount = 4096;
    private const int MaxTableCount = 4096;
    private const int MaxCharacterMapSize = 64 * 1024 * 1024;
    private const int MaxSbixStrikeCount = 4096;

    public static bool TryReadFontInfos(string file, out List<FontInfo> infos)
    {
        ArgumentNullException.ThrowIfNull(file);

        try
        {
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.RandomAccess);
            infos = ReadFontInfos(stream, file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            infos = new List<FontInfo>();
            return false;
        }
    }

    public static bool TryReadCharacterMap(string file, int faceIndex, out byte[] characterMap)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfNegative(faceIndex);

        try
        {
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.RandomAccess);
            uint[] faceOffsets = ReadFaceOffsets(stream);
            if ((uint)faceIndex >= (uint)faceOffsets.Length)
            {
                characterMap = Array.Empty<byte>();
                return false;
            }

            return TryReadFaceTable(stream, faceOffsets[faceIndex], "cmap", out characterMap);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or OverflowException)
        {
            characterMap = Array.Empty<byte>();
            return false;
        }
    }

    public static bool TryCreateGlyphResidentFont(
        string file,
        int faceIndex,
        ushort glyphIndex,
        out byte[] fontData)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfNegative(faceIndex);

        try
        {
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.RandomAccess);
            uint[] faceOffsets = ReadFaceOffsets(stream);
            if ((uint)faceIndex >= (uint)faceOffsets.Length)
            {
                fontData = Array.Empty<byte>();
                return false;
            }

            List<SourceTable> tables = ReadSourceTables(stream, faceOffsets[faceIndex], out uint sfntVersion);
            int sbixIndex = tables.FindIndex(static table => table.Tag == "sbix");
            int maxpIndex = tables.FindIndex(static table => table.Tag == "maxp");
            if (sbixIndex < 0 || maxpIndex < 0 || tables[sbixIndex].Length < 8)
            {
                fontData = Array.Empty<byte>();
                return false;
            }

            byte[] maxp = ReadTable(stream, tables[maxpIndex]);
            if (maxp.Length < 6)
            {
                fontData = Array.Empty<byte>();
                return false;
            }

            ushort glyphCount = ReadUShort(maxp, 4);
            if (glyphIndex >= glyphCount ||
                !TryBuildGlyphSbix(stream, tables[sbixIndex], glyphCount, glyphIndex, out byte[] sbix))
            {
                fontData = Array.Empty<byte>();
                return false;
            }

            var residentTables = new List<ResidentTable>(tables.Count);
            for (var tableIndex = 0; tableIndex < tables.Count; tableIndex++)
            {
                SourceTable table = tables[tableIndex];
                byte[] data = tableIndex == sbixIndex
                    ? sbix
                    : tableIndex == maxpIndex
                        ? maxp
                        : ReadTable(stream, table);
                residentTables.Add(new ResidentTable(table.Tag, table.Checksum, data));
            }

            fontData = BuildSfnt(sfntVersion, residentTables);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or
                                   ArgumentException or OverflowException)
        {
            fontData = Array.Empty<byte>();
            return false;
        }
    }

    private static List<FontInfo> ReadFontInfos(Stream stream, string file)
    {
        uint[] faceOffsets = ReadFaceOffsets(stream);
        var infos = new List<FontInfo>(faceOffsets.Length);
        var fallbackName = Path.GetFileNameWithoutExtension(file);

        for (var faceIndex = 0; faceIndex < faceOffsets.Length; faceIndex++)
        {
            NameSelection names = ReadFaceNames(stream, faceOffsets[faceIndex]);
            FaceStyle style = ReadFaceStyle(stream, faceOffsets[faceIndex]);
            var familyName = names.PreferredFamilyName ?? names.FamilyName ?? fallbackName;
            var fullName = names.FullName ?? familyName;
            infos.Add(new FontInfo
            {
                Name = fullName,
                FamilyName = familyName,
                FilePath = file,
                FaceIndex = faceIndex,
                Weight = style.Weight,
                Width = style.Width,
                IsItalic = style.IsItalic
            });
        }

        return infos;
    }

    private static FaceStyle ReadFaceStyle(Stream stream, uint faceOffset)
    {
        Span<byte> header = stackalloc byte[SfntHeaderSize];
        ReadExactly(stream, faceOffset, header);
        ushort tableCount = ReadUShort(header, 4);
        if (tableCount > MaxTableCount)
        {
            throw new FormatException("SFNT face has an invalid table count.");
        }

        var weight = 400;
        var width = 5;
        var italic = false;
        Span<byte> record = stackalloc byte[TableRecordSize];
        for (var tableIndex = 0; tableIndex < tableCount; tableIndex++)
        {
            ReadExactly(
                stream,
                checked((long)faceOffset + SfntHeaderSize + (long)tableIndex * TableRecordSize),
                record);
            uint tableOffset = ReadUInt(record, 8);
            uint tableLength = ReadUInt(record, 12);
            if (HasTag(record, "OS/2") && tableLength >= 64)
            {
                Span<byte> attributes = stackalloc byte[64];
                ReadExactly(stream, tableOffset, attributes);
                weight = Math.Clamp((int)ReadUShort(attributes, 4), 1, 1000);
                width = Math.Clamp((int)ReadUShort(attributes, 6), 1, 9);
                italic |= (ReadUShort(attributes, 62) & 0x0001) != 0;
            }
            else if (HasTag(record, "head") && tableLength >= 46)
            {
                Span<byte> attributes = stackalloc byte[46];
                ReadExactly(stream, tableOffset, attributes);
                italic |= (ReadUShort(attributes, 44) & 0x0002) != 0;
            }
        }

        return new FaceStyle(weight, width, italic);
    }

    private static uint[] ReadFaceOffsets(Stream stream)
    {
        Span<byte> header = stackalloc byte[SfntHeaderSize];
        ReadExactly(stream, 0, header);
        if (!HasTag(header, "ttcf"))
        {
            return new[] { 0u };
        }

        uint faceCountValue = ReadUInt(header, 8);
        if (faceCountValue == 0 || faceCountValue > MaxFaceCount)
        {
            throw new FormatException("TrueType collection has an invalid face count.");
        }

        var faceCount = checked((int)faceCountValue);
        var offsets = new uint[faceCount];
        Span<byte> offsetBytes = stackalloc byte[4];
        for (var i = 0; i < offsets.Length; i++)
        {
            ReadExactly(stream, SfntHeaderSize + (long)i * 4, offsetBytes);
            offsets[i] = ReadUInt(offsetBytes, 0);
        }

        return offsets;
    }

    private static List<SourceTable> ReadSourceTables(Stream stream, uint faceOffset, out uint sfntVersion)
    {
        Span<byte> header = stackalloc byte[SfntHeaderSize];
        ReadExactly(stream, faceOffset, header);
        sfntVersion = ReadUInt(header, 0);
        ushort tableCount = ReadUShort(header, 4);
        if (tableCount == 0 || tableCount > MaxTableCount)
        {
            throw new FormatException("SFNT face has an invalid table count.");
        }

        var tables = new List<SourceTable>(tableCount);
        Span<byte> record = stackalloc byte[TableRecordSize];
        for (var tableIndex = 0; tableIndex < tableCount; tableIndex++)
        {
            ReadExactly(
                stream,
                checked((long)faceOffset + SfntHeaderSize + (long)tableIndex * TableRecordSize),
                record);
            string tag = Encoding.ASCII.GetString(record[..4]);
            uint checksum = ReadUInt(record, 4);
            uint offset = ReadUInt(record, 8);
            uint length = ReadUInt(record, 12);
            if (offset > stream.Length || length > stream.Length - offset)
            {
                throw new FormatException("SFNT table is outside the font file.");
            }

            tables.Add(new SourceTable(tag, checksum, offset, length));
        }

        return tables;
    }

    private static byte[] ReadTable(Stream stream, SourceTable table)
    {
        var data = GC.AllocateUninitializedArray<byte>(checked((int)table.Length));
        ReadExactly(stream, table.Offset, data);
        return data;
    }

    private static bool TryBuildGlyphSbix(
        Stream stream,
        SourceTable table,
        ushort glyphCount,
        ushort glyphIndex,
        out byte[] sbix)
    {
        Span<byte> header = stackalloc byte[8];
        ReadExactly(stream, table.Offset, header);
        uint strikeCountValue = ReadUInt(header, 4);
        if (ReadUShort(header, 0) != 1 || strikeCountValue == 0 ||
            strikeCountValue > MaxSbixStrikeCount || 8L + strikeCountValue * 4L > table.Length)
        {
            sbix = Array.Empty<byte>();
            return false;
        }

        int strikeCount = checked((int)strikeCountValue);
        var sourceStrikeOffsets = new uint[strikeCount + 1];
        Span<byte> offsetBytes = stackalloc byte[4];
        for (var strikeIndex = 0; strikeIndex < strikeCount; strikeIndex++)
        {
            ReadExactly(stream, table.Offset + 8L + strikeIndex * 4L, offsetBytes);
            sourceStrikeOffsets[strikeIndex] = ReadUInt(offsetBytes, 0);
        }
        sourceStrikeOffsets[strikeCount] = table.Length;

        var strikes = new byte[strikeCount][];
        for (var strikeIndex = 0; strikeIndex < strikeCount; strikeIndex++)
        {
            uint sourceStart = sourceStrikeOffsets[strikeIndex];
            uint sourceEnd = sourceStrikeOffsets[strikeIndex + 1];
            if (sourceStart >= sourceEnd || sourceEnd > table.Length)
            {
                sbix = Array.Empty<byte>();
                return false;
            }

            Span<byte> strikeHeader = stackalloc byte[4];
            ReadExactly(stream, table.Offset + sourceStart, strikeHeader);
            byte[] record = TryReadSbixGlyphRecord(
                stream,
                table.Offset,
                sourceStart,
                sourceEnd,
                glyphCount,
                glyphIndex,
                glyphIndex,
                0,
                out byte[] glyphRecord)
                ? glyphRecord
                : Array.Empty<byte>();
            int glyphOffsetsLength = checked((glyphCount + 1) * 4);
            int recordOffset = checked(4 + glyphOffsetsLength);
            var strike = new byte[checked(recordOffset + record.Length)];
            strikeHeader.CopyTo(strike);
            for (var offsetIndex = 0; offsetIndex <= glyphCount; offsetIndex++)
            {
                uint offset = checked((uint)(offsetIndex <= glyphIndex ? recordOffset : strike.Length));
                WriteUInt(strike, 4 + offsetIndex * 4, offset);
            }
            record.CopyTo(strike.AsSpan(recordOffset));
            strikes[strikeIndex] = strike;
        }

        int headerLength = checked(8 + strikeCount * 4);
        int totalLength = headerLength;
        for (var strikeIndex = 0; strikeIndex < strikes.Length; strikeIndex++)
        {
            totalLength = checked(totalLength + strikes[strikeIndex].Length);
        }

        sbix = new byte[totalLength];
        header.CopyTo(sbix);
        int targetOffset = headerLength;
        for (var strikeIndex = 0; strikeIndex < strikes.Length; strikeIndex++)
        {
            WriteUInt(sbix, 8 + strikeIndex * 4, checked((uint)targetOffset));
            strikes[strikeIndex].CopyTo(sbix.AsSpan(targetOffset));
            targetOffset += strikes[strikeIndex].Length;
        }
        return true;
    }

    private static bool TryReadSbixGlyphRecord(
        Stream stream,
        long tableOffset,
        uint strikeOffset,
        uint strikeEnd,
        ushort glyphCount,
        ushort glyphIndex,
        ushort originalGlyphIndex,
        int depth,
        out byte[] record)
    {
        record = Array.Empty<byte>();
        if (depth > 16 || glyphIndex >= glyphCount ||
            strikeOffset + 4L + ((long)glyphCount + 1) * 4L > strikeEnd)
        {
            return false;
        }

        Span<byte> offsets = stackalloc byte[8];
        ReadExactly(stream, tableOffset + strikeOffset + 4L + glyphIndex * 4L, offsets);
        uint startRelative = ReadUInt(offsets, 0);
        uint endRelative = ReadUInt(offsets, 4);
        if (startRelative >= endRelative || strikeOffset + endRelative > strikeEnd ||
            endRelative - startRelative < 8)
        {
            return false;
        }

        long sourceRecordOffset = tableOffset + strikeOffset + startRelative;
        Span<byte> recordHeader = stackalloc byte[8];
        ReadExactly(stream, sourceRecordOffset, recordHeader);
        uint graphicType = ReadUInt(recordHeader, 4);
        if (graphicType == 0x64757065) // "dupe"
        {
            if (endRelative - startRelative < 10)
            {
                return false;
            }

            Span<byte> duplicateBytes = stackalloc byte[2];
            ReadExactly(stream, sourceRecordOffset + 8, duplicateBytes);
            ushort duplicateGlyphIndex = ReadUShort(duplicateBytes, 0);
            if (duplicateGlyphIndex == originalGlyphIndex ||
                !TryReadSbixGlyphRecord(
                    stream,
                    tableOffset,
                    strikeOffset,
                    strikeEnd,
                    glyphCount,
                    duplicateGlyphIndex,
                    originalGlyphIndex,
                    depth + 1,
                    out byte[] duplicate))
            {
                return false;
            }

            record = duplicate;
            recordHeader[..4].CopyTo(record);
            return true;
        }

        if (graphicType != TtfFont.PngBitmapGraphicType &&
            graphicType != TtfFont.JpegBitmapGraphicType &&
            graphicType != TtfFont.TiffBitmapGraphicType)
        {
            return false;
        }

        record = GC.AllocateUninitializedArray<byte>(checked((int)(endRelative - startRelative)));
        ReadExactly(stream, sourceRecordOffset, record);
        return true;
    }

    private static byte[] BuildSfnt(uint sfntVersion, List<ResidentTable> tables)
    {
        int directoryLength = checked(SfntHeaderSize + tables.Count * TableRecordSize);
        int targetOffset = Align4(directoryLength);
        int resultLength = targetOffset;
        for (var tableIndex = 0; tableIndex < tables.Count; tableIndex++)
        {
            resultLength = Align4(checked(resultLength + tables[tableIndex].Data.Length));
        }

        var result = new byte[resultLength];
        WriteUInt(result, 0, sfntVersion);
        WriteUShort(result, 4, checked((ushort)tables.Count));
        WriteSearchParameters(result, checked((ushort)tables.Count));
        for (var tableIndex = 0; tableIndex < tables.Count; tableIndex++)
        {
            ResidentTable table = tables[tableIndex];
            int recordOffset = SfntHeaderSize + tableIndex * TableRecordSize;
            Encoding.ASCII.GetBytes(table.Tag, result.AsSpan(recordOffset, 4));
            WriteUInt(result, recordOffset + 4, table.Tag == "sbix" ? CalculateChecksum(table.Data) : table.Checksum);
            WriteUInt(result, recordOffset + 8, checked((uint)targetOffset));
            WriteUInt(result, recordOffset + 12, checked((uint)table.Data.Length));
            table.Data.CopyTo(result.AsSpan(targetOffset));
            targetOffset = Align4(checked(targetOffset + table.Data.Length));
        }

        return result;
    }

    private static uint CalculateChecksum(ReadOnlySpan<byte> data)
    {
        uint checksum = 0;
        for (var offset = 0; offset < data.Length; offset += 4)
        {
            uint value = (uint)data[offset] << 24;
            if (offset + 1 < data.Length) value |= (uint)data[offset + 1] << 16;
            if (offset + 2 < data.Length) value |= (uint)data[offset + 2] << 8;
            if (offset + 3 < data.Length) value |= data[offset + 3];
            checksum = unchecked(checksum + value);
        }
        return checksum;
    }

    private static int Align4(int value) => checked((value + 3) & ~3);

    private static void WriteSearchParameters(Span<byte> data, ushort tableCount)
    {
        ushort maximumPowerOfTwo = 1;
        ushort entrySelector = 0;
        while (maximumPowerOfTwo <= tableCount / 2)
        {
            maximumPowerOfTwo *= 2;
            entrySelector++;
        }
        WriteUShort(data, 6, checked((ushort)(maximumPowerOfTwo * TableRecordSize)));
        WriteUShort(data, 8, entrySelector);
        WriteUShort(data, 10, checked((ushort)(tableCount * TableRecordSize - maximumPowerOfTwo * TableRecordSize)));
    }

    private static void WriteUShort(Span<byte> data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    private static void WriteUInt(Span<byte> data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static NameSelection ReadFaceNames(Stream stream, uint faceOffset)
    {
        Span<byte> header = stackalloc byte[SfntHeaderSize];
        ReadExactly(stream, faceOffset, header);
        ushort tableCount = ReadUShort(header, 4);
        if (tableCount > MaxTableCount)
        {
            throw new FormatException("SFNT face has an invalid table count.");
        }

        Span<byte> record = stackalloc byte[TableRecordSize];
        for (var i = 0; i < tableCount; i++)
        {
            var recordOffset = checked((long)faceOffset + SfntHeaderSize + (long)i * TableRecordSize);
            ReadExactly(stream, recordOffset, record);
            if (!HasTag(record, "name"))
            {
                continue;
            }

            uint tableOffset = ReadUInt(record, 8);
            uint tableLength = ReadUInt(record, 12);
            return ReadNameTable(stream, tableOffset, tableLength);
        }

        return default;
    }

    private static bool TryReadFaceTable(Stream stream, uint faceOffset, string tag, out byte[] table)
    {
        Span<byte> header = stackalloc byte[SfntHeaderSize];
        ReadExactly(stream, faceOffset, header);
        ushort tableCount = ReadUShort(header, 4);
        if (tableCount > MaxTableCount)
        {
            throw new FormatException("SFNT face has an invalid table count.");
        }

        Span<byte> record = stackalloc byte[TableRecordSize];
        for (var i = 0; i < tableCount; i++)
        {
            var recordOffset = checked((long)faceOffset + SfntHeaderSize + (long)i * TableRecordSize);
            ReadExactly(stream, recordOffset, record);
            if (!HasTag(record, tag))
            {
                continue;
            }

            uint tableOffset = ReadUInt(record, 8);
            uint tableLength = ReadUInt(record, 12);
            if (tableLength == 0 || tableLength > MaxCharacterMapSize)
            {
                table = Array.Empty<byte>();
                return false;
            }

            table = GC.AllocateUninitializedArray<byte>(checked((int)tableLength));
            ReadExactly(stream, tableOffset, table);
            return true;
        }

        table = Array.Empty<byte>();
        return false;
    }

    private static NameSelection ReadNameTable(Stream stream, uint tableOffset, uint tableLength)
    {
        if (tableLength < NameHeaderSize)
        {
            return default;
        }

        var tableEnd = checked((long)tableOffset + tableLength);
        Span<byte> header = stackalloc byte[NameHeaderSize];
        ReadExactly(stream, tableOffset, header);
        ushort recordCount = ReadUShort(header, 2);
        ushort stringOffset = ReadUShort(header, 4);
        var recordsEnd = checked((long)tableOffset + NameHeaderSize + (long)recordCount * NameRecordSize);
        var stringsStart = checked((long)tableOffset + stringOffset);
        if (recordsEnd > tableEnd || stringsStart > tableEnd)
        {
            throw new FormatException("SFNT name table is truncated.");
        }

        var selection = new NameSelection();
        Span<byte> record = stackalloc byte[NameRecordSize];
        for (var i = 0; i < recordCount; i++)
        {
            ReadExactly(stream, (long)tableOffset + NameHeaderSize + (long)i * NameRecordSize, record);
            ushort nameId = ReadUShort(record, 6);
            if (nameId is not (SfntNameIds.FamilyName or SfntNameIds.FullName or SfntNameIds.PreferredFamilyName))
            {
                continue;
            }

            ushort platformId = ReadUShort(record, 0);
            ushort encodingId = ReadUShort(record, 2);
            ushort languageId = ReadUShort(record, 4);
            ushort valueLength = ReadUShort(record, 8);
            ushort valueOffset = ReadUShort(record, 10);
            if (valueLength == 0)
            {
                continue;
            }

            var absoluteValueOffset = checked(stringsStart + valueOffset);
            if (absoluteValueOffset > tableEnd || valueLength > tableEnd - absoluteValueOffset)
            {
                continue;
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(valueLength);
            try
            {
                var bytes = buffer.AsSpan(0, valueLength);
                ReadExactly(stream, absoluteValueOffset, bytes);
                var value = SfntFontFace.DecodeName(bytes, platformId, encodingId);
                var score = SfntFontFace.GetNameScore(platformId, languageId, value);
                if (!string.IsNullOrWhiteSpace(value) && selection.ShouldRead(nameId, score))
                {
                    selection.Set(nameId, value, score);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return selection;
    }

    private static void ReadExactly(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > stream.Length || buffer.Length > stream.Length - offset)
        {
            throw new FormatException("SFNT data is truncated.");
        }

        stream.Position = offset;
        stream.ReadExactly(buffer);
    }

    private static bool HasTag(ReadOnlySpan<byte> data, string tag)
    {
        return data.Length >= 4 &&
               data[0] == tag[0] &&
               data[1] == tag[1] &&
               data[2] == tag[2] &&
               data[3] == tag[3];
    }

    private static ushort ReadUShort(ReadOnlySpan<byte> data, int offset)
    {
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static uint ReadUInt(ReadOnlySpan<byte> data, int offset)
    {
        return (uint)((data[offset] << 24) |
                      (data[offset + 1] << 16) |
                      (data[offset + 2] << 8) |
                       data[offset + 3]);
    }

    private struct NameSelection
    {
        private int _preferredFamilyScore;
        private int _familyScore;
        private int _fullNameScore;

        public string? PreferredFamilyName { get; private set; }
        public string? FamilyName { get; private set; }
        public string? FullName { get; private set; }

        public readonly bool ShouldRead(ushort nameId, int score)
        {
            return nameId switch
            {
                SfntNameIds.PreferredFamilyName => PreferredFamilyName == null || score > _preferredFamilyScore,
                SfntNameIds.FamilyName => FamilyName == null || score > _familyScore,
                SfntNameIds.FullName => FullName == null || score > _fullNameScore,
                _ => false
            };
        }

        public void Set(ushort nameId, string value, int score)
        {
            switch (nameId)
            {
                case SfntNameIds.PreferredFamilyName:
                    PreferredFamilyName = value;
                    _preferredFamilyScore = score;
                    break;
                case SfntNameIds.FamilyName:
                    FamilyName = value;
                    _familyScore = score;
                    break;
                case SfntNameIds.FullName:
                    FullName = value;
                    _fullNameScore = score;
                    break;
            }
        }
    }

    private readonly record struct SourceTable(string Tag, uint Checksum, uint Offset, uint Length);

    private readonly record struct ResidentTable(string Tag, uint Checksum, byte[] Data);

    private readonly record struct FaceStyle(int Weight, int Width, bool IsItalic);
}
