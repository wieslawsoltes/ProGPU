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
        GpuOpenTypeTableDirectory tables,
        byte[] tableData)
    {
        Cmap = cmap;
        Metrics = metrics;
        Tables = tables;
        TableData = tableData;
    }

    public ReadOnlyMemory<GpuCmapRange> Cmap { get; }
    public ReadOnlyMemory<GpuGlyphMetrics> Metrics { get; }
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
        byte[] tableData = CompileTables(font, out GpuOpenTypeTableDirectory tables);
        return new GpuOpenTypeShapingPlan(cmap, metrics, tables, tableData);
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
}
