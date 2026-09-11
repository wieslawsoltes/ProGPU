using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.Intrinsics;
using ProGPU.Vector;

namespace ProGPU.Scene;

internal readonly record struct DirectedStrokeCaps(
    Vector2 Point, Vector2 Adjacent, PenLineCap StartCap, PenLineCap EndCap);

public static partial class StrokeCoverageGeometry
{
    // Original ProGPU 18ee4f8b: native Direct2D compound stroke pieces,
    // append_stroke_side_join, append_circular_arc_segments and terminal caps.
    // Unlike ideal solid-stroke support, this measures float-narrowed emitted
    // cubics. Input is an owned, gap-split, constant-compacted undashed spine;
    // closing edges must be explicit. No fill geometry or world transform enters
    // the material bounds. Bounds-only: O(S) time, O(1) scratch, no outline object
    // allocation. Optional materialization owns O(S + T) positive-winding pieces
    // for S stroke records and T directed terminal-cap records.
    internal static bool TryMeasurePreparedLinearStrokeOutline(
        PathGeometry prepared, Pen pen, out Rect bounds,
        PathGeometry? outline = null, IReadOnlyList<DirectedStrokeCaps>? terminals = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(pen);
        bounds = default;
        if (prepared.IsCombined || pen.HasDashPattern || pen.StrokeTransformMode != PenStrokeTransformMode.Normal
            || !float.IsFinite(pen.Thickness) || pen.Thickness < 0 || !float.IsFinite(pen.MiterLimit)
            || (uint)pen.LineJoin > 2 || (uint)pen.StartLineCap > 3 || (uint)pen.EndLineCap > 3) return false;
        int records = prepared.Figures.Count;
        if (records > 1_000_000) return false;
        for (int f = 0; f < prepared.Figures.Count; f++)
        {
            var figure = prepared.Figures[f];
            if (figure == null || !FinitePoint(figure.StartPoint)
                || (uint)(figure.StrokeStartLineCap ?? pen.StartLineCap) > 3
                || (uint)(figure.StrokeEndLineCap ?? pen.EndLineCap) > 3
                || figure.Segments.Count > 1_000_000 - records) return false;
            records += figure.Segments.Count;
            var current = figure.StartPoint;
            for (int i = 0; i < figure.Segments.Count; i++)
            {
                if (figure.Segments[i] is not LineSegment line || !line.IsStroked
                    || !FinitePoint(line.Point) || line.Point == current) return false;
                current = line.Point;
            }
            if (figure.IsClosed && current != figure.StartPoint) return false;
        }
        if (pen.Thickness == 0) return true;
        double radius = pen.Thickness * 0.5;
        var state = new LineBounds();
        bool any = false;
        for (int f = 0; f < prepared.Figures.Count; f++)
        {
            var figure = prepared.Figures[f];
            if (figure.Segments.Count == 0) continue;
            var start = Wide(figure.StartPoint);
            var firstDirection = Vector128<double>.Zero;
            var previousDirection = Vector128<double>.Zero;
            for (int i = 0; i < figure.Segments.Count; i++)
            {
                var segment = (LineSegment)figure.Segments[i];
                var end = Wide(segment.Point);
                var delta = end - start;
                double length = double.Hypot(delta[0], delta[1]);
                var direction = delta / Vector128.Create(length);
                var normal = LeftNormal(direction) * Vector128.Create(radius);
                var a = NarrowPair(start + normal); var b = NarrowPair(start - normal);
                var c = NarrowPair(end - normal); var d = NarrowPair(end + normal);
                // Native compound pieces reject a float-collapsed zero-area body.
                double area = Cross(b - a, c - a) + Cross(c - a, d - a);
                if (!double.IsFinite(area) || area == 0) return false;
                state.Include(a); state.Include(b); state.Include(c); state.Include(d);
                if (outline != null)
                {
                    var body = OutlineFigure(a);
                    body.Segments.Add(OutlineLine(b)); body.Segments.Add(OutlineLine(c)); body.Segments.Add(OutlineLine(d));
                    if (!AppendPositiveOutline(outline, body)) return false;
                }
                if (i == 0) firstDirection = direction;
                else if (!IncludeEmittedLinearJoin(ref state, start, previousDirection, direction, radius,
                    segment.IsSmoothJoin ? PenLineJoin.Round : pen.LineJoin, Math.Max(1, pen.MiterLimit), outline)) return false;
                previousDirection = direction;
                start = end;
            }
            if (figure.IsClosed)
            {
                if (!IncludeEmittedLinearJoin(ref state, Wide(figure.StartPoint), previousDirection,
                    firstDirection, radius, figure.Segments[0].IsSmoothJoin ? PenLineJoin.Round : pen.LineJoin,
                    Math.Max(1, pen.MiterLimit), outline)) return false;
            }
            else if (!IncludeEmittedLinearCap(ref state, Wide(figure.StartPoint), -firstDirection, radius,
                         figure.StrokeStartLineCap ?? pen.StartLineCap, outline)
                     || !IncludeEmittedLinearCap(ref state, start, previousDirection, radius,
                         figure.StrokeEndLineCap ?? pen.EndLineCap, outline)) return false;
            any = true;
        }
        if (terminals != null)
        {
            for (int i = 0; i < terminals.Count; i++)
            {
                var terminal = terminals[i];
                if (!FinitePoint(terminal.Point) || !FinitePoint(terminal.Adjacent)
                    || (uint)terminal.StartCap > 3 || (uint)terminal.EndCap > 3) return false;
                var point = Wide(terminal.Point);
                var delta = point - Wide(terminal.Adjacent);
                double length = double.Hypot(delta[0], delta[1]);
                if (length == 0 || !double.IsFinite(length)) return false;
                var outward = delta / Vector128.Create(length);
                if (!IncludeEmittedLinearCap(ref state, point, -outward, radius, terminal.StartCap, outline)
                    || !IncludeEmittedLinearCap(ref state, point, outward, radius, terminal.EndCap, outline)) return false;
                any |= terminal.StartCap != PenLineCap.Flat || terminal.EndCap != PenLineCap.Flat;
            }
        }
        if (!any) return true;
        if (!state.TryGetBounds(out var min, out var max)) return false;
        var extent = max - min;
        if (!FinitePoint(extent)) return false;
        if (extent.X <= 0 || extent.Y <= 0) return true;
        var result = new Rect(min.X, min.Y, extent.X, extent.Y);
        if (!float.IsFinite(result.Right) || !float.IsFinite(result.Bottom)) return false;
        bounds = result;
        return true;
    }

