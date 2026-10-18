using System.Numerics;

namespace ProGPU.Text;

/// <summary>
/// One original positioned occurrence, including non-drawable descriptors.
/// OutlineIndex is an explicit retained slot, never a glyph-ID lookup.
/// These are semantic managed records, not a second native wire declaration.
/// </summary>
public readonly record struct HintedGlyphOccurrence(
    uint PositionedIndex, uint LogicalIndex, uint GlyphId, uint FontIndex,
    uint RunIndex, uint RunGlyphIndex, uint DescriptorIndex, uint OutlineIndex,
    uint Cluster, int ClusterEnd, sbyte BidiLevel, Vector2 Position, Vector2 Advance);

/// <summary>
/// One captured font descriptor at the caller's original baseline position.
/// This record has no shaping, cluster, advance or bidi metadata.
/// </summary>
public readonly record struct FontGlyphPlacement(
    uint DescriptorIndex, uint GlyphId, uint OutlineIndex, Vector2 Position);

/// <summary>
/// Immutable original physical Y-up glyph geometry and source-indexed draws.
/// Shaped occurrences and already-positioned font descriptors remain distinct;
/// the latter do not acquire invented layout or interaction metadata.
/// Preparation does not bind a target, paint, font decoder or presentation mode.
/// Recorded owners retain the same originating producer generation after the
/// caller disposes this wrapper. This explicit resource does not select Display.
/// </summary>
public sealed class HintedGlyphGeometry : IDisposable
{
    private readonly object _gate = new();
    private readonly GpuGlyphRecord[] _outlines;
    private readonly GpuSegment[] _segments;
    private readonly HintedGlyphOccurrence[] _occurrences;
    private readonly FontGlyphPlacement[]? _fontPlacements;
    private readonly object _rasterGenerationIdentity;
    private IDisposable? _sourceOwner;
    private int _uses;
    private bool _disposed;
    private bool _retired;
    private bool _releaseInProgress;

    // Only the reviewed native adapter transfers arrays and its ORIGINAL read
    // lease here. No public constructor accepts manufactured geometry or owners.
    internal HintedGlyphGeometry(float dpiScale, GpuGlyphRecord[] outlines,
        GpuSegment[] segments, HintedGlyphOccurrence[] occurrences, IDisposable sourceOwner)
        : this(dpiScale, outlines, segments, occurrences, sourceOwner, new object())
    {
    }

    internal static HintedGlyphGeometry FromFontPlacements(float dpiScale, GpuGlyphRecord[] outlines,
        GpuSegment[] segments, FontGlyphPlacement[] placements, IDisposable sourceOwner)
        => new(dpiScale, outlines, segments, [], sourceOwner, new object(),
            placements ?? throw new ArgumentNullException(nameof(placements)));

