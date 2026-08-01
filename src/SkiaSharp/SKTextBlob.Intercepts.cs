using System;
using System.Numerics;
using ProGPU.Vector;

#nullable disable
#pragma warning disable CS8632

namespace SkiaSharp;

public partial class SKTextBlob
{
    private const double InterceptRootEpsilon = 1e-12;

#nullable enable
    public float[] GetIntercepts(
        float upperBounds,
        float lowerBounds,
        SKPaint? paint = null)
    {
        if (!IsValidInterceptBand(upperBounds, lowerBounds))
        {
            return Array.Empty<float>();
        }

        var intervals = new float[GlyphIndices.Length * 2];
        var count = ComputeIntercepts(upperBounds, lowerBounds, intervals, paint);
        if (count != intervals.Length)
        {
            Array.Resize(ref intervals, count);
        }

        return intervals;
    }
#nullable disable

    public void GetIntercepts(
        float upperBounds,
        float lowerBounds,
        Span<float> intervals,
        SKPaint? paint = null) =>
        ComputeIntercepts(upperBounds, lowerBounds, intervals, paint);

    public int CountIntercepts(
        float upperBounds,
        float lowerBounds,
        SKPaint? paint = null) =>
        ComputeIntercepts(upperBounds, lowerBounds, Span<float>.Empty, paint);

    private int ComputeIntercepts(
        float lowerLimit,
        float upperLimit,
        Span<float> intervals,
        SKPaint? paint)
    {
        _ = paint;
        if (!IsValidInterceptBand(lowerLimit, upperLimit))
        {
            return 0;
        }

        var intervalCount = 0;
        for (var runIndex = 0; runIndex < Runs.Length; runIndex++)
        {
            var run = Runs[runIndex];
            if (run.RotationScaleMatrices is not null)
            {
                continue;
            }

            var glyphCount = Math.Min(run.GlyphIndices.Length, run.GlyphPositions.Length);
            for (var glyphIndex = 0; glyphIndex < glyphCount; glyphIndex++)
            {
                var position = run.GlyphPositions[glyphIndex];
                using var path = run.Font.GetGlyphPath(run.GlyphIndices[glyphIndex]);
                if (path is null)
                {
                    continue;
                }

                if (!TryFindPathIntercept(
                        path,
                        lowerLimit - position.Y,
                        upperLimit - position.Y,
                        out var minimum,
                        out var maximum))
                {
                    continue;
                }

                WriteInterval(intervals, intervalCount, minimum + position.X);
                WriteInterval(intervals, intervalCount + 1, maximum + position.X);
                intervalCount += 2;
            }
        }

        return intervalCount;
    }

    private static bool IsValidInterceptBand(float lowerLimit, float upperLimit) =>
        !float.IsNaN(lowerLimit) &&
        !float.IsNaN(upperLimit) &&
        lowerLimit <= upperLimit;

    private static void WriteInterval(Span<float> intervals, int index, float value)
    {
        if ((uint)index < (uint)intervals.Length)
        {
            intervals[index] = value;
        }
    }

    private static bool TryFindPathIntercept(
        SKPath path,
        float lowerLimit,
        float upperLimit,
        out float minimum,
        out float maximum)
    {
        minimum = float.PositiveInfinity;
        maximum = float.NegativeInfinity;
        var bounds = path.Bounds;
        if (bounds.Bottom < lowerLimit || upperLimit < bounds.Top)
        {
            return false;
        }

        var figures = path.Geometry.Figures;
        for (var figureIndex = 0; figureIndex < figures.Count; figureIndex++)
        {
            var figure = figures[figureIndex];
            var current = figure.StartPoint;
            var segments = figure.Segments;
            for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
            {
                switch (segments[segmentIndex])
                {
                    case LineSegment line:
                        AddLineIntercepts(
                            current,
                            line.Point,
                            lowerLimit,
                            upperLimit,
                            ref minimum,
                            ref maximum);
                        current = line.Point;
                        break;

                    case QuadraticBezierSegment quadratic:
                        AddQuadraticIntercepts(
                            current,
                            quadratic.ControlPoint,
                            quadratic.Point,
                            lowerLimit,
                            upperLimit,
                            ref minimum,
                            ref maximum);
                        current = quadratic.Point;
                        break;

                    case CubicBezierSegment cubic:
                        AddCubicIntercepts(
                            current,
                            cubic.ControlPoint1,
                            cubic.ControlPoint2,
                            cubic.Point,
                            lowerLimit,
                            upperLimit,
                            ref minimum,
                            ref maximum);
                        current = cubic.Point;
                        break;

                    case ArcSegment arc:
                        var points = ArcSegmentGeometry.FlattenArc(current, arc);
                        for (var pointIndex = 1; pointIndex < points.Length; pointIndex++)
                        {
                            AddLineIntercepts(
                                points[pointIndex - 1],
                                points[pointIndex],
                                lowerLimit,
                                upperLimit,
                                ref minimum,
                                ref maximum);
                        }

                        current = arc.Point;
                        break;
                }
            }

            if (figure.IsClosed && current != figure.StartPoint)
            {
                AddLineIntercepts(
                    current,
                    figure.StartPoint,
                    lowerLimit,
                    upperLimit,
                    ref minimum,
                    ref maximum);
            }
        }

        return minimum < maximum;
    }

