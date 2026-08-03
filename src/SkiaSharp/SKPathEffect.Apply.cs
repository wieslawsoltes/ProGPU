#pragma warning disable CS0618 // The shim internally composes its official legacy SKPath contract.

using System.Numerics;
using ProGPU.Vector;

namespace SkiaSharp;

public partial class SKPathEffect
{
    // Measured effects walk each source contour once and emit only generated spans/stamps.
    // Work is O(S + G) and owned output is O(G), where S is measured source complexity and
    // G is the number of generated path elements; fixed guards prevent degenerate inputs
    // from producing unbounded retained command geometry.
    private const float EffectEpsilon = 0.00001f;
    private const int MaximumGeneratedElements = 1_000_000;

    internal readonly record struct PaintAdjustment(SKPaintStyle? Style, float? StrokeWidth)
    {
        public void Apply(SKPaint paint)
        {
            if (Style is { } style)
            {
                paint.Style = style;
            }
            if (StrokeWidth is { } strokeWidth)
            {
                paint.StrokeWidth = strokeWidth;
            }
        }
    }

    internal bool TryApply(SKPath source, float resScale, out SKPath result)
    {
        ArgumentNullException.ThrowIfNull(source);
        var normalizedScale = float.IsFinite(resScale) && resScale > 0f ? resScale : 1f;
        return IsDash
            ? TryApplyDash(source, Intervals, Phase, normalizedScale, out result)
            : TryApply(_data!, source, normalizedScale, out result);
    }

    internal bool TryApply(
        SKPath source,
        float resScale,
        out SKPath result,
        out PaintAdjustment paintAdjustment)
    {
        var applied = TryApply(source, resScale, out result);
        paintAdjustment = IsDash ? default : GetPaintAdjustment(_data!);
        return applied;
    }

    private static bool TryApply(EffectData data, SKPath source, float resScale, out SKPath result)
    {
        switch (data)
        {
            case DashData dash:
                return TryApplyDash(source, dash.Intervals, dash.Phase, resScale, out result);
            case TrimData trim:
                return TryApplyTrim(source, trim, resScale, out result);
            case PairData { Kind: EffectKind.Compose } compose:
                if (!TryApply(compose.Second, source, resScale, out var inner))
                {
                    inner.Dispose();
                    result = new SKPath(source);
                    return false;
                }
                using (inner)
                {
                    return TryApply(compose.First, inner, resScale, out result);
                }
            case PairData { Kind: EffectKind.Sum } sum:
                return TryApplySum(source, sum, resScale, out result);
            case CornerData corner:
                return TryApplyCorner(source, corner, out result);
            case DiscreteData discrete:
                return TryApplyDiscrete(source, discrete, resScale, out result);
            case Path1DData path1D:
                return TryApplyPath1D(source, path1D, resScale, out result);
            case Line2DData line2D:
                return TryApplyLine2D(source, line2D, resScale, out result);
            case Path2DData path2D:
                return TryApplyPath2D(source, path2D, out result);
            default:
                result = new SKPath(source);
                return false;
        }
    }

    private static PaintAdjustment GetPaintAdjustment(EffectData data) => data switch
    {
        Line2DData line => new PaintAdjustment(SKPaintStyle.Stroke, line.Width),
        Path2DData => new PaintAdjustment(SKPaintStyle.Fill, null),
        PairData { Kind: EffectKind.Compose } compose =>
            MergePaintAdjustments(GetPaintAdjustment(compose.Second), GetPaintAdjustment(compose.First)),
        PairData { Kind: EffectKind.Sum } sum =>
            IntersectPaintAdjustments(GetPaintAdjustment(sum.First), GetPaintAdjustment(sum.Second)),
        _ => default,
    };

    private static PaintAdjustment MergePaintAdjustments(
        PaintAdjustment inner,
        PaintAdjustment outer) =>
        new(outer.Style ?? inner.Style, outer.StrokeWidth ?? inner.StrokeWidth);

    private static PaintAdjustment IntersectPaintAdjustments(
        PaintAdjustment first,
        PaintAdjustment second) =>
        first == second ? first : default;

