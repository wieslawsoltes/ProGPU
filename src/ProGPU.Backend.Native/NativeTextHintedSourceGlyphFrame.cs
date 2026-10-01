using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

public partial struct NativeHintedSourceGlyphOffset { }
public partial struct NativeHintedSourceGlyphFrame { }

internal unsafe delegate NativeRendererStatus HintedSourceFrameValidator(nint resource, uint* indices, uint count,
    float em, Vector2 baseline, double* advances, NativeHintedSourceGlyphOffset* offsets, NativeHintedSourceGlyphFrame* frame);

public sealed unsafe partial class NativeHintedGlyphResource
{
    private static readonly HintedSourceFrameValidator NativeSourceFrameValidator = NativeMethods.ValidateHintedSourceGlyphFrame;
    private readonly HintedSourceFrameValidator _validateSourceFrame = NativeSourceFrameValidator;

    // Called only while an existing reader holds its own disposal gate. Resource
    // disposal may already have occurred; that reader still excludes destruction.
    internal NativeHintedSourceGlyphFrame ValidateSourceFrameWhileRetained(ReadOnlySpan<uint> indices,
        float em, Vector2 baseline, ReadOnlySpan<double> advances, ReadOnlySpan<NativeHintedSourceGlyphOffset> offsets)
    {
        lock (_gate)
        {
            if (_handle == 0 || _uses <= 0) throw new ObjectDisposedException(nameof(NativeHintedGlyphResource));
            NativeHintedSourceGlyphFrame frame = default;
            fixed (uint* pIndices = indices)
            fixed (double* pAdvances = advances)
            fixed (NativeHintedSourceGlyphOffset* pOffsets = offsets)
                NativeHintedParagraph.ThrowForStatus(_validateSourceFrame(_handle, pIndices, checked((uint)indices.Length),
                    em, baseline, pAdvances, pOffsets, &frame), "source glyph frame validation");
            return frame;
        }
    }
}

public sealed unsafe partial class NativeHintedGlyphResourceReadLease
{
    /// <summary>
    /// Validates the source's nominal horizontal offset convention against one
    /// original writer line and original hmtx metadata. Returns separate source
    /// baseline and paragraph draw translations without changing glyph positions.
    /// One synchronous native crossing; no font access, layout or per-glyph call.
    /// Display rounding, cross-line runs and unrepresentable frames are unsupported.
    /// </summary>
    public NativeHintedSourceGlyphFrame ValidateSourceFrame(ReadOnlySpan<uint> positionedIndices,
        float sourceEmSize, Vector2 sourceBaselineOrigin, ReadOnlySpan<double> sourceAdvances,
        ReadOnlySpan<NativeHintedSourceGlyphOffset> sourceOffsets)
    {
        lock (_gate)
        {
            EnsureLive();
            if (!_nominalMetrics.HasValue) throw new NotSupportedException("The original resource has no retained nominal design metrics.");
            if (positionedIndices.IsEmpty || sourceAdvances.Length != positionedIndices.Length || sourceOffsets.Length != positionedIndices.Length)
                throw new ArgumentException("Source offsets and advances must cover the exact nonempty occurrence selection.");
            return _owner!.ValidateSourceFrameWhileRetained(positionedIndices, sourceEmSize, sourceBaselineOrigin, sourceAdvances, sourceOffsets);
        }
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_glyph_resource_validate_source_frame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ValidateHintedSourceGlyphFrame(nint resource, uint* indices, uint count,
        float em, Vector2 baseline, double* advances, NativeHintedSourceGlyphOffset* offsets, NativeHintedSourceGlyphFrame* frame);
}
