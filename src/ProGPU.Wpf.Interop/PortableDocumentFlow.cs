using System.Runtime.InteropServices;

namespace ProGPU.Wpf.Interop;

/// <summary>
/// Source-resolved block tree, in preorder. NoParent denotes roots; SubtreeEnd is
/// exclusive. Containers have no lines; line ranges partition the line span.
/// Insets combine resolved border and padding. All values are nonnegative DIPs.
/// The sequential value layout is shared with ProGPU's native document contract;
/// changes require matched ABI fixtures, not property-based adaptation.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PortableDocumentBlock
{
    public const uint NoParent = uint.MaxValue;
    public uint ParentIndex;
    public uint SubtreeEnd;
    public uint LineStart;
    public uint LineCount;
    public double MarginLeft;
    public double MarginTop;
    public double MarginRight;
    public double MarginBottom;
    public double InsetLeft;
    public double InsetTop;
    public double InsetRight;
    public double InsetBottom;
}

/// <summary>Already formatted line's actual width and positive source advance.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PortableDocumentLine
{
    public double Width;
    public double Height;
}

/// <summary>
/// Real source-measured non-text leaf, ordered by unique BlockIndex. Measure at
/// the resolved content width before arrangement. Nonnegative size, including
/// zero, retains object identity rather than becoming an empty flow container.
/// Reserved must be zero. Does not transfer source UI or text-position ownership.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PortableDocumentObject
{
    public uint BlockIndex;
    public uint Reserved;
    public double Width;
    public double Height;
}

/// <summary>
/// Horizontal row in the source block tree with a shared slice of fixed column
/// widths. Outer half-spacing, full inter-cell spacing. Sorted unique BlockIndex;
/// direct children are declared cells. No automatic columns or row spans implied.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PortableDocumentRow
{
    public uint BlockIndex;
    public uint ColumnStart;
    public uint ColumnCount;
    public uint Reserved;
    public double CellSpacing;
}

/// <summary>
/// Cell block directly owned by the indexed row, using a positive relative
/// column span. Sorted block indices and ordered, nonoverlapping spans per row.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PortableDocumentCell
{
    public uint BlockIndex;
    public uint RowIndex;
    public uint ColumnStart;
    public uint ColumnCount;
}

/// <summary>Content box excluding margins, border and padding. Zero width is not unbounded.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PortableDocumentBox
{
    public double X;
    public double Y;
    public double Width;
    public double Height;
}

[StructLayout(LayoutKind.Sequential)]
public struct PortableDocumentLinePosition
{
    public double X;
    public double Y;
}

public readonly record struct PortableDocumentExtent(double Width, double Height);

/// <summary>
/// Source-resolved fragmentation policy: 0/1 flags and nonnegative DIPs, with a
/// positive height. LeadingSpace replaces SpaceBefore at each fragment start.
/// Forced page/column flags are mutually exclusive and override AllowBreakBefore.
/// Keep and widow/orphan rules must be reflected in the admitted break boundaries.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PortableDocumentFragmentLine
{
    public uint AllowBreakBefore;
    public uint ForceColumnBefore;
    public uint ForcePageBefore;
    public uint Reserved;
    public double Height;
    public double SpaceBefore;
    public double LeadingSpace;
}

/// <summary>Zero-based page/column and Y relative to the column content origin.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PortableDocumentFragmentPosition
{
    public uint Page;
    public uint Column;
    public double Y;
}

public readonly record struct PortableDocumentPagination(uint FragmentCount, uint PageCount);

/// <summary>Actual paragraph extent with explicit local line positions.
/// Sorted unique line-bearing leaf BlockIndex; Reserved is zero. Extents retain
/// clearance gaps and same-row fragments, not a sum of fragment heights.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PortableDocumentPositionedParagraph
{
    public uint BlockIndex;
    public uint Reserved;
    public double Width;
    public double Height;
}

