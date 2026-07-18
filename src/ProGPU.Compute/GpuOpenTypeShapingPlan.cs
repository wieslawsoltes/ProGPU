using System.Runtime.InteropServices;
using ProGPU.Text.Shaping;

namespace ProGPU.Compute;

/// <summary>A compact nominal-character-map interval consumed by WebGPU.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GpuCmapRange(uint Start, uint End, uint Glyph, uint Kind)
{
    public const uint Sequential = 0;
    public const uint Constant = 1;

    public uint Map(uint codePoint) => codePoint < Start || codePoint > End
        ? 0
        : Kind == Constant ? Glyph : checked(Glyph + codePoint - Start);
}

/// <summary>Unscaled design-unit metrics indexed by glyph ID.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GpuGlyphMetrics(
    int AdvanceX,
    int AdvanceY,
    int OriginX,
    int OriginY);

/// <summary>One optional y-up glyph bounding box used only by fallback positioning.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GpuGlyphExtents(
    int XBearing,
    int YBearing,
    int Width,
    int Height,
    uint IsValid);

/// <summary>One pre-evaluated ItemVariationStore delta keyed by outer/inner indices.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GpuLayoutVariationDelta(uint Key, float Delta);

/// <summary>A cmap format-14 default range or non-default UVS mapping.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GpuVariationMapping(uint Start, uint End, uint Selector, uint Glyph)
{
    public const uint DefaultGlyph = uint.MaxValue;
}

/// <summary>Byte ranges for raw big-endian OpenType tables in one upload.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GpuOpenTypeTableDirectory(
    uint GdefOffset,
    uint GdefLength,
    uint GsubOffset,
    uint GsubLength,
    uint GposOffset,
    uint GposLength,
    uint KernOffset,
    uint KernLength);

/// <summary>One sanitized lookup selected for a shaping run.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GpuOpenTypeLookupCommand(
    uint TableKind,
    uint LookupOffset,
    uint LookupType,
    uint LookupFlags,
    uint FeatureTag,
    uint FeatureValue,
    uint RangeStart = 0,
    uint RangeEnd = uint.MaxValue,
    uint CommandFlags = 0,
    uint Stage = 0);

/// <summary>
/// Immutable GPU upload plan for one font face and variation instance. The
/// arrays are built once, are sorted by code point/glyph ID, and can be shared
/// by every shaping run using that face.
/// </summary>
public sealed class GpuOpenTypeShapingPlan
{
    internal GpuOpenTypeShapingPlan(
        GpuCmapRange[] cmap,
        GpuGlyphMetrics[] metrics,
        GpuGlyphExtents[] extents,
        GpuVariationMapping[] variationMappings,
        short[] normalizedVariationCoordinates,
        GpuLayoutVariationDelta[] variations,
        GpuOpenTypeTableDirectory tables,
        byte[] tableData,
        ushort unitsPerEm)
    {
        Cmap = cmap;
        Metrics = metrics;
        Extents = extents;
        VariationMappings = variationMappings;
        NormalizedVariationCoordinates = normalizedVariationCoordinates;
        Variations = variations;
        Tables = tables;
        TableData = tableData;
        UnitsPerEm = unitsPerEm;
    }

    public ReadOnlyMemory<GpuCmapRange> Cmap { get; }
    public ReadOnlyMemory<GpuGlyphMetrics> Metrics { get; }
    public ReadOnlyMemory<GpuGlyphExtents> Extents { get; }
    public ReadOnlyMemory<GpuVariationMapping> VariationMappings { get; }
    public ReadOnlyMemory<short> NormalizedVariationCoordinates { get; }
    public ReadOnlyMemory<GpuLayoutVariationDelta> Variations { get; }
    public GpuOpenTypeTableDirectory Tables { get; }
    public ReadOnlyMemory<byte> TableData { get; }
    public ushort UnitsPerEm { get; }

    public uint GetNominalGlyph(uint codePoint)
    {
        ReadOnlySpan<GpuCmapRange> ranges = Cmap.Span;
        int low = 0;
        int high = ranges.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            GpuCmapRange range = ranges[middle];
            if (codePoint < range.Start) high = middle - 1;
            else if (codePoint > range.End) low = middle + 1;
            else return range.Map(codePoint);
        }
        return 0;
    }
}