    private static bool IncludeEmittedLinearCap(ref LineBounds state, Vector128<double> center,
        Vector128<double> outward, double radius, PenLineCap cap, PathGeometry? outline = null)
    {
        if (cap == PenLineCap.Flat) return true;
        var normal = LeftNormal(outward) * Vector128.Create(radius);
        var first = NarrowPair(center + normal);
        var last = NarrowPair(center - normal);
        state.Include(first); state.Include(last);
        var figure = outline == null ? null : OutlineFigure(first);
        figure?.Segments.Add(OutlineLine(last));
        var along = outward * Vector128.Create(radius);
        if (cap == PenLineCap.Square)
        {
            var farFirst = NarrowPair(first + along); var farLast = NarrowPair(last + along);
            state.Include(farFirst); state.Include(farLast);
            figure?.Segments.Add(OutlineLine(farLast)); figure?.Segments.Add(OutlineLine(farFirst));
            if (figure != null && !AppendPositiveOutline(outline!, figure)) return false;
            return true;
        }
        var outer = NarrowPair(center + along);
        if (cap == PenLineCap.Triangle)
        {
            state.Include(outer); figure?.Segments.Add(OutlineLine(outer));
            if (figure != null && !AppendPositiveOutline(outline!, figure)) return false;
            return true;
        }
        var side = first - center;
        double sideLength = double.Hypot(side[0], side[1]);
        if (sideLength == 0 || !double.IsFinite(sideLength)) return false;
        var sideUnit = side / Vector128.Create(sideLength);
        var distance = Vector128.Create(radius * 0.5522847498307933984);
        state.IncludeCubic(first, first + outward * distance, outer + sideUnit * distance, outer);
        state.IncludeCubic(outer, outer - sideUnit * distance, last + outward * distance, last);
        if (figure != null)
        {
            // Reverse the outward semicircle after its flat base so every
            // compound piece has the same positive nonzero winding.
            figure.Segments.Add(OutlineCubic(last + outward * distance, outer - sideUnit * distance, outer));
            figure.Segments.Add(OutlineCubic(outer + sideUnit * distance, first + outward * distance, first));
            if (!AppendPositiveOutline(outline!, figure)) return false;
        }
        return true;
    }

    private static bool IncludeEmittedLinearJoin(ref LineBounds state, Vector128<double> center,
        Vector128<double> incoming, Vector128<double> outgoing, double radius, PenLineJoin join, double limit,
        PathGeometry? outline = null)
    {
        double turn = Cross(incoming, outgoing);
        if (Math.Abs(turn) <= 1e-6)
            return join != PenLineJoin.Round || Dot(incoming, outgoing) >= 0
                || IncludeEmittedLinearCap(ref state, center, incoming, radius, PenLineCap.Round, outline);
        double side = turn > 0 ? -1 : 1;
        var a = NarrowPair(center + LeftNormal(incoming) * Vector128.Create(radius * side));
        var b = NarrowPair(center + LeftNormal(outgoing) * Vector128.Create(radius * side));
        var wedge = outline == null ? null : OutlineFigure(center);
        if (join == PenLineJoin.Round)
        {
            wedge?.Segments.Add(OutlineLine(turn > 0 ? a : b));
            bool reverse = wedge != null && turn < 0;
            if (!IncludeEmittedCircularArc(ref state, center, reverse ? b : a, reverse ? a : b, wedge)) return false;
            if (wedge != null && !AppendPositiveOutline(outline!, wedge)) return false;
            return true;
        }
        wedge?.Segments.Add(OutlineLine(turn > 0 ? a : b));
        if (join == PenLineJoin.Bevel)
        {
            wedge?.Segments.Add(OutlineLine(turn > 0 ? b : a));
            if (wedge != null && !AppendPositiveOutline(outline!, wedge)) return false;
            return true;
        }
        var intersection = NarrowPair(a + incoming * Vector128.Create(Cross(b - a, outgoing) / turn));
        var delta = intersection - center;
        double length = double.Hypot(delta[0], delta[1]);
        if (!double.IsFinite(length)) return false;
        if (length <= limit * radius)
        {
            state.Include(intersection); wedge?.Segments.Add(OutlineLine(intersection));
        }
        else
        {
            var bisector = delta / Vector128.Create(length);
            if (!IncludeEmittedClippedMiter(ref state, center, turn > 0 ? a : b, intersection,
                    bisector, length, limit * radius, wedge)
                || !IncludeEmittedClippedMiter(ref state, center, turn > 0 ? b : a, intersection,
                    bisector, length, limit * radius, wedge)) return false;
        }
        wedge?.Segments.Add(OutlineLine(turn > 0 ? b : a));
        if (wedge != null && !AppendPositiveOutline(outline!, wedge)) return false;
        return true;
    }

