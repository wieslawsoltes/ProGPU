using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;

namespace Microsoft.UI.Composition;

/// <summary>
/// Owns an immutable retained path and bounded length evidence used only when
/// a Composition geometry is trimmed.
/// </summary>
/// <remarks>
/// Construction is O(S * K) time and storage for S path segments and at most
/// K=128 bounded samples per curved segment. A trim rebuild is O(S log K) and
/// emits at most S+2 exact retained sub-segments per non-wrapping interval.
/// Stable frames reuse the original or cached trimmed path without traversal.
/// </remarks>
internal sealed class CompositionPathData
{
    private readonly CompositionPathMeasure _measure;

    public CompositionPathData(PathGeometry geometry)
    {
        Geometry = geometry ??
            throw new ArgumentNullException(nameof(geometry));
        GeometryCache = RenderCommandGeometryCache.ForPath(geometry);
        _measure = new CompositionPathMeasure(geometry);
    }

    public PathGeometry Geometry { get; }

    public RenderCommandGeometryCache GeometryCache { get; }

    public PathGeometry CreateTrimmed(float origin, float length) =>
        _measure.CreateTrimmed(origin, length);
}

internal sealed class CompositionPathMeasure
{
    private const float Epsilon = 0.00001f;
    private const int BezierSampleCount = 32;
    private const int MaximumArcSampleCount = 128;
    private const float MaximumArcSampleAngle = MathF.PI / 64f;

    private readonly FillRule _fillRule;
    private readonly MeasuredSegment[] _segments;
    private readonly float _totalLength;

    public CompositionPathMeasure(PathGeometry geometry)
    {
        _fillRule = geometry.FillRule;
        global::System.Diagnostics.Debug.Assert(!geometry.IsCombined);

        var segments = new List<MeasuredSegment>();
        float totalLength = 0f;
        for (int figureIndex = 0;
             figureIndex < geometry.Figures.Count;
             figureIndex++)
        {
            PathFigure figure = geometry.Figures[figureIndex];
            Vector2 current = figure.StartPoint;
            for (int segmentIndex = 0;
                 segmentIndex < figure.Segments.Count;
                 segmentIndex++)
            {
                PathSegment segment = figure.Segments[segmentIndex];
                Vector2 end = GetEndPoint(segment, current);
                if (TryMeasureSegment(
                        current,
                        segment,
                        out PathSegment measuredSegment,
                        out float[]? cumulativeLengths,
                        out float segmentLength))
                {
                    segments.Add(new MeasuredSegment(
                        figureIndex,
                        figure.IsFilled,
                        current,
                        measuredSegment,
                        totalLength,
                        segmentLength,
                        cumulativeLengths));
                    totalLength += segmentLength;
                }
                current = end;
            }

            if (figure.IsClosed &&
                Vector2.DistanceSquared(
                    current,
                    figure.StartPoint) > Epsilon * Epsilon)
            {
                var close = new LineSegment(figure.StartPoint);
                float closeLength = Vector2.Distance(
                    current,
                    figure.StartPoint);
                segments.Add(new MeasuredSegment(
                    figureIndex,
                    figure.IsFilled,
                    current,
                    close,
                    totalLength,
                    closeLength,
                    null));
                totalLength += closeLength;
            }
        }

        _segments = segments.ToArray();
        _totalLength = totalLength;
    }

    public PathGeometry CreateTrimmed(float origin, float length)
    {
        var result = new PathGeometry
        {
            FillRule = _fillRule
        };
        if (_totalLength <= Epsilon ||
            !float.IsFinite(origin) ||
            !float.IsFinite(length))
        {
            return result;
        }

        origin -= MathF.Floor(origin);
        length = Math.Clamp(length, 0f, 1f);
        if (length <= Epsilon)
            return result;

        float startDistance = origin * _totalLength;
        float endDistance = startDistance + (length * _totalLength);
        AppendInterval(
            result,
            startDistance,
            MathF.Min(endDistance, _totalLength));
        if (endDistance > _totalLength + Epsilon)
        {
            AppendInterval(
                result,
                0f,
                endDistance - _totalLength);
        }
        return result;
    }

