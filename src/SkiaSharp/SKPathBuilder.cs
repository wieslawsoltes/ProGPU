#pragma warning disable CS0618 // The builder delegates to the shim's official legacy SKPath contract.

namespace SkiaSharp;

public class SKPathBuilder : SKObject
{
    private SKPath _path;

    public SKPathBuilder()
        : base(SKObjectHandle.Create(), owns: true)
    {
        _path = new SKPath();
    }

    public SKPathBuilder(SKPath path)
        : base(SKObjectHandle.Create(), owns: true)
    {
        ArgumentNullException.ThrowIfNull(path);
        _path = new SKPath(path);
    }

    public SKPathFillType FillType
    {
        get => _path.FillType;
        set => _path.FillType = value;
    }

    internal bool IsEmpty => _path.IsEmpty;

    internal void ReplaceWith(SKPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var previous = _path;
        _path = path;
        previous.Dispose();
    }

    public SKPath Detach()
    {
        var path = _path;
        _path = new SKPath();
        return path;
    }

    public SKPath Snapshot() => new(_path);

    public void Reset() => _path.Reset();

    public void MoveTo(SKPoint point) => _path.MoveTo(point);

    public void MoveTo(float x, float y) => _path.MoveTo(x, y);

    public void RMoveTo(SKPoint point) => _path.RMoveTo(point);

    public void RMoveTo(float dx, float dy) => _path.RMoveTo(dx, dy);

    public void LineTo(SKPoint point) => _path.LineTo(point);

    public void LineTo(float x, float y) => _path.LineTo(x, y);

    public void RLineTo(SKPoint point) => _path.RLineTo(point);

    public void RLineTo(float dx, float dy) => _path.RLineTo(dx, dy);

    public void QuadTo(SKPoint point0, SKPoint point1) => _path.QuadTo(point0, point1);

    public void QuadTo(float x0, float y0, float x1, float y1) => _path.QuadTo(x0, y0, x1, y1);

    public void RQuadTo(SKPoint point0, SKPoint point1) => _path.RQuadTo(point0, point1);

    public void RQuadTo(float dx0, float dy0, float dx1, float dy1) =>
        _path.RQuadTo(dx0, dy0, dx1, dy1);

    public void ConicTo(SKPoint point0, SKPoint point1, float w) => _path.ConicTo(point0, point1, w);

    public void ConicTo(float x0, float y0, float x1, float y1, float w) =>
        _path.ConicTo(x0, y0, x1, y1, w);

    public void RConicTo(SKPoint point0, SKPoint point1, float w) => _path.RConicTo(point0, point1, w);

    public void RConicTo(float dx0, float dy0, float dx1, float dy1, float w) =>
        _path.RConicTo(dx0, dy0, dx1, dy1, w);

    public void CubicTo(SKPoint point0, SKPoint point1, SKPoint point2) =>
        _path.CubicTo(point0, point1, point2);

    public void CubicTo(float x0, float y0, float x1, float y1, float x2, float y2) =>
        _path.CubicTo(x0, y0, x1, y1, x2, y2);

    public void RCubicTo(SKPoint point0, SKPoint point1, SKPoint point2) =>
        _path.RCubicTo(point0, point1, point2);

    public void RCubicTo(float dx0, float dy0, float dx1, float dy1, float dx2, float dy2) =>
        _path.RCubicTo(dx0, dy0, dx1, dy1, dx2, dy2);

    public void ArcTo(SKPoint r, float xAxisRotate, SKPathArcSize largeArc, SKPathDirection sweep, SKPoint xy) =>
        _path.ArcTo(r, xAxisRotate, largeArc, sweep, xy);

    public void ArcTo(
        float rx,
        float ry,
        float xAxisRotate,
        SKPathArcSize largeArc,
        SKPathDirection sweep,
        float x,
        float y) =>
        _path.ArcTo(rx, ry, xAxisRotate, largeArc, sweep, x, y);

    public void ArcTo(SKRect oval, float startAngle, float sweepAngle, bool forceMoveTo) =>
        _path.ArcTo(oval, startAngle, sweepAngle, forceMoveTo);

    public void ArcTo(SKPoint point1, SKPoint point2, float radius) =>
        _path.ArcTo(point1, point2, radius);

    public void ArcTo(float x1, float y1, float x2, float y2, float radius) =>
        _path.ArcTo(x1, y1, x2, y2, radius);

    public void RArcTo(SKPoint r, float xAxisRotate, SKPathArcSize largeArc, SKPathDirection sweep, SKPoint xy) =>
        _path.RArcTo(r, xAxisRotate, largeArc, sweep, xy);

    public void RArcTo(
        float rx,
        float ry,
        float xAxisRotate,
        SKPathArcSize largeArc,
        SKPathDirection sweep,
        float x,
        float y) =>
        _path.RArcTo(rx, ry, xAxisRotate, largeArc, sweep, x, y);

    public void Close() => _path.Close();

    public void AddRect(SKRect rect, SKPathDirection direction = SKPathDirection.Clockwise) =>
        _path.AddRect(rect, direction);

    public void AddRect(SKRect rect, SKPathDirection direction, uint startIndex) =>
        _path.AddRect(rect, direction, startIndex);

    public void AddRoundRect(SKRoundRect rect, SKPathDirection direction = SKPathDirection.Clockwise) =>
        _path.AddRoundRect(rect, direction);

    public void AddRoundRect(SKRoundRect rect, SKPathDirection direction, uint startIndex) =>
        _path.AddRoundRect(rect, direction, startIndex);

    public void AddRoundRect(
        SKRect rect,
        float rx,
        float ry,
        SKPathDirection dir = SKPathDirection.Clockwise) =>
        _path.AddRoundRect(rect, rx, ry, dir);

    public void AddOval(SKRect rect, SKPathDirection direction = SKPathDirection.Clockwise) =>
        _path.AddOval(rect, direction);

    public void AddCircle(
        float x,
        float y,
        float radius,
        SKPathDirection dir = SKPathDirection.Clockwise) =>
        _path.AddCircle(x, y, radius, dir);

    public void AddArc(SKRect oval, float startAngle, float sweepAngle) =>
        _path.AddArc(oval, startAngle, sweepAngle);

    public void AddPoly(ReadOnlySpan<SKPoint> points, bool close = true) => _path.AddPoly(points, close);

    public void AddPoly(SKPoint[] points, bool close = true) => _path.AddPoly(points, close);

    public void AddPath(SKPath other, SKPathAddMode mode = SKPathAddMode.Append) => _path.AddPath(other, mode);

    public void AddPath(SKPath other, float dx, float dy, SKPathAddMode mode = SKPathAddMode.Append) =>
        _path.AddPath(other, dx, dy, mode);

    public void AddPath(SKPath other, in SKMatrix matrix, SKPathAddMode mode = SKPathAddMode.Append) =>
        _path.AddPath(other, matrix, mode);

    public void ReverseAddPath(SKPath other) => _path.AddPathReverse(other);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    protected override void DisposeNative()
    {
    }

    protected override void DisposeManaged()
    {
        _path.Dispose();
        base.DisposeManaged();
    }
}
