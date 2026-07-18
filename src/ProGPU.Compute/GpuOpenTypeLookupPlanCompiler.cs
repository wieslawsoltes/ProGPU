using ProGPU.Text.Shaping;

namespace ProGPU.Compute;

/// <summary>
/// Resolves OpenType Script/LangSys/Feature/Lookup lists into the compact,
/// validated command stream consumed by the WebGPU lookup VM.
/// </summary>
public static class GpuOpenTypeLookupPlanCompiler
{
    private static readonly string[] s_defaultTags =
    [
        "rvrn", "frac", "numr", "dnom", "ccmp", "locl", "isol", "fina", "fin2", "fin3",
        "medi", "med2", "init", "rlig", "mark", "mkmk", "calt", "clig", "curs", "dist",
        "abvm", "blwm", "kern", "liga", "rclt", "rand"
    ];

    public static GpuOpenTypeLookupCommand[] Compile(
        GpuOpenTypeShapingPlan plan,
        in ShapingRequest request)
    {
        ArgumentNullException.ThrowIfNull(plan);
        FeaturePlan features = CreateFeaturePlan(request);
        var resolved = new List<ResolvedLookup>();
        AddTable(plan, request, plan.Tables.GsubOffset, plan.Tables.GsubLength, 1, features, resolved);
        AddTable(plan, request, plan.Tables.GposOffset, plan.Tables.GposLength, 2, features, resolved);
        resolved.Sort(static (left, right) =>
        {
            int table = left.Command.TableKind.CompareTo(right.Command.TableKind);
            if (table != 0) return table;
            int stage = left.Stage.CompareTo(right.Stage);
            return stage != 0 ? stage : left.LookupIndex.CompareTo(right.LookupIndex);
        });

        var commands = new List<GpuOpenTypeLookupCommand>(resolved.Count);
        foreach (ResolvedLookup lookup in resolved)
        {
            if (lookup.Required)
            {
                commands.Add(lookup.Command with { FeatureValue = 1, RangeStart = 0, RangeEnd = uint.MaxValue });
                continue;
            }
            foreach (FeatureInterval interval in ResolveIntervals(request.Features.Span, lookup.FeatureTag, lookup.BaseValue))
            {
                if (interval.Value == 0) continue;
                commands.Add(lookup.Command with
                {
                    FeatureValue = interval.Value,
                    RangeStart = interval.Start,
                    RangeEnd = interval.End,
                    CommandFlags = lookup.Explicit ? 1u : 0u
                });
            }
        }
        bool hasGposKerning = resolved.Any(static lookup =>
            lookup.Command.TableKind == 2 && lookup.FeatureTag is 0x6b65726eu or 0x64697374u);
        if (!hasGposKerning && plan.Tables.KernLength != 0 &&
            request.Direction is ShapingDirection.LeftToRight or ShapingDirection.RightToLeft &&
            !IsIndicScript(request.Script.ToString().ToLowerInvariant()))
        {
            uint kernTag = Tag("kern");
            foreach (FeatureInterval interval in ResolveIntervals(
                         request.Features.Span, kernTag, features.BaseValues.GetValueOrDefault(kernTag)))
            {
                if (interval.Value == 0) continue;
                commands.Add(new GpuOpenTypeLookupCommand(
                    3, plan.Tables.KernOffset, 0, 0, kernTag, interval.Value,
                    interval.Start, interval.End, HasFeatureTag(request.Features.Span, kernTag) ? 1u : 0u));
            }
        }
        return commands.ToArray();
    }

    private static FeaturePlan CreateFeaturePlan(in ShapingRequest request)
    {
        var orderedTags = new List<uint>();
        var baseValues = new Dictionary<uint, uint>();
        foreach (string tag in s_defaultTags) Add(tag, tag == "rand" ? (uint)ushort.MaxValue : 1u);

        string script = request.Script.ToString().ToLowerInvariant();
        foreach (string tag in ScriptFeatures(script)) Add(tag, 1);
        foreach (string tag in DirectionFeatures(request.Direction)) Add(tag, 1);
        if (request.Direction is ShapingDirection.TopToBottom or ShapingDirection.BottomToTop)
            AddOrReplace(Tag("kern"), 0);

        bool explicitLiga = false;
        foreach (ShapingFeature feature in request.Features.Span)
        {
            explicitLiga |= feature.Tag.Value == Tag("liga");
            if (feature.Start == 0 && feature.End == uint.MaxValue)
                AddOrReplace(feature.Tag.Value, feature.Value);
            else if (!baseValues.ContainsKey(feature.Tag.Value))
                AddOrReplace(feature.Tag.Value, 0);
        }
        if (script == "khmr" && !explicitLiga) AddOrReplace(Tag("liga"), 0);
        else if (IsIndicScript(script)) AddOrReplace(Tag("liga"), 0);
        if (IsArabicScript(script))
        {
            Add("stch", 1);
            Add("mset", 1);
        }
        return new FeaturePlan(orderedTags, baseValues);

        void Add(string tag, uint value)
        {
            uint packed = Tag(tag);
            if (baseValues.TryAdd(packed, value)) orderedTags.Add(packed);
        }

        void AddOrReplace(uint tag, uint value)
        {
            if (!baseValues.ContainsKey(tag)) orderedTags.Add(tag);
            baseValues[tag] = value;
        }
    }

