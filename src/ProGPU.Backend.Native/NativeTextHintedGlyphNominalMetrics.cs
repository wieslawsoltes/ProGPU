using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

// Generated from the public C header. AdvanceWidthDesignUnits is unhinted hmtx
// data, not a positioned, device-pixel or variable-instance advance.
public partial struct NativeHintedGlyphNominalMetrics { }

public sealed unsafe partial class NativeHintedGlyphResource
{
    private readonly NativeMethods.HintedGlyphNominalMetricsView? _nominalMetrics;

    private static NativeMethods.HintedGlyphNominalMetricsView BorrowNominalMetrics(nint handle, uint count)
    {
        NativeMethods.HintedGlyphNominalMetricsView view = default;
        NativeHintedParagraph.ThrowForStatus(NativeMethods.BorrowHintedGlyphNominalMetrics(handle, &view), "nominal metric borrow");
        NativeHintedGlyphResourceReadLease.ValidateNominalMetrics(in view, count);
        return view;
    }
}

public sealed unsafe partial class NativeHintedGlyphResourceReadLease
{
    private readonly NativeMethods.HintedGlyphNominalMetricsView? _nominalMetrics;

    /// <summary>True only for explicit nominal-metric preparation of this same generation.</summary>
    public bool HasNominalMetrics { get { EnsureLive(); return _nominalMetrics.HasValue; } }

    /// <summary>
    /// One original horizontal design advance per positioned occurrence,
    /// including repeats and no-ink glyphs. Borrowed through this same reader
    /// lease; no native call, font lookup or shaping occurs on access.
    /// Ordinary resources reject rather than synthesizing missing metrics.
    /// </summary>
    public ReadOnlySpan<NativeHintedGlyphNominalMetrics> NominalMetrics
    {
        get
        {
            EnsureLive();
            if (_nominalMetrics is not { } view)
                throw new NotSupportedException("The original resource was not prepared with nominal design metrics.");
            return Read<NativeHintedGlyphNominalMetrics>(view.Metrics, view.MetricCount);
        }
    }

    internal static void ValidateNominalMetrics(in NativeMethods.HintedGlyphNominalMetricsView view, uint count)
    {
        if (view.AbiVersion != NativeMethods.AbiVersion || view.StructSize != sizeof(NativeMethods.HintedGlyphNominalMetricsView) ||
            view.Reserved != 0 || view.MetricCount != count)
            throw new InvalidOperationException("Nominal metrics do not cover the original positioned generation.");
        ValidateRange<NativeHintedGlyphNominalMetrics>(view.Metrics, view.MetricCount);
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_paragraph_prepare_glyph_resource_with_nominal_metrics")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus PrepareHintedGlyphResourceWithNominalMetrics(nint paragraph,
        HintedGlyphResourceRequest* request, nint* resource);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_glyph_resource_borrow_nominal_metrics")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus BorrowHintedGlyphNominalMetrics(nint resource,
        HintedGlyphNominalMetricsView* view);
}
