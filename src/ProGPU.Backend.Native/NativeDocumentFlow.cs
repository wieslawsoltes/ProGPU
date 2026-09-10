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

    /// <summary>Places measured anchors in source order, retaining prior boxes
    /// as collision exclusions. No child measurement or wrap policy is inferred.
    /// A rejected batch leaves all results untouched.</summary>
    public static void PlaceAnchors(ReadOnlySpan<NativeDocumentAnchorRequest> requests,
        ReadOnlySpan<NativeDocumentAnchorRectangle> exclusions, Span<NativeDocumentAnchorRectangle> results,
        NativeMilBackend backend = NativeMilBackend.WgpuNative)
    {
        Validate(requests.Length, 0, results.Length, backend);
        if (exclusions.Length > MaximumItems - requests.Length)
            throw new ArgumentOutOfRangeException(nameof(exclusions));
        NativeRendererStatus status;
        fixed (NativeDocumentAnchorRequest* input = requests)
        fixed (NativeDocumentAnchorRectangle* obstacles = exclusions)
        fixed (NativeDocumentAnchorRectangle* output = results)
            status = backend == NativeMilBackend.Dawn
                ? NativeDawnDocumentFlowMethods.PlaceAnchors(input, (uint)requests.Length, obstacles,
                    (uint)exclusions.Length, output, (uint)results.Length)
                : NativeDocumentFlowMethods.PlaceAnchors(input, (uint)requests.Length, obstacles,
                    (uint)exclusions.Length, output, (uint)results.Length);
        if (status != NativeRendererStatus.Success)
            throw new NativeRendererException(status, "Native anchor placement failed.");
    }

    /// <summary>Resolves a batch of anchor width constraints through the shared
    /// native policy. Requests use mode 0=fixed, 1=fill, 2=fit-content and a 0/1
    /// measurement flag. Source retains and remeasures the actual child subtree.
    /// Inputs and outputs must not overlap; any failure preserves all outputs.</summary>
    public static void ResolveAnchorWidths(ReadOnlySpan<NativeDocumentAnchorWidthRequest> requests,
        Span<NativeDocumentAnchorWidthResult> results, NativeMilBackend backend = NativeMilBackend.WgpuNative)
    {
        Validate(requests.Length, 0, results.Length, backend);
        NativeRendererStatus status;
        fixed (NativeDocumentAnchorWidthRequest* input = requests)
        fixed (NativeDocumentAnchorWidthResult* output = results)
            status = backend == NativeMilBackend.Dawn
                ? NativeDawnDocumentFlowMethods.ResolveAnchorWidths(input, (uint)requests.Length, output, (uint)results.Length)
                : NativeDocumentFlowMethods.ResolveAnchorWidths(input, (uint)requests.Length, output, (uint)results.Length);
        if (status != NativeRendererStatus.Success)
            throw new NativeRendererException(status, "Native anchor width resolution failed.");
    }

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

    /// <summary>
    /// Arranges source-measured block objects together with real paragraph lines.
    /// Objects target distinct line-free leaves in increasing block-index order;
    /// zero size is an actual object, not a margin-collapsing empty container.
    /// Source owns measurement, visual lifetime and source-position interaction.
    /// All metrics cross once in borrowed spans. Outputs stay untouched on failure.
    /// </summary>
    public static NativeDocumentFlowResult ArrangeWithObjects(ReadOnlySpan<NativeDocumentBlock> blocks, double width,
        ReadOnlySpan<NativeDocumentLine> lines, ReadOnlySpan<NativeDocumentObject> objects,
        Span<NativeDocumentBox> boxes, Span<NativeDocumentLinePosition> positions,
        NativeMilBackend backend = NativeMilBackend.WgpuNative)
    {
        Validate(blocks.Length, width, boxes.Length, backend);
        if (lines.Length > MaximumItems) throw new ArgumentOutOfRangeException(nameof(lines));
        if (objects.Length > MaximumItems) throw new ArgumentOutOfRangeException(nameof(objects));
        if (positions.Length < lines.Length) throw new ArgumentException("One position per line is required.", nameof(positions));
        NativeDocumentFlowResult result = new() { StructSize = (uint)sizeof(NativeDocumentFlowResult) };
        NativeRendererStatus status;
        fixed (NativeDocumentBlock* input = blocks)
        fixed (NativeDocumentLine* metrics = lines)
        fixed (NativeDocumentObject* measured = objects)
        fixed (NativeDocumentBox* output = boxes)
        fixed (NativeDocumentLinePosition* placed = positions)
            status = backend == NativeMilBackend.Dawn
                ? NativeDawnDocumentFlowMethods.ArrangeWithObjects(input, (uint)blocks.Length, width, metrics, (uint)lines.Length,
                    measured, (uint)objects.Length, output, (uint)boxes.Length, placed, (uint)positions.Length, &result)
                : NativeDocumentFlowMethods.ArrangeWithObjects(input, (uint)blocks.Length, width, metrics, (uint)lines.Length,
                    measured, (uint)objects.Length, output, (uint)boxes.Length, placed, (uint)positions.Length, &result);
        if (status != NativeRendererStatus.Success)
            throw new NativeRendererException(status, "Native document object placement failed.");
        return result;
    }

    /// <summary>
    /// Resolves fixed shared column tracks and cell spans inside the block forest
    /// before source text formatting. Does not guess intrinsic/automatic widths.
    /// All direct row children must be declared cells; slices are identical or
    /// disjoint. One synchronous crossing, no pointers retained.
    /// </summary>
    public static void ResolveWidthsWithRows(ReadOnlySpan<NativeDocumentBlock> blocks, double width,
        ReadOnlySpan<NativeDocumentRow> rows, ReadOnlySpan<double> columnWidths,
        ReadOnlySpan<NativeDocumentCell> cells, Span<NativeDocumentBox> boxes,
        NativeMilBackend backend = NativeMilBackend.WgpuNative)
    {
        Validate(blocks.Length, width, boxes.Length, backend);
        ValidateRows(rows.Length, columnWidths.Length, cells.Length);
        NativeRendererStatus status;
        fixed (NativeDocumentBlock* input = blocks)
        fixed (NativeDocumentRow* rowInput = rows)
        fixed (double* columns = columnWidths)
        fixed (NativeDocumentCell* cellInput = cells)
        fixed (NativeDocumentBox* output = boxes)
            status = backend == NativeMilBackend.Dawn
                ? NativeDawnDocumentFlowMethods.ResolveWidthsWithRows(input, (uint)blocks.Length, width,
                    rowInput, (uint)rows.Length, columns, (uint)columnWidths.Length, cellInput, (uint)cells.Length,
                    output, (uint)boxes.Length)
                : NativeDocumentFlowMethods.ResolveWidthsWithRows(input, (uint)blocks.Length, width,
                    rowInput, (uint)rows.Length, columns, (uint)columnWidths.Length, cellInput, (uint)cells.Length,
                    output, (uint)boxes.Length);
        if (status != NativeRendererStatus.Success)
            throw new NativeRendererException(status, "Native document column resolution failed.");
    }

    /// <summary>
    /// Places horizontal rows alongside ordinary blocks and measured objects.
    /// Rows use the tallest cell; cells stretch their boxes without stretching
    /// text. Source line order is unchanged, so output Y need not be monotonic.
    /// No row spans, automatic columns, pagination or source hit policy inferred.
    /// </summary>
    public static NativeDocumentFlowResult ArrangeWithRows(ReadOnlySpan<NativeDocumentBlock> blocks, double width,
        ReadOnlySpan<NativeDocumentLine> lines, ReadOnlySpan<NativeDocumentObject> objects,
        ReadOnlySpan<NativeDocumentRow> rows, ReadOnlySpan<double> columnWidths,
        ReadOnlySpan<NativeDocumentCell> cells, Span<NativeDocumentBox> boxes,
        Span<NativeDocumentLinePosition> positions, NativeMilBackend backend = NativeMilBackend.WgpuNative)
    {
        Validate(blocks.Length, width, boxes.Length, backend);
        ValidateRows(rows.Length, columnWidths.Length, cells.Length);
        if (lines.Length > MaximumItems) throw new ArgumentOutOfRangeException(nameof(lines));
        if (objects.Length > MaximumItems) throw new ArgumentOutOfRangeException(nameof(objects));
        if (positions.Length < lines.Length) throw new ArgumentException("One position per line is required.", nameof(positions));
        NativeDocumentFlowResult result = new() { StructSize = (uint)sizeof(NativeDocumentFlowResult) };
        NativeRendererStatus status;
        fixed (NativeDocumentBlock* input = blocks)
        fixed (NativeDocumentLine* metrics = lines)
        fixed (NativeDocumentObject* measured = objects)
        fixed (NativeDocumentRow* rowInput = rows)
        fixed (double* columns = columnWidths)
        fixed (NativeDocumentCell* cellInput = cells)
        fixed (NativeDocumentBox* output = boxes)
        fixed (NativeDocumentLinePosition* placed = positions)
            status = backend == NativeMilBackend.Dawn
                ? NativeDawnDocumentFlowMethods.ArrangeWithRows(input, (uint)blocks.Length, width,
                    metrics, (uint)lines.Length, measured, (uint)objects.Length,
                    rowInput, (uint)rows.Length, columns, (uint)columnWidths.Length, cellInput, (uint)cells.Length,
                    output, (uint)boxes.Length, placed, (uint)positions.Length, &result)
                : NativeDocumentFlowMethods.ArrangeWithRows(input, (uint)blocks.Length, width,
                    metrics, (uint)lines.Length, measured, (uint)objects.Length,
                    rowInput, (uint)rows.Length, columns, (uint)columnWidths.Length, cellInput, (uint)cells.Length,
                    output, (uint)boxes.Length, placed, (uint)positions.Length, &result);
        if (status != NativeRendererStatus.Success)
            throw new NativeRendererException(status, "Native document row placement failed.");
        return result;
    }

    private static void ValidateRows(int rows, int columns, int cells)
    {
        if (rows > MaximumItems) throw new ArgumentOutOfRangeException(nameof(rows));
        if (columns > MaximumItems) throw new ArgumentOutOfRangeException(nameof(columns));
        if (cells > MaximumItems) throw new ArgumentOutOfRangeException(nameof(cells));
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
    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_document_place_anchors")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus PlaceAnchors(NativeDocumentAnchorRequest* requests, uint count,
        NativeDocumentAnchorRectangle* exclusions, uint exclusionCount, NativeDocumentAnchorRectangle* results, uint capacity);
    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_document_resolve_anchor_widths")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ResolveAnchorWidths(NativeDocumentAnchorWidthRequest* requests,
        uint count, NativeDocumentAnchorWidthResult* results, uint capacity);
    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_document_resolve_widths_with_rows")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ResolveWidthsWithRows(NativeDocumentBlock* blocks, uint count, double width,
        NativeDocumentRow* rows, uint rowCount, double* columns, uint columnCount, NativeDocumentCell* cells, uint cellCount,
        NativeDocumentBox* boxes, uint capacity);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_document_arrange_with_rows")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ArrangeWithRows(NativeDocumentBlock* blocks, uint count, double width,
        NativeDocumentLine* lines, uint lineCount, NativeDocumentObject* objects, uint objectCount,
        NativeDocumentRow* rows, uint rowCount, double* columns, uint columnCount, NativeDocumentCell* cells, uint cellCount,
        NativeDocumentBox* boxes, uint capacity, NativeDocumentLinePosition* positions, uint positionCapacity,
        NativeDocumentFlowResult* result);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_document_arrange_with_objects")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ArrangeWithObjects(NativeDocumentBlock* blocks, uint count, double width,
        NativeDocumentLine* lines, uint lineCount, NativeDocumentObject* objects, uint objectCount,
        NativeDocumentBox* boxes, uint capacity, NativeDocumentLinePosition* positions, uint positionCapacity,
        NativeDocumentFlowResult* result);

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
    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_document_place_anchors")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus PlaceAnchors(NativeDocumentAnchorRequest* requests, uint count,
        NativeDocumentAnchorRectangle* exclusions, uint exclusionCount, NativeDocumentAnchorRectangle* results, uint capacity);
    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_document_resolve_anchor_widths")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ResolveAnchorWidths(NativeDocumentAnchorWidthRequest* requests,
        uint count, NativeDocumentAnchorWidthResult* results, uint capacity);
    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_document_resolve_widths_with_rows")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ResolveWidthsWithRows(NativeDocumentBlock* blocks, uint count, double width,
        NativeDocumentRow* rows, uint rowCount, double* columns, uint columnCount, NativeDocumentCell* cells, uint cellCount,
        NativeDocumentBox* boxes, uint capacity);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_document_arrange_with_rows")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ArrangeWithRows(NativeDocumentBlock* blocks, uint count, double width,
        NativeDocumentLine* lines, uint lineCount, NativeDocumentObject* objects, uint objectCount,
        NativeDocumentRow* rows, uint rowCount, double* columns, uint columnCount, NativeDocumentCell* cells, uint cellCount,
        NativeDocumentBox* boxes, uint capacity, NativeDocumentLinePosition* positions, uint positionCapacity,
        NativeDocumentFlowResult* result);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_document_arrange_with_objects")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ArrangeWithObjects(NativeDocumentBlock* blocks, uint count, double width,
        NativeDocumentLine* lines, uint lineCount, NativeDocumentObject* objects, uint objectCount,
        NativeDocumentBox* boxes, uint capacity, NativeDocumentLinePosition* positions, uint positionCapacity,
        NativeDocumentFlowResult* result);

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
