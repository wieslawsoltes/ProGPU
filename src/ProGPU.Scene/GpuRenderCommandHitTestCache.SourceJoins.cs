using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Vector;

namespace ProGPU.Scene;

public sealed partial class GpuRenderCommandHitTestCacheBuilder
{
    private readonly record struct SourceStrokeSegment(
        Vector2 Start, Vector2 End, PathSegment Segment,
        Vector2 Incoming, Vector2 Outgoing, bool HasIncoming, bool HasOutgoing);

    // A multi-segment PathStroke payload rounds internal segment boundaries.
    // Explicit source joins instead own one flat-ended body per source segment,
    // followed by the actual join triangles. Curve sampling inside a segment,
    // endpoint cap predicates, clips and the retained source frame stay intact.
    private bool TryAddSourcePathStrokePrimitives(PathGeometry path, Matrix4x4 transform,
        int id, float zIndex, Pen pen, float thickness)
    {
        int primitiveStart = _primitives.Count, segmentStart = _pathSegments.Count;
        var oldMinimum = _boundsMin; var oldMaximum = _boundsMax; bool oldHasBounds = _hasBounds;
        try
        {
            if (path.IsCombined) throw new NotSupportedException("Combined source stroke geometry must be resolved before input capture.");
            var run = new List<SourceStrokeSegment>();
            foreach (var figure in path.Figures)
            {
                run.Clear();
                var current = figure.StartPoint;
                var startCap = figure.StrokeStartLineCap ?? pen.StartLineCap;
                var endCap = figure.StrokeEndLineCap ?? pen.EndLineCap;
                Vector2? degenerate = null;
                foreach (var segment in figure.Segments)
                {
                    if (!Compositor.TryGetPathSegmentEndPoint(segment, out var end) || !IsFinite(current) || !IsFinite(end))
                        throw new NotSupportedException("Source input requires an owned finite stroke segment.");
                    if (!segment.IsStroked)
                    {
                        AppendSourceStrokeRun(run, false, !figure.IsClosed, figure.StartPoint,
                            current, startCap, endCap, transform, id, zIndex, pen, thickness);
                        if (run.Count == 0 && degenerate.HasValue && !figure.IsClosed)
                            AppendSourceDegenerateCaps(degenerate.Value, startCap, endCap, thickness, transform, id, zIndex);
                        run.Clear();
                        degenerate = null;
                        current = end;
                        continue;
                    }
                    bool hasIncoming = Compositor.TryGetPathSegmentStartDirection(segment, current, out var incoming);
                    bool hasOutgoing = Compositor.TryGetPathSegmentEndDirection(segment, current, out var outgoing);
                    // Keep constants out of the tangent chain, as the painter does.
                    if (hasIncoming || hasOutgoing || Vector2.DistanceSquared(current, end) > 0.00000001f)
                    {
                        run.Add(new(current, end, segment, incoming, outgoing, hasIncoming, hasOutgoing));
                        degenerate = null;
                    }
                    else if (run.Count == 0 && !figure.IsClosed)
                        degenerate = current;
                    current = end;
                }
                AppendSourceStrokeRun(run, figure.IsClosed, !figure.IsClosed, figure.StartPoint,
                    current, startCap, endCap, transform, id, zIndex, pen, thickness);
                if (run.Count == 0 && degenerate.HasValue && !figure.IsClosed)
                    AppendSourceDegenerateCaps(degenerate.Value, startCap, endCap, thickness, transform, id, zIndex);
            }
            return true;
        }
        catch
        {
            _primitives.RemoveRange(primitiveStart, _primitives.Count - primitiveStart);
            _pathSegments.RemoveRange(segmentStart, _pathSegments.Count - segmentStart);
            _boundsMin = oldMinimum; _boundsMax = oldMaximum; _hasBounds = oldHasBounds;
            throw;
        }
    }

    private void AppendSourceDegenerateCaps(Vector2 point, PenLineCap startCap, PenLineCap endCap,
        float thickness, Matrix4x4 transform, int id, float zIndex)
    {
        if (!Compositor.RequiresAffineStrokeGeometry(transform))
        {
            point = Vector2.Transform(point, transform);
            thickness *= TransformMetrics.GetStrokeScale(transform);
            transform = Matrix4x4.Identity;
        }
        AddPrimitive(GpuHitTestPrimitive.LineStroke(id, point, point, thickness,
            ToLineGeometryCap(startCap), ToLineGeometryCap(endCap), 0f, transform, zIndex));
    }

