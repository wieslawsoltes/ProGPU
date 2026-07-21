using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Compute;
using ProGPU.Text;
using ProGPU.Text.Shaping;

return await HarfBuzzParityProgram.RunAsync(args);

internal static class HarfBuzzParityProgram
{
    private const int DefaultFailureDetails = 25;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        if (!string.Equals(args[0], "suite", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            PrintUsage();
            return 2;
        }

        SuiteOptions options;
        try
        {
            options = SuiteOptions.Parse(args[1..]);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            PrintUsage();
            return 2;
        }

        using var runner = new SuiteRunner(options);
        SuiteReport report = await runner.RunAsync();
        PrintSummary(report, options.MaxFailureDetails);

        if (options.ReportPath is not null)
        {
            string reportPath = Path.GetFullPath(options.ReportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await using FileStream stream = File.Create(reportPath);
            await JsonSerializer.SerializeAsync(stream, report, JsonContext.Default.SuiteReport);
            Console.WriteLine($"Report: {reportPath}");
        }

        return report.Failed == 0 && report.Unsupported == 0 && report.Errors == 0 ? 0 : 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            HarfBuzz OpenType differential conformance runner

            Usage:
              dotnet run --project tools/HarfBuzzParity -- suite \
                --harfbuzz-root PATH [--hb-shape PATH] [--filter GLOB-TEXT] \
                [--skip-cases N] [--max-cases N] [--report PATH] \
                [--backend cpu|gpu|both] [--verify] \
                [--max-failure-details N] [--progress-every N]

            The HarfBuzz root must contain test/shape/data/in-house. Unsupported
            OpenType behavior is reported separately and still makes the command fail.
            """);
    }

    private static void PrintSummary(SuiteReport report, int maxDetails)
    {
        Console.WriteLine($"HarfBuzz: {report.HarfBuzzVersion}");
        Console.WriteLine($"Corpus:   {report.CorpusRoot}");
        Console.WriteLine($"Cases:    {report.Total}");
        Console.WriteLine($"Passed:   {report.Passed}");
        Console.WriteLine($"Failed:   {report.Failed}");
        Console.WriteLine($"Unsupported: {report.Unsupported}");
        Console.WriteLine($"Non-OT:   {report.NonOpenType}");
        Console.WriteLine($"Errors:   {report.Errors}");

        foreach (CaseResult result in report.Results
                     .Where(static result => result.Status is CaseStatus.Failed or CaseStatus.Unsupported or CaseStatus.Error)
                     .Take(maxDetails))
        {
            Console.WriteLine($"{result.Status}: {result.TestFile}:{result.Line} - {result.Message}");
        }
    }
}

internal sealed record SuiteOptions(
    string HarfBuzzRoot,
    string HarfBuzzShape,
    string? Filter,
    int SkipCases,
    int? MaxCases,
    string? ReportPath,
    ShapingBackend Backend,
    bool Verify,
    int MaxFailureDetails,
    int ProgressEvery)
{
    public static SuiteOptions Parse(string[] args)
    {
        string? root = null;
        string hbShape = "hb-shape";
        string? filter = null;
        int skipCases = 0;
        int? maxCases = null;
        string? report = null;
        ShapingBackend backend = ShapingBackend.Cpu;
        bool verify = false;
        int maxDetails = 25;
        int progressEvery = 250;

        for (int index = 0; index < args.Length; index++)
        {
            string option = args[index];
            string Value()
            {
                if (++index >= args.Length)
                {
                    throw new ArgumentException($"Missing value for {option}.");
                }
                return args[index];
            }

            switch (option)
            {
                case "--harfbuzz-root": root = Value(); break;
                case "--hb-shape": hbShape = Value(); break;
                case "--filter": filter = Value(); break;
                case "--skip-cases": skipCases = ParseNonNegative(Value(), option); break;
                case "--max-cases": maxCases = ParsePositive(Value(), option); break;
                case "--report": report = Value(); break;
                case "--backend": backend = Value().ToLowerInvariant() switch
                {
                    "cpu" => ShapingBackend.Cpu,
                    "gpu" => ShapingBackend.Gpu,
                    "both" => ShapingBackend.Both,
                    var value => throw new ArgumentException($"Unknown shaping backend '{value}'.")
                }; break;
                case "--verify": verify = true; break;
                case "--max-failure-details": maxDetails = ParseNonNegative(Value(), option); break;
                case "--progress-every": progressEvery = ParsePositive(Value(), option); break;
                default: throw new ArgumentException($"Unknown option '{option}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("--harfbuzz-root is required.");
        }

        return new SuiteOptions(
            Path.GetFullPath(root), hbShape, filter, skipCases, maxCases, report, backend, verify, maxDetails, progressEvery);
    }

    private static int ParsePositive(string value, string option) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"{option} requires a positive integer.");

    private static int ParseNonNegative(string value, string option) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"{option} requires a non-negative integer.");
}

[Flags]
internal enum ShapingBackend
{
    Cpu = 1,
    Gpu = 2,
    Both = Cpu | Gpu
}

internal sealed class SuiteRunner : IDisposable
{
    private readonly SuiteOptions _options;
    private readonly Dictionary<(string Path, int FaceIndex), TtfFont> _fontCache = new();
    private readonly Dictionary<string, TtfFont> _variationFontCache = new(StringComparer.Ordinal);
    private readonly Dictionary<TtfFont, GpuOpenTypeFontData> _gpuFontCache = new();
    private readonly Dictionary<TtfFont, GpuOpenTypeShapingPlan> _gpuPlanCache = new();
    private WgpuContext? _gpuContext;
    private GpuOpenTypeRunPipeline? _gpuPipeline;

    public SuiteRunner(SuiteOptions options) => _options = options;

    public async Task<SuiteReport> RunAsync()
    {
        string corpusRoot = Path.Combine(_options.HarfBuzzRoot, "test", "shape", "data", "in-house");
        string testsRoot = Path.Combine(corpusRoot, "tests");
        if (!Directory.Exists(testsRoot))
        {
            throw new DirectoryNotFoundException($"HarfBuzz in-house shaping tests were not found at '{testsRoot}'.");
        }

        string version = await GetHarfBuzzVersionAsync();
        var results = new List<CaseResult>();
        int discoveredCases = 0;
        IEnumerable<string> files = Directory.EnumerateFiles(testsRoot, "*.tests", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(_options.Filter))
        {
            files = files.Where(path => Path.GetFileName(path).Contains(_options.Filter, StringComparison.OrdinalIgnoreCase));
        }

        foreach (string file in files)
        {
            int lineNumber = 0;
            bool openTypeEnabled = !IsAatOnlyFile(file);
            foreach (string line in File.ReadLines(file))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line) || line.AsSpan().TrimStart().StartsWith("#"))
                {
                    continue;
                }
                if (line[0] == '@')
                {
                    openTypeEnabled = ApplyDirective(line, openTypeEnabled);
                    continue;
                }

                if (discoveredCases++ < _options.SkipCases)
                {
                    continue;
                }

                if (_options.MaxCases is int limit && results.Count >= limit)
                {
                    return CreateReport(corpusRoot, version, results);
                }

                TestCase? testCase = TestCase.TryParse(file, lineNumber, line, out string? parseError);
                if (testCase is null)
                {
                    results.Add(CaseResult.Error(Path.GetFileName(file), lineNumber, parseError!));
                    continue;
                }

                if (!openTypeEnabled)
                {
                    results.Add(CaseResult.NonOpenType(testCase, "file shaper/backend directive"));
                    continue;
                }

                int shardCase = results.Count + 1;
                if (shardCase % _options.ProgressEvery == 0)
                {
                    Console.Error.WriteLine(
                        $"Starting: source case {discoveredCases}, shard case {shardCase}, {Path.GetFileName(file)}:{lineNumber}");
                }
                results.Add(await RunCaseAsync(testCase));
            }
        }

        return CreateReport(corpusRoot, version, results);
    }

    private static bool IsAatOnlyFile(string path) =>
        Path.GetFileName(path).StartsWith("aat-", StringComparison.OrdinalIgnoreCase);

    private static bool ApplyDirective(string line, bool current)
    {
        int comment = line.IndexOf('#');
        string directive = (comment >= 0 ? line[..comment] : line).Trim();
        if (directive.StartsWith("@shapers=", StringComparison.Ordinal))
        {
            return ContainsBackend(directive[9..], "ot");
        }
        if (directive.StartsWith("@shapers-=", StringComparison.Ordinal))
        {
            return current && !ContainsBackend(directive[10..], "ot");
        }
        if (directive.StartsWith("@font-funcs=", StringComparison.Ordinal))
        {
            return current && ContainsBackend(directive[12..], "ot");
        }
        if (directive.StartsWith("@font-funcs-=", StringComparison.Ordinal))
        {
            return current && !ContainsBackend(directive[13..], "ot");
        }
        if (directive.StartsWith("@face-loaders=", StringComparison.Ordinal))
        {
            return current && ContainsBackend(directive[14..], "ot");
        }
        if (directive.StartsWith("@face-loaders-=", StringComparison.Ordinal))
        {
            return current && !ContainsBackend(directive[15..], "ot");
        }
        return current;
    }

    private static bool ContainsBackend(string values, string backend) =>
        values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(backend, StringComparer.Ordinal);

    private async Task<CaseResult> RunCaseAsync(TestCase testCase)
    {
        CaseConfiguration configuration = CaseConfiguration.Parse(testCase.Options);
        if (configuration.NonOpenTypeReason is not null)
        {
            return CaseResult.NonOpenType(testCase, configuration.NonOpenTypeReason);
        }
        if (configuration.UnsupportedReasons.Count != 0)
        {
            return CaseResult.Unsupported(testCase, string.Join(", ", configuration.UnsupportedReasons));
        }
        if ((_options.Backend & ShapingBackend.Gpu) != 0 && configuration.HasItemContext)
        {
            return CaseResult.Unsupported(testCase, "gpu-item-context");
        }
        if ((_options.Backend & ShapingBackend.Gpu) != 0 && configuration.HasGlyphFlagRequest)
        {
            return CaseResult.Unsupported(testCase, "gpu-glyph-flags");
        }
        if (string.Equals(Path.GetExtension(testCase.FontPath), ".dfont", StringComparison.OrdinalIgnoreCase))
        {
            return CaseResult.Unsupported(testCase, "font-container:dfont");
        }
        if (!File.Exists(testCase.FontPath))
        {
            return CaseResult.Error(testCase, $"Font not found: {testCase.FontPath}");
        }

        try
        {
            TtfFont font = GetFont(testCase.FontPath, configuration);
            if ((font.TryGetTable("morx", out _) || font.TryGetTable("mort", out _)) &&
                !font.TryGetTable("GSUB", out _))
            {
                return CaseResult.NonOpenType(testCase, "AAT substitution table without GSUB");
            }
            ReferenceGlyph[] expected = await ShapeWithHarfBuzzAsync(testCase);

            string? mismatch = null;
            if ((_options.Backend & ShapingBackend.Cpu) != 0)
            {
                mismatch = Compare(expected, ShapeCpu(testCase, configuration, font), configuration, testCase.Text);
                if (mismatch is not null) mismatch = $"CPU: {mismatch}";
            }
            if (mismatch is null && (_options.Backend & ShapingBackend.Gpu) != 0)
            {
                mismatch = Compare(expected, ShapeGpu(testCase, configuration, font), configuration, testCase.Text);
                if (mismatch is not null) mismatch = $"GPU: {mismatch}";
            }
            return mismatch is null
                ? CaseResult.Passed(testCase)
                : CaseResult.Failed(testCase, mismatch);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CaseResult.Error(testCase, exception.ToString());
        }
    }

    private IReadOnlyList<ShapedGlyph> ShapeCpu(
        TestCase testCase,
        CaseConfiguration configuration,
        TtfFont font)
    {
        if (configuration.ClusterLevel is null && !configuration.RequiresContractShape && !_options.Verify)
        {
            return OpenTypeTextShaper.Shape(
                testCase.Text,
                font,
                configuration.FontSize ?? font.UnitsPerEm,
                configuration.CreateShapingOptions());
        }

        using var buffer = new ShapingBuffer();
        ShapingClusterLevel clusterLevel = configuration.ClusterLevel ?? ShapingClusterLevel.MonotoneGraphemes;
        ShapingRequest request = CreateRequest(
            testCase,
            configuration,
            clusterLevel,
            _options.Verify ? ShapingBufferFlags.Verify : ShapingBufferFlags.None);
        CpuOpenTypeShaper.Instance.Shape(testCase.Text, new TtfShapingFontFace(font), request, buffer);
        return Convert(
            buffer,
            configuration.FontSize ?? font.UnitsPerEm,
            font,
            request.Direction);
    }

    private IReadOnlyList<ShapedGlyph> ShapeGpu(
        TestCase testCase,
        CaseConfiguration configuration,
        TtfFont font)
    {
        EnsureGpu();
        if (!_gpuFontCache.TryGetValue(font, out GpuOpenTypeFontData? fontData))
        {
            GpuOpenTypeShapingPlan compiled = GpuOpenTypeShapingPlanCompiler.Compile(new TtfShapingFontFace(font));
            fontData = new GpuOpenTypeFontData(_gpuContext!, compiled);
            _gpuFontCache.Add(font, fontData);
            _gpuPlanCache.Add(font, compiled);
        }
        GpuOpenTypeShapingPlan plan = _gpuPlanCache[font];
        ShapingRequest request = CreateRequest(
            testCase, configuration, configuration.ClusterLevel ?? ShapingClusterLevel.MonotoneGraphemes);
        GpuOpenTypeLookupCommand[] commands = GpuOpenTypeLookupPlanCompiler.Compile(plan, request);
        var input = new List<GpuShapingScalar>(testCase.Text.Length);
        bool characterClusters = request.ClusterLevel is
            ShapingClusterLevel.MonotoneCharacters or ShapingClusterLevel.Characters;
        OpenTypeTag layoutScript = GpuOpenTypeLookupPlanCompiler.ResolveLayoutScript(plan, request.Script);
        bool preserveUseMarkOrder = OpenTypeScriptResolver.UsesUniversalShapingEngine(layoutScript);
        bool preserveDefaultIgnorableCluster = font.GetGlyphIndex(' ') != 0;
        string script = request.Script.ToString();
        bool indicShaper = !preserveUseMarkOrder && IsIndicShaperScript(script);
        bool khmerShaper = script == "khmr";
        uint indicVirama = GetIndicVirama(script);
        int useCluster = 0;
        bool hasUseCluster = false;
        for (int graphemeStart = 0; graphemeStart < testCase.Text.Length;)
        {
            int graphemeLength = StringInfo.GetNextTextElementLength(testCase.Text.AsSpan(graphemeStart));
            int graphemeEnd = checked(graphemeStart + graphemeLength);
            Rune.DecodeFromUtf16(testCase.Text.AsSpan(graphemeStart, graphemeLength), out Rune firstRune, out _);
            bool separateClusters = IsPrepend(firstRune.Value);
            int indicCluster = graphemeStart;
            uint previousCodePoint = 0;
            bool splitAfterIndicZwnj = false;
            for (int utf16 = graphemeStart; utf16 < graphemeEnd;)
            {
                Rune.DecodeFromUtf16(testCase.Text.AsSpan(utf16), out Rune rune, out int consumed);
                uint followingCodePoint = 0;
                if (utf16 + consumed < graphemeEnd &&
                    Rune.DecodeFromUtf16(testCase.Text.AsSpan(utf16 + consumed, graphemeEnd - utf16 - consumed),
                        out Rune followingRune, out _) == OperationStatus.Done)
                {
                    followingCodePoint = checked((uint)followingRune.Value);
                }
                if (splitAfterIndicZwnj)
                {
                    indicCluster = utf16;
                    splitAfterIndicZwnj = false;
                }
                if (indicShaper &&
                    (rune.Value == 0x200c || rune.Value == 0x200d &&
                     previousCodePoint != indicVirama && followingCodePoint != indicVirama))
                {
                    indicCluster = utf16;
                    splitAfterIndicZwnj = rune.Value == 0x200c && followingCodePoint != 0 &&
                        !UnicodeShapingProperties.IsMark(followingCodePoint);
                }
                else if (khmerShaper &&
                         (rune.Value is 0x200c or 0x200d ||
                          previousCodePoint == 0x17d2 && IsKhmerBaseCategory(checked((uint)rune.Value))))
                {
                    indicCluster = utf16;
                }
                int cluster = characterClusters
                    ? utf16
                    : separateClusters || script == "hang"
                        ? utf16
                        : indicShaper || khmerShaper ? indicCluster : graphemeStart;
                if (preserveUseMarkOrder)
                {
                    if (preserveDefaultIgnorableCluster && rune.Value is 0x200c or 0x200d)
                        cluster = utf16;
                    UnicodeCategory category = Rune.GetUnicodeCategory(rune);
                    bool mark = category is UnicodeCategory.NonSpacingMark or
                        UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
                    if (mark && hasUseCluster) cluster = useCluster;
                    else
                    {
                        useCluster = cluster;
                        hasUseCluster = true;
                    }
                }
                if (rune.Value == 0x200d && input.Count != 0) cluster = input[^1].Cluster;
                input.Add(new GpuShapingScalar(
                    checked((uint)rune.Value), cluster));
                previousCodePoint = checked((uint)rune.Value);
                utf16 += Math.Max(consumed, 1);
            }
            graphemeStart = graphemeEnd;
        }
        using var buffer = new ShapingBuffer();
        _gpuPipeline!.ExecuteRun(input.ToArray(), fontData, request, commands, buffer);
        return Convert(
            buffer,
            configuration.FontSize ?? font.UnitsPerEm,
            font,
            request.Direction);
    }

    private static bool IsPrepend(int codePoint) => codePoint is
        >= 0x0600 and <= 0x0605 or 0x06dd or 0x070f or
        >= 0x0890 and <= 0x0891 or 0x08e2 or 0x0d4e or
        0x110bd or 0x110cd or >= 0x111c2 and <= 0x111c3 or
        0x1193f or 0x11941 or 0x11a3a or >= 0x11a84 and <= 0x11a89 or
        0x11d46;

    private static bool IsIndicShaperScript(string script) => script is
        "beng" or "deva" or "gujr" or "guru" or "knda" or "mlym" or "orya" or "taml" or "telu";

    private static uint GetIndicVirama(string script) => script switch
    {
        "deva" => 0x094d, "beng" => 0x09cd, "guru" => 0x0a4d, "gujr" => 0x0acd,
        "orya" => 0x0b4d, "taml" => 0x0bcd, "telu" => 0x0c4d, "knda" => 0x0ccd,
        "mlym" => 0x0d4d, _ => 0
    };

    private static bool IsKhmerBaseCategory(uint codePoint)
    {
        byte category = (byte)(UnicodeShapingProperties.GetIndicProperties(codePoint) & 0xff);
        return category is 1 or 2 or 10 or 11 or 15;
    }

    private static ShapingRequest CreateRequest(
        TestCase testCase,
        CaseConfiguration configuration,
        ShapingClusterLevel clusterLevel,
        ShapingBufferFlags additionalFlags = ShapingBufferFlags.None)
    {
        ShapingDirection direction = configuration.Direction == ShapingDirection.Unspecified
            ? InferDirection(testCase.Text)
            : configuration.Direction;
        OpenTypeTag script = configuration.Script is { Length: 4 } scriptTag
            ? new OpenTypeTag(scriptTag)
            : InferScript(testCase.Text);
        var features = new List<ShapingFeature>(
            configuration.Features.Count + configuration.RangedFeatures.Count);
        features.AddRange(configuration.Features.Select(static feature =>
            new ShapingFeature(new OpenTypeTag(feature.Tag), checked((uint)Math.Max(feature.Value, 0)))));
        foreach (CorpusRangedFeature feature in configuration.RangedFeatures)
        {
            uint start = ScalarIndexToUtf16Offset(testCase.Text, feature.Start, endInclusive: false);
            uint end = feature.EndInclusive == int.MaxValue
                ? uint.MaxValue
                : ScalarIndexToUtf16Offset(testCase.Text, feature.EndInclusive, endInclusive: true);
            features.Add(new ShapingFeature(
                new OpenTypeTag(feature.Tag),
                checked((uint)Math.Max(feature.Value, 0)),
                start,
                end));
        }
        return new ShapingRequest(
            direction,
            script,
            configuration.Language,
            clusterLevel,
            configuration.BufferFlags | additionalFlags,
            features.ToArray(),
            configuration.PreContext.AsMemory(),
            configuration.PostContext.AsMemory());
    }

    private static uint ScalarIndexToUtf16Offset(string text, int scalarIndex, bool endInclusive)
    {
        int scalarCount = 0;
        foreach (Rune _ in text.EnumerateRunes()) scalarCount++;
        int target = scalarIndex < 0 ? scalarCount + scalarIndex : scalarIndex;
        if (endInclusive) target++;
        target = Math.Clamp(target, 0, scalarCount);
        if (target == scalarCount) return checked((uint)text.Length);

        int scalar = 0;
        int utf16 = 0;
        while (scalar < target)
        {
            Rune.DecodeFromUtf16(text.AsSpan(utf16), out _, out int consumed);
            utf16 += Math.Max(consumed, 1);
            scalar++;
        }
        return checked((uint)utf16);
    }

    private static OpenTypeTag InferScript(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            OpenTypeTag script = OpenTypeScriptResolver.GetScript(checked((uint)rune.Value));
            if (script != OpenTypeTag.DefaultScript) return script;
        }
        return OpenTypeTag.DefaultScript;
    }

    private static IReadOnlyList<ShapedGlyph> Convert(
        ShapingBuffer buffer,
        float size,
        TtfFont font,
        ShapingDirection direction)
    {
        float scale = size / font.UnitsPerEm;
        bool vertical = direction is ShapingDirection.TopToBottom or ShapingDirection.BottomToTop;
        return buffer.Glyphs.ToArray().Select(glyph =>
        {
            float advanceY = glyph.AdvanceY * scale;
            float offsetX = glyph.OffsetX * scale;
            float offsetY = glyph.OffsetY * scale;
            if (vertical && scale != 1f)
            {
                ushort glyphId = checked((ushort)glyph.GlyphId);
                int baseAdvanceY = -(int)MathF.Round(font.GetAdvanceHeight(glyphId, font.UnitsPerEm));
                int scaledAdvanceY = -(int)MathF.Round(font.GetAdvanceHeight(glyphId, size));
                advanceY = (glyph.AdvanceY - baseAdvanceY) * scale + scaledAdvanceY;

                int baseOffsetX = -((int)MathF.Round(font.GetAdvanceWidth(glyphId, font.UnitsPerEm)) / 2);
                int scaledOffsetX = -((int)MathF.Round(font.GetAdvanceWidth(glyphId, size)) / 2);
                offsetX = (glyph.OffsetX - baseOffsetX) * scale + scaledOffsetX;

                int baseOffsetY = -(int)MathF.Round(font.GetVerticalOriginY(glyphId, font.UnitsPerEm));
                int scaledOffsetY = -(int)MathF.Round(font.GetVerticalOriginY(glyphId, size));
                offsetY = (glyph.OffsetY - baseOffsetY) * scale + scaledOffsetY;
            }
            return new ShapedGlyph(
                checked((ushort)glyph.GlyphId), glyph.Cluster, glyph.CodePoint,
                glyph.AdvanceX * scale, advanceY, offsetX, offsetY, glyph.Flags);
        }).ToArray();
    }

    private void EnsureGpu()
    {
        if (_gpuContext is not null) return;
        _gpuContext = new WgpuContext();
        _gpuContext.Initialize(null);
        _gpuPipeline = new GpuOpenTypeRunPipeline(_gpuContext);
    }

    public void Dispose()
    {
        foreach (GpuOpenTypeFontData font in _gpuFontCache.Values) font.Dispose();
        _gpuPipeline?.Dispose();
        _gpuContext?.Dispose();
    }

    private static ShapingDirection InferDirection(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            OpenTypeTag scriptTag = OpenTypeScriptResolver.GetScript(checked((uint)rune.Value));
            if (scriptTag == OpenTypeTag.DefaultScript) continue;
            string script = scriptTag.ToString();
            return script is "arab" or "hebr" or "syrc" or "thaa" or "nko " or "nkoo" or "adlm" or "rohg"
                ? ShapingDirection.RightToLeft
                : ShapingDirection.LeftToRight;
        }
        return ShapingDirection.LeftToRight;
    }

    private TtfFont GetFont(string path, CaseConfiguration configuration)
    {
        var faceKey = (path, configuration.FaceIndex);
        if (!_fontCache.TryGetValue(faceKey, out TtfFont? baseFont))
        {
            baseFont = configuration.FaceIndex == 0
                ? new TtfFont(path)
                : new TtfFont(path, configuration.FaceIndex);
            _fontCache.Add(faceKey, baseFont);
        }

        if (configuration.Variations.Count == 0)
        {
            return baseFont;
        }

        string variationKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{path}\u001f{configuration.FaceIndex}\u001f{string.Join(',', configuration.Variations.Select(static value => $"{value.Tag}={value.Value:R}"))}");
        if (!_variationFontCache.TryGetValue(variationKey, out TtfFont? variationFont))
        {
            variationFont = baseFont.WithVariations(configuration.Variations.ToArray());
            _variationFontCache.Add(variationKey, variationFont);
        }
        return variationFont;
    }

    private async Task<ReferenceGlyph[]> ShapeWithHarfBuzzAsync(TestCase testCase)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _options.HarfBuzzShape,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        process.StartInfo.ArgumentList.Add(testCase.FontPath);
        foreach (string option in OptionTokenizer.Tokenize(testCase.Options))
        {
            if (option.StartsWith("--shaper=", StringComparison.Ordinal) ||
                option.StartsWith("--shapers=", StringComparison.Ordinal) ||
                option == "--no-glyph-names")
            {
                continue;
            }
            process.StartInfo.ArgumentList.Add(option);
        }
        process.StartInfo.ArgumentList.Add("--shapers=ot");
        process.StartInfo.ArgumentList.Add("--no-glyph-names");
        process.StartInfo.ArgumentList.Add("--output-format=json");
        process.StartInfo.ArgumentList.Add($"--unicodes={testCase.UnicodeArgument}");

        process.Start();
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"hb-shape exited with {process.ExitCode}: {error.Trim()}");
        }

        return JsonSerializer.Deserialize(output, JsonContext.Default.ReferenceGlyphArray) ?? [];
    }

    private async Task<string> GetHarfBuzzVersionAsync()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _options.HarfBuzzShape,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add("--version");
        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Unable to execute '{_options.HarfBuzzShape}'.");
        }
        return output.Trim();
    }

    private static string? Compare(
        IReadOnlyList<ReferenceGlyph> expected,
        IReadOnlyList<ShapedGlyph> actual,
        CaseConfiguration configuration,
        string text)
    {
        if (expected.Count != actual.Count)
        {
            return $"glyph count expected {expected.Count}, actual {actual.Count}; " +
                   $"expected [{string.Join(',', expected.Select(static glyph => glyph.Glyph))}], " +
                   $"actual [{string.Join('|', actual.Select(static glyph => $"{glyph.GlyphIndex}/{glyph.CodePoint:X}:{Round(glyph.AdvanceX)},{Round(-glyph.AdvanceY)},{Round(glyph.OffsetX)},{Round(-glyph.OffsetY)}"))}]";
        }

        for (int index = 0; index < expected.Count; index++)
        {
            ReferenceGlyph left = expected[index];
            ShapedGlyph right = actual[index];
            if (left.Glyph != right.GlyphIndex)
            {
                return $"glyph[{index}] expected gid {left.Glyph}, actual {right.GlyphIndex}; " +
                       $"expected [{string.Join(',', expected.Select(static glyph => glyph.Glyph))}], " +
                       $"actual [{string.Join('|', actual.Select(static glyph => $"{glyph.GlyphIndex}/{glyph.CodePoint:X}:{Round(glyph.AdvanceX)},{Round(-glyph.AdvanceY)},{Round(glyph.OffsetX)},{Round(-glyph.OffsetY)}"))}]";
            }
            int actualCluster = Utf16ClusterToScalarIndex(text, right.Cluster);
            if (!configuration.IgnoreClusters && left.Cluster != actualCluster)
            {
                return $"glyph[{index}] cluster expected {left.Cluster}, actual {actualCluster}; " +
                       $"expected [{string.Join(',', expected.Select(static glyph => glyph.Cluster))}], " +
                       $"actual [{string.Join(',', actual.Select(glyph => Utf16ClusterToScalarIndex(text, glyph.Cluster)))}]; " +
                       $"actual glyph/codepoint [{string.Join(',', actual.Select(static glyph => $"{glyph.GlyphIndex}/{glyph.CodePoint:X}"))}]";
            }
            if (!configuration.IgnorePositions &&
                ((!configuration.IgnoreAdvances &&
                  (left.AdvanceX != Round(right.AdvanceX) ||
                   left.AdvanceY != Round(-right.AdvanceY))) ||
                 left.OffsetX != Round(right.OffsetX) ||
                 left.OffsetY != Round(-right.OffsetY)))
            {
                return $"glyph[{index}] position expected ({left.AdvanceX},{left.AdvanceY},{left.OffsetX},{left.OffsetY}), " +
                       $"actual ({Round(right.AdvanceX)},{Round(-right.AdvanceY)},{Round(right.OffsetX)},{Round(-right.OffsetY)}); " +
                       $"expected [{string.Join('|', expected.Select(static glyph => $"{glyph.Glyph}:{glyph.AdvanceX},{glyph.AdvanceY},{glyph.OffsetX},{glyph.OffsetY}"))}], " +
                       $"actual [{string.Join('|', actual.Select(static glyph => $"{glyph.GlyphIndex}/{glyph.CodePoint:X}:{Round(glyph.AdvanceX)},{Round(-glyph.AdvanceY)},{Round(glyph.OffsetX)},{Round(-glyph.OffsetY)}"))}]";
            }
            if (configuration.ShowFlags && left.Flags != (uint)right.Flags)
            {
                return $"glyph[{index}] flags expected {left.Flags}, actual {(uint)right.Flags}";
            }
        }
        return null;
    }

    private static int Utf16ClusterToScalarIndex(string text, int utf16Index)
    {
        int limit = Math.Clamp(utf16Index, 0, text.Length);
        int scalarIndex = 0;
        for (int index = 0; index < limit; scalarIndex++)
        {
            index += Rune.DecodeFromUtf16(text.AsSpan(index), out _, out int consumed) == System.Buffers.OperationStatus.Done
                ? consumed
                : 1;
        }
        return scalarIndex;
    }

    private static int Round(float value) => checked((int)MathF.Round(value));

    private static SuiteReport CreateReport(string corpusRoot, string version, List<CaseResult> results) => new()
    {
        HarfBuzzVersion = version,
        CorpusRoot = corpusRoot,
        Total = results.Count,
        Passed = results.Count(static result => result.Status == CaseStatus.Passed),
        Failed = results.Count(static result => result.Status == CaseStatus.Failed),
        Unsupported = results.Count(static result => result.Status == CaseStatus.Unsupported),
        NonOpenType = results.Count(static result => result.Status == CaseStatus.NonOpenType),
        Errors = results.Count(static result => result.Status == CaseStatus.Error),
        Results = results
    };
}

internal sealed class CaseConfiguration
{
    public string? NonOpenTypeReason { get; private set; }
    public List<string> UnsupportedReasons { get; } = [];
    public List<OpenTypeFeatureSetting> Features { get; } = [];
    public List<CorpusRangedFeature> RangedFeatures { get; } = [];
    public List<FontVariationSetting> Variations { get; } = [];
    public string? Script { get; private set; }
    public string? Language { get; private set; }
    public ProGPU.Text.Shaping.ShapingDirection Direction { get; private set; }
    public int FaceIndex { get; private set; }
    public float? FontSize { get; private set; }
    public bool IgnorePositions { get; private set; }
    public bool IgnoreAdvances { get; private set; }
    public bool IgnoreClusters { get; private set; }
    public ShapingClusterLevel? ClusterLevel { get; private set; }
    public ShapingBufferFlags BufferFlags { get; private set; }
    public string PreContext { get; private set; } = string.Empty;
    public string PostContext { get; private set; } = string.Empty;
    public bool HasItemContext =>
        (BufferFlags & (ShapingBufferFlags.BeginningOfText | ShapingBufferFlags.EndOfText)) != 0 ||
        PreContext.Length != 0 || PostContext.Length != 0;
    public bool RequiresContractShape => BufferFlags != ShapingBufferFlags.None || RangedFeatures.Count != 0 ||
        PreContext.Length != 0 || PostContext.Length != 0;
    public bool HasGlyphFlagRequest =>
        (BufferFlags & (ShapingBufferFlags.ProduceUnsafeToConcat |
                        ShapingBufferFlags.ProduceSafeToInsertTatweel)) != 0 || ShowFlags;
    public bool ShowFlags { get; private set; }

    public static CaseConfiguration Parse(string options)
    {
        var result = new CaseConfiguration();
        foreach (string option in OptionTokenizer.Tokenize(options))
        {
            if (option is "--shaper=fallback" or "--shapers=fallback" or
                "--shaper=graphite2" or "--shapers=graphite2" or
                "--shaper=coretext" or "--shapers=coretext" ||
                option is "--font-funcs=ft" or "--font-funcs=coretext" or "--font-funcs=fallback")
            {
                result.NonOpenTypeReason = option;
                continue;
            }

            if (option is "--shaper=ot" or "--shapers=ot" or "--font-funcs=ot" or "--no-glyph-names")
            {
                continue;
            }
            if (option == "--no-positions")
            {
                result.IgnorePositions = true;
                continue;
            }
            if (option == "--no-advances")
            {
                result.IgnoreAdvances = true;
                continue;
            }
            if (option == "--no-clusters")
            {
                result.IgnoreClusters = true;
                continue;
            }
            if (option == "--ned")
            {
                result.IgnoreAdvances = true;
                result.IgnoreClusters = true;
                continue;
            }
            if (TryValue(option, "--features=", out string? features))
            {
                ParseFeatures(features, result);
                continue;
            }
            if (TryValue(option, "--variations=", out string? variations))
            {
                ParseVariations(variations, result);
                continue;
            }
            if (TryValue(option, "--script=", out string? script))
            {
                result.Script = script.Length >= 4 ? script[..4].ToLowerInvariant() : script.ToLowerInvariant();
                continue;
            }
            if (TryValue(option, "--language=", out string? language))
            {
                result.Language = language;
                continue;
            }
            if (TryValue(option, "--direction=", out string? direction))
            {
                result.Direction = direction.ToLowerInvariant() switch
                {
                    "l" or "ltr" => ProGPU.Text.Shaping.ShapingDirection.LeftToRight,
                    "r" or "rtl" => ProGPU.Text.Shaping.ShapingDirection.RightToLeft,
                    "t" or "ttb" => ProGPU.Text.Shaping.ShapingDirection.TopToBottom,
                    "b" or "btt" => ProGPU.Text.Shaping.ShapingDirection.BottomToTop,
                    _ => ProGPU.Text.Shaping.ShapingDirection.Unspecified
                };
                if (result.Direction == ProGPU.Text.Shaping.ShapingDirection.Unspecified)
                {
                    AddUnsupported(result, $"direction:{direction}");
                }
                continue;
            }
            if (TryValue(option, "--face-index=", out string? faceIndex) &&
                int.TryParse(faceIndex, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedFaceIndex))
            {
                result.FaceIndex = parsedFaceIndex;
                continue;
            }
            if (TryValue(option, "--font-size=", out string? fontSize))
            {
                if (!string.Equals(fontSize, "upem", StringComparison.OrdinalIgnoreCase) &&
                    float.TryParse(fontSize.Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedSize))
                {
                    result.FontSize = parsedSize;
                }
                continue;
            }
            if (TryValue(option, "--cluster-level=", out string? clusterLevel) &&
                int.TryParse(clusterLevel, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedClusterLevel) &&
                Enum.IsDefined((ShapingClusterLevel)parsedClusterLevel))
            {
                result.ClusterLevel = (ShapingClusterLevel)parsedClusterLevel;
                continue;
            }

            if (option == "--bot") result.BufferFlags |= ShapingBufferFlags.BeginningOfText;
            else if (option == "--eot") result.BufferFlags |= ShapingBufferFlags.EndOfText;
            else if (TryValue(option, "--unicodes-before=", out string? before))
            {
                if (TryParseUnicodeSequence(before, out string decoded)) result.PreContext = decoded;
                else AddUnsupported(result, "item-context");
            }
            else if (TryValue(option, "--unicodes-after=", out string? after))
            {
                if (TryParseUnicodeSequence(after, out string decoded)) result.PostContext = decoded;
                else AddUnsupported(result, "item-context");
            }
            else if (option == "--preserve-default-ignorables")
                result.BufferFlags |= ShapingBufferFlags.PreserveDefaultIgnorables;
            else if (option == "--remove-default-ignorables")
                result.BufferFlags |= ShapingBufferFlags.RemoveDefaultIgnorables;
            else if (option == "--unsafe-to-concat") result.BufferFlags |= ShapingBufferFlags.ProduceUnsafeToConcat;
            else if (option == "--safe-to-insert-tatweel") result.BufferFlags |= ShapingBufferFlags.ProduceSafeToInsertTatweel;
            else if (option == "--show-flags") result.ShowFlags = true;
            else if (option is "--show-extents") AddUnsupported(result, "glyph-extents");
            else if (option.StartsWith("--font-ptem=", StringComparison.Ordinal)) AddUnsupported(result, "font-ptem");
            else if (option.StartsWith("--font-bold=", StringComparison.Ordinal)) AddUnsupported(result, "synthetic-bold");
            else if (option.StartsWith("--font-slant=", StringComparison.Ordinal)) AddUnsupported(result, "synthetic-slant");
            else if (option.StartsWith("--not-found-variation-selector-glyph=", StringComparison.Ordinal)) AddUnsupported(result, "missing-variation-selector");
            else AddUnsupported(result, $"option:{option}");
        }
        return result;
    }

    public TextShapingOptions CreateShapingOptions()
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (OpenTypeFeatureSetting feature in TextShapingOptions.DefaultFeatures)
        {
            values[feature.Tag] = feature.Value;
        }
        foreach (OpenTypeFeatureSetting feature in Features)
        {
            values[feature.Tag] = feature.Value;
        }
        return new TextShapingOptions
        {
            Script = Script,
            Language = Language,
            Direction = Direction,
            Features = values.Select(static pair => new OpenTypeFeatureSetting(pair.Key, pair.Value)).ToArray(),
            ExplicitFeatureTags = Features.Select(static feature => feature.Tag).ToHashSet(StringComparer.Ordinal)
        };
    }

    private static bool TryParseUnicodeSequence(string value, out string decoded)
    {
        var text = new StringBuilder();
        foreach (string rawCodePoint in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string scalar = rawCodePoint.StartsWith("U+", StringComparison.OrdinalIgnoreCase)
                ? rawCodePoint[2..]
                : rawCodePoint;
            if (!int.TryParse(scalar, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int codePoint) ||
                !Rune.IsValid(codePoint))
            {
                decoded = string.Empty;
                return false;
            }
            text.Append(char.ConvertFromUtf32(codePoint));
        }
        decoded = text.ToString();
        return true;
    }

    private static void ParseFeatures(string value, CaseConfiguration result)
    {
        foreach (string token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string setting = token;
            int enabled = 1;
            if (setting[0] == '-')
            {
                enabled = 0;
                setting = setting[1..];
            }
            else if (setting[0] == '+')
            {
                setting = setting[1..];
            }
            int rangeStart = 0;
            int rangeEndInclusive = int.MaxValue;
            bool ranged = false;
            int openBracket = setting.IndexOf('[');
            if (openBracket >= 0)
            {
                int closeBracket = setting.IndexOf(']', openBracket + 1);
                if (closeBracket < 0 || !TryParseFeatureRange(
                        setting.AsSpan(openBracket + 1, closeBracket - openBracket - 1),
                        out rangeStart,
                        out rangeEndInclusive))
                {
                    AddUnsupported(result, $"feature:{token}");
                    continue;
                }
                setting = string.Concat(setting.AsSpan(0, openBracket), setting.AsSpan(closeBracket + 1));
                ranged = true;
            }
            int equals = setting.IndexOf('=');
            if (equals >= 0)
            {
                if (!int.TryParse(setting.AsSpan(equals + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out enabled))
                {
                    AddUnsupported(result, $"feature:{token}");
                    continue;
                }
                setting = setting[..equals];
            }
            if (setting.Length != 4)
            {
                AddUnsupported(result, $"feature:{token}");
                continue;
            }
            if (ranged)
                result.RangedFeatures.Add(new CorpusRangedFeature(setting, enabled, rangeStart, rangeEndInclusive));
            else
                result.Features.Add(new OpenTypeFeatureSetting(setting, enabled));
        }
    }

    private static bool TryParseFeatureRange(
        ReadOnlySpan<char> value,
        out int start,
        out int endInclusive)
    {
        start = 0;
        endInclusive = int.MaxValue;
        int colon = value.IndexOf(':');
        if (colon < 0)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out start))
                return false;
            endInclusive = start;
            return true;
        }

        ReadOnlySpan<char> startText = value[..colon];
        ReadOnlySpan<char> endText = value[(colon + 1)..];
        if (!startText.IsEmpty &&
            !int.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out start))
            return false;
        return endText.IsEmpty ||
            int.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out endInclusive);
    }

    private static void ParseVariations(string value, CaseConfiguration result)
    {
        foreach (string token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = token.IndexOf('=');
            if (equals != 4 || !float.TryParse(token.AsSpan(equals + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float axisValue))
            {
                AddUnsupported(result, $"variation:{token}");
                continue;
            }
            result.Variations.Add(new FontVariationSetting(token[..4], axisValue));
        }
    }

    private static bool TryValue(string option, string prefix, out string value)
    {
        if (option.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = option[prefix.Length..];
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static void AddUnsupported(CaseConfiguration result, string reason)
    {
        if (!result.UnsupportedReasons.Contains(reason, StringComparer.Ordinal))
        {
            result.UnsupportedReasons.Add(reason);
        }
    }
}

internal readonly record struct CorpusRangedFeature(
    string Tag,
    int Value,
    int Start,
    int EndInclusive);

internal sealed record TestCase(
    string TestFile,
    int Line,
    string FontPath,
    string Options,
    string Text,
    string UnicodeArgument)
{
    public static TestCase? TryParse(string testFile, int lineNumber, string line, out string? error)
    {
        string[] fields = line.Split(';', 4);
        if (fields.Length != 4)
        {
            error = "Expected four semicolon-delimited fields.";
            return null;
        }

        string font = fields[0].Replace("\\ ", " ", StringComparison.Ordinal);
        int expectedHashSeparator = font.LastIndexOf('@');
        if (expectedHashSeparator >= 0)
        {
            font = font[..expectedHashSeparator];
        }
        string fontPath = Path.GetFullPath(font, Path.GetDirectoryName(testFile)!);
        var text = new StringBuilder();
        var unicode = new List<string>();
        foreach (string rawCodePoint in fields[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string value = rawCodePoint.StartsWith("U+", StringComparison.OrdinalIgnoreCase) ? rawCodePoint[2..] : rawCodePoint;
            if (!int.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int codePoint) || !Rune.IsValid(codePoint))
            {
                error = $"Invalid Unicode scalar '{rawCodePoint}'.";
                return null;
            }
            text.Append(char.ConvertFromUtf32(codePoint));
            unicode.Add(codePoint.ToString("X", CultureInfo.InvariantCulture));
        }

        error = null;
        return new TestCase(Path.GetFileName(testFile), lineNumber, fontPath, fields[1], text.ToString(), string.Join(',', unicode));
    }
}

internal static class OptionTokenizer
{
    private static readonly HashSet<string> s_optionsWithValues = new(StringComparer.Ordinal)
    {
        "--cluster-level",
        "--direction",
        "--face-index",
        "--features",
        "--font-bold",
        "--font-ptem",
        "--font-size",
        "--font-slant",
        "--language",
        "--not-found-variation-selector-glyph",
        "--script",
        "--unicodes-after",
        "--unicodes-before",
        "--variations"
    };

    public static IEnumerable<string> Tokenize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        var rawTokens = new List<string>();
        var token = new StringBuilder();
        bool escaped = false;
        foreach (char character in value)
        {
            if (escaped)
            {
                token.Append(character);
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (token.Length != 0)
                {
                    rawTokens.Add(token.ToString());
                    token.Clear();
                }
            }
            else
            {
                token.Append(character);
            }
        }
        if (escaped) token.Append('\\');
        if (token.Length != 0) rawTokens.Add(token.ToString());

        for (int index = 0; index < rawTokens.Count; index++)
        {
            string option = rawTokens[index];
            if (s_optionsWithValues.Contains(option) && index + 1 < rawTokens.Count)
            {
                yield return option + "=" + rawTokens[++index];
            }
            else
            {
                yield return option;
            }
        }
    }
}

internal enum CaseStatus
{
    Passed,
    Failed,
    Unsupported,
    NonOpenType,
    Error
}

internal sealed record CaseResult
{
    public required CaseStatus Status { get; init; }
    public required string TestFile { get; init; }
    public required int Line { get; init; }
    public required string Message { get; init; }

    public static CaseResult Passed(TestCase value) => Create(value, CaseStatus.Passed, string.Empty);
    public static CaseResult Failed(TestCase value, string message) => Create(value, CaseStatus.Failed, message);
    public static CaseResult Unsupported(TestCase value, string message) => Create(value, CaseStatus.Unsupported, message);
    public static CaseResult NonOpenType(TestCase value, string message) => Create(value, CaseStatus.NonOpenType, message);
    public static CaseResult Error(TestCase value, string message) => Create(value, CaseStatus.Error, message);
    public static CaseResult Error(string file, int line, string message) => new() { Status = CaseStatus.Error, TestFile = file, Line = line, Message = message };

    private static CaseResult Create(TestCase value, CaseStatus status, string message) =>
        new() { Status = status, TestFile = value.TestFile, Line = value.Line, Message = message };
}

internal sealed record SuiteReport
{
    public required string HarfBuzzVersion { get; init; }
    public required string CorpusRoot { get; init; }
    public required int Total { get; init; }
    public required int Passed { get; init; }
    public required int Failed { get; init; }
    public required int Unsupported { get; init; }
    public required int NonOpenType { get; init; }
    public required int Errors { get; init; }
    public required List<CaseResult> Results { get; init; }
}

internal sealed record ReferenceGlyph
{
    [System.Text.Json.Serialization.JsonPropertyName("g")]
    public uint Glyph { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("cl")]
    public int Cluster { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("dx")]
    public int OffsetX { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("dy")]
    public int OffsetY { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("ax")]
    public int AdvanceX { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("ay")]
    public int AdvanceY { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("fl")]
    public uint Flags { get; init; }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(SuiteReport))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ReferenceGlyph[]))]
internal sealed partial class JsonContext : System.Text.Json.Serialization.JsonSerializerContext;
