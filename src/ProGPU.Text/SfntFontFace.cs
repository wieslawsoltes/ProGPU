using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ProGPU.Text;

#pragma warning disable IDE0078, IDE0300

#if PROGPU_TEXT_PUBLIC
public
#else
internal
#endif
static class SfntNameIds
{
    public const ushort FamilyName = 1;
    public const ushort SubfamilyName = 2;
    public const ushort UniqueFontIdentifier = 3;
    public const ushort FullName = 4;
    public const ushort Version = 5;
    public const ushort PostScriptName = 6;
    public const ushort PreferredFamilyName = 16;
    public const ushort PreferredSubfamilyName = 17;
}

#if PROGPU_TEXT_PUBLIC
public
#else
internal
#endif
readonly struct SfntTableRecord
{
    public SfntTableRecord(string tag, uint checksum, uint offset, uint length)
    {
        Tag = tag;
        Checksum = checksum;
        Offset = offset;
        Length = length;
    }

    public string Tag { get; }
    public uint Checksum { get; }
    public uint Offset { get; }
    public uint Length { get; }
}

#if PROGPU_TEXT_PUBLIC
public
#else
internal
#endif
readonly struct SfntHorizontalGlyphMetrics
{
    public SfntHorizontalGlyphMetrics(ushort advanceWidth, short leftSideBearing)
    {
        AdvanceWidth = advanceWidth;
        LeftSideBearing = leftSideBearing;
    }

    public ushort AdvanceWidth { get; }
    public short LeftSideBearing { get; }
}

#if PROGPU_TEXT_PUBLIC
public
#else
internal
#endif
readonly struct SfntGlyphBounds
{
    public SfntGlyphBounds(short xMin, short yMin, short xMax, short yMax)
    {
        XMin = xMin;
        YMin = yMin;
        XMax = xMax;
        YMax = yMax;
    }

    public short XMin { get; }
    public short YMin { get; }
    public short XMax { get; }
    public short YMax { get; }
}

#if PROGPU_TEXT_PUBLIC
public
#else
internal
#endif
sealed class SfntFontFace
{
    private readonly byte[] _data;
    private readonly Dictionary<string, SfntTableRecord> _tables;

    private SfntFontFace(byte[] data, int faceIndex, uint baseOffset, Dictionary<string, SfntTableRecord> tables)
    {
        _data = data;
        FaceIndex = faceIndex;
        BaseOffset = baseOffset;
        _tables = tables;
    }

    public int FaceIndex { get; }
    public uint BaseOffset { get; }
    public IReadOnlyDictionary<string, SfntTableRecord> Tables => _tables;
    public bool UsesSymbolCharacterMap => TryFindCmapSubtables(out _, out _, out _, out bool usesSymbolCharacterMap) && usesSymbolCharacterMap;

    public static SfntFontFace Load(string filePath, int faceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        return Load(File.ReadAllBytes(filePath), faceIndex);
    }

    public static SfntFontFace Load(byte[] fontData, int faceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(fontData);
        IReadOnlyList<SfntFontFace> faces = LoadFaces(fontData);
        if ((uint)faceIndex >= (uint)faces.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(faceIndex));
        }