/// <summary>Optional shared document arrangement of positioned paragraph lines.
/// Source owns the native fragment metadata and original TextLines. This must
/// not fall back to ordinary line-height prefix placement when absent.</summary>
public interface IPortablePositionedDocumentFlow : IPortableDocumentFlow
{
    /// <summary>All spans are borrowed synchronously and outputs are disjoint.
    /// Local positions cover every line if paragraphs is nonempty, otherwise
    /// the span is empty. Ordinary lines have zero local positions. Invalid
    /// input leaves all outputs unchanged; source order is preserved.</summary>
    PortableDocumentExtent ArrangeWithPositionedParagraphs(ReadOnlySpan<PortableDocumentBlock> blocks, double width,
        ReadOnlySpan<PortableDocumentLine> lines, ReadOnlySpan<PortableDocumentObject> objects,
        ReadOnlySpan<PortableDocumentRow> rows, ReadOnlySpan<double> columnWidths,
        ReadOnlySpan<PortableDocumentCell> cells, ReadOnlySpan<PortableDocumentPositionedParagraph> paragraphs,
        ReadOnlySpan<PortableDocumentLinePosition> localPositions, Span<PortableDocumentBox> boxes,
        Span<PortableDocumentLinePosition> positions);
}

public enum PortableDocumentAnchorWidthMode : uint { Fixed, Fill, FitContent }
public enum PortableDocumentAnchorAlignment : uint { Left, Center, Right }

/// <summary>Source-resolved two-pass width request. Widths include horizontal
/// insets; measured width is actual child content from the initial constraint.
/// HasMeasurement is exactly 0/1. Finite nonnegative DIPs; no child is retained.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PortableDocumentAnchorWidthRequest
{
    public float AvailableWidth;
    public float HorizontalInsets;
    public float SpecifiedWidth;
    public float MeasuredWidth;
    public PortableDocumentAnchorWidthMode Mode;
    public uint HasMeasurement;
}

/// <summary>RequiresRemeasure is 0/1; honor it by formatting the actual child
/// at ContentWidth, never by scaling existing lines. Reserved is zero.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PortableDocumentAnchorWidthResult
{
    public float ContentWidth;
    public float OuterWidth;
    public uint RequiresRemeasure;
    public uint Reserved;
}

/// <summary>Resolved finite reference edges and measured positive outer size.
/// AllowDelay is 0/1; MaximumAttempts is 1..1,048,576; Reserved must be zero.
/// The source owns reference/offset policy, child identity and source positions.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PortableDocumentAnchorRequest
{
    public float Left;
    public float Top;
    public float Right;
    public float Bottom;
    public float Width;
    public float Height;
    public PortableDocumentAnchorAlignment Alignment;
    public uint AllowDelay;
    public uint MaximumAttempts;
    public uint Reserved;
}

/// <summary>Half-open collision rectangle, not source wrap-side or hit policy.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PortableDocumentAnchorRectangle
{
    public float Left;
    public float Top;
    public float Right;
    public float Bottom;
}

/// <summary>Optional native anchored-child primitives. The source retains and
/// measures real child subtrees, resolves wrapping, and preserves document
/// positions. This capability does not itself admit Figure/Floater content.
/// Batched synchronous disjoint spans; no input is retained and failures leave
/// every output unchanged. Missing capability must not use ordinary block flow.</summary>
public interface IPortableAnchoredDocumentFlow : IPortableDocumentFlow
{
    void ResolveAnchorWidths(ReadOnlySpan<PortableDocumentAnchorWidthRequest> requests,
        Span<PortableDocumentAnchorWidthResult> results);

    /// <summary>Source order is authoritative. Prior boxes join collision
    /// exclusions, with combined item count at most 1,048,576. A failed fit
    /// throws rather than clipping or overlapping the child.</summary>
    void PlaceAnchors(ReadOnlySpan<PortableDocumentAnchorRequest> requests,
        ReadOnlySpan<PortableDocumentAnchorRectangle> exclusions,
        Span<PortableDocumentAnchorRectangle> results);
}

