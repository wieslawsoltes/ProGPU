namespace ProGPU.Wpf.Interop;

/// <summary>Immutable font snapshot, cached by the source face. No WPF type identity crosses this seam.</summary>
public sealed class PortableTextFont(ReadOnlyMemory<byte> data, uint faceIndex, ushort unitsPerEm)
{
    public ReadOnlyMemory<byte> Data { get; } = data;
    public uint FaceIndex { get; } = faceIndex;
    public ushort UnitsPerEm { get; } = unitsPerEm;
}

public enum PortableTextAlignment { Left, Center, Right, Justify }
public enum PortableTextWrapping { Emergency, WholeWord }
public enum PortableTextTrimming { Character, Word }
public readonly record struct PortableTextCollapseRequest(int LineIndex, float Width, float SymbolWidth, PortableTextTrimming Trimming);
public readonly record struct PortableTextCollapsedRange(int LineIndex, int Start, int End, int SymbolGlyphIndex);
public readonly record struct PortableTextFeature(uint Tag, uint Value);

/// <summary>
/// Horizontal paragraph. Optional styles partition Text at UTF-16 scalar boundaries;
/// an empty style list preserves the uniform font/feature domain.
/// </summary>
public readonly record struct PortableTextParagraphRequest(
    ReadOnlyMemory<char> Text, PortableTextFont Font, float FontSize,
    float LineHeight, float MaximumWidth, bool RightToLeft, PortableTextAlignment Alignment,
    ReadOnlyMemory<PortableTextFeature> Features = default,
    ReadOnlyMemory<PortableTextStyle> Styles = default,
    float IncrementalTab = 0, float TabOrigin = 0, bool MeasureIntrinsicWidths = false,
    PortableTextWrapping Wrapping = PortableTextWrapping.Emergency);

public readonly record struct PortableTextIntrinsicWidths(float Minimum, float Maximum);

public readonly record struct PortableTextStyle(int Start, int Length, PortableTextFont Font,
    float FontSize, ReadOnlyMemory<PortableTextFeature> Features = default, uint Language = 0);

public readonly record struct PortableTextGlyph(uint GlyphId, int Cluster, int ClusterEnd,
    float X, float Y, float Advance, sbyte BidiLevel, uint FontIndex = 0, bool IsTab = false, bool IsCollapseSymbol = false)
{
    /// <summary>Non-ink source object. Never resolve its font index or draw its glyph id.</summary>
    public bool IsInlineObject { get; init; }
}
public readonly record struct PortableTextLineInfo(int GlyphStart, int GlyphCount,
    int InputStart, int InputEnd, float Width, float Y, float Height);
public readonly record struct PortableTextHit(int Position, bool Trailing);

/// <summary>Immutable owned layout output; no borrowed native handles survive formatting.</summary>
public interface IPortableTextParagraph
{
    PortableTextCollapsedRange? CollapsedRange => null;
    IPortableTextParagraph Collapse(in PortableTextCollapseRequest request)
        => throw new PlatformNotSupportedException("The text provider does not expose source-preserving collapse.");
    /// <summary>Optional native intrinsic widths, not the current formatted line's width.</summary>
    PortableTextIntrinsicWidths? IntrinsicWidths => null;
    /// <summary>Opaque render-font annotation, as on PortableNativeGlyphRun; never inspected by source WPF.</summary>
    object? NativeFont => null;
    /// <summary>Exact context face annotation for each positioned glyph, not a family-name lookup.</summary>
    object? GetNativeFont(uint fontIndex) => fontIndex == 0 ? NativeFont :
        throw new ArgumentOutOfRangeException(nameof(fontIndex));
    ReadOnlyMemory<PortableTextGlyph> Glyphs { get; }
    ReadOnlyMemory<PortableTextLineInfo> Lines { get; }
    PortableTextHit HitTest(int lineIndex, float distance);
    float GetCaretDistance(int lineIndex, PortableTextHit hit);
    int GetNextLogicalCaret(int lineIndex, int position, bool previous);
    int GetSelection(int lineIndex, int start, int end, Span<PortableRect> rectangles);
}

public interface IPortableTextFormatting
{
    IPortableTextParagraph Format(in PortableTextParagraphRequest request);
}

/// <summary>Source physical-font extents in paragraph DIPs, one per explicit style.</summary>
public readonly record struct PortableTextStyleMetrics(float Ascent, float Descent);

/// <summary>Measured source object at an actual UTF-16 U+FFFC position, in paragraph DIPs.</summary>
public readonly record struct PortableTextInlineObject(int Position, float Width, float Ascent, float Descent);

/// <summary>Owned, source-ordered object placement in paragraph coordinates.</summary>
public readonly record struct PortableTextInlineObjectPlacement(int InputPosition, int GlyphIndex,
    int LineIndex, float X, float Y, float Width, float Height);

