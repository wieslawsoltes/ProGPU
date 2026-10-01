using System.Security.Cryptography;
using System.Text;
using ProGPU.Backend;

namespace HintedTextureSamplingProbe;

internal static class ShaderDiagnostics
{
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    internal static string VerifySource(bool paint)
    {
        string[] names = paint
            ? ["RegisteredMaterialCommon", "TextGlyphGeometryCommon", "TextMaskCommon", "TextGlyphCoverageCommon", "TextureImageSamplingCommon", "HintedGlyphPaint"]
            : ["TextGlyphGeometryCommon", "TextMaskCommon", "TextGlyphCoverageCommon", "Text"];
        string original = paint ? Shaders.HintedGlyphPaintShader : Shaders.TextShader;
        string files = string.Join("\n", names.Select(name => File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "shader-sources", name + ".wgsl"))));
        if (original != files) throw new InvalidOperationException("Embedded shader differs from the recorded source components.");
        string expectedHash = paint
            ? "2EE776CD0912D979E24F65B201C2AAEA11AFFEFCA454A00418F80AB4C5BAADEC"
            : "008A9A9E0B8CDC4FAEFF422BA799E45896F18FBA2E5AB646DCAE6EC498BB25D1";
        if (Hash(original) != expectedHash)
            throw new InvalidOperationException("Production shader differs from the reviewed 8adb sampling diagnostic parent.");
        return original;
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
            const string anchor = "    return text_glyph_color_with_mask_alpha(input.color, input.texCoord, input.texelBounds,\n        input.textMode, input.cornerRadius, input.strokeThickness, maskAlpha);";
            return ReplaceOnce(result, anchor,
                "    let diagnosticColor = text_glyph_color_with_mask_alpha(input.color, input.texCoord, input.texelBounds,\n        input.textMode, input.cornerRadius, input.strokeThickness, maskAlpha);\n    return " +
                (helperSample ? "diagnosticHelperSample;" : "vec4<f32>(input.texCoord, diagnosticRawCoverage, diagnosticColor.a);"));
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
        if (Count(source, anchor) != 1) throw new InvalidOperationException("Diagnostic shader anchor changed: " + anchor);
        return source.Replace(anchor, replacement, StringComparison.Ordinal);
    }

    private static int Count(string source, string anchor)
    {
        int count = 0, position = 0;
        while ((position = source.IndexOf(anchor, position, StringComparison.Ordinal)) >= 0)
        { count++; position += anchor.Length; }
        return count;
    }
}
