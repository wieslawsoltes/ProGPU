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
/// Device-independent block placement, separate from text shaping and drawing.
/// The source retains its document, source positions and formatted line objects.
/// Negative margins, floats, columns, pagination and RTL block ordering are not
/// part of this contract. Missing or unsupported capability must throw.
/// Synchronous borrowed spans, at most 1,048,576 items and 128 nested nodes;
/// no input or output retained. Disjoint output spans remain untouched on failure.
/// </summary>
public interface IPortableDocumentFlow
{
    void ResolveWidths(ReadOnlySpan<PortableDocumentBlock> blocks, double width, Span<PortableDocumentBox> boxes);
    PortableDocumentExtent Arrange(ReadOnlySpan<PortableDocumentBlock> blocks, double width,
        ReadOnlySpan<PortableDocumentLine> lines, Span<PortableDocumentBox> boxes,
        Span<PortableDocumentLinePosition> positions);
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