/// <summary>
/// Explicit optional capability: text-only providers must not silently discard objects.
/// Nonempty text requires explicit styles, matching metrics and ordered objects covering
/// every U+FFFC. Input spans are borrowed only until this call returns.
/// </summary>
public interface IPortableInlineTextFormatting : IPortableTextFormatting
{
    IPortableInlineTextParagraph FormatInline(in PortableTextParagraphRequest request,
        ReadOnlySpan<PortableTextStyleMetrics> styleMetrics,
        ReadOnlySpan<PortableTextInlineObject> inlineObjects);
}

/// <summary>
/// Measured lines use line-top Y coordinates and their real individual heights.
/// Glyph positions and object placements remain in paragraph coordinates.
/// Selection rectangles retain the ordinary line-local convention.
/// </summary>
public interface IPortableInlineTextParagraph : IPortableTextParagraph
{
    ReadOnlyMemory<PortableTextInlineObjectPlacement> InlineObjects { get; }
    /// <summary>Actual baseline relative to the selected line's top.</summary>
    float GetBaselineOffset(int lineIndex);
}

/// <summary>Resolved half-open exclusion in paragraph DIPs, not an anchor's paint bounds.</summary>
public readonly record struct PortableTextExclusion(float Left, float Top, float Right, float Bottom);

/// <summary>Explicit bounded native fitting policy; zero is invalid, not an implicit default.</summary>
public readonly record struct PortableTextExclusionOptions(uint MaximumAttempts);

/// <summary>One native line fragment. Several fragments can share a row and top.</summary>
public readonly record struct PortableTextFragment(int RowIndex, float Left, double Top, float Width);

/// <summary>Original UTF-16 position and affinity, owned by one retained paragraph generation.</summary>
public readonly record struct PortableTextCaretStop(int Position, bool Trailing, int FragmentIndex,
    float X, float Y, float Height, sbyte BidiLevel);

public enum PortableTextCaretMovement { Left, Right, Up, Down }

/// <summary>
/// Explicit optional capability for source-owned anchored content. The caller resolves
/// exclusion rectangles and measures inline objects; the provider performs native
/// exclusion-dependent fitting. Inputs are borrowed only during this call. Unsupported
/// empty-row, fitting or source policies must fail, never discard exclusions.
/// </summary>
public interface IPortableExcludedTextFormatting : IPortableInlineTextFormatting
{
    IPortableExcludedTextParagraph FormatExcluded(in PortableTextParagraphRequest request,
        ReadOnlySpan<PortableTextStyleMetrics> styleMetrics,
        ReadOnlySpan<PortableTextInlineObject> inlineObjects,
        in PortableTextExclusionOptions options, ReadOnlySpan<PortableTextExclusion> exclusions);
}

/// <summary>
/// Optional hard-segment origin support over the retained excluded paragraph service.
/// </summary>
public interface IPortableSegmentedTextFormatting : IPortableExcludedTextFormatting
{
    /// <summary>Formats against unchanged paragraph-local exclusions at a finite nonnegative Y origin.
    /// Fragment tops and ContentHeight retain absolute paragraph coordinates.</summary>
    IPortableExcludedTextParagraph FormatExcludedAt(in PortableTextParagraphRequest request,
        ReadOnlySpan<PortableTextStyleMetrics> styleMetrics,
        ReadOnlySpan<PortableTextInlineObject> inlineObjects,
        in PortableTextExclusionOptions options, ReadOnlySpan<PortableTextExclusion> exclusions, double originY);
}

/// <summary>
/// Retained excluded layout. Lines and Fragments have identical indexing; line indices
/// are not row indices. Native metrics include cleared gaps and must not be recreated
/// by summing fragment heights. Existing line-local hit/selection conventions remain.
/// No source document/child ownership is transferred by this contract.
/// </summary>
public interface IPortableExcludedTextParagraph : IPortableInlineTextParagraph
{
    ReadOnlyMemory<PortableTextFragment> Fragments { get; }
    ReadOnlyMemory<PortableTextCaretStop> Carets { get; }
    double ContentWidth { get; }
    double ContentHeight { get; }
    double MeasuredWidth { get; }
    /// <summary>
    /// Moves an index in this paragraph's Carets, returning an existing index (unchanged
    /// at an outer boundary). Preferred X is in paragraph DIPs; invalid input fails.
    /// The provider retains paragraph direction. Never reuse indices after reformatting.
    /// </summary>
    int MoveCaret(int caretIndex, PortableTextCaretMovement direction, float preferredX);
}

public static partial class PortableWpfServiceRegistry
{
    private static readonly PortableDefaultServiceSlot<IPortableTextFormatting> s_textFormatting = new();

    public static bool TryGetTextFormatting(out IPortableTextFormatting service)
    {
        service = s_textFormatting.Current!;
        return service != null;
    }

    /// <summary>Replaces the explicit override; disposal reveals the installed default, if any.</summary>
    public static IDisposable RegisterTextFormatting(IPortableTextFormatting service) => s_textFormatting.Register(service);

    /// <summary>Installs the first process default without replacing an explicit provider or doing native work.</summary>
    public static void EnsureTextFormatting(IPortableTextFormatting service) => s_textFormatting.EnsureDefault(service);
}
