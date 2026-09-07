using System;
using System.Numerics;
using ProGPU.Vector;

namespace ProGPU.Scene;

/// <summary>
/// A shared, lazily rasterized picture source. Draw it repeatedly with
/// <see cref="DrawingContext.DrawCachedPicture"/> to reuse one layer texture
/// while keeping consumer transforms, clips and opacity outside the capture.
/// </summary>
/// <remarks>
/// Access is serialized on the rendering thread, like Visual. Bounds are an
/// exact capture rectangle, not a culling hint. Update shares immutable command
/// storage and owns independent picture resource leases. Mutable resources or
/// embedded visuals in that picture require an explicit Invalidate notification.
/// Disposal empties previously recorded references; GPU retirement remains owned
/// by the compositor. No GPU is initialized by construction, update or recording.
/// </remarks>
public sealed class CachedPicture : IDisposable
{
    private readonly CacheVisual _visual;
    private readonly ICachedPictureSource? _source;
    private readonly bool _ownsSource;
    private GpuPicture? _picture;
    private bool _disposed;
    private bool _sourceDirty;
    private bool _refreshing;
    private ulong _sourceVersion;

    private CachedPicture() => _visual = new CacheVisual(this);

    /// <summary>
    /// Captures an initial recording and subscribes to typed invalidation.
    /// Subsequent captures are deferred until Refresh or layer preparation.
    /// When ownsSource is true, disposal and failed construction dispose source.
    /// </summary>
    public CachedPicture(ICachedPictureSource source, bool ownsSource = false) : this()
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _ownsSource = ownsSource;
        _sourceDirty = true;
        source.Invalidated += OnSourceInvalidated;
        try
        {
            Refresh();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public CachedPicture(GpuPicture picture, Rect bounds, float renderScale = 1f)
        : this(picture, bounds, renderScale, enableClearType: true)
    {
    }

    public CachedPicture(GpuPicture picture, Rect bounds, float renderScale, bool enableClearType) : this()
    {
        Update(picture, bounds, renderScale, enableClearType);
    }

    public Rect Bounds { get; private set; }
    public float RenderScale => _visual.LayerCacheRenderScale;
    /// <summary>
    /// Whether captured text may use its recorded ClearType mode. False lowers
    /// ClearType to grayscale without changing explicitly aliased text. Generic
    /// picture construction preserves recorded modes by default; WPF cache policy
    /// supplies its own false default explicitly.
    /// </summary>
    public bool EnableClearType => _visual.EnableClearType;

    public bool IsSourceDirty => _sourceDirty;

    /// <summary>
    /// Recaptures a dirty live source transactionally. Unchanged sources are
    /// O(1) no-ops. Failure preserves previous ownership and remains dirty;
    /// rendering propagates the failure instead of using stale pixels.
    /// </summary>
    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source == null || !_sourceDirty) return;
        if (_refreshing)
            throw new InvalidOperationException("A cached picture source cannot recursively capture itself.");
        _refreshing = true;
        ulong version = _sourceVersion;
        try
        {
            using var snapshot = _source.Capture();
            if (version != _sourceVersion)
                throw new InvalidOperationException("A cached picture source changed during capture.");
            UpdateCore(snapshot.Picture, snapshot.Bounds, snapshot.RenderScale, snapshot.EnableClearType);
            _sourceDirty = false;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void OnSourceInvalidated(object? sender, EventArgs args)
    {
        if (!_disposed) Invalidate();
    }

    /// <summary>
    /// Replaces content and raster policy without changing shared source identity.
    /// Zero scale or an empty rectangle paints nothing. Invalid values fail before
    /// changing the previous source. Identical ownership clones are a no-op.
    /// </summary>
    public void Update(GpuPicture picture, Rect bounds, float renderScale = 1f)
        => Update(picture, bounds, renderScale, EnableClearType);

    public void Update(GpuPicture picture, Rect bounds, float renderScale, bool enableClearType)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source != null)
            throw new InvalidOperationException("A live cached picture is updated through its source and Refresh.");
        UpdateCore(picture, bounds, renderScale, enableClearType);
    }

    private void UpdateCore(GpuPicture picture, Rect bounds, float renderScale, bool enableClearType)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(picture);
        if (!float.IsFinite(bounds.X) || !float.IsFinite(bounds.Y) ||
            !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height) ||
            !float.IsFinite(bounds.X + bounds.Width) || !float.IsFinite(bounds.Y + bounds.Height) ||
            bounds.Width < 0 || bounds.Height < 0)
            throw new ArgumentOutOfRangeException(nameof(bounds));
        if (!float.IsFinite(renderScale) || renderScale < 0 ||
            !float.IsFinite(bounds.Width * renderScale) || !float.IsFinite(bounds.Height * renderScale))
            throw new ArgumentOutOfRangeException(nameof(renderScale));

        ObjectDisposedException.ThrowIf(picture.IsDisposed, picture);
        if (_picture?.SharesRetainedCommandStorageWith(picture) == true &&
            Bounds == bounds && RenderScale == renderScale && EnableClearType == enableClearType)
        {
            return;
        }

        // Acquire independent leases before changing live state. The clone
        // shares packed command/side-buffer storage rather than copying it.
        GpuPicture owned = picture.Clone();
        GpuPicture? previous = _picture;
        _picture = owned;
        Bounds = bounds;
        _visual.Commands.Clear();
        // The picture clone owns the leases; do not acquire a duplicate lease
        // collection just to retain this single command in our private context.
        _visual.Commands.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPicture,
            Picture = owned,
            Transform = Matrix4x4.CreateTranslation(-bounds.X, -bounds.Y, 0)
        });
        _visual.Offset = new Vector2(bounds.X, bounds.Y);
        _visual.Size = new Vector2(bounds.Width, bounds.Height);
        _visual.LayerCacheRenderScale = renderScale;
        _visual.EnableClearType = enableClearType;
        _visual.Invalidate();
        previous?.Dispose();
    }

    /// <summary>Invalidates pixels after a referenced mutable resource changes.</summary>
    public void Invalidate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source != null)
        {
            _sourceDirty = true;
            unchecked { _sourceVersion++; }
        }
        _visual.Invalidate();
    }

    internal Visual GetVisual()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _visual;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_source != null) _source.Invalidated -= OnSourceInvalidated;
        _visual.Commands.Clear();
        _visual.IsVisible = false;
        _visual.Invalidate();
        _picture?.Dispose();
        _picture = null;
        if (_ownsSource) _source!.Dispose();
    }

    private sealed class CacheVisual : Visual, IOwnedRenderCommandCache
    {
        internal readonly DrawingContext Commands = new();
        internal override bool RequiresLayerCache => true;
        internal bool EnableClearType = true;
        internal override bool? LayerCacheClearTypePolicy => EnableClearType;

        private readonly CachedPicture _owner;
        internal CacheVisual(CachedPicture owner)
        {
            _owner = owner;
            CacheAsLayer = true;
        }
        internal override void PrepareLayerCache()
        {
            if (!_owner._disposed) _owner.Refresh();
        }
        public DrawingContext GetOrUpdateRenderCommandCache() => Commands;
        public bool HasRenderCommands => Commands.Commands.Count != 0;
    }
}