    private static bool TryApplyDash(
        SKPath source,
        ReadOnlySpan<float> intervals,
        float phase,
        float resScale,
        out SKPath result)
    {
        result = new SKPath { FillType = source.FillType };
        if (!TryCreateDashState(intervals, phase, out var initialIndex, out var initialRemaining))
        {
            return false;
        }

        using var measure = new SKPathMeasure(source, forceClosed: false, resScale);
        do
        {
            var length = measure.Length;
            if (length <= EffectEpsilon)
            {
                continue;
            }

            var distance = 0f;
            var patternIndex = initialIndex;
            var remaining = initialRemaining;
            var generated = 0;
            while (distance < length - EffectEpsilon)
            {
                var step = MathF.Min(remaining, length - distance);
                if ((patternIndex & 1) == 0 && step > EffectEpsilon)
                {
                    using var segment = measure.GetSegment(distance, distance + step, startWithMoveTo: true);
                    if (segment is not null)
                    {
                        result.AddPath(segment);
                    }
                }

                distance += step;
                remaining -= step;
                if (remaining <= EffectEpsilon && !AdvanceDash(intervals, ref patternIndex, ref remaining))
                {
                    result.Dispose();
                    result = new SKPath(source);
                    return false;
                }

                if (++generated > MaximumGeneratedElements)
                {
                    result.Dispose();
                    result = new SKPath(source);
                    return false;
                }
            }
        }
        while (measure.NextContour());

        return true;
    }

    private static bool TryCreateDashState(
        ReadOnlySpan<float> intervals,
        float phaseValue,
        out int index,
        out float remaining)
    {
        index = 0;
        remaining = 0f;
        if (intervals.Length == 0 || (intervals.Length & 1) != 0 || !float.IsFinite(phaseValue))
        {
            return false;
        }

        var patternLength = 0f;
        foreach (var interval in intervals)
        {
            if (!float.IsFinite(interval) || interval < 0f)
            {
                return false;
            }
            patternLength += interval;
        }
        if (!float.IsFinite(patternLength) || patternLength <= EffectEpsilon)
        {
            return false;
        }

        var phase = phaseValue % patternLength;
        if (phase < 0f)
        {
            phase += patternLength;
        }

        remaining = intervals[0];
        if (!AdvancePastEmptyDashIntervals(intervals, ref index, ref remaining))
        {
            return false;
        }
        while (phase >= remaining - EffectEpsilon)
        {
            phase -= remaining;
            if (!AdvanceDash(intervals, ref index, ref remaining))
            {
                return false;
            }
        }
        remaining -= phase;
        return remaining > EffectEpsilon;
    }

    private static bool AdvanceDash(ReadOnlySpan<float> intervals, ref int index, ref float remaining)
    {
        index = (index + 1) % intervals.Length;
        remaining = intervals[index];
        return AdvancePastEmptyDashIntervals(intervals, ref index, ref remaining);
    }

    private static bool AdvancePastEmptyDashIntervals(ReadOnlySpan<float> intervals, ref int index, ref float remaining)
    {
        for (var skipped = 0; skipped < intervals.Length; skipped++)
        {
            if (remaining > EffectEpsilon)
            {
                return true;
            }
            index = (index + 1) % intervals.Length;
            remaining = intervals[index];
        }
        return false;
    }

    private static bool TryApplyTrim(
        SKPath source,
        TrimData trim,
        float resScale,
        out SKPath result)
    {
        result = new SKPath { FillType = source.FillType };
        if (!float.IsFinite(trim.Start) || !float.IsFinite(trim.Stop) ||
            trim.Mode is not (SKTrimPathEffectMode.Normal or SKTrimPathEffectMode.Inverted))
        {
            return false;
        }

        var start = Math.Clamp(trim.Start, 0f, 1f);
        var stop = Math.Clamp(trim.Stop, 0f, 1f);
        using var measure = new SKPathMeasure(source, forceClosed: false, resScale);
        do
        {
            var length = measure.Length;
            if (length <= EffectEpsilon)
            {
                continue;
            }

            if (trim.Mode == SKTrimPathEffectMode.Normal)
            {
                AppendNormalizedRange(measure, result, start, stop, length);
            }
            else if (start <= stop)
            {
                AppendMeasuredRange(measure, result, 0f, start * length);
                AppendMeasuredRange(measure, result, stop * length, length);
            }
            else
            {
                AppendMeasuredRange(measure, result, stop * length, start * length);
            }
        }
        while (measure.NextContour());

        return true;
    }