    private void AppendSourceStrokeRun(List<SourceStrokeSegment> run, bool close, bool caps,
        Vector2 figureStart, Vector2 currentPoint, PenLineCap startCap, PenLineCap endCap, Matrix4x4 transform,
        int id, float zIndex, Pen pen, float thickness)
    {
        if (run.Count == 0)
        {
            // The painter retains the implicit closing edge even after a
            // trailing unstroked source segment cleared all join tangents.
            if (close && currentPoint != figureStart)
                AppendSourceHitBody(currentPoint, new LineSegment(figureStart),
                    LineGeometryCap.Flat, LineGeometryCap.Flat, thickness, transform, id, zIndex);
            return;
        }
        int sourceCount = run.Count;
        if (close && run[^1].End != figureStart)
        {
            var start = run[^1].End;
            var direction = figureStart - start;
            run.Add(new(start, figureStart, new LineSegment(figureStart), direction, direction, true, true));
        }
        for (int i = 0; i < run.Count; i++)
        {
            var segment = run[i];
            AppendSourceHitBody(segment.Start, segment.Segment,
                caps && i == 0 ? ToLineGeometryCap(startCap) : LineGeometryCap.Flat,
                caps && i == run.Count - 1 ? ToLineGeometryCap(endCap) : LineGeometryCap.Flat,
                thickness, transform, id, zIndex);
            if (i > 0 && run[i - 1].HasOutgoing && segment.HasIncoming)
                AppendSourceHitJoin(pen, thickness, segment.Start, run[i - 1].Outgoing,
                    segment.Incoming, segment.Segment.IsSmoothJoin, transform, id, zIndex);
        }
        if (close && run[^1].HasOutgoing && run[0].HasIncoming)
            AppendSourceHitJoin(pen, thickness, figureStart, run[^1].Outgoing, run[0].Incoming,
                run[0].Segment.IsSmoothJoin, transform, id, zIndex);
        if (run.Count != sourceCount) run.RemoveAt(run.Count - 1);
    }

    private void AppendSourceHitBody(Vector2 start, PathSegment segment,
        LineGeometryCap startCap, LineGeometryCap endCap, float thickness,
        Matrix4x4 transform, int id, float zIndex)
    {
        var body = new PathGeometry();
        var figure = new PathFigure(start) { IsFilled = false };
        figure.Segments.Add(segment); body.Figures.Add(figure);
        if (!Compositor.RequiresAffineStrokeGeometry(transform))
        {
            body = body.CreateTransformed(transform);
            thickness *= TransformMetrics.GetStrokeScale(transform);
            transform = Matrix4x4.Identity;
        }
        if (!TryCompileHitTestPath(body, out var compiled) ||
            !IsFinite(compiled.Min) || !IsFinite(compiled.Max))
            throw new NotSupportedException("Source stroke input body could not be compiled.");
        AddPathStrokePrimitive(compiled, transform, id, zIndex, thickness, startCap, endCap);
    }

    private void AppendSourceHitJoin(Pen pen, float thickness, Vector2 center,
        Vector2 incoming, Vector2 outgoing, bool smooth, Matrix4x4 transform, int id, float zIndex)
    {
        Span<StrokeJoinTriangle> triangles = stackalloc StrokeJoinTriangle[StrokeJoinGeometry.MaxTrianglesPerJoin];
        bool affine = Compositor.RequiresAffineStrokeGeometry(transform);
        if (!affine)
        {
            center = Vector2.Transform(center, transform);
            incoming = Compositor.TransformDirection(incoming, transform);
            outgoing = Compositor.TransformDirection(outgoing, transform);
            thickness *= TransformMetrics.GetStrokeScale(transform);
        }
        int count = StrokeJoinGeometry.WriteDirectionalJoin(triangles, pen, thickness,
            center, incoming, outgoing, smooth);
        for (int i = 0; i < count; i++)
        {
            var triangle = triangles[i];
            if (!IsFinite(triangle.P0) || !IsFinite(triangle.P1) || !IsFinite(triangle.P2))
                throw new NotSupportedException("Source stroke input join is not finite.");
            var world0 = affine ? Vector2.Transform(triangle.P0, transform) : triangle.P0;
            var world1 = affine ? Vector2.Transform(triangle.P1, transform) : triangle.P1;
            var world2 = affine ? Vector2.Transform(triangle.P2, transform) : triangle.P2;
            var a = world1 - world0; var b = world2 - world0;
            // Match emitted triangle coverage, not its padded raster quad.
            if (MathF.Abs(a.X * b.Y - a.Y * b.X) <= 0.0001f) continue;
            var path = RenderCommandGeometryCache.CreateTrianglePath(world0, world1, world2);
            if (!TryCompileHitTestPath(path, out var compiled))
                throw new NotSupportedException("Source stroke input join could not be compiled.");
            AddPathFillPrimitive(compiled, Matrix4x4.Identity, id, zIndex);
        }
    }
}
