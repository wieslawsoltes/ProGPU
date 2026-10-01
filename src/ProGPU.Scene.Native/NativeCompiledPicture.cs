using ProGPU.Backend.Native;
using ProGPU.Text;
using System.Runtime.ExceptionServices;

namespace ProGPU.Scene.Native;

/// <summary>
/// Owns one immutable pointer-free native semantic scene snapshot.
/// </summary>
public sealed class NativeCompiledPicture : IDisposable
{
    internal NativeCompiledPicture(
        byte[] storage,
        int length,
        ulong sceneId,
        ulong generation,
        float targetDpiScale,
        int sourceCommandCount,
        int nativeCommandCount,
        int nativeDrawCount,
        int analyticPrimitiveCount,
        int geometryPrimitiveCount,
        int pathCount,
        int pathSegmentCount,
        int pointBatchCount,
        int pointCount,
        int vertexMeshCount,
        int meshVertexCount,
        int meshIndexCount,
        int strokeCount,
        int strokePointCount,
        int strokeDoubleCount,
        int glyphOutlineCount,
        int glyphSegmentCount,
        int colorGlyphBitmapCount,
        int colorGlyphPixelBytes,
        int positionedGlyphCount,
        int textStyleCount,
        int line3DCount,
        int brushCount,
        int gradientStopCount,
        NativeSceneExternalImageBinding[] externalImages,
        SourceOwnership? sourceOwnership = null)
    {
        Storage = storage;
        _externalImages = externalImages;
        Length = length;
        SceneId = sceneId;
        Generation = generation;
        TargetDpiScale = targetDpiScale;
        SourceCommandCount = sourceCommandCount;
        NativeCommandCount = nativeCommandCount;
        NativeDrawCount = nativeDrawCount;
        AnalyticPrimitiveCount = analyticPrimitiveCount;
        GeometryPrimitiveCount = geometryPrimitiveCount;
        PathCount = pathCount;
        PathSegmentCount = pathSegmentCount;
        PointBatchCount = pointBatchCount;
        PointCount = pointCount;
        VertexMeshCount = vertexMeshCount;
        MeshVertexCount = meshVertexCount;
        MeshIndexCount = meshIndexCount;
        StrokeCount = strokeCount;
        StrokePointCount = strokePointCount;
        StrokeDoubleCount = strokeDoubleCount;
        GlyphOutlineCount = glyphOutlineCount;
        GlyphSegmentCount = glyphSegmentCount;
        ColorGlyphBitmapCount = colorGlyphBitmapCount;
        ColorGlyphPixelBytes = colorGlyphPixelBytes;
        PositionedGlyphCount = positionedGlyphCount;
        TextStyleCount = textStyleCount;
        Line3DCount = line3DCount;
        BrushCount = brushCount;
        GradientStopCount = gradientStopCount;
        // The compiler transfers its transaction only after all stream and
        // binding construction succeeds. Ordinary pictures have no ownership
        // holder/finalizer and preserve their original copied-byte behavior.
        _sourceOwnership = sourceOwnership;
    }

    private byte[] Storage { get; }

    private readonly NativeSceneExternalImageBinding[] _externalImages;
    private readonly SourceOwnership? _sourceOwnership;

    public int Length { get; }

    public ulong SceneId { get; }

    public ulong Generation { get; }

    /// <summary>
    /// Gets the physical target scale used to compile target-sensitive glyph
    /// raster records.
    /// </summary>
    public float TargetDpiScale { get; }

    /// <summary>
    /// Gets the total command count across the root picture and every
    /// recursively flattened retained child picture.
    /// </summary>
    public int SourceCommandCount { get; }

    public int NativeCommandCount { get; }

    public int NativeDrawCount { get; }

    public int AnalyticPrimitiveCount { get; }

    public int GeometryPrimitiveCount { get; }

    public int PathCount { get; }

    public int PathSegmentCount { get; }

    public int PointBatchCount { get; }

    public int PointCount { get; }

    public int VertexMeshCount { get; }

    public int MeshVertexCount { get; }

    public int MeshIndexCount { get; }

    public int StrokeCount { get; }

    public int StrokePointCount { get; }

    public int StrokeDoubleCount { get; }

    public int GlyphOutlineCount { get; }

    public int GlyphSegmentCount { get; }

    public int ColorGlyphBitmapCount { get; }

    public int ColorGlyphPixelBytes { get; }

    public int PositionedGlyphCount { get; }

    public int TextStyleCount { get; }

    public int Line3DCount { get; }

    public int BrushCount { get; }

    public int GradientStopCount { get; }

    /// <summary>
    /// Gets the live same-device image bindings required before this retained
    /// scene snapshot is installed. The scene stream itself stays pointer-free.
    /// </summary>
    public ReadOnlySpan<NativeSceneExternalImageBinding> ExternalImages
    {
        get { EnsureRetainedSourceLive(); return _externalImages; }
    }

