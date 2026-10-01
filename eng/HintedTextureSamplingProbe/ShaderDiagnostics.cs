using System.Security.Cryptography;
using System.Text;
using ProGPU.Backend;

namespace HintedTextureSamplingProbe;

internal static class ShaderDiagnostics
{
    internal const string BaselineCommit = "8adb6350927fa64d8d4a025cb8181ff111868b74";
    internal const string CanonicalFrameProfile = "canonical-physical-coverage-frame";

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    internal static string CanonicalHash(string text) => Hash(text.Replace("\r\n", "\n", StringComparison.Ordinal));

    internal static string VerifySource(bool paint)
    {
        string original = paint ? Shaders.HintedGlyphPaintShader : Shaders.TextShader;
        return VerifySource(original, ReadComponents(paint), paint);
    }

    internal static string[] ReadComponents(bool paint)
    {
        string[] names = paint
            ? ["RegisteredMaterialCommon", "TextGlyphGeometryCommon", "TextMaskCommon", "TextGlyphCoverageCommon", "TextureImageSamplingCommon", "HintedGlyphPaint"]
            : ["TextGlyphGeometryCommon", "TextMaskCommon", "TextGlyphCoverageCommon", "Text"];
        return names.Select(name => File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "shader-sources", name + ".wgsl"))).ToArray();
    }

    internal static string VerifySource(string original, string[] components, bool paint)
    {
        // First compare the exact embedded/checked-out bytes after their original
        // UTF-8 decoding. Only the separate parent-content check permits CRLF.
        string files = string.Join("\n", components);
        if (original != files) throw new InvalidOperationException("Embedded shader differs from the recorded source components.");
        _ = SourceProfile(original, paint);
        // Never normalize the shader returned to compilation or exact hashing.
        return original;
    }

    internal static string SourceProfile(string source, bool paint)
    {
        // Admit only the original immutable control or the reviewed coverage-
        // frame candidate. Never accept arbitrary changed shaders, normalize
        // compilation input, or pretend the candidate is the original parent.
        string baselineHash = paint
            ? "2EE776CD0912D979E24F65B201C2AAEA11AFFEFCA454A00418F80AB4C5BAADEC"
            : "008A9A9E0B8CDC4FAEFF422BA799E45896F18FBA2E5AB646DCAE6EC498BB25D1";
        string canonicalFrameHash = paint
            ? "359150D2437D40D867E8D27FF94A05C0B5816E2014DB3A073BE9064D1442CA06"
            : "5C48708499D2A3890A05EAF0B3AD12E2C5DEAC7D534D5567955FDAF4E0DC8270";
        string hash = CanonicalHash(source);
        if (hash == baselineHash) return "original-interpolated-coverage";
        if (hash == canonicalFrameHash) return CanonicalFrameProfile;
        throw new InvalidOperationException("Production shader differs from both reviewed sampling diagnostic source profiles.");
    }

    // Diagnostic-only instrumentation. All original vertex arithmetic, coverage
    // sampling, gamma, alpha arithmetic and guards remain at their exact anchors.
    // The final color is replaced with (atlas U, atlas V, raw sample, final alpha).
    // No production module is edited, and these floats are not a pixel oracle.
    internal static string Instrument(string original, bool paint, bool helperSample = false)
    {
        string result = ReplaceOnce(original, "fn text_coverage_to_alpha(",
            "var<private> diagnosticRawCoverage: f32;\nvar<private> diagnosticHelperSample: vec4<f32>;\nfn text_coverage_to_alpha(");
        result = ReplaceOnce(result, "    let alpha = atlasColor.r;",
            "    let alpha = atlasColor.r;\n    diagnosticRawCoverage = alpha;");
        result = ReplaceOnce(result,
            "    let grayscaleAlpha = text_coverage_to_alpha(alpha, strokeThickness, gamma, aliasedText);",
            "    let grayscaleAlpha = text_coverage_to_alpha(alpha, strokeThickness, gamma, aliasedText);\n    diagnosticHelperSample = vec4<f32>(atlasCoord, alpha, grayscaleAlpha);");
        if (!paint)
        {
            // Gate0 still passes its original varying through the new helper;
            // gate1 observes the actual canonical address, not stale input UV.
            bool canonicalSource = original.Contains("fn text_glyph_coverage_tex_coord(", StringComparison.Ordinal);
            string coordinate = canonicalSource ? "texCoord" : "input.texCoord";
            string anchor = "    return text_glyph_color_with_mask_alpha(input.color, " + coordinate + ", input.texelBounds,\n        input.textMode, input.cornerRadius, input.strokeThickness, maskAlpha);";
            return ReplaceOnce(result, anchor,
                "    let diagnosticColor = text_glyph_color_with_mask_alpha(input.color, " + coordinate + ", input.texelBounds,\n        input.textMode, input.cornerRadius, input.strokeThickness, maskAlpha);\n    return " +
                (helperSample ? "diagnosticHelperSample;" : "vec4<f32>(" + coordinate + ", diagnosticRawCoverage, diagnosticColor.a);"));
        }

        // Scope substitutions to this function: helpers may contain identical
        // return statements, and changing them would invalidate the observation.
        const string start = "fn hinted_glyph_paint_color(";
        int begin = result.IndexOf(start, StringComparison.Ordinal);
        int end = result.IndexOf("\n@fragment", begin, StringComparison.Ordinal);
        if (begin < 0 || end < 0) throw new InvalidOperationException("Missing paint instrumentation function.");
        string body = result[begin..end];
        const string colorReturn = "return vec4<f32>(select(color.rgb, color.rgb * alpha, premultipliedOutput), alpha);";
        if (Count(body, colorReturn) != 2) throw new InvalidOperationException("Paint return anchors changed.");
        string diagnosticReturn = helperSample ? "return diagnosticHelperSample;" : "return vec4<f32>(texCoord, diagnosticRawCoverage, alpha);";
        body = body.Replace(colorReturn, diagnosticReturn, StringComparison.Ordinal);
        body = ReplaceOnce(body, "return vec4<f32>(color.rgb * opacity * coverage * maskAlpha, alpha);",
            diagnosticReturn);
        body = ReplaceOnce(body, "return vec4<f32>(atlas_unpremultiply(color).rgb, alpha);",
            diagnosticReturn);
        return result[..begin] + body + result[end..];
    }

    private static string ReplaceOnce(string source, string anchor, string replacement)
    {
        string windowsAnchor = anchor.Replace("\n", "\r\n", StringComparison.Ordinal);
        int ordinaryCount = Count(source, anchor);
        int windowsCount = windowsAnchor == anchor ? 0 : Count(source, windowsAnchor);
        if (ordinaryCount + windowsCount != 1)
            throw new InvalidOperationException("Diagnostic shader anchor changed: " + anchor);
        string selectedAnchor = windowsCount == 1 ? windowsAnchor : anchor;
        bool windowsLines = windowsCount == 1 ||
            (windowsAnchor == anchor && source.Contains("\r\n", StringComparison.Ordinal));
        string selectedReplacement = windowsLines
            ? replacement.Replace("\n", "\r\n", StringComparison.Ordinal) : replacement;
        return source.Replace(selectedAnchor, selectedReplacement, StringComparison.Ordinal);
    }

    private static int Count(string source, string anchor)
    {
        int count = 0, position = 0;
        while ((position = source.IndexOf(anchor, position, StringComparison.Ordinal)) >= 0)
        { count++; position += anchor.Length; }
        return count;
    }
}
