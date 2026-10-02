using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

public sealed unsafe partial class NativeHintedGlyphResource
{
    // Source-double nominal preparation is separate from the old float nominal
    // borrow. The native owner retains the original double cache and hmtx data.
    private readonly bool _hasSourceMetrics;

    internal void CopySourceMetricsWhileRetained(ReadOnlySpan<uint> indices, double em, double dpi,
        Span<double> advances, Span<NativeHintedSourceGlyphOffset> offsets)
    {
        lock (_gate)
        {
            if (_handle == 0 || _uses <= 0) throw new ObjectDisposedException(nameof(NativeHintedGlyphResource));
            fixed (uint* pIndices = indices)
            fixed (double* pAdvances = advances)
            fixed (NativeHintedSourceGlyphOffset* pOffsets = offsets)
                NativeHintedParagraph.ThrowForStatus(NativeMethods.CopyHintedSourceMetrics(_handle, pIndices,
                    checked((uint)indices.Length), em, dpi, pAdvances, checked((uint)advances.Length),
                    pOffsets, checked((uint)offsets.Length)), "double source metric copy");
        }
    }

    internal NativeHintedSourceRunFrame ValidateSourceRunWhileRetained(ReadOnlySpan<uint> indices, double em, double dpi,
        NativeHintedSourceGlyphOffset baseline, ReadOnlySpan<double> advances, ReadOnlySpan<NativeHintedSourceGlyphOffset> offsets)
    {
        lock (_gate)
        {
            if (_handle == 0 || _uses <= 0) throw new ObjectDisposedException(nameof(NativeHintedGlyphResource));
            NativeHintedSourceRunFrame result = default;
            fixed (uint* pIndices = indices)
            fixed (double* pAdvances = advances)
            fixed (NativeHintedSourceGlyphOffset* pOffsets = offsets)
                NativeHintedParagraph.ThrowForStatus(NativeMethods.ValidateHintedSourceRun(_handle, pIndices,
                    checked((uint)indices.Length), em, dpi, baseline, pAdvances, pOffsets, &result), "double source run validation");
            return result;
        }
    }
}

public sealed unsafe partial class NativeHintedGlyphResourceReadLease
{
    private readonly bool _hasSourceMetrics;
    public bool HasSourceMetrics { get { EnsureLive(); return _hasSourceMetrics; } }

    /// <summary>
    /// Copies original double advances and nominal source offsets for the selected
    /// occurrence order in one native crossing. Exact native em/DPI identity and
    /// one original run/font/full level/line are required. Caller tails are untouched.
    /// </summary>
    public void CopySourceMetrics(ReadOnlySpan<uint> positionedIndices, double sourceEmSize, double sourcePixelsPerDip,
        Span<double> advances, Span<NativeHintedSourceGlyphOffset> offsets)
    {
        lock (_gate)
        {
            EnsureLive();
            if (!_hasSourceMetrics) throw new NotSupportedException("The resource has no original source-double nominal metrics.");
            if (positionedIndices.IsEmpty || advances.Length < positionedIndices.Length || offsets.Length < positionedIndices.Length)
                throw new ArgumentException("Source metric capacities must cover the exact nonempty occurrence selection.");
            _owner!.CopySourceMetricsWhileRetained(positionedIndices, sourceEmSize, sourcePixelsPerDip, advances, offsets);
        }
    }

    /// <summary>Validates the original double source run and separately admitted raster translation.</summary>
    public NativeHintedSourceRunFrame ValidateSourceRun(ReadOnlySpan<uint> positionedIndices, double sourceEmSize,
        double sourcePixelsPerDip, NativeHintedSourceGlyphOffset sourceBaselineOrigin, ReadOnlySpan<double> sourceAdvances,
        ReadOnlySpan<NativeHintedSourceGlyphOffset> sourceOffsets)
    {
        lock (_gate)
        {
            EnsureLive();
            if (!_hasSourceMetrics) throw new NotSupportedException("The resource has no original source-double nominal metrics.");
            if (positionedIndices.IsEmpty || sourceAdvances.Length != positionedIndices.Length || sourceOffsets.Length != positionedIndices.Length)
                throw new ArgumentException("Source metrics must cover the exact nonempty occurrence selection.");
            return _owner!.ValidateSourceRunWhileRetained(positionedIndices, sourceEmSize, sourcePixelsPerDip,
                sourceBaselineOrigin, sourceAdvances, sourceOffsets);
        }
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_glyph_resource_copy_source_metrics")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus CopyHintedSourceMetrics(nint resource, uint* indices, uint count,
        double em, double dpi, double* advances, uint advanceCapacity, NativeHintedSourceGlyphOffset* offsets, uint offsetCapacity);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_glyph_resource_validate_source_run")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ValidateHintedSourceRun(nint resource, uint* indices, uint count,
        double em, double dpi, NativeHintedSourceGlyphOffset baseline, double* advances,
        NativeHintedSourceGlyphOffset* offsets, NativeHintedSourceRunFrame* frame);
}