    private static void AppendNormalizedRange(
        SKPathMeasure measure,
        SKPath result,
        float start,
        float stop,
        float length)
    {
        if (start <= stop)
        {
            AppendMeasuredRange(measure, result, start * length, stop * length);
        }
        else
        {
            AppendMeasuredRange(measure, result, start * length, length);
            AppendMeasuredRange(measure, result, 0f, stop * length);
        }
    }

    private static void AppendMeasuredRange(SKPathMeasure measure, SKPath result, float start, float stop)
    {
        if (stop - start <= EffectEpsilon)
        {
            return;
        }
        using var segment = measure.GetSegment(start, stop, startWithMoveTo: true);
        if (segment is not null)
        {
            result.AddPath(segment);
        }
    }

    private static bool TryApplySum(
        SKPath source,
        PairData sum,
        float resScale,
        out SKPath result)
    {
        if (!TryApply(sum.First, source, resScale, out var first))
        {
            first.Dispose();
            result = new SKPath(source);
            return false;
        }
        using (first)
        {
            if (!TryApply(sum.Second, source, resScale, out var second))
            {
                second.Dispose();
                result = new SKPath(source);
                return false;
            }
            using (second)
            {
                result = new SKPath { FillType = source.FillType };
                result.AddPath(first);
                result.AddPath(second);
                return true;
            }
        }
    }

    private static bool TryApplyCorner(SKPath source, CornerData corner, out SKPath result)
    {
        result = new SKPath { FillType = source.FillType };
        if (!float.IsFinite(corner.Radius) || corner.Radius <= 0f)
        {
            result.AddPath(source);
            return corner.Radius == 0f;
        }

        foreach (var figure in source.Geometry.Figures)
        {
            if (!TryRoundLineFigure(result, figure, corner.Radius))
            {
                result.Geometry.Figures.Add(SKPath.CloneFigureForPathEffect(figure));
            }
        }
        return true;
    }

    private static bool TryRoundLineFigure(SKPath result, PathFigure figure, float radius)
    {
        if (figure.Segments.Count == 0 || figure.Segments.Any(static segment => segment is not LineSegment))
        {
            return false;
        }

        var points = new List<Vector2>(figure.Segments.Count + 1) { figure.StartPoint };
        points.AddRange(figure.Segments.Cast<LineSegment>().Select(static segment => segment.Point));
        if (figure.IsClosed && points.Count > 1 && Vector2.DistanceSquared(points[0], points[^1]) <= EffectEpsilon * EffectEpsilon)
        {
            points.RemoveAt(points.Count - 1);
        }
        if (points.Count < (figure.IsClosed ? 3 : 2))
        {
            return false;
        }

        if (!figure.IsClosed)
        {
            result.MoveTo(points[0].X, points[0].Y);
            for (var index = 1; index < points.Count - 1; index++)
            {
                AppendRoundedCorner(result, points[index - 1], points[index], points[index + 1], radius);
            }
            result.LineTo(points[^1].X, points[^1].Y);
            return true;
        }

        GetRoundedCorner(points[^1], points[0], points[1], radius, out var firstBefore, out var firstAfter);
        result.MoveTo(firstAfter.X, firstAfter.Y);
        for (var index = 1; index < points.Count; index++)
        {
            AppendRoundedCorner(
                result,
                points[index - 1],
                points[index],
                points[(index + 1) % points.Count],
                radius);
        }
        result.LineTo(firstBefore.X, firstBefore.Y);
        result.QuadTo(points[0].X, points[0].Y, firstAfter.X, firstAfter.Y);
        result.Close();
        return true;
    }

    private static void AppendRoundedCorner(
        SKPath result,
        Vector2 previous,
        Vector2 corner,
        Vector2 next,
        float radius)
    {
        GetRoundedCorner(previous, corner, next, radius, out var before, out var after);
        result.LineTo(before.X, before.Y);
        result.QuadTo(corner.X, corner.Y, after.X, after.Y);
    }

