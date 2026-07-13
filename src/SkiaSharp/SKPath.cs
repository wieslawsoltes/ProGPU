using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Vector;

namespace SkiaSharp;

public class SKPath : IDisposable
{
    public IntPtr Handle { get; } = SKObjectHandle.Create();
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
        ArgumentNullException.ThrowIfNull(source);
        PathFigure? copiedCurrentFigure = null;
        foreach (var figure in source.Geometry.Figures)
        {
            var copiedFigure = CloneFigure(figure, Vector2.Zero);
            Geometry.Figures.Add(copiedFigure);
            if (ReferenceEquals(figure, source._currentFigure))
            {
                copiedCurrentFigure = copiedFigure;
            }
        }

        _currentFigure = copiedCurrentFigure;
        FillType = source.FillType;
    }

    public static SKPath ParseSvgPathData(string pathData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathData);
        var geometry = PathGeometry.Parse(pathData);
        var path = new SKPath
        {
            FillType = geometry.FillRule == FillRule.EvenOdd
                ? SKPathFillType.EvenOdd
                : SKPathFillType.Winding
        };
        foreach (var figure in geometry.Figures)
        {
            path.Geometry.Figures.Add(figure);
        }

        return path;
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

    public SKRect TightBounds
    {
        get
        {
            if (Geometry.IsCombined)
            {
                return Bounds;
            }

            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            var hasBounds = false;

            void Include(Vector2 point)
            {
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                {
                    return;
                }

                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
                hasBounds = true;
            }

            foreach (var figure in Geometry.Figures)
            {
                var current = figure.StartPoint;
                Include(current);

                foreach (var segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case LineSegment line:
                            Include(line.Point);
                            current = line.Point;
                            break;

                        case QuadraticBezierSegment quadratic:
                            IncludeQuadraticExtrema(current, quadratic.ControlPoint, quadratic.Point, Include);
                            Include(quadratic.Point);
                            current = quadratic.Point;
                            break;

                        case CubicBezierSegment cubic:
                            IncludeCubicExtrema(
                                current,
                                cubic.ControlPoint1,
                                cubic.ControlPoint2,
                                cubic.Point,
                                Include);
                            Include(cubic.Point);
                            current = cubic.Point;
                            break;

                        case ArcSegment arc:
                            if (ArcSegmentGeometry.TryGetArcBounds(current, arc, out var arcMin, out var arcMax))
                            {
                                Include(arcMin);
                                Include(arcMax);
                            }
                            else
                            {
                                Include(arc.Point);
                            }

                            current = arc.Point;
                            break;
                    }
                }
            }

            return hasBounds
                ? new SKRect(min.X, min.Y, max.X, max.Y)
                : SKRect.Empty;
        }
    }

    public bool IsEmpty => Geometry.Figures.Count == 0;

    public SKPathConvexity Convexity => IsConvex
        ? SKPathConvexity.Convex
        : SKPathConvexity.Concave;

    public bool IsConvex => ComputeIsConvex();

    public bool IsConcave => !IsConvex;

    public bool IsLine => TryGetLine(out _, out _);

    public bool IsOval => TryGetOvalBounds(out _);

    public bool IsRect => TryGetRect(out _, out _, out _);

    public bool IsRoundRect
    {
        get
        {
            if (!TryGetRoundRect(out var roundRect))
            {
                return false;
            }

            roundRect.Dispose();
            return true;
        }
    }

    public SKPathSegmentMask SegmentMasks => GetSegmentMasks();

    public int VerbCount
    {
        get
        {
            var count = 0;
            foreach (var figure in Geometry.Figures)
            {
                count = checked(count + 1 + (figure.IsClosed ? 1 : 0));
                var current = figure.StartPoint;
                foreach (var segment in figure.Segments)
                {
                    count = checked(count + (segment is ArcSegment arc
                        ? GetArcVerbCount(current, arc)
                        : 1));
                    current = GetSegmentEndPoint(segment, current);
                }
            }

            return count;
        }
    }

    public int PointCount => CountPoints();

    public SKPoint this[int index] => GetPoint(index);

    public SKPoint[] Points => GetPoints(PointCount);

    public SKPoint LastPoint
    {
        get
        {
            if (Geometry.Figures.Count == 0)
            {
                return SKPoint.Empty;
            }

            var figure = Geometry.Figures[^1];
            var point = GetFigureEndPoint(figure);
            return new SKPoint(point.X, point.Y);
        }
    }

    public SKPoint[]? GetLine() => TryGetLine(out var start, out var end)
        ? [start, end]
        : null;

    public SKRect GetOvalBounds() => TryGetOvalBounds(out var bounds)
        ? bounds
        : SKRect.Empty;

    public SKRoundRect? GetRoundRect() => TryGetRoundRect(out var roundRect)
        ? roundRect
        : null;

    public SKRect GetRect() => GetRect(out _, out _);

    public SKRect GetRect(out bool isClosed, out SKPathDirection direction) =>
        TryGetRect(out var rect, out isClosed, out direction)
            ? rect
            : SKRect.Empty;

    public SKPoint GetPoint(int index)
    {
        if ((uint)index >= (uint)PointCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var current = 0;
        foreach (var figure in Geometry.Figures)
        {
            if (current++ == index)
            {
                return ToPoint(figure.StartPoint);
            }

            var segmentStart = figure.StartPoint;
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        if (current++ == index)
                        {
                            return ToPoint(line.Point);
                        }
                        break;

                    case QuadraticBezierSegment quadratic:
                        if (current++ == index)
                        {
                            return ToPoint(quadratic.ControlPoint);
                        }
                        if (current++ == index)
                        {
                            return ToPoint(quadratic.Point);
                        }
                        break;

                    case CubicBezierSegment cubic:
                        if (current++ == index)
                        {
                            return ToPoint(cubic.ControlPoint1);
                        }
                        if (current++ == index)
                        {
                            return ToPoint(cubic.ControlPoint2);
                        }
                        if (current++ == index)
                        {
                            return ToPoint(cubic.Point);
                        }
                        break;

                    case ArcSegment arc:
                        Span<Vector2> arcPoints = stackalloc Vector2[8];
                        var arcPointCount = GetArcConicPoints(
                            segmentStart,
                            arc,
                            arcPoints);
                        for (var pointIndex = 0; pointIndex < arcPointCount; pointIndex++)
                        {
                            if (current++ == index)
                            {
                                return ToPoint(arcPoints[pointIndex]);
                            }
                        }
                        break;
                }

                segmentStart = GetSegmentEndPoint(segment, segmentStart);
            }
        }

        throw new ArgumentOutOfRangeException(nameof(index));
    }

    public SKPoint[] GetPoints(int max)
    {
        if (max < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(max));
        }

        var points = new SKPoint[max];
        GetPoints(points, max);
        return points;
    }

    public int GetPoints(SKPoint[] points, int max)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (max < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(max));
        }

        var limit = Math.Min(points.Length, max);
        var written = 0;
        foreach (var figure in Geometry.Figures)
        {
            var segmentStart = figure.StartPoint;
            WritePoint(ToPoint(figure.StartPoint));
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        WritePoint(ToPoint(line.Point));
                        break;
                    case QuadraticBezierSegment quadratic:
                        WritePoint(ToPoint(quadratic.ControlPoint));
                        WritePoint(ToPoint(quadratic.Point));
                        break;
                    case CubicBezierSegment cubic:
                        WritePoint(ToPoint(cubic.ControlPoint1));
                        WritePoint(ToPoint(cubic.ControlPoint2));
                        WritePoint(ToPoint(cubic.Point));
                        break;
                    case ArcSegment arc:
                        Span<Vector2> arcPoints = stackalloc Vector2[8];
                        var arcPointCount = GetArcConicPoints(segmentStart, arc, arcPoints);
                        for (var pointIndex = 0; pointIndex < arcPointCount; pointIndex++)
                        {
                            WritePoint(ToPoint(arcPoints[pointIndex]));
                        }
                        break;
                }

                segmentStart = GetSegmentEndPoint(segment, segmentStart);
            }
        }

        return written;

        void WritePoint(SKPoint point)
        {
            if (written < limit)
            {
                points[written] = point;
                written++;
            }
        }
    }

    public bool GetBounds(out SKRect rect)
    {
        if (IsEmpty)
        {
            rect = SKRect.Empty;
            return false;
        }

        rect = Bounds;
        return true;
    }

    public SKRect ComputeTightBounds() => TightBounds;

    public bool GetTightBounds(out SKRect result)
    {
        if (IsEmpty)
        {
            result = SKRect.Empty;
            return false;
        }

        result = TightBounds;
        return true;
    }

    private int CountPoints()
    {
        var count = 0;
        foreach (var figure in Geometry.Figures)
        {
            count = checked(count + 1);
            var current = figure.StartPoint;
            foreach (var segment in figure.Segments)
            {
                count = checked(count + (segment switch
                {
                    QuadraticBezierSegment => 2,
                    CubicBezierSegment => 3,
                    ArcSegment arc => GetArcVerbCount(current, arc) * 2,
                    _ => 1,
                }));
                current = GetSegmentEndPoint(segment, current);
            }
        }

        return count;
    }

    private SKPathSegmentMask GetSegmentMasks()
    {
        var masks = (SKPathSegmentMask)0;
        foreach (var figure in Geometry.Figures)
        {
            foreach (var segment in figure.Segments)
            {
                masks |= segment switch
                {
                    LineSegment => SKPathSegmentMask.Line,
                    QuadraticBezierSegment => SKPathSegmentMask.Quad,
                    CubicBezierSegment => SKPathSegmentMask.Cubic,
                    ArcSegment => SKPathSegmentMask.Conic,
                    _ => 0,
                };
            }
        }

        return masks;
    }

    private bool ComputeIsConvex()
    {
        if (Geometry.Figures.Count == 0)
        {
            return true;
        }

        if (Geometry.Figures.Count != 1)
        {
            return false;
        }

        if (TryGetOvalBounds(out _))
        {
            return true;
        }

        if (TryGetRoundRect(out var roundRect))
        {
            roundRect.Dispose();
            return true;
        }

        var figure = Geometry.Figures[0];
        if (figure.Segments.Count < 2)
        {
            return true;
        }

        foreach (var segment in figure.Segments)
        {
            if (segment is not LineSegment)
            {
                return false;
            }
        }

        var first = figure.StartPoint;
        var previous = first;
        var firstEdge = default(Vector2);
        var previousEdge = default(Vector2);
        var turnSign = 0f;
        var hasEdge = false;
        foreach (var segment in figure.Segments)
        {
            var point = ((LineSegment)segment).Point;
            var edge = point - previous;
            if (edge.LengthSquared() > 1e-12f)
            {
                if (!hasEdge)
                {
                    firstEdge = edge;
                    hasEdge = true;
                }
                else if (!UpdateTurn(previousEdge, edge, ref turnSign))
                {
                    return false;
                }

                previousEdge = edge;
            }

            previous = point;
        }

        if (hasEdge && figure.IsClosed)
        {
            var closingEdge = first - previous;
            if (closingEdge.LengthSquared() > 1e-12f &&
                (!UpdateTurn(previousEdge, closingEdge, ref turnSign) ||
                 !UpdateTurn(closingEdge, firstEdge, ref turnSign)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool UpdateTurn(Vector2 first, Vector2 second, ref float sign)
    {
        var cross = first.X * second.Y - first.Y * second.X;
        if (MathF.Abs(cross) <= 1e-6f)
        {
            return true;
        }

        var nextSign = MathF.CopySign(1f, cross);
        if (sign != 0f && sign != nextSign)
        {
            return false;
        }

        sign = nextSign;
        return true;
    }

    private bool TryGetLine(out SKPoint start, out SKPoint end)
    {
        if (Geometry.Figures.Count == 1 &&
            !Geometry.Figures[0].IsClosed &&
            Geometry.Figures[0].Segments.Count == 1 &&
            Geometry.Figures[0].Segments[0] is LineSegment line)
        {
            start = ToPoint(Geometry.Figures[0].StartPoint);
            end = ToPoint(line.Point);
            return true;
        }

        start = default;
        end = default;
        return false;
    }

    private bool TryGetRect(
        out SKRect rect,
        out bool isClosed,
        out SKPathDirection direction)
    {
        rect = SKRect.Empty;
        isClosed = false;
        direction = SKPathDirection.Clockwise;
        if (Geometry.Figures.Count != 1)
        {
            return false;
        }

        var figure = Geometry.Figures[0];
        if (figure.Segments.Count is < 3 or > 4)
        {
            return false;
        }

        Span<Vector2> points = stackalloc Vector2[5];
        points[0] = figure.StartPoint;
        var count = 1;
        foreach (var segment in figure.Segments)
        {
            if (segment is not LineSegment line)
            {
                return false;
            }

            points[count++] = line.Point;
        }

        if (count == 5)
        {
            if (!NearlyEqual(points[4], points[0]))
            {
                return false;
            }

            count--;
        }

        if (count != 4 || (!figure.IsClosed && figure.Segments.Count != 4))
        {
            return false;
        }

        var min = points[0];
        var max = points[0];
        for (var index = 1; index < count; index++)
        {
            min = Vector2.Min(min, points[index]);
            max = Vector2.Max(max, points[index]);
        }

        if (max.X <= min.X || max.Y <= min.Y)
        {
            return false;
        }

        var corners = 0;
        for (var index = 0; index < count; index++)
        {
            var point = points[index];
            var corner = (NearlyEqual(point.X, max.X) ? 1 : 0) |
                (NearlyEqual(point.Y, max.Y) ? 2 : 0);
            if ((!NearlyEqual(point.X, min.X) && !NearlyEqual(point.X, max.X)) ||
                (!NearlyEqual(point.Y, min.Y) && !NearlyEqual(point.Y, max.Y)) ||
                (corners & (1 << corner)) != 0)
            {
                return false;
            }

            corners |= 1 << corner;
            var next = points[(index + 1) % count];
            if (NearlyEqual(point.X, next.X) == NearlyEqual(point.Y, next.Y))
            {
                return false;
            }
        }

        var signedArea = 0f;
        for (var index = 0; index < count; index++)
        {
            var point = points[index];
            var next = points[(index + 1) % count];
            signedArea += point.X * next.Y - point.Y * next.X;
        }

        rect = new SKRect(min.X, min.Y, max.X, max.Y);
        isClosed = figure.IsClosed;
        direction = signedArea >= 0f
            ? SKPathDirection.Clockwise
            : SKPathDirection.CounterClockwise;
        return true;
    }

    private bool TryGetOvalBounds(out SKRect bounds)
    {
        bounds = SKRect.Empty;
        if (Geometry.Figures.Count != 1)
        {
            return false;
        }

        var figure = Geometry.Figures[0];
        if (!figure.IsClosed ||
            figure.Segments.Count != 2 ||
            figure.Segments[0] is not ArcSegment first ||
            figure.Segments[1] is not ArcSegment second ||
            first.SweepDirection != second.SweepDirection ||
            !first.IsLargeArc ||
            !second.IsLargeArc ||
            !NearlyEqual(second.Point, figure.StartPoint))
        {
            return false;
        }

        bounds = TightBounds;
        var radiusX = bounds.Width * 0.5f;
        var radiusY = bounds.Height * 0.5f;
        if (!NearlyEqual(first.Size.X, radiusX) ||
            !NearlyEqual(first.Size.Y, radiusY) ||
            !NearlyEqual(second.Size.X, radiusX) ||
            !NearlyEqual(second.Size.Y, radiusY) ||
            !NearlyEqual(figure.StartPoint + first.Point, new Vector2(bounds.MidX * 2f, bounds.MidY * 2f)))
        {
            bounds = SKRect.Empty;
            return false;
        }

        return true;
    }

    private bool TryGetRoundRect(out SKRoundRect roundRect)
    {
        roundRect = null!;
        if (Geometry.Figures.Count != 1)
        {
            return false;
        }

        var figure = Geometry.Figures[0];
        if (!figure.IsClosed || figure.Segments.Count != 8)
        {
            return false;
        }

        var bounds = TightBounds;
        Span<SKPoint> radii = stackalloc SKPoint[4];
        Span<bool> assigned = stackalloc bool[4];
        var current = figure.StartPoint;
        var arcCount = 0;
        var lineCount = 0;
        foreach (var segment in figure.Segments)
        {
            switch (segment)
            {
                case LineSegment line:
                    current = line.Point;
                    lineCount++;
                    break;

                case ArcSegment arc:
                    var corner = ClassifyRoundRectCorner(current, arc.Point, bounds);
                    if (corner < 0 || assigned[corner])
                    {
                        return false;
                    }

                    radii[corner] = new SKPoint(MathF.Abs(arc.Size.X), MathF.Abs(arc.Size.Y));
                    assigned[corner] = true;
                    current = arc.Point;
                    arcCount++;
                    break;

                default:
                    return false;
            }
        }

        if (arcCount != 4 || lineCount != 4)
        {
            return false;
        }

        var result = new SKRoundRect();
        result.SetRectRadii(bounds, radii);
        if (!result.IsValid)
        {
            result.Dispose();
            return false;
        }

        roundRect = result;
        return true;
    }

    private static int ClassifyRoundRectCorner(Vector2 start, Vector2 end, SKRect bounds)
    {
        var startTop = NearlyEqual(start.Y, bounds.Top);
        var startRight = NearlyEqual(start.X, bounds.Right);
        var startBottom = NearlyEqual(start.Y, bounds.Bottom);
        var startLeft = NearlyEqual(start.X, bounds.Left);
        var endTop = NearlyEqual(end.Y, bounds.Top);
        var endRight = NearlyEqual(end.X, bounds.Right);
        var endBottom = NearlyEqual(end.Y, bounds.Bottom);
        var endLeft = NearlyEqual(end.X, bounds.Left);
        if ((startTop && endRight) || (startRight && endTop))
        {
            return 1;
        }
        if ((startRight && endBottom) || (startBottom && endRight))
        {
            return 2;
        }
        if ((startBottom && endLeft) || (startLeft && endBottom))
        {
            return 3;
        }
        if ((startLeft && endTop) || (startTop && endLeft))
        {
            return 0;
        }

        return -1;
    }

    private static Vector2 GetFigureEndPoint(PathFigure figure)
    {
        if (figure.Segments.Count == 0)
        {
            return figure.StartPoint;
        }

        return figure.Segments[^1] switch
        {
            LineSegment line => line.Point,
            QuadraticBezierSegment quadratic => quadratic.Point,
            CubicBezierSegment cubic => cubic.Point,
            ArcSegment arc => arc.Point,
            _ => figure.StartPoint,
        };
    }

    private static Vector2 GetSegmentEndPoint(PathSegment segment, Vector2 fallback) =>
        segment switch
        {
            LineSegment line => line.Point,
            QuadraticBezierSegment quadratic => quadratic.Point,
            CubicBezierSegment cubic => cubic.Point,
            ArcSegment arc => arc.Point,
            _ => fallback,
        };

    private static int GetArcVerbCount(Vector2 start, ArcSegment arc)
    {
        Span<Vector2> points = stackalloc Vector2[8];
        var pointCount = GetArcConicPoints(start, arc, points);
        return pointCount == 1 ? 1 : pointCount / 2;
    }

    private static int GetArcConicPoints(
        Vector2 start,
        ArcSegment arc,
        Span<Vector2> points)
    {
        if (!ArcSegmentGeometry.TryGetArcCenter(
                start,
                arc.Point,
                arc.Size,
                arc.RotationAngle,
                arc.IsLargeArc,
                arc.SweepDirection,
                out var center,
                out var startAngle,
                out var sweepAngle,
                out var radiusX,
                out var radiusY))
        {
            points[0] = arc.Point;
            return 1;
        }

        const float angleEpsilon = 1e-5f;
        var conicCount = Math.Clamp(
            (int)MathF.Ceiling(
                MathF.Max(0f, MathF.Abs(sweepAngle) - angleEpsilon) /
                (MathF.PI * 0.5f)),
            1,
            4);
        var step = sweepAngle / conicCount;
        var rotation = arc.RotationAngle * (MathF.PI / 180f);
        var cosRotation = MathF.Cos(rotation);
        var sinRotation = MathF.Sin(rotation);
        var axisX = new Vector2(cosRotation * radiusX, sinRotation * radiusX);
        var axisY = new Vector2(-sinRotation * radiusY, cosRotation * radiusY);
        for (var index = 0; index < conicCount; index++)
        {
            var segmentStart = startAngle + step * index;
            var segmentEnd = segmentStart + step;
            var middle = (segmentStart + segmentEnd) * 0.5f;
            var weight = MathF.Cos(step * 0.5f);
            points[index * 2] = center +
                (axisX * MathF.Cos(middle) + axisY * MathF.Sin(middle)) / weight;
            points[index * 2 + 1] = index == conicCount - 1
                ? arc.Point
                : center + axisX * MathF.Cos(segmentEnd) + axisY * MathF.Sin(segmentEnd);
        }

        return conicCount * 2;
    }

    private static bool NearlyEqual(Vector2 left, Vector2 right) =>
        NearlyEqual(left.X, right.X) && NearlyEqual(left.Y, right.Y);

    private static bool NearlyEqual(float left, float right) =>
        MathF.Abs(left - right) <= 0.0001f;

    private static SKPoint ToPoint(Vector2 point) => new(point.X, point.Y);

    private static void IncludeQuadraticExtrema(
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Action<Vector2> include)
    {
        IncludeQuadraticAxisExtremum(p0.X, p1.X, p2.X, p0, p1, p2, include);
        IncludeQuadraticAxisExtremum(p0.Y, p1.Y, p2.Y, p0, p1, p2, include);
    }

    private static void IncludeQuadraticAxisExtremum(
        float v0,
        float v1,
        float v2,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Action<Vector2> include)
    {
        var denominator = v0 - 2f * v1 + v2;
        if (MathF.Abs(denominator) <= 1e-6f)
        {
            return;
        }

        var t = (v0 - v1) / denominator;
        if (t > 0f && t < 1f)
        {
            var oneMinusT = 1f - t;
            include(oneMinusT * oneMinusT * p0 + 2f * oneMinusT * t * p1 + t * t * p2);
        }
    }

    private static void IncludeCubicExtrema(
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3,
        Action<Vector2> include)
    {
        IncludeCubicAxisExtrema(p0.X, p1.X, p2.X, p3.X, p0, p1, p2, p3, include);
        IncludeCubicAxisExtrema(p0.Y, p1.Y, p2.Y, p3.Y, p0, p1, p2, p3, include);
    }

    private static void IncludeCubicAxisExtrema(
        float v0,
        float v1,
        float v2,
        float v3,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3,
        Action<Vector2> include)
    {
        var a = -v0 + 3f * v1 - 3f * v2 + v3;
        var b = 2f * (v0 - 2f * v1 + v2);
        var c = v1 - v0;

        if (MathF.Abs(a) <= 1e-6f)
        {
            if (MathF.Abs(b) > 1e-6f)
            {
                IncludeCubicAt(-c / b, p0, p1, p2, p3, include);
            }

            return;
        }

        var discriminant = b * b - 4f * a * c;
        if (discriminant < 0f)
        {
            return;
        }

        var root = MathF.Sqrt(MathF.Max(0f, discriminant));
        var denominator = 2f * a;
        IncludeCubicAt((-b + root) / denominator, p0, p1, p2, p3, include);
        if (root > 1e-6f)
        {
            IncludeCubicAt((-b - root) / denominator, p0, p1, p2, p3, include);
        }
    }

    private static void IncludeCubicAt(
        float t,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3,
        Action<Vector2> include)
    {
        if (t <= 0f || t >= 1f || !float.IsFinite(t))
        {
            return;
        }

        var oneMinusT = 1f - t;
        include(
            oneMinusT * oneMinusT * oneMinusT * p0 +
            3f * oneMinusT * oneMinusT * t * p1 +
            3f * oneMinusT * t * t * p2 +
            t * t * t * p3);
    }

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
        FillType = SKPathFillType.Winding;
    }

    public void AddCircle(float x, float y, float radius, SKPathDirection direction = SKPathDirection.Clockwise)
    {
        MoveTo(x + radius, y);
        ArcTo(radius, radius, 0, SKPathArcSize.Large, direction, x - radius, y);
        ArcTo(radius, radius, 0, SKPathArcSize.Large, direction, x + radius, y);
        Close();
    }

    public void AddOval(SKRect rect, SKPathDirection direction = SKPathDirection.Clockwise)
    {
        var radiusX = rect.Width / 2f;
        var radiusY = rect.Height / 2f;
        var centerX = rect.MidX;
        var centerY = rect.MidY;
        MoveTo(centerX + radiusX, centerY);
        ArcTo(radiusX, radiusY, 0f, SKPathArcSize.Large, direction, centerX - radiusX, centerY);
        ArcTo(radiusX, radiusY, 0f, SKPathArcSize.Large, direction, centerX + radiusX, centerY);
        Close();
    }

    public void ConicTo(SKPoint control, SKPoint end, float weight)
    {
        QuadTo(control, end);
    }

    public bool Contains(float x, float y)
    {
        if (!PathGeometryHitTesting.TryContainsFill(
                Geometry,
                new Vector2(x, y),
                0f,
                relativeTolerance: false,
                out var contains))
        {
            contains = Bounds is var bounds
                && x >= bounds.Left
                && x <= bounds.Right
                && y >= bounds.Top
                && y <= bounds.Bottom;
        }

        return FillType is SKPathFillType.InverseEvenOdd or SKPathFillType.InverseWinding
            ? !contains
            : contains;
    }

    public SKPathRawIterator CreateIterator(bool forceClose)
    {
        return new SKPathRawIterator(this, forceClose);
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

        if (direction == SKPathDirection.Clockwise)
        {
            MoveTo(r.Left + radii[0].X, r.Top);
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
            MoveTo(r.Left, r.Top + radii[0].Y);
            LineTo(r.Left, r.Bottom - radii[3].Y);
            ArcTo(radii[3].X, radii[3].Y, 0f, SKPathArcSize.Small, SKPathDirection.CounterClockwise, r.Left + radii[3].X, r.Bottom);
            LineTo(r.Right - radii[2].X, r.Bottom);
            ArcTo(radii[2].X, radii[2].Y, 0f, SKPathArcSize.Small, SKPathDirection.CounterClockwise, r.Right, r.Bottom - radii[2].Y);
            LineTo(r.Right, r.Top + radii[1].Y);
            ArcTo(radii[1].X, radii[1].Y, 0f, SKPathArcSize.Small, SKPathDirection.CounterClockwise, r.Right - radii[1].X, r.Top);
            LineTo(r.Left + radii[0].X, r.Top);
            ArcTo(radii[0].X, radii[0].Y, 0f, SKPathArcSize.Small, SKPathDirection.CounterClockwise, r.Left, r.Top + radii[0].Y);
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
            Geometry.Figures.Add(CloneFigure(fig, Vector2.Zero));
        }
        _currentFigure = null;
    }

    public void AddPath(SKPath other, float x, float y)
    {
        var offset = new Vector2(x, y);
        foreach (var fig in other.Geometry.Figures)
        {
            Geometry.Figures.Add(CloneFigure(fig, offset));
        }
        _currentFigure = null;
    }

    public void AddPath(SKPath other, float x, float y, SKPathAddMode mode)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (mode == SKPathAddMode.Extend &&
            _currentFigure != null &&
            other.Geometry.Figures.Count > 0)
        {
            var start = other.Geometry.Figures[0].StartPoint + new Vector2(x, y);
            _currentFigure.Segments.Add(new LineSegment(start));
        }

        AddPath(other, x, y);
    }

    public void AddPath(SKPath other, SKPathAddMode mode = SKPathAddMode.Append) =>
        AddPath(other, 0f, 0f, mode);

    public void AddPath(SKPath other, in SKMatrix matrix, SKPathAddMode mode = SKPathAddMode.Append)
    {
        using var copy = new SKPath(other);
        copy.Transform(matrix);
        AddPath(copy, mode);
    }

    public void AddPoly(ReadOnlySpan<SKPoint> points, bool close = true)
    {
        if (points.IsEmpty)
        {
            return;
        }

        MoveTo(points[0]);
        for (var i = 1; i < points.Length; i++)
        {
            LineTo(points[i]);
        }

        if (close)
        {
            Close();
        }
    }

    public void AddPoly(SKPoint[] points, bool close = true)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddPoly(points.AsSpan(), close);
    }

    public SKPathRawIterator CreateRawIterator() => new(this, forceClose: false);

    private static PathFigure CloneFigure(PathFigure figure, Vector2 offset)
    {
        var copy = new PathFigure(figure.StartPoint + offset, figure.IsClosed)
        {
            IsFilled = figure.IsFilled
        };
        foreach (var segment in figure.Segments)
        {
            copy.Segments.Add(CloneSegment(segment, offset));
        }

        return copy;
    }

    public void Transform(SKMatrix matrix)
    {
        var m = matrix.ToMatrix4x4();
        foreach (var fig in Geometry.Figures)
        {
            var sourceCurrentPoint = fig.StartPoint;
            fig.StartPoint = Vector2.Transform(fig.StartPoint, m);
            for (int i = 0; i < fig.Segments.Count; i++)
            {
                var seg = fig.Segments[i];
                if (seg is LineSegment line)
                {
                    sourceCurrentPoint = line.Point;
                    line.Point = Vector2.Transform(line.Point, m);
                }
                else if (seg is QuadraticBezierSegment quad)
                {
                    sourceCurrentPoint = quad.Point;
                    quad.ControlPoint = Vector2.Transform(quad.ControlPoint, m);
                    quad.Point = Vector2.Transform(quad.Point, m);
                }
                else if (seg is CubicBezierSegment cubic)
                {
                    sourceCurrentPoint = cubic.Point;
                    cubic.ControlPoint1 = Vector2.Transform(cubic.ControlPoint1, m);
                    cubic.ControlPoint2 = Vector2.Transform(cubic.ControlPoint2, m);
                    cubic.Point = Vector2.Transform(cubic.Point, m);
                }
                else if (seg is ArcSegment arc)
                {
                    var sourceEndPoint = arc.Point;
                    if (ArcSegmentGeometry.TryTransformArcSegment(
                            sourceCurrentPoint,
                            arc,
                            m,
                            out _,
                            out var transformedArc))
                    {
                        fig.Segments[i] = transformedArc;
                    }
                    else
                    {
                        fig.Segments[i] = new LineSegment(
                            Vector2.Transform(arc.Point, m),
                            arc.IsSmoothJoin,
                            arc.IsStroked);
                    }

                    sourceCurrentPoint = sourceEndPoint;
                }
            }
        }
        _currentFigure = null;
    }

    private static PathSegment CloneSegment(PathSegment segment, Vector2 offset)
    {
        return segment switch
        {
            LineSegment line => new LineSegment(
                line.Point + offset,
                line.IsSmoothJoin,
                line.IsStroked),
            QuadraticBezierSegment quad => new QuadraticBezierSegment(
                quad.ControlPoint + offset,
                quad.Point + offset,
                quad.IsSmoothJoin,
                quad.IsStroked),
            CubicBezierSegment cubic => new CubicBezierSegment(
                cubic.ControlPoint1 + offset,
                cubic.ControlPoint2 + offset,
                cubic.Point + offset,
                cubic.IsSmoothJoin,
                cubic.IsStroked),
            ArcSegment arc => new ArcSegment(
                arc.Point + offset,
                arc.Size,
                arc.RotationAngle,
                arc.IsLargeArc,
                arc.SweepDirection,
                arc.IsSmoothJoin,
                arc.IsStroked),
            _ => throw new NotSupportedException($"Unsupported SKPath segment type '{segment.GetType().FullName}'.")
        };
    }

    public SKPath Op(SKPath other, SKPathOp op)
    {
        var result = new SKPath();
        var solvedGeometry = PathOpGeometrySolver.Combine(this.Geometry, other.Geometry, (int)op);
        ApplySolvedGeometry(result, solvedGeometry);
        return result;
    }

    public static SKPath Op(SKPath first, SKPath second, SKPathOp op)
    {
        return first.Op(second, op);
    }

    public static bool Op(SKPath first, SKPath second, SKPathOp op, SKPath result)
    {
        if (result == null) return false;
        var solvedGeometry = PathOpGeometrySolver.Combine(first.Geometry, second.Geometry, (int)op);
        ApplySolvedGeometry(result, solvedGeometry);
        return true;
    }

    private static void ApplySolvedGeometry(SKPath result, PathGeometry solvedGeometry)
    {
        result.Geometry.Figures.Clear();
        result.FillType = ToSkPathFillType(solvedGeometry.FillRule);
        foreach (var fig in solvedGeometry.Figures)
        {
            result.Geometry.Figures.Add(fig);
        }

        result._currentFigure = null;
    }

    private static SKPathFillType ToSkPathFillType(FillRule fillRule)
    {
        return fillRule == FillRule.EvenOdd
            ? SKPathFillType.EvenOdd
            : SKPathFillType.Winding;
    }


    public void Dispose() { }
}

