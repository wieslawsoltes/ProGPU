namespace ProGPU.Wpf.Interop;

/// <summary>Original source values, before any float transport or device conversion.</summary>
public readonly record struct PortableDisplayTextStyle(double EmSize, double Ascent, double Descent);

/// <summary>
/// Exact source paragraph dimensions. The corresponding float fields in the common
/// request are compatibility transport, not authority for Display layout. Zero
/// MaximumWidth is unconstrained. PixelsPerDip is the original source device scale.
/// </summary>
public readonly record struct PortableDisplayTextOptions(double PixelsPerDip, double EmSize,
    double LineHeight, double MaximumWidth, double TabOrigin);

/// <summary>
/// Optional complete source Display formatting, separate from explicit ppem hinting.
/// The request retains original text, font bytes, UTF-16 style boundaries, features,
/// digit/bidi policy, wrapping and intrinsic-width intent. Source styles partition
/// that same text. Inputs are borrowed only until return; the producer must own them.
///
/// Advertisement requires native-owned source rounding, fitting/recomposition,
/// drawing and interaction in one retained generation. Unsupported text, fonts,
/// numeric frames or policies must throw before publication, never call Ideal or
/// reshape a prefix. This capability does not select an interpreter or infer ppem
/// in the source framework, nor make a metrics-only producer a Display provider.
/// </summary>
public interface IPortableDisplayTextFormatting : IPortableTextFormatting
{
    IPortableDisplayTextParagraph FormatDisplay(in PortableTextParagraphRequest request,
        ReadOnlySpan<PortableDisplayTextStyle> sourceStyles, in PortableDisplayTextOptions options);
}

/// <summary>Native-owned DIP metrics in the existing original positioned-glyph order.</summary>
public readonly record struct PortableDisplayTextGlyphMetrics(double X, double Y, double Advance);

/// <summary>
/// Native writer frame, in the existing original line order. Top, baseline and
/// baseline offset are independently retained outputs, not source prefix sums.
/// </summary>
public readonly record struct PortableDisplayTextLineMetrics(double Width, double Top, double Height,
    double BaselineOffset, double BaselineY);

public readonly record struct PortableDisplayTextIntrinsicWidths(double Minimum, double Maximum);

/// <summary>
/// Owns the complete original source Display generation. Common glyph/line arrays
/// supply occurrence, font, bidi and UTF-16 identities, not Display coordinates.
/// Display metrics have exactly the same indexing and preserve doubles end to end.
/// Selection rectangles and logical caret boundaries use the common source methods
/// against this same generation. The legacy float hit/caret/reflow APIs are not a
/// source Display bridge; callers must use the explicit double methods below.
///
/// Retain and acquired runs must preserve this capability, SourceText, SourceEmSize,
/// SourcePixelsPerDip and every SourceStyles entry. Reflow keeps the complete
/// original paragraph and only changes native placement from a shaped boundary;
/// it cannot re-fetch source text or shape an isolated suffix. Each returned
/// paragraph owns an independent reference with the existing hinted retirement
/// semantics. Unsupported collapse/objects/empty rows remain explicit failures.
/// </summary>
public interface IPortableDisplayTextParagraph : IPortableTextParagraph, IPortableHintedTextParagraph
{
    double SourceEmSize { get; }
    double SourcePixelsPerDip { get; }
    ReadOnlyMemory<PortableDisplayTextStyle> SourceStyles { get; }
    ReadOnlyMemory<PortableDisplayTextGlyphMetrics> DisplayGlyphMetrics { get; }
    ReadOnlyMemory<PortableDisplayTextLineMetrics> DisplayLineMetrics { get; }
    PortableDisplayTextIntrinsicWidths? DisplayIntrinsicWidths { get; }
    PortableTextHit HitTestDisplay(int lineIndex, double distance);
    double GetDisplayCaretDistance(int lineIndex, PortableTextHit hit);
    IPortableDisplayTextParagraph ReflowDisplay(int inputStart, double maximumWidth);
}

/// <summary>
/// Double source metrics and publication for an original owned occurrence selection.
/// No float-promoted advance, nominal-font recomputation or source division by DPI
/// may replace these values. Both output spans are validated before either changes;
/// unused tails stay untouched. Bind validates the exact original face, em/DPI,
/// occurrence order, advances, offsets and original writer frame before publication.
/// </summary>
public interface IPortableDisplayGlyphRunBindingFactory
{
    void CopyDisplaySourceMetrics(double sourceEmSize, double sourcePixelsPerDip,
        Span<double> sourceAdvances, Span<PortablePoint> sourceOffsets);
    IPortableDisplayGlyphRunBinding BindDisplayGlyphRun(PortableTextFont sourceFont,
        double sourceEmSize, double sourcePixelsPerDip, PortablePoint sourceBaselineOrigin,
        ReadOnlySpan<double> sourceAdvances, ReadOnlySpan<PortablePoint> sourceOffsets);
}

public readonly record struct PortableDisplayGlyphSourceFrame(int LineIndex, double ParagraphBaselineY,
    PortablePoint BaselineOrigin);

/// <summary>
/// The existing render lease plus exact original Display source identity. The
/// renderer's float projection is not source geometry. Retain must preserve this
/// interface and its double identity; AcquireGlyphRun retains the same generation.
/// </summary>
public interface IPortableDisplayGlyphRunBinding : IPortableHintedGlyphRunBinding
{
    double SourceEmSize { get; }
    double SourcePixelsPerDip { get; }
    PortableDisplayGlyphSourceFrame DisplaySourceFrame { get; }
}