    private static void AddTable(
        GpuOpenTypeShapingPlan plan,
        in ShapingRequest request,
        uint tableOffset,
        uint tableLength,
        uint tableKind,
        FeaturePlan features,
        List<ResolvedLookup> output)
    {
        if (tableLength < 10 || tableOffset > int.MaxValue) return;
        ReadOnlyMemory<byte> dataMemory = plan.TableData;
        ReadOnlySpan<byte> data = dataMemory.Span;
        OpenTypeTag scriptTag = request.Script;
        ReadOnlyMemory<ShapingFeature> requestFeatures = request.Features;
        int table = checked((int)tableOffset);
        int end = checked(table + (int)tableLength);
        if (!CanRead(data, table, 10) || end > data.Length) return;
        int featureList = table + ReadU16(data, table + 6);
        int lookupList = table + ReadU16(data, table + 8);
        if (!CanRead(data, featureList, 2) || !CanRead(data, lookupList, 2)) return;
        Dictionary<ushort, int>? substitutions = GetFeatureVariationSubstitutions(plan, data, table, end);
        HashSet<ushort> allowed = ResolveLanguageSystem(
            data, table, end, request.Script.Value, request.Language, out ushort required);
        ushort featureCount = ReadU16(data, featureList);
        var selected = new Dictionary<ushort, ResolvedLookup>();

        if (required < featureCount)
            AddFeature(required, 1, requiredFeature: true);

        foreach (uint requestedTag in features.OrderedTags)
        {
            uint baseValue = features.BaseValues[requestedTag];
            bool hasEnabledRange = HasEnabledRange(request.Features.Span, requestedTag);
            if (baseValue == 0 && !hasEnabledRange) continue;
            for (ushort featureIndex = 0; featureIndex < featureCount; featureIndex++)
            {
                int record = featureList + 2 + featureIndex * 6;
                if (!CanRead(data, record, 6)) break;
                if (ReadU32(data, record) != requestedTag) continue;
                if (!allowed.Contains(featureIndex) && !IsGlobalShaperFeature(request.Script, requestedTag)) continue;
                AddFeature(featureIndex, baseValue, requiredFeature: false);
            }
        }
        output.AddRange(selected.Values);

        void AddFeature(ushort featureIndex, uint baseValue, bool requiredFeature)
        {
            ReadOnlySpan<byte> data = dataMemory.Span;
            int record = featureList + 2 + featureIndex * 6;
            if (!CanRead(data, record, 6)) return;
            uint tag = ReadU32(data, record);
            int feature = substitutions is not null && substitutions.TryGetValue(featureIndex, out int alternate)
                ? alternate
                : featureList + ReadU16(data, record + 4);
            if (!CanRead(data, feature, 4)) return;
            ushort lookupCount = ReadU16(data, feature + 2);
            for (var index = 0; index < lookupCount; index++)
            {
                int lookupIndexOffset = feature + 4 + index * 2;
                if (!CanRead(data, lookupIndexOffset, 2)) break;
                ushort lookupIndex = ReadU16(data, lookupIndexOffset);
                if (!TryCreateCommand(data, lookupList, lookupIndex, tableKind, tag, baseValue,
                        out GpuOpenTypeLookupCommand command)) continue;
                var value = new ResolvedLookup(
                    lookupIndex,
                    tableKind == 1 ? GetSubstitutionStage(scriptTag, tag) : 0,
                    tag,
                    baseValue,
                    requiredFeature,
                    HasFeatureTag(requestFeatures.Span, tag),
                    command);
                if (!selected.TryGetValue(lookupIndex, out ResolvedLookup existing) ||
                    !IsGlobalFeature(existing.FeatureTag) || IsGlobalFeature(tag))
                    selected[lookupIndex] = value;
            }
        }
    }

