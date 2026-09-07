using System;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.InteropServices;
using ProGPU.Vector;

namespace ProGPU.Scene;

/// <summary>Prepares retained stroke coverage and material bounds without a device.</summary>
public static partial class StrokeCoverageGeometry
{
    /// <summary>
    /// Prepares a solid, normal-width rectangle after its geometry-local affine
    /// transform. The returned closed spine retains joins; the outer drawing
    /// transform must be applied later to the completed stroke. Dashed, fixed,
    /// hairline and collapsed rectangles require separate preparation.
    /// </summary>
    public static bool TryPrepareRectangle(Rect rectangle, Matrix3x2 geometryTransform, Pen pen,
        out PathGeometry path, out Pen coveragePen, out Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(pen);
        path = null!;
        coveragePen = null!;
        bounds = default;
        if (!float.IsFinite(rectangle.X) || !float.IsFinite(rectangle.Y)
            || !float.IsFinite(rectangle.Width) || !float.IsFinite(rectangle.Height)
            || rectangle.Width <= 0 || rectangle.Height <= 0
            || !float.IsFinite(pen.Thickness) || pen.Thickness < 0 || pen.IsFixed || pen.HasDashPattern
            || (uint)pen.LineJoin > 2 || !float.IsFinite(pen.MiterLimit)
            || !float.IsFinite(geometryTransform.M11) || !float.IsFinite(geometryTransform.M12)
            || !float.IsFinite(geometryTransform.M21) || !float.IsFinite(geometryTransform.M22)
            || !float.IsFinite(geometryTransform.M31) || !float.IsFinite(geometryTransform.M32)) return false;

        // Original ProGPU native MIL try_transformed_rectangle_stroke_bounds.
        // Fixed four-corner storage; coordinate mapping, offsets and extrema use
        // intrinsic pairs. Normalize the retained float spine before measuring it.
        Span<Vector2> points = stackalloc Vector2[4];
        var xBasis = Vector128.Create((double)geometryTransform.M11, geometryTransform.M12);
        var yBasis = Vector128.Create((double)geometryTransform.M21, geometryTransform.M22);
        var translation = Vector128.Create((double)geometryTransform.M31, geometryTransform.M32);
        for (int i = 0; i < 4; i++)
        {
            double x = rectangle.X + (i is 1 or 2 ? (double)rectangle.Width : 0);
            double y = rectangle.Y + (i >= 2 ? (double)rectangle.Height : 0);
            var mapped = Vector128.Create(x) * xBasis + Vector128.Create(y) * yBasis + translation;
            points[i] = new((float)mapped[0], (float)mapped[1]);
            if (!float.IsFinite(points[i].X) || !float.IsFinite(points[i].Y)) return false;
        }
        return TryPrepareConvexQuadrilateral(points, pen, out path, out coveragePen, out bounds);
    }

    /// <summary>
    /// Prepares an already transformed, strictly convex four-corner closed spine.
    /// Accepts exactly four finite vertices in perimeter order; no borrowed span
    /// survives the call. Stroke width is applied after the vertex transform.
    /// </summary>
    public static bool TryPrepareConvexQuadrilateral(ReadOnlySpan<Vector2> points, Pen pen,
        out PathGeometry path, out Pen coveragePen, out Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(pen);
        path = null!;
        coveragePen = null!;
        bounds = default;
        if (points.Length != 4 || !float.IsFinite(pen.Thickness) || pen.Thickness < 0
            || pen.IsFixed || pen.HasDashPattern || (uint)pen.LineJoin > 2 || !float.IsFinite(pen.MiterLimit)) return false;
        Span<Vector128<double>> vertices = stackalloc Vector128<double>[4];
        Span<Vector128<double>> directions = stackalloc Vector128<double>[4];
        for (int i = 0; i < 4; i++)
        {
            if (!float.IsFinite(points[i].X) || !float.IsFinite(points[i].Y)) return false;
            vertices[i] = Vector128.Create((double)points[i].X, points[i].Y);
        }
        for (int i = 0; i < 4; i++)
        {
            var delta = vertices[(i + 1) & 3] - vertices[i];
            double length = double.Hypot(delta[0], delta[1]);
            if (!(length > 0) || !double.IsFinite(length)) return false;
            directions[i] = delta / Vector128.Create(length);
        }
        double orientation = Cross(directions[3], directions[0]);
        if (!double.IsFinite(orientation) || Math.Abs(orientation) <= 0.0001) return false;
        // Validate every corner even for zero-width strokes. A concave, crossed
        // or collapsed input is not a successfully prepared quadrilateral.
        for (int i = 1; i < 4; i++)
        {
            double turn = Cross(directions[i - 1], directions[i]);
            if (!double.IsFinite(turn) || Math.Abs(turn) <= 0.0001 || turn * orientation <= 0) return false;
        }
        if (pen.Thickness == 0)
        {
            path = RenderCommandGeometryCache.CreatePolylinePath(points, isClosed: true);
            coveragePen = pen;
            return true;
        }
        var state = new LineBounds();
        double radius = pen.Thickness * 0.5;
        for (int i = 0; i < 4; i++)
        {
            var normal = LeftNormal(directions[i]) * Vector128.Create(radius);
            state.Include(vertices[i] - normal); state.Include(vertices[i] + normal);
            state.Include(vertices[(i + 1) & 3] - normal); state.Include(vertices[(i + 1) & 3] + normal);
            if (!IncludeRectangleJoin(ref state, vertices[i], directions[(i + 3) & 3], directions[i],
                    radius, pen.LineJoin, pen.MiterLimit, orientation)) return false;
        }
        if (!state.TryGetBounds(out var minimum, out var maximum)) return false;
        var extent = maximum - minimum;
        if (!float.IsFinite(extent.X) || !float.IsFinite(extent.Y) || extent.X <= 0 || extent.Y <= 0) return false;
        var result = new Rect(minimum.X, minimum.Y, extent.X, extent.Y);
        if (!float.IsFinite(result.Right) || !float.IsFinite(result.Bottom)) return false;
        path = RenderCommandGeometryCache.CreatePolylinePath(points, isClosed: true);
        coveragePen = pen;
        bounds = result;
        return true;
    }

