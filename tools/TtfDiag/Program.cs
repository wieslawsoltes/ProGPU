using System;
using System.IO;
using System.Globalization;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.Tools;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
        {
            ShowUsage();
            return 0;
        }

        string fontPath = args[0];
        if (!File.Exists(fontPath))
        {
            // Fallback for Arial convenience on macOS Supplemental folder
            if (fontPath.Equals("Arial", StringComparison.OrdinalIgnoreCase) || fontPath.Equals("Arial.ttf", StringComparison.OrdinalIgnoreCase))
            {
                fontPath = "/System/Library/Fonts/Supplemental/Arial.ttf";
            }
        }

        if (!File.Exists(fontPath))
        {
            Console.Error.WriteLine($"Error: Font file not found at: '{args[0]}'");
            return 1;
        }

        string charsToDump = "Gg";
        if (args.Length > 1)
        {
            charsToDump = string.Join("", args[1..]);
        }

        try
        {
            Console.WriteLine($"[TtfDiag] Loading font: {fontPath}");
            var font = new TtfFont(fontPath);
            Console.WriteLine($"[TtfDiag] Units per Em: {font.UnitsPerEm}, Total Glyphs: {font.NumGlyphs}");
            Console.WriteLine($"[TtfDiag] Ascender: {font.Ascender}, Descender: {font.Descender}, LineGap: {font.LineGap}");
            Console.WriteLine();

            foreach (char c in charsToDump)
            {
                ushort glyphIdx = font.GetGlyphIndex(c);
                var outline = font.GetGlyphOutline(glyphIdx);

                Console.WriteLine($"================================================================================");
                Console.WriteLine($"Glyph: '{c}' | Unicode: U+{(int)c:X4} | Glyph Index: {glyphIdx}");
                Console.WriteLine($"================================================================================");

                if (outline == null)
                {
                    Console.WriteLine("Outline is null (empty or composite glyph)");
                    Console.WriteLine();
                    continue;
                }

                Console.WriteLine($"Figures count: {outline.Figures.Count}");
                for (int f = 0; f < outline.Figures.Count; f++)
                {
                    var fig = outline.Figures[f];
                    Console.WriteLine($"  Figure {f}: StartPoint = ({fig.StartPoint.X.ToString(CultureInfo.InvariantCulture)}, {fig.StartPoint.Y.ToString(CultureInfo.InvariantCulture)}), Closed = {fig.IsClosed}, Filled = {fig.IsFilled}");
                    Console.WriteLine($"    Segments count: {fig.Segments.Count}");

                    for (int s = 0; s < fig.Segments.Count; s++)
                    {
                        var seg = fig.Segments[s];
                        if (seg is LineSegment line)
                        {
                            Console.WriteLine($"      Segment {s:D2}: [Line]  -> End: ({line.Point.X.ToString(CultureInfo.InvariantCulture)}, {line.Point.Y.ToString(CultureInfo.InvariantCulture)})");
                        }
                        else if (seg is QuadraticBezierSegment quad)
                        {
                            Console.WriteLine($"      Segment {s:D2}: [Quad]  -> End: ({quad.Point.X.ToString(CultureInfo.InvariantCulture)}, {quad.Point.Y.ToString(CultureInfo.InvariantCulture)}) | Ctrl: ({quad.ControlPoint.X.ToString(CultureInfo.InvariantCulture)}, {quad.ControlPoint.Y.ToString(CultureInfo.InvariantCulture)})");
                        }
                        else
                        {
                            Console.WriteLine($"      Segment {s:D2}: [Other] -> {seg.GetType().Name}");
                        }
                    }
                    Console.WriteLine();
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"An error occurred parsing the font: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }

        return 0;
    }

    private static void ShowUsage()
    {
        Console.WriteLine("ProGPU TTF Font Outline Diagnostic Tool (TtfDiag)");
        Console.WriteLine("=================================================");
        Console.WriteLine("Extracts and prints exact TrueType outline geometry coordinates and segments to help debug text issues.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project tools/TtfDiag -- <font-path-or-name> [characters]");
        Console.WriteLine();
        Console.WriteLine("Arguments:");
        Console.WriteLine("  <font-path-or-name>   Path to a TTF font file, or 'Arial' to use macOS system Arial fallback.");
        Console.WriteLine("  [characters]          Characters to extract outlines for (defaults to 'Gg' if omitted).");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project tools/TtfDiag -- Arial Gg");
        Console.WriteLine("  dotnet run --project tools/TtfDiag -- /System/Library/Fonts/Supplemental/Georgia.ttf ABC");
    }
}
