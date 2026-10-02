using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

public sealed unsafe partial class NativeHintedGlyphResource
{
    private readonly NativeMethods.HintedSourceGlyphResourceView? _sourceView;

    /// <summary>Whether this exact owner has the additive original source-double import contract.</summary>
    public bool HasSourceGeometry => _sourceView.HasValue;

    private static NativeMethods.HintedSourceGlyphResourceView BorrowSourceView(nint handle,
        in NativeMethods.HintedGlyphResourceView raster)
    {
        NativeMethods.HintedSourceGlyphResourceView source = default;
        NativeHintedParagraph.ThrowForStatus(NativeMethods.BorrowHintedSourceGlyphResource(handle, &source),
            "source glyph resource borrow");
        ValidateSourceView(in source, in raster);
        return source;
    }

    internal static void ValidateSourceView(in NativeMethods.HintedSourceGlyphResourceView source,
        in NativeMethods.HintedGlyphResourceView raster)
    {
        var options = source.Source.Options;
        if (source.AbiVersion != NativeMethods.AbiVersion || source.StructSize != sizeof(NativeMethods.HintedSourceGlyphResourceView) ||
            source.Version != 2 || source.Flags != 0 || source.Reserved != 0 ||
            options.AbiVersion != NativeMethods.AbiVersion || options.StructSize != sizeof(NativeHintedSourceOptions) ||
            options.Version != 1 || (options.Flags & ~1U) != 0 || options.EmPolicy > 3 || options.AdvancePolicy > 2 ||
            options.OffsetPolicy > 1 || options.AllowEmergencyBreak > 1 ||
            source.Source.StyleCount != raster.Counts.StyleCount || source.Source.LogicalCount != raster.Counts.LogicalGlyphCount ||
            source.Source.GlyphCount != raster.Counts.PositionedGlyphCount || source.Source.LineCount != raster.Counts.LineCount ||
            raster.Counts.ClusterBoxCount != 0 || raster.Counts.CaretStopCount != 0)
            throw new InvalidOperationException("Native source geometry changed its exact versioned import contract.");
    }

    internal NativeMethods.HintedSourceGlyphResourceView? SourceViewWhileRetained()
    {
        lock (_gate)
        {
            // Dispose may have been requested after acquisition. The active
            // import still owns every reachable array until its lease ends.
            if (_handle == 0 || _uses <= 0) throw new ObjectDisposedException(nameof(NativeHintedGlyphResource));
            return _sourceView;
        }
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_glyph_resource_borrow_source")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus BorrowHintedSourceGlyphResource(nint resource,
        HintedSourceGlyphResourceView* view);
}
