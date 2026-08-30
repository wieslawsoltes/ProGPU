namespace ProGPU.CAD;

public static partial class CadObjectSnapQuery
{
    private static void EvaluateNearest(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        int entityIndex,
        CadPoint3D queryPoint,
        ref SearchState search)
    {
        switch (header.Kind)
        {
            case CadEntityKind.Point:
                search.Consider(
                    CadObjectSnapKind.Nearest,
                    snapshot.Points.Span[header.PrimitiveIndex].Position,
                    entityIndex,
                    header.Handle,
                    0);
                return;
            case CadEntityKind.Line:
            {
                CadLinePrimitive line = snapshot.Lines.Span[header.PrimitiveIndex];
                search.Consider(
                    CadObjectSnapKind.Nearest,
                    ClosestPlanPointOnLinear(
                        line.Start,
                        line.End - line.Start,
                        0.0,
                        1.0,
                        queryPoint),
                    entityIndex,
                    header.Handle,
                    0);
                return;
            }
            case CadEntityKind.Ray:
            case CadEntityKind.XLine:
            {
                CadConstructionLinePrimitive line =
                    snapshot.ConstructionLines.Span[header.PrimitiveIndex];
                search.Consider(
                    CadObjectSnapKind.Nearest,
                    ClosestPlanPointOnLinear(
                        line.BasePoint,
                        line.Direction,
                        header.Kind == CadEntityKind.Ray
                            ? 0.0
                            : double.NegativeInfinity,
                        double.PositiveInfinity,
                        queryPoint),
                    entityIndex,
                    header.Handle,
                    0);
                return;
            }
            case CadEntityKind.Circle:
            {
                CadCirclePrimitive circle =
                    snapshot.Circles.Span[header.PrimitiveIndex];
                if (TryClosestPlanPointOnEllipticalArc(
                        circle.Center,
                        circle.CoordinateSystem.XAxis * circle.Radius,
                        circle.CoordinateSystem.YAxis * circle.Radius,
                        0.0,
                        TwoPi,
                        queryPoint,
                        out CadPoint3D closest))
                {
                    search.Consider(
                        CadObjectSnapKind.Nearest,
                        closest,
                        entityIndex,
                        header.Handle,
                        0);
                }
                else
                {
                    search.UnsupportedGeometryCount++;
                }
                return;
            }
            case CadEntityKind.Arc:
            {
                CadArcPrimitive arc = snapshot.Arcs.Span[header.PrimitiveIndex];
                if (TryClosestPlanPointOnEllipticalArc(
                        arc.Center,
                        arc.CoordinateSystem.XAxis * arc.Radius,
                        arc.CoordinateSystem.YAxis * arc.Radius,
                        arc.StartAngle,
                        arc.SweepAngle,
                        queryPoint,
                        out CadPoint3D closest))
                {
                    search.Consider(
                        CadObjectSnapKind.Nearest,
                        closest,
                        entityIndex,
                        header.Handle,
                        0);
                }
                else
                {
                    search.UnsupportedGeometryCount++;
                }
                return;
            }
            case CadEntityKind.Ellipse:
            {
                CadEllipsePrimitive ellipse =
                    snapshot.Ellipses.Span[header.PrimitiveIndex];
                if (TryClosestPlanPointOnEllipticalArc(
                        ellipse.Center,
                        ellipse.MajorAxis,
                        ellipse.MinorAxis,
                        ellipse.StartParameter,
                        ellipse.SweepParameter,
                        queryPoint,
                        out CadPoint3D closest))
                {
                    search.Consider(
                        CadObjectSnapKind.Nearest,
                        closest,
                        entityIndex,
                        header.Handle,
                        0);
                }
                else
                {
                    search.UnsupportedGeometryCount++;
                }
                return;
            }
            case CadEntityKind.LightweightPolyline:
            case CadEntityKind.Polyline2D:
                EvaluateNearestPolyline2D(
                    snapshot,
                    header,
                    entityIndex,
                    queryPoint,
                    ref search);
                return;
            case CadEntityKind.Polyline3D:
                EvaluateNearestPolyline3D(
                    snapshot,
                    header,
                    entityIndex,
                    queryPoint,
                    ref search);
                return;
            case CadEntityKind.Spline:
            {
                CadSplinePrimitive spline =
                    snapshot.Splines.Span[header.PrimitiveIndex];
                if (CadSplineSelection.TryGetClosestPlanPoint(
                        snapshot,
                        spline,
                        queryPoint,
                        out CadPoint3D closest))
                {
                    search.Consider(
                        CadObjectSnapKind.Nearest,
                        closest,
                        entityIndex,
                        header.Handle,
                        0);
                }
                else
                {
                    search.UnsupportedGeometryCount++;
                }
                return;
            }
            default:
                search.UnsupportedGeometryCount++;
                return;
        }
    }