public class SKRegion : IDisposable
{
    private readonly List<SKRectI> _rects = new();
    private SKRectI _bounds;

    public bool IsEmpty => _rects.Count == 0;

    public SKRectI Bounds => _bounds;

    public SKRegion() { }

    internal IReadOnlyList<SKRectI> Rects => _rects;

    public bool Contains(int x, int y)
    {
        foreach (var rect in _rects)
        {
            if (Contains(rect, x, y))
            {
                return true;
            }
        }

        return false;
    }

    public bool SetPath(SKPath path)
    {
        if (!TryGetSingleAxisAlignedRect(path, out var rect))
        {
            _rects.Clear();
            _bounds = SKRectI.Empty;
            return false;
        }

        SetSingleRect(rect);
        return !IsEmpty;
    }

    public bool SetRect(SKRectI rect)
    {
        SetSingleRect(rect);
        return !IsEmpty;
    }

    public bool Op(SKRectI rect, SKRegionOperation op)
    {
        switch (op)
        {
            case SKRegionOperation.Replace:
                SetSingleRect(rect);
                break;
            case SKRegionOperation.Intersect:
                IntersectWith(rect);
                break;
            case SKRegionOperation.Union:
                AddRect(rect);
                break;
            case SKRegionOperation.Difference:
                DifferenceWith(rect);
                break;
            case SKRegionOperation.ReverseDifference:
                ReverseDifferenceWith(rect);
                break;
            case SKRegionOperation.XOR:
                XorWith(rect);
                break;
            default:
                return false;
        }

        UpdateBounds();
        return !IsEmpty;
    }

