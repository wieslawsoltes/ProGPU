using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

// Fields are generated from the authoritative C header, not duplicated here.
public partial struct NativeHintedGlyphFontSource { }
public partial struct NativeHintedGlyphRunSlice { }
public partial struct NativeHintedGlyphOutlineOwner { }
public partial struct NativeMilHintedGlyphBinding { }

/// <summary>
/// Target-independent original hinted geometry, format, interaction and font
/// bytes. Its native owner stays in the producer library; renderer imports
/// receive only immutable flat records under a destruction-excluding lease.
/// This explicit resource does not select source Display rendering.
/// </summary>
public sealed unsafe partial class NativeHintedGlyphResource : IDisposable
{
    private static readonly Action<nint> NativeDestroy = NativeMethods.DestroyHintedGlyphResource;
    private readonly object _gate = new();
    private readonly NativeMethods.HintedGlyphResourceView _view;
    private readonly Action<nint> _destroy = NativeDestroy;
    private nint _handle;
    private int _uses;
    private bool _disposed;

    internal NativeHintedGlyphResource(nint handle, float dpiScale,
        NativeHintedProjectionPolicy projection, NativeHintedCoverage coverage, bool nominalMetrics = false)
    {
        NativeMethods.HintedGlyphResourceView view = default;
        NativeHintedParagraph.ThrowForStatus(NativeMethods.BorrowHintedGlyphResource(handle, &view), "glyph resource borrow");
        if (view.StructSize != (uint)sizeof(NativeMethods.HintedGlyphResourceView) ||
            view.AbiVersion != NativeMethods.AbiVersion ||
            BitConverter.SingleToInt32Bits(view.DpiScale) != BitConverter.SingleToInt32Bits(dpiScale) ||
            view.ProjectionPolicy != (uint)projection || view.Coverage != (uint)coverage)
            throw new InvalidOperationException("Native hinted geometry changed its exact prepared execution contract.");
        _view = view;
        if (nominalMetrics) _nominalMetrics = BorrowNominalMetrics(handle, view.Counts.PositionedGlyphCount);
        // Ownership transfers last. The factory destroys the raw handle if
        // borrowing or validation throws before this nonthrowing assignment.
        _handle = handle;
    }

    // Internal ownership/fault controls borrow the same generated view without
    // a native dependency. Production preparation still uses the original
    // native borrow/validation constructor above and the cached native destroy.
    internal NativeHintedGlyphResource(nint handle, in NativeMethods.HintedGlyphResourceView view,
        Action<nint> destroy, NativeMethods.HintedGlyphNominalMetricsView? nominalMetrics = null)
    {
        ArgumentNullException.ThrowIfNull(destroy);
        _view = view;
        _nominalMetrics = nominalMetrics;
        _destroy = destroy;
        _handle = handle;
    }

    public float DpiScale => _view.DpiScale;
    public NativeHintedProjectionPolicy Projection => (NativeHintedProjectionPolicy)_view.ProjectionPolicy;
    public NativeHintedCoverage Coverage => (NativeHintedCoverage)_view.Coverage;
    public NativeHintedParagraphCounts Counts => _view.Counts;
    public NativeTextParagraphResult Result => _view.Result;
    public bool IsDisposed { get { lock (_gate) return _disposed; } }

    /// <summary>
    /// Retains read-only typed access to every original cached flat resource
    /// array. The owner may be disposed while the returned reference lease is
    /// live. Managed allocation is bounded to this explicit retained
    /// acquisition; no data is copied or native geometry prepared by
    /// acquisition or span access.
    /// </summary>
    public NativeHintedGlyphResourceReadLease AcquireReadLease()
    {
        NativeMethods.HintedGlyphResourceView view = AcquireForImport();
        try
        {
            return new NativeHintedGlyphResourceReadLease(this, in view, _nominalMetrics);
        }
        catch (Exception error)
        {
            try { EndImport(); }
            catch (Exception cleanupError)
            {
                try { error.Data["HintedGlyphResourceCleanupFailure"] = cleanupError; }
                catch { /* Diagnostic attachment must not replace the setup failure. */ }
            }
            throw;
        }
    }

    // Unlike a mutable shaping context, cached flat data needs no long-held
    // monitor. Concurrent readers acquire short lifetime-counted leases, so
    // reversed multi-resource import order cannot deadlock. No native call or
    // opaque handle is returned to the renderer here.
    internal NativeMethods.HintedGlyphResourceView AcquireForImport()
    {
        lock (_gate)
        {
            if (_disposed || _handle == 0) throw new ObjectDisposedException(nameof(NativeHintedGlyphResource));
            _uses = checked(_uses + 1);
            return _view;
        }
    }

    internal void EndImport()
    {
        lock (_gate)
        {
            System.Diagnostics.Debug.Assert(_uses > 0);
            _uses--;
            ReleaseIfUnused();
        }
    }

