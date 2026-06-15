using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Vector;

namespace SkiaSharp;

public class SKPath : IDisposable
{
    private PathFigure? _currentFigure;
    private SKPathFillType _fillType = SKPathFillType.Winding;

    public PathGeometry Geometry { get; } = new();
    public SKPathFillType FillType
    {
        get => _fillType;
        set
        {
            _fillType = value;
            Geometry.FillRule = value is SKPathFillType.EvenOdd or SKPathFillType.InverseEvenOdd
                ? FillRule.EvenOdd
                : FillRule.Nonzero;
        }
    }

    public SKPath() { }

    public SKPath(SKPath source)
    {
        AddPath(source);
        FillType = source.FillType;
    }

    public SKRect Bounds
    {
        get
        {
            return Geometry.TryGetBounds(out var min, out var max)
                ? new SKRect(min.X, min.Y, max.X, max.Y)
                : SKRect.Empty;
        }
    }

    public SKRect TightBounds => Bounds;

    public bool IsEmpty => Geometry.Figures.Count == 0;

    private void EnsureFigure()
    {
        if (_currentFigure == null)
        {
            _currentFigure = new PathFigure(Vector2.Zero);
            Geometry.Figures.Add(_currentFigure);
        }
    }

    public void MoveTo(float x, float y)
    {
        _currentFigure = new PathFigure(new Vector2(x, y));
        Geometry.Figures.Add(_currentFigure);
    }

    public void MoveTo(SKPoint p) => MoveTo(p.X, p.Y);

    public void LineTo(float x, float y)
    {
        EnsureFigure();
        _currentFigure!.Segments.Add(new LineSegment(new Vector2(x, y)));
    }

    public void LineTo(SKPoint p) => LineTo(p.X, p.Y);

    public void QuadTo(float x0, float y0, float x1, float y1)
    {
        EnsureFigure();
        _currentFigure!.Segments.Add(new QuadraticBezierSegment(new Vector2(x0, y0), new Vector2(x1, y1)));
    }

    public void QuadTo(SKPoint p0, SKPoint p1) => QuadTo(p0.X, p0.Y, p1.X, p1.Y);

    public void CubicTo(float x0, float y0, float x1, float y1, float x2, float y2)
    {
        EnsureFigure();
        _currentFigure!.Segments.Add(new CubicBezierSegment(new Vector2(x0, y0), new Vector2(x1, y1), new Vector2(x2, y2)));
    }

    public void CubicTo(SKPoint p0, SKPoint p1, SKPoint p2) => CubicTo(p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y);

    public void ArcTo(float rx, float ry, float xAxisRotation, SKPathArcSize largeArc, SKPathDirection sweep, float x, float y)
    {
        EnsureFigure();
        var sd = sweep == SKPathDirection.Clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
        _currentFigure!.Segments.Add(new ArcSegment(new Vector2(x, y), new Vector2(rx, ry), xAxisRotation, largeArc == SKPathArcSize.Large, sd));
    }

    public void Close()
    {
        if (_currentFigure != null)
        {
            _currentFigure.IsClosed = true;
            _currentFigure = null;
        }
    }

    public void Reset()
    {
        Geometry.Figures.Clear();
        _currentFigure = null;
    }

    public void AddCircle(float x, float y, float radius, SKPathDirection direction = SKPathDirection.Clockwise)
    {
        MoveTo(x - radius, y);
        ArcTo(radius, radius, 0, SKPathArcSize.Large, direction, x + radius, y);
        ArcTo(radius, radius, 0, SKPathArcSize.Large, direction, x - radius, y);
        Close();
    }

    public void AddRect(SKRect rect, SKPathDirection direction = SKPathDirection.Clockwise)
    {
        if (direction == SKPathDirection.Clockwise)
        {
            MoveTo(rect.Left, rect.Top);
            LineTo(rect.Right, rect.Top);
            LineTo(rect.Right, rect.Bottom);
            LineTo(rect.Left, rect.Bottom);
        }
        else
        {
            MoveTo(rect.Left, rect.Top);
            LineTo(rect.Left, rect.Bottom);
            LineTo(rect.Right, rect.Bottom);
            LineTo(rect.Right, rect.Top);
        }
        Close();
    }

