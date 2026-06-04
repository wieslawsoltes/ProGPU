using ProGPU.Vector;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace System.Drawing.Drawing2D;

public enum FillMode
{
    Alternate = 0,
    Winding = 1
}

public class GraphicsPath : IDisposable
{
    private readonly PathGeometry _geometry = new();
    private PathFigure? _currentFigure;
    private PointF _lastPoint;

    public FillMode FillMode { get; set; } = FillMode.Alternate;
    internal PathGeometry Geometry => _geometry;

    public GraphicsPath() {}

    public GraphicsPath(FillMode fillMode)
    {
        FillMode = fillMode;
    }

    public void Reset()
    {
        _geometry.Figures.Clear();
        _currentFigure = null;
        _lastPoint = default;
    }

    public void StartFigure()
    {
        _currentFigure = null;
    }

    public void CloseFigure()
    {
        if (_currentFigure != null)
        {
            _currentFigure.IsClosed = true;
            _currentFigure = null;
        }
    }

    private void ConnectOrStart(PointF pt)
    {
        if (_currentFigure == null)
        {
            _currentFigure = new PathFigure(new Vector2(pt.X, pt.Y));
            _geometry.Figures.Add(_currentFigure);
        }
        else
        {
            var last = _lastPoint;
            if (Math.Abs(last.X - pt.X) > 1e-5f || Math.Abs(last.Y - pt.Y) > 1e-5f)
            {
                _currentFigure.Segments.Add(new LineSegment(new Vector2(pt.X, pt.Y)));
            }
        }
        _lastPoint = pt;
    }

    public void AddLine(PointF pt1, PointF pt2) => AddLine(pt1.X, pt1.Y, pt2.X, pt2.Y);
    public void AddLine(Point pt1, Point pt2) => AddLine((float)pt1.X, pt1.Y, pt2.X, pt2.Y);
    public void AddLine(int x1, int y1, int x2, int y2) => AddLine((float)x1, y1, x2, y2);

    public void AddLine(float x1, float y1, float x2, float y2)
    {
        ConnectOrStart(new PointF(x1, y1));
        var end = new PointF(x2, y2);
        _currentFigure!.Segments.Add(new LineSegment(new Vector2(x2, y2)));
        _lastPoint = end;
    }

    public void AddLines(PointF[] points)
    {
        if (points == null || points.Length < 2) return;
        ConnectOrStart(points[0]);
        for (int i = 1; i < points.Length; i++)
        {
            _currentFigure!.Segments.Add(new LineSegment(new Vector2(points[i].X, points[i].Y)));
            _lastPoint = points[i];
        }
    }

    public void AddLines(Point[] points)
    {
        if (points == null || points.Length < 2) return;
        ConnectOrStart(points[0]);
        for (int i = 1; i < points.Length; i++)
        {
            _currentFigure!.Segments.Add(new LineSegment(new Vector2(points[i].X, points[i].Y)));
            _lastPoint = points[i];
        }
    }

    public void AddRectangle(RectangleF rect)
    {
        StartFigure();
        _currentFigure = new PathFigure(new Vector2(rect.X, rect.Y));
        _geometry.Figures.Add(_currentFigure);
        _currentFigure.Segments.Add(new LineSegment(new Vector2(rect.Right, rect.Y)));
        _currentFigure.Segments.Add(new LineSegment(new Vector2(rect.Right, rect.Bottom)));
        _currentFigure.Segments.Add(new LineSegment(new Vector2(rect.X, rect.Bottom)));
        _currentFigure.IsClosed = true;
        _lastPoint = new PointF(rect.X, rect.Y);
        _currentFigure = null;
    }

    public void AddRectangle(Rectangle rect) => AddRectangle((RectangleF)rect);

    public void AddRectangles(RectangleF[] rects)
    {
        foreach (var r in rects) AddRectangle(r);
    }

    public void AddRectangles(Rectangle[] rects)
    {
        foreach (var r in rects) AddRectangle(r);
    }

    public void AddEllipse(RectangleF rect)
    {
        StartFigure();
        float rx = rect.Width / 2f;
        float ry = rect.Height / 2f;
        float cx = rect.X + rx;
        float cy = rect.Y + ry;
        _currentFigure = new PathFigure(new Vector2(cx - rx, cy));
        _geometry.Figures.Add(_currentFigure);
        _currentFigure.Segments.Add(new ArcSegment(
            new Vector2(cx + rx, cy),
            new Vector2(rx, ry),
            0f, false, SweepDirection.Clockwise
        ));
        _currentFigure.Segments.Add(new ArcSegment(
            new Vector2(cx - rx, cy),
            new Vector2(rx, ry),
            0f, false, SweepDirection.Clockwise
        ));
        _currentFigure.IsClosed = true;
        _lastPoint = new PointF(cx - rx, cy);
        _currentFigure = null;
    }

