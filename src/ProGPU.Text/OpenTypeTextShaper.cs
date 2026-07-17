using OpenFontSharp;
using OpenFontSharp.Tables.AdvancedLayout;
using ProGPU.Text.Shaping;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace ProGPU.Text;

public readonly record struct OpenTypeFeatureSetting
{
    public OpenTypeFeatureSetting(string tag, int value = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        if (tag.Length != 4)
        {
            throw new ArgumentException("OpenType feature tags must contain exactly four characters.", nameof(tag));
        }

        Tag = tag;
        Value = value;
    }

    public string Tag { get; }
    public int Value { get; }
}

public sealed class TextShapingOptions
{
    private static readonly OpenTypeFeatureSetting[] s_defaultFeatures =
    [
        new("ccmp"),
        new("locl"),
        new("rlig"),
        new("liga"),
        new("clig"),
        new("calt"),
        new("rclt"),
        new("curs"),
        new("dist"),
        new("kern"),
        new("mark"),
        new("mkmk")
    ];

    public static TextShapingOptions Default { get; } = new();
    public static IReadOnlyList<OpenTypeFeatureSetting> DefaultFeatures => s_defaultFeatures;

    public static TextShapingOptions WithFeatures(params OpenTypeFeatureSetting[] overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < s_defaultFeatures.Length; index++)
        {
            values[s_defaultFeatures[index].Tag] = s_defaultFeatures[index].Value;
        }
        for (var index = 0; index < overrides.Length; index++)
        {
            values[overrides[index].Tag] = overrides[index].Value;
        }
        return new TextShapingOptions
        {
            Features = values.Select(pair => new OpenTypeFeatureSetting(pair.Key, pair.Value)).ToArray()
        };
    }

    public string? Script { get; init; }
    public string? Language { get; init; }
    public ShapingDirection Direction { get; init; }
    public IReadOnlyList<OpenTypeFeatureSetting> Features { get; init; } = s_defaultFeatures;
}

public readonly record struct ShapedGlyph(
    ushort GlyphIndex,
    int Cluster,
    uint CodePoint,
    float AdvanceX,
    float AdvanceY,
    float OffsetX,
    float OffsetY);

public static class OpenTypeTextShaper
{
    private const int MaximumShapedGlyphCount = 1_048_576;