/// <summary>Compiles backend-neutral font access into bounded GPU buffers.</summary>
public static class GpuOpenTypeShapingPlanCompiler
{
    public static GpuOpenTypeShapingPlan Compile(IShapingFontFace font)
    {
        ArgumentNullException.ThrowIfNull(font);
        GpuCmapRange[] cmap = CompileCmap(font);
        if (font.GlyphCount > int.MaxValue)
            throw new InvalidOperationException("The font glyph count exceeds managed/GPU buffer limits.");
        var metrics = new GpuGlyphMetrics[checked((int)font.GlyphCount)];
        var extents = new GpuGlyphExtents[metrics.Length];
        for (uint glyph = 0; glyph < font.GlyphCount; glyph++)
        {
            int index = checked((int)glyph);
            metrics[index] = new GpuGlyphMetrics(
                font.GetHorizontalAdvance(glyph),
                font.GetVerticalAdvance(glyph),
                font.GetHorizontalOrigin(glyph),
                font.GetVerticalOrigin(glyph));
            if (font.TryGetGlyphExtents(glyph, out ShapingGlyphExtents value))
            {
                extents[index] = new GpuGlyphExtents(
                    value.XBearing, value.YBearing, value.Width, value.Height, 1);
            }
        }
        GpuVariationMapping[] variationMappings = CompileVariationMappings(font);
        short[] coordinates = CompileVariationCoordinates(font);
        byte[] tableData = CompileTables(font, out GpuOpenTypeTableDirectory tables);
        GpuLayoutVariationDelta[] variations = CompileVariations(font, tableData, tables);
        return new GpuOpenTypeShapingPlan(
            cmap, metrics, extents, variationMappings, coordinates, variations, tables, tableData,
            font.UnitsPerEm);
    }

    private static GpuVariationMapping[] CompileVariationMappings(IShapingFontFace font)
    {
        if (!font.TryGetTable(new OpenTypeTag("cmap"), out ReadOnlyMemory<byte> memory)) return [];
        ReadOnlySpan<byte> data = memory.Span;
        if (!CanRead(data, 0, 4)) return [];
        ushort tableCount = ReadU16(data, 2);
        if (!CanRead(data, 4, tableCount * 8)) return [];
        var result = new Dictionary<(uint Selector, uint Start), GpuVariationMapping>();
        var visited = new HashSet<uint>();
        for (var tableIndex = 0; tableIndex < tableCount; tableIndex++)
        {
            uint relative = ReadU32(data, 4 + tableIndex * 8 + 4);
            if (!visited.Add(relative) || relative > int.MaxValue) continue;
            int subtable = checked((int)relative);
            if (!CanRead(data, subtable, 10) || ReadU16(data, subtable) != 14) continue;
            uint length = ReadU32(data, subtable + 2);
            uint selectorCount = ReadU32(data, subtable + 6);
            if (length > int.MaxValue || selectorCount > int.MaxValue / 11 ||
                !CanRead(data, subtable, checked((int)length)) ||
                !CanRead(data, subtable + 10, checked((int)selectorCount * 11))) continue;
            for (var selectorIndex = 0; selectorIndex < (int)selectorCount; selectorIndex++)
            {
                int record = subtable + 10 + selectorIndex * 11;
                uint selector = ReadU24(data, record);
                uint defaultRelative = ReadU32(data, record + 3);
                uint nonDefaultRelative = ReadU32(data, record + 7);
                if (defaultRelative != 0 && defaultRelative <= int.MaxValue)
                {
                    int ranges = checked(subtable + (int)defaultRelative);
                    if (CanRead(data, ranges, 4))
                    {
                        uint count = ReadU32(data, ranges);
                        if (count <= int.MaxValue / 4 && CanRead(data, ranges + 4, checked((int)count * 4)))
                        {
                            for (var index = 0; index < (int)count; index++)
                            {
                                int item = ranges + 4 + index * 4;
                                uint start = ReadU24(data, item);
                                uint end = start + data[item + 3];
                                result[(selector, start)] = new GpuVariationMapping(
                                    start, end, selector, GpuVariationMapping.DefaultGlyph);
                            }
                        }
                    }
                }
                if (nonDefaultRelative == 0 || nonDefaultRelative > int.MaxValue) continue;
                int mappings = checked(subtable + (int)nonDefaultRelative);
                if (!CanRead(data, mappings, 4)) continue;
                uint mappingCount = ReadU32(data, mappings);
                if (mappingCount > int.MaxValue / 5 ||
                    !CanRead(data, mappings + 4, checked((int)mappingCount * 5))) continue;
                for (var index = 0; index < (int)mappingCount; index++)
                {
                    int item = mappings + 4 + index * 5;
                    uint codePoint = ReadU24(data, item);
                    result[(selector, codePoint)] = new GpuVariationMapping(
                        codePoint, codePoint, selector, ReadU16(data, item + 3));
                }
            }
        }
        return result.Values.OrderBy(static item => item.Selector).ThenBy(static item => item.Start).ToArray();
    }

    private static short[] CompileVariationCoordinates(IShapingFontFace font)
    {
        if (font.VariationAxisCount > int.MaxValue)
            throw new InvalidOperationException("The font variation axis count exceeds managed buffer limits.");
        var result = new short[checked((int)font.VariationAxisCount)];
        for (uint axis = 0; axis < font.VariationAxisCount; axis++)
        {
            if (!font.TryGetNormalizedVariationCoordinate(axis, out result[checked((int)axis)]))
                throw new InvalidOperationException($"The font did not provide normalized variation axis {axis}.");
        }
        return result;
    }