/// <summary>
/// Device-independent block placement, separate from text shaping and drawing.
/// The source retains its document, source positions and formatted line objects.
/// Negative margins, floats and RTL block ordering are not part of block placement.
/// Optional pagination consumes source-admitted boundaries, not a full document
/// pagination policy. Missing or unsupported capability must throw.
/// Synchronous borrowed spans, at most 1,048,576 items and 128 nested nodes;
/// no input or output retained. Disjoint output spans remain untouched on failure.
/// </summary>
public interface IPortableDocumentFlow
{
    /// <summary>Optional fixed column/cell constraints before source formatting.</summary>
    void ResolveWidthsWithRows(ReadOnlySpan<PortableDocumentBlock> blocks, double width,
        ReadOnlySpan<PortableDocumentRow> rows, ReadOnlySpan<double> columnWidths,
        ReadOnlySpan<PortableDocumentCell> cells, Span<PortableDocumentBox> boxes)
    {
        if (!rows.IsEmpty || !columnWidths.IsEmpty || !cells.IsEmpty)
            throw new PlatformNotSupportedException("Native document row constraints are unavailable.");
        ResolveWidths(blocks, width, boxes);
    }

    /// <summary>
    /// Optional shared row/cell placement. Returned line order remains source
    /// order, not Y order; consumers must implement table-aware interaction.
    /// </summary>
    PortableDocumentExtent ArrangeWithRows(ReadOnlySpan<PortableDocumentBlock> blocks, double width,
        ReadOnlySpan<PortableDocumentLine> lines, ReadOnlySpan<PortableDocumentObject> objects,
        ReadOnlySpan<PortableDocumentRow> rows, ReadOnlySpan<double> columnWidths,
        ReadOnlySpan<PortableDocumentCell> cells, Span<PortableDocumentBox> boxes,
        Span<PortableDocumentLinePosition> positions)
        => rows.IsEmpty && columnWidths.IsEmpty && cells.IsEmpty
            ? ArrangeWithObjects(blocks, width, lines, objects, boxes, positions)
            : throw new PlatformNotSupportedException("Native document row placement is unavailable.");

    /// <summary>
    /// Optional measured-block placement. Objects target line-free leaves only;
    /// the caller still owns actual child visuals, editing and invalidation.
    /// Missing capability rejects nonempty input, never omits object height.
    /// </summary>
    PortableDocumentExtent ArrangeWithObjects(ReadOnlySpan<PortableDocumentBlock> blocks, double width,
        ReadOnlySpan<PortableDocumentLine> lines, ReadOnlySpan<PortableDocumentObject> objects,
        Span<PortableDocumentBox> boxes, Span<PortableDocumentLinePosition> positions)
        => objects.IsEmpty ? Arrange(blocks, width, lines, boxes, positions)
            : throw new PlatformNotSupportedException("The document provider does not support measured block objects.");

    void ResolveWidths(ReadOnlySpan<PortableDocumentBlock> blocks, double width, Span<PortableDocumentBox> boxes);
    PortableDocumentExtent Arrange(ReadOnlySpan<PortableDocumentBlock> blocks, double width,
        ReadOnlySpan<PortableDocumentLine> lines, Span<PortableDocumentBox> boxes,
        Span<PortableDocumentLinePosition> positions);

    /// <summary>
    /// Optional sequential fragmentation over already formatted lines. Widths,
    /// source policies and page visuals are separate; a utility result does not
    /// imply that a paginated viewer is implemented. Impossible legal fits throw.
    /// </summary>
    PortableDocumentPagination Paginate(ReadOnlySpan<PortableDocumentFragmentLine> lines,
        double contentHeight, uint columns, Span<PortableDocumentFragmentPosition> positions)
        => throw new PlatformNotSupportedException("The document provider does not support pagination.");
}

public static partial class PortableWpfServiceRegistry
{
    private static readonly PortableDefaultServiceSlot<IPortableDocumentFlow> s_documentFlow = new();

    public static bool TryGetDocumentFlow(out IPortableDocumentFlow service)
    {
        service = s_documentFlow.Current!;
        return service != null;
    }

    /// <summary>Explicit overrides retain priority; disposal reveals the process default.</summary>
    public static IDisposable RegisterDocumentFlow(IPortableDocumentFlow service) => s_documentFlow.Register(service);

    /// <summary>Installs a lazy default without loading native code or replacing an override.</summary>
    public static void EnsureDocumentFlow(IPortableDocumentFlow service) => s_documentFlow.EnsureDefault(service);
}