    internal void RetryRelease()
    {
        lock (_gate) ReleaseIfUnused();
    }

    private void ReleaseIfUnused()
    {
        if (!_disposed || _uses != 0 || _handle == 0) return;
        _destroy(_handle);
        _handle = 0;
        GC.SuppressFinalize(this);
    }

    ~NativeHintedGlyphResource()
    {
        try { Dispose(); }
        catch { /* Finalization cannot surface a native-module teardown fault. */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            ReleaseIfUnused();
        }
    }
}

/// <summary>
/// Read-only typed access to one resource's ORIGINAL cached flat arrays.
/// Acquisition retains the producer owner; properties neither copy records nor
/// execute native calls. This lease does not create a formatted generation or
/// admit rendering, source Display, paint or a target.
/// </summary>
/// <remarks>
/// Keep this lease alive (normally with a using declaration) until the last span
/// read completes. Disposing or collecting it invalidates every borrowed span.
/// Do not dispose the lease concurrently with span use. Disposing the resource
/// itself is allowed while this lease remains live.
/// </remarks>
public sealed unsafe partial class NativeHintedGlyphResourceReadLease : IDisposable
{
    private readonly object _gate = new();
    private readonly NativeMethods.HintedGlyphResourceView _view;
    private NativeHintedGlyphResource? _owner;
    private bool _ended;

    internal NativeHintedGlyphResourceReadLease(NativeHintedGlyphResource owner,
        in NativeMethods.HintedGlyphResourceView view, NativeMethods.HintedGlyphNominalMetricsView? nominalMetrics = null)
    {
        ValidateRanges(in view);
        if (nominalMetrics is { } metrics) ValidateNominalMetrics(in metrics, view.Counts.PositionedGlyphCount);
        _view = view;
        _nominalMetrics = nominalMetrics;
        // Only a complete checked view can publish a retained reader.
        _owner = owner;
    }

    public bool IsDisposed => Volatile.Read(ref _ended) || Volatile.Read(ref _owner) is null;
    public float DpiScale { get { EnsureLive(); return _view.DpiScale; } }
    public NativeHintedProjectionPolicy Projection { get { EnsureLive(); return (NativeHintedProjectionPolicy)_view.ProjectionPolicy; } }
    public NativeHintedCoverage Coverage { get { EnsureLive(); return (NativeHintedCoverage)_view.Coverage; } }
    public NativeHintedParagraphCounts Counts { get { EnsureLive(); return _view.Counts; } }
    public NativeTextParagraphResult Result { get { EnsureLive(); return _view.Result; } }
    public NativeTextLayoutOptions Layout { get { EnsureLive(); return _view.Layout; } }
    public bool SourceDigitBidi { get { EnsureLive(); return _view.SourceDigitBidi != 0; } }
    public int ParagraphLevel { get { EnsureLive(); return _view.ParagraphLevel; } }
    public NativeTextDirection ShapingDirection { get { EnsureLive(); return (NativeTextDirection)_view.ShapingDirection; } }
    public NativeTextShapeFlags ShapingFlags { get { EnsureLive(); return (NativeTextShapeFlags)_view.ShapingFlags; } }