    private static GpuLayoutVariationDelta[] CompileVariations(
        IShapingFontFace font,
        ReadOnlySpan<byte> data,
        GpuOpenTypeTableDirectory tables)
    {
        if (!font.HasActiveVariations || tables.GdefLength < 18 || tables.GdefOffset > int.MaxValue)
            return [];
        int gdef = checked((int)tables.GdefOffset);
        if (!CanRead(data, gdef, 18) || ReadU16(data, gdef) != 1 || ReadU16(data, gdef + 2) < 3)
            return [];
        uint relative = ReadU32(data, gdef + 14);
        if (relative == 0 || relative > int.MaxValue) return [];
        int store = checked(gdef + (int)relative);
        if (!CanRead(data, store, 8) || ReadU16(data, store) != 1) return [];
        ushort outerCount = ReadU16(data, store + 6);
        if (!CanRead(data, store + 8, outerCount * 4)) return [];
        var result = new List<GpuLayoutVariationDelta>();
        for (ushort outer = 0; outer < outerCount; outer++)
        {
            uint dataRelative = ReadU32(data, store + 8 + outer * 4);
            if (dataRelative > int.MaxValue) continue;
            int itemData = checked(store + (int)dataRelative);
            if (!CanRead(data, itemData, 6)) continue;
            ushort innerCount = ReadU16(data, itemData);
            for (ushort inner = 0; inner < innerCount; inner++)
            {
                float delta = font.GetLayoutVariationDelta(outer, inner);
                if (delta != 0f)
                    result.Add(new GpuLayoutVariationDelta((uint)outer << 16 | inner, delta));
            }
        }
        return result.ToArray();
    }

    private static byte[] CompileTables(IShapingFontFace font, out GpuOpenTypeTableDirectory directory)
    {
        var bytes = new List<byte>();
        (uint gdefOffset, uint gdefLength) = Append(new OpenTypeTag("GDEF"));
        (uint gsubOffset, uint gsubLength) = Append(new OpenTypeTag("GSUB"));
        (uint gposOffset, uint gposLength) = Append(new OpenTypeTag("GPOS"));
        (uint kernOffset, uint kernLength) = Append(new OpenTypeTag("kern"));
        directory = new GpuOpenTypeTableDirectory(
            gdefOffset, gdefLength, gsubOffset, gsubLength,
            gposOffset, gposLength, kernOffset, kernLength);
        return bytes.ToArray();

        (uint Offset, uint Length) Append(OpenTypeTag tag)
        {
            while ((bytes.Count & 3) != 0) bytes.Add(0);
            uint offset = checked((uint)bytes.Count);
            if (!font.TryGetTable(tag, out ReadOnlyMemory<byte> table) || table.IsEmpty)
                return (offset, 0);
            bytes.AddRange(table.Span);
            return (offset, checked((uint)table.Length));
        }
    }

    private static GpuCmapRange[] CompileCmap(IShapingFontFace font)
    {
        // A face may select among cmap formats/platform encodings internally.
        // Enumerating Unicode scalar values once preserves that selection and
        // variation-instance policy without duplicating platform preference
        // rules in the GPU package. Adjacent linear mappings are compressed.
        var result = new List<GpuCmapRange>();
        uint rangeStart = 0;
        uint rangeGlyph = 0;
        uint previousCodePoint = 0;
        uint previousGlyph = 0;
        bool hasRange = false;
        for (uint codePoint = 0; codePoint <= 0x10ffff; codePoint++)
        {
            if (codePoint is >= 0xd800 and <= 0xdfff) continue;
            uint glyph = font.GetNominalGlyph(codePoint);
            if (glyph == 0)
            {
                Flush();
                continue;
            }
            if (hasRange && codePoint == previousCodePoint + 1 && glyph == previousGlyph + 1)
            {
                previousCodePoint = codePoint;
                previousGlyph = glyph;
                continue;
            }
            Flush();
            rangeStart = previousCodePoint = codePoint;
            rangeGlyph = previousGlyph = glyph;
            hasRange = true;
        }
        Flush();
        return result.ToArray();

        void Flush()
        {
            if (!hasRange) return;
            result.Add(new GpuCmapRange(rangeStart, previousCodePoint, rangeGlyph, GpuCmapRange.Sequential));
            hasRange = false;
        }
    }

    private static bool CanRead(ReadOnlySpan<byte> data, int offset, int count) =>
        offset >= 0 && count >= 0 && offset <= data.Length - count;
    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)(data[offset] << 8 | data[offset + 1]);
    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
    private static uint ReadU24(ReadOnlySpan<byte> data, int offset) =>
        (uint)(data[offset] << 16 | data[offset + 1] << 8 | data[offset + 2]);
}