    private static bool IncludeEmittedClippedMiter(ref LineBounds state, Vector128<double> center,
        Vector128<double> offset, Vector128<double> intersection, Vector128<double> bisector,
        double length, double clipDistance, PathFigure? figure = null)
    {
        double projection = Dot(offset - center, bisector), divisor = length - projection;
        if (divisor <= 0) return false;
        double amount = (clipDistance - projection) / divisor;
        if (!double.IsFinite(amount) || amount < 0 || amount > 1) return false;
        // Native subtracts the float offset/intersection before widening for the
        // multiply. Keep this narrowing separate from the final point conversion.
        var point = NarrowPair(offset + NarrowPair(intersection - offset) * Vector128.Create(amount));
        state.Include(point); figure?.Segments.Add(OutlineLine(point));
        return true;
    }

    private static bool IncludeEmittedCircularArc(ref LineBounds state, Vector128<double> center,
        Vector128<double> first, Vector128<double> last, PathFigure? figure = null)
    {
        var a = first - center; var b = last - center;
        double ra = double.Hypot(a[0], a[1]), rb = double.Hypot(b[0], b[1]);
        if (ra == 0 || rb == 0) return false;
        double radius = (ra + rb) * 0.5;
        double angle = Math.Atan2(Cross(a, b), Dot(a, b));
        if (!double.IsFinite(angle) || angle == 0) return false;
        int count = (int)Math.Ceiling(Math.Abs(angle) / (Math.PI * 0.5));
        if (count is < 1 or > 2) return false;
        double step = angle / count;
        double currentAngle = Math.Atan2(a[1], a[0]);
        var current = first;
        for (int i = 0; i < count; i++)
        {
            double nextAngle = currentAngle + step;
            var next = i + 1 == count ? last : NarrowPair(center
                + Vector128.Create(Math.Cos(nextAngle), Math.Sin(nextAngle)) * Vector128.Create(radius));
            var factor = Vector128.Create(4.0 / 3.0 * Math.Tan(step * 0.25));
            var currentUnit = (current - center) / Vector128.Create(radius);
            var nextUnit = (next - center) / Vector128.Create(radius);
            var control1 = current + LeftNormal(currentUnit) * Vector128.Create(radius) * factor;
            var control2 = next - LeftNormal(nextUnit) * Vector128.Create(radius) * factor;
            state.IncludeCubic(current, control1, control2, next);
            figure?.Segments.Add(OutlineCubic(control1, control2, next));
            current = next;
            currentAngle = nextAngle;
        }
        return true;
    }

    private static Vector2 OutlinePoint(Vector128<double> point) => new((float)point[0], (float)point[1]);
    private static bool AppendPositiveOutline(PathGeometry output, PathFigure figure)
    {
        var anchor = Wide(figure.StartPoint);
        var previous = anchor;
        double area = 0;
        // Each emitted piece has a fixed bounded number of edges. Use locally
        // anchored endpoint area, as the native compound writer does, so a large
        // translation cannot erase a small shape by cancellation.
        for (int i = 0; i < figure.Segments.Count; i++)
        {
            var segment = figure.Segments[i];
            var end = segment is LineSegment line ? line.Point : ((CubicBezierSegment)segment).Point;
            if (!FinitePoint(end)) return false;
            if (segment is CubicBezierSegment cubic
                && (!FinitePoint(cubic.ControlPoint1) || !FinitePoint(cubic.ControlPoint2))) return false;
            var current = Wide(end);
            area += Cross(previous - anchor, current - anchor);
            previous = current;
        }
        if (!double.IsFinite(area) || area <= 0) return false;
        output.Figures.Add(figure);
        return true;
    }
    private static PathFigure OutlineFigure(Vector128<double> start) => new(OutlinePoint(start), isClosed: true);
    private static LineSegment OutlineLine(Vector128<double> point) => new(OutlinePoint(point));
    private static CubicBezierSegment OutlineCubic(Vector128<double> a, Vector128<double> b, Vector128<double> end)
        => new(OutlinePoint(a), OutlinePoint(b), OutlinePoint(end));
}
