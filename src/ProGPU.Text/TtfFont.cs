using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using OpenFontSharp;
using OpenFontSharp.Tables.CFF;
using ProGPU.Vector;

namespace ProGPU.Text;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuSegment
{
    public Vector2 P0;
    public Vector2 P1;
    public Vector2 P2;
    public Vector2 P3;
    public uint SegmentType;
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuGlyphRecord
{
    public uint StartSegment;
    public uint SegmentCount;
    public float MinX;
    public float MinY;
    public float MaxX;
    public float MaxY;
    public uint Pad0;
    public uint Pad1;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GlyphUniforms
{
    public float XStart;
    public float YStart;
    public float Scale;
    public uint GlyphIndex;
    
    public uint AtlasX;
    public uint AtlasY;
    public uint Width;
    public uint Height;
    
    public float SubpixelX;
    public float Pad0;
    public float Pad1;
    public float Pad2;
}

public readonly struct BitmapGlyphData
{
    public BitmapGlyphData(
        ushort pixelsPerEm,
        ushort pixelsPerInch,
        short originOffsetX,
        short originOffsetY,
        uint graphicType,
        ReadOnlyMemory<byte> data)
    {
        PixelsPerEm = pixelsPerEm;
        PixelsPerInch = pixelsPerInch;
        OriginOffsetX = originOffsetX;
        OriginOffsetY = originOffsetY;
        GraphicType = graphicType;
        Data = data;
        UsesHorizontalMetrics = false;
        BearingX = 0;
        BearingY = 0;
    }

    private BitmapGlyphData(
        ushort pixelsPerEm,
        short bearingX,
        short bearingY,
        uint graphicType,
        ReadOnlyMemory<byte> data)
    {
        PixelsPerEm = pixelsPerEm;
        PixelsPerInch = 72;
        OriginOffsetX = 0;
        OriginOffsetY = 0;
        GraphicType = graphicType;
        Data = data;
        UsesHorizontalMetrics = true;
        BearingX = bearingX;
        BearingY = bearingY;
    }

    public ushort PixelsPerEm { get; }
    public ushort PixelsPerInch { get; }
    public short OriginOffsetX { get; }
    public short OriginOffsetY { get; }
    public uint GraphicType { get; }
    public ReadOnlyMemory<byte> Data { get; }
    public bool UsesHorizontalMetrics { get; }
    public short BearingX { get; }
    public short BearingY { get; }

    internal static BitmapGlyphData FromHorizontalMetrics(
        ushort pixelsPerEm,
        short bearingX,
        short bearingY,
        uint graphicType,
        ReadOnlyMemory<byte> data) =>
        new(pixelsPerEm, bearingX, bearingY, graphicType, data);
}

public class TtfFont
{
    private const int MaxSvgDocumentBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public const uint JpegBitmapGraphicType = 0x6A706720;
    public const uint PngBitmapGraphicType = 0x706E6720;
    public const uint TiffBitmapGraphicType = 0x74696666;

    private readonly byte[] _data;
    private readonly SfntFontFace _face;
    private readonly Typeface? _cffTypeface;
    private readonly Dictionary<string, (uint offset, uint length)> _tables = new();
    private readonly Dictionary<ushort, List<FontColorLayer>?> _svgColorLayerCache = new();

    public int FaceIndex { get; }
    public string FamilyName { get; }
    public string SubfamilyName { get; }
    public string FullName { get; }
    public string PostScriptName { get; }
    public ushort WeightClass { get; private set; } = 400;
    public ushort WidthClass { get; private set; } = 5;
    public bool IsItalic { get; private set; }
    public bool HasTrueTypeOutlines { get; private set; }
    public bool HasCffOutlines => _cffTypeface is not null;
    public bool HasBitmapGlyphs { get; }
    public bool HasColorGlyphs { get; }
    public bool UsesSymbolCharacterMap => _face.UsesSymbolCharacterMap;
    public bool IsFixedPitch { get; private set; }
    public IReadOnlyCollection<string> TableTags => _tables.Keys;

    // Font parameters
    public ushort UnitsPerEm { get; private set; }
    public short XMin { get; private set; }
    public short YMin { get; private set; }
    public short XMax { get; private set; }
    public short YMax { get; private set; }
    public short Ascender { get; private set; }
    public short Descender { get; private set; }
    public short LineGap { get; private set; }
    public short? UnderlinePosition { get; private set; }
    public short? UnderlineThickness { get; private set; }
    public short? StrikeoutPosition { get; private set; }
    public short? StrikeoutThickness { get; private set; }
    public short? XHeight { get; private set; }
    public short? CapHeight { get; private set; }
    public ushort NumGlyphs { get; private set; }
    public ReadOnlyMemory<byte> FontData => _data;
    private short _indexToLocFormat; // 0 = short (16-bit), 1 = long (32-bit)

    // hmtx metrics
    private ushort _numberOfHMetrics;
    private uint _hmtxOffset;

    // loca and glyf offsets
    private uint _locaOffset;
    private uint _glyfOffset;

    // COLR & CPAL color tables variables
    private uint _colrOffset;
    private uint _cpalOffset;

    // CPAL state
    private ushort _numPaletteEntries;
    private ushort _numPalettes;
    private ushort _numColorRecords;
    private uint _colorRecordsOffset;
    private Vector4[] _colorPalette = null!;

    // COLR state
    private ushort _numBaseGlyphRecords;
    private uint _baseGlyphRecordsOffset;
    private uint _layerRecordsOffset;
    private ushort _numLayerRecords;

    public TtfFont(byte[] fontData)
        : this(fontData, 0)
    {
    }

    public TtfFont(byte[] fontData, int faceIndex)
    {
        ArgumentNullException.ThrowIfNull(fontData);
        ArgumentOutOfRangeException.ThrowIfNegative(faceIndex);
        _data = SfntFontContainer.Normalize(fontData);
        FaceIndex = faceIndex;
        _face = SfntFontFace.Load(_data, faceIndex);
        FamilyName = GetName(SfntNameIds.PreferredFamilyName, SfntNameIds.FamilyName) ?? string.Empty;
        SubfamilyName = GetName(SfntNameIds.PreferredSubfamilyName, SfntNameIds.SubfamilyName) ?? string.Empty;
        FullName = GetName(SfntNameIds.FullName) ?? FamilyName;
        PostScriptName = GetName(SfntNameIds.PostScriptName) ?? string.Empty;
        ParseTableDirectory();
        HasBitmapGlyphs =
            _tables.ContainsKey("sbix") ||
            (_tables.ContainsKey("CBLC") && _tables.ContainsKey("CBDT")) ||
            (_tables.ContainsKey("bloc") && _tables.ContainsKey("bdat"));
        HasColorGlyphs =
            (_tables.ContainsKey("COLR") && _tables.ContainsKey("CPAL")) ||
            _tables.ContainsKey("SVG ");
        if (_tables.ContainsKey("CFF "))
        {
            byte[] cffFontData = _face.BaseOffset == 0
                ? _data
                : _face.CreateStandaloneFontData();
            _cffTypeface = TryLoadCffTypeface(cffFontData);
        }
        ParseHeadTable();
        ParseHheaTable();
        ParseMaxpTable();
        ParseFontAttributes();
        ParsePostMetrics();
        ParseColrTable();
        ParseCpalTable();
    }

    public TtfFont(string filePath) : this(File.ReadAllBytes(filePath))
    {
    }

    public TtfFont(string filePath, int faceIndex) : this(File.ReadAllBytes(filePath), faceIndex)
    {
    }

    #region Big-Endian Readers
    private ushort ReadUShort(uint offset)
    {
        return (ushort)((_data[offset] << 8) | _data[offset + 1]);
    }

    private short ReadShort(uint offset)
    {
        return (short)((_data[offset] << 8) | _data[offset + 1]);
    }

    private uint ReadUInt(uint offset)
    {
        return (uint)((_data[offset] << 24) | 
                      (_data[offset + 1] << 16) | 
                      (_data[offset + 2] << 8) | 
                      _data[offset + 3]);
    }

    private static ushort ReadUShort(ReadOnlySpan<byte> data, int offset)
    {
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static short ReadShort(ReadOnlySpan<byte> data, int offset)
    {
        return unchecked((short)ReadUShort(data, offset));
    }

    private static uint ReadUInt(ReadOnlySpan<byte> data, int offset)
    {
        return (uint)((data[offset] << 24) |
                      (data[offset + 1] << 16) |
                      (data[offset + 2] << 8) |
                      data[offset + 3]);
    }
    #endregion

    private void ParseTableDirectory()
    {
        foreach (var (tag, record) in _face.Tables)
        {
            _tables[tag] = (record.Offset, record.Length);
        }

        if ((!_tables.ContainsKey("head") && !_tables.ContainsKey("bhed")) ||
            !_tables.ContainsKey("cmap"))
        {
            throw new FormatException("Font file is missing essential SFNT tables (head/bhed or cmap).");
        }

        if (_tables.TryGetValue("loca", out var loca) && _tables.TryGetValue("glyf", out var glyf))
        {
            _locaOffset = loca.offset;
            _glyfOffset = glyf.offset;
            HasTrueTypeOutlines = true;
        }
    }

    private string? GetName(params ushort[] nameIds)
    {
        foreach (var nameId in nameIds)
        {
            if (_face.TryGetName(nameId, out var name))
            {
                return name;
            }
        }

        return null;
    }

    private void ParseFontAttributes()
    {
        if (TryGetTable("OS/2", out var os2))
        {
            var span = os2.Span;
            if (span.Length >= 8)
            {
                WeightClass = ReadUShort(span, 4);
                WidthClass = ReadUShort(span, 6);
            }

            if (span.Length >= 64)
            {
                var selection = ReadUShort(span, 62);
                IsItalic = (selection & 0x0001) != 0 || (selection & 0x0200) != 0;
            }

            if (span.Length >= 30)
            {
                StrikeoutThickness = ReadShort(span, 26);
                StrikeoutPosition = ReadShort(span, 28);
            }

            var version = span.Length >= 2 ? ReadUShort(span, 0) : (ushort)0;
            if (version >= 2 && span.Length >= 90)
            {
                XHeight = ReadShort(span, 86);
                CapHeight = ReadShort(span, 88);
            }
        }

        if (!IsItalic &&
            (TryGetTable("head", out var head) || TryGetTable("bhed", out head)) &&
            head.Length >= 46)
        {
            IsItalic = (ReadUShort(head.Span, 44) & 0x0002) != 0;
        }
    }

    private void ParsePostMetrics()
    {
        if (!TryGetTable("post", out var post) || post.Length < 12)
        {
            return;
        }

        UnderlinePosition = ReadShort(post.Span, 8);
        UnderlineThickness = ReadShort(post.Span, 10);
        if (post.Length >= 16)
        {
            IsFixedPitch = ReadUInt(post.Span, 12) != 0;
        }
    }

    private void ParseHeadTable()
    {
        uint headOffset = _tables.TryGetValue("head", out var head)
            ? head.offset
            : _tables["bhed"].offset;
        UnitsPerEm = ReadUShort(headOffset + 18);
        XMin = ReadShort(headOffset + 36);
        YMin = ReadShort(headOffset + 38);
        XMax = ReadShort(headOffset + 40);
        YMax = ReadShort(headOffset + 42);
        _indexToLocFormat = ReadShort(headOffset + 50);
    }

    private void ParseHheaTable()
    {
        if (!_tables.TryGetValue("hhea", out var hhea)) return;
        uint offset = hhea.offset;
        Ascender = ReadShort(offset + 4);
        Descender = ReadShort(offset + 6);
        LineGap = ReadShort(offset + 8);
        _numberOfHMetrics = ReadUShort(offset + 34);

        if (_tables.TryGetValue("hmtx", out var hmtx))
        {
            _hmtxOffset = hmtx.offset;
        }
    }

    private void ParseMaxpTable()
    {
        if (!_tables.TryGetValue("maxp", out var maxp)) return;
        NumGlyphs = ReadUShort(maxp.offset + 4);
    }

    public ushort GetGlyphIndex(char c)
    {
        return GetGlyphIndex((uint)c);
    }

    public ushort GetGlyphIndex(uint codePoint)
    {
        return _face.TryGetGlyphIndex(codePoint, out var glyphIndex) ? glyphIndex : (ushort)0;
    }

    public bool HasGlyph(uint codePoint)
    {
        return GetGlyphIndex(codePoint) != 0;
    }

    public bool TryGetTable(string tag, out ReadOnlyMemory<byte> table)
    {
        return _face.TryGetTable(tag, out table);
    }

    public static int GetFaceCount(byte[] fontData)
    {
        ArgumentNullException.ThrowIfNull(fontData);
        return SfntFontFace.LoadFaces(fontData).Count;
    }

    private static Typeface? TryLoadCffTypeface(byte[] fontData)
    {
        try
        {
            using var stream = new MemoryStream(fontData, writable: false);
            return new OpenFontReader().Read(stream);
        }
        catch (Exception ex) when (ex is OpenFontException or OpenFontNotSupportedException or
                                   InvalidDataException or ArgumentException or IndexOutOfRangeException or
                                   NullReferenceException or OverflowException)
        {
            return null;
        }
    }

    public bool TryGetBitmapGlyph(ushort glyphIndex, float targetPixelsPerEm, out BitmapGlyphData glyph)
    {
        glyph = default;
        if (glyphIndex >= NumGlyphs)
        {
            return false;
        }

        return TryGetSbixBitmapGlyph(glyphIndex, targetPixelsPerEm, out glyph) ||
               TryGetCbdtBitmapGlyph(glyphIndex, targetPixelsPerEm, out glyph);
    }

    private bool TryGetSbixBitmapGlyph(
        ushort glyphIndex,
        float targetPixelsPerEm,
        out BitmapGlyphData glyph)
    {
        glyph = default;
        if (!TryGetTable("sbix", out var sbixMemory))
        {
            return false;
        }

        var sbix = sbixMemory.Span;
        if (sbix.Length < 12 || ReadUShort(sbix, 0) != 1)
        {
            return false;
        }

        var strikeCount = ReadUInt(sbix, 4);
        if (strikeCount == 0 || strikeCount > int.MaxValue ||
            8L + strikeCount * 4L > sbix.Length)
        {
            return false;
        }

        var bestDistance = float.MaxValue;
        BitmapGlyphData best = default;
        var found = false;
        for (var strikeIndex = 0; strikeIndex < (int)strikeCount; strikeIndex++)
        {
            var strikeOffset = ReadUInt(sbix, 8 + strikeIndex * 4);
            var strikeEnd = strikeIndex + 1 < strikeCount
                ? ReadUInt(sbix, 8 + (strikeIndex + 1) * 4)
                : (uint)sbix.Length;
            if (!TryGetBitmapGlyphFromStrike(
                    sbixMemory,
                    strikeOffset,
                    strikeEnd,
                    glyphIndex,
                    out var candidate))
            {
                continue;
            }

            var distance = MathF.Abs(candidate.PixelsPerEm - targetPixelsPerEm);
            if (!found || distance < bestDistance ||
                (distance == bestDistance && candidate.PixelsPerEm > best.PixelsPerEm))
            {
                found = true;
                bestDistance = distance;
                best = candidate;
            }
        }

        glyph = best;
        return found;
    }

    private bool TryGetBitmapGlyphFromStrike(
        ReadOnlyMemory<byte> sbixMemory,
        uint strikeOffset,
        uint strikeEnd,
        ushort glyphIndex,
        out BitmapGlyphData glyph)
    {
        glyph = default;
        var sbix = sbixMemory.Span;
        var offsetTableLength = ((long)NumGlyphs + 1) * 4;
        if (strikeOffset > int.MaxValue || strikeEnd > sbix.Length || strikeOffset >= strikeEnd ||
            strikeOffset + 4L + offsetTableLength > strikeEnd)
        {
            return false;
        }

        var strike = (int)strikeOffset;
        var pixelsPerEm = ReadUShort(sbix, strike);
        var pixelsPerInch = ReadUShort(sbix, strike + 2);
        return TryResolveBitmapGlyph(
            sbixMemory,
            strike,
            (int)strikeEnd,
            pixelsPerEm,
            pixelsPerInch,
            glyphIndex,
            glyphIndex,
            0,
            out glyph);
    }

    private bool TryResolveBitmapGlyph(
        ReadOnlyMemory<byte> sbixMemory,
        int strikeOffset,
        int strikeEnd,
        ushort pixelsPerEm,
        ushort pixelsPerInch,
        ushort glyphIndex,
        ushort originalGlyphIndex,
        int depth,
        out BitmapGlyphData glyph)
    {
        glyph = default;
        if (depth > 16 || glyphIndex >= NumGlyphs)
        {
            return false;
        }

        var sbix = sbixMemory.Span;
        var offsets = strikeOffset + 4;
        var startRelative = ReadUInt(sbix, offsets + glyphIndex * 4);
        var endRelative = ReadUInt(sbix, offsets + (glyphIndex + 1) * 4);
        if (startRelative >= endRelative || startRelative > int.MaxValue || endRelative > int.MaxValue)
        {
            return false;
        }

        var start = strikeOffset + (int)startRelative;
        var end = strikeOffset + (int)endRelative;
        if (start < offsets || end > strikeEnd || end - start < 8)
        {
            return false;
        }

        var originOffsetX = ReadShort(sbix, start);
        var originOffsetY = ReadShort(sbix, start + 2);
        var graphicType = ReadUInt(sbix, start + 4);
        if (graphicType == 0x64757065) // "dupe"
        {
            if (end - start < 10)
            {
                return false;
            }

            var duplicateGlyphIndex = ReadUShort(sbix, start + 8);
            if (duplicateGlyphIndex == originalGlyphIndex ||
                !TryResolveBitmapGlyph(
                    sbixMemory,
                    strikeOffset,
                    strikeEnd,
                    pixelsPerEm,
                    pixelsPerInch,
                    duplicateGlyphIndex,
                    originalGlyphIndex,
                    depth + 1,
                    out var duplicate))
            {
                return false;
            }

            glyph = new BitmapGlyphData(
                pixelsPerEm,
                pixelsPerInch,
                originOffsetX,
                originOffsetY,
                duplicate.GraphicType,
                duplicate.Data);
            return true;
        }

        if (graphicType != PngBitmapGraphicType &&
            graphicType != JpegBitmapGraphicType &&
            graphicType != TiffBitmapGraphicType)
        {
            return false;
        }

        glyph = new BitmapGlyphData(
            pixelsPerEm,
            pixelsPerInch,
            originOffsetX,
            originOffsetY,
            graphicType,
            sbixMemory.Slice(start + 8, end - start - 8));
        return true;
    }

    private bool TryGetCbdtBitmapGlyph(
        ushort glyphIndex,
        float targetPixelsPerEm,
        out BitmapGlyphData glyph)
    {
        glyph = default;
        if (!TryGetTable("CBLC", out var cblcMemory) ||
            !TryGetTable("CBDT", out var cbdtMemory))
        {
            return false;
        }

        var cblc = cblcMemory.Span;
        var cbdt = cbdtMemory.Span;
        if (cblc.Length < 8 || cbdt.Length < 4 ||
            ReadUShort(cblc, 0) != 3 || ReadUShort(cbdt, 0) != 3)
        {
            return false;
        }

        var strikeCount = ReadUInt(cblc, 4);
        if (strikeCount == 0 || strikeCount > int.MaxValue ||
            8L + strikeCount * 48L > cblc.Length)
        {
            return false;
        }

        var bestDistance = float.MaxValue;
        BitmapGlyphData best = default;
        var found = false;
        for (var strikeIndex = 0; strikeIndex < (int)strikeCount; strikeIndex++)
        {
            var strikeOffset = 8 + strikeIndex * 48;
            var firstGlyph = ReadUShort(cblc, strikeOffset + 40);
            var lastGlyph = ReadUShort(cblc, strikeOffset + 42);
            if (glyphIndex < firstGlyph || glyphIndex > lastGlyph)
            {
                continue;
            }

            var pixelsPerEm = cblc[strikeOffset + 45] != 0
                ? cblc[strikeOffset + 45]
                : cblc[strikeOffset + 44];
            if (pixelsPerEm == 0 ||
                !TryGetCbdtBitmapGlyphFromStrike(
                    cblcMemory,
                    cbdtMemory,
                    strikeOffset,
                    glyphIndex,
                    pixelsPerEm,
                    out var candidate))
            {
                continue;
            }

            var distance = MathF.Abs(candidate.PixelsPerEm - targetPixelsPerEm);
            if (!found || distance < bestDistance ||
                (distance == bestDistance && candidate.PixelsPerEm > best.PixelsPerEm))
            {
                found = true;
                bestDistance = distance;
                best = candidate;
            }
        }

        glyph = best;
        return found;
    }

    private static bool TryGetCbdtBitmapGlyphFromStrike(
        ReadOnlyMemory<byte> cblcMemory,
        ReadOnlyMemory<byte> cbdtMemory,
        int strikeOffset,
        ushort glyphIndex,
        ushort pixelsPerEm,
        out BitmapGlyphData glyph)
    {
        glyph = default;
        var cblc = cblcMemory.Span;
        var indexListOffsetValue = ReadUInt(cblc, strikeOffset);
        var indexListSizeValue = ReadUInt(cblc, strikeOffset + 4);
        var subtableCount = ReadUInt(cblc, strikeOffset + 8);
        if (indexListOffsetValue > int.MaxValue || indexListSizeValue > int.MaxValue ||
            subtableCount == 0 || subtableCount > int.MaxValue)
        {
            return false;
        }

        var indexListOffset = (int)indexListOffsetValue;
        var indexListSize = (int)indexListSizeValue;
        var indexListEnd = (long)indexListOffset + indexListSize;
        if (indexListOffset < 0 || indexListSize < 8 || indexListEnd > cblc.Length ||
            (long)subtableCount * 8L > indexListSize)
        {
            return false;
        }

        for (var recordIndex = 0; recordIndex < (int)subtableCount; recordIndex++)
        {
            var recordOffset = indexListOffset + recordIndex * 8;
            var firstGlyph = ReadUShort(cblc, recordOffset);
            var lastGlyph = ReadUShort(cblc, recordOffset + 2);
            if (glyphIndex < firstGlyph || glyphIndex > lastGlyph)
            {
                continue;
            }

            var subtableRelativeOffset = ReadUInt(cblc, recordOffset + 4);
            var subtableOffsetValue = (long)indexListOffset + subtableRelativeOffset;
            if (subtableOffsetValue < indexListOffset || subtableOffsetValue + 8 > indexListEnd)
            {
                return false;
            }

            var subtableOffset = (int)subtableOffsetValue;
            var imageFormat = ReadUShort(cblc, subtableOffset + 2);
            if (!TryResolveCbdtImageRange(
                    cblc,
                    subtableOffset,
                    (int)indexListEnd,
                    firstGlyph,
                    lastGlyph,
                    glyphIndex,
                    out var imageStart,
                    out var imageEnd,
                    out var indexMetrics) ||
                !TryReadCbdtImage(
                    cbdtMemory,
                    imageStart,
                    imageEnd,
                    imageFormat,
                    pixelsPerEm,
                    cblc[strikeOffset + 47],
                    indexMetrics,
                    out glyph))
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private static bool TryResolveCbdtImageRange(
        ReadOnlySpan<byte> cblc,
        int subtableOffset,
        int subtableLimit,
        ushort firstGlyph,
        ushort lastGlyph,
        ushort glyphIndex,
        out long imageStart,
        out long imageEnd,
        out CbdtGlyphMetrics indexMetrics)
    {
        imageStart = 0;
        imageEnd = 0;
        indexMetrics = default;
        var indexFormat = ReadUShort(cblc, subtableOffset);
        var imageDataOffset = ReadUInt(cblc, subtableOffset + 4);
        var glyphOffset = glyphIndex - firstGlyph;
        var glyphCount = (long)lastGlyph - firstGlyph + 1L;
        long relativeStart;
        long relativeEnd;

        switch (indexFormat)
        {
            case 1:
                {
                    var offsetsLength = (glyphCount + 1L) * 4L;
                    if (subtableOffset + 8L + offsetsLength > subtableLimit)
                    {
                        return false;
                    }

                    relativeStart = ReadUInt(cblc, subtableOffset + 8 + glyphOffset * 4);
                    relativeEnd = ReadUInt(cblc, subtableOffset + 12 + glyphOffset * 4);
                    break;
                }
            case 2:
                {
                    if (subtableOffset + 20 > subtableLimit)
                    {
                        return false;
                    }

                    var imageSize = ReadUInt(cblc, subtableOffset + 8);
                    if (imageSize == 0)
                    {
                        return false;
                    }

                    indexMetrics = ReadBigCbdtGlyphMetrics(cblc, subtableOffset + 12);
                    relativeStart = (long)glyphOffset * imageSize;
                    relativeEnd = relativeStart + imageSize;
                    break;
                }
            case 3:
                {
                    var offsetsLength = (glyphCount + 1L) * 2L;
                    if (subtableOffset + 8L + offsetsLength > subtableLimit)
                    {
                        return false;
                    }

                    relativeStart = ReadUShort(cblc, subtableOffset + 8 + glyphOffset * 2);
                    relativeEnd = ReadUShort(cblc, subtableOffset + 10 + glyphOffset * 2);
                    break;
                }
            case 4:
                {
                    if (subtableOffset + 12 > subtableLimit)
                    {
                        return false;
                    }

                    var sparseGlyphCount = ReadUInt(cblc, subtableOffset + 8);
                    if (sparseGlyphCount == 0 || sparseGlyphCount > int.MaxValue ||
                        subtableOffset + 12L + ((long)sparseGlyphCount + 1L) * 4L > subtableLimit)
                    {
                        return false;
                    }

                    var pairOffset = subtableOffset + 12;
                    var found = false;
                    relativeStart = 0;
                    relativeEnd = 0;
                    for (var pairIndex = 0; pairIndex < (int)sparseGlyphCount; pairIndex++)
                    {
                        var currentPair = pairOffset + pairIndex * 4;
                        if (ReadUShort(cblc, currentPair) != glyphIndex)
                        {
                            continue;
                        }

                        relativeStart = ReadUShort(cblc, currentPair + 2);
                        relativeEnd = ReadUShort(cblc, currentPair + 6);
                        found = true;
                        break;
                    }

                    if (!found)
                    {
                        return false;
                    }

                    break;
                }
            case 5:
                {
                    if (subtableOffset + 24 > subtableLimit)
                    {
                        return false;
                    }

                    var imageSize = ReadUInt(cblc, subtableOffset + 8);
                    var sparseGlyphCount = ReadUInt(cblc, subtableOffset + 20);
                    if (imageSize == 0 || sparseGlyphCount == 0 || sparseGlyphCount > int.MaxValue ||
                        subtableOffset + 24L + (long)sparseGlyphCount * 2L > subtableLimit)
                    {
                        return false;
                    }

                    var glyphArrayOffset = subtableOffset + 24;
                    var sparseIndex = -1;
                    for (var pairIndex = 0; pairIndex < (int)sparseGlyphCount; pairIndex++)
                    {
                        if (ReadUShort(cblc, glyphArrayOffset + pairIndex * 2) == glyphIndex)
                        {
                            sparseIndex = pairIndex;
                            break;
                        }
                    }

                    if (sparseIndex < 0)
                    {
                        return false;
                    }

                    indexMetrics = ReadBigCbdtGlyphMetrics(cblc, subtableOffset + 12);
                    relativeStart = (long)sparseIndex * imageSize;
                    relativeEnd = relativeStart + imageSize;
                    break;
                }
            default:
                return false;
        }

        if (relativeStart < 0 || relativeStart >= relativeEnd)
        {
            return false;
        }

        imageStart = imageDataOffset + relativeStart;
        imageEnd = imageDataOffset + relativeEnd;
        return imageStart >= 0 && imageStart < imageEnd;
    }

    private static bool TryReadCbdtImage(
        ReadOnlyMemory<byte> cbdtMemory,
        long imageStartValue,
        long imageEndValue,
        ushort imageFormat,
        ushort pixelsPerEm,
        byte strikeFlags,
        CbdtGlyphMetrics indexMetrics,
        out BitmapGlyphData glyph)
    {
        glyph = default;
        if (imageStartValue > int.MaxValue || imageEndValue > cbdtMemory.Length ||
            imageStartValue < 4 || imageStartValue >= imageEndValue)
        {
            return false;
        }

        var cbdt = cbdtMemory.Span;
        var imageStart = (int)imageStartValue;
        var imageEnd = (int)imageEndValue;
        CbdtGlyphMetrics metrics;
        int dataOffset;
        uint dataLength;
        switch (imageFormat)
        {
            case 17:
                if ((strikeFlags & 0x01) == 0 || imageEnd - imageStart < 9)
                {
                    return false;
                }

                metrics = ReadSmallCbdtGlyphMetrics(cbdt, imageStart);
                dataLength = ReadUInt(cbdt, imageStart + 5);
                dataOffset = imageStart + 9;
                break;
            case 18:
                if (imageEnd - imageStart < 12)
                {
                    return false;
                }

                metrics = ReadBigCbdtGlyphMetrics(cbdt, imageStart);
                dataLength = ReadUInt(cbdt, imageStart + 8);
                dataOffset = imageStart + 12;
                break;
            case 19:
                if (!indexMetrics.IsValid || imageEnd - imageStart < 4)
                {
                    return false;
                }

                metrics = indexMetrics;
                dataLength = ReadUInt(cbdt, imageStart);
                dataOffset = imageStart + 4;
                break;
            default:
                return false;
        }

        if (!metrics.IsValid || dataLength == 0 ||
            (long)dataOffset + dataLength > imageEnd)
        {
            return false;
        }

        glyph = BitmapGlyphData.FromHorizontalMetrics(
            pixelsPerEm,
            metrics.BearingX,
            metrics.BearingY,
            PngBitmapGraphicType,
            cbdtMemory.Slice(dataOffset, (int)dataLength));
        return true;
    }

    private static CbdtGlyphMetrics ReadSmallCbdtGlyphMetrics(ReadOnlySpan<byte> data, int offset) =>
        new(
            data[offset + 1],
            data[offset],
            unchecked((sbyte)data[offset + 2]),
            unchecked((sbyte)data[offset + 3]));

    private static CbdtGlyphMetrics ReadBigCbdtGlyphMetrics(ReadOnlySpan<byte> data, int offset) =>
        new(
            data[offset + 1],
            data[offset],
            unchecked((sbyte)data[offset + 2]),
            unchecked((sbyte)data[offset + 3]));

    private readonly struct CbdtGlyphMetrics
    {
        public CbdtGlyphMetrics(byte width, byte height, sbyte bearingX, sbyte bearingY)
        {
            Width = width;
            Height = height;
            BearingX = bearingX;
            BearingY = bearingY;
        }

        public byte Width { get; }
        public byte Height { get; }
        public short BearingX { get; }
        public short BearingY { get; }
        public bool IsValid => Width != 0 && Height != 0;
    }

    public float GetAdvanceWidth(ushort glyphIndex, float emSize)
    {
        if (_hmtxOffset == 0 || _numberOfHMetrics == 0) return emSize * 0.5f;

        uint offset;
        if (glyphIndex < _numberOfHMetrics)
        {
            offset = _hmtxOffset + (uint)(glyphIndex * 4);
        }
        else
        {
            offset = _hmtxOffset + (uint)((_numberOfHMetrics - 1) * 4);
        }

        ushort advanceWidth = ReadUShort(offset);
        float scale = emSize / UnitsPerEm;
        return advanceWidth * scale;
    }

    public float GetKerning(char left, char right, float emSize)
    {
        return GetKerning((uint)left, (uint)right, emSize);
    }

    public float GetKerning(uint left, uint right, float emSize)
    {
        if (!_tables.TryGetValue("kern", out var kern)) return 0;
        
        uint offset = kern.offset;
        ushort version = ReadUShort(offset);
        ushort nTables = ReadUShort(offset + 2);
        
        uint subtableOffset = offset + 4;
        float scale = emSize / UnitsPerEm;

        ushort leftIdx = GetGlyphIndex(left);
        ushort rightIdx = GetGlyphIndex(right);

        for (int i = 0; i < nTables; i++)
        {
            ushort length = ReadUShort(subtableOffset + 2);
            ushort coverage = ReadUShort(subtableOffset + 4);

            // Subtable Format 0 (sorted list of kerning pairs)
            if ((coverage >> 8) == 0 && (coverage & 1) != 0)
            {
                ushort nPairs = ReadUShort(subtableOffset + 6);
                uint pairsOffset = subtableOffset + 14;

                // Perform binary search for the glyph pair
                uint key = ((uint)leftIdx << 16) | rightIdx;
                int low = 0;
                int high = nPairs - 1;

                while (low <= high)
                {
                    int mid = (low + high) / 2;
                    uint midOffset = pairsOffset + (uint)(mid * 6);
                    uint pairKey = ReadUInt(midOffset);

                    if (pairKey == key)
                    {
                        short value = ReadShort(midOffset + 4);
                        return value * scale;
                    }
                    else if (pairKey < key)
                    {
                        low = mid + 1;
                    }
                    else
                    {
                        high = mid - 1;
                    }
                }
            }
            subtableOffset += length;
        }

        return 0;
    }

    private sealed class ParsedGlyph
    {
        public ParsedGlyph(PathGeometry geometry, Vector2[] points)
        {
            Geometry = geometry;
            Points = points;
        }

        public PathGeometry Geometry { get; }
        public Vector2[] Points { get; }
    }

    private sealed class CffPathTranslator : IGlyphTranslator
    {
        private readonly PathGeometry _geometry = new();
        private PathFigure? _currentFigure;

        public PathGeometry Geometry => _geometry;

        public void BeginRead(int contourCount)
        {
        }

        public void EndRead()
        {
            CloseContour();
        }

        public void MoveTo(float x0, float y0)
        {
            CloseContour();
            _currentFigure = new PathFigure(new Vector2(x0, y0));
            _geometry.Figures.Add(_currentFigure);
        }

        public void LineTo(float x1, float y1)
        {
            EnsureFigure().Segments.Add(new LineSegment(new Vector2(x1, y1)));
        }

        public void Curve3(float x1, float y1, float x2, float y2)
        {
            EnsureFigure().Segments.Add(new QuadraticBezierSegment(
                new Vector2(x1, y1),
                new Vector2(x2, y2)));
        }

        public void Curve4(float x1, float y1, float x2, float y2, float x3, float y3)
        {
            EnsureFigure().Segments.Add(new CubicBezierSegment(
                new Vector2(x1, y1),
                new Vector2(x2, y2),
                new Vector2(x3, y3)));
        }

        public void CloseContour()
        {
            if (_currentFigure is null)
            {
                return;
            }

            _currentFigure.IsClosed = true;
            _currentFigure = null;
        }

        private PathFigure EnsureFigure()
        {
            if (_currentFigure is null)
            {
                MoveTo(0f, 0f);
            }

            return _currentFigure!;
        }
    }

    private readonly Dictionary<ushort, ParsedGlyph?> _glyphOutlineCache = new();
    private readonly Dictionary<ushort, PathGeometry?> _flippedOutlineCache = new();

    public PathGeometry? GetGlyphOutline(ushort glyphIndex)
    {
        lock (_glyphOutlineCache)
        {
            if (_glyphOutlineCache.TryGetValue(glyphIndex, out var cached))
            {
                return cached?.Geometry;
            }

            var result = HasTrueTypeOutlines
                ? GetGlyphOutlineInternal(glyphIndex, new HashSet<ushort>(), 0)
                : GetCffGlyphOutline(glyphIndex);
            _glyphOutlineCache[glyphIndex] = result;
            return result?.Geometry;
        }
    }

    private ParsedGlyph? GetCffGlyphOutline(ushort glyphIndex)
    {
        if (_cffTypeface is null || glyphIndex >= _cffTypeface.GlyphCount)
        {
            return null;
        }

        var glyph = _cffTypeface.GetGlyph(glyphIndex);
        if (!glyph.IsCffGlyph)
        {
            return null;
        }

        var translator = new CffPathTranslator();
        new CffEvaluationEngine().Run(translator, glyph.GetCff1GlyphData());
        return translator.Geometry.Figures.Count == 0
            ? null
            : new ParsedGlyph(translator.Geometry, Array.Empty<Vector2>());
    }

    public PathGeometry? GetFlippedGlyphOutline(ushort glyphIndex)
    {
        lock (_flippedOutlineCache)
        {
            if (_flippedOutlineCache.TryGetValue(glyphIndex, out var cached))
            {
                return cached;
            }

            var rawOutline = GetGlyphOutline(glyphIndex);
            if (rawOutline == null)
            {
                _flippedOutlineCache[glyphIndex] = null;
                return null;
            }

            var flippedOutline = new PathGeometry();
            foreach (var figure in rawOutline.Figures)
            {
                var startPt = new Vector2(figure.StartPoint.X, -figure.StartPoint.Y);
                var newFigure = new PathFigure(startPt, figure.IsClosed) { IsFilled = figure.IsFilled };
                foreach (var segment in figure.Segments)
                {
                    if (segment is LineSegment line)
                    {
                        newFigure.Segments.Add(new LineSegment(new Vector2(line.Point.X, -line.Point.Y)));
                    }
                    else if (segment is QuadraticBezierSegment quad)
                    {
                        newFigure.Segments.Add(new QuadraticBezierSegment(
                            new Vector2(quad.ControlPoint.X, -quad.ControlPoint.Y),
                            new Vector2(quad.Point.X, -quad.Point.Y)
                        ));
                    }
                    else if (segment is CubicBezierSegment cubic)
                    {
                        newFigure.Segments.Add(new CubicBezierSegment(
                            new Vector2(cubic.ControlPoint1.X, -cubic.ControlPoint1.Y),
                            new Vector2(cubic.ControlPoint2.X, -cubic.ControlPoint2.Y),
                            new Vector2(cubic.Point.X, -cubic.Point.Y)
                        ));
                    }
                }
                flippedOutline.Figures.Add(newFigure);
            }

            _flippedOutlineCache[glyphIndex] = flippedOutline;
            return flippedOutline;
        }
    }

    public bool TryGetGlyphBounds(
        ushort glyphIndex,
        out short xMin,
        out short yMin,
        out short xMax,
        out short yMax)
    {
        xMin = 0;
        yMin = 0;
        xMax = 0;
        yMax = 0;
        if (glyphIndex >= NumGlyphs)
        {
            return false;
        }

        if (!HasTrueTypeOutlines)
        {
            var outline = GetGlyphOutline(glyphIndex);
            if (outline is null || !outline.TryGetBounds(out var minimum, out var maximum))
            {
                return false;
            }

            xMin = ToInt16Floor(minimum.X);
            yMin = ToInt16Floor(minimum.Y);
            xMax = ToInt16Ceiling(maximum.X);
            yMax = ToInt16Ceiling(maximum.Y);
            return xMax > xMin && yMax > yMin;
        }

        uint startOffset;
        uint endOffset;
        if (_indexToLocFormat == 0)
        {
            startOffset = (uint)(ReadUShort(_locaOffset + (uint)(glyphIndex * 2)) * 2);
            endOffset = (uint)(ReadUShort(_locaOffset + (uint)((glyphIndex + 1) * 2)) * 2);
        }
        else
        {
            startOffset = ReadUInt(_locaOffset + (uint)(glyphIndex * 4));
            endOffset = ReadUInt(_locaOffset + (uint)((glyphIndex + 1) * 4));
        }

        if (startOffset == endOffset)
        {
            return false;
        }

        var glyphOffset = _glyfOffset + startOffset;
        if (_data.Length < 10 || glyphOffset > (uint)_data.Length - 10u)
        {
            return false;
        }

        xMin = ReadShort(glyphOffset + 2);
        yMin = ReadShort(glyphOffset + 4);
        xMax = ReadShort(glyphOffset + 6);
        yMax = ReadShort(glyphOffset + 8);
        return xMax > xMin && yMax > yMin;
    }

    private static short ToInt16Floor(float value) =>
        (short)Math.Clamp(MathF.Floor(value), short.MinValue, short.MaxValue);

    private static short ToInt16Ceiling(float value) =>
        (short)Math.Clamp(MathF.Ceiling(value), short.MinValue, short.MaxValue);

    private ParsedGlyph? GetGlyphOutlineInternal(ushort glyphIndex, HashSet<ushort> ancestors, int depth)
    {
        if (!HasTrueTypeOutlines || glyphIndex >= NumGlyphs || depth > 32 || !ancestors.Add(glyphIndex))
        {
            return null;
        }

        try
        {
            if (_glyphOutlineCache.TryGetValue(glyphIndex, out var cached))
            {
                return cached;
            }

            var result = ParseGlyphOutline(glyphIndex, ancestors, depth);
            _glyphOutlineCache[glyphIndex] = result;
            return result;
        }
        finally
        {
            ancestors.Remove(glyphIndex);
        }
    }

    private ParsedGlyph? ParseGlyphOutline(ushort glyphIndex, HashSet<ushort> ancestors, int depth)
    {
        uint startOffset = 0;
        uint endOffset = 0;

        if (_indexToLocFormat == 0) // Short offsets
        {
            startOffset = (uint)(ReadUShort(_locaOffset + (uint)(glyphIndex * 2)) * 2);
            endOffset = (uint)(ReadUShort(_locaOffset + (uint)((glyphIndex + 1) * 2)) * 2);
        }
        else // Long offsets
        {
            startOffset = ReadUInt(_locaOffset + (uint)(glyphIndex * 4));
            endOffset = ReadUInt(_locaOffset + (uint)((glyphIndex + 1) * 4));
        }

        if (startOffset == endOffset)
        {
            return null; // Empty glyph (e.g. space)
        }

        uint glyphOffset = _glyfOffset + startOffset;
        short numberOfContours = ReadShort(glyphOffset);

        if (numberOfContours < 0)
        {
            return ParseCompositeGlyphOutline(glyphOffset, ancestors, depth);
        }

        if (numberOfContours == 0)
        {
            return null;
        }

        var geometry = new PathGeometry();
        uint offset = glyphOffset + 10;

        ushort[] endPtsOfContours = new ushort[numberOfContours];
        for (int i = 0; i < numberOfContours; i++)
        {
            endPtsOfContours[i] = ReadUShort(offset);
            offset += 2;
        }

        ushort instructionLength = ReadUShort(offset);
        offset += (uint)(2 + instructionLength); // Skip instructions

        int totalPoints = endPtsOfContours[numberOfContours - 1] + 1;
        byte[] flags = new byte[totalPoints];
        
        // Read Flags
        for (int i = 0; i < totalPoints; i++)
        {
            byte flag = _data[offset++];
            flags[i] = flag;
            
            // Check if flag repeats
            if ((flag & 8) != 0)
            {
                byte repeatCount = _data[offset++];
                for (int r = 0; r < repeatCount; r++)
                {
                    flags[++i] = flag;
                }
            }
        }

        Vector2[] coords = new Vector2[totalPoints];
        
        // Read X Coordinates
        float lastX = 0;
        for (int i = 0; i < totalPoints; i++)
        {
            byte flag = flags[i];
            float xValue = 0;

            if ((flag & 2) != 0) // X Short Vector
            {
                byte val = _data[offset++];
                xValue = ((flag & 16) != 0) ? val : -val;
            }
            else
            {
                if ((flag & 16) != 0) // X Is Same
                {
                    xValue = 0;
                }
                else
                {
                    xValue = ReadShort(offset);
                    offset += 2;
                }
            }
            lastX += xValue;
            coords[i].X = lastX;
        }

        // Read Y Coordinates
        float lastY = 0;
        for (int i = 0; i < totalPoints; i++)
        {
            byte flag = flags[i];
            float yValue = 0;

            if ((flag & 4) != 0) // Y Short Vector
            {
                byte val = _data[offset++];
                yValue = ((flag & 32) != 0) ? val : -val;
            }
            else
            {
                if ((flag & 32) != 0) // Y Is Same
                {
                    yValue = 0;
                }
                else
                {
                    yValue = ReadShort(offset);
                    offset += 2;
                }
            }
            lastY += yValue;
            coords[i].Y = lastY;
        }

        // Process coordinates into PathGeometry (contour by contour)
        int ptIndex = 0;
        for (int c = 0; c < numberOfContours; c++)
        {
            int endPt = endPtsOfContours[c];
            int count = endPt - ptIndex + 1;
            if (count < 2)
            {
                ptIndex = endPt + 1;
                continue;
            }

            Vector2[] contourPoints = new Vector2[count];
            byte[] contourFlags = new byte[count];

            for (int i = 0; i < count; i++)
            {
                contourPoints[i] = coords[ptIndex + i];
                contourFlags[i] = flags[ptIndex + i];
            }

            ptIndex = endPt + 1;

            // Generate PathFigure
            PathFigure figure = DecodeContourToFigure(contourPoints, contourFlags);
            geometry.Figures.Add(figure);
        }

        return new ParsedGlyph(geometry, coords);
    }

    private ParsedGlyph? ParseCompositeGlyphOutline(uint glyphOffset, HashSet<ushort> ancestors, int depth)
    {
        const ushort ArgumentsAreWords = 0x0001;
        const ushort ArgumentsAreXyValues = 0x0002;
        const ushort RoundXyToGrid = 0x0004;
        const ushort WeHaveScale = 0x0008;
        const ushort MoreComponents = 0x0020;
        const ushort WeHaveXAndYScale = 0x0040;
        const ushort WeHaveTwoByTwo = 0x0080;
        const ushort ScaledComponentOffset = 0x0800;

        var geometry = new PathGeometry();
        var points = new List<Vector2>();
        var offset = glyphOffset + 10;
        ushort flags;

        do
        {
            if (offset + 4 > _data.Length)
            {
                return null;
            }

            flags = ReadUShort(offset);
            var componentGlyphIndex = ReadUShort(offset + 2);
            offset += 4;

            int argument1;
            int argument2;
            if ((flags & ArgumentsAreWords) != 0)
            {
                if (offset + 4 > _data.Length)
                {
                    return null;
                }

                if ((flags & ArgumentsAreXyValues) != 0)
                {
                    argument1 = ReadShort(offset);
                    argument2 = ReadShort(offset + 2);
                }
                else
                {
                    argument1 = ReadUShort(offset);
                    argument2 = ReadUShort(offset + 2);
                }
                offset += 4;
            }
            else
            {
                if (offset + 2 > _data.Length)
                {
                    return null;
                }

                if ((flags & ArgumentsAreXyValues) != 0)
                {
                    argument1 = unchecked((sbyte)_data[offset]);
                    argument2 = unchecked((sbyte)_data[offset + 1]);
                }
                else
                {
                    argument1 = _data[offset];
                    argument2 = _data[offset + 1];
                }
                offset += 2;
            }

            var m00 = 1f;
            var m01 = 0f;
            var m10 = 0f;
            var m11 = 1f;
            if ((flags & WeHaveScale) != 0)
            {
                if (offset + 2 > _data.Length)
                {
                    return null;
                }

                m00 = m11 = ReadF2Dot14(offset);
                offset += 2;
            }
            else if ((flags & WeHaveXAndYScale) != 0)
            {
                if (offset + 4 > _data.Length)
                {
                    return null;
                }

                m00 = ReadF2Dot14(offset);
                m11 = ReadF2Dot14(offset + 2);
                offset += 4;
            }
            else if ((flags & WeHaveTwoByTwo) != 0)
            {
                if (offset + 8 > _data.Length)
                {
                    return null;
                }

                m00 = ReadF2Dot14(offset);
                m01 = ReadF2Dot14(offset + 2);
                m10 = ReadF2Dot14(offset + 4);
                m11 = ReadF2Dot14(offset + 6);
                offset += 8;
            }

            var component = GetGlyphOutlineInternal(componentGlyphIndex, ancestors, depth + 1);
            if (component == null)
            {
                continue;
            }

            var linearTransform = new Matrix4x4(
                m00, m10, 0, 0,
                m01, m11, 0, 0,
                0, 0, 1, 0,
                0, 0, 0, 1);

            Vector2 translation;
            if ((flags & ArgumentsAreXyValues) != 0)
            {
                translation = new Vector2(argument1, argument2);
                if ((flags & ScaledComponentOffset) != 0)
                {
                    translation = Vector2.TransformNormal(translation, linearTransform);
                }
            }
            else
            {
                if ((uint)argument1 >= (uint)points.Count ||
                    (uint)argument2 >= (uint)component.Points.Length)
                {
                    continue;
                }

                var componentPoint = Vector2.Transform(component.Points[argument2], linearTransform);
                translation = points[argument1] - componentPoint;
            }

            if ((flags & RoundXyToGrid) != 0)
            {
                translation = new Vector2(MathF.Round(translation.X), MathF.Round(translation.Y));
            }

            linearTransform.M41 = translation.X;
            linearTransform.M42 = translation.Y;
            var transformed = component.Geometry.CreateTransformed(linearTransform);
            geometry.Figures.AddRange(transformed.Figures);
            foreach (var point in component.Points)
            {
                points.Add(Vector2.Transform(point, linearTransform));
            }
        }
        while ((flags & MoreComponents) != 0);

        return geometry.Figures.Count == 0
            ? null
            : new ParsedGlyph(geometry, points.ToArray());
    }

    private float ReadF2Dot14(uint offset)
    {
        return ReadShort(offset) / 16384f;
    }

    private PathFigure DecodeContourToFigure(Vector2[] pts, byte[] flags)
    {
        var figure = new PathFigure();
        int count = pts.Length;

        // Check on-curve flags
        bool IsOnCurve(int idx) => (flags[idx] & 1) != 0;

        // Find starting point on contour
        int startIdx = 0;
        Vector2 startPoint;

        if (IsOnCurve(0))
        {
            startPoint = pts[0];
            startIdx = 1;
        }
        else if (IsOnCurve(count - 1))
        {
            startPoint = pts[count - 1];
            startIdx = 0;
        }
        else
        {
            // Both start and end are off-curve (implicit start point is halfway)
            startPoint = (pts[0] + pts[count - 1]) / 2f;
            startIdx = 0;
        }

        figure.StartPoint = startPoint;
        Vector2 current = startPoint;

        int idx = startIdx;
        int processed = 0;

        while (processed < count)
        {
            int i = idx % count;
            int iNext = (idx + 1) % count;

            Vector2 pt = pts[i];
            bool isOn = IsOnCurve(i);

            if (isOn)
            {
                figure.Segments.Add(new LineSegment(pt));
                current = pt;
                idx++;
                processed++;
            }
            else
            {
                // Quadratic Bezier control point
                Vector2 ctrl = pt;
                Vector2 end;

                if (IsOnCurve(iNext))
                {
                    end = pts[iNext];
                    idx += 2;
                    processed += 2;
                }
                else
                {
                    // Implicit on-curve end point is halfway to next off-curve point
                    end = (ctrl + pts[iNext]) / 2f;
                    idx += 1;
                    processed += 1;
                }

                figure.Segments.Add(new QuadraticBezierSegment(ctrl, end));
                current = end;
            }
        }

        figure.IsClosed = true;
        figure.IsFilled = true;
        return figure;
    }

    private void ParseColrTable()
    {
        if (!_tables.TryGetValue("COLR", out var colr)) return;
        _colrOffset = colr.offset;
        
        ushort version = ReadUShort(_colrOffset);
        if (version == 0)
        {
            _numBaseGlyphRecords = ReadUShort(_colrOffset + 2);
            _baseGlyphRecordsOffset = _colrOffset + ReadUInt(_colrOffset + 4);
            _layerRecordsOffset = _colrOffset + ReadUInt(_colrOffset + 8);
            _numLayerRecords = ReadUShort(_colrOffset + 12);
        }
    }

    private void ParseCpalTable()
    {
        if (!_tables.TryGetValue("CPAL", out var cpal)) return;
        _cpalOffset = cpal.offset;

        ushort version = ReadUShort(_cpalOffset);
        if (version == 0)
        {
            _numPaletteEntries = ReadUShort(_cpalOffset + 2);
            _numPalettes = ReadUShort(_cpalOffset + 4);
            _numColorRecords = ReadUShort(_cpalOffset + 6);
            _colorRecordsOffset = _cpalOffset + ReadUInt(_cpalOffset + 8);

            // Parse default palette (palette 0)
            _colorPalette = new Vector4[_numPaletteEntries];
            
            // Check first palette record index
            ushort firstPaletteRecordIndex = ReadUShort(_cpalOffset + 12);

            for (int i = 0; i < _numPaletteEntries; i++)
            {
                uint recordOffset = _colorRecordsOffset + (uint)((firstPaletteRecordIndex + i) * 4);
                byte b = _data[recordOffset];
                byte g = _data[recordOffset + 1];
                byte r = _data[recordOffset + 2];
                byte a = _data[recordOffset + 3];

                _colorPalette[i] = new Vector4(r / 255.0f, g / 255.0f, b / 255.0f, a / 255.0f);
            }
        }
    }

    public bool HasColorLayers(ushort glyphId)
    {
        if (_numBaseGlyphRecords != 0)
        {
            int low = 0;
            int high = _numBaseGlyphRecords - 1;
            while (low <= high)
            {
                int mid = (low + high) / 2;
                uint recordOffset = _baseGlyphRecordsOffset + (uint)(mid * 6);
                ushort gid = ReadUShort(recordOffset);

                if (gid == glyphId)
                {
                    return true;
                }
                else if (gid < glyphId)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }
        }

        return GetSvgColorLayers(glyphId) is { Count: > 0 };
    }

    public List<FontColorLayer>? GetColorLayers(ushort glyphId)
    {
        return GetColrVersion0Layers(glyphId) ?? GetSvgColorLayers(glyphId);
    }

    private List<FontColorLayer>? GetColrVersion0Layers(ushort glyphId)
    {
        if (_numBaseGlyphRecords == 0) return null;

        int low = 0;
        int high = _numBaseGlyphRecords - 1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            uint recordOffset = _baseGlyphRecordsOffset + (uint)(mid * 6);
            ushort gid = ReadUShort(recordOffset);

            if (gid == glyphId)
            {
                ushort firstLayerIndex = ReadUShort(recordOffset + 2);
                ushort numLayers = ReadUShort(recordOffset + 4);

                var layers = new List<FontColorLayer>(numLayers);
                for (int i = 0; i < numLayers; i++)
                {
                    uint layerOffset = _layerRecordsOffset + (uint)((firstLayerIndex + i) * 4);
                    ushort layerGid = ReadUShort(layerOffset);
                    ushort paletteIndex = ReadUShort(layerOffset + 2);

                    Vector4 color = Vector4.One; // Default color
                    if (paletteIndex < _numPaletteEntries && _colorPalette != null)
                    {
                        color = _colorPalette[paletteIndex];
                    }

                    layers.Add(new FontColorLayer { GlyphId = layerGid, Color = color });
                }
                return layers;
            }
            else if (gid < glyphId)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return null;
    }

    private List<FontColorLayer>? GetSvgColorLayers(ushort glyphId)
    {
        if (!_tables.ContainsKey("SVG "))
        {
            return null;
        }

        lock (_svgColorLayerCache)
        {
            if (_svgColorLayerCache.TryGetValue(glyphId, out var cached))
            {
                return cached;
            }

            List<FontColorLayer>? layers = null;
            if (TryReadSvgGlyphDocument(glyphId, out var xml))
            {
                try
                {
                    layers = OpenTypeSvgGlyphParser.Parse(xml, glyphId, UnitsPerEm);
                }
                catch (Exception ex) when (ex is XmlException or FormatException or
                                           NotSupportedException or InvalidDataException or
                                           ArgumentException or OverflowException)
                {
                    layers = null;
                }
            }

            _svgColorLayerCache[glyphId] = layers;
            return layers;
        }
    }

    private bool TryReadSvgGlyphDocument(ushort glyphId, out string xml)
    {
        xml = string.Empty;
        if (!TryGetTable("SVG ", out var svgMemory))
        {
            return false;
        }

        var svg = svgMemory.Span;
        if (svg.Length < 12 || ReadUShort(svg, 0) != 0)
        {
            return false;
        }

        var listOffsetValue = ReadUInt(svg, 2);
        if (listOffsetValue > int.MaxValue)
        {
            return false;
        }

        var listOffset = (int)listOffsetValue;
        if (listOffset < 10 || listOffset + 2 > svg.Length)
        {
            return false;
        }

        var recordCount = ReadUShort(svg, listOffset);
        if (recordCount == 0 || listOffset + 2L + recordCount * 12L > svg.Length)
        {
            return false;
        }

        for (var recordIndex = 0; recordIndex < recordCount; recordIndex++)
        {
            var recordOffset = listOffset + 2 + recordIndex * 12;
            var firstGlyph = ReadUShort(svg, recordOffset);
            var lastGlyph = ReadUShort(svg, recordOffset + 2);
            if (glyphId < firstGlyph || glyphId > lastGlyph)
            {
                continue;
            }

            var documentOffset = ReadUInt(svg, recordOffset + 4);
            var documentLength = ReadUInt(svg, recordOffset + 8);
            var documentStart = (long)listOffset + documentOffset;
            if (documentOffset == 0 || documentLength == 0 ||
                documentLength > MaxSvgDocumentBytes ||
                documentStart < listOffset ||
                documentStart + documentLength > svg.Length)
            {
                return false;
            }

            var encoded = svgMemory.Slice((int)documentStart, (int)documentLength);
            byte[] decoded;
            if (encoded.Length >= 3 &&
                encoded.Span[0] == 0x1f &&
                encoded.Span[1] == 0x8b &&
                encoded.Span[2] == 0x08)
            {
                if (!TryDecompressSvgDocument(encoded, out decoded))
                {
                    return false;
                }
            }
            else
            {
                decoded = encoded.ToArray();
            }

            try
            {
                xml = StrictUtf8.GetString(decoded);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryDecompressSvgDocument(ReadOnlyMemory<byte> encoded, out byte[] decoded)
    {
        decoded = Array.Empty<byte>();
        try
        {
            using var input = new MemoryStream(encoded.ToArray(), writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = gzip.Read(buffer, 0, buffer.Length);
                if (count == 0)
                {
                    break;
                }
                if (output.Length + count > MaxSvgDocumentBytes)
                {
                    return false;
                }
                output.Write(buffer, 0, count);
            }

            decoded = output.ToArray();
            return decoded.Length != 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return false;
        }
    }

    public (GpuGlyphRecord[] Records, GpuSegment[] Segments) CompileGpuOutlineData()
    {
        var records = new GpuGlyphRecord[NumGlyphs];
        var segments = new List<GpuSegment>();

        for (ushort glyphId = 0; glyphId < NumGlyphs; glyphId++)
        {
            var outline = GetGlyphOutline(glyphId);

            uint startSegment = (uint)segments.Count;
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            if (outline != null)
            {
                foreach (var figure in outline.Figures)
                {
                    if (figure.Segments.Count == 0) continue;

                    Vector2 currentPoint = figure.StartPoint;

                    foreach (var segment in figure.Segments)
                    {
                        if (segment is LineSegment line)
                        {
                            var seg = new GpuSegment
                            {
                                P0 = currentPoint,
                                P1 = line.Point,
                                P2 = Vector2.Zero,
                                SegmentType = 0,
                                Pad0 = 0,
                                Pad1 = 0,
                                Pad2 = 0
                            };
                            segments.Add(seg);

                            UpdateBoundingBoxWithLine(seg.P0, seg.P1, ref minX, ref minY, ref maxX, ref maxY);
                            currentPoint = line.Point;
                        }
                        else if (segment is QuadraticBezierSegment quad)
                        {
                            var seg = new GpuSegment
                            {
                                P0 = currentPoint,
                                P1 = quad.ControlPoint,
                                P2 = quad.Point,
                                SegmentType = 1,
                                Pad0 = 0,
                                Pad1 = 0,
                                Pad2 = 0
                            };
                            segments.Add(seg);

                            UpdateBoundingBoxWithQuad(seg.P0, seg.P1, seg.P2, ref minX, ref minY, ref maxX, ref maxY);
                            currentPoint = quad.Point;
                        }
                        else if (segment is CubicBezierSegment cubic)
                        {
                            var seg = new GpuSegment
                            {
                                P0 = currentPoint,
                                P1 = cubic.ControlPoint1,
                                P2 = cubic.ControlPoint2,
                                P3 = cubic.Point,
                                SegmentType = 2,
                                Pad0 = 0,
                                Pad1 = 0,
                                Pad2 = 0
                            };
                            segments.Add(seg);

                            UpdateBoundingBoxWithCubic(seg.P0, seg.P1, seg.P2, seg.P3, ref minX, ref minY, ref maxX, ref maxY);
                            currentPoint = cubic.Point;
                        }
                    }

                    if (figure.IsClosed && currentPoint != figure.StartPoint)
                    {
                        var seg = new GpuSegment
                        {
                            P0 = currentPoint,
                            P1 = figure.StartPoint,
                            P2 = Vector2.Zero,
                            SegmentType = 0,
                            Pad0 = 0,
                            Pad1 = 0,
                            Pad2 = 0
                        };
                        segments.Add(seg);

                        UpdateBoundingBoxWithLine(seg.P0, seg.P1, ref minX, ref minY, ref maxX, ref maxY);
                        currentPoint = figure.StartPoint;
                    }
                }
            }

            uint segmentCount = (uint)segments.Count - startSegment;

            if (segmentCount > 0)
            {
                records[glyphId] = new GpuGlyphRecord
                {
                    StartSegment = startSegment,
                    SegmentCount = segmentCount,
                    MinX = minX,
                    MinY = minY,
                    MaxX = maxX,
                    MaxY = maxY,
                    Pad0 = 0,
                    Pad1 = 0
                };
            }
            else
            {
                records[glyphId] = new GpuGlyphRecord
                {
                    StartSegment = 0,
                    SegmentCount = 0,
                    MinX = 0,
                    MinY = 0,
                    MaxX = 0,
                    MaxY = 0,
                    Pad0 = 0,
                    Pad1 = 0
                };
            }
        }

        return (records, segments.ToArray());
    }

    private static void UpdateBoundingBoxWithLine(Vector2 p0, Vector2 p1, ref float minX, ref float minY, ref float maxX, ref float maxY)
    {
        UpdateMinMax(p0, ref minX, ref minY, ref maxX, ref maxY);
        UpdateMinMax(p1, ref minX, ref minY, ref maxX, ref maxY);
    }

    private static void UpdateBoundingBoxWithQuad(Vector2 p0, Vector2 p1, Vector2 p2, ref float minX, ref float minY, ref float maxX, ref float maxY)
    {
        UpdateMinMax(p0, ref minX, ref minY, ref maxX, ref maxY);
        UpdateMinMax(p2, ref minX, ref minY, ref maxX, ref maxY);

        // X extremum
        float denomX = p0.X - 2 * p1.X + p2.X;
        if (Math.Abs(denomX) > 1e-6f)
        {
            float t = (p0.X - p1.X) / denomX;
            if (t > 0 && t < 1)
            {
                float x = (1 - t) * (1 - t) * p0.X + 2 * (1 - t) * t * p1.X + t * t * p2.X;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
            }
        }

        // Y extremum
        float denomY = p0.Y - 2 * p1.Y + p2.Y;
        if (Math.Abs(denomY) > 1e-6f)
        {
            float t = (p0.Y - p1.Y) / denomY;
            if (t > 0 && t < 1)
            {
                float y = (1 - t) * (1 - t) * p0.Y + 2 * (1 - t) * t * p1.Y + t * t * p2.Y;
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }
    }

    private static void UpdateBoundingBoxWithCubic(
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3,
        ref float minX,
        ref float minY,
        ref float maxX,
        ref float maxY)
    {
        UpdateMinMax(p0, ref minX, ref minY, ref maxX, ref maxY);
        UpdateMinMax(p1, ref minX, ref minY, ref maxX, ref maxY);
        UpdateMinMax(p2, ref minX, ref minY, ref maxX, ref maxY);
        UpdateMinMax(p3, ref minX, ref minY, ref maxX, ref maxY);
    }

    private static void UpdateMinMax(Vector2 p, ref float minX, ref float minY, ref float maxX, ref float maxY)
    {
        if (p.X < minX) minX = p.X;
        if (p.X > maxX) maxX = p.X;
        if (p.Y < minY) minY = p.Y;
        if (p.Y > maxY) maxY = p.Y;
    }
}

public struct FontColorLayer
{
    public ushort GlyphId;
    public Vector4 Color;
    public PathGeometry? Geometry;
    public Brush? Brush;
    public bool UsesSvgCoordinates;
}
