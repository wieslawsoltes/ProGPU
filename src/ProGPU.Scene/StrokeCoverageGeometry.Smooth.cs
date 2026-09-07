using System;
using System.Numerics;
using System.Runtime.Intrinsics;
using ProGPU.Vector;

namespace ProGPU.Scene;

public static partial class StrokeCoverageGeometry
{
    /// <summary>Prepares a solid normal-width ellipse, mapping its spine before widening.</summary>
    public static bool TryPrepareEllipse(Vector2 center, float radiusX, float radiusY,
        Matrix3x2 geometryTransform, Pen pen, out PathGeometry path, out Pen coveragePen, out Rect bounds)
    {
        path = null!; coveragePen = null!; bounds = default;
        if (!CanPrepareSmooth(pen, geometryTransform) || !float.IsFinite(center.X) || !float.IsFinite(center.Y)
            || !float.IsFinite(radiusX) || !float.IsFinite(radiusY) || radiusX <= 0 || radiusY <= 0) return false;
        // Original ProGPU native MIL try_transformed_ellipse_stroke_bounds:
        // preserve the source float rounding before double affine preparation.
        const double k = 0.5522847498307933984;
        float mx = (float)(radiusX * k), my = (float)(radiusY * k);
        float left = center.X - radiusX, right = center.X + radiusX;
        float top = center.Y - radiusY, bottom = center.Y + radiusY;
        Span<SmoothCubic> curves = stackalloc SmoothCubic[4];
        curves[0] = new(new(right, center.Y), new(right, center.Y + my), new(center.X + mx, bottom), new(center.X, bottom));
        curves[1] = new(new(center.X, bottom), new(center.X - mx, bottom), new(left, center.Y + my), new(left, center.Y));
        curves[2] = new(new(left, center.Y), new(left, center.Y - my), new(center.X - mx, top), new(center.X, top));
        curves[3] = new(new(center.X, top), new(center.X + mx, top), new(right, center.Y - my), new(right, center.Y));
        if (!TrySmoothBounds(curves, geometryTransform, pen.Thickness, out var ink)) return false;
        var result = PrimitivePathGeometry.CreateEllipse(center, radiusX, radiusY);
        if (result.Figures.Count == 0) return false;
        if (!geometryTransform.IsIdentity) result = result.CreateTransformed(new Matrix4x4(geometryTransform));
        if (!HasFinitePathBounds(result)) return false;
        path = result; coveragePen = pen; bounds = ink;
        return true;
    }

    /// <summary>Prepares a solid normal-width rounded rectangle; zero corner axes use sharp joins.</summary>
    public static bool TryPrepareRoundedRectangle(Rect rectangle, float radiusX, float radiusY,
        Matrix3x2 geometryTransform, Pen pen, out PathGeometry path, out Pen coveragePen, out Rect bounds)
    {
        path = null!; coveragePen = null!; bounds = default;
        if (!CanPrepareSmooth(pen, geometryTransform) || !float.IsFinite(rectangle.X) || !float.IsFinite(rectangle.Y)
            || !float.IsFinite(rectangle.Width) || !float.IsFinite(rectangle.Height)
            || rectangle.Width <= 0 || rectangle.Height <= 0 || !float.IsFinite(radiusX) || !float.IsFinite(radiusY)
            || radiusX < 0 || radiusY < 0) return false;
        if (radiusX == 0 || radiusY == 0)
            return TryPrepareRectangle(rectangle, geometryTransform, pen, out path, out coveragePen, out bounds);
        // Original native MIL try_transformed_rounded_rectangle_stroke_bounds.
        double l = rectangle.X, t = rectangle.Y;
        double r = l + rectangle.Width, b = t + rectangle.Height;
        double rx = Math.Min(radiusX, rectangle.Width * 0.5), ry = Math.Min(radiusY, rectangle.Height * 0.5);
        const double k = 0.5522847498307933984;
        double bx = (1 - k) * rx, by = (1 - k) * ry;
        var p0 = Pair(l, t + ry); var p1 = Pair(l + rx, t);
        var p2 = Pair(r - rx, t); var p3 = Pair(r, t + ry);
        var p4 = Pair(r, b - ry); var p5 = Pair(r - rx, b);
        var p6 = Pair(l + rx, b); var p7 = Pair(l, b - ry);
        Span<SmoothCubic> curves = stackalloc SmoothCubic[8];
        curves[0] = new(p0, Pair(l, t + by), Pair(l + bx, t), p1);
        curves[1] = SmoothCubic.Line(p1, p2);
        curves[2] = new(p2, Pair(r - bx, t), Pair(r, t + by), p3);
        curves[3] = SmoothCubic.Line(p3, p4);
        curves[4] = new(p4, Pair(r, b - by), Pair(r - bx, b), p5);
        curves[5] = SmoothCubic.Line(p5, p6);
        curves[6] = new(p6, Pair(l + bx, b), Pair(l, b - by), p7);
        curves[7] = SmoothCubic.Line(p7, p0);
        if (!TrySmoothBounds(curves, geometryTransform, pen.Thickness, out var ink)) return false;
        var result = PrimitivePathGeometry.CreateRoundedRectangle(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height,
            (float)rx, (float)ry);
        if (result.Figures.Count == 0) return false;
        if (!geometryTransform.IsIdentity) result = result.CreateTransformed(new Matrix4x4(geometryTransform));
        if (!HasFinitePathBounds(result)) return false;
        path = result; coveragePen = pen; bounds = ink;
        return true;
    }

