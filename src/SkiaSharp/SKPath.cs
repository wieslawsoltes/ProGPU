using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using ProGPU.Vector;

namespace SkiaSharp;

public partial class SKPath : IDisposable
{
    private static readonly ConditionalWeakTable<QuadraticBezierSegment, ConicWeight> s_conicWeights = new();

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

    public static SKPoint[] ConvertConicToQuads(
        SKPoint p0,
        SKPoint p1,
        SKPoint p2,
        float weight,
        int pow2)
    {
        var pointCount = GetConicQuadPointCount(pow2);
        var points = new SKPoint[pointCount];
        ConvertConicToQuadsCore(p0, p1, p2, weight, points, pow2);
        return points;
    }

    public static int ConvertConicToQuads(
        SKPoint p0,
        SKPoint p1,
        SKPoint p2,
        float weight,
        SKPoint[] points,
        int pow2)
    {
        ArgumentNullException.ThrowIfNull(points);
        var pointCount = GetConicQuadPointCount(pow2);
        if (points.Length < pointCount)
        {
            throw new ArgumentException(
                $"The destination requires at least {pointCount} points.",
                nameof(points));
        }

        ConvertConicToQuadsCore(p0, p1, p2, weight, points, pow2);
        return 1 << pow2;
    }

    public static int ConvertConicToQuads(
        SKPoint p0,
        SKPoint p1,
        SKPoint p2,
        float weight,
        out SKPoint[] points,
        int pow2)
    {
        points = ConvertConicToQuads(p0, p1, p2, weight, pow2);
        return 1 << pow2;
    }

    private static int GetConicQuadPointCount(int pow2)
    {
        if ((uint)pow2 > 20u)
        {
            throw new ArgumentOutOfRangeException(nameof(pow2));
        }

        return checked((1 << pow2) * 2 + 1);
    }

    private static void ConvertConicToQuadsCore(
        SKPoint p0,
        SKPoint p1,
        SKPoint p2,
        float weight,
        Span<SKPoint> points,
        int pow2)
    {
        var pointIndex = 0;
        Subdivide(
            new Vector2(p0.X, p0.Y),
            new Vector2(p1.X, p1.Y),
            new Vector2(p2.X, p2.Y),
            weight,
            pow2,
            writeStart: true,
            points,
            ref pointIndex);

        static void Subdivide(
            Vector2 start,
            Vector2 control,
            Vector2 end,
            float weight,
            int depth,
            bool writeStart,
            Span<SKPoint> output,
            ref int outputIndex)
        {
            if (depth == 0)
            {
                if (writeStart)
                {
                    output[outputIndex++] = ToSkPoint(start);
                }
                output[outputIndex++] = ToSkPoint(control);
                output[outputIndex++] = ToSkPoint(end);
                return;
            }

            var inverseWeightSum = 1f / (1f + weight);
            var leftControl = (start + control * weight) * inverseWeightSum;
            var rightControl = (control * weight + end) * inverseWeightSum;
            var middle = (start + control * (2f * weight) + end) *
                (inverseWeightSum * 0.5f);
            var childWeight = MathF.Sqrt((1f + weight) * 0.5f);
            Subdivide(
                start,
                leftControl,
                middle,
                childWeight,
                depth - 1,
                writeStart,
                output,
                ref outputIndex);
            Subdivide(
                middle,
                rightControl,
                end,
                childWeight,
                depth - 1,
                writeStart: false,
                output,
                ref outputIndex);
        }

        static SKPoint ToSkPoint(Vector2 point) => new(point.X, point.Y);
    }

