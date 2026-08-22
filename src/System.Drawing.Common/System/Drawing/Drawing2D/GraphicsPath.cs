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

public sealed class GraphicsPath : MarshalByRefObject, ICloneable, IDisposable
{
    private PathGeometry _geometry = new();
    private PathFigure? _currentFigure;
    private PointF _lastPoint;
    private FillMode _fillMode;
    private readonly HashSet<object> _markers = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    public FillMode FillMode
    {
        get
        {
            ThrowIfDisposed();
            return _fillMode;
        }
        set
        {
            ThrowIfDisposed();
            if (value is not System.Drawing.Drawing2D.FillMode.Alternate and not System.Drawing.Drawing2D.FillMode.Winding)
            {
                throw new ArgumentException("Parameter is not valid.", nameof(value));
            }

            _fillMode = value;
            _geometry.FillRule = value == System.Drawing.Drawing2D.FillMode.Alternate
                ? FillRule.EvenOdd
                : FillRule.Nonzero;
        }
    }

    internal PathGeometry Geometry
    {
        get
        {
            ThrowIfDisposed();
            return _geometry;
        }
    }

    public GraphicsPath()
    {
        FillMode = System.Drawing.Drawing2D.FillMode.Alternate;
    }

    public GraphicsPath(FillMode fillMode)
    {
        FillMode = fillMode;
    }

    public GraphicsPath(Point[] pts, byte[] types)
        : this(pts, types, System.Drawing.Drawing2D.FillMode.Alternate)
    {
    }

    public GraphicsPath(Point[] pts, byte[] types, FillMode fillMode)
        : this(ConvertPoints(pts), types, fillMode)
    {
    }

    public GraphicsPath(PointF[] pts, byte[] types)
        : this(pts, types, System.Drawing.Drawing2D.FillMode.Alternate)
    {
    }

    public GraphicsPath(PointF[] pts, byte[] types, FillMode fillMode)
        : this((ReadOnlySpan<PointF>)(pts ?? throw new ArgumentNullException(nameof(pts))), types, fillMode)
    {
    }

    public GraphicsPath(ReadOnlySpan<Point> pts, ReadOnlySpan<byte> types, FillMode fillMode)
        : this(ConvertPoints(pts), types, fillMode)
    {
    }

    public GraphicsPath(ReadOnlySpan<PointF> pts, ReadOnlySpan<byte> types, FillMode fillMode)
    {
        FillMode = fillMode;
        InitializeFromPathData(pts, types);
    }

    public void Reset()
    {
        ThrowIfDisposed();
        _geometry.Figures.Clear();
        _markers.Clear();
        _currentFigure = null;
        _lastPoint = default;
        FillMode = System.Drawing.Drawing2D.FillMode.Alternate;
    }

    public void StartFigure()
    {
        ThrowIfDisposed();
        _currentFigure = null;
    }

    public void CloseFigure()
    {
        ThrowIfDisposed();
        if (_currentFigure != null)
        {
            _currentFigure.IsClosed = true;
            _currentFigure = null;
        }
    }

    public void CloseAllFigures()
    {
        ThrowIfDisposed();
        foreach (PathFigure figure in _geometry.Figures)
        {
            figure.IsClosed = true;
        }

        _currentFigure = null;
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
        ArgumentNullException.ThrowIfNull(points);
        AddLines((ReadOnlySpan<PointF>)points);
    }

    public void AddLines(ReadOnlySpan<PointF> points)
    {
        ThrowIfDisposed();
        if (points.Length < 2) throw new ArgumentException("Parameter is not valid.", nameof(points));
        ConnectOrStart(points[0]);
        for (int i = 1; i < points.Length; i++)
        {
            _currentFigure!.Segments.Add(new LineSegment(new Vector2(points[i].X, points[i].Y)));
            _lastPoint = points[i];
        }
    }

