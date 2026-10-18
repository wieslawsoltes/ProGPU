using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

public enum NativeFontHintInterpreter : uint
{
    TrueType35 = 35,
    TrueType40 = 40,
}

/// <summary>Native outline execution targets, independent of source rendering modes and GPU coverage.</summary>
public enum NativeFontOutlinePolicy : uint
{
    Normal = (uint)NativeMethods.FontOutlineNormal,
    Monochrome = (uint)NativeMethods.FontOutlineMonochrome,
    HorizontalLcd = (uint)NativeMethods.FontOutlineHorizontalLcd,
    Unhinted = (uint)NativeMethods.FontOutlineUnhinted,
}

/// <summary>
/// One independently owned original-order hinted generation. Copies borrow
/// its exclusive handle lease; context disposal and cache eviction cannot
/// invalidate it. This explicit batch does not enable source Display layout.
/// </summary>
public sealed unsafe class NativeHintedFontBatch : IDisposable
{
    private readonly NativeTextContextOwner _owner = null!;

    internal NativeHintedFontBatch(nint handle)
    {
        NativeHintedBatchCounts counts = default;
        NativeRendererStatus status = NativeMethods.GetHintedBatchCounts(handle, &counts);
        if (status != NativeRendererStatus.Success)
            throw new InvalidOperationException($"Native hinted batch counts failed with {status}.");
        Counts = counts;
        // This is the last throwing construction operation. The capturing
        // caller releases the raw handle if construction fails before ownership.
        _owner = new NativeTextContextOwner(handle, NativeMethods.DestroyHintedBatch, nameof(NativeHintedFontBatch));
    }

    public NativeHintedBatchCounts Counts { get; }

    /// <summary>
    /// Atomically copies complete fixed-point records and original outline
    /// metadata, preserving every unused destination slot. No font execution,
    /// allocation or GPU operation occurs inside the native transfer.
    /// </summary>
    public void CopyTo(Span<NativeHintedGlyph> glyphs, Span<NativeHintedPoint> points,
        Span<byte> tags, Span<int> contourEnds)
    {
        using var use = _owner.Acquire();
        if ((uint)glyphs.Length < Counts.Glyphs || (uint)points.Length < Counts.Points ||
            (uint)tags.Length < Counts.Points || (uint)contourEnds.Length < Counts.Contours)
            throw new ArgumentException("Buffers must cover the complete retained hinted generation.");
        fixed (NativeHintedGlyph* glyphPointer = glyphs)
        fixed (NativeHintedPoint* pointPointer = points)
        fixed (byte* tagPointer = tags)
        fixed (int* contourPointer = contourEnds)
        {
            NativeRendererStatus status = NativeMethods.CopyHintedBatch(use.Handle,
                glyphPointer, checked((uint)glyphs.Length), pointPointer, checked((uint)points.Length),
                tagPointer, checked((uint)tags.Length), contourPointer, checked((uint)contourEnds.Length));
            if (status != NativeRendererStatus.Success)
                throw new ArgumentException($"Native hinted transfer rejected buffers with {status}.");
        }
    }

    ~NativeHintedFontBatch() => _owner?.Dispose();