    public bool Op(int left, int top, int right, int bottom, SKRegionOperation op)
    {
        return Op(new SKRectI(left, top, right, bottom), op);
    }

    public void SetEmpty()
    {
        _rects.Clear();
        _bounds = SKRectI.Empty;
    }

    public bool Intersects(SKRectI rect)
    {
        foreach (var existing in _rects)
        {
            if (IsValid(Intersect(existing, rect)))
            {
                return true;
            }
        }

        return false;
    }

    public SKRegionRectIterator CreateRectIterator()
    {
        return new SKRegionRectIterator(_rects);
    }

    private void SetSingleRect(SKRectI rect)
    {
        _rects.Clear();
        AddRect(rect);
        UpdateBounds();
    }

    private void AddRect(SKRectI rect)
    {
        if (!IsValid(rect))
        {
            return;
        }

        _rects.Add(rect);
    }

    private void IntersectWith(SKRectI rect)
    {
        if (!IsValid(rect))
        {
            _rects.Clear();
            return;
        }

        for (int i = _rects.Count - 1; i >= 0; i--)
        {
            var intersection = Intersect(_rects[i], rect);
            if (IsValid(intersection))
            {
                _rects[i] = intersection;
            }
            else
            {
                _rects.RemoveAt(i);
            }
        }
    }

