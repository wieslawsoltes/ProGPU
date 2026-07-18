using OpenFontSharp;
using OpenFontSharp.Tables.AdvancedLayout;
using ProGPU.Text.Shaping;
using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
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

public sealed class TextShapingOptions : IEquatable<TextShapingOptions>
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
        new("rclt"),
        new("rand", ushort.MaxValue)
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

    internal ShapingClusterLevel ClusterLevel { get; init; } = ShapingClusterLevel.MonotoneGraphemes;
    internal ShapingBufferFlags BufferFlags { get; init; }
    internal ReadOnlyMemory<ShapingFeature> RangedFeatures { get; init; }
    internal IReadOnlyList<OpenTypeFeatureSetting>? BaseFeatures { get; init; }

    internal int GetFeatureValue(string tag, int inputIndex)
    {
        IReadOnlyList<OpenTypeFeatureSetting> baseline = BaseFeatures ?? Features;
        int value = 0;
        bool foundBaseline = false;
        for (var index = baseline.Count - 1; index >= 0; index--)
        {
            if (baseline[index].Tag != tag) continue;
            value = baseline[index].Value;
            foundBaseline = true;
            break;
        }
        ReadOnlySpan<ShapingFeature> ranges = RangedFeatures.Span;
        if (!foundBaseline)
        {
            bool hasRange = false;
            for (var index = 0; index < ranges.Length; index++)
                hasRange |= ranges[index].Tag.ToString() == tag;
            if (!hasRange)
            {
                for (var index = Features.Count - 1; index >= 0; index--)
                {
                    if (Features[index].Tag != tag) continue;
                    value = Features[index].Value;
                    break;
                }
            }
        }
        uint position = checked((uint)Math.Max(inputIndex, 0));
        for (var index = 0; index < ranges.Length; index++)
        {
            ShapingFeature feature = ranges[index];
            if (feature.Tag.ToString() == tag && feature.AppliesTo(position))
                value = checked((int)Math.Min(feature.Value, int.MaxValue));
        }
        return value;
    }

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

    internal bool IsFeatureExplicitAt(string tag, int inputIndex)
    {
        if (ExplicitFeatureTags?.Contains(tag) != true)
        {
            return false;
        }

        bool hasRangedSetting = false;
        uint position = checked((uint)Math.Max(inputIndex, 0));
        ReadOnlySpan<ShapingFeature> ranges = RangedFeatures.Span;
        for (var index = 0; index < ranges.Length; index++)
        {
            ShapingFeature feature = ranges[index];
            if (feature.Tag.ToString() != tag)
            {
                continue;
            }

            hasRangedSetting = true;
            if (feature.AppliesTo(position))
            {
                return true;
            }
        }

        // Public TextShapingOptions.WithFeatures calls have no ranged request memory;
        // their explicit tags apply to the complete run.
        return !hasRangedSetting;
    }

    public bool Equals(TextShapingOptions? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null ||
            !string.Equals(Script, other.Script, StringComparison.Ordinal) ||
            !string.Equals(Language, other.Language, StringComparison.Ordinal) ||
            Direction != other.Direction ||
            ClusterLevel != other.ClusterLevel ||
            BufferFlags != other.BufferFlags ||
            !FeatureListsEqual(Features, other.Features) ||
            !FeatureListsEqual(BaseFeatures, other.BaseFeatures) ||
            !FeatureSetsEqual(ExplicitFeatureTags, other.ExplicitFeatureTags))
        {
            return false;
        }

        ReadOnlySpan<ShapingFeature> leftRanges = RangedFeatures.Span;
        ReadOnlySpan<ShapingFeature> rightRanges = other.RangedFeatures.Span;
        return leftRanges.SequenceEqual(rightRanges);
    }

    public override bool Equals(object? obj) => obj is TextShapingOptions other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Script, StringComparer.Ordinal);
        hash.Add(Language, StringComparer.Ordinal);
        hash.Add(Direction);
        hash.Add(ClusterLevel);
        hash.Add(BufferFlags);
        AddFeatures(ref hash, Features);
        AddFeatures(ref hash, BaseFeatures);
        AddFeatureSet(ref hash, ExplicitFeatureTags);
        ReadOnlySpan<ShapingFeature> ranges = RangedFeatures.Span;
        hash.Add(ranges.Length);
        for (var index = 0; index < ranges.Length; index++)
        {
            hash.Add(ranges[index]);
        }
        return hash.ToHashCode();
    }

    private static bool FeatureListsEqual(
        IReadOnlyList<OpenTypeFeatureSetting>? left,
        IReadOnlyList<OpenTypeFeatureSetting>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index]) return false;
        }
        return true;
    }

    private static bool FeatureSetsEqual(IReadOnlySet<string>? left, IReadOnlySet<string>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        return left is not null && right is not null && left.Count == right.Count && left.SetEquals(right);
    }

    private static void AddFeatures(ref HashCode hash, IReadOnlyList<OpenTypeFeatureSetting>? features)
    {
        if (features is null)
        {
            hash.Add(-1);
            return;
        }

        hash.Add(features.Count);
        for (var index = 0; index < features.Count; index++)
        {
            hash.Add(features[index]);
        }
    }

    private static void AddFeatureSet(ref HashCode hash, IReadOnlySet<string>? features)
    {
        if (features is null)
        {
            hash.Add(-1);
            return;
        }

        // Addition is order-independent, so equal sets produce equal hashes without
        // sorting or allocating in the compositor cache lookup path.
        var setHash = 0;
        foreach (string feature in features)
        {
            setHash = unchecked(setHash + StringComparer.Ordinal.GetHashCode(feature));
        }
        hash.Add(features.Count);
        hash.Add(setHash);
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
    private const int MaximumLookupNestingDepth = 64;
    private const int ShapingPlanCacheLimit = 64;

    private static readonly ConditionalWeakTable<TtfFont, FontShapingPlanCache> s_shapingPlanCaches = new();

    private readonly record struct ShapingPlanKey(
        string UnicodeScript,
        TextShapingOptions Options);

    private sealed class FontShapingPlanCache
    {
        private readonly object _lock = new();
        private readonly Dictionary<ShapingPlanKey, ShapingPlan> _plans = new();
        private readonly Queue<ShapingPlanKey> _order = new();

        public ShapingPlan GetOrCreate(
            TtfFont font,
            string unicodeScript,
            TextShapingOptions options)
        {
            var key = new ShapingPlanKey(unicodeScript, options);
            lock (_lock)
            {
                if (_plans.TryGetValue(key, out ShapingPlan cached))
                {
                    return cached;
                }

                ShapingPlan created = CreateShapingPlan(font, unicodeScript, options);
                _plans.Add(key, created);
                _order.Enqueue(key);
                while (_order.Count > ShapingPlanCacheLimit)
                {
                    ShapingPlanKey oldest = _order.Dequeue();
                    _plans.Remove(oldest);
                }
                return created;
            }
        }
    }

    internal static bool IsDefaultIgnorableCodePoint(uint codePoint) =>
        GlyphSubstitutionBuffer.IsDefaultIgnorable(codePoint);

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

        ShapingResult shaping = ShapeCore(text, font, options);
        GlyphPositionBuffer positions = shaping.Positions;
        ShapingDirection direction = shaping.Direction;
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

    internal static void ShapeDesignUnits(
        string text,
        TtfFont font,
        TextShapingOptions options,
        ShapingBuffer destination)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        if (text.Length == 0) return;

        ShapingResult shaping = ShapeCore(text, font, options);
        GlyphPositionBuffer positions = shaping.Positions;
        destination.EnsureCapacity(positions.Count);
        for (var index = 0; index < positions.Count; index++)
        {
            GlyphRecord record = positions[index];
            destination.Append(new ShapingGlyph
            {
                GlyphId = record.GlyphIndex,
                CodePoint = record.CodePoint,
                Cluster = record.Cluster,
                AdvanceX = record.AdvanceX,
                AdvanceY = -record.AdvanceY,
                OffsetX = record.OffsetX,
                OffsetY = -record.OffsetY
            });
        }
    }

    private static ShapingResult ShapeCore(
        string text,
        TtfFont font,
        TextShapingOptions options)
    {
        string unicodeScript = options.Script ?? InferScript(text);
        ShapingPlan shapingPlan = s_shapingPlanCaches
            .GetValue(font, static _ => new FontShapingPlanCache())
            .GetOrCreate(font, unicodeScript, options);
        string script = shapingPlan.Script;
        bool useShaper = shapingPlan.UseShaper;
        bool indicShaper = shapingPlan.IndicShaper;
        bool khmerShaper = shapingPlan.KhmerShaper;
        bool myanmarShaper = shapingPlan.MyanmarShaper;
        bool arabicShaper = shapingPlan.ArabicShaper;
        ShapingDirection direction = shapingPlan.Direction;
        options = shapingPlan.Options;
        SubstitutionPlan substitutionPlan = shapingPlan.Substitution;

        var substitutions = GlyphSubstitutionBuffer.Create(
            text,
            font,
            unicodeScript,
            options.ClusterLevel,
            options.BufferFlags);
        substitutions.ApplyDirectionalCodePointFallback(
            font,
            direction,
            substitutionPlan.Lookups.Any(static lookup => lookup.Tag is "vert" or "vrt2") ||
            substitutionPlan.RawLookups.Any(static lookup => lookup.Tag is "vert" or "vrt2"));
        substitutions.PrepareHangulShaping(unicodeScript == "hang", font);
        substitutions.ReorderModifiedCombiningMarks(unicodeScript);
        substitutions.ComposeHebrewPresentationForms(
            unicodeScript == "hebr" && !shapingPlan.HasMarkFeature);
        substitutions.ApplyVowelConstraints(unicodeScript);
        substitutions.NormalizeUseDiacritics(useShaper);
        substitutions.PrepareThaiLao(unicodeScript);
        substitutions.AssignFractionActions();
        substitutions.PrepareKhmerShaping(script);
        substitutions.PrepareIndicShaping(indicShaper);
        substitutions.PrepareMyanmarShaping(myanmarShaper);
        substitutions.PrepareUseShaping(useShaper, unicodeScript);
        substitutions.AssignArabicJoiningActions(unicodeScript);
        if (useShaper || indicShaper || khmerShaper || arabicShaper)
        {
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.Directional);
        }
        if (useShaper)
        {
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.Preprocessing);
            substitutions.ClearSubstitutionFlags();
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.Repha);
            substitutions.RecordUseRepha();
            substitutions.ClearSubstitutionFlags();
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.Prebase);
            substitutions.RecordUsePrebase();
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.Basic);
            substitutions.ReorderUseShaping(true);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.Topographical);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.Presentation);
        }
        else if (indicShaper)
        {
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.IndicPreprocessing);
            substitutions.InitialReorderIndic(unicodeScript, script, substitutionPlan);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.IndicNukta);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.IndicAkhand);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.Repha);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.IndicRakar);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.Prebase);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.IndicBelow);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.IndicAbove);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.IndicHalf);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.IndicPost);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.IndicVattu);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.IndicConjunct);
            substitutions.FinalReorderIndic(unicodeScript);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.IndicPresentation);
        }
        else if (khmerShaper)
        {
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.KhmerBasic);
            substitutions.ClearSyllables();
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.KhmerPresentation);
        }
        else if (myanmarShaper)
        {
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.MyanmarPreprocessing);
            substitutions.ReorderMyanmar();
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.MyanmarRepha);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.MyanmarPrebase);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.MyanmarBelow);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.MyanmarPostbase);
            substitutions.ClearSyllables();
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.MyanmarPresentation);
        }
        else if (arabicShaper)
        {
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.ArabicStretch);
            substitutions.RecordArabicStretch();
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.ArabicPreprocessing);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.ArabicIsolated);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.ArabicFinal);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.ArabicFinal2);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.ArabicFinal3);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.ArabicMedial);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.ArabicMedial2);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.ArabicInitial);
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.ArabicRequiredLigatures);
            substitutions.ApplyArabicFallback(
                unicodeScript == "arab" && NeedsArabicFallback(substitutionPlan),
                options);
            bool reverseArabicContext = direction == ShapingDirection.LeftToRight &&
                !NeedsArabicFallback(substitutionPlan);
            if (reverseArabicContext) substitutions.Reverse();
            bool hasRequiredContextualAlternates = substitutionPlan.RawLookups.Any(static lookup => lookup.Tag == "rclt") ||
                substitutionPlan.Lookups.Any(static lookup => lookup.Tag == "rclt");
            if (hasRequiredContextualAlternates)
            {
                ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.ArabicPostRequired);
            }
            else
            {
                ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.ArabicContextual);
                ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.ArabicPostContextual);
            }
            if (reverseArabicContext) substitutions.Reverse();
        }
        else
        {
            ApplySubstitutions(font, substitutionPlan, substitutions, options, UseSubstitutionStage.All);
        }

        var positions = new GlyphPositionBuffer(
            substitutions,
            font,
            direction,
            zeroMarkAdvancesEarly: useShaper || myanmarShaper,
            clusterLevel: options.ClusterLevel,
            bufferFlags: options.BufferFlags);
        bool hasGpos = shapingPlan.Positioning.HasTable;
        bool hasGposKerning = ApplyPositions(font, shapingPlan.Positioning, positions, options);
        bool kernEnabled = IsFeatureEnabled(options.Features, "kern");
        if (!hasGposKerning && kernEnabled && !indicShaper &&
            direction is ShapingDirection.LeftToRight or ShapingDirection.RightToLeft)
        {
            positions.ApplyLegacyKern(font);
        }
        bool usesFallbackMarkPositioning = UsesFallbackMarkPositioning(
            unicodeScript, useShaper, indicShaper, khmerShaper);
        bool zeroMarkAdvancesLate = usesFallbackMarkPositioning || unicodeScript is "thai" or "lao ";
        if (zeroMarkAdvancesLate)
        {
            positions.ZeroMarkAdvancesLate(
                adjustOffsets: !hasGpos && direction is ShapingDirection.LeftToRight or ShapingDirection.TopToBottom);
        }
        positions.ResolveAttachmentOffsets();
        if (!hasGpos && usesFallbackMarkPositioning)
        {
            positions.ApplyFallbackMarkPositioning(font);
        }

        if (direction is ShapingDirection.RightToLeft or ShapingDirection.BottomToTop)
        {
            positions.Reverse();
            if (options.ClusterLevel == ShapingClusterLevel.MonotoneCharacters)
                positions.ReverseClustersWithinCombiningRuns();
        }
        if (arabicShaper)
        {
            positions.ApplyArabicStretch(font, direction == ShapingDirection.RightToLeft);
        }

        return new ShapingResult(positions, direction);
    }

    private static ShapingPlan CreateShapingPlan(
        TtfFont font,
        string unicodeScript,
        TextShapingOptions requestedOptions)
    {
        bool useShaper = ResolveLayoutScript(font, unicodeScript, out string script);
        bool indicShaper = !useShaper && IsIndicShaperScript(unicodeScript);
        bool khmerShaper = script == "khmr";
        bool myanmarShaper = unicodeScript == "mymr";
        bool arabicShaper = UsesArabicJoiningScript(unicodeScript);
        ShapingDirection direction = ResolveDirection(requestedOptions.Direction, unicodeScript);
        TextShapingOptions options = AddScriptFeatures(requestedOptions, script, useShaper, indicShaper);
        options = AddDirectionalFeatures(options, direction);
        Typeface? typeface = font.LayoutTypeface;
        SubstitutionPlan substitution = CreateSubstitutionPlan(font, typeface?.GSUBTable, options, script);
        PositioningPlan positioning = CreatePositioningPlan(font, typeface?.GPOSTable, options, script);
        bool hasMarkFeature = unicodeScript == "hebr" &&
            GetFeatureTags(font).Contains("mark", StringComparer.Ordinal);
        return new ShapingPlan(
            script,
            useShaper,
            indicShaper,
            khmerShaper,
            myanmarShaper,
            arabicShaper,
            direction,
            options,
            substitution,
            positioning,
            hasMarkFeature);
    }

    private readonly record struct ShapingResult(
        GlyphPositionBuffer Positions,
        ShapingDirection Direction);

    private static ShapingDirection ResolveDirection(ShapingDirection requested, string script)
    {
        if (requested != ShapingDirection.Unspecified) return requested;
        return script is "arab" or "hebr" or "syrc" or "thaa" or "nkoo" or "adlm" or "rohg"
            ? ShapingDirection.RightToLeft
            : ShapingDirection.LeftToRight;
    }

    private static bool UsesArabicJoiningScript(string script) => script is
        "adlm" or "arab" or "chrs" or "rohg" or "mand" or "mani" or "mong" or
        "nkoo" or "ougr" or "phag" or "phlp" or "sogd" or "syrc";

    private static bool UsesFallbackMarkPositioning(
        string script,
        bool useShaper,
        bool indicShaper,
        bool khmerShaper) =>
        !useShaper && !indicShaper && !khmerShaper && script is not ("thai" or "lao " or "mymr" or "qaag");

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
            ExplicitFeatureTags = explicitFeatureTags,
            ClusterLevel = options.ClusterLevel,
            BufferFlags = options.BufferFlags,
            RangedFeatures = options.RangedFeatures,
            BaseFeatures = options.BaseFeatures
        };
    }

    private static TextShapingOptions AddScriptFeatures(
        TextShapingOptions options,
        string script,
        bool useShaper,
        bool indicShaper)
    {
        bool arabicShaper = UsesArabicJoiningScript(script);
        if (script is not ("khmr" or "hang" or "mymr" or "mym2") && !useShaper && !indicShaper && !arabicShaper)
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

        string[] scriptFeatures = script == "khmr"
            ? ["pref", "blwf", "abvf", "pstf", "cfar", "pres", "abvs", "blws", "psts"]
            : script is "mymr" or "mym2"
            ? ["rphf", "pref", "blwf", "pstf", "pres", "abvs", "blws", "psts"]
            : script == "hang"
            ? ["ljmo", "vjmo", "tjmo"]
            : indicShaper
            ? ["nukt", "akhn", "rphf", "rkrf", "pref", "blwf", "abvf", "half", "pstf", "vatu", "cjct",
               "init", "pres", "abvs", "blws", "psts", "haln"]
            : ["nukt", "akhn", "rphf", "pref", "rkrf", "abvf", "blwf", "half", "pstf", "vatu", "cjct",
               "isol", "init", "medi", "fina", "abvs", "blws", "haln", "pres", "psts"];
        foreach (string tag in scriptFeatures)
        {
            values.TryAdd(tag, 1);
        }
        if (script == "khmr")
        {
            values.TryAdd("clig", 1);
            if (!explicitFeatureTags.Contains("liga"))
            {
                values["liga"] = 0;
            }
        }
        else if (indicShaper)
        {
            values["liga"] = 0;
        }
        else if (arabicShaper)
        {
            values.TryAdd("stch", 1);
            values.TryAdd("mset", 1);
        }
        else if (script == "hang")
        {
            values.TryAdd("ljmo", 1);
            values.TryAdd("vjmo", 1);
            values.TryAdd("tjmo", 1);
        }

        string[] orderedTags = script == "khmr"
            ? ["rvrn", "frac", "numr", "dnom", "locl", "ccmp",
               "pref", "blwf", "abvf", "pstf", "cfar", "pres", "abvs", "blws", "psts"]
            : arabicShaper
            ? ["rvrn", "frac", "numr", "dnom", "stch", "ccmp", "locl", "isol", "fina", "fin2", "fin3",
               "medi", "med2", "init", "rlig", "calt", "rclt", "liga", "clig", "mset"]
            : ["rvrn", "frac", "numr", "dnom", "locl", "ccmp", "nukt", "akhn", "rphf", "pref",
               "rkrf", "abvf", "blwf", "half", "pstf", "vatu", "cjct", "isol", "init", "medi", "fina",
               "abvs", "blws", "haln", "pres", "psts", "ljmo", "vjmo", "tjmo"];
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
            ExplicitFeatureTags = explicitFeatureTags,
            ClusterLevel = options.ClusterLevel,
            BufferFlags = options.BufferFlags,
            RangedFeatures = options.RangedFeatures,
            BaseFeatures = options.BaseFeatures
        };
    }

    private static SubstitutionPlan CreateSubstitutionPlan(
        TtfFont font,
        GSUB? table,
        TextShapingOptions options,
        string script)
    {
        ReadOnlyMemory<byte> rawTable = default;
        bool hasRawTable = font.TryGetTable("GSUB", out rawTable);
        EnabledLookup[] rawLookups = hasRawTable
            ? GetRawEnabledLookupIndices(font, rawTable.Span, options.Features, script, options.Language, out _).ToArray()
            : [];
        EnabledLookup[] lookups = table?.FeatureList?.featureTables is { Length: > 0 } features
            ? GetEnabledLookupIndices(table, features, options, script).ToArray()
            : [];
        return new SubstitutionPlan(table, rawTable, lookups, rawLookups, hasRawTable);
    }

    private static void ApplySubstitutions(
        TtfFont font,
        SubstitutionPlan plan,
        GlyphSubstitutionBuffer glyphs,
        TextShapingOptions options,
        UseSubstitutionStage stage)
    {
        GSUB? table = plan.Table;
        ReadOnlyMemory<byte> rawTable = plan.RawTable;
        EnabledLookup[] rawLookups = plan.RawLookups;

        if (table is not null && !plan.RawOnly)
        {
            foreach (EnabledLookup enabled in plan.Lookups)
            {
                if (!IsSubstitutionStageFeature(enabled.Tag, stage)) continue;
                glyphs.SetManualJoiners(IsManualJoinerStage(stage));
                ushort lookupIndex = enabled.LookupIndex;
                if (lookupIndex >= table.LookupList.Count)
                {
                    continue;
                }

                GSUB.LookupTable lookup = table.LookupList[lookupIndex];
                bool rawOwnedLookup = IsRawOwnedLookup(rawTable.Span, lookupIndex);
                bool restrictToSyllable = IsUsePerSyllableFeature(enabled.Tag, stage);
                if (IsRawReverseLookup(rawTable.Span, lookupIndex))
                {
                    ApplyReverseChainingLookup(
                        rawTable.Span, glyphs, lookupIndex, restrictToSyllable,
                        enabled.Tag, enabled.Required, options);
                    continue;
                }
                for (var position = 0; position < glyphs.Count; position++)
                {
                    glyphs.SetLookupSyllable(position, restrictToSyllable);
                    if (!enabled.Required && !glyphs.IsFeatureEnabled(position, enabled.Tag, options))
                    {
                        continue;
                    }
                    if (rawOwnedLookup)
                    {
                        glyphs.ResetContextMatchEnd();
                        int rawCountBefore = glyphs.Count;
                        ApplyNestedLookup(rawTable.Span, glyphs, lookupIndex, position);
                        int rawInsertedGlyphs = glyphs.Count - rawCountBefore;
                        if (rawInsertedGlyphs > 0) position += rawInsertedGlyphs;
                        if (glyphs.ContextMatchEnd > position + 1)
                            position = glyphs.ContextMatchEnd - 1;
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
                glyphs.ClearLookupSyllable();
                glyphs.SetManualJoiners(false);

                ApplyAlternateLookup(
                    font, glyphs, lookupIndex, enabled.Value, enabled.Tag, enabled.Required, options);
            }
        }

        if (table is null || plan.RawOnly)
        {
            foreach (EnabledLookup enabled in rawLookups)
            {
                if (!IsSubstitutionStageFeature(enabled.Tag, stage)) continue;
                glyphs.SetManualJoiners(IsManualJoinerStage(stage));
                bool restrictToSyllable = IsUsePerSyllableFeature(enabled.Tag, stage);
                bool alternateLookup = IsRawAlternateLookup(rawTable.Span, enabled.LookupIndex);
                if (IsRawReverseLookup(rawTable.Span, enabled.LookupIndex))
                {
                    ApplyReverseChainingLookup(
                        rawTable.Span, glyphs, enabled.LookupIndex, restrictToSyllable,
                        enabled.Tag, enabled.Required, options);
                    continue;
                }
                if (!alternateLookup)
                {
                    for (var position = 0; position < glyphs.Count; position++)
                    {
                        glyphs.SetLookupSyllable(position, restrictToSyllable);
                        if (enabled.Required || glyphs.IsFeatureEnabled(position, enabled.Tag, options))
                        {
                            glyphs.ResetContextMatchEnd();
                            int countBefore = glyphs.Count;
                            ApplyNestedLookup(rawTable.Span, glyphs, enabled.LookupIndex, position);
                            int insertedGlyphs = glyphs.Count - countBefore;
                            if (insertedGlyphs > 0) position += insertedGlyphs;
                            if (glyphs.ContextMatchEnd > position + 1)
                                position = glyphs.ContextMatchEnd - 1;
                        }
                    }
                }
                glyphs.ClearLookupSyllable();
                glyphs.SetManualJoiners(false);
                ApplyAlternateLookup(
                    font, glyphs, enabled.LookupIndex, enabled.Value, enabled.Tag, enabled.Required, options);
            }
        }

        glyphs.SetManualJoiners(false);
    }

    private static bool NeedsArabicFallback(SubstitutionPlan plan)
    {
        static bool IsArabicFormFeature(EnabledLookup lookup) => lookup.Tag is
            "isol" or "fina" or "fin2" or "fin3" or "medi" or "med2" or "init";
        return !plan.RawLookups.Any(IsArabicFormFeature) && !plan.Lookups.Any(IsArabicFormFeature);
    }

    private static bool IsRawAlternateLookup(ReadOnlySpan<byte> data, ushort lookupIndex)
    {
        if (!TryGetLookup(data, lookupIndex, out int lookupOffset, out ushort lookupType, out int subtableCountOffset))
            return false;
        if (lookupType == 3) return true;
        if (lookupType != 7 || ReadU16(data, subtableCountOffset) == 0 ||
            !CanRead(data, subtableCountOffset + 2, 2)) return false;
        int extension = lookupOffset + ReadU16(data, subtableCountOffset + 2);
        return CanRead(data, extension, 4) && ReadU16(data, extension) == 1 &&
               ReadU16(data, extension + 2) == 3;
    }

    private static bool IsRawReverseLookup(ReadOnlySpan<byte> data, ushort lookupIndex)
    {
        if (!TryGetLookup(data, lookupIndex, out int lookupOffset, out ushort lookupType, out int subtableCountOffset))
            return false;
        if (lookupType == 8) return true;
        if (lookupType != 7 || ReadU16(data, subtableCountOffset) == 0 ||
            !CanRead(data, subtableCountOffset + 2, 2)) return false;
        int extension = lookupOffset + ReadU16(data, subtableCountOffset + 2);
        return CanRead(data, extension, 4) && ReadU16(data, extension) == 1 &&
               ReadU16(data, extension + 2) == 8;
    }

    private static bool IsUsePerSyllableFeature(string tag, UseSubstitutionStage stage) =>
        stage is not (UseSubstitutionStage.All or UseSubstitutionStage.KhmerPresentation or UseSubstitutionStage.MyanmarPresentation) &&
        (tag is "locl" or "ccmp" or "nukt" or "akhn" or "rphf" or "pref" or
            "rkrf" or "abvf" or "blwf" or "half" or "pstf" or "vatu" or "cjct" ||
         stage == UseSubstitutionStage.IndicPresentation && tag is "init" or "pres" or "abvs" or "blws" or "psts" or "haln");

    private static bool IsManualJoinerStage(UseSubstitutionStage stage) => stage is
        UseSubstitutionStage.IndicNukta or UseSubstitutionStage.IndicAkhand or UseSubstitutionStage.Repha or
        UseSubstitutionStage.IndicRakar or UseSubstitutionStage.Prebase or UseSubstitutionStage.IndicBelow or
        UseSubstitutionStage.IndicAbove or UseSubstitutionStage.IndicHalf or UseSubstitutionStage.IndicPost or
        UseSubstitutionStage.IndicVattu or UseSubstitutionStage.IndicConjunct or UseSubstitutionStage.IndicPresentation or
        UseSubstitutionStage.KhmerBasic or UseSubstitutionStage.KhmerPresentation or
        UseSubstitutionStage.MyanmarRepha or UseSubstitutionStage.MyanmarPrebase or UseSubstitutionStage.MyanmarBelow or
        UseSubstitutionStage.MyanmarPostbase or UseSubstitutionStage.MyanmarPresentation;

    private static bool IsSubstitutionStageFeature(string tag, UseSubstitutionStage stage)
    {
        if (stage == UseSubstitutionStage.All) return true;
        return stage switch
        {
            UseSubstitutionStage.Directional => tag is "ltra" or "ltrm" or "rtla" or "rtlm",
            UseSubstitutionStage.Preprocessing => tag is
                "rvrn" or "frac" or "numr" or "dnom" or "locl" or "ccmp" or "nukt" or "akhn",
            UseSubstitutionStage.Repha => tag == "rphf",
            UseSubstitutionStage.Prebase => tag == "pref",
            UseSubstitutionStage.Basic => tag is
                "rkrf" or "abvf" or "blwf" or "half" or "pstf" or "vatu" or "cjct",
            UseSubstitutionStage.Topographical => tag is "isol" or "init" or "medi" or "fina",
            UseSubstitutionStage.Presentation => tag is not
                ("ltra" or "ltrm" or "rtla" or "rtlm" or "rvrn" or "frac" or "numr" or "dnom" or "locl" or "ccmp" or "nukt" or "akhn" or
                 "rphf" or "pref" or "rkrf" or "abvf" or "blwf" or "half" or "pstf" or "vatu" or
                 "cjct" or "isol" or "init" or "medi" or "fina"),
            UseSubstitutionStage.IndicPreprocessing => tag is
                "rvrn" or "frac" or "numr" or "dnom" or "locl" or "ccmp",
            UseSubstitutionStage.IndicNukta => tag == "nukt",
            UseSubstitutionStage.IndicAkhand => tag == "akhn",
            UseSubstitutionStage.IndicRakar => tag == "rkrf",
            UseSubstitutionStage.IndicBelow => tag == "blwf",
            UseSubstitutionStage.IndicAbove => tag == "abvf",
            UseSubstitutionStage.IndicHalf => tag == "half",
            UseSubstitutionStage.IndicPost => tag == "pstf",
            UseSubstitutionStage.IndicVattu => tag == "vatu",
            UseSubstitutionStage.IndicConjunct => tag == "cjct",
            UseSubstitutionStage.IndicPresentation => tag is not
                ("ltra" or "ltrm" or "rtla" or "rtlm" or "rvrn" or "frac" or "numr" or "dnom" or "locl" or "ccmp" or "nukt" or "akhn" or
                 "rphf" or "rkrf" or "pref" or "blwf" or "abvf" or "half" or "pstf" or "vatu" or "cjct"),
            UseSubstitutionStage.KhmerBasic => tag is
                "rvrn" or "frac" or "numr" or "dnom" or "locl" or "ccmp" or
                "pref" or "blwf" or "abvf" or "pstf" or "cfar",
            UseSubstitutionStage.KhmerPresentation => tag is not
                ("ltra" or "ltrm" or "rtla" or "rtlm" or "rvrn" or "frac" or "numr" or "dnom" or "locl" or "ccmp" or
                 "pref" or "blwf" or "abvf" or "pstf" or "cfar"),
            UseSubstitutionStage.MyanmarPreprocessing => tag is
                "rvrn" or "frac" or "numr" or "dnom" or "locl" or "ccmp",
            UseSubstitutionStage.MyanmarRepha => tag == "rphf",
            UseSubstitutionStage.MyanmarPrebase => tag == "pref",
            UseSubstitutionStage.MyanmarBelow => tag == "blwf",
            UseSubstitutionStage.MyanmarPostbase => tag == "pstf",
            UseSubstitutionStage.MyanmarPresentation => tag is not
                ("ltra" or "ltrm" or "rtla" or "rtlm" or "rvrn" or "frac" or "numr" or "dnom" or "locl" or "ccmp" or
                 "rphf" or "pref" or "blwf" or "pstf"),
            UseSubstitutionStage.ArabicStretch => tag == "stch",
            UseSubstitutionStage.ArabicPreprocessing => tag is
                "rvrn" or "frac" or "numr" or "dnom" or "ccmp" or "locl",
            UseSubstitutionStage.ArabicIsolated => tag == "isol",
            UseSubstitutionStage.ArabicFinal => tag == "fina",
            UseSubstitutionStage.ArabicFinal2 => tag == "fin2",
            UseSubstitutionStage.ArabicFinal3 => tag == "fin3",
            UseSubstitutionStage.ArabicMedial => tag == "medi",
            UseSubstitutionStage.ArabicMedial2 => tag == "med2",
            UseSubstitutionStage.ArabicInitial => tag == "init",
            UseSubstitutionStage.ArabicRequiredLigatures => tag == "rlig",
            UseSubstitutionStage.ArabicContextual => tag is "calt" or "rclt",
            UseSubstitutionStage.ArabicPostRequired => tag is not
                ("ltra" or "ltrm" or "rtla" or "rtlm" or "rvrn" or "frac" or "numr" or "dnom" or "stch" or "ccmp" or "locl" or
                 "isol" or "fina" or "fin2" or "fin3" or "medi" or "med2" or "init" or "rlig"),
            UseSubstitutionStage.ArabicPostContextual => tag is not
                ("ltra" or "ltrm" or "rtla" or "rtlm" or "rvrn" or "frac" or "numr" or "dnom" or "stch" or "ccmp" or "locl" or
                 "isol" or "fina" or "fin2" or "fin3" or "medi" or "med2" or "init" or "rlig" or "calt" or "rclt"),
            UseSubstitutionStage.ArabicLigatures => tag is "liga" or "clig" or "mset",
            UseSubstitutionStage.ArabicPresentation => tag is not
                ("ltra" or "ltrm" or "rtla" or "rtlm" or "rvrn" or "frac" or "numr" or "dnom" or "stch" or "ccmp" or "locl" or
                 "isol" or "fina" or "fin2" or "fin3" or "medi" or "med2" or "init" or
                 "rlig" or "calt" or "rclt" or "liga" or "clig" or "mset"),
            _ => false
        };
    }

    private static bool IsRawOwnedLookup(ReadOnlySpan<byte> data, ushort lookupIndex)
    {
        if (!TryGetLookup(data, lookupIndex, out int lookupOffset, out ushort lookupType, out int subtableCountOffset))
        {
            return false;
        }
        if (lookupType is 1 or 2 or 4 or 5 or 6) return true;
        if (lookupType != 7 || ReadU16(data, subtableCountOffset) == 0 || !CanRead(data, subtableCountOffset + 2, 2))
        {
            return false;
        }
        int extension = lookupOffset + ReadU16(data, subtableCountOffset + 2);
        return CanRead(data, extension, 4) && ReadU16(data, extension) == 1 &&
            ReadU16(data, extension + 2) is 1 or 2 or 4 or 5 or 6;
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
                5 => ApplyContextSubtable(data, glyphs, subtableOffset, position, ReadU16(data, lookupOffset + 2)),
                6 => ApplyChainContextSubtable(data, glyphs, subtableOffset, position, ReadU16(data, lookupOffset + 2)),
                _ => false
            };
            if (applied) return true;
        }
        return false;
    }

    private static List<EnabledLookup> GetRawEnabledLookupIndices(
        TtfFont font,
        ReadOnlySpan<byte> data,
        IReadOnlyList<OpenTypeFeatureSetting> settings,
        string script,
        string? language,
        out bool usesFeatureVariations)
    {
        Dictionary<ushort, int>? substitutions = GetFeatureVariationSubstitutions(font, data);
        usesFeatureVariations = substitutions is not null;
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
        HashSet<ushort>? allowedFeatures = GetRawLanguageFeatureIndices(
            data, script, language, out ushort requiredFeatureIndex);
        if (requiredFeatureIndex < featureCount)
        {
            AddRawFeatureLookups(
                data,
                featureListOffset,
                requiredFeatureIndex,
                1,
                result,
                positions,
                substitutions,
                required: true);
        }
        foreach (OpenTypeFeatureSetting setting in settings)
        {
            if (!requested.TryGetValue(setting.Tag, out int value) || value == 0 || value != setting.Value)
            {
                continue;
            }
            for (var featureIndex = 0; featureIndex < featureCount; featureIndex++)
            {
                if (allowedFeatures is not null && !allowedFeatures.Contains((ushort)featureIndex) &&
                    !IsGlobalShaperFeature(script, setting.Tag))
                    continue;
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
                AddRawFeatureLookups(data, featureListOffset, (ushort)featureIndex, value, result, positions, substitutions);
            }
        }
        result.Sort(static (left, right) => left.LookupIndex.CompareTo(right.LookupIndex));
        return result;
    }

    private static void AddRawFeatureLookups(
        ReadOnlySpan<byte> data,
        int featureListOffset,
        ushort featureIndex,
        int value,
        List<EnabledLookup> result,
        Dictionary<ushort, int> positions,
        Dictionary<ushort, int>? substitutions,
        bool required = false)
    {
        int recordOffset = featureListOffset + 2 + featureIndex * 6;
        if (!CanRead(data, recordOffset, 6)) return;
        string tag = Encoding.ASCII.GetString(data.Slice(recordOffset, 4));
        int featureOffset = substitutions is not null && substitutions.TryGetValue(featureIndex, out int alternateOffset)
            ? alternateOffset
            : featureListOffset + ReadU16(data, recordOffset + 4);
        if (!CanRead(data, featureOffset, 4)) return;
        ushort lookupCount = ReadU16(data, featureOffset + 2);
        for (var lookup = 0; lookup < lookupCount; lookup++)
        {
            int lookupOffset = featureOffset + 4 + lookup * 2;
            if (!CanRead(data, lookupOffset, 2)) break;
            ushort lookupIndex = ReadU16(data, lookupOffset);
            if (positions.TryGetValue(lookupIndex, out int existing))
            {
                EnabledLookup current = result[existing];
                if (current.Required && !required) continue;
                if (!IsGlobalFeatureTag(current.Tag) || IsGlobalFeatureTag(tag))
                    result[existing] = new EnabledLookup(
                        lookupIndex, value, tag, required || current.Required);
            }
            else
            {
                positions.Add(lookupIndex, result.Count);
                result.Add(new EnabledLookup(lookupIndex, value, tag, required));
            }
        }
    }

    private static Dictionary<ushort, int>? GetFeatureVariationSubstitutions(TtfFont font, ReadOnlySpan<byte> data)
    {
        if (!CanRead(data, 0, 14) || ReadU16(data, 0) != 1 || ReadU16(data, 2) < 1)
        {
            return null;
        }
        uint featureVariationsRelative = ReadU32(data, 10);
        if (featureVariationsRelative == 0 || featureVariationsRelative > int.MaxValue)
        {
            return null;
        }
        int featureVariations = (int)featureVariationsRelative;
        if (!CanRead(data, featureVariations, 8) ||
            ReadU16(data, featureVariations) != 1 ||
            ReadU16(data, featureVariations + 2) != 0)
        {
            return null;
        }
        uint recordCount = ReadU32(data, featureVariations + 4);
        if (recordCount > int.MaxValue / 8 || !CanRead(data, featureVariations + 8, (int)recordCount * 8))
        {
            return null;
        }
        for (var recordIndex = 0; recordIndex < (int)recordCount; recordIndex++)
        {
            int record = featureVariations + 8 + recordIndex * 8;
            uint conditionSetRelative = ReadU32(data, record);
            uint substitutionRelative = ReadU32(data, record + 4);
            if (conditionSetRelative > int.MaxValue || substitutionRelative > int.MaxValue)
            {
                continue;
            }
            int conditionSet = featureVariations + (int)conditionSetRelative;
            if (!MatchesFeatureVariationConditions(font, data, conditionSet))
            {
                continue;
            }
            int substitutionTable = featureVariations + (int)substitutionRelative;
            if (!CanRead(data, substitutionTable, 6) ||
                ReadU16(data, substitutionTable) != 1 ||
                ReadU16(data, substitutionTable + 2) != 0)
            {
                return null;
            }
            ushort substitutionCount = ReadU16(data, substitutionTable + 4);
            if (!CanRead(data, substitutionTable + 6, substitutionCount * 6))
            {
                return null;
            }
            var result = new Dictionary<ushort, int>(substitutionCount);
            for (var index = 0; index < substitutionCount; index++)
            {
                int substitution = substitutionTable + 6 + index * 6;
                uint alternateRelative = ReadU32(data, substitution + 2);
                if (alternateRelative <= int.MaxValue - substitutionTable)
                {
                    result[ReadU16(data, substitution)] = substitutionTable + (int)alternateRelative;
                }
            }
            return result;
        }
        return null;
    }

    private static bool MatchesFeatureVariationConditions(TtfFont font, ReadOnlySpan<byte> data, int conditionSet)
    {
        if (!CanRead(data, conditionSet, 2)) return false;
        ushort conditionCount = ReadU16(data, conditionSet);
        if (!CanRead(data, conditionSet + 2, conditionCount * 4)) return false;
        for (var index = 0; index < conditionCount; index++)
        {
            uint conditionRelative = ReadU32(data, conditionSet + 2 + index * 4);
            if (conditionRelative > int.MaxValue) return false;
            int condition = conditionSet + (int)conditionRelative;
            if (!CanRead(data, condition, 8) || ReadU16(data, condition) != 1) return false;
            int axisIndex = ReadU16(data, condition + 2);
            if (!font.TryGetNormalizedVariationCoordinate(axisIndex, out short coordinate)) return false;
            short minimum = ReadI16(data, condition + 4);
            short maximum = ReadI16(data, condition + 6);
            if (coordinate < minimum || coordinate > maximum) return false;
        }
        return true;
    }

    private static HashSet<ushort>? GetRawLanguageFeatureIndices(
        ReadOnlySpan<byte> data,
        string script,
        string? language,
        out ushort requiredFeatureIndex)
    {
        requiredFeatureIndex = ushort.MaxValue;
        if (!CanRead(data, 4, 2)) return null;
        int scriptListOffset = ReadU16(data, 4);
        if (!CanRead(data, scriptListOffset, 2)) return null;
        ushort scriptCount = ReadU16(data, scriptListOffset);
        uint requestedScript = ToTag(script);
        int selectedScriptOffset = 0;
        int defaultScriptOffset = 0;
        int lowercaseDefaultScriptOffset = 0;
        int latinScriptOffset = 0;
        for (var index = 0; index < scriptCount; index++)
        {
            int record = scriptListOffset + 2 + index * 6;
            if (!CanRead(data, record, 6)) break;
            uint tag = ReadU32(data, record);
            int tableOffset = scriptListOffset + ReadU16(data, record + 4);
            if (tag == requestedScript) selectedScriptOffset = tableOffset;
            else if (tag == ToTag("DFLT")) defaultScriptOffset = tableOffset;
            else if (tag == ToTag("dflt")) lowercaseDefaultScriptOffset = tableOffset;
            else if (tag == ToTag("latn")) latinScriptOffset = tableOffset;
        }
        int scriptOffset = selectedScriptOffset != 0 ? selectedScriptOffset :
            defaultScriptOffset != 0 ? defaultScriptOffset :
            lowercaseDefaultScriptOffset != 0 ? lowercaseDefaultScriptOffset : latinScriptOffset;
        if (!CanRead(data, scriptOffset, 4)) return [];

        int languageOffset = 0;
        ushort defaultRelative = ReadU16(data, scriptOffset);
        if (defaultRelative != 0) languageOffset = scriptOffset + defaultRelative;
        ushort languageCount = ReadU16(data, scriptOffset + 2);
        if (!string.IsNullOrWhiteSpace(language))
        {
            uint requestedLanguage = ToLanguageTag(language);
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
        if (languageOffset == 0 || !CanRead(data, languageOffset, 6)) return [];
        requiredFeatureIndex = ReadU16(data, languageOffset + 2);
        ushort featureCount = ReadU16(data, languageOffset + 4);
        if (!CanRead(data, languageOffset + 6, featureCount * 2)) return null;
        int languageEnd = languageOffset + 6 + featureCount * 2;
        int featureListOffset = ReadU16(data, 6);
        int lookupListOffset = ReadU16(data, 8);
        if (languageOffset < featureListOffset && languageEnd > featureListOffset ||
            languageOffset < lookupListOffset && languageEnd > lookupListOffset)
        {
            requiredFeatureIndex = ushort.MaxValue;
            return [];
        }
        var result = new HashSet<ushort>();
        for (var index = 0; index < featureCount; index++)
            result.Add(ReadU16(data, languageOffset + 6 + index * 2));
        if (requiredFeatureIndex != ushort.MaxValue) result.Add(requiredFeatureIndex);
        return result;
    }

    private static void ApplyReverseChainingLookup(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        ushort lookupIndex,
        bool restrictToSyllable,
        string featureTag,
        bool required,
        TextShapingOptions options)
    {
        if (!TryGetLookup(data, lookupIndex, out int lookupOffset, out ushort lookupType, out int subtableCountOffset))
        {
            return;
        }
        ushort subtableCount = ReadU16(data, subtableCountOffset);
        ushort lookupFlags = ReadU16(data, lookupOffset + 2);
        ushort markFilteringSet = ushort.MaxValue;
        int markFilteringSetOffset = subtableCountOffset + 2 + subtableCount * 2;
        if ((lookupFlags & 0x0010) != 0 && CanRead(data, markFilteringSetOffset, 2))
            markFilteringSet = ReadU16(data, markFilteringSetOffset);
        int previousMarkFilteringCoverage = glyphs.SetMarkFilteringSet(markFilteringSet);
        for (var position = glyphs.Count - 1; position >= 0; position--)
        {
            glyphs.SetLookupSyllable(position, restrictToSyllable);
            if (!required && !glyphs.IsFeatureEnabled(position, featureTag, options)) continue;
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

                if (effectiveType == 8 && ApplyReverseChainingSubtable(
                        data, glyphs, subtableOffset, position, lookupFlags))
                {
                    break;
                }
            }
        }
        glyphs.RestoreMarkFilteringCoverage(previousMarkFilteringCoverage);
        glyphs.ClearLookupSyllable();
    }

    private static bool ApplyReverseChainingSubtable(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int offset,
        int position,
        ushort lookupFlags)
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
        if (!CanRead(data, cursor, backtrackCount * 2))
        {
            return false;
        }
        int matchPosition = position;
        for (var index = 0; index < backtrackCount; index++)
        {
            matchPosition = glyphs.PreviousContextIndex(matchPosition - 1, lookupFlags);
            if (matchPosition < 0) return false;
            int coverageOffset = offset + ReadU16(data, cursor + index * 2);
            if (FindCoverage(data, coverageOffset, glyphs[matchPosition]) < 0)
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
        if (!CanRead(data, cursor, lookaheadCount * 2))
        {
            return false;
        }
        matchPosition = position;
        for (var index = 0; index < lookaheadCount; index++)
        {
            matchPosition = glyphs.NextContextIndex(matchPosition + 1, lookupFlags);
            if (matchPosition < 0) return false;
            int coverageOffset = offset + ReadU16(data, cursor + index * 2);
            if (FindCoverage(data, coverageOffset, glyphs[matchPosition]) < 0)
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
        int featureValue,
        string featureTag,
        bool required,
        TextShapingOptions options)
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
                int positionValue = required
                    ? featureValue
                    : options.GetFeatureValue(featureTag, glyphs.GetRecord(position).Cluster);
                if (positionValue <= 0 ||
                    !required && !glyphs.IsFeatureEnabled(position, featureTag, options)) continue;
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

                int alternateIndex = featureTag == "rand" && positionValue == ushort.MaxValue
                    ? glyphs.NextRandomAlternate(alternateCount)
                    : Math.Clamp(positionValue, 1, alternateCount) - 1;
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
        int position,
        ushort lookupFlags)
    {
        if (!CanRead(data, subtableOffset, 6)) return false;
        return ReadU16(data, subtableOffset) switch
        {
            1 => ApplyContextFormat1(data, glyphs, subtableOffset, position, lookupFlags),
            2 => ApplyContextFormat2(data, glyphs, subtableOffset, position, lookupFlags),
            3 => ApplyContextFormat3(data, glyphs, subtableOffset, position, lookupFlags),
            _ => false
        };
    }

    private static bool ApplyContextFormat1(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int subtableOffset,
        int position,
        ushort lookupFlags)
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
            if (glyphCount == 0 ||
                !CanRead(data, ruleOffset + 4, (glyphCount - 1 + recordCount * 2) * 2)) continue;
            bool matches = true;
            int matchPosition = position;
            for (var index = 1; index < glyphCount; index++)
            {
                matchPosition = glyphs.NextVisibleIndex(matchPosition + 1, lookupFlags);
                if (matchPosition < 0 ||
                    glyphs[matchPosition] != ReadU16(data, ruleOffset + 4 + (index - 1) * 2))
                {
                    matches = false;
                    break;
                }
            }
            if (!matches) continue;
            int recordsOffset = ruleOffset + 4 + (glyphCount - 1) * 2;
            return ApplySubstitutionRecords(
                data, glyphs, position, recordsOffset, recordCount, lookupFlags, matchPosition + 1);
        }
        return false;
    }

    private static bool ApplyContextFormat2(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int subtableOffset,
        int position,
        ushort lookupFlags)
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
            if (glyphCount == 0 ||
                !CanRead(data, ruleOffset + 4, (glyphCount - 1 + recordCount * 2) * 2)) continue;
            bool matches = true;
            int matchPosition = position;
            for (var index = 1; index < glyphCount; index++)
            {
                matchPosition = glyphs.NextVisibleIndex(matchPosition + 1, lookupFlags);
                int expectedClass = ReadU16(data, ruleOffset + 4 + (index - 1) * 2);
                if (matchPosition < 0 || GetGlyphClass(data, classDef, glyphs[matchPosition]) != expectedClass)
                {
                    matches = false;
                    break;
                }
            }
            if (!matches) continue;
            int recordsOffset = ruleOffset + 4 + (glyphCount - 1) * 2;
            return ApplySubstitutionRecords(
                data, glyphs, position, recordsOffset, recordCount, lookupFlags, matchPosition + 1);
        }
        return false;
    }

    private static bool ApplyContextFormat3(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int subtableOffset,
        int position,
        ushort lookupFlags)
    {
        ushort glyphCount = ReadU16(data, subtableOffset + 2);
        ushort recordCount = ReadU16(data, subtableOffset + 4);
        int coverageOffsets = subtableOffset + 6;
        if (glyphCount == 0 ||
            !CanRead(data, coverageOffsets, (glyphCount + recordCount * 2) * 2)) return false;
        int matchPosition = position;
        for (var index = 0; index < glyphCount; index++)
        {
            int coverage = subtableOffset + ReadU16(data, coverageOffsets + index * 2);
            if (FindCoverage(data, coverage, glyphs[matchPosition]) < 0) return false;
            if (index + 1 < glyphCount)
            {
                matchPosition = glyphs.NextVisibleIndex(matchPosition + 1, lookupFlags);
                if (matchPosition < 0) return false;
            }
        }
        return ApplySubstitutionRecords(
            data,
            glyphs,
            position,
            coverageOffsets + glyphCount * 2,
            recordCount,
            lookupFlags,
            matchPosition + 1);
    }

    private static bool ApplyChainContextSubtable(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int subtableOffset,
        int position,
        ushort lookupFlags = 0)
    {
        if (!CanRead(data, subtableOffset, 2)) return false;
        return ReadU16(data, subtableOffset) switch
        {
            1 => ApplyChainContextFormat1(data, glyphs, subtableOffset, position, lookupFlags),
            2 => ApplyChainContextFormat2(data, glyphs, subtableOffset, position, lookupFlags),
            3 => ApplyChainContextFormat3(data, glyphs, subtableOffset, position, lookupFlags),
            _ => false
        };
    }

    private static bool ApplyChainContextFormat3(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int subtableOffset,
        int position,
        ushort lookupFlags)
    {
        int cursor = subtableOffset + 2;
        if (!CanRead(data, cursor, 2)) return false;
        ushort backtrackCount = ReadU16(data, cursor);
        cursor += 2;
        if (!CanRead(data, cursor, backtrackCount * 2)) return false;
        int matchPosition = position;
        for (var index = 0; index < backtrackCount; index++)
        {
            matchPosition = glyphs.PreviousContextIndex(matchPosition - 1, lookupFlags);
            if (matchPosition < 0) return false;
            int coverage = subtableOffset + ReadU16(data, cursor + index * 2);
            if (FindCoverage(data, coverage, glyphs[matchPosition]) < 0) return false;
        }
        cursor += backtrackCount * 2;

        if (!CanRead(data, cursor, 2)) return false;
        ushort inputCount = ReadU16(data, cursor);
        cursor += 2;
        if (inputCount == 0 || !CanRead(data, cursor, inputCount * 2)) return false;
        matchPosition = position;
        for (var index = 0; index < inputCount; index++)
        {
            if (index != 0) matchPosition = glyphs.NextVisibleIndex(matchPosition + 1, lookupFlags);
            if (matchPosition < 0) return false;
            int coverage = subtableOffset + ReadU16(data, cursor + index * 2);
            if (FindCoverage(data, coverage, glyphs[matchPosition]) < 0) return false;
        }
        int inputEnd = matchPosition + 1;
        cursor += inputCount * 2;

        if (!CanRead(data, cursor, 2)) return false;
        ushort lookaheadCount = ReadU16(data, cursor);
        cursor += 2;
        if (!CanRead(data, cursor, lookaheadCount * 2)) return false;
        for (var index = 0; index < lookaheadCount; index++)
        {
            matchPosition = glyphs.NextContextIndex(matchPosition + 1, lookupFlags);
            if (matchPosition < 0) return false;
            int coverage = subtableOffset + ReadU16(data, cursor + index * 2);
            if (FindCoverage(data, coverage, glyphs[matchPosition]) < 0) return false;
        }
        cursor += lookaheadCount * 2;

        if (!CanRead(data, cursor, 2)) return false;
        ushort recordCount = ReadU16(data, cursor);
        cursor += 2;
        return CanRead(data, cursor, recordCount * 4) &&
               ApplySubstitutionRecords(data, glyphs, position, cursor, recordCount, lookupFlags, inputEnd);
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
                    ApplyChainContextFormat1(data, glyphs, subtableOffset, position, ReadU16(data, lookupOffset + 2));
                }
                else
                {
                    ApplyChainContextFormat2(data, glyphs, subtableOffset, position, ReadU16(data, lookupOffset + 2));
                }
            }
        }
    }

    private static bool ApplyChainContextFormat1(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int subtableOffset,
        int position,
        ushort lookupFlags)
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
            if (MatchChainRule(data, glyphs, ruleOffset, position, lookupFlags, useClasses: false, 0, 0, 0,
                    out int recordsOffset, out ushort recordCount, out int inputEnd))
            {
                return ApplySubstitutionRecords(
                    data, glyphs, position, recordsOffset, recordCount, lookupFlags, inputEnd);
            }
        }
        return false;
    }

    private static bool ApplyChainContextFormat2(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int subtableOffset,
        int position,
        ushort lookupFlags)
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
                    lookupFlags,
                    useClasses: true,
                    backtrackClassDef,
                    inputClassDef,
                    lookaheadClassDef,
                    out int recordsOffset,
                    out ushort recordCount,
                    out int inputEnd))
            {
                return ApplySubstitutionRecords(
                    data, glyphs, position, recordsOffset, recordCount, lookupFlags, inputEnd);
            }
        }
        return false;
    }

    private static bool MatchChainRule(
        ReadOnlySpan<byte> data,
        GlyphSubstitutionBuffer glyphs,
        int ruleOffset,
        int position,
        ushort lookupFlags,
        bool useClasses,
        int backtrackClassDef,
        int inputClassDef,
        int lookaheadClassDef,
        out int recordsOffset,
        out ushort recordCount,
        out int inputEnd)
    {
        recordsOffset = 0;
        recordCount = 0;
        inputEnd = 0;
        if (!CanRead(data, ruleOffset, 2))
        {
            return false;
        }

        int cursor = ruleOffset;
        ushort backtrackCount = ReadU16(data, cursor);
        cursor += 2;
        if (!CanRead(data, cursor, backtrackCount * 2))
        {
            return false;
        }
        int matchPosition = position;
        for (var index = 0; index < backtrackCount; index++)
        {
            matchPosition = glyphs.PreviousContextIndex(matchPosition - 1, lookupFlags);
            if (matchPosition < 0) return false;
            ushort expected = ReadU16(data, cursor + index * 2);
            ushort actualGlyph = glyphs[matchPosition];
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
        if (!CanRead(data, cursor, trailingInputCount * 2))
        {
            return false;
        }
        matchPosition = position;
        for (var index = 0; index < trailingInputCount; index++)
        {
            matchPosition = glyphs.NextVisibleIndex(matchPosition + 1, lookupFlags);
            if (matchPosition < 0) return false;
            ushort expected = ReadU16(data, cursor + index * 2);
            ushort actualGlyph = glyphs[matchPosition];
            int actual = useClasses ? GetGlyphClass(data, inputClassDef, actualGlyph) : actualGlyph;
            if (actual != expected)
            {
                return false;
            }
        }
        inputEnd = matchPosition + 1;
        cursor += trailingInputCount * 2;

        if (!CanRead(data, cursor, 2))
        {
            return false;
        }
        ushort lookaheadCount = ReadU16(data, cursor);
        cursor += 2;
        if (!CanRead(data, cursor, lookaheadCount * 2))
        {
            return false;
        }
        for (var index = 0; index < lookaheadCount; index++)
        {
            matchPosition = glyphs.NextContextIndex(matchPosition + 1, lookupFlags);
            if (matchPosition < 0) return false;
            ushort expected = ReadU16(data, cursor + index * 2);
            ushort actualGlyph = glyphs[matchPosition];
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
        ushort recordCount,
        ushort lookupFlags,
        int inputEnd)
    {
        bool changed = false;
        for (var recordIndex = 0; recordIndex < recordCount; recordIndex++)
        {
            int recordOffset = recordsOffset + recordIndex * 4;
            ushort sequenceIndex = ReadU16(data, recordOffset);
            ushort lookupIndex = ReadU16(data, recordOffset + 2);
            int target = glyphs.GetVisibleSequenceIndex(position, sequenceIndex, lookupFlags);
            if (target >= 0)
            {
                int countBefore = glyphs.Count;
                bool recordChanged = ApplyNestedLookup(data, glyphs, lookupIndex, target);
                changed |= recordChanged;
                if (recordChanged && target < inputEnd)
                    inputEnd += glyphs.Count - countBefore;
            }
        }
        if (changed) glyphs.SetContextMatchEnd(inputEnd);
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
        if (!glyphs.TryEnterNestedLookup()) return false;

        ushort subtableCount = ReadU16(data, subtableCountOffset);
        ushort lookupFlags = ReadU16(data, lookupOffset + 2);
        ushort markFilteringSet = ushort.MaxValue;
        int markFilteringSetOffset = subtableCountOffset + 2 + subtableCount * 2;
        if ((lookupFlags & 0x0010) != 0 && CanRead(data, markFilteringSetOffset, 2))
            markFilteringSet = ReadU16(data, markFilteringSetOffset);
        int previousMarkFilteringCoverage = glyphs.SetMarkFilteringSet(markFilteringSet);
        try
        {
            for (var subtableIndex = 0; subtableIndex < subtableCount; subtableIndex++)
            {
                int subtableOffset = lookupOffset + ReadU16(data, subtableCountOffset + 2 + subtableIndex * 2);
                if (ApplyNestedSubtable(data, glyphs, lookupType, subtableOffset, position, lookupFlags))
                    return true;
            }
            return false;
        }
        finally
        {
            glyphs.ExitNestedLookup();
            glyphs.RestoreMarkFilteringCoverage(previousMarkFilteringCoverage);
        }
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
                ? ApplyContextSubtable(data, glyphs, offset, position, lookupFlags)
                : ApplyChainContextSubtable(data, glyphs, offset, position, lookupFlags);
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
                    ushort expectedGlyph = ReadU16(data, ligatureOffset + 4 + (component - 1) * 2);
                    candidateIndex = glyphs.NextLigatureComponentIndex(candidateIndex + 1, lookupFlags, expectedGlyph);
                    if (candidateIndex < 0 || glyphs[candidateIndex] != expectedGlyph)
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

    private static int GetTableLength(TtfFont font, string tag) =>
        font.TryGetTable(tag, out ReadOnlyMemory<byte> table) ? table.Length : 0;

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static short ReadI16(ReadOnlySpan<byte> data, int offset) => unchecked((short)ReadU16(data, offset));

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

    private static PositioningPlan CreatePositioningPlan(
        TtfFont font,
        GPOS? table,
        TextShapingOptions options,
        string script)
    {
        if (font.TryGetTable("GPOS", out ReadOnlyMemory<byte> rawTable))
        {
            EnabledLookup[] rawLookups = GetRawEnabledLookupIndices(
                font,
                rawTable.Span,
                options.Features,
                script,
                options.Language,
                out _).ToArray();
            return new PositioningPlan(
                table,
                rawTable,
                rawLookups,
                HasRawTable: true,
                HasTable: true,
                HasKerning: rawLookups.Any(static lookup => lookup.Tag is "kern" or "dist"));
        }

        EnabledLookup[] lookups = table?.FeatureList?.featureTables is { Length: > 0 } features
            ? GetEnabledLookupIndices(table, features, options, script).ToArray()
            : [];
        return new PositioningPlan(
            table,
            default,
            lookups,
            HasRawTable: false,
            HasTable: table is not null,
            HasKerning: lookups.Any(static lookup => lookup.Tag is "kern" or "dist"));
    }

    private static bool ApplyPositions(
        TtfFont font,
        PositioningPlan plan,
        GlyphPositionBuffer glyphs,
        TextShapingOptions options)
    {
        if (plan.HasRawTable)
        {
            foreach (EnabledLookup enabled in plan.Lookups)
            {
                ApplyRawPositionLookup(
                    plan.RawTable.Span, glyphs, enabled.LookupIndex, enabled.Tag, enabled.Required, options);
                ApplyPositionVariations(font, glyphs, enabled.LookupIndex);
            }
            return plan.HasKerning;
        }

        GPOS? table = plan.Table;
        if (table is null) return false;
        foreach (EnabledLookup enabled in plan.Lookups)
        {
            ushort lookupIndex = enabled.LookupIndex;
            if (lookupIndex < table.LookupList.Count)
            {
                table.LookupList[lookupIndex].DoGlyphPosition(glyphs, 0, glyphs.Count);
            }
        }
        return plan.HasKerning;
    }

    private static void ApplyRawPositionLookup(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        ushort lookupIndex,
        string featureTag,
        bool required,
        TextShapingOptions options)
    {
        if (!TryGetLookup(data, lookupIndex, out int lookupOffset, out ushort lookupType, out int subtableCountOffset))
        {
            return;
        }

        ushort lookupFlags = ReadU16(data, lookupOffset + 2);
        for (var position = 0; position < glyphs.Count; position++)
        {
            if (!required && options.GetFeatureValue(featureTag, glyphs[position].Cluster) == 0) continue;
            ApplyRawPositionLookupAt(data, glyphs, lookupIndex, position);
        }
    }

    private static bool ApplyRawPositionLookupAt(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        ushort lookupIndex,
        int position)
    {
        if ((uint)position >= glyphs.Count ||
            !TryGetLookup(data, lookupIndex, out int lookupOffset, out ushort lookupType, out int subtableCountOffset))
        {
            return false;
        }
        ushort lookupFlags = ReadU16(data, lookupOffset + 2);
        ushort subtableCount = ReadU16(data, subtableCountOffset);
        ushort markFilteringSet = ushort.MaxValue;
        int markFilteringSetOffset = subtableCountOffset + 2 + subtableCount * 2;
        if ((lookupFlags & 0x0010) != 0 && CanRead(data, markFilteringSetOffset, 2))
            markFilteringSet = ReadU16(data, markFilteringSetOffset);
        int previousMarkFilteringCoverage = glyphs.SetMarkFilteringSet(markFilteringSet);
        try
        {
            if (!glyphs.IsEligibleIndex(position, lookupFlags)) return false;
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
                    3 => ApplyCursivePosition(data, glyphs, subtableOffset, position, lookupFlags),
                    4 => ApplyMarkToBasePosition(data, glyphs, subtableOffset, position, lookupFlags),
                    5 => ApplyMarkToLigaturePosition(data, glyphs, subtableOffset, position, lookupFlags),
                    6 => ApplyMarkToMarkPosition(data, glyphs, subtableOffset, position, lookupFlags),
                    7 => ApplyContextPosition(data, glyphs, subtableOffset, position, lookupFlags),
                    8 => ApplyChainContextPosition(data, glyphs, subtableOffset, position, lookupFlags),
                    _ => false
                };
                if (matched) return true;
            }
            return false;
        }
        finally
        {
            glyphs.RestoreMarkFilteringCoverage(previousMarkFilteringCoverage);
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

    private static bool ApplyCursivePosition(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int offset,
        int position,
        ushort lookupFlags)
    {
        if (!CanRead(data, offset, 6) || ReadU16(data, offset) != 1) return false;
        int coverage = offset + ReadU16(data, offset + 2);
        int coverageIndex = FindCoverage(data, coverage, glyphs.GetGlyph(position));
        int recordCount = ReadU16(data, offset + 4);
        if ((uint)coverageIndex >= recordCount ||
            !CanRead(data, offset + 6 + coverageIndex * 4, 4)) return false;
        int currentRecord = offset + 6 + coverageIndex * 4;
        ushort entryRelative = ReadU16(data, currentRecord);
        if (entryRelative == 0) return false;

        int previous = glyphs.PreviousEligibleIndex(
            position - 1, lookupFlags, coverage, data, skipMarks: false);
        if (previous < 0) return false;
        int previousCoverageIndex = FindCoverage(data, coverage, glyphs.GetGlyph(previous));
        if ((uint)previousCoverageIndex >= recordCount ||
            !CanRead(data, offset + 6 + previousCoverageIndex * 4, 4)) return false;
        int previousRecord = offset + 6 + previousCoverageIndex * 4;
        ushort exitRelative = ReadU16(data, previousRecord + 2);
        if (exitRelative == 0 ||
            !TryReadAnchor(data, offset + entryRelative, out int entryX, out int entryY) ||
            !TryReadAnchor(data, offset + exitRelative, out int exitX, out int exitY)) return false;

        glyphs.AttachCursive(previous, position, entryX, entryY, exitX, exitY, lookupFlags);
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
        if (!glyphs.CanAttachMarkToMark(markIndex, mark2Index)) return false;
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

    private static bool ApplyContextPosition(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int offset,
        int position,
        ushort lookupFlags)
    {
        if (!CanRead(data, offset, 6)) return false;
        return ReadU16(data, offset) switch
        {
            1 => ApplyContextPositionFormat1(data, glyphs, offset, position, lookupFlags),
            2 => ApplyContextPositionFormat2(data, glyphs, offset, position, lookupFlags),
            3 => ApplyContextPositionFormat3(data, glyphs, offset, position, lookupFlags),
            _ => false
        };
    }

    private static bool ApplyContextPositionFormat1(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int offset,
        int position,
        ushort lookupFlags)
    {
        int coverageIndex = FindCoverage(data, offset + ReadU16(data, offset + 2), glyphs.GetGlyph(position));
        int setCount = ReadU16(data, offset + 4);
        if ((uint)coverageIndex >= setCount || !CanRead(data, offset + 6 + coverageIndex * 2, 2)) return false;
        ushort setRelative = ReadU16(data, offset + 6 + coverageIndex * 2);
        if (setRelative == 0) return false;
        int setOffset = offset + setRelative;
        if (!CanRead(data, setOffset, 2)) return false;
        int ruleCount = ReadU16(data, setOffset);
        for (var ruleIndex = 0; ruleIndex < ruleCount; ruleIndex++)
        {
            int pointer = setOffset + 2 + ruleIndex * 2;
            if (!CanRead(data, pointer, 2)) break;
            int rule = setOffset + ReadU16(data, pointer);
            if (!CanRead(data, rule, 4)) continue;
            int glyphCount = ReadU16(data, rule);
            int recordCount = ReadU16(data, rule + 2);
            if (glyphCount == 0 ||
                !CanRead(data, rule + 4, (glyphCount - 1) * 2 + recordCount * 4)) continue;
            bool matches = true;
            int matchPosition = position;
            for (var index = 1; index < glyphCount; index++)
            {
                matchPosition = glyphs.NextEligibleIndex(matchPosition + 1, lookupFlags);
                if (matchPosition < 0 ||
                    glyphs.GetGlyph(matchPosition) != ReadU16(data, rule + 4 + (index - 1) * 2))
                {
                    matches = false;
                    break;
                }
            }
            if (!matches) continue;
            return ApplyPositionRecords(data, glyphs, position, rule + 4 + (glyphCount - 1) * 2, recordCount, lookupFlags);
        }
        return false;
    }

    private static bool ApplyContextPositionFormat2(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int offset,
        int position,
        ushort lookupFlags)
    {
        if (!CanRead(data, offset, 8) ||
            FindCoverage(data, offset + ReadU16(data, offset + 2), glyphs.GetGlyph(position)) < 0) return false;
        int classDef = offset + ReadU16(data, offset + 4);
        int setCount = ReadU16(data, offset + 6);
        int firstClass = GetGlyphClass(data, classDef, glyphs.GetGlyph(position));
        if ((uint)firstClass >= setCount || !CanRead(data, offset + 8 + firstClass * 2, 2)) return false;
        ushort setRelative = ReadU16(data, offset + 8 + firstClass * 2);
        if (setRelative == 0) return false;
        int setOffset = offset + setRelative;
        if (!CanRead(data, setOffset, 2)) return false;
        int ruleCount = ReadU16(data, setOffset);
        for (var ruleIndex = 0; ruleIndex < ruleCount; ruleIndex++)
        {
            int pointer = setOffset + 2 + ruleIndex * 2;
            if (!CanRead(data, pointer, 2)) break;
            int rule = setOffset + ReadU16(data, pointer);
            if (!CanRead(data, rule, 4)) continue;
            int glyphCount = ReadU16(data, rule);
            int recordCount = ReadU16(data, rule + 2);
            if (glyphCount == 0 ||
                !CanRead(data, rule + 4, (glyphCount - 1) * 2 + recordCount * 4)) continue;
            bool matches = true;
            int matchPosition = position;
            for (var index = 1; index < glyphCount; index++)
            {
                matchPosition = glyphs.NextEligibleIndex(matchPosition + 1, lookupFlags);
                int expectedClass = ReadU16(data, rule + 4 + (index - 1) * 2);
                if (matchPosition < 0 ||
                    GetGlyphClass(data, classDef, glyphs.GetGlyph(matchPosition)) != expectedClass)
                {
                    matches = false;
                    break;
                }
            }
            if (!matches) continue;
            return ApplyPositionRecords(data, glyphs, position, rule + 4 + (glyphCount - 1) * 2, recordCount, lookupFlags);
        }
        return false;
    }

    private static bool ApplyContextPositionFormat3(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int offset,
        int position,
        ushort lookupFlags)
    {
        int glyphCount = ReadU16(data, offset + 2);
        int recordCount = ReadU16(data, offset + 4);
        int coverages = offset + 6;
        if (glyphCount == 0 ||
            !CanRead(data, coverages, glyphCount * 2 + recordCount * 4)) return false;
        int matchPosition = position;
        for (var index = 0; index < glyphCount; index++)
        {
            int coverage = offset + ReadU16(data, coverages + index * 2);
            if (FindCoverage(data, coverage, glyphs.GetGlyph(matchPosition)) < 0) return false;
            if (index + 1 < glyphCount)
            {
                matchPosition = glyphs.NextEligibleIndex(matchPosition + 1, lookupFlags);
                if (matchPosition < 0) return false;
            }
        }
        return ApplyPositionRecords(data, glyphs, position, coverages + glyphCount * 2, recordCount, lookupFlags);
    }

    private static bool ApplyChainContextPosition(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int offset,
        int position,
        ushort lookupFlags)
    {
        if (!CanRead(data, offset, 2)) return false;
        return ReadU16(data, offset) switch
        {
            1 => ApplyChainContextPositionFormat1(data, glyphs, offset, position, lookupFlags),
            2 => ApplyChainContextPositionFormat2(data, glyphs, offset, position, lookupFlags),
            3 => ApplyChainContextPositionFormat3(data, glyphs, offset, position, lookupFlags),
            _ => false
        };
    }

    private static bool ApplyChainContextPositionFormat1(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int offset,
        int position,
        ushort lookupFlags)
    {
        if (!CanRead(data, offset, 6)) return false;
        int coverageIndex = FindCoverage(data, offset + ReadU16(data, offset + 2), glyphs.GetGlyph(position));
        int setCount = ReadU16(data, offset + 4);
        if ((uint)coverageIndex >= setCount || !CanRead(data, offset + 6 + coverageIndex * 2, 2)) return false;
        ushort setRelative = ReadU16(data, offset + 6 + coverageIndex * 2);
        if (setRelative == 0) return false;
        int setOffset = offset + setRelative;
        if (!CanRead(data, setOffset, 2)) return false;
        int ruleCount = ReadU16(data, setOffset);
        for (var ruleIndex = 0; ruleIndex < ruleCount; ruleIndex++)
        {
            int pointer = setOffset + 2 + ruleIndex * 2;
            if (!CanRead(data, pointer, 2)) break;
            int rule = setOffset + ReadU16(data, pointer);
            if (MatchChainPositionRule(data, glyphs, rule, position, lookupFlags, false, 0, 0, 0,
                    out int records, out int recordCount))
            {
                return ApplyPositionRecords(data, glyphs, position, records, recordCount, lookupFlags);
            }
        }
        return false;
    }

    private static bool ApplyChainContextPositionFormat2(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int offset,
        int position,
        ushort lookupFlags)
    {
        if (!CanRead(data, offset, 12) ||
            FindCoverage(data, offset + ReadU16(data, offset + 2), glyphs.GetGlyph(position)) < 0) return false;
        int backtrackClassDef = offset + ReadU16(data, offset + 4);
        int inputClassDef = offset + ReadU16(data, offset + 6);
        int lookaheadClassDef = offset + ReadU16(data, offset + 8);
        int setCount = ReadU16(data, offset + 10);
        int firstClass = GetGlyphClass(data, inputClassDef, glyphs.GetGlyph(position));
        if ((uint)firstClass >= setCount || !CanRead(data, offset + 12 + firstClass * 2, 2)) return false;
        ushort setRelative = ReadU16(data, offset + 12 + firstClass * 2);
        if (setRelative == 0) return false;
        int setOffset = offset + setRelative;
        if (!CanRead(data, setOffset, 2)) return false;
        int ruleCount = ReadU16(data, setOffset);
        for (var ruleIndex = 0; ruleIndex < ruleCount; ruleIndex++)
        {
            int pointer = setOffset + 2 + ruleIndex * 2;
            if (!CanRead(data, pointer, 2)) break;
            int rule = setOffset + ReadU16(data, pointer);
            if (MatchChainPositionRule(data, glyphs, rule, position, lookupFlags, true,
                    backtrackClassDef, inputClassDef, lookaheadClassDef,
                    out int records, out int recordCount))
            {
                return ApplyPositionRecords(data, glyphs, position, records, recordCount, lookupFlags);
            }
        }
        return false;
    }

    private static bool ApplyChainContextPositionFormat3(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int offset,
        int position,
        ushort lookupFlags)
    {
        int cursor = offset + 2;
        if (!CanRead(data, cursor, 2)) return false;
        int backtrackCount = ReadU16(data, cursor);
        cursor += 2;
        if (!CanRead(data, cursor, backtrackCount * 2)) return false;
        int matchPosition = position;
        for (var index = 0; index < backtrackCount; index++)
        {
            matchPosition = glyphs.PreviousEligibleIndex(matchPosition - 1, lookupFlags);
            if (matchPosition < 0) return false;
            int coverage = offset + ReadU16(data, cursor + index * 2);
            if (FindCoverage(data, coverage, glyphs.GetGlyph(matchPosition)) < 0) return false;
        }
        cursor += backtrackCount * 2;
        if (!CanRead(data, cursor, 2)) return false;
        int inputCount = ReadU16(data, cursor);
        cursor += 2;
        if (inputCount == 0 || !CanRead(data, cursor, inputCount * 2)) return false;
        matchPosition = position;
        for (var index = 0; index < inputCount; index++)
        {
            int coverage = offset + ReadU16(data, cursor + index * 2);
            if (FindCoverage(data, coverage, glyphs.GetGlyph(matchPosition)) < 0) return false;
            if (index + 1 < inputCount)
            {
                matchPosition = glyphs.NextEligibleIndex(matchPosition + 1, lookupFlags);
                if (matchPosition < 0) return false;
            }
        }
        cursor += inputCount * 2;
        if (!CanRead(data, cursor, 2)) return false;
        int lookaheadCount = ReadU16(data, cursor);
        cursor += 2;
        if (!CanRead(data, cursor, lookaheadCount * 2)) return false;
        for (var index = 0; index < lookaheadCount; index++)
        {
            matchPosition = glyphs.NextEligibleIndex(matchPosition + 1, lookupFlags);
            if (matchPosition < 0) return false;
            int coverage = offset + ReadU16(data, cursor + index * 2);
            if (FindCoverage(data, coverage, glyphs.GetGlyph(matchPosition)) < 0) return false;
        }
        cursor += lookaheadCount * 2;
        if (!CanRead(data, cursor, 2)) return false;
        int recordCount = ReadU16(data, cursor);
        cursor += 2;
        return CanRead(data, cursor, recordCount * 4) &&
               ApplyPositionRecords(data, glyphs, position, cursor, recordCount, lookupFlags);
    }

    private static bool MatchChainPositionRule(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int ruleOffset,
        int position,
        ushort lookupFlags,
        bool useClasses,
        int backtrackClassDef,
        int inputClassDef,
        int lookaheadClassDef,
        out int recordsOffset,
        out int recordCount)
    {
        recordsOffset = 0;
        recordCount = 0;
        if (!CanRead(data, ruleOffset, 2)) return false;
        int cursor = ruleOffset;
        int backtrackCount = ReadU16(data, cursor);
        cursor += 2;
        if (!CanRead(data, cursor, backtrackCount * 2)) return false;
        int matchPosition = position;
        for (var index = 0; index < backtrackCount; index++)
        {
            matchPosition = glyphs.PreviousEligibleIndex(matchPosition - 1, lookupFlags);
            if (matchPosition < 0) return false;
            int expected = ReadU16(data, cursor + index * 2);
            ushort glyph = glyphs.GetGlyph(matchPosition);
            if (useClasses ? GetGlyphClass(data, backtrackClassDef, glyph) != expected : glyph != expected) return false;
        }
        cursor += backtrackCount * 2;
        if (!CanRead(data, cursor, 2)) return false;
        int inputCount = ReadU16(data, cursor);
        cursor += 2;
        if (inputCount == 0 || !CanRead(data, cursor, (inputCount - 1) * 2)) return false;
        matchPosition = position;
        for (var index = 1; index < inputCount; index++)
        {
            matchPosition = glyphs.NextEligibleIndex(matchPosition + 1, lookupFlags);
            if (matchPosition < 0) return false;
            int expected = ReadU16(data, cursor + (index - 1) * 2);
            ushort glyph = glyphs.GetGlyph(matchPosition);
            if (useClasses ? GetGlyphClass(data, inputClassDef, glyph) != expected : glyph != expected) return false;
        }
        cursor += (inputCount - 1) * 2;
        if (!CanRead(data, cursor, 2)) return false;
        int lookaheadCount = ReadU16(data, cursor);
        cursor += 2;
        if (!CanRead(data, cursor, lookaheadCount * 2)) return false;
        for (var index = 0; index < lookaheadCount; index++)
        {
            matchPosition = glyphs.NextEligibleIndex(matchPosition + 1, lookupFlags);
            if (matchPosition < 0) return false;
            int expected = ReadU16(data, cursor + index * 2);
            ushort glyph = glyphs.GetGlyph(matchPosition);
            if (useClasses ? GetGlyphClass(data, lookaheadClassDef, glyph) != expected : glyph != expected) return false;
        }
        cursor += lookaheadCount * 2;
        if (!CanRead(data, cursor, 2)) return false;
        recordCount = ReadU16(data, cursor);
        recordsOffset = cursor + 2;
        return CanRead(data, recordsOffset, recordCount * 4);
    }

    private static bool ApplyPositionRecords(
        ReadOnlySpan<byte> data,
        GlyphPositionBuffer glyphs,
        int position,
        int recordsOffset,
        int recordCount,
        ushort lookupFlags = 0)
    {
        if (!CanRead(data, recordsOffset, recordCount * 4)) return false;
        bool applied = false;
        for (var record = 0; record < recordCount; record++)
        {
            int offset = recordsOffset + record * 4;
            int sequenceIndex = ReadU16(data, offset);
            ushort lookupIndex = ReadU16(data, offset + 2);
            int target = glyphs.GetEligibleSequenceIndex(position, sequenceIndex, lookupFlags);
            if ((uint)target >= glyphs.Count) continue;
            applied |= ApplyRawPositionLookupAt(data, glyphs, lookupIndex, target);
        }
        return applied || recordCount == 0;
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

    private readonly record struct EnabledLookup(
        ushort LookupIndex,
        int Value,
        string Tag,
        bool Required = false);
    private readonly record struct SubstitutionPlan(
        GSUB? Table,
        ReadOnlyMemory<byte> RawTable,
        EnabledLookup[] Lookups,
        EnabledLookup[] RawLookups,
        bool RawOnly);
    private readonly record struct PositioningPlan(
        GPOS? Table,
        ReadOnlyMemory<byte> RawTable,
        EnabledLookup[] Lookups,
        bool HasRawTable,
        bool HasTable,
        bool HasKerning);
    private readonly record struct ShapingPlan(
        string Script,
        bool UseShaper,
        bool IndicShaper,
        bool KhmerShaper,
        bool MyanmarShaper,
        bool ArabicShaper,
        ShapingDirection Direction,
        TextShapingOptions Options,
        SubstitutionPlan Substitution,
        PositioningPlan Positioning,
        bool HasMarkFeature);

    private enum UseSubstitutionStage : byte
    {
        All,
        Directional,
        Preprocessing,
        Repha,
        Prebase,
        Basic,
        Topographical,
        Presentation,
        IndicPreprocessing,
        IndicNukta,
        IndicAkhand,
        IndicRakar,
        IndicBelow,
        IndicAbove,
        IndicHalf,
        IndicPost,
        IndicVattu,
        IndicConjunct,
        IndicPresentation,
        KhmerBasic,
        KhmerPresentation,
        MyanmarPreprocessing,
        MyanmarRepha,
        MyanmarPrebase,
        MyanmarBelow,
        MyanmarPostbase,
        MyanmarPresentation,
        ArabicStretch,
        ArabicPreprocessing,
        ArabicIsolated,
        ArabicFinal,
        ArabicFinal2,
        ArabicFinal3,
        ArabicMedial,
        ArabicMedial2,
        ArabicInitial,
        ArabicRequiredLigatures,
        ArabicContextual,
        ArabicPostRequired,
        ArabicPostContextual,
        ArabicLigatures,
        ArabicPresentation
    }

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
            AddFeatureLookups(features[required], 1, lookups, lookupPositions, required: true);
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
                    (allowedFeatures is not null && !allowedFeatures.Contains(featureIndex) &&
                     !IsGlobalShaperFeature(script, setting.Tag)))
                {
                    continue;
                }

                AddFeatureLookups(feature, value, lookups, lookupPositions);
            }
        }

        lookups.Sort(static (left, right) => left.LookupIndex.CompareTo(right.LookupIndex));
        return lookups;
    }

    private static bool IsGlobalShaperFeature(string script, string tag) =>
        tag == "rand" || IsDirectionalShaperFeature(tag) || script == "hang" && IsHangulJamoFeature(tag);

    private static bool IsHangulJamoFeature(string tag) => tag is "ljmo" or "vjmo" or "tjmo";

    private static bool IsDirectionalShaperFeature(string tag) =>
        tag is "ltra" or "ltrm" or "rtla" or "rtlm" or "vert" or "vrt2";

    private static bool IsGlobalFeatureTag(string tag) =>
        tag == "rand" || IsHangulJamoFeature(tag) || IsDirectionalShaperFeature(tag);

    private static void AddFeatureLookups(
        FeatureList.FeatureTable feature,
        int value,
        List<EnabledLookup> lookups,
        Dictionary<ushort, int> lookupPositions,
        bool required = false)
    {
        for (var lookupIndex = 0; lookupIndex < feature.LookupListIndices.Length; lookupIndex++)
        {
            ushort index = feature.LookupListIndices[lookupIndex];
            if (lookupPositions.TryGetValue(index, out int existing))
            {
                EnabledLookup current = lookups[existing];
                if (current.Required && !required) continue;
                if (!IsGlobalFeatureTag(current.Tag) || IsGlobalFeatureTag(feature.TagName))
                    lookups[existing] = new EnabledLookup(
                        index, value, feature.TagName, required || current.Required);
            }
            else
            {
                lookupPositions[index] = lookups.Count;
                lookups.Add(new EnabledLookup(index, value, feature.TagName, required));
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

    private static bool IsFeatureEnabled(IReadOnlyList<OpenTypeFeatureSetting> settings, string tag)
    {
        for (var index = settings.Count - 1; index >= 0; index--)
            if (settings[index].Tag == tag) return settings[index].Value != 0;
        return false;
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
            return [];
        }

        ScriptTable? scriptTable = scripts[ToTag(script)] ?? scripts[ToTag("DFLT")];
        if (scriptTable is null)
        {
            return [];
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
            return [];
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
            "dv" => ToTag("DHV "),
            "fa" => ToTag("FAR "),
            "ja" => ToTag("JAN "),
            "nl" => ToTag("NLD "),
            "pl" => ToTag("PLK "),
            "ro" => ToTag("ROM "),
            "tr" => ToTag("TRK "),
            "zh" or "zh-cn" or "zh-sg" or "zh-hans" => ToTag("ZHS "),
            "zh-tw" or "zh-hant" => ToTag("ZHT "),
            "zh-hk" or "zh-mo" or "zh-hant-hk" or "zh-hant-mo" => ToTag("ZHH "),
            _ => ToTag("dflt")
        };
    }

    private static string InferScript(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            string generatedScript = UnicodeScriptData.GetScript((uint)rune.Value);
            if (generatedScript.Length != 0)
            {
                return generatedScript switch
                {
                    "hira" => "kana",
                    "laoo" => "lao ",
                    _ => generatedScript
                };
            }
        }
        return "DFLT";
    }

    private static bool ResolveLayoutScript(TtfFont font, string unicodeScript, out string layoutScript)
    {
        string? thirdGenerationTag = unicodeScript switch
        {
            "beng" => "bng3",
            "deva" => "dev3",
            "gujr" => "gjr3",
            "guru" => "gur3",
            "knda" => "knd3",
            "mlym" => "mlm3",
            "orya" => "ory3",
            "taml" => "tml3",
            "telu" => "tel3",
            _ => null
        };
        if (thirdGenerationTag is not null && HasOpenTypeScript(font, "GSUB", thirdGenerationTag))
        {
            layoutScript = thirdGenerationTag;
            return true;
        }

        string? secondGenerationTag = unicodeScript switch
        {
            "beng" => "bng2",
            "deva" => "dev2",
            "gujr" => "gjr2",
            "guru" => "gur2",
            "knda" => "knd2",
            "mlym" => "mlm2",
            "mymr" => "mym2",
            "orya" => "ory2",
            "taml" => "tml2",
            "telu" => "tel2",
            _ => null
        };
        if (secondGenerationTag is not null && HasOpenTypeScript(font, "GSUB", secondGenerationTag))
        {
            layoutScript = secondGenerationTag;
            return false;
        }

        layoutScript = unicodeScript;
        return unicodeScript is
            "tibt" or "mong" or "sinh" or "java" or "marc" or "limb" or "tale" or
            "bugi" or "khar" or "sylo" or "tfng" or "bali" or "nkoo" or "phag" or
            "cham" or "kali" or "lepc" or "rjng" or "saur" or "sund" or "egyp" or
            "kthi" or "mtei" or "lana" or "tavt" or "batk" or "brah" or "mand" or
            "cakm" or "plrd" or "shrd" or "takr" or "dupl" or "gran" or "khoj" or
            "sind" or "mahj" or "mani" or "modi" or "hmng" or "phlp" or "sidd" or
            "tirh" or "ahom" or "mult" or "adlm" or "bhks" or "newa" or "gonm" or
            "soyo" or "zanb" or "dogr" or "gong" or "rohg" or "maka" or "medf" or
            "sogo" or "sogd" or "elym" or "nand" or "hmnp" or "wcho" or "chrs" or
            "diak" or "kits" or "yezi" or "cpmn" or "ougr" or "tnsa" or "toto" or
            "vith" or "kawi" or "nagm";
    }

    private static bool IsIndicShaperScript(string script) => script is
        "beng" or "deva" or "gujr" or "guru" or "knda" or "mlym" or "orya" or "taml" or "telu";

    private static bool HasOpenTypeScript(TtfFont font, string tableTag, string scriptTag)
    {
        if (!font.TryGetTable(tableTag, out ReadOnlyMemory<byte> memory)) return false;
        ReadOnlySpan<byte> data = memory.Span;
        if (!CanRead(data, 4, 2)) return false;
        int scriptList = ReadU16(data, 4);
        if (!CanRead(data, scriptList, 2)) return false;
        int count = ReadU16(data, scriptList);
        uint requested = ToTag(scriptTag);
        for (var index = 0; index < count; index++)
        {
            int record = scriptList + 2 + index * 6;
            if (!CanRead(data, record, 6)) break;
            if (ReadU32(data, record) == requested) return true;
        }
        return false;
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
        private readonly ReadOnlyMemory<byte> _gdefTable;
        private readonly ShapingClusterLevel _clusterLevel;
        private readonly ShapingBufferFlags _bufferFlags;
        private byte _lookupSyllable;
        private bool _restrictLookupToSyllable;
        private bool _manualJoiners;
        private int _contextMatchEnd;
        private int _markFilteringCoverage = -1;
        private uint _randomState = 1;
        private int _nestedLookupDepth;

        private GlyphSubstitutionBuffer(
            List<GlyphRecord> glyphs,
            TtfFont font,
            ShapingClusterLevel clusterLevel = ShapingClusterLevel.MonotoneGraphemes,
            ShapingBufferFlags bufferFlags = ShapingBufferFlags.None)
        {
            _glyphs = glyphs;
            _font = font;
            _clusterLevel = clusterLevel;
            _bufferFlags = bufferFlags;
            _typeface = font.LayoutTypeface;
            font.TryGetTable("GDEF", out _gdefTable);
            if (OpenTypeGdefPolicy.IsBlocklisted(
                    _gdefTable.Length,
                    GetTableLength(font, "GSUB"),
                    GetTableLength(font, "GPOS")))
                _gdefTable = default;
        }

        public int Count => _glyphs.Count;

        public void Reverse() => _glyphs.Reverse();

        public int NextRandomAlternate(int count)
        {
            _randomState = unchecked(_randomState * 48271u) % 2147483647u;
            return (int)(_randomState % (uint)count);
        }

        public int ContextMatchEnd => _contextMatchEnd;
        public ushort this[int index] =>
            _restrictLookupToSyllable && _glyphs[index].UseSyllable != _lookupSyllable
                ? ushort.MaxValue
                : _glyphs[index].GlyphIndex;
        public GlyphRecord GetRecord(int index) => _glyphs[index];

        public void ResetContextMatchEnd() => _contextMatchEnd = 0;

        public void SetContextMatchEnd(int end) =>
            _contextMatchEnd = Math.Max(_contextMatchEnd, Math.Clamp(end, 0, _glyphs.Count));

        public void SetLookupSyllable(int index, bool restrict)
        {
            _restrictLookupToSyllable = restrict;
            _lookupSyllable = restrict ? _glyphs[index].UseSyllable : (byte)0;
        }

        public void ClearLookupSyllable()
        {
            _restrictLookupToSyllable = false;
            _lookupSyllable = 0;
        }

        public void SetManualJoiners(bool enabled) => _manualJoiners = enabled;

        public void ClearSyllables()
        {
            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                glyph.UseSyllable = 0;
                _glyphs[index] = glyph;
            }
        }

        public void ApplyVowelConstraints(string script)
        {
            for (var index = 0; index + 1 < _glyphs.Count;)
            {
                uint third = index + 2 < _glyphs.Count ? _glyphs[index + 2].CodePoint : 0;
                int matchLength = VowelConstraintData.MatchLength(
                    script,
                    _glyphs[index].CodePoint,
                    _glyphs[index + 1].CodePoint,
                    third);
                if (matchLength == 0)
                {
                    index++;
                    continue;
                }

                int finalIndex = index + matchLength - 1;
                GlyphRecord final = _glyphs[finalIndex];
                _glyphs.Insert(
                    finalIndex,
                    new GlyphRecord(_font.GetGlyphIndex(0x25CC), final.Cluster, 0x25CC));
                index += matchLength + 1;
            }
        }

        public void ComposeHebrewPresentationForms(bool enabled)
        {
            if (!enabled) return;
            for (var start = 0; start < _glyphs.Count;)
            {
                int cluster = _glyphs[start].GraphemeCluster;
                int end = start + 1;
                while (end < _glyphs.Count && _glyphs[end].GraphemeCluster == cluster) end++;
                int starter = start;
                while (starter < end && !IsHebrewStarter(_glyphs[starter].CodePoint)) starter++;
                if (starter < end)
                {
                    for (var index = starter + 1; index < end;)
                    {
                        GlyphRecord baseGlyph = _glyphs[starter];
                        if (TryComposeHebrew(baseGlyph.CodePoint, _glyphs[index].CodePoint, out uint composed) &&
                            _font.GetGlyphIndex(composed) is ushort composedGlyph and not 0)
                        {
                            baseGlyph.CodePoint = composed;
                            baseGlyph.GlyphIndex = composedGlyph;
                            _glyphs[starter] = baseGlyph;
                            _glyphs.RemoveAt(index);
                            end--;
                            continue;
                        }
                        index++;
                    }
                }
                start = end;
            }
        }

        public void ReorderModifiedCombiningMarks(string script)
        {
            int segmentStart = 0;
            while (segmentStart < _glyphs.Count)
            {
                while (segmentStart < _glyphs.Count &&
                       GetModifiedCombiningClass(_glyphs[segmentStart].CodePoint) == 0)
                    segmentStart++;
                int segmentEnd = segmentStart;
                while (segmentEnd < _glyphs.Count &&
                       GetModifiedCombiningClass(_glyphs[segmentEnd].CodePoint) != 0)
                    segmentEnd++;
                for (var index = segmentStart + 1; index < segmentEnd; index++)
                {
                    GlyphRecord value = _glyphs[index];
                    int valueClass = GetModifiedCombiningClass(value.CodePoint);
                    int destination = index;
                    int crossedCluster = int.MaxValue;
                    while (destination > segmentStart &&
                           GetModifiedCombiningClass(_glyphs[destination - 1].CodePoint) > valueClass)
                    {
                        crossedCluster = Math.Min(crossedCluster, _glyphs[destination - 1].Cluster);
                        _glyphs[destination] = _glyphs[destination - 1];
                        destination--;
                    }
                    _glyphs[destination] = value;
                    if (_clusterLevel == ShapingClusterLevel.MonotoneCharacters && destination < index)
                    {
                        for (int crossed = destination + 1; crossed <= index; crossed++)
                        {
                            GlyphRecord glyph = _glyphs[crossed];
                            glyph.Cluster = crossedCluster;
                            _glyphs[crossed] = glyph;
                        }
                    }
                }
                if (UsesArabicJoining(script))
                    ReorderArabicModifierMarks(segmentStart, segmentEnd);
                segmentStart = segmentEnd + 1;
            }
        }

        public void ApplyArabicFallback(bool enabled, TextShapingOptions options)
        {
            if (!enabled) return;
            Dictionary<string, int> requested = CreateFeatureMap(options.Features);
            ReadOnlySpan<ushort> forms = ArabicFallbackData.ShapingForms;
            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                int form = glyph.ArabicAction switch
                {
                    Initial when IsEnabled(requested, "init") => 0,
                    Medial when IsEnabled(requested, "medi") => 1,
                    Final when IsEnabled(requested, "fina") => 2,
                    Isolated when IsEnabled(requested, "isol") => 3,
                    _ => -1
                };
                if (form < 0 || glyph.CodePoint < ArabicFallbackData.FirstCodePoint ||
                    glyph.CodePoint > ArabicFallbackData.LastCodePoint) continue;
                ushort presentationCodePoint = forms[checked((int)(glyph.CodePoint - ArabicFallbackData.FirstCodePoint) * 4 + form)];
                if (presentationCodePoint == 0 || glyph.GlyphIndex != _font.GetGlyphIndex(glyph.CodePoint)) continue;
                ushort presentationGlyph = _font.GetGlyphIndex(presentationCodePoint);
                if (presentationGlyph != 0 && presentationGlyph != glyph.GlyphIndex)
                    Replace(index, presentationGlyph);
            }

            if (!IsEnabled(requested, "rlig")) return;
            ApplyArabicFallbackLigatures(ArabicFallbackData.ThreeComponentLigatures, 2, ignoreMarks: true);
            ApplyArabicFallbackLigatures(ArabicFallbackData.TwoComponentLigatures, 1, ignoreMarks: true);
            ApplyArabicFallbackLigatures(ArabicFallbackData.MarkLigatures, 1, ignoreMarks: false);
        }

        public void RecordArabicStretch()
        {
            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                if (glyph.Multiplied == 0) continue;
                glyph.ArabicAction = (glyph.MultipleSubstitutionComponent & 1) != 0
                    ? StretchRepeating
                    : StretchFixed;
                _glyphs[index] = glyph;
            }
        }

        private static bool IsEnabled(Dictionary<string, int> requested, string tag) =>
            requested.TryGetValue(tag, out int value) && value != 0;

        private void ApplyArabicFallbackLigatures(
            ReadOnlySpan<ushort> table,
            int componentCount,
            bool ignoreMarks)
        {
            int stride = componentCount + 2;
            ushort lookupFlags = ignoreMarks ? (ushort)0x0008 : (ushort)0;
            Span<int> components = stackalloc int[3];
            for (var position = 0; position < _glyphs.Count; position++)
            {
                ushort firstGlyph = _glyphs[position].GlyphIndex;
                for (var row = 0; row + stride <= table.Length; row += stride)
                {
                    ushort expectedFirst = _font.GetGlyphIndex(table[row]);
                    if (expectedFirst == 0 || firstGlyph != expectedFirst) continue;
                    components[0] = position;
                    int candidate = position;
                    bool matched = true;
                    for (var component = 0; component < componentCount; component++)
                    {
                        ushort expected = _font.GetGlyphIndex(table[row + 1 + component]);
                        if (expected == 0)
                        {
                            matched = false;
                            break;
                        }
                        candidate = NextLigatureComponentIndex(candidate + 1, lookupFlags, expected);
                        if (candidate < 0 || _glyphs[candidate].GlyphIndex != expected)
                        {
                            matched = false;
                            break;
                        }
                        components[component + 1] = candidate;
                    }
                    ushort ligature = _font.GetGlyphIndex(table[row + 1 + componentCount]);
                    if (!matched || ligature == 0) continue;
                    ReplaceLigature(components[..(componentCount + 1)], ligature);
                    break;
                }
            }
        }

        public static int GetModifiedCombiningClass(uint codePoint)
        {
            if (codePoint is 0x1A60 or 0x0FC6) return 254;
            if (codePoint == 0x0F39) return 127;
            int canonical = UnicodeCombiningClassData.GetCanonicalClass(codePoint);
            return canonical switch
            {
                10 => 22, 11 => 15, 12 => 16, 13 => 17, 14 => 23, 15 => 18,
                16 => 19, 17 => 20, 18 => 21, 19 => 14, 20 => 24, 21 => 12,
                22 => 25, 23 => 13, 24 => 10, 25 => 11,
                27 => 28, 28 => 29, 29 => 30, 30 => 31, 31 => 32, 32 => 33,
                33 => 27, 84 => 4, 91 => 5, 103 => 3, 130 => 132, 132 => 131,
                _ => canonical
            };
        }

        private void ReorderArabicModifierMarks(int start, int end)
        {
            for (var canonicalClass = 220; canonicalClass <= 230; canonicalClass += 10)
            {
                int first = start;
                while (first < end && GetModifiedCombiningClass(_glyphs[first].CodePoint) < canonicalClass)
                    first++;
                if (first == end || GetModifiedCombiningClass(_glyphs[first].CodePoint) != canonicalClass)
                    continue;
                int last = first;
                while (last < end &&
                       GetModifiedCombiningClass(_glyphs[last].CodePoint) == canonicalClass &&
                       IsArabicModifierCombiningMark(_glyphs[last].CodePoint))
                    last++;
                int count = last - first;
                if (count == 0) continue;
                ReverseGlyphRecords(start, first);
                ReverseGlyphRecords(first, last);
                ReverseGlyphRecords(start, last);
                start += count;
            }
        }

        private void ReverseGlyphRecords(int start, int end)
        {
            for (end--; start < end; start++, end--)
                (_glyphs[start], _glyphs[end]) = (_glyphs[end], _glyphs[start]);
        }

        private static bool IsArabicModifierCombiningMark(uint codePoint) => codePoint is
            0x0654 or 0x0655 or 0x0658 or 0x06DC or 0x06E3 or 0x06E7 or 0x06E8 or
            0x08CA or 0x08CB or 0x08CD or 0x08CE or 0x08CF or 0x08D3 or 0x08F3;

        private static bool IsHebrewStarter(uint codePoint) =>
            codePoint is >= 0x05D0 and <= 0x05EA or >= 0xFB1D and <= 0xFB4E or 0x05F2;

        private static bool TryComposeHebrew(uint first, uint second, out uint composed)
        {
            composed = 0;
            if (second == 0x05BC && first is >= 0x05D0 and <= 0x05EA)
            {
                ReadOnlySpan<ushort> dageshForms =
                [
                    0xFB30, 0xFB31, 0xFB32, 0xFB33, 0xFB34, 0xFB35, 0xFB36, 0,
                    0xFB38, 0xFB39, 0xFB3A, 0xFB3B, 0xFB3C, 0, 0xFB3E, 0,
                    0xFB40, 0xFB41, 0, 0xFB43, 0xFB44, 0, 0xFB46, 0xFB47, 0xFB48,
                    0xFB49, 0xFB4A
                ];
                composed = dageshForms[(int)(first - 0x05D0)];
                return composed != 0;
            }
            composed = (first, second) switch
            {
                (0x05D9, 0x05B4) => 0xFB1D,
                (0x05F2, 0x05B7) => 0xFB1F,
                (0x05D0, 0x05B7) => 0xFB2E,
                (0x05D0, 0x05B8) => 0xFB2F,
                (0x05D5, 0x05B9) => 0xFB4B,
                (0x05D1, 0x05BF) => 0xFB4C,
                (0x05DB, 0x05BF) => 0xFB4D,
                (0x05E4, 0x05BF) => 0xFB4E,
                (0x05E9, 0x05C1) => 0xFB2A,
                (0x05E9, 0x05C2) => 0xFB2B,
                (0xFB49, 0x05C1) or (0xFB2A, 0x05BC) => 0xFB2C,
                (0xFB49, 0x05C2) or (0xFB2B, 0x05BC) => 0xFB2D,
                _ => 0
            };
            return composed != 0;
        }

        public void NormalizeUseDiacritics(bool enabled)
        {
            if (!enabled)
            {
                return;
            }
            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord source = _glyphs[index];
                string scalar = new Rune((int)source.CodePoint).ToString();
                string decomposed = scalar.Normalize(NormalizationForm.FormD);
                if (decomposed.Equals(scalar, StringComparison.Ordinal))
                {
                    continue;
                }
                Rune.DecodeFromUtf16(decomposed, out Rune first, out _);
                if (!IsUnicodeMark((uint)first.Value))
                {
                    continue;
                }

                _glyphs.RemoveAt(index);
                int component = 0;
                foreach (Rune rune in decomposed.EnumerateRunes())
                {
                    _glyphs.Insert(
                        index + component,
                        new GlyphRecord(_font.GetGlyphIndex((uint)rune.Value), source.Cluster, (uint)rune.Value));
                    component++;
                }
                index += component - 1;
            }
        }

        public void PrepareThaiLao(string script)
        {
            if (script is not ("thai" or "lao "))
            {
                return;
            }
            uint saraAm = script == "thai" ? 0x0E33u : 0x0EB3u;
            uint nikhahit = script == "thai" ? 0x0E4Du : 0x0ECDu;
            uint saraAa = saraAm - 1;
            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord source = _glyphs[index];
                if (source.CodePoint != saraAm) continue;

                var nikhahitGlyph = new GlyphRecord(_font.GetGlyphIndex(nikhahit), source.Cluster, nikhahit);
                var saraAaGlyph = new GlyphRecord(_font.GetGlyphIndex(saraAa), source.Cluster, saraAa);
                _glyphs[index] = nikhahitGlyph;
                _glyphs.Insert(index + 1, saraAaGlyph);

                int start = index;
                while (start > 0 && IsThaiLaoAboveBaseMark(_glyphs[start - 1].CodePoint)) start--;
                if (start < index)
                {
                    _glyphs.RemoveAt(index);
                    _glyphs.Insert(start, nikhahitGlyph);
                }
                int end = index + 2;
                if (_clusterLevel is ShapingClusterLevel.MonotoneGraphemes or ShapingClusterLevel.Graphemes)
                    MergeCluster(start - 1, end);
                else if (_clusterLevel == ShapingClusterLevel.MonotoneCharacters)
                    MergeCluster(start, end);
                index++;
            }
        }

        public void PrepareHangulShaping(bool enabled, TtfFont font)
        {
            if (!enabled) return;
            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                glyph.ScriptShaper = ScriptShaperHangul;
                glyph.ScriptFeatureMask = 0;
                _glyphs[index] = glyph;
            }

            for (var index = 0; index < _glyphs.Count; index++)
            {
                uint codePoint = _glyphs[index].CodePoint;
                if (IsHangulL(codePoint) && index + 1 < _glyphs.Count && IsHangulV(_glyphs[index + 1].CodePoint))
                {
                    uint trailing = index + 2 < _glyphs.Count && IsHangulT(_glyphs[index + 2].CodePoint)
                        ? _glyphs[index + 2].CodePoint
                        : 0;
                    int inputCount = trailing == 0 ? 2 : 3;
                    if (codePoint is >= 0x1100 and <= 0x1112 &&
                        _glyphs[index + 1].CodePoint is >= 0x1161 and <= 0x1175 &&
                        (trailing == 0 || trailing is >= 0x11A8 and <= 0x11C2))
                    {
                        uint syllable = 0xAC00u + (codePoint - 0x1100u) * 588u +
                            (_glyphs[index + 1].CodePoint - 0x1161u) * 28u +
                            (trailing == 0 ? 0 : trailing - 0x11A7u);
                        ushort composedGlyph = font.GetGlyphIndex(syllable);
                        if (composedGlyph != 0)
                        {
                            GlyphRecord composed = _glyphs[index];
                            composed.CodePoint = syllable;
                            composed.GlyphIndex = composedGlyph;
                            composed.Cluster = MergeCluster(index, index + inputCount);
                            composed.ScriptFeatureMask = 0;
                            _glyphs[index] = composed;
                            _glyphs.RemoveRange(index + 1, inputCount - 1);
                            continue;
                        }
                    }

                    SetHangulFeature(index, HangulLjmoMask);
                    SetHangulFeature(index + 1, HangulVjmoMask);
                    if (trailing != 0) SetHangulFeature(index + 2, HangulTjmoMask);
                    MergeCluster(index, index + inputCount);
                    index += inputCount - 1;
                    continue;
                }

                if (codePoint is >= 0xAC00 and <= 0xD7A3)
                {
                    uint syllableIndex = codePoint - 0xAC00u;
                    uint trailingIndex = syllableIndex % 28u;
                    if (trailingIndex == 0 && index + 1 < _glyphs.Count &&
                        _glyphs[index + 1].CodePoint is >= 0x11A8 and <= 0x11C2)
                    {
                        uint combined = codePoint + _glyphs[index + 1].CodePoint - 0x11A7u;
                        ushort combinedGlyph = font.GetGlyphIndex(combined);
                        if (combinedGlyph != 0)
                        {
                            GlyphRecord composed = _glyphs[index];
                            composed.CodePoint = combined;
                            composed.GlyphIndex = combinedGlyph;
                            composed.Cluster = MergeCluster(index, index + 2);
                            _glyphs[index] = composed;
                            _glyphs.RemoveAt(index + 1);
                            continue;
                        }
                    }

                    bool hasSyllableGlyph = font.GetGlyphIndex(codePoint) != 0;
                    bool followedByNonCombiningTrailing = trailingIndex == 0 && index + 1 < _glyphs.Count &&
                        IsHangulT(_glyphs[index + 1].CodePoint) &&
                        _glyphs[index + 1].CodePoint is not (>= 0x11A8 and <= 0x11C2);
                    if (!hasSyllableGlyph || followedByNonCombiningTrailing)
                    {
                        uint leading = 0x1100u + syllableIndex / 588u;
                        uint vowel = 0x1161u + syllableIndex % 588u / 28u;
                        uint trailingJamo = 0x11A7u + trailingIndex;
                        ushort leadingGlyph = font.GetGlyphIndex(leading);
                        ushort vowelGlyph = font.GetGlyphIndex(vowel);
                        ushort trailingGlyph = trailingIndex == 0 ? (ushort)0 : font.GetGlyphIndex(trailingJamo);
                        if (leadingGlyph != 0 && vowelGlyph != 0 && (trailingIndex == 0 || trailingGlyph != 0))
                        {
                            GlyphRecord source = _glyphs[index];
                            source.CodePoint = leading;
                            source.GlyphIndex = leadingGlyph;
                            source.ScriptFeatureMask = HangulLjmoMask;
                            _glyphs[index] = source;
                            GlyphRecord vowelRecord = source;
                            vowelRecord.CodePoint = vowel;
                            vowelRecord.GlyphIndex = vowelGlyph;
                            vowelRecord.ScriptFeatureMask = HangulVjmoMask;
                            _glyphs.Insert(index + 1, vowelRecord);
                            int decompositionCount = 2;
                            if (trailingIndex != 0)
                            {
                                GlyphRecord trailingRecord = source;
                                trailingRecord.CodePoint = trailingJamo;
                                trailingRecord.GlyphIndex = trailingGlyph;
                                trailingRecord.ScriptFeatureMask = HangulTjmoMask;
                                _glyphs.Insert(index + 2, trailingRecord);
                                decompositionCount++;
                            }
                            if (followedByNonCombiningTrailing)
                            {
                                SetHangulFeature(index + decompositionCount, HangulTjmoMask);
                                decompositionCount++;
                            }
                            index += decompositionCount - 1;
                        }
                    }
                }
            }
        }

        public void ApplyDirectionalCodePointFallback(
            TtfFont font,
            ShapingDirection direction,
            bool hasVerticalSubstitution)
        {
            bool backward = direction is ShapingDirection.RightToLeft or ShapingDirection.BottomToTop;
            bool verticalFallback = !hasVerticalSubstitution &&
                direction is ShapingDirection.TopToBottom or ShapingDirection.BottomToTop;
            if (!backward && !verticalFallback) return;

            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                uint codePoint = glyph.CodePoint;
                if (backward)
                {
                    uint mirrored = UnicodeDirectionalData.GetMirroredCodePoint(codePoint);
                    if (mirrored != codePoint && font.GetGlyphIndex(mirrored) != 0)
                        codePoint = mirrored;
                }
                if (verticalFallback)
                {
                    uint vertical = UnicodeDirectionalData.GetVerticalCodePoint(codePoint);
                    if (vertical != codePoint && font.GetGlyphIndex(vertical) != 0)
                        codePoint = vertical;
                }
                if (codePoint == glyph.CodePoint) continue;
                glyph.CodePoint = codePoint;
                glyph.GlyphIndex = font.GetGlyphIndex(codePoint);
                _glyphs[index] = glyph;
            }
        }

        private void SetHangulFeature(int index, byte feature)
        {
            GlyphRecord glyph = _glyphs[index];
            glyph.ScriptShaper = ScriptShaperHangul;
            glyph.ScriptFeatureMask = feature;
            _glyphs[index] = glyph;
        }

        private static bool IsHangulL(uint codePoint) => codePoint is
            >= 0x1100 and <= 0x115F or >= 0xA960 and <= 0xA97C;

        private static bool IsHangulV(uint codePoint) => codePoint is
            >= 0x1160 and <= 0x11A7 or >= 0xD7B0 and <= 0xD7C6;

        private static bool IsHangulT(uint codePoint) => codePoint is
            >= 0x11A8 and <= 0x11FF or >= 0xD7CB and <= 0xD7FB;

        private static bool IsThaiLaoAboveBaseMark(uint codePoint)
        {
            uint thai = codePoint & ~0x80u;
            return thai is >= 0x0E34 and <= 0x0E37 or >= 0x0E47 and <= 0x0E4E or 0x0E31 or 0x0E3B;
        }

        public void PrepareKhmerShaping(string script)
        {
            if (script != "khmr")
            {
                return;
            }

            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                glyph.ScriptShaper = ScriptShaperKhmer;
                glyph.IndicCategory = IndicShapingData.GetCategory(IndicShapingData.GetProperties(glyph.CodePoint));
                _glyphs[index] = glyph;
            }

            FindKhmerSyllables();
            InsertBrokenKhmerDottedCircles();
            for (var start = 0; start < _glyphs.Count;)
            {
                byte syllable = _glyphs[start].UseSyllable;
                int end = start + 1;
                while (end < _glyphs.Count && _glyphs[end].UseSyllable == syllable) end++;
                byte type = (byte)(syllable & 0x0F);
                if (type is KhmerConsonantSyllable or KhmerBrokenCluster)
                    PrepareKhmerSyllable(start, end);
                start = end;
            }
        }

        private void FindKhmerSyllables()
        {
            if (_glyphs.Count == 0) return;
            int state = KhmerSyllableMachineData.StartState;
            int position = 0;
            int tokenStart = -1;
            int tokenEnd = -1;
            int pendingAction = 0;
            byte serial = 1;
            while (true)
            {
                int transition;
                if (position == _glyphs.Count)
                {
                    transition = KhmerSyllableMachineData.GetEofTransition(state);
                    if (transition < 0) break;
                }
                else
                {
                    if (KhmerSyllableMachineData.GetFromStateAction(state) == 7) tokenStart = position;
                    transition = KhmerSyllableMachineData.GetTransition(state, _glyphs[position].IndicCategory);
                }

                state = KhmerSyllableMachineData.GetTarget(transition);
                switch (KhmerSyllableMachineData.GetAction(transition))
                {
                    case 2: tokenEnd = position + 1; break;
                    case 8: tokenEnd = position + 1; AssignKhmerSyllable(tokenStart, tokenEnd, KhmerNonCluster, ref serial); break;
                    case 10: tokenEnd = position; position--; AssignKhmerSyllable(tokenStart, tokenEnd, KhmerConsonantSyllable, ref serial); break;
                    case 11: tokenEnd = position; position--; AssignKhmerSyllable(tokenStart, tokenEnd, KhmerBrokenCluster, ref serial); break;
                    case 12: tokenEnd = position; position--; AssignKhmerSyllable(tokenStart, tokenEnd, KhmerNonCluster, ref serial); break;
                    case 1: position = tokenEnd - 1; AssignKhmerSyllable(tokenStart, tokenEnd, KhmerConsonantSyllable, ref serial); break;
                    case 3: position = tokenEnd - 1; AssignKhmerSyllable(tokenStart, tokenEnd, KhmerBrokenCluster, ref serial); break;
                    case 5:
                        position = tokenEnd - 1;
                        AssignKhmerSyllable(tokenStart, tokenEnd,
                            pendingAction == 2 ? KhmerBrokenCluster : KhmerNonCluster, ref serial);
                        break;
                    case 4: tokenEnd = position + 1; pendingAction = 2; break;
                    case 9: tokenEnd = position + 1; pendingAction = 3; break;
                }
                if (KhmerSyllableMachineData.GetToStateAction(state) == 6) tokenStart = -1;
                position++;
                if (position < 0 || position > _glyphs.Count) break;
            }
        }

        private void AssignKhmerSyllable(int start, int end, byte type, ref byte serial)
        {
            if (start < 0 || end < start) return;
            byte value = (byte)(serial << 4 | type);
            for (var index = start; index < end; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                glyph.UseSyllable = value;
                _glyphs[index] = glyph;
            }
            if (++serial == 16) serial = 1;
        }

        private void InsertBrokenKhmerDottedCircles()
        {
            if ((_bufferFlags & ShapingBufferFlags.DoNotInsertDottedCircle) != 0) return;
            ushort dottedGlyph = _font.GetGlyphIndex(0x25CC);
            if (dottedGlyph == 0) return;
            byte previous = 0;
            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord current = _glyphs[index];
                if (current.UseSyllable == previous || (current.UseSyllable & 0x0F) != KhmerBrokenCluster)
                {
                    previous = current.UseSyllable;
                    continue;
                }
                previous = current.UseSyllable;
                _glyphs.Insert(index, new GlyphRecord(dottedGlyph, current.Cluster, 0x25CC)
                {
                    ScriptShaper = ScriptShaperKhmer,
                    IndicCategory = IndicShapingData.DottedCircle,
                    UseSyllable = previous
                });
                index++;
            }
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

            int coengCount = 0;
            for (var index = start + 1; index < end; index++)
            {
                if (_glyphs[index].IndicCategory == IndicShapingData.Halant && coengCount <= 2 && index + 1 < end)
                {
                    coengCount++;
                    if (_glyphs[index + 1].IndicCategory == IndicShapingData.Ra)
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
                else if (_glyphs[index].IndicCategory == IndicShapingData.VowelPre)
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
            if (start >= end) return start < _glyphs.Count ? _glyphs[start].Cluster : 0;
            int firstCluster = _glyphs[start].Cluster;
            int lastCluster = _glyphs[end - 1].Cluster;
            while (start > 0 && _glyphs[start - 1].Cluster == firstCluster) start--;
            while (end < _glyphs.Count && _glyphs[end].Cluster == lastCluster) end++;
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

        public void PrepareMyanmarShaping(bool enabled)
        {
            if (!enabled) return;
            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                glyph.ScriptShaper = ScriptShaperMyanmar;
                glyph.IndicCategory = IndicShapingData.GetCategory(
                    IndicShapingData.GetProperties(glyph.CodePoint));
                _glyphs[index] = glyph;
            }
            FindMyanmarSyllables();
        }

        private void FindMyanmarSyllables()
        {
            if (_glyphs.Count == 0) return;
            int state = MyanmarSyllableMachineData.StartState;
            int position = 0;
            int tokenStart = -1;
            int tokenEnd = -1;
            int pendingAction = 0;
            byte serial = 1;
            while (true)
            {
                int transition;
                if (position == _glyphs.Count)
                {
                    transition = MyanmarSyllableMachineData.GetEofTransition(state);
                    if (transition < 0) break;
                }
                else
                {
                    if (MyanmarSyllableMachineData.GetFromStateAction(state) == 2)
                        tokenStart = position;
                    transition = MyanmarSyllableMachineData.GetTransition(
                        state, _glyphs[position].IndicCategory);
                }

                state = MyanmarSyllableMachineData.GetTarget(transition);
                switch (MyanmarSyllableMachineData.GetAction(transition))
                {
                    case 8: tokenEnd = position + 1; AssignMyanmarSyllable(tokenStart, tokenEnd, MyanmarConsonantSyllable, ref serial); break;
                    case 4:
                    case 3: tokenEnd = position + 1; AssignMyanmarSyllable(tokenStart, tokenEnd, MyanmarNonCluster, ref serial); break;
                    case 10: tokenEnd = position + 1; AssignMyanmarSyllable(tokenStart, tokenEnd, MyanmarBrokenCluster, ref serial); break;
                    case 7: tokenEnd = position; position--; AssignMyanmarSyllable(tokenStart, tokenEnd, MyanmarConsonantSyllable, ref serial); break;
                    case 9: tokenEnd = position; position--; AssignMyanmarSyllable(tokenStart, tokenEnd, MyanmarBrokenCluster, ref serial); break;
                    case 12: tokenEnd = position; position--; AssignMyanmarSyllable(tokenStart, tokenEnd, MyanmarNonCluster, ref serial); break;
                    case 11:
                        position = tokenEnd - 1;
                        AssignMyanmarSyllable(
                            tokenStart,
                            tokenEnd,
                            pendingAction == 2 ? MyanmarNonCluster : MyanmarBrokenCluster,
                            ref serial);
                        break;
                    case 6: tokenEnd = position + 1; pendingAction = 2; break;
                    case 5: tokenEnd = position + 1; pendingAction = 3; break;
                }
                if (MyanmarSyllableMachineData.GetToStateAction(state) == 1)
                    tokenStart = -1;
                position++;
                if (position < 0 || position > _glyphs.Count) break;
            }
        }

        private void AssignMyanmarSyllable(int start, int end, byte type, ref byte serial)
        {
            if (start < 0 || end < start) return;
            byte value = (byte)(serial << 4 | type);
            for (var index = start; index < end; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                glyph.UseSyllable = value;
                _glyphs[index] = glyph;
            }
            if (++serial == 16) serial = 1;
        }

        public void ReorderMyanmar()
        {
            InsertBrokenMyanmarDottedCircles();
            for (var start = 0; start < _glyphs.Count;)
            {
                byte syllable = _glyphs[start].UseSyllable;
                int end = start + 1;
                while (end < _glyphs.Count && _glyphs[end].UseSyllable == syllable) end++;
                byte type = (byte)(syllable & 0x0F);
                if (type is MyanmarConsonantSyllable or MyanmarBrokenCluster)
                    ReorderMyanmarSyllable(start, end);
                start = end;
            }
        }

        private void InsertBrokenMyanmarDottedCircles()
        {
            if ((_bufferFlags & ShapingBufferFlags.DoNotInsertDottedCircle) != 0) return;
            ushort dottedGlyph = _font.GetGlyphIndex(0x25CC);
            if (dottedGlyph == 0) return;
            byte previous = 0;
            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord current = _glyphs[index];
                if (current.UseSyllable == previous ||
                    (current.UseSyllable & 0x0F) != MyanmarBrokenCluster)
                {
                    previous = current.UseSyllable;
                    continue;
                }
                previous = current.UseSyllable;
                _glyphs.Insert(index, new GlyphRecord(dottedGlyph, current.Cluster, 0x25CC)
                {
                    ScriptShaper = ScriptShaperMyanmar,
                    IndicCategory = IndicShapingData.DottedCircle,
                    UseSyllable = previous
                });
                index++;
            }
        }

        private void ReorderMyanmarSyllable(int start, int end)
        {
            int baseIndex = end;
            bool hasReph = end - start >= 3 &&
                _glyphs[start].IndicCategory == IndicShapingData.Ra &&
                _glyphs[start + 1].IndicCategory == IndicShapingData.Asat &&
                _glyphs[start + 2].IndicCategory == IndicShapingData.Halant;
            int limit = hasReph ? start + 3 : start;
            if (hasReph) baseIndex = start;
            else baseIndex = limit;
            for (var index = limit; index < end; index++)
            {
                if (IsMyanmarConsonant(_glyphs[index]))
                {
                    baseIndex = index;
                    break;
                }
            }

            int cursor = start;
            for (; cursor < start + (hasReph ? 3 : 0); cursor++)
                SetIndicPosition(cursor, IndicShapingData.PositionAfterMain);
            for (; cursor < baseIndex; cursor++)
                SetIndicPosition(cursor, IndicShapingData.PositionPreConsonant);
            if (cursor < end)
                SetIndicPosition(cursor++, IndicShapingData.PositionBaseConsonant);

            byte position = IndicShapingData.PositionAfterMain;
            for (; cursor < end; cursor++)
            {
                byte category = _glyphs[cursor].IndicCategory;
                if (category == IndicShapingData.MedialRa)
                {
                    SetIndicPosition(cursor, IndicShapingData.PositionPreConsonant);
                    continue;
                }
                if (category == IndicShapingData.VowelPre)
                {
                    SetIndicPosition(cursor, IndicShapingData.PositionPreMatra);
                    continue;
                }
                if (category == IndicShapingData.VariationSelector)
                {
                    SetIndicPosition(cursor, _glyphs[cursor - 1].IndicPosition);
                    continue;
                }
                if (position == IndicShapingData.PositionAfterMain && category == IndicShapingData.VowelBelow)
                {
                    position = IndicShapingData.PositionBelowConsonant;
                    SetIndicPosition(cursor, position);
                    continue;
                }
                if (position == IndicShapingData.PositionBelowConsonant && category == IndicShapingData.VedicSign)
                {
                    SetIndicPosition(cursor, IndicShapingData.PositionBeforeSub);
                    continue;
                }
                if (position == IndicShapingData.PositionBelowConsonant && category == IndicShapingData.VowelBelow)
                {
                    SetIndicPosition(cursor, position);
                    continue;
                }
                if (position == IndicShapingData.PositionBelowConsonant && category != IndicShapingData.VedicSign)
                    position = IndicShapingData.PositionAfterSub;
                SetIndicPosition(cursor, position);
            }

            MergeCluster(start, end);
            StableSortMyanmarPositions(start, end);

            int firstLeftMatra = end;
            int lastLeftMatra = end;
            for (var index = start; index < end; index++)
            {
                if (_glyphs[index].IndicPosition != IndicShapingData.PositionPreMatra) continue;
                if (firstLeftMatra == end) firstLeftMatra = index;
                lastLeftMatra = index;
            }
            if (firstLeftMatra < lastLeftMatra)
            {
                _glyphs.Reverse(firstLeftMatra, lastLeftMatra - firstLeftMatra + 1);
                int segmentStart = firstLeftMatra;
                for (var index = segmentStart; index <= lastLeftMatra; index++)
                {
                    if (_glyphs[index].IndicCategory != IndicShapingData.VowelPre) continue;
                    _glyphs.Reverse(segmentStart, index - segmentStart + 1);
                    segmentStart = index + 1;
                }
            }
        }

        private void SetIndicPosition(int index, byte position)
        {
            GlyphRecord glyph = _glyphs[index];
            glyph.IndicPosition = position;
            _glyphs[index] = glyph;
        }

        private void StableSortMyanmarPositions(int start, int end)
        {
            int count = end - start;
            if (count < 2) return;
            Span<int> buckets = stackalloc int[IndicShapingData.PositionEnd + 2];
            for (var index = start; index < end; index++)
                buckets[Math.Min(_glyphs[index].IndicPosition, IndicShapingData.PositionEnd) + 1]++;
            for (var index = 1; index < buckets.Length; index++)
                buckets[index] += buckets[index - 1];
            GlyphRecord[] ordered = ArrayPool<GlyphRecord>.Shared.Rent(count);
            try
            {
                for (var index = start; index < end; index++)
                {
                    GlyphRecord glyph = _glyphs[index];
                    ordered[buckets[Math.Min(glyph.IndicPosition, IndicShapingData.PositionEnd)]++] = glyph;
                }
                for (var index = 0; index < count; index++) _glyphs[start + index] = ordered[index];
            }
            finally
            {
                ArrayPool<GlyphRecord>.Shared.Return(ordered);
            }
        }

        private static bool IsMyanmarConsonant(GlyphRecord glyph)
        {
            if (glyph.Ligated != 0) return false;
            return glyph.IndicCategory is IndicShapingData.Consonant or IndicShapingData.ConsonantWithStacker or
                IndicShapingData.Ra or IndicShapingData.Vowel or IndicShapingData.Placeholder or IndicShapingData.DottedCircle;
        }

        public void PrepareIndicShaping(bool enabled)
        {
            if (!enabled) return;
            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                ushort properties = IndicShapingData.GetProperties(glyph.CodePoint);
                glyph.ScriptShaper = ScriptShaperIndic;
                glyph.IndicCategory = IndicShapingData.GetCategory(properties);
                glyph.IndicPosition = IndicShapingData.GetPosition(properties);
                _glyphs[index] = glyph;
            }
            FindIndicSyllables();
        }

        private void FindIndicSyllables()
        {
            if (_glyphs.Count == 0) return;
            int state = IndicSyllableMachineData.StartState;
            int position = 0;
            int tokenStart = -1;
            int tokenEnd = -1;
            int pendingAction = 0;
            byte serial = 1;
            while (true)
            {
                int transition;
                if (position == _glyphs.Count)
                {
                    transition = IndicSyllableMachineData.GetEofTransition(state);
                    if (transition < 0) break;
                }
                else
                {
                    if (IndicSyllableMachineData.GetFromStateAction(state) == 10) tokenStart = position;
                    transition = IndicSyllableMachineData.GetTransition(state, _glyphs[position].IndicCategory);
                }

                state = IndicSyllableMachineData.GetTarget(transition);
                switch (IndicSyllableMachineData.GetAction(transition))
                {
                    case 2: tokenEnd = position + 1; break;
                    case 11: tokenEnd = position + 1; AssignIndicSyllable(tokenStart, tokenEnd, IndicNonCluster, ref serial); break;
                    case 14: tokenEnd = position; position--; AssignIndicSyllable(tokenStart, tokenEnd, IndicConsonantSyllable, ref serial); break;
                    case 15: tokenEnd = position; position--; AssignIndicSyllable(tokenStart, tokenEnd, IndicVowelSyllable, ref serial); break;
                    case 18: tokenEnd = position; position--; AssignIndicSyllable(tokenStart, tokenEnd, IndicStandaloneCluster, ref serial); break;
                    case 20: tokenEnd = position; position--; AssignIndicSyllable(tokenStart, tokenEnd, IndicSymbolCluster, ref serial); break;
                    case 16: tokenEnd = position; position--; AssignIndicSyllable(tokenStart, tokenEnd, IndicBrokenCluster, ref serial); break;
                    case 17: tokenEnd = position; position--; AssignIndicSyllable(tokenStart, tokenEnd, IndicNonCluster, ref serial); break;
                    case 1: position = tokenEnd - 1; AssignIndicSyllable(tokenStart, tokenEnd, IndicConsonantSyllable, ref serial); break;
                    case 3: position = tokenEnd - 1; AssignIndicSyllable(tokenStart, tokenEnd, IndicVowelSyllable, ref serial); break;
                    case 7: position = tokenEnd - 1; AssignIndicSyllable(tokenStart, tokenEnd, IndicStandaloneCluster, ref serial); break;
                    case 8: position = tokenEnd - 1; AssignIndicSyllable(tokenStart, tokenEnd, IndicSymbolCluster, ref serial); break;
                    case 4: position = tokenEnd - 1; AssignIndicSyllable(tokenStart, tokenEnd, IndicBrokenCluster, ref serial); break;
                    case 6:
                        position = tokenEnd - 1;
                        AssignIndicSyllable(tokenStart, tokenEnd, pendingAction switch
                        {
                            1 => IndicConsonantSyllable,
                            6 => IndicBrokenCluster,
                            _ => IndicNonCluster
                        }, ref serial);
                        break;
                    case 19: tokenEnd = position + 1; pendingAction = 1; break;
                    case 13: tokenEnd = position + 1; pendingAction = 5; break;
                    case 5: tokenEnd = position + 1; pendingAction = 6; break;
                    case 12: tokenEnd = position + 1; pendingAction = 7; break;
                }
                if (IndicSyllableMachineData.GetToStateAction(state) == 9) tokenStart = -1;
                position++;
                if (position < 0 || position > _glyphs.Count) break;
            }
        }

        private void AssignIndicSyllable(int start, int end, byte type, ref byte serial)
        {
            if (start < 0 || end < start) return;
            byte value = (byte)(serial << 4 | type);
            for (var index = start; index < end; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                glyph.UseSyllable = value;
                _glyphs[index] = glyph;
            }
            if (++serial == 16) serial = 1;
        }

        public void InitialReorderIndic(string unicodeScript, string layoutScript, SubstitutionPlan plan)
        {
            bool oldSpec = layoutScript.Length < 4 || layoutScript[3] != '2';
            UpdateIndicConsonantPositions(unicodeScript, plan, oldSpec);
            InsertBrokenIndicDottedCircles();
            for (var start = 0; start < _glyphs.Count;)
            {
                byte syllable = _glyphs[start].UseSyllable;
                int end = start + 1;
                while (end < _glyphs.Count && _glyphs[end].UseSyllable == syllable) end++;
                byte type = (byte)(syllable & 0x0F);
                if (type is IndicConsonantSyllable or IndicVowelSyllable or IndicStandaloneCluster or IndicBrokenCluster)
                    InitialReorderIndicSyllable(start, end, unicodeScript, oldSpec, plan);
                start = end;
            }
        }

        private void UpdateIndicConsonantPositions(string script, SubstitutionPlan plan, bool oldSpec)
        {
            uint virama = GetIndicVirama(script);
            ushort viramaGlyph = _font.GetGlyphIndex(virama);
            if (viramaGlyph == 0) return;
            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                if (glyph.IndicPosition != IndicShapingData.PositionBaseConsonant) continue;
                Span<ushort> first = stackalloc ushort[2] { viramaGlyph, glyph.GlyphIndex };
                Span<ushort> second = stackalloc ushort[2] { glyph.GlyphIndex, viramaGlyph };
                if (WouldSubstitute(plan, "blwf", first) || WouldSubstitute(plan, "blwf", second) ||
                    WouldSubstitute(plan, "vatu", first) || WouldSubstitute(plan, "vatu", second))
                    glyph.IndicPosition = IndicShapingData.PositionBelowConsonant;
                else if (WouldSubstitute(plan, "pstf", first) || WouldSubstitute(plan, "pstf", second) ||
                         WouldSubstitute(plan, "pref", first) || WouldSubstitute(plan, "pref", second))
                    glyph.IndicPosition = IndicShapingData.PositionPostConsonant;
                _glyphs[index] = glyph;
            }
        }

        private bool WouldSubstitute(SubstitutionPlan plan, string tag, ReadOnlySpan<ushort> glyphIndices)
        {
            if (plan.RawTable.IsEmpty) return false;
            var records = new List<GlyphRecord>(glyphIndices.Length);
            for (var index = 0; index < glyphIndices.Length; index++)
                records.Add(new GlyphRecord(glyphIndices[index], index, 0));
            var probe = new GlyphSubstitutionBuffer(records, _font);
            foreach (EnabledLookup lookup in plan.RawLookups)
            {
                if (lookup.Tag != tag) continue;
                int beforeCount = probe.Count;
                ushort beforeGlyph = probe[0];
                if (ApplyNestedLookup(plan.RawTable.Span, probe, lookup.LookupIndex, 0) &&
                    (probe.Count != beforeCount || probe[0] != beforeGlyph)) return true;
            }
            return false;
        }

        private void InsertBrokenIndicDottedCircles()
        {
            if ((_bufferFlags & ShapingBufferFlags.DoNotInsertDottedCircle) != 0) return;
            ushort dottedGlyph = _font.GetGlyphIndex(0x25CC);
            if (dottedGlyph == 0) return;
            byte previous = 0;
            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord current = _glyphs[index];
                if (current.UseSyllable == previous || (current.UseSyllable & 0x0F) != IndicBrokenCluster)
                {
                    previous = current.UseSyllable;
                    continue;
                }
                previous = current.UseSyllable;
                while (index < _glyphs.Count && _glyphs[index].UseSyllable == previous &&
                       _glyphs[index].IndicCategory == IndicShapingData.Repha) index++;
                _glyphs.Insert(index, new GlyphRecord(dottedGlyph, current.Cluster, 0x25CC)
                {
                    ScriptShaper = ScriptShaperIndic,
                    IndicCategory = IndicShapingData.DottedCircle,
                    IndicPosition = IndicShapingData.PositionEnd,
                    UseSyllable = previous
                });
            }
        }

        private void InitialReorderIndicSyllable(
            int start,
            int end,
            string script,
            bool oldSpec,
            SubstitutionPlan plan)
        {
            if (script == "knda" && end - start >= 3 &&
                _glyphs[start].IndicCategory == IndicShapingData.Ra &&
                _glyphs[start + 1].IndicCategory == IndicShapingData.Halant &&
                _glyphs[start + 2].IndicCategory == IndicShapingData.Zwj)
            {
                MergeCluster(start + 1, start + 3);
                (_glyphs[start + 1], _glyphs[start + 2]) = (_glyphs[start + 2], _glyphs[start + 1]);
            }

            int limit = start;
            int baseIndex = end;
            bool hasReph = false;
            IndicRephMode rephMode = GetIndicRephMode(script);
            if (end - start >= 3 &&
                ((rephMode == IndicRephMode.Implicit && !IsIndicJoiner(_glyphs[start + 2])) ||
                 (rephMode == IndicRephMode.Explicit && _glyphs[start + 2].IndicCategory == IndicShapingData.Zwj)))
            {
                int length = rephMode == IndicRephMode.Explicit ? 3 : 2;
                Span<ushort> probe = stackalloc ushort[3];
                for (var index = 0; index < length; index++) probe[index] = _glyphs[start + index].GlyphIndex;
                if (WouldSubstitute(plan, "rphf", probe[..2]) ||
                    length == 3 && WouldSubstitute(plan, "rphf", probe[..3]))
                {
                    limit += 2;
                    while (limit < end && IsIndicJoiner(_glyphs[limit])) limit++;
                    baseIndex = start;
                    hasReph = true;
                }
            }
            else if (rephMode == IndicRephMode.Logical && _glyphs[start].IndicCategory == IndicShapingData.Repha)
            {
                limit++;
                while (limit < end && IsIndicJoiner(_glyphs[limit])) limit++;
                baseIndex = start;
                hasReph = true;
            }

            bool seenBelow = false;
            for (var index = end - 1; index >= limit; index--)
            {
                GlyphRecord glyph = _glyphs[index];
                if (IsIndicConsonant(glyph))
                {
                    if (glyph.IndicPosition != IndicShapingData.PositionBelowConsonant &&
                        (glyph.IndicPosition != IndicShapingData.PositionPostConsonant || seenBelow))
                    {
                        baseIndex = index;
                        break;
                    }
                    if (glyph.IndicPosition == IndicShapingData.PositionBelowConsonant) seenBelow = true;
                    baseIndex = index;
                }
                else if (index > start && glyph.IndicCategory == IndicShapingData.Zwj &&
                         _glyphs[index - 1].IndicCategory == IndicShapingData.Halant) break;
            }
            if (hasReph && baseIndex == start && limit - baseIndex <= 2) hasReph = false;

            for (var index = start; index < baseIndex; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                glyph.IndicPosition = Math.Min(IndicShapingData.PositionPreConsonant, glyph.IndicPosition);
                _glyphs[index] = glyph;
            }
            if (baseIndex < end)
            {
                GlyphRecord glyph = _glyphs[baseIndex];
                glyph.IndicPosition = IndicShapingData.PositionBaseConsonant;
                _glyphs[baseIndex] = glyph;
            }
            if (hasReph)
            {
                GlyphRecord glyph = _glyphs[start];
                glyph.IndicPosition = IndicShapingData.PositionRaToBecomeReph;
                _glyphs[start] = glyph;
            }

            if (oldSpec && baseIndex < end)
            {
                bool disallowDoubleHalants = script == "knda";
                for (var index = baseIndex + 1; index < end; index++)
                {
                    if (_glyphs[index].IndicCategory != IndicShapingData.Halant) continue;
                    int destination = end - 1;
                    while (destination > index && !IsIndicConsonant(_glyphs[destination]) &&
                           !(disallowDoubleHalants && _glyphs[destination].IndicCategory == IndicShapingData.Halant))
                        destination--;
                    if (_glyphs[destination].IndicCategory != IndicShapingData.Halant && destination > index)
                    {
                        GlyphRecord halant = _glyphs[index];
                        _glyphs.RemoveAt(index);
                        _glyphs.Insert(destination, halant);
                    }
                    break;
                }
            }

            AttachIndicMarkPositions(start, end, baseIndex);
            for (var index = start; index < end; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                glyph.IndicOriginalOrder = (byte)Math.Min(index - start, byte.MaxValue);
                _glyphs[index] = glyph;
            }
            StableSortIndic(start, end);
            ReverseIndicLeftMatras(start, end);
            baseIndex = FindIndicBase(start, end);
            MergeIndicSortClusters(start, end, baseIndex, oldSpec);
            SetupIndicFeatureMasks(start, end, baseIndex, oldSpec, script, plan);
        }

        private void ReverseIndicLeftMatras(int start, int end)
        {
            int first = end;
            int last = end;
            for (var index = start; index < end; index++)
            {
                if (_glyphs[index].IndicPosition == IndicShapingData.PositionBaseConsonant) break;
                if (_glyphs[index].IndicPosition != IndicShapingData.PositionPreMatra) continue;
                if (first == end) first = index;
                last = index;
            }
            if (first >= last) return;
            ReverseGlyphRange(first, last + 1);
            int groupStart = first;
            for (var index = first; index <= last; index++)
            {
                if (_glyphs[index].IndicCategory is not (IndicShapingData.Matra or IndicShapingData.MatraPost)) continue;
                ReverseGlyphRange(groupStart, index + 1);
                groupStart = index + 1;
            }
        }

        private void ReverseGlyphRange(int start, int end)
        {
            for (int left = start, right = end - 1; left < right; left++, right--)
                (_glyphs[left], _glyphs[right]) = (_glyphs[right], _glyphs[left]);
        }

        private void MergeIndicSortClusters(int start, int end, int baseIndex, bool oldSpec)
        {
            if (baseIndex >= end) return;
            if (oldSpec || end - start > 127)
            {
                MergeCluster(baseIndex, end);
                return;
            }
            for (var index = baseIndex; index < end; index++)
            {
                if (_glyphs[index].IndicOriginalOrder == byte.MaxValue) continue;
                int minimum = index;
                int maximum = index;
                int cursor = start + _glyphs[index].IndicOriginalOrder;
                while (cursor != index && cursor >= start && cursor < end)
                {
                    minimum = Math.Min(minimum, cursor);
                    maximum = Math.Max(maximum, cursor);
                    int next = start + _glyphs[cursor].IndicOriginalOrder;
                    GlyphRecord visited = _glyphs[cursor];
                    visited.IndicOriginalOrder = byte.MaxValue;
                    _glyphs[cursor] = visited;
                    cursor = next;
                }
                MergeCluster(Math.Max(baseIndex, minimum), maximum + 1);
            }
        }

        private void AttachIndicMarkPositions(int start, int end, int baseIndex)
        {
            byte lastPosition = IndicShapingData.PositionStart;
            for (var index = start; index < end; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                if (IsIndicJoiner(glyph) || glyph.IndicCategory is IndicShapingData.Nukta or
                    IndicShapingData.RegisterShifter or IndicShapingData.ConsonantMedial or IndicShapingData.Halant)
                {
                    glyph.IndicPosition = lastPosition;
                    if (glyph.IndicCategory == IndicShapingData.Halant && glyph.IndicPosition == IndicShapingData.PositionPreMatra)
                    {
                        for (var prior = index; prior > start; prior--)
                            if (_glyphs[prior - 1].IndicPosition != IndicShapingData.PositionPreMatra)
                            {
                                glyph.IndicPosition = _glyphs[prior - 1].IndicPosition;
                                break;
                            }
                    }
                    _glyphs[index] = glyph;
                }
                else if (glyph.IndicPosition != IndicShapingData.PositionSyllableModifierVedic)
                {
                    if (glyph.IndicCategory == IndicShapingData.MatraPost && index > start &&
                        _glyphs[index - 1].IndicCategory == IndicShapingData.SyllableModifier)
                    {
                        GlyphRecord previous = _glyphs[index - 1];
                        previous.IndicPosition = glyph.IndicPosition;
                        _glyphs[index - 1] = previous;
                    }
                    lastPosition = glyph.IndicPosition;
                }
            }
            int last = Math.Min(baseIndex, end - 1);
            for (var index = last + 1; index < end; index++)
            {
                if (IsIndicConsonant(_glyphs[index]))
                {
                    for (var mark = last + 1; mark < index; mark++)
                    {
                        GlyphRecord glyph = _glyphs[mark];
                        if (glyph.IndicPosition < IndicShapingData.PositionSyllableModifierVedic)
                        {
                            glyph.IndicPosition = _glyphs[index].IndicPosition;
                            _glyphs[mark] = glyph;
                        }
                    }
                    last = index;
                }
                else if (_glyphs[index].IndicCategory is IndicShapingData.Matra or IndicShapingData.MatraPost) last = index;
            }
        }

        private void StableSortIndic(int start, int end)
        {
            for (var index = start + 1; index < end; index++)
            {
                GlyphRecord value = _glyphs[index];
                int insertion = index;
                while (insertion > start && _glyphs[insertion - 1].IndicPosition > value.IndicPosition)
                {
                    _glyphs[insertion] = _glyphs[insertion - 1];
                    insertion--;
                }
                _glyphs[insertion] = value;
            }
        }

        private int FindIndicBase(int start, int end)
        {
            for (var index = start; index < end; index++)
                if (_glyphs[index].IndicPosition == IndicShapingData.PositionBaseConsonant) return index;
            return end;
        }

        private void SetupIndicFeatureMasks(
            int start,
            int end,
            int baseIndex,
            bool oldSpec,
            string script,
            SubstitutionPlan plan)
        {
            for (var index = start; index < end && _glyphs[index].IndicPosition == IndicShapingData.PositionRaToBecomeReph; index++)
                AddIndicMask(index, IndicRphfMask);
            byte preMask = IndicHalfMask;
            if (!oldSpec && GetIndicBelowMode(script) == IndicBelowMode.PreAndPost) preMask |= IndicBlwfMask;
            for (var index = start; index < baseIndex; index++) AddIndicMask(index, preMask);
            for (var index = baseIndex + 1; index < end; index++) AddIndicMask(index, IndicBlwfMask | IndicAbvfMask | IndicPstfMask);

            if (baseIndex + 2 < end)
            {
                for (var index = baseIndex + 1; index + 1 < end; index++)
                {
                    Span<ushort> probe = stackalloc ushort[2] { _glyphs[index].GlyphIndex, _glyphs[index + 1].GlyphIndex };
                    if (!WouldSubstitute(plan, "pref", probe)) continue;
                    AddIndicMask(index, IndicPrefMask);
                    AddIndicMask(index + 1, IndicPrefMask);
                    break;
                }
            }
            for (var index = start + 1; index < end; index++)
            {
                if (!IsIndicJoiner(_glyphs[index]) || _glyphs[index].IndicCategory != IndicShapingData.Zwnj) continue;
                for (var prior = index - 1; prior >= start; prior--)
                {
                    RemoveIndicMask(prior, IndicHalfMask);
                    if (IsIndicConsonant(_glyphs[prior])) break;
                }
            }
        }

        private void AddIndicMask(int index, byte mask)
        {
            GlyphRecord glyph = _glyphs[index];
            glyph.ScriptFeatureMask |= mask;
            _glyphs[index] = glyph;
        }

        private void RemoveIndicMask(int index, byte mask)
        {
            GlyphRecord glyph = _glyphs[index];
            glyph.ScriptFeatureMask &= (byte)~mask;
            _glyphs[index] = glyph;
        }

        public void FinalReorderIndic(string script)
        {
            for (var start = 0; start < _glyphs.Count;)
            {
                byte syllable = _glyphs[start].UseSyllable;
                int end = start + 1;
                while (end < _glyphs.Count && _glyphs[end].UseSyllable == syllable) end++;
                FinalReorderIndicSyllable(start, end, script);
                start = end;
            }
        }

        private void FinalReorderIndicSyllable(int start, int end, string script)
        {
            bool reordered = false;
            ushort viramaGlyph = _font.GetGlyphIndex(GetIndicVirama(script));
            if (viramaGlyph != 0)
            {
                for (var index = start; index < end; index++)
                {
                    GlyphRecord glyph = _glyphs[index];
                    if (glyph.GlyphIndex != viramaGlyph || glyph.Ligated == 0 || glyph.Multiplied == 0) continue;
                    glyph.IndicCategory = IndicShapingData.Halant;
                    glyph.Ligated = 0;
                    glyph.Multiplied = 0;
                    _glyphs[index] = glyph;
                }
            }
            bool tryPrebase = false;
            for (var index = start; index < end; index++)
                if ((_glyphs[index].ScriptFeatureMask & IndicPrefMask) != 0)
                {
                    tryPrebase = true;
                    break;
                }
            int baseIndex = start;
            while (baseIndex < end && _glyphs[baseIndex].IndicPosition < IndicShapingData.PositionBaseConsonant) baseIndex++;
            if (tryPrebase && baseIndex + 1 < end)
            {
                for (var index = baseIndex + 1; index < end; index++)
                {
                    if ((_glyphs[index].ScriptFeatureMask & IndicPrefMask) == 0) continue;
                    if (!(_glyphs[index].Substituted != 0 && _glyphs[index].Ligated != 0 &&
                          _glyphs[index].Multiplied == 0))
                    {
                        baseIndex = index;
                        while (baseIndex < end && IsIndicHalant(_glyphs[baseIndex])) baseIndex++;
                        if (baseIndex < end)
                        {
                            GlyphRecord replacementBase = _glyphs[baseIndex];
                            replacementBase.IndicPosition = IndicShapingData.PositionBaseConsonant;
                            _glyphs[baseIndex] = replacementBase;
                        }
                        tryPrebase = false;
                    }
                    break;
                }
            }
            if (script == "mlym" && baseIndex < end)
            {
                for (var index = baseIndex + 1; index < end; index++)
                {
                    while (index < end && IsIndicJoiner(_glyphs[index])) index++;
                    if (index == end || !IsIndicHalant(_glyphs[index])) break;
                    index++;
                    while (index < end && IsIndicJoiner(_glyphs[index])) index++;
                    if (index < end && IsIndicConsonant(_glyphs[index]) &&
                        _glyphs[index].IndicPosition == IndicShapingData.PositionBelowConsonant)
                    {
                        baseIndex = index;
                        GlyphRecord replacementBase = _glyphs[baseIndex];
                        replacementBase.IndicPosition = IndicShapingData.PositionBaseConsonant;
                        _glyphs[baseIndex] = replacementBase;
                    }
                }
            }
            if (baseIndex < end && baseIndex > start &&
                _glyphs[baseIndex].IndicPosition > IndicShapingData.PositionBaseConsonant) baseIndex--;
            if (baseIndex == end && end > start && _glyphs[end - 1].IndicCategory == IndicShapingData.Zwj) baseIndex--;
            while (baseIndex > start && baseIndex < end &&
                   (_glyphs[baseIndex].Ligated == 0 && _glyphs[baseIndex].IndicCategory == IndicShapingData.Nukta ||
                    IsIndicHalant(_glyphs[baseIndex]))) baseIndex--;

            if (start + 1 < end && start < baseIndex)
            {
                int destination = baseIndex == end ? baseIndex - 2 : baseIndex - 1;
                if (script is not ("mlym" or "taml"))
                {
                    while (true)
                    {
                        while (destination > start && _glyphs[destination].IndicCategory is not
                               (IndicShapingData.Matra or IndicShapingData.MatraPost or IndicShapingData.Halant)) destination--;
                        if (!IsIndicHalant(_glyphs[destination]) ||
                            _glyphs[destination].IndicPosition == IndicShapingData.PositionPreMatra)
                        {
                            destination = start;
                            break;
                        }
                        if (destination + 1 < end &&
                            _glyphs[destination + 1].IndicCategory == IndicShapingData.Zwj && destination > start)
                        {
                            destination--;
                            continue;
                        }
                        break;
                    }
                }
                if (destination > start && _glyphs[destination].IndicPosition != IndicShapingData.PositionPreMatra)
                {
                    for (var index = destination; index > start; index--)
                    {
                        if (_glyphs[index - 1].IndicPosition != IndicShapingData.PositionPreMatra) continue;
                        GlyphRecord matra = _glyphs[index - 1];
                        _glyphs.RemoveAt(index - 1);
                        _glyphs.Insert(destination, matra);
                        MergeCluster(destination, Math.Min(end, baseIndex + 1));
                        reordered = true;
                        destination--;
                    }
                }
                else
                {
                    for (var index = start; index < baseIndex; index++)
                        if (_glyphs[index].IndicPosition == IndicShapingData.PositionPreMatra)
                        {
                            MergeCluster(index, Math.Min(end, baseIndex + 1));
                            break;
                        }
                }
            }

            if (start + 1 < end &&
                _glyphs[start].IndicPosition == IndicShapingData.PositionRaToBecomeReph &&
                ((_glyphs[start].IndicCategory == IndicShapingData.Repha) ^
                 (_glyphs[start].Ligated != 0 && _glyphs[start].Multiplied == 0)))
            {
                int destination = FindIndicRephDestination(start, end, baseIndex, script);
                MergeCluster(start, destination + 1);
                GlyphRecord reph = _glyphs[start];
                _glyphs.RemoveAt(start);
                _glyphs.Insert(destination, reph);
                reordered = true;
                if (start < baseIndex && baseIndex <= destination) baseIndex--;
            }

            if (tryPrebase && baseIndex + 1 < end)
            {
                for (var index = baseIndex + 1; index < end; index++)
                {
                    if ((_glyphs[index].ScriptFeatureMask & IndicPrefMask) == 0) continue;
                    if (_glyphs[index].Ligated != 0 && _glyphs[index].Multiplied == 0)
                    {
                        int destination = baseIndex;
                        if (script is not ("mlym" or "taml"))
                        {
                            while (destination > start && _glyphs[destination - 1].IndicCategory is not
                                   (IndicShapingData.Matra or IndicShapingData.MatraPost or IndicShapingData.Halant))
                                destination--;
                        }
                        if (destination > start && IsIndicHalant(_glyphs[destination - 1]) &&
                            destination < end && IsIndicJoiner(_glyphs[destination])) destination++;
                        MergeCluster(destination, index + 1);
                        GlyphRecord prebase = _glyphs[index];
                        _glyphs.RemoveAt(index);
                        _glyphs.Insert(destination, prebase);
                        reordered = true;
                        if (destination <= baseIndex && baseIndex < index) baseIndex++;
                    }
                    break;
                }
            }

            if (reordered || _glyphs[start].IndicPosition == IndicShapingData.PositionPreMatra)
                MergeCluster(start, end);

            if (_glyphs[start].IndicPosition == IndicShapingData.PositionPreMatra &&
                (start == 0 || IsIndicWordBoundary(_glyphs[start - 1].CodePoint)))
                AddIndicMask(start, IndicInitMask);
        }

        private int FindIndicRephDestination(int start, int end, int baseIndex, string script)
        {
            byte desired = GetIndicRephPosition(script);
            if (desired != IndicShapingData.PositionAfterPost)
            {
                int explicitHalant = start + 1;
                while (explicitHalant < baseIndex && !IsIndicHalant(_glyphs[explicitHalant])) explicitHalant++;
                if (explicitHalant < baseIndex)
                {
                    if (explicitHalant + 1 < baseIndex && IsIndicJoiner(_glyphs[explicitHalant + 1])) explicitHalant++;
                    return explicitHalant;
                }
            }
            if (desired == IndicShapingData.PositionAfterMain)
            {
                int destination = baseIndex;
                while (destination + 1 < end &&
                       _glyphs[destination + 1].IndicPosition <= IndicShapingData.PositionAfterMain) destination++;
                if (destination < end) return destination;
            }
            if (desired == IndicShapingData.PositionAfterSub)
            {
                int destination = baseIndex;
                while (destination + 1 < end && _glyphs[destination + 1].IndicPosition is not
                       (IndicShapingData.PositionPostConsonant or IndicShapingData.PositionAfterPost or
                        IndicShapingData.PositionSyllableModifierVedic)) destination++;
                if (destination < end) return destination;
            }

            int finalDestination = end - 1;
            while (finalDestination > start &&
                   _glyphs[finalDestination].IndicPosition == IndicShapingData.PositionSyllableModifierVedic)
                finalDestination--;
            if (IsIndicHalant(_glyphs[finalDestination]))
            {
                for (var index = baseIndex + 1; index < finalDestination; index++)
                    if (_glyphs[index].IndicCategory is IndicShapingData.Matra or IndicShapingData.MatraPost)
                    {
                        finalDestination--;
                        break;
                    }
            }
            return finalDestination;
        }

        private static byte GetIndicRephPosition(string script) => script switch
        {
            "beng" => IndicShapingData.PositionAfterSub,
            "guru" => IndicShapingData.PositionBeforeSub,
            "orya" or "mlym" => IndicShapingData.PositionAfterMain,
            "taml" or "telu" or "knda" => IndicShapingData.PositionAfterPost,
            _ => IndicShapingData.PositionBeforePost
        };

        private static bool IsIndicHalant(GlyphRecord glyph) =>
            glyph.Ligated == 0 && glyph.IndicCategory == IndicShapingData.Halant;

        private static bool IsIndicWordBoundary(uint codePoint)
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(new Rune((int)codePoint));
            return category is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator or UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or
                UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or
                UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or
                UnicodeCategory.OtherPunctuation;
        }

        private static bool IsIndicConsonant(GlyphRecord glyph) => glyph.Ligated == 0 && glyph.IndicCategory is
            IndicShapingData.Consonant or IndicShapingData.ConsonantWithStacker or IndicShapingData.Ra or
            IndicShapingData.ConsonantMedial or IndicShapingData.Vowel or IndicShapingData.Placeholder or
            IndicShapingData.DottedCircle;

        private static bool IsIndicJoiner(GlyphRecord glyph) => glyph.Ligated == 0 &&
            glyph.IndicCategory is IndicShapingData.Zwj or IndicShapingData.Zwnj;

        private static uint GetIndicVirama(string script) => script switch
        {
            "deva" => 0x094D, "beng" => 0x09CD, "guru" => 0x0A4D, "gujr" => 0x0ACD,
            "orya" => 0x0B4D, "taml" => 0x0BCD, "telu" => 0x0C4D, "knda" => 0x0CCD,
            "mlym" => 0x0D4D, _ => 0
        };

        private static IndicRephMode GetIndicRephMode(string script) => script switch
        {
            "telu" => IndicRephMode.Explicit,
            "mlym" => IndicRephMode.Logical,
            _ => IndicRephMode.Implicit
        };

        private static IndicBelowMode GetIndicBelowMode(string script) => script is "telu" or "knda"
            ? IndicBelowMode.PostOnly
            : IndicBelowMode.PreAndPost;

        public void PrepareUseShaping(bool enabled, string unicodeScript)
        {
            if (!enabled)
            {
                return;
            }

            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                glyph.ScriptShaper = ScriptShaperUse;
                glyph.UseCategory = UseShapingData.GetCategory(glyph.CodePoint);
                _glyphs[index] = glyph;
            }

            FindUseSyllables();
            AssignUseRephaEligibility();
            if (!UsesArabicJoining(unicodeScript))
            {
                AssignUseTopographicalActions();
            }
        }

        public void ReorderUseShaping(bool enabled)
        {
            if (!enabled)
            {
                return;
            }

            InsertBrokenUseDottedCircles();
            for (var start = 0; start < _glyphs.Count;)
            {
                byte syllable = _glyphs[start].UseSyllable;
                int end = start + 1;
                while (end < _glyphs.Count && _glyphs[end].UseSyllable == syllable)
                {
                    end++;
                }
                ReorderUseSyllable(start, end, (byte)(syllable & 0x0F));
                start = end;
            }
        }

        public void ClearSubstitutionFlags()
        {
            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                glyph.Substituted = 0;
                _glyphs[index] = glyph;
            }
        }

        public void RecordUseRepha()
        {
            for (var start = 0; start < _glyphs.Count;)
            {
                byte syllable = _glyphs[start].UseSyllable;
                int end = start + 1;
                while (end < _glyphs.Count && _glyphs[end].UseSyllable == syllable) end++;
                for (var index = start; index < end && _glyphs[index].UseRphfEligible != 0; index++)
                {
                    if (_glyphs[index].Substituted == 0) continue;
                    GlyphRecord glyph = _glyphs[index];
                    glyph.UseCategory = UseShapingData.Repha;
                    _glyphs[index] = glyph;
                    break;
                }
                start = end;
            }
        }

        public void RecordUsePrebase()
        {
            for (var start = 0; start < _glyphs.Count;)
            {
                byte syllable = _glyphs[start].UseSyllable;
                int end = start + 1;
                while (end < _glyphs.Count && _glyphs[end].UseSyllable == syllable) end++;
                for (var index = start; index < end; index++)
                {
                    if (_glyphs[index].Substituted == 0) continue;
                    GlyphRecord glyph = _glyphs[index];
                    glyph.UseCategory = UseShapingData.VowelPre;
                    _glyphs[index] = glyph;
                    break;
                }
                start = end;
            }
        }

        private void AssignUseRephaEligibility()
        {
            for (var start = 0; start < _glyphs.Count;)
            {
                byte syllable = _glyphs[start].UseSyllable;
                int end = start + 1;
                while (end < _glyphs.Count && _glyphs[end].UseSyllable == syllable) end++;
                int limit = _glyphs[start].UseCategory == UseShapingData.Repha
                    ? 1
                    : Math.Min(3, end - start);
                for (var index = start; index < start + limit; index++)
                {
                    GlyphRecord glyph = _glyphs[index];
                    glyph.UseRphfEligible = 1;
                    _glyphs[index] = glyph;
                }
                start = end;
            }
        }

        private void FindUseSyllables()
        {
            int[] indices = ArrayPool<int>.Shared.Rent(_glyphs.Count + 1);
            try
            {
                int machineCount = 0;
                for (var index = 0; index < _glyphs.Count; index++)
                {
                    byte category = _glyphs[index].UseCategory;
                    if (category == UseShapingData.Cgj)
                    {
                        continue;
                    }
                    if (category == UseShapingData.Zwnj)
                    {
                        int following = index + 1;
                        while (following < _glyphs.Count && _glyphs[following].UseCategory == UseShapingData.Cgj)
                        {
                            following++;
                        }
                        if (following < _glyphs.Count && IsUnicodeMark(_glyphs[following].CodePoint))
                        {
                            continue;
                        }
                    }
                    indices[machineCount++] = index;
                }
                indices[machineCount] = _glyphs.Count;
                RunUseSyllableMachine(indices.AsSpan(0, machineCount + 1), machineCount);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(indices);
            }
        }

        private void RunUseSyllableMachine(ReadOnlySpan<int> indices, int count)
        {
            if (count == 0)
            {
                return;
            }

            int state = UseSyllableMachineData.StartState;
            int position = 0;
            int tokenStart = -1;
            int tokenEnd = -1;
            int pendingAction = 0;
            byte serial = 1;

            while (true)
            {
                int transition;
                if (position == count)
                {
                    transition = UseSyllableMachineData.GetEofTransition(state);
                    if (transition < 0)
                    {
                        break;
                    }
                }
                else
                {
                    if (UseSyllableMachineData.GetFromStateAction(state) == 3)
                    {
                        tokenStart = position;
                    }
                    transition = UseSyllableMachineData.GetTransition(
                        state,
                        _glyphs[indices[position]].UseCategory);
                }

                state = UseSyllableMachineData.GetTarget(transition);
                int action = UseSyllableMachineData.GetAction(transition);
                switch (action)
                {
                    case 7:
                        tokenEnd = position + 1;
                        break;
                    case 16: tokenEnd = position + 1; AssignUseSyllable(indices, tokenStart, tokenEnd, UseViramaTerminated, ref serial); break;
                    case 14: tokenEnd = position + 1; AssignUseSyllable(indices, tokenStart, tokenEnd, UseSakotTerminated, ref serial); break;
                    case 12: tokenEnd = position + 1; AssignUseSyllable(indices, tokenStart, tokenEnd, UseStandard, ref serial); break;
                    case 20: tokenEnd = position + 1; AssignUseSyllable(indices, tokenStart, tokenEnd, UseNumberJoinerTerminated, ref serial); break;
                    case 18: tokenEnd = position + 1; AssignUseSyllable(indices, tokenStart, tokenEnd, UseNumeral, ref serial); break;
                    case 10: tokenEnd = position + 1; AssignUseSyllable(indices, tokenStart, tokenEnd, UseSymbol, ref serial); break;
                    case 25: tokenEnd = position + 1; AssignUseSyllable(indices, tokenStart, tokenEnd, UseHieroglyph, ref serial); break;
                    case 5: tokenEnd = position + 1; AssignUseSyllable(indices, tokenStart, tokenEnd, UseBroken, ref serial); break;
                    case 4: tokenEnd = position + 1; AssignUseSyllable(indices, tokenStart, tokenEnd, UseNonCluster, ref serial); break;
                    case 15: tokenEnd = position; position--; AssignUseSyllable(indices, tokenStart, tokenEnd, UseViramaTerminated, ref serial); break;
                    case 13: tokenEnd = position; position--; AssignUseSyllable(indices, tokenStart, tokenEnd, UseSakotTerminated, ref serial); break;
                    case 11: tokenEnd = position; position--; AssignUseSyllable(indices, tokenStart, tokenEnd, UseStandard, ref serial); break;
                    case 19: tokenEnd = position; position--; AssignUseSyllable(indices, tokenStart, tokenEnd, UseNumberJoinerTerminated, ref serial); break;
                    case 17: tokenEnd = position; position--; AssignUseSyllable(indices, tokenStart, tokenEnd, UseNumeral, ref serial); break;
                    case 9: tokenEnd = position; position--; AssignUseSyllable(indices, tokenStart, tokenEnd, UseSymbol, ref serial); break;
                    case 24: tokenEnd = position; position--; AssignUseSyllable(indices, tokenStart, tokenEnd, UseHieroglyph, ref serial); break;
                    case 21: tokenEnd = position; position--; AssignUseSyllable(indices, tokenStart, tokenEnd, UseBroken, ref serial); break;
                    case 23: tokenEnd = position; position--; AssignUseSyllable(indices, tokenStart, tokenEnd, UseNonCluster, ref serial); break;
                    case 1:
                        position = tokenEnd - 1;
                        AssignUseSyllable(indices, tokenStart, tokenEnd, UseSymbol, ref serial);
                        break;
                    case 22:
                        position = tokenEnd - 1;
                        AssignUseSyllable(
                            indices,
                            tokenStart,
                            tokenEnd,
                            pendingAction == 9 ? UseBroken : UseNonCluster,
                            ref serial);
                        break;
                    case 6:
                        tokenEnd = position + 1;
                        pendingAction = 8;
                        break;
                    case 8:
                        tokenEnd = position + 1;
                        pendingAction = 9;
                        break;
                }

                if (UseSyllableMachineData.GetToStateAction(state) == 2)
                {
                    tokenStart = -1;
                }
                position++;
                if (position < 0 || position > count)
                {
                    break;
                }
            }
        }

        private void AssignUseSyllable(
            ReadOnlySpan<int> indices,
            int tokenStart,
            int tokenEnd,
            byte type,
            ref byte serial)
        {
            if (tokenStart < 0 || tokenEnd < tokenStart)
            {
                return;
            }
            int start = indices[tokenStart];
            int end = indices[tokenEnd];
            byte value = (byte)(serial << 4 | type);
            for (var index = start; index < end; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                glyph.UseSyllable = value;
                _glyphs[index] = glyph;
            }
            serial++;
            if (serial == 16)
            {
                serial = 1;
            }
        }

        private void AssignUseTopographicalActions()
        {
            int previousStart = 0;
            byte previousForm = None;
            for (var start = 0; start < _glyphs.Count;)
            {
                byte syllable = _glyphs[start].UseSyllable;
                int end = start + 1;
                while (end < _glyphs.Count && _glyphs[end].UseSyllable == syllable)
                {
                    end++;
                }
                byte type = (byte)(syllable & 0x0F);
                if (type is UseHieroglyph or UseNonCluster)
                {
                    previousForm = None;
                }
                else
                {
                    bool joins = previousForm is Final or Isolated;
                    if (joins)
                    {
                        previousForm = previousForm == Final ? Medial : Initial;
                        SetArabicAction(previousStart, start, previousForm);
                    }
                    previousForm = joins ? Final : Isolated;
                    SetArabicAction(start, end, previousForm);
                }
                previousStart = start;
                start = end;
            }
        }

        private void SetArabicAction(int start, int end, byte action)
        {
            for (var index = start; index < end; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                glyph.ArabicAction = action;
                _glyphs[index] = glyph;
            }
        }

        private void InsertBrokenUseDottedCircles()
        {
            if ((_bufferFlags & ShapingBufferFlags.DoNotInsertDottedCircle) != 0) return;
            ushort dottedCircleGlyph = _font.GetGlyphIndex(0x25CC);
            if (dottedCircleGlyph == 0)
            {
                return;
            }
            byte previousSyllable = 0;
            for (var index = 0; index < _glyphs.Count; index++)
            {
                GlyphRecord current = _glyphs[index];
                if (current.UseSyllable == previousSyllable || (current.UseSyllable & 0x0F) != UseBroken)
                {
                    previousSyllable = current.UseSyllable;
                    continue;
                }
                previousSyllable = current.UseSyllable;
                while (index < _glyphs.Count &&
                       _glyphs[index].UseSyllable == previousSyllable &&
                       _glyphs[index].UseCategory == UseShapingData.Repha)
                {
                    index++;
                }
                GlyphRecord dottedCircle = new(dottedCircleGlyph, current.Cluster, 0x25CC)
                {
                    ScriptShaper = ScriptShaperUse,
                    UseCategory = UseShapingData.Base,
                    UseSyllable = previousSyllable,
                    ArabicAction = current.ArabicAction
                };
                _glyphs.Insert(index, dottedCircle);
            }
        }

        private void ReorderUseSyllable(int start, int end, byte type)
        {
            if (type is not (UseViramaTerminated or UseSakotTerminated or UseStandard or UseSymbol or UseBroken))
            {
                return;
            }

            if (_glyphs[start].UseCategory == UseShapingData.Repha && end - start > 1)
            {
                for (var index = start + 1; index < end; index++)
                {
                    bool postBase = IsUsePostBase(_glyphs[index].UseCategory) || IsUseHalant(_glyphs[index]);
                    if (postBase || index == end - 1)
                    {
                        int destination = postBase ? index - 1 : index;
                        MergeCluster(start, destination + 1);
                        GlyphRecord repha = _glyphs[start];
                        _glyphs.RemoveAt(start);
                        _glyphs.Insert(destination, repha);
                        break;
                    }
                }
            }

            int target = start;
            for (var index = start; index < end; index++)
            {
                GlyphRecord glyph = _glyphs[index];
                if (IsUseHalant(glyph))
                {
                    target = index + 1;
                }
                else if (glyph.UseCategory is UseShapingData.VowelPre or UseShapingData.VowelModifierPre &&
                         glyph.MultipleSubstitutionComponent == 0 &&
                         target < index)
                {
                    MergeCluster(target, index + 1);
                    _glyphs.RemoveAt(index);
                    _glyphs.Insert(target, glyph);
                }
            }
        }

        private static bool IsUsePostBase(byte category) => category is
            UseShapingData.FinalAbove or UseShapingData.FinalBelow or UseShapingData.FinalPost or
            UseShapingData.FinalModifierAbove or UseShapingData.FinalModifierBelow or UseShapingData.FinalModifierPost or
            UseShapingData.MedialAbove or UseShapingData.MedialBelow or UseShapingData.MedialPost or UseShapingData.MedialPre or
            UseShapingData.VowelAbove or UseShapingData.VowelBelow or UseShapingData.VowelPost or UseShapingData.VowelPre or
            UseShapingData.VowelModifierAbove or UseShapingData.VowelModifierBelow or UseShapingData.VowelModifierPost or
            UseShapingData.VowelModifierPre;

        private static bool IsUseHalant(GlyphRecord glyph) =>
            glyph.UseCategory is UseShapingData.Halant or UseShapingData.HalantOrVowelModifier or UseShapingData.InvisibleStacker &&
            glyph.Ligated == 0;

        private static bool IsUnicodeMark(uint codePoint)
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(new Rune((int)codePoint));
            return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
        }

        private static bool UsesArabicJoining(string script) => script is
            "adlm" or "arab" or "chrs" or "rohg" or "mand" or "mani" or "mong" or
            "nkoo" or "ougr" or "phag" or "phlp" or "sogd" or "syrc";

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
            GlyphRecord glyph = _glyphs[index];
            if (options.GetFeatureValue(tag, glyph.Cluster) == 0) return false;
            if (glyph.ScriptShaper == ScriptShaperHangul)
            {
                return tag switch
                {
                    "calt" => options.IsFeatureExplicitAt("calt", glyph.Cluster),
                    "ljmo" => (glyph.ScriptFeatureMask & HangulLjmoMask) != 0,
                    "vjmo" => (glyph.ScriptFeatureMask & HangulVjmoMask) != 0,
                    "tjmo" => (glyph.ScriptFeatureMask & HangulTjmoMask) != 0,
                    _ => true
                };
            }
            if (glyph.ScriptShaper == ScriptShaperIndic)
            {
                return tag switch
                {
                    "rphf" => (glyph.ScriptFeatureMask & IndicRphfMask) != 0,
                    "pref" => (glyph.ScriptFeatureMask & IndicPrefMask) != 0,
                    "blwf" => (glyph.ScriptFeatureMask & IndicBlwfMask) != 0,
                    "abvf" => (glyph.ScriptFeatureMask & IndicAbvfMask) != 0,
                    "half" => (glyph.ScriptFeatureMask & IndicHalfMask) != 0,
                    "pstf" => (glyph.ScriptFeatureMask & IndicPstfMask) != 0,
                    "init" => (glyph.ScriptFeatureMask & IndicInitMask) != 0,
                    _ => true
                };
            }
            byte action = glyph.ArabicAction;
            bool explicitlyEnabled = options.IsFeatureExplicitAt(tag, glyph.Cluster);
            return tag switch
            {
                "frac" => explicitlyEnabled || glyph.FractionAction != FractionNone,
                "numr" => explicitlyEnabled || glyph.FractionAction == FractionNumerator,
                "dnom" => explicitlyEnabled || glyph.FractionAction == FractionDenominator,
                "pref" => glyph.ScriptShaper != ScriptShaperKhmer || (glyph.ScriptFeatureMask & KhmerPrefMask) != 0,
                "blwf" or "abvf" or "pstf" => glyph.ScriptShaper != ScriptShaperKhmer || (glyph.ScriptFeatureMask & KhmerPostBaseMask) != 0,
                "cfar" => glyph.ScriptShaper == ScriptShaperKhmer && (glyph.ScriptFeatureMask & KhmerCfarMask) != 0,
                "rphf" => glyph.ScriptShaper != ScriptShaperUse || glyph.UseRphfEligible != 0,
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
                if (_restrictLookupToSyllable && glyph.UseSyllable != _lookupSyllable)
                {
                    return -1;
                }
                // ZWNJ remains a shaping barrier. CGJ blocks canonical mark
                // reordering, but OpenType lookup matching skips it just like
                // HarfBuzz skips the temporary .notdef buffer item.
                if (!IsDefaultIgnorable(glyph.CodePoint) || IsVisibleDefaultIgnorable(glyph) ||
                    glyph.CodePoint == 0x200C || _manualJoiners && glyph.CodePoint == 0x200D ||
                    IsMongolianShapingControl(glyph.CodePoint) ||
                    IsUnicodeTagCharacter(glyph.CodePoint))
                {
                    if (!IsGlyphClassIgnored(index, lookupFlags)) return index;
                }
                index++;
            }
            return -1;
        }

        public int NextContextIndex(int index, ushort lookupFlags)
        {
            while (index < _glyphs.Count)
            {
                GlyphRecord glyph = _glyphs[index];
                if (_restrictLookupToSyllable && glyph.UseSyllable != _lookupSyllable) return -1;
                if (!IsContextLookupIgnored(index, lookupFlags)) return index;
                index++;
            }
            return -1;
        }

        public int PreviousContextIndex(int index, ushort lookupFlags)
        {
            while (index >= 0)
            {
                GlyphRecord glyph = _glyphs[index];
                if (_restrictLookupToSyllable && glyph.UseSyllable != _lookupSyllable) return -1;
                if (!IsContextLookupIgnored(index, lookupFlags)) return index;
                index--;
            }
            return -1;
        }

        public int NextLigatureComponentIndex(int index, ushort lookupFlags, ushort expectedGlyph)
        {
            while (index < _glyphs.Count)
            {
                GlyphRecord glyph = _glyphs[index];
                if (_restrictLookupToSyllable && glyph.UseSyllable != _lookupSyllable) return -1;
                if (IsDefaultIgnorable(glyph.CodePoint) && !IsVisibleDefaultIgnorable(glyph))
                {
                    // CGJ is skipped by contextual matching, but remains an
                    // explicit component boundary for ligature formation.
                    if (glyph.GlyphIndex == expectedGlyph || glyph.CodePoint is 0x034F or 0x200C ||
                        _manualJoiners && glyph.CodePoint == 0x200D ||
                        IsMongolianShapingControl(glyph.CodePoint) ||
                        IsUnicodeTagCharacter(glyph.CodePoint)) return index;
                    index++;
                    continue;
                }
                if (!IsLookupIgnored(index, lookupFlags)) return index;
                index++;
            }
            return -1;
        }

        public int GetVisibleSequenceIndex(int position, int sequenceIndex, ushort lookupFlags)
        {
            int result = position;
            for (var index = 0; index < sequenceIndex; index++)
            {
                result = NextVisibleIndex(result + 1, lookupFlags);
                if (result < 0) return -1;
            }
            return result;
        }

        private bool IsLookupIgnored(int index, ushort lookupFlags)
        {
            GlyphRecord glyph = _glyphs[index];
            if (IsDefaultIgnorable(glyph.CodePoint) && !IsVisibleDefaultIgnorable(glyph) && glyph.CodePoint != 0x200C &&
                !(_manualJoiners && glyph.CodePoint == 0x200D) &&
                !IsMongolianShapingControl(glyph.CodePoint) &&
                !IsUnicodeTagCharacter(glyph.CodePoint)) return true;
            return IsGlyphClassIgnored(index, lookupFlags);
        }

        private bool IsContextLookupIgnored(int index, ushort lookupFlags)
        {
            GlyphRecord glyph = _glyphs[index];
            if (IsDefaultIgnorable(glyph.CodePoint) && !IsVisibleDefaultIgnorable(glyph) &&
                !(_manualJoiners && glyph.CodePoint == 0x200C) &&
                !IsMongolianShapingControl(glyph.CodePoint) &&
                !IsUnicodeTagCharacter(glyph.CodePoint)) return true;
            return IsGlyphClassIgnored(index, lookupFlags);
        }

        private static bool IsVisibleDefaultIgnorable(GlyphRecord glyph) =>
            glyph.Substituted != 0 ||
            glyph.ScriptShaper == ScriptShaperHangul && glyph.ScriptFeatureMask != 0;

        private bool IsGlyphClassIgnored(int index, ushort lookupFlags)
        {
            GlyphRecord glyph = _glyphs[index];
            GlyphClassKind glyphClass = GetGlyphClassKind(glyph);
            if (glyphClass == GlyphClassKind.Mark)
            {
                if ((lookupFlags & 0x0010) != 0 &&
                    (_markFilteringCoverage < 0 ||
                     FindCoverage(_gdefTable.Span, _markFilteringCoverage, glyph.GlyphIndex) < 0))
                    return true;
                int requiredMarkClass = lookupFlags >> 8;
                if (requiredMarkClass != 0 && GetMarkAttachmentClass(glyph.GlyphIndex) != requiredMarkClass)
                    return true;
            }
            return glyphClass switch
            {
                GlyphClassKind.Base => (lookupFlags & 0x0002) != 0,
                GlyphClassKind.Ligature => (lookupFlags & 0x0004) != 0,
                GlyphClassKind.Mark => (lookupFlags & 0x0008) != 0,
                _ => false
            };
        }

        private GlyphClassKind GetGlyphClassKind(GlyphRecord record)
        {
            ReadOnlySpan<byte> data = _gdefTable.Span;
            if (CanRead(data, 4, 2))
            {
                int classDefOffset = ReadU16(data, 4);
                if (classDefOffset != 0)
                    return (GlyphClassKind)OpenTypeTextShaper.GetGlyphClass(data, classDefOffset, record.GlyphIndex);
            }
            if (IsUnicodeMark(record.CodePoint)) return GlyphClassKind.Mark;
            return _typeface?.GetGlyph(record.GlyphIndex).GlyphClass ?? GlyphClassKind.Base;
        }

        private int GetMarkAttachmentClass(ushort glyph)
        {
            ReadOnlySpan<byte> data = _gdefTable.Span;
            if (!CanRead(data, 10, 2)) return 0;
            int classDefOffset = ReadU16(data, 10);
            return classDefOffset == 0 ? 0 : OpenTypeTextShaper.GetGlyphClass(data, classDefOffset, glyph);
        }

        public int SetMarkFilteringSet(ushort setIndex)
        {
            int previous = _markFilteringCoverage;
            _markFilteringCoverage = -1;
            if (setIndex == ushort.MaxValue) return previous;
            ReadOnlySpan<byte> data = _gdefTable.Span;
            if (!CanRead(data, 12, 2)) return previous;
            int setsOffset = ReadU16(data, 12);
            if (setsOffset == 0 || !CanRead(data, setsOffset, 4) || ReadU16(data, setsOffset) != 1)
                return previous;
            int setCount = ReadU16(data, setsOffset + 2);
            int pointer = setsOffset + 4 + setIndex * 4;
            if (setIndex >= setCount || !CanRead(data, pointer, 4)) return previous;
            uint relative = ReadU32(data, pointer);
            if (relative <= int.MaxValue && CanRead(data, setsOffset + (int)relative, 4))
                _markFilteringCoverage = setsOffset + (int)relative;
            return previous;
        }

        public void RestoreMarkFilteringCoverage(int coverage) =>
            _markFilteringCoverage = coverage;

        public bool TryEnterNestedLookup()
        {
            if (_nestedLookupDepth >= MaximumLookupNestingDepth) return false;
            _nestedLookupDepth++;
            return true;
        }

        public void ExitNestedLookup() => _nestedLookupDepth--;

        public void ReplaceLigature(ReadOnlySpan<int> componentIndices, ushort ligatureGlyph)
        {
            if (componentIndices.IsEmpty) return;
            GlyphRecord ligature = _glyphs[componentIndices[0]];
            ligature.GlyphIndex = ligatureGlyph;
            ligature.Substituted = 1;
            ligature.Ligated = 1;
            ligature.Multiplied = 0;
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
            ligature.Cluster = MergeCluster(first, last + 1);
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

        private static bool IsMongolianShapingControl(uint codePoint) =>
            codePoint is >= 0x180B and <= 0x180E;

        private static bool IsUnicodeTagCharacter(uint codePoint) =>
            codePoint is >= 0xE0000 and <= 0xE007F;

        public static GlyphSubstitutionBuffer Create(
            string text,
            TtfFont font,
            string script,
            ShapingClusterLevel clusterLevel,
            ShapingBufferFlags bufferFlags)
        {
            if (text.Length > MaximumShapedGlyphCount)
            {
                throw new InvalidOperationException($"Text exceeds the {MaximumShapedGlyphCount} glyph shaping limit.");
            }
            var glyphs = new List<GlyphRecord>(text.Length);
            bool textIsNormalized = text.IsNormalized(NormalizationForm.FormC);
            bool preserveUseMarkOrder = ResolveLayoutScript(font, script, out _);
            bool preserveDefaultIgnorableCluster = font.GetGlyphIndex(' ') != 0;
            int useCluster = 0;
            bool hasUseCluster = false;
            for (var graphemeStart = 0; graphemeStart < text.Length;)
            {
                int graphemeLength = StringInfo.GetNextTextElementLength(text.AsSpan(graphemeStart));
                int graphemeEnd = checked(graphemeStart + graphemeLength);
                ReadOnlySpan<char> originalGrapheme = text.AsSpan(graphemeStart, graphemeLength);
                string? normalizedGrapheme = textIsNormalized || preserveUseMarkOrder || script == "hang" ||
                    PreservesIndicComposite(originalGrapheme, script)
                    ? null
                    : text.Substring(graphemeStart, graphemeLength).Normalize(NormalizationForm.FormC);
                ReadOnlySpan<char> grapheme = normalizedGrapheme is null
                    ? originalGrapheme
                    : normalizedGrapheme.AsSpan();
                Rune.DecodeFromUtf16(grapheme, out Rune firstRune, out _);
                bool separateClusters = IsPrepend(firstRune.Value);
                int indicCluster = graphemeStart;
                uint previousCodePoint = 0;
                bool splitAfterIndicZwnj = false;
                bool hasIndicSplitMatra = false;
                for (var index = 0; index < grapheme.Length;)
                {
                    int appendedStart = glyphs.Count;
                    Rune.DecodeFromUtf16(grapheme[index..], out Rune rune, out int consumed);
                    uint followingCodePoint = 0;
                    if (index + consumed < grapheme.Length &&
                        Rune.DecodeFromUtf16(grapheme[(index + consumed)..], out Rune followingRune, out _) == OperationStatus.Done)
                        followingCodePoint = (uint)followingRune.Value;
                    if (splitAfterIndicZwnj)
                    {
                        indicCluster = graphemeStart + index;
                        splitAfterIndicZwnj = false;
                    }
                    if (IsIndicShaperScript(script) &&
                        (rune.Value == 0x200C || rune.Value == 0x200D &&
                         previousCodePoint != GetIndicVirama(script) && followingCodePoint != GetIndicVirama(script)))
                    {
                        indicCluster = graphemeStart + index;
                        splitAfterIndicZwnj = rune.Value == 0x200C &&
                            followingCodePoint != 0 && !IsUnicodeMark(followingCodePoint);
                    }
                    else if (script == "khmr" &&
                             (rune.Value is 0x200C or 0x200D ||
                              previousCodePoint == 0x17D2 && IsKhmerBaseCategory((uint)rune.Value)))
                    {
                        indicCluster = graphemeStart + index;
                    }
                    bool characterClusters = clusterLevel is
                        ShapingClusterLevel.MonotoneCharacters or ShapingClusterLevel.Characters;
                    int cluster = characterClusters
                        ? graphemeStart + index
                        : (separateClusters || script == "hang") && normalizedGrapheme is null
                            ? graphemeStart + index
                            : IsIndicShaperScript(script) || script == "khmr" ? indicCluster : graphemeStart;
                    if (preserveUseMarkOrder)
                    {
                        if (preserveDefaultIgnorableCluster && rune.Value is 0x200C or 0x200D)
                            cluster = graphemeStart + index;
                        if (IsUnicodeMark((uint)rune.Value) && hasUseCluster)
                        {
                            cluster = useCluster;
                        }
                        else
                        {
                            useCluster = cluster;
                            hasUseCluster = true;
                        }
                    }
                    if (rune.Value == 0x200D && glyphs.Count > 0)
                        cluster = glyphs[^1].Cluster;
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
                    bool appendedSplitMatra = TryAppendIndicSplitMatra(glyphs, font, rune, cluster, script);
                    if (!appendedSplitMatra)
                    {
                        AppendNormalizedRune(glyphs, font, rune, cluster);
                    }
                    else if (!hasIndicSplitMatra)
                    {
                        for (int appended = appendedStart; appended < glyphs.Count; appended++)
                        {
                            GlyphRecord glyph = glyphs[appended];
                            glyph.HasCharacterCluster = 0;
                            glyphs[appended] = glyph;
                        }
                        hasIndicSplitMatra = true;
                    }
                    for (int appended = appendedStart; appended < glyphs.Count; appended++)
                    {
                        GlyphRecord glyph = glyphs[appended];
                        glyph.GraphemeCluster = graphemeStart;
                        glyphs[appended] = glyph;
                    }
                    previousCodePoint = (uint)rune.Value;
                    index += consumed;
                }
                graphemeStart = graphemeEnd;
            }
            return new GlyphSubstitutionBuffer(glyphs, font, clusterLevel, bufferFlags);
        }

        private static bool IsKhmerBaseCategory(uint codePoint)
        {
            byte category = IndicShapingData.GetCategory(IndicShapingData.GetProperties(codePoint));
            return category is IndicShapingData.Consonant or IndicShapingData.Vowel or
                IndicShapingData.Ra or IndicShapingData.Placeholder or IndicShapingData.DottedCircle;
        }

        private static bool PreservesIndicComposite(ReadOnlySpan<char> grapheme, string script)
        {
            if (!IsIndicShaperScript(script)) return false;
            foreach (Rune rune in grapheme.EnumerateRunes())
                if (rune.Value is 0x0931 or 0x09DC or 0x09DD or 0x0B94) return true;
            return false;
        }

        private static bool TryAppendIndicSplitMatra(
            List<GlyphRecord> glyphs,
            TtfFont font,
            Rune rune,
            int cluster,
            string script)
        {
            if (!IsIndicShaperScript(script)) return false;
            string scalar = rune.ToString();
            string decomposed = scalar.Normalize(NormalizationForm.FormD);
            if (decomposed.Equals(scalar, StringComparison.Ordinal)) return false;
            Rune.DecodeFromUtf16(decomposed, out Rune first, out _);
            UnicodeCategory category = Rune.GetUnicodeCategory(first);
            if (category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark)) return false;
            int firstComponent = glyphs.Count;
            foreach (Rune component in decomposed.EnumerateRunes())
                AppendMappedRune(glyphs, font, component, cluster);
            for (int index = firstComponent; index < glyphs.Count; index++)
            {
                GlyphRecord glyph = glyphs[index];
                glyph.CharacterCluster = cluster;
                glyph.HasCharacterCluster = 1;
                glyphs[index] = glyph;
            }
            return true;
        }

        private static void AppendNormalizedRune(
            List<GlyphRecord> glyphs,
            TtfFont font,
            Rune rune,
            int cluster)
        {
            uint codePoint = (uint)rune.Value;
            ushort glyphIndex = font.GetGlyphIndex(codePoint);
            if (glyphIndex == 0 && codePoint == 0x2011)
            {
                codePoint = 0x2010;
                rune = new Rune((int)codePoint);
                glyphIndex = font.GetGlyphIndex(codePoint);
            }
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
            record.Substituted = 1;
            _glyphs[index] = record;
        }

        public void Replace(int index, int removeLen, ushort newGlyphIndex)
        {
            GlyphRecord record = _glyphs[index];
            record.GlyphIndex = newGlyphIndex;
            record.Substituted = 1;
            if (removeLen > 1) record.Ligated = 1;
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
            if (record.Ligated != 0 && newGlyphIndices.Length > 1)
            {
                int insertionIndex = index + 1;
                while (insertionIndex < _glyphs.Count &&
                       _glyphs[insertionIndex].LigatureComponent != byte.MaxValue)
                {
                    insertionIndex++;
                }
                GlyphRecord first = record;
                first.GlyphIndex = newGlyphIndices[0];
                first.Substituted = 1;
                first.MultipleSubstitutionComponent = 0;
                first.Multiplied = 1;
                _glyphs[index] = first;
                for (var replacementIndex = 1; replacementIndex < newGlyphIndices.Length; replacementIndex++)
                {
                    GlyphRecord replacement = record;
                    replacement.GlyphIndex = newGlyphIndices[replacementIndex];
                    replacement.Substituted = 1;
                    replacement.MultipleSubstitutionComponent = checked((byte)Math.Min(replacementIndex, byte.MaxValue));
                    replacement.Multiplied = 1;
                    _glyphs.Insert(insertionIndex++, replacement);
                }
                _contextMatchEnd = Math.Max(_contextMatchEnd, insertionIndex);
                return;
            }
            _glyphs.RemoveAt(index);
            for (var replacementIndex = newGlyphIndices.Length - 1; replacementIndex >= 0; replacementIndex--)
            {
                GlyphRecord replacement = record;
                replacement.GlyphIndex = newGlyphIndices[replacementIndex];
                replacement.Substituted = 1;
                replacement.MultipleSubstitutionComponent = checked((byte)Math.Min(replacementIndex, byte.MaxValue));
                replacement.Multiplied = 1;
                _glyphs.Insert(index, replacement);
            }
        }
    }

    private sealed class GlyphPositionBuffer : IGlyphPositions
    {
        private GlyphRecord[] _glyphs;
        private readonly Typeface? _typeface;
        private readonly ReadOnlyMemory<byte> _gdefTable;
        private readonly ShapingDirection _direction;
        private int _markFilteringCoverage = -1;

        public GlyphPositionBuffer(
            GlyphSubstitutionBuffer substitutions,
            TtfFont font,
            ShapingDirection direction,
            bool zeroMarkAdvancesEarly,
            ShapingClusterLevel clusterLevel,
            ShapingBufferFlags bufferFlags)
        {
            _typeface = font.LayoutTypeface;
            _direction = direction;
            font.TryGetTable("GDEF", out _gdefTable);
            if (OpenTypeGdefPolicy.IsBlocklisted(
                    _gdefTable.Length,
                    GetTableLength(font, "GSUB"),
                    GetTableLength(font, "GPOS")))
                _gdefTable = default;
            bool preserveDefaultIgnorables =
                (bufferFlags & ShapingBufferFlags.PreserveDefaultIgnorables) != 0;
            bool removeDefaultIgnorables =
                (bufferFlags & ShapingBufferFlags.RemoveDefaultIgnorables) != 0;
            ushort invisibleGlyph = preserveDefaultIgnorables || removeDefaultIgnorables
                ? (ushort)0
                : font.GetGlyphIndex(' ');
            int outputCount = substitutions.Count;
            if (invisibleGlyph == 0)
            {
                for (var index = 0; index < substitutions.Count; index++)
                {
                    GlyphRecord record = substitutions.GetRecord(index);
                    if (GlyphSubstitutionBuffer.IsDefaultIgnorable(record.CodePoint) && record.Substituted == 0 &&
                        !preserveDefaultIgnorables)
                        outputCount--;
                }
            }
            _glyphs = new GlyphRecord[outputCount];
            bool vertical = direction is ShapingDirection.TopToBottom or ShapingDirection.BottomToTop;
            int forwardSourceCluster = int.MinValue;
            int forwardMergedCluster = int.MaxValue;
            for (int sourceIndex = 0, outputIndex = 0; sourceIndex < substitutions.Count; sourceIndex++)
            {
                GlyphRecord record = substitutions.GetRecord(sourceIndex);
                bool defaultIgnorable = GlyphSubstitutionBuffer.IsDefaultIgnorable(record.CodePoint);
                if (defaultIgnorable && record.Substituted == 0 && invisibleGlyph == 0 &&
                    !preserveDefaultIgnorables)
                {
                    int cluster = record.Cluster;
                    if (sourceIndex + 1 < substitutions.Count &&
                        cluster == substitutions.GetRecord(sourceIndex + 1).Cluster)
                        continue;
                    if (outputIndex > 0)
                    {
                        if (cluster < _glyphs[outputIndex - 1].Cluster)
                        {
                            int oldCluster = _glyphs[outputIndex - 1].Cluster;
                            for (var index = outputIndex - 1; index >= 0 && _glyphs[index].Cluster == oldCluster; index--)
                                _glyphs[index].Cluster = cluster;
                        }
                    }
                    else if (sourceIndex + 1 < substitutions.Count)
                    {
                        forwardSourceCluster = substitutions.GetRecord(sourceIndex + 1).Cluster;
                        forwardMergedCluster = Math.Min(cluster, forwardSourceCluster);
                    }
                    continue;
                }
                if (record.Cluster == forwardSourceCluster)
                    record.Cluster = forwardMergedCluster;
                else if (forwardSourceCluster != int.MinValue)
                    forwardSourceCluster = int.MinValue;
                if (defaultIgnorable && record.Substituted == 0 && !preserveDefaultIgnorables)
                {
                    record.GlyphIndex = invisibleGlyph;
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
                        MathF.Round(
                            font.GetAdvanceWidth(record.GlyphIndex, font.UnitsPerEm),
                            MidpointRounding.AwayFromZero),
                        short.MinValue,
                        short.MaxValue));
                }
                ApplySpaceFallback(font, vertical, ref record);
                if (zeroMarkAdvancesEarly && GetGlyphClassKind(record) == GlyphClassKind.Mark)
                {
                    if (!font.TryGetTable("GPOS", out _) &&
                        direction is ShapingDirection.LeftToRight or ShapingDirection.TopToBottom)
                    {
                        record.OffsetX = AddClamped(record.OffsetX, -record.AdvanceX);
                        record.OffsetY = AddClamped(record.OffsetY, -record.AdvanceY);
                    }
                    record.AdvanceX = 0;
                    record.AdvanceY = 0;
                }
                if (defaultIgnorable && record.Substituted == 0 && !preserveDefaultIgnorables)
                {
                    record.AdvanceX = 0;
                    record.AdvanceY = 0;
                }
                _glyphs[outputIndex++] = record;
            }
            RestoreSplitMatraCharacterClusters(clusterLevel);
        }

        private void RestoreSplitMatraCharacterClusters(ShapingClusterLevel clusterLevel)
        {
            if (clusterLevel != ShapingClusterLevel.MonotoneCharacters) return;
            bool changed = false;
            for (var index = 0; index < _glyphs.Length; index++)
            {
                if (_glyphs[index].HasCharacterCluster == 0) continue;
                _glyphs[index].Cluster = _glyphs[index].CharacterCluster;
                changed = true;
            }
            if (!changed) return;
            for (var index = 1; index < _glyphs.Length; index++)
            {
                int cluster = _glyphs[index].Cluster;
                for (int previous = index - 1;
                     previous >= 0 && _glyphs[previous].Cluster > cluster;
                     previous--)
                    _glyphs[previous].Cluster = cluster;
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
            GetGlyphClassKind(_glyphs[index]);

        private GlyphClassKind GetGlyphClassKind(GlyphRecord record)
        {
            ReadOnlySpan<byte> data = _gdefTable.Span;
            if (CanRead(data, 4, 2))
            {
                int classDefOffset = ReadU16(data, 4);
                if (classDefOffset != 0)
                    return (GlyphClassKind)OpenTypeTextShaper.GetGlyphClass(
                        data, classDefOffset, record.GlyphIndex);
            }
            return !GlyphSubstitutionBuffer.IsDefaultIgnorable(record.CodePoint) &&
                   Rune.GetUnicodeCategory(new Rune((int)record.CodePoint)) == UnicodeCategory.NonSpacingMark
                ? GlyphClassKind.Mark
                : GlyphClassKind.Base;
        }

        public int NextEligibleIndex(int index, ushort lookupFlags)
        {
            while (index < _glyphs.Length)
            {
                if (!IsIgnored(index, lookupFlags)) return index;
                index++;
            }
            return -1;
        }

        public bool IsEligibleIndex(int index, ushort lookupFlags) =>
            (uint)index < _glyphs.Length && !IsIgnored(index, lookupFlags);

        public int PreviousEligibleIndex(int index, ushort lookupFlags)
        {
            while (index >= 0)
            {
                if (!IsIgnored(index, lookupFlags)) return index;
                index--;
            }
            return -1;
        }

        public int GetEligibleSequenceIndex(int position, int sequenceIndex, ushort lookupFlags)
        {
            int target = position;
            for (var index = 0; index < sequenceIndex; index++)
            {
                target = NextEligibleIndex(target + 1, lookupFlags);
                if (target < 0) return -1;
            }
            return target;
        }

        public int SetMarkFilteringSet(ushort setIndex)
        {
            int previous = _markFilteringCoverage;
            _markFilteringCoverage = -1;
            if (setIndex == ushort.MaxValue) return previous;
            ReadOnlySpan<byte> data = _gdefTable.Span;
            if (!CanRead(data, 12, 2)) return previous;
            int setsOffset = ReadU16(data, 12);
            if (setsOffset == 0 || !CanRead(data, setsOffset, 4) || ReadU16(data, setsOffset) != 1)
                return previous;
            int setCount = ReadU16(data, setsOffset + 2);
            int pointer = setsOffset + 4 + setIndex * 4;
            if (setIndex >= setCount || !CanRead(data, pointer, 4)) return previous;
            uint relative = ReadU32(data, pointer);
            if (relative <= int.MaxValue && CanRead(data, setsOffset + (int)relative, 4))
                _markFilteringCoverage = setsOffset + (int)relative;
            return previous;
        }

        public void RestoreMarkFilteringCoverage(int coverage) =>
            _markFilteringCoverage = coverage;

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
                if ((lookupFlags & 0x0010) != 0 &&
                    (_markFilteringCoverage < 0 ||
                     FindCoverage(_gdefTable.Span, _markFilteringCoverage, _glyphs[index].GlyphIndex) < 0))
                {
                    return true;
                }
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

        public bool CanAttachMarkToMark(int firstMarkIndex, int secondMarkIndex)
        {
            byte first = _glyphs[firstMarkIndex].LigatureComponent;
            byte second = _glyphs[secondMarkIndex].LigatureComponent;
            return first == byte.MaxValue || second == byte.MaxValue || first == second;
        }

        public void Attach(int markIndex, int targetIndex, int anchorDeltaX, int anchorDeltaY)
        {
            GlyphRecord mark = _glyphs[markIndex];
            mark.OffsetX = ClampToShort(anchorDeltaX);
            mark.OffsetY = ClampToShort(anchorDeltaY);
            mark.AttachmentTarget = targetIndex;
            mark.AttachmentKind = AttachmentMark;
            _glyphs[markIndex] = mark;
        }

        public void AttachCursive(
            int previousIndex,
            int currentIndex,
            int entryX,
            int entryY,
            int exitX,
            int exitY,
            ushort lookupFlags)
        {
            GlyphRecord previous = _glyphs[previousIndex];
            GlyphRecord current = _glyphs[currentIndex];
            switch (_direction)
            {
                case ShapingDirection.LeftToRight:
                    previous.AdvanceX = ClampToShort(exitX + previous.OffsetX);
                    int ltrDelta = entryX + current.OffsetX;
                    current.AdvanceX = ClampToShort(current.AdvanceX - ltrDelta);
                    current.OffsetX = ClampToShort(current.OffsetX - ltrDelta);
                    break;
                case ShapingDirection.RightToLeft:
                    int rtlDelta = exitX + previous.OffsetX;
                    previous.AdvanceX = ClampToShort(previous.AdvanceX - rtlDelta);
                    previous.OffsetX = ClampToShort(previous.OffsetX - rtlDelta);
                    current.AdvanceX = ClampToShort(entryX + current.OffsetX);
                    break;
                case ShapingDirection.TopToBottom:
                    previous.AdvanceY = ClampToShort(exitY + previous.OffsetY);
                    int ttbDelta = entryY + current.OffsetY;
                    current.AdvanceY = ClampToShort(current.AdvanceY - ttbDelta);
                    current.OffsetY = ClampToShort(current.OffsetY - ttbDelta);
                    break;
                case ShapingDirection.BottomToTop:
                    int bttDelta = exitY + previous.OffsetY;
                    previous.AdvanceY = ClampToShort(previous.AdvanceY - bttDelta);
                    previous.OffsetY = ClampToShort(previous.OffsetY - bttDelta);
                    current.AdvanceY = ClampToShort(entryY);
                    break;
            }

            bool horizontal = _direction is ShapingDirection.LeftToRight or ShapingDirection.RightToLeft;
            bool rightToLeftLookup = (lookupFlags & 0x0001) != 0;
            int childIndex = rightToLeftLookup ? previousIndex : currentIndex;
            int parentIndex = rightToLeftLookup ? currentIndex : previousIndex;
            _glyphs[previousIndex] = previous;
            _glyphs[currentIndex] = current;

            ReverseCursiveMinorOffset(childIndex, parentIndex, horizontal);
            GlyphRecord child = _glyphs[childIndex];
            child.AttachmentTarget = parentIndex;
            child.AttachmentKind = horizontal ? AttachmentCursiveHorizontal : AttachmentCursiveVertical;
            if (horizontal)
                child.OffsetY = ClampToShort(rightToLeftLookup ? entryY - exitY : exitY - entryY);
            else
                child.OffsetX = ClampToShort(rightToLeftLookup ? entryX - exitX : exitX - entryX);

            _glyphs[childIndex] = child;
            GlyphRecord parent = _glyphs[parentIndex];
            if (parent.AttachmentTarget == childIndex &&
                parent.AttachmentKind == child.AttachmentKind)
            {
                parent.AttachmentTarget = -1;
                parent.AttachmentKind = 0;
                if (horizontal) parent.OffsetY = 0;
                else parent.OffsetX = 0;
                _glyphs[parentIndex] = parent;
            }
        }

        private void ReverseCursiveMinorOffset(int index, int newParent, bool horizontal)
        {
            GlyphRecord glyph = _glyphs[index];
            if (glyph.AttachmentKind is not (AttachmentCursiveHorizontal or AttachmentCursiveVertical)) return;
            int oldParent = glyph.AttachmentTarget;
            glyph.AttachmentTarget = -1;
            glyph.AttachmentKind = 0;
            _glyphs[index] = glyph;
            if ((uint)oldParent >= _glyphs.Length || oldParent == newParent) return;

            ReverseCursiveMinorOffset(oldParent, newParent, horizontal);
            GlyphRecord reversed = _glyphs[oldParent];
            if (horizontal) reversed.OffsetY = ClampToShort(-glyph.OffsetY);
            else reversed.OffsetX = ClampToShort(-glyph.OffsetX);
            reversed.AttachmentTarget = index;
            reversed.AttachmentKind = horizontal ? AttachmentCursiveHorizontal : AttachmentCursiveVertical;
            _glyphs[oldParent] = reversed;
        }

        public void ResolveAttachmentOffsets()
        {
            var states = new byte[_glyphs.Length];
            for (var index = 0; index < _glyphs.Length; index++)
            {
                ResolveAttachmentOffset(index, states);
            }
        }

        public void ZeroMarkAdvancesLate(bool adjustOffsets)
        {
            for (var index = 0; index < _glyphs.Length; index++)
            {
                if (GetGlyphClassKind(index) != GlyphClassKind.Mark) continue;
                GlyphRecord mark = _glyphs[index];
                if (adjustOffsets)
                {
                    mark.OffsetX = AddClamped(mark.OffsetX, -mark.AdvanceX);
                    mark.OffsetY = AddClamped(mark.OffsetY, -mark.AdvanceY);
                }
                mark.AdvanceX = 0;
                mark.AdvanceY = 0;
                _glyphs[index] = mark;
            }
        }

        public void ApplyFallbackMarkPositioning(TtfFont font)
        {
            int clusterStart = 0;
            for (var index = 1; index < _glyphs.Length; index++)
            {
                if (IsUnicodeMark(_glyphs[index].CodePoint) ||
                    GlyphSubstitutionBuffer.IsDefaultIgnorable(_glyphs[index].CodePoint)) continue;
                PositionFallbackCluster(font, clusterStart, index);
                clusterStart = index;
            }
            PositionFallbackCluster(font, clusterStart, _glyphs.Length);
        }

        public void ApplyLegacyKern(TtfFont font)
        {
            if (!font.TryGetTable("kern", out ReadOnlyMemory<byte> memory)) return;
            ReadOnlySpan<byte> data = memory.Span;
            bool apple = CanRead(data, 0, 8) && ReadU32(data, 0) == 0x00010000;
            int subtableCount;
            int subtable;
            if (apple)
            {
                uint count = ReadU32(data, 4);
                if (count > int.MaxValue) return;
                subtableCount = (int)count;
                subtable = 8;
            }
            else
            {
                if (!CanRead(data, 0, 4) || ReadU16(data, 0) != 0) return;
                subtableCount = ReadU16(data, 2);
                subtable = 4;
            }

            for (var tableIndex = 0; tableIndex < subtableCount; tableIndex++)
            {
                int headerSize = apple ? 8 : 6;
                if (!CanRead(data, subtable, headerSize)) break;
                uint rawLength = apple ? ReadU32(data, subtable) : ReadU16(data, subtable + 2);
                if (rawLength < headerSize || rawLength > int.MaxValue ||
                    !CanRead(data, subtable, (int)rawLength)) break;
                int length = (int)rawLength;
                byte format = data[subtable + (apple ? 5 : 4)];
                byte coverage = data[subtable + (apple ? 4 : 5)];
                bool horizontal = apple ? (coverage & 0x80) == 0 : (coverage & 0x01) != 0;
                bool crossStream = apple ? (coverage & 0x40) != 0 : (coverage & 0x04) != 0;
                if (horizontal)
                {
                    if (format == 0) ApplyLegacyKernFormat0(data, subtable, headerSize, length, crossStream);
                    else if (format == 2) ApplyLegacyKernFormat2(data, subtable, headerSize, length, crossStream);
                }
                subtable += length;
            }
        }

        private void ApplyLegacyKernFormat0(
            ReadOnlySpan<byte> data,
            int subtable,
            int headerSize,
            int length,
            bool crossStream)
        {
            int body = subtable + headerSize;
            if (!CanRead(data, body, 8)) return;
            int pairCount = ReadU16(data, body);
            int records = body + 8;
            if (!CanRead(data, records, pairCount * 6)) return;
            for (var leftIndex = 0; leftIndex + 1 < _glyphs.Length; leftIndex++)
            {
                int rightIndex = NextLegacyKernGlyph(leftIndex + 1);
                if (rightIndex >= _glyphs.Length) break;
                int kerning = FindLegacyKernPair(
                    data,
                    records,
                    pairCount,
                    _glyphs[leftIndex].GlyphIndex,
                    _glyphs[rightIndex].GlyphIndex);
                ApplyLegacyKernAdjustment(leftIndex, rightIndex, kerning, crossStream);
            }
        }

        private void ApplyLegacyKernFormat2(
            ReadOnlySpan<byte> data,
            int subtable,
            int headerSize,
            int length,
            bool crossStream)
        {
            int body = subtable + headerSize;
            if (!CanRead(data, body, 8)) return;
            int leftTable = ReadU16(data, body + 2);
            int rightTable = ReadU16(data, body + 4);
            int array = ReadU16(data, body + 6);
            for (var leftIndex = 0; leftIndex + 1 < _glyphs.Length; leftIndex++)
            {
                int rightIndex = NextLegacyKernGlyph(leftIndex + 1);
                if (rightIndex >= _glyphs.Length) break;
                int leftOffset = GetLegacyKernClass(
                    data, subtable, length, leftTable, _glyphs[leftIndex].GlyphIndex);
                int rightOffset = GetLegacyKernClass(
                    data, subtable, length, rightTable, _glyphs[rightIndex].GlyphIndex);
                int valueOffset = leftOffset + rightOffset;
                int kerning = valueOffset < array || valueOffset > length - 2
                    ? 0
                    : ReadI16(data, subtable + valueOffset);
                ApplyLegacyKernAdjustment(leftIndex, rightIndex, kerning, crossStream);
            }
        }

        private int NextLegacyKernGlyph(int index)
        {
            while (index < _glyphs.Length && GetGlyphClassKind(index) == GlyphClassKind.Mark) index++;
            return index;
        }

        private void ApplyLegacyKernAdjustment(int leftIndex, int rightIndex, int kerning, bool crossStream)
        {
            if (kerning == 0) return;
            GlyphRecord left = _glyphs[leftIndex];
            GlyphRecord right = _glyphs[rightIndex];
            if (crossStream)
            {
                right.OffsetY = AddClamped(right.OffsetY, kerning);
            }
            else
            {
                int first = kerning >> 1;
                int second = kerning - first;
                left.AdvanceX = AddClamped(left.AdvanceX, first);
                right.AdvanceX = AddClamped(right.AdvanceX, second);
                right.OffsetX = AddClamped(right.OffsetX, second);
            }
            _glyphs[leftIndex] = left;
            _glyphs[rightIndex] = right;
        }

        private static int FindLegacyKernPair(
            ReadOnlySpan<byte> data,
            int records,
            int pairCount,
            ushort left,
            ushort right)
        {
            uint key = ((uint)left << 16) | right;
            var low = 0;
            var high = pairCount - 1;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                int record = records + middle * 6;
                uint candidate = ReadU32(data, record);
                if (key < candidate) high = middle - 1;
                else if (key > candidate) low = middle + 1;
                else return ReadI16(data, record + 4);
            }
            return 0;
        }

        private static int GetLegacyKernClass(
            ReadOnlySpan<byte> data,
            int subtable,
            int length,
            int relativeOffset,
            ushort glyph)
        {
            if (relativeOffset < 0 || relativeOffset > length - 4) return 0;
            int table = subtable + relativeOffset;
            ushort firstGlyph = ReadU16(data, table);
            ushort glyphCount = ReadU16(data, table + 2);
            int index = glyph - firstGlyph;
            return (uint)index < glyphCount && CanRead(data, table + 4 + index * 2, 2)
                ? ReadU16(data, table + 4 + index * 2)
                : 0;
        }

        private void PositionFallbackCluster(TtfFont font, int start, int end)
        {
            if (end - start < 2) return;
            for (var baseIndex = start; baseIndex < end; baseIndex++)
            {
                if (IsUnicodeMark(_glyphs[baseIndex].CodePoint)) continue;
                int markEnd = baseIndex + 1;
                while (markEnd < end &&
                       (IsUnicodeMark(_glyphs[markEnd].CodePoint) ||
                        GlyphSubstitutionBuffer.IsDefaultIgnorable(_glyphs[markEnd].CodePoint)))
                    markEnd++;
                PositionFallbackMarksAroundBase(font, baseIndex, markEnd);
                baseIndex = markEnd - 1;
            }
        }

        private void PositionFallbackMarksAroundBase(TtfFont font, int baseIndex, int end)
        {
            if (!TryGetGlyphExtents(font, _glyphs[baseIndex].GlyphIndex, out GlyphExtents baseExtents))
                return;

            baseExtents.YBearing += _glyphs[baseIndex].OffsetY;
            baseExtents.XBearing = 0;
            baseExtents.Width = (int)MathF.Round(
                font.GetAdvanceWidth(_glyphs[baseIndex].GlyphIndex, font.UnitsPerEm));

            int xOffset = 0;
            int yOffset = 0;
            if (_direction is ShapingDirection.LeftToRight or ShapingDirection.TopToBottom)
            {
                xOffset -= _glyphs[baseIndex].AdvanceX;
                yOffset -= _glyphs[baseIndex].AdvanceY;
            }

            int lastClass = 255;
            int lastLigatureComponent = -1;
            GlyphExtents classExtents = baseExtents;
            GlyphExtents componentExtents = baseExtents;
            int ligatureComponentCount = _glyphs[baseIndex].LigatureComponentCount;
            for (var index = baseIndex + 1; index < end; index++)
            {
                int combiningClass = RecategorizeCombiningClass(
                    _glyphs[index].CodePoint,
                    GlyphSubstitutionBuffer.GetModifiedCombiningClass(_glyphs[index].CodePoint));
                if (combiningClass == 0)
                {
                    if (_direction is ShapingDirection.LeftToRight or ShapingDirection.TopToBottom)
                    {
                        xOffset -= _glyphs[index].AdvanceX;
                        yOffset -= _glyphs[index].AdvanceY;
                    }
                    else
                    {
                        xOffset += _glyphs[index].AdvanceX;
                        yOffset += _glyphs[index].AdvanceY;
                    }
                    continue;
                }

                if (ligatureComponentCount > 1)
                {
                    int component = _glyphs[index].LigatureComponent == byte.MaxValue
                        ? ligatureComponentCount - 1
                        : Math.Min(_glyphs[index].LigatureComponent, ligatureComponentCount - 1);
                    if (lastLigatureComponent != component)
                    {
                        lastLigatureComponent = component;
                        lastClass = 255;
                        componentExtents = baseExtents;
                        if (_direction == ShapingDirection.LeftToRight)
                            componentExtents.XBearing += component * componentExtents.Width / ligatureComponentCount;
                        else
                            componentExtents.XBearing +=
                                (ligatureComponentCount - 1 - component) * componentExtents.Width / ligatureComponentCount;
                        componentExtents.Width /= ligatureComponentCount;
                    }
                }

                if (lastClass != combiningClass)
                {
                    lastClass = combiningClass;
                    classExtents = componentExtents;
                }
                PositionFallbackMark(font, index, combiningClass, ref classExtents);
                GlyphRecord mark = _glyphs[index];
                mark.AdvanceX = 0;
                mark.AdvanceY = 0;
                mark.OffsetX = AddClamped(mark.OffsetX, xOffset);
                mark.OffsetY = AddClamped(mark.OffsetY, yOffset);
                _glyphs[index] = mark;
            }
        }

        private void PositionFallbackMark(
            TtfFont font,
            int index,
            int combiningClass,
            ref GlyphExtents baseExtents)
        {
            if (!TryGetGlyphExtents(font, _glyphs[index].GlyphIndex, out GlyphExtents markExtents)) return;
            int xOffset = combiningClass switch
            {
                233 or 234 when _direction == ShapingDirection.LeftToRight =>
                    baseExtents.XBearing + baseExtents.Width - markExtents.Width / 2 - markExtents.XBearing,
                233 or 234 when _direction == ShapingDirection.RightToLeft =>
                    baseExtents.XBearing - markExtents.Width / 2 - markExtents.XBearing,
                200 or 218 or 228 => baseExtents.XBearing - markExtents.XBearing,
                216 or 222 or 232 =>
                    baseExtents.XBearing + baseExtents.Width - markExtents.Width - markExtents.XBearing,
                _ => baseExtents.XBearing + (baseExtents.Width - markExtents.Width) / 2 - markExtents.XBearing
            };

            int yGap = font.UnitsPerEm / 16;
            int yOffset = 0;
            if (combiningClass is 233 or 218 or 220 or 222)
                baseExtents.Height -= yGap;
            if (combiningClass is 200 or 202 or 218 or 220 or 222 or 233)
            {
                yOffset = baseExtents.YBearing + baseExtents.Height - markExtents.YBearing;
                if ((yGap > 0) == (yOffset > 0))
                {
                    baseExtents.Height -= yOffset;
                    yOffset = 0;
                }
                baseExtents.Height += markExtents.Height;
            }
            else if (combiningClass is 228 or 230 or 232 or 234)
            {
                baseExtents.YBearing += yGap;
                baseExtents.Height -= yGap;
                yOffset = PositionFallbackAbove(ref baseExtents, markExtents, yGap);
            }
            else if (combiningClass is 214 or 216)
            {
                yOffset = PositionFallbackAbove(ref baseExtents, markExtents, yGap);
            }

            GlyphRecord mark = _glyphs[index];
            mark.OffsetX = ClampToShort(xOffset);
            mark.OffsetY = ClampToShort(yOffset);
            _glyphs[index] = mark;
        }

        private static int PositionFallbackAbove(
            ref GlyphExtents baseExtents,
            GlyphExtents markExtents,
            int yGap)
        {
            int yOffset = baseExtents.YBearing - (markExtents.YBearing + markExtents.Height);
            if ((yGap > 0) != (yOffset > 0))
            {
                int correction = -yOffset / 2;
                baseExtents.YBearing += correction;
                baseExtents.Height -= correction;
                yOffset += correction;
            }
            baseExtents.YBearing -= markExtents.Height;
            baseExtents.Height += markExtents.Height;
            return yOffset;
        }

        private static bool TryGetGlyphExtents(TtfFont font, ushort glyph, out GlyphExtents extents)
        {
            if (font.TryGetGlyphBounds(glyph, out short xMin, out short yMin, out short xMax, out short yMax))
            {
                extents = new GlyphExtents(xMin, yMax, xMax - xMin, yMin - yMax);
                return true;
            }
            extents = default;
            return false;
        }

        private static bool IsUnicodeMark(uint codePoint)
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(new Rune((int)codePoint));
            return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark;
        }

        private static int RecategorizeCombiningClass(uint codePoint, int combiningClass)
        {
            if (combiningClass >= 200) return combiningClass;
            if ((codePoint & ~0xFFu) == 0x0E00u)
            {
                if (combiningClass == 0)
                {
                    combiningClass = codePoint switch
                    {
                        0x0E31 or 0x0E34 or 0x0E35 or 0x0E36 or 0x0E37 or 0x0E47 or
                        0x0E4C or 0x0E4D or 0x0E4E => 232,
                        0x0EB1 or 0x0EB4 or 0x0EB5 or 0x0EB6 or 0x0EB7 or 0x0EBB or
                        0x0ECC or 0x0ECD => 230,
                        0x0EBC => 220,
                        _ => 0
                    };
                }
                else if (codePoint == 0x0E3A)
                {
                    combiningClass = 222;
                }
            }

            return combiningClass switch
            {
                22 or 15 or 16 or 17 or 23 or 18 or 19 or 20 or 21 or 24 or 25 => 220,
                13 => 214,
                10 => 232,
                11 or 14 => 228,
                26 => 230,
                28 or 29 or 31 or 32 or 27 or 34 or 35 or 36 => 230,
                30 or 33 => 220,
                103 or 107 => 232,
                118 or 129 or 132 => 220,
                122 or 130 => 230,
                _ => combiningClass
            };
        }

        private struct GlyphExtents
        {
            public GlyphExtents(int xBearing, int yBearing, int width, int height)
            {
                XBearing = xBearing;
                YBearing = yBearing;
                Width = width;
                Height = height;
            }

            public int XBearing;
            public int YBearing;
            public int Width;
            public int Height;
        }

        private void ResolveAttachmentOffset(int index, byte[] states)
        {
            if (states[index] != 0) return;
            states[index] = 1;
            GlyphRecord glyph = _glyphs[index];
            int targetIndex = glyph.AttachmentTarget;
            if ((uint)targetIndex < _glyphs.Length && states[targetIndex] != 1)
            {
                ResolveAttachmentOffset(targetIndex, states);
                GlyphRecord target = _glyphs[targetIndex];
                if (glyph.AttachmentKind == AttachmentMark)
                {
                    glyph.OffsetX = AddClamped(glyph.OffsetX, target.OffsetX);
                    glyph.OffsetY = AddClamped(glyph.OffsetY, target.OffsetY);
                    bool forward = _direction is ShapingDirection.LeftToRight or ShapingDirection.TopToBottom;
                    if (targetIndex < index)
                    {
                        int start = forward ? targetIndex : targetIndex + 1;
                        int end = forward ? index : index + 1;
                        int sign = forward ? -1 : 1;
                        for (var advanceIndex = start; advanceIndex < end; advanceIndex++)
                        {
                            glyph.OffsetX = AddClamped(glyph.OffsetX, sign * _glyphs[advanceIndex].AdvanceX);
                            glyph.OffsetY = AddClamped(glyph.OffsetY, sign * _glyphs[advanceIndex].AdvanceY);
                        }
                    }
                    else if (targetIndex > index)
                    {
                        int start = forward ? index : index + 1;
                        int end = forward ? targetIndex : targetIndex + 1;
                        int sign = forward ? 1 : -1;
                        for (var advanceIndex = start; advanceIndex < end; advanceIndex++)
                        {
                            glyph.OffsetX = AddClamped(glyph.OffsetX, sign * _glyphs[advanceIndex].AdvanceX);
                            glyph.OffsetY = AddClamped(glyph.OffsetY, sign * _glyphs[advanceIndex].AdvanceY);
                        }
                    }
                }
                else
                {
                    if (glyph.AttachmentKind == AttachmentCursiveVertical)
                        glyph.OffsetX = AddClamped(glyph.OffsetX, target.OffsetX);
                    if (glyph.AttachmentKind == AttachmentCursiveHorizontal)
                        glyph.OffsetY = AddClamped(glyph.OffsetY, target.OffsetY);
                }
                _glyphs[index] = glyph;
            }
            states[index] = 2;
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

        public void ReverseClustersWithinCombiningRuns()
        {
            for (var start = 0; start < _glyphs.Length;)
            {
                while (start < _glyphs.Length &&
                       GlyphSubstitutionBuffer.GetModifiedCombiningClass(_glyphs[start].CodePoint) == 0)
                    start++;
                int end = start;
                while (end < _glyphs.Length &&
                       GlyphSubstitutionBuffer.GetModifiedCombiningClass(_glyphs[end].CodePoint) != 0)
                    end++;
                for (int left = start, right = end - 1; left < right; left++, right--)
                    (_glyphs[left].Cluster, _glyphs[right].Cluster) =
                        (_glyphs[right].Cluster, _glyphs[left].Cluster);
                start = end + 1;
            }
        }

        public void ApplyArabicStretch(TtfFont font, bool rightToLeft)
        {
            if (!Array.Exists(_glyphs, static glyph => IsStretchAction(glyph.ArabicAction))) return;
            if (!rightToLeft) Array.Reverse(_glyphs);

            var runs = new List<StretchRun>();
            int extraGlyphs = 0;
            for (int index = _glyphs.Length; index > 0;)
            {
                if (!IsStretchAction(_glyphs[index - 1].ArabicAction))
                {
                    index--;
                    continue;
                }

                int end = index;
                int fixedWidth = 0;
                int repeatingWidth = 0;
                int fixedCount = 0;
                int repeatingCount = 0;
                while (index > 0 && IsStretchAction(_glyphs[index - 1].ArabicAction))
                {
                    index--;
                    int width = (int)MathF.Round(font.GetAdvanceWidth(_glyphs[index].GlyphIndex, font.UnitsPerEm));
                    if (_glyphs[index].ArabicAction == StretchFixed)
                    {
                        fixedWidth += width;
                        fixedCount++;
                    }
                    else
                    {
                        repeatingWidth += width;
                        repeatingCount++;
                    }
                }
                int start = index;
                int context = start;
                int totalWidth = 0;
                while (context > 0 && !IsStretchAction(_glyphs[context - 1].ArabicAction) &&
                       (GlyphSubstitutionBuffer.IsDefaultIgnorable(_glyphs[context - 1].CodePoint) ||
                        IsArabicStretchWordCharacter(_glyphs[context - 1].CodePoint)))
                {
                    context--;
                    totalWidth += _glyphs[context].AdvanceX;
                }

                int remaining = totalWidth - fixedWidth;
                int copies = remaining > repeatingWidth && repeatingWidth > 0
                    ? remaining / repeatingWidth - 1
                    : 0;
                int overlap = 0;
                long shortfall = (long)remaining - (long)repeatingWidth * (copies + 1);
                if (shortfall > 0 && repeatingCount > 0)
                {
                    copies++;
                    long excess = (long)(copies + 1) * repeatingWidth - remaining;
                    if (excess > 0)
                    {
                        overlap = checked((int)(excess / (copies * repeatingCount)));
                        remaining = 0;
                    }
                }

                int baseGlyphs = fixedCount + repeatingCount;
                int maxCopies = repeatingCount > 0 && baseGlyphs < 256
                    ? (256 - baseGlyphs) / repeatingCount
                    : 0;
                copies = Math.Min(copies, maxCopies);
                int added = checked(copies * repeatingCount);
                extraGlyphs = checked(extraGlyphs + added);
                if (_glyphs.Length + extraGlyphs > MaximumShapedGlyphCount)
                    throw new InvalidOperationException($"OpenType stretching exceeds the {MaximumShapedGlyphCount} glyph shaping limit.");
                runs.Add(new StretchRun(start, end, copies, remaining, overlap));
            }

            GlyphRecord[] source = _glyphs;
            var stretched = new GlyphRecord[checked(source.Length + extraGlyphs)];
            int write = stretched.Length;
            int sourceIndex = source.Length;
            int runIndex = 0;
            while (sourceIndex > 0)
            {
                if (!IsStretchAction(source[sourceIndex - 1].ArabicAction))
                {
                    stretched[--write] = source[--sourceIndex];
                    continue;
                }

                StretchRun run = runs[runIndex++];
                sourceIndex = run.Start;
                int xOffset = run.RemainingWidth / 2;
                for (var glyphIndex = run.End; glyphIndex > run.Start; glyphIndex--)
                {
                    GlyphRecord glyph = source[glyphIndex - 1];
                    int width = (int)MathF.Round(font.GetAdvanceWidth(glyph.GlyphIndex, font.UnitsPerEm));
                    int repeat = glyph.ArabicAction == StretchRepeating ? run.CopyCount + 1 : 1;
                    glyph.AdvanceX = 0;
                    for (var copy = 0; copy < repeat; copy++)
                    {
                        if (rightToLeft)
                        {
                            xOffset -= width;
                            if (copy > 0) xOffset += run.ExtraRepeatOverlap;
                        }
                        glyph.OffsetX = ClampToShort(xOffset);
                        stretched[--write] = glyph;
                        if (!rightToLeft)
                        {
                            xOffset += width;
                            if (copy > 0) xOffset -= run.ExtraRepeatOverlap;
                        }
                    }
                }
            }

            _glyphs = stretched;
            if (!rightToLeft) Array.Reverse(_glyphs);
        }

        private static bool IsStretchAction(byte action) => action is StretchFixed or StretchRepeating;

        private static bool IsArabicStretchWordCharacter(uint codePoint)
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(new Rune((int)codePoint));
            return category is UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse or
                UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
                UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or
                UnicodeCategory.NonSpacingMark or UnicodeCategory.DecimalDigitNumber or
                UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or
                UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or
                UnicodeCategory.MathSymbol or UnicodeCategory.OtherSymbol;
        }

        private readonly record struct StretchRun(
            int Start,
            int End,
            int CopyCount,
            int RemainingWidth,
            int ExtraRepeatOverlap);

        private bool IsMark(int index)
        {
            return GetGlyphClassKind(index) == GlyphClassKind.Mark;
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

        private static short ClampToShort(int value) =>
            checked((short)Math.Clamp(value, short.MinValue, short.MaxValue));
    }

    private struct GlyphRecord
    {
        public GlyphRecord(ushort glyphIndex, int cluster, uint codePoint)
        {
            GlyphIndex = glyphIndex;
            Cluster = cluster;
            GraphemeCluster = cluster;
            CodePoint = codePoint;
            ArabicAction = None;
            LigatureComponent = byte.MaxValue;
            AttachmentTarget = -1;
        }

        public ushort GlyphIndex;
        public int Cluster;
        public int GraphemeCluster;
        public int CharacterCluster;
        public uint CodePoint;
        public short AdvanceX;
        public short AdvanceY;
        public short OffsetX;
        public short OffsetY;
        public byte ArabicAction;
        public byte SpaceFallback;
        public byte FractionAction;
        public byte ScriptFeatureMask;
        public byte ScriptShaper;
        public byte UseCategory;
        public byte UseSyllable;
        public byte IndicCategory;
        public byte IndicPosition;
        public byte IndicOriginalOrder;
        public byte UseRphfEligible;
        public byte Substituted;
        public byte Ligated;
        public byte MultipleSubstitutionComponent;
        public byte Multiplied;
        public byte LigatureComponentCount;
        public byte LigatureComponent;
        public byte HasCharacterCluster;
        public byte AttachmentKind;
        public int AttachmentTarget;
    }

    private readonly record struct ArabicStateEntry(byte PreviousAction, byte CurrentAction, byte NextState);

    private enum IndicRephMode : byte { Implicit, Explicit, Logical }
    private enum IndicBelowMode : byte { PreAndPost, PostOnly }

    private const byte Isolated = 0;
    private const byte Final = 1;
    private const byte Final2 = 2;
    private const byte Final3 = 3;
    private const byte Medial = 4;
    private const byte Medial2 = 5;
    private const byte Initial = 6;
    private const byte None = 7;
    private const byte StretchFixed = 8;
    private const byte StretchRepeating = 9;

    private const byte ScriptShaperKhmer = 1;
    private const byte ScriptShaperUse = 2;
    private const byte ScriptShaperIndic = 3;
    private const byte ScriptShaperHangul = 4;
    private const byte ScriptShaperMyanmar = 5;

    private const byte KhmerConsonantSyllable = 0;
    private const byte KhmerBrokenCluster = 1;
    private const byte KhmerNonCluster = 2;

    private const byte IndicConsonantSyllable = 0;
    private const byte IndicVowelSyllable = 1;
    private const byte IndicStandaloneCluster = 2;
    private const byte IndicSymbolCluster = 3;
    private const byte IndicBrokenCluster = 4;
    private const byte IndicNonCluster = 5;

    private const byte MyanmarConsonantSyllable = 0;
    private const byte MyanmarBrokenCluster = 1;
    private const byte MyanmarNonCluster = 2;

    private const byte IndicRphfMask = 1 << 0;
    private const byte IndicPrefMask = 1 << 1;
    private const byte IndicBlwfMask = 1 << 2;
    private const byte IndicAbvfMask = 1 << 3;
    private const byte IndicHalfMask = 1 << 4;
    private const byte IndicPstfMask = 1 << 5;
    private const byte IndicInitMask = 1 << 6;

    private const byte UseViramaTerminated = 0;
    private const byte UseSakotTerminated = 1;
    private const byte UseStandard = 2;
    private const byte UseNumberJoinerTerminated = 3;
    private const byte UseNumeral = 4;
    private const byte UseSymbol = 5;
    private const byte UseHieroglyph = 6;
    private const byte UseBroken = 7;
    private const byte UseNonCluster = 8;

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

    private const byte AttachmentMark = 1;
    private const byte AttachmentCursiveHorizontal = 2;
    private const byte AttachmentCursiveVertical = 3;

    private const byte KhmerPrefMask = 1 << 0;
    private const byte KhmerPostBaseMask = 1 << 1;
    private const byte KhmerCfarMask = 1 << 2;
    private const byte HangulLjmoMask = 1 << 0;
    private const byte HangulVjmoMask = 1 << 1;
    private const byte HangulTjmoMask = 1 << 2;
}
