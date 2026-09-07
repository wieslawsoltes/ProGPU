using System;
using System.Numerics;
using System.Runtime.Intrinsics;
using ProGPU.Vector;

namespace ProGPU.Scene;

public static partial class StrokeCoverageGeometry
{
    /// <summary>
    /// Prepares owned solid linear stroke contours and their material bounds.
    /// Geometry-local transforms must already be applied. Keeps gaps, cyclic
    /// seams, smooth joins and endpoint overrides; does not flatten curves.
    /// The original path remains the independent fill geometry.
    /// </summary>
    public static bool TryPrepareLinearPath(PathGeometry source, Pen pen,
        out PathGeometry strokePath, out Pen coveragePen, out Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pen);
        strokePath = null!; coveragePen = null!; bounds = default;
        if (source.IsCombined || (uint)source.FillRule > 1 || !float.IsFinite(pen.Thickness) || pen.Thickness < 0
            || pen.StrokeTransformMode != PenStrokeTransformMode.Normal || pen.HasDashPattern || !double.IsFinite(pen.DashOffset)
            || (uint)pen.LineJoin > 2 || !float.IsFinite(pen.MiterLimit) || (uint)pen.StartLineCap > 3
            || (uint)pen.EndLineCap > 3 || (uint)pen.DashCap > 3) return false;
        // Validate the entire input before allocating the owned stroke snapshot.
        // One-million source records bounds output storage and traversal work.
        int records = source.Figures.Count;
        if (records > 1_000_000) return false;
        for (int f = 0; f < source.Figures.Count; f++)
        {
            var figure = source.Figures[f];
            if (figure == null || !FinitePoint(figure.StartPoint) || (uint)(figure.StrokeStartLineCap ?? pen.StartLineCap) > 3
                || (uint)(figure.StrokeEndLineCap ?? pen.EndLineCap) > 3
                || figure.Segments.Count > 1_000_000 - records) return false;
            records += figure.Segments.Count;
            for (int i = 0; i < figure.Segments.Count; i++)
                if (figure.Segments[i] is not LineSegment line || !FinitePoint(line.Point)) return false;
        }
        var prepared = new PathGeometry { FillRule = source.FillRule };
        for (int f = 0; f < source.Figures.Count; f++) AppendLinearRuns(source.Figures[f], pen, prepared);
        var state = new LineBounds();
        bool any = false;
        if (pen.Thickness > 0)
        {
            for (int f = 0; f < prepared.Figures.Count; f++)
            {
                if (!MeasureLinearRun(prepared.Figures[f], pen, ref state, out var contributed)) return false;
                any |= contributed;
            }
        }
        if (any)
        {
            if (!state.TryGetBounds(out var min, out var max)) return false;
            var extent = max - min;
            if (!float.IsFinite(extent.X) || !float.IsFinite(extent.Y)) return false;
            if (extent.X > 0 && extent.Y > 0)
            {
                var result = new Rect(min.X, min.Y, extent.X, extent.Y);
                if (!float.IsFinite(result.Right) || !float.IsFinite(result.Bottom)) return false;
                bounds = result;
            }
        }
        strokePath = prepared; coveragePen = pen;
        return true;
    }

    private static bool FinitePoint(Vector2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y);
    private static Vector128<double> Wide(Vector2 point) => Vector128.Create((double)point.X, point.Y);
    private static Vector128<double> NarrowPair(Vector128<double> point) =>
        Vector128.Create((double)(float)point[0], (double)(float)point[1]);

    // Original ProGPU native MIL parsed_stroke_edge / append_open_run contour
    // topology. The closing edge participates in stroke even for hollow fills.
    private static void AppendLinearRuns(PathFigure source, Pen pen, PathGeometry output)
    {
        int count = source.Segments.Count;
        if (count == 0) return;
        var last = ((LineSegment)source.Segments[count - 1]).Point;
        int edges = count + (source.IsClosed && last != source.StartPoint ? 1 : 0);
        int firstGap = -1;
        for (int i = 0; i < count; i++)
            if (!source.Segments[i].IsStroked) { firstGap = i; break; }
        bool closed = source.IsClosed && firstGap < 0;
        int origin = source.IsClosed && firstGap >= 0 ? (firstGap + 1) % edges : 0;
        int consumed = 0;
        while (consumed < edges)
        {
            int index = (origin + consumed) % edges;
            if (index < count && !source.Segments[index].IsStroked) { consumed++; continue; }
            int first = consumed;
            var start = index == 0 ? source.StartPoint : ((LineSegment)source.Segments[index - 1]).Point;
            var run = new PathFigure(start, closed) { IsFilled = false };
            run.StrokeStartLineCap = !source.IsClosed && index == 0
                ? source.StrokeStartLineCap ?? pen.StartLineCap : pen.DashCap;
            var current = start;
            bool pendingSmooth = false;
            while (consumed < edges)
            {
                index = (origin + consumed) % edges;
                if (index < count && !source.Segments[index].IsStroked) break;
                var line = index < count ? (LineSegment)source.Segments[index] : null;
                var end = line?.Point ?? source.StartPoint;
                // Exact constants do not change tangents or introduce a join.
                // Preserve a wholly point-like run below for its terminal caps.
                if (end != current)
                {
                    run.Segments.Add(new LineSegment(end, pendingSmooth || (line?.IsSmoothJoin ?? false)));
                    pendingSmooth = false;
                }
                else pendingSmooth |= line?.IsSmoothJoin ?? false;
                current = end;
                consumed++;
            }
            if (run.Segments.Count == 0 && consumed != first) run.Segments.Add(new LineSegment(current));
            if (closed && pendingSmooth) run.Segments[0].IsSmoothJoin = true;
            run.StrokeEndLineCap = !source.IsClosed && consumed == edges
                ? source.StrokeEndLineCap ?? pen.EndLineCap : pen.DashCap;
            output.Figures.Add(run);
        }
    }

    // Original ProGPU C++ Direct2D solid_polyline_widened_bounds and
    // append_stroke_{segment,cap,join}_bounds_points, identity outer matrix.
    // Stream support points into an intrinsic reducer instead of an O(S) array.
    private static bool MeasureLinearRun(PathFigure figure, Pen pen, ref LineBounds bounds, out bool contributed)
    {
        contributed = false;
        int count = figure.Segments.Count;
        if (count == 0) return true;
        var first = figure.StartPoint;
        var last = ((LineSegment)figure.Segments[count - 1]).Point;
        double radius = pen.Thickness * 0.5;
        if (count == 1 && first == last)
        {
            var pointBounds = new LineBounds(first, last, pen.Thickness);
            var startCap = figure.IsClosed ? PenLineCap.Round : figure.StrokeStartLineCap ?? pen.StartLineCap;
            var endCap = figure.IsClosed ? PenLineCap.Round : figure.StrokeEndLineCap ?? pen.EndLineCap;
            if (startCap == PenLineCap.Flat && endCap == PenLineCap.Flat) return true;
            if (!pointBounds.IncludeCap(first, -1, startCap) || !pointBounds.IncludeCap(first, 1, endCap)
                || !pointBounds.TryGetBounds(out var a, out var b)) return false;
            bounds.Include(Wide(a)); bounds.Include(Wide(b));
            contributed = true;
            return true;
        }
        var start = Wide(first);
        var firstDirection = Vector128<double>.Zero;
        var previousDirection = Vector128<double>.Zero;
        for (int i = 0; i < count; i++)
        {
            var segment = (LineSegment)figure.Segments[i];
            var end = Wide(segment.Point);
            var delta = end - start;
            double length = double.Hypot(delta[0], delta[1]);
            if (!double.IsFinite(length) || length == 0) return false;
            var direction = delta / Vector128.Create(length);
            var normal = LeftNormal(direction) * Vector128.Create(radius);
            bounds.Include(NarrowPair(start + normal)); bounds.Include(NarrowPair(start - normal));
            bounds.Include(NarrowPair(end + normal)); bounds.Include(NarrowPair(end - normal));
            if (i == 0) firstDirection = direction;
            else IncludeLinearJoin(ref bounds, start, previousDirection, direction, radius,
                segment.IsSmoothJoin ? PenLineJoin.Round : pen.LineJoin, pen.MiterLimit);
            previousDirection = direction;
            start = end;
        }
        if (figure.IsClosed)
            IncludeLinearJoin(ref bounds, Wide(first), previousDirection, firstDirection, radius,
                figure.Segments[0].IsSmoothJoin ? PenLineJoin.Round : pen.LineJoin, pen.MiterLimit);
        else
        {
            IncludeLinearCap(ref bounds, Wide(first), -firstDirection, radius, figure.StrokeStartLineCap ?? pen.StartLineCap);
            IncludeLinearCap(ref bounds, Wide(last), previousDirection, radius, figure.StrokeEndLineCap ?? pen.EndLineCap);
        }
        contributed = true;
        return true;
    }

    private static void IncludeLinearCap(ref LineBounds state, Vector128<double> center,
        Vector128<double> outward, double radius, PenLineCap cap)
    {
        if (cap == PenLineCap.Flat) return;
        if (cap == PenLineCap.Round) { IncludeRoundSector(ref state, center, outward, -outward, radius); return; }
        var extension = NarrowPair(center + outward * Vector128.Create(radius));
        if (cap == PenLineCap.Triangle) { state.Include(extension); return; }
        var normal = LeftNormal(outward) * Vector128.Create(radius);
        state.Include(NarrowPair(extension + normal)); state.Include(NarrowPair(extension - normal));
    }

    private static void IncludeRoundSector(ref LineBounds state, Vector128<double> center,
        Vector128<double> incoming, Vector128<double> outgoing, double radius)
    {
        state.Include(center);
        for (int axis = 0; axis < 2; axis++)
        {
            var positive = axis == 0 ? Vector128.Create(radius, 0.0) : Vector128.Create(0.0, radius);
            if (Dot(positive, incoming) >= 0 && Dot(positive, outgoing) <= 0) state.Include(NarrowPair(center + positive));
            var negative = -positive;
            if (Dot(negative, incoming) >= 0 && Dot(negative, outgoing) <= 0) state.Include(NarrowPair(center + negative));
        }
    }

    private static void IncludeLinearJoin(ref LineBounds state, Vector128<double> center,
        Vector128<double> incoming, Vector128<double> outgoing, double radius, PenLineJoin join, double limit)
    {
        if (join == PenLineJoin.Round) { IncludeRoundSector(ref state, center, incoming, outgoing, radius); return; }
        if (join == PenLineJoin.Bevel) return;
        double denominator = Cross(incoming, outgoing);
        if (Math.Abs(denominator) <= 1e-6) return;
        for (int side = -1; side <= 1; side += 2)
        {
            var a = NarrowPair(center + LeftNormal(incoming) * Vector128.Create(radius * side));
            var b = NarrowPair(center + LeftNormal(outgoing) * Vector128.Create(radius * side));
            double parameter = Cross(b - a, outgoing) / denominator;
            var miter = NarrowPair(a + incoming * Vector128.Create(parameter));
            var delta = miter - center;
            double length = double.Hypot(delta[0], delta[1]);
            if (length <= Math.Max(1, limit) * radius) { state.Include(miter); continue; }
            if (length == 0) continue;
            var bisector = delta / Vector128.Create(length);
            IncludeClippedMiter(ref state, center, a, miter, bisector, length, Math.Max(1, limit) * radius);
            IncludeClippedMiter(ref state, center, b, miter, bisector, length, Math.Max(1, limit) * radius);
        }
    }

    private static void IncludeClippedMiter(ref LineBounds state, Vector128<double> center, Vector128<double> offset,
        Vector128<double> miter, Vector128<double> bisector, double length, double clipDistance)
    {
        double projection = Dot(offset - center, bisector), divisor = length - projection;
        if (divisor <= 0) return;
        double amount = (clipDistance - projection) / divisor;
        if (double.IsFinite(amount) && amount >= 0 && amount <= 1)
            state.Include(NarrowPair(offset + (miter - offset) * Vector128.Create(amount)));
    }
}