    private void AppendInterval(
        PathGeometry result,
        float intervalStart,
        float intervalEnd)
    {
        if (intervalEnd <= intervalStart + Epsilon)
            return;

        int activeFigure = -1;
        PathFigure? outputFigure = null;
        Vector2 outputEnd = default;
        foreach (MeasuredSegment measured in _segments)
        {
            float segmentStart = measured.StartDistance;
            float segmentEnd = segmentStart + measured.Length;
            float overlapStart = MathF.Max(intervalStart, segmentStart);
            float overlapEnd = MathF.Min(intervalEnd, segmentEnd);
            if (overlapEnd <= overlapStart + Epsilon)
                continue;

            float startParameter = measured.GetParameter(
                overlapStart - segmentStart);
            float endParameter = measured.GetParameter(
                overlapEnd - segmentStart);
            if (!TrySliceSegment(
                    measured.Start,
                    measured.Segment,
                    startParameter,
                    endParameter,
                    out Vector2 sliceStart,
                    out PathSegment slice))
            {
                continue;
            }

            bool startsNewFigure =
                outputFigure is null ||
                activeFigure != measured.FigureIndex ||
                Vector2.DistanceSquared(
                    outputEnd,
                    sliceStart) > Epsilon * Epsilon;
            if (startsNewFigure)
            {
                outputFigure = new PathFigure(sliceStart)
                {
                    IsFilled = measured.IsFilled,
                    IsClosed = false
                };
                result.Figures.Add(outputFigure);
                activeFigure = measured.FigureIndex;
            }

            outputFigure!.Segments.Add(slice);
            outputEnd = GetEndPoint(slice, sliceStart);
        }
    }

    private static bool TryMeasureSegment(
        Vector2 start,
        PathSegment segment,
        out PathSegment measuredSegment,
        out float[]? cumulativeLengths,
        out float length)
    {
        measuredSegment = segment;
        cumulativeLengths = null;
        Vector2 end = GetEndPoint(segment, start);
        switch (segment)
        {
            case LineSegment:
                length = Vector2.Distance(start, end);
                break;
            case QuadraticBezierSegment quadratic:
                cumulativeLengths = BuildLengthTable(
                    BezierSampleCount,
                    parameter => BezierSegmentGeometry.EvaluateQuadratic(
                        start,
                        quadratic.ControlPoint,
                        quadratic.Point,
                        parameter));
                length = cumulativeLengths[^1];
                break;
            case CubicBezierSegment cubic:
                cumulativeLengths = BuildLengthTable(
                    BezierSampleCount,
                    parameter => BezierSegmentGeometry.EvaluateCubic(
                        start,
                        cubic.ControlPoint1,
                        cubic.ControlPoint2,
                        cubic.Point,
                        parameter));
                length = cumulativeLengths[^1];
                break;
            case ArcSegment arc when ArcSegmentGeometry.TryGetArcCenter(
                start,
                arc.Point,
                arc.Size,
                arc.RotationAngle,
                arc.IsLargeArc,
                arc.SweepDirection,
                out Vector2 center,
                out float theta,
                out float deltaTheta,
                out float radiusX,
                out float radiusY):
                int sampleCount = Math.Clamp(
                    (int)MathF.Ceiling(
                        MathF.Abs(deltaTheta) /
                        MaximumArcSampleAngle),
                    1,
                    MaximumArcSampleCount);
                cumulativeLengths = BuildLengthTable(
                    sampleCount,
                    parameter => ArcSegmentGeometry.EvaluatePoint(
                        center,
                        radiusX,
                        radiusY,
                        arc.RotationAngle,
                        theta + (deltaTheta * parameter)));
                length = cumulativeLengths[^1];
                break;
            case ArcSegment arc:
                measuredSegment = new LineSegment(
                    arc.Point,
                    arc.IsSmoothJoin,
                    arc.IsStroked);
                length = Vector2.Distance(start, arc.Point);
                break;
            default:
                length = 0f;
                break;
        }

        return float.IsFinite(length) && length > Epsilon;
    }

