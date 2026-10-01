namespace ProGPU.Backend.Native;

public sealed unsafe partial class NativeTextShapingContext
{
    /// <summary>
    /// Formats the complete original UTF-16 paragraph through the same scalar and
    /// style transport as NativeTextParagraphSnapshot. Device configuration and
    /// source metrics remain explicit. No suffix, glyph, or prefix is reshaped.
    /// </summary>
    public NativeHintedParagraph LayoutHintedParagraph(ReadOnlySpan<char> text,
        NativeTextDirection direction, in NativeTextParagraphOptions options,
        ReadOnlySpan<NativeTextParagraphStyle> styles,
        ReadOnlySpan<NativeTextStyleMetrics> metrics,
        ReadOnlySpan<NativeHintedParagraphDeviceStyle> deviceStyles,
        ReadOnlySpan<NativeTextFeature> features = default,
        ReadOnlySpan<int> variationCoordinates16_16 = default,
        ReadOnlySpan<short> normalizedCoordinates = default)
    {
        if (styles.IsEmpty || metrics.Length != styles.Length || deviceStyles.Length != styles.Length)
            throw new ArgumentException("Hinted UTF-16 paragraphs require explicit matching styles, metrics and device configurations.");
        var scalars = new NativeTextScalar[text.Length];
        int count = NativeTextParagraphSnapshot.DecodeUtf16(text, scalars);
        var nativeStyles = NativeTextParagraphSnapshot.MapStyles(styles, scalars.AsSpan(0, count), text.Length);
        var input = new NativeTextShapeInput(default, scalars.AsSpan(0, count), direction: direction,
            features: features, normalizedCoordinates: normalizedCoordinates);
        return LayoutHintedParagraph(in input, in options, nativeStyles, metrics, deviceStyles,
            variationCoordinates16_16);
    }
}