    public ReadOnlySpan<NativeHintedGlyphFontSource> FontSources => Read<NativeHintedGlyphFontSource>(_view.FontSources, _view.FontSourceCount);
    public ReadOnlySpan<byte> FontBytes => Read<byte>(_view.FontBytes, _view.FontByteCount);
    public ReadOnlySpan<NativeHintedParagraphDeviceStyle> DeviceStyles => Read<NativeHintedParagraphDeviceStyle>(_view.DeviceStyles, _view.Counts.StyleCount);
    public ReadOnlySpan<int> VariationCoordinates1616 => Read<int>(_view.VariationCoordinates1616, _view.VariationCoordinateCount);
    public ReadOnlySpan<short> NormalizedCoordinates => Read<short>(_view.NormalizedCoordinates, _view.NormalizedCoordinateCount);
    public ReadOnlySpan<NativeGlyphOutline> Outlines => Read<NativeGlyphOutline>(_view.Outlines, _view.OutlineCount);
    public ReadOnlySpan<NativePathSegment> Segments => Read<NativePathSegment>(_view.Segments, _view.SegmentCount);
    public ReadOnlySpan<NativeHintedGlyphRunSlice> RunSlices => Read<NativeHintedGlyphRunSlice>(_view.RunSlices, _view.Counts.RunCount);
    public ReadOnlySpan<uint> SourceOutlineIndices => Read<uint>(_view.SourceOutlineIndices, _view.SourceOutlineCount);
    public ReadOnlySpan<uint> RunOutlineIndices => Read<uint>(_view.RunOutlineIndices, _view.RunOutlineCount);
    public ReadOnlySpan<NativeHintedGlyphOutlineOwner> OutlineOwners => Read<NativeHintedGlyphOutlineOwner>(_view.OutlineOwners, _view.OutlineCount);
    public ReadOnlySpan<uint> PositionedOutlineIndices => Read<uint>(_view.PositionedOutlineIndices, _view.Counts.PositionedGlyphCount);
    public ReadOnlySpan<NativeTextScalar> SourceScalars => Read<NativeTextScalar>(_view.SourceScalars, _view.Counts.SourceScalarCount);
    public ReadOnlySpan<NativeTextScalar> AdmittedScalars => Read<NativeTextScalar>(_view.AdmittedScalars, _view.Counts.AdmittedScalarCount);
    public ReadOnlySpan<NativeTextBidiLevel> ScalarLevels => Read<NativeTextBidiLevel>(_view.ScalarLevels, _view.Counts.SourceScalarCount);
    public ReadOnlySpan<NativeTextStyleRun> Styles => Read<NativeTextStyleRun>(_view.Styles, _view.Counts.StyleCount);
    public ReadOnlySpan<NativeTextStyleMetrics> SourceMetrics => Read<NativeTextStyleMetrics>(_view.SourceMetrics, _view.Counts.StyleCount);
    public ReadOnlySpan<NativeHintedParagraphRun> Runs => Read<NativeHintedParagraphRun>(_view.Runs, _view.Counts.RunCount);
    public ReadOnlySpan<NativeTextShapingGlyph> LogicalGlyphs => Read<NativeTextShapingGlyph>(_view.LogicalGlyphs, _view.Counts.LogicalGlyphCount);
    public ReadOnlySpan<NativeHintedParagraphGlyphOwner> LogicalOwners => Read<NativeHintedParagraphGlyphOwner>(_view.LogicalOwners, _view.Counts.LogicalGlyphCount);
    public ReadOnlySpan<int> LogicalClusterEnds => Read<int>(_view.LogicalClusterEnds, _view.Counts.LogicalGlyphCount);
    public ReadOnlySpan<sbyte> LogicalBidiLevels => Read<sbyte>(_view.LogicalBidiLevels, _view.Counts.LogicalGlyphCount);
    public ReadOnlySpan<float> GlyphScales => Read<float>(_view.GlyphScales, _view.Counts.LogicalGlyphCount);
    public ReadOnlySpan<NativePositionedTextGlyph> Glyphs => Read<NativePositionedTextGlyph>(_view.PositionedGlyphs, _view.Counts.PositionedGlyphCount);
    public ReadOnlySpan<NativeHintedParagraphGlyphOwner> PositionedOwners => Read<NativeHintedParagraphGlyphOwner>(_view.PositionedOwners, _view.Counts.PositionedGlyphCount);
    public ReadOnlySpan<int> ClusterEnds => Read<int>(_view.PositionedClusterEnds, _view.Counts.PositionedGlyphCount);
    public ReadOnlySpan<sbyte> BidiLevels => Read<sbyte>(_view.PositionedBidiLevels, _view.Counts.PositionedGlyphCount);
    public ReadOnlySpan<NativePositionedTextLine> Lines => Read<NativePositionedTextLine>(_view.Lines, _view.Counts.LineCount);
    public ReadOnlySpan<float> LineOrigins => Read<float>(_view.LineOrigins, _view.Counts.LineCount);
    public ReadOnlySpan<NativeTextClusterBox> Boxes => Read<NativeTextClusterBox>(_view.Boxes, _view.Counts.ClusterBoxCount);
    public ReadOnlySpan<NativeTextCaretStop> Carets => Read<NativeTextCaretStop>(_view.Carets, _view.Counts.CaretStopCount);
    public ReadOnlySpan<NativeTextScalar> PreContext => Read<NativeTextScalar>(_view.PreContext, _view.PreContextCount);
    public ReadOnlySpan<NativeTextScalar> PostContext => Read<NativeTextScalar>(_view.PostContext, _view.PostContextCount);
    public ReadOnlySpan<NativeTextFeature> Features => Read<NativeTextFeature>(_view.Features, _view.FeatureCount);