    private void DifferenceWith(SKRectI rect)
    {
        if (!IsValid(rect) || _rects.Count == 0)
        {
            return;
        }

        var result = new List<SKRectI>(_rects.Count);
        foreach (var source in _rects)
        {
            AddDifference(result, source, rect);
        }

        _rects.Clear();
        _rects.AddRange(result);
    }

    private void ReverseDifferenceWith(SKRectI rect)
    {
        var result = new List<SKRectI>();
        AddIfValid(result, rect);
        foreach (var existing in _rects)
        {
            for (int i = result.Count - 1; i >= 0; i--)
            {
                var current = result[i];
                result.RemoveAt(i);
                AddDifference(result, current, existing);
            }
        }

        _rects.Clear();
        _rects.AddRange(result);
    }

    private void XorWith(SKRectI rect)
    {
        var left = new List<SKRectI>();
        foreach (var existing in _rects)
        {
            AddDifference(left, existing, rect);
        }

        var right = new List<SKRectI>();
        AddIfValid(right, rect);
        foreach (var existing in _rects)
        {
            for (int i = right.Count - 1; i >= 0; i--)
            {
                var current = right[i];
                right.RemoveAt(i);
                AddDifference(right, current, existing);
            }
        }

        _rects.Clear();
        _rects.AddRange(left);
        _rects.AddRange(right);
    }

