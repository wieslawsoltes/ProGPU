using ProGPU.Backend;

namespace ProGPU.Scene;

internal static class GlyphCoverageTransferPolicy
{
    internal static bool IsSupported(GlyphCoverageTransfer transfer, TextRenderingMode mode) =>
        transfer.Encoding <= 13u && (transfer == default || mode == TextRenderingMode.Grayscale);

    internal static void Validate(GlyphCoverageTransfer transfer, TextRenderingMode mode)
    {
        if (!IsSupported(transfer, mode))
            throw new ArgumentException("Explicit source coverage transfer requires grayscale glyph coverage.");
    }

    // The vector coverage field is a positive exponent for legacy commands.
    // Negative integers -1..-13 explicitly encode the new source transfer.
    internal static float VectorEncoding(GlyphCoverageTransfer transfer, float legacyGamma) =>
        transfer == default ? legacyGamma : -(float)transfer.Encoding;
}
