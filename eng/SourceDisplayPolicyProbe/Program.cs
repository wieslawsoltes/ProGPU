using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HintedDisplayPolicyProbe;
using ProGPU.Backend.Native;
using ProGPU.Text;

namespace SourceDisplayPolicyProbe;

internal static class Program
{
    private static void Main(string[] args)
    {
        bool validate = args.Length == 2 && args[0] == "--validate-reference";
        if (!validate && args.Length != 10)
            throw new ArgumentException("Expected reference.json Inter.ttf Hebrew.ttf exact-library CreateNew-output.json native-commit successful-Build-URL em-policy advance-policy offset-policy; or --validate-reference reference.json.");
        using var reference = SourceReferenceInput.Read(args[validate ? 1 : 0]);
        if (validate)
        {
            Console.WriteLine($"Verified exact original receipt: {reference.Root.GetProperty("CaseCount").GetInt32()} cases; genuine source request metrics present: {reference.HasIndependentMetrics}. No native load.");
            return;
        }
        if (!reference.HasIndependentMetrics)
            throw new InvalidDataException("The original receipt lacks independently observed Typeface request metrics. Capture and review the source-input receipt first.");
        var emPolicy = Policy<NativeSourceEmPolicy>(args[7]);
        var advancePolicy = Policy<NativeSourceAdvancePolicy>(args[8]);
        var offsetPolicy = Policy<NativeSourceOffsetPolicy>(args[9]);
        if (args[5].Length != 40 || !args[5].All(Uri.IsHexDigit) ||
            !Uri.TryCreate(args[6], UriKind.Absolute, out var build) || build.Scheme != "https" || build.Host != "github.com" ||
            !build.AbsolutePath.StartsWith("/wieslawsoltes/ProGPU/actions/runs/", StringComparison.Ordinal))
            throw new ArgumentException("Exact source commit and whole-successful Build provenance are required before native staging.");
        if (File.Exists(args[4])) throw new IOException("Observation output already exists.");
        byte[] inter = File.ReadAllBytes(args[1]); byte[] hebrew = reference.IsMidpoint ? File.ReadAllBytes(args[2]) : [];
        VerifyFont(inter, ReferenceInput.FontHash);
        if (reference.IsMidpoint) VerifyFont(hebrew, SourceReferenceInput.RtlFontHash);
        var libraryIdentity = Identity(args[3]);
        nint library = NativeLibrary.Load(libraryIdentity.Path);
        NativeLibrary.SetDllImportResolver(typeof(NativeTextShapingContext).Assembly, (name, _, _) => name == "progpu_native" ? library : 0);
        foreach (string export in new[] { "progpu_native_text_context_layout_hinted_source_paragraph", "progpu_native_hinted_source_paragraph_borrow",
            "progpu_native_hinted_glyph_resource_copy_source_metrics", "progpu_native_hinted_glyph_resource_borrow_source" })
            _ = NativeLibrary.GetExport(library, export);
        var observations = new List<object>(); var timer = Stopwatch.StartNew();
        int ordinal = 0;
        foreach (var item in reference.Cases)
        {
            int sourceOrdinal = ordinal++;
            if (item.GetProperty("Mode").GetString() != "Display") continue;
            string fontHash = item.GetProperty("IndependentSourceMetrics").GetProperty("Font").GetProperty("Sha256").GetString()!;
            byte[] font = fontHash == ReferenceInput.FontHash ? inter : hebrew;
            VerifyFont(font, fontHash);
            var source = SourceReferenceInput.GetMetrics(reference.Root, item, fontHash);
            foreach (var interpreter in new[] { NativeFontHintInterpreter.TrueType35, NativeFontHintInterpreter.TrueType40 })
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Paired source probe exceeded 60 seconds.");
                // The producer and every borrowed source metric are retired before
                // advancing to another policy. Unsupported is an explicit failed
                // observation, never a skipped case, Ideal fallback or exact match.
                try { observations.Add(Capture(sourceOrdinal, item, source, font, fontHash, interpreter, emPolicy, advancePolicy, offsetPolicy)); }
                catch (NotSupportedException error)
                { observations.Add(new { SourceOrdinal = sourceOrdinal, OriginalCase = item.Clone(), Interpreter = (uint)interpreter,
                    Status = "ExplicitNativeRejection", Failure = error.Message, EmPolicy = emPolicy, AdvancePolicy = advancePolicy, OffsetPolicy = offsetPolicy }); }
            }
        }
        if (observations.Count != (reference.IsMidpoint ? 288 : 192) || Identity(args[3]).Sha256 != libraryIdentity.Sha256)
            throw new InvalidOperationException("Incomplete observations or changed native library.");
        var receipt = new
        {
            Schema = 1, NativeSourceCommit = args[5], SuppliedSuccessfulBuild = args[6], NativeLibrary = libraryIdentity,
            ReferenceReceipt = new { reference.Path, reference.Sha256 }, ReferenceProvenance = reference.Root.Clone(),
            FontSha256 = Hash(inter), RtlFontSha256 = reference.IsMidpoint ? Hash(hebrew) : null,
            Probe = Identity(Assembly.GetExecutingAssembly().Location), Backend = Identity(typeof(NativeTextShapingContext).Assembly.Location),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            EmPolicy = emPolicy, AdvancePolicy = advancePolicy, OffsetPolicy = offsetPolicy,
            CaseCount = observations.Count, Cases = observations,
            Qualification = "Original source-double observations only. Both interpreters remain explicit; no default, Display registration, pixel, caret or application qualification."
        };
        byte[] output = JsonSerializer.SerializeToUtf8Bytes(receipt, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true });
        using var stream = new FileStream(args[4], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(output);
        Console.WriteLine($"Retained {observations.Count} explicit source-policy observations, including all rejected/unmatched cases.");
    }

    private static object Capture(int ordinal, JsonElement item, IndependentSourceMetrics source, byte[] font, string fontHash,
        NativeFontHintInterpreter interpreter, NativeSourceEmPolicy emPolicy, NativeSourceAdvancePolicy advancePolicy, NativeSourceOffsetPolicy offsetPolicy)
    {
        var face = SfntFontFace.Load(font);
        if (!face.TryGetTable("head", out var head) || head.Length < 20) throw new InvalidDataException("Original units-per-em is absent.");
        ushort upm = BinaryPrimitives.ReadUInt16BigEndian(head.Span[18..]);
        if (upm == 0) throw new InvalidDataException("Original units-per-em is zero.");
        string text = item.GetProperty("Text").GetString()!;
        float scale = (float)source.Em / upm;
        uint ppem = CapturePpem(source.Em, source.Dpi, emPolicy);
        var direction = item.GetProperty("Direction").GetString() == "RightToLeft" ? NativeTextDirection.RightToLeft : NativeTextDirection.LeftToRight;
        var options = NativeHintedSourceOptions.Create(source.Em, source.Dpi, item.GetProperty("Width").GetDouble(),
            source.LineSpacing, 0, emPolicy, advancePolicy, true, offsetPolicy: offsetPolicy);
        NativeTextParagraphStyle[] styles = [new(0, text.Length, 0, scale)];
        NativeTextStyleMetrics[] metrics = [new() { Ascent = (float)source.Baseline, Descent = (float)(source.LineSpacing - source.Baseline) }];
        NativeHintedSourceStyle[] originalStyles = [new() { EmSize = source.Em, Ascent = source.Baseline, Descent = source.LineSpacing - source.Baseline }];
        NativeHintedParagraphDeviceStyle[] devices = [new() { FontIndex = 0, SourceScale = scale,
            LogicalUnitsPerPhysicalPixel = (float)(1.0 / source.Dpi), XPixelsPerEm266 = ppem, YPixelsPerEm266 = ppem, Interpreter = (uint)interpreter }];
        var layout = new NativeTextParagraphOptions(scale, (float)options.MaximumWidth, (float)options.LineHeight);
        using var context = new NativeTextShapingContext(font);
        using var paragraph = context.LayoutHintedSourceParagraph(text, direction, in layout, styles, metrics, devices, in options, originalStyles);
        var native = CopyIdentity(paragraph, fontHash);
        var alignment = SourceComparison.Compare(item, native, fontHash);
        using var resource = paragraph.PrepareGlyphResourceWithNominalMetrics(NativeHintedProjectionPolicy.IntrinsicSimd);
        using var read = resource.AcquireReadLease();
        var copied = new List<SourceRunMetric>();
        foreach (var run in alignment.Runs)
        {
            if (run.Status == "Unmatched") continue;
            uint[] indices = run.Occurrences.Select(value => checked((uint)value.PositionedIndex)).ToArray();
            double[] advances = new double[indices.Length]; var offsets = new NativeHintedSourceGlyphOffset[indices.Length];
            read.CopySourceMetrics(indices, source.Em, source.Dpi, advances, offsets);
            for (int i = 0; i < indices.Length; ++i)
                copied.Add(new(run.SourceLineIndex, run.SourceRunIndex, checked((int)indices[i]), advances[i], offsets[i].X, offsets[i].Y));
        }
        var comparison = SourceGeometryComparison.Compare(item, alignment, paragraph.LineMetrics.ToArray()
            .Select(line => new SourceLineMetric(line.Width, line.Top, line.Height, line.BaselineOffset, line.BaselineY, line.OriginX)).ToArray(), copied.ToArray());
        return new
        {
            SourceOrdinal = ordinal, OriginalCase = item.Clone(), Interpreter = (uint)interpreter, DeviceEm26_6 = ppem,
            SourceOptions = paragraph.Options, SourceStyles = paragraph.SourceStyles.ToArray(), DeviceStyles = devices,
            OriginalSourceFactoryRasterEmAdmitted = (double)(float)source.Em == source.Em && (double)(float)source.Dpi == source.Dpi,
            SourceGeometryComparison = comparison, RawLogical = paragraph.LogicalGlyphs.ToArray(), Runs = paragraph.Runs.ToArray(),
            OriginalOwners = paragraph.PositionedOwners.ToArray(), SourceScalars = paragraph.SourceScalars.ToArray(),
            SourceLogicalMetrics = paragraph.LogicalMetrics.ToArray(), SourceGlyphs = paragraph.GlyphMetrics.ToArray(),
            SourceLines = paragraph.LineMetrics.ToArray(), SourceBoxes = paragraph.Boxes.ToArray(), SourceCarets = paragraph.Carets.ToArray(),
            RasterGlyphs = paragraph.RasterGlyphs.ToArray(), RasterLines = paragraph.RasterLines.ToArray(),
            SourceRunMetrics = copied, Status = comparison.Status
        };
    }

    private static NativeSourceParagraph CopyIdentity(NativeHintedSourceParagraph paragraph, string fontHash)
    {
        var glyphs = new NativeSourceGlyph[paragraph.RasterGlyphs.Length];
        for (int i = 0; i < glyphs.Length; ++i)
        {
            var glyph = paragraph.RasterGlyphs[i]; int logical = checked((int)glyph.GlyphIndex);
            var original = paragraph.LogicalGlyphs[logical]; var owner = paragraph.PositionedOwners[i];
            var run = paragraph.Runs[checked((int)owner.RunIndex)];
            if (glyph.GlyphId != original.GlyphId || glyph.Cluster != original.Cluster || run.FontIndex != glyph.FontIndex ||
                owner.RunGlyphIndex >= run.LogicalCount || run.LogicalStart + owner.RunGlyphIndex != glyph.GlyphIndex ||
                paragraph.GlyphMetrics[i].Cluster != glyph.Cluster)
                throw new InvalidDataException("Original retained glyph/run/source ownership changed.");
            glyphs[i] = new(i, logical, glyph.GlyphId, glyph.FontIndex, glyph.Cluster, paragraph.ClusterEnds[i], paragraph.BidiLevels[i],
                original.AdvanceX, original.OffsetX, original.OffsetY, glyph.AdvanceX, glyph.X, glyph.Y);
        }
        return new(fontHash, 0, paragraph.SourceScalars.ToArray().Select(value => new NativeSourceScalar(value.CodePoint,
            checked((int)value.InputIndex), checked((int)value.InputLength))).ToArray(), glyphs,
            paragraph.RasterLines.ToArray().Select(line => new NativeSourceLine(line.InputStart, line.InputEnd,
                checked((int)line.GlyphStart), checked((int)line.GlyphCount), line.Width, line.Height, line.BaselineY)).ToArray());
    }

    private static uint CapturePpem(double em, double dpi, NativeSourceEmPolicy policy)
    {
        // Explicit caller-selected CAPTURE arguments only; native independently
        // validates them. No returned advance/offset/source value is repaired.
        double physical = policy == NativeSourceEmPolicy.FloatCaptureNearestHalfUp ? (double)((float)em * (float)dpi) : em * dpi;
        double selected = policy switch
        {
            NativeSourceEmPolicy.Exact26Dot6 => physical * 64,
            NativeSourceEmPolicy.NearestTiesToEven => Math.Round(physical, MidpointRounding.ToEven) * 64,
            _ => Math.Floor(physical + 0.5) * 64
        };
        if (!double.IsFinite(selected) || selected < 1 || selected > uint.MaxValue || selected != Math.Truncate(selected))
            throw new NotSupportedException("Explicit device-em capture is not representable in 26.6.");
        return checked((uint)selected);
    }
    private static T Policy<T>(string value) where T : struct, Enum
        => Enum.TryParse<T>(value, out var policy) && Enum.IsDefined(policy) ? policy : throw new ArgumentException("Unknown explicit source policy: " + value);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void VerifyFont(byte[] bytes, string expected)
    { if (Hash(bytes) != expected) throw new InvalidDataException("Original physical font bytes changed."); }
    private static FileIdentity Identity(string path) => new(Path.GetFullPath(path), Hash(File.ReadAllBytes(path)));
    private sealed record FileIdentity(string Path, string Sha256);
}