    private static void AddDifference(List<SKRectI> result, SKRectI source, SKRectI cutter)
    {
        if (!IsValid(source))
        {
            return;
        }

        var overlap = Intersect(source, cutter);
        if (!IsValid(overlap))
        {
            result.Add(source);
            return;
        }

        AddIfValid(result, new SKRectI(source.Left, source.Top, source.Right, overlap.Top));
        AddIfValid(result, new SKRectI(source.Left, overlap.Bottom, source.Right, source.Bottom));
        AddIfValid(result, new SKRectI(source.Left, overlap.Top, overlap.Left, overlap.Bottom));
        AddIfValid(result, new SKRectI(overlap.Right, overlap.Top, source.Right, overlap.Bottom));
    }

    private static void AddIfValid(List<SKRectI> result, SKRectI rect)
    {
        if (IsValid(rect))
        {
            result.Add(rect);
        }
    }

    private void UpdateBounds()
    {
        if (_rects.Count == 0)
        {
            _bounds = SKRectI.Empty;
            return;
        }

        var bounds = _rects[0];
        for (int i = 1; i < _rects.Count; i++)
        {
            var rect = _rects[i];
            bounds = new SKRectI(
                Math.Min(bounds.Left, rect.Left),
                Math.Min(bounds.Top, rect.Top),
                Math.Max(bounds.Right, rect.Right),
                Math.Max(bounds.Bottom, rect.Bottom));
        }

        _bounds = bounds;
    }