    private static void EvaluateNearestPolyline2D(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        int entityIndex,
        CadPoint3D queryPoint,
        ref SearchState search)
    {
        CadPolylinePrimitive polyline =
            snapshot.Polylines.Span[header.PrimitiveIndex];
        ReadOnlySpan<CadPolylineVertex> vertices =
            snapshot.PolylineVertices.Span.Slice(
                polyline.VertexOffset,
                polyline.VertexCount);
        if (vertices.Length == 1)
        {
            search.Consider(
                CadObjectSnapKind.Nearest,
                ToWorld(polyline, vertices[0]),
                entityIndex,
                header.Handle,
                0);
            return;
        }
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
                CadPoint3D worldEnd = ToWorld(polyline, end);
                search.Consider(
                    CadObjectSnapKind.Nearest,
                    ClosestPlanPointOnLinear(
                        worldStart,
                        worldEnd - worldStart,
                        0.0,
                        1.0,
                        queryPoint),
                    entityIndex,
                    header.Handle,
                    segmentIndex);
                continue;
            }
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
                if (TryClosestPlanPointOnEllipticalArc(
                        ToWorld(polyline, centerX, centerY),
                        polyline.CoordinateSystem.XAxis * radius,
                        polyline.CoordinateSystem.YAxis * radius,
                        startAngle,
                        sweep,
                        queryPoint,
                        out CadPoint3D closest))
                {
                    search.Consider(
                        CadObjectSnapKind.Nearest,
                        closest,
                        entityIndex,
                        header.Handle,
                        segmentIndex);
                    continue;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (ArithmeticException)
            {
            }
            search.UnsupportedGeometryCount++;
        }
    }

    private static void EvaluateNearestPolyline3D(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        int entityIndex,
        CadPoint3D queryPoint,
        ref SearchState search)
    {
        CadPolyline3DPrimitive polyline =
            snapshot.Polylines3D.Span[header.PrimitiveIndex];
        ReadOnlySpan<CadPoint3D> points =
            snapshot.Polyline3DPoints.Span.Slice(
                polyline.PointOffset,
                polyline.PointCount);
        if (points.Length == 1)
        {
            search.Consider(
                CadObjectSnapKind.Nearest,
                points[0],
                entityIndex,
                header.Handle,
                0);
            return;
        }
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
            CadPoint3D end = points[(segmentIndex + 1) % points.Length];
            search.Consider(
                CadObjectSnapKind.Nearest,
                ClosestPlanPointOnLinear(
                    start,
                    end - start,
                    0.0,
                    1.0,
                    queryPoint),
                entityIndex,
                header.Handle,
                segmentIndex);
        }
    }

    private static CadPoint3D ClosestPlanPointOnLinear(
        CadPoint3D origin,
        CadPoint3D direction,
        double minimumParameter,
        double maximumParameter,
        CadPoint3D queryPoint)
    {
        double denominator =
            (direction.X * direction.X) +
            (direction.Y * direction.Y);
        double parameter = denominator > 0.0 && double.IsFinite(denominator)
            ? (((queryPoint.X - origin.X) * direction.X) +
               ((queryPoint.Y - origin.Y) * direction.Y)) / denominator
            : 0.0;
        parameter = Math.Max(minimumParameter, Math.Min(maximumParameter, parameter));
        return origin + (direction * parameter);
    }

    private static bool TryClosestPlanPointOnEllipticalArc(
        CadPoint3D center,
        CadPoint3D axisX,
        CadPoint3D axisY,
        double startParameter,
        double sweepParameter,
        CadPoint3D queryPoint,
        out CadPoint3D closest)
    {
        closest = default;
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
        double minimumDistance = double.PositiveInfinity;
        Span<CadHomogeneousPoint> span = stackalloc CadHomogeneousPoint[3];
        for (int spanIndex = 0; spanIndex < spanCount; spanIndex++)
        {
            double start = startParameter + (spanIndex * spanSweep);
            double end = start + spanSweep;
            double middle = (start * 0.5) + (end * 0.5);
            double weight = Math.Cos(spanSweep * 0.5);
            if (!(weight > 0.0) || !double.IsFinite(weight))
            {
                return false;
            }
            CadPoint3D first = EllipsePoint(center, axisX, axisY, start);
            CadPoint3D last = EllipsePoint(center, axisX, axisY, end);
            CadPoint3D middleDirection =
                (axisX * Math.Cos(middle)) +
                (axisY * Math.Sin(middle));
            span[0] = CadHomogeneousPoint.FromCartesian(first, 1.0);
            span[1] = new CadHomogeneousPoint(
                (center.X * weight) + middleDirection.X,
                (center.Y * weight) + middleDirection.Y,
                (center.Z * weight) + middleDirection.Z,
                weight);
            span[2] = CadHomogeneousPoint.FromCartesian(last, 1.0);
            if (!CadSplineSelection.TryClosestPlanPointToBezier(
                    span,
                    queryPoint,
                    out CadPoint3D candidate,
                    out double distance))
            {
                return false;
            }
            if (distance < minimumDistance)
            {
                minimumDistance = distance;
                closest = candidate;
            }
        }
        return IsFinite(closest);
    }

    private static CadPoint3D EllipsePoint(
        CadPoint3D center,
        CadPoint3D axisX,
        CadPoint3D axisY,
        double parameter) =>
        center +
        (axisX * Math.Cos(parameter)) +
        (axisY * Math.Sin(parameter));
}