    private HintedGlyphGeometry(float dpiScale, GpuGlyphRecord[] outlines,
        GpuSegment[] segments, HintedGlyphOccurrence[] occurrences, IDisposable sourceOwner,
        object rasterGenerationIdentity, FontGlyphPlacement[]? fontPlacements = null)
    {
        ArgumentNullException.ThrowIfNull(outlines);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(occurrences);
        ArgumentNullException.ThrowIfNull(sourceOwner);
        if (!float.IsFinite(dpiScale) || dpiScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpiScale));
        DpiScale = dpiScale;
        _outlines = outlines;
        _segments = segments;
        _occurrences = occurrences;
        _fontPlacements = fontPlacements;
        _rasterGenerationIdentity = rasterGenerationIdentity;
        // The factory owns failure cleanup until this last nonthrowing transfer.
        _sourceOwner = sourceOwner;
    }

    public float DpiScale { get; }
    public int OutlineCount => _outlines.Length;
    public int SegmentCount => _segments.Length;
    public int OccurrenceCount => _fontPlacements?.Length ?? _occurrences.Length;
    /// <summary>True only when the original producer retained shaping metadata.</summary>
    public bool HasShapingMetadata => _fontPlacements is null;
    public bool IsDisposed => Volatile.Read(ref _disposed);

    /// <summary>Borrowed immutable physical outlines while this wrapper is live.</summary>
    public ReadOnlySpan<GpuGlyphRecord> Outlines { get { EnsureRecordingAdmission(); return _outlines; } }
    /// <summary>Borrowed immutable physical segments while this wrapper is live.</summary>
    public ReadOnlySpan<GpuSegment> Segments { get { EnsureRecordingAdmission(); return _segments; } }
    /// <summary>Every original positioned occurrence, including no-ink slots.</summary>
    public ReadOnlySpan<HintedGlyphOccurrence> Occurrences { get { EnsureRecordingAdmission(); RequireShapingMetadata(); return _occurrences; } }
    /// <summary>Original font descriptors and caller placement, without invented shaping records.</summary>
    public ReadOnlySpan<FontGlyphPlacement> FontPlacements
    {
        get
        {
            EnsureRecordingAdmission();
            if (_fontPlacements is null) throw new InvalidOperationException("This resource retains shaped occurrences.");
            return _fontPlacements;
        }
    }

    /// <summary>
    /// Owns the selected occurrence slots in caller order, including duplicates
    /// and no-ink slots. Indices address this view; PositionedIndex and all other
    /// original identities remain unchanged. Physical outlines/segments are
    /// shared, never copied, decoded, reshaped or renumbered. The selected view
    /// can outlive this wrapper and owns the same original producer generation.
    /// </summary>
    public HintedGlyphGeometry SelectOccurrences(ReadOnlySpan<int> occurrenceIndices)
    {
        lock (_gate)
        {
            EnsureRecordingAdmission();
            // Own the indices before validation so later caller mutation cannot
            // change a validated slot. Publish neither a view nor an owner on a
            // late invalid index.
            int[] indices = occurrenceIndices.ToArray();
            foreach (int index in indices)
                if ((uint)index >= (uint)OccurrenceCount)
                    throw new ArgumentOutOfRangeException(nameof(occurrenceIndices));
            var selected = _fontPlacements is null ? new HintedGlyphOccurrence[indices.Length] : [];
            var placements = _fontPlacements is null ? null : new FontGlyphPlacement[indices.Length];
            for (int i = 0; i < indices.Length; i++)
                if (placements is null) selected[i] = _occurrences[indices[i]];
                else placements[i] = _fontPlacements![indices[i]];
            IDisposable owner = RetainForRecording();
            try { return new(DpiScale, _outlines, _segments, selected, owner, _rasterGenerationIdentity, placements); }
            catch (Exception failure)
            {
                try { owner.Dispose(); }
                catch (Exception cleanup)
                {
                    try { failure.Data["HintedGlyphSelectionCleanupFailure"] = cleanup; }
                    catch { /* Preserve the original publication failure. */ }
                }
                throw;
            }
        }
    }

    // Renderers borrow a drawing/picture's retained lifetime, not the disposed
    // caller wrapper. Data is never cleared/redecoded/repositioned at retirement.
    internal ReadOnlySpan<GpuGlyphRecord> RenderOutlines { get { EnsureRenderStorage(); return _outlines; } }
    internal ReadOnlySpan<GpuSegment> RenderSegments { get { EnsureRenderStorage(); return _segments; } }
    internal ReadOnlySpan<HintedGlyphOccurrence> RenderOccurrences { get { EnsureRenderStorage(); RequireShapingMetadata(); return _occurrences; } }
    internal FontGlyphPlacement GetRenderPlacement(int index)
    {
        EnsureRenderStorage();
        if (_fontPlacements is not null) return _fontPlacements[index];
        ref readonly HintedGlyphOccurrence source = ref _occurrences[index];
        return new(source.DescriptorIndex, source.GlyphId, source.OutlineIndex, source.Position);
    }

    private void RequireShapingMetadata()
    {
        if (_fontPlacements is not null)
            throw new InvalidOperationException("Captured font geometry has no shaping metadata.");
    }
    internal bool HasRenderStorage => !Volatile.Read(ref _retired);

    // Cache identity is opaque and immutable, not a geometry/source-owner lease.
    // Selected and nested views retain the original physical outline numbering.
    // Comparing a retired key must never dereference its former storage owner.
    internal object RasterGenerationIdentity => _rasterGenerationIdentity;

    internal void EnsureRecordingAdmission()
    {
        if (Volatile.Read(ref _disposed)) throw new ObjectDisposedException(nameof(HintedGlyphGeometry));
    }

    private void EnsureRenderStorage()
    {
        if (!HasRenderStorage) throw new ObjectDisposedException(nameof(HintedGlyphGeometry));
    }

    internal IDisposable RetainForRecording()
    {
        lock (_gate)
        {
            EnsureRecordingAdmission();
            _uses = checked(_uses + 1);
            try { return new RecordingLease(this); }
            catch { _uses--; throw; }
        }
    }

    // A renderer may outlive its recorded source. It borrows an already-owned
    // recorded generation, not the caller's disposed public admission surface.
    internal IDisposable RetainForReplay()
    {
        lock (_gate)
        {
            if (_retired || _uses == 0)
                throw new ObjectDisposedException(nameof(HintedGlyphGeometry),
                    "Hinted replay requires an existing recorded generation owner.");
            _uses = checked(_uses + 1);
            try { return new RecordingLease(this); }
            catch { _uses--; throw; }
        }
    }

    private void EndUse()
    {
        lock (_gate)
        {
            System.Diagnostics.Debug.Assert(_uses > 0);
            _uses--;
            ReleaseIfUnused();
        }
    }

    private void RetryRelease()
    {
        lock (_gate) ReleaseIfUnused();
    }

    private void ReleaseIfUnused()
    {
        if (!_disposed || _uses != 0) return;
        // Retirement is visible before teardown/reentrancy. Failure retains the
        // exact source owner for retry but never admits another use or rendering.
        Volatile.Write(ref _retired, true);
        if (_sourceOwner is null || _releaseInProgress) return;
        _releaseInProgress = true;
        try
        {
            _sourceOwner.Dispose();
            _sourceOwner = null;
            GC.SuppressFinalize(this);
        }
        finally { _releaseInProgress = false; }
    }

    ~HintedGlyphGeometry()
    {
        try { Dispose(); }
        catch { /* Explicit disposal retains retry ownership on teardown faults. */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Volatile.Write(ref _disposed, true);
            ReleaseIfUnused();
        }
    }

    private sealed class RecordingLease : IDisposable
    {
        private readonly object _gate = new();
        private HintedGlyphGeometry? _owner;
        private bool _ended;

        internal RecordingLease(HintedGlyphGeometry owner) => _owner = owner;

        ~RecordingLease()
        {
            try { Dispose(); }
            catch { /* The exact source owner retains failed teardown state. */ }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                var owner = _owner;
                if (owner is null) return;
                if (!_ended)
                {
                    _ended = true;
                    owner.EndUse();
                }
                else owner.RetryRelease();
                _owner = null;
                GC.SuppressFinalize(this);
            }
        }
    }
}
