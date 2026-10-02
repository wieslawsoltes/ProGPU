using System.Security.Cryptography;
using System.Text.Json;
using HintedDisplayPolicyProbe;

namespace SourceDisplayPolicyProbe;

internal readonly record struct IndependentSourceMetrics(double Em, double Dpi, double Baseline, double LineSpacing);

internal sealed class SourceReferenceInput(JsonDocument document, string path, string hash) : IDisposable
{
    internal const string RtlFontHash = "A7FA16FFFB27BEDB060A0866267C29E9859AEB9C21CC33F5B3AAF6EB062ECA85";
    private const string MidpointCommit = "6ef45fee1be4dc1bbf11cf3328c5fcf3086496ac";
    private static readonly HashSet<string> MidpointHashes = new(StringComparer.Ordinal)
    {
        "C389F0A6ECDFC6C7E0B9799733DA945FDC8C07A360FE8DEAB19A8F4F5BA53C93",
        "E705D6469BD41568887D7E5DCAEE2833142EE36497DE6795B357895C5AEB6AB1"
    };
    // Exact immutable schemas2/4 from both successful original Windows jobs in
    // workflow37011550318. All original case payloads equal their prior192/v3
    // receipts. No caller-supplied trust hash or self-declared commit bypasses it.
    private const string SourceMetricCommit = "92f4778b0db2d802d84b50281bc4c4e97349b4db";
    private static readonly Dictionary<string, string> SourceMetricReceipts = new(StringComparer.Ordinal)
    {
        ["1542BCBF3FE0837A85565C8E716213C8D24AAD2FD2CEE69DAE87D7888633934E"] = SourceMetricCommit,
        ["66EB33702BFA83A208B7AF91E7F6A8D332F28ED9D03631B43DFB9B3B84039A56"] = SourceMetricCommit,
        ["4F0D43EC902A767E01C46E635196A5D1F960489D93CDE863A9E8010A8CEC669A"] = SourceMetricCommit,
        ["781CEAF0E530463543FC6AB27857B8DC34D26CD7ED88B77281F7F80414395B90"] = SourceMetricCommit
    };
    internal JsonElement Root => document.RootElement;
    internal string Path { get; } = path;
    internal string Sha256 { get; } = hash;
    internal bool HasIndependentMetrics => Root.GetProperty("Schema").GetInt32() is 2 or 4;
    internal bool IsMidpoint => Root.GetProperty("Schema").GetInt32() is 3 or 4;
    internal IEnumerable<JsonElement> Cases => Root.GetProperty("Cases").EnumerateArray()
        .Select(item => IsMidpoint ? item.GetProperty("Original") : item);
    public void Dispose() => document.Dispose();

    internal static SourceReferenceInput Read(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Reference exceeds its original budget.");
        return Parse(File.ReadAllBytes(path), System.IO.Path.GetFullPath(path));
    }

    internal static SourceReferenceInput Parse(ReadOnlySpan<byte> bytes, string path)
    {
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Reference exceeds its original budget.");
        byte[] owned = bytes.ToArray();
        string sha = Convert.ToHexString(SHA256.HashData(owned));
        bool midpoint = MidpointHashes.Contains(sha);
        bool hasMetrics = SourceMetricReceipts.TryGetValue(sha, out string? metricCommit);
        if (!midpoint && !hasMetrics)
        {
            // Reuse the old exact-byte ownership/identity validator unchanged.
            using var legacy = ReferenceInput.ParseVerified(owned, path);
            if (!ReferenceInput.HasIndependentSourceInputs(legacy.RootElement))
                throw new InvalidDataException("The old receipt has no independent source font inputs.");
        }
        var document = JsonDocument.Parse(owned);
        try
        {
            var result = new SourceReferenceInput(document, path, sha);
            ValidateStructure(result.Root);
            if (midpoint && (result.Root.GetProperty("SourceCommit").GetString() != MidpointCommit || result.HasIndependentMetrics) ||
                hasMetrics && (result.Root.GetProperty("SourceCommit").GetString() != metricCommit || !result.HasIndependentMetrics))
                throw new InvalidDataException("Reviewed receipt producer/schema changed.");
            return result;
        }
        catch { document.Dispose(); throw; }
    }

