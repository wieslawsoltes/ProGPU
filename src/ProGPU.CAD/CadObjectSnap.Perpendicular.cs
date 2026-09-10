namespace ProGPU.CAD;

public static partial class CadObjectSnapQuery
{
    private const int MaximumPerpendicularBezierDegree = 10;
    private const int MaximumPerpendicularRoots =
        (3 * MaximumPerpendicularBezierDegree) - 1;
    private const int MaximumPerpendicularConicCandidates =
        4 * MaximumPerpendicularRoots;

    private static void EvaluatePerpendicular(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        int entityIndex,
        CadPoint3D referencePoint,
        CadPoint3D queryPoint,
        ref SearchState search)
    {
        switch (header.Kind)
        {
            case CadEntityKind.Line:
            {
                CadLinePrimitive line = snapshot.Lines.Span[header.PrimitiveIndex];
                if (TryPerpendicularPlanPointOnLinear(
                        line.Start,
                        line.End - line.Start,
                        0.0,
                        1.0,
                        referencePoint,
                        out CadPoint3D candidate))
                {
                    search.Consider(
                        CadObjectSnapKind.Perpendicular,
                        candidate,
                        entityIndex,
                        header.Handle,
                        0);
                }
                return;
            }
            case CadEntityKind.Ray:
            case CadEntityKind.XLine:
            {
                CadConstructionLinePrimitive line =
                    snapshot.ConstructionLines.Span[header.PrimitiveIndex];
                if (TryPerpendicularPlanPointOnLinear(
                        line.BasePoint,
                        line.Direction,
                        header.Kind == CadEntityKind.Ray
                            ? 0.0
                            : double.NegativeInfinity,
                        double.PositiveInfinity,
                        referencePoint,
                        out CadPoint3D candidate))
                {
                    search.Consider(
                        CadObjectSnapKind.Perpendicular,
                        candidate,
                        entityIndex,
                        header.Handle,
                        0);
                }
                return;
            }
            case CadEntityKind.Circle:
            {
                CadCirclePrimitive circle =
                    snapshot.Circles.Span[header.PrimitiveIndex];
                SearchState checkpoint = search;
                if (!EvaluatePerpendicularEllipticalArc(
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
                if (!EvaluatePerpendicularEllipticalArc(
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
                if (!EvaluatePerpendicularEllipticalArc(
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
                EvaluatePerpendicularPolyline2D(
                    snapshot,
                    header,
                    entityIndex,
                    referencePoint,
                    queryPoint,
                    ref search);
                return;
            case CadEntityKind.Polyline3D:
                EvaluatePerpendicularPolyline3D(
                    snapshot,
                    header,
                    entityIndex,
                    referencePoint,
                    ref search);
                return;
            case CadEntityKind.Spline:
            {
                SearchState checkpoint = search;
                if (!EvaluatePerpendicularSpline(
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

    private static void EvaluatePerpendicularPolyline2D(
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
        for (int segmentIndex = 0;
             segmentIndex < segmentCount;
             segmentIndex++)
        {
            CadPolylineVertex start = vertices[segmentIndex];
            CadPolylineVertex end = vertices[(segmentIndex + 1) % vertices.Length];
            if (start.Bulge == 0.0)
            {
                CadPoint3D worldStart = ToWorld(polyline, start);
                if (TryPerpendicularPlanPointOnLinear(
                        worldStart,
                        ToWorld(polyline, end) - worldStart,
                        0.0,
                        1.0,
                        referencePoint,
                        out CadPoint3D candidate))
                {
                    search.Consider(
                        CadObjectSnapKind.Perpendicular,
                        candidate,
                        entityIndex,
                        header.Handle,
                        GetPerpendicularOrdinalBase(segmentIndex));
                }
                continue;
            }

            SearchState checkpoint = search;
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
                if (EvaluatePerpendicularEllipticalArc(
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
                        GetPerpendicularOrdinalBase(segmentIndex)))
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
        }
    }

    private static void EvaluatePerpendicularPolyline3D(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        int entityIndex,
        CadPoint3D referencePoint,
        ref SearchState search)
    {
        CadPolyline3DPrimitive polyline =
            snapshot.Polylines3D.Span[header.PrimitiveIndex];
        ReadOnlySpan<CadPoint3D> points =
            snapshot.Polyline3DPoints.Span.Slice(
                polyline.PointOffset,
                polyline.PointCount);
        int segmentCount = points.Length < 2
            ? 0
            : polyline.IsClosed
                ? points.Length
                : points.Length - 1;
        for (int segmentIndex = 0;
             segmentIndex < segmentCount;
             segmentIndex++)
        {
            CadPoint3D start = points[segmentIndex];
            if (TryPerpendicularPlanPointOnLinear(
                    start,
                    points[(segmentIndex + 1) % points.Length] - start,
                    0.0,
                    1.0,
                    referencePoint,
                    out CadPoint3D candidate))
            {
                search.Consider(
                    CadObjectSnapKind.Perpendicular,
                    candidate,
                    entityIndex,
                    header.Handle,
                    segmentIndex);
            }
        }
    }

    private static bool EvaluatePerpendicularEllipticalArc(
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
            stackalloc CadPoint3D[MaximumPerpendicularRoots];
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
                !CadSplineSelection.TryCollectPlanPerpendicularPoints(
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
                    CadObjectSnapKind.Perpendicular,
                    candidates[index],
                    entityIndex,
                    handle,
                    TakePerpendicularOrdinal(ref ordinal));
            }
        }
        return true;
    }

    private static bool EvaluatePerpendicularSpline(
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
            stackalloc CadHomogeneousPoint[MaximumPerpendicularBezierDegree + 1];
        Span<CadPoint3D> candidates =
            stackalloc CadPoint3D[MaximumPerpendicularRoots];
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
                !CadSplineSelection.TryCollectPlanPerpendicularPoints(
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
                    CadObjectSnapKind.Perpendicular,
                    candidates[index],
                    entityIndex,
                    handle,
                    TakePerpendicularOrdinal(ref ordinal));
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
        if (!CadSplineSelection.TryCollectPlanPerpendicularPoints(
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
                CadObjectSnapKind.Perpendicular,
                candidates[index],
                entityIndex,
                handle,
                TakePerpendicularOrdinal(ref ordinal));
        }
        return true;
    }

    private static bool TryPerpendicularPlanPointOnLinear(
        CadPoint3D origin,
        CadPoint3D direction,
        double minimumParameter,
        double maximumParameter,
        CadPoint3D referencePoint,
        out CadPoint3D candidate)
    {
        candidate = default;
        double denominator =
            (direction.X * direction.X) +
            (direction.Y * direction.Y);
        if (!(denominator > 0.0) || !double.IsFinite(denominator))
        {
            return false;
        }
        double parameter =
            (((referencePoint.X - origin.X) * direction.X) +
             ((referencePoint.Y - origin.Y) * direction.Y)) / denominator;
        if (!double.IsFinite(parameter) ||
            parameter < minimumParameter - SnapParameterTolerance ||
            parameter > maximumParameter + SnapParameterTolerance)
        {
            return false;
        }
        parameter = Math.Max(
            minimumParameter,
            Math.Min(maximumParameter, parameter));
        candidate = origin + (direction * parameter);
        return IsFinite(candidate);
    }

    private static int GetPerpendicularOrdinalBase(int segmentIndex)
    {
        int maximumBase = int.MaxValue - MaximumPerpendicularConicCandidates;
        return segmentIndex > maximumBase / MaximumPerpendicularConicCandidates
            ? maximumBase
            : segmentIndex * MaximumPerpendicularConicCandidates;
    }

    private static int TakePerpendicularOrdinal(ref int ordinal)
    {
        int current = ordinal;
        if (ordinal < int.MaxValue)
        {
            ordinal++;
        }
        return current;
    }
}
