using System;
using System.Collections.Generic;
using System.Numerics;

namespace ProGPU.Vector;

public static class BezierSegmentGeometry
{
    public const int DefaultLengthSegmentCount = 32;

    private const float Epsilon = 0.00001f;

    public static Vector2 EvaluateQuadratic(Vector2 p0, Vector2 p1, Vector2 p2, float t)
    {
        var inverse = 1.0f - t;
        return inverse * inverse * p0
            + 2.0f * inverse * t * p1
            + t * t * p2;
    }

    public static Vector2 EvaluateCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        var inverse = 1.0f - t;
        return inverse * inverse * inverse * p0
            + 3.0f * inverse * inverse * t * p1
            + 3.0f * inverse * t * t * p2
            + t * t * t * p3;
    }

    public static bool TryCreateSubQuadraticBezierSegment(
        Vector2 start,
        QuadraticBezierSegment segment,
        float startParameter,
        float endParameter,
        out Vector2 subStart,
        out QuadraticBezierSegment subSegment)
    {
        subStart = default;
        subSegment = null!;

        if (segment == null ||
            !TryNormalizeParameters(ref startParameter, ref endParameter) ||
            !IsFinite(start) ||
            !IsFinite(segment.ControlPoint) ||
            !IsFinite(segment.Point))
        {
            return false;
        }

        SplitQuadratic(
            start,
            segment.ControlPoint,
            segment.Point,
            endParameter,
            out var left0,
            out var left1,
            out var left2,
            out _,
            out _,
            out _);

        if (startParameter <= Epsilon)
        {
            subStart = left0;
            subSegment = new QuadraticBezierSegment(left1, left2, segment.IsSmoothJoin, segment.IsStroked);
            return HasQuadraticLength(subStart, subSegment);
        }

        SplitQuadratic(
            left0,
            left1,
            left2,
            startParameter / endParameter,
            out _,
            out _,
            out _,
            out var right0,
            out var right1,
            out var right2);

        subStart = right0;
        subSegment = new QuadraticBezierSegment(right1, right2, segment.IsSmoothJoin, segment.IsStroked);
        return HasQuadraticLength(subStart, subSegment);
    }

    public static bool TryCreateSubCubicBezierSegment(
        Vector2 start,
        CubicBezierSegment segment,
        float startParameter,
        float endParameter,
        out Vector2 subStart,
        out CubicBezierSegment subSegment)
    {
        subStart = default;
        subSegment = null!;

        if (segment == null ||
            !TryNormalizeParameters(ref startParameter, ref endParameter) ||
            !IsFinite(start) ||
            !IsFinite(segment.ControlPoint1) ||
            !IsFinite(segment.ControlPoint2) ||
            !IsFinite(segment.Point))
        {
            return false;
        }

        SplitCubic(
            start,
            segment.ControlPoint1,
            segment.ControlPoint2,
            segment.Point,
            endParameter,
            out var left0,
            out var left1,
            out var left2,
            out var left3,
            out _,
            out _,
            out _,
            out _);

        if (startParameter <= Epsilon)
        {
            subStart = left0;
            subSegment = new CubicBezierSegment(left1, left2, left3, segment.IsSmoothJoin, segment.IsStroked);
            return HasCubicLength(subStart, subSegment);
        }

        SplitCubic(
            left0,
            left1,
            left2,
            left3,
            startParameter / endParameter,
            out _,
            out _,
            out _,
            out _,
            out var right0,
            out var right1,
            out var right2,
            out var right3);

        subStart = right0;
        subSegment = new CubicBezierSegment(right1, right2, right3, segment.IsSmoothJoin, segment.IsStroked);
        return HasCubicLength(subStart, subSegment);
    }

    public static bool TryCreateDashedQuadraticBezierSegments(
        Vector2 start,
        QuadraticBezierSegment segment,
        ReadOnlySpan<float> dashPattern,
        int patternIndex,
        float distanceInPattern,
        out QuadraticBezierDashSegment[] dashSegments,
        out int finalPatternIndex,
        out float finalDistanceInPattern,
        int lengthSegmentCount = DefaultLengthSegmentCount)
    {
        dashSegments = Array.Empty<QuadraticBezierDashSegment>();
        if (!TryBuildQuadraticLengthTable(
                start,
                segment,
                lengthSegmentCount,
                out var cumulativeLengths,
                out _)
            || !TryCreateDashParameterSpans(
                cumulativeLengths,
                dashPattern,
                patternIndex,
                distanceInPattern,
                out var parameterSpans,
                out finalPatternIndex,
                out finalDistanceInPattern))
        {
            finalPatternIndex = patternIndex;
            finalDistanceInPattern = distanceInPattern;
            return false;
        }

        var segments = new List<QuadraticBezierDashSegment>(parameterSpans.Length);
        foreach (var parameterSpan in parameterSpans)
        {
            if (TryCreateSubQuadraticBezierSegment(
                    start,
                    segment,
                    parameterSpan.Start,
                    parameterSpan.End,
                    out var dashStart,
                    out var dashSegment))
            {
                segments.Add(new QuadraticBezierDashSegment(dashStart, dashSegment));
            }
        }

        dashSegments = segments.ToArray();
        return true;
    }

    public static bool TryCreateDashedCubicBezierSegments(
        Vector2 start,
        CubicBezierSegment segment,
        ReadOnlySpan<float> dashPattern,
        int patternIndex,
        float distanceInPattern,
        out CubicBezierDashSegment[] dashSegments,
        out int finalPatternIndex,
        out float finalDistanceInPattern,
        int lengthSegmentCount = DefaultLengthSegmentCount)
    {
        dashSegments = Array.Empty<CubicBezierDashSegment>();
        if (!TryBuildCubicLengthTable(
                start,
                segment,
                lengthSegmentCount,
                out var cumulativeLengths,
                out _)
            || !TryCreateDashParameterSpans(
                cumulativeLengths,
                dashPattern,
                patternIndex,
                distanceInPattern,
                out var parameterSpans,
                out finalPatternIndex,
                out finalDistanceInPattern))
        {
            finalPatternIndex = patternIndex;
            finalDistanceInPattern = distanceInPattern;
            return false;
        }

        var segments = new List<CubicBezierDashSegment>(parameterSpans.Length);
        foreach (var parameterSpan in parameterSpans)
        {
            if (TryCreateSubCubicBezierSegment(
                    start,
                    segment,
                    parameterSpan.Start,
                    parameterSpan.End,
                    out var dashStart,
                    out var dashSegment))
            {
                segments.Add(new CubicBezierDashSegment(dashStart, dashSegment));
            }
        }

        dashSegments = segments.ToArray();
        return true;
    }

    private static bool TryBuildQuadraticLengthTable(
        Vector2 start,
        QuadraticBezierSegment segment,
        int segmentCount,
        out float[] cumulativeLengths,
        out float totalLength)
    {
        cumulativeLengths = Array.Empty<float>();
        totalLength = 0.0f;

        if (segment == null ||
            !IsFinite(start) ||
            !IsFinite(segment.ControlPoint) ||
            !IsFinite(segment.Point))
        {
            return false;
        }

        segmentCount = NormalizeLengthSegmentCount(segmentCount);
        cumulativeLengths = new float[segmentCount + 1];
        var previous = start;
        for (var i = 1; i <= segmentCount; i++)
        {
            var t = (float)i / segmentCount;
            var current = EvaluateQuadratic(start, segment.ControlPoint, segment.Point, t);
            totalLength += Vector2.Distance(previous, current);
            cumulativeLengths[i] = totalLength;
            previous = current;
        }

        return totalLength > Epsilon;
    }

    private static bool TryBuildCubicLengthTable(
        Vector2 start,
        CubicBezierSegment segment,
        int segmentCount,
        out float[] cumulativeLengths,
        out float totalLength)
    {
        cumulativeLengths = Array.Empty<float>();
        totalLength = 0.0f;

        if (segment == null ||
            !IsFinite(start) ||
            !IsFinite(segment.ControlPoint1) ||
            !IsFinite(segment.ControlPoint2) ||
            !IsFinite(segment.Point))
        {
            return false;
        }

        segmentCount = NormalizeLengthSegmentCount(segmentCount);
        cumulativeLengths = new float[segmentCount + 1];
        var previous = start;
        for (var i = 1; i <= segmentCount; i++)
        {
            var t = (float)i / segmentCount;
            var current = EvaluateCubic(start, segment.ControlPoint1, segment.ControlPoint2, segment.Point, t);
            totalLength += Vector2.Distance(previous, current);
            cumulativeLengths[i] = totalLength;
            previous = current;
        }

        return totalLength > Epsilon;
    }

    private static bool TryCreateDashParameterSpans(
        float[] cumulativeLengths,
        ReadOnlySpan<float> dashPattern,
        int patternIndex,
        float distanceInPattern,
        out DashParameterSpan[] parameterSpans,
        out int finalPatternIndex,
        out float finalDistanceInPattern)
    {
        parameterSpans = Array.Empty<DashParameterSpan>();
        finalPatternIndex = patternIndex;
        finalDistanceInPattern = distanceInPattern;

        if (cumulativeLengths.Length < 2 ||
            !DashPattern.TryValidateState(dashPattern, patternIndex, distanceInPattern))
        {
            return false;
        }

        DashPattern.NormalizeState(dashPattern, ref patternIndex, ref distanceInPattern);

        var totalLength = cumulativeLengths[^1];
        var spans = new List<DashParameterSpan>();
        var distance = 0.0f;
        while (distance < totalLength - Epsilon)
        {
            var remainingInElement = dashPattern[patternIndex] - distanceInPattern;
            var step = MathF.Min(remainingInElement, totalLength - distance);
            if ((patternIndex % 2) == 0 && step > Epsilon)
            {
                spans.Add(new DashParameterSpan(
                    GetParameterAtDistance(cumulativeLengths, distance),
                    GetParameterAtDistance(cumulativeLengths, distance + step)));
            }

            DashPattern.Advance(dashPattern, ref patternIndex, ref distanceInPattern, remainingInElement, step);
            distance += step;
        }

        parameterSpans = spans.ToArray();
        finalPatternIndex = patternIndex;
        finalDistanceInPattern = distanceInPattern;
        return true;
    }

    private static bool TryNormalizeParameters(ref float startParameter, ref float endParameter)
    {
        if (!float.IsFinite(startParameter) ||
            !float.IsFinite(endParameter) ||
            endParameter <= startParameter)
        {
            return false;
        }

        startParameter = Math.Clamp(startParameter, 0.0f, 1.0f);
        endParameter = Math.Clamp(endParameter, 0.0f, 1.0f);
        return endParameter > startParameter + Epsilon;
    }

    private static float GetParameterAtDistance(float[] cumulativeLengths, float distance)
    {
        if (distance <= 0.0f)
        {
            return 0.0f;
        }

        var totalLength = cumulativeLengths[^1];
        if (distance >= totalLength)
        {
            return 1.0f;
        }

        for (var i = 1; i < cumulativeLengths.Length; i++)
        {
            var segmentEnd = cumulativeLengths[i];
            if (distance > segmentEnd)
            {
                continue;
            }

            var segmentStart = cumulativeLengths[i - 1];
            var segmentLength = segmentEnd - segmentStart;
            var local = segmentLength > Epsilon
                ? (distance - segmentStart) / segmentLength
                : 0.0f;
            return (i - 1 + local) / (cumulativeLengths.Length - 1);
        }

        return 1.0f;
    }

    private static void SplitQuadratic(
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        float t,
        out Vector2 left0,
        out Vector2 left1,
        out Vector2 left2,
        out Vector2 right0,
        out Vector2 right1,
        out Vector2 right2)
    {
        var p01 = Vector2.Lerp(p0, p1, t);
        var p12 = Vector2.Lerp(p1, p2, t);
        var p012 = Vector2.Lerp(p01, p12, t);

        left0 = p0;
        left1 = p01;
        left2 = p012;
        right0 = p012;
        right1 = p12;
        right2 = p2;
    }

    private static void SplitCubic(
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3,
        float t,
        out Vector2 left0,
        out Vector2 left1,
        out Vector2 left2,
        out Vector2 left3,
        out Vector2 right0,
        out Vector2 right1,
        out Vector2 right2,
        out Vector2 right3)
    {
        var p01 = Vector2.Lerp(p0, p1, t);
        var p12 = Vector2.Lerp(p1, p2, t);
        var p23 = Vector2.Lerp(p2, p3, t);
        var p012 = Vector2.Lerp(p01, p12, t);
        var p123 = Vector2.Lerp(p12, p23, t);
        var p0123 = Vector2.Lerp(p012, p123, t);

        left0 = p0;
        left1 = p01;
        left2 = p012;
        left3 = p0123;
        right0 = p0123;
        right1 = p123;
        right2 = p23;
        right3 = p3;
    }

    private static bool HasQuadraticLength(Vector2 start, QuadraticBezierSegment segment)
    {
        return TryBuildQuadraticLengthTable(
            start,
            segment,
            DefaultLengthSegmentCount,
            out _,
            out _);
    }

    private static bool HasCubicLength(Vector2 start, CubicBezierSegment segment)
    {
        return TryBuildCubicLengthTable(
            start,
            segment,
            DefaultLengthSegmentCount,
            out _,
            out _);
    }

    private static int NormalizeLengthSegmentCount(int segmentCount)
    {
        return Math.Clamp(segmentCount, 1, 256);
    }

    private static bool IsFinite(Vector2 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y);
    }

    private readonly struct DashParameterSpan
    {
        public DashParameterSpan(float start, float end)
        {
            Start = start;
            End = end;
        }

        public float Start { get; }
        public float End { get; }
    }
}