    public void AddLines(Point[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddLines((ReadOnlySpan<Point>)points);
    }

    public void AddLines(ReadOnlySpan<Point> points)
    {
        ThrowIfDisposed();
        if (points.Length < 2) throw new ArgumentException("Parameter is not valid.", nameof(points));
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
        _lastPoint = new PointF(rect.X, rect.Bottom);
        _currentFigure = null;
    }

    public void AddRectangle(Rectangle rect) => AddRectangle((RectangleF)rect);

    public void AddRectangles(RectangleF[] rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        AddRectangles((ReadOnlySpan<RectangleF>)rects);
    }

    public void AddRectangles(ReadOnlySpan<RectangleF> rects)
    {
        ThrowIfDisposed();
        foreach (RectangleF rectangle in rects) AddRectangle(rectangle);
    }

    public void AddRectangles(Rectangle[] rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        AddRectangles((ReadOnlySpan<Rectangle>)rects);
    }

    public void AddRectangles(ReadOnlySpan<Rectangle> rects)
    {
        ThrowIfDisposed();
        foreach (Rectangle rectangle in rects) AddRectangle(rectangle);
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

    public void AddRoundedRectangle(Rectangle rect, Size radius) =>
        AddRoundedRectangle((RectangleF)rect, new SizeF(radius.Width, radius.Height));

    public void AddRoundedRectangle(RectangleF rect, SizeF radius)
    {
        float diameterX = MathF.Min(MathF.Abs(radius.Width), MathF.Abs(rect.Width));
        float diameterY = MathF.Min(MathF.Abs(radius.Height), MathF.Abs(rect.Height));
        if (diameterX <= 0f || diameterY <= 0f)
        {
            AddRectangle(rect);
            return;
        }

        StartFigure();
        AddArc(rect.X, rect.Y, diameterX, diameterY, 180f, 90f);
        AddArc(rect.Right - diameterX, rect.Y, diameterX, diameterY, 270f, 90f);
        AddArc(rect.Right - diameterX, rect.Bottom - diameterY, diameterX, diameterY, 0f, 90f);
        AddArc(rect.X, rect.Bottom - diameterY, diameterX, diameterY, 90f, 90f);
        CloseFigure();
    }

    public void AddPie(Rectangle rect, float startAngle, float sweepAngle) =>
        AddPie(rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    public void AddPie(int x, int y, int width, int height, float startAngle, float sweepAngle) =>
        AddPie((float)x, y, width, height, startAngle, sweepAngle);

    public void AddPie(float x, float y, float width, float height, float startAngle, float sweepAngle)
    {
        StartFigure();
        float centerX = x + (width / 2f);
        float centerY = y + (height / 2f);
        double startRadians = startAngle * Math.PI / 180d;
        PointF start = new(
            centerX + ((width / 2f) * (float)Math.Cos(startRadians)),
            centerY + ((height / 2f) * (float)Math.Sin(startRadians)));
        AddLine(centerX, centerY, start.X, start.Y);
        AddArc(x, y, width, height, startAngle, sweepAngle);
        AddLine(_lastPoint.X, _lastPoint.Y, centerX, centerY);
        CloseFigure();
    }

    public void AddArc(RectangleF rect, float startAngle, float sweepAngle) => AddArc(rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);
    public void AddArc(Rectangle rect, float startAngle, float sweepAngle) => AddArc(rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);
    public void AddArc(int x, int y, int width, int height, float startAngle, float sweepAngle) => AddArc((float)x, y, width, height, startAngle, sweepAngle);

    public void AddArc(float x, float y, float width, float height, float startAngle, float sweepAngle)
    {
        if (Math.Abs(sweepAngle) >= 360.0f)
        {
            var halfSweep = sweepAngle >= 0f ? 180.0f : -180.0f;
            AddArc(x, y, width, height, startAngle, halfSweep);
            AddArc(x, y, width, height, startAngle + halfSweep, halfSweep);
            return;
        }

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
        ArgumentNullException.ThrowIfNull(points);
        AddBeziers((ReadOnlySpan<PointF>)points);
    }

    public void AddBeziers(ReadOnlySpan<PointF> points)
    {
        ThrowIfDisposed();
        if (points.Length < 4 || (points.Length - 1) % 3 != 0)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(points));
        }
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
        ArgumentNullException.ThrowIfNull(points);
        AddBeziers((ReadOnlySpan<Point>)points);
    }

    public void AddBeziers(ReadOnlySpan<Point> points)
    {
        ThrowIfDisposed();
        if (points.Length < 4 || (points.Length - 1) % 3 != 0)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(points));
        }
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
        ArgumentNullException.ThrowIfNull(points);
        AddPolygon((ReadOnlySpan<PointF>)points);
    }

    public void AddPolygon(ReadOnlySpan<PointF> points)
    {
        ThrowIfDisposed();
        if (points.Length < 3) throw new ArgumentException("Parameter is not valid.", nameof(points));
        StartFigure();
        _currentFigure = new PathFigure(new Vector2(points[0].X, points[0].Y));
        _geometry.Figures.Add(_currentFigure);
        for (int i = 1; i < points.Length; i++)
        {
            _currentFigure.Segments.Add(new LineSegment(new Vector2(points[i].X, points[i].Y)));
        }
        _currentFigure.IsClosed = true;
        _lastPoint = points[^1];
        _currentFigure = null;
    }

    public void AddPolygon(Point[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddPolygon((ReadOnlySpan<Point>)points);
    }

    public void AddPolygon(ReadOnlySpan<Point> points)
    {
        ThrowIfDisposed();
        if (points.Length < 3) throw new ArgumentException("Parameter is not valid.", nameof(points));
        StartFigure();
        _currentFigure = new PathFigure(new Vector2(points[0].X, points[0].Y));
        _geometry.Figures.Add(_currentFigure);
        for (int i = 1; i < points.Length; i++)
        {
            _currentFigure.Segments.Add(new LineSegment(new Vector2(points[i].X, points[i].Y)));
        }
        _currentFigure.IsClosed = true;
        _lastPoint = points[^1];
        _currentFigure = null;
    }

    public void AddCurve(Point[] points) => AddCurve(points, 0.5f);

    public void AddCurve(Point[] points, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddCurve((ReadOnlySpan<Point>)points, tension);
    }

    public void AddCurve(Point[] points, int offset, int numberOfSegments, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (offset < 0 || numberOfSegments < 1 || offset + numberOfSegments >= points.Length)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(points));
        }

        PointF[] converted = ConvertPoints(points);
        AddOpenCurve(converted, offset, numberOfSegments, tension);
    }

    public void AddCurve(ReadOnlySpan<Point> points) => AddCurve(points, 0.5f);

    public void AddCurve(ReadOnlySpan<Point> points, float tension) =>
        AddOpenCurve(ConvertPoints(points), 0, points.Length - 1, tension);

    public void AddCurve(PointF[] points) => AddCurve(points, 0.5f);

    public void AddCurve(PointF[] points, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddCurve((ReadOnlySpan<PointF>)points, tension);
    }

    public void AddCurve(PointF[] points, int offset, int numberOfSegments, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddOpenCurve(points, offset, numberOfSegments, tension);
    }

    public void AddCurve(ReadOnlySpan<PointF> points) => AddCurve(points, 0.5f);

    public void AddCurve(ReadOnlySpan<PointF> points, float tension) =>
        AddOpenCurve(points, 0, points.Length - 1, tension);

    public void AddClosedCurve(Point[] points) => AddClosedCurve(points, 0.5f);

    public void AddClosedCurve(Point[] points, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddClosedCurve((ReadOnlySpan<Point>)points, tension);
    }

    public void AddClosedCurve(ReadOnlySpan<Point> points) => AddClosedCurve(points, 0.5f);

    public void AddClosedCurve(ReadOnlySpan<Point> points, float tension) =>
        AddClosedCurveCore(ConvertPoints(points), tension);

    public void AddClosedCurve(PointF[] points) => AddClosedCurve(points, 0.5f);

    public void AddClosedCurve(PointF[] points, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddClosedCurve((ReadOnlySpan<PointF>)points, tension);
    }

    public void AddClosedCurve(ReadOnlySpan<PointF> points) => AddClosedCurve(points, 0.5f);

    public void AddClosedCurve(ReadOnlySpan<PointF> points, float tension) =>
        AddClosedCurveCore(points, tension);

    public void AddPath(GraphicsPath addingPath, bool connect)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(addingPath);
        addingPath.ThrowIfDisposed();

        bool first = true;
        foreach (PathFigure source in addingPath._geometry.Figures)
        {
            if (connect && first && _currentFigure != null)
            {
                ConnectOrStart(ToPointF(source.StartPoint));
                CopySegments(source, _currentFigure, addingPath._markers, _markers);
                _currentFigure.IsClosed = source.IsClosed;
                _lastPoint = GetFigureLastPoint(_currentFigure);
                if (source.IsClosed) _currentFigure = null;
            }
            else
            {
                PathFigure copy = CloneFigure(source, addingPath._markers, _markers);
                _geometry.Figures.Add(copy);
                _lastPoint = GetFigureLastPoint(copy);
                _currentFigure = copy.IsClosed ? null : copy;
            }

            first = false;
        }
    }

    public void SetMarkers()
    {
        ThrowIfDisposed();
        object? endpoint = GetCurrentEndpointObject();
        if (endpoint != null) _markers.Add(endpoint);
    }

    public void ClearMarkers()
    {
        ThrowIfDisposed();
        _markers.Clear();
    }

    public object Clone()
    {
        ThrowIfDisposed();
        var clone = new GraphicsPath(FillMode);
        clone.AddPath(this, connect: false);
        return clone;
    }

    public int PointCount
    {
        get
        {
            ThrowIfDisposed();
            int count = 0;
            foreach (PathFigure figure in _geometry.Figures)
            {
                count++;
                foreach (PathSegment segment in figure.Segments)
                {
                    count += segment switch
                    {
                        LineSegment => 1,
                        QuadraticBezierSegment => 3,
                        CubicBezierSegment => 3,
                        ArcSegment arc => GetArcCubicCount(figure, segment, arc),
                        _ => 0
                    };
                }
            }

            return count;
        }
    }

    public PointF[] PathPoints
    {
        get
        {
            PointF[] points = new PointF[PointCount];
            GetPathPoints(points);
            return points;
        }
    }

    public byte[] PathTypes
    {
        get
        {
            byte[] types = new byte[PointCount];
            GetPathTypes(types);
            return types;
        }
    }

    public PathData PathData => new() { Points = PathPoints, Types = PathTypes };

    public int GetPathPoints(Span<PointF> destination)
    {
        ThrowIfDisposed();
        int count = PointCount;
        if (destination.Length < count) throw new ArgumentException("Destination is too short.", nameof(destination));
        WritePathData(destination, default, writePoints: true, writeTypes: false);
        return count;
    }

    public int GetPathTypes(Span<byte> destination)
    {
        ThrowIfDisposed();
        int count = PointCount;
        if (destination.Length < count) throw new ArgumentException("Destination is too short.", nameof(destination));
        WritePathData(default, destination, writePoints: false, writeTypes: true);
        return count;
    }

    public RectangleF GetBounds() => GetBounds(null, null);

    public RectangleF GetBounds(Matrix? matrix) => GetBounds(matrix, null);

    public RectangleF GetBounds(Matrix? matrix, Pen? pen)
    {
        ThrowIfDisposed();
        PathGeometry geometry = matrix == null
            ? _geometry
            : _geometry.CreateTransformed(ToMatrix4x4(matrix.MatrixElements));
        if (!geometry.TryGetBounds(out Vector2 min, out Vector2 max)) return RectangleF.Empty;

        float inflate = pen == null ? 0f : MathF.Abs(pen.Width) * 0.5f;
        return RectangleF.FromLTRB(min.X - inflate, min.Y - inflate, max.X + inflate, max.Y + inflate);
    }

    public PointF GetLastPoint()
    {
        ThrowIfDisposed();
        if (_geometry.Figures.Count == 0) throw new ArgumentException("Parameter is not valid.");
        return _lastPoint;
    }

    public bool IsVisible(Point point) => IsVisible(point.X, point.Y, null);
    public bool IsVisible(Point point, Graphics? graphics) => IsVisible(point.X, point.Y, graphics);
    public bool IsVisible(PointF point) => IsVisible(point.X, point.Y, null);
    public bool IsVisible(PointF point, Graphics? graphics) => IsVisible(point.X, point.Y, graphics);
    public bool IsVisible(int x, int y) => IsVisible((float)x, y, null);
    public bool IsVisible(int x, int y, Graphics? graphics) => IsVisible((float)x, y, graphics);
    public bool IsVisible(float x, float y) => IsVisible(x, y, null);

    public bool IsVisible(float x, float y, Graphics? graphics)
    {
        ThrowIfDisposed();
        return PathGeometryHitTesting.TryContainsFill(
            _geometry,
            new Vector2(x, y),
            tolerance: 0f,
            relativeTolerance: false,
            out bool contains) && contains;
    }

    public bool IsOutlineVisible(Point point, Pen pen) => IsOutlineVisible(point.X, point.Y, pen, null);
    public bool IsOutlineVisible(Point point, Pen pen, Graphics? graphics) => IsOutlineVisible(point.X, point.Y, pen, graphics);
    public bool IsOutlineVisible(PointF point, Pen pen) => IsOutlineVisible(point.X, point.Y, pen, null);
    public bool IsOutlineVisible(PointF point, Pen pen, Graphics? graphics) => IsOutlineVisible(point.X, point.Y, pen, graphics);
    public bool IsOutlineVisible(int x, int y, Pen pen) => IsOutlineVisible((float)x, y, pen, null);
    public bool IsOutlineVisible(int x, int y, Pen pen, Graphics? graphics) => IsOutlineVisible((float)x, y, pen, graphics);
    public bool IsOutlineVisible(float x, float y, Pen pen) => IsOutlineVisible(x, y, pen, null);

    public bool IsOutlineVisible(float x, float y, Pen pen, Graphics? graphics)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(pen);
        if (_geometry.Figures.Count == 0)
        {
            return false;
        }

        PathGeometry flattened = RequiresFlattening()
            ? CreateFlattenedGeometry(matrix: null, flatness: 0.25f)
            : _geometry;
        ProGPU.Vector.Pen nativePen = pen.ToProGpuPen(GetEffectiveStrokeWidth(pen));
        return StrokePathGeometry.TryContains(flattened, nativePen, new Vector2(x, y), out bool contains) && contains;
    }

    public void Transform(Matrix matrix)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(matrix);
        HashSet<int> markerIndices = CaptureMarkerIndices();
        _geometry = _geometry.CreateTransformed(ToMatrix4x4(matrix.MatrixElements));
        RestoreMarkers(markerIndices);
        SynchronizeCurrentState();
    }

    public void Flatten() => Flatten(null, 0.25f);

    public void Flatten(Matrix? matrix) => Flatten(matrix, 0.25f);

    public void Flatten(Matrix? matrix, float flatness)
    {
        ThrowIfDisposed();
        _geometry = CreateFlattenedGeometry(matrix, flatness);
        _markers.Clear();
        SynchronizeCurrentState();
    }

    public void Widen(Pen pen) => Widen(pen, null, 0.25f);

    public void Widen(Pen pen, Matrix? matrix) => Widen(pen, matrix, 0.25f);

    public void Widen(Pen pen, Matrix? matrix, float flatness)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(pen);
        if (_geometry.Figures.Count == 0)
        {
            return;
        }

        PathGeometry flattened = CreateFlattenedGeometry(matrix, flatness);
        ProGPU.Vector.Pen nativePen = pen.ToProGpuPen(GetEffectiveStrokeWidth(pen));
        if (!StrokePathGeometry.TryCreateWidenedPath(flattened, nativePen, out PathGeometry widened))
        {
            throw new ArgumentException("Parameter is not valid.", nameof(pen));
        }

        _geometry = widened;
        _fillMode = System.Drawing.Drawing2D.FillMode.Winding;
        _markers.Clear();
        SynchronizeCurrentState();
    }

    public void Warp(PointF[] destPoints, RectangleF srcRect)
    {
        ArgumentNullException.ThrowIfNull(destPoints);
        Warp((ReadOnlySpan<PointF>)destPoints, srcRect);
    }

    public void Warp(PointF[] destPoints, RectangleF srcRect, Matrix? matrix)
    {
        ArgumentNullException.ThrowIfNull(destPoints);
        Warp((ReadOnlySpan<PointF>)destPoints, srcRect, matrix);
    }

    public void Warp(PointF[] destPoints, RectangleF srcRect, Matrix? matrix, WarpMode warpMode)
    {
        ArgumentNullException.ThrowIfNull(destPoints);
        Warp((ReadOnlySpan<PointF>)destPoints, srcRect, matrix, warpMode);
    }

    public void Warp(PointF[] destPoints, RectangleF srcRect, Matrix? matrix, WarpMode warpMode, float flatness)
    {
        ArgumentNullException.ThrowIfNull(destPoints);
        Warp((ReadOnlySpan<PointF>)destPoints, srcRect, matrix, warpMode, flatness);
    }

    public void Warp(
        ReadOnlySpan<PointF> destPoints,
        RectangleF srcRect,
        Matrix? matrix = null,
        WarpMode warpMode = WarpMode.Perspective,
        float flatness = 0.25f)
    {
        ThrowIfDisposed();
        if (destPoints.Length is not (3 or 4))
        {
            throw new ArgumentException("Parameter is not valid.", nameof(destPoints));
        }

        if (warpMode is not WarpMode.Perspective and not WarpMode.Bilinear)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(warpMode));
        }

        Span<Vector2> destination = stackalloc Vector2[destPoints.Length];
        for (int index = 0; index < destPoints.Length; index++)
        {
            destination[index] = new Vector2(destPoints[index].X, destPoints[index].Y);
        }

        PathGeometry flattened = CreateFlattenedGeometry(matrix, flatness);
        if (!PathWarpGeometry.TryCreateWarpedPath(
                flattened,
                destination,
                new Vector2(srcRect.X, srcRect.Y),
                new Vector2(srcRect.Width, srcRect.Height),
                warpMode == WarpMode.Perspective ? PathWarpMode.Perspective : PathWarpMode.Bilinear,
                flatness,
                out PathGeometry warped))
        {
            throw new ArgumentException("Parameter is not valid.", nameof(destPoints));
        }

        _geometry = warped;
        _markers.Clear();
        SynchronizeCurrentState();
    }

    public void Reverse()
    {
        ThrowIfDisposed();
        var reversed = new PathGeometry { FillRule = _geometry.FillRule };
        for (int figureIndex = _geometry.Figures.Count - 1; figureIndex >= 0; figureIndex--)
        {
            PathFigure source = _geometry.Figures[figureIndex];
            var starts = new Vector2[source.Segments.Count];
            Vector2 current = source.StartPoint;
            for (int segmentIndex = 0; segmentIndex < source.Segments.Count; segmentIndex++)
            {
                starts[segmentIndex] = current;
                current = GetSegmentEnd(source.Segments[segmentIndex]);
            }

            var output = new PathFigure(current, source.IsClosed)
            {
                IsFilled = source.IsFilled,
                StrokeStartLineCap = source.StrokeEndLineCap,
                StrokeEndLineCap = source.StrokeStartLineCap
            };
            for (int segmentIndex = source.Segments.Count - 1; segmentIndex >= 0; segmentIndex--)
            {
                PathSegment segment = source.Segments[segmentIndex];
                Vector2 endpoint = starts[segmentIndex];
                output.Segments.Add(segment switch
                {
                    LineSegment line => new LineSegment(endpoint, line.IsSmoothJoin, line.IsStroked),
                    QuadraticBezierSegment quadratic => new QuadraticBezierSegment(quadratic.ControlPoint, endpoint, quadratic.IsSmoothJoin, quadratic.IsStroked),
                    CubicBezierSegment cubic => new CubicBezierSegment(cubic.ControlPoint2, cubic.ControlPoint1, endpoint, cubic.IsSmoothJoin, cubic.IsStroked),
                    ArcSegment arc => new ArcSegment(
                        endpoint,
                        arc.Size,
                        arc.RotationAngle,
                        arc.IsLargeArc,
                        arc.SweepDirection == SweepDirection.Clockwise ? SweepDirection.Counterclockwise : SweepDirection.Clockwise,
                        arc.IsSmoothJoin,
                        arc.IsStroked),
                    _ => throw new NotSupportedException()
                });
            }
            reversed.Figures.Add(output);
        }

        _geometry = reversed;
        _markers.Clear();
        SynchronizeCurrentState();
    }

    private void AddOpenCurve(ReadOnlySpan<PointF> points, int offset, int numberOfSegments, float tension)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(tension) || offset < 0 || numberOfSegments < 1 || offset + numberOfSegments >= points.Length)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(points));
        }

        ConnectOrStart(points[offset]);
        float scale = tension / 3f;
        int end = offset + numberOfSegments;
        for (int i = offset; i < end; i++)
        {
            PointF previous = points[i == 0 ? i : i - 1];
            PointF current = points[i];
            PointF next = points[i + 1];
            PointF following = points[i + 2 < points.Length ? i + 2 : i + 1];
            AddBezier(
                current,
                new PointF(current.X + ((next.X - previous.X) * scale), current.Y + ((next.Y - previous.Y) * scale)),
                new PointF(next.X - ((following.X - current.X) * scale), next.Y - ((following.Y - current.Y) * scale)),
                next);
        }
    }

    private PathGeometry CreateFlattenedGeometry(Matrix? matrix, float flatness)
    {
        if (!float.IsFinite(flatness) || flatness <= 0f)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(flatness));
        }

        PathGeometry source = matrix == null
            ? _geometry
            : _geometry.CreateTransformed(ToMatrix4x4(matrix.MatrixElements));
        var flattened = new PathGeometry { FillRule = source.FillRule };
        foreach (PathFigure figure in source.Figures)
        {
            var output = new PathFigure(figure.StartPoint, figure.IsClosed)
            {
                IsFilled = figure.IsFilled,
                StrokeStartLineCap = figure.StrokeStartLineCap,
                StrokeEndLineCap = figure.StrokeEndLineCap
            };
            Vector2 current = figure.StartPoint;
            foreach (PathSegment segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        output.Segments.Add(new LineSegment(line.Point, line.IsSmoothJoin, line.IsStroked));
                        current = line.Point;
                        break;
                    case QuadraticBezierSegment quadratic:
                        FlattenQuadratic(output, current, quadratic, flatness, depth: 0);
                        current = quadratic.Point;
                        break;
                    case CubicBezierSegment cubic:
                        FlattenCubic(output, current, cubic.ControlPoint1, cubic.ControlPoint2, cubic.Point, cubic.IsStroked, flatness, depth: 0);
                        current = cubic.Point;
                        break;
                    case ArcSegment arc:
                        float radius = MathF.Max(MathF.Abs(arc.Size.X), MathF.Abs(arc.Size.Y));
                        float maxAngle = radius <= flatness
                            ? MathF.PI * 0.5f
                            : MathF.Min(MathF.PI * 0.5f, 2f * MathF.Acos(Math.Clamp(1f - (flatness / radius), -1f, 1f)));
                        Vector2[] arcPoints = ArcSegmentGeometry.FlattenArc(current, arc, MathF.Max(maxAngle, 0.001f));
                        for (int i = 1; i < arcPoints.Length; i++)
                        {
                            output.Segments.Add(new LineSegment(arcPoints[i], isStroked: arc.IsStroked));
                        }
                        current = arc.Point;
                        break;
                }
            }
            flattened.Figures.Add(output);
        }

        return flattened;
    }

    private static float GetEffectiveStrokeWidth(Pen pen)
    {
        float width = MathF.Abs(pen.Width);
        if (!float.IsFinite(width))
        {
            throw new ArgumentException("Parameter is not valid.", nameof(pen));
        }

        return MathF.Max(1f, width);
    }

    private bool RequiresFlattening()
    {
        foreach (PathFigure figure in _geometry.Figures)
        {
            foreach (PathSegment segment in figure.Segments)
            {
                if (segment is not LineSegment)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void FlattenQuadratic(
        PathFigure output,
        Vector2 start,
        QuadraticBezierSegment segment,
        float flatness,
        int depth)
    {
        Vector2 control1 = start + ((segment.ControlPoint - start) * (2f / 3f));
        Vector2 control2 = segment.Point + ((segment.ControlPoint - segment.Point) * (2f / 3f));
        FlattenCubic(output, start, control1, control2, segment.Point, segment.IsStroked, flatness, depth);
    }

    private static void FlattenCubic(
        PathFigure output,
        Vector2 start,
        Vector2 control1,
        Vector2 control2,
        Vector2 end,
        bool isStroked,
        float flatness,
        int depth)
    {
        if (depth >= 16 || (DistanceToLine(control1, start, end) <= flatness && DistanceToLine(control2, start, end) <= flatness))
        {
            output.Segments.Add(new LineSegment(end, isStroked: isStroked));
            return;
        }

        Vector2 p01 = (start + control1) * 0.5f;
        Vector2 p12 = (control1 + control2) * 0.5f;
        Vector2 p23 = (control2 + end) * 0.5f;
        Vector2 p012 = (p01 + p12) * 0.5f;
        Vector2 p123 = (p12 + p23) * 0.5f;
        Vector2 middle = (p012 + p123) * 0.5f;
        FlattenCubic(output, start, p01, p012, middle, isStroked, flatness, depth + 1);
        FlattenCubic(output, middle, p123, p23, end, isStroked, flatness, depth + 1);
    }

    private static float DistanceToLine(Vector2 point, Vector2 start, Vector2 end)
    {
        Vector2 delta = end - start;
        float length = delta.Length();
        if (length <= 0.000001f) return Vector2.Distance(point, start);
        return MathF.Abs((delta.X * (start.Y - point.Y)) - ((start.X - point.X) * delta.Y)) / length;
    }

    private void AddClosedCurveCore(ReadOnlySpan<PointF> points, float tension)
    {
        ThrowIfDisposed();
        if (points.Length < 3 || !float.IsFinite(tension))
        {
            throw new ArgumentException("Parameter is not valid.", nameof(points));
        }

        StartFigure();
        ConnectOrStart(points[0]);
        float scale = tension / 3f;
        for (int i = 0; i < points.Length; i++)
        {
            PointF previous = points[(i - 1 + points.Length) % points.Length];
            PointF current = points[i];
            PointF next = points[(i + 1) % points.Length];
            PointF following = points[(i + 2) % points.Length];
            AddBezier(
                current,
                new PointF(current.X + ((next.X - previous.X) * scale), current.Y + ((next.Y - previous.Y) * scale)),
                new PointF(next.X - ((following.X - current.X) * scale), next.Y - ((following.Y - current.Y) * scale)),
                next);
        }

        CloseFigure();
    }

    private void InitializeFromPathData(ReadOnlySpan<PointF> points, ReadOnlySpan<byte> types)
    {
        if (points.Length == 0 || points.Length != types.Length)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(points));
        }

        for (int i = 0; i < points.Length; i++)
        {
            byte pathType = (byte)(types[i] & (byte)PathPointType.PathTypeMask);
            if (pathType == (byte)PathPointType.Start)
            {
                StartFigure();
                ConnectOrStart(points[i]);
            }
            else if (pathType == (byte)PathPointType.Line)
            {
                if (_currentFigure == null) ConnectOrStart(i == 0 ? points[i] : points[i - 1]);
                AddLine(_lastPoint, points[i]);
            }
            else if (pathType == (byte)PathPointType.Bezier3)
            {
                if (_currentFigure == null || i + 2 >= points.Length ||
                    (types[i + 1] & (byte)PathPointType.PathTypeMask) != (byte)PathPointType.Bezier3 ||
                    (types[i + 2] & (byte)PathPointType.PathTypeMask) != (byte)PathPointType.Bezier3)
                {
                    throw new ArgumentException("Parameter is not valid.", nameof(types));
                }

                AddBezier(_lastPoint, points[i], points[i + 1], points[i + 2]);
                i += 2;
            }
            else
            {
                throw new ArgumentException("Parameter is not valid.", nameof(types));
            }

            if ((types[i] & (byte)PathPointType.PathMarker) != 0) SetMarkers();
            if ((types[i] & (byte)PathPointType.CloseSubpath) != 0) CloseFigure();
        }

        SynchronizeCurrentState();
    }

    private void WritePathData(Span<PointF> pointDestination, Span<byte> typeDestination, bool writePoints, bool writeTypes)
    {
        int index = 0;
        foreach (PathFigure figure in _geometry.Figures)
        {
            Vector2 current = figure.StartPoint;
            WritePoint(ToPointF(current), (byte)PathPointType.Start, figure, ref index, pointDestination, typeDestination, writePoints, writeTypes);

            foreach (PathSegment segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        WritePoint(ToPointF(line.Point), (byte)PathPointType.Line, line, ref index, pointDestination, typeDestination, writePoints, writeTypes);
                        current = line.Point;
                        break;

                    case QuadraticBezierSegment quadratic:
                        {
                            Vector2 control1 = current + ((quadratic.ControlPoint - current) * (2f / 3f));
                            Vector2 control2 = quadratic.Point + ((quadratic.ControlPoint - quadratic.Point) * (2f / 3f));
                            WritePoint(ToPointF(control1), (byte)PathPointType.Bezier3, null, ref index, pointDestination, typeDestination, writePoints, writeTypes);
                            WritePoint(ToPointF(control2), (byte)PathPointType.Bezier3, null, ref index, pointDestination, typeDestination, writePoints, writeTypes);
                            WritePoint(ToPointF(quadratic.Point), (byte)PathPointType.Bezier3, quadratic, ref index, pointDestination, typeDestination, writePoints, writeTypes);
                            current = quadratic.Point;
                            break;
                        }

                    case CubicBezierSegment cubic:
                        WritePoint(ToPointF(cubic.ControlPoint1), (byte)PathPointType.Bezier3, null, ref index, pointDestination, typeDestination, writePoints, writeTypes);
                        WritePoint(ToPointF(cubic.ControlPoint2), (byte)PathPointType.Bezier3, null, ref index, pointDestination, typeDestination, writePoints, writeTypes);
                        WritePoint(ToPointF(cubic.Point), (byte)PathPointType.Bezier3, cubic, ref index, pointDestination, typeDestination, writePoints, writeTypes);
                        current = cubic.Point;
                        break;

                    case ArcSegment arc:
                        WriteArcAsCubics(current, arc, ref index, pointDestination, typeDestination, writePoints, writeTypes);
                        current = arc.Point;
                        break;
                }
            }

            if (figure.IsClosed && writeTypes && index > 0)
            {
                typeDestination[index - 1] |= (byte)PathPointType.CloseSubpath;
            }
        }
    }

    private void WriteArcAsCubics(
        Vector2 start,
        ArcSegment arc,
        ref int index,
        Span<PointF> pointDestination,
        Span<byte> typeDestination,
        bool writePoints,
        bool writeTypes)
    {
        if (!ArcSegmentGeometry.TryGetArcCenter(
                start,
                arc.Point,
                arc.Size,
                arc.RotationAngle,
                arc.IsLargeArc,
                arc.SweepDirection,
                out Vector2 center,
                out float theta,
                out float delta,
                out float radiusX,
                out float radiusY))
        {
            WritePoint(ToPointF(arc.Point), (byte)PathPointType.Line, arc, ref index, pointDestination, typeDestination, writePoints, writeTypes);
            return;
        }

        int spanCount = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(delta) / (MathF.PI * 0.5f)));
        float span = delta / spanCount;
        float rotation = arc.RotationAngle * MathF.PI / 180f;
        float cosRotation = MathF.Cos(rotation);
        float sinRotation = MathF.Sin(rotation);
        for (int spanIndex = 0; spanIndex < spanCount; spanIndex++)
        {
            float startTheta = theta + (spanIndex * span);
            float endTheta = startTheta + span;
            Vector2 p0 = ArcSegmentGeometry.EvaluatePoint(center, radiusX, radiusY, arc.RotationAngle, startTheta);
            Vector2 p3 = ArcSegmentGeometry.EvaluatePoint(center, radiusX, radiusY, arc.RotationAngle, endTheta);
            Vector2 d0 = EllipseDerivative(radiusX, radiusY, cosRotation, sinRotation, startTheta);
            Vector2 d1 = EllipseDerivative(radiusX, radiusY, cosRotation, sinRotation, endTheta);
            float alpha = (4f / 3f) * MathF.Tan(span * 0.25f);
            object? endpointMarker = spanIndex == spanCount - 1 ? arc : null;
            WritePoint(ToPointF(p0 + (alpha * d0)), (byte)PathPointType.Bezier3, null, ref index, pointDestination, typeDestination, writePoints, writeTypes);
            WritePoint(ToPointF(p3 - (alpha * d1)), (byte)PathPointType.Bezier3, null, ref index, pointDestination, typeDestination, writePoints, writeTypes);
            WritePoint(ToPointF(p3), (byte)PathPointType.Bezier3, endpointMarker, ref index, pointDestination, typeDestination, writePoints, writeTypes);
        }
    }

    private static Vector2 EllipseDerivative(float rx, float ry, float cosRotation, float sinRotation, float theta) =>
        new(
            (-rx * MathF.Sin(theta) * cosRotation) - (ry * MathF.Cos(theta) * sinRotation),
            (-rx * MathF.Sin(theta) * sinRotation) + (ry * MathF.Cos(theta) * cosRotation));

    private void WritePoint(
        PointF point,
        byte type,
        object? endpoint,
        ref int index,
        Span<PointF> pointDestination,
        Span<byte> typeDestination,
        bool writePoints,
        bool writeTypes)
    {
        if (writePoints) pointDestination[index] = point;
        if (writeTypes)
        {
            typeDestination[index] = endpoint != null && _markers.Contains(endpoint)
                ? (byte)(type | (byte)PathPointType.PathMarker)
                : type;
        }

        index++;
    }

    private int GetArcCubicCount(PathFigure figure, PathSegment target, ArcSegment arc)
    {
        Vector2 current = figure.StartPoint;
        foreach (PathSegment segment in figure.Segments)
        {
            if (ReferenceEquals(segment, target))
            {
                return ArcSegmentGeometry.TryGetArcCenter(
                    current, arc.Point, arc.Size, arc.RotationAngle, arc.IsLargeArc, arc.SweepDirection,
                    out _, out _, out float delta, out _, out _)
                    ? Math.Max(1, (int)MathF.Ceiling(MathF.Abs(delta) / (MathF.PI * 0.5f))) * 3
                    : 1;
            }

            current = GetSegmentEnd(segment);
        }

        return 1;
    }

    private static void CopySegments(PathFigure source, PathFigure destination, HashSet<object> sourceMarkers, HashSet<object> destinationMarkers)
    {
        foreach (PathSegment segment in source.Segments)
        {
            PathSegment copy = CloneSegment(segment);
            destination.Segments.Add(copy);
            if (sourceMarkers.Contains(segment)) destinationMarkers.Add(copy);
        }
    }

    private static PathFigure CloneFigure(PathFigure source, HashSet<object> sourceMarkers, HashSet<object> destinationMarkers)
    {
        var copy = new PathFigure(source.StartPoint, source.IsClosed)
        {
            IsFilled = source.IsFilled,
            StrokeStartLineCap = source.StrokeStartLineCap,
            StrokeEndLineCap = source.StrokeEndLineCap
        };
        if (sourceMarkers.Contains(source)) destinationMarkers.Add(copy);
        CopySegments(source, copy, sourceMarkers, destinationMarkers);
        return copy;
    }

    private static PathSegment CloneSegment(PathSegment segment) => segment switch
    {
        LineSegment line => new LineSegment(line.Point, line.IsSmoothJoin, line.IsStroked),
        QuadraticBezierSegment quadratic => new QuadraticBezierSegment(quadratic.ControlPoint, quadratic.Point, quadratic.IsSmoothJoin, quadratic.IsStroked),
        CubicBezierSegment cubic => new CubicBezierSegment(cubic.ControlPoint1, cubic.ControlPoint2, cubic.Point, cubic.IsSmoothJoin, cubic.IsStroked),
        ArcSegment arc => new ArcSegment(arc.Point, arc.Size, arc.RotationAngle, arc.IsLargeArc, arc.SweepDirection, arc.IsSmoothJoin, arc.IsStroked),
        _ => throw new NotSupportedException($"Unsupported path segment {segment.GetType().Name}.")
    };

    private object? GetCurrentEndpointObject()
    {
        if (_geometry.Figures.Count == 0) return null;
        PathFigure figure = _currentFigure ?? _geometry.Figures[^1];
        return figure.Segments.Count == 0 ? figure : figure.Segments[^1];
    }

    private HashSet<int> CaptureMarkerIndices()
    {
        var result = new HashSet<int>();
        int index = 0;
        foreach (PathFigure figure in _geometry.Figures)
        {
            if (_markers.Contains(figure)) result.Add(index);
            index++;
            foreach (PathSegment segment in figure.Segments)
            {
                if (_markers.Contains(segment)) result.Add(index);
                index++;
            }
        }

        return result;
    }

    private void RestoreMarkers(HashSet<int> markerIndices)
    {
        _markers.Clear();
        int index = 0;
        foreach (PathFigure figure in _geometry.Figures)
        {
            if (markerIndices.Contains(index)) _markers.Add(figure);
            index++;
            foreach (PathSegment segment in figure.Segments)
            {
                if (markerIndices.Contains(index)) _markers.Add(segment);
                index++;
            }
        }
    }

    private void SynchronizeCurrentState()
    {
        if (_geometry.Figures.Count == 0)
        {
            _currentFigure = null;
            _lastPoint = default;
            return;
        }

        PathFigure last = _geometry.Figures[^1];
        _lastPoint = GetFigureLastPoint(last);
        _currentFigure = last.IsClosed ? null : last;
    }

    private static PointF GetFigureLastPoint(PathFigure figure) =>
        figure.Segments.Count == 0 ? ToPointF(figure.StartPoint) : ToPointF(GetSegmentEnd(figure.Segments[^1]));

    private static Vector2 GetSegmentEnd(PathSegment segment) => segment switch
    {
        LineSegment line => line.Point,
        QuadraticBezierSegment quadratic => quadratic.Point,
        CubicBezierSegment cubic => cubic.Point,
        ArcSegment arc => arc.Point,
        _ => default
    };

    private static PointF ToPointF(Vector2 point) => new(point.X, point.Y);

    private static PointF[] ConvertPoints(Point[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        return ConvertPoints((ReadOnlySpan<Point>)points);
    }

    private static PointF[] ConvertPoints(ReadOnlySpan<Point> points)
    {
        PointF[] result = new PointF[points.Length];
        for (int i = 0; i < points.Length; i++) result[i] = points[i];
        return result;
    }

    private static Matrix4x4 ToMatrix4x4(Matrix3x2 matrix) => new(
        matrix.M11, matrix.M12, 0f, 0f,
        matrix.M21, matrix.M22, 0f, 0f,
        0f, 0f, 1f, 0f,
        matrix.M31, matrix.M32, 0f, 1f);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        _disposed = true;
        _markers.Clear();
        _currentFigure = null;
    }
}
