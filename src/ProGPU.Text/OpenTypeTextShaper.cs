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
    private static readonly IReadOnlySet<string> s_noExplicitFeatures = new HashSet<string>(StringComparer.Ordinal);
    private static readonly OpenTypeFeatureSetting[] s_defaultFeatures =
    [
        new("rvrn"),
        new("frac"),
        new("numr"),
        new("dnom"),
        new("ccmp"),
        new("locl"),
        new("isol"),
        new("fina"),
        new("fin2"),
        new("fin3"),
        new("medi"),
        new("med2"),
        new("init"),
        new("rlig"),
        new("mark"),
        new("mkmk"),
        new("calt"),
        new("clig"),
        new("curs"),
        new("dist"),
        new("abvm"),
        new("blwm"),
        new("kern"),
        new("liga"),
        new("rclt")
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
            Features = values.Select(pair => new OpenTypeFeatureSetting(pair.Key, pair.Value)).ToArray(),
            ExplicitFeatureTags = overrides.Select(static feature => feature.Tag).ToHashSet(StringComparer.Ordinal)
        };
    }

    public string? Script { get; init; }
    public string? Language { get; init; }
    public ShapingDirection Direction { get; init; }
    public IReadOnlyList<OpenTypeFeatureSetting> Features { get; init; } = s_defaultFeatures;

    /// <summary>
    /// Identifies feature settings explicitly requested by the caller. A null
    /// value treats a custom <see cref="Features"/> list as entirely explicit;
    /// an empty set marks a fully resolved plan containing only shaper defaults.
    /// </summary>
    public IReadOnlySet<string>? ExplicitFeatureTags { get; init; }

    internal IReadOnlySet<string> ResolveExplicitFeatureTags()
    {
        if (ExplicitFeatureTags is not null)
        {
            return ExplicitFeatureTags;
        }

        return ReferenceEquals(Features, s_defaultFeatures)
            ? s_noExplicitFeatures
            : Features.Select(static feature => feature.Tag).ToHashSet(StringComparer.Ordinal);
    }
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
        options = AddScriptFeatures(options, script);
        options = AddDirectionalFeatures(options, direction);

        var substitutions = GlyphSubstitutionBuffer.Create(text, font, script);
        substitutions.AssignFractionActions();
        substitutions.PrepareKhmerShaping(script);
        substitutions.AssignArabicJoiningActions(script);
        Typeface? typeface = font.LayoutTypeface;
        ApplySubstitutions(font, typeface?.GSUBTable, substitutions, options, script);

        var positions = new GlyphPositionBuffer(substitutions, font, direction);
        if (typeface is not null)
        {
            ApplyPositions(font, typeface.GPOSTable, positions, options, script);
        }
        positions.ResolveAttachmentOffsets();

        if (direction is ShapingDirection.RightToLeft or ShapingDirection.BottomToTop)
        {
            positions.ResolveBackwardMarkOffsets(direction);
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
        IReadOnlySet<string> explicitFeatureTags = options.ResolveExplicitFeatureTags();
        string[] required = direction switch
        {
            ShapingDirection.TopToBottom or ShapingDirection.BottomToTop => ["vert", "vrt2", "vkrn"],
            ShapingDirection.RightToLeft => ["rtla", "rtlm"],
            _ => ["ltra", "ltrm"]
        };
        var features = new List<OpenTypeFeatureSetting>(options.Features.Count + required.Length);
        foreach (string tag in required)
        {
            if (!options.Features.Any(feature => feature.Tag == tag)) features.Add(new OpenTypeFeatureSetting(tag));
        }
        foreach (OpenTypeFeatureSetting feature in options.Features)
        {
            if (direction is ShapingDirection.TopToBottom or ShapingDirection.BottomToTop && feature.Tag == "kern")
            {
                continue;
            }
            features.Add(feature);
        }
        return new TextShapingOptions
        {
            Script = options.Script,
            Language = options.Language,
            Direction = direction,
            Features = features,
            ExplicitFeatureTags = explicitFeatureTags
        };
    }

    private static TextShapingOptions AddScriptFeatures(TextShapingOptions options, string script)
    {
        if (script != "khmr")
        {
            return options;
        }

        IReadOnlySet<string> explicitFeatureTags = options.ResolveExplicitFeatureTags();
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < options.Features.Count; index++)
        {
            OpenTypeFeatureSetting feature = options.Features[index];
            values[feature.Tag] = feature.Value;
        }

        string[] khmerFeatures = ["pref", "blwf", "abvf", "pstf", "cfar", "pres", "abvs", "blws", "psts"];
        foreach (string tag in khmerFeatures)
        {
            values.TryAdd(tag, 1);
        }
        values.TryAdd("clig", 1);
        if (!explicitFeatureTags.Contains("liga"))
        {
            values["liga"] = 0;
        }

        string[] orderedTags =
        [
            "rvrn", "frac", "numr", "dnom", "locl", "ccmp",
            "pref", "blwf", "abvf", "pstf", "cfar",
            "pres", "abvs", "blws", "psts"
        ];
        var features = new List<OpenTypeFeatureSetting>(values.Count);
        foreach (string tag in orderedTags)
        {
            if (values.Remove(tag, out int value))
            {
                features.Add(new OpenTypeFeatureSetting(tag, value));
            }
        }
        foreach (OpenTypeFeatureSetting feature in options.Features)
        {
            if (values.Remove(feature.Tag, out int value))
            {
                features.Add(new OpenTypeFeatureSetting(feature.Tag, value));
            }
        }
        foreach ((string tag, int value) in values)
        {
            features.Add(new OpenTypeFeatureSetting(tag, value));
        }

        return new TextShapingOptions
        {
            Script = options.Script,
            Language = options.Language,
            Direction = options.Direction,
            Features = features,
            ExplicitFeatureTags = explicitFeatureTags
        };
    }

    private static void ApplySubstitutions(
        TtfFont font,
        GSUB? table,
        GlyphSubstitutionBuffer glyphs,
        TextShapingOptions options,
        string script)
    {
        ReadOnlyMemory<byte> rawTable = default;
        List<EnabledLookup> rawLookups = font.TryGetTable("GSUB", out rawTable)
            ? GetRawEnabledLookupIndices(rawTable.Span, options.Features, script, options.Language)
            : [];

        if (table?.FeatureList?.featureTables is { Length: > 0 } features)
        {
            foreach (EnabledLookup enabled in GetEnabledLookupIndices(table, features, options, script))
            {
                ushort lookupIndex = enabled.LookupIndex;
                if (lookupIndex >= table.LookupList.Count)
                {
                    continue;
                }

                GSUB.LookupTable lookup = table.LookupList[lookupIndex];
                bool rawOwnedLookup = IsRawOwnedLookup(rawTable.Span, lookupIndex);
                for (var position = 0; position < glyphs.Count; position++)
                {
                    if (!glyphs.IsFeatureEnabled(position, enabled.Tag, options))
                    {
                        continue;
                    }
                    if (rawOwnedLookup)
                    {
                        int rawCountBefore = glyphs.Count;
                        ApplyNestedLookup(rawTable.Span, glyphs, lookupIndex, position);
                        int rawInsertedGlyphs = glyphs.Count - rawCountBefore;
                        if (rawInsertedGlyphs > 0) position += rawInsertedGlyphs;
                        continue;
                    }

                    int countBefore = glyphs.Count;
                    try
                    {
                        lookup.DoSubstitutionAt(glyphs, position, glyphs.Count - position);
                    }
                    catch (IndexOutOfRangeException)
                    {
                        // OpenFontSharp's contextual format-2 executor indexes
                        // malformed/non-matching class rules unsafely. The raw
                        // bounded parser below owns those lookup formats.
                        break;
                    }
                    int insertedGlyphs = glyphs.Count - countBefore;
                    if (insertedGlyphs > 0)
                    {
                        // A multiple substitution is applied once to the original
                        // input glyph. Its newly inserted output must not be fed
                        // back through the same lookup during this pass.
                        position += insertedGlyphs;
                    }
                }

                ApplyAlternateLookup(font, glyphs, lookupIndex, enabled.Value);
            }
        }

        if (table is null)
        {
            foreach (EnabledLookup enabled in rawLookups)
            {
                if (!IsRawOwnedLookup(rawTable.Span, enabled.LookupIndex)) continue;
                for (var position = 0; position < glyphs.Count; position++)
                {
                    if (glyphs.IsFeatureEnabled(position, enabled.Tag, options))
                    {
                        int countBefore = glyphs.Count;
                        ApplyNestedLookup(rawTable.Span, glyphs, enabled.LookupIndex, position);
                        int insertedGlyphs = glyphs.Count - countBefore;
                        if (insertedGlyphs > 0) position += insertedGlyphs;
                    }
                }
            }
        }

        foreach (EnabledLookup enabled in rawLookups)
        {
            ApplyReverseChainingLookup(rawTable.Span, glyphs, enabled.LookupIndex);
        }
    }

    private static bool IsRawOwnedLookup(ReadOnlySpan<byte> data, ushort lookupIndex)
    {
        if (!TryGetLookup(data, lookupIndex, out int lookupOffset, out ushort lookupType, out int subtableCountOffset))
        {
            return false;
        }
        if (lookupType is 4 or 5 or 6) return true;
        if (lookupType != 7 || ReadU16(data, subtableCountOffset) == 0 || !CanRead(data, subtableCountOffset + 2, 2))
        {
            return false;
        }
        int extension = lookupOffset + ReadU16(data, subtableCountOffset + 2);
        return CanRead(data, extension, 4) && ReadU16(data, extension) == 1 && ReadU16(data, extension + 2) is 4 or 5 or 6;
    }

    private static bool ApplyContextLookupAt(
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
            int relativeOffset = subtableCountOffset + 2 + subtableIndex * 2;
            if (!CanRead(data, relativeOffset, 2)) break;
            int subtableOffset = lookupOffset + ReadU16(data, relativeOffset);
            ushort effectiveType = lookupType;
            if (effectiveType == 7)
            {
                if (!CanRead(data, subtableOffset, 8) || ReadU16(data, subtableOffset) != 1) continue;
                effectiveType = ReadU16(data, subtableOffset + 2);
                uint extensionOffset = ReadU32(data, subtableOffset + 4);
                if (extensionOffset > int.MaxValue) continue;
                subtableOffset += (int)extensionOffset;
            }

            bool applied = effectiveType switch
            {
                5 => ApplyContextSubtable(data, glyphs, subtableOffset, position),
                6 => ApplyChainContextSubtable(data, glyphs, subtableOffset, position),
                _ => false
            };
            if (applied) return true;
        }
        return false;
    }

    private static List<EnabledLookup> GetRawEnabledLookupIndices(
        ReadOnlySpan<byte> data,
        IReadOnlyList<OpenTypeFeatureSetting> settings,
        string script,
        string? language)
    {
        var requested = CreateFeatureMap(settings);
        var result = new List<EnabledLookup>();
        var positions = new Dictionary<ushort, int>();
        if (!CanRead(data, 6, 2))
        {
            return result;
        }

        int featureListOffset = ReadU16(data, 6);
        if (!CanRead(data, featureListOffset, 2))
        {
            return result;
        }

        ushort featureCount = ReadU16(data, featureListOffset);
        if (TryGetRawRequiredFeatureIndex(data, script, language, out ushort requiredFeatureIndex) &&
            requiredFeatureIndex < featureCount)
        {
            AddRawFeatureLookups(
                data,
                featureListOffset,
                requiredFeatureIndex,
                1,
                result,
                positions);
        }
        foreach (OpenTypeFeatureSetting setting in settings)
        {
            if (!requested.TryGetValue(setting.Tag, out int value) || value == 0 || value != setting.Value)
            {
                continue;
            }
            for (var featureIndex = 0; featureIndex < featureCount; featureIndex++)
            {
                int recordOffset = featureListOffset + 2 + featureIndex * 6;
                if (!CanRead(data, recordOffset, 6))
                {
                    break;
                }

                string tag = Encoding.ASCII.GetString(data.Slice(recordOffset, 4));
                if (tag != setting.Tag)
                {
                    continue;
                }
                AddRawFeatureLookups(data, featureListOffset, (ushort)featureIndex, value, result, positions);
            }
        }
        return result;
    }

    private static void AddRawFeatureLookups(
        ReadOnlySpan<byte> data,
        int featureListOffset,
        ushort featureIndex,
        int value,
        List<EnabledLookup> result,
        Dictionary<ushort, int> positions)
    {
        int recordOffset = featureListOffset + 2 + featureIndex * 6;
        if (!CanRead(data, recordOffset, 6)) return;
        string tag = Encoding.ASCII.GetString(data.Slice(recordOffset, 4));
        int featureOffset = featureListOffset + ReadU16(data, recordOffset + 4);
        if (!CanRead(data, featureOffset, 4)) return;
        ushort lookupCount = ReadU16(data, featureOffset + 2);
        for (var lookup = 0; lookup < lookupCount; lookup++)
        {
            int lookupOffset = featureOffset + 4 + lookup * 2;
            if (!CanRead(data, lookupOffset, 2)) break;
            ushort lookupIndex = ReadU16(data, lookupOffset);
            if (positions.TryGetValue(lookupIndex, out int existing))
            {
                result[existing] = new EnabledLookup(lookupIndex, value, tag);
            }
            else
            {
                positions.Add(lookupIndex, result.Count);
                result.Add(new EnabledLookup(lookupIndex, value, tag));
            }
        }
    }

    private static bool TryGetRawRequiredFeatureIndex(
        ReadOnlySpan<byte> data,
        string script,
        string? language,
        out ushort requiredFeatureIndex)
    {
        requiredFeatureIndex = ushort.MaxValue;
        if (!CanRead(data, 4, 2)) return false;
        int scriptListOffset = ReadU16(data, 4);
        if (!CanRead(data, scriptListOffset, 2)) return false;
        ushort scriptCount = ReadU16(data, scriptListOffset);
        uint requestedScript = ToTag(script);
        int selectedScriptOffset = 0;
        int defaultScriptOffset = 0;
        for (var index = 0; index < scriptCount; index++)
        {
            int record = scriptListOffset + 2 + index * 6;
            if (!CanRead(data, record, 6)) break;
            uint tag = ReadU32(data, record);
            int tableOffset = scriptListOffset + ReadU16(data, record + 4);
            if (tag == requestedScript) selectedScriptOffset = tableOffset;
            else if (tag == ToTag("DFLT")) defaultScriptOffset = tableOffset;
        }
        int scriptOffset = selectedScriptOffset != 0 ? selectedScriptOffset : defaultScriptOffset;
        if (!CanRead(data, scriptOffset, 4)) return false;

        int languageOffset = 0;
        ushort defaultRelative = ReadU16(data, scriptOffset);
        if (defaultRelative != 0) languageOffset = scriptOffset + defaultRelative;
        if (!string.IsNullOrWhiteSpace(language))
        {
            uint requestedLanguage = ToLanguageTag(language);
            ushort languageCount = ReadU16(data, scriptOffset + 2);
            for (var index = 0; index < languageCount; index++)
            {
                int record = scriptOffset + 4 + index * 6;
                if (!CanRead(data, record, 6)) break;
                if (ReadU32(data, record) == requestedLanguage)
                {
                    languageOffset = scriptOffset + ReadU16(data, record + 4);
                    break;
                }
            }
        }
        if (!CanRead(data, languageOffset, 6)) return false;
        requiredFeatureIndex = ReadU16(data, languageOffset + 2);
        return requiredFeatureIndex != ushort.MaxValue;
    }

    private static void ApplyReverseChainingLookup(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        ushort lookupIndex)
    {
        if (!TryGetLookup(data, lookupIndex, out int lookupOffset, out ushort lookupType, out int subtableCountOffset))
        {
            return;
        }

        ushort subtableCount = ReadU16(data, subtableCountOffset);
        for (var position = glyphs.Count - 1; position >= 0; position--)
        {
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

                if (effectiveType == 8 && ApplyReverseChainingSubtable(data, glyphs, subtableOffset, position))
                {
                    break;
                }
            }
        }
    }

    private static bool ApplyReverseChainingSubtable(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int offset,
        int position)
    {
        if (!CanRead(data, offset, 6) || ReadU16(data, offset) != 1)
        {
            return false;
        }

        int coverageIndex = FindCoverage(data, offset + ReadU16(data, offset + 2), glyphs[position]);
        if (coverageIndex < 0)
        {
            return false;
        }

        int cursor = offset + 4;
        ushort backtrackCount = ReadU16(data, cursor);
        cursor += 2;
        if (position < backtrackCount || !CanRead(data, cursor, backtrackCount * 2))
        {
            return false;
        }
        for (var index = 0; index < backtrackCount; index++)
        {
            int coverageOffset = offset + ReadU16(data, cursor + index * 2);
            if (FindCoverage(data, coverageOffset, glyphs[position - index - 1]) < 0)
            {
                return false;
            }
        }
        cursor += backtrackCount * 2;

        if (!CanRead(data, cursor, 2))
        {
            return false;
        }
        ushort lookaheadCount = ReadU16(data, cursor);
        cursor += 2;
        if (position + lookaheadCount >= glyphs.Count || !CanRead(data, cursor, lookaheadCount * 2))
        {
            return false;
        }
        for (var index = 0; index < lookaheadCount; index++)
        {
            int coverageOffset = offset + ReadU16(data, cursor + index * 2);
            if (FindCoverage(data, coverageOffset, glyphs[position + index + 1]) < 0)
            {
                return false;
            }
        }
        cursor += lookaheadCount * 2;

        if (!CanRead(data, cursor, 2))
        {
            return false;
        }
        ushort glyphCount = ReadU16(data, cursor);
        cursor += 2;
        if ((uint)coverageIndex >= glyphCount || !CanRead(data, cursor + coverageIndex * 2, 2))
        {
            return false;
        }
        glyphs.Replace(position, ReadU16(data, cursor + coverageIndex * 2));
        return true;
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

    private static bool ApplyContextSubtable(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int subtableOffset,
        int position)
    {
        if (!CanRead(data, subtableOffset, 6)) return false;
        return ReadU16(data, subtableOffset) switch
        {
            1 => ApplyContextFormat1(data, glyphs, subtableOffset, position),
            2 => ApplyContextFormat2(data, glyphs, subtableOffset, position),
            3 => ApplyContextFormat3(data, glyphs, subtableOffset, position),
            _ => false
        };
    }

    private static bool ApplyContextFormat1(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int subtableOffset,
        int position)
    {
        int coverageIndex = FindCoverage(data, subtableOffset + ReadU16(data, subtableOffset + 2), glyphs[position]);
        ushort setCount = ReadU16(data, subtableOffset + 4);
        if ((uint)coverageIndex >= setCount || !CanRead(data, subtableOffset + 6 + coverageIndex * 2, 2)) return false;
        ushort setRelative = ReadU16(data, subtableOffset + 6 + coverageIndex * 2);
        if (setRelative == 0) return false;
        int setOffset = subtableOffset + setRelative;
        if (!CanRead(data, setOffset, 2)) return false;
        ushort ruleCount = ReadU16(data, setOffset);
        for (var ruleIndex = 0; ruleIndex < ruleCount; ruleIndex++)
        {
            int rulePointer = setOffset + 2 + ruleIndex * 2;
            if (!CanRead(data, rulePointer, 2)) break;
            int ruleOffset = setOffset + ReadU16(data, rulePointer);
            if (!CanRead(data, ruleOffset, 4)) continue;
            ushort glyphCount = ReadU16(data, ruleOffset);
            ushort recordCount = ReadU16(data, ruleOffset + 2);
            if (glyphCount == 0 || position + glyphCount > glyphs.Count ||
                !CanRead(data, ruleOffset + 4, (glyphCount - 1 + recordCount * 2) * 2)) continue;
            bool matches = true;
            for (var index = 1; index < glyphCount; index++)
            {
                if (glyphs[position + index] != ReadU16(data, ruleOffset + 4 + (index - 1) * 2))
                {
                    matches = false;
                    break;
                }
            }
            if (!matches) continue;
            int recordsOffset = ruleOffset + 4 + (glyphCount - 1) * 2;
            return ApplySubstitutionRecords(data, glyphs, position, recordsOffset, recordCount);
        }
        return false;
    }

    private static bool ApplyContextFormat2(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int subtableOffset,
        int position)
    {
        if (!CanRead(data, subtableOffset, 8) ||
            FindCoverage(data, subtableOffset + ReadU16(data, subtableOffset + 2), glyphs[position]) < 0) return false;
        int classDef = subtableOffset + ReadU16(data, subtableOffset + 4);
        ushort setCount = ReadU16(data, subtableOffset + 6);
        int firstClass = GetGlyphClass(data, classDef, glyphs[position]);
        if ((uint)firstClass >= setCount || !CanRead(data, subtableOffset + 8 + firstClass * 2, 2)) return false;
        ushort setRelative = ReadU16(data, subtableOffset + 8 + firstClass * 2);
        if (setRelative == 0) return false;
        int setOffset = subtableOffset + setRelative;
        if (!CanRead(data, setOffset, 2)) return false;
        ushort ruleCount = ReadU16(data, setOffset);
        for (var ruleIndex = 0; ruleIndex < ruleCount; ruleIndex++)
        {
            int pointer = setOffset + 2 + ruleIndex * 2;
            if (!CanRead(data, pointer, 2)) break;
            int ruleOffset = setOffset + ReadU16(data, pointer);
            if (!CanRead(data, ruleOffset, 4)) continue;
            ushort glyphCount = ReadU16(data, ruleOffset);
            ushort recordCount = ReadU16(data, ruleOffset + 2);
            if (glyphCount == 0 || position + glyphCount > glyphs.Count ||
                !CanRead(data, ruleOffset + 4, (glyphCount - 1 + recordCount * 2) * 2)) continue;
            bool matches = true;
            for (var index = 1; index < glyphCount; index++)
            {
                int expectedClass = ReadU16(data, ruleOffset + 4 + (index - 1) * 2);
                if (GetGlyphClass(data, classDef, glyphs[position + index]) != expectedClass)
                {
                    matches = false;
                    break;
                }
            }
            if (!matches) continue;
            int recordsOffset = ruleOffset + 4 + (glyphCount - 1) * 2;
            return ApplySubstitutionRecords(data, glyphs, position, recordsOffset, recordCount);
        }
        return false;
    }

    private static bool ApplyContextFormat3(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int subtableOffset,
        int position)
    {
        ushort glyphCount = ReadU16(data, subtableOffset + 2);
        ushort recordCount = ReadU16(data, subtableOffset + 4);
        int coverageOffsets = subtableOffset + 6;
        if (glyphCount == 0 || position + glyphCount > glyphs.Count ||
            !CanRead(data, coverageOffsets, (glyphCount + recordCount * 2) * 2)) return false;
        for (var index = 0; index < glyphCount; index++)
        {
            int coverage = subtableOffset + ReadU16(data, coverageOffsets + index * 2);
            if (FindCoverage(data, coverage, glyphs[position + index]) < 0) return false;
        }
        return ApplySubstitutionRecords(
            data,
            glyphs,
            position,
            coverageOffsets + glyphCount * 2,
            recordCount);
    }

    private static bool ApplyChainContextSubtable(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int subtableOffset,
        int position)
    {
        if (!CanRead(data, subtableOffset, 2)) return false;
        return ReadU16(data, subtableOffset) switch
        {
            1 => ApplyChainContextFormat1(data, glyphs, subtableOffset, position),
            2 => ApplyChainContextFormat2(data, glyphs, subtableOffset, position),
            3 => ApplyChainContextFormat3(data, glyphs, subtableOffset, position),
            _ => false
        };
    }

    private static bool ApplyChainContextFormat3(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int subtableOffset,
        int position)
    {
        int cursor = subtableOffset + 2;
        if (!CanRead(data, cursor, 2)) return false;
        ushort backtrackCount = ReadU16(data, cursor);
        cursor += 2;
        if (position < backtrackCount || !CanRead(data, cursor, backtrackCount * 2)) return false;
        for (var index = 0; index < backtrackCount; index++)
        {
            int coverage = subtableOffset + ReadU16(data, cursor + index * 2);
            if (FindCoverage(data, coverage, glyphs[position - index - 1]) < 0) return false;
        }
        cursor += backtrackCount * 2;

        if (!CanRead(data, cursor, 2)) return false;
        ushort inputCount = ReadU16(data, cursor);
        cursor += 2;
        if (inputCount == 0 || position + inputCount > glyphs.Count || !CanRead(data, cursor, inputCount * 2)) return false;
        for (var index = 0; index < inputCount; index++)
        {
            int coverage = subtableOffset + ReadU16(data, cursor + index * 2);
            if (FindCoverage(data, coverage, glyphs[position + index]) < 0) return false;
        }
        cursor += inputCount * 2;

        if (!CanRead(data, cursor, 2)) return false;
        ushort lookaheadCount = ReadU16(data, cursor);
        cursor += 2;
        if (position + inputCount + lookaheadCount > glyphs.Count || !CanRead(data, cursor, lookaheadCount * 2)) return false;
        for (var index = 0; index < lookaheadCount; index++)
        {
            int coverage = subtableOffset + ReadU16(data, cursor + index * 2);
            if (FindCoverage(data, coverage, glyphs[position + inputCount + index]) < 0) return false;
        }
        cursor += lookaheadCount * 2;

        if (!CanRead(data, cursor, 2)) return false;
        ushort recordCount = ReadU16(data, cursor);
        cursor += 2;
        return CanRead(data, cursor, recordCount * 4) &&
               ApplySubstitutionRecords(data, glyphs, position, cursor, recordCount);
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
        ushort lookupFlags = ReadU16(data, lookupOffset + 2);
        for (var subtableIndex = 0; subtableIndex < subtableCount; subtableIndex++)
        {
            int subtableOffset = lookupOffset + ReadU16(data, subtableCountOffset + 2 + subtableIndex * 2);
            if (ApplyNestedSubtable(data, glyphs, lookupType, subtableOffset, position, lookupFlags))
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
        int position,
        ushort lookupFlags)
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
            return ApplyNestedSubtable(data, glyphs, extensionType, offset + (int)extensionOffset, position, lookupFlags);
        }

        if (lookupType is 5 or 6)
        {
            return lookupType == 5
                ? ApplyContextSubtable(data, glyphs, offset, position)
                : ApplyChainContextSubtable(data, glyphs, offset, position);
        }

        int coverageOffset = offset + ReadU16(data, offset + 2);
        int coverageIndex = FindCoverage(data, coverageOffset, glyphs[position]);
        if (coverageIndex < 0)
        {
            return false;
        }

        if (lookupType == 1 && format == 1)
        {
            short delta = ReadI16(data, offset + 4);
            glyphs.Replace(position, unchecked((ushort)(glyphs[position] + delta)));
            return true;
        }
        if (lookupType == 1 && format == 2 && CanRead(data, offset + 4, 2))
        {
            ushort count = ReadU16(data, offset + 4);
            if ((uint)coverageIndex < count && CanRead(data, offset + 6 + coverageIndex * 2, 2))
            {
                glyphs.Replace(position, ReadU16(data, offset + 6 + coverageIndex * 2));
                return true;
            }
        }

        if (lookupType == 2 && format == 1 && CanRead(data, offset + 4, 2))
        {
            ushort sequenceCount = ReadU16(data, offset + 4);
            int pointer = offset + 6 + coverageIndex * 2;
            if ((uint)coverageIndex >= sequenceCount || !CanRead(data, pointer, 2)) return false;
            int sequenceOffset = offset + ReadU16(data, pointer);
            if (!CanRead(data, sequenceOffset, 2)) return false;
            ushort glyphCount = ReadU16(data, sequenceOffset);
            if (!CanRead(data, sequenceOffset + 2, glyphCount * 2)) return false;
            var replacements = new ushort[glyphCount];
            for (var index = 0; index < glyphCount; index++)
            {
                replacements[index] = ReadU16(data, sequenceOffset + 2 + index * 2);
            }
            glyphs.Replace(position, replacements);
            return true;
        }

        if (lookupType == 3 && format == 1 && CanRead(data, offset + 4, 2))
        {
            ushort setCount = ReadU16(data, offset + 4);
            int pointer = offset + 6 + coverageIndex * 2;
            if ((uint)coverageIndex >= setCount || !CanRead(data, pointer, 2)) return false;
            int setOffset = offset + ReadU16(data, pointer);
            if (!CanRead(data, setOffset, 4) || ReadU16(data, setOffset) == 0) return false;
            glyphs.Replace(position, ReadU16(data, setOffset + 2));
            return true;
        }

        if (lookupType == 4 && format == 1 && CanRead(data, offset + 4, 2))
        {
            ushort setCount = ReadU16(data, offset + 4);
            int pointer = offset + 6 + coverageIndex * 2;
            if ((uint)coverageIndex >= setCount || !CanRead(data, pointer, 2)) return false;
            int setOffset = offset + ReadU16(data, pointer);
            if (!CanRead(data, setOffset, 2)) return false;
            ushort ligatureCount = ReadU16(data, setOffset);
            for (var ligatureIndex = 0; ligatureIndex < ligatureCount; ligatureIndex++)
            {
                int ligaturePointer = setOffset + 2 + ligatureIndex * 2;
                if (!CanRead(data, ligaturePointer, 2)) break;
                int ligatureOffset = setOffset + ReadU16(data, ligaturePointer);
                if (!CanRead(data, ligatureOffset, 4)) continue;
                ushort ligatureGlyph = ReadU16(data, ligatureOffset);
                ushort componentCount = ReadU16(data, ligatureOffset + 2);
                if (componentCount == 0 ||
                    !CanRead(data, ligatureOffset + 4, (componentCount - 1) * 2)) continue;
                Span<int> componentIndices = componentCount <= 64
                    ? stackalloc int[componentCount]
                    : new int[componentCount];
                componentIndices[0] = position;
                bool matches = true;
                int candidateIndex = position;
                for (var component = 1; component < componentCount; component++)
                {
                    candidateIndex = glyphs.NextVisibleIndex(candidateIndex + 1, lookupFlags);
                    if (candidateIndex < 0 ||
                        glyphs[candidateIndex] != ReadU16(data, ligatureOffset + 4 + (component - 1) * 2))
                    {
                        matches = false;
                        break;
                    }
                    componentIndices[component] = candidateIndex;
                }
                if (!matches) continue;
                glyphs.ReplaceLigature(componentIndices, ligatureGlyph);
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

        ReadOnlyMemory<byte> rawTable = default;
        bool hasRawTable = font.TryGetTable("GPOS", out rawTable);

        foreach (EnabledLookup enabled in GetEnabledLookupIndices(table, features, options, script))
        {
            ushort lookupIndex = enabled.LookupIndex;
            if (lookupIndex < table.LookupList.Count)
            {
                bool rawOwned = hasRawTable && IsRawOwnedPositionLookup(rawTable.Span, lookupIndex);
                if (rawOwned)
                {
                    ApplyRawPositionLookup(rawTable.Span, glyphs, lookupIndex);
                }
                else
                {
                    table.LookupList[lookupIndex].DoGlyphPosition(glyphs, 0, glyphs.Count);
                }
                ApplyPositionVariations(font, glyphs, lookupIndex);
            }
        }
    }

    private static bool IsRawOwnedPositionLookup(ReadOnlySpan<byte> data, ushort lookupIndex)
    {
        if (!TryGetLookup(data, lookupIndex, out int lookupOffset, out ushort lookupType, out int subtableCountOffset))
        {
            return false;
        }
        if (lookupType is 1 or 2 or 4 or 5 or 6) return true;
        if (lookupType != 9 || ReadU16(data, subtableCountOffset) == 0 || !CanRead(data, subtableCountOffset + 2, 2))
        {
            return false;
        }
        int extension = lookupOffset + ReadU16(data, subtableCountOffset + 2);
        return CanRead(data, extension, 8) &&
               ReadU16(data, extension) == 1 &&
               ReadU16(data, extension + 2) is 1 or 2 or 4 or 5 or 6;
    }

    private static void ApplyRawPositionLookup(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        ushort lookupIndex)
    {
        if (!TryGetLookup(data, lookupIndex, out int lookupOffset, out ushort lookupType, out int subtableCountOffset))
        {
            return;
        }

        ushort lookupFlags = ReadU16(data, lookupOffset + 2);
        ushort subtableCount = ReadU16(data, subtableCountOffset);
        for (var position = 0; position < glyphs.Count; position++)
        {
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

                bool matched = effectiveType switch
                {
                    1 => ApplySinglePosition(data, glyphs, subtableOffset, position),
                    2 => ApplyPairPosition(data, glyphs, subtableOffset, position, lookupFlags),
                    4 => ApplyMarkToBasePosition(data, glyphs, subtableOffset, position, lookupFlags),
                    5 => ApplyMarkToLigaturePosition(data, glyphs, subtableOffset, position, lookupFlags),
                    6 => ApplyMarkToMarkPosition(data, glyphs, subtableOffset, position, lookupFlags),
                    _ => false
                };
                if (matched) break;
            }
        }
    }

    private static bool ApplySinglePosition(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int offset,
        int position)
    {
        if (!CanRead(data, offset, 6)) return false;
        ushort format = ReadU16(data, offset);
        int coverageIndex = FindCoverage(data, offset + ReadU16(data, offset + 2), glyphs.GetGlyph(position));
        if (coverageIndex < 0) return false;
        ushort valueFormat = ReadU16(data, offset + 4);
        int valueOffset;
        if (format == 1)
        {
            valueOffset = offset + 6;
        }
        else if (format == 2 && CanRead(data, offset + 6, 2) && coverageIndex < ReadU16(data, offset + 6))
        {
            valueOffset = offset + 8 + coverageIndex * GetValueRecordSize(valueFormat);
        }
        else
        {
            return false;
        }
        return ApplyValueRecord(data, glyphs, position, valueOffset, valueFormat);
    }

    private static bool ApplyPairPosition(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int offset,
        int position,
        ushort lookupFlags)
    {
        if (!CanRead(data, offset, 10)) return false;
        int coverageIndex = FindCoverage(data, offset + ReadU16(data, offset + 2), glyphs.GetGlyph(position));
        if (coverageIndex < 0) return false;
        int second = glyphs.NextEligibleIndex(position + 1, lookupFlags);
        if (second < 0) return false;
        ushort valueFormat1 = ReadU16(data, offset + 4);
        ushort valueFormat2 = ReadU16(data, offset + 6);
        int valueSize1 = GetValueRecordSize(valueFormat1);
        int valueSize2 = GetValueRecordSize(valueFormat2);
        int value1Offset;
        int value2Offset;
        if (ReadU16(data, offset) == 1)
        {
            ushort setCount = ReadU16(data, offset + 8);
            if ((uint)coverageIndex >= setCount || !CanRead(data, offset + 10 + coverageIndex * 2, 2)) return false;
            int pairSet = offset + ReadU16(data, offset + 10 + coverageIndex * 2);
            if (!CanRead(data, pairSet, 2)) return false;
            ushort pairCount = ReadU16(data, pairSet);
            int recordSize = 2 + valueSize1 + valueSize2;
            int record = pairSet + 2;
            bool found = false;
            for (var pair = 0; pair < pairCount; pair++, record += recordSize)
            {
                if (!CanRead(data, record, recordSize)) return false;
                if (ReadU16(data, record) != glyphs.GetGlyph(second)) continue;
                value1Offset = record + 2;
                value2Offset = value1Offset + valueSize1;
                found = true;
                if (valueFormat1 != 0) ApplyValueRecord(data, glyphs, position, value1Offset, valueFormat1);
                if (valueFormat2 != 0) ApplyValueRecord(data, glyphs, second, value2Offset, valueFormat2);
                break;
            }
            return found;
        }
        if (ReadU16(data, offset) != 2 || !CanRead(data, offset, 16)) return false;
        int class1 = GetGlyphClass(data, offset + ReadU16(data, offset + 8), glyphs.GetGlyph(position));
        int class2 = GetGlyphClass(data, offset + ReadU16(data, offset + 10), glyphs.GetGlyph(second));
        int class1Count = ReadU16(data, offset + 12);
        int class2Count = ReadU16(data, offset + 14);
        if ((uint)class1 >= class1Count || (uint)class2 >= class2Count) return false;
        int recordOffset = offset + 16 + (class1 * class2Count + class2) * (valueSize1 + valueSize2);
        if (!CanRead(data, recordOffset, valueSize1 + valueSize2)) return false;
        if (valueFormat1 != 0) ApplyValueRecord(data, glyphs, position, recordOffset, valueFormat1);
        if (valueFormat2 != 0) ApplyValueRecord(data, glyphs, second, recordOffset + valueSize1, valueFormat2);
        return true;
    }

    private static bool ApplyMarkToBasePosition(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int offset,
        int markIndex,
        ushort lookupFlags)
    {
        if (!TryReadMarkAttachmentHeader(data, glyphs, offset, markIndex,
                out int markCoverageIndex, out int targetCoverage, out int classCount,
                out int markArray, out int targetArray)) return false;
        int targetIndex = glyphs.PreviousEligibleIndex(markIndex - 1, lookupFlags, targetCoverage, data, skipMarks: true);
        if (targetIndex < 0 || glyphs.GetGlyphClassKind(targetIndex) is not (GlyphClassKind.Base or GlyphClassKind.Zero)) return false;
        int targetCoverageIndex = FindCoverage(data, targetCoverage, glyphs.GetGlyph(targetIndex));
        return ApplyMarkAttachment(data, glyphs, markIndex, targetIndex, markCoverageIndex,
            targetCoverageIndex, classCount, markArray, targetArray, targetArray + 2);
    }

    private static bool ApplyMarkToLigaturePosition(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int offset,
        int markIndex,
        ushort lookupFlags)
    {
        if (!TryReadMarkAttachmentHeader(data, glyphs, offset, markIndex,
                out int markCoverageIndex, out int ligatureCoverage, out int classCount,
                out int markArray, out int ligatureArray)) return false;
        int ligatureIndex = glyphs.PreviousEligibleIndex(markIndex - 1, lookupFlags, ligatureCoverage, data, skipMarks: true);
        if (ligatureIndex < 0 || glyphs.GetGlyphClassKind(ligatureIndex) != GlyphClassKind.Ligature) return false;
        int ligatureCoverageIndex = FindCoverage(data, ligatureCoverage, glyphs.GetGlyph(ligatureIndex));
        if (!CanRead(data, ligatureArray + 2 + ligatureCoverageIndex * 2, 2)) return false;
        int ligatureAttach = ligatureArray + ReadU16(data, ligatureArray + 2 + ligatureCoverageIndex * 2);
        if (!CanRead(data, ligatureAttach, 2)) return false;
        int componentCount = ReadU16(data, ligatureAttach);
        if (componentCount == 0) return false;
        int component = Math.Min(glyphs.GetLigatureComponent(markIndex, ligatureIndex), componentCount - 1);
        int componentRecords = ligatureAttach + 2 + component * classCount * 2;
        return ApplyMarkAttachment(data, glyphs, markIndex, ligatureIndex, markCoverageIndex,
            0, classCount, markArray, ligatureAttach, componentRecords);
    }

    private static bool ApplyMarkToMarkPosition(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int offset,
        int markIndex,
        ushort lookupFlags)
    {
        if (!TryReadMarkAttachmentHeader(data, glyphs, offset, markIndex,
                out int markCoverageIndex, out int mark2Coverage, out int classCount,
                out int mark1Array, out int mark2Array)) return false;
        int mark2Index = glyphs.PreviousEligibleIndex(markIndex - 1, lookupFlags, mark2Coverage, data, skipMarks: false);
        if (mark2Index < 0 || glyphs.GetGlyphClassKind(mark2Index) != GlyphClassKind.Mark) return false;
        int mark2CoverageIndex = FindCoverage(data, mark2Coverage, glyphs.GetGlyph(mark2Index));
        return ApplyMarkAttachment(data, glyphs, markIndex, mark2Index, markCoverageIndex,
            mark2CoverageIndex, classCount, mark1Array, mark2Array, mark2Array + 2);
    }

    private static bool TryReadMarkAttachmentHeader(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int offset,
        int markIndex,
        out int markCoverageIndex,
        out int targetCoverage,
        out int classCount,
        out int markArray,
        out int targetArray)
    {
        markCoverageIndex = -1;
        targetCoverage = classCount = markArray = targetArray = 0;
        if (!CanRead(data, offset, 12) || ReadU16(data, offset) != 1) return false;
        int markCoverage = offset + ReadU16(data, offset + 2);
        markCoverageIndex = FindCoverage(data, markCoverage, glyphs.GetGlyph(markIndex));
        if (markCoverageIndex < 0) return false;
        targetCoverage = offset + ReadU16(data, offset + 4);
        classCount = ReadU16(data, offset + 6);
        markArray = offset + ReadU16(data, offset + 8);
        targetArray = offset + ReadU16(data, offset + 10);
        return classCount > 0;
    }

    private static bool ApplyMarkAttachment(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int markIndex,
        int targetIndex,
        int markCoverageIndex,
        int targetCoverageIndex,
        int classCount,
        int markArray,
        int targetAnchorBase,
        int targetRecordBase)
    {
        int markRecord = markArray + 2 + markCoverageIndex * 4;
        if (!CanRead(data, markRecord, 4)) return false;
        int markClass = ReadU16(data, markRecord);
        if ((uint)markClass >= classCount) return false;
        int markAnchorOffset = markArray + ReadU16(data, markRecord + 2);
        int targetRecord = targetRecordBase + (targetCoverageIndex * classCount + markClass) * 2;
        if (!CanRead(data, targetRecord, 2)) return false;
        ushort targetAnchorRelative = ReadU16(data, targetRecord);
        if (targetAnchorRelative == 0) return false;
        int targetAnchorOffset = targetAnchorBase + targetAnchorRelative;
        if (!TryReadAnchor(data, markAnchorOffset, out int markX, out int markY) ||
            !TryReadAnchor(data, targetAnchorOffset, out int targetX, out int targetY)) return false;
        glyphs.Attach(markIndex, targetIndex, targetX - markX, targetY - markY);
        return true;
    }

    private static bool TryReadAnchor(ReadOnlySpan<byte> data, int offset, out int x, out int y)
    {
        x = y = 0;
        if (!CanRead(data, offset, 6) || ReadU16(data, offset) is < 1 or > 3) return false;
        x = ReadI16(data, offset + 2);
        y = ReadI16(data, offset + 4);
        return true;
    }

    private static bool ApplyValueRecord(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int index,
        int offset,
        ushort valueFormat)
    {
        int size = GetValueRecordSize(valueFormat);
        if (!CanRead(data, offset, size)) return false;
        int cursor = offset;
        short xPlacement = 0;
        short yPlacement = 0;
        short xAdvance = 0;
        short yAdvance = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            if ((valueFormat & (1 << bit)) == 0) continue;
            short value = ReadI16(data, cursor);
            cursor += 2;
            switch (bit)
            {
                case 0: xPlacement = value; break;
                case 1: yPlacement = value; break;
                case 2: xAdvance = value; break;
                case 3: yAdvance = value; break;
            }
        }
        glyphs.AppendGlyphOffset(index, xPlacement, yPlacement);
        glyphs.AppendGlyphAdvance(index, xAdvance, yAdvance);
        return true;
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

    private readonly record struct EnabledLookup(ushort LookupIndex, int Value, string Tag);

    private static IEnumerable<EnabledLookup> GetEnabledLookupIndices(
        GlyphShapingTableEntry table,
        FeatureList.FeatureTable[] features,
        TextShapingOptions options,
        string script)
    {
        Dictionary<string, int> requested = CreateFeatureMap(options.Features);
        HashSet<ushort>? allowedFeatures = GetLanguageFeatureIndices(
            table.ScriptList,
            script,
            options.Language,
            out ushort? requiredFeatureIndex);
        var lookups = new List<EnabledLookup>();
        var lookupPositions = new Dictionary<ushort, int>();
        if (requiredFeatureIndex is ushort required && required < features.Length)
        {
            AddFeatureLookups(features[required], 1, lookups, lookupPositions);
        }
        foreach (OpenTypeFeatureSetting setting in options.Features)
        {
            if (!requested.TryGetValue(setting.Tag, out int value) || value == 0 || value != setting.Value)
            {
                continue;
            }
            for (ushort featureIndex = 0; featureIndex < features.Length; featureIndex++)
            {
                FeatureList.FeatureTable feature = features[featureIndex];
                if (feature.TagName != setting.Tag ||
                    (allowedFeatures is not null && !allowedFeatures.Contains(featureIndex)))
                {
                    continue;
                }

                AddFeatureLookups(feature, value, lookups, lookupPositions);
            }
        }

        return lookups;
    }

    private static void AddFeatureLookups(
        FeatureList.FeatureTable feature,
        int value,
        List<EnabledLookup> lookups,
        Dictionary<ushort, int> lookupPositions)
    {
        for (var lookupIndex = 0; lookupIndex < feature.LookupListIndices.Length; lookupIndex++)
        {
            ushort index = feature.LookupListIndices[lookupIndex];
            if (lookupPositions.TryGetValue(index, out int existing))
            {
                lookups[existing] = new EnabledLookup(index, value, feature.TagName);
            }
            else
            {
                lookupPositions[index] = lookups.Count;
                lookups.Add(new EnabledLookup(index, value, feature.TagName));
            }
        }
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
        string? language,
        out ushort? requiredFeatureIndex)
    {
        requiredFeatureIndex = null;
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
            requiredFeatureIndex = languageTable.RequiredFeatureIndex;
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
            if (value is >= 0x0700 and <= 0x074F) return "syrc";
            if (value is >= 0x07C0 and <= 0x07FF) return "nkoo";
            if (value is >= 0x0840 and <= 0x085F) return "mand";
            if (value is >= 0x0900 and <= 0x097F) return "deva";
            if (value is >= 0x0980 and <= 0x09FF) return "beng";
            if (value is >= 0x0A00 and <= 0x0A7F) return "guru";
            if (value is >= 0x0A80 and <= 0x0AFF) return "gujr";
            if (value is >= 0x0B00 and <= 0x0B7F) return "orya";
            if (value is >= 0x0B80 and <= 0x0BFF) return "taml";
            if (value is >= 0x0C00 and <= 0x0C7F) return "telu";
            if (value is >= 0x0C80 and <= 0x0CFF) return "knda";
            if (value is >= 0x0D00 and <= 0x0D7F) return "mlym";
            if (value is >= 0x0D80 and <= 0x0DFF) return "sinh";
            if (value is >= 0x0E00 and <= 0x0E7F) return "thai";
            if (value is >= 0x0E80 and <= 0x0EFF) return "lao ";
            if (value is >= 0x0F00 and <= 0x0FFF) return "tibt";
            if (value is >= 0x1000 and <= 0x109F or >= 0xA9E0 and <= 0xA9FF or >= 0xAA60 and <= 0xAA7F) return "mymr";
            if (value is >= 0x1800 and <= 0x18AF) return "mong";
            if (value is >= 0x1780 and <= 0x17FF) return "khmr";
            if (value is >= 0xA840 and <= 0xA87F) return "phag";
            if (value is >= 0x10AC0 and <= 0x10AFF) return "mani";
            if (value is >= 0x10B80 and <= 0x10BAF) return "phlp";
            if (value is >= 0x10D00 and <= 0x10D3F) return "rohg";
            if (value is >= 0x10F30 and <= 0x10F6F) return "sogd";
            if (value is >= 0x10F70 and <= 0x10FAF) return "ougr";
            if (value is >= 0x10FB0 and <= 0x10FDF) return "chrs";
            if (value is >= 0x1E900 and <= 0x1E95F) return "adlm";
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
        private static readonly ArabicStateEntry[] s_arabicStateTable =
        [
            new(None, None, 0), new(None, Isolated, 2), new(None, Isolated, 1), new(None, Isolated, 2), new(None, Isolated, 1), new(None, Isolated, 6),
            new(None, None, 0), new(None, Isolated, 2), new(None, Isolated, 1), new(None, Isolated, 2), new(None, Final2, 5), new(None, Isolated, 6),
            new(None, None, 0), new(None, Isolated, 2), new(Initial, Final, 1), new(Initial, Final, 3), new(Initial, Final, 4), new(Initial, Final, 6),
            new(None, None, 0), new(None, Isolated, 2), new(Medial, Final, 1), new(Medial, Final, 3), new(Medial, Final, 4), new(Medial, Final, 6),
            new(None, None, 0), new(None, Isolated, 2), new(Medial2, Isolated, 1), new(Medial2, Isolated, 2), new(Medial2, Final2, 5), new(Medial2, Isolated, 6),
            new(None, None, 0), new(None, Isolated, 2), new(Isolated, Isolated, 1), new(Isolated, Isolated, 2), new(Isolated, Final2, 5), new(Isolated, Isolated, 6),
            new(None, None, 0), new(None, Isolated, 2), new(None, Isolated, 1), new(None, Isolated, 2), new(None, Final3, 5), new(None, Isolated, 6)
        ];

        private readonly List<GlyphRecord> _glyphs;
        private readonly Typeface? _typeface;
        private readonly TtfFont _font;

        private GlyphSubstitutionBuffer(List<GlyphRecord> glyphs, TtfFont font)
        {
            _glyphs = glyphs;
            _font = font;
            _typeface = font.LayoutTypeface;
        }

        public int Count => _glyphs.Count;
        public ushort this[int index] => _glyphs[index].GlyphIndex;
        public GlyphRecord GetRecord(int index) => _glyphs[index];

        public void PrepareKhmerShaping(string script)
        {
            if (script != "khmr")
            {
                return;
            }

            InsertBrokenKhmerDottedCircles();

            for (var start = 0; start < _glyphs.Count;)
            {
                if (!IsKhmerCharacter(_glyphs[start].CodePoint))
                {
                    start++;
                    continue;
                }

                int end = FindKhmerSyllableEnd(start);
                PrepareKhmerSyllable(start, end);
                start = end;
            }
        }

        private void InsertBrokenKhmerDottedCircles()
        {
            bool hasBase = false;
            for (var index = 0; index < _glyphs.Count; index++)
            {
                uint codePoint = _glyphs[index].CodePoint;
                if (IsKhmerBase(codePoint) || codePoint == 0x17D9)
                {
                    hasBase = true;
                    continue;
                }
                if (codePoint is 0x200C or 0x200D)
                {
                    continue;
                }
                if (!IsKhmerMark(codePoint))
                {
                    hasBase = false;
                    continue;
                }
                if (!hasBase)
                {
                    GlyphRecord mark = _glyphs[index];
                    _glyphs.Insert(index, new GlyphRecord(_font.GetGlyphIndex(0x25CC), mark.Cluster, 0x25CC));
                    index++;
                    hasBase = true;
                }
                if (codePoint == 0x17D2)
                {
                    hasBase = false;
                }
            }
        }

        private int FindKhmerSyllableEnd(int start)
        {
            int index = start + 1;
            bool previousWasCoeng = _glyphs[start].CodePoint == 0x17D2;
            while (index < _glyphs.Count)
            {
                uint codePoint = _glyphs[index].CodePoint;
                if (codePoint is 0x200C or 0x200D || IsKhmerMark(codePoint))
                {
                    previousWasCoeng = codePoint == 0x17D2;
                    index++;
                    continue;
                }
                if (IsKhmerBase(codePoint) && previousWasCoeng)
                {
                    previousWasCoeng = false;
                    index++;
                    continue;
                }
                break;
            }
            return index;
        }

        private void PrepareKhmerSyllable(int start, int end)
        {
            if (end - start <= 1)
            {
                return;
            }

            for (var index = start + 1; index < end; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                glyph.ScriptFeatureMask |= KhmerPostBaseMask;
                _glyphs[index] = glyph;
            }

            for (var index = start + 1; index + 1 < end; index++)
            {
                if (_glyphs[index].CodePoint == 0x17D2 && IsKhmerConsonant(_glyphs[index + 1].CodePoint))
                {
                    MergeCluster(start, end);
                    break;
                }
            }

            int coengCount = 0;
            for (var index = start + 1; index < end; index++)
            {
                if (_glyphs[index].CodePoint == 0x17D2 && coengCount <= 2 && index + 1 < end)
                {
                    coengCount++;
                    if (_glyphs[index + 1].CodePoint == 0x179A)
                    {
                        GlyphRecord coeng = _glyphs[index];
                        GlyphRecord ro = _glyphs[index + 1];
                        coeng.ScriptFeatureMask |= KhmerPrefMask;
                        ro.ScriptFeatureMask |= KhmerPrefMask;
                        int cluster = MergeCluster(start, index + 2);
                        coeng.Cluster = cluster;
                        ro.Cluster = cluster;
                        _glyphs.RemoveRange(index, 2);
                        _glyphs.Insert(start, ro);
                        _glyphs.Insert(start, coeng);

                        for (var following = index + 2; following < end; following++)
                        {
                            GlyphRecord glyph = _glyphs[following];
                            glyph.ScriptFeatureMask |= KhmerCfarMask;
                            _glyphs[following] = glyph;
                        }
                        coengCount = 2;
                    }
                }
                else if (IsKhmerPreBaseVowel(_glyphs[index].CodePoint))
                {
                    GlyphRecord vowel = _glyphs[index];
                    vowel.Cluster = MergeCluster(start, index + 1);
                    _glyphs.RemoveAt(index);
                    _glyphs.Insert(start, vowel);
                }
            }
        }

        private int MergeCluster(int start, int end)
        {
            int cluster = int.MaxValue;
            for (var index = start; index < end; index++)
            {
                cluster = Math.Min(cluster, _glyphs[index].Cluster);
            }
            for (var index = start; index < end; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                glyph.Cluster = cluster;
                _glyphs[index] = glyph;
            }
            return cluster;
        }

        private static bool IsKhmerCharacter(uint codePoint) =>
            codePoint is >= 0x1780 and <= 0x17FF or 0x25CC;

        private static bool IsKhmerBase(uint codePoint) =>
            codePoint is >= 0x1780 and <= 0x17B3 or 0x25CC;

        private static bool IsKhmerConsonant(uint codePoint) =>
            codePoint is >= 0x1780 and <= 0x17A2;

        private static bool IsKhmerMark(uint codePoint) =>
            codePoint is >= 0x17B4 and <= 0x17D3 or >= 0x17DD and <= 0x17DD;

        private static bool IsKhmerPreBaseVowel(uint codePoint) =>
            codePoint is >= 0x17C1 and <= 0x17C3;

        public void AssignArabicJoiningActions(string script)
        {
            if (script is not ("adlm" or "arab" or "chrs" or "rohg" or "mand" or "mani" or
                "mong" or "nkoo" or "ougr" or "phag" or "phlp" or "sogd" or "syrc"))
            {
                return;
            }

            int previous = -1;
            int state = 0;
            for (var index = 0; index < _glyphs.Count; index++)
            {
                ArabicJoiningData.JoiningType joiningType = ArabicJoiningData.GetJoiningType(_glyphs[index].CodePoint);
                if (joiningType == ArabicJoiningData.JoiningType.Transparent)
                {
                    GlyphRecord transparent = _glyphs[index];
                    transparent.ArabicAction = None;
                    _glyphs[index] = transparent;
                    continue;
                }

                ArabicStateEntry entry = s_arabicStateTable[state * 6 + (int)joiningType];
                if (entry.PreviousAction != None && previous >= 0)
                {
                    GlyphRecord prior = _glyphs[previous];
                    prior.ArabicAction = entry.PreviousAction;
                    _glyphs[previous] = prior;
                }

                GlyphRecord current = _glyphs[index];
                current.ArabicAction = entry.CurrentAction;
                _glyphs[index] = current;
                previous = index;
                state = entry.NextState;
            }
        }

        public void AssignFractionActions()
        {
            for (var slash = 0; slash < _glyphs.Count; slash++)
            {
                if (_glyphs[slash].CodePoint != 0x2044) continue;
                int numeratorStart = slash;
                while (numeratorStart > 0 && IsDecimalDigit(_glyphs[numeratorStart - 1].CodePoint)) numeratorStart--;
                int denominatorEnd = slash + 1;
                while (denominatorEnd < _glyphs.Count && IsDecimalDigit(_glyphs[denominatorEnd].CodePoint)) denominatorEnd++;
                if (numeratorStart == slash || denominatorEnd == slash + 1) continue;
                for (var index = numeratorStart; index < slash; index++)
                {
                    GlyphRecord glyph = _glyphs[index];
                    glyph.FractionAction = FractionNumerator;
                    _glyphs[index] = glyph;
                }
                for (var index = slash + 1; index < denominatorEnd; index++)
                {
                    GlyphRecord glyph = _glyphs[index];
                    glyph.FractionAction = FractionDenominator;
                    _glyphs[index] = glyph;
                }
                GlyphRecord fractionSlash = _glyphs[slash];
                fractionSlash.FractionAction = FractionSlash;
                _glyphs[slash] = fractionSlash;
            }
        }

        private static bool IsDecimalDigit(uint codePoint) =>
            Rune.GetUnicodeCategory(new Rune((int)codePoint)) == UnicodeCategory.DecimalDigitNumber;

        public bool IsFeatureEnabled(int index, string tag, TextShapingOptions options)
        {
            byte action = _glyphs[index].ArabicAction;
            bool explicitlyEnabled = options.ExplicitFeatureTags?.Contains(tag) == true;
            return tag switch
            {
                "frac" => explicitlyEnabled || _glyphs[index].FractionAction != FractionNone,
                "numr" => explicitlyEnabled || _glyphs[index].FractionAction == FractionNumerator,
                "dnom" => explicitlyEnabled || _glyphs[index].FractionAction == FractionDenominator,
                "pref" => (_glyphs[index].ScriptFeatureMask & KhmerPrefMask) != 0,
                "blwf" or "abvf" or "pstf" => (_glyphs[index].ScriptFeatureMask & KhmerPostBaseMask) != 0,
                "cfar" => (_glyphs[index].ScriptFeatureMask & KhmerCfarMask) != 0,
                "isol" => action == Isolated,
                "fina" => action == Final,
                "fin2" => action == Final2,
                "fin3" => action == Final3,
                "medi" => action == Medial,
                "med2" => action == Medial2,
                "init" => action == Initial,
                _ => true
            };
        }

        public int NextVisibleIndex(int index, ushort lookupFlags)
        {
            while (index < _glyphs.Count)
            {
                GlyphRecord glyph = _glyphs[index];
                if (!IsDefaultIgnorable(glyph.CodePoint))
                {
                    GlyphClassKind glyphClass = _typeface?.GetGlyph(glyph.GlyphIndex).GlyphClass ?? GlyphClassKind.Zero;
                    bool ignored = glyphClass switch
                    {
                        GlyphClassKind.Base => (lookupFlags & 0x0002) != 0,
                        GlyphClassKind.Ligature => (lookupFlags & 0x0004) != 0,
                        GlyphClassKind.Mark => (lookupFlags & 0x0008) != 0,
                        _ => false
                    };
                    if (!ignored) return index;
                }
                index++;
            }
            return -1;
        }

        public void ReplaceLigature(ReadOnlySpan<int> componentIndices, ushort ligatureGlyph)
        {
            if (componentIndices.IsEmpty) return;
            GlyphRecord ligature = _glyphs[componentIndices[0]];
            ligature.GlyphIndex = ligatureGlyph;
            ligature.LigatureComponentCount = checked((byte)Math.Min(componentIndices.Length, byte.MaxValue));
            int first = componentIndices[0];
            int last = componentIndices[^1];
            for (var component = 0; component + 1 < componentIndices.Length; component++)
            {
                for (var index = componentIndices[component] + 1; index < componentIndices[component + 1]; index++)
                {
                    GlyphRecord skipped = _glyphs[index];
                    skipped.LigatureComponent = checked((byte)Math.Min(component, byte.MaxValue - 1));
                    _glyphs[index] = skipped;
                }
            }
            for (var index = first; index <= last; index++)
            {
                ligature.Cluster = Math.Min(ligature.Cluster, _glyphs[index].Cluster);
            }
            for (var index = first; index <= last; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                glyph.Cluster = ligature.Cluster;
                _glyphs[index] = glyph;
            }
            _glyphs[first] = ligature;
            for (var index = componentIndices.Length - 1; index >= 1; index--)
            {
                _glyphs.RemoveAt(componentIndices[index]);
            }
        }

        public static bool IsDefaultIgnorable(uint codePoint) => codePoint is
            0x00AD or 0x034F or 0x061C or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or
            >= 0x180B and <= 0x180F or >= 0x200B and <= 0x200F or
            >= 0x202A and <= 0x202E or >= 0x2060 and <= 0x206F or
            0x3164 or 0xFEFF or 0xFFA0 or >= 0xFFF0 and <= 0xFFF8 or
            >= 0xFE00 and <= 0xFE0F or
            >= 0x1BCA0 and <= 0x1BCAF or >= 0x1D173 and <= 0x1D17A or
            >= 0xE0000 and <= 0xE0FFF;

        public static GlyphSubstitutionBuffer Create(string text, TtfFont font, string script)
        {
            if (text.Length > MaximumShapedGlyphCount)
            {
                throw new InvalidOperationException($"Text exceeds the {MaximumShapedGlyphCount} glyph shaping limit.");
            }
            var glyphs = new List<GlyphRecord>(text.Length);
            bool textIsNormalized = text.IsNormalized(NormalizationForm.FormC);
            for (var graphemeStart = 0; graphemeStart < text.Length;)
            {
                int graphemeLength = StringInfo.GetNextTextElementLength(text.AsSpan(graphemeStart));
                int graphemeEnd = checked(graphemeStart + graphemeLength);
                string? normalizedGrapheme = textIsNormalized
                    ? null
                    : text.Substring(graphemeStart, graphemeLength).Normalize(NormalizationForm.FormC);
                ReadOnlySpan<char> grapheme = normalizedGrapheme is null
                    ? text.AsSpan(graphemeStart, graphemeLength)
                    : normalizedGrapheme.AsSpan();
                Rune.DecodeFromUtf16(grapheme, out Rune firstRune, out _);
                bool separateClusters = IsPrepend(firstRune.Value);
                for (var index = 0; index < grapheme.Length;)
                {
                    Rune.DecodeFromUtf16(grapheme[index..], out Rune rune, out int consumed);
                    int cluster = separateClusters && normalizedGrapheme is null
                        ? graphemeStart + index
                        : graphemeStart;
                    if (IsVariationSelector(rune.Value) && glyphs.Count > 0)
                    {
                        GlyphRecord previous = glyphs[^1];
                        if (font.TryGetVariationGlyph(previous.CodePoint, (uint)rune.Value, out ushort variationGlyph))
                        {
                            previous.GlyphIndex = variationGlyph;
                            glyphs[^1] = previous;
                            index += consumed;
                            continue;
                        }
                    }
                    if (script == "khmr" && IsKhmerSplitMatra((uint)rune.Value))
                    {
                        AppendMappedRune(glyphs, font, new Rune(0x17C1), cluster);
                    }
                    AppendNormalizedRune(glyphs, font, rune, cluster);
                    index += consumed;
                }
                graphemeStart = graphemeEnd;
            }
            return new GlyphSubstitutionBuffer(glyphs, font);
        }

        private static void AppendNormalizedRune(
            List<GlyphRecord> glyphs,
            TtfFont font,
            Rune rune,
            int cluster)
        {
            uint codePoint = (uint)rune.Value;
            ushort glyphIndex = font.GetGlyphIndex(codePoint);
            if (glyphIndex == 0)
            {
                string scalar = rune.ToString();
                string decomposed = scalar.Normalize(NormalizationForm.FormD);
                if (!decomposed.Equals(scalar, StringComparison.Ordinal))
                {
                    foreach (Rune component in decomposed.EnumerateRunes())
                    {
                        AppendMappedRune(glyphs, font, component, cluster);
                    }
                    return;
                }
            }
            AppendMappedRune(glyphs, font, rune, cluster);
        }

        private static void AppendMappedRune(
            List<GlyphRecord> glyphs,
            TtfFont font,
            Rune rune,
            int cluster)
        {
            if (glyphs.Count >= MaximumShapedGlyphCount)
            {
                throw new InvalidOperationException($"Unicode decomposition exceeds the {MaximumShapedGlyphCount} glyph shaping limit.");
            }
            uint codePoint = (uint)rune.Value;
            ushort glyphIndex = font.GetGlyphIndex(codePoint);
            byte spaceFallback = glyphIndex == 0 ? GetSpaceFallback(codePoint) : SpaceNone;
            if (spaceFallback != SpaceNone)
            {
                ushort spaceGlyph = font.GetGlyphIndex(0x20);
                if (spaceGlyph != 0)
                {
                    glyphIndex = spaceGlyph;
                }
                else
                {
                    spaceFallback = SpaceNone;
                }
            }
            glyphs.Add(new GlyphRecord(glyphIndex, cluster, codePoint)
            {
                SpaceFallback = spaceFallback
            });
        }

        private static bool IsPrepend(int codePoint) => codePoint is
            >= 0x0600 and <= 0x0605 or 0x06DD or 0x070F or
            >= 0x0890 and <= 0x0891 or 0x08E2 or 0x0D4E or
            0x110BD or 0x110CD or >= 0x111C2 and <= 0x111C3 or
            0x1193F or 0x11941 or 0x11A3A or >= 0x11A84 and <= 0x11A89 or
            0x11D46;

        private static bool IsVariationSelector(int codePoint) =>
            codePoint is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF;

        private static bool IsKhmerSplitMatra(uint codePoint) =>
            codePoint is 0x17BE or 0x17BF or 0x17C0 or 0x17C4 or 0x17C5;

        private static byte GetSpaceFallback(uint codePoint) => codePoint switch
        {
            0x0020 or 0x00A0 => Space,
            0x2000 or 0x2002 => SpaceEm2,
            0x2001 or 0x2003 or 0x3000 => SpaceEm,
            0x2004 => SpaceEm3,
            0x2005 => SpaceEm4,
            0x2006 => SpaceEm6,
            0x2007 => SpaceFigure,
            0x2008 => SpacePunctuation,
            0x2009 => SpaceEm5,
            0x200A => SpaceEm16,
            0x202F => SpaceNarrow,
            0x205F => SpaceFourEm18,
            _ => SpaceNone
        };

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
        private readonly ReadOnlyMemory<byte> _gdefTable;

        public GlyphPositionBuffer(GlyphSubstitutionBuffer substitutions, TtfFont font, ShapingDirection direction)
        {
            _typeface = font.LayoutTypeface;
            font.TryGetTable("GDEF", out _gdefTable);
            _glyphs = new GlyphRecord[substitutions.Count];
            bool vertical = direction is ShapingDirection.TopToBottom or ShapingDirection.BottomToTop;
            for (var index = 0; index < _glyphs.Length; index++)
            {
                GlyphRecord record = substitutions.GetRecord(index);
                bool defaultIgnorable = GlyphSubstitutionBuffer.IsDefaultIgnorable(record.CodePoint);
                if (defaultIgnorable)
                {
                    record.GlyphIndex = font.GetGlyphIndex(' ');
                }
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
                ApplySpaceFallback(font, vertical, ref record);
                if (defaultIgnorable)
                {
                    record.AdvanceX = 0;
                    record.AdvanceY = 0;
                }
                _glyphs[index] = record;
            }
        }

        private static void ApplySpaceFallback(TtfFont font, bool vertical, ref GlyphRecord record)
        {
            int sign = vertical ? -1 : 1;
            int advance = vertical ? record.AdvanceY : record.AdvanceX;
            switch (record.SpaceFallback)
            {
                case SpaceEm:
                case SpaceEm2:
                case SpaceEm3:
                case SpaceEm4:
                case SpaceEm5:
                case SpaceEm6:
                case SpaceEm16:
                    int divisor = record.SpaceFallback;
                    advance = sign * ((font.UnitsPerEm + divisor / 2) / divisor);
                    break;
                case SpaceFourEm18:
                    advance = checked((int)(sign * ((long)font.UnitsPerEm * 4 / 18)));
                    break;
                case SpaceFigure:
                    for (uint codePoint = '0'; codePoint <= '9'; codePoint++)
                    {
                        ushort glyph = font.GetGlyphIndex(codePoint);
                        if (glyph == 0) continue;
                        advance = vertical
                            ? -(int)MathF.Round(font.GetAdvanceHeight(glyph, font.UnitsPerEm))
                            : (int)MathF.Round(font.GetAdvanceWidth(glyph, font.UnitsPerEm));
                        break;
                    }
                    break;
                case SpacePunctuation:
                    ushort punctuation = font.GetGlyphIndex('.');
                    if (punctuation == 0) punctuation = font.GetGlyphIndex(',');
                    if (punctuation != 0)
                    {
                        advance = vertical
                            ? -(int)MathF.Round(font.GetAdvanceHeight(punctuation, font.UnitsPerEm))
                            : (int)MathF.Round(font.GetAdvanceWidth(punctuation, font.UnitsPerEm));
                    }
                    break;
                case SpaceNarrow:
                    advance /= 2;
                    break;
            }

            short value = checked((short)Math.Clamp(advance, short.MinValue, short.MaxValue));
            if (vertical) record.AdvanceY = value;
            else record.AdvanceX = value;
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

        public int NextEligibleIndex(int index, ushort lookupFlags)
        {
            while (index < _glyphs.Length)
            {
                if (!IsIgnored(index, lookupFlags)) return index;
                index++;
            }
            return -1;
        }

        public int PreviousEligibleIndex(
            int index,
            ushort lookupFlags,
            int coverageOffset,
            ReadOnlySpan<byte> data,
            bool skipMarks)
        {
            while (index >= 0 && (IsIgnored(index, lookupFlags) || skipMarks && IsMark(index))) index--;
            return index >= 0 && FindCoverage(data, coverageOffset, GetGlyph(index)) >= 0 ? index : -1;
        }

        private bool IsIgnored(int index, ushort lookupFlags)
        {
            if (GlyphSubstitutionBuffer.IsDefaultIgnorable(_glyphs[index].CodePoint)) return true;
            GlyphClassKind glyphClass = GetGlyphClassKind(index);
            if (glyphClass == GlyphClassKind.Mark)
            {
                int requiredMarkClass = lookupFlags >> 8;
                if (requiredMarkClass != 0 && GetMarkAttachmentClass(_glyphs[index].GlyphIndex) != requiredMarkClass)
                {
                    return true;
                }
            }
            return glyphClass switch
            {
                GlyphClassKind.Base => (lookupFlags & 0x0002) != 0,
                GlyphClassKind.Ligature => (lookupFlags & 0x0004) != 0,
                GlyphClassKind.Mark => (lookupFlags & 0x0008) != 0,
                _ => false
            };
        }

        private int GetMarkAttachmentClass(ushort glyph)
        {
            ReadOnlySpan<byte> data = _gdefTable.Span;
            if (!CanRead(data, 10, 2)) return 0;
            int classDefOffset = ReadU16(data, 10);
            return classDefOffset == 0 ? 0 : OpenTypeTextShaper.GetGlyphClass(data, classDefOffset, glyph);
        }

        public int GetLigatureComponent(int markIndex, int ligatureIndex)
        {
            if (_glyphs[markIndex].LigatureComponent != byte.MaxValue)
            {
                return _glyphs[markIndex].LigatureComponent;
            }
            int componentCount = _glyphs[ligatureIndex].LigatureComponentCount;
            return componentCount > 0 ? componentCount - 1 : int.MaxValue;
        }

        public void Attach(int markIndex, int targetIndex, int anchorDeltaX, int anchorDeltaY)
        {
            GlyphRecord mark = _glyphs[markIndex];
            int x = anchorDeltaX - mark.OffsetX;
            int y = anchorDeltaY - mark.OffsetY;
            for (var index = targetIndex; index < markIndex; index++)
            {
                x -= _glyphs[index].AdvanceX;
                y -= _glyphs[index].AdvanceY;
            }
            mark.OffsetX = AddClamped(mark.OffsetX, x);
            mark.OffsetY = AddClamped(mark.OffsetY, y);
            mark.AttachmentTarget = targetIndex;
            _glyphs[markIndex] = mark;
        }

        public void ResolveAttachmentOffsets()
        {
            for (var index = 0; index < _glyphs.Length; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                if (glyph.AttachmentTarget < 0 || glyph.AttachmentTarget >= index)
                {
                    continue;
                }
                GlyphRecord target = _glyphs[glyph.AttachmentTarget];
                glyph.OffsetX = AddClamped(glyph.OffsetX, target.OffsetX);
                glyph.OffsetY = AddClamped(glyph.OffsetY, target.OffsetY);
                _glyphs[index] = glyph;
            }
        }

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

        public void ResolveBackwardMarkOffsets(ShapingDirection direction)
        {
            for (var index = 1; index < _glyphs.Length; index++)
            {
                GlyphRecord mark = _glyphs[index];
                if (!IsMark(index))
                {
                    continue;
                }

                int baseIndex = index - 1;
                while (baseIndex >= 0 &&
                       _glyphs[baseIndex].Cluster == mark.Cluster &&
                       IsMark(baseIndex))
                {
                    baseIndex--;
                }
                if (baseIndex < 0 || _glyphs[baseIndex].Cluster != mark.Cluster)
                {
                    continue;
                }

                if (direction == ShapingDirection.RightToLeft)
                {
                    mark.OffsetX = AddClamped(mark.OffsetX, _glyphs[baseIndex].AdvanceX);
                }
                else
                {
                    mark.OffsetY = AddClamped(mark.OffsetY, _glyphs[baseIndex].AdvanceY);
                }
                _glyphs[index] = mark;
            }
        }

        private bool IsMark(int index)
        {
            GlyphClassKind glyphClass = GetGlyphClassKind(index);
            if (glyphClass == GlyphClassKind.Mark)
            {
                return true;
            }
            if (glyphClass != GlyphClassKind.Zero)
            {
                return false;
            }
            UnicodeCategory category = Rune.GetUnicodeCategory(new Rune((int)_glyphs[index].CodePoint));
            return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
        }

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
            ArabicAction = None;
            LigatureComponent = byte.MaxValue;
            AttachmentTarget = -1;
        }

        public ushort GlyphIndex;
        public int Cluster;
        public uint CodePoint;
        public short AdvanceX;
        public short AdvanceY;
        public short OffsetX;
        public short OffsetY;
        public byte ArabicAction;
        public byte SpaceFallback;
        public byte FractionAction;
        public byte ScriptFeatureMask;
        public byte LigatureComponentCount;
        public byte LigatureComponent;
        public int AttachmentTarget;
    }

    private readonly record struct ArabicStateEntry(byte PreviousAction, byte CurrentAction, byte NextState);

    private const byte Isolated = 0;
    private const byte Final = 1;
    private const byte Final2 = 2;
    private const byte Final3 = 3;
    private const byte Medial = 4;
    private const byte Medial2 = 5;
    private const byte Initial = 6;
    private const byte None = 7;

    private const byte SpaceNone = 0;
    private const byte SpaceEm = 1;
    private const byte SpaceEm2 = 2;
    private const byte SpaceEm3 = 3;
    private const byte SpaceEm4 = 4;
    private const byte SpaceEm5 = 5;
    private const byte SpaceEm6 = 6;
    private const byte SpaceEm16 = 16;
    private const byte SpaceFourEm18 = 17;
    private const byte Space = 18;
    private const byte SpaceFigure = 19;
    private const byte SpacePunctuation = 20;
    private const byte SpaceNarrow = 21;

    private const byte FractionNone = 0;
    private const byte FractionNumerator = 1;
    private const byte FractionDenominator = 2;
    private const byte FractionSlash = 3;

    private const byte KhmerPrefMask = 1 << 0;
    private const byte KhmerPostBaseMask = 1 << 1;
    private const byte KhmerCfarMask = 1 << 2;
}