    private static void AddLineIntercepts(
        Vector2 start,
        Vector2 end,
        float lowerLimit,
        float upperLimit,
        ref float minimum,
        ref float maximum)
    {
        AddLineBoundary(start, end, lowerLimit, ref minimum, ref maximum);
        AddLineBoundary(start, end, upperLimit, ref minimum, ref maximum);
        AddPointInsideBand(start, lowerLimit, upperLimit, ref minimum, ref maximum);
        AddPointInsideBand(end, lowerLimit, upperLimit, ref minimum, ref maximum);
    }

    private static void AddLineBoundary(
        Vector2 start,
        Vector2 end,
        float axis,
        ref float minimum,
        ref float maximum)
    {
        if (!float.IsFinite(axis))
        {
            return;
        }

        var parameter = (axis - start.Y) / (end.Y - start.Y);
        if (parameter >= 0f && parameter < 1f)
        {
            AddIntervalValue(
                start.X + parameter * (end.X - start.X),
                ref minimum,
                ref maximum);
        }
    }

    private static void AddQuadraticIntercepts(
        Vector2 start,
        Vector2 control,
        Vector2 end,
        float lowerLimit,
        float upperLimit,
        ref float minimum,
        ref float maximum)
    {
        var minimumY = MathF.Min(start.Y, MathF.Min(control.Y, end.Y));
        var maximumY = MathF.Max(start.Y, MathF.Max(control.Y, end.Y));
        if (upperLimit < minimumY || lowerLimit >= maximumY)
        {
            return;
        }

        AddQuadraticBoundary(start, control, end, lowerLimit, ref minimum, ref maximum);
        AddQuadraticBoundary(start, control, end, upperLimit, ref minimum, ref maximum);
        AddPointInsideBand(start, lowerLimit, upperLimit, ref minimum, ref maximum);
        AddPointInsideBand(control, lowerLimit, upperLimit, ref minimum, ref maximum);
        AddPointInsideBand(end, lowerLimit, upperLimit, ref minimum, ref maximum);
    }

    private static void AddQuadraticBoundary(
        Vector2 start,
        Vector2 control,
        Vector2 end,
        float axis,
        ref float minimum,
        ref float maximum)
    {
        if (!float.IsFinite(axis))
        {
            return;
        }

        Span<double> roots = stackalloc double[2];
        var rootCount = SolveQuadratic(
            start.Y - 2.0 * control.Y + end.Y,
            2.0 * (control.Y - start.Y),
            start.Y - axis,
            roots);
        for (var rootIndex = 0; rootIndex < rootCount; rootIndex++)
        {
            var parameter = roots[rootIndex];
            if (parameter < 0.0 || parameter > 1.0)
            {
                continue;
            }

            AddIntervalValue(
                EvaluateQuadraticX(start.X, control.X, end.X, parameter),
                ref minimum,
                ref maximum);
        }
    }

    private static void AddCubicIntercepts(
        Vector2 start,
        Vector2 control1,
        Vector2 control2,
        Vector2 end,
        float lowerLimit,
        float upperLimit,
        ref float minimum,
        ref float maximum)
    {
        var minimumY = MathF.Min(
            MathF.Min(start.Y, control1.Y),
            MathF.Min(control2.Y, end.Y));
        var maximumY = MathF.Max(
            MathF.Max(start.Y, control1.Y),
            MathF.Max(control2.Y, end.Y));
        if (upperLimit < minimumY || lowerLimit >= maximumY)
        {
            return;
        }

        AddCubicBoundary(start, control1, control2, end, lowerLimit, ref minimum, ref maximum);
        AddCubicBoundary(start, control1, control2, end, upperLimit, ref minimum, ref maximum);
        AddPointInsideBand(start, lowerLimit, upperLimit, ref minimum, ref maximum);
        AddPointInsideBand(control1, lowerLimit, upperLimit, ref minimum, ref maximum);
        AddPointInsideBand(control2, lowerLimit, upperLimit, ref minimum, ref maximum);
        AddPointInsideBand(end, lowerLimit, upperLimit, ref minimum, ref maximum);
    }