    private static void GetRoundedCorner(
        Vector2 previous,
        Vector2 corner,
        Vector2 next,
        float radius,
        out Vector2 before,
        out Vector2 after)
    {
        var incoming = corner - previous;
        var outgoing = next - corner;
        var incomingLength = incoming.Length();
        var outgoingLength = outgoing.Length();
        if (incomingLength <= EffectEpsilon || outgoingLength <= EffectEpsilon)
        {
            before = corner;
            after = corner;
            return;
        }

        var trim = MathF.Min(radius, MathF.Min(incomingLength, outgoingLength) * 0.5f);
        before = corner - incoming * (trim / incomingLength);
        after = corner + outgoing * (trim / outgoingLength);
    }

    private static bool TryApplyDiscrete(
        SKPath source,
        DiscreteData discrete,
        float resScale,
        out SKPath result)
    {
        result = new SKPath { FillType = source.FillType };
        if (!float.IsFinite(discrete.SegmentLength) || discrete.SegmentLength <= EffectEpsilon ||
            !float.IsFinite(discrete.Deviation) || discrete.Deviation < 0f)
        {
            return false;
        }

        var random = discrete.SeedAssist == 0 ? 0x9e3779b9u : discrete.SeedAssist;
        using var measure = new SKPathMeasure(source, forceClosed: false, resScale);
        do
        {
            var length = measure.Length;
            var exactSegmentCount = MathF.Ceiling(length / discrete.SegmentLength);
            if (!float.IsFinite(exactSegmentCount) || exactSegmentCount > MaximumGeneratedElements)
            {
                result.Dispose();
                result = new SKPath(source);
                return false;
            }
            var segmentCount = (int)exactSegmentCount;
            if (segmentCount <= 0)
            {
                continue;
            }
            if (segmentCount > MaximumGeneratedElements)
            {
                result.Dispose();
                result = new SKPath(source);
                return false;
            }

            for (var index = 0; index <= segmentCount; index++)
            {
                var distance = MathF.Min(length, index * discrete.SegmentLength);
                if (!measure.GetPositionAndTangent(distance, out var position, out var tangent))
                {
                    continue;
                }

                var offset = NextSignedRandom(ref random) * discrete.Deviation;
                var point = new SKPoint(position.X - tangent.Y * offset, position.Y + tangent.X * offset);
                if (index == 0)
                {
                    result.MoveTo(point);
                }
                else
                {
                    result.LineTo(point);
                }
            }
            if (measure.IsClosed)
            {
                result.Close();
            }
        }
        while (measure.NextContour());
        return true;
    }

    private static float NextSignedRandom(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return ((state >> 8) * (1f / 16777215f)) * 2f - 1f;
    }

    private static bool TryApplyPath1D(
        SKPath source,
        Path1DData path1D,
        float resScale,
        out SKPath result)
    {
        result = new SKPath { FillType = source.FillType };
        if (!float.IsFinite(path1D.Advance) || path1D.Advance <= EffectEpsilon ||
            !float.IsFinite(path1D.Phase) ||
            path1D.Style is not (SKPath1DPathEffectStyle.Translate or SKPath1DPathEffectStyle.Rotate or SKPath1DPathEffectStyle.Morph))
        {
            return false;
        }

        var phase = path1D.Phase % path1D.Advance;
        if (phase < 0f)
        {
            phase += path1D.Advance;
        }

        using var measure = new SKPathMeasure(source, forceClosed: false, resScale);
        do
        {
            var length = measure.Length;
            var exactStampCount = length <= phase
                ? 0f
                : MathF.Floor((length - phase) / path1D.Advance) + 1f;
            if (!float.IsFinite(exactStampCount) || exactStampCount > MaximumGeneratedElements)
            {
                result.Dispose();
                result = new SKPath(source);
                return false;
            }
            var stampCount = (int)exactStampCount;

            for (var index = 0; index < stampCount; index++)
            {
                var distance = phase + index * path1D.Advance;
                if (!measure.GetPositionAndTangent(distance, out var position, out var tangent))
                {
                    continue;
                }

                if (path1D.Style == SKPath1DPathEffectStyle.Morph)
                {
                    using var morphed = MorphStamp(path1D.Path, measure, distance);
                    result.AddPath(morphed);
                    continue;
                }

                var matrix = path1D.Style == SKPath1DPathEffectStyle.Rotate
                    ? new SKMatrix(
                        tangent.X,
                        -tangent.Y,
                        position.X,
                        tangent.Y,
                        tangent.X,
                        position.Y,
                        0f,
                        0f,
                        1f)
                    : SKMatrix.CreateTranslation(position.X, position.Y);
                result.AddPath(path1D.Path, in matrix);
            }
        }
        while (measure.NextContour());
        return true;
    }