    private static SKRectI Intersect(SKRectI left, SKRectI right)
    {
        return new SKRectI(
            Math.Max(left.Left, right.Left),
            Math.Max(left.Top, right.Top),
            Math.Min(left.Right, right.Right),
            Math.Min(left.Bottom, right.Bottom));
    }

    private static bool Contains(SKRectI rect, int x, int y)
    {
        return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
    }

    private static bool IsValid(SKRectI rect)
    {
        return rect.Width > 0 && rect.Height > 0;
    }

    private static bool TryGetSingleAxisAlignedRect(SKPath path, out SKRectI rect)
    {
        rect = SKRectI.Empty;
        if (path.Geometry.Figures.Count != 1)
        {
            return false;
        }

        var figure = path.Geometry.Figures[0];
        if (!figure.IsClosed || figure.Segments.Count != 3)
        {
            return false;
        }

        Span<Vector2> points = stackalloc Vector2[4];
        points[0] = figure.StartPoint;
        for (int i = 0; i < figure.Segments.Count; i++)
        {
            if (figure.Segments[i] is not LineSegment line)
            {
                return false;
            }

            points[i + 1] = line.Point;
        }

        float left = points[0].X;
        float right = points[0].X;
        float top = points[0].Y;
        float bottom = points[0].Y;
        for (int i = 1; i < points.Length; i++)
        {
            left = MathF.Min(left, points[i].X);
            right = MathF.Max(right, points[i].X);
            top = MathF.Min(top, points[i].Y);
            bottom = MathF.Max(bottom, points[i].Y);
        }

        if (!float.IsFinite(left) ||
            !float.IsFinite(right) ||
            !float.IsFinite(top) ||
            !float.IsFinite(bottom) ||
            right <= left ||
            bottom <= top)
        {
            return false;
        }

        bool hasTopLeft = false;
        bool hasTopRight = false;
        bool hasBottomRight = false;
        bool hasBottomLeft = false;
        foreach (var point in points)
        {
            if (Near(point.X, left) && Near(point.Y, top))
            {
                hasTopLeft = true;
            }
            else if (Near(point.X, right) && Near(point.Y, top))
            {
                hasTopRight = true;
            }
            else if (Near(point.X, right) && Near(point.Y, bottom))
            {
                hasBottomRight = true;
            }
            else if (Near(point.X, left) && Near(point.Y, bottom))
            {
                hasBottomLeft = true;
            }
            else
            {
                return false;
            }
        }

        if (!hasTopLeft || !hasTopRight || !hasBottomRight || !hasBottomLeft)
        {
            return false;
        }

        rect = new SKRectI(
            (int)MathF.Floor(left),
            (int)MathF.Floor(top),
            (int)MathF.Ceiling(right),
            (int)MathF.Ceiling(bottom));
        return IsValid(rect);
    }