    private static void AddCubicBoundary(
        Vector2 start,
        Vector2 control1,
        Vector2 control2,
        Vector2 end,
        float axis,
        ref float minimum,
        ref float maximum)
    {
        if (!float.IsFinite(axis))
        {
            return;
        }

        Span<double> roots = stackalloc double[3];
        var rootCount = SolveCubic(
            -start.Y + 3.0 * control1.Y - 3.0 * control2.Y + end.Y,
            3.0 * start.Y - 6.0 * control1.Y + 3.0 * control2.Y,
            -3.0 * start.Y + 3.0 * control1.Y,
            start.Y - axis,
            roots);
        for (var rootIndex = 0; rootIndex < rootCount; rootIndex++)
        {
            var parameter = roots[rootIndex];
            if (parameter < 0.0 || parameter > 1.0)
            {
                continue;
            }

            AddIntervalValue(
                EvaluateCubicX(start.X, control1.X, control2.X, end.X, parameter),
                ref minimum,
                ref maximum);
        }
    }

    private static void AddPointInsideBand(
        Vector2 point,
        float lowerLimit,
        float upperLimit,
        ref float minimum,
        ref float maximum)
    {
        if (lowerLimit < point.Y && point.Y < upperLimit)
        {
            AddIntervalValue(point.X, ref minimum, ref maximum);
        }
    }

    private static void AddIntervalValue(
        float value,
        ref float minimum,
        ref float maximum)
    {
        if (!float.IsFinite(value))
        {
            return;
        }

        minimum = MathF.Min(minimum, value);
        maximum = MathF.Max(maximum, value);
    }

    private static float EvaluateQuadraticX(
        float start,
        float control,
        float end,
        double parameter)
    {
        var inverse = 1.0 - parameter;
        return (float)(
            inverse * inverse * start
            + 2.0 * inverse * parameter * control
            + parameter * parameter * end);
    }

    private static float EvaluateCubicX(
        float start,
        float control1,
        float control2,
        float end,
        double parameter)
    {
        var inverse = 1.0 - parameter;
        return (float)(
            inverse * inverse * inverse * start
            + 3.0 * inverse * inverse * parameter * control1
            + 3.0 * inverse * parameter * parameter * control2
            + parameter * parameter * parameter * end);
    }

    private static int SolveQuadratic(
        double quadratic,
        double linear,
        double constant,
        Span<double> roots)
    {
        if (Math.Abs(quadratic) <= InterceptRootEpsilon)
        {
            if (Math.Abs(linear) <= InterceptRootEpsilon)
            {
                return 0;
            }

            roots[0] = -constant / linear;
            return 1;
        }

        var discriminant = linear * linear - 4.0 * quadratic * constant;
        if (discriminant < -InterceptRootEpsilon)
        {
            return 0;
        }

        if (Math.Abs(discriminant) <= InterceptRootEpsilon)
        {
            roots[0] = -linear / (2.0 * quadratic);
            return 1;
        }

        var squareRoot = Math.Sqrt(discriminant);
        var firstNumerator = -0.5 * (linear + Math.CopySign(squareRoot, linear));
        roots[0] = firstNumerator / quadratic;
        roots[1] = constant / firstNumerator;
        return 2;
    }

    private static int SolveCubic(
        double cubic,
        double quadratic,
        double linear,
        double constant,
        Span<double> roots)
    {
        if (Math.Abs(cubic) <= InterceptRootEpsilon)
        {
            return SolveQuadratic(quadratic, linear, constant, roots);
        }

        var normalizedQuadratic = quadratic / cubic;
        var normalizedLinear = linear / cubic;
        var normalizedConstant = constant / cubic;
        var p = normalizedLinear - normalizedQuadratic * normalizedQuadratic / 3.0;
        var q = 2.0 * normalizedQuadratic * normalizedQuadratic * normalizedQuadratic / 27.0
            - normalizedQuadratic * normalizedLinear / 3.0
            + normalizedConstant;
        var discriminant = q * q / 4.0 + p * p * p / 27.0;
        var offset = normalizedQuadratic / 3.0;

        if (discriminant > InterceptRootEpsilon)
        {
            var squareRoot = Math.Sqrt(discriminant);
            roots[0] = Math.Cbrt(-q / 2.0 + squareRoot)
                + Math.Cbrt(-q / 2.0 - squareRoot)
                - offset;
            return 1;
        }

        if (Math.Abs(discriminant) <= InterceptRootEpsilon)
        {
            var root = Math.Cbrt(-q / 2.0);
            roots[0] = 2.0 * root - offset;
            roots[1] = -root - offset;
            return Math.Abs(roots[0] - roots[1]) <= InterceptRootEpsilon ? 1 : 2;
        }

        var radius = 2.0 * Math.Sqrt(-p / 3.0);
        var cosine = Math.Clamp(-q / (2.0 * Math.Sqrt(-(p * p * p) / 27.0)), -1.0, 1.0);
        var angle = Math.Acos(cosine) / 3.0;
        roots[0] = radius * Math.Cos(angle) - offset;
        roots[1] = radius * Math.Cos(angle - 2.0 * Math.PI / 3.0) - offset;
        roots[2] = radius * Math.Cos(angle - 4.0 * Math.PI / 3.0) - offset;
        return 3;
    }
}