    public ReadOnlyMemory<byte> Memory { get { EnsureRetainedSourceLive(); return Storage.AsMemory(0, Length); } }

    public ReadOnlySpan<byte> Stream { get { EnsureRetainedSourceLive(); return Storage.AsSpan(0, Length); } }

    private void EnsureRetainedSourceLive()
    {
        if (_sourceOwnership?.IsRetired == true)
            throw new ObjectDisposedException(nameof(NativeCompiledPicture));
    }

    /// <summary>
    /// Retires original hinted source uses held by this snapshot. This does not
    /// dispose a renderer's independent native scene or any caller-owned cache.
    /// Failed teardown retains the exact failed use for a subsequent retry.
    /// </summary>
    public void Dispose() => _sourceOwnership?.Dispose();

    internal sealed class SourceTransaction : IDisposable
    {
        private List<NativeCompiledPicture?>? _ownedChildren;
        internal SourceOwnership? Ownership { get; private set; }
        internal bool TryRetain(HintedGlyphGeometry geometry) =>
            (Ownership ??= new SourceOwnership()).TryRetain(geometry);
        internal void RetainChild(NativeCompiledPicture picture)
        {
            if (picture._sourceOwnership is null) return;
            (_ownedChildren ??= []).EnsureCapacity(checked(_ownedChildren.Count + 1));
            (Ownership ??= new SourceOwnership()).RetainFrom(picture._sourceOwnership);
            // These are exclusively created compile candidates, not caller or
            // renderer cached snapshots. The parent acquires distinct original
            // geometry uses; children retire after flattening or on failure.
            _ownedChildren.Add(picture);
        }
        internal void CommitTransfer() => Ownership = null;
        internal void RetireChildren()
        {
            Exception? first = null;
            if (_ownedChildren is not null)
                for (int i = 0; i < _ownedChildren.Count; i++)
                {
                    NativeCompiledPicture? child = _ownedChildren[i];
                    if (child is null) continue;
                    try { child.Dispose(); _ownedChildren[i] = null; }
                    catch (Exception failure) { first ??= failure; }
                }
            if (first is not null) ExceptionDispatchInfo.Capture(first).Throw();
        }
        public void Dispose()
        {
            Exception? first = null;
            try { RetireChildren(); }
            catch (Exception failure) { first = failure; }
            try { Ownership?.Dispose(); }
            catch (Exception failure) { first ??= failure; }
            if (first is not null) ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    // Created only for an actual original hinted resource. It is also the
    // compiler's failure transaction, so no untransferred source use can escape.
    internal sealed class SourceOwnership : IDisposable
    {
        private readonly object _gate = new();
        private readonly HashSet<HintedGlyphGeometry> _geometries = new(ReferenceEqualityComparer.Instance);
        private readonly List<IDisposable?> _uses = [];
        private bool _retired;
        private bool _draining;

        internal bool IsRetired => Volatile.Read(ref _retired);

        internal bool TryRetain(HintedGlyphGeometry geometry)
        {
            lock (_gate)
            {
                if (_retired) return false;
                if (_geometries.Contains(geometry)) return true;
                _geometries.EnsureCapacity(checked(_geometries.Count + 1));
                _uses.EnsureCapacity(checked(_uses.Count + 1));
                IDisposable use;
                try { use = geometry.RetainForReplay(); }
                catch (ObjectDisposedException) { return false; }
                // Capacity was reserved before the originating lifetime is touched.
                _geometries.Add(geometry);
                _uses.Add(use);
                return true;
            }
        }

        internal void RetainFrom(SourceOwnership child)
        {
            lock (child._gate)
            {
                if (child._retired) throw new ObjectDisposedException(nameof(NativeCompiledPicture));
                foreach (HintedGlyphGeometry geometry in child._geometries)
                    if (!TryRetain(geometry)) throw new ObjectDisposedException(nameof(HintedGlyphGeometry));
            }
        }

        ~SourceOwnership()
        {
            try { Dispose(); }
            catch { /* Explicit disposal keeps exact failed uses for retry. */ }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                Volatile.Write(ref _retired, true);
                if (_draining) return;
                _draining = true;
                Exception? first = null;
                try
                {
                    for (int i = 0; i < _uses.Count; i++)
                    {
                        IDisposable? use = _uses[i];
                        if (use is null) continue;
                        try { use.Dispose(); _uses[i] = null; }
                        catch (Exception failure) { first ??= failure; }
                    }
                    if (first is null)
                    {
                        _geometries.Clear();
                        GC.SuppressFinalize(this);
                    }
                }
                finally { _draining = false; }
                if (first is not null) ExceptionDispatchInfo.Capture(first).Throw();
            }
        }
    }
}
