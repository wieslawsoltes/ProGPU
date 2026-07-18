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

/// <summary>One pre-evaluated ItemVariationStore delta keyed by outer/inner indices.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GpuLayoutVariationDelta(uint Key, float Delta);

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
    uint CommandFlags = 0);

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
        short[] normalizedVariationCoordinates,
        GpuLayoutVariationDelta[] variations,
        GpuOpenTypeTableDirectory tables,
        byte[] tableData)
    {
        Cmap = cmap;
        Metrics = metrics;
        NormalizedVariationCoordinates = normalizedVariationCoordinates;
        Variations = variations;
        Tables = tables;
        TableData = tableData;
    }

    public ReadOnlyMemory<GpuCmapRange> Cmap { get; }
    public ReadOnlyMemory<GpuGlyphMetrics> Metrics { get; }
    public ReadOnlyMemory<short> NormalizedVariationCoordinates { get; }
    public ReadOnlyMemory<GpuLayoutVariationDelta> Variations { get; }
    public GpuOpenTypeTableDirectory Tables { get; }
    public ReadOnlyMemory<byte> TableData { get; }

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
        for (uint glyph = 0; glyph < font.GlyphCount; glyph++)
        {
            metrics[checked((int)glyph)] = new GpuGlyphMetrics(
                font.GetHorizontalAdvance(glyph),
                font.GetVerticalAdvance(glyph),
                font.GetHorizontalOrigin(glyph),
                font.GetVerticalOrigin(glyph));
        }
        short[] coordinates = CompileVariationCoordinates(font);
        byte[] tableData = CompileTables(font, out GpuOpenTypeTableDirectory tables);
        GpuLayoutVariationDelta[] variations = CompileVariations(font, tableData, tables);
        return new GpuOpenTypeShapingPlan(cmap, metrics, coordinates, variations, tables, tableData);
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
}
