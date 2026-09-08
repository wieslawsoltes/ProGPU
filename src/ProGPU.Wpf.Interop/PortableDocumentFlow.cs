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
