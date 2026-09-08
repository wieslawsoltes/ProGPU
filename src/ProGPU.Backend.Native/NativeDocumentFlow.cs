using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

/// <summary>
/// Device-independent C++ placement of source-owned, already formatted paragraph
/// lines, including sequential page/column fragmentation at source-admitted breaks.
/// This does not shape text, create a renderer or retain pointers.
/// Containers and leaves form a preorder forest; metrics use nonnegative DIPs.
/// </summary>
public static unsafe class NativeDocumentFlow
{
    public const uint NoParent = uint.MaxValue;
    public const int MaximumItems = 1 << 20;
    public const int MaximumDepth = 128;

    /// <summary>
    /// Fits lines into uniform content-height columns at source-admitted breaks.
    /// Source owns column widths, keep/widow/orphan policy, box decorations and
    /// page visuals. Non-fitting indivisible ranges fail; no clipping or implicit
    /// constraint relaxation. Empty input yields zero pages. Outputs stay untouched
    /// on failure and must not overlap inputs. One synchronous native crossing.
    /// </summary>
    public static NativeDocumentPaginationResult Paginate(ReadOnlySpan<NativeDocumentFragmentLine> lines,
        double contentHeight, uint columns, Span<NativeDocumentFragmentPosition> positions,
        NativeMilBackend backend = NativeMilBackend.WgpuNative)
    {
        Validate(lines.Length, contentHeight, positions.Length, backend);
        if (contentHeight == 0) throw new ArgumentOutOfRangeException(nameof(contentHeight));
        if (columns is 0 or > 1024) throw new ArgumentOutOfRangeException(nameof(columns));
        NativeDocumentPaginationResult result = new() { StructSize = (uint)sizeof(NativeDocumentPaginationResult) };
        NativeRendererStatus status;
        fixed (NativeDocumentFragmentLine* input = lines)
        fixed (NativeDocumentFragmentPosition* output = positions)
            status = backend == NativeMilBackend.Dawn
                ? NativeDawnDocumentFlowMethods.Paginate(input, (uint)lines.Length, contentHeight, columns,
                    output, (uint)positions.Length, &result)
                : NativeDocumentFlowMethods.Paginate(input, (uint)lines.Length, contentHeight, columns,
                    output, (uint)positions.Length, &result);
        if (status != NativeRendererStatus.Success)
            throw new NativeRendererException(status, "Native document pagination failed.");
        return result;
    }

    /// <summary>
    /// Resolves one content-width constraint per block before paragraph formatting.
    /// A zero width is exhausted space, not permission to format without a limit.
    /// Output spans must not alias input. Outputs remain untouched on failure.
    /// </summary>
    public static void ResolveWidths(ReadOnlySpan<NativeDocumentBlock> blocks, double width,
        Span<NativeDocumentBox> boxes, NativeMilBackend backend = NativeMilBackend.WgpuNative)
    {
        Validate(blocks.Length, width, boxes.Length, backend);
        NativeRendererStatus status;
        fixed (NativeDocumentBlock* input = blocks)
        fixed (NativeDocumentBox* output = boxes)
            status = backend == NativeMilBackend.Dawn
                ? NativeDawnDocumentFlowMethods.ResolveWidths(input, (uint)blocks.Length, width, output, (uint)boxes.Length)
                : NativeDocumentFlowMethods.ResolveWidths(input, (uint)blocks.Length, width, output, (uint)boxes.Length);
        if (status != NativeRendererStatus.Success)
            throw new NativeRendererException(status, "Native document width resolution failed.");
    }

    /// <summary>
    /// Places real line metrics after formatting at the resolved widths. Positive
    /// adjoining margins collapse; padding/border insets stop edge collapse.
    /// Caller retains source text positions, line objects and drawing ownership.
    /// Outputs must be disjoint and remain untouched on failure.
    /// </summary>
    public static NativeDocumentFlowResult Arrange(ReadOnlySpan<NativeDocumentBlock> blocks, double width,
        ReadOnlySpan<NativeDocumentLine> lines, Span<NativeDocumentBox> boxes,
        Span<NativeDocumentLinePosition> positions, NativeMilBackend backend = NativeMilBackend.WgpuNative)
    {
        Validate(blocks.Length, width, boxes.Length, backend);
        if (lines.Length > MaximumItems) throw new ArgumentOutOfRangeException(nameof(lines));
        if (positions.Length < lines.Length) throw new ArgumentException("One position per line is required.", nameof(positions));
        NativeDocumentFlowResult result = new() { StructSize = (uint)sizeof(NativeDocumentFlowResult) };
        NativeRendererStatus status;
        fixed (NativeDocumentBlock* input = blocks)
        fixed (NativeDocumentLine* metrics = lines)
        fixed (NativeDocumentBox* output = boxes)
        fixed (NativeDocumentLinePosition* placed = positions)
            status = backend == NativeMilBackend.Dawn
                ? NativeDawnDocumentFlowMethods.Arrange(input, (uint)blocks.Length, width, metrics, (uint)lines.Length,
                    output, (uint)boxes.Length, placed, (uint)positions.Length, &result)
                : NativeDocumentFlowMethods.Arrange(input, (uint)blocks.Length, width, metrics, (uint)lines.Length,
                    output, (uint)boxes.Length, placed, (uint)positions.Length, &result);
        if (status != NativeRendererStatus.Success)
            throw new NativeRendererException(status, "Native document line placement failed.");
        return result;
    }

    private static void Validate(int count, double width, int capacity, NativeMilBackend backend)
    {
        if (count > MaximumItems) throw new ArgumentOutOfRangeException(nameof(count));
        if (!double.IsFinite(width) || width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (capacity < count) throw new ArgumentException("One content box per block is required.", nameof(capacity));
        if (backend is not NativeMilBackend.WgpuNative and not NativeMilBackend.Dawn)
            throw new ArgumentOutOfRangeException(nameof(backend));
    }
}

internal static unsafe partial class NativeDocumentFlowMethods
{
    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_document_paginate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus Paginate(NativeDocumentFragmentLine* lines, uint count,
        double height, uint columns, NativeDocumentFragmentPosition* positions, uint capacity,
        NativeDocumentPaginationResult* result);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_document_resolve_widths")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ResolveWidths(NativeDocumentBlock* blocks, uint count,
        double width, NativeDocumentBox* boxes, uint capacity);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_document_arrange")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus Arrange(NativeDocumentBlock* blocks, uint count, double width,
        NativeDocumentLine* lines, uint lineCount, NativeDocumentBox* boxes, uint capacity,
        NativeDocumentLinePosition* positions, uint positionCapacity, NativeDocumentFlowResult* result);
}

internal static unsafe partial class NativeDawnDocumentFlowMethods
{
    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_document_paginate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus Paginate(NativeDocumentFragmentLine* lines, uint count,
        double height, uint columns, NativeDocumentFragmentPosition* positions, uint capacity,
        NativeDocumentPaginationResult* result);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_document_resolve_widths")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ResolveWidths(NativeDocumentBlock* blocks, uint count,
        double width, NativeDocumentBox* boxes, uint capacity);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_document_arrange")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus Arrange(NativeDocumentBlock* blocks, uint count, double width,
        NativeDocumentLine* lines, uint lineCount, NativeDocumentBox* boxes, uint capacity,
        NativeDocumentLinePosition* positions, uint positionCapacity, NativeDocumentFlowResult* result);
}
