using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Windows.Foundation.Metadata;
using Windows.Graphics;

namespace Microsoft.UI.Composition;

/// <summary>
/// An immutable snapshot of connected two-dimensional lines and curves.
/// </summary>
[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionPath : IGeometrySource2D
{
    private readonly CompositionPathData _data;

    public CompositionPath(IGeometrySource2D source)
    {
        ArgumentNullException.ThrowIfNull(source);
        PathGeometry geometry = source switch
        {
            CompositionPath path => path._data.Geometry,
            PathGeometry vectorPath => vectorPath,
            _ => throw new NotSupportedException(
                "The geometry source is not backed by a typed ProGPU " +
                "path provider.")
        };

        if (geometry.IsCombined)
        {
            throw new NotSupportedException(
                "Combined ProGPU paths require a typed Composition path " +
                "adapter with defined trim semantics.");
        }

        _data = new CompositionPathData(
            geometry.CreateTransformed(Matrix4x4.Identity));
    }

    internal CompositionPathData Data => _data;
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionPathGeometry : CompositionGeometry
{
    private CompositionPath? _path;
    private PathGeometry? _trimmedPath;
    private RenderCommandGeometryCache? _trimmedCache;

    internal CompositionPathGeometry(
        Compositor compositor,
        CompositionPath? path = null)
        : base(compositor)
    {
        _path = path;
    }

    public CompositionPath? Path
    {
        get => _path;
        set
        {
            ThrowIfDisposed();
            if (ReferenceEquals(_path, value))
                return;
            _path = value;
            _trimmedPath = null;
            _trimmedCache = null;
            NotifyOwnersChanged();
        }
    }

    internal override void Record(
        DrawingContext context,
        Brush? fill,
        Pen? stroke,
        Matrix4x4 transform)
    {
        if (_path is null)
            return;

        if (HasFullTrim)
        {
            context.DrawPath(
                fill,
                stroke,
                _path.Data.Geometry,
                transform,
                _path.Data.GeometryCache);
            return;
        }

        if (_trimmedPath is null)
        {
            _trimmedPath = _path.Data.CreateTrimmed(
                TrimOrigin,
                TrimLength);
        }
        _trimmedCache ??= RenderCommandGeometryCache.ForPath(
            _trimmedPath);
        context.DrawPath(
            fill,
            stroke,
            _trimmedPath,
            transform,
            _trimmedCache!);
    }

    internal override void OnTrimChanged()
    {
        _trimmedPath = null;
        _trimmedCache = null;
    }

    internal override PathGeometry? GetClipPath()
    {
        if (_path is null)
            return null;
        if (HasFullTrim)
            return _path.Data.Geometry;
        return _trimmedPath ??= _path.Data.CreateTrimmed(
            TrimOrigin,
            TrimLength);
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionRoundedRectangleGeometry : CompositionGeometry
{
    private Vector2 _cornerRadius;
    private Vector2 _offset;
    private Vector2 _size;
    private CompositionPathData? _pathData;
    private PathGeometry? _trimmedPath;
    private RenderCommandGeometryCache? _trimmedCache;

    internal CompositionRoundedRectangleGeometry(Compositor compositor)
        : base(compositor)
    {
    }

    public Vector2 CornerRadius
    {
        get => _cornerRadius;
        set
        {
            ThrowIfDisposed();
            ValidateFinite(value, nameof(value));
            if (value.X < 0f || value.Y < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_cornerRadius == value)
                return;
            _cornerRadius = value;
            InvalidateGeometry();
        }
    }

    public Vector2 Offset
    {
        get => _offset;
        set
        {
            ThrowIfDisposed();
            ValidateFinite(value, nameof(value));
            if (_offset == value)
                return;
            _offset = value;
            InvalidateGeometry();
        }
    }

    public Vector2 Size
    {
        get => _size;
        set
        {
            ThrowIfDisposed();
            ValidateFinite(value, nameof(value));
            if (value.X < 0f || value.Y < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_size == value)
                return;
            _size = value;
            InvalidateGeometry();
        }
    }

    internal override void Record(
        DrawingContext context,
        Brush? fill,
        Pen? stroke,
        Matrix4x4 transform)
    {
        if (_size.X <= 0f || _size.Y <= 0f)
            return;

        Vector2 radius = new(
            MathF.Min(_cornerRadius.X, _size.X * 0.5f),
            MathF.Min(_cornerRadius.Y, _size.Y * 0.5f));
        if (HasFullTrim)
        {
            context.DrawRoundedRectangle(
                fill,
                stroke,
                new Rect(
                    _offset.X,
                    _offset.Y,
                    _size.X,
                    _size.Y),
                radius.X,
                radius.Y,
                transform);
            return;
        }

        EnsurePathData();
        if (_trimmedPath is null)
        {
            _trimmedPath = _pathData!.CreateTrimmed(
                TrimOrigin,
                TrimLength);
        }
        _trimmedCache ??= RenderCommandGeometryCache.ForPath(
            _trimmedPath);
        context.DrawPath(
            fill,
            stroke,
            _trimmedPath,
            transform,
            _trimmedCache!);
    }

    internal override void OnTrimChanged()
    {
        _trimmedPath = null;
        _trimmedCache = null;
    }

    internal override PathGeometry GetClipPath()
    {
        EnsurePathData();
        if (HasFullTrim)
            return _pathData!.Geometry;
        return _trimmedPath ??= _pathData!.CreateTrimmed(
            TrimOrigin,
            TrimLength);
    }

    private void InvalidateGeometry()
    {
        _pathData = null;
        _trimmedPath = null;
        _trimmedCache = null;
        NotifyOwnersChanged();
    }

    private void EnsurePathData()
    {
        if (_pathData is not null)
            return;

        Vector2 radius = new(
            MathF.Min(_cornerRadius.X, _size.X * 0.5f),
            MathF.Min(_cornerRadius.Y, _size.Y * 0.5f));
        _pathData = new CompositionPathData(
            PrimitivePathGeometry.CreateRoundedRectangle(
                _offset.X,
                _offset.Y,
                _size.X,
                _size.Y,
                radius.X,
                radius.Y));
    }
}