    private static Dictionary<ushort, int>? GetFeatureVariationSubstitutions(
        GpuOpenTypeShapingPlan plan,
        ReadOnlySpan<byte> data,
        int table,
        int tableEnd)
    {
        if (!CanRead(data, table, 14) || ReadU16(data, table) != 1 || ReadU16(data, table + 2) < 1)
            return null;
        uint relative = ReadU32(data, table + 10);
        if (relative == 0 || relative > int.MaxValue - table) return null;
        int featureVariations = table + checked((int)relative);
        if (!CanReadInTable(data, featureVariations, 8, tableEnd) ||
            ReadU16(data, featureVariations) != 1 || ReadU16(data, featureVariations + 2) != 0)
            return null;
        uint recordCount = ReadU32(data, featureVariations + 4);
        if (recordCount > int.MaxValue / 8 ||
            !CanReadInTable(data, featureVariations + 8, checked((int)recordCount * 8), tableEnd))
            return null;

        ReadOnlySpan<short> coordinates = plan.NormalizedVariationCoordinates.Span;
        for (var recordIndex = 0; recordIndex < (int)recordCount; recordIndex++)
        {
            int record = featureVariations + 8 + recordIndex * 8;
            uint conditionRelative = ReadU32(data, record);
            uint substitutionRelative = ReadU32(data, record + 4);
            if (conditionRelative > int.MaxValue - featureVariations ||
                substitutionRelative > int.MaxValue - featureVariations)
                continue;
            int conditionSet = featureVariations + checked((int)conditionRelative);
            if (!MatchesFeatureVariationConditions(data, conditionSet, tableEnd, coordinates)) continue;
            int substitution = featureVariations + checked((int)substitutionRelative);
            if (!CanReadInTable(data, substitution, 6, tableEnd) ||
                ReadU16(data, substitution) != 1 || ReadU16(data, substitution + 2) != 0)
                return null;
            ushort count = ReadU16(data, substitution + 4);
            if (!CanReadInTable(data, substitution + 6, count * 6, tableEnd)) return null;
            var result = new Dictionary<ushort, int>(count);
            for (var index = 0; index < count; index++)
            {
                int item = substitution + 6 + index * 6;
                uint alternateRelative = ReadU32(data, item + 2);
                if (alternateRelative <= int.MaxValue - substitution)
                {
                    int alternate = substitution + checked((int)alternateRelative);
                    if (CanReadInTable(data, alternate, 4, tableEnd))
                        result[ReadU16(data, item)] = alternate;
                }
            }
            return result;
        }
        return null;
    }

    private static bool MatchesFeatureVariationConditions(
        ReadOnlySpan<byte> data,
        int conditionSet,
        int tableEnd,
        ReadOnlySpan<short> coordinates)
    {
        if (!CanReadInTable(data, conditionSet, 2, tableEnd)) return false;
        ushort count = ReadU16(data, conditionSet);
        if (!CanReadInTable(data, conditionSet + 2, count * 4, tableEnd)) return false;
        for (var index = 0; index < count; index++)
        {
            uint relative = ReadU32(data, conditionSet + 2 + index * 4);
            if (relative > int.MaxValue - conditionSet) return false;
            int condition = conditionSet + checked((int)relative);
            if (!CanReadInTable(data, condition, 8, tableEnd) || ReadU16(data, condition) != 1)
                return false;
            ushort axis = ReadU16(data, condition + 2);
            if (axis >= coordinates.Length) return false;
            short coordinate = coordinates[axis];
            if (coordinate < ReadI16(data, condition + 4) || coordinate > ReadI16(data, condition + 6))
                return false;
        }
        return true;
    }

    private static List<FeatureInterval> ResolveIntervals(
        ReadOnlySpan<ShapingFeature> requested,
        uint tag,
        uint baseValue)
    {
        var result = new List<FeatureInterval>();
        var boundaries = new SortedSet<uint> { 0, uint.MaxValue };
        for (var index = 0; index < requested.Length; index++)
        {
            ShapingFeature feature = requested[index];
            if (feature.Tag.Value != tag) continue;
            boundaries.Add(feature.Start);
            boundaries.Add(feature.End);
        }
        uint[] values = boundaries.ToArray();
        for (var boundary = 0; boundary + 1 < values.Length; boundary++)
        {
            uint start = values[boundary];
            uint end = values[boundary + 1];
            if (start == end) continue;
            uint value = baseValue;
            for (var featureIndex = 0; featureIndex < requested.Length; featureIndex++)
            {
                ShapingFeature feature = requested[featureIndex];
                if (feature.Tag.Value == tag && start >= feature.Start && start < feature.End)
                    value = feature.Value;
            }
            result.Add(new FeatureInterval(start, end, value));
        }
        return result;
    }