    public void AddRoundRect(SKRoundRect rect, SKPathDirection direction = SKPathDirection.Clockwise)
    {
        var r = rect.Rect;
        var radii = rect.CornerRadii;

        MoveTo(r.Left + radii[0].X, r.Top);
        if (direction == SKPathDirection.Clockwise)
        {
            LineTo(r.Right - radii[1].X, r.Top);
            ArcTo(radii[1].X, radii[1].Y, 0f, SKPathArcSize.Small, SKPathDirection.Clockwise, r.Right, r.Top + radii[1].Y);
            LineTo(r.Right, r.Bottom - radii[2].Y);
            ArcTo(radii[2].X, radii[2].Y, 0f, SKPathArcSize.Small, SKPathDirection.Clockwise, r.Right - radii[2].X, r.Bottom);
            LineTo(r.Left + radii[3].X, r.Bottom);
            ArcTo(radii[3].X, radii[3].Y, 0f, SKPathArcSize.Small, SKPathDirection.Clockwise, r.Left, r.Bottom - radii[3].Y);
            LineTo(r.Left, r.Top + radii[0].Y);
            ArcTo(radii[0].X, radii[0].Y, 0f, SKPathArcSize.Small, SKPathDirection.Clockwise, r.Left + radii[0].X, r.Top);
        }
        else
        {
            LineTo(r.Left, r.Top + radii[0].Y);
            ArcTo(radii[0].X, radii[0].Y, 0f, SKPathArcSize.Small, SKPathDirection.CounterClockwise, r.Left + radii[0].X, r.Top);
            LineTo(r.Left + radii[3].X, r.Bottom);
            ArcTo(radii[3].X, radii[3].Y, 0f, SKPathArcSize.Small, SKPathDirection.CounterClockwise, r.Left, r.Bottom - radii[3].Y);
            LineTo(r.Right - radii[2].X, r.Bottom);
            ArcTo(radii[2].X, radii[2].Y, 0f, SKPathArcSize.Small, SKPathDirection.CounterClockwise, r.Right, r.Bottom - radii[2].Y);
            LineTo(r.Right, r.Top + radii[1].Y);
            ArcTo(radii[1].X, radii[1].Y, 0f, SKPathArcSize.Small, SKPathDirection.CounterClockwise, r.Right - radii[1].X, r.Top);
        }
        Close();
    }

    public void AddRoundRect(SKRect rect, float rx, float ry, SKPathDirection direction = SKPathDirection.Clockwise)
    {
        AddRoundRect(new SKRoundRect(rect, rx, ry), direction);
    }

    public void AddPath(SKPath other)
    {
        foreach (var fig in other.Geometry.Figures)
        {
            var newFig = new PathFigure(fig.StartPoint, fig.IsClosed) { IsFilled = fig.IsFilled };
            newFig.Segments.AddRange(fig.Segments);
            Geometry.Figures.Add(newFig);
        }
        _currentFigure = null;
    }

    public void AddPath(SKPath other, float x, float y)
    {
        var offset = new Vector2(x, y);
        foreach (var fig in other.Geometry.Figures)
        {
            var newFig = new PathFigure(fig.StartPoint + offset, fig.IsClosed) { IsFilled = fig.IsFilled };
            foreach (var seg in fig.Segments)
            {
                if (seg is LineSegment line)
                {
                    newFig.Segments.Add(new LineSegment(line.Point + offset));
                }
                else if (seg is QuadraticBezierSegment quad)
                {
                    newFig.Segments.Add(new QuadraticBezierSegment(quad.ControlPoint + offset, quad.Point + offset));
                }
                else if (seg is CubicBezierSegment cubic)
                {
                    newFig.Segments.Add(new CubicBezierSegment(cubic.ControlPoint1 + offset, cubic.ControlPoint2 + offset, cubic.Point + offset));
                }
                else if (seg is ArcSegment arc)
                {
                    newFig.Segments.Add(new ArcSegment(arc.Point + offset, arc.Size, arc.RotationAngle, arc.IsLargeArc, arc.SweepDirection));
                }
            }
            Geometry.Figures.Add(newFig);
        }
        _currentFigure = null;
    }