    public void Dispose()
    {
        _owner.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed unsafe partial class NativeTextShapingContext
{
    /// <summary>
    /// Captures the entire original-order glyph batch through one context use.
    /// Device-em/phase values are exact 26.6 integers; variation values are exact
    /// original-order 16.16 design coordinates. No managed source text is changed.
    /// An unavailable/unsupported native font path fails explicitly.
    /// </summary>
    public NativeHintedFontBatch CaptureHintedBatch(uint fontIndex, ReadOnlySpan<uint> glyphIndices,
        uint xPixelsPerEm26_6, uint yPixelsPerEm26_6, NativeFontHintInterpreter interpreter,
        uint xPhase26_6 = 0, uint yPhase26_6 = 0, ReadOnlySpan<int> variationCoordinates16_16 = default)
        => CaptureFontBatch(fontIndex, glyphIndices, xPixelsPerEm26_6, yPixelsPerEm26_6, interpreter,
            NativeFontOutlinePolicy.Normal, false, xPhase26_6, yPhase26_6, variationCoordinates16_16);

    /// <summary>
    /// Captures exact outlines through one native context use with an explicit
    /// load target. This requires the matching native producer; it does not
    /// select source text layout, GPU coverage, contrast or GDI/GDI+ policy.
    /// </summary>
    public NativeHintedFontBatch CaptureOutlineBatch(uint fontIndex, ReadOnlySpan<uint> glyphIndices,
        uint xPixelsPerEm26_6, uint yPixelsPerEm26_6, NativeFontHintInterpreter interpreter,
        NativeFontOutlinePolicy outlinePolicy, uint xPhase26_6 = 0, uint yPhase26_6 = 0,
        ReadOnlySpan<int> variationCoordinates16_16 = default)
        => CaptureFontBatch(fontIndex, glyphIndices, xPixelsPerEm26_6, yPixelsPerEm26_6, interpreter,
            outlinePolicy, true, xPhase26_6, yPhase26_6, variationCoordinates16_16);

    private NativeHintedFontBatch CaptureFontBatch(uint fontIndex, ReadOnlySpan<uint> glyphIndices,
        uint xPixelsPerEm26_6, uint yPixelsPerEm26_6, NativeFontHintInterpreter interpreter,
        NativeFontOutlinePolicy outlinePolicy, bool explicitOutlinePolicy,
        uint xPhase26_6, uint yPhase26_6, ReadOnlySpan<int> variationCoordinates16_16)
    {
        using var use = _owner.Acquire();
        NativeMethods.HintedFontRequest request = new()
        {
            AbiVersion = NativeMethods.AbiVersion,
            StructSize = (uint)sizeof(NativeMethods.HintedFontRequest),
            FontIndex = fontIndex,
            XPixelsPerEm266 = xPixelsPerEm26_6,
            YPixelsPerEm266 = yPixelsPerEm26_6,
            Interpreter = (uint)interpreter,
            XPhase266 = xPhase26_6,
            YPhase266 = yPhase26_6,
            VariationCount = checked((uint)variationCoordinates16_16.Length),
        };
        nint batch = 0;
        fixed (uint* glyphPointer = glyphIndices)
        fixed (int* variationPointer = variationCoordinates16_16)
        {
            NativeRendererStatus status = explicitOutlinePolicy
                ? NativeMethods.CaptureOutlineBatch(use.Handle, &request, (uint)outlinePolicy,
                    variationPointer, glyphPointer, checked((uint)glyphIndices.Length), &batch)
                : NativeMethods.CaptureHintedBatch(use.Handle, &request,
                    variationPointer, glyphPointer, checked((uint)glyphIndices.Length), &batch);
            if (status != NativeRendererStatus.Success)
            {
                if (status == NativeRendererStatus.Unsupported)
                    throw new NotSupportedException("The selected native font hinting path is unavailable or unsupported.");
                if (status == NativeRendererStatus.InvalidArgument)
                    throw new ArgumentException("The native hinted font request or glyph batch is invalid.");
                throw new InvalidOperationException($"Native hinted font capture failed with {status}.");
            }
        }
        try { return new NativeHintedFontBatch(batch); }
        catch
        {
            NativeMethods.DestroyHintedBatch(batch);
            throw;
        }
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_capture_outline_batch")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus CaptureOutlineBatch(nint context, HintedFontRequest* request,
        uint outlinePolicy, int* variationCoordinates16_16, uint* glyphIndices, uint glyphCount, nint* batch);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_capture_hinted_batch")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus CaptureHintedBatch(nint context, HintedFontRequest* request,
        int* variationCoordinates16_16, uint* glyphIndices, uint glyphCount, nint* batch);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_batch_get_counts")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetHintedBatchCounts(nint batch, NativeHintedBatchCounts* counts);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_batch_copy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus CopyHintedBatch(nint batch,
        NativeHintedGlyph* glyphs, uint glyphCapacity, NativeHintedPoint* points, uint pointCapacity,
        byte* tags, uint tagCapacity, int* contourEnds, uint contourCapacity);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_batch_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyHintedBatch(nint batch);
}
