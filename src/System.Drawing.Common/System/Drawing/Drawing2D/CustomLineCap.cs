using System.Numerics;
using ProGPU.Vector;

namespace System.Drawing.Drawing2D;

public class CustomLineCap : MarshalByRefObject, ICloneable, IDisposable
{
    private readonly PathGeometry? _fillPath;
    private readonly PathGeometry? _strokePath;
    private LineCap _baseCap;
    private float _baseInset;
    private bool _disposed;
    private LineCap _strokeStartCap;
    private LineCap _strokeEndCap;
    private LineJoin _strokeJoin = LineJoin.Miter;
    private float _widthScale = 1f;

    public CustomLineCap(GraphicsPath? fillPath, GraphicsPath? strokePath)
        : this(fillPath, strokePath, LineCap.Flat, 0f)
    {
    }

    public CustomLineCap(GraphicsPath? fillPath, GraphicsPath? strokePath, LineCap baseCap)
        : this(fillPath, strokePath, baseCap, 0f)
    {
    }

    public CustomLineCap(
        GraphicsPath? fillPath,
        GraphicsPath? strokePath,
        LineCap baseCap,
        float baseInset)
    {
        ValidateFillPath(fillPath);
        _fillPath = Snapshot(fillPath);
        _strokePath = Snapshot(strokePath);
        _baseCap = NormalizeBaseCap(baseCap);
        _baseInset = baseInset;
    }

    internal CustomLineCap(CustomLineCap source)
    {
        _fillPath = CloneGeometry(source._fillPath);
        _strokePath = CloneGeometry(source._strokePath);
        _baseCap = source._baseCap;
        _baseInset = source._baseInset;
        _strokeStartCap = source._strokeStartCap;
        _strokeEndCap = source._strokeEndCap;
        _strokeJoin = source._strokeJoin;
        _widthScale = source._widthScale;
    }

    public LineCap BaseCap
    {
        get
        {
            ThrowIfDisposed();
            return _baseCap;
        }
        set
        {
            ThrowIfDisposed();
            if (!IsBaseCap(value))
            {
                throw new ArgumentException("Parameter is not valid.", nameof(value));
            }

            _baseCap = value;
        }
    }

    public float BaseInset
    {
        get
        {
            ThrowIfDisposed();
            return _baseInset;
        }
        set
        {
            ThrowIfDisposed();
            _baseInset = value;
        }
    }

    public LineJoin StrokeJoin
    {
        get
        {
            ThrowIfDisposed();
            return _strokeJoin;
        }
        set
        {
            ThrowIfDisposed();
            _strokeJoin = value;
        }
    }

    public float WidthScale
    {
        get
        {
            ThrowIfDisposed();
            return _widthScale;
        }
        set
        {
            ThrowIfDisposed();
            _widthScale = value;
        }
    }

    public object Clone()
    {
        ThrowIfDisposed();
        return CloneCore();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public void GetStrokeCaps(out LineCap startCap, out LineCap endCap)
    {
        ThrowIfDisposed();
        startCap = _strokeStartCap;
        endCap = _strokeEndCap;
    }

    public void SetStrokeCaps(LineCap startCap, LineCap endCap)
    {
        ThrowIfDisposed();
        if (!IsBaseCap(startCap) || !IsBaseCap(endCap))
        {
            throw new ArgumentException("Parameter is not valid.");
        }

        _strokeStartCap = startCap;
        _strokeEndCap = endCap;
    }

    protected virtual void Dispose(bool disposing) => _disposed = true;

    internal LineCap StrokeStartCap
    {
        get
        {
            ThrowIfDisposed();
            return _strokeStartCap;
        }
    }

    internal LineCap StrokeEndCap
    {
        get
        {
            ThrowIfDisposed();
            return _strokeEndCap;
        }
    }

    internal virtual PathGeometry? FillGeometry
    {
        get
        {
            ThrowIfDisposed();
            return _fillPath;
        }
    }

    internal virtual PathGeometry? StrokeGeometry
    {
        get
        {
            ThrowIfDisposed();
            return _strokePath;
        }
    }

    internal bool IsDisposed => _disposed;

    internal virtual CustomLineCap CloneCore() => new(this);

    private static PathGeometry? Snapshot(GraphicsPath? path)
        => path is null ? null : CloneGeometry(path.Geometry);

    private static PathGeometry? CloneGeometry(PathGeometry? geometry)
        => geometry?.CreateTransformed(Matrix4x4.Identity);

    private static void ValidateFillPath(GraphicsPath? fillPath)
    {
        if (fillPath is null || fillPath.PointCount == 0)
        {
            return;
        }

        PathGeometry geometry = fillPath.Geometry;
        bool crossesAxis = false;
        foreach (PathFigure figure in geometry.Figures)
        {
            Vector2 endpoint = figure.StartPoint;
            float minimumY = figure.StartPoint.Y;
            float maximumY = figure.StartPoint.Y;
            foreach (PathSegment segment in figure.Segments)
            {
                Vector2 point = segment switch
                {
                    LineSegment line => line.Point,
                    QuadraticBezierSegment quadratic => quadratic.Point,
                    CubicBezierSegment cubic => cubic.Point,
                    _ => figure.StartPoint,
                };
                endpoint = point;
                minimumY = MathF.Min(minimumY, point.Y);
                maximumY = MathF.Max(maximumY, point.Y);
            }

            if (!figure.IsClosed &&
                Vector2.DistanceSquared(endpoint, figure.StartPoint) > 0.00000001f)
            {
                throw new ArgumentException("The fill path must be closed.", nameof(fillPath));
            }

            crossesAxis |= minimumY <= 0f && maximumY >= 0f;
        }

        if (!crossesAxis)
        {
            throw new NotImplementedException("The fill path must intersect the local Y axis.");
        }
    }

    private static LineCap NormalizeBaseCap(LineCap cap) => IsBaseCap(cap) ? cap : LineCap.Flat;

    private static bool IsBaseCap(LineCap cap)
        => cap is LineCap.Flat or LineCap.Square or LineCap.Round or LineCap.Triangle;

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ArgumentException("Parameter is not valid.");
        }
    }
}
