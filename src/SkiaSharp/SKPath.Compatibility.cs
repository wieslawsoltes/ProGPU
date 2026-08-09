#pragma warning disable CS0618 // The shim internally composes its official legacy SKPath contract.

using System.Globalization;
using System.Numerics;
using System.Text;
using ProGPU.Vector;

namespace SkiaSharp;

internal sealed class RationalConicQuadraticSegment : QuadraticBezierSegment
{
    public RationalConicQuadraticSegment(
        Vector2 controlPoint,
        Vector2 point,
        Vector2 originalStart,
        Vector2 originalControl,
        Vector2 originalEnd,
        float weight,
        int spanCount,
        bool isSmoothJoin = false,
        bool isStroked = true)
        : base(controlPoint, point, isSmoothJoin, isStroked)
    {
        OriginalStart = originalStart;
        OriginalControl = originalControl;
        OriginalEnd = originalEnd;
        Weight = weight;
        SpanCount = spanCount;
    }

    public Vector2 OriginalStart { get; set; }

    public Vector2 OriginalControl { get; set; }

    public Vector2 OriginalEnd { get; set; }

    public float Weight { get; }

    public int SpanCount { get; }
}

public partial class SKPath
{
    private const float PathEpsilon = 0.00001f;
    private const int RetainedConicSpanCount = 16;

    public SKPathConvexity Convexity => ComputeConvexity();

    public bool IsConcave => Convexity == SKPathConvexity.Concave;

    public bool IsConvex => Convexity == SKPathConvexity.Convex;

    public bool IsLine => TryGetLine(out _, out _);

    public bool IsOval => TryGetOval(out _);

    public bool IsRect => TryGetRect(out _, out _, out _);

    public bool IsRoundRect => TryGetRoundRect(out _);

    public SKPoint this[int index] => GetPoint(index);

    public SKPoint LastPoint
    {
        get
        {
            if (Geometry.Figures.Count == 0)
            {
                return default;
            }

            var figure = Geometry.Figures[^1];
            return ToSkPoint(GetFigureEnd(figure));
        }
    }

    public int PointCount => CountPoints(BuildRawOperations());

    public SKPoint[] Points => GetPoints(PointCount);

    public SKPathSegmentMask SegmentMasks
    {
        get
        {
            var mask = (SKPathSegmentMask)0;
            foreach (var operation in BuildRawOperations())
            {
                mask |= operation.Verb switch
                {
                    SKPathVerb.Line => SKPathSegmentMask.Line,
                    SKPathVerb.Quad => SKPathSegmentMask.Quad,
                    SKPathVerb.Conic => SKPathSegmentMask.Conic,
                    SKPathVerb.Cubic => SKPathSegmentMask.Cubic,
                    _ => 0,
                };
            }

            return mask;
        }
    }

    public int VerbCount => BuildRawOperations().Count;

    [Obsolete(UsePathBuilderMessage)]
    public void AddArc(SKRect oval, float startAngle, float sweepAngle)
    {
        if (!IsValidOvalArc(oval, startAngle, sweepAngle))
        {
            return;
        }

        AppendOvalArc(oval, startAngle, sweepAngle, forceMoveTo: true);
        if (MathF.Abs(sweepAngle) >= 360f)
        {
            Close();
        }
    }

    [Obsolete(UsePathBuilderMessage)]
    public void AddPathReverse(SKPath other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var figures = other.Geometry.Figures;
        for (var index = figures.Count - 1; index >= 0; index--)
        {
            var reversed = ReverseFigure(figures[index]);
            Geometry.Figures.Add(reversed);
            SetCurrentFigure(reversed);
        }
    }