    private static bool HasEnabledRange(ReadOnlySpan<ShapingFeature> features, uint tag)
    {
        for (var index = 0; index < features.Length; index++)
            if (features[index].Tag.Value == tag && features[index].Value != 0) return true;
        return false;
    }

    private static bool HasFeatureTag(ReadOnlySpan<ShapingFeature> features, uint tag)
    {
        for (var index = 0; index < features.Length; index++)
            if (features[index].Tag.Value == tag) return true;
        return false;
    }

    private static HashSet<ushort> ResolveLanguageSystem(
        ReadOnlySpan<byte> data,
        int table,
        int tableEnd,
        uint requestedScript,
        string? language,
        out ushort required)
    {
        required = ushort.MaxValue;
        int scriptList = table + ReadU16(data, table + 4);
        if (!CanRead(data, scriptList, 2)) return [];
        int selected = 0;
        int fallback = 0;
        ushort scriptCount = ReadU16(data, scriptList);
        for (var index = 0; index < scriptCount; index++)
        {
            int record = scriptList + 2 + index * 6;
            if (!CanRead(data, record, 6)) break;
            int offset = scriptList + ReadU16(data, record + 4);
            uint tag = ReadU32(data, record);
            if (tag == requestedScript) selected = offset;
            else if (tag == Tag("DFLT")) fallback = offset;
        }
        int script = selected != 0 ? selected : fallback;
        if (!CanRead(data, script, 4) || script >= tableEnd) return [];
        int languageSystem = 0;
        ushort defaultOffset = ReadU16(data, script);
        if (defaultOffset != 0) languageSystem = script + defaultOffset;
        uint languageTag = LanguageTag(language);
        ushort languageCount = ReadU16(data, script + 2);
        for (var index = 0; index < languageCount; index++)
        {
            int record = script + 4 + index * 6;
            if (!CanRead(data, record, 6)) break;
            if (ReadU32(data, record) == languageTag)
                languageSystem = script + ReadU16(data, record + 4);
        }
        if (!CanRead(data, languageSystem, 6) || languageSystem >= tableEnd) return [];
        required = ReadU16(data, languageSystem + 2);
        ushort count = ReadU16(data, languageSystem + 4);
        var result = new HashSet<ushort>();
        for (var index = 0; index < count && CanRead(data, languageSystem + 6 + index * 2, 2); index++)
            result.Add(ReadU16(data, languageSystem + 6 + index * 2));
        if (required != ushort.MaxValue) result.Add(required);
        return result;
    }

    private static bool TryCreateCommand(
        ReadOnlySpan<byte> data,
        int lookupList,
        ushort lookupIndex,
        uint tableKind,
        uint featureTag,
        uint featureValue,
        out GpuOpenTypeLookupCommand command)
    {
        ushort count = ReadU16(data, lookupList);
        if (lookupIndex >= count || !CanRead(data, lookupList + 2 + lookupIndex * 2, 2))
        {
            command = default;
            return false;
        }
        int lookup = lookupList + ReadU16(data, lookupList + 2 + lookupIndex * 2);
        if (!CanRead(data, lookup, 6))
        {
            command = default;
            return false;
        }
        command = new GpuOpenTypeLookupCommand(
            tableKind,
            checked((uint)lookup),
            ReadU16(data, lookup),
            ReadU16(data, lookup + 2),
            featureTag,
            featureValue);
        return true;
    }

    private static int GetSubstitutionStage(OpenTypeTag scriptTag, uint tag)
    {
        string script = scriptTag.ToString().ToLowerInvariant();
        string feature = new OpenTypeTag(tag).ToString();
        if (IsArabicScript(script))
        {
            string[] order = ["stch", "rvrn", "frac", "numr", "dnom", "ccmp", "locl", "isol", "fina",
                "fin2", "fin3", "medi", "med2", "init", "rlig", "calt", "rclt", "liga", "clig", "mset"];
            int index = Array.IndexOf(order, feature);
            return index < 0 ? 18 : index;
        }
        if (IsIndicScript(script))
        {
            string[] order = ["rvrn", "frac", "numr", "dnom", "locl", "ccmp", "nukt", "akhn", "rphf",
                "rkrf", "pref", "blwf", "abvf", "half", "pstf", "vatu", "cjct"];
            int index = Array.IndexOf(order, feature);
            return index < 0 ? 17 : index;
        }
        if (script == "khmr")
        {
            string[] order = ["rvrn", "frac", "numr", "dnom", "locl", "ccmp", "pref", "blwf", "abvf", "pstf", "cfar"];
            int index = Array.IndexOf(order, feature);
            return index < 0 ? 11 : index;
        }
        if (script is "mymr" or "mym2")
        {
            string[] order = ["rvrn", "frac", "numr", "dnom", "locl", "ccmp", "rphf", "pref", "blwf", "pstf"];
            int index = Array.IndexOf(order, feature);
            return index < 0 ? 10 : index;
        }
        return 0;
    }

