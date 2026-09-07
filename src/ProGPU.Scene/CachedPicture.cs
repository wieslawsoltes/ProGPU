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
    private readonly CacheVisual _visual = new();
    private GpuPicture? _picture;
    private bool _disposed;

    public CachedPicture(GpuPicture picture, Rect bounds, float renderScale = 1f)
    {
        Update(picture, bounds, renderScale);
    }

    public Rect Bounds { get; private set; }
    public float RenderScale => _visual.LayerCacheRenderScale;

    /// <summary>
    /// Replaces content and raster policy without changing shared source identity.
    /// Zero scale or an empty rectangle paints nothing. Invalid values fail before
    /// changing the previous source. Identical ownership clones are a no-op.
    /// </summary>
    public void Update(GpuPicture picture, Rect bounds, float renderScale = 1f)
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
            Bounds == bounds && RenderScale == renderScale)
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
        _visual.Invalidate();
        previous?.Dispose();
    }

    /// <summary>Invalidates pixels after a referenced mutable resource changes.</summary>
    public void Invalidate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        _visual.Commands.Clear();
        _visual.IsVisible = false;
        _visual.Invalidate();
        _picture?.Dispose();
        _picture = null;
    }

    private sealed class CacheVisual : Visual, IOwnedRenderCommandCache
    {
        internal readonly DrawingContext Commands = new();
        internal override bool RequiresLayerCache => true;

        internal CacheVisual() => CacheAsLayer = true;
        public DrawingContext GetOrUpdateRenderCommandCache() => Commands;
        public bool HasRenderCommands => Commands.Commands.Count != 0;
    }
}