    public string ToSvgPathData()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Max(32, VerbCount * 16));
        using var iterator = CreateIterator(forceClose: false);
        Span<SKPoint> points = stackalloc SKPoint[4];
        while (true)
        {
            var verb = iterator.Next(points);
            switch (verb)
            {
                case SKPathVerb.Move:
                    AppendPoint(builder, 'M', points[0]);
                    break;
                case SKPathVerb.Line:
                    AppendPoint(builder, 'L', points[1]);
                    break;
                case SKPathVerb.Quad:
                case SKPathVerb.Conic:
                    builder.Append('Q');
                    AppendCoordinates(builder, points[1]);
                    builder.Append(' ');
                    AppendCoordinates(builder, points[2]);
                    break;
                case SKPathVerb.Cubic:
                    builder.Append('C');
                    AppendCoordinates(builder, points[1]);
                    builder.Append(' ');
                    AppendCoordinates(builder, points[2]);
                    builder.Append(' ');
                    AppendCoordinates(builder, points[3]);
                    break;
                case SKPathVerb.Close:
                    builder.Append('Z');
                    break;
                case SKPathVerb.Done:
                    return builder.ToString();
            }
        }

        static void AppendPoint(StringBuilder builder, char verb, SKPoint point)
        {
            builder.Append(verb);
            AppendCoordinates(builder, point);
        }

        static void AppendCoordinates(StringBuilder builder, SKPoint point)
        {
            AppendScalar(builder, point.X);
            builder.Append(' ');
            AppendScalar(builder, point.Y);
        }

        static void AppendScalar(StringBuilder builder, float value)
        {
            if (value == 0f)
            {
                value = 0f;
            }

            builder.Append(value.ToString("G9", CultureInfo.InvariantCulture));
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
        Span<Vector2> arcPoints = stackalloc Vector2[8];
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
        Span<Vector2> arcPoints = stackalloc Vector2[8];
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
                    QuadraticBezierSegment quadratic => TryGetConicWeight(quadratic, out _)
                        ? SKPathSegmentMask.Conic
                        : SKPathSegmentMask.Quad,
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
        Span<Vector2> points,
        Span<float> weights = default)
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
            if (index < weights.Length)
            {
                weights[index] = weight;
            }
            var controlCos = SnapUnitTrigonometricValue(MathF.Cos(middle) / weight);
            var controlSin = SnapUnitTrigonometricValue(MathF.Sin(middle) / weight);
            points[index * 2] = center + axisX * controlCos + axisY * controlSin;
            points[index * 2 + 1] = index == conicCount - 1
                ? arc.Point
                : center +
                    axisX * SnapUnitTrigonometricValue(MathF.Cos(segmentEnd)) +
                    axisY * SnapUnitTrigonometricValue(MathF.Sin(segmentEnd));
        }

        return conicCount * 2;
    }

    private static float SnapUnitTrigonometricValue(float value)
    {
        const float epsilon = 1e-5f;
        if (MathF.Abs(value) <= epsilon)
        {
            return 0f;
        }
        if (MathF.Abs(value - 1f) <= epsilon)
        {
            return 1f;
        }
        if (MathF.Abs(value + 1f) <= epsilon)
        {
            return -1f;
        }

        return value;
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
            _currentFigure = new PathFigure(GetCurrentCommandPoint());
            Geometry.Figures.Add(_currentFigure);
        }
    }

    private Vector2 GetCurrentCommandPoint()
    {
        if (_currentFigure != null)
        {
            return GetFigureEndPoint(_currentFigure);
        }
        if (Geometry.Figures.Count == 0)
        {
            return Vector2.Zero;
        }

        var figure = Geometry.Figures[^1];
        return figure.IsClosed
            ? figure.StartPoint
            : GetFigureEndPoint(figure);
    }

    public void MoveTo(float x, float y)
    {
        if (_currentFigure != null && _currentFigure.Segments.Count == 0)
        {
            _currentFigure.StartPoint = new Vector2(x, y);
            return;
        }

        _currentFigure = new PathFigure(new Vector2(x, y));
        Geometry.Figures.Add(_currentFigure);
    }

    public void MoveTo(SKPoint p) => MoveTo(p.X, p.Y);

    public void RMoveTo(float dx, float dy)
    {
        var current = GetCurrentCommandPoint();
        MoveTo(current.X + dx, current.Y + dy);
    }

    public void RMoveTo(SKPoint delta) => RMoveTo(delta.X, delta.Y);

    public void LineTo(float x, float y)
    {
        EnsureFigure();
        _currentFigure!.Segments.Add(new LineSegment(new Vector2(x, y)));
    }

    public void LineTo(SKPoint p) => LineTo(p.X, p.Y);

    public void RLineTo(float dx, float dy)
    {
        var current = GetCurrentCommandPoint();
        LineTo(current.X + dx, current.Y + dy);
    }

    public void RLineTo(SKPoint delta) => RLineTo(delta.X, delta.Y);

    public void QuadTo(float x0, float y0, float x1, float y1)
    {
        EnsureFigure();
        _currentFigure!.Segments.Add(new QuadraticBezierSegment(new Vector2(x0, y0), new Vector2(x1, y1)));
    }

    public void QuadTo(SKPoint p0, SKPoint p1) => QuadTo(p0.X, p0.Y, p1.X, p1.Y);

    public void RQuadTo(float dx0, float dy0, float dx1, float dy1)
    {
        var current = GetCurrentCommandPoint();
        QuadTo(
            current.X + dx0,
            current.Y + dy0,
            current.X + dx1,
            current.Y + dy1);
    }

    public void RQuadTo(SKPoint controlDelta, SKPoint endDelta) =>
        RQuadTo(controlDelta.X, controlDelta.Y, endDelta.X, endDelta.Y);

    public void CubicTo(float x0, float y0, float x1, float y1, float x2, float y2)
    {
        EnsureFigure();
        _currentFigure!.Segments.Add(new CubicBezierSegment(new Vector2(x0, y0), new Vector2(x1, y1), new Vector2(x2, y2)));
    }

    public void CubicTo(SKPoint p0, SKPoint p1, SKPoint p2) => CubicTo(p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y);

    public void RCubicTo(
        float dx0,
        float dy0,
        float dx1,
        float dy1,
        float dx2,
        float dy2)
    {
        var current = GetCurrentCommandPoint();
        CubicTo(
            current.X + dx0,
            current.Y + dy0,
            current.X + dx1,
            current.Y + dy1,
            current.X + dx2,
            current.Y + dy2);
    }

    public void RCubicTo(SKPoint control1Delta, SKPoint control2Delta, SKPoint endDelta) =>
        RCubicTo(
            control1Delta.X,
            control1Delta.Y,
            control2Delta.X,
            control2Delta.Y,
            endDelta.X,
            endDelta.Y);

    public void ArcTo(float rx, float ry, float xAxisRotation, SKPathArcSize largeArc, SKPathDirection sweep, float x, float y)
    {
        EnsureFigure();
        var sd = sweep == SKPathDirection.Clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
        _currentFigure!.Segments.Add(new ArcSegment(new Vector2(x, y), new Vector2(rx, ry), xAxisRotation, largeArc == SKPathArcSize.Large, sd));
    }

    public void ArcTo(
        SKPoint radius,
        float xAxisRotation,
        SKPathArcSize largeArc,
        SKPathDirection sweep,
        SKPoint end) =>
        ArcTo(
            radius.X,
            radius.Y,
            xAxisRotation,
            largeArc,
            sweep,
            end.X,
            end.Y);

    public void ArcTo(SKPoint point1, SKPoint point2, float radius) =>
        ArcTo(point1.X, point1.Y, point2.X, point2.Y, radius);

    public void ArcTo(float x1, float y1, float x2, float y2, float radius)
    {
        var current = GetCurrentCommandPoint();
        var corner = new Vector2(x1, y1);
        var next = new Vector2(x2, y2);
        var incoming = current - corner;
        var outgoing = next - corner;
        var incomingLength = incoming.Length();
        var outgoingLength = outgoing.Length();
        if (!float.IsFinite(radius) ||
            radius <= 0f ||
            incomingLength <= 1e-6f ||
            outgoingLength <= 1e-6f)
        {
            LineTo(x1, y1);
            return;
        }

        incoming /= incomingLength;
        outgoing /= outgoingLength;
        var dot = Math.Clamp(Vector2.Dot(incoming, outgoing), -1f, 1f);
        var cross = incoming.X * outgoing.Y - incoming.Y * outgoing.X;
        var halfAngle = MathF.Acos(dot) * 0.5f;
        var tangent = MathF.Tan(halfAngle);
        if (MathF.Abs(cross) <= 1e-6f ||
            !float.IsFinite(tangent) ||
            MathF.Abs(tangent) <= 1e-6f)
        {
            LineTo(x1, y1);
            return;
        }

        var tangentDistance = radius / tangent;
        var arcStart = corner + incoming * tangentDistance;
        var arcEnd = corner + outgoing * tangentDistance;
        LineTo(arcStart.X, arcStart.Y);
        ArcTo(
            radius,
            radius,
            0f,
            SKPathArcSize.Small,
            cross < 0f ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise,
            arcEnd.X,
            arcEnd.Y);
    }

    public void ArcTo(SKRect oval, float startAngle, float sweepAngle, bool forceMoveTo)
    {
        if (!TryGetOvalArc(oval, startAngle, sweepAngle, out var arc))
        {
            return;
        }

        if (forceMoveTo || IsEmpty)
        {
            MoveTo(arc.Start.X, arc.Start.Y);
        }
        else if (!NearlyEqual(GetCurrentCommandPoint(), arc.Start))
        {
            LineTo(arc.Start.X, arc.Start.Y);
        }

        if (MathF.Abs(sweepAngle) >= 360f)
        {
            return;
        }

        AppendOvalArc(arc);
    }

    public void RArcTo(
        float rx,
        float ry,
        float xAxisRotation,
        SKPathArcSize largeArc,
        SKPathDirection sweep,
        float dx,
        float dy)
    {
        var current = GetCurrentCommandPoint();
        ArcTo(rx, ry, xAxisRotation, largeArc, sweep, current.X + dx, current.Y + dy);
    }

    public void RArcTo(
        SKPoint radius,
        float xAxisRotation,
        SKPathArcSize largeArc,
        SKPathDirection sweep,
        SKPoint delta) =>
        RArcTo(
            radius.X,
            radius.Y,
            xAxisRotation,
            largeArc,
            sweep,
            delta.X,
            delta.Y);

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

    public void Rewind() => Reset();

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

    public void AddArc(SKRect oval, float startAngle, float sweepAngle)
    {
        if (!TryGetOvalArc(oval, startAngle, sweepAngle, out var arc))
        {
            return;
        }

        MoveTo(arc.Start.X, arc.Start.Y);
        if (MathF.Abs(sweepAngle) >= 360f)
        {
            var signedHalfTurn = MathF.CopySign(180f, sweepAngle);
            var middle = EvaluateOvalPoint(oval, startAngle + signedHalfTurn);
            AppendEndpointArc(
                oval,
                middle,
                signedHalfTurn,
                isLargeArc: true);
            AppendEndpointArc(
                oval,
                arc.Start,
                signedHalfTurn,
                isLargeArc: true);
            Close();
            return;
        }

        AppendOvalArc(arc);
    }

    private static bool TryGetOvalArc(
        SKRect oval,
        float startAngle,
        float sweepAngle,
        out OvalArc arc)
    {
        arc = default;
        if (!float.IsFinite(startAngle) ||
            !float.IsFinite(sweepAngle) ||
            MathF.Abs(sweepAngle) <= 1e-6f ||
            !float.IsFinite(oval.Width) ||
            !float.IsFinite(oval.Height) ||
            MathF.Abs(oval.Width) <= 1e-6f ||
            MathF.Abs(oval.Height) <= 1e-6f)
        {
            return false;
        }

        var clampedSweep = Math.Clamp(sweepAngle, -360f, 360f);
        arc = new OvalArc(
            oval,
            EvaluateOvalPoint(oval, startAngle),
            EvaluateOvalPoint(oval, startAngle + clampedSweep),
            clampedSweep);
        return true;
    }

    private void AppendOvalArc(in OvalArc arc) =>
        AppendEndpointArc(
            arc.Oval,
            arc.End,
            arc.SweepAngle,
            MathF.Abs(arc.SweepAngle) >= 180f);

    private void AppendEndpointArc(
        SKRect oval,
        Vector2 end,
        float sweepAngle,
        bool isLargeArc)
    {
        EnsureFigure();
        _currentFigure!.Segments.Add(new ArcSegment(
            end,
            new Vector2(MathF.Abs(oval.Width) * 0.5f, MathF.Abs(oval.Height) * 0.5f),
            0f,
            isLargeArc,
            sweepAngle >= 0f ? SweepDirection.Clockwise : SweepDirection.Counterclockwise));
    }

    private static Vector2 EvaluateOvalPoint(SKRect oval, float angleDegrees)
    {
        var angle = angleDegrees * (MathF.PI / 180f);
        return new Vector2(
            oval.MidX + oval.Width * 0.5f * SnapUnitTrigonometricValue(MathF.Cos(angle)),
            oval.MidY + oval.Height * 0.5f * SnapUnitTrigonometricValue(MathF.Sin(angle)));
    }

    public void ConicTo(SKPoint control, SKPoint end, float weight)
    {
        EnsureFigure();
        var segment = new QuadraticBezierSegment(
            new Vector2(control.X, control.Y),
            new Vector2(end.X, end.Y));
        _currentFigure!.Segments.Add(segment);
        s_conicWeights.Add(segment, new ConicWeight(weight));
    }

    public void ConicTo(float x0, float y0, float x1, float y1, float weight) =>
        ConicTo(new SKPoint(x0, y0), new SKPoint(x1, y1), weight);

    public void RConicTo(float dx0, float dy0, float dx1, float dy1, float weight)
    {
        var current = GetCurrentCommandPoint();
        ConicTo(
            current.X + dx0,
            current.Y + dy0,
            current.X + dx1,
            current.Y + dy1,
            weight);
    }

    public void RConicTo(SKPoint controlDelta, SKPoint endDelta, float weight) =>
        RConicTo(controlDelta.X, controlDelta.Y, endDelta.X, endDelta.Y, weight);

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

    public Iterator CreateIterator(bool forceClose)
    {
        return new Iterator(this, forceClose);
    }

    public void AddRect(SKRect rect, SKPathDirection direction = SKPathDirection.Clockwise) =>
        AddRect(rect, direction, 0);

    public void AddRect(SKRect rect, SKPathDirection direction, uint startIndex)
    {
        if (startIndex > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startIndex),
                "Starting index must be in the range of 0..3 (inclusive).");
        }

        Span<Vector2> corners =
        [
            new Vector2(rect.Left, rect.Top),
            new Vector2(rect.Right, rect.Top),
            new Vector2(rect.Right, rect.Bottom),
            new Vector2(rect.Left, rect.Bottom),
        ];
        var index = (int)startIndex;
        MoveTo(corners[index].X, corners[index].Y);
        var step = direction == SKPathDirection.Clockwise ? 1 : -1;
        for (var point = 1; point < 4; point++)
        {
            index = (index + step + 4) % 4;
            LineTo(corners[index].X, corners[index].Y);
        }

        Close();
    }

    public void AddRoundRect(
        SKRoundRect rect,
        SKPathDirection direction = SKPathDirection.Clockwise) =>
        AddRoundRect(
            rect,
            direction,
            direction == SKPathDirection.Clockwise ? 0u : 7u);

    public void AddRoundRect(
        SKRoundRect rect,
        SKPathDirection direction,
        uint startIndex)
    {
        ArgumentNullException.ThrowIfNull(rect);
        if (startIndex > 7)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startIndex),
                "Starting index must be in the range of 0..7 (inclusive).");
        }

        var r = rect.Rect;
        var radii = rect.CornerRadii;
        Span<Vector2> points =
        [
            new Vector2(r.Left + radii[0].X, r.Top),
            new Vector2(r.Right - radii[1].X, r.Top),
            new Vector2(r.Right, r.Top + radii[1].Y),
            new Vector2(r.Right, r.Bottom - radii[2].Y),
            new Vector2(r.Right - radii[2].X, r.Bottom),
            new Vector2(r.Left + radii[3].X, r.Bottom),
            new Vector2(r.Left, r.Bottom - radii[3].Y),
            new Vector2(r.Left, r.Top + radii[0].Y),
        ];

        var index = (int)startIndex;
        MoveTo(points[index].X, points[index].Y);
        var clockwise = direction == SKPathDirection.Clockwise;
        var step = clockwise ? 1 : -1;
        for (var edge = 1; edge <= 8; edge++)
        {
            var next = (index + step + 8) % 8;
            var isArc = clockwise ? (index & 1) != 0 : (index & 1) == 0;
            if (edge == 8 && !isArc)
            {
                break;
            }

            if (isArc)
            {
                var corner = clockwise
                    ? ((index + 1) / 2) & 3
                    : (index / 2) & 3;
                var radius = radii[corner];
                if (radius.X > 0f && radius.Y > 0f)
                {
                    ArcTo(
                        radius.X,
                        radius.Y,
                        0f,
                        SKPathArcSize.Small,
                        direction,
                        points[next].X,
                        points[next].Y);
                }
                else
                {
                    LineTo(points[next].X, points[next].Y);
                }
            }
            else
            {
                LineTo(points[next].X, points[next].Y);
            }

            index = next;
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

    public void AddPathReverse(SKPath other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other))
        {
            using var copy = new SKPath(other);
            AddPathReverseCore(copy);
            return;
        }

        AddPathReverseCore(other);
    }

    private void AddPathReverseCore(SKPath source)
    {
        for (var figureIndex = source.Geometry.Figures.Count - 1;
             figureIndex >= 0;
             figureIndex--)
        {
            var figure = source.Geometry.Figures[figureIndex];
            var starts = new Vector2[figure.Segments.Count];
            var current = figure.StartPoint;
            for (var segmentIndex = 0; segmentIndex < figure.Segments.Count; segmentIndex++)
            {
                starts[segmentIndex] = current;
                current = GetSegmentEndPoint(figure.Segments[segmentIndex], current);
            }

            MoveTo(current.X, current.Y);
            for (var segmentIndex = figure.Segments.Count - 1;
                 segmentIndex >= 0;
                 segmentIndex--)
            {
                var segment = figure.Segments[segmentIndex];
                var end = starts[segmentIndex];
                switch (segment)
                {
                    case LineSegment:
                        LineTo(end.X, end.Y);
                        break;

                    case QuadraticBezierSegment quadratic:
                        if (TryGetConicWeight(quadratic, out var conicWeight))
                        {
                            ConicTo(
                                new SKPoint(quadratic.ControlPoint.X, quadratic.ControlPoint.Y),
                                new SKPoint(end.X, end.Y),
                                conicWeight);
                        }
                        else
                        {
                            QuadTo(
                                quadratic.ControlPoint.X,
                                quadratic.ControlPoint.Y,
                                end.X,
                                end.Y);
                        }
                        break;

                    case CubicBezierSegment cubic:
                        CubicTo(
                            cubic.ControlPoint2.X,
                            cubic.ControlPoint2.Y,
                            cubic.ControlPoint1.X,
                            cubic.ControlPoint1.Y,
                            end.X,
                            end.Y);
                        break;

                    case ArcSegment arc:
                        EnsureFigure();
                        _currentFigure!.Segments.Add(new ArcSegment(
                            end,
                            arc.Size,
                            arc.RotationAngle,
                            arc.IsLargeArc,
                            arc.SweepDirection == SweepDirection.Clockwise
                                ? SweepDirection.Counterclockwise
                                : SweepDirection.Clockwise,
                            arc.IsSmoothJoin,
                            arc.IsStroked));
                        break;
                }
            }

            if (figure.IsClosed)
            {
                Close();
            }
        }
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

    public RawIterator CreateRawIterator() => new(this);

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

    public void Offset(float dx, float dy) =>
        TransformCore(SKMatrix.CreateTranslation(dx, dy));

    public void Offset(SKPoint delta) => Offset(delta.X, delta.Y);

    public void Transform(SKMatrix matrix) => TransformCore(matrix);

    public void Transform(in SKMatrix matrix) => TransformCore(matrix);

    public void Transform(SKMatrix matrix, SKPath destination) =>
        TransformTo(matrix, destination);

    public void Transform(in SKMatrix matrix, SKPath destination) =>
        TransformTo(matrix, destination);

    private void TransformTo(in SKMatrix matrix, SKPath destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(this, destination))
        {
            TransformCore(matrix);
            return;
        }

        using var transformed = new SKPath(this);
        transformed.TransformCore(matrix);
        destination.ReplaceWith(transformed);
    }

    private void TransformCore(in SKMatrix matrix)
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
    }

    private void ReplaceWith(SKPath source)
    {
        Geometry.Figures.Clear();
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

    private static PathSegment CloneSegment(PathSegment segment, Vector2 offset)
    {
        return segment switch
        {
            LineSegment line => new LineSegment(
                line.Point + offset,
                line.IsSmoothJoin,
                line.IsStroked),
            QuadraticBezierSegment quad => CloneQuadraticSegment(quad, offset),
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

    private static QuadraticBezierSegment CloneQuadraticSegment(
        QuadraticBezierSegment source,
        Vector2 offset)
    {
        var clone = new QuadraticBezierSegment(
            source.ControlPoint + offset,
            source.Point + offset,
            source.IsSmoothJoin,
            source.IsStroked);
        if (TryGetConicWeight(source, out var weight))
        {
            s_conicWeights.Add(clone, new ConicWeight(weight));
        }

        return clone;
    }

    private static bool TryGetConicWeight(
        QuadraticBezierSegment segment,
        out float weight)
    {
        if (s_conicWeights.TryGetValue(segment, out var metadata))
        {
            weight = metadata.Value;
            return true;
        }

        weight = 1f;
        return false;
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

    private static List<PathIteratorOperation> BuildIteratorOperations(
        SKPath path,
        bool forceClose,
        bool raw)
    {
        var operations = new List<PathIteratorOperation>();
        Span<Vector2> arcPoints = stackalloc Vector2[8];
        Span<float> arcWeights = stackalloc float[4];
        foreach (var figure in path.Geometry.Figures)
        {
            var closesContour = figure.IsClosed || forceClose;
            var current = figure.StartPoint;
            operations.Add(new PathIteratorOperation(
                SKPathVerb.Move,
                current,
                default,
                default,
                default,
                0f,
                IsCloseLine: false,
                ClosesContour: closesContour));

            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        operations.Add(new PathIteratorOperation(
                            SKPathVerb.Line,
                            current,
                            line.Point,
                            default,
                            default,
                            0f,
                            IsCloseLine: false,
                            ClosesContour: closesContour));
                        current = line.Point;
                        break;

                    case QuadraticBezierSegment quadratic:
                        var isConic = TryGetConicWeight(quadratic, out var conicWeight);
                        operations.Add(new PathIteratorOperation(
                            isConic ? SKPathVerb.Conic : SKPathVerb.Quad,
                            current,
                            quadratic.ControlPoint,
                            quadratic.Point,
                            default,
                            conicWeight,
                            IsCloseLine: false,
                            ClosesContour: closesContour));
                        current = quadratic.Point;
                        break;

                    case CubicBezierSegment cubic:
                        operations.Add(new PathIteratorOperation(
                            SKPathVerb.Cubic,
                            current,
                            cubic.ControlPoint1,
                            cubic.ControlPoint2,
                            cubic.Point,
                            0f,
                            IsCloseLine: false,
                            ClosesContour: closesContour));
                        current = cubic.Point;
                        break;

                    case ArcSegment arc:
                        var arcPointCount = GetArcConicPoints(
                            current,
                            arc,
                            arcPoints,
                            arcWeights);
                        if (arcPointCount == 1)
                        {
                            operations.Add(new PathIteratorOperation(
                                SKPathVerb.Line,
                                current,
                                arc.Point,
                                default,
                                default,
                                0f,
                                IsCloseLine: false,
                                ClosesContour: closesContour));
                        }
                        else
                        {
                            for (var pointIndex = 0; pointIndex < arcPointCount; pointIndex += 2)
                            {
                                var end = arcPoints[pointIndex + 1];
                                operations.Add(new PathIteratorOperation(
                                    SKPathVerb.Conic,
                                    current,
                                    arcPoints[pointIndex],
                                    end,
                                    default,
                                    arcWeights[pointIndex / 2],
                                    IsCloseLine: false,
                                    ClosesContour: closesContour));
                                current = end;
                            }
                        }

                        current = arc.Point;
                        break;
                }
            }

            if (raw)
            {
                if (figure.IsClosed)
                {
                    operations.Add(new PathIteratorOperation(
                        SKPathVerb.Close,
                        default,
                        default,
                        default,
                        default,
                        0f,
                        IsCloseLine: false,
                        ClosesContour: true));
                }

                continue;
            }

            if (!closesContour)
            {
                continue;
            }

            if (!NearlyEqual(current, figure.StartPoint))
            {
                operations.Add(new PathIteratorOperation(
                    SKPathVerb.Line,
                    current,
                    figure.StartPoint,
                    default,
                    default,
                    0f,
                    IsCloseLine: true,
                    ClosesContour: closesContour));
            }

            operations.Add(new PathIteratorOperation(
                SKPathVerb.Close,
                figure.StartPoint,
                default,
                default,
                default,
                0f,
                IsCloseLine: true,
                ClosesContour: closesContour));
        }

        return operations;
    }

    private static void ValidateIteratorPoints(Span<SKPoint> points)
    {
        if (points.Length != 4)
        {
            throw new ArgumentException("Must be an array of four elements.", nameof(points));
        }
    }

    private static void WriteIteratorPoints(
        Span<SKPoint> points,
        in PathIteratorOperation operation,
        bool writeClosePoint)
    {
        var pointCount = operation.Verb switch
        {
            SKPathVerb.Move => 1,
            SKPathVerb.Line => 2,
            SKPathVerb.Quad or SKPathVerb.Conic => 3,
            SKPathVerb.Cubic => 4,
            SKPathVerb.Close when writeClosePoint => 1,
            _ => 0,
        };

        if (pointCount > 0)
        {
            points[0] = ToPoint(operation.P0);
        }
        if (pointCount > 1)
        {
            points[1] = ToPoint(operation.P1);
        }
        if (pointCount > 2)
        {
            points[2] = ToPoint(operation.P2);
        }
        if (pointCount > 3)
        {
            points[3] = ToPoint(operation.P3);
        }
    }

    public sealed class RawIterator : SKObject
    {
        private readonly List<PathIteratorOperation> _operations;
        private int _index;
        private float _conicWeight;

        internal RawIterator(SKPath path)
            : base(SKObjectHandle.Create(), owns: true)
        {
            _operations = BuildIteratorOperations(path, forceClose: false, raw: true);
        }

        public SKPathVerb Next(SKPoint[] points)
        {
            ArgumentNullException.ThrowIfNull(points);
            return Next(points.AsSpan());
        }

        public SKPathVerb Next(Span<SKPoint> points)
        {
            ValidateIteratorPoints(points);
            if (_index >= _operations.Count)
            {
                return SKPathVerb.Done;
            }

            var operation = _operations[_index++];
            WriteIteratorPoints(points, operation, writeClosePoint: false);
            if (operation.Verb == SKPathVerb.Conic)
            {
                _conicWeight = operation.ConicWeight;
            }

            return operation.Verb;
        }

        public float ConicWeight() => _conicWeight;

        public SKPathVerb Peek() => _index < _operations.Count
            ? _operations[_index].Verb
            : SKPathVerb.Done;

        protected override void DisposeManaged()
        {
            _operations.Clear();
        }
    }

    public sealed class Iterator : SKObject
    {
        private readonly List<PathIteratorOperation> _operations;
        private int _index;
        private float _conicWeight;
        private bool _isCloseLine;
        private bool _isCloseContour;

        internal Iterator(SKPath path, bool forceClose)
            : base(SKObjectHandle.Create(), owns: true)
        {
            _operations = BuildIteratorOperations(path, forceClose, raw: false);
        }

        public SKPathVerb Next(SKPoint[] points)
        {
            ArgumentNullException.ThrowIfNull(points);
            return Next(points.AsSpan());
        }

        public SKPathVerb Next(Span<SKPoint> points)
        {
            ValidateIteratorPoints(points);
            if (_index >= _operations.Count)
            {
                return SKPathVerb.Done;
            }

            var operation = _operations[_index++];
            WriteIteratorPoints(points, operation, writeClosePoint: true);
            if (operation.Verb == SKPathVerb.Conic)
            {
                _conicWeight = operation.ConicWeight;
            }
            if (operation.Verb == SKPathVerb.Line)
            {
                _isCloseLine = operation.IsCloseLine;
            }
            if (operation.Verb == SKPathVerb.Move)
            {
                _isCloseContour = operation.ClosesContour;
            }
            else if (operation.Verb == SKPathVerb.Close)
            {
                _isCloseContour = _index < _operations.Count &&
                    _operations[_index].Verb == SKPathVerb.Move &&
                    _operations[_index].ClosesContour;
            }

            return operation.Verb;
        }

        public float ConicWeight() => _conicWeight;

        public bool IsCloseLine() => _isCloseLine;

        public bool IsCloseContour() => _isCloseContour;

        protected override void DisposeManaged()
        {
            _operations.Clear();
        }
    }

    private sealed class ConicWeight
    {
        public ConicWeight(float value)
        {
            Value = value;
        }

        public float Value { get; }
    }

    private readonly record struct OvalArc(
        SKRect Oval,
        Vector2 Start,
        Vector2 End,
        float SweepAngle);

    private readonly record struct PathIteratorOperation(
        SKPathVerb Verb,
        Vector2 P0,
        Vector2 P1,
        Vector2 P2,
        Vector2 P3,
        float ConicWeight,
        bool IsCloseLine,
        bool ClosesContour);


    public void Dispose() { }
}

public class SKRegion : IDisposable
{
    private readonly List<SKRectI> _rects = new();
    private SKRectI _bounds;

    public bool IsEmpty => _rects.Count == 0;

    public bool IsRect => _rects.Count == 1;

    public bool IsComplex => _rects.Count > 1;

    public SKRectI Bounds => _bounds;

    public SKRegion() { }

    public SKRegion(SKRectI rect)
    {
        SetRect(rect);
    }

    public SKRegion(SKRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        SetRegion(region);
    }

    public SKRegion(SKPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var bounds = path.Bounds;
        using var clip = new SKRegion(new SKRectI(
            (int)MathF.Floor(bounds.Left),
            (int)MathF.Floor(bounds.Top),
            (int)MathF.Ceiling(bounds.Right),
            (int)MathF.Ceiling(bounds.Bottom)));
        SetPath(path, clip);
    }

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

    public bool Contains(SKPointI point) => Contains(point.X, point.Y);

    public bool Contains(SKRectI rect)
    {
        if (!IsValid(rect))
        {
            return false;
        }

        using var remainder = new SKRegion(rect);
        remainder.Op(this, SKRegionOperation.Difference);
        return remainder.IsEmpty;
    }

    public bool Contains(SKRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (region.IsEmpty)
        {
            return false;
        }

        using var remainder = new SKRegion(region);
        remainder.Op(this, SKRegionOperation.Difference);
        return remainder.IsEmpty;
    }

    public bool Contains(SKPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var region = new SKRegion(path);
        return Contains(region);
    }

    public bool QuickContains(SKRectI rect) => Contains(rect);

    public bool QuickReject(SKRectI rect) => !Intersects(rect);

    public bool QuickReject(SKRegion region) => !Intersects(region);

    public bool QuickReject(SKPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var bounds = path.Bounds;
        return QuickReject(new SKRectI(
            (int)MathF.Floor(bounds.Left),
            (int)MathF.Floor(bounds.Top),
            (int)MathF.Ceiling(bounds.Right),
            (int)MathF.Ceiling(bounds.Bottom)));
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

    public bool SetPath(SKPath path, SKRegion clip)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(clip);
        SetEmpty();
        if (path.IsEmpty || clip.IsEmpty)
        {
            return false;
        }

        var pathBounds = path.Bounds;
        var scanBounds = Intersect(
            clip.Bounds,
            new SKRectI(
                (int)MathF.Floor(pathBounds.Left),
                (int)MathF.Floor(pathBounds.Top),
                (int)MathF.Ceiling(pathBounds.Right),
                (int)MathF.Ceiling(pathBounds.Bottom)));
        if (!IsValid(scanBounds))
        {
            return false;
        }

        var polygons = BuildScanPolygons(path);
        if (polygons.Count == 0)
        {
            return false;
        }

        var previousRuns = new Dictionary<(int Left, int Right), int>();
        for (var y = scanBounds.Top; y < scanBounds.Bottom; y++)
        {
            var currentRuns = new Dictionary<(int Left, int Right), int>();
            var x = scanBounds.Left;
            while (x < scanBounds.Right)
            {
                while (x < scanBounds.Right &&
                       (!clip.Contains(x, y) ||
                        !ContainsScanPolygons(
                            polygons,
                            path.FillType,
                            new Vector2(x + 0.5f, y + 0.5f))))
                {
                    x++;
                }
                if (x >= scanBounds.Right)
                {
                    break;
                }

                var left = x++;
                while (x < scanBounds.Right &&
                       clip.Contains(x, y) &&
                       ContainsScanPolygons(
                           polygons,
                           path.FillType,
                           new Vector2(x + 0.5f, y + 0.5f)))
                {
                    x++;
                }

                var run = (Left: left, Right: x);
                if (previousRuns.TryGetValue(run, out var rectIndex) &&
                    _rects[rectIndex].Bottom == y)
                {
                    var previous = _rects[rectIndex];
                    _rects[rectIndex] = new SKRectI(
                        previous.Left,
                        previous.Top,
                        previous.Right,
                        y + 1);
                }
                else
                {
                    rectIndex = _rects.Count;
                    _rects.Add(new SKRectI(left, y, x, y + 1));
                }

                currentRuns[run] = rectIndex;
            }

            previousRuns = currentRuns;
        }

        NormalizeRects();
        UpdateBounds();
        return !IsEmpty;
    }

    private static List<Vector2[]> BuildScanPolygons(SKPath path)
    {
        const int quadraticSteps = 16;
        const int cubicSteps = 24;
        var polygons = new List<Vector2[]>();
        List<Vector2>? current = null;
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        while (true)
        {
            var verb = iterator.Next(points);
            switch (verb)
            {
                case SKPathVerb.Move:
                    FlushCurrent();
                    current = new List<Vector2>
                    {
                        new(points[0].X, points[0].Y),
                    };
                    break;

                case SKPathVerb.Line:
                    AddPoint(new Vector2(points[1].X, points[1].Y));
                    break;

                case SKPathVerb.Quad:
                    AppendQuadratic(
                        new Vector2(points[0].X, points[0].Y),
                        new Vector2(points[1].X, points[1].Y),
                        new Vector2(points[2].X, points[2].Y));
                    break;

                case SKPathVerb.Conic:
                    AppendConic(
                        new Vector2(points[0].X, points[0].Y),
                        new Vector2(points[1].X, points[1].Y),
                        new Vector2(points[2].X, points[2].Y),
                        iterator.ConicWeight());
                    break;

                case SKPathVerb.Cubic:
                    AppendCubic(
                        new Vector2(points[0].X, points[0].Y),
                        new Vector2(points[1].X, points[1].Y),
                        new Vector2(points[2].X, points[2].Y),
                        new Vector2(points[3].X, points[3].Y));
                    break;

                case SKPathVerb.Close:
                    FlushCurrent();
                    break;

                case SKPathVerb.Done:
                    FlushCurrent();
                    return polygons;
            }
        }

        void AppendQuadratic(Vector2 p0, Vector2 p1, Vector2 p2)
        {
            for (var step = 1; step <= quadraticSteps; step++)
            {
                var t = (float)step / quadraticSteps;
                var u = 1f - t;
                AddPoint(u * u * p0 + 2f * u * t * p1 + t * t * p2);
            }
        }

        void AppendConic(Vector2 p0, Vector2 p1, Vector2 p2, float weight)
        {
            for (var step = 1; step <= quadraticSteps; step++)
            {
                var t = (float)step / quadraticSteps;
                var u = 1f - t;
                var weightedMiddle = 2f * weight * u * t;
                var denominator = u * u + weightedMiddle + t * t;
                AddPoint(
                    (u * u * p0 + weightedMiddle * p1 + t * t * p2) /
                    denominator);
            }
        }

        void AppendCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            for (var step = 1; step <= cubicSteps; step++)
            {
                var t = (float)step / cubicSteps;
                var u = 1f - t;
                AddPoint(
                    u * u * u * p0 +
                    3f * u * u * t * p1 +
                    3f * u * t * t * p2 +
                    t * t * t * p3);
            }
        }

        void AddPoint(Vector2 point)
        {
            if (current == null ||
                !float.IsFinite(point.X) ||
                !float.IsFinite(point.Y))
            {
                return;
            }

            if (current.Count == 0 ||
                Vector2.DistanceSquared(current[^1], point) > 1e-12f)
            {
                current.Add(point);
            }
        }

        void FlushCurrent()
        {
            if (current is { Count: >= 3 })
            {
                polygons.Add(current.ToArray());
            }
            current = null;
        }
    }

    private static bool ContainsScanPolygons(
        List<Vector2[]> polygons,
        SKPathFillType fillType,
        Vector2 point)
    {
        var evenOdd = false;
        var winding = 0;
        foreach (var polygon in polygons)
        {
            var previous = polygon[^1];
            foreach (var current in polygon)
            {
                var upward = previous.Y <= point.Y && current.Y > point.Y;
                var downward = previous.Y > point.Y && current.Y <= point.Y;
                if (upward || downward)
                {
                    var intersectionX = previous.X +
                        (point.Y - previous.Y) *
                        (current.X - previous.X) /
                        (current.Y - previous.Y);
                    if (intersectionX > point.X)
                    {
                        evenOdd = !evenOdd;
                        winding += upward ? 1 : -1;
                    }
                }
                previous = current;
            }
        }

        var contains = fillType is SKPathFillType.EvenOdd or SKPathFillType.InverseEvenOdd
            ? evenOdd
            : winding != 0;
        return fillType is SKPathFillType.InverseEvenOdd or SKPathFillType.InverseWinding
            ? !contains
            : contains;
    }

    public bool SetRects(ReadOnlySpan<SKRectI> rects)
    {
        SetEmpty();
        foreach (var rect in rects)
        {
            AddRect(rect);
        }

        NormalizeRects();
        UpdateBounds();
        return !IsEmpty;
    }

    public bool SetRegion(SKRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (ReferenceEquals(this, region))
        {
            return !IsEmpty;
        }

        _rects.Clear();
        _rects.AddRange(region._rects);
        _bounds = region._bounds;
        return !IsEmpty;
    }

    public bool SetRect(SKRectI rect)
    {
        SetSingleRect(rect);
        return !IsEmpty;
    }

    public void Translate(int x, int y)
    {
        for (var index = 0; index < _rects.Count; index++)
        {
            var rect = _rects[index];
            _rects[index] = new SKRectI(
                unchecked(rect.Left + x),
                unchecked(rect.Top + y),
                unchecked(rect.Right + x),
                unchecked(rect.Bottom + y));
        }

        UpdateBounds();
    }

    public SKPath GetBoundaryPath()
    {
        var path = new SKPath();
        foreach (var rect in _rects)
        {
            path.AddRect(rect);
        }

        return path;
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

        NormalizeRects();
        UpdateBounds();
        return !IsEmpty;
    }

    public bool Op(SKPath path, SKRegionOperation op)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var region = new SKRegion(path);
        return Op(region, op);
    }

    public bool Op(SKRegion region, SKRegionOperation op)
    {
        ArgumentNullException.ThrowIfNull(region);
        using var leftSnapshot = new SKRegion(this);
        using var rightSnapshot = new SKRegion(region);
        switch (op)
        {
            case SKRegionOperation.Replace:
                return SetRegion(rightSnapshot);

            case SKRegionOperation.Intersect:
                _rects.Clear();
                foreach (var left in leftSnapshot._rects)
                {
                    foreach (var right in rightSnapshot._rects)
                    {
                        AddRect(Intersect(left, right));
                    }
                }
                break;

            case SKRegionOperation.Union:
                _rects.Clear();
                _rects.AddRange(leftSnapshot._rects);
                _rects.AddRange(rightSnapshot._rects);
                break;

            case SKRegionOperation.Difference:
                _rects.Clear();
                _rects.AddRange(leftSnapshot._rects);
                foreach (var cutter in rightSnapshot._rects)
                {
                    DifferenceWith(cutter);
                }
                break;

            case SKRegionOperation.ReverseDifference:
                _rects.Clear();
                _rects.AddRange(rightSnapshot._rects);
                foreach (var cutter in leftSnapshot._rects)
                {
                    DifferenceWith(cutter);
                }
                break;

            case SKRegionOperation.XOR:
                using (var leftOnly = new SKRegion(leftSnapshot))
                using (var rightOnly = new SKRegion(rightSnapshot))
                {
                    leftOnly.Op(rightSnapshot, SKRegionOperation.Difference);
                    rightOnly.Op(leftSnapshot, SKRegionOperation.Difference);
                    _rects.Clear();
                    _rects.AddRange(leftOnly._rects);
                    _rects.AddRange(rightOnly._rects);
                }
                break;

            default:
                return false;
        }

        NormalizeRects();
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

    public bool Intersects(SKRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        foreach (var rect in region._rects)
        {
            if (Intersects(rect))
            {
                return true;
            }
        }

        return false;
    }

    public bool Intersects(SKPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var region = new SKRegion(path);
        return Intersects(region);
    }

    public RectIterator CreateRectIterator() => new(_rects);

    public ClipIterator CreateClipIterator(SKRectI clip) => new(_rects, clip);

    public SpanIterator CreateSpanIterator(int y, int left, int right) =>
        new(_rects, y, left, right);

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

    private void NormalizeRects()
    {
        if (_rects.Count <= 1)
        {
            return;
        }

        var yCoordinates = new SortedSet<int>();
        foreach (var rect in _rects)
        {
            if (IsValid(rect))
            {
                yCoordinates.Add(rect.Top);
                yCoordinates.Add(rect.Bottom);
            }
        }
        if (yCoordinates.Count < 2)
        {
            _rects.Clear();
            return;
        }

        var ys = new int[yCoordinates.Count];
        yCoordinates.CopyTo(ys);
        var normalized = new List<SKRectI>();
        var previousBand = new Dictionary<(int Left, int Right), int>();
        for (var yIndex = 0; yIndex + 1 < ys.Length; yIndex++)
        {
            var top = ys[yIndex];
            var bottom = ys[yIndex + 1];
            if (bottom <= top)
            {
                continue;
            }

            var intervals = new List<(int Left, int Right)>();
            foreach (var rect in _rects)
            {
                if (IsValid(rect) && rect.Top <= top && rect.Bottom >= bottom)
                {
                    intervals.Add((rect.Left, rect.Right));
                }
            }
            if (intervals.Count == 0)
            {
                previousBand.Clear();
                continue;
            }

            intervals.Sort(static (left, right) =>
            {
                var comparison = left.Left.CompareTo(right.Left);
                return comparison != 0 ? comparison : left.Right.CompareTo(right.Right);
            });

            var merged = new List<(int Left, int Right)>();
            var current = intervals[0];
            for (var intervalIndex = 1; intervalIndex < intervals.Count; intervalIndex++)
            {
                var next = intervals[intervalIndex];
                if (next.Left <= current.Right)
                {
                    current.Right = Math.Max(current.Right, next.Right);
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }
            merged.Add(current);

            var currentBand = new Dictionary<(int Left, int Right), int>();
            foreach (var interval in merged)
            {
                if (previousBand.TryGetValue(interval, out var rectIndex) &&
                    normalized[rectIndex].Bottom == top)
                {
                    var previous = normalized[rectIndex];
                    normalized[rectIndex] = new SKRectI(
                        previous.Left,
                        previous.Top,
                        previous.Right,
                        bottom);
                }
                else
                {
                    rectIndex = normalized.Count;
                    normalized.Add(new SKRectI(interval.Left, top, interval.Right, bottom));
                }

                currentBand[interval] = rectIndex;
            }

            previousBand = currentBand;
        }

        _rects.Clear();
        _rects.AddRange(normalized);
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

    public sealed class RectIterator : SKObject
    {
        private SKRectI[] _rects;
        private int _index;

        internal RectIterator(IReadOnlyList<SKRectI> rects)
            : base(SKObjectHandle.Create(), owns: true)
        {
            _rects = CopyRects(rects);
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

        protected override void DisposeManaged()
        {
            _rects = Array.Empty<SKRectI>();
        }
    }

    public sealed class ClipIterator : SKObject
    {
        private SKRectI[] _rects;
        private int _index;

        internal ClipIterator(IReadOnlyList<SKRectI> rects, SKRectI clip)
            : base(SKObjectHandle.Create(), owns: true)
        {
            var clipped = new List<SKRectI>(rects.Count);
            foreach (var rect in rects)
            {
                if (IsValid(Intersect(rect, clip)))
                {
                    clipped.Add(rect);
                }
            }
            _rects = clipped.ToArray();
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

        protected override void DisposeManaged()
        {
            _rects = Array.Empty<SKRectI>();
        }
    }

    public sealed class SpanIterator : SKObject
    {
        private (int Left, int Right)[] _spans;
        private int _index;

        internal SpanIterator(
            IReadOnlyList<SKRectI> rects,
            int y,
            int left,
            int right)
            : base(SKObjectHandle.Create(), owns: true)
        {
            var spans = new List<(int Left, int Right)>();
            if (right > left)
            {
                foreach (var rect in rects)
                {
                    if (y >= rect.Top && y < rect.Bottom)
                    {
                        var spanLeft = Math.Max(left, rect.Left);
                        var spanRight = Math.Min(right, rect.Right);
                        if (spanRight > spanLeft)
                        {
                            spans.Add((spanLeft, spanRight));
                        }
                    }
                }
            }

            spans.Sort(static (first, second) => first.Left.CompareTo(second.Left));
            if (spans.Count > 1)
            {
                var write = 0;
                for (var read = 1; read < spans.Count; read++)
                {
                    if (spans[read].Left <= spans[write].Right)
                    {
                        spans[write] = (
                            spans[write].Left,
                            Math.Max(spans[write].Right, spans[read].Right));
                    }
                    else
                    {
                        spans[++write] = spans[read];
                    }
                }
                if (write + 1 < spans.Count)
                {
                    spans.RemoveRange(write + 1, spans.Count - write - 1);
                }
            }

            _spans = spans.ToArray();
        }

        public bool Next(out int left, out int right)
        {
            if (_index >= _spans.Length)
            {
                left = 0;
                right = 0;
                return false;
            }

            (left, right) = _spans[_index++];
            return true;
        }

        protected override void DisposeManaged()
        {
            _spans = Array.Empty<(int Left, int Right)>();
        }
    }

    private static SKRectI[] CopyRects(IReadOnlyList<SKRectI> rects)
    {
        var copy = new SKRectI[rects.Count];
        for (var index = 0; index < rects.Count; index++)
        {
            copy[index] = rects[index];
        }

        return copy;
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