    private static SKPath MorphStamp(SKPath stamp, SKPathMeasure measure, float baseDistance)
    {
        var result = new SKPath { FillType = stamp.FillType };
        using var iterator = stamp.CreateRawIterator();
        Span<SKPoint> points = stackalloc SKPoint[4];
        while (true)
        {
            var verb = iterator.Next(points);
            switch (verb)
            {
                case SKPathVerb.Move:
                    result.MoveTo(MapMorphPoint(measure, baseDistance, points[0]));
                    break;
                case SKPathVerb.Line:
                    result.LineTo(MapMorphPoint(measure, baseDistance, points[1]));
                    break;
                case SKPathVerb.Quad:
                    result.QuadTo(
                        MapMorphPoint(measure, baseDistance, points[1]),
                        MapMorphPoint(measure, baseDistance, points[2]));
                    break;
                case SKPathVerb.Conic:
                    result.ConicTo(
                        MapMorphPoint(measure, baseDistance, points[1]),
                        MapMorphPoint(measure, baseDistance, points[2]),
                        iterator.ConicWeight());
                    break;
                case SKPathVerb.Cubic:
                    result.CubicTo(
                        MapMorphPoint(measure, baseDistance, points[1]),
                        MapMorphPoint(measure, baseDistance, points[2]),
                        MapMorphPoint(measure, baseDistance, points[3]));
                    break;
                case SKPathVerb.Close:
                    result.Close();
                    break;
                case SKPathVerb.Done:
                    return result;
            }
        }
    }

    private static SKPoint MapMorphPoint(SKPathMeasure measure, float baseDistance, SKPoint local)
    {
        if (!measure.GetPositionAndTangent(baseDistance + local.X, out var position, out var tangent))
        {
            return SKPoint.Empty;
        }
        return new SKPoint(position.X - tangent.Y * local.Y, position.Y + tangent.X * local.Y);
    }

