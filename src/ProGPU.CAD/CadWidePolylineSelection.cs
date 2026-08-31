namespace ProGPU.CAD;

/// <summary>Exact selection for one retained constant-width 2D polyline.</summary>
/// <remarks>
/// Each source-space line strip and bevel join is tested as transformed
/// triangles. Circular bulge strips retain their two signed-radius rational
/// quadratic boundaries and endpoint cross-sections, so arbitrary affine OCS
/// transforms remain exact. Work is O(S) for S segments with bounded stack
/// storage and no warm-query allocation.
/// </remarks>
internal static class CadWidePolylineSelection
{
    private const double DirectionEpsilon = 1e-12;
    private const double JoinEpsilon = 0.0001;
    private const double HalfPi = Math.PI * 0.5;
    private const double TwoPi = Math.PI * 2.0;

    public static CadPointHitResult HitTestPoint(
        CadDocumentSnapshot snapshot,
        CadPolylinePrimitive polyline,
        CadPoint3D point,
        double tolerance)
    {
        ReadOnlySpan<CadPolylineVertex> vertices = GetVertices(snapshot, polyline);
        if (vertices.Length < 2)
        {
            return new CadPointHitResult(CadPointHitStatus.Miss, double.PositiveInfinity);
        }
        if (!TryCreatePlane(polyline, out PlaneMapping plane))
        {
            return UnsupportedPoint();
        }

        double halfWidth = polyline.ConstantWidth * 0.5;
        double minimum = double.PositiveInfinity;
        int segmentCount = polyline.IsClosed ? vertices.Length : vertices.Length - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            CadPolylineVertex start = vertices[i];
            CadPolylineVertex end = vertices[(i + 1) % vertices.Length];
            if (!TryDistanceToSegmentBody(
                    polyline,
                    plane,
                    start,
                    end,
                    halfWidth,
                    point,
                    out double distance))
            {
                return UnsupportedPoint();
            }
            minimum = Math.Min(minimum, distance);
        }

        int firstJoin = polyline.IsClosed ? 0 : 1;
        int joinEnd = polyline.IsClosed ? vertices.Length : vertices.Length - 1;
        for (int vertexIndex = firstJoin; vertexIndex < joinEnd; vertexIndex++)
        {
            int previousSegment = (vertexIndex + segmentCount - 1) % segmentCount;
            int nextSegment = vertexIndex % segmentCount;
            if (!TryGetSegmentEndDirection(
                    vertices[previousSegment],
                    vertices[(previousSegment + 1) % vertices.Length],
                    out LocalPoint incoming) ||
                !TryGetSegmentStartDirection(
                    vertices[nextSegment],
                    vertices[(nextSegment + 1) % vertices.Length],
                    out LocalPoint outgoing))
            {
                continue;
            }

            if (TryCreateBevelJoin(
                    polyline,
                    vertices[vertexIndex],
                    incoming,
                    outgoing,
                    halfWidth,
                    out CadPoint3D first,
                    out CadPoint3D second,
                    out CadPoint3D third))
            {
                minimum = Math.Min(
                    minimum,
                    CadSelectionHitTester.DistanceToTriangle(
                        point,
                        first,
                        second,
                        third));
            }
        }

