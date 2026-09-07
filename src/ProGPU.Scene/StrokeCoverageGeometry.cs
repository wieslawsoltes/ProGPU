using System;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.InteropServices;
using ProGPU.Vector;

namespace ProGPU.Scene;

/// <summary>Prepares retained stroke coverage and material bounds without a device.</summary>
public static class StrokeCoverageGeometry
{
    /// <summary>
    /// Prepares one positive, normal-width line, including its dash figures and
    /// asymmetric caps. Empty coverage returns true with empty bounds. Invalid
    /// state or unsupported topology fails without an approximate bounds fallback.
    /// </summary>
    public static bool TryPrepareLine(Vector2 start, Vector2 end, Pen pen,
        out PathGeometry path, out Pen coveragePen, out Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(pen);
        path = null!;
        coveragePen = null!;
        bounds = default;
        if (!float.IsFinite(start.X) || !float.IsFinite(start.Y)
            || !float.IsFinite(end.X) || !float.IsFinite(end.Y)
            || !float.IsFinite(pen.Thickness) || pen.Thickness < 0 || pen.IsFixed
            || (uint)pen.StartLineCap > 3 || (uint)pen.EndLineCap > 3 || (uint)pen.DashCap > 3
            || !double.IsFinite(pen.DashOffset)) return false;
        path = RenderCommandGeometryCache.CreateLinePath(start, end);
        coveragePen = pen;
        if (pen.Thickness == 0) return true;
        if (pen.HasDashPattern)
        {
            // Do not inherit the legacy dash engine's epsilon replacement of
            // zero/tiny intervals or allow unbounded generated figure storage.
            if (!CanPrepareDashPattern(pen, start, end)) return false;
            var cache = RenderCommandGeometryCache.ForStrokePath(path);
            if (!cache.TryGetDashedStrokePath(pen, pen.Thickness, out path, out coveragePen)) return false;
        }
        Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        bool any = false;
        // Figure order and bounds union are reductions. Independent coordinate
        // arithmetic inside each fixed-work cap uses paired intrinsic lanes.
        for (int index = 0; index < path.Figures.Count; index++)
        {
            var figure = path.Figures[index];
            if (figure.IsClosed || figure.Segments.Count != 1
                || figure.Segments[0] is not LineSegment line || !line.IsStroked) return false;
            var state = new LineBounds(figure.StartPoint, line.Point, coveragePen.Thickness);
            if (!state.IncludeCap(figure.StartPoint, -1, figure.StrokeStartLineCap ?? coveragePen.StartLineCap)
                || !state.IncludeCap(line.Point, 1, figure.StrokeEndLineCap ?? coveragePen.EndLineCap)
                || !state.TryGetBounds(out var partMin, out var partMax)) return false;
            if (partMax.X <= partMin.X || partMax.Y <= partMin.Y) continue;
            min = Vector2.Min(min, partMin);
            max = Vector2.Max(max, partMax);
            any = true;
        }
        if (!any) return true;
        var extent = max - min;
        if (!float.IsFinite(extent.X) || !float.IsFinite(extent.Y)) return false;
        bounds = new Rect(min.X, min.Y, extent.X, extent.Y);
        return float.IsFinite(bounds.Right) && float.IsFinite(bounds.Bottom);
    }

    private static bool CanPrepareDashPattern(Pen pen, Vector2 start, Vector2 end)
    {
        ReadOnlySpan<double> intervals = pen.DashArrayStorage;
        var minimum = Vector128.Create(double.PositiveInfinity);
        var sum = Vector128<double>.Zero;
        var width = Vector128.Create((double)pen.Thickness);
        int index = 0;
        ref double first = ref MemoryMarshal.GetReference(intervals);
        for (; index <= intervals.Length - 2; index += 2)
        {
            var values = Vector128.LoadUnsafe(ref first, (nuint)index) * width;
            if (!Vector128.GreaterThanAll(values, Vector128.Create(0.0001))
                || !Vector128.LessThanAll(values, Vector128.Create((double)float.MaxValue))) return false;
            minimum = Vector128.Min(minimum, values);
            sum += values;
        }
        double smallest = Math.Min(minimum[0], minimum[1]);
        double total = sum[0] + sum[1];
        if (index < intervals.Length)
        {
            double tail = intervals[index] * pen.Thickness;
            if (!(tail > 0.0001 && tail < float.MaxValue)) return false;
            smallest = Math.Min(smallest, tail);
            total += tail;
        }
        if ((intervals.Length & 1) != 0) total *= 2;
        double length = double.Hypot((double)end.X - start.X, (double)end.Y - start.Y);
        return total < float.MaxValue && float.IsFinite((float)(pen.DashOffset * pen.Thickness))
            && length / smallest <= 1_000_000;
    }

