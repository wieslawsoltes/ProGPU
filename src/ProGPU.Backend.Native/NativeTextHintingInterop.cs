using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

public enum NativeFontHintInterpreter : uint
{
    TrueType35 = 35,
    TrueType40 = 40,
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
            NativeRendererStatus status = NativeMethods.CaptureHintedBatch(use.Handle, &request,
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
