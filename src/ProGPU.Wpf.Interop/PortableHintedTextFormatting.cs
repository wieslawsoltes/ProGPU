namespace ProGPU.Wpf.Interop;

public enum PortableTextHintInterpreter { TrueType35, TrueType40 }
public enum PortableHintedTextProjection { Automatic, NativeCompute, GpuShader, IntrinsicSimd, ScalarReference }
public enum PortableHintedTextCoverage { Strict, NonzeroVector, AntialiasedVector }

/// <summary>Explicit physical 26.6 size/phase and original-order 16.16 axis range for one source style.</summary>
public readonly record struct PortableTextHintingStyle(
    uint XPixelsPerEm266, uint YPixelsPerEm266, PortableTextHintInterpreter Interpreter,
    uint XPhase266 = 0, uint YPhase266 = 0, uint VariationStart = 0, uint VariationCount = 0);

public readonly record struct PortableHintedTextOptions(
    float DpiScale, PortableHintedTextProjection Projection, PortableHintedTextCoverage Coverage);

/// <summary>
/// Optional original-generation formatting. This is not source Display admission.
/// Explicit styles partition the original text; metrics/device styles match that
/// partition. Inputs are borrowed only until return. Unsupported policies throw;
/// they cannot fall through to ordinary formatting or return empty replacement text.
/// Normalized 2.14 shaping coordinates retain the same selected variable instance
/// as the original-order 16.16 device axes; the native producer verifies equality.
/// </summary>
public interface IPortableHintedTextFormatting
{
    IPortableHintedTextParagraph FormatHinted(in PortableTextParagraphRequest request,
        ReadOnlySpan<PortableTextStyleMetrics> metrics,
        ReadOnlySpan<PortableTextHintingStyle> deviceStyles,
        in PortableHintedTextOptions options,
        ReadOnlySpan<int> variationCoordinates16_16 = default,
        ReadOnlySpan<short> normalizedCoordinates = default);

    /// <summary>
    /// Explicit original nominal hmtx retention for source offset binding.
    /// Missing metrics and coordinate-bearing instances reject atomically;
    /// the ordinary preparation above remains unchanged. Not Display admission.
    /// </summary>
    IPortableHintedTextParagraph FormatHintedWithNominalMetrics(in PortableTextParagraphRequest request,
        ReadOnlySpan<PortableTextStyleMetrics> metrics, ReadOnlySpan<PortableTextHintingStyle> deviceStyles,
        in PortableHintedTextOptions options)
        => throw new NotSupportedException("The hinted provider does not retain original nominal design metrics.");
}

/// <summary>Original positioned occurrence, including no-ink and repeated glyphs. Coordinates stay in paragraph DIPs.</summary>
public readonly record struct PortableHintedTextGlyph(
    int PositionedIndex, uint LogicalIndex, uint GlyphId, uint FontIndex, uint StyleIndex,
    uint RunIndex, uint RunGlyphIndex, uint DescriptorIndex, int Cluster, int ClusterEnd,
    sbyte BidiLevel, float X, float Y, float AdvanceX, float AdvanceY);

/// <summary>
/// Actual measured native baseline/height and writer pen origin. BaselineY is not
/// a line top; empty-row navigation must not infer a top or manufacture a caret.
/// </summary>
public readonly record struct PortableHintedTextLine(
    int GlyphStart, int GlyphCount, int InputStart, int InputEnd,
    float Width, float BaselineY, float Height, float PenOriginX, bool Clipped);

public readonly record struct PortableHintedTextClusterBox(
    int InputStart, int InputEnd, int LineIndex, float X, float Y, float Width, float Height,
    sbyte BidiLevel);

public readonly record struct PortableHintedTextCaret(
    int Position, bool Trailing, int LineIndex, float X, float Y, float Height, sbyte BidiLevel);

/// <summary>
/// One explicit owning reference to the original formatted generation. Retain
/// returns an independent reference; disposal excludes new uses of this reference
/// while already acquired paragraph/run references remain valid. Snapshots contain
/// no native pointers. This contract deliberately exposes no design-font alias,
/// continuation, collapse, object, or automatic Display capability.
/// </summary>
public interface IPortableHintedTextParagraph : IDisposable
{
    bool IsDisposed { get; }
    float DpiScale { get; }
    ReadOnlyMemory<char> SourceText { get; }
    ReadOnlyMemory<PortableHintedTextGlyph> Glyphs { get; }
    ReadOnlyMemory<PortableHintedTextLine> Lines { get; }
    ReadOnlyMemory<PortableHintedTextClusterBox> Boxes { get; }
    ReadOnlyMemory<PortableHintedTextCaret> Carets { get; }
    IPortableHintedTextParagraph Retain();
    /// <summary>
    /// Copies explicit original positioned indices in caller order, including RTL
    /// permutations and no-ink occurrences. No lookup by glyph ID or reshaping.
    /// Every index is validated before acquiring or publishing the independent run.
    /// </summary>
    IPortableHintedTextGlyphRun AcquireGlyphRun(ReadOnlySpan<int> positionedIndices);
}

/// <summary>An independently owned selection in one original paragraph generation.</summary>
public interface IPortableHintedTextGlyphRun : IDisposable
{
    bool IsDisposed { get; }
    ReadOnlyMemory<int> PositionedGlyphIndices { get; }
    IPortableHintedTextGlyphRun Retain();
    /// <summary>Acquires independent access to the same original layout and interaction.</summary>
    IPortableHintedTextParagraph AcquireParagraph();
}
