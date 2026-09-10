namespace ProGPU.CAD;

public static partial class CadObjectSnapQuery
{
    private const int MaximumTangentBezierDegree = 10;
    private const int MaximumTangentRoots =
        (2 * MaximumTangentBezierDegree) - 1;
    private const int MaximumTangentConicCandidates =
        4 * MaximumTangentRoots;

    private static void EvaluateTangent(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        int entityIndex,
        CadPoint3D referencePoint,
        CadPoint3D queryPoint,
        ref SearchState search)
    {
        switch (header.Kind)
        {
            case CadEntityKind.Circle:
            {
                CadCirclePrimitive circle =
                    snapshot.Circles.Span[header.PrimitiveIndex];
                SearchState checkpoint = search;
                if (!EvaluateTangentEllipticalArc(
                        circle.Center,
                        circle.CoordinateSystem.XAxis * circle.Radius,
                        circle.CoordinateSystem.YAxis * circle.Radius,
                        0.0,
                        TwoPi,
                        referencePoint,
                        queryPoint,
                        entityIndex,
                        header.Handle,
                        ref search))
                {
                    search = checkpoint;
                    search.UnsupportedGeometryCount++;
                }
                return;
            }
            case CadEntityKind.Arc:
            {
                CadArcPrimitive arc = snapshot.Arcs.Span[header.PrimitiveIndex];
                SearchState checkpoint = search;
                if (!EvaluateTangentEllipticalArc(
                        arc.Center,
                        arc.CoordinateSystem.XAxis * arc.Radius,
                        arc.CoordinateSystem.YAxis * arc.Radius,
                        arc.StartAngle,
                        arc.SweepAngle,
                        referencePoint,
                        queryPoint,
                        entityIndex,
                        header.Handle,
                        ref search))
                {
                    search = checkpoint;
                    search.UnsupportedGeometryCount++;
                }
                return;
            }
            case CadEntityKind.Ellipse:
            {
                CadEllipsePrimitive ellipse =
                    snapshot.Ellipses.Span[header.PrimitiveIndex];
                SearchState checkpoint = search;
                if (!EvaluateTangentEllipticalArc(
                        ellipse.Center,
                        ellipse.MajorAxis,
                        ellipse.MinorAxis,
                        ellipse.StartParameter,
                        ellipse.SweepParameter,
                        referencePoint,
                        queryPoint,
                        entityIndex,
                        header.Handle,
                        ref search))
                {
                    search = checkpoint;
                    search.UnsupportedGeometryCount++;
                }
                return;
            }
            case CadEntityKind.LightweightPolyline:
            case CadEntityKind.Polyline2D:
                EvaluateTangentPolyline2D(
                    snapshot,
                    header,
                    entityIndex,
                    referencePoint,
                    queryPoint,
                    ref search);
                return;
            case CadEntityKind.Spline:
            {
                SearchState checkpoint = search;
                if (!EvaluateTangentSpline(
                        snapshot,
                        snapshot.Splines.Span[header.PrimitiveIndex],
                        referencePoint,
                        queryPoint,
                        entityIndex,
                        header.Handle,
                        ref search))
                {
                    search = checkpoint;
                    search.UnsupportedGeometryCount++;
                }
                return;
            }
            default:
                search.UnsupportedGeometryCount++;
                return;
        }
    }