    private void EnsureLive()
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(NativeHintedGlyphResourceReadLease));
    }

    private ReadOnlySpan<T> Read<T>(nuint pointer, uint count) where T : unmanaged
    {
        EnsureLive();
        return new ReadOnlySpan<T>((void*)pointer, checked((int)count));
    }

    private static void ValidateRange<T>(nuint pointer, uint count) where T : unmanaged
    {
        _ = checked((int)count);
        if (count != 0 && pointer == 0)
            throw new InvalidOperationException("The original cached resource contains an unavailable nonempty range.");
        _ = checked(pointer + checked((nuint)count * (nuint)sizeof(T)));
    }

    private static void ValidateRanges(in NativeMethods.HintedGlyphResourceView view)
    {
        ValidateRange<NativeHintedGlyphFontSource>(view.FontSources, view.FontSourceCount);
        ValidateRange<byte>(view.FontBytes, view.FontByteCount);
        ValidateRange<NativeHintedParagraphDeviceStyle>(view.DeviceStyles, view.Counts.StyleCount);
        ValidateRange<int>(view.VariationCoordinates1616, view.VariationCoordinateCount);
        ValidateRange<short>(view.NormalizedCoordinates, view.NormalizedCoordinateCount);
        ValidateRange<NativeGlyphOutline>(view.Outlines, view.OutlineCount);
        ValidateRange<NativePathSegment>(view.Segments, view.SegmentCount);
        ValidateRange<NativeHintedGlyphRunSlice>(view.RunSlices, view.Counts.RunCount);
        ValidateRange<uint>(view.SourceOutlineIndices, view.SourceOutlineCount);
        ValidateRange<uint>(view.RunOutlineIndices, view.RunOutlineCount);
        ValidateRange<NativeHintedGlyphOutlineOwner>(view.OutlineOwners, view.OutlineCount);
        ValidateRange<uint>(view.PositionedOutlineIndices, view.Counts.PositionedGlyphCount);
        ValidateRange<NativeTextScalar>(view.SourceScalars, view.Counts.SourceScalarCount);
        ValidateRange<NativeTextScalar>(view.AdmittedScalars, view.Counts.AdmittedScalarCount);
        ValidateRange<NativeTextBidiLevel>(view.ScalarLevels, view.Counts.SourceScalarCount);
        ValidateRange<NativeTextStyleRun>(view.Styles, view.Counts.StyleCount);
        ValidateRange<NativeTextStyleMetrics>(view.SourceMetrics, view.Counts.StyleCount);
        ValidateRange<NativeHintedParagraphRun>(view.Runs, view.Counts.RunCount);
        ValidateRange<NativeTextShapingGlyph>(view.LogicalGlyphs, view.Counts.LogicalGlyphCount);
        ValidateRange<NativeHintedParagraphGlyphOwner>(view.LogicalOwners, view.Counts.LogicalGlyphCount);
        ValidateRange<int>(view.LogicalClusterEnds, view.Counts.LogicalGlyphCount);
        ValidateRange<sbyte>(view.LogicalBidiLevels, view.Counts.LogicalGlyphCount);
        ValidateRange<float>(view.GlyphScales, view.Counts.LogicalGlyphCount);
        ValidateRange<NativePositionedTextGlyph>(view.PositionedGlyphs, view.Counts.PositionedGlyphCount);
        ValidateRange<NativeHintedParagraphGlyphOwner>(view.PositionedOwners, view.Counts.PositionedGlyphCount);
        ValidateRange<int>(view.PositionedClusterEnds, view.Counts.PositionedGlyphCount);
        ValidateRange<sbyte>(view.PositionedBidiLevels, view.Counts.PositionedGlyphCount);
        ValidateRange<NativePositionedTextLine>(view.Lines, view.Counts.LineCount);
        ValidateRange<float>(view.LineOrigins, view.Counts.LineCount);
        ValidateRange<NativeTextClusterBox>(view.Boxes, view.Counts.ClusterBoxCount);
        ValidateRange<NativeTextCaretStop>(view.Carets, view.Counts.CaretStopCount);
        ValidateRange<NativeTextScalar>(view.PreContext, view.PreContextCount);
        ValidateRange<NativeTextScalar>(view.PostContext, view.PostContextCount);
        ValidateRange<NativeTextFeature>(view.Features, view.FeatureCount);
    }

    ~NativeHintedGlyphResourceReadLease()
    {
        try { Dispose(); }
        catch { /* The producer owner retains a failed native teardown. */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            NativeHintedGlyphResource? owner = _owner;
            if (owner is null) return;
            if (!_ended)
            {
                // EndImport decrements before native teardown can throw. The
                // use is ended even on that fault; it must never end twice.
                Volatile.Write(ref _ended, true);
                owner.EndImport();
            }
            else
            {
                // A failed teardown keeps this exact owner for an explicit
                // retry, without consuming a second lifetime count.
                owner.RetryRelease();
            }
            _owner = null;
            GC.SuppressFinalize(this);
        }
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_paragraph_prepare_glyph_resource")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus PrepareHintedGlyphResource(nint paragraph,
        HintedGlyphResourceRequest* request, nint* resource);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_glyph_resource_borrow")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus BorrowHintedGlyphResource(nint resource,
        HintedGlyphResourceView* view);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_glyph_resource_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyHintedGlyphResource(nint resource);
}
