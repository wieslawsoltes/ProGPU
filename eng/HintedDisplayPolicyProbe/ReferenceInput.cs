using System.Security.Cryptography;
using System.Text.Json;

namespace HintedDisplayPolicyProbe;

internal static class ReferenceInput
{
    internal const string FontHash = "40D692FCE188E4471E2B3CBA937BE967878F631AD3EBBBDCD587687C7EBE0C82";
    internal const string ReferenceCommit = "3195c6e963276957591d9cab8343f2c6f99814a8";

    internal static JsonDocument Read(string path, byte[] font)
    {
        if (Convert.ToHexString(SHA256.HashData(font)) != FontHash)
            throw new InvalidDataException("The probe requires the original pinned Inter-Regular bytes.");
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Reference receipt exceeds its budget.");
        byte[] bytes = File.ReadAllBytes(path);
        var document = JsonDocument.Parse(bytes);
        try { Validate(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    internal static void Validate(JsonElement root)
    {
        if (root.GetProperty("Schema").GetInt32() != 1 || root.GetProperty("CaseCount").GetInt32() != 192 ||
            root.GetProperty("SourceCommit").GetString() != ReferenceCommit ||
            root.GetProperty("Font").GetProperty("Sha256").GetString() != FontHash ||
            root.GetProperty("Architecture").GetString() is not ("X64" or "Arm64") ||
            !root.GetProperty("PresentationIdentity").GetString()!.Contains("PublicKeyToken=31bf3856ad364e35", StringComparison.Ordinal))
            throw new InvalidDataException("An original reviewed Microsoft WPF receipt is required.");
        var keys = new HashSet<(string?, string?, double, double, string?, double)>();
        int lineCount = 0, runCount = 0;
        foreach (var item in root.GetProperty("Cases").EnumerateArray())
        {
            string text = item.GetProperty("Text").GetString()!;
            string? mode = item.GetProperty("Mode").GetString(), direction = item.GetProperty("Direction").GetString();
            double dpi = item.GetProperty("Dpi").GetDouble(), em = item.GetProperty("Em").GetDouble(), width = item.GetProperty("Width").GetDouble();
            if (mode is not ("Ideal" or "Display") || direction is not ("LeftToRight" or "RightToLeft") ||
                dpi is not (1 or 1.25 or 1.5 or 2) || em is not (13 or 17) || width is not (25 or 1000) ||
                text is not ("Hello" or "AVATAR fi A A " or "a\u0301 b c ") || !keys.Add((mode, direction, dpi, em, text, width)))
                throw new InvalidDataException("Changed or repeated reference case inventory.");
            int sourceStart = 0;
            foreach (var line in item.GetProperty("Lines").EnumerateArray())
            {
                lineCount++;
                int length = line.GetProperty("Length").GetInt32();
                if (line.GetProperty("SourceStart").GetInt32() != sourceStart || length <= 0)
                    throw new InvalidDataException("Reference lines lost their original source progress.");
                foreach (var run in line.GetProperty("Runs").EnumerateArray())
                {
                    runCount++;
                    int count = run.GetProperty("GlyphIds").GetArrayLength();
                    int start = run.GetProperty("TextSourceCharacterIndex").GetInt32(), runLength = run.GetProperty("TextSourceLength").GetInt32();
                    if (count == 0 || count != run.GetProperty("Advances").GetArrayLength() ||
                        run.GetProperty("Offsets").GetArrayLength() != count || run.GetProperty("IsSideways").GetBoolean() ||
                        run.GetProperty("PixelsPerDip").GetDouble() != dpi || run.GetProperty("FontRenderingEmSize").GetDouble() != em ||
                        run.GetProperty("StyleSimulations").GetString() != "None" || start < sourceStart || runLength <= 0 ||
                        start + runLength > Math.Min(text.Length, sourceStart + length))
                        throw new InvalidDataException("Reference run identity or frame is incomplete.");
                    if (!run.GetProperty("Utf16").EnumerateArray().Select(value => checked((char)value.GetInt32()))
                            .SequenceEqual(text.AsSpan(start, runLength).ToArray()) ||
                        run.GetProperty("Clusters").GetArrayLength() != runLength ||
                        run.GetProperty("Clusters").EnumerateArray().Any(value => value.GetUInt32() >= count) ||
                        run.GetProperty("GlyphIds").EnumerateArray().Any(value => value.GetUInt32() > ushort.MaxValue) ||
                        run.GetProperty("Baseline").GetArrayLength() != 2)
                        throw new InvalidDataException("Original UTF-16, cluster or glyph identity changed.");
                    foreach (var coordinate in run.GetProperty("Baseline").EnumerateArray()) Finite(coordinate.GetDouble());
                    foreach (var advance in run.GetProperty("Advances").EnumerateArray()) Finite(advance.GetDouble());
                    foreach (var offset in run.GetProperty("Offsets").EnumerateArray())
                    {
                        if (offset.GetArrayLength() != 2) throw new InvalidDataException("Invalid original offset.");
                        foreach (var coordinate in offset.EnumerateArray()) Finite(coordinate.GetDouble());
                    }
                }
                sourceStart = checked(sourceStart + length);
            }
            if (sourceStart < text.Length) throw new InvalidDataException("Reference paragraph is incomplete.");
        }
        if (keys.Count != 192 || lineCount != 386 || runCount != 450)
            throw new InvalidDataException("Reference case/line/run inventory is incomplete.");
    }

    internal static double DeviceToDip(long value26_6, double dpi)
    {
        if (!double.IsFinite(dpi) || dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));
        // Diagnostic conversion only, never a source repair or policy selection.
        return value26_6 / 64.0 / dpi;
    }

    private static void Finite(double value)
    { if (!double.IsFinite(value)) throw new InvalidDataException("Nonfinite original source value."); }
}
