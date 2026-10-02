using System.Globalization;
using System.Text.Json;

// Pure input/coverage contract, shared with device-free tests. No native metric
// hypothesis is an expected value here: all output comes from TextFormatter.
internal sealed record MidpointInput(int Ordinal, string Mode, string Direction, double Dpi,
    double MidpointPhysicalEm, string Side, double PhysicalEm, double Em, string TextKind, string Text, double Width)
{
    public string FontKey => TextKind == "RtlPositioned" ? "NotoSansHebrewRegular" : "InterRegular";
    public string DpiBits => MidpointCases.Bits(Dpi);
    public string EmBits => MidpointCases.Bits(Em);
    public string PhysicalEmBits => MidpointCases.Bits(PhysicalEm);
    public string RecomputedPhysicalEmBits => MidpointCases.Bits(Em * Dpi);
}

internal sealed record MidpointCoverage(int RunCount, int GlyphCount, int NonzeroOffsetCount, int OddDirectionRunCount,
    int OddDirectionNonzeroOffsetCount = 0);

internal static class MidpointCases
{
    internal const string LegacyFamily = "midpoint-positioned-v2";
    internal const string Family = "midpoint-rtl-positioned-v3";
    internal const int Count = 288;
    internal const string PositionedText = "x\u0301 m\u0302 A\u0308 ";
    internal const string RtlPositionedText = "\u05E9\u05B8\u05C1\u05DC\u05D5\u05B9\u05DD ";

    internal static string Bits(double value)
        => BitConverter.DoubleToUInt64Bits(value).ToString("X16", CultureInfo.InvariantCulture);

    internal static IEnumerable<MidpointInput> Create()
    {
        // Preserve the failed v2 hypothesis as its own input inventory. v3 uses
        // strong Hebrew source characters and a separately pinned physical face,
        // not relabelled RLO observations or paragraph direction as run evidence.
        foreach (MidpointInput input in CreateLegacy())
            yield return input.TextKind == "RtlOverride"
                ? input with { TextKind = "RtlPositioned", Text = RtlPositionedText }
                : input;
    }

    internal static IEnumerable<MidpointInput> CreateLegacy()
    {
        int ordinal = 0;
        foreach (string mode in new[] { "Ideal", "Display" })
        foreach (string direction in new[] { "LeftToRight", "RightToLeft" })
        // Binary-exact scales preserve both adjacent source-double sides of the
        // physical midpoint. These are additional cases, not replacements for
        // the original four-scale inventory.
        foreach (double dpi in new[] { 1.0, 2.0 })
        foreach (double midpoint in new[] { 18.5, 20.5 })
        foreach (string side in new[] { "Below", "Exact", "Above" })
        foreach (string kind in new[] { "Plain", "Positioned", "RtlOverride" })
        foreach (double width in new[] { 25.0, 1000.0 })
        {
            double physical = side switch
            {
                "Below" => Math.BitDecrement(midpoint),
                "Above" => Math.BitIncrement(midpoint),
                _ => midpoint,
            };
            double em = physical / dpi;
            if (Bits(em * dpi) != Bits(physical))
                throw new InvalidOperationException("The input lost its exact physical-em side.");
            string text = kind switch
            {
                "Plain" => "Hello",
                "Positioned" => PositionedText,
                // Latin in an RTL paragraph need not produce an RTL GlyphRun.
                // Keep the original Unicode controls and the no-control pair.
                _ => "\u202E" + PositionedText + "\u202C",
            };
            yield return new(ordinal++, mode, direction, dpi, midpoint, side, physical, em, kind, text, width);
        }
        if (ordinal != Count) throw new InvalidOperationException("Midpoint inventory changed.");
    }

    internal static MidpointCoverage Observe(MidpointInput input, JsonElement original)
    {
        if (original.GetProperty("Text").GetString() != input.Text
            || original.GetProperty("Mode").GetString() != input.Mode
            || original.GetProperty("Direction").GetString() != input.Direction
            || Bits(original.GetProperty("Dpi").GetDouble()) != input.DpiBits
            || Bits(original.GetProperty("Em").GetDouble()) != input.EmBits
            || original.GetProperty("Width").GetDouble() != input.Width)
            throw new InvalidOperationException("Original source input identity changed.");
        int runs = 0, glyphs = 0, offsets = 0, odd = 0, oddOffsets = 0;
        foreach (JsonElement line in original.GetProperty("Lines").EnumerateArray())
        foreach (JsonElement run in line.GetProperty("Runs").EnumerateArray())
        {
            ++runs;
            int count = run.GetProperty("GlyphIds").GetArrayLength();
            glyphs = checked(glyphs + count);
            if (run.GetProperty("Advances").GetArrayLength() != count
                || run.GetProperty("NominalDesignAdvances").GetArrayLength() != count)
                throw new InvalidOperationException("Original glyph/advance identity is incomplete.");
            bool isOdd = (run.GetProperty("BidiLevel").GetInt32() & 1) != 0;
            if (isOdd) ++odd;
            JsonElement points = run.GetProperty("Offsets");
            if (points.ValueKind == JsonValueKind.Null) continue;
            if (points.GetArrayLength() != count)
                throw new InvalidOperationException("Original glyph/offset identity is incomplete.");
            foreach (JsonElement point in points.EnumerateArray())
            {
                if (point.GetArrayLength() != 2
                    || !double.IsFinite(point[0].GetDouble()) || !double.IsFinite(point[1].GetDouble()))
                    throw new InvalidOperationException("Original offset was not a finite pair.");
                if (point[0].GetDouble() != 0 || point[1].GetDouble() != 0)
                {
                    ++offsets;
                    if (isOdd) ++oddOffsets;
                }
            }
        }
        if (runs == 0 || glyphs == 0)
            throw new InvalidOperationException($"Midpoint case {input.Ordinal} produced no original glyphs.");
        if (input.TextKind != "Plain" && offsets == 0)
            throw new InvalidOperationException($"Midpoint case {input.Ordinal} did not observe nonzero original mark offsets.");
        if (input.TextKind is "RtlOverride" or "RtlPositioned" && odd == 0)
            throw new InvalidOperationException($"Midpoint case {input.Ordinal} did not observe an odd-direction original GlyphRun.");
        if (input.TextKind == "RtlPositioned" && oddOffsets == 0)
            throw new InvalidOperationException($"Midpoint case {input.Ordinal} did not observe a nonzero offset in an odd-direction original GlyphRun.");
        return new(runs, glyphs, offsets, odd, oddOffsets);
    }
}
