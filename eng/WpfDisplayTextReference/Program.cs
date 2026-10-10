using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

internal static partial class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool sourceInputs = args.Length != 0 && args[0] == "--source-inputs";
        if (sourceInputs) args = args[1..];
        if (!OperatingSystem.IsWindows() || args.Length is not (4 or 6) ||
            !string.Equals(RuntimeInformation.ProcessArchitecture.ToString(), args[2], StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected Windows, font path, CreateNew receipt path, actual architecture and source commit.");
        if (args[3].Length != 40 || !args[3].All(Uri.IsHexDigit)) throw new ArgumentException("Exact source commit required.");
        bool midpointCases = args.Length == 6;
        if (midpointCases && args[4] != MidpointCases.Family)
            throw new ArgumentException("Unknown original reference case family.");
        Assembly presentation = typeof(TextFormatter).Assembly;
        if (Convert.ToHexString(presentation.GetName().GetPublicKeyToken() ?? []) != "31BF3856AD364E35")
            throw new InvalidOperationException("This reference requires original Microsoft PresentationCore, not the portable source.");

        string font = Path.GetFullPath(args[0]);
        var family = new FontFamily(new Uri(Path.GetDirectoryName(font)! + Path.DirectorySeparatorChar), "./#Inter");
        var cases = new List<object>();
        var timer = Stopwatch.StartNew();
        object? rtlFontIdentity = null;
        if (midpointCases)
        {
            ReferenceFontPin pin = ReferenceFontPin.Load();
            string directory = Path.GetFullPath(args[5]);
            pin.Verify(directory);
            string rtlFont = Path.Combine(directory, pin.FontFileName);
            var rtlFamily = new FontFamily(new Uri(directory + Path.DirectorySeparatorChar), "./#" + pin.FamilyName);
            CaptureMidpointCases(font, family, rtlFont, rtlFamily, timer, cases, sourceInputs);
            // Verify again after capture, before any receipt is published.
            pin.Verify(directory);
            rtlFontIdentity = new { Pin = pin, Font = FileIdentity(rtlFont),
                License = FileIdentity(Path.Combine(directory, pin.LicenseFileName)) };
        }
        else
        {
            foreach (TextFormattingMode mode in new[] { TextFormattingMode.Ideal, TextFormattingMode.Display })
            foreach (FlowDirection direction in new[] { FlowDirection.LeftToRight, FlowDirection.RightToLeft })
            foreach (double dpi in new[] { 1.0, 1.25, 1.5, 2.0 })
            foreach (double em in new[] { 13.0, 17.0 })
            foreach (string text in new[] { "Hello", "AVATAR fi A A ", "a\u0301 b c " })
            foreach (double width in new[] { 25.0, 1000.0 })
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Original reference exceeded 60 seconds.");
                cases.Add(Capture(font, family, text, mode, direction, dpi, em, width, sourceInputs));
            }
            if (cases.Count != 192) throw new InvalidOperationException("Incomplete reference case inventory.");
        }
        var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Where(module => new[] { "dwrite.dll", "dwriteforwarder.dll", "presentationnative_cor3.dll", "wpfgfx_cor3.dll" }
                .Contains(module.ModuleName, StringComparer.OrdinalIgnoreCase))
            .Select(module => FileIdentity(module.FileName)).ToArray();
        if (modules.Length == 0 || !Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Any(module => string.Equals(module.ModuleName, "dwrite.dll", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Original DirectWrite module identity was not observed.");
        var receipt = new
        {
            Schema = midpointCases ? (sourceInputs ? 4 : 3) : (sourceInputs ? 2 : 1), SourceCommit = args[3], Cases = cases, CaseCount = cases.Count,
            Font = FileIdentity(font), Producer = FileIdentity(Assembly.GetExecutingAssembly().Location),
            PresentationCore = FileIdentity(presentation.Location), PresentationIdentity = presentation.FullName,
            OperatingSystem = RuntimeInformation.OSDescription, OsVersion = Environment.OSVersion.VersionString,
            Runtime = RuntimeInformation.FrameworkDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Culture = CultureInfo.CurrentCulture.Name, UiCulture = CultureInfo.CurrentUICulture.Name,
            NativeModules = modules, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            Qualification = "Microsoft API observations only; not a portable metric, pixel or Display-policy parity result."
        };
        // The old four-argument invocation retains its complete schema-1 shape
        // and inventory. New observations cannot masquerade as those receipts.
        object serialized = receipt;
        if (midpointCases)
        {
            var expanded = JsonSerializer.SerializeToNode(receipt)!;
            expanded["CaseFamily"] = MidpointCases.Family;
            expanded["RtlFont"] = JsonSerializer.SerializeToNode(rtlFontIdentity);
            serialized = expanded;
        }
        using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, serialized, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"WPF text reference: {cases.Count} original Ideal/Display cases, 0 skipped; {args[2]}.");
    }

    private static object Capture(string font, FontFamily family, string text, TextFormattingMode mode,
        FlowDirection direction, double dpi, double em, double width, bool sourceInputs = false)
    {
        var properties = new RunProperties(family, em);
        // Independent INPUT capture precedes every formatter/line operation.
        // Public FontFamily ratios are design metrics, not this Display request.
        object? independentMetrics = sourceInputs
            ? CaptureSourceMetrics(properties.Typeface, font, mode, em, dpi) : null;
        var source = new Source(text, properties) { PixelsPerDip = dpi };
        var paragraph = new Paragraph(properties, direction);
        using var formatter = TextFormatter.Create(mode);
        var lines = new List<object>();
        TextLineBreak? previous = null;
        int start = 0;
        try
        {
            while (start < text.Length)
            {
                if (lines.Count >= 32) throw new InvalidOperationException("Line inventory exceeded its bound.");
                using TextLine line = formatter.FormatLine(source, start, width, paragraph, previous);
                if (line.Length <= 0) throw new InvalidOperationException("The original formatter made no source progress.");
                var runs = line.GetIndexedGlyphRuns().Select(indexed =>
                {
                    GlyphRun run = indexed.GlyphRun;
                    if (!run.GlyphTypeface.FontUri.IsFile ||
                        !string.Equals(Path.GetFullPath(run.GlyphTypeface.FontUri.LocalPath), font, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The reference unexpectedly selected another physical font.");
                    foreach (double advance in run.AdvanceWidths) Finite(advance);
                    return new
                    {
                        indexed.TextSourceCharacterIndex, indexed.TextSourceLength,
                        run.BidiLevel, run.IsSideways, run.FontRenderingEmSize, run.PixelsPerDip,
                        Baseline = Point(run.BaselineOrigin), GlyphIds = run.GlyphIndices.ToArray(),
                        Advances = run.AdvanceWidths.ToArray(), Offsets = run.GlyphOffsets?.Select(Point).ToArray(),
                        NominalDesignAdvances = run.GlyphIndices.Select(index => run.GlyphTypeface.AdvanceWidths[index]).ToArray(),
                        Utf16 = run.Characters?.Select(character => (int)character).ToArray(),
                        Clusters = run.ClusterMap?.ToArray(), CaretStops = run.CaretStops?.ToArray(),
                        FontUri = run.GlyphTypeface.FontUri.AbsoluteUri, StyleSimulations = run.GlyphTypeface.StyleSimulations.ToString(),
                        FontMetrics = new { run.GlyphTypeface.Baseline, run.GlyphTypeface.Height,
                            run.GlyphTypeface.CapsHeight, run.GlyphTypeface.XHeight,
                            run.GlyphTypeface.UnderlinePosition, run.GlyphTypeface.UnderlineThickness },
                        Ink = Rectangle(run.ComputeInkBoundingBox()),
                    };
                }).ToArray();
                var carets = new List<object>();
                var hit = new CharacterHit(start, 0);
                bool stopped = false;
                for (int attempt = 0; attempt < text.Length * 2 + 4; attempt++)
                {
                    double distance = line.GetDistanceFromCharacterHit(hit);
                    Finite(distance);
                    CharacterHit roundTrip = line.GetCharacterHitFromDistance(distance);
                    carets.Add(new { hit.FirstCharacterIndex, hit.TrailingLength, Distance = distance,
                        HitFirst = roundTrip.FirstCharacterIndex, HitTrailing = roundTrip.TrailingLength });
                    var next = line.GetNextCaretCharacterHit(hit);
                    if (next == hit) { stopped = true; break; }
                    if (next.FirstCharacterIndex < start || next.FirstCharacterIndex + next.TrailingLength > start + line.Length)
                        throw new InvalidOperationException("Caret left its original source line.");
                    hit = next;
                }
                if (!stopped) throw new InvalidOperationException("Caret traversal did not terminate within its bound.");
                foreach (double metric in new[] { line.Width, line.WidthIncludingTrailingWhitespace, line.Height, line.Baseline, line.Start }) Finite(metric);
                int visible = Math.Min(line.Length, text.Length - start);
                lines.Add(new
                {
                    SourceStart = start, line.Length, line.NewlineLength, line.TrailingWhitespaceLength,
                    line.Start, line.Width, line.WidthIncludingTrailingWhitespace, line.Height, line.Baseline,
                    line.DependentLength, line.HasOverflowed, Runs = runs, Carets = carets,
                    Selection = line.GetTextBounds(start, visible).Select(bounds => new
                    { bounds.FlowDirection, Bounds = Rectangle(bounds.Rectangle) }).ToArray(),
                });
                using TextLineBreak? nextBreak = line.GetTextLineBreak();
                TextLineBreak? clone = nextBreak?.Clone();
                previous?.Dispose();
                previous = clone;
                start = checked(start + line.Length);
            }
        }
        finally { previous?.Dispose(); }
        var result = new { Text = text, Mode = mode.ToString(), Direction = direction.ToString(), Dpi = dpi, Em = em, Width = width,
            SourceFont = new { family.Baseline, family.LineSpacing,
                properties.FontRenderingEmSize, properties.FontHintingEmSize, Culture = properties.CultureInfo.Name }, Lines = lines };
        if (!sourceInputs) return result;
        var captured = JsonSerializer.SerializeToNode(result)!.AsObject();
        captured.Add("IndependentSourceMetrics", JsonSerializer.SerializeToNode(independentMetrics));
        return captured;
    }

    private static double[] Point(Point value) { Finite(value.X); Finite(value.Y); return [value.X, value.Y]; }
    private static object Rectangle(Rect value)
    {
        if (!value.IsEmpty) foreach (double coordinate in new[] { value.X, value.Y, value.Width, value.Height }) Finite(coordinate);
        return new { value.IsEmpty, X = value.IsEmpty ? 0 : value.X, Y = value.IsEmpty ? 0 : value.Y,
            Width = value.IsEmpty ? 0 : value.Width, Height = value.IsEmpty ? 0 : value.Height };
    }
    private static void Finite(double value)
    { if (!double.IsFinite(value)) throw new InvalidOperationException("Original metric was not finite."); }
    private static object FileIdentity(string path) => new
    {
        Path = Path.GetFullPath(path), Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
        Version = FileVersionInfo.GetVersionInfo(path).FileVersion,
    };

    private sealed class Source(string text, RunProperties properties) : TextSource
    {
        public override TextRun GetTextRun(int index) => index < text.Length
            ? new TextCharacters(text, index, text.Length - index, properties) : new TextEndOfParagraph(1);
        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int limit)
        {
            int count = Math.Clamp(limit, 0, text.Length);
            return new(count, new(CultureInfo.InvariantCulture, new CharacterBufferRange(text, 0, count)));
        }
        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index) => index;
    }

    private sealed class RunProperties(FontFamily family, double em) : TextRunProperties
    {
        public override Typeface Typeface { get; } = new(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        public override double FontRenderingEmSize => em;
        public override double FontHintingEmSize => em;
        public override TextDecorationCollection? TextDecorations => null;
        public override Brush ForegroundBrush => Brushes.Black;
        public override Brush? BackgroundBrush => null;
        public override CultureInfo CultureInfo => CultureInfo.InvariantCulture;
        public override TextEffectCollection? TextEffects => null;
    }

    private sealed class Paragraph(RunProperties properties, FlowDirection direction) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => direction;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties => properties;
        public override TextWrapping TextWrapping => TextWrapping.Wrap;
        public override TextMarkerProperties? TextMarkerProperties => null;
        public override double Indent => 0;
    }
}