    public static IReadOnlyList<string> GetFeatureTags(TtfFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        Typeface? typeface = font.LayoutTypeface;
        if (typeface is null)
        {
            return [];
        }

        var tags = new HashSet<string>(StringComparer.Ordinal);
        AddFeatureTags(typeface.GSUBTable?.FeatureList, tags);
        AddFeatureTags(typeface.GPOSTable?.FeatureList, tags);
        var result = tags.ToList();
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    public static IReadOnlyList<ShapedGlyph> Shape(
        string text,
        TtfFont font,
        float fontSize,
        TextShapingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        options ??= TextShapingOptions.Default;
        if (text.Length == 0)
        {
            return [];
        }

        string script = options.Script ?? InferScript(text);
        ShapingDirection direction = ResolveDirection(options.Direction, script);
        options = AddDirectionalFeatures(options, direction);

        var substitutions = GlyphSubstitutionBuffer.Create(text, font);
        Typeface? typeface = font.LayoutTypeface;
        if (typeface is not null)
        {
            ApplySubstitutions(font, typeface.GSUBTable, substitutions, options, script);
        }

        var positions = new GlyphPositionBuffer(substitutions, font, direction);
        if (typeface is not null)
        {
            ApplyPositions(font, typeface.GPOSTable, positions, options, script);
        }

        if (direction is ShapingDirection.RightToLeft or ShapingDirection.BottomToTop)
        {
            positions.Reverse();
        }

        float scale = fontSize / font.UnitsPerEm;
        bool vertical = direction is ShapingDirection.TopToBottom or ShapingDirection.BottomToTop;
        var result = new ShapedGlyph[positions.Count];
        for (var index = 0; index < result.Length; index++)
        {
            GlyphRecord record = positions[index];
            float advanceY = record.AdvanceY * scale;
            float offsetX = record.OffsetX * scale;
            float offsetY = record.OffsetY * scale;
            if (vertical && scale != 1f)
            {
                // HarfBuzz scales integer font metrics before applying the
                // vertical-origin integer division. Preserve that order while
                // leaving GPOS deltas in design units for the common path.
                int baseAdvanceY = -(int)MathF.Round(font.GetAdvanceHeight(record.GlyphIndex, font.UnitsPerEm));
                int scaledAdvanceY = -(int)MathF.Round(font.GetAdvanceHeight(record.GlyphIndex, fontSize));
                advanceY = (record.AdvanceY - baseAdvanceY) * scale + scaledAdvanceY;

                int baseOffsetX = -((int)MathF.Round(font.GetAdvanceWidth(record.GlyphIndex, font.UnitsPerEm)) / 2);
                int scaledOffsetX = -((int)MathF.Round(font.GetAdvanceWidth(record.GlyphIndex, fontSize)) / 2);
                offsetX = (record.OffsetX - baseOffsetX) * scale + scaledOffsetX;

                int baseOffsetY = -(int)MathF.Round(font.GetVerticalOriginY(record.GlyphIndex, font.UnitsPerEm));
                int scaledOffsetY = -(int)MathF.Round(font.GetVerticalOriginY(record.GlyphIndex, fontSize));
                offsetY = (record.OffsetY - baseOffsetY) * scale + scaledOffsetY;
            }
            result[index] = new ShapedGlyph(
                record.GlyphIndex,
                record.Cluster,
                record.CodePoint,
                record.AdvanceX * scale,
                -advanceY,
                offsetX,
                -offsetY);
        }

        return result;
    }

    private static ShapingDirection ResolveDirection(ShapingDirection requested, string script)
    {
        if (requested != ShapingDirection.Unspecified) return requested;
        return script is "arab" or "hebr" or "syrc" or "thaa" or "nko " or "adlm" or "rohg"
            ? ShapingDirection.RightToLeft
            : ShapingDirection.LeftToRight;
    }

    private static TextShapingOptions AddDirectionalFeatures(TextShapingOptions options, ShapingDirection direction)
    {
        string[] required = direction switch
        {
            ShapingDirection.TopToBottom or ShapingDirection.BottomToTop => ["vert", "vrt2", "vkrn"],
            ShapingDirection.RightToLeft => ["rtla", "rtlm"],
            _ => ["ltra", "ltrm"]
        };
        var features = new List<OpenTypeFeatureSetting>(options.Features.Count + required.Length);
        foreach (OpenTypeFeatureSetting feature in options.Features)
        {
            if (direction is ShapingDirection.TopToBottom or ShapingDirection.BottomToTop && feature.Tag == "kern")
            {
                continue;
            }
            features.Add(feature);
        }
        foreach (string tag in required)
        {
            if (!features.Any(feature => feature.Tag == tag)) features.Add(new OpenTypeFeatureSetting(tag));
        }
        return new TextShapingOptions
        {
            Script = options.Script,
            Language = options.Language,
            Direction = direction,
            Features = features
        };
    }

    private static void ApplySubstitutions(
        TtfFont font,
        GSUB? table,
        GlyphSubstitutionBuffer glyphs,
        TextShapingOptions options,
        string script)
    {
        if (table?.FeatureList?.featureTables is not { Length: > 0 } features)
        {
            return;
        }

        foreach (EnabledLookup enabled in GetEnabledLookupIndices(table, features, options, script))
        {
            ushort lookupIndex = enabled.LookupIndex;
            if (lookupIndex >= table.LookupList.Count)
            {
                continue;
            }

            GSUB.LookupTable lookup = table.LookupList[lookupIndex];
            for (var position = 0; position < glyphs.Count; position++)
            {
                int countBefore = glyphs.Count;
                lookup.DoSubstitutionAt(glyphs, position, glyphs.Count - position);
                int insertedGlyphs = glyphs.Count - countBefore;
                if (insertedGlyphs > 0)
                {
                    // A multiple substitution is applied once to the original
                    // input glyph. Its newly inserted output must not be fed
                    // back through the same lookup during this pass.
                    position += insertedGlyphs;
                }
            }

            ApplyUnsupportedChainingLookup(font, glyphs, lookupIndex);
            ApplyAlternateLookup(font, glyphs, lookupIndex, enabled.Value);
        }
    }

    private static void ApplyAlternateLookup(
        TtfFont font,
        GlyphSubstitutionBuffer glyphs,
        ushort lookupIndex,
        int featureValue)
    {
        if (featureValue <= 0 || !font.TryGetTable("GSUB", out ReadOnlyMemory<byte> tableMemory))
        {
            return;
        }

        ReadOnlySpan<byte> data = tableMemory.Span;
        if (!TryGetLookup(data, lookupIndex, out int lookupOffset, out ushort lookupType, out int subtableCountOffset))
        {
            return;
        }

        ushort subtableCount = ReadU16(data, subtableCountOffset);
        for (var subtableIndex = 0; subtableIndex < subtableCount; subtableIndex++)
        {
            int offsetPosition = subtableCountOffset + 2 + subtableIndex * 2;
            if (!CanRead(data, offsetPosition, 2))
            {
                break;
            }

            int subtableOffset = lookupOffset + ReadU16(data, offsetPosition);
            ushort effectiveType = lookupType;
            if (effectiveType == 7)
            {
                if (!CanRead(data, subtableOffset, 8) || ReadU16(data, subtableOffset) != 1)
                {
                    continue;
                }

                effectiveType = ReadU16(data, subtableOffset + 2);
                uint extensionOffset = ReadU32(data, subtableOffset + 4);
                if (extensionOffset > int.MaxValue)
                {
                    continue;
                }
                subtableOffset += (int)extensionOffset;
            }

            if (effectiveType != 3 || !CanRead(data, subtableOffset, 6) || ReadU16(data, subtableOffset) != 1)
            {
                continue;
            }

            int coverageOffset = subtableOffset + ReadU16(data, subtableOffset + 2);
            ushort setCount = ReadU16(data, subtableOffset + 4);
            for (var position = 0; position < glyphs.Count; position++)
            {
                int coverageIndex = FindCoverage(data, coverageOffset, glyphs[position]);
                if ((uint)coverageIndex >= setCount ||
                    !CanRead(data, subtableOffset + 6 + coverageIndex * 2, 2))
                {
                    continue;
                }

                int setOffset = subtableOffset + ReadU16(data, subtableOffset + 6 + coverageIndex * 2);
                if (!CanRead(data, setOffset, 2))
                {
                    continue;
                }

                int alternateCount = ReadU16(data, setOffset);
                if (alternateCount == 0)
                {
                    continue;
                }

                int alternateIndex = Math.Clamp(featureValue, 1, alternateCount) - 1;
                int alternateOffset = setOffset + 2 + alternateIndex * 2;
                if (CanRead(data, alternateOffset, 2))
                {
                    glyphs.Replace(position, ReadU16(data, alternateOffset));
                }
            }
        }
    }

    private static void ApplyUnsupportedChainingLookup(
        TtfFont font,
        GlyphSubstitutionBuffer glyphs,
        ushort lookupIndex)
    {
        if (!font.TryGetTable("GSUB", out ReadOnlyMemory<byte> tableMemory))
        {
            return;
        }

        ReadOnlySpan<byte> data = tableMemory.Span;
        if (!TryGetLookup(data, lookupIndex, out int lookupOffset, out ushort lookupType, out int subtableCountOffset) ||
            lookupType != 6)
        {
            return;
        }

        ushort subtableCount = ReadU16(data, subtableCountOffset);
        for (var subtableIndex = 0; subtableIndex < subtableCount; subtableIndex++)
        {
            int offsetPosition = subtableCountOffset + 2 + subtableIndex * 2;
            int subtableOffset = lookupOffset + ReadU16(data, offsetPosition);
            if (!CanRead(data, subtableOffset, 2))
            {
                continue;
            }

            ushort format = ReadU16(data, subtableOffset);
            if (format is not (1 or 2))
            {
                continue;
            }

            for (var position = 0; position < glyphs.Count; position++)
            {
                if (format == 1)
                {
                    ApplyChainContextFormat1(data, glyphs, subtableOffset, position);
                }
                else
                {
                    ApplyChainContextFormat2(data, glyphs, subtableOffset, position);
                }
            }
        }
    }

    private static bool ApplyChainContextFormat1(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int subtableOffset,
        int position)
    {
        if (!CanRead(data, subtableOffset, 6))
        {
            return false;
        }

        int coverageOffset = subtableOffset + ReadU16(data, subtableOffset + 2);
        int coverageIndex = FindCoverage(data, coverageOffset, glyphs[position]);
        ushort ruleSetCount = ReadU16(data, subtableOffset + 4);
        if ((uint)coverageIndex >= ruleSetCount || !CanRead(data, subtableOffset + 6 + coverageIndex * 2, 2))
        {
            return false;
        }

        ushort setRelative = ReadU16(data, subtableOffset + 6 + coverageIndex * 2);
        if (setRelative == 0)
        {
            return false;
        }

        int setOffset = subtableOffset + setRelative;
        if (!CanRead(data, setOffset, 2))
        {
            return false;
        }

        ushort ruleCount = ReadU16(data, setOffset);
        for (var ruleIndex = 0; ruleIndex < ruleCount; ruleIndex++)
        {
            if (!CanRead(data, setOffset + 2 + ruleIndex * 2, 2))
            {
                break;
            }

            int ruleOffset = setOffset + ReadU16(data, setOffset + 2 + ruleIndex * 2);
            if (MatchChainRule(data, glyphs, ruleOffset, position, useClasses: false, 0, 0, 0, out int recordsOffset, out ushort recordCount))
            {
                return ApplySubstitutionRecords(data, glyphs, position, recordsOffset, recordCount);
            }
        }
        return false;
    }

    private static bool ApplyChainContextFormat2(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int subtableOffset,
        int position)
    {
        if (!CanRead(data, subtableOffset, 12))
        {
            return false;
        }

        int coverageOffset = subtableOffset + ReadU16(data, subtableOffset + 2);
        if (FindCoverage(data, coverageOffset, glyphs[position]) < 0)
        {
            return false;
        }

        int backtrackClassDef = subtableOffset + ReadU16(data, subtableOffset + 4);
        int inputClassDef = subtableOffset + ReadU16(data, subtableOffset + 6);
        int lookaheadClassDef = subtableOffset + ReadU16(data, subtableOffset + 8);
        ushort setCount = ReadU16(data, subtableOffset + 10);
        int firstClass = GetGlyphClass(data, inputClassDef, glyphs[position]);
        if ((uint)firstClass >= setCount || !CanRead(data, subtableOffset + 12 + firstClass * 2, 2))
        {
            return false;
        }

        ushort setRelative = ReadU16(data, subtableOffset + 12 + firstClass * 2);
        if (setRelative == 0)
        {
            return false;
        }

        int setOffset = subtableOffset + setRelative;
        if (!CanRead(data, setOffset, 2))
        {
            return false;
        }

        ushort ruleCount = ReadU16(data, setOffset);
        for (var ruleIndex = 0; ruleIndex < ruleCount; ruleIndex++)
        {
            if (!CanRead(data, setOffset + 2 + ruleIndex * 2, 2))
            {
                break;
            }

            int ruleOffset = setOffset + ReadU16(data, setOffset + 2 + ruleIndex * 2);
            if (MatchChainRule(
                    data,
                    glyphs,
                    ruleOffset,
                    position,
                    useClasses: true,
                    backtrackClassDef,
                    inputClassDef,
                    lookaheadClassDef,
                    out int recordsOffset,
                    out ushort recordCount))
            {
                return ApplySubstitutionRecords(data, glyphs, position, recordsOffset, recordCount);
            }
        }
        return false;
    }

    private static bool MatchChainRule(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int ruleOffset,
        int position,
        bool useClasses,
        int backtrackClassDef,
        int inputClassDef,
        int lookaheadClassDef,
        out int recordsOffset,
        out ushort recordCount)
    {
        recordsOffset = 0;
        recordCount = 0;
        if (!CanRead(data, ruleOffset, 2))
        {
            return false;
        }

        int cursor = ruleOffset;
        ushort backtrackCount = ReadU16(data, cursor);
        cursor += 2;
        if (position < backtrackCount || !CanRead(data, cursor, backtrackCount * 2))
        {
            return false;
        }
        for (var index = 0; index < backtrackCount; index++)
        {
            ushort expected = ReadU16(data, cursor + index * 2);
            ushort actualGlyph = glyphs[position - index - 1];
            int actual = useClasses ? GetGlyphClass(data, backtrackClassDef, actualGlyph) : actualGlyph;
            if (actual != expected)
            {
                return false;
            }
        }
        cursor += backtrackCount * 2;

        if (!CanRead(data, cursor, 2))
        {
            return false;
        }
        ushort inputCount = ReadU16(data, cursor);
        cursor += 2;
        int trailingInputCount = Math.Max(0, inputCount - 1);
        if (position + inputCount > glyphs.Count || !CanRead(data, cursor, trailingInputCount * 2))
        {
            return false;
        }
        for (var index = 0; index < trailingInputCount; index++)
        {
            ushort expected = ReadU16(data, cursor + index * 2);
            ushort actualGlyph = glyphs[position + index + 1];
            int actual = useClasses ? GetGlyphClass(data, inputClassDef, actualGlyph) : actualGlyph;
            if (actual != expected)
            {
                return false;
            }
        }
        cursor += trailingInputCount * 2;

        if (!CanRead(data, cursor, 2))
        {
            return false;
        }
        ushort lookaheadCount = ReadU16(data, cursor);
        cursor += 2;
        if (position + inputCount + lookaheadCount > glyphs.Count || !CanRead(data, cursor, lookaheadCount * 2))
        {
            return false;
        }
        for (var index = 0; index < lookaheadCount; index++)
        {
            ushort expected = ReadU16(data, cursor + index * 2);
            ushort actualGlyph = glyphs[position + inputCount + index];
            int actual = useClasses ? GetGlyphClass(data, lookaheadClassDef, actualGlyph) : actualGlyph;
            if (actual != expected)
            {
                return false;
            }
        }
        cursor += lookaheadCount * 2;

        if (!CanRead(data, cursor, 2))
        {
            return false;
        }
        recordCount = ReadU16(data, cursor);
        recordsOffset = cursor + 2;
        return CanRead(data, recordsOffset, recordCount * 4);
    }

    private static bool ApplySubstitutionRecords(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int position,
        int recordsOffset,
        ushort recordCount)
    {
        bool changed = false;
        for (var recordIndex = 0; recordIndex < recordCount; recordIndex++)
        {
            int recordOffset = recordsOffset + recordIndex * 4;
            ushort sequenceIndex = ReadU16(data, recordOffset);
            ushort lookupIndex = ReadU16(data, recordOffset + 2);
            int target = position + sequenceIndex;
            if (target < glyphs.Count)
            {
                changed |= ApplyNestedLookup(data, glyphs, lookupIndex, target);
            }
        }
        return changed;
    }

    private static bool ApplyNestedLookup(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        ushort lookupIndex,
        int position)
    {
        if (!TryGetLookup(data, lookupIndex, out int lookupOffset, out ushort lookupType, out int subtableCountOffset))
        {
            return false;
        }

        ushort subtableCount = ReadU16(data, subtableCountOffset);
        for (var subtableIndex = 0; subtableIndex < subtableCount; subtableIndex++)
        {
            int subtableOffset = lookupOffset + ReadU16(data, subtableCountOffset + 2 + subtableIndex * 2);
            if (ApplyNestedSubtable(data, glyphs, lookupType, subtableOffset, position))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ApplyNestedSubtable(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        ushort lookupType,
        int offset,
        int position)
    {
        if (!CanRead(data, offset, 6))
        {
            return false;
        }

        ushort format = ReadU16(data, offset);
        if (lookupType == 7)
        {
            if (format != 1 || !CanRead(data, offset, 8)) return false;
            ushort extensionType = ReadU16(data, offset + 2);
            uint extensionOffset = ReadU32(data, offset + 4);
            if (extensionOffset > int.MaxValue) return false;
            return ApplyNestedSubtable(data, glyphs, extensionType, offset + (int)extensionOffset, position);
        }

        if (lookupType != 1)
        {
            return false;
        }

        int coverageOffset = offset + ReadU16(data, offset + 2);
        int coverageIndex = FindCoverage(data, coverageOffset, glyphs[position]);
        if (coverageIndex < 0)
        {
            return false;
        }

        if (format == 1)
        {
            short delta = ReadI16(data, offset + 4);
            glyphs.Replace(position, unchecked((ushort)(glyphs[position] + delta)));
            return true;
        }
        if (format == 2 && CanRead(data, offset + 4, 2))
        {
            ushort count = ReadU16(data, offset + 4);
            if ((uint)coverageIndex < count && CanRead(data, offset + 6 + coverageIndex * 2, 2))
            {
                glyphs.Replace(position, ReadU16(data, offset + 6 + coverageIndex * 2));
                return true;
            }
        }
        return false;
    }

    private static bool TryGetLookup(
        ReadOnlySpan<byte> data,
        ushort lookupIndex,
        out int lookupOffset,
        out ushort lookupType,
        out int subtableCountOffset)
    {
        lookupOffset = 0;
        lookupType = 0;
        subtableCountOffset = 0;
        if (!CanRead(data, 8, 2))
        {
            return false;
        }

        int lookupListOffset = ReadU16(data, 8);
        if (!CanRead(data, lookupListOffset, 2))
        {
            return false;
        }
        ushort lookupCount = ReadU16(data, lookupListOffset);
        if (lookupIndex >= lookupCount || !CanRead(data, lookupListOffset + 2 + lookupIndex * 2, 2))
        {
            return false;
        }

        lookupOffset = lookupListOffset + ReadU16(data, lookupListOffset + 2 + lookupIndex * 2);
        if (!CanRead(data, lookupOffset, 6))
        {
            return false;
        }
        lookupType = ReadU16(data, lookupOffset);
        subtableCountOffset = lookupOffset + 4;
        return true;
    }

    private static int FindCoverage(ReadOnlySpan<byte> data, int offset, ushort glyph)
    {
        if (!CanRead(data, offset, 4)) return -1;
        ushort format = ReadU16(data, offset);
        ushort count = ReadU16(data, offset + 2);
        if (format == 1)
        {
            var low = 0;
            var high = count - 1;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                ushort value = ReadU16(data, offset + 4 + middle * 2);
                if (value == glyph) return middle;
                if (value < glyph) low = middle + 1;
                else high = middle - 1;
            }
        }
        else if (format == 2)
        {
            for (var index = 0; index < count; index++)
            {
                int rangeOffset = offset + 4 + index * 6;
                if (!CanRead(data, rangeOffset, 6)) return -1;
                ushort start = ReadU16(data, rangeOffset);
                ushort end = ReadU16(data, rangeOffset + 2);
                if (glyph >= start && glyph <= end)
                {
                    return ReadU16(data, rangeOffset + 4) + glyph - start;
                }
            }
        }
        return -1;
    }

    private static int GetGlyphClass(ReadOnlySpan<byte> data, int offset, ushort glyph)
    {
        if (!CanRead(data, offset, 4)) return 0;
        ushort format = ReadU16(data, offset);
        if (format == 1)
        {
            if (!CanRead(data, offset, 6)) return 0;
            ushort start = ReadU16(data, offset + 2);
            ushort count = ReadU16(data, offset + 4);
            int index = glyph - start;
            return (uint)index < count && CanRead(data, offset + 6 + index * 2, 2)
                ? ReadU16(data, offset + 6 + index * 2)
                : 0;
        }
        if (format == 2)
        {
            ushort count = ReadU16(data, offset + 2);
            for (var index = 0; index < count; index++)
            {
                int rangeOffset = offset + 4 + index * 6;
                if (!CanRead(data, rangeOffset, 6)) return 0;
                ushort start = ReadU16(data, rangeOffset);
                ushort end = ReadU16(data, rangeOffset + 2);
                if (glyph >= start && glyph <= end)
                {
                    return ReadU16(data, rangeOffset + 4);
                }
            }
        }
        return 0;
    }

    private static bool CanRead(ReadOnlySpan<byte> data, int offset, int length) =>
        offset >= 0 && length >= 0 && offset <= data.Length - length;

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static short ReadI16(ReadOnlySpan<byte> data, int offset) => unchecked((short)ReadU16(data, offset));

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

    private static void ApplyPositions(
        TtfFont font,
        GPOS? table,
        GlyphPositionBuffer glyphs,
        TextShapingOptions options,
        string script)
    {
        if (table?.FeatureList?.featureTables is not { Length: > 0 } features)
        {
            return;
        }

        foreach (EnabledLookup enabled in GetEnabledLookupIndices(table, features, options, script))
        {
            ushort lookupIndex = enabled.LookupIndex;
            if (lookupIndex < table.LookupList.Count)
            {
                table.LookupList[lookupIndex].DoGlyphPosition(glyphs, 0, glyphs.Count);
                ApplyPositionVariations(font, glyphs, lookupIndex);
            }
        }
    }

    private static void ApplyPositionVariations(
        TtfFont font,
        GlyphPositionBuffer glyphs,
        ushort lookupIndex)
    {
        if (!font.HasActiveFontVariations ||
            !font.TryGetTable("GPOS", out ReadOnlyMemory<byte> tableMemory))
        {
            return;
        }

        ReadOnlySpan<byte> data = tableMemory.Span;
        if (!TryGetLookup(data, lookupIndex, out int lookupOffset, out ushort lookupType, out int subtableCountOffset))
        {
            return;
        }

        ushort subtableCount = ReadU16(data, subtableCountOffset);
        for (var subtableIndex = 0; subtableIndex < subtableCount; subtableIndex++)
        {
            int offsetPosition = subtableCountOffset + 2 + subtableIndex * 2;
            if (!CanRead(data, offsetPosition, 2)) break;
            int subtableOffset = lookupOffset + ReadU16(data, offsetPosition);
            ushort effectiveType = lookupType;
            if (effectiveType == 9)
            {
                if (!CanRead(data, subtableOffset, 8) || ReadU16(data, subtableOffset) != 1) continue;
                effectiveType = ReadU16(data, subtableOffset + 2);
                uint extensionOffset = ReadU32(data, subtableOffset + 4);
                if (extensionOffset > int.MaxValue) continue;
                subtableOffset += (int)extensionOffset;
            }

            switch (effectiveType)
            {
                case 1:
                    ApplySinglePositionVariations(data, font, glyphs, subtableOffset);
                    break;
                case 2:
                    ApplyPairPositionVariations(data, font, glyphs, subtableOffset);
                    break;
                case 4:
                    ApplyMarkToBaseVariations(data, font, glyphs, subtableOffset);
                    break;
                case 6:
                    ApplyMarkToMarkVariations(data, font, glyphs, subtableOffset);
                    break;
            }
        }
    }

    private static void ApplySinglePositionVariations(
        ReadOnlySpan<byte> data,
        TtfFont font,
        GlyphPositionBuffer glyphs,
        int offset)
    {
        if (!CanRead(data, offset, 6)) return;
        ushort format = ReadU16(data, offset);
        int coverageOffset = offset + ReadU16(data, offset + 2);
        ushort valueFormat = ReadU16(data, offset + 4);
        int valueSize = GetValueRecordSize(valueFormat);
        for (var index = 0; index < glyphs.Count; index++)
        {
            int coverageIndex = FindCoverage(data, coverageOffset, glyphs.GetGlyph(index));
            if (coverageIndex < 0) continue;
            int recordOffset;
            if (format == 1)
            {
                recordOffset = offset + 6;
            }
            else if (format == 2 && CanRead(data, offset + 6, 2) && coverageIndex < ReadU16(data, offset + 6))
            {
                recordOffset = offset + 8 + coverageIndex * valueSize;
            }
            else
            {
                continue;
            }
            ApplyValueVariation(data, font, glyphs, index, recordOffset, valueFormat, offset);
        }
    }

    private static void ApplyPairPositionVariations(
        ReadOnlySpan<byte> data,
        TtfFont font,
        GlyphPositionBuffer glyphs,
        int offset)
    {
        if (!CanRead(data, offset, 10)) return;
        ushort format = ReadU16(data, offset);
        int coverageOffset = offset + ReadU16(data, offset + 2);
        ushort valueFormat1 = ReadU16(data, offset + 4);
        ushort valueFormat2 = ReadU16(data, offset + 6);
        int valueSize1 = GetValueRecordSize(valueFormat1);
        int valueSize2 = GetValueRecordSize(valueFormat2);
        for (var index = 0; index + 1 < glyphs.Count; index++)
        {
            int coverageIndex = FindCoverage(data, coverageOffset, glyphs.GetGlyph(index));
            if (coverageIndex < 0) continue;
            int value1Offset;
            int value2Offset;
            if (format == 1)
            {
                ushort setCount = ReadU16(data, offset + 8);
                if ((uint)coverageIndex >= setCount || !CanRead(data, offset + 10 + coverageIndex * 2, 2)) continue;
                int setOffset = offset + ReadU16(data, offset + 10 + coverageIndex * 2);
                if (!CanRead(data, setOffset, 2)) continue;
                int pairCount = ReadU16(data, setOffset);
                int recordSize = 2 + valueSize1 + valueSize2;
                int recordOffset = setOffset + 2;
                bool found = false;
                for (var pair = 0; pair < pairCount; pair++, recordOffset += recordSize)
                {
                    if (!CanRead(data, recordOffset, recordSize)) break;
                    if (ReadU16(data, recordOffset) != glyphs.GetGlyph(index + 1)) continue;
                    value1Offset = recordOffset + 2;
                    value2Offset = value1Offset + valueSize1;
                    ApplyValueVariation(data, font, glyphs, index, value1Offset, valueFormat1, offset);
                    ApplyValueVariation(data, font, glyphs, index + 1, value2Offset, valueFormat2, offset);
                    found = true;
                    break;
                }
                if (!found) continue;
            }
            else if (format == 2 && CanRead(data, offset, 16))
            {
                int class1Def = offset + ReadU16(data, offset + 8);
                int class2Def = offset + ReadU16(data, offset + 10);
                int class1Count = ReadU16(data, offset + 12);
                int class2Count = ReadU16(data, offset + 14);
                int class1 = GetGlyphClass(data, class1Def, glyphs.GetGlyph(index));
                int class2 = GetGlyphClass(data, class2Def, glyphs.GetGlyph(index + 1));
                if ((uint)class1 >= class1Count || (uint)class2 >= class2Count) continue;
                int recordSize = valueSize1 + valueSize2;
                value1Offset = offset + 16 + (class1 * class2Count + class2) * recordSize;
                value2Offset = value1Offset + valueSize1;
                ApplyValueVariation(data, font, glyphs, index, value1Offset, valueFormat1, offset);
                ApplyValueVariation(data, font, glyphs, index + 1, value2Offset, valueFormat2, offset);
            }
        }
    }

    private static void ApplyMarkToBaseVariations(
        ReadOnlySpan<byte> data,
        TtfFont font,
        GlyphPositionBuffer glyphs,
        int offset)
    {
        if (!CanRead(data, offset, 12) || ReadU16(data, offset) != 1) return;
        int markCoverage = offset + ReadU16(data, offset + 2);
        int baseCoverage = offset + ReadU16(data, offset + 4);
        int classCount = ReadU16(data, offset + 6);
        int markArray = offset + ReadU16(data, offset + 8);
        int baseArray = offset + ReadU16(data, offset + 10);
        for (var markIndex = 1; markIndex < glyphs.Count; markIndex++)
        {
            int markCoverageIndex = FindCoverage(data, markCoverage, glyphs.GetGlyph(markIndex));
            if (markCoverageIndex < 0) continue;
            int baseIndex = markIndex - 1;
            while (baseIndex >= 0 && glyphs.GetGlyphClass(baseIndex) == GlyphClassKind.Mark) baseIndex--;
            if (baseIndex < 0) continue;
            int baseCoverageIndex = FindCoverage(data, baseCoverage, glyphs.GetGlyph(baseIndex));
            if (baseCoverageIndex < 0 || !CanRead(data, markArray + 2 + markCoverageIndex * 4, 4)) continue;
            int markRecord = markArray + 2 + markCoverageIndex * 4;
            int markClass = ReadU16(data, markRecord);
            if ((uint)markClass >= classCount) continue;
            int markAnchor = markArray + ReadU16(data, markRecord + 2);
            int baseRecord = baseArray + 2 + (baseCoverageIndex * classCount + markClass) * 2;
            if (!CanRead(data, baseRecord, 2)) continue;
            ushort baseAnchorRelative = ReadU16(data, baseRecord);
            if (baseAnchorRelative == 0) continue;
            Vector2 markDelta = GetAnchorVariation(data, font, markAnchor);
            Vector2 baseDelta = GetAnchorVariation(data, font, baseArray + baseAnchorRelative);
            glyphs.AppendVariation(markIndex, baseDelta.X - markDelta.X, baseDelta.Y - markDelta.Y, 0f);
        }
    }

    private static void ApplyMarkToMarkVariations(
        ReadOnlySpan<byte> data,
        TtfFont font,
        GlyphPositionBuffer glyphs,
        int offset)
    {
        if (!CanRead(data, offset, 12) || ReadU16(data, offset) != 1) return;
        int mark1Coverage = offset + ReadU16(data, offset + 2);
        int mark2Coverage = offset + ReadU16(data, offset + 4);
        int classCount = ReadU16(data, offset + 6);
        int mark1Array = offset + ReadU16(data, offset + 8);
        int mark2Array = offset + ReadU16(data, offset + 10);
        for (var mark1Index = 1; mark1Index < glyphs.Count; mark1Index++)
        {
            int mark1CoverageIndex = FindCoverage(data, mark1Coverage, glyphs.GetGlyph(mark1Index));
            if (mark1CoverageIndex < 0) continue;
            int mark2Index = mark1Index - 1;
            int mark2CoverageIndex = FindCoverage(data, mark2Coverage, glyphs.GetGlyph(mark2Index));
            if (mark2CoverageIndex < 0 || !CanRead(data, mark1Array + 2 + mark1CoverageIndex * 4, 4)) continue;
            int mark1Record = mark1Array + 2 + mark1CoverageIndex * 4;
            int markClass = ReadU16(data, mark1Record);
            if ((uint)markClass >= classCount) continue;
            int mark1Anchor = mark1Array + ReadU16(data, mark1Record + 2);
            int mark2Record = mark2Array + 2 + (mark2CoverageIndex * classCount + markClass) * 2;
            if (!CanRead(data, mark2Record, 2)) continue;
            ushort mark2AnchorRelative = ReadU16(data, mark2Record);
            if (mark2AnchorRelative == 0) continue;
            Vector2 mark1Delta = GetAnchorVariation(data, font, mark1Anchor);
            Vector2 mark2Delta = GetAnchorVariation(data, font, mark2Array + mark2AnchorRelative);
            glyphs.AppendVariation(mark1Index, mark2Delta.X - mark1Delta.X, mark2Delta.Y - mark1Delta.Y, 0f);
        }
    }

    private static void ApplyValueVariation(
        ReadOnlySpan<byte> data,
        TtfFont font,
        GlyphPositionBuffer glyphs,
        int glyphIndex,
        int recordOffset,
        ushort valueFormat,
        int subtableOffset)
    {
        int cursor = recordOffset;
        if ((valueFormat & 0x0001) != 0) cursor += 2;
        if ((valueFormat & 0x0002) != 0) cursor += 2;
        if ((valueFormat & 0x0004) != 0) cursor += 2;
        if ((valueFormat & 0x0008) != 0) cursor += 2;
        float x = 0f;
        float y = 0f;
        float advance = 0f;
        if ((valueFormat & 0x0010) != 0) x = ReadDeviceVariation(data, font, subtableOffset, ref cursor);
        if ((valueFormat & 0x0020) != 0) y = ReadDeviceVariation(data, font, subtableOffset, ref cursor);
        if ((valueFormat & 0x0040) != 0) advance = ReadDeviceVariation(data, font, subtableOffset, ref cursor);
        if ((valueFormat & 0x0080) != 0) _ = ReadDeviceVariation(data, font, subtableOffset, ref cursor);
        glyphs.AppendVariation(glyphIndex, x, y, advance);
    }

    private static float ReadDeviceVariation(
        ReadOnlySpan<byte> data,
        TtfFont font,
        int baseOffset,
        ref int cursor)
    {
        if (!CanRead(data, cursor, 2)) return 0f;
        ushort relative = ReadU16(data, cursor);
        cursor += 2;
        return relative == 0 ? 0f : ReadVariationIndex(data, font, baseOffset + relative);
    }

    private static Vector2 GetAnchorVariation(ReadOnlySpan<byte> data, TtfFont font, int anchorOffset)
    {
        if (!CanRead(data, anchorOffset, 10) || ReadU16(data, anchorOffset) != 3) return Vector2.Zero;
        ushort xRelative = ReadU16(data, anchorOffset + 6);
        ushort yRelative = ReadU16(data, anchorOffset + 8);
        return new Vector2(
            xRelative == 0 ? 0f : ReadVariationIndex(data, font, anchorOffset + xRelative),
            yRelative == 0 ? 0f : ReadVariationIndex(data, font, anchorOffset + yRelative));
    }

    private static float ReadVariationIndex(ReadOnlySpan<byte> data, TtfFont font, int offset)
    {
        if (!CanRead(data, offset, 6) || ReadU16(data, offset + 4) != 0x8000) return 0f;
        return font.GetLayoutVariationDelta(ReadU16(data, offset), ReadU16(data, offset + 2));
    }

    private static int GetValueRecordSize(ushort valueFormat)
    {
        int count = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            if ((valueFormat & (1 << bit)) != 0) count++;
        }
        return count * 2;
    }

    private readonly record struct EnabledLookup(ushort LookupIndex, int Value);

    private static IEnumerable<EnabledLookup> GetEnabledLookupIndices(
        GlyphShapingTableEntry table,
        FeatureList.FeatureTable[] features,
        TextShapingOptions options,
        string script)
    {
        Dictionary<string, int> requested = CreateFeatureMap(options.Features);
        HashSet<ushort>? allowedFeatures = GetLanguageFeatureIndices(table.ScriptList, script, options.Language);
        var lookups = new List<EnabledLookup>();
        var lookupPositions = new Dictionary<ushort, int>();
        for (ushort featureIndex = 0; featureIndex < features.Length; featureIndex++)
        {
            if (features[featureIndex].TagName == "locl" &&
                allowedFeatures is not null &&
                !allowedFeatures.Contains(featureIndex))
            {
                continue;
            }

            FeatureList.FeatureTable feature = features[featureIndex];
            if (!requested.TryGetValue(feature.TagName, out int value) || value == 0)
            {
                continue;
            }

            for (var lookupIndex = 0; lookupIndex < feature.LookupListIndices.Length; lookupIndex++)
            {
                ushort index = feature.LookupListIndices[lookupIndex];
                if (lookupPositions.TryGetValue(index, out int existing))
                {
                    lookups[existing] = new EnabledLookup(index, value);
                }
                else
                {
                    lookupPositions[index] = lookups.Count;
                    lookups.Add(new EnabledLookup(index, value));
                }
            }
        }

        return lookups;
    }

    private static Dictionary<string, int> CreateFeatureMap(IReadOnlyList<OpenTypeFeatureSetting> settings)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < settings.Count; index++)
        {
            OpenTypeFeatureSetting setting = settings[index];
            result[setting.Tag] = setting.Value;
        }
        return result;
    }

    private static HashSet<ushort>? GetLanguageFeatureIndices(
        ScriptList? scripts,
        string script,
        string? language)
    {
        if (scripts is null || scripts.Count == 0)
        {
            return null;
        }

        ScriptTable? scriptTable = scripts[ToTag(script)] ?? scripts[ToTag("DFLT")];
        if (scriptTable is null)
        {
            return null;
        }

        ScriptTable.LangSysTable? languageTable = null;
        if (!string.IsNullOrWhiteSpace(language))
        {
            uint languageTag = ToLanguageTag(language);
            for (var index = 0; index < scriptTable.langSysTables.Length; index++)
            {
                if (scriptTable.langSysTables[index].langSysTagIden == languageTag)
                {
                    languageTable = scriptTable.langSysTables[index];
                    break;
                }
            }
        }

        languageTable ??= scriptTable.defaultLang;
        if (languageTable is null)
        {
            return null;
        }

        var result = new HashSet<ushort>(languageTable.featureIndexList);
        if (languageTable.HasRequireFeature)
        {
            result.Add(languageTable.RequiredFeatureIndex);
        }
        return result;
    }

    private static uint ToLanguageTag(string language)
    {
        string normalized = language.Replace('_', '-').ToLowerInvariant();
        return normalized switch
        {
            "az" or "az-latn" => ToTag("AZE "),
            "de" => ToTag("DEU "),
            "nl" => ToTag("NLD "),
            "pl" => ToTag("PLK "),
            "ro" => ToTag("ROM "),
            "tr" => ToTag("TRK "),
            _ => ToTag("dflt")
        };
    }

    private static string InferScript(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            int value = rune.Value;
            if (value is >= 0x0370 and <= 0x03FF or >= 0x1F00 and <= 0x1FFF) return "grek";
            if (value is >= 0x0400 and <= 0x052F or >= 0x2DE0 and <= 0x2DFF or >= 0xA640 and <= 0xA69F) return "cyrl";
            if (value is >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F) return "hebr";
            if (value is >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x08A0 and <= 0x08FF) return "arab";
            if (value is >= 0x3040 and <= 0x30FF) return "kana";
            if (value is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF) return "hang";
            if (value is >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF) return "hani";
            if (Rune.IsLetter(rune)) return "latn";
        }
        return "DFLT";
    }

    private static uint ToTag(string value)
    {
        Span<char> tag = stackalloc char[4] { ' ', ' ', ' ', ' ' };
        int length = Math.Min(4, value.Length);
        for (var index = 0; index < length; index++)
        {
            tag[index] = value[index];
        }
        return (uint)tag[0] << 24 | (uint)tag[1] << 16 | (uint)tag[2] << 8 | tag[3];
    }

    private static void AddFeatureTags(FeatureList? featureList, HashSet<string> tags)
    {
        if (featureList?.featureTables is not { } features)
        {
            return;
        }
        for (var index = 0; index < features.Length; index++)
        {
            tags.Add(features[index].TagName);
        }
    }

    private sealed class GlyphSubstitutionBuffer : IGlyphIndexList
    {
        private readonly List<GlyphRecord> _glyphs;

        private GlyphSubstitutionBuffer(List<GlyphRecord> glyphs) => _glyphs = glyphs;

        public int Count => _glyphs.Count;
        public ushort this[int index] => _glyphs[index].GlyphIndex;
        public GlyphRecord GetRecord(int index) => _glyphs[index];

        public static GlyphSubstitutionBuffer Create(string text, TtfFont font)
        {
            if (text.Length > MaximumShapedGlyphCount)
            {
                throw new InvalidOperationException($"Text exceeds the {MaximumShapedGlyphCount} glyph shaping limit.");
            }
            var glyphs = new List<GlyphRecord>(text.Length);
            for (var graphemeStart = 0; graphemeStart < text.Length;)
            {
                int graphemeLength = StringInfo.GetNextTextElementLength(text.AsSpan(graphemeStart));
                int graphemeEnd = checked(graphemeStart + graphemeLength);
                for (var index = graphemeStart; index < graphemeEnd; index++)
                {
                    char character = text[index];
                    uint codePoint = character;
                    if (char.IsHighSurrogate(character) && index + 1 < graphemeEnd && char.IsLowSurrogate(text[index + 1]))
                    {
                        codePoint = (uint)char.ConvertToUtf32(character, text[++index]);
                    }
                    glyphs.Add(new GlyphRecord(font.GetGlyphIndex(codePoint), graphemeStart, codePoint));
                }
                graphemeStart = graphemeEnd;
            }
            return new GlyphSubstitutionBuffer(glyphs);
        }

        public void Replace(int index, ushort newGlyphIndex)
        {
            GlyphRecord record = _glyphs[index];
            record.GlyphIndex = newGlyphIndex;
            _glyphs[index] = record;
        }

        public void Replace(int index, int removeLen, ushort newGlyphIndex)
        {
            GlyphRecord record = _glyphs[index];
            record.GlyphIndex = newGlyphIndex;
            _glyphs[index] = record;
            if (removeLen > 1)
            {
                _glyphs.RemoveRange(index + 1, Math.Min(removeLen - 1, _glyphs.Count - index - 1));
            }
        }

        public void Replace(int index, ushort[] newGlyphIndices)
        {
            ArgumentNullException.ThrowIfNull(newGlyphIndices);
            int newCount = checked(_glyphs.Count - 1 + newGlyphIndices.Length);
            if (newCount > MaximumShapedGlyphCount)
            {
                throw new InvalidOperationException($"OpenType substitution exceeds the {MaximumShapedGlyphCount} glyph shaping limit.");
            }
            GlyphRecord record = _glyphs[index];
            _glyphs.RemoveAt(index);
            for (var replacementIndex = newGlyphIndices.Length - 1; replacementIndex >= 0; replacementIndex--)
            {
                GlyphRecord replacement = record;
                replacement.GlyphIndex = newGlyphIndices[replacementIndex];
                _glyphs.Insert(index, replacement);
            }
        }
    }

    private sealed class GlyphPositionBuffer : IGlyphPositions
    {
        private readonly GlyphRecord[] _glyphs;
        private readonly Typeface? _typeface;

        public GlyphPositionBuffer(GlyphSubstitutionBuffer substitutions, TtfFont font, ShapingDirection direction)
        {
            _typeface = font.LayoutTypeface;
            _glyphs = new GlyphRecord[substitutions.Count];
            bool vertical = direction is ShapingDirection.TopToBottom or ShapingDirection.BottomToTop;
            for (var index = 0; index < _glyphs.Length; index++)
            {
                GlyphRecord record = substitutions.GetRecord(index);
                if (vertical)
                {
                    record.AdvanceY = checked((short)Math.Clamp(
                        -MathF.Round(font.GetAdvanceHeight(record.GlyphIndex, font.UnitsPerEm)),
                        short.MinValue,
                        short.MaxValue));
                    record.OffsetX = checked((short)Math.Clamp(
                        -((int)MathF.Round(font.GetAdvanceWidth(record.GlyphIndex, font.UnitsPerEm)) / 2),
                        short.MinValue,
                        short.MaxValue));
                    record.OffsetY = checked((short)Math.Clamp(
                        -MathF.Round(font.GetVerticalOriginY(record.GlyphIndex, font.UnitsPerEm)),
                        short.MinValue,
                        short.MaxValue));
                }
                else
                {
                    record.AdvanceX = checked((short)Math.Clamp(
                        MathF.Round(font.GetAdvanceWidth(record.GlyphIndex, font.UnitsPerEm)),
                        short.MinValue,
                        short.MaxValue));
                }
                _glyphs[index] = record;
            }
        }

        public int Count => _glyphs.Length;
        public GlyphRecord this[int index] => _glyphs[index];
        public ushort GetGlyph(int index) => _glyphs[index].GlyphIndex;
        public GlyphClassKind GetGlyphClass(int index) => GetGlyphClassKind(index);

        public void AppendVariation(int index, float x, float y, float advance)
        {
            _glyphs[index].OffsetX = AddClamped(_glyphs[index].OffsetX, x);
            _glyphs[index].OffsetY = AddClamped(_glyphs[index].OffsetY, y);
            _glyphs[index].AdvanceX = AddClamped(_glyphs[index].AdvanceX, advance);
        }

        public GlyphClassKind GetGlyphClassKind(int index) =>
            _typeface?.GetGlyph(_glyphs[index].GlyphIndex).GlyphClass ?? GlyphClassKind.Zero;

        public void AppendGlyphOffset(int index, short appendOffsetX, short appendOffsetY)
        {
            _glyphs[index].OffsetX = AddClamped(_glyphs[index].OffsetX, appendOffsetX);
            _glyphs[index].OffsetY = AddClamped(_glyphs[index].OffsetY, appendOffsetY);
        }

        public void AppendGlyphAdvance(int index, short appendAdvX, short appendAdvY)
        {
            _glyphs[index].AdvanceX = AddClamped(_glyphs[index].AdvanceX, appendAdvX);
            _glyphs[index].AdvanceY = AddClamped(_glyphs[index].AdvanceY, appendAdvY);
        }

        public void Reverse() => Array.Reverse(_glyphs);

        public ushort GetGlyph(int index, out short advW)
        {
            advW = _glyphs[index].AdvanceX;
            return _glyphs[index].GlyphIndex;
        }

        public ushort GetGlyph(
            int index,
            out ushort inputOffset,
            out short offsetX,
            out short offsetY,
            out short advW)
        {
            GlyphRecord glyph = _glyphs[index];
            inputOffset = checked((ushort)Math.Clamp(glyph.Cluster, 0, ushort.MaxValue));
            offsetX = glyph.OffsetX;
            offsetY = glyph.OffsetY;
            advW = glyph.AdvanceX;
            return glyph.GlyphIndex;
        }

        public void GetOffset(int index, out short offsetX, out short offsetY)
        {
            offsetX = _glyphs[index].OffsetX;
            offsetY = _glyphs[index].OffsetY;
        }

        private static short AddClamped(short left, short right) =>
            (short)Math.Clamp(left + right, short.MinValue, short.MaxValue);

        private static short AddClamped(short left, float right) =>
            (short)Math.Clamp(
                MathF.Round(left + right),
                short.MinValue,
                short.MaxValue);
    }

    private struct GlyphRecord
    {
        public GlyphRecord(ushort glyphIndex, int cluster, uint codePoint)
        {
            GlyphIndex = glyphIndex;
            Cluster = cluster;
            CodePoint = codePoint;
        }

        public ushort GlyphIndex;
        public int Cluster;
        public uint CodePoint;
        public short AdvanceX;
        public short AdvanceY;
        public short OffsetX;
        public short OffsetY;
    }
}
