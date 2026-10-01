using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ProGPU.Backend.Native;
using ProGPU.Text;

namespace HintedDisplayPolicyProbe;

internal static class Program
{
    private static void Main(string[] args)
    {
        bool validateOnly = args.Length == 3 && args[0] == "--validate-reference";
        if (!validateOnly && args.Length != 6)
            throw new ArgumentException("Expected reference.json font.ttf exact-native-library CreateNew-output.json native-commit successful-Build-URL; or --validate-reference reference.json font.ttf.");
        string referencePath = Path.GetFullPath(args[validateOnly ? 1 : 0]);
        string fontPath = Path.GetFullPath(args[validateOnly ? 2 : 1]);
        byte[] font = File.ReadAllBytes(fontPath);
        using var reference = ReferenceInput.Read(referencePath, font);
        bool sourceInputs = ReferenceInput.HasIndependentSourceInputs(reference.RootElement);
        if (validateOnly)
        {
            Console.WriteLine("Original receipt validated: 192 cases / 386 lines / 450 runs; no native library loaded.");
            return;
        }
        string libraryPath = Path.GetFullPath(args[2]);
        if (args[4].Length != 40 || !args[4].All(Uri.IsHexDigit) ||
            !Uri.TryCreate(args[5], UriKind.Absolute, out var build) || build.Scheme != "https" ||
            build.Host != "github.com" || !build.AbsolutePath.StartsWith("/wieslawsoltes/ProGPU/actions/runs/", StringComparison.Ordinal))
            throw new ArgumentException("Exact native source and whole successful Build provenance are required; verify them before staging.");
        if (File.Exists(args[3])) throw new IOException("The observation receipt already exists.");
        var nativeIdentity = Identity(libraryPath);
        nint library = NativeLibrary.Load(libraryPath);
        // This process owns its exact native choice. Never fall back to an ambient
        // same-name library, replace a package asset, or unload while leases exist.
        NativeLibrary.SetDllImportResolver(typeof(NativeTextShapingContext).Assembly,
            (name, _, _) => name == "progpu_native" ? library : 0);
        foreach (string export in new[] { "progpu_native_text_context_create", "progpu_native_text_context_capture_hinted_batch",
            "progpu_native_text_context_layout_hinted_paragraph", "progpu_native_hinted_paragraph_copy_format",
            "progpu_native_hinted_paragraph_copy_interaction" })
            _ = NativeLibrary.GetExport(library, export);

        // Read genuine default head/hhea metadata through the existing SFNT
        // parser. This is explicit diagnostic line input, not WPF Display policy.
        var face = SfntFontFace.Load(font);
        if (!face.TryGetTable("head", out var head) || !face.TryGetTable("hhea", out var hhea) ||
            head.Length < 20 || hhea.Length < 10) throw new InvalidDataException("Original head/hhea metadata is unavailable.");
        ushort upm = BinaryPrimitives.ReadUInt16BigEndian(head.Span[18..]);
        short ascent = BinaryPrimitives.ReadInt16BigEndian(hhea.Span[4..]);
        short descent = BinaryPrimitives.ReadInt16BigEndian(hhea.Span[6..]);
        short lineGap = BinaryPrimitives.ReadInt16BigEndian(hhea.Span[8..]);
        if (upm == 0 || ascent < 0 || descent > 0) throw new InvalidDataException("Unsupported original horizontal metrics.");
        using var context = new NativeTextShapingContext(font);
        var cases = new List<object>();
        var timer = Stopwatch.StartNew();
        foreach (var item in reference.RootElement.GetProperty("Cases").EnumerateArray())
        {
            if (item.GetProperty("Mode").GetString() != "Display") continue;
            string text = item.GetProperty("Text").GetString()!;
            double dpi = item.GetProperty("Dpi").GetDouble(), em = item.GetProperty("Em").GetDouble();
            double deviceEm = em * dpi * 64;
            if (deviceEm != Math.Truncate(deviceEm)) throw new InvalidDataException("Device em is not exactly representable in 26.6.");
            uint ppem26_6 = checked((uint)deviceEm);
            var direction = item.GetProperty("Direction").GetString() == "RightToLeft" ? NativeTextDirection.RightToLeft : NativeTextDirection.LeftToRight;
            float sourceScale = (float)em / upm;
            float logicalUnits = (float)(1.0 / dpi);
            NativeTextParagraphStyle[] styles = [new(0, text.Length, 0, sourceScale)];
            NativeTextStyleMetrics[] metrics = [new() { Ascent = ascent * sourceScale, Descent = -descent * sourceScale }];
            var options = new NativeTextParagraphOptions(sourceScale, (float)item.GetProperty("Width").GetDouble(),
                Math.Max(metrics[0].Ascent + metrics[0].Descent, (ascent - descent + lineGap) * sourceScale));
            foreach (var interpreter in new[] { NativeFontHintInterpreter.TrueType35, NativeFontHintInterpreter.TrueType40 })
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Paired CPU probe exceeded 60 seconds.");
                var rawRuns = new List<object>();
                foreach (var line in item.GetProperty("Lines").EnumerateArray())
                foreach (var run in line.GetProperty("Runs").EnumerateArray())
                {
                    uint[] ids = run.GetProperty("GlyphIds").EnumerateArray().Select(value => value.GetUInt32()).ToArray();
                    using var batch = context.CaptureHintedBatch(0, ids, ppem26_6, ppem26_6, interpreter);
                    var glyphs = new NativeHintedGlyph[batch.Counts.Glyphs];
                    batch.CopyTo(glyphs, new NativeHintedPoint[batch.Counts.Points], new byte[batch.Counts.Points], new int[batch.Counts.Contours]);
                    var sourceAdvances = run.GetProperty("Advances").EnumerateArray().Select(value => value.GetDouble()).ToArray();
                    if (glyphs.Length != ids.Length || !glyphs.Select(g => g.GlyphIndex).SequenceEqual(ids))
                        throw new InvalidOperationException("The original raw glyph occurrence order changed.");
                    rawRuns.Add(new
                    {
                        SourceLineStart = line.GetProperty("SourceStart").GetInt32(), OriginalRun = run.Clone(),
                        IndependentSourceFaceMetrics = sourceInputs ? run.GetProperty("FontMetrics").Clone() : (JsonElement?)null,
                        IndependentNominalDesignAdvances = sourceInputs ? run.GetProperty("NominalDesignAdvances").Clone() : (JsonElement?)null,
                        RawGlyphs = glyphs,
                        RawHorizontalAdvancesDip = glyphs.Select(g => ReferenceInput.DeviceToDip(g.HorizontalAdvance266, dpi)).ToArray(),
                        RawSlotAdvancesDip = glyphs.Select(g => ReferenceInput.DeviceToDip(g.AdvanceX266, dpi)).ToArray(),
                        RawEqualsSourceAdvance = glyphs.Select((g, i) => ReferenceInput.DeviceToDip(g.AdvanceX266, dpi) == sourceAdvances[i]).ToArray(),
                        Meaning = "Unpositioned original glyph IDs only; differences include shaping/GPOS/context and do not select an interpreter."
                    });
                }
                NativeHintedParagraphDeviceStyle[] devices = [new()
                {
                    FontIndex = 0, SourceScale = sourceScale, LogicalUnitsPerPhysicalPixel = logicalUnits,
                    XPixelsPerEm266 = ppem26_6, YPixelsPerEm266 = ppem26_6, Interpreter = (uint)interpreter
                }];
                // Shape the COMPLETE source text once, not the Microsoft line or
                // run suffixes. Paragraph direction and original UTF-16 remain input.
                using var paragraph = context.LayoutHintedParagraph(text, direction, options, styles, metrics, devices);
                var logical = paragraph.LogicalGlyphs.ToArray();
                var sourceComparison = SourceComparison.Compare(item, CopySourceParagraph(paragraph, logical, font));
                cases.Add(new
                {
                    OriginalCase = item.Clone(), Interpreter = (uint)interpreter, DeviceEm26_6 = ppem26_6,
                    IndependentSourceFontInputs = sourceInputs ? item.GetProperty("SourceFont").Clone() : (JsonElement?)null,
                    ExactSourceDpi = dpi, NativeLogicalUnitsPerPhysicalPixel = logicalUnits,
                    NativeDirection = direction, FontIndex = 0, Phase26_6 = new uint[] { 0, 0 },
                    Features = Array.Empty<NativeTextFeature>(), VariationCoordinates16_16 = Array.Empty<int>(),
                    NormalizedCoordinates = Array.Empty<short>(),
                    SourceMetricsOrigin = "Original default head/hhea design metrics; not a WPF baseline/line-height equivalence claim.",
                    SourceMetrics = metrics, SourceOptions = options, RawRuns = rawRuns,
                    SourceContextComparison = sourceComparison,
                    SourceScalars = paragraph.SourceScalars.ToArray(), AdmittedScalars = paragraph.AdmittedScalars.ToArray(),
                    ScalarLevels = paragraph.ScalarLevels.ToArray(), Runs = paragraph.Runs.ToArray(),
                    LogicalGlyphs26_6 = logical, LogicalOwners = paragraph.LogicalOwners.ToArray(),
                    LogicalAdvancesDipFromExactDpi = logical.Select(g => ReferenceInput.DeviceToDip(g.AdvanceX, dpi)).ToArray(),
                    LogicalOffsetsDipFromExactDpi = logical.Select(g => new[] { ReferenceInput.DeviceToDip(g.OffsetX, dpi), ReferenceInput.DeviceToDip(g.OffsetY, dpi) }).ToArray(),
                    LogicalClusterEnds = paragraph.LogicalClusterEnds.ToArray(), LogicalBidiLevels = paragraph.LogicalBidiLevels.ToArray(),
                    NativeCounts = paragraph.Counts, NativeResult = paragraph.Result,
                    GlyphScales = paragraph.GlyphScales.ToArray(), PositionedGlyphs = paragraph.Glyphs.ToArray(),
                    PositionedOwners = paragraph.PositionedOwners.ToArray(), ClusterEnds = paragraph.ClusterEnds.ToArray(),
                    BidiLevels = paragraph.BidiLevels.ToArray(), Lines = paragraph.Lines.ToArray(),
                    LineOrigins = paragraph.LineOrigins.ToArray(), Boxes = paragraph.Boxes.ToArray(), Carets = paragraph.Carets.ToArray()
                });
            }
        }
        if (cases.Count != 192 || Identity(libraryPath).Sha256 != nativeIdentity.Sha256)
            throw new InvalidOperationException("Incomplete observations or changed native payload.");
        var receipt = new
        {
            Schema = 1, NativeSourceCommit = args[4], SuppliedSuccessfulBuild = args[5],
            HasIndependentSourceInputs = sourceInputs,
            NativeLibrary = nativeIdentity, ReferenceReceipt = new { reference.Path, reference.Sha256 },
            ReferenceProvenance = reference.RootElement.Clone(),
            Font = Identity(fontPath), FontFace = 0, UnitsPerEm = upm, HheaAscent = ascent, HheaDescent = descent, HheaLineGap = lineGap,
            Probe = Identity(Assembly.GetExecutingAssembly().Location), Backend = Identity(typeof(NativeTextShapingContext).Assembly.Location),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), OperatingSystem = RuntimeInformation.OSDescription,
            Runtime = RuntimeInformation.FrameworkDescription, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            CaseCount = cases.Count, Cases = cases,
            Qualification = "Explicit CPU observations only. Raw hinted slots are not positioned advances; no interpreter/default, source Display, package, pixel or UI admission."
        };
        byte[] output = JsonSerializer.SerializeToUtf8Bytes(receipt, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
        using var stream = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(output);
        Console.WriteLine($"Retained {cases.Count} explicit-policy observations; no interpreter selected.");
    }

    private static FileIdentity Identity(string path) => new(Path.GetFullPath(path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))) ;

    private static NativeSourceParagraph CopySourceParagraph(NativeHintedParagraph paragraph, NativeTextShapingGlyph[] logical, byte[] font)
    {
        var glyphs = new NativeSourceGlyph[paragraph.Glyphs.Length];
        for (int i = 0; i < glyphs.Length; ++i)
        {
            var positioned = paragraph.Glyphs[i];
            int index = checked((int)positioned.GlyphIndex);
            if ((uint)index >= (uint)logical.Length || positioned.GlyphId != logical[index].GlyphId || positioned.Cluster != logical[index].Cluster ||
                paragraph.ClusterEnds[i] != paragraph.LogicalClusterEnds[index])
                throw new InvalidDataException("The original native logical/positioned identity changed.");
            var owner = paragraph.PositionedOwners[i]; var logicalOwner = paragraph.LogicalOwners[index];
            if (owner.RunIndex != logicalOwner.RunIndex || owner.RunGlyphIndex != logicalOwner.RunGlyphIndex || owner.DescriptorIndex != logicalOwner.DescriptorIndex ||
                owner.RunIndex >= paragraph.Runs.Length || paragraph.Runs[checked((int)owner.RunIndex)].FontIndex != positioned.FontIndex)
                throw new InvalidDataException("The original native occurrence owner changed.");
            var shaped = logical[index];
            glyphs[i] = new(i, index, positioned.GlyphId, positioned.FontIndex, positioned.Cluster, paragraph.ClusterEnds[i],
                paragraph.BidiLevels[i], shaped.AdvanceX, shaped.OffsetX, shaped.OffsetY, positioned.AdvanceX, positioned.X, positioned.Y);
        }
        return new(Convert.ToHexString(SHA256.HashData(font)), 0,
            paragraph.SourceScalars.ToArray().Select(value => new NativeSourceScalar(value.CodePoint, checked((int)value.InputIndex), checked((int)value.InputLength))).ToArray(),
            glyphs, paragraph.Lines.ToArray().Select(value => new NativeSourceLine(value.InputStart, value.InputEnd,
                checked((int)value.GlyphStart), checked((int)value.GlyphCount), value.Width, value.Height, value.BaselineY)).ToArray());
    }

    private sealed record FileIdentity(string Path, string Sha256);
}