        return faces[faceIndex];
    }

    public static IReadOnlyList<SfntFontFace> LoadFaces(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        return LoadFaces(File.ReadAllBytes(filePath));
    }

    public static IReadOnlyList<SfntFontFace> LoadFaces(byte[] fontData)
    {
        ArgumentNullException.ThrowIfNull(fontData);
        fontData = SfntFontContainer.Normalize(fontData);

        uint[] faceOffsets = ReadFaceOffsets(fontData);
        var faces = new List<SfntFontFace>(faceOffsets.Length);
        for (int i = 0; i < faceOffsets.Length; i++)
        {
            faces.Add(ParseFace(fontData, i, faceOffsets[i]));
        }

        return faces;
    }

    public static bool TryLoadFaces(string filePath, out IReadOnlyList<SfntFontFace> faces)
    {
        try
        {
            faces = LoadFaces(filePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException || ex is ArgumentException)
        {
            faces = Array.Empty<SfntFontFace>();
            return false;
        }
    }

    public bool TryGetTable(string tag, out ReadOnlyMemory<byte> table)
    {
        ArgumentNullException.ThrowIfNull(tag);

        if (_tables.TryGetValue(tag, out SfntTableRecord record) &&
            record.Offset <= _data.Length &&
            record.Length <= _data.Length - record.Offset)
        {
            table = _data.AsMemory((int)record.Offset, (int)record.Length);
            return true;
        }

        table = default;
        return false;
    }

    internal byte[] CreateStandaloneFontData()
    {
        SfntTableRecord[] records = _tables.Values
            .OrderBy(static record => record.Tag, StringComparer.Ordinal)
            .ToArray();
        if (records.Length == 0 || records.Length > ushort.MaxValue)
        {
            throw new FormatException("SFNT face does not contain a valid table directory.");
        }

        int directoryLength = checked(12 + records.Length * 16);
        int dataOffset = Align4(directoryLength);
        int resultLength = dataOffset;
        foreach (SfntTableRecord record in records)
        {
            resultLength = Align4(checked(resultLength + checked((int)record.Length)));
        }

        var result = new byte[resultLength];
        if (_tables.ContainsKey("CFF ") || _tables.ContainsKey("CFF2"))
        {
            Encoding.ASCII.GetBytes("OTTO", result.AsSpan(0, 4));
        }
        else
        {
            _data.AsSpan(checked((int)BaseOffset), 4).CopyTo(result);
        }

        ushort tableCount = checked((ushort)records.Length);
        WriteUShort(result, 4, tableCount);
        WriteSearchParameters(result, tableCount);

        int targetOffset = dataOffset;
        for (int i = 0; i < records.Length; i++)
        {
            SfntTableRecord record = records[i];
            int recordOffset = 12 + i * 16;
            Encoding.ASCII.GetBytes(record.Tag, result.AsSpan(recordOffset, 4));
            WriteUInt(result, recordOffset + 4, record.Checksum);
            WriteUInt(result, recordOffset + 8, checked((uint)targetOffset));
            WriteUInt(result, recordOffset + 12, record.Length);
            _data.AsSpan(checked((int)record.Offset), checked((int)record.Length))
                .CopyTo(result.AsSpan(targetOffset));
            targetOffset = Align4(checked(targetOffset + checked((int)record.Length)));
        }

        return result;
    }

    public IReadOnlyList<string> GetNames(ushort nameId)
    {
        if (!TryGetTable("name", out ReadOnlyMemory<byte> tableMemory))
        {
            return Array.Empty<string>();
        }

        ReadOnlySpan<byte> table = tableMemory.Span;
        if (table.Length < 6)
        {
            return Array.Empty<string>();
        }

        ushort count = ReadUShort(table, 2);
        ushort stringOffset = ReadUShort(table, 4);
        int recordsEnd = 6 + count * 12;
        if (recordsEnd > table.Length || stringOffset > table.Length)
        {
            return Array.Empty<string>();
        }

        var candidates = new List<NameCandidate>();
        for (int i = 0; i < count; i++)
        {
            int recordOffset = 6 + i * 12;
            ushort platformId = ReadUShort(table, recordOffset);
            ushort encodingId = ReadUShort(table, recordOffset + 2);
            ushort languageId = ReadUShort(table, recordOffset + 4);
            ushort recordNameId = ReadUShort(table, recordOffset + 6);
            ushort length = ReadUShort(table, recordOffset + 8);
            ushort offset = ReadUShort(table, recordOffset + 10);

            if (recordNameId != nameId)
            {
                continue;
            }

            int valueOffset = stringOffset + offset;
            if (valueOffset > table.Length || length > table.Length - valueOffset)
            {
                continue;
            }

            string value = DecodeName(table.Slice(valueOffset, length), platformId, encodingId);
            if (!string.IsNullOrWhiteSpace(value))
            {
                candidates.Add(new NameCandidate(value, GetNameScore(platformId, languageId, value)));
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public bool TryGetName(ushort nameId, out string name)
    {
        IReadOnlyList<string> names = GetNames(nameId);
        if (names.Count > 0)
        {
            name = names[0];
            return true;
        }

        name = string.Empty;
        return false;
    }

    public bool TryGetGlyphCount(out ushort glyphCount)
    {
        glyphCount = 0;
        if (!TryGetTable("maxp", out ReadOnlyMemory<byte> tableMemory))
        {
            return false;
        }

        ReadOnlySpan<byte> table = tableMemory.Span;
        if (table.Length < 6)
        {
            return false;
        }

        glyphCount = ReadUShort(table, 4);
        return true;
    }

    public bool TryGetEmbeddingRights(out ushort fsType)
    {
        fsType = 0;
        if (!TryGetTable("OS/2", out ReadOnlyMemory<byte> tableMemory))
        {
            return false;
        }

        ReadOnlySpan<byte> table = tableMemory.Span;
        if (table.Length < 10)
        {
            return false;
        }

        fsType = ReadUShort(table, 8);
        return true;
    }

    public bool TryGetGlyphIndex(uint codePoint, out ushort glyphIndex)
    {
        if (!TryGetTable("cmap", out ReadOnlyMemory<byte> cmapMemory))
        {
            glyphIndex = 0;
            return false;
        }

        return TryGetGlyphIndexFromCmap(cmapMemory, codePoint, out glyphIndex);
    }

    internal static bool TryGetGlyphIndexFromCmap(
        ReadOnlyMemory<byte> cmapMemory,
        uint codePoint,
        out ushort glyphIndex)
    {
        glyphIndex = 0;
        if (!TryFindCmapSubtables(
                cmapMemory,
                out ReadOnlyMemory<byte> format4Memory,
                out ReadOnlyMemory<byte> format12Memory,
                out ReadOnlyMemory<byte> format13Memory,
                out _))
        {
            return false;
        }

        if (!format12Memory.IsEmpty && TryGetFormat12GlyphIndex(format12Memory.Span, codePoint, out glyphIndex) &&
            (glyphIndex != 0 || format4Memory.IsEmpty))
        {
            return true;
        }

        if (!format13Memory.IsEmpty && TryGetFormat13GlyphIndex(format13Memory.Span, codePoint, out glyphIndex) &&
            (glyphIndex != 0 || format4Memory.IsEmpty))
        {
            return true;
        }

        if (!format4Memory.IsEmpty && TryGetFormat4GlyphIndex(format4Memory.Span, codePoint, out glyphIndex))
        {
            return true;
        }

        glyphIndex = 0;
        return true;
    }

    public bool TryGetHorizontalGlyphMetrics(ushort glyphIndex, out SfntHorizontalGlyphMetrics metrics)
    {
        metrics = default;
        if (!TryGetTable("hhea", out ReadOnlyMemory<byte> hheaMemory) ||
            !TryGetTable("hmtx", out ReadOnlyMemory<byte> hmtxMemory))
        {
            return false;
        }

        ReadOnlySpan<byte> hhea = hheaMemory.Span;
        ReadOnlySpan<byte> hmtx = hmtxMemory.Span;
        if (hhea.Length < 36)
        {
            return false;
        }

        ushort numberOfHorizontalMetrics = ReadUShort(hhea, 34);
        if (numberOfHorizontalMetrics == 0)
        {
            return false;
        }

        int advanceOffset;
        int leftSideBearingOffset;
        if (glyphIndex < numberOfHorizontalMetrics)
        {
            advanceOffset = glyphIndex * 4;
            leftSideBearingOffset = advanceOffset + 2;
        }
        else
        {
            advanceOffset = (numberOfHorizontalMetrics - 1) * 4;
            leftSideBearingOffset = numberOfHorizontalMetrics * 4 + (glyphIndex - numberOfHorizontalMetrics) * 2;
        }

        if (!CanRead(hmtx, advanceOffset, 2))
        {
            return false;
        }

        ushort advanceWidth = ReadUShort(hmtx, advanceOffset);
        short leftSideBearing = CanRead(hmtx, leftSideBearingOffset, 2)
            ? ReadShort(hmtx, leftSideBearingOffset)
            : (short)0;

        metrics = new SfntHorizontalGlyphMetrics(advanceWidth, leftSideBearing);
        return true;
    }

    public bool TryGetGlyphBounds(ushort glyphIndex, out SfntGlyphBounds bounds)
    {
        bounds = default;
        if (!TryGetTable("head", out ReadOnlyMemory<byte> headMemory) ||
            !TryGetTable("loca", out ReadOnlyMemory<byte> locaMemory) ||
            !TryGetTable("glyf", out ReadOnlyMemory<byte> glyfMemory))
        {
            return false;
        }

        ReadOnlySpan<byte> head = headMemory.Span;
        ReadOnlySpan<byte> loca = locaMemory.Span;
        ReadOnlySpan<byte> glyf = glyfMemory.Span;
        if (head.Length < 52)
        {
            return false;
        }

        short indexToLocFormat = ReadShort(head, 50);
        uint startOffset;
        uint endOffset;
        if (indexToLocFormat == 0)
        {
            int locaOffset = glyphIndex * 2;
            if (!CanRead(loca, locaOffset, 4))
            {
                return false;
            }

            startOffset = (uint)(ReadUShort(loca, locaOffset) * 2);
            endOffset = (uint)(ReadUShort(loca, locaOffset + 2) * 2);
        }
        else if (indexToLocFormat == 1)
        {
            int locaOffset = glyphIndex * 4;
            if (!CanRead(loca, locaOffset, 8))
            {
                return false;
            }

            startOffset = ReadUInt(loca, locaOffset);
            endOffset = ReadUInt(loca, locaOffset + 4);
        }
        else
        {
            return false;
        }

        if (startOffset == endOffset)
        {
            bounds = default;
            return true;
        }

        if (startOffset > glyf.Length || endOffset > glyf.Length || startOffset > endOffset)
        {
            return false;
        }

        int glyphOffset = checked((int)startOffset);
        if (!CanRead(glyf, glyphOffset, 10))
        {
            return false;
        }

        bounds = new SfntGlyphBounds(
            ReadShort(glyf, glyphOffset + 2),
            ReadShort(glyf, glyphOffset + 4),
            ReadShort(glyf, glyphOffset + 6),
            ReadShort(glyf, glyphOffset + 8));
        return true;
    }

    private static uint[] ReadFaceOffsets(byte[] data)
    {
        if (data.Length < 12)
        {
            throw new FormatException("Font data is too short to contain an SFNT header.");
        }

        if (ReadTag(data, 0) != "ttcf")
        {
            return new[] { 0u };
        }

        uint faceCount = ReadUInt(data, 8);
        if (faceCount == 0 || faceCount > int.MaxValue)
        {
            throw new FormatException("TrueType collection has an invalid face count.");
        }

        int offsetsLength = checked((int)faceCount * 4);
        if (12 + offsetsLength > data.Length)
        {
            throw new FormatException("TrueType collection offset table is truncated.");
        }

        var offsets = new uint[faceCount];
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = ReadUInt(data, 12 + i * 4);
        }

        return offsets;
    }

    private static SfntFontFace ParseFace(byte[] data, int faceIndex, uint baseOffset)
    {
        if (baseOffset > data.Length || data.Length - baseOffset < 12)
        {
            throw new FormatException("SFNT face header is outside the font data.");
        }

        ushort tableCount = ReadUShort(data, checked((int)baseOffset + 4));
        int directoryOffset = checked((int)baseOffset + 12);
        int directoryLength = checked(tableCount * 16);
        if (directoryOffset > data.Length || directoryLength > data.Length - directoryOffset)
        {
            throw new FormatException("SFNT table directory is truncated.");
        }

        var tables = new Dictionary<string, SfntTableRecord>(StringComparer.Ordinal);
        for (int i = 0; i < tableCount; i++)
        {
            int recordOffset = directoryOffset + i * 16;
            string tag = ReadTag(data, recordOffset);
            uint checksum = ReadUInt(data, recordOffset + 4);
            uint tableOffset = ReadUInt(data, recordOffset + 8);
            uint tableLength = ReadUInt(data, recordOffset + 12);

            if (tableOffset > data.Length || tableLength > data.Length - tableOffset)
            {
                continue;
            }

            tables[tag] = new SfntTableRecord(tag, checksum, tableOffset, tableLength);
        }

        return new SfntFontFace(data, faceIndex, baseOffset, tables);
    }

    private bool TryFindCmapSubtables(
        out ReadOnlyMemory<byte> format4,
        out ReadOnlyMemory<byte> format12,
        out ReadOnlyMemory<byte> format13,
        out bool usesSymbolCharacterMap)
    {
        if (!TryGetTable("cmap", out ReadOnlyMemory<byte> cmapMemory))
        {
            format4 = default;
            format12 = default;
            format13 = default;
            usesSymbolCharacterMap = false;
            return false;
        }

        return TryFindCmapSubtables(cmapMemory, out format4, out format12, out format13, out usesSymbolCharacterMap);
    }

    private static bool TryFindCmapSubtables(
        ReadOnlyMemory<byte> cmapMemory,
        out ReadOnlyMemory<byte> format4,
        out ReadOnlyMemory<byte> format12,
        out ReadOnlyMemory<byte> format13,
        out bool usesSymbolCharacterMap)
    {
        format4 = default;
        format12 = default;
        format13 = default;
        usesSymbolCharacterMap = false;

        ReadOnlySpan<byte> cmap = cmapMemory.Span;
        if (cmap.Length < 4)
        {
            return false;
        }

        ReadOnlyMemory<byte> symbolFormat4 = default;
        ushort tableCount = ReadUShort(cmap, 2);
        for (int i = 0; i < tableCount; i++)
        {
            int recordOffset = 4 + i * 8;
            if (!CanRead(cmap, recordOffset, 8))
            {
                break;
            }

            ushort platformId = ReadUShort(cmap, recordOffset);
            ushort encodingId = ReadUShort(cmap, recordOffset + 2);
            uint subtableOffset = ReadUInt(cmap, recordOffset + 4);
            if (subtableOffset > cmap.Length || !CanRead(cmap, (int)subtableOffset, 2))
            {
                continue;
            }

            ushort format = ReadUShort(cmap, (int)subtableOffset);
            ReadOnlyMemory<byte> subtable = GetCmapSubtable(cmapMemory, subtableOffset, format);
            if (subtable.IsEmpty)
            {
                continue;
            }

            if (format == 12 && IsUnicodeCmap(platformId, encodingId))
            {
                format12 = subtable;
            }
            else if (format == 13 && IsUnicodeCmap(platformId, encodingId))
            {
                format13 = subtable;
            }
            else if (format == 4 && IsUnicodeCmap(platformId, encodingId))
            {
                format4 = subtable;
                usesSymbolCharacterMap = false;
            }
            else if (format == 4 && platformId == 3 && encodingId == 0 && format4.IsEmpty)
            {
                symbolFormat4 = subtable;
            }
        }

        if (format4.IsEmpty && !symbolFormat4.IsEmpty)
        {
            format4 = symbolFormat4;
            usesSymbolCharacterMap = true;
        }

        return !format4.IsEmpty || !format12.IsEmpty || !format13.IsEmpty;
    }

    private static ReadOnlyMemory<byte> GetCmapSubtable(ReadOnlyMemory<byte> cmap, uint offset, ushort format)
    {
        ReadOnlySpan<byte> span = cmap.Span;
        int subtableOffset = checked((int)offset);
        if (format == 4)
        {
            if (!CanRead(span, subtableOffset, 4))
            {
                return default;
            }

            ushort length = ReadUShort(span, subtableOffset + 2);
            return length > 0 && CanRead(span, subtableOffset, length)
                ? cmap.Slice(subtableOffset, length)
                : default;
        }

        if (format is 12 or 13)
        {
            if (!CanRead(span, subtableOffset, 8))
            {
                return default;
            }

            uint length = ReadUInt(span, subtableOffset + 4);
            return length <= int.MaxValue && length > 0 && CanRead(span, subtableOffset, checked((int)length))
                ? cmap.Slice(subtableOffset, checked((int)length))
                : default;
        }

        return default;
    }

    private static bool TryGetFormat12GlyphIndex(ReadOnlySpan<byte> format12, uint codePoint, out ushort glyphIndex)
    {
        glyphIndex = 0;
        if (format12.Length < 16)
        {
            return false;
        }

        uint groupCount = ReadUInt(format12, 12);
        int low = 0;
        int high = checked((int)groupCount) - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            int offset = 16 + mid * 12;
            if (!CanRead(format12, offset, 12))
            {
                return false;
            }

            uint start = ReadUInt(format12, offset);
            uint end = ReadUInt(format12, offset + 4);
            if (codePoint >= start && codePoint <= end)
            {
                uint value = ReadUInt(format12, offset + 8) + (codePoint - start);
                glyphIndex = value <= ushort.MaxValue ? (ushort)value : (ushort)0;
                return true;
            }

            if (codePoint < start)
            {
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        glyphIndex = 0;
        return true;
    }

    private static bool TryGetFormat13GlyphIndex(ReadOnlySpan<byte> format13, uint codePoint, out ushort glyphIndex)
    {
        glyphIndex = 0;
        if (format13.Length < 16)
        {
            return false;
        }

        uint groupCount = ReadUInt(format13, 12);
        if (groupCount > (uint)((format13.Length - 16) / 12))
        {
            return false;
        }

        int low = 0;
        int high = checked((int)groupCount) - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            int offset = 16 + mid * 12;
            uint start = ReadUInt(format13, offset);
            uint end = ReadUInt(format13, offset + 4);
            if (codePoint >= start && codePoint <= end)
            {
                uint value = ReadUInt(format13, offset + 8);
                glyphIndex = value <= ushort.MaxValue ? (ushort)value : (ushort)0;
                return true;
            }

            if (codePoint < start)
            {
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return true;
    }

    private static bool TryGetFormat4GlyphIndex(ReadOnlySpan<byte> format4, uint codePoint, out ushort glyphIndex)
    {
        glyphIndex = 0;
        if (format4.Length < 14 || codePoint > ushort.MaxValue)
        {
            return false;
        }

        ushort code = (ushort)codePoint;
        ushort segmentCount = (ushort)(ReadUShort(format4, 6) / 2);
        int endCodeOffset = 14;
        int startCodeOffset = endCodeOffset + segmentCount * 2 + 2;
        int deltaOffset = startCodeOffset + segmentCount * 2;
        int rangeOffset = deltaOffset + segmentCount * 2;
        if (!CanRead(format4, rangeOffset, segmentCount * 2))
        {
            return false;
        }

        int segment = -1;
        for (int i = 0; i < segmentCount; i++)
        {
            if (ReadUShort(format4, endCodeOffset + i * 2) >= code)
            {
                segment = i;
                break;
            }
        }

        if (segment < 0 || ReadUShort(format4, startCodeOffset + segment * 2) > code)
        {
            glyphIndex = 0;
            return true;
        }

        short delta = ReadShort(format4, deltaOffset + segment * 2);
        ushort idRangeOffset = ReadUShort(format4, rangeOffset + segment * 2);
        if (idRangeOffset == 0)
        {
            glyphIndex = (ushort)((code + delta) & 0xFFFF);
            return true;
        }

        int rangeOffsetAddress = rangeOffset + segment * 2;
        int glyphIndexAddress = rangeOffsetAddress + idRangeOffset + (code - ReadUShort(format4, startCodeOffset + segment * 2)) * 2;
        if (!CanRead(format4, glyphIndexAddress, 2))
        {
            glyphIndex = 0;
            return true;
        }

        ushort rawIndex = ReadUShort(format4, glyphIndexAddress);
        glyphIndex = rawIndex == 0 ? (ushort)0 : (ushort)((rawIndex + delta) & 0xFFFF);
        return true;
    }

    private static bool IsUnicodeCmap(ushort platformId, ushort encodingId)
    {
        return platformId == 0 ||
               (platformId == 3 && (encodingId == 1 || encodingId == 10));
    }

    internal static string DecodeName(ReadOnlySpan<byte> bytes, ushort platformId, ushort encodingId)
    {
        string value;
        if (platformId == 0 || platformId == 3)
        {
            value = Encoding.BigEndianUnicode.GetString(bytes);
        }
        else if (platformId == 1 && encodingId == 0)
        {
            value = Encoding.Latin1.GetString(bytes);
        }
        else
        {
            value = Encoding.UTF8.GetString(bytes);
        }

        return value.Replace("\0", string.Empty).Trim();
    }

    internal static int GetNameScore(ushort platformId, ushort languageId)
    {
        if (platformId == 3 && languageId == 0x0409)
        {
            return 4;
        }

        if (platformId == 3)
        {
            return 3;
        }

        if (platformId == 0)
        {
            return 2;
        }

        return 1;
    }

    internal static int GetNameScore(ushort platformId, ushort languageId, string value)
    {
        int score = GetNameScore(platformId, languageId);
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (char.IsLetter(ch) && ch > '\u024F')
            {
                return score;
            }
        }

        return score + 10;
    }

    private static string ReadTag(byte[] data, int offset)
    {
        if (offset > data.Length || data.Length - offset < 4)
        {
            throw new FormatException("SFNT tag is truncated.");
        }

        return new string(new[]
        {
            (char)data[offset],
            (char)data[offset + 1],
            (char)data[offset + 2],
            (char)data[offset + 3],
        });
    }

    private static ushort ReadUShort(byte[] data, int offset)
    {
        if (offset > data.Length || data.Length - offset < 2)
        {
            throw new FormatException("SFNT UInt16 value is truncated.");
        }

        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static uint ReadUInt(byte[] data, int offset)
    {
        if (offset > data.Length || data.Length - offset < 4)
        {
            throw new FormatException("SFNT UInt32 value is truncated.");
        }

        return (uint)((data[offset] << 24) |
                      (data[offset + 1] << 16) |
                      (data[offset + 2] << 8) |
                       data[offset + 3]);
    }

    private static ushort ReadUShort(ReadOnlySpan<byte> data, int offset)
    {
        if (offset > data.Length || data.Length - offset < 2)
        {
            throw new FormatException("SFNT UInt16 value is truncated.");
        }

        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static uint ReadUInt(ReadOnlySpan<byte> data, int offset)
    {
        if (offset > data.Length || data.Length - offset < 4)
        {
            throw new FormatException("SFNT UInt32 value is truncated.");
        }

        return (uint)((data[offset] << 24) |
                      (data[offset + 1] << 16) |
                      (data[offset + 2] << 8) |
                       data[offset + 3]);
    }

    private static short ReadShort(ReadOnlySpan<byte> data, int offset)
    {
        return unchecked((short)ReadUShort(data, offset));
    }

    private static int Align4(int value) => checked((value + 3) & ~3);

    private static void WriteSearchParameters(Span<byte> data, ushort tableCount)
    {
        ushort powerOfTwo = 1;
        ushort entrySelector = 0;
        while (powerOfTwo <= tableCount / 2)
        {
            powerOfTwo *= 2;
            entrySelector++;
        }

        ushort searchRange = checked((ushort)(powerOfTwo * 16));
        WriteUShort(data, 6, searchRange);
        WriteUShort(data, 8, entrySelector);
        WriteUShort(data, 10, checked((ushort)(tableCount * 16 - searchRange)));
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

    private static bool CanRead(ReadOnlySpan<byte> data, int offset, int length)
    {
        return offset >= 0 && length >= 0 && offset <= data.Length && length <= data.Length - offset;
    }

    private readonly struct NameCandidate
    {
        public NameCandidate(string value, int score)
        {
            Value = value;
            Score = score;
        }

        public string Value { get; }
        public int Score { get; }
    }
}