        if (double.IsPositiveInfinity(minimum))
        {
            return new CadPointHitResult(CadPointHitStatus.Miss, minimum);
        }
        if (!double.IsFinite(minimum))
        {
            return UnsupportedPoint();
        }
        return new CadPointHitResult(
            minimum <= tolerance ? CadPointHitStatus.Hit : CadPointHitStatus.Miss,
            minimum);
    }

    public static CadBoundsHitResult HitTestBounds(
        CadDocumentSnapshot snapshot,
        CadPolylinePrimitive polyline,
        CadBounds3D bounds)
    {
        ReadOnlySpan<CadPolylineVertex> vertices = GetVertices(snapshot, polyline);
        if (vertices.Length < 2)
        {
            return new CadBoundsHitResult(CadBoundsHitStatus.Miss);
        }
        if (!TryCreatePlane(polyline, out PlaneMapping plane))
        {
            return UnsupportedBounds();
        }

        double halfWidth = polyline.ConstantWidth * 0.5;
        int segmentCount = polyline.IsClosed ? vertices.Length : vertices.Length - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            if (!TrySegmentBodyIntersectsBounds(
                    polyline,
                    plane,
                    vertices[i],
                    vertices[(i + 1) % vertices.Length],
                    halfWidth,
                    bounds,
                    out bool intersects))
            {
                return UnsupportedBounds();
            }
            if (intersects)
            {
                return HitBounds();
            }
        }

        int firstJoin = polyline.IsClosed ? 0 : 1;
        int joinEnd = polyline.IsClosed ? vertices.Length : vertices.Length - 1;
        for (int vertexIndex = firstJoin; vertexIndex < joinEnd; vertexIndex++)
        {
            int previousSegment = (vertexIndex + segmentCount - 1) % segmentCount;
            int nextSegment = vertexIndex % segmentCount;
            if (TryGetSegmentEndDirection(
                    vertices[previousSegment],
                    vertices[(previousSegment + 1) % vertices.Length],
                    out LocalPoint incoming) &&
                TryGetSegmentStartDirection(
                    vertices[nextSegment],
                    vertices[(nextSegment + 1) % vertices.Length],
                    out LocalPoint outgoing) &&
                TryCreateBevelJoin(
                    polyline,
                    vertices[vertexIndex],
                    incoming,
                    outgoing,
                    halfWidth,
                    out CadPoint3D first,
                    out CadPoint3D second,
                    out CadPoint3D third) &&
                CadSelectionHitTester.TriangleIntersectsBounds(
                    first,
                    second,
                    third,
                    bounds))
            {
                return HitBounds();
            }
        }

        return new CadBoundsHitResult(CadBoundsHitStatus.Miss);
    }

    private static bool TryDistanceToSegmentBody(
        CadPolylinePrimitive polyline,
        PlaneMapping plane,
        CadPolylineVertex start,
        CadPolylineVertex end,
        double halfWidth,
        CadPoint3D point,
        out double distance)
    {
        if (start.Bulge == 0.0)
        {
            distance = double.NaN;
            if (Hypot(end.X - start.X, end.Y - start.Y) <= DirectionEpsilon)
            {
                distance = double.PositiveInfinity;
                return true;
            }
            return TryCreateLineStrip(
                polyline,
                start,
                end,
                halfWidth,
                out CadPoint3D first,
                out CadPoint3D second,
                out CadPoint3D third,
                out CadPoint3D fourth) &&
                TryDistanceToQuadrilateral(
                    point,
                    first,
                    second,
                    third,
                    fourth,
                    out distance);
        }

        if (!TryGetArc(start, end, out ArcData arc))
        {
            distance = double.NaN;
            return false;
        }

        double innerRadius = arc.Radius - halfWidth;
        double outerRadius = arc.Radius + halfWidth;
        if (TryProjectToLocal(polyline, plane, point, out LocalPoint local, out double planeDistance) &&
            ContainsArcStrip(arc, innerRadius, outerRadius, local))
        {
            distance = Math.Abs(planeDistance);
            return true;
        }

        distance = double.PositiveInfinity;
        if (!TryDistanceToArcBoundary(polyline, arc, innerRadius, point, ref distance) ||
            !TryDistanceToArcBoundary(polyline, arc, outerRadius, point, ref distance))
        {
            return false;
        }

        CadPoint3D innerStart = ToWorld(polyline, PointOnArc(arc, innerRadius, arc.StartAngle));
        CadPoint3D outerStart = ToWorld(polyline, PointOnArc(arc, outerRadius, arc.StartAngle));
        CadPoint3D innerEnd = ToWorld(polyline, PointOnArc(arc, innerRadius, arc.StartAngle + arc.Sweep));
        CadPoint3D outerEnd = ToWorld(polyline, PointOnArc(arc, outerRadius, arc.StartAngle + arc.Sweep));
        distance = Math.Min(
            distance,
            Math.Min(
                CadSelectionHitTester.DistanceToSegment(point, innerStart, outerStart),
                CadSelectionHitTester.DistanceToSegment(point, innerEnd, outerEnd)));

        return double.IsFinite(distance);
    }

    private static bool TrySegmentBodyIntersectsBounds(
        CadPolylinePrimitive polyline,
        PlaneMapping plane,
        CadPolylineVertex start,
        CadPolylineVertex end,
        double halfWidth,
        CadBounds3D bounds,
        out bool intersects)
    {
        if (start.Bulge == 0.0)
        {
            if (Hypot(end.X - start.X, end.Y - start.Y) <= DirectionEpsilon)
            {
                intersects = false;
                return true;
            }
            if (!TryCreateLineStrip(
                    polyline,
                    start,
                    end,
                    halfWidth,
                    out CadPoint3D first,
                    out CadPoint3D second,
                    out CadPoint3D third,
                    out CadPoint3D fourth))
            {
                intersects = false;
                return false;
            }
            intersects =
                CadSelectionHitTester.TriangleIntersectsBounds(first, second, third, bounds) ||
                CadSelectionHitTester.TriangleIntersectsBounds(first, third, fourth, bounds);
            return true;
        }

        if (!TryGetArc(start, end, out ArcData arc))
        {
            intersects = false;
            return false;
        }
        double innerRadius = arc.Radius - halfWidth;
        double outerRadius = arc.Radius + halfWidth;
        if (PlaneSliceIntersectsArcStrip(
                polyline,
                plane,
                arc,
                innerRadius,
                outerRadius,
                bounds))
        {
            intersects = true;
            return true;
        }
        if (!TryArcBoundaryIntersectsBounds(polyline, arc, innerRadius, bounds, out intersects) ||
            intersects ||
            !TryArcBoundaryIntersectsBounds(polyline, arc, outerRadius, bounds, out intersects) ||
            intersects)
        {
            return intersects;
        }

        CadPoint3D innerStart = ToWorld(polyline, PointOnArc(arc, innerRadius, arc.StartAngle));
        CadPoint3D outerStart = ToWorld(polyline, PointOnArc(arc, outerRadius, arc.StartAngle));
        CadPoint3D innerEnd = ToWorld(polyline, PointOnArc(arc, innerRadius, arc.StartAngle + arc.Sweep));
        CadPoint3D outerEnd = ToWorld(polyline, PointOnArc(arc, outerRadius, arc.StartAngle + arc.Sweep));
        if (CadSelectionHitTester.SegmentIntersectsBounds(innerStart, outerStart, bounds) ||
            CadSelectionHitTester.SegmentIntersectsBounds(innerEnd, outerEnd, bounds))
        {
            intersects = true;
            return true;
        }

        intersects = false;
        return true;
    }

    private static bool TryCreateLineStrip(
        CadPolylinePrimitive polyline,
        CadPolylineVertex start,
        CadPolylineVertex end,
        double halfWidth,
        out CadPoint3D first,
        out CadPoint3D second,
        out CadPoint3D third,
        out CadPoint3D fourth)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double length = Hypot(dx, dy);
        if (!double.IsFinite(length) || length <= DirectionEpsilon)
        {
            first = second = third = fourth = default;
            return false;
        }
        double nx = (-dy / length) * halfWidth;
        double ny = (dx / length) * halfWidth;
        first = ToWorld(polyline, new LocalPoint(start.X + nx, start.Y + ny));
        second = ToWorld(polyline, new LocalPoint(start.X - nx, start.Y - ny));
        third = ToWorld(polyline, new LocalPoint(end.X - nx, end.Y - ny));
        fourth = ToWorld(polyline, new LocalPoint(end.X + nx, end.Y + ny));
        return AreFinite(first) && AreFinite(second) && AreFinite(third) && AreFinite(fourth);
    }

    private static bool TryDistanceToQuadrilateral(
        CadPoint3D point,
        CadPoint3D first,
        CadPoint3D second,
        CadPoint3D third,
        CadPoint3D fourth,
        out double distance)
    {
        distance = Math.Min(
            CadSelectionHitTester.DistanceToTriangle(point, first, second, third),
            CadSelectionHitTester.DistanceToTriangle(point, first, third, fourth));
        return double.IsFinite(distance);
    }

    private static bool TryDistanceToArcBoundary(
        CadPolylinePrimitive polyline,
        ArcData arc,
        double signedRadius,
        CadPoint3D point,
        ref double minimum)
    {
        if (signedRadius == 0.0)
        {
            minimum = Math.Min(
                minimum,
                (point - ToWorld(polyline, arc.Center)).Length);
            return true;
        }
        int spanCount = Math.Max(1, (int)Math.Ceiling(Math.Abs(arc.Sweep) / HalfPi));
        double spanSweep = arc.Sweep / spanCount;
        Span<CadHomogeneousPoint> controls = stackalloc CadHomogeneousPoint[3];
        for (int i = 0; i < spanCount; i++)
        {
            if (!TryCreateArcSpan(
                    polyline,
                    arc,
                    signedRadius,
                    arc.StartAngle + (spanSweep * i),
                    spanSweep,
                    controls) ||
                !CadSplineSelection.TryDistanceToBezier(controls, point, out double distance))
            {
                return false;
            }
            minimum = Math.Min(minimum, distance);
        }
        return true;
    }

    private static bool TryArcBoundaryIntersectsBounds(
        CadPolylinePrimitive polyline,
        ArcData arc,
        double signedRadius,
        CadBounds3D bounds,
        out bool intersects)
    {
        if (signedRadius == 0.0)
        {
            intersects = ContainsPoint(bounds, ToWorld(polyline, arc.Center));
            return true;
        }
        int spanCount = Math.Max(1, (int)Math.Ceiling(Math.Abs(arc.Sweep) / HalfPi));
        double spanSweep = arc.Sweep / spanCount;
        Span<CadHomogeneousPoint> controls = stackalloc CadHomogeneousPoint[3];
        for (int i = 0; i < spanCount; i++)
        {
            if (!TryCreateArcSpan(
                    polyline,
                    arc,
                    signedRadius,
                    arc.StartAngle + (spanSweep * i),
                    spanSweep,
                    controls) ||
                !CadSplineSelection.TryTestBezierBounds(
                    controls,
                    bounds,
                    CadBoundsSelectionMode.Crossing,
                    out bool hit))
            {
                intersects = false;
                return false;
            }
            if (hit)
            {
                intersects = true;
                return true;
            }
        }
        intersects = false;
        return true;
    }

    private static bool TryCreateArcSpan(
        CadPolylinePrimitive polyline,
        ArcData arc,
        double signedRadius,
        double startAngle,
        double sweep,
        Span<CadHomogeneousPoint> controls)
    {
        double endAngle = startAngle + sweep;
        double middleAngle = startAngle + (sweep * 0.5);
        double weight = Math.Cos(sweep * 0.5);
        if (!double.IsFinite(weight) || weight <= 0.0)
        {
            return false;
        }
        CadPoint3D first = ToWorld(polyline, PointOnArc(arc, signedRadius, startAngle));
        CadPoint3D last = ToWorld(polyline, PointOnArc(arc, signedRadius, endAngle));
        LocalPoint middleOnCircle = PointOnArc(arc, signedRadius, middleAngle);
        var middle = new LocalPoint(
            arc.Center.X + ((middleOnCircle.X - arc.Center.X) / weight),
            arc.Center.Y + ((middleOnCircle.Y - arc.Center.Y) / weight));
        CadPoint3D control = ToWorld(polyline, middle);
        if (!AreFinite(first) || !AreFinite(control) || !AreFinite(last))
        {
            return false;
        }
        controls[0] = CadHomogeneousPoint.FromCartesian(first, 1.0);
        controls[1] = CadHomogeneousPoint.FromCartesian(control, weight);
        controls[2] = CadHomogeneousPoint.FromCartesian(last, 1.0);
        return true;
    }

    private static bool PlaneSliceIntersectsArcStrip(
        CadPolylinePrimitive polyline,
        PlaneMapping plane,
        ArcData arc,
        double innerRadius,
        double outerRadius,
        CadBounds3D bounds)
    {
        Span<CadPoint3D> corners = stackalloc CadPoint3D[8];
        WriteBoundsCorners(bounds, corners);
        Span<double> distances = stackalloc double[8];
        double coordinateScale = Math.Max(
            1.0,
            Math.Max(Math.Abs(CadPoint3D.Dot(bounds.Min - polyline.WorldOrigin, plane.Normal)),
                Math.Abs(CadPoint3D.Dot(bounds.Max - polyline.WorldOrigin, plane.Normal))));
        double epsilon = coordinateScale * 1e-12;
        for (int i = 0; i < corners.Length; i++)
        {
            distances[i] = CadPoint3D.Dot(corners[i] - polyline.WorldOrigin, plane.Normal);
            if (Math.Abs(distances[i]) <= epsilon &&
                TryProjectToLocal(polyline, plane, corners[i], out LocalPoint local, out _) &&
                ContainsArcStrip(arc, innerRadius, outerRadius, local))
            {
                return true;
            }
        }

        ReadOnlySpan<byte> edgePairs =
        [0, 1, 0, 2, 0, 4, 1, 3, 1, 5, 2, 3, 2, 6, 3, 7, 4, 5, 4, 6, 5, 7, 6, 7];
        for (int edge = 0; edge < edgePairs.Length; edge += 2)
        {
            int firstIndex = edgePairs[edge];
            int secondIndex = edgePairs[edge + 1];
            double firstDistance = distances[firstIndex];
            double secondDistance = distances[secondIndex];
            if ((firstDistance < -epsilon && secondDistance < -epsilon) ||
                (firstDistance > epsilon && secondDistance > epsilon) ||
                Math.Abs(firstDistance - secondDistance) <= epsilon)
            {
                continue;
            }
            double amount = firstDistance / (firstDistance - secondDistance);
            if (amount < 0.0 || amount > 1.0)
            {
                continue;
            }
            CadPoint3D intersection = corners[firstIndex] +
                ((corners[secondIndex] - corners[firstIndex]) * amount);
            if (TryProjectToLocal(polyline, plane, intersection, out LocalPoint local, out _) &&
                ContainsArcStrip(arc, innerRadius, outerRadius, local))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsArcStrip(
        ArcData arc,
        double innerRadius,
        double outerRadius,
        LocalPoint point)
    {
        double x = point.X - arc.Center.X;
        double y = point.Y - arc.Center.Y;
        double magnitude = Hypot(x, y);
        if (!double.IsFinite(magnitude))
        {
            return false;
        }
        if (magnitude <= DirectionEpsilon)
        {
            return innerRadius <= 0.0 && outerRadius >= 0.0;
        }
        double angle = Math.Atan2(y, x);
        if (magnitude >= innerRadius && magnitude <= outerRadius &&
            ContainsAngle(arc.StartAngle, arc.Sweep, angle))
        {
            return true;
        }
        return -magnitude >= innerRadius && -magnitude <= outerRadius &&
            ContainsAngle(arc.StartAngle, arc.Sweep, angle + Math.PI);
    }

    private static bool TryCreateBevelJoin(
        CadPolylinePrimitive polyline,
        CadPolylineVertex vertex,
        LocalPoint incoming,
        LocalPoint outgoing,
        double halfWidth,
        out CadPoint3D first,
        out CadPoint3D second,
        out CadPoint3D third)
    {
        double turn = (incoming.X * outgoing.Y) - (incoming.Y * outgoing.X);
        if (!double.IsFinite(turn) || Math.Abs(turn) <= JoinEpsilon ||
            polyline.ConstantWidth <= JoinEpsilon)
        {
            first = second = third = default;
            return false;
        }
        double outerSign = turn > 0.0 ? -1.0 : 1.0;
        var join = new LocalPoint(vertex.X, vertex.Y);
        var previousOuter = new LocalPoint(
            join.X + (-incoming.Y * outerSign * halfWidth),
            join.Y + (incoming.X * outerSign * halfWidth));
        var nextOuter = new LocalPoint(
            join.X + (-outgoing.Y * outerSign * halfWidth),
            join.Y + (outgoing.X * outerSign * halfWidth));
        first = ToWorld(polyline, previousOuter);
        second = ToWorld(polyline, join);
        third = ToWorld(polyline, nextOuter);
        return AreFinite(first) && AreFinite(second) && AreFinite(third);
    }

    private static bool TryGetSegmentStartDirection(
        CadPolylineVertex start,
        CadPolylineVertex end,
        out LocalPoint direction)
    {
        if (start.Bulge == 0.0)
        {
            return TryNormalize(end.X - start.X, end.Y - start.Y, out direction);
        }
        if (!TryGetArc(start, end, out ArcData arc))
        {
            direction = default;
            return false;
        }
        return ArcDirection(arc.StartAngle, arc.Sweep, out direction);
    }

    private static bool TryGetSegmentEndDirection(
        CadPolylineVertex start,
        CadPolylineVertex end,
        out LocalPoint direction)
    {
        if (start.Bulge == 0.0)
        {
            return TryNormalize(end.X - start.X, end.Y - start.Y, out direction);
        }
        if (!TryGetArc(start, end, out ArcData arc))
        {
            direction = default;
            return false;
        }
        return ArcDirection(arc.StartAngle + arc.Sweep, arc.Sweep, out direction);
    }

    private static bool ArcDirection(double angle, double sweep, out LocalPoint direction)
    {
        double sign = Math.Sign(sweep);
        direction = new LocalPoint(-Math.Sin(angle) * sign, Math.Cos(angle) * sign);
        return sign != 0.0 && double.IsFinite(direction.X) && double.IsFinite(direction.Y);
    }

    private static bool TryGetArc(
        CadPolylineVertex start,
        CadPolylineVertex end,
        out ArcData arc)
    {
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
            arc = new ArcData(
                new LocalPoint(centerX, centerY),
                radius,
                startAngle,
                sweep);
            return radius > 0.0 && double.IsFinite(sweep) && Math.Abs(sweep) < TwoPi;
        }
        catch (Exception exception) when (exception is ArgumentException or ArithmeticException)
        {
            arc = default;
            return false;
        }
    }

    private static bool TryCreatePlane(
        CadPolylinePrimitive polyline,
        out PlaneMapping plane)
    {
        CadPoint3D xAxis = polyline.CoordinateSystem.XAxis;
        CadPoint3D yAxis = polyline.CoordinateSystem.YAxis;
        CadPoint3D normal = CadPoint3D.Cross(xAxis, yAxis);
        double normalLength = normal.Length;
        double xx = CadPoint3D.Dot(xAxis, xAxis);
        double xy = CadPoint3D.Dot(xAxis, yAxis);
        double yy = CadPoint3D.Dot(yAxis, yAxis);
        double determinant = (xx * yy) - (xy * xy);
        if (!double.IsFinite(normalLength) || normalLength <= 0.0 ||
            !double.IsFinite(determinant) || determinant <= 0.0)
        {
            plane = default;
            return false;
        }
        plane = new PlaneMapping(normal / normalLength, xx, xy, yy, determinant);
        return true;
    }

    private static bool TryProjectToLocal(
        CadPolylinePrimitive polyline,
        PlaneMapping plane,
        CadPoint3D point,
        out LocalPoint local,
        out double planeDistance)
    {
        CadPoint3D delta = point - polyline.WorldOrigin;
        planeDistance = CadPoint3D.Dot(delta, plane.Normal);
        CadPoint3D projected = delta - (plane.Normal * planeDistance);
        double projectedX = CadPoint3D.Dot(projected, polyline.CoordinateSystem.XAxis);
        double projectedY = CadPoint3D.Dot(projected, polyline.CoordinateSystem.YAxis);
        local = new LocalPoint(
            ((projectedX * plane.YY) - (projectedY * plane.XY)) / plane.Determinant,
            ((projectedY * plane.XX) - (projectedX * plane.XY)) / plane.Determinant);
        return double.IsFinite(local.X) && double.IsFinite(local.Y) &&
            double.IsFinite(planeDistance);
    }

    private static bool TryNormalize(double x, double y, out LocalPoint direction)
    {
        double length = Hypot(x, y);
        if (!double.IsFinite(length) || length <= JoinEpsilon)
        {
            direction = default;
            return false;
        }
        direction = new LocalPoint(x / length, y / length);
        return true;
    }

    private static bool ContainsAngle(double start, double sweep, double angle)
    {
        if (sweep >= 0.0)
        {
            return NormalizeAngle(angle - start) <= sweep + DirectionEpsilon;
        }
        return NormalizeAngle(start - angle) <= -sweep + DirectionEpsilon;
    }

    private static double NormalizeAngle(double angle)
    {
        double normalized = angle % TwoPi;
        return normalized < 0.0 ? normalized + TwoPi : normalized;
    }

    private static LocalPoint PointOnArc(ArcData arc, double radius, double angle) =>
        new(
            arc.Center.X + (radius * Math.Cos(angle)),
            arc.Center.Y + (radius * Math.Sin(angle)));

    private static CadPoint3D ToWorld(CadPolylinePrimitive polyline, LocalPoint point) =>
        polyline.WorldOrigin +
        (polyline.CoordinateSystem.XAxis * point.X) +
        (polyline.CoordinateSystem.YAxis * point.Y);

    private static ReadOnlySpan<CadPolylineVertex> GetVertices(
        CadDocumentSnapshot snapshot,
        CadPolylinePrimitive polyline) =>
        snapshot.PolylineVertices.Span.Slice(polyline.VertexOffset, polyline.VertexCount);

    private static void WriteBoundsCorners(CadBounds3D bounds, Span<CadPoint3D> corners)
    {
        corners[0] = new CadPoint3D(bounds.Min.X, bounds.Min.Y, bounds.Min.Z);
        corners[1] = new CadPoint3D(bounds.Max.X, bounds.Min.Y, bounds.Min.Z);
        corners[2] = new CadPoint3D(bounds.Min.X, bounds.Max.Y, bounds.Min.Z);
        corners[3] = new CadPoint3D(bounds.Max.X, bounds.Max.Y, bounds.Min.Z);
        corners[4] = new CadPoint3D(bounds.Min.X, bounds.Min.Y, bounds.Max.Z);
        corners[5] = new CadPoint3D(bounds.Max.X, bounds.Min.Y, bounds.Max.Z);
        corners[6] = new CadPoint3D(bounds.Min.X, bounds.Max.Y, bounds.Max.Z);
        corners[7] = new CadPoint3D(bounds.Max.X, bounds.Max.Y, bounds.Max.Z);
    }

    private static double Hypot(double x, double y)
    {
        double scale = Math.Max(Math.Abs(x), Math.Abs(y));
        if (scale == 0.0)
        {
            return 0.0;
        }
        x /= scale;
        y /= scale;
        return scale * Math.Sqrt((x * x) + (y * y));
    }

    private static bool AreFinite(CadPoint3D point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);

    private static bool ContainsPoint(CadBounds3D bounds, CadPoint3D point) =>
        point.X >= bounds.Min.X && point.X <= bounds.Max.X &&
        point.Y >= bounds.Min.Y && point.Y <= bounds.Max.Y &&
        point.Z >= bounds.Min.Z && point.Z <= bounds.Max.Z;

    private static CadPointHitResult UnsupportedPoint() =>
        new(CadPointHitStatus.UnsupportedGeometry, double.NaN);

    private static CadBoundsHitResult UnsupportedBounds() =>
        new(CadBoundsHitStatus.UnsupportedGeometry);

    private static CadBoundsHitResult HitBounds() =>
        new(CadBoundsHitStatus.Hit);

    private readonly record struct LocalPoint(double X, double Y);

    private readonly record struct ArcData(
        LocalPoint Center,
        double Radius,
        double StartAngle,
        double Sweep);

    private readonly record struct PlaneMapping(
        CadPoint3D Normal,
        double XX,
        double XY,
        double YY,
        double Determinant);
}