    private static bool Near(float left, float right)
    {
        return MathF.Abs(left - right) <= 0.0001f;
    }

    public void Dispose() { }
}

public enum SKPathVerb
{
    Move = 0,
    Line = 1,
    Quad = 2,
    Conic = 3,
    Cubic = 4,
    Close = 5,
    Done = 6
}

public sealed class SKPathRawIterator : IDisposable
{
    private readonly List<PathOperation> _operations = new();
    private int _index;
    private float _conicWeight = 1f;

    internal SKPathRawIterator(SKPath path, bool forceClose)
    {
        foreach (var figure in path.Geometry.Figures)
        {
            var current = figure.StartPoint;
            _operations.Add(new PathOperation(SKPathVerb.Move, current, default, default, default));
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        _operations.Add(new PathOperation(SKPathVerb.Line, current, line.Point, default, default));
                        current = line.Point;
                        break;
                    case QuadraticBezierSegment quadratic:
                        _operations.Add(new PathOperation(
                            SKPathVerb.Quad,
                            current,
                            quadratic.ControlPoint,
                            quadratic.Point,
                            default));
                        current = quadratic.Point;
                        break;
                    case CubicBezierSegment cubic:
                        _operations.Add(new PathOperation(
                            SKPathVerb.Cubic,
                            current,
                            cubic.ControlPoint1,
                            cubic.ControlPoint2,
                            cubic.Point));
                        current = cubic.Point;
                        break;
                    case ArcSegment arc:
                        var flattened = ArcSegmentGeometry.FlattenArc(current, arc, MathF.PI / 32f);
                        for (var i = 1; i < flattened.Length; i++)
                        {
                            _operations.Add(new PathOperation(
                                SKPathVerb.Line,
                                flattened[i - 1],
                                flattened[i],
                                default,
                                default));
                        }

                        current = arc.Point;
                        break;
                }
            }