    public void Transform(SKMatrix matrix)
    {
        var m = matrix.ToMatrix4x4();
        foreach (var fig in Geometry.Figures)
        {
            fig.StartPoint = Vector2.Transform(fig.StartPoint, m);
            for (int i = 0; i < fig.Segments.Count; i++)
            {
                var seg = fig.Segments[i];
                if (seg is LineSegment line)
                {
                    line.Point = Vector2.Transform(line.Point, m);
                }
                else if (seg is QuadraticBezierSegment quad)
                {
                    quad.ControlPoint = Vector2.Transform(quad.ControlPoint, m);
                    quad.Point = Vector2.Transform(quad.Point, m);
                }
                else if (seg is CubicBezierSegment cubic)
                {
                    cubic.ControlPoint1 = Vector2.Transform(cubic.ControlPoint1, m);
                    cubic.ControlPoint2 = Vector2.Transform(cubic.ControlPoint2, m);
                    cubic.Point = Vector2.Transform(cubic.Point, m);
                }
                else if (seg is ArcSegment arc)
                {
                    arc.Point = Vector2.Transform(arc.Point, m);
                }
            }
        }
        _currentFigure = null;
    }

    public SKPath Op(SKPath other, SKPathOp op)
    {
        var result = new SKPath();
        var solvedGeometry = PathOpGeometrySolver.Combine(this.Geometry, other.Geometry, (int)op);
        foreach (var fig in solvedGeometry.Figures)
        {
            result.Geometry.Figures.Add(fig);
        }
        return result;
    }

    public static SKPath Op(SKPath first, SKPath second, SKPathOp op)
    {
        return first.Op(second, op);
    }

    public static bool Op(SKPath first, SKPath second, SKPathOp op, SKPath result)
    {
        if (result == null) return false;
        result.Geometry.Figures.Clear();
        var solvedGeometry = PathOpGeometrySolver.Combine(first.Geometry, second.Geometry, (int)op);
        foreach (var fig in solvedGeometry.Figures)
        {
            result.Geometry.Figures.Add(fig);
        }
        return true;
    }


    public void Dispose() { }
}

public class SKRoundRect : IDisposable
{
    public SKRect Rect { get; private set; }
    public SKPoint[] CornerRadii { get; } = new SKPoint[4];

    public SKRoundRect()
    {
        Rect = SKRect.Empty;
    }

    public SKRoundRect(SKRect rect)
    {
        Rect = rect;
    }

    public SKRoundRect(SKRect rect, float rx, float ry)
    {
        Rect = rect;
        for (int i = 0; i < 4; i++)
        {
            CornerRadii[i] = new SKPoint(rx, ry);
        }
    }

    public void SetRectRadii(SKRect rect, SKPoint[] radii)
    {
        Rect = rect;
        Array.Copy(radii, CornerRadii, 4);
    }

    public void SetRect(SKRect rect)
    {
        Rect = rect;
    }

    public void Dispose() { }
}

public class SKRegion : IDisposable
{
    private SKPath? _path;
    private SKRectI _rect;

    public bool IsEmpty => (_path == null || _path.IsEmpty) && _rect.Width <= 0;

    public SKRectI Bounds => _rect;

    public SKRegion() { }

    public bool Contains(int x, int y)
    {
        return x >= _rect.Left && x < _rect.Right && y >= _rect.Top && y < _rect.Bottom;
    }

    public bool SetPath(SKPath path)
    {
        _path = path;
        var b = path.Bounds;
        _rect = new SKRectI((int)Math.Floor(b.Left), (int)Math.Floor(b.Top), (int)Math.Ceiling(b.Right), (int)Math.Ceiling(b.Bottom));
        return true;
    }

    public bool SetRect(SKRectI rect)
    {
        _rect = rect;
        _path = null;
        return true;
    }

    public bool Op(SKRectI rect, SKRegionOperation op)
    {
        if (op == SKRegionOperation.Replace)
        {
            _rect = rect;
            _path = null;
            return true;
        }
        
        // Approximate other ops by union-ing bounds
        _rect = new SKRectI(
            Math.Min(_rect.Left, rect.Left),
            Math.Min(_rect.Top, rect.Top),
            Math.Max(_rect.Right, rect.Right),
            Math.Max(_rect.Bottom, rect.Bottom)
        );
        return true;
    }

    public void Dispose() { }
}
