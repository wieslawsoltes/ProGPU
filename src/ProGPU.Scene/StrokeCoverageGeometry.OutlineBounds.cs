using System;
using System.Numerics;
using System.Runtime.Intrinsics;
using ProGPU.Vector;

namespace ProGPU.Scene;

public static partial class StrokeCoverageGeometry
{
    // Original ProGPU 18ee4f8b: native Direct2D compound stroke pieces,
    // append_stroke_side_join, append_circular_arc_segments and terminal caps.
    // Unlike ideal solid-stroke support, this measures float-narrowed emitted
    // cubics. Input is an owned, gap-split, constant-compacted undashed spine;
    // closing edges must be explicit. No fill geometry or world transform enters
    // the material bounds. O(S) time, O(1) scratch, no outline object allocation.
    internal static bool TryMeasurePreparedLinearStrokeOutline(
        PathGeometry prepared, Pen pen, out Rect bounds)
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
                if (i == 0) firstDirection = direction;
                else if (!IncludeEmittedLinearJoin(ref state, start, previousDirection, direction, radius,
                    segment.IsSmoothJoin ? PenLineJoin.Round : pen.LineJoin, Math.Max(1, pen.MiterLimit))) return false;
                previousDirection = direction;
                start = end;
            }
            if (figure.IsClosed)
            {
                if (!IncludeEmittedLinearJoin(ref state, Wide(figure.StartPoint), previousDirection,
                    firstDirection, radius, figure.Segments[0].IsSmoothJoin ? PenLineJoin.Round : pen.LineJoin,
                    Math.Max(1, pen.MiterLimit))) return false;
            }
            else if (!IncludeEmittedLinearCap(ref state, Wide(figure.StartPoint), -firstDirection, radius,
                         figure.StrokeStartLineCap ?? pen.StartLineCap)
                     || !IncludeEmittedLinearCap(ref state, start, previousDirection, radius,
                         figure.StrokeEndLineCap ?? pen.EndLineCap)) return false;
            any = true;
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
        Vector128<double> outward, double radius, PenLineCap cap)
    {
        if (cap == PenLineCap.Flat) return true;
        var normal = LeftNormal(outward) * Vector128.Create(radius);
        var first = NarrowPair(center + normal);
        var last = NarrowPair(center - normal);
        var along = outward * Vector128.Create(radius);
        if (cap == PenLineCap.Square)
        {
            state.Include(NarrowPair(first + along)); state.Include(NarrowPair(last + along));
            return true;
        }
        var outer = NarrowPair(center + along);
        if (cap == PenLineCap.Triangle) { state.Include(outer); return true; }
        var side = first - center;
        double sideLength = double.Hypot(side[0], side[1]);
        if (sideLength == 0 || !double.IsFinite(sideLength)) return false;
        var sideUnit = side / Vector128.Create(sideLength);
        var distance = Vector128.Create(radius * 0.5522847498307933984);
        state.IncludeCubic(first, first + outward * distance, outer + sideUnit * distance, outer);
        state.IncludeCubic(outer, outer - sideUnit * distance, last + outward * distance, last);
        return true;
    }

    private static bool IncludeEmittedLinearJoin(ref LineBounds state, Vector128<double> center,
        Vector128<double> incoming, Vector128<double> outgoing, double radius, PenLineJoin join, double limit)
    {
        double turn = Cross(incoming, outgoing);
        if (Math.Abs(turn) <= 1e-6)
            return join != PenLineJoin.Round || Dot(incoming, outgoing) >= 0
                || IncludeEmittedLinearCap(ref state, center, incoming, radius, PenLineCap.Round);
        if (join == PenLineJoin.Bevel) return true;
        double side = turn > 0 ? -1 : 1;
        var a = NarrowPair(center + LeftNormal(incoming) * Vector128.Create(radius * side));
        var b = NarrowPair(center + LeftNormal(outgoing) * Vector128.Create(radius * side));
        if (join == PenLineJoin.Round) return IncludeEmittedCircularArc(ref state, center, a, b);
        var intersection = NarrowPair(a + incoming * Vector128.Create(Cross(b - a, outgoing) / turn));
        var delta = intersection - center;
        double length = double.Hypot(delta[0], delta[1]);
        if (!double.IsFinite(length)) return false;
        if (length <= limit * radius) { state.Include(intersection); return true; }
        var bisector = delta / Vector128.Create(length);
        return IncludeEmittedClippedMiter(ref state, center, a, intersection, bisector, length, limit * radius)
            && IncludeEmittedClippedMiter(ref state, center, b, intersection, bisector, length, limit * radius);
    }

    private static bool IncludeEmittedClippedMiter(ref LineBounds state, Vector128<double> center,
        Vector128<double> offset, Vector128<double> intersection, Vector128<double> bisector,
        double length, double clipDistance)
    {
        double projection = Dot(offset - center, bisector), divisor = length - projection;
        if (divisor <= 0) return false;
        double amount = (clipDistance - projection) / divisor;
        if (!double.IsFinite(amount) || amount < 0 || amount > 1) return false;
        // Native subtracts the float offset/intersection before widening for the
        // multiply. Keep this narrowing separate from the final point conversion.
        state.Include(NarrowPair(offset + NarrowPair(intersection - offset) * Vector128.Create(amount)));
        return true;
    }

    private static bool IncludeEmittedCircularArc(ref LineBounds state, Vector128<double> center,
        Vector128<double> first, Vector128<double> last)
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
            state.IncludeCubic(current,
                current + LeftNormal(currentUnit) * Vector128.Create(radius) * factor,
                next - LeftNormal(nextUnit) * Vector128.Create(radius) * factor, next);
            current = next;
            currentAngle = nextAngle;
        }
        return true;
    }
}