    private static bool CanPrepareSmooth(Pen pen, Matrix3x2 matrix)
    {
        ArgumentNullException.ThrowIfNull(pen);
        return float.IsFinite(pen.Thickness) && pen.Thickness >= 0 && !pen.IsFixed && !pen.HasDashPattern
            && (uint)pen.LineJoin <= 2 && float.IsFinite(pen.MiterLimit)
            && float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12)
            && float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22)
            && float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32)
            && (double)matrix.M11 * matrix.M22 - (double)matrix.M12 * matrix.M21 != 0;
    }

    private static bool HasFinitePathBounds(PathGeometry path) => path.TryGetBounds(out var min, out var max)
        && float.IsFinite(min.X) && float.IsFinite(min.Y) && float.IsFinite(max.X) && float.IsFinite(max.Y);

    private static Vector128<double> Pair(double x, double y) => Vector128.Create(x, y);

    private readonly struct SmoothCubic
    {
        public readonly Vector128<double> A, B, C, D;
        public readonly bool IsLine;
        public SmoothCubic(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
            : this(Pair(a.X, a.Y), Pair(b.X, b.Y), Pair(c.X, c.Y), Pair(d.X, d.Y)) { }
        public SmoothCubic(Vector128<double> a, Vector128<double> b, Vector128<double> c, Vector128<double> d, bool line = false)
        { A = a; B = b; C = c; D = d; IsLine = line; }
        public static SmoothCubic Line(Vector128<double> a, Vector128<double> b) => new(a, a, b, b, true);
    }

    // Original native MIL try_transformed_cubic_contour_stroke_bounds with
    // identity post-widen world matrix. Adaptive decisions are sequential;
    // all independent coordinate arithmetic uses paired intrinsic lanes.
    private static bool TrySmoothBounds(ReadOnlySpan<SmoothCubic> curves, Matrix3x2 matrix, float width, out Rect bounds)
    {
        bounds = default;
        if (width == 0) return true;
        var state = new SmoothBounds(width * 0.5);
        var x = Pair(matrix.M11, matrix.M12);
        var y = Pair(matrix.M21, matrix.M22);
        var translation = Pair(matrix.M31, matrix.M32);
        Vector128<double> Map(Vector128<double> p) => Vector128.Create(p[0]) * x + Vector128.Create(p[1]) * y + translation;
        for (int i = 0; i < curves.Length; i++)
        {
            var source = curves[i];
            var a = Map(source.A); var b = Map(source.B); var c = Map(source.C); var d = Map(source.D);
            if (!Finite(a) || !Finite(b) || !Finite(c) || !Finite(d)) return false;
            if (source.IsLine)
            {
                // Half-extent corner clamping creates exact zero-length straight
                // spans. They do not interrupt the adjacent smooth arcs.
                if (a == d) continue;
                if (!state.Line(a, d)) return false;
                continue;
            }
            if (!state.Cubic(a, b, c, d)) return false;
        }
        if (!state.Bounds.TryGetBounds(out var min, out var max)) return false;
        var extent = max - min;
        if (!float.IsFinite(extent.X) || !float.IsFinite(extent.Y) || extent.X <= 0 || extent.Y <= 0) return false;
        var result = new Rect(min.X, min.Y, extent.X, extent.Y);
        if (!float.IsFinite(result.Right) || !float.IsFinite(result.Bottom)) return false;
        bounds = result;
        return true;
    }

    private static bool Finite(Vector128<double> value) => double.IsFinite(value[0]) && double.IsFinite(value[1]);
    private static double Norm(Vector128<double> value) => Math.Max(Math.Abs(value[0]), Math.Abs(value[1]));

    private struct SmoothBounds
    {
        public LineBounds Bounds;
        private readonly double _radius, _threshold;
        private Vector128<double> _previousPosition, _previousDirection;
        private bool _hasPrevious;
        public SmoothBounds(double radius)
        {
            Bounds = new LineBounds(); _radius = radius;
            _threshold = radius < 0.25 ? -2 : 2 * (1 - 0.25 / radius) * (1 - 0.25 / radius) - 1;
            _previousPosition = _previousDirection = default; _hasPrevious = false;
        }
        private void OffsetPair(Vector128<double> position, Vector128<double> direction)
        {
            var offset = LeftNormal(direction) * Vector128.Create(_radius);
            Bounds.Include(position - offset); Bounds.Include(position + offset);
        }
        private bool RoundTo(Vector128<double> center, Vector128<double> incoming, Vector128<double> outgoing)
        {
            double turn = Cross(incoming, outgoing), dot = Dot(incoming, outgoing);
            if (!double.IsFinite(turn) || !double.IsFinite(dot) || Math.Abs(turn) <= 0.0001) return dot > 0;
            if (dot > _threshold)
            {
                Bounds.Include(center + LeftNormal(outgoing) * Vector128.Create((turn > 0 ? -1 : 1) * _radius));
                return true;
            }
            return IncludeRectangleJoin(ref Bounds, center, incoming, outgoing, _radius, PenLineJoin.Round, 1, turn);
        }
        private bool CurvePoint(Vector128<double> position, Vector128<double> tangent, bool last)
        {
            double length = double.Hypot(tangent[0], tangent[1]);
            if (!double.IsFinite(length) || length <= 0.000001) return false;
            var direction = tangent / Vector128.Create(length);
            if (!_hasPrevious) OffsetPair(position, direction);
            else
            {
                double dot = Dot(_previousDirection, direction);
                if (!double.IsFinite(dot)) return false;
                if (dot < _threshold)
                {
                    var chord = position - _previousPosition;
                    double chordLength = double.Hypot(chord[0], chord[1]);
                    if (!double.IsFinite(chordLength) || chordLength <= 0.000001) return false;
                    var chordDirection = chord / Vector128.Create(chordLength);
                    if (!RoundTo(_previousPosition, _previousDirection, chordDirection)) return false;
                    OffsetPair(position, chordDirection);
                    if (!RoundTo(position, chordDirection, direction)) return false;
                    if (last) OffsetPair(position, direction);
                }
                else OffsetPair(position, direction);
            }
            _previousPosition = position; _previousDirection = direction; _hasPrevious = true;
            return true;
        }
        public bool Line(Vector128<double> start, Vector128<double> end)
        {
            var tangent = end - start;
            double length = double.Hypot(tangent[0], tangent[1]);
            if (!double.IsFinite(length) || length <= 0.000001) return false;
            if (!_hasPrevious)
            {
                _previousPosition = start; _previousDirection = tangent / Vector128.Create(length); _hasPrevious = true;
                OffsetPair(start, _previousDirection);
            }
            OffsetPair(end, _previousDirection); _previousPosition = end;
            return true;
        }
        public bool Cubic(Vector128<double> a, Vector128<double> b, Vector128<double> c, Vector128<double> d)
        {
            const double tolerance = 1.5, quarterTolerance = 0.375, twiceMinimumStep = 0.001;
            var e0 = a; var e1 = d - a;
            var e2 = Vector128.Create(6.0) * (b - Vector128.Create(2.0) * c + d);
            var e3 = Vector128.Create(6.0) * (a - Vector128.Create(2.0) * b + c);
            int count = 1; double step = 1;
            // Identity world means the flatness differences equal the local
            // differences; no duplicate set of coordinate accumulators needed.
            while ((Norm(e2) > tolerance || Norm(e3) > tolerance) && step > twiceMinimumStep)
                Halve(ref e1, ref e2, ref e3, ref count, ref step);
            if (!CurvePoint(a, b - a, false)) return false;
            while (count > 1)
            {
                e0 += e1;
                var previous = e2;
                e1 += previous; e2 = Vector128.Create(2.0) * e2 - e3; e3 = previous;
                if (!CurvePoint(e0, Vector128.Create(6.0) * e1 - e2 - Vector128.Create(2.0) * e3, false)) return false;
                count--;
                if (Norm(e2) > tolerance && step > twiceMinimumStep)
                { Halve(ref e1, ref e2, ref e3, ref count, ref step); continue; }
                while ((count & 1) == 0)
                {
                    var candidate = Vector128.Create(2.0) * e2 - e3;
                    if (Norm(e3) > quarterTolerance || Norm(candidate) > quarterTolerance) break;
                    e1 = Vector128.Create(2.0) * e1 + e2;
                    e3 *= Vector128.Create(4.0); e2 = Vector128.Create(4.0) * candidate;
                    count /= 2; step *= 2;
                }
            }
            return CurvePoint(d, d - c, true);
        }
        private static void Halve(ref Vector128<double> e1, ref Vector128<double> e2, ref Vector128<double> e3,
            ref int count, ref double step)
        {
            e2 = (e2 + e3) * Vector128.Create(0.125);
            e1 = (e1 - e2) * Vector128.Create(0.5);
            e3 *= Vector128.Create(0.25); count *= 2; step *= 0.5;
        }
    }
}