    [Obsolete(UsePathBuilderMessage)]
    public void AddRect(SKRect rect, SKPathDirection direction, uint startIndex)
    {
        if (startIndex > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startIndex),
                "Starting index must be in the range of 0..3 (inclusive).");
        }

        Span<Vector2> points =
        [
            new(rect.Left, rect.Top),
            new(rect.Right, rect.Top),
            new(rect.Right, rect.Bottom),
            new(rect.Left, rect.Bottom),
        ];
        var index = (int)startIndex;
        MoveTo(points[index].X, points[index].Y);
        var step = direction == SKPathDirection.Clockwise ? 1 : -1;
        for (var count = 0; count < 3; count++)
        {
            index = (index + step + 4) % 4;
            LineTo(points[index].X, points[index].Y);
        }

        Close();
    }

    [Obsolete(UsePathBuilderMessage)]
    public void AddRoundRect(SKRoundRect rect, SKPathDirection direction, uint startIndex)
    {
        ArgumentNullException.ThrowIfNull(rect);
        if (startIndex > 7)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startIndex),
                "Starting index must be in the range of 0..7 (inclusive).");
        }

        Span<SKPoint> radii = stackalloc SKPoint[4];
        rect.CopyCornerRadii(radii);
        AppendRoundRect(rect.Rect, radii, direction, startIndex);
    }

    [Obsolete(UsePathBuilderMessage)]
    public void ArcTo(SKPoint point1, SKPoint point2, float radius) =>
        ArcTo(point1.X, point1.Y, point2.X, point2.Y, radius);

    [Obsolete(UsePathBuilderMessage)]
    public void ArcTo(
        SKPoint r,
        float xAxisRotate,
        SKPathArcSize largeArc,
        SKPathDirection sweep,
        SKPoint xy) =>
        ArcTo(r.X, r.Y, xAxisRotate, largeArc, sweep, xy.X, xy.Y);

    [Obsolete(UsePathBuilderMessage)]
    public void ArcTo(SKRect oval, float startAngle, float sweepAngle, bool forceMoveTo)
    {
        if (!TryGetOvalArc(
                oval,
                startAngle,
                sweepAngle,
                out var center,
                out var radiusX,
                out var radiusY,
                out var sweep,
                out var start))
        {
            return;
        }

        ConnectOvalArc(start, forceMoveTo);
        if (MathF.Abs(sweepAngle) >= 360f)
        {
            return;
        }

        AppendOvalArcSegments(center, radiusX, radiusY, startAngle, sweep);
    }

    [Obsolete(UsePathBuilderMessage)]
    public void ArcTo(float x1, float y1, float x2, float y2, float radius)
    {
        EnsureFigure();
        var corner = new Vector2(x1, y1);
        var next = new Vector2(x2, y2);
        var incoming = _currentPoint - corner;
        var outgoing = next - corner;
        var incomingLength = incoming.Length();
        var outgoingLength = outgoing.Length();
        var normalizedRadius = MathF.Abs(radius);
        if (!float.IsFinite(normalizedRadius) || normalizedRadius <= PathEpsilon ||
            incomingLength <= PathEpsilon || outgoingLength <= PathEpsilon)
        {
            LineTo(x1, y1);
            return;
        }

        incoming /= incomingLength;
        outgoing /= outgoingLength;
        var dot = Math.Clamp(Vector2.Dot(incoming, outgoing), -1f, 1f);
        var cross = incoming.X * outgoing.Y - incoming.Y * outgoing.X;
        var tangent = MathF.Tan(MathF.Acos(dot) * 0.5f);
        if (MathF.Abs(cross) <= PathEpsilon || MathF.Abs(tangent) <= PathEpsilon)
        {
            LineTo(x1, y1);
            return;
        }

        var distance = normalizedRadius / tangent;
        if (!float.IsFinite(distance))
        {
            LineTo(x1, y1);
            return;
        }

        var tangentStart = corner + incoming * distance;
        var tangentEnd = corner + outgoing * distance;
        if (!Near(_currentPoint, tangentStart))
        {
            LineTo(tangentStart.X, tangentStart.Y);
        }

        _currentFigure!.Segments.Add(new ArcSegment(
            tangentEnd,
            new Vector2(normalizedRadius),
            0f,
            false,
            cross < 0f ? SweepDirection.Clockwise : SweepDirection.Counterclockwise));
        _currentPoint = tangentEnd;
    }

    public SKRect ComputeTightBounds() => TightBounds;

    [Obsolete(UsePathBuilderMessage)]
    public void ConicTo(float x0, float y0, float x1, float y1, float w)
    {
        EnsureFigure();
        AppendConicSpans(
            _currentFigure!,
            _currentPoint,
            new Vector2(x0, y0),
            new Vector2(x1, y1),
            w);
        _currentPoint = new Vector2(x1, y1);
    }

    public bool GetBounds(out SKRect rect)
    {
        rect = Bounds;
        return !IsEmpty;
    }

    public SKPoint[]? GetLine() =>
        TryGetLine(out var start, out var end) ? [ToSkPoint(start), ToSkPoint(end)] : null;

    public SKRect GetOvalBounds() => TryGetOval(out var bounds) ? bounds : SKRect.Empty;

    public SKPoint GetPoint(int index)
    {
        var points = Points;
        if ((uint)index >= (uint)points.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return points[index];
    }

    public int GetPoints(SKPoint[] points, int max)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (max < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(max));
        }

        var source = Points;
        Array.Copy(source, points, Math.Min(Math.Min(max, points.Length), source.Length));
        return source.Length;
    }

    public SKPoint[] GetPoints(int max)
    {
        if (max < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(max));
        }

        var operations = BuildRawOperations();
        var result = new SKPoint[max];
        WritePoints(operations, result);
        return result;
    }

    public SKRect GetRect() => GetRect(out _, out _);

    public SKRect GetRect(out bool isClosed, out SKPathDirection direction)
    {
        if (TryGetRect(out var rect, out isClosed, out direction))
        {
            return rect;
        }

        isClosed = false;
        direction = SKPathDirection.Clockwise;
        return SKRect.Empty;
    }

    public SKRoundRect GetRoundRect() =>
        TryGetRoundRect(out var roundRect) ? roundRect : new SKRoundRect();

    public bool GetTightBounds(out SKRect result)
    {
        result = TightBounds;
        return !IsEmpty;
    }

    public void Offset(SKPoint offset) => Offset(offset.X, offset.Y);

    public void Offset(float dx, float dy)
    {
        var offset = SKMatrix.CreateTranslation(dx, dy);
        Transform(offset);
    }

    [Obsolete(UsePathBuilderMessage)]
    public void RArcTo(
        SKPoint r,
        float xAxisRotate,
        SKPathArcSize largeArc,
        SKPathDirection sweep,
        SKPoint xy) =>
        RArcTo(r.X, r.Y, xAxisRotate, largeArc, sweep, xy.X, xy.Y);

    [Obsolete(UsePathBuilderMessage)]
    public void RArcTo(
        float rx,
        float ry,
        float xAxisRotate,
        SKPathArcSize largeArc,
        SKPathDirection sweep,
        float x,
        float y)
    {
        var start = _currentPoint;
        ArcTo(rx, ry, xAxisRotate, largeArc, sweep, start.X + x, start.Y + y);
    }

    [Obsolete(UsePathBuilderMessage)]
    public void RConicTo(SKPoint point0, SKPoint point1, float w) =>
        RConicTo(point0.X, point0.Y, point1.X, point1.Y, w);

    [Obsolete(UsePathBuilderMessage)]
    public void RConicTo(float dx0, float dy0, float dx1, float dy1, float w)
    {
        var start = _currentPoint;
        ConicTo(start.X + dx0, start.Y + dy0, start.X + dx1, start.Y + dy1, w);
    }

    [Obsolete(UsePathBuilderMessage)]
    public void RCubicTo(SKPoint point0, SKPoint point1, SKPoint point2) =>
        RCubicTo(point0.X, point0.Y, point1.X, point1.Y, point2.X, point2.Y);

    [Obsolete(UsePathBuilderMessage)]
    public void RCubicTo(float dx0, float dy0, float dx1, float dy1, float dx2, float dy2)
    {
        var start = _currentPoint;
        CubicTo(
            start.X + dx0,
            start.Y + dy0,
            start.X + dx1,
            start.Y + dy1,
            start.X + dx2,
            start.Y + dy2);
    }

    [Obsolete(UsePathBuilderMessage)]
    public void RLineTo(SKPoint point) => RLineTo(point.X, point.Y);

    [Obsolete(UsePathBuilderMessage)]
    public void RLineTo(float dx, float dy) => LineTo(_currentPoint.X + dx, _currentPoint.Y + dy);

    [Obsolete(UsePathBuilderMessage)]
    public void RMoveTo(SKPoint point) => RMoveTo(point.X, point.Y);

    [Obsolete(UsePathBuilderMessage)]
    public void RMoveTo(float dx, float dy) => MoveTo(_currentPoint.X + dx, _currentPoint.Y + dy);

    [Obsolete(UsePathBuilderMessage)]
    public void RQuadTo(SKPoint point0, SKPoint point1) =>
        RQuadTo(point0.X, point0.Y, point1.X, point1.Y);

    [Obsolete(UsePathBuilderMessage)]
    public void RQuadTo(float dx0, float dy0, float dx1, float dy1)
    {
        var start = _currentPoint;
        QuadTo(start.X + dx0, start.Y + dy0, start.X + dx1, start.Y + dy1);
    }

    [Obsolete(UsePathBuilderMessage)]
    public void Rewind()
    {
        Geometry.Figures.Clear();
        ResetCurrentState();
        FillType = SKPathFillType.Winding;
    }

    public string ToSvgPathData()
    {
        var builder = new StringBuilder(Math.Max(16, VerbCount * 16));
        foreach (var figure in Geometry.Figures)
        {
            if (figure.Segments.Count == 0 && !figure.IsClosed)
            {
                continue;
            }

            AppendCommand(builder, 'M', figure.StartPoint);
            var current = figure.StartPoint;
            for (var index = 0; index < figure.Segments.Count; index++)
            {
                switch (figure.Segments[index])
                {
                    case RationalConicQuadraticSegment conic:
                        AppendConicSvg(builder, conic);
                        current = conic.OriginalEnd;
                        index += conic.SpanCount - 1;
                        break;
                    case LineSegment line:
                        AppendCommand(builder, 'L', line.Point);
                        current = line.Point;
                        break;
                    case QuadraticBezierSegment quadratic:
                        AppendCommand(builder, 'Q', quadratic.ControlPoint, quadratic.Point);
                        current = quadratic.Point;
                        break;
                    case CubicBezierSegment cubic:
                        AppendCommand(
                            builder,
                            'C',
                            cubic.ControlPoint1,
                            cubic.ControlPoint2,
                            cubic.Point);
                        current = cubic.Point;
                        break;
                    case ArcSegment arc:
                        AppendArcCommand(builder, arc);
                        current = arc.Point;
                        break;
                }
            }

            if (figure.IsClosed)
            {
                if (!Near(current, figure.StartPoint))
                {
                    AppendCommand(builder, 'L', figure.StartPoint);
                }

                builder.Append('Z');
            }
        }

        return builder.ToString();
    }

    public void Transform(in SKMatrix matrix) => Transform((SKMatrix)matrix);

    public void Transform(SKMatrix matrix, SKPath destination) =>
        TransformCore(matrix, destination);

    public void Transform(in SKMatrix matrix, SKPath destination) =>
        TransformCore(matrix, destination);

    public static int ConvertConicToQuads(
        SKPoint p0,
        SKPoint p1,
        SKPoint p2,
        float w,
        SKPoint[] pts,
        int pow2)
    {
        ArgumentNullException.ThrowIfNull(pts);
        pow2 = ValidateConicPow2(pow2);
        var requiredPointCount = checked((1 << pow2) * 2 + 1);
        if (pts.Length < requiredPointCount)
        {
            throw new ArgumentException(
                $"The destination requires at least {requiredPointCount} points.",
                nameof(pts));
        }

        var converted = ConvertConicToQuads(p0, p1, p2, w, pow2);
        Array.Copy(converted, pts, converted.Length);
        return 1 << pow2;
    }

    public static SKPoint[] ConvertConicToQuads(
        SKPoint p0,
        SKPoint p1,
        SKPoint p2,
        float w,
        int pow2)
    {
        pow2 = ValidateConicPow2(pow2);
        var conicCount = 1 << pow2;
        var result = new SKPoint[conicCount * 2 + 1];
        result[0] = p0;
        WriteChoppedConic(p0, p1, p2, w, pow2, result, 0);
        return result;
    }

    public static int ConvertConicToQuads(
        SKPoint p0,
        SKPoint p1,
        SKPoint p2,
        float w,
        out SKPoint[] pts,
        int pow2)
    {
        pts = ConvertConicToQuads(p0, p1, p2, w, pow2);
        return 1 << pow2;
    }

    private void TransformCore(SKMatrix matrix, SKPath destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(this, destination))
        {
            Transform(matrix);
            return;
        }

        using var copy = new SKPath(this);
        copy.Transform(matrix);
        CopyTo(copy, destination);
    }

    private void AddPathCore(SKPath other, Vector2 offset, SKPathAddMode mode)
    {
        var figures = other.Geometry.Figures;
        var firstIndex = 0;
        if (mode == SKPathAddMode.Extend && Geometry.Figures.Count > 0 && figures.Count > 0)
        {
            EnsureFigure();
            var source = figures[0];
            var sourceStart = source.StartPoint + offset;
            LineTo(sourceStart.X, sourceStart.Y);
            foreach (var segment in source.Segments)
            {
                var clone = CloneSegment(segment, offset);
                _currentFigure!.Segments.Add(clone);
                _currentPoint = GetSegmentEnd(clone);
            }

            if (source.IsClosed)
            {
                Close();
            }

            firstIndex = 1;
        }

        for (var index = firstIndex; index < figures.Count; index++)
        {
            var clone = CloneFigure(figures[index], offset);
            Geometry.Figures.Add(clone);
            SetCurrentFigure(clone);
        }
    }

    private void AppendOvalArc(SKRect oval, float startAngle, float sweepAngle, bool forceMoveTo)
    {
        if (!TryGetOvalArc(
                oval,
                startAngle,
                sweepAngle,
                out var center,
                out var radiusX,
                out var radiusY,
                out var sweep,
                out var start))
        {
            return;
        }

        ConnectOvalArc(start, forceMoveTo);
        AppendOvalArcSegments(center, radiusX, radiusY, startAngle, sweep);
    }

    private static bool IsValidOvalArc(SKRect oval, float startAngle, float sweepAngle) =>
        TryGetOvalArc(
            oval,
            startAngle,
            sweepAngle,
            out _,
            out _,
            out _,
            out _,
            out _);

    private static bool TryGetOvalArc(
        SKRect oval,
        float startAngle,
        float sweepAngle,
        out Vector2 center,
        out float radiusX,
        out float radiusY,
        out float sweep,
        out Vector2 start)
    {
        center = new Vector2(oval.MidX, oval.MidY);
        radiusX = MathF.Abs(oval.Width) * 0.5f;
        radiusY = MathF.Abs(oval.Height) * 0.5f;
        sweep = Math.Clamp(sweepAngle, -360f, 360f);
        start = default;
        if (!float.IsFinite(startAngle) ||
            !float.IsFinite(sweepAngle) ||
            !float.IsFinite(center.X) ||
            !float.IsFinite(center.Y) ||
            !float.IsFinite(radiusX) ||
            !float.IsFinite(radiusY) ||
            radiusX <= PathEpsilon ||
            radiusY <= PathEpsilon ||
            MathF.Abs(sweep) <= PathEpsilon)
        {
            return false;
        }

        start = GetOvalPoint(center, radiusX, radiusY, startAngle);
        return true;
    }

    private void ConnectOvalArc(Vector2 start, bool forceMoveTo)
    {
        var geometry = Geometry;
        if (forceMoveTo || geometry.Figures.Count == 0)
        {
            MoveTo(start.X, start.Y);
            return;
        }

        EnsureFigure();
        if (!Near(_currentPoint, start))
        {
            LineTo(start.X, start.Y);
        }
    }

    private void AppendOvalArcSegments(
        Vector2 center,
        float radiusX,
        float radiusY,
        float startAngle,
        float sweep)
    {
        var segmentCount = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(sweep) / 180f));
        var segmentSweep = sweep / segmentCount;
        var direction = segmentSweep >= 0f ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
        for (var index = 1; index <= segmentCount; index++)
        {
            var end = GetOvalPoint(center, radiusX, radiusY, startAngle + segmentSweep * index);
            _currentFigure!.Segments.Add(new ArcSegment(
                end,
                new Vector2(radiusX, radiusY),
                0f,
                MathF.Abs(segmentSweep) >= 180f - PathEpsilon,
                direction));
            _currentPoint = end;
        }
    }

    private void AppendRoundRect(
        SKRect rect,
        ReadOnlySpan<SKPoint> sourceRadii,
        SKPathDirection direction,
        uint startIndex)
    {
        _ = Geometry;

        Span<Vector2> radii = stackalloc Vector2[4];
        for (var index = 0; index < radii.Length; index++)
        {
            radii[index] = new Vector2(
                MathF.Abs(sourceRadii[index].X),
                MathF.Abs(sourceRadii[index].Y));
        }

        NormalizeRoundRectRadii(rect, radii);
        Span<Vector2> points =
        [
            new(rect.Left + radii[0].X, rect.Top),
            new(rect.Right - radii[1].X, rect.Top),
            new(rect.Right, rect.Top + radii[1].Y),
            new(rect.Right, rect.Bottom - radii[2].Y),
            new(rect.Right - radii[2].X, rect.Bottom),
            new(rect.Left + radii[3].X, rect.Bottom),
            new(rect.Left, rect.Bottom - radii[3].Y),
            new(rect.Left, rect.Top + radii[0].Y),
        ];

        var currentIndex = (int)startIndex;
        MoveTo(points[currentIndex].X, points[currentIndex].Y);
        var step = direction == SKPathDirection.Clockwise ? 1 : -1;
        for (var edge = 1; edge <= 8; edge++)
        {
            var nextIndex = (currentIndex + step + 8) % 8;
            var isArc = direction == SKPathDirection.Clockwise
                ? currentIndex % 2 == 1
                : currentIndex % 2 == 0;
            if (edge == 8 && !isArc)
            {
                break;
            }

            if (isArc)
            {
                var cornerIndex = direction == SKPathDirection.Clockwise
                    ? ((currentIndex + 1) / 2) % 4
                    : currentIndex / 2;
                var radius = radii[cornerIndex];
                if (radius.X > PathEpsilon && radius.Y > PathEpsilon)
                {
                    _currentFigure!.Segments.Add(new ArcSegment(
                        points[nextIndex],
                        radius,
                        0f,
                        false,
                        direction == SKPathDirection.Clockwise
                            ? SweepDirection.Clockwise
                            : SweepDirection.Counterclockwise));
                    _currentPoint = points[nextIndex];
                }
                else
                {
                    LineTo(points[nextIndex].X, points[nextIndex].Y);
                }
            }
            else
            {
                LineTo(points[nextIndex].X, points[nextIndex].Y);
            }

            currentIndex = nextIndex;
        }

        Close();
    }

    private void RestoreCurrentState()
    {
        if (Geometry.Figures.Count == 0)
        {
            ResetCurrentState();
            return;
        }

        SetCurrentFigure(Geometry.Figures[^1]);
    }

    private void SetCurrentFigure(PathFigure figure)
    {
        _contourStart = figure.StartPoint;
        if (figure.IsClosed)
        {
            _currentFigure = null;
            _currentPoint = figure.StartPoint;
        }
        else
        {
            _currentFigure = figure;
            _currentPoint = GetFigureEnd(figure);
        }
    }

    private void ResetCurrentState()
    {
        _currentFigure = null;
        _currentPoint = Vector2.Zero;
        _contourStart = Vector2.Zero;
    }

    internal static void AppendConicSpans(
        PathFigure figure,
        Vector2 start,
        Vector2 control,
        Vector2 end,
        float weight)
    {
        if (!float.IsFinite(weight) || weight <= 0f || MathF.Abs(weight - 1f) <= PathEpsilon)
        {
            figure.Segments.Add(new QuadraticBezierSegment(control, end));
            return;
        }

        for (var index = 0; index < RetainedConicSpanCount; index++)
        {
            var t0 = index / (float)RetainedConicSpanCount;
            var t1 = (index + 1f) / RetainedConicSpanCount;
            var spanStart = EvaluateConic(start, control, end, weight, t0);
            var spanEnd = EvaluateConic(start, control, end, weight, t1);
            var midpoint = EvaluateConic(start, control, end, weight, (t0 + t1) * 0.5f);
            var spanControl = 2f * midpoint - 0.5f * (spanStart + spanEnd);
            figure.Segments.Add(index == 0
                ? new RationalConicQuadraticSegment(
                    spanControl,
                    spanEnd,
                    start,
                    control,
                    end,
                    weight,
                    RetainedConicSpanCount)
                : new QuadraticBezierSegment(spanControl, spanEnd));
        }
    }

    private static Vector2 EvaluateConic(
        Vector2 start,
        Vector2 control,
        Vector2 end,
        float weight,
        float t)
    {
        var inverse = 1f - t;
        var startFactor = inverse * inverse;
        var controlFactor = 2f * weight * inverse * t;
        var endFactor = t * t;
        return (startFactor * start + controlFactor * control + endFactor * end) /
            (startFactor + controlFactor + endFactor);
    }

    private static Vector2 GetOvalPoint(
        Vector2 center,
        float radiusX,
        float radiusY,
        float angleDegrees)
    {
        var angle = angleDegrees * MathF.PI / 180f;
        return new Vector2(
            center.X + radiusX * SnapUnitTrigonometricValue(MathF.Cos(angle)),
            center.Y + radiusY * SnapUnitTrigonometricValue(MathF.Sin(angle)));
    }

    private static float SnapUnitTrigonometricValue(float value)
    {
        if (MathF.Abs(value) <= PathEpsilon)
        {
            return 0f;
        }

        if (MathF.Abs(value - 1f) <= PathEpsilon)
        {
            return 1f;
        }

        if (MathF.Abs(value + 1f) <= PathEpsilon)
        {
            return -1f;
        }

        return value;
    }

    private static void NormalizeRoundRectRadii(SKRect rect, Span<Vector2> radii)
    {
        var width = MathF.Abs(rect.Width);
        var height = MathF.Abs(rect.Height);
        var scale = 1f;
        ScaleToFit(width, radii[0].X + radii[1].X, ref scale);
        ScaleToFit(width, radii[3].X + radii[2].X, ref scale);
        ScaleToFit(height, radii[0].Y + radii[3].Y, ref scale);
        ScaleToFit(height, radii[1].Y + radii[2].Y, ref scale);
        if (scale >= 1f)
        {
            return;
        }

        for (var index = 0; index < radii.Length; index++)
        {
            radii[index] *= scale;
        }
    }

    private static void ScaleToFit(float available, float requested, ref float scale)
    {
        if (requested > available && requested > PathEpsilon)
        {
            scale = MathF.Min(scale, available / requested);
        }
    }

    private static PathFigure ReverseFigure(PathFigure source)
    {
        var starts = new Vector2[source.Segments.Count];
        var current = source.StartPoint;
        for (var index = 0; index < source.Segments.Count; index++)
        {
            starts[index] = current;
            current = GetSegmentEnd(source.Segments[index]);
        }

        var reversed = new PathFigure(current, source.IsClosed)
        {
            IsFilled = source.IsFilled,
            StrokeStartLineCap = source.StrokeEndLineCap,
            StrokeEndLineCap = source.StrokeStartLineCap
        };
        for (var index = source.Segments.Count - 1; index >= 0; index--)
        {
            if (TryFindConicGroup(source.Segments, index, out var conicStart, out var conic))
            {
                AppendConicSpans(
                    reversed,
                    conic.OriginalEnd,
                    conic.OriginalControl,
                    conic.OriginalStart,
                    conic.Weight);
                index = conicStart;
                continue;
            }

            var end = starts[index];
            reversed.Segments.Add(source.Segments[index] switch
            {
                LineSegment line => new LineSegment(end, line.IsSmoothJoin, line.IsStroked),
                QuadraticBezierSegment quadratic => new QuadraticBezierSegment(
                    quadratic.ControlPoint,
                    end,
                    quadratic.IsSmoothJoin,
                    quadratic.IsStroked),
                CubicBezierSegment cubic => new CubicBezierSegment(
                    cubic.ControlPoint2,
                    cubic.ControlPoint1,
                    end,
                    cubic.IsSmoothJoin,
                    cubic.IsStroked),
                ArcSegment arc => new ArcSegment(
                    end,
                    arc.Size,
                    arc.RotationAngle,
                    arc.IsLargeArc,
                    arc.SweepDirection == SweepDirection.Clockwise
                        ? SweepDirection.Counterclockwise
                        : SweepDirection.Clockwise,
                    arc.IsSmoothJoin,
                    arc.IsStroked),
                _ => throw new NotSupportedException(
                    $"Unsupported path segment '{source.Segments[index].GetType().FullName}'."),
            });
        }

        return reversed;
    }

    internal static bool TryFindConicGroup(
        IReadOnlyList<PathSegment> segments,
        int segmentIndex,
        out int groupStart,
        out RationalConicQuadraticSegment conic)
    {
        var minimum = Math.Max(0, segmentIndex - RetainedConicSpanCount + 1);
        for (var index = segmentIndex; index >= minimum; index--)
        {
            if (segments[index] is RationalConicQuadraticSegment candidate &&
                index + candidate.SpanCount > segmentIndex)
            {
                groupStart = index;
                conic = candidate;
                return true;
            }
        }

        groupStart = -1;
        conic = null!;
        return false;
    }

    private static Vector2 GetFigureEnd(PathFigure figure) =>
        figure.Segments.Count == 0 ? figure.StartPoint : GetSegmentEnd(figure.Segments[^1]);

    private static Vector2 GetSegmentEnd(PathSegment segment) => segment switch
    {
        LineSegment line => line.Point,
        QuadraticBezierSegment quadratic => quadratic.Point,
        CubicBezierSegment cubic => cubic.Point,
        ArcSegment arc => arc.Point,
        _ => throw new NotSupportedException($"Unsupported path segment '{segment.GetType().FullName}'."),
    };

    private static bool Near(Vector2 left, Vector2 right) =>
        Vector2.DistanceSquared(left, right) <= PathEpsilon * PathEpsilon;

    private static bool Near(float left, float right) =>
        MathF.Abs(left - right) <= PathEpsilon;

    private static SKPoint ToSkPoint(Vector2 point) => new(point.X, point.Y);

    private static bool TryGetLine(SKPath path, out Vector2 start, out Vector2 end) =>
        path.TryGetLine(out start, out end);

    private bool TryGetLine(out Vector2 start, out Vector2 end)
    {
        start = default;
        end = default;
        if (Geometry.Figures.Count != 1)
        {
            return false;
        }

        var figure = Geometry.Figures[0];
        if (figure.IsClosed || figure.Segments.Count != 1 || figure.Segments[0] is not LineSegment line)
        {
            return false;
        }

        start = figure.StartPoint;
        end = line.Point;
        return true;
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
        if (figure.Segments.Count is < 3 or > 4 ||
            figure.Segments.Any(static segment => segment is not LineSegment))
        {
            return false;
        }

        Span<Vector2> points = stackalloc Vector2[5];
        points[0] = figure.StartPoint;
        for (var index = 0; index < figure.Segments.Count; index++)
        {
            points[index + 1] = ((LineSegment)figure.Segments[index]).Point;
        }

        var count = figure.Segments.Count + 1;
        if (count == 5 && Near(points[0], points[4]))
        {
            count = 4;
        }

        if (count != 4)
        {
            return false;
        }

        var min = points[0];
        var max = points[0];
        for (var index = 1; index < 4; index++)
        {
            min = Vector2.Min(min, points[index]);
            max = Vector2.Max(max, points[index]);
        }

        if (max.X - min.X <= PathEpsilon || max.Y - min.Y <= PathEpsilon)
        {
            return false;
        }

        var corners = 0;
        for (var index = 0; index < 4; index++)
        {
            var point = points[index];
            if ((Near(point.X, min.X) || Near(point.X, max.X)) &&
                (Near(point.Y, min.Y) || Near(point.Y, max.Y)))
            {
                corners++;
            }
        }

        if (corners != 4)
        {
            return false;
        }

        var area = 0f;
        for (var index = 0; index < 4; index++)
        {
            var next = (index + 1) % 4;
            area += points[index].X * points[next].Y - points[next].X * points[index].Y;
        }

        rect = new SKRect(min.X, min.Y, max.X, max.Y);
        isClosed = figure.IsClosed || (figure.Segments.Count == 4 && Near(points[0], points[4]));
        direction = area >= 0f ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise;
        return true;
    }

    private bool TryGetOval(out SKRect bounds)
    {
        bounds = SKRect.Empty;
        if (Geometry.Figures.Count != 1)
        {
            return false;
        }

        var figure = Geometry.Figures[0];
        if (!figure.IsClosed || figure.Segments.Count != 2 ||
            figure.Segments[0] is not ArcSegment first ||
            figure.Segments[1] is not ArcSegment second ||
            !Near(first.Size, second.Size) ||
            !Near(second.Point, figure.StartPoint))
        {
            return false;
        }

        bounds = TightBounds;
        return bounds.Width > PathEpsilon && bounds.Height > PathEpsilon;
    }

    private bool TryGetRoundRect(out SKRoundRect roundRect)
    {
        roundRect = new SKRoundRect();
        if (Geometry.Figures.Count != 1)
        {
            return false;
        }

        var figure = Geometry.Figures[0];
        if (!figure.IsClosed || figure.Segments.Count != 8 ||
            figure.Segments.Count(static segment => segment is ArcSegment) != 4 ||
            figure.Segments.Count(static segment => segment is LineSegment) != 4)
        {
            return false;
        }

        var bounds = TightBounds;
        var radii = new SKPoint[4];
        var found = new bool[4];
        var current = figure.StartPoint;
        foreach (var segment in figure.Segments)
        {
            if (segment is ArcSegment arc &&
                MathF.Abs(arc.RotationAngle) <= PathEpsilon &&
                !arc.IsLargeArc)
            {
                var midpoint = (current + arc.Point) * 0.5f;
                var corner = midpoint.X < bounds.MidX
                    ? midpoint.Y < bounds.MidY ? 0 : 3
                    : midpoint.Y < bounds.MidY ? 1 : 2;
                radii[corner] = new SKPoint(MathF.Abs(arc.Size.X), MathF.Abs(arc.Size.Y));
                found[corner] = true;
            }

            current = GetSegmentEnd(segment);
        }

        if (found.Any(static value => !value))
        {
            return false;
        }

        roundRect = new SKRoundRect();
        roundRect.SetRectRadii(bounds, radii);
        return true;
    }

    private SKPathConvexity ComputeConvexity()
    {
        if (Geometry.Figures.Count == 0)
        {
            return SKPathConvexity.Convex;
        }

        if (Geometry.Figures.Count > 1)
        {
            return SKPathConvexity.Concave;
        }

        if (IsRect || IsOval || IsRoundRect || IsLine || Geometry.Figures[0].Segments.Count == 0)
        {
            return SKPathConvexity.Convex;
        }

        var figure = Geometry.Figures[0];
        if (!figure.IsClosed && TryGetArcOnlySweep(figure, out var arcSweep) &&
            MathF.Abs(arcSweep) > MathF.PI + PathEpsilon)
        {
            return SKPathConvexity.Concave;
        }

        var samples = BuildConvexitySamples(figure);
        if (samples.Count < 3)
        {
            return SKPathConvexity.Convex;
        }

        var sign = 0f;
        for (var index = 0; index < samples.Count; index++)
        {
            var a = samples[index];
            var b = samples[(index + 1) % samples.Count];
            var c = samples[(index + 2) % samples.Count];
            var cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (MathF.Abs(cross) <= PathEpsilon)
            {
                continue;
            }

            if (sign == 0f)
            {
                sign = MathF.Sign(cross);
            }
            else if (MathF.Sign(cross) != sign)
            {
                return SKPathConvexity.Concave;
            }
        }

        return SKPathConvexity.Convex;
    }

    private static bool TryGetArcOnlySweep(PathFigure figure, out float sweep)
    {
        sweep = 0f;
        var current = figure.StartPoint;
        foreach (var segment in figure.Segments)
        {
            if (segment is not ArcSegment arc ||
                !ArcSegmentGeometry.TryGetArcCenter(
                    current,
                    arc.Point,
                    arc.Size,
                    arc.RotationAngle,
                    arc.IsLargeArc,
                    arc.SweepDirection,
                    out _,
                    out _,
                    out var delta,
                    out _,
                    out _))
            {
                return false;
            }

            sweep += delta;
            current = arc.Point;
        }

        return figure.Segments.Count > 0;
    }

    private static List<Vector2> BuildConvexitySamples(PathFigure figure)
    {
        var result = new List<Vector2>(Math.Max(4, figure.Segments.Count * 8)) { figure.StartPoint };
        var current = figure.StartPoint;
        foreach (var segment in figure.Segments)
        {
            switch (segment)
            {
                case LineSegment line:
                    result.Add(line.Point);
                    current = line.Point;
                    break;
                case QuadraticBezierSegment quadratic:
                    for (var sample = 1; sample <= 8; sample++)
                    {
                        var t = sample / 8f;
                        var inverse = 1f - t;
                        result.Add(
                            inverse * inverse * current +
                            2f * inverse * t * quadratic.ControlPoint +
                            t * t * quadratic.Point);
                    }

                    current = quadratic.Point;
                    break;
                case CubicBezierSegment cubic:
                    for (var sample = 1; sample <= 12; sample++)
                    {
                        var t = sample / 12f;
                        var inverse = 1f - t;
                        result.Add(
                            inverse * inverse * inverse * current +
                            3f * inverse * inverse * t * cubic.ControlPoint1 +
                            3f * inverse * t * t * cubic.ControlPoint2 +
                            t * t * t * cubic.Point);
                    }

                    current = cubic.Point;
                    break;
                case ArcSegment arc:
                    var flattened = ArcSegmentGeometry.FlattenArc(current, arc, MathF.PI / 16f);
                    for (var sample = 1; sample < flattened.Length; sample++)
                    {
                        result.Add(flattened[sample]);
                    }

                    current = arc.Point;
                    break;
            }
        }

        if (result.Count > 1 && Near(result[0], result[^1]))
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private List<IteratorOperation> BuildRawOperations()
    {
        var operations = new List<IteratorOperation>(Math.Max(1, Geometry.Figures.Count * 4));
        foreach (var figure in Geometry.Figures)
        {
            var current = figure.StartPoint;
            operations.Add(new IteratorOperation(SKPathVerb.Move, current, default, default, default, 0f, figure.IsClosed, false));
            for (var index = 0; index < figure.Segments.Count; index++)
            {
                switch (figure.Segments[index])
                {
                    case RationalConicQuadraticSegment conic:
                        operations.Add(new IteratorOperation(
                            SKPathVerb.Conic,
                            conic.OriginalStart,
                            conic.OriginalControl,
                            conic.OriginalEnd,
                            default,
                            conic.Weight,
                            figure.IsClosed,
                            false));
                        current = conic.OriginalEnd;
                        index += conic.SpanCount - 1;
                        break;
                    case LineSegment line:
                        operations.Add(new IteratorOperation(SKPathVerb.Line, current, line.Point, default, default, 0f, figure.IsClosed, false));
                        current = line.Point;
                        break;
                    case QuadraticBezierSegment quadratic:
                        operations.Add(new IteratorOperation(SKPathVerb.Quad, current, quadratic.ControlPoint, quadratic.Point, default, 0f, figure.IsClosed, false));
                        current = quadratic.Point;
                        break;
                    case CubicBezierSegment cubic:
                        operations.Add(new IteratorOperation(SKPathVerb.Cubic, current, cubic.ControlPoint1, cubic.ControlPoint2, cubic.Point, 0f, figure.IsClosed, false));
                        current = cubic.Point;
                        break;
                    case ArcSegment arc:
                        var consumed = AppendArcRunOperations(
                            operations,
                            figure,
                            index,
                            current,
                            figure.IsClosed,
                            out var arcEnd);
                        current = arcEnd;
                        index += consumed - 1;
                        break;
                }
            }

            if (figure.IsClosed)
            {
                operations.Add(new IteratorOperation(SKPathVerb.Close, current, figure.StartPoint, default, default, 0f, true, false));
            }
        }

        return operations;
    }

    private static int AppendArcRunOperations(
        List<IteratorOperation> operations,
        PathFigure figure,
        int firstIndex,
        Vector2 start,
        bool explicitlyClosed,
        out Vector2 finalEnd)
    {
        var arc = (ArcSegment)figure.Segments[firstIndex];
        finalEnd = arc.Point;
        if (!ArcSegmentGeometry.TryGetArcCenter(
                start,
                arc.Point,
                arc.Size,
                arc.RotationAngle,
                arc.IsLargeArc,
                arc.SweepDirection,
                out var center,
                out var theta,
                out var delta,
                out var radiusX,
                out var radiusY))
        {
            operations.Add(new IteratorOperation(SKPathVerb.Line, start, arc.Point, default, default, 0f, explicitlyClosed, false));
            return 1;
        }

        var totalDelta = delta;
        var consumed = 1;
        var runEnd = arc.Point;
        while (firstIndex + consumed < figure.Segments.Count &&
               figure.Segments[firstIndex + consumed] is ArcSegment next &&
               next.SweepDirection == arc.SweepDirection &&
               MathF.Abs(next.RotationAngle - arc.RotationAngle) <= 0.0001f &&
               ArcSegmentGeometry.TryGetArcCenter(
                   runEnd,
                   next.Point,
                   next.Size,
                   next.RotationAngle,
                   next.IsLargeArc,
                   next.SweepDirection,
                   out var nextCenter,
                   out _,
                   out var nextDelta,
                   out var nextRadiusX,
                   out var nextRadiusY) &&
               Vector2.DistanceSquared(center, nextCenter) <= 0.0001f &&
               MathF.Abs(radiusX - nextRadiusX) <= 0.0001f &&
               MathF.Abs(radiusY - nextRadiusY) <= 0.0001f)
        {
            totalDelta += nextDelta;
            runEnd = next.Point;
            consumed++;
        }

        finalEnd = runEnd;
        var normalizedQuarterCount = MathF.Abs(totalDelta) / (MathF.PI * 0.5f);
        var segmentCount = Math.Max(1, (int)MathF.Ceiling(normalizedQuarterCount - 0.00001f));
        var current = start;
        var currentTheta = theta;
        var remainingDelta = totalDelta;
        var rotation = arc.RotationAngle * (MathF.PI / 180f);
        var cosRotation = MathF.Cos(rotation);
        var sinRotation = MathF.Sin(rotation);
        var axisX = new Vector2(cosRotation * radiusX, sinRotation * radiusX);
        var axisY = new Vector2(-sinRotation * radiusY, cosRotation * radiusY);
        for (var index = 0; index < segmentCount; index++)
        {
            var segmentDelta = MathF.CopySign(
                MathF.Min(MathF.Abs(remainingDelta), MathF.PI * 0.5f),
                remainingDelta);
            var endTheta = currentTheta + segmentDelta;
            var midpointTheta = currentTheta + segmentDelta * 0.5f;
            var weight = MathF.Cos(segmentDelta * 0.5f);
            var control = center +
                axisX * SnapUnitTrigonometricValue(MathF.Cos(midpointTheta) / weight) +
                axisY * SnapUnitTrigonometricValue(MathF.Sin(midpointTheta) / weight);
            var end = index == segmentCount - 1
                ? runEnd
                : center +
                    axisX * SnapUnitTrigonometricValue(MathF.Cos(endTheta)) +
                    axisY * SnapUnitTrigonometricValue(MathF.Sin(endTheta));
            operations.Add(new IteratorOperation(SKPathVerb.Conic, current, control, end, default, weight, explicitlyClosed, false));
            current = end;
            currentTheta = endTheta;
            remainingDelta -= segmentDelta;
        }

        return consumed;
    }

    private static int CountPoints(IReadOnlyList<IteratorOperation> operations)
    {
        var count = 0;
        foreach (var operation in operations)
        {
            count += operation.Verb switch
            {
                SKPathVerb.Move or SKPathVerb.Line => 1,
                SKPathVerb.Quad or SKPathVerb.Conic => 2,
                SKPathVerb.Cubic => 3,
                _ => 0,
            };
        }

        return count;
    }

    private static void WritePoints(IReadOnlyList<IteratorOperation> operations, Span<SKPoint> destination)
    {
        var index = 0;
        foreach (var operation in operations)
        {
            switch (operation.Verb)
            {
                case SKPathVerb.Move:
                    WritePoint(destination, ref index, operation.P0);
                    break;
                case SKPathVerb.Line:
                    WritePoint(destination, ref index, operation.P1);
                    break;
                case SKPathVerb.Quad:
                case SKPathVerb.Conic:
                    WritePoint(destination, ref index, operation.P1);
                    WritePoint(destination, ref index, operation.P2);
                    break;
                case SKPathVerb.Cubic:
                    WritePoint(destination, ref index, operation.P1);
                    WritePoint(destination, ref index, operation.P2);
                    WritePoint(destination, ref index, operation.P3);
                    break;
            }

            if (index >= destination.Length)
            {
                return;
            }
        }
    }

    private static void WritePoint(Span<SKPoint> destination, ref int index, Vector2 point)
    {
        if (index < destination.Length)
        {
            destination[index] = ToSkPoint(point);
        }

        index++;
    }

    private static void CopyTo(SKPath source, SKPath destination)
    {
        destination.Geometry.Figures.Clear();
        destination.FillType = source.FillType;
        foreach (var figure in source.Geometry.Figures)
        {
            destination.Geometry.Figures.Add(CloneFigure(figure, Vector2.Zero));
        }

        destination.RestoreCurrentState();
    }

    private static void AppendCommand(StringBuilder builder, char command, params Vector2[] points)
    {
        builder.Append(command);
        for (var index = 0; index < points.Length; index++)
        {
            if (index != 0)
            {
                builder.Append(' ');
            }

            AppendScalar(builder, points[index].X);
            builder.Append(' ');
            AppendScalar(builder, points[index].Y);
        }
    }

    private static void AppendArcCommand(StringBuilder builder, ArcSegment arc)
    {
        builder.Append('A');
        AppendScalar(builder, MathF.Abs(arc.Size.X));
        builder.Append(' ');
        AppendScalar(builder, MathF.Abs(arc.Size.Y));
        builder.Append(' ');
        AppendScalar(builder, arc.RotationAngle);
        builder.Append(arc.IsLargeArc ? " 1 " : " 0 ");
        builder.Append(arc.SweepDirection == SweepDirection.Clockwise ? "1 " : "0 ");
        AppendScalar(builder, arc.Point.X);
        builder.Append(' ');
        AppendScalar(builder, arc.Point.Y);
    }

    private static void AppendConicSvg(StringBuilder builder, RationalConicQuadraticSegment conic)
    {
        var points = ConvertConicToQuads(
            ToSkPoint(conic.OriginalStart),
            ToSkPoint(conic.OriginalControl),
            ToSkPoint(conic.OriginalEnd),
            conic.Weight,
            5);
        for (var index = 1; index < points.Length; index += 2)
        {
            AppendCommand(
                builder,
                'Q',
                new Vector2(points[index].X, points[index].Y),
                new Vector2(points[index + 1].X, points[index + 1].Y));
        }
    }

    private static void AppendScalar(StringBuilder builder, float value) =>
        builder.Append(value.ToString("R", CultureInfo.InvariantCulture));

    private static int ValidateConicPow2(int pow2)
    {
        if ((uint)pow2 > 20u)
        {
            throw new ArgumentOutOfRangeException(nameof(pow2));
        }

        return pow2;
    }

    private static void WriteChoppedConic(
        SKPoint point0,
        SKPoint point1,
        SKPoint point2,
        float weight,
        int pow2,
        SKPoint[] destination,
        int curveIndex)
    {
        if (pow2 == 0)
        {
            destination[curveIndex * 2 + 1] = point1;
            destination[curveIndex * 2 + 2] = point2;
            return;
        }

        var scale = 1f / (1f + weight);
        var leftControl = new SKPoint(
            (point0.X + weight * point1.X) * scale,
            (point0.Y + weight * point1.Y) * scale);
        var midpoint = new SKPoint(
            (point0.X + 2f * weight * point1.X + point2.X) * (scale * 0.5f),
            (point0.Y + 2f * weight * point1.Y + point2.Y) * (scale * 0.5f));
        var rightControl = new SKPoint(
            (weight * point1.X + point2.X) * scale,
            (weight * point1.Y + point2.Y) * scale);
        var nextWeight = MathF.Sqrt((1f + weight) * 0.5f);
        var halfCurveCount = 1 << (pow2 - 1);
        WriteChoppedConic(
            point0,
            leftControl,
            midpoint,
            nextWeight,
            pow2 - 1,
            destination,
            curveIndex);
        WriteChoppedConic(
            midpoint,
            rightControl,
            point2,
            nextWeight,
            pow2 - 1,
            destination,
            curveIndex + halfCurveCount);
    }

    private readonly record struct IteratorOperation(
        SKPathVerb Verb,
        Vector2 P0,
        Vector2 P1,
        Vector2 P2,
        Vector2 P3,
        float Weight,
        bool ExplicitlyClosed,
        bool IsSyntheticCloseLine);

    private static void ValidateIteratorPoints(Span<SKPoint> points)
    {
        if (points.Length != 4)
        {
            throw new ArgumentException("Must be an array of four elements.", nameof(points));
        }
    }

    public class RawIterator : SKObject
    {
        private readonly List<IteratorOperation> _operations;
        private int _index;
        private float _conicWeight;

        internal RawIterator(SKPath path)
            : base(SKObjectHandle.Create(), owns: true)
        {
            _operations = path.BuildRawOperations();
        }

        public float ConicWeight() => _conicWeight;

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
            WriteOperationPoints(operation, points, writeClosePoint: false);
            if (operation.Verb == SKPathVerb.Conic)
            {
                _conicWeight = operation.Weight;
            }

            return operation.Verb;
        }

        public SKPathVerb Peek() =>
            _index < _operations.Count ? _operations[_index].Verb : SKPathVerb.Done;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        protected override void DisposeNative()
        {
            _operations.Clear();
            base.DisposeNative();
        }
    }

    public class Iterator : SKObject
    {
        private readonly List<IteratorOperation> _operations;
        private int _index;
        private float _conicWeight;
        private bool _isCloseLine;
        private bool _isCloseContour;

        internal Iterator(SKPath path, bool forceClose)
            : base(SKObjectHandle.Create(), owns: true)
        {
            _operations = BuildIteratorOperations(path, forceClose);
        }

        public float ConicWeight() => _conicWeight;

        public bool IsCloseContour() => _isCloseContour;

        public bool IsCloseLine() => _isCloseLine;

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
                _isCloseLine = false;
                _isCloseContour = false;
                return SKPathVerb.Done;
            }

            var operation = _operations[_index++];
            WriteOperationPoints(operation, points, writeClosePoint: true);
            if (operation.Verb == SKPathVerb.Conic)
            {
                _conicWeight = operation.Weight;
            }

            _isCloseLine = operation.IsSyntheticCloseLine;
            _isCloseContour = operation.ExplicitlyClosed && operation.Verb != SKPathVerb.Close;
            return operation.Verb;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        protected override void DisposeNative()
        {
            _operations.Clear();
            base.DisposeNative();
        }

        private static List<IteratorOperation> BuildIteratorOperations(SKPath path, bool forceClose)
        {
            var result = new List<IteratorOperation>();
            foreach (var figure in path.Geometry.Figures)
            {
                if (figure.Segments.Count == 0)
                {
                    if (figure.IsClosed)
                    {
                        result.Add(new IteratorOperation(SKPathVerb.Move, figure.StartPoint, default, default, default, 0f, true, false));
                        result.Add(new IteratorOperation(SKPathVerb.Close, figure.StartPoint, figure.StartPoint, default, default, 0f, true, false));
                    }

                    continue;
                }

                var contour = new SKPath();
                contour.Geometry.Figures.Add(CloneFigure(figure, Vector2.Zero));
                var raw = contour.BuildRawOperations();
                contour.Dispose();
                if (raw.Count > 0 && raw[^1].Verb == SKPathVerb.Close)
                {
                    raw.RemoveAt(raw.Count - 1);
                }

                var closesContour = figure.IsClosed || forceClose;
                if (forceClose)
                {
                    for (var index = 0; index < raw.Count; index++)
                    {
                        raw[index] = raw[index] with { ExplicitlyClosed = true };
                    }
                }

                result.AddRange(raw);
                var current = GetFigureEnd(figure);
                if (closesContour)
                {
                    var hasSyntheticCloseLine = !Near(current, figure.StartPoint);
                    if (hasSyntheticCloseLine)
                    {
                        result.Add(new IteratorOperation(
                            SKPathVerb.Line,
                            current,
                            figure.StartPoint,
                            default,
                            default,
                            0f,
                            closesContour,
                            true));
                    }

                    result.Add(new IteratorOperation(
                        SKPathVerb.Close,
                        figure.StartPoint,
                        figure.StartPoint,
                        default,
                        default,
                        0f,
                        closesContour,
                        hasSyntheticCloseLine));
                }
            }

            return result;
        }
    }

    public class OpBuilder : SKObject
    {
        private SKPath? _result;

        public OpBuilder()
            : base(SKObjectHandle.Create(), owns: true)
        {
        }

        public void Add(SKPath path, SKPathOp op)
        {
            ArgumentNullException.ThrowIfNull(path);
            if (_result is null)
            {
                _result = new SKPath(path);
                return;
            }

            var combined = _result.Op(path, op);
            _result.Dispose();
            _result = combined;
        }

        public bool Resolve(SKPath result)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (_result is null)
            {
                result.Reset();
                return false;
            }

            CopyTo(_result, result);
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        protected override void DisposeNative()
        {
            _result?.Dispose();
            _result = null;
            base.DisposeNative();
        }
    }

    private static void WriteOperationPoints(
        IteratorOperation operation,
        Span<SKPoint> points,
        bool writeClosePoint)
    {
        switch (operation.Verb)
        {
            case SKPathVerb.Move:
                SetPoint(points, 0, operation.P0);
                break;
            case SKPathVerb.Line:
                SetPoint(points, 0, operation.P0);
                SetPoint(points, 1, operation.P1);
                break;
            case SKPathVerb.Quad:
            case SKPathVerb.Conic:
                SetPoint(points, 0, operation.P0);
                SetPoint(points, 1, operation.P1);
                SetPoint(points, 2, operation.P2);
                break;
            case SKPathVerb.Cubic:
                SetPoint(points, 0, operation.P0);
                SetPoint(points, 1, operation.P1);
                SetPoint(points, 2, operation.P2);
                SetPoint(points, 3, operation.P3);
                break;
            case SKPathVerb.Close when writeClosePoint:
                SetPoint(points, 0, operation.P0);
                break;
        }
    }

    private static void SetPoint(Span<SKPoint> points, int index, Vector2 point)
    {
        if (index < points.Length)
        {
            points[index] = ToSkPoint(point);
        }
    }
}
