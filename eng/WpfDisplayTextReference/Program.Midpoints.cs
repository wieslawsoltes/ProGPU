using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

internal static partial class Program
{
    private static void CaptureMidpointCases(string font, FontFamily family, Stopwatch timer, List<object> cases)
    {
        foreach (MidpointInput input in MidpointCases.Create())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException("Original reference exceeded 60 seconds.");
            JsonElement original;
            try
            {
                original = JsonSerializer.SerializeToElement(Capture(font, family, input.Text,
                    Enum.Parse<TextFormattingMode>(input.Mode), Enum.Parse<FlowDirection>(input.Direction),
                    input.Dpi, input.Em, input.Width));
            }
            catch
            {
                Console.Error.WriteLine($"Original midpoint input rejected: {JsonSerializer.Serialize(input)}");
                try { ReportOriginalFontSelection(family, input); }
                catch (Exception diagnostic) { Console.Error.WriteLine($"Font-selection diagnostic failed: {diagnostic.Message}"); }
                throw;
            }
            // A label is not evidence of mark positioning or RTL shaping. Fail
            // before receipt publication if this physical font did not provide
            // the requested observable coverage. Never synthesize an offset.
            MidpointCoverage coverage = MidpointCases.Observe(input, original);
            cases.Add(new { Input = input, Original = original, Coverage = coverage });
        }
        if (cases.Count != MidpointCases.Count)
            throw new InvalidOperationException("Incomplete midpoint reference case inventory.");
        if (timer.Elapsed > TimeSpan.FromSeconds(60))
            throw new TimeoutException("Original reference exceeded 60 seconds.");
    }

    // Failure-only observation, never a receipt or a replacement for Capture's
    // exact physical-font guard. Reproduce the original first line and report
    // which source occurrences selected another font, including zero-ink runs.
    private static void ReportOriginalFontSelection(FontFamily family, MidpointInput input)
    {
        var properties = new RunProperties(family, input.Em);
        var source = new Source(input.Text, properties) { PixelsPerDip = input.Dpi };
        var paragraph = new Paragraph(properties, Enum.Parse<FlowDirection>(input.Direction));
        using var formatter = TextFormatter.Create(Enum.Parse<TextFormattingMode>(input.Mode));
        using TextLine line = formatter.FormatLine(source, 0, input.Width, paragraph, null);
        var runs = line.GetIndexedGlyphRuns().Select(indexed =>
        {
            GlyphRun run = indexed.GlyphRun;
            return new
            {
                indexed.TextSourceCharacterIndex, indexed.TextSourceLength,
                FontUri = run.GlyphTypeface.FontUri.AbsoluteUri,
                run.BidiLevel, GlyphIds = run.GlyphIndices.ToArray(),
                Advances = run.AdvanceWidths.ToArray(), Offsets = run.GlyphOffsets?.Select(Point).ToArray(),
                Utf16 = run.Characters?.Select(character => (int)character).ToArray(),
                Ink = Rectangle(run.ComputeInkBoundingBox()),
            };
        }).ToArray();
        Console.Error.WriteLine($"Original first-line font selection (not a receipt): {JsonSerializer.Serialize(runs)}");
    }
}