    public void AddEllipse(Rectangle rect) => AddEllipse((RectangleF)rect);
    public void AddEllipse(float x, float y, float width, float height) => AddEllipse(new RectangleF(x, y, width, height));
    public void AddEllipse(int x, int y, int width, int height) => AddEllipse(new RectangleF(x, y, width, height));

    public void AddArc(RectangleF rect, float startAngle, float sweepAngle) => AddArc(rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);
    public void AddArc(Rectangle rect, float startAngle, float sweepAngle) => AddArc(rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);
    public void AddArc(int x, int y, int width, int height, float startAngle, float sweepAngle) => AddArc((float)x, y, width, height, startAngle, sweepAngle);

    public void AddArc(float x, float y, float width, float height, float startAngle, float sweepAngle)
    {
        float rx = width / 2f;
        float ry = height / 2f;
        float cx = x + rx;
        float cy = y + ry;

        double startRad = startAngle * Math.PI / 180.0;
        double endRad = (startAngle + sweepAngle) * Math.PI / 180.0;

        float sx = cx + rx * (float)Math.Cos(startRad);
        float sy = cy + ry * (float)Math.Sin(startRad);

        float ex = cx + rx * (float)Math.Cos(endRad);
        float ey = cy + ry * (float)Math.Sin(endRad);

        ConnectOrStart(new PointF(sx, sy));

        bool isLargeArc = Math.Abs(sweepAngle) > 180.0;
        SweepDirection sweepDir = sweepAngle > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;

        _currentFigure!.Segments.Add(new ArcSegment(
            new Vector2(ex, ey),
            new Vector2(rx, ry),
            0f, isLargeArc, sweepDir
        ));
        _lastPoint = new PointF(ex, ey);
    }

    public void AddBezier(PointF pt1, PointF pt2, PointF pt3, PointF pt4) => AddBezier(pt1.X, pt1.Y, pt2.X, pt2.Y, pt3.X, pt3.Y, pt4.X, pt4.Y);
    public void AddBezier(Point pt1, Point pt2, Point pt3, Point pt4) => AddBezier((float)pt1.X, pt1.Y, pt2.X, pt2.Y, pt3.X, pt3.Y, pt4.X, pt4.Y);
    public void AddBezier(int x1, int y1, int x2, int y2, int x3, int y3, int x4, int y4) => AddBezier((float)x1, y1, x2, y2, x3, y3, x4, y4);

    public void AddBezier(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4)
    {
        ConnectOrStart(new PointF(x1, y1));
        _currentFigure!.Segments.Add(new CubicBezierSegment(
            new Vector2(x2, y2),
            new Vector2(x3, y3),
            new Vector2(x4, y4)
        ));
        _lastPoint = new PointF(x4, y4);
    }

    public void AddBeziers(PointF[] points)
    {
        if (points == null || points.Length < 4) return;
        ConnectOrStart(points[0]);
        for (int i = 1; i < points.Length; i += 3)
        {
            if (i + 2 >= points.Length) break;
            _currentFigure!.Segments.Add(new CubicBezierSegment(
                new Vector2(points[i].X, points[i].Y),
                new Vector2(points[i + 1].X, points[i + 1].Y),
                new Vector2(points[i + 2].X, points[i + 2].Y)
            ));
            _lastPoint = points[i + 2];
        }
    }

    public void AddBeziers(Point[] points)
    {
        if (points == null || points.Length < 4) return;
        ConnectOrStart(points[0]);
        for (int i = 1; i < points.Length; i += 3)
        {
            if (i + 2 >= points.Length) break;
            _currentFigure!.Segments.Add(new CubicBezierSegment(
                new Vector2(points[i].X, points[i].Y),
                new Vector2(points[i + 1].X, points[i + 1].Y),
                new Vector2(points[i + 2].X, points[i + 2].Y)
            ));
            _lastPoint = points[i + 2];
        }
    }

    public void AddPolygon(PointF[] points)
    {
        if (points == null || points.Length < 3) return;
        StartFigure();
        _currentFigure = new PathFigure(new Vector2(points[0].X, points[0].Y));
        _geometry.Figures.Add(_currentFigure);
        for (int i = 1; i < points.Length; i++)
        {
            _currentFigure.Segments.Add(new LineSegment(new Vector2(points[i].X, points[i].Y)));
        }
        _currentFigure.IsClosed = true;
        _lastPoint = points[0];
        _currentFigure = null;
    }

    public void AddPolygon(Point[] points)
    {
        if (points == null || points.Length < 3) return;
        StartFigure();
        _currentFigure = new PathFigure(new Vector2(points[0].X, points[0].Y));
        _geometry.Figures.Add(_currentFigure);
        for (int i = 1; i < points.Length; i++)
        {
            _currentFigure.Segments.Add(new LineSegment(new Vector2(points[i].X, points[i].Y)));
        }
        _currentFigure.IsClosed = true;
        _lastPoint = points[0];
        _currentFigure = null;
    }

    public void Dispose() {}
}
