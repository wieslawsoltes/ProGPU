namespace ProGPU.CAD;

/// <summary>One immutable broad-phase selection candidate from a document snapshot.</summary>
public readonly record struct CadSelectionCandidate(
    ulong ContentGeneration,
    int EntityIndex,
    ulong Handle,
    CadEntityKind Kind,
    CadBounds3D Bounds);

public readonly record struct CadSelectionQueryResult(
    ulong ContentGeneration,
    int WrittenCount,
    int TotalCount)
{
    public bool IsTruncated => WrittenCount != TotalCount;
}

/// <summary>Caller-buffered broad-phase selection over immutable snapshot bounds.</summary>
public static class CadSelectionQuery
{
    /// <summary>Maps intersecting BVH entries to source primitive candidates.</summary>
    /// <remarks>
    /// Work is O(log E + K) on typical spatial data and O(E + K) worst-case for E
    /// snapshot primitives and K intersecting bounds. Expanded block primitives may
    /// share one semantic root handle and remain separate candidates for exact
    /// geometry testing. The smaller buffer capacity controls the written count.
    /// </remarks>
    public static CadSelectionQueryResult QueryBounds(
        CadDocumentSnapshot snapshot,
        CadBounds3D bounds,
        Span<int> entityIndexScratch,
        Span<CadSelectionCandidate> destination)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int capacity = Math.Min(entityIndexScratch.Length, destination.Length);
        CadSpatialQueryResult spatial = snapshot.SpatialIndex.Query(
            bounds,
            entityIndexScratch[..capacity]);
        ReadOnlySpan<CadEntityHeader> entities = snapshot.Entities.Span;
        for (int i = 0; i < spatial.WrittenCount; i++)
        {
            int entityIndex = entityIndexScratch[i];
            CadEntityHeader entity = entities[entityIndex];
            destination[i] = new CadSelectionCandidate(
                snapshot.ContentGeneration,
                entityIndex,
                entity.Handle,
                entity.Kind,
                entity.Bounds);
        }

        return new CadSelectionQueryResult(
            snapshot.ContentGeneration,
            spatial.WrittenCount,
            spatial.TotalCount);
    }
}

public enum CadPointHitStatus : byte
{
    Miss = 0,
    Hit = 1,
    UnsupportedKind = 2,
    UnsupportedGeometry = 3,
}

public readonly record struct CadPointHitResult(
    CadPointHitStatus Status,
    double Distance)
{
    public bool IsHit => Status == CadPointHitStatus.Hit;

    public bool IsSupported =>
        Status is CadPointHitStatus.Hit or CadPointHitStatus.Miss;
}

/// <summary>Exact world-space point proximity tests for supported snapshot primitives.</summary>
public static class CadSelectionHitTester
{
    private const double AxisTolerance = 1e-10;
    private const double TwoPi = Math.PI * 2.0;

