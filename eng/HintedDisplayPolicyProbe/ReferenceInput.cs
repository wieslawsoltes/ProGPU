using System.Security.Cryptography;
using System.Text.Json;

namespace HintedDisplayPolicyProbe;

internal static class ReferenceInput
{
    internal const string FontHash = "40D692FCE188E4471E2B3CBA937BE967878F631AD3EBBBDCD587687C7EBE0C82";
    internal const string ReferenceCommit = "3195c6e963276957591d9cab8343f2c6f99814a8";
    internal const string SourceInputCommit = "432f5d5d5ef587681dd64adfde0be863979c9724";

    internal static bool HasIndependentSourceInputs(JsonElement root)
        => root.GetProperty("SourceCommit").GetString() == SourceInputCommit;

    // Exact unmodified receipts from whole-successful source reference runs
    // 36864998524 and 36866824045. Self-declared identity is not provenance.
    private static readonly HashSet<string> ReceiptHashes = new(StringComparer.Ordinal)
    {
        "2E5592DAED549A1471328E28626DA819F5B9C661EA1993A0BAB68AA8CC1815DF",
        "57929419229BD8E8042CC69536BBCF58F7D7B0E6A6AB1BFB37016D1654C23B5A",
        "911C90F310641D25C99E232F84698309C26AD4BD8EA2EF0FFBCA16A148ADF268",
        "3BDC9725AD75956DB04FE830756F96D58E34211D9454DB9D13AEB3974E3AA01B"
    };

    internal sealed class VerifiedReference(JsonDocument document, string path, string sha256) : IDisposable
    {
        internal JsonElement RootElement => document.RootElement;
        internal string Path { get; } = path;
        internal string Sha256 { get; } = sha256;
        public void Dispose() => document.Dispose();
    }

    internal static VerifiedReference Read(string path, byte[] font)
    {
        if (Convert.ToHexString(SHA256.HashData(font)) != FontHash)
            throw new InvalidDataException("The probe requires the original pinned Inter-Regular bytes.");
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Reference receipt exceeds its budget.");
        byte[] bytes = File.ReadAllBytes(path);
        return ParseVerified(bytes, System.IO.Path.GetFullPath(path));
    }

    internal static VerifiedReference ParseVerified(ReadOnlySpan<byte> bytes, string path)
    {
        // The hash and parser consume one private immutable snapshot. Never
        // re-read a mutable path later to manufacture publication provenance.
        byte[] owned = bytes.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(owned));
        if (!ReceiptHashes.Contains(hash)) throw new InvalidDataException("Receipt bytes do not match an exact successful original reference.");
        var document = JsonDocument.Parse(owned);
        try { ValidateStructure(document.RootElement); return new(document, path, hash); }
        catch { document.Dispose(); throw; }
    }

    // Structural controls alone are not original-receipt provenance. Production
    // must enter through Read/ParseVerified and its exact-byte hash allowlist.
    internal static void ValidateStructure(JsonElement root)
    {
        if (root.GetProperty("Schema").GetInt32() != 1 || root.GetProperty("CaseCount").GetInt32() != 192 ||
            root.GetProperty("SourceCommit").GetString() is not (ReferenceCommit or SourceInputCommit) ||
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
            bool sourceInputs = HasIndependentSourceInputs(root);
            if (sourceInputs)
            {
                if (!item.TryGetProperty("SourceFont", out var source))
                    throw new InvalidDataException("This producer requires separately captured source font inputs.");
                double baseline = source.GetProperty("Baseline").GetDouble(), spacing = source.GetProperty("LineSpacing").GetDouble();
                Finite(baseline); Finite(spacing);
                if (baseline <= 0 || spacing < baseline || source.GetProperty("FontRenderingEmSize").GetDouble() != em ||
                    source.GetProperty("FontHintingEmSize").GetDouble() != em || source.GetProperty("Culture").GetString() != "")
                    throw new InvalidDataException("Independent source font inputs changed or are incomplete.");
            }
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
                    if (sourceInputs)
                    {
                        if (!run.TryGetProperty("FontMetrics", out var face) || !run.TryGetProperty("NominalDesignAdvances", out var nominal) ||
                            nominal.GetArrayLength() != count)
                            throw new InvalidDataException("Independent physical-face metrics must cover every original occurrence.");
                        foreach (string field in new[] { "Baseline", "Height", "CapsHeight", "XHeight", "UnderlinePosition", "UnderlineThickness" })
                            Finite(face.GetProperty(field).GetDouble());
                        foreach (var advance in nominal.EnumerateArray())
                        {
                            double value = advance.GetDouble(); Finite(value);
                            if (value < 0) throw new InvalidDataException("Invalid nominal design advance.");
                        }
                    }
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