            if (figure.IsClosed || forceClose)
            {
                _operations.Add(new PathOperation(SKPathVerb.Close, current, figure.StartPoint, default, default));
            }
        }
    }

    public SKPathVerb Next(SKPoint[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (_index >= _operations.Count)
        {
            return SKPathVerb.Done;
        }

        var operation = _operations[_index++];
        SetPoint(points, 0, operation.P0);
        SetPoint(points, 1, operation.P1);
        SetPoint(points, 2, operation.P2);
        SetPoint(points, 3, operation.P3);
        _conicWeight = 1f;
        return operation.Verb;
    }

    public float ConicWeight() => _conicWeight;

    public void Dispose()
    {
        _operations.Clear();
    }

    private static void SetPoint(SKPoint[] points, int index, Vector2 value)
    {
        if (index < points.Length)
        {
            points[index] = new SKPoint(value.X, value.Y);
        }
    }

    private readonly record struct PathOperation(
        SKPathVerb Verb,
        Vector2 P0,
        Vector2 P1,
        Vector2 P2,
        Vector2 P3);
}

public sealed class SKRegionRectIterator : IDisposable
{
    private readonly SKRectI[] _rects;
    private int _index;

    internal SKRegionRectIterator(IReadOnlyList<SKRectI> rects)
    {
        _rects = new SKRectI[rects.Count];
        for (var i = 0; i < rects.Count; i++)
        {
            _rects[i] = rects[i];
        }
    }

    public bool Next(out SKRectI rect)
    {
        if (_index >= _rects.Length)
        {
            rect = default;
            return false;
        }

        rect = _rects[_index++];
        return true;
    }

    public void Dispose()
    {
    }
}