    public static CadPointHitResult HitTestPoint(
        CadDocumentSnapshot snapshot,
        CadSelectionCandidate candidate,
        CadPoint3D point,
        double tolerance)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!AreFinite(point))
        {
            throw new ArgumentException("A hit-test point must be finite.", nameof(point));
        }
        if (!double.IsFinite(tolerance) || tolerance < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tolerance),
                "Hit-test tolerance must be finite and non-negative.");
        }
        if (candidate.ContentGeneration != snapshot.ContentGeneration)
        {
            throw new InvalidOperationException(
                "The selection candidate belongs to a different snapshot generation.");
        }

        ReadOnlySpan<CadEntityHeader> entities = snapshot.Entities.Span;
        if ((uint)candidate.EntityIndex >= (uint)entities.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidate),
                "The selection candidate entity index is outside the snapshot.");
        }
        CadEntityHeader header = entities[candidate.EntityIndex];
        if (candidate.Handle != header.Handle ||
            candidate.Kind != header.Kind ||
            candidate.Bounds != header.Bounds)
        {
            throw new InvalidOperationException(
                "The selection candidate does not match its snapshot entity.");
        }

        return header.Kind switch
        {
            CadEntityKind.Line => FromDistance(
                DistanceToSegment(
                    point,
                    snapshot.Lines.Span[header.PrimitiveIndex].Start,
                    snapshot.Lines.Span[header.PrimitiveIndex].End),
                tolerance),
            CadEntityKind.Circle => HitCircle(
                snapshot.Circles.Span[header.PrimitiveIndex],
                point,
                tolerance),
            CadEntityKind.Arc => HitArc(
                snapshot.Arcs.Span[header.PrimitiveIndex],
                point,
                tolerance),
            CadEntityKind.LightweightPolyline or CadEntityKind.Polyline2D =>
                HitPolyline2D(snapshot, header, point, tolerance),
            CadEntityKind.Polyline3D =>
                HitPolyline3D(snapshot, header, point, tolerance),
            CadEntityKind.Solid =>
                HitSolid(snapshot.Faces.Span[header.PrimitiveIndex], point, tolerance),
            CadEntityKind.Face3D =>
                HitFaceEdges(snapshot.Faces.Span[header.PrimitiveIndex], point, tolerance),
            _ => new CadPointHitResult(
                CadPointHitStatus.UnsupportedKind,
                double.NaN),
        };
    }

    private static CadPointHitResult HitCircle(
        CadCirclePrimitive circle,
        CadPoint3D point,
        double tolerance)
    {
        if (!TryGetCircularBasis(
                circle.CoordinateSystem,
                circle.Radius,
                out CircularBasis basis))
        {
            return UnsupportedGeometry();
        }
        CadPoint3D delta = point - circle.Center;
        double x = CadPoint3D.Dot(delta, basis.XAxis);
        double y = CadPoint3D.Dot(delta, basis.YAxis);
        double radial = new CadPoint3D(x, y, 0.0).Length;
        double plane = Math.Abs(CadPoint3D.Dot(delta, basis.Normal));
        double distance = new CadPoint3D(
            radial - basis.Radius,
            plane,
            0.0).Length;
        return FromDistance(distance, tolerance);
    }

    private static CadPointHitResult HitArc(
        CadArcPrimitive arc,
        CadPoint3D point,
        double tolerance)
    {
        if (!TryGetCircularBasis(
                arc.CoordinateSystem,
                arc.Radius,
                out CircularBasis basis))
        {
            return UnsupportedGeometry();
        }
        return FromDistance(
            DistanceToCircularArc(
                point,
                arc.Center,
                basis,
                arc.StartAngle,
                arc.SweepAngle),
            tolerance);
    }

    private static CadPointHitResult HitPolyline2D(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        CadPoint3D point,
        double tolerance)
    {
        CadPolylinePrimitive polyline = snapshot.Polylines.Span[header.PrimitiveIndex];
        ReadOnlySpan<CadPolylineVertex> vertices = snapshot.PolylineVertices.Span.Slice(
            polyline.VertexOffset,
            polyline.VertexCount);
        if (vertices.Length == 0)
        {
            return FromDistance(double.PositiveInfinity, tolerance);
        }
        if (vertices.Length == 1)
        {
            return FromDistance(
                (point - ToWorld(polyline, vertices[0])).Length,
                tolerance);
        }

        double minimum = double.PositiveInfinity;
        bool hasUnsupportedBulge = false;
        int segmentCount = polyline.IsClosed ? vertices.Length : vertices.Length - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            CadPolylineVertex start = vertices[i];
            CadPolylineVertex end = vertices[(i + 1) % vertices.Length];
            if (start.Bulge != 0.0)
            {
                if (TryDistanceToBulge(
                        point,
                        polyline,
                        start,
                        end,
                        out double bulgeDistance))
                {
                    minimum = Math.Min(minimum, bulgeDistance);
                }
                else
                {
                    hasUnsupportedBulge = true;
                }
                continue;
            }
            minimum = Math.Min(
                minimum,
                DistanceToSegment(
                    point,
                    ToWorld(polyline, start),
                    ToWorld(polyline, end)));
        }
        if (minimum <= tolerance)
        {
            return FromDistance(minimum, tolerance);
        }
        return hasUnsupportedBulge
            ? UnsupportedGeometry()
            : FromDistance(minimum, tolerance);
    }

    private static bool TryDistanceToBulge(
        CadPoint3D point,
        CadPolylinePrimitive polyline,
        CadPolylineVertex start,
        CadPolylineVertex end,
        out double distance)
    {
        double bulge = start.Bulge;
        double deltaX = end.X - start.X;
        double deltaY = end.Y - start.Y;
        double chord = new CadPoint3D(deltaX, deltaY, 0.0).Length;
        if (!double.IsFinite(bulge) || bulge == 0.0 ||
            !double.IsFinite(chord) || chord == 0.0)
        {
            distance = double.NaN;
            return false;
        }

        double inverseBulge = 1.0 / bulge;
        double centerOffset = (chord * 0.25) * (inverseBulge - bulge);
        double localRadius = (chord * 0.25) *
            (Math.Abs(bulge) + Math.Abs(inverseBulge));
        if (!double.IsFinite(centerOffset) || !double.IsFinite(localRadius) ||
            !TryGetCircularBasis(
                polyline.CoordinateSystem,
                localRadius,
                out CircularBasis basis))
        {
            distance = double.NaN;
            return false;
        }

        double centerX = (start.X * 0.5) + (end.X * 0.5) -
            ((deltaY / chord) * centerOffset);
        double centerY = (start.Y * 0.5) + (end.Y * 0.5) +
            ((deltaX / chord) * centerOffset);
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY))
        {
            distance = double.NaN;
            return false;
        }
        CadPoint3D center = ToWorld(polyline, centerX, centerY);
        double startAngle = Math.Atan2(start.Y - centerY, start.X - centerX);
        double sweep = 4.0 * Math.Atan(bulge);
        distance = DistanceToCircularArc(
            point,
            center,
            basis,
            startAngle,
            sweep);
        return double.IsFinite(distance);
    }

    private static CadPointHitResult HitPolyline3D(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        CadPoint3D point,
        double tolerance)
    {
        CadPolyline3DPrimitive polyline = snapshot.Polylines3D.Span[header.PrimitiveIndex];
        ReadOnlySpan<CadPoint3D> points = snapshot.Polyline3DPoints.Span.Slice(
            polyline.PointOffset,
            polyline.PointCount);
        if (points.Length == 0)
        {
            return FromDistance(double.PositiveInfinity, tolerance);
        }
        if (points.Length == 1)
        {
            return FromDistance((point - points[0]).Length, tolerance);
        }

        double minimum = double.PositiveInfinity;
        int segmentCount = polyline.IsClosed ? points.Length : points.Length - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            minimum = Math.Min(
                minimum,
                DistanceToSegment(
                    point,
                    points[i],
                    points[(i + 1) % points.Length]));
        }
        return FromDistance(minimum, tolerance);
    }

    private static CadPointHitResult HitSolid(
        CadFacePrimitive face,
        CadPoint3D point,
        double tolerance)
    {
        double distance = DistanceToTriangle(
            point,
            face.First,
            face.Second,
            face.Third);
        if (face.Fourth != face.Third)
        {
            distance = Math.Min(
                distance,
                DistanceToTriangle(
                    point,
                    face.First,
                    face.Third,
                    face.Fourth));
        }
        return FromDistance(distance, tolerance);
    }

    private static CadPointHitResult HitFaceEdges(
        CadFacePrimitive face,
        CadPoint3D point,
        double tolerance)
    {
        double minimum = double.PositiveInfinity;
        IncludeVisibleEdge(ref minimum, point, face.First, face.Second, face.InvisibleEdgeMask, 1);
        IncludeVisibleEdge(ref minimum, point, face.Second, face.Third, face.InvisibleEdgeMask, 2);
        IncludeVisibleEdge(ref minimum, point, face.Third, face.Fourth, face.InvisibleEdgeMask, 4);
        IncludeVisibleEdge(ref minimum, point, face.Fourth, face.First, face.InvisibleEdgeMask, 8);
        return FromDistance(minimum, tolerance);
    }

    private static void IncludeVisibleEdge(
        ref double minimum,
        CadPoint3D point,
        CadPoint3D start,
        CadPoint3D end,
        byte invisibleEdgeMask,
        byte edgeFlag)
    {
        if ((invisibleEdgeMask & edgeFlag) == 0 && start != end)
        {
            minimum = Math.Min(minimum, DistanceToSegment(point, start, end));
        }
    }

    private static CadPoint3D ToWorld(
        CadPolylinePrimitive polyline,
        CadPolylineVertex vertex) => ToWorld(polyline, vertex.X, vertex.Y);

    private static CadPoint3D ToWorld(
        CadPolylinePrimitive polyline,
        double x,
        double y) =>
        polyline.WorldOrigin + polyline.CoordinateSystem.Transform(
            new CadPoint3D(x, y, 0.0));

    private static double DistanceToCircularArc(
        CadPoint3D point,
        CadPoint3D center,
        CircularBasis basis,
        double startAngle,
        double sweepAngle)
    {
        CadPoint3D delta = point - center;
        double x = CadPoint3D.Dot(delta, basis.XAxis);
        double y = CadPoint3D.Dot(delta, basis.YAxis);
        double radial = new CadPoint3D(x, y, 0.0).Length;
        double angle = radial == 0.0 ? startAngle : Math.Atan2(y, x);
        if (ContainsAngle(startAngle, sweepAngle, angle))
        {
            double plane = Math.Abs(CadPoint3D.Dot(delta, basis.Normal));
            return new CadPoint3D(
                radial - basis.Radius,
                plane,
                0.0).Length;
        }

        CadPoint3D start = PointOnCircle(center, basis, startAngle);
        CadPoint3D end = PointOnCircle(center, basis, startAngle + sweepAngle);
        return Math.Min((point - start).Length, (point - end).Length);
    }

    private static CadPoint3D PointOnCircle(
        CadPoint3D center,
        CircularBasis basis,
        double angle) =>
        center +
        (basis.XAxis * (basis.Radius * Math.Cos(angle))) +
        (basis.YAxis * (basis.Radius * Math.Sin(angle)));

    private static double DistanceToSegment(
        CadPoint3D point,
        CadPoint3D start,
        CadPoint3D end)
    {
        CadPoint3D segment = end - start;
        double length = segment.Length;
        if (length == 0.0)
        {
            return (point - start).Length;
        }
        CadPoint3D direction = segment / length;
        double projection = CadPoint3D.Dot(point - start, direction);
        if (projection <= 0.0)
        {
            return (point - start).Length;
        }
        if (projection >= length)
        {
            return (point - end).Length;
        }
        return (point - (start + (direction * projection))).Length;
    }

    private static double DistanceToTriangle(
        CadPoint3D point,
        CadPoint3D first,
        CadPoint3D second,
        CadPoint3D third)
    {
        CadPoint3D firstToSecond = second - first;
        CadPoint3D firstToThird = third - first;
        CadPoint3D normal = CadPoint3D.Cross(firstToSecond, firstToThird);
        double normalLength = normal.Length;
        if (!double.IsFinite(normalLength) || normalLength == 0.0)
        {
            return Math.Min(
                DistanceToSegment(point, first, second),
                Math.Min(
                    DistanceToSegment(point, second, third),
                    DistanceToSegment(point, third, first)));
        }

        CadPoint3D unitNormal = normal / normalLength;
        double signedPlaneDistance = CadPoint3D.Dot(point - first, unitNormal);
        if (!double.IsFinite(signedPlaneDistance))
        {
            return double.PositiveInfinity;
        }
        CadPoint3D projected = point - (unitNormal * signedPlaneDistance);
        CadPoint3D fromFirst = projected - first;
        double secondSquared = CadPoint3D.Dot(firstToSecond, firstToSecond);
        double secondThird = CadPoint3D.Dot(firstToSecond, firstToThird);
        double thirdSquared = CadPoint3D.Dot(firstToThird, firstToThird);
        double projectedSecond = CadPoint3D.Dot(fromFirst, firstToSecond);
        double projectedThird = CadPoint3D.Dot(fromFirst, firstToThird);
        double denominator =
            (secondSquared * thirdSquared) - (secondThird * secondThird);
        if (!double.IsFinite(denominator) || denominator <= 0.0)
        {
            return Math.Min(
                DistanceToSegment(point, first, second),
                Math.Min(
                    DistanceToSegment(point, second, third),
                    DistanceToSegment(point, third, first)));
        }
        double secondWeight =
            ((thirdSquared * projectedSecond) - (secondThird * projectedThird)) /
            denominator;
        double thirdWeight =
            ((secondSquared * projectedThird) - (secondThird * projectedSecond)) /
            denominator;
        if (secondWeight >= 0.0 &&
            thirdWeight >= 0.0 &&
            secondWeight + thirdWeight <= 1.0)
        {
            return Math.Abs(signedPlaneDistance);
        }

        return Math.Min(
            DistanceToSegment(point, first, second),
            Math.Min(
                DistanceToSegment(point, second, third),
                DistanceToSegment(point, third, first)));
    }

    private static bool TryGetCircularBasis(
        CadCoordinateSystem coordinateSystem,
        double radius,
        out CircularBasis basis)
    {
        double xLength = coordinateSystem.XAxis.Length;
        double yLength = coordinateSystem.YAxis.Length;
        if (!double.IsFinite(radius) || radius < 0.0 ||
            !double.IsFinite(xLength) || !double.IsFinite(yLength) ||
            xLength == 0.0 || yLength == 0.0)
        {
            basis = default;
            return false;
        }
        CadPoint3D xAxis = coordinateSystem.XAxis / xLength;
        CadPoint3D yAxis = coordinateSystem.YAxis / yLength;
        double scale = Math.Max(xLength, yLength);
        if (Math.Abs(xLength - yLength) > AxisTolerance * scale ||
            Math.Abs(CadPoint3D.Dot(xAxis, yAxis)) > AxisTolerance)
        {
            basis = default;
            return false;
        }
        CadPoint3D normal = CadPoint3D.Cross(xAxis, yAxis);
        double normalLength = normal.Length;
        if (!double.IsFinite(normalLength) || normalLength == 0.0)
        {
            basis = default;
            return false;
        }
        basis = new CircularBasis(
            xAxis,
            yAxis,
            normal / normalLength,
            radius * ((xLength + yLength) * 0.5));
        return double.IsFinite(basis.Radius);
    }

    private static bool ContainsAngle(double start, double sweep, double angle)
    {
        if (Math.Abs(sweep) >= TwoPi)
        {
            return true;
        }
        return sweep >= 0.0
            ? NormalizePositive(angle - start) <= sweep
            : NormalizePositive(start - angle) <= -sweep;
    }

    private static double NormalizePositive(double angle)
    {
        double normalized = angle % TwoPi;
        return normalized < 0.0 ? normalized + TwoPi : normalized;
    }

    private static CadPointHitResult FromDistance(double distance, double tolerance) =>
        new(
            distance <= tolerance ? CadPointHitStatus.Hit : CadPointHitStatus.Miss,
            distance);

    private static CadPointHitResult UnsupportedGeometry() =>
        new(CadPointHitStatus.UnsupportedGeometry, double.NaN);

    private static bool AreFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);

    private readonly record struct CircularBasis(
        CadPoint3D XAxis,
        CadPoint3D YAxis,
        CadPoint3D Normal,
        double Radius);
}