    private static float[] BuildLengthTable(
        int sampleCount,
        Func<float, Vector2> evaluate)
    {
        var cumulative = new float[sampleCount + 1];
        Vector2 previous = evaluate(0f);
        float length = 0f;
        for (int index = 1; index <= sampleCount; index++)
        {
            Vector2 point = evaluate((float)index / sampleCount);
            length += Vector2.Distance(previous, point);
            cumulative[index] = length;
            previous = point;
        }
        return cumulative;
    }

    private static bool TrySliceSegment(
        Vector2 start,
        PathSegment segment,
        float startParameter,
        float endParameter,
        out Vector2 sliceStart,
        out PathSegment slice)
    {
        sliceStart = default;
        slice = null!;
        if (endParameter <= startParameter + Epsilon)
            return false;

        switch (segment)
        {
            case LineSegment line:
                sliceStart = Vector2.Lerp(
                    start,
                    line.Point,
                    startParameter);
                slice = new LineSegment(
                    Vector2.Lerp(start, line.Point, endParameter),
                    line.IsSmoothJoin,
                    line.IsStroked);
                return true;
            case QuadraticBezierSegment quadratic:
                if (BezierSegmentGeometry
                    .TryCreateSubQuadraticBezierSegment(
                        start,
                        quadratic,
                        startParameter,
                        endParameter,
                        out sliceStart,
                        out QuadraticBezierSegment subQuadratic))
                {
                    slice = subQuadratic;
                    return true;
                }
                return false;
            case CubicBezierSegment cubic:
                if (BezierSegmentGeometry
                    .TryCreateSubCubicBezierSegment(
                        start,
                        cubic,
                        startParameter,
                        endParameter,
                        out sliceStart,
                        out CubicBezierSegment subCubic))
                {
                    slice = subCubic;
                    return true;
                }
                return false;
            case ArcSegment arc:
                if (ArcSegmentGeometry.TryCreateSubArcSegment(
                        start,
                        arc,
                        startParameter,
                        endParameter,
                        out sliceStart,
                        out ArcSegment subArc))
                {
                    slice = subArc;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static Vector2 GetEndPoint(
        PathSegment segment,
        Vector2 fallback) => segment switch
        {
            LineSegment line => line.Point,
            QuadraticBezierSegment quadratic => quadratic.Point,
            CubicBezierSegment cubic => cubic.Point,
            ArcSegment arc => arc.Point,
            _ => fallback
        };

    private sealed class MeasuredSegment
    {
        public MeasuredSegment(
            int figureIndex,
            bool isFilled,
            Vector2 start,
            PathSegment segment,
            float startDistance,
            float length,
            float[]? cumulativeLengths)
        {
            FigureIndex = figureIndex;
            IsFilled = isFilled;
            Start = start;
            Segment = segment;
            StartDistance = startDistance;
            Length = length;
            CumulativeLengths = cumulativeLengths;
        }

        public int FigureIndex { get; }

        public bool IsFilled { get; }

        public Vector2 Start { get; }

        public PathSegment Segment { get; }

        public float StartDistance { get; }

        public float Length { get; }

        private float[]? CumulativeLengths { get; }

        public float GetParameter(float distance)
        {
            distance = Math.Clamp(distance, 0f, Length);
            if (CumulativeLengths is null)
                return distance / Length;
            if (distance <= Epsilon)
                return 0f;
            if (distance >= Length - Epsilon)
                return 1f;

            int index = Array.BinarySearch(
                CumulativeLengths,
                distance);
            if (index >= 0)
            {
                return (float)index /
                    (CumulativeLengths.Length - 1);
            }

            int upper = ~index;
            int lower = upper - 1;
            float lowerDistance = CumulativeLengths[lower];
            float span = CumulativeLengths[upper] - lowerDistance;
            float fraction = span <= Epsilon
                ? 0f
                : (distance - lowerDistance) / span;
            return (lower + fraction) /
                (CumulativeLengths.Length - 1);
        }
    }
}