    private static IEnumerable<string> DirectionFeatures(ShapingDirection direction) => direction switch
    {
        ShapingDirection.RightToLeft => ["rtla", "rtlm"],
        ShapingDirection.TopToBottom or ShapingDirection.BottomToTop => ["vert", "vrt2", "vkrn"],
        _ => ["ltra", "ltrm"]
    };

    private static IEnumerable<string> ScriptFeatures(string script) => script switch
    {
        "khmr" => ["pref", "blwf", "abvf", "pstf", "cfar", "pres", "abvs", "blws", "psts"],
        "mymr" or "mym2" => ["rphf", "pref", "blwf", "pstf", "pres", "abvs", "blws", "psts"],
        "hang" => ["ljmo", "vjmo", "tjmo"],
        _ when IsIndicScript(script) =>
            ["nukt", "akhn", "rphf", "rkrf", "pref", "blwf", "abvf", "half", "pstf", "vatu", "cjct",
             "init", "pres", "abvs", "blws", "psts", "haln"],
        _ when IsArabicScript(script) =>
            ["stch", "isol", "fina", "fin2", "fin3", "medi", "med2", "init", "mset"],
        _ => []
    };

    private static bool IsIndicScript(string script) => script is
        "beng" or "bng2" or "bng3" or "deva" or "dev2" or "dev3" or
        "gujr" or "gjr2" or "gjr3" or "guru" or "gur2" or "gur3" or
        "knda" or "knd2" or "knd3" or "mlym" or "mlm2" or "mlm3" or
        "orya" or "ory2" or "ory3" or "taml" or "tml2" or "tml3" or
        "telu" or "tel2" or "tel3";

    private static bool IsArabicScript(string script) => script is
        "arab" or "syrc" or "nkoo" or "adlm" or "rohg" or "mand" or "mong" or "phlp" or "sogd";

    private static bool IsGlobalShaperFeature(OpenTypeTag script, uint tag) =>
        IsGlobalFeature(tag) || script.ToString().Equals("hang", StringComparison.OrdinalIgnoreCase) &&
        tag is 0x6c6a6d6f or 0x766a6d6f or 0x746a6d6f;

    private static bool IsGlobalFeature(uint tag) => tag is
        0x72616e64 or // rand
        0x6c747261 or 0x6c74726d or 0x72746c61 or 0x72746c6d or // ltra/ltrm/rtla/rtlm
        0x76657274 or 0x76727432; // vert/vrt2

    private static uint LanguageTag(string? language)
    {
        string normalized = language?.Replace('_', '-').ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "az" or "az-latn" => Tag("AZE "), "de" => Tag("DEU "), "dv" => Tag("DHV "),
            "fa" => Tag("FAR "), "ja" => Tag("JAN "), "nl" => Tag("NLD "), "pl" => Tag("PLK "),
            "ro" => Tag("ROM "), "tr" => Tag("TRK "),
            "zh" or "zh-cn" or "zh-sg" or "zh-hans" => Tag("ZHS "),
            "zh-tw" or "zh-hant" => Tag("ZHT "),
            "zh-hk" or "zh-mo" or "zh-hant-hk" or "zh-hant-mo" => Tag("ZHH "),
            _ => Tag("dflt")
        };
    }

    private static uint Tag(string tag) => new OpenTypeTag(tag).Value;
    private static bool CanRead(ReadOnlySpan<byte> data, int offset, int count) =>
        offset >= 0 && count >= 0 && offset <= data.Length - count;
    private static bool CanReadInTable(ReadOnlySpan<byte> data, int offset, int count, int tableEnd) =>
        CanRead(data, offset, count) && offset <= tableEnd - count;
    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)(data[offset] << 8 | data[offset + 1]);
    private static short ReadI16(ReadOnlySpan<byte> data, int offset) => unchecked((short)ReadU16(data, offset));
    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

    private sealed record FeaturePlan(List<uint> OrderedTags, Dictionary<uint, uint> BaseValues);
    private readonly record struct FeatureInterval(uint Start, uint End, uint Value);
    private readonly record struct ResolvedLookup(
        ushort LookupIndex,
        int Stage,
        uint FeatureTag,
        uint BaseValue,
        bool Required,
        bool Explicit,
        GpuOpenTypeLookupCommand Command);
}
