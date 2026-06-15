using System;
using System.Collections.Generic;
using System.Numerics;

namespace System.Windows.Media;

public abstract class StreamGeometryContext : IDisposable
{
    internal StreamGeometryContext() { }

    public abstract void BeginFigure(Point startPoint, bool isFilled, bool isClosed);
    public abstract void LineTo(Point point, bool isStroked, bool isSmoothJoin);
    public abstract void QuadraticBezierTo(Point point1, Point point2, bool isStroked, bool isSmoothJoin);
    public abstract void BezierTo(Point point1, Point point2, Point point3, bool isStroked, bool isSmoothJoin);
    public abstract void PolyLineTo(IList<Point> points, bool isStroked, bool isSmoothJoin);
    public abstract void PolyQuadraticBezierTo(IList<Point> points, bool isStroked, bool isSmoothJoin);
    public abstract void PolyBezierTo(IList<Point> points, bool isStroked, bool isSmoothJoin);
    public abstract void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc, SweepDirection sweepDirection, bool isStroked, bool isSmoothJoin);

    public virtual void Close()
    {
        DisposeCore();
    }

    void IDisposable.Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    protected virtual void DisposeCore() { }
    internal abstract void SetClosedState(bool closed);
}

internal class StreamGeometryContextImpl : StreamGeometryContext
{
    private readonly PathGeometry _pathGeometry;
    private PathFigure? _currentFigure;

    public StreamGeometryContextImpl(PathGeometry pathGeometry)
    {
        _pathGeometry = pathGeometry;
    }

    public override void BeginFigure(Point startPoint, bool isFilled, bool isClosed)
    {
        _currentFigure = new PathFigure
        {
            StartPoint = new Vector2((float)startPoint.X, (float)startPoint.Y),
            IsFilled = isFilled,
            IsClosed = isClosed
        };
        _pathGeometry.Figures.Add(_currentFigure);
    }

    public override void LineTo(Point point, bool isStroked, bool isSmoothJoin)
    {
        if (_currentFigure == null) throw new InvalidOperationException("No current figure.");
        _currentFigure.Segments.Add(new LineSegment(new Vector2((float)point.X, (float)point.Y), isSmoothJoin, isStroked));
    }

    public override void QuadraticBezierTo(Point point1, Point point2, bool isStroked, bool isSmoothJoin)
    {
        if (_currentFigure == null) throw new InvalidOperationException("No current figure.");
        _currentFigure.Segments.Add(new QuadraticBezierSegment(
            new Vector2((float)point1.X, (float)point1.Y),
            new Vector2((float)point2.X, (float)point2.Y),
            isSmoothJoin,
            isStroked));
    }

    public override void BezierTo(Point point1, Point point2, Point point3, bool isStroked, bool isSmoothJoin)
    {
        if (_currentFigure == null) throw new InvalidOperationException("No current figure.");
        _currentFigure.Segments.Add(new BezierSegment(
            new Vector2((float)point1.X, (float)point1.Y),
            new Vector2((float)point2.X, (float)point2.Y),
            new Vector2((float)point3.X, (float)point3.Y),
            isSmoothJoin,
            isStroked));
    }

    public override void PolyLineTo(IList<Point> points, bool isStroked, bool isSmoothJoin)
    {
        if (_currentFigure == null) throw new InvalidOperationException("No current figure.");
        foreach (var pt in points)
        {
            _currentFigure.Segments.Add(new LineSegment(new Vector2((float)pt.X, (float)pt.Y), isSmoothJoin, isStroked));
        }
    }

    public override void PolyQuadraticBezierTo(IList<Point> points, bool isStroked, bool isSmoothJoin)
    {
        if (_currentFigure == null) throw new InvalidOperationException("No current figure.");
        for (int i = 0; i < points.Count - 1; i += 2)
        {
            _currentFigure.Segments.Add(new QuadraticBezierSegment(
                new Vector2((float)points[i].X, (float)points[i].Y),
                new Vector2((float)points[i + 1].X, (float)points[i + 1].Y),
                isSmoothJoin,
                isStroked));
        }
    }

    public override void PolyBezierTo(IList<Point> points, bool isStroked, bool isSmoothJoin)
    {
        if (_currentFigure == null) throw new InvalidOperationException("No current figure.");
        for (int i = 0; i < points.Count - 2; i += 3)
        {
            _currentFigure.Segments.Add(new BezierSegment(
                new Vector2((float)points[i].X, (float)points[i].Y),
                new Vector2((float)points[i + 1].X, (float)points[i + 1].Y),
                new Vector2((float)points[i + 2].X, (float)points[i + 2].Y),
                isSmoothJoin,
                isStroked));
        }
    }

    public override void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc, SweepDirection sweepDirection, bool isStroked, bool isSmoothJoin)
    {
        if (_currentFigure == null) throw new InvalidOperationException("No current figure.");
        _currentFigure.Segments.Add(new ArcSegment
        {
            Point = new Vector2((float)point.X, (float)point.Y),
            Size = new Vector2((float)size.Width, (float)size.Height),
            RotationAngle = (float)rotationAngle,
            IsLargeArc = isLargeArc,
            SweepDirection = sweepDirection,
            IsSmoothJoin = isSmoothJoin,
            IsStroked = isStroked
        });
    }

    protected override void DisposeCore()
    {
        // No-op
    }

    internal override void SetClosedState(bool closed)
    {
        if (_currentFigure != null)
        {
            _currentFigure.IsClosed = closed;
        }
    }
}
