#pragma warning disable CS0618 // The builder delegates to the shim's official legacy SKPath contract.

using System.Runtime.CompilerServices;

namespace SkiaSharp;

public class SKPathBuilder : SKObject
{
    private PackedPathData? _packedPath;
    private SKPath? _path;
    private SKPathFillType _fillType = SKPathFillType.Winding;

    public SKPathBuilder()
        : base(SKObjectHandle.Create(), owns: true)
    {
        _packedPath = PackedPathData.Rent(trackTightBounds: false);
    }

    public SKPathBuilder(SKPath path)
        : base(SKObjectHandle.Create(), owns: true)
    {
        ArgumentNullException.ThrowIfNull(path);
        _path = new SKPath(path);
        _fillType = path.FillType;
    }

    public SKPathFillType FillType
    {
        get => _path?.FillType ?? _fillType;
        set
        {
            _fillType = value;
            if (_path is not null)
            {
                _path.FillType = value;
            }
        }
    }

    internal bool IsEmpty => _path?.IsEmpty ?? (_packedPath?.IsEmpty ?? true);

    internal void ReplaceWith(SKPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _packedPath?.Dispose();
        _packedPath = null;
        var previous = _path;
        _path = path;
        _fillType = path.FillType;
        previous?.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SKPath Detach()
    {
        SKPath path;
        if (_path is not null)
        {
            path = _path;
            _path = null;
        }
        else
        {
            path = new SKPath(TakePackedPath(), _fillType);
        }

        _fillType = SKPathFillType.Winding;
        return path;
    }

    public SKPath Snapshot() => _path is not null
        ? new SKPath(_path)
        : new SKPath(EnsurePackedPath().Clone(), _fillType);

    public void Reset()
    {
        if (_path is not null)
        {
            _path.Reset();
            _fillType = _path.FillType;
            return;
        }

        EnsurePackedPath().Reset();
        _fillType = SKPathFillType.Winding;
    }

    public void MoveTo(SKPoint point) => MoveTo(point.X, point.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MoveTo(float x, float y)
    {
        if (_packedPath is { } packed)
        {
            packed.MoveToBoundsOnly(x, y);
        }
        else if (_path is { } path)
        {
            path.MoveTo(x, y);
        }
        else
        {
            EnsurePackedPath().MoveToBoundsOnly(x, y);
        }
    }

    public void RMoveTo(SKPoint point) => RMoveTo(point.X, point.Y);

    public void RMoveTo(float dx, float dy)
    {
        if (_path is not null)
        {
            _path.RMoveTo(dx, dy);
            return;
        }

        var current = EnsurePackedPath().CurrentPoint;
        MoveTo(current.X + dx, current.Y + dy);
    }

    public void LineTo(SKPoint point) => LineTo(point.X, point.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LineTo(float x, float y)
    {
        if (_packedPath is { } packed)
        {
            packed.LineToBoundsOnly(x, y);
        }
        else if (_path is { } path)
        {
            path.LineTo(x, y);
        }
        else
        {
            EnsurePackedPath().LineToBoundsOnly(x, y);
        }
    }

    public void RLineTo(SKPoint point) => RLineTo(point.X, point.Y);

    public void RLineTo(float dx, float dy)
    {
        if (_path is not null)
        {
            _path.RLineTo(dx, dy);
            return;
        }

        var current = EnsurePackedPath().CurrentPoint;
        LineTo(current.X + dx, current.Y + dy);
    }

    public void QuadTo(SKPoint point0, SKPoint point1) =>
        QuadTo(point0.X, point0.Y, point1.X, point1.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void QuadTo(float x0, float y0, float x1, float y1)
    {
        if (_packedPath is { } packed)
        {
            packed.QuadToBoundsOnly(x0, y0, x1, y1);
        }
        else if (_path is { } path)
        {
            path.QuadTo(x0, y0, x1, y1);
        }
        else
        {
            EnsurePackedPath().QuadToBoundsOnly(x0, y0, x1, y1);
        }
    }

    public void RQuadTo(SKPoint point0, SKPoint point1) =>
        RQuadTo(point0.X, point0.Y, point1.X, point1.Y);

    public void RQuadTo(float dx0, float dy0, float dx1, float dy1)
    {
        if (_path is not null)
        {
            _path.RQuadTo(dx0, dy0, dx1, dy1);
            return;
        }

        var current = EnsurePackedPath().CurrentPoint;
        QuadTo(
            current.X + dx0,
            current.Y + dy0,
            current.X + dx1,
            current.Y + dy1);
    }

    public void ConicTo(SKPoint point0, SKPoint point1, float w) =>
        GetMutablePath().ConicTo(point0, point1, w);

    public void ConicTo(float x0, float y0, float x1, float y1, float w) =>
        GetMutablePath().ConicTo(x0, y0, x1, y1, w);

    public void RConicTo(SKPoint point0, SKPoint point1, float w) =>
        GetMutablePath().RConicTo(point0, point1, w);

    public void RConicTo(float dx0, float dy0, float dx1, float dy1, float w) =>
        GetMutablePath().RConicTo(dx0, dy0, dx1, dy1, w);

    public void CubicTo(SKPoint point0, SKPoint point1, SKPoint point2) =>
        CubicTo(point0.X, point0.Y, point1.X, point1.Y, point2.X, point2.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CubicTo(float x0, float y0, float x1, float y1, float x2, float y2)
    {
        if (_packedPath is { } packed)
        {
            packed.CubicToBoundsOnly(x0, y0, x1, y1, x2, y2);
        }
        else if (_path is { } path)
        {
            path.CubicTo(x0, y0, x1, y1, x2, y2);
        }
        else
        {
            EnsurePackedPath().CubicToBoundsOnly(x0, y0, x1, y1, x2, y2);
        }
    }

    public void RCubicTo(SKPoint point0, SKPoint point1, SKPoint point2) =>
        RCubicTo(point0.X, point0.Y, point1.X, point1.Y, point2.X, point2.Y);

    public void RCubicTo(float dx0, float dy0, float dx1, float dy1, float dx2, float dy2)
    {
        if (_path is not null)
        {
            _path.RCubicTo(dx0, dy0, dx1, dy1, dx2, dy2);
            return;
        }

        var current = EnsurePackedPath().CurrentPoint;
        CubicTo(
            current.X + dx0,
            current.Y + dy0,
            current.X + dx1,
            current.Y + dy1,
            current.X + dx2,
            current.Y + dy2);
    }

    public void ArcTo(SKPoint r, float xAxisRotate, SKPathArcSize largeArc, SKPathDirection sweep, SKPoint xy) =>
        GetMutablePath().ArcTo(r, xAxisRotate, largeArc, sweep, xy);

    public void ArcTo(
        float rx,
        float ry,
        float xAxisRotate,
        SKPathArcSize largeArc,
        SKPathDirection sweep,
        float x,
        float y) =>
        GetMutablePath().ArcTo(rx, ry, xAxisRotate, largeArc, sweep, x, y);

    public void ArcTo(SKRect oval, float startAngle, float sweepAngle, bool forceMoveTo) =>
        GetMutablePath().ArcTo(oval, startAngle, sweepAngle, forceMoveTo);

    public void ArcTo(SKPoint point1, SKPoint point2, float radius) =>
        GetMutablePath().ArcTo(point1, point2, radius);

    public void ArcTo(float x1, float y1, float x2, float y2, float radius) =>
        GetMutablePath().ArcTo(x1, y1, x2, y2, radius);

    public void RArcTo(SKPoint r, float xAxisRotate, SKPathArcSize largeArc, SKPathDirection sweep, SKPoint xy) =>
        GetMutablePath().RArcTo(r, xAxisRotate, largeArc, sweep, xy);

    public void RArcTo(
        float rx,
        float ry,
        float xAxisRotate,
        SKPathArcSize largeArc,
        SKPathDirection sweep,
        float x,
        float y) =>
        GetMutablePath().RArcTo(rx, ry, xAxisRotate, largeArc, sweep, x, y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Close()
    {
        if (_packedPath is { } packed)
        {
            packed.Close();
        }
        else if (_path is { } path)
        {
            path.Close();
        }
        else
        {
            EnsurePackedPath().Close();
        }
    }

    public void AddRect(SKRect rect, SKPathDirection direction = SKPathDirection.Clockwise) =>
        GetMutablePath().AddRect(rect, direction);

    public void AddRect(SKRect rect, SKPathDirection direction, uint startIndex) =>
        GetMutablePath().AddRect(rect, direction, startIndex);

    public void AddRoundRect(SKRoundRect rect, SKPathDirection direction = SKPathDirection.Clockwise) =>
        GetMutablePath().AddRoundRect(rect, direction);

    public void AddRoundRect(SKRoundRect rect, SKPathDirection direction, uint startIndex) =>
        GetMutablePath().AddRoundRect(rect, direction, startIndex);

    public void AddRoundRect(
        SKRect rect,
        float rx,
        float ry,
        SKPathDirection dir = SKPathDirection.Clockwise) =>
        GetMutablePath().AddRoundRect(rect, rx, ry, dir);

    public void AddOval(SKRect rect, SKPathDirection direction = SKPathDirection.Clockwise) =>
        GetMutablePath().AddOval(rect, direction);

    public void AddCircle(
        float x,
        float y,
        float radius,
        SKPathDirection dir = SKPathDirection.Clockwise) =>
        GetMutablePath().AddCircle(x, y, radius, dir);

    public void AddArc(SKRect oval, float startAngle, float sweepAngle) =>
        GetMutablePath().AddArc(oval, startAngle, sweepAngle);

    public void AddPoly(ReadOnlySpan<SKPoint> points, bool close = true) =>
        GetMutablePath().AddPoly(points, close);

    public void AddPoly(SKPoint[] points, bool close = true) =>
        GetMutablePath().AddPoly(points, close);

    public void AddPath(SKPath other, SKPathAddMode mode = SKPathAddMode.Append) =>
        GetMutablePath().AddPath(other, mode);

    public void AddPath(SKPath other, float dx, float dy, SKPathAddMode mode = SKPathAddMode.Append) =>
        GetMutablePath().AddPath(other, dx, dy, mode);

    public void AddPath(SKPath other, in SKMatrix matrix, SKPathAddMode mode = SKPathAddMode.Append) =>
        GetMutablePath().AddPath(other, matrix, mode);

    public void ReverseAddPath(SKPath other) => GetMutablePath().AddPathReverse(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PackedPathData EnsurePackedPath() =>
        _packedPath ??= PackedPathData.Rent(trackTightBounds: false);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PackedPathData TakePackedPath()
    {
        var packedPath = EnsurePackedPath();
        _packedPath = null;
        return packedPath;
    }

    private SKPath GetMutablePath()
    {
        if (_path is not null)
        {
            return _path;
        }

        _path = new SKPath(TakePackedPath(), _fillType);
        _ = _path.Geometry;
        return _path;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    protected override void DisposeNative()
    {
    }

    protected override void DisposeManaged()
    {
        _packedPath?.Dispose();
        _packedPath = null;
        _path?.Dispose();
        _path = null;
        base.DisposeManaged();
    }
}
