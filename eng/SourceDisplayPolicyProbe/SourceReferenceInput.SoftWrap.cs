using System.Text.Json;
using HintedDisplayPolicyProbe;

namespace SourceDisplayPolicyProbe;

internal sealed partial class SourceReferenceInput
{
    private static readonly HashSet<string> SoftWrapHashes = new(StringComparer.Ordinal)
    {
        "C7D621870AE3BE67205FEF73B4471C07B24320BE450B73D5605C01C87DD98216",
        "CA8B7CA06A3DE5C6F767B6A137DC6308F366AD813F46451B3A3E5569F06BA9F3"
    };
    internal bool IsSoftWrap => Root.GetProperty("Schema").GetInt32() == 106;
    internal bool HasRtlFont => IsMidpoint || IsSoftWrap;

    private static void ValidateSoftWrap(JsonElement root)
    {
        if (root.GetProperty("Architecture").GetString() is not ("X64" or "Arm64") ||
            root.GetProperty("OriginalSourceCommit").GetString() != "92f4778b0db2d802d84b50281bc4c4e97349b4db" ||
            root.GetProperty("CaseFamily").GetString() != "soft-wrap-spaces-v1" ||
            root.GetProperty("CaseCount").GetInt32() != 198 || root.GetProperty("Cases").GetArrayLength() != 198 ||
            !root.GetProperty("SupplementalDrawingFrames").GetBoolean() || !root.GetProperty("SupplementalNumericMetrics").GetBoolean() ||
            root.GetProperty("Font").GetProperty("Sha256").GetString() != ReferenceInput.FontHash ||
            root.GetProperty("RtlFont").GetProperty("Font").GetProperty("Sha256").GetString() != RtlFontHash ||
            !root.GetProperty("PresentationIdentity").GetString()!.Contains("PublicKeyToken=31bf3856ad364e35", StringComparison.Ordinal))
            throw new InvalidDataException("Original supplemental Microsoft identity changed.");
        string[] texts = ["AB CD EF ", "AB  CD   EF  ", "AB\u00a0CD EF ", "AB\u2002CD EF ",
            "a\u0301 b c ", "AB CD\nEF GH ", "AB 12 CD ", "\u05d0\u05d1 \u05d2\u05d3 \u05d4\u05d5 ",
            "\u05d0\u05d1  \u05d2\u05d3   \u05d4\u05d5  ", "\u05d0\u05d1\u00a0\u05d2\u05d3 \u05d4\u05d5 ",
            "\u05d0\u05d1 \u05d2\u05d3\n\u05d4\u05d5 \u05d0\u05d1 "];
        int ordinal = 0, lines = 0, runs = 0;
        foreach (string text in texts)
        foreach (string direction in new[] { "LeftToRight", "RightToLeft" })
        foreach (double dpi in new[] { 1.0, 1.25, 1.5 })
        foreach (double width in new[] { 25.0, 41.0, 1000.0 })
        {
            var item = root.GetProperty("Cases")[ordinal];
            string font = ordinal / 18 < 7 ? ReferenceInput.FontHash : RtlFontHash;
            if (item.GetProperty("Text").GetString() != text || item.GetProperty("Direction").GetString() != direction ||
                item.GetProperty("Mode").GetString() != "Display" || item.GetProperty("Dpi").GetDouble() != dpi ||
                item.GetProperty("Width").GetDouble() != width || item.GetProperty("Em").GetDouble() != 13)
                throw new InvalidDataException("Supplemental original source inventory changed.");
            _ = GetMetrics(root, item, font);
            foreach (var line in item.GetProperty("Lines").EnumerateArray())
            {
                ++lines; runs += line.GetProperty("Runs").GetArrayLength();
                _ = line.GetProperty("SupplementalDrawingFrame");
                _ = line.GetProperty("SupplementalMetricFields");
            }
            ++ordinal;
        }
        if (ordinal != 198 || lines != 410 || runs != 622)
            throw new InvalidDataException("Supplemental original line/run inventory changed.");
    }
}