    // Original ProGPU provenance: native MIL try_transformed_line_stroke_bounds
    // and try_get_path_segment_bounds, restricted to identity geometry transforms.
    // Round caps retain the same two cubic quarters and analytic derivative roots.
    // Fixed-size Vector128<double> operations pair independent X/Y coordinates;
    // scalar root selection is data dependent and bounded to four roots per cubic.
    private struct LineBounds
    {
        private Vector128<double> _min, _max;
        private readonly Vector128<double> _normal, _along;
        private bool _valid;

        public LineBounds(Vector2 start, Vector2 end, float width)
        {
            var first = Pair(start);
            var last = Pair(end);
            var delta = last - first;
            double length = double.Hypot(delta[0], delta[1]);
            var unit = length == 0 ? Vector128.Create(1.0, 0.0) : delta / Vector128.Create(length);
            _along = unit * Vector128.Create(width * 0.5);
            _normal = Vector128.Create(-_along[1], _along[0]);
            _min = Vector128.Create(double.PositiveInfinity);
            _max = Vector128.Create(double.NegativeInfinity);
            _valid = double.IsFinite(length);
            Include(first - _normal); Include(first + _normal);
            Include(last - _normal); Include(last + _normal);
        }

        public bool IncludeCap(Vector2 center, double sign, PenLineCap cap)
        {
            var origin = Pair(center);
            var along = _along * Vector128.Create(sign);
            var outer = origin + along;
            switch (cap)
            {
                case PenLineCap.Flat: break;
                case PenLineCap.Square:
                    Include(outer - _normal); Include(outer + _normal); break;
                case PenLineCap.Triangle: Include(outer); break;
                case PenLineCap.Round:
                    var k = Vector128.Create(0.5522847498307933984);
                    var first = origin - _normal;
                    var last = origin + _normal;
                    IncludeCubic(first, first + along * k, outer - _normal * k, outer);
                    IncludeCubic(outer, outer + _normal * k, last + along * k, last);
                    break;
                default: return false;
            }
            return _valid;
        }

        private static Vector128<double> Pair(Vector2 value) => Vector128.Create((double)value.X, value.Y);
        private static Vector128<double> Narrow(Vector128<double> value) =>
            Vector128.Create((double)(float)value[0], (double)(float)value[1]);

        private void Include(Vector128<double> value)
        {
            if (!double.IsFinite(value[0]) || !double.IsFinite(value[1])) { _valid = false; return; }
            _min = Vector128.Min(_min, value);
            _max = Vector128.Max(_max, value);
        }

        private void IncludeCubic(Vector128<double> a, Vector128<double> b, Vector128<double> c, Vector128<double> d)
        {
            a = Narrow(a); b = Narrow(b); c = Narrow(c); d = Narrow(d);
            Include(a); Include(d);
            var quadratic = Vector128.Create(3.0) * (-a + Vector128.Create(3.0) * b - Vector128.Create(3.0) * c + d);
            var linear = Vector128.Create(6.0) * (a - Vector128.Create(2.0) * b + c);
            var constant = Vector128.Create(3.0) * (b - a);
            for (int axis = 0; axis < 2; axis++)
            {
                double q = quadratic[axis], l = linear[axis], k = constant[axis];
                if (q == 0)
                {
                    if (l != 0) IncludeCubicAt(a, b, c, d, -k / l);
                    continue;
                }
                double discriminant = l * l - 4 * q * k;
                if (discriminant < 0) continue;
                double root = Math.Sqrt(discriminant);
                IncludeCubicAt(a, b, c, d, (-l - root) / (2 * q));
                IncludeCubicAt(a, b, c, d, (-l + root) / (2 * q));
            }
        }

        private void IncludeCubicAt(Vector128<double> a, Vector128<double> b, Vector128<double> c,
            Vector128<double> d, double t)
        {
            if (!double.IsFinite(t)) { _valid = false; return; }
            if (t <= 0 || t >= 1) return;
            double s = 1 - t;
            Include(Vector128.Create(s * s * s) * a + Vector128.Create(3 * s * s * t) * b
                + Vector128.Create(3 * s * t * t) * c + Vector128.Create(t * t * t) * d);
        }

        public bool TryGetBounds(out Vector2 min, out Vector2 max)
        {
            min = new((float)_min[0], (float)_min[1]);
            max = new((float)_max[0], (float)_max[1]);
            return _valid && float.IsFinite(min.X) && float.IsFinite(min.Y)
                && float.IsFinite(max.X) && float.IsFinite(max.Y);
        }
    }
}
