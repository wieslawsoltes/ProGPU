using System;
using System.Numerics;

#nullable enable

namespace ProGPU.Vector;

/// <summary>
/// Expands flattened retained paths into filled stroke geometry and performs
/// matching outline hit tests without depending on a renderer backend.
/// </summary>
public static class StrokePathGeometry
{
    private const float Epsilon = 0.0001f;

    /// <summary>
    /// Creates a nonzero-filled triangle path for the supplied stroke.
    /// </summary>
    /// <remarks>
    /// The source must contain line segments only. Curves should be flattened by
    /// the owning API so that its public flatness contract remains authoritative.
    /// </remarks>
    public static bool TryCreateWidenedPath(PathGeometry source, Pen pen, out PathGeometry widenedPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pen);

        widenedPath = new PathGeometry { FillRule = FillRule.Nonzero };
        var sink = new GeometrySink(widenedPath);
        return TryEmitStroke(source, pen, ref sink);
    }

    /// <summary>
    /// Tests a point against the same retained stroke geometry used by widening.
    /// </summary>
    public static bool TryContains(PathGeometry source, Pen pen, Vector2 point, out bool contains)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pen);

        contains = false;
        if (!IsFinite(point))
        {
            return false;
        }

        var sink = new HitTestSink(point);
        bool success = TryEmitStroke(source, pen, ref sink);
        contains = sink.Contains;
        return success;
    }

    private static bool TryEmitStroke<TSink>(PathGeometry source, Pen pen, ref TSink sink)
        where TSink : struct, ITriangleSink
    {
        float thickness = pen.IsHairline ? 1f : pen.Thickness;
        if (source.IsCombined || !float.IsFinite(thickness) || thickness <= Epsilon)
        {
            return false;
        }

        double[]? dashArray = pen.DashArrayStorage;
        bool dashed = dashArray is { Length: > 0 };
        DashPattern dashPattern = default;
        if (dashed && !DashPattern.TryCreate(dashArray, pen.DashOffset, thickness, out dashPattern))
        {
            return false;
        }

        foreach (PathFigure figure in source.Figures)
        {
            if (!TryValidateLineFigure(figure))
            {
                return false;
            }

            if (dashed)
            {
                EmitDashedFigure(figure, pen, thickness, dashPattern, ref sink);
            }
            else
            {
                EmitSolidFigure(figure, pen, thickness, ref sink);
            }

            if (sink.IsComplete)
            {
                return true;
            }
        }

        return true;
    }

    private static bool TryValidateLineFigure(PathFigure figure)
    {
        if (!IsFinite(figure.StartPoint))
        {
            return false;
        }

        foreach (PathSegment segment in figure.Segments)
        {
            if (segment is not LineSegment line || !IsFinite(line.Point))
            {
                return false;
            }
        }

        return true;
    }

    private static void EmitSolidFigure<TSink>(PathFigure figure, Pen pen, float thickness, ref TSink sink)
        where TSink : struct, ITriangleSink
    {
        StrokeRun run = default;
        Vector2 current = figure.StartPoint;
        foreach (PathSegment segment in figure.Segments)
        {
            var line = (LineSegment)segment;
            if (line.IsStroked)
            {
                AppendRunSegment(ref run, current, line.Point, pen.StartLineCap, suppressStartCap: false, pen, thickness, ref sink);
            }
            else
            {
                FinishRun(ref run, pen.EndLineCap, thickness, ref sink);
            }

            current = line.Point;
            if (sink.IsComplete)
            {
                return;
            }
        }

        if (figure.IsClosed)
        {
            AppendRunSegment(ref run, current, figure.StartPoint, pen.StartLineCap, suppressStartCap: true, pen, thickness, ref sink);
            FinishClosedRun(ref run, pen, thickness, ref sink);
        }
        else
        {
            FinishRun(ref run, pen.EndLineCap, thickness, ref sink);
        }
    }

    private static void EmitDashedFigure<TSink>(
        PathFigure figure,
        Pen pen,
        float thickness,
        DashPattern pattern,
        ref TSink sink)
        where TSink : struct, ITriangleSink
    {
        ReadOnlySpan<float> intervals = pattern.Intervals;
        int patternIndex = pattern.InitialIndex;
        float distanceInPattern = pattern.InitialDistance;
        StrokeRun run = default;
        DelayedStartCap delayedStart = default;
        Vector2 current = figure.StartPoint;
        bool atFigureStart = true;

        foreach (PathSegment segment in figure.Segments)
        {
            var line = (LineSegment)segment;
            if (line.IsStroked)
            {
                EmitDashedLine(
                    current,
                    line.Point,
                    figure.IsClosed,
                    atFigureStart,
                    intervals,
                    ref patternIndex,
                    ref distanceInPattern,
                    ref run,
                    ref delayedStart,
                    pen,
                    thickness,
                    ref sink);
            }
            else
            {
                FinishRun(ref run, pen.DashCap, thickness, ref sink, ref delayedStart);
                patternIndex = pattern.InitialIndex;
                distanceInPattern = pattern.InitialDistance;
            }

            current = line.Point;
            atFigureStart = false;
            if (sink.IsComplete)
            {
                return;
            }
        }

        if (figure.IsClosed && Vector2.DistanceSquared(current, figure.StartPoint) > Epsilon * Epsilon)
        {
            EmitDashedLine(
                current,
                figure.StartPoint,
                closedFigure: true,
                atFigureStart: false,
                intervals,
                ref patternIndex,
                ref distanceInPattern,
                ref run,
                ref delayedStart,
                pen,
                thickness,
                ref sink);
        }

        if (figure.IsClosed)
        {
            if (run.Active && run.SuppressStartCap)
            {
                FinishClosedRun(ref run, pen, thickness, ref sink);
            }
            else if (run.Active && delayedStart.HasValue &&
                Vector2.DistanceSquared(run.Current, figure.StartPoint) <= Epsilon * Epsilon)
            {
                EmitRunStartCap(run, thickness, ref sink);
                EmitJoin(run.Previous, run.Current, delayedStart.Next, pen, thickness, ref sink);
                run = default;
                delayedStart = default;
            }
            else
            {
                FinishRun(ref run, pen.DashCap, thickness, ref sink);
                EmitDelayedStartCap(ref delayedStart, thickness, ref sink);
            }
        }
        else
        {
            FinishRun(ref run, pen.EndLineCap, thickness, ref sink);
        }
    }

    private static void EmitDashedLine<TSink>(
        Vector2 start,
        Vector2 end,
        bool closedFigure,
        bool atFigureStart,
        ReadOnlySpan<float> intervals,
        ref int patternIndex,
        ref float distanceInPattern,
        ref StrokeRun run,
        ref DelayedStartCap delayedStart,
        Pen pen,
        float thickness,
        ref TSink sink)
        where TSink : struct, ITriangleSink
    {
        Vector2 delta = end - start;
        float length = delta.Length();
        if (!float.IsFinite(length) || length <= Epsilon)
        {
            return;
        }

        DashPattern.NormalizeState(intervals, ref patternIndex, ref distanceInPattern);
        if (run.Active && (patternIndex & 1) != 0)
        {
            FinishRun(ref run, pen.DashCap, thickness, ref sink, ref delayedStart);
        }

        Vector2 direction = delta / length;
        float distance = 0f;
        while (distance < length - Epsilon && !sink.IsComplete)
        {
            float remaining = intervals[patternIndex] - distanceInPattern;
            float step = MathF.Min(remaining, length - distance);
            bool isDrawn = (patternIndex & 1) == 0;
            if (isDrawn && step > Epsilon)
            {
                Vector2 dashStart = start + (direction * distance);
                Vector2 dashEnd = start + (direction * (distance + step));
                bool startsAtFigureStart = atFigureStart && distance <= Epsilon;
                AppendRunSegment(
                    ref run,
                    dashStart,
                    dashEnd,
                    startsAtFigureStart ? pen.StartLineCap : pen.DashCap,
                    suppressStartCap: closedFigure && startsAtFigureStart,
                    pen,
                    thickness,
                    ref sink);
            }

            bool completesInterval = step >= remaining - Epsilon;
            DashPattern.Advance(intervals, ref patternIndex, ref distanceInPattern, remaining, step);
            distance += step;
            if (isDrawn && completesInterval && (patternIndex & 1) != 0 && distance < length - Epsilon)
            {
                FinishRun(ref run, pen.DashCap, thickness, ref sink, ref delayedStart);
            }
        }
    }

    private static void AppendRunSegment<TSink>(
        ref StrokeRun run,
        Vector2 start,
        Vector2 end,
        PenLineCap startCap,
        bool suppressStartCap,
        Pen pen,
        float thickness,
        ref TSink sink)
        where TSink : struct, ITriangleSink
    {
        if (Vector2.DistanceSquared(start, end) <= Epsilon * Epsilon)
        {
            return;
        }

        if (!run.Active || Vector2.DistanceSquared(run.Current, start) > Epsilon * Epsilon)
        {
            if (run.Active)
            {
                FinishRun(ref run, pen.DashCap, thickness, ref sink);
            }

            run = new StrokeRun(start, end, startCap, suppressStartCap);
        }
        else
        {
            EmitJoin(run.Previous, run.Current, end, pen, thickness, ref sink);
            run.Previous = run.Current;
            run.Current = end;
        }

        EmitSegmentBody(start, end, thickness, ref sink);
    }

    private static void FinishRun<TSink>(
        ref StrokeRun run,
        PenLineCap endCap,
        float thickness,
        ref TSink sink)
        where TSink : struct, ITriangleSink
    {
        DelayedStartCap ignored = default;
        FinishRun(ref run, endCap, thickness, ref sink, ref ignored);
    }

    private static void FinishRun<TSink>(
        ref StrokeRun run,
        PenLineCap endCap,
        float thickness,
        ref TSink sink,
        ref DelayedStartCap delayedStart)
        where TSink : struct, ITriangleSink
    {
        if (!run.Active)
        {
            return;
        }

        if (run.SuppressStartCap)
        {
            delayedStart = new DelayedStartCap(run.Start, run.FirstNext, run.StartCap);
        }
        else
        {
            EmitRunStartCap(run, thickness, ref sink);
        }

        EmitCap(endCap, run.Previous, run.Current, isStart: false, thickness, ref sink);
        run = default;
    }

    private static void FinishClosedRun<TSink>(ref StrokeRun run, Pen pen, float thickness, ref TSink sink)
        where TSink : struct, ITriangleSink
    {
        if (!run.Active)
        {
            return;
        }

        if (Vector2.DistanceSquared(run.Current, run.Start) <= Epsilon * Epsilon)
        {
            EmitJoin(run.Previous, run.Current, run.FirstNext, pen, thickness, ref sink);
        }
        else
        {
            EmitRunStartCap(run, thickness, ref sink);
            EmitCap(pen.EndLineCap, run.Previous, run.Current, isStart: false, thickness, ref sink);
        }

        run = default;
    }

    private static void EmitRunStartCap<TSink>(StrokeRun run, float thickness, ref TSink sink)
        where TSink : struct, ITriangleSink
        => EmitCap(run.StartCap, run.Start, run.FirstNext, isStart: true, thickness, ref sink);

    private static void EmitDelayedStartCap<TSink>(
        ref DelayedStartCap delayedStart,
        float thickness,
        ref TSink sink)
        where TSink : struct, ITriangleSink
    {
        if (delayedStart.HasValue)
        {
            EmitCap(delayedStart.Cap, delayedStart.Start, delayedStart.Next, isStart: true, thickness, ref sink);
            delayedStart = default;
        }
    }

    private static void EmitSegmentBody<TSink>(Vector2 start, Vector2 end, float thickness, ref TSink sink)
        where TSink : struct, ITriangleSink
    {
        Vector2 delta = end - start;
        float length = delta.Length();
        if (!float.IsFinite(length) || length <= Epsilon)
        {
            return;
        }

        Vector2 normal = new(-delta.Y / length, delta.X / length);
        normal *= thickness * 0.5f;
        Vector2 p0 = start + normal;
        Vector2 p1 = start - normal;
        Vector2 p2 = end - normal;
        Vector2 p3 = end + normal;
        sink.AddQuad(p0, p1, p2, p3);
    }

    private static void EmitJoin<TSink>(
        Vector2 previous,
        Vector2 join,
        Vector2 next,
        Pen pen,
        float thickness,
        ref TSink sink)
        where TSink : struct, ITriangleSink
    {
        Span<StrokeJoinTriangle> triangles = stackalloc StrokeJoinTriangle[StrokeJoinGeometry.MaxTrianglesPerJoin];
        int count = StrokeJoinGeometry.WriteLineJoin(
            triangles,
            pen.LineJoin,
            thickness,
            pen.MiterLimit,
            previous,
            join,
            next);
        for (int index = 0; index < count && !sink.IsComplete; index++)
        {
            StrokeJoinTriangle triangle = triangles[index];
            sink.AddTriangle(triangle.P0, triangle.P1, triangle.P2);
        }
    }

    private static void EmitCap<TSink>(
        PenLineCap cap,
        Vector2 start,
        Vector2 end,
        bool isStart,
        float thickness,
        ref TSink sink)
        where TSink : struct, ITriangleSink
    {
        Span<StrokeJoinTriangle> triangles = stackalloc StrokeJoinTriangle[StrokeCapGeometry.MaxTrianglesPerCap];
        int count = StrokeCapGeometry.WriteLineCap(triangles, cap, thickness, start, end, isStart);
        for (int index = 0; index < count && !sink.IsComplete; index++)
        {
            StrokeJoinTriangle triangle = triangles[index];
            sink.AddTriangle(triangle.P0, triangle.P1, triangle.P2);
        }
    }

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static float Cross(Vector2 a, Vector2 b, Vector2 c) =>
        ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private interface ITriangleSink
    {
        bool IsComplete { get; }
        void AddTriangle(Vector2 p0, Vector2 p1, Vector2 p2);
        void AddQuad(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3);
    }

    private readonly struct GeometrySink : ITriangleSink
    {
        private readonly PathGeometry _path;

        public GeometrySink(PathGeometry path) => _path = path;

        public bool IsComplete => false;

        public void AddTriangle(Vector2 p0, Vector2 p1, Vector2 p2)
        {
            float area = Cross(p0, p1, p2);
            if (!float.IsFinite(area) || MathF.Abs(area) <= Epsilon)
            {
                return;
            }

            if (area < 0f)
            {
                (p1, p2) = (p2, p1);
            }

            var figure = new PathFigure(p0, isClosed: true) { IsFilled = true };
            figure.Segments.Add(new LineSegment(p1));
            figure.Segments.Add(new LineSegment(p2));
            _path.Figures.Add(figure);
        }

        public void AddQuad(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            float area = Cross(p0, p1, p2) + Cross(p0, p2, p3);
            if (!float.IsFinite(area) || MathF.Abs(area) <= Epsilon)
            {
                return;
            }

            if (area < 0f)
            {
                (p1, p3) = (p3, p1);
            }

            var figure = new PathFigure(p0, isClosed: true) { IsFilled = true };
            figure.Segments.Add(new LineSegment(p1));
            figure.Segments.Add(new LineSegment(p2));
            figure.Segments.Add(new LineSegment(p3));
            _path.Figures.Add(figure);
        }
    }

    private struct HitTestSink : ITriangleSink
    {
        private readonly Vector2 _point;

        public HitTestSink(Vector2 point)
        {
            _point = point;
            Contains = false;
        }

        public bool Contains { get; private set; }
        public bool IsComplete => Contains;

        public void AddTriangle(Vector2 p0, Vector2 p1, Vector2 p2)
        {
            if (Contains)
            {
                return;
            }

            float c0 = Cross(p0, p1, _point);
            float c1 = Cross(p1, p2, _point);
            float c2 = Cross(p2, p0, _point);
            bool hasNegative = c0 < -Epsilon || c1 < -Epsilon || c2 < -Epsilon;
            bool hasPositive = c0 > Epsilon || c1 > Epsilon || c2 > Epsilon;
            Contains = !(hasNegative && hasPositive);
        }

        public void AddQuad(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            AddTriangle(p0, p1, p2);
            AddTriangle(p0, p2, p3);
        }
    }

    private struct StrokeRun
    {
        public StrokeRun(Vector2 start, Vector2 firstNext, PenLineCap startCap, bool suppressStartCap)
        {
            Start = start;
            FirstNext = firstNext;
            Previous = start;
            Current = firstNext;
            StartCap = startCap;
            SuppressStartCap = suppressStartCap;
            Active = true;
        }

        public bool Active;
        public bool SuppressStartCap;
        public Vector2 Start;
        public Vector2 FirstNext;
        public Vector2 Previous;
        public Vector2 Current;
        public PenLineCap StartCap;
    }

    private struct DelayedStartCap
    {
        public DelayedStartCap(Vector2 start, Vector2 next, PenLineCap cap)
        {
            Start = start;
            Next = next;
            Cap = cap;
            HasValue = true;
        }

        public bool HasValue;
        public Vector2 Start;
        public Vector2 Next;
        public PenLineCap Cap;
    }
}