    private static Vector128<double> LeftNormal(Vector128<double> value) => Vector128.Create(-value[1], value[0]);
    private static double Cross(Vector128<double> left, Vector128<double> right) => left[0] * right[1] - left[1] * right[0];
    private static double Dot(Vector128<double> left, Vector128<double> right) => Vector128.Sum(left * right);

    private static bool IncludeRectangleJoin(ref LineBounds state, Vector128<double> point,
        Vector128<double> incoming, Vector128<double> outgoing, double radius,
        PenLineJoin join, float miterLimit, double orientation)
    {
        double turn = Cross(incoming, outgoing);
        // Float narrowing can collapse or reverse a nearly singular corner.
        if (!double.IsFinite(turn) || Math.Abs(turn) <= 0.0001 || turn * orientation <= 0) return false;
        if (join == PenLineJoin.Bevel) return true; // Edge endpoints already contain the bevel extrema.
        double sign = turn > 0 ? -1 : 1;
        var previous = point + LeftNormal(incoming) * Vector128.Create(sign * radius);
        var next = point + LeftNormal(outgoing) * Vector128.Create(sign * radius);
        double dot = Dot(incoming, outgoing);
        if (join == PenLineJoin.Miter)
        {
            double limit = Math.Max(1, miterLimit);
            var miter = previous + incoming * Vector128.Create(Cross(next - previous, outgoing) / turn);
            var delta = miter - point;
            if (double.Hypot(delta[0], delta[1]) <= radius * limit + 0.0001)
                state.Include(miter);
            else
            {
                double denominator = radius * Math.Sqrt(Math.Max(0, (1 - dot) * 0.5));
                if (!double.IsFinite(denominator) || denominator <= 0.0001) return false;
                double numerator = radius * Math.Sqrt(Math.Max(0, (1 + dot) * 0.5));
                var distance = Vector128.Create(radius * Math.Max(0, (limit * radius - numerator) / denominator));
                state.Include(previous + incoming * distance);
                state.Include(next - outgoing * distance);
            }
            return true;
        }
        const double tolerance = 0.25;
        double threshold = radius < tolerance ? -2 : 2 * (1 - tolerance / radius) * (1 - tolerance / radius) - 1;
        if (dot > threshold) return true;
        if (dot >= 0)
        {
            var distance = Vector128.Create(radius * RoundJoinBezierDistance(radius, dot));
            state.IncludeCubic(previous, previous + incoming * distance, next - outgoing * distance, next, narrow: false);
            return true;
        }
        var tangentSum = incoming + outgoing;
        var radialSum = previous + next - Vector128.Create(2.0) * point;
        double tangentLength = double.Hypot(tangentSum[0], tangentSum[1]);
        double radialLength = double.Hypot(radialSum[0], radialSum[1]);
        if (!(tangentLength > 0.0001 && radialLength > 0.0001)) return false;
        var tangent = tangentSum / Vector128.Create(tangentLength);
        var middle = point + radialSum * Vector128.Create(radius / radialLength);
        var halfDistance = Vector128.Create(radius * RoundJoinBezierDistance(radius, Math.Abs(Dot(outgoing, tangent))));
        state.IncludeCubic(previous, previous + incoming * halfDistance, middle - tangent * halfDistance, middle, narrow: false);
        state.IncludeCubic(middle, middle + tangent * halfDistance, next - outgoing * halfDistance, next, narrow: false);
        return true;
    }

    private static double RoundJoinBezierDistance(double radius, double directionDot)
    {
        double squared = radius * radius;
        double a = Math.Max(0, 0.5 * (squared + directionDot * squared));
        if (squared - a <= 0) return 0;
        double denominator = Math.Sqrt(squared - a);
        double numerator = (4.0 / 3.0) * (radius - Math.Sqrt(a));
        return numerator <= denominator * 0.000001 ? 0 : numerator / denominator;
    }

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

        public LineBounds()
        {
            _min = Vector128.Create(double.PositiveInfinity);
            _max = Vector128.Create(double.NegativeInfinity);
            _normal = _along = Vector128<double>.Zero;
            _valid = true;
        }

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

        public void Include(Vector128<double> value)
        {
            if (!double.IsFinite(value[0]) || !double.IsFinite(value[1])) { _valid = false; return; }
            _min = Vector128.Min(_min, value);
            _max = Vector128.Max(_max, value);
        }

        public void IncludeCubic(Vector128<double> a, Vector128<double> b, Vector128<double> c, Vector128<double> d, bool narrow = true)
        {
            if (narrow) { a = Narrow(a); b = Narrow(b); c = Narrow(c); d = Narrow(d); }
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
