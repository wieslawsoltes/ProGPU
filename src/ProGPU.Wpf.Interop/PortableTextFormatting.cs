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