    private static void EvaluateTangentPolyline2D(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        int entityIndex,
        CadPoint3D referencePoint,
        CadPoint3D queryPoint,
        ref SearchState search)
    {
        CadPolylinePrimitive polyline =
            snapshot.Polylines.Span[header.PrimitiveIndex];
        ReadOnlySpan<CadPolylineVertex> vertices =
            snapshot.PolylineVertices.Span.Slice(
                polyline.VertexOffset,
                polyline.VertexCount);
        int segmentCount = vertices.Length < 2
            ? 0
            : polyline.IsClosed
                ? vertices.Length
                : vertices.Length - 1;
        SearchState checkpoint = search;
        bool hasArc = false;
        for (int segmentIndex = 0;
             segmentIndex < segmentCount;
             segmentIndex++)
        {
            CadPolylineVertex start = vertices[segmentIndex];
            if (start.Bulge == 0.0)
            {
                continue;
            }
            hasArc = true;
            CadPolylineVertex end = vertices[(segmentIndex + 1) % vertices.Length];
            try
            {
                CadSnapshotCompiler.GetBulgeArc(
                    start,
                    end,
                    out double centerX,
                    out double centerY,
                    out double radius,
                    out double startAngle,
                    out double sweep);
                if (EvaluateTangentEllipticalArc(
                        ToWorld(polyline, centerX, centerY),
                        polyline.CoordinateSystem.XAxis * radius,
                        polyline.CoordinateSystem.YAxis * radius,
                        startAngle,
                        sweep,
                        referencePoint,
                        queryPoint,
                        entityIndex,
                        header.Handle,
                        ref search,
                        GetTangentOrdinalBase(segmentIndex)))
                {
                    continue;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (ArithmeticException)
            {
            }
            search = checkpoint;
            search.UnsupportedGeometryCount++;
            return;
        }
        if (!hasArc)
        {
            search.UnsupportedGeometryCount++;
        }
    }

    private static bool EvaluateTangentEllipticalArc(
        CadPoint3D center,
        CadPoint3D axisX,
        CadPoint3D axisY,
        double startParameter,
        double sweepParameter,
        CadPoint3D referencePoint,
        CadPoint3D queryPoint,
        int entityIndex,
        ulong handle,
        ref SearchState search,
        int ordinalBase = 0)
    {
        if (!IsFinite(center) || !IsFinite(axisX) || !IsFinite(axisY) ||
            !double.IsFinite(startParameter) ||
            !double.IsFinite(sweepParameter) || sweepParameter == 0.0)
        {
            return false;
        }
        double boundedSweep = Math.CopySign(
            Math.Min(Math.Abs(sweepParameter), TwoPi),
            sweepParameter);
        int spanCount = Math.Max(
            1,
            (int)Math.Ceiling(Math.Abs(boundedSweep) / (Math.PI * 0.5)));
        double spanSweep = boundedSweep / spanCount;
        Span<CadHomogeneousPoint> span = stackalloc CadHomogeneousPoint[3];
        Span<CadPoint3D> candidates =
            stackalloc CadPoint3D[MaximumTangentRoots];
        int ordinal = ordinalBase;
        for (int spanIndex = 0; spanIndex < spanCount; spanIndex++)
        {
            if (!TryCreateEllipticalArcBezierSpan(
                    center,
                    axisX,
                    axisY,
                    startParameter + (spanIndex * spanSweep),
                    spanSweep,
                    span) ||
                !CadSplineSelection.TryCollectPlanTangentPoints(
                    span,
                    referencePoint,
                    queryPoint,
                    candidates,
                    out int candidateCount))
            {
                return false;
            }
            for (int index = 0; index < candidateCount; index++)
            {
                search.Consider(
                    CadObjectSnapKind.Tangent,
                    candidates[index],
                    entityIndex,
                    handle,
                    TakeTangentOrdinal(ref ordinal));
            }
        }
        return true;
    }

    private static bool EvaluateTangentSpline(
        CadDocumentSnapshot snapshot,
        in CadSplinePrimitive spline,
        CadPoint3D referencePoint,
        CadPoint3D queryPoint,
        int entityIndex,
        ulong handle,
        ref SearchState search)
    {
        if (!CadSplineCanonicalizer.TryCreate(
                snapshot,
                spline,
                out CadCanonicalSpline canonical))
        {
            return false;
        }

        Span<CadHomogeneousPoint> controlPoints =
            stackalloc CadHomogeneousPoint[MaximumTangentBezierDegree + 1];
        Span<CadPoint3D> candidates =
            stackalloc CadPoint3D[MaximumTangentRoots];
        CadPoint3D firstPoint = default;
        CadPoint3D lastPoint = default;
        bool hasSpan = false;
        int ordinal = 0;
        for (int sourceSpan = canonical.Degree;
             sourceSpan < canonical.ControlPointCount;
             sourceSpan++)
        {
            if (!(canonical.GetKnot(sourceSpan + 1) >
                  canonical.GetKnot(sourceSpan)))
            {
                continue;
            }
            Span<CadHomogeneousPoint> span =
                controlPoints[..(canonical.Degree + 1)];
            if (!CadRationalBezier.TryExtractSpan(canonical, sourceSpan, span) ||
                !CadSplineSelection.TryCollectPlanTangentPoints(
                    span,
                    referencePoint,
                    queryPoint,
                    candidates,
                    out int candidateCount))
            {
                return false;
            }
            if (!hasSpan)
            {
                firstPoint = span[0].Cartesian;
                hasSpan = true;
            }
            lastPoint = span[^1].Cartesian;
            for (int index = 0; index < candidateCount; index++)
            {
                search.Consider(
                    CadObjectSnapKind.Tangent,
                    candidates[index],
                    entityIndex,
                    handle,
                    TakeTangentOrdinal(ref ordinal));
            }
        }
        if (!hasSpan)
        {
            return false;
        }
        if (!canonical.HasClosingEdge)
        {
            return true;
        }

        Span<CadHomogeneousPoint> closing =
            controlPoints[..(canonical.Degree + 1)];
        CadRationalBezier.CreateElevatedLine(lastPoint, firstPoint, closing);
        if (!CadSplineSelection.TryCollectPlanTangentPoints(
                closing,
                referencePoint,
                queryPoint,
                candidates,
                out int closingCount))
        {
            return false;
        }
        for (int index = 0; index < closingCount; index++)
        {
            search.Consider(
                CadObjectSnapKind.Tangent,
                candidates[index],
                entityIndex,
                handle,
                TakeTangentOrdinal(ref ordinal));
        }
        return true;
    }

    private static int GetTangentOrdinalBase(int segmentIndex)
    {
        int maximumBase = int.MaxValue - MaximumTangentConicCandidates;
        return segmentIndex > maximumBase / MaximumTangentConicCandidates
            ? maximumBase
            : segmentIndex * MaximumTangentConicCandidates;
    }

    private static int TakeTangentOrdinal(ref int ordinal)
    {
        int current = ordinal;
        if (ordinal < int.MaxValue)
        {
            ordinal++;
        }
        return current;
    }
}