    internal static void ValidateStructure(JsonElement root)
    {
        int schema = root.GetProperty("Schema").GetInt32();
        bool midpoint = schema is 3 or 4, metrics = schema is 2 or 4;
        if (schema is < 1 or > 4 || root.GetProperty("Architecture").GetString() is not ("X64" or "Arm64") ||
            root.GetProperty("Font").GetProperty("Sha256").GetString() != ReferenceInput.FontHash ||
            !root.GetProperty("PresentationIdentity").GetString()!.Contains("PublicKeyToken=31bf3856ad364e35", StringComparison.Ordinal))
            throw new InvalidDataException("Original Microsoft reference identity is required.");
        JsonElement[] cases = root.GetProperty("Cases").EnumerateArray().ToArray();
        int expected = midpoint ? 288 : 192;
        if (cases.Length != expected || root.GetProperty("CaseCount").GetInt32() != expected)
            throw new InvalidDataException("The complete original inventory is required.");
        if (midpoint && (root.GetProperty("CaseFamily").GetString() != MidpointCases.Family ||
            root.GetProperty("RtlFont").GetProperty("Font").GetProperty("Sha256").GetString() != RtlFontHash))
            throw new InvalidDataException("Original midpoint/Hebrew source identity changed.");
        var originalInputs = new HashSet<(string?, string?, double, double, string?, double)>();
        MidpointInput[] midpointInputs = midpoint ? MidpointCases.Create().ToArray() : [];
        int lines = 0, runs = 0;
        for (int ordinal = 0; ordinal < cases.Length; ++ordinal)
        {
            JsonElement item = midpoint ? cases[ordinal].GetProperty("Original") : cases[ordinal];
            string font = midpoint && midpointInputs[ordinal].FontKey == "NotoSansHebrewRegular" ? RtlFontHash : ReferenceInput.FontHash;
            if (midpoint)
            {
                if (cases[ordinal].GetProperty("Input").GetRawText() is not { } inputJson ||
                    JsonSerializer.Deserialize<MidpointInput>(inputJson) != midpointInputs[ordinal])
                    throw new InvalidDataException("Original midpoint ordinal/input changed.");
                var coverage = MidpointCases.Observe(midpointInputs[ordinal], item);
                if (JsonSerializer.Deserialize<MidpointCoverage>(cases[ordinal].GetProperty("Coverage").GetRawText()) != coverage)
                    throw new InvalidDataException("Original midpoint coverage changed.");
            }
            else
            {
                string? mode = item.GetProperty("Mode").GetString(), direction = item.GetProperty("Direction").GetString(), text = item.GetProperty("Text").GetString();
                double dpi = item.GetProperty("Dpi").GetDouble(), em = item.GetProperty("Em").GetDouble(), width = item.GetProperty("Width").GetDouble();
                if (mode is not ("Ideal" or "Display") || direction is not ("LeftToRight" or "RightToLeft") ||
                    dpi is not (1 or 1.25 or 1.5 or 2) || em is not (13 or 17) || width is not (25 or 1000) ||
                    text is not ("Hello" or "AVATAR fi A A " or "a\u0301 b c ") || !originalInputs.Add((mode, direction, dpi, em, text, width)))
                    throw new InvalidDataException("Original 192-case inventory changed.");
            }
            foreach (var line in item.GetProperty("Lines").EnumerateArray())
            { ++lines; runs += line.GetProperty("Runs").GetArrayLength(); }
            if (metrics) _ = GetMetrics(root, item, font);
        }
        if (lines != (midpoint ? 426 : 386) || runs != (midpoint ? 546 : 450))
            throw new InvalidDataException("Original line/run inventory changed; no trimming is allowed.");
    }

    internal static IndependentSourceMetrics GetMetrics(JsonElement root, JsonElement item, string fontHash)
    {
        if (!item.TryGetProperty("IndependentSourceMetrics", out var value))
            throw new InvalidDataException("Missing genuine original Typeface Display input metrics; design ratios and line outputs cannot substitute.");
        double em = item.GetProperty("Em").GetDouble(), dpi = item.GetProperty("Dpi").GetDouble();
        double baseline = value.GetProperty("Baseline").GetDouble(), spacing = value.GetProperty("LineSpacing").GetDouble();
        if (value.GetProperty("Version").GetInt32() != 1 || value.GetProperty("Mode").GetString() != item.GetProperty("Mode").GetString() ||
            value.GetProperty("ToReal").GetDouble() != 1 || !Same(em, value.GetProperty("Em").GetDouble()) || !Same(dpi, value.GetProperty("Dpi").GetDouble()) ||
            value.GetProperty("Font").GetProperty("Sha256").GetString() != fontHash ||
            value.GetProperty("TypefaceIdentity").GetString() != root.GetProperty("PresentationIdentity").GetString() ||
            value.GetProperty("TypefaceAssembly").GetProperty("Sha256").GetString() != root.GetProperty("PresentationCore").GetProperty("Sha256").GetString() ||
            value.GetProperty("BaselineMethod").GetString() != "System.Windows.Media.Typeface.Baseline(Double,Double,Double,TextFormattingMode):Double" ||
            value.GetProperty("LineSpacingMethod").GetString() != "System.Windows.Media.Typeface.LineSpacing(Double,Double,Double,TextFormattingMode):Double" ||
            value.GetProperty("CaptureOrder").GetString() != "Before TextFormatter creation and FormatLine; original source request metrics, not line outputs." ||
            !double.IsFinite(em) || em <= 0 || !double.IsFinite(dpi) || dpi <= 0 ||
            !double.IsFinite(baseline) || !double.IsFinite(spacing) || baseline <= 0 || spacing < baseline)
            throw new InvalidDataException("Independent source metric identity or invocation changed.");
        return new(em, dpi, baseline, spacing);
    }
    private static bool Same(double left, double right) => BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);
}