    private static bool TryApplyLine2D(
        SKPath source,
        Line2DData line2D,
        float resScale,
        out SKPath result)
    {
        result = new SKPath { FillType = source.FillType };
        if (!float.IsFinite(line2D.Width) || line2D.Width < 0f ||
            HasPerspective(line2D.Matrix) ||
            !line2D.Matrix.TryInvert(out var inverse) ||
            !TryBuildLatticeContours(source, inverse, resScale, out var contours, out var minimum, out var maximum))
        {
            return false;
        }

        if (!TryGetLatticeRange(minimum.Y, maximum.Y, padding: 0, out var firstRow, out var lastRow))
        {
            return false;
        }
        if ((long)lastRow - firstRow + 1 > MaximumGeneratedElements)
        {
            return false;
        }

        var intersections = new List<float>();
        var generated = 0;
        for (var row = firstRow; row <= lastRow; row++)
        {
            intersections.Clear();
            var y = (float)row;
            foreach (var contour in contours)
            {
                for (var index = 1; index < contour.Length; index++)
                {
                    var first = contour[index - 1];
                    var second = contour[index];
                    if ((first.Y <= y && second.Y > y) || (second.Y <= y && first.Y > y))
                    {
                        var amount = (y - first.Y) / (second.Y - first.Y);
                        intersections.Add(first.X + (second.X - first.X) * amount);
                    }
                }
            }

            intersections.Sort();
            for (var index = 1; index < intersections.Count; index++)
            {
                var left = intersections[index - 1];
                var right = intersections[index];
                if (right - left <= EffectEpsilon)
                {
                    continue;
                }

                var midpoint = line2D.Matrix.MapPoint((left + right) * 0.5f, y);
                if (!source.Contains(midpoint.X, midpoint.Y))
                {
                    continue;
                }

                var start = line2D.Matrix.MapPoint(left, y);
                var stop = line2D.Matrix.MapPoint(right, y);
                result.MoveTo(start);
                result.LineTo(stop);
                if (++generated > MaximumGeneratedElements)
                {
                    result.Dispose();
                    result = new SKPath(source);
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryApplyPath2D(SKPath source, Path2DData path2D, out SKPath result)
    {
        result = new SKPath { FillType = path2D.Path.FillType };
        if (HasPerspective(path2D.Matrix) || !path2D.Matrix.TryInvert(out var inverse))
        {
            return false;
        }

        var latticeBounds = inverse.MapRect(source.Bounds);
        if (!IsFinite(latticeBounds))
        {
            return false;
        }

        if (!TryGetLatticeRange(latticeBounds.Left, latticeBounds.Right, padding: 1, out var firstColumn, out var lastColumn) ||
            !TryGetLatticeRange(latticeBounds.Top, latticeBounds.Bottom, padding: 1, out var firstRow, out var lastRow))
        {
            return false;
        }

        var columnCount = (long)lastColumn - firstColumn + 1;
        var rowCount = (long)lastRow - firstRow + 1;
        if (columnCount > MaximumGeneratedElements || rowCount > MaximumGeneratedElements ||
            columnCount * rowCount > MaximumGeneratedElements)
        {
            return false;
        }

        var generatedVerbs = 0L;
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var origin = path2D.Matrix.MapPoint(column, row);
                if (!source.Contains(origin.X, origin.Y))
                {
                    continue;
                }

                generatedVerbs += path2D.Path.VerbCount;
                if (generatedVerbs > MaximumGeneratedElements)
                {
                    result.Dispose();
                    result = new SKPath(source);
                    return false;
                }
                result.AddPath(path2D.Path, origin.X, origin.Y);
            }
        }
        return true;
    }

    private static bool TryBuildLatticeContours(
        SKPath source,
        SKMatrix inverse,
        float resScale,
        out List<Vector2[]> contours,
        out Vector2 minimum,
        out Vector2 maximum)
    {
        contours = new List<Vector2[]>();
        minimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        maximum = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        var generated = 0;
        using var measure = new SKPathMeasure(source, forceClosed: true, resScale);
        do
        {
            var length = measure.Length;
            if (length <= EffectEpsilon)
            {
                continue;
            }

            var exactSegmentCount = MathF.Ceiling(length * MathF.Max(1f, resScale) * 0.5f);
            if (!float.IsFinite(exactSegmentCount) || exactSegmentCount > MaximumGeneratedElements)
            {
                return false;
            }
            var segmentCount = Math.Max(2, (int)exactSegmentCount);
            generated = checked(generated + segmentCount + 1);
            if (generated > MaximumGeneratedElements)
            {
                return false;
            }

            var contour = new Vector2[segmentCount + 1];
            for (var index = 0; index <= segmentCount; index++)
            {
                var point = measure.GetPosition(length * index / segmentCount);
                var lattice = inverse.MapPoint(point);
                var value = new Vector2(lattice.X, lattice.Y);
                contour[index] = value;
                minimum = Vector2.Min(minimum, value);
                maximum = Vector2.Max(maximum, value);
            }
            contours.Add(contour);
        }
        while (measure.NextContour());

        return contours.Count > 0 &&
            float.IsFinite(minimum.X) && float.IsFinite(minimum.Y) &&
            float.IsFinite(maximum.X) && float.IsFinite(maximum.Y);
    }

    private static bool IsFinite(SKRect rect) =>
        float.IsFinite(rect.Left) && float.IsFinite(rect.Top) &&
        float.IsFinite(rect.Right) && float.IsFinite(rect.Bottom);

    private static bool TryGetLatticeRange(
        float minimum,
        float maximum,
        int padding,
        out int first,
        out int last)
    {
        first = 0;
        last = -1;
        var floor = MathF.Floor(minimum);
        var ceiling = MathF.Ceiling(maximum);
        if (!float.IsFinite(floor) || !float.IsFinite(ceiling) ||
            floor < int.MinValue + padding || ceiling > int.MaxValue - padding)
        {
            return false;
        }

        first = (int)floor - padding;
        last = (int)ceiling + padding;
        return last >= first;
    }

    private static bool HasPerspective(SKMatrix matrix) =>
        matrix.Persp0 != 0f || matrix.Persp1 != 0f || matrix.Persp2 != 1f;
}

public partial class SKPath
{
    internal static PathFigure CloneFigureForPathEffect(PathFigure figure) =>
        CloneFigure(figure, Vector2.Zero);
}
