namespace ProGPU.CAD;

/// <summary>Exact top-plane selection for retained HATCH island styles.</summary>
/// <remarks>
/// Point containment evaluates direction-aware half-open ray crossings over the
/// contributing retained line, elliptic-arc, quadratic, and cubic segments in
/// O(S) time and O(1) bounded stack storage. Normal, Outer, and Ignore reuse the
/// immutable contribution decision made during snapshot construction.
/// Patterned point selection evaluates the retained affine row and dash grammar
/// with bounded nearest-row visits. Window selection uses exact analytic
/// bounds. Patterned crossing queries, elliptic outside-proximity, and
/// non-horizontal filled-surface queries return UnsupportedGeometry rather than
/// using a flattened or iterative approximation; polynomial Bezier proximity
/// and crossing remain exact.
/// </remarks>
internal static class CadHatchSelection
{
    private const double AxisTolerance = 1e-10;
    private const int MaximumPatternRowVisits = 4096;

    public static CadPointHitResult HitTestPoint(
        CadDocumentSnapshot snapshot,
        CadHatchPrimitive hatch,
        CadPoint3D point,
        double tolerance)
    {
        if (!TryGetHorizontalPlane(hatch, out double planeZ))
        {
            return UnsupportedPoint();
        }
        double planeDistance = Math.Abs(point.Z - planeZ);
        if (planeDistance > tolerance)
        {
            return new CadPointHitResult(CadPointHitStatus.Miss, planeDistance);
        }
        if (!TryContainsProjected(snapshot, hatch, point.X, point.Y, out bool contains))
        {
            return UnsupportedPoint();
        }
        if (hatch.PatternIndex >= 0)
        {
            return HitTestPatternPoint(
                snapshot,
                hatch,
                point,
                tolerance,
                planeDistance,
                contains);
        }
        if (contains)
        {
            return new CadPointHitResult(CadPointHitStatus.Hit, planeDistance);
        }
        if (tolerance == 0.0)
        {
            return new CadPointHitResult(CadPointHitStatus.Miss, double.PositiveInfinity);
        }

        double minimum = double.PositiveInfinity;
        bool hasUnsupportedCurvedProximity = false;
        Span<CadHomogeneousPoint> bezierControls = stackalloc CadHomogeneousPoint[4];
        ReadOnlySpan<CadHatchLoop> loops = snapshot.HatchLoops.Span.Slice(
            hatch.LoopOffset,
            hatch.LoopCount);
        ReadOnlySpan<CadHatchSegment> segments = snapshot.HatchSegments.Span;
        for (int loopIndex = 0; loopIndex < loops.Length; loopIndex++)
        {
            CadHatchLoop loop = loops[loopIndex];
            if (!loop.ContributesToFill)
            {
                continue;
            }
            int end = checked(loop.SegmentOffset + loop.SegmentCount);
            for (int i = loop.SegmentOffset; i < end; i++)
            {
                CadHatchSegment segment = segments[i];
                if (segment.Kind == CadHatchSegmentKind.Line)
                {
                    minimum = Math.Min(
                        minimum,
                        DistanceToSegment(
                            point,
                            ToWorldPoint(hatch, segment.StartX, segment.StartY),
                            ToWorldPoint(hatch, segment.EndX, segment.EndY)));
                    continue;
                }
                if (segment.Kind is CadHatchSegmentKind.QuadraticBezier or
                    CadHatchSegmentKind.CubicBezier)
                {
                    int degree = FillWorldBezierControls(
                        hatch,
                        segment,
                        bezierControls);
                    if (!CadSplineSelection.TryDistanceToBezier(
                            bezierControls[..(degree + 1)],
                            point,
                            out double distance))
                    {
                        return UnsupportedPoint();
                    }
                    minimum = Math.Min(minimum, distance);
                    continue;
                }
                hasUnsupportedCurvedProximity = true;
            }
        }
        if (minimum <= tolerance)
        {
            return new CadPointHitResult(CadPointHitStatus.Hit, minimum);
        }
        if (hasUnsupportedCurvedProximity)
        {
            return UnsupportedPoint();
        }
        return new CadPointHitResult(CadPointHitStatus.Miss, minimum);
    }

    public static CadBoundsHitResult HitTestBounds(
        CadDocumentSnapshot snapshot,
        CadHatchPrimitive hatch,
        CadBounds3D hatchBounds,
        CadBounds3D selectionBounds,
        CadBoundsSelectionMode mode)
    {
        if (selectionBounds.IsEmpty)
        {
            return BoundsMiss();
        }
        if (mode == CadBoundsSelectionMode.Window)
        {
            return ContainsBounds(selectionBounds, hatchBounds)
                ? BoundsHit()
                : BoundsMiss();
        }
        if (hatch.PatternIndex >= 0)
        {
            return IntersectsBounds(hatchBounds, selectionBounds)
                ? BoundsUnsupported()
                : BoundsMiss();
        }
        if (!TryGetHorizontalPlane(hatch, out double planeZ))
        {
            return BoundsUnsupported();
        }
        if (planeZ < selectionBounds.Min.Z || planeZ > selectionBounds.Max.Z)
        {
            return BoundsMiss();
        }

        ReadOnlySpan<CadHatchLoop> loops = snapshot.HatchLoops.Span.Slice(
            hatch.LoopOffset,
            hatch.LoopCount);
        ReadOnlySpan<CadHatchSegment> segments = snapshot.HatchSegments.Span;
        Span<CadHomogeneousPoint> bezierControls = stackalloc CadHomogeneousPoint[4];
        for (int loopIndex = 0; loopIndex < loops.Length; loopIndex++)
        {
            CadHatchLoop loop = loops[loopIndex];
            if (!loop.ContributesToFill)
            {
                continue;
            }
            int end = checked(loop.SegmentOffset + loop.SegmentCount);
            for (int i = loop.SegmentOffset; i < end; i++)
            {
                CadHatchSegment segment = segments[i];
                bool intersects;
                if (segment.Kind == CadHatchSegmentKind.Line)
                {
                    intersects = CadSelectionHitTester.SegmentIntersectsBounds(
                        ToWorldPoint(hatch, segment.StartX, segment.StartY),
                        ToWorldPoint(hatch, segment.EndX, segment.EndY),
                        selectionBounds);
                }
                else if (segment.Kind is CadHatchSegmentKind.QuadraticBezier or
                    CadHatchSegmentKind.CubicBezier)
                {
                    int degree = FillWorldBezierControls(
                        hatch,
                        segment,
                        bezierControls);
                    if (!CadSplineSelection.TryTestBezierBounds(
                            bezierControls[..(degree + 1)],
                            selectionBounds,
                            CadBoundsSelectionMode.Crossing,
                            out intersects))
                    {
                        return BoundsUnsupported();
                    }
                }
                else if (!CadSelectionHitTester.TryParametricArcIntersectsBounds(
                    ToWorldPoint(hatch, segment.CenterX, segment.CenterY),
                    ToWorldVector(hatch, segment.CosineAxisX, segment.CosineAxisY),
                    ToWorldVector(hatch, segment.SineAxisX, segment.SineAxisY),
                    segment.StartParameter,
                    segment.SweepParameter,
                    selectionBounds,
                    out intersects))
                {
                    return BoundsUnsupported();
                }
                if (intersects)
                {
                    return BoundsHit();
                }
            }
        }

        Span<(double X, double Y)> corners = stackalloc (double X, double Y)[4]
        {
            (selectionBounds.Min.X, selectionBounds.Min.Y),
            (selectionBounds.Max.X, selectionBounds.Min.Y),
            (selectionBounds.Max.X, selectionBounds.Max.Y),
            (selectionBounds.Min.X, selectionBounds.Max.Y),
        };
        for (int i = 0; i < corners.Length; i++)
        {
            if (!TryContainsProjected(
                    snapshot,
                    hatch,
                    corners[i].X,
                    corners[i].Y,
                    out bool contains))
            {
                return BoundsUnsupported();
            }
            if (contains)
            {
                return BoundsHit();
            }
        }
        return BoundsMiss();
    }

    private static int FillWorldBezierControls(
        in CadHatchPrimitive hatch,
        CadHatchSegment segment,
        Span<CadHomogeneousPoint> destination)
    {
        int degree = segment.Kind == CadHatchSegmentKind.QuadraticBezier ? 2 : 3;
        destination[0] = CadHomogeneousPoint.FromCartesian(
            ToWorldPoint(hatch, segment.StartX, segment.StartY),
            1.0);
        destination[1] = CadHomogeneousPoint.FromCartesian(
            ToWorldPoint(hatch, segment.CenterX, segment.CenterY),
            1.0);
        if (degree == 3)
        {
            destination[2] = CadHomogeneousPoint.FromCartesian(
                ToWorldPoint(hatch, segment.CosineAxisX, segment.CosineAxisY),
                1.0);
        }
        destination[degree] = CadHomogeneousPoint.FromCartesian(
            ToWorldPoint(hatch, segment.EndX, segment.EndY),
            1.0);
        return degree;
    }

    private static CadPointHitResult HitTestPatternPoint(
        CadDocumentSnapshot snapshot,
        in CadHatchPrimitive hatch,
        CadPoint3D point,
        double tolerance,
        double planeDistance,
        bool contains)
    {
        if (!contains)
        {
            return tolerance == 0.0
                ? new CadPointHitResult(CadPointHitStatus.Miss, double.PositiveInfinity)
                : UnsupportedPoint();
        }

        CadHatchPattern pattern = snapshot.HatchPatterns.Span[hatch.PatternIndex];
        if (!TryGetLocalCoordinates(hatch, point.X, point.Y, out _, out _))
        {
            return UnsupportedPoint();
        }
        double distance = double.PositiveInfinity;
        double projectedX = 0.0;
        double projectedY = 0.0;
        ReadOnlySpan<CadHatchPatternFamily> families =
            snapshot.HatchPatternFamilies.Span.Slice(
                pattern.FamilyOffset,
                pattern.FamilyCount);
        for (int i = 0; i < families.Length; i++)
        {
            if (!TryGetPatternFamilyDistance(
                    snapshot,
                    hatch,
                    families[i],
                    point.X,
                    point.Y,
                    out double familyDistance,
                    out double familyProjectedX,
                    out double familyProjectedY))
            {
                return UnsupportedPoint();
            }
            if (familyDistance < distance)
            {
                distance = familyDistance;
                projectedX = familyProjectedX;
                projectedY = familyProjectedY;
            }
        }

        double combinedDistance = Math.Sqrt(
            (distance * distance) +
            (planeDistance * planeDistance));
        if (combinedDistance > tolerance)
        {
            return new CadPointHitResult(CadPointHitStatus.Miss, combinedDistance);
        }
        if (!TryContainsProjected(
            snapshot,
            hatch,
            projectedX,
            projectedY,
            out bool projectedInside) || !projectedInside)
        {
            return UnsupportedPoint();
        }
        return new CadPointHitResult(CadPointHitStatus.Hit, combinedDistance);
    }

    private static bool TryGetPatternFamilyDistance(
        CadDocumentSnapshot snapshot,
        in CadHatchPrimitive hatch,
        in CadHatchPatternFamily family,
        double queryX,
        double queryY,
        out double distance,
        out double projectedX,
        out double projectedY)
    {
        double directionX = family.DirectionX;
        double directionY = family.DirectionY;
        double normalX = -directionY;
        double normalY = directionX;
        double worldTangentX =
            (hatch.CoordinateSystem.XAxis.X * directionX) +
            (hatch.CoordinateSystem.YAxis.X * directionY);
        double worldTangentY =
            (hatch.CoordinateSystem.XAxis.Y * directionX) +
            (hatch.CoordinateSystem.YAxis.Y * directionY);
        double worldRowX =
            (hatch.CoordinateSystem.XAxis.X *
                ((family.TangentShift * directionX) + (family.Spacing * normalX))) +
            (hatch.CoordinateSystem.YAxis.X *
                ((family.TangentShift * directionY) + (family.Spacing * normalY)));
        double worldRowY =
            (hatch.CoordinateSystem.XAxis.Y *
                ((family.TangentShift * directionX) + (family.Spacing * normalX))) +
            (hatch.CoordinateSystem.YAxis.Y *
                ((family.TangentShift * directionY) + (family.Spacing * normalY)));
        double tangentLengthSquared =
            (worldTangentX * worldTangentX) + (worldTangentY * worldTangentY);
        double tangentLength = Math.Sqrt(tangentLengthSquared);
        double signedRowSeparation =
            ((worldRowX * -worldTangentY) + (worldRowY * worldTangentX)) /
            tangentLength;
        if (!double.IsFinite(tangentLengthSquared) || tangentLengthSquared <= 0.0 ||
            !double.IsFinite(signedRowSeparation) ||
            Math.Abs(signedRowSeparation) <= AxisTolerance)
        {
            distance = projectedX = projectedY = 0.0;
            return false;
        }

        double baseX = hatch.WorldOrigin.X +
            (hatch.CoordinateSystem.XAxis.X * family.BasePointX) +
            (hatch.CoordinateSystem.YAxis.X * family.BasePointY);
        double baseY = hatch.WorldOrigin.Y +
            (hatch.CoordinateSystem.XAxis.Y * family.BasePointX) +
            (hatch.CoordinateSystem.YAxis.Y * family.BasePointY);
        double queryDeltaX = queryX - baseX;
        double queryDeltaY = queryY - baseY;
        double signedQueryDistance =
            ((queryDeltaX * -worldTangentY) +
             (queryDeltaY * worldTangentX)) / tangentLength;
        double nearestRowValue = Math.Round(
            signedQueryDistance / signedRowSeparation);
        if (!double.IsFinite(nearestRowValue) ||
            nearestRowValue < long.MinValue || nearestRowValue > long.MaxValue)
        {
            distance = projectedX = projectedY = 0.0;
            return false;
        }
        long nearestRow = (long)nearestRowValue;

        distance = double.PositiveInfinity;
        projectedX = projectedY = 0.0;
        int visits = 0;
        for (long radius = 0; ; radius++)
        {
            int candidates = radius == 0 ? 1 : 2;
            for (int side = 0; side < candidates; side++)
            {
                if (++visits > MaximumPatternRowVisits)
                    return false;
                if ((side == 0 && nearestRow > long.MaxValue - radius) ||
                    (side != 0 && nearestRow < long.MinValue + radius))
                    return false;
                long row = nearestRow + (side == 0 ? radius : -radius);
                if (!TryGetPatternRowDistance(
                        snapshot.HatchPatternDashes.Span,
                        family,
                        row,
                        baseX,
                        baseY,
                        worldTangentX,
                        worldTangentY,
                        worldRowX,
                        worldRowY,
                        queryX,
                        queryY,
                        out double candidateDistance,
                        out double candidateX,
                        out double candidateY))
                    return false;
                if (candidateDistance < distance)
                {
                    distance = candidateDistance;
                    projectedX = candidateX;
                    projectedY = candidateY;
                }
            }

            double nextLowerBound =
                Math.Max(0.0, (radius + 0.5) * Math.Abs(signedRowSeparation));
            if (double.IsFinite(distance) && nextLowerBound > distance)
                break;
        }
        double zeroTolerance = AxisTolerance * Math.Max(
            1.0,
            Math.Max(Math.Abs(queryX), Math.Abs(queryY)));
        if (distance <= zeroTolerance)
            distance = 0.0;
        return double.IsFinite(distance) && double.IsFinite(projectedX) &&
            double.IsFinite(projectedY);
    }

    private static bool TryGetPatternRowDistance(
        ReadOnlySpan<double> allDashes,
        in CadHatchPatternFamily family,
        long row,
        double baseX,
        double baseY,
        double tangentX,
        double tangentY,
        double rowX,
        double rowY,
        double queryX,
        double queryY,
        out double distance,
        out double projectedX,
        out double projectedY)
    {
        double originX = baseX + (row * rowX);
        double originY = baseY + (row * rowY);
        double tangentLengthSquared = (tangentX * tangentX) + (tangentY * tangentY);
        double u = (((queryX - originX) * tangentX) +
            ((queryY - originY) * tangentY)) / tangentLengthSquared;
        if (family.DashCount == 0)
        {
            projectedX = originX + (u * tangentX);
            projectedY = originY + (u * tangentY);
            distance = Math.Sqrt(
                ((queryX - projectedX) * (queryX - projectedX)) +
                ((queryY - projectedY) * (queryY - projectedY)));
            return double.IsFinite(distance);
        }

        ReadOnlySpan<double> dashes = allDashes.Slice(
            family.DashOffset,
            family.DashCount);
        double cycle = Math.Floor(u / family.DashPeriod);
        distance = double.PositiveInfinity;
        projectedX = projectedY = 0.0;
        for (int cycleDelta = -1; cycleDelta <= 1; cycleDelta++)
        {
            double cursor = (cycle + cycleDelta) * family.DashPeriod;
            for (int dashIndex = 0; dashIndex < dashes.Length; dashIndex++)
            {
                double dash = dashes[dashIndex];
                double length = Math.Abs(dash);
                if (dash >= 0.0)
                {
                    double candidateU = dash == 0.0
                        ? cursor
                        : Math.Clamp(u, cursor, cursor + length);
                    double candidateX = originX + (candidateU * tangentX);
                    double candidateY = originY + (candidateU * tangentY);
                    double candidateDistance = Math.Sqrt(
                        ((queryX - candidateX) * (queryX - candidateX)) +
                        ((queryY - candidateY) * (queryY - candidateY)));
                    if (candidateDistance < distance)
                    {
                        distance = candidateDistance;
                        projectedX = candidateX;
                        projectedY = candidateY;
                    }
                }
                cursor += length;
            }
        }
        return double.IsFinite(distance);
    }

    private static bool TryGetLocalCoordinates(
        in CadHatchPrimitive hatch,
        double worldX,
        double worldY,
        out double localX,
        out double localY)
    {
        double determinant =
            (hatch.CoordinateSystem.XAxis.X * hatch.CoordinateSystem.YAxis.Y) -
            (hatch.CoordinateSystem.XAxis.Y * hatch.CoordinateSystem.YAxis.X);
        if (!double.IsFinite(determinant) || Math.Abs(determinant) <= AxisTolerance)
        {
            localX = localY = 0.0;
            return false;
        }
        double deltaX = worldX - hatch.WorldOrigin.X;
        double deltaY = worldY - hatch.WorldOrigin.Y;
        localX =
            ((deltaX * hatch.CoordinateSystem.YAxis.Y) -
             (deltaY * hatch.CoordinateSystem.YAxis.X)) / determinant;
        localY =
            ((hatch.CoordinateSystem.XAxis.X * deltaY) -
             (hatch.CoordinateSystem.XAxis.Y * deltaX)) / determinant;
        return double.IsFinite(localX) && double.IsFinite(localY);
    }

    private static bool TryContainsProjected(
        CadDocumentSnapshot snapshot,
        CadHatchPrimitive hatch,
        double queryX,
        double queryY,
        out bool contains)
    {
        if (!TryGetLocalCoordinates(
            hatch,
            queryX,
            queryY,
            out double localX,
            out double localY))
        {
            contains = false;
            return false;
        }
        bool parity = false;
        ReadOnlySpan<CadHatchLoop> loops = snapshot.HatchLoops.Span.Slice(
            hatch.LoopOffset,
            hatch.LoopCount);
        ReadOnlySpan<CadHatchSegment> segments = snapshot.HatchSegments.Span;
        for (int loopIndex = 0; loopIndex < loops.Length; loopIndex++)
        {
            CadHatchLoop loop = loops[loopIndex];
            if (!loop.ContributesToFill)
            {
                continue;
            }
            CadHatchPointContainment classification = CadHatchContainment.Classify(
                segments.Slice(loop.SegmentOffset, loop.SegmentCount),
                localX,
                localY);
            if (classification == CadHatchPointContainment.Unsupported)
            {
                contains = false;
                return false;
            }
            if (classification == CadHatchPointContainment.Boundary)
            {
                contains = true;
                return true;
            }
            if (classification == CadHatchPointContainment.Inside)
            {
                parity = !parity;
            }
        }
        contains = parity;
        return true;
    }

    private static bool TryGetHorizontalPlane(
        CadHatchPrimitive hatch,
        out double planeZ)
    {
        double scale = Math.Max(
            1.0,
            Math.Max(
                hatch.CoordinateSystem.XAxis.Length,
                hatch.CoordinateSystem.YAxis.Length));
        double determinant =
            (hatch.CoordinateSystem.XAxis.X * hatch.CoordinateSystem.YAxis.Y) -
            (hatch.CoordinateSystem.XAxis.Y * hatch.CoordinateSystem.YAxis.X);
        planeZ = hatch.WorldOrigin.Z;
        return Math.Abs(hatch.CoordinateSystem.XAxis.Z) <= AxisTolerance * scale &&
            Math.Abs(hatch.CoordinateSystem.YAxis.Z) <= AxisTolerance * scale &&
            Math.Abs(determinant) > AxisTolerance * scale * scale;
    }

    private static CadPoint3D ToWorldPoint(
        CadHatchPrimitive hatch,
        double x,
        double y) =>
        CadSnapshotCompiler.ToHatchWorldPoint(
            hatch.WorldOrigin,
            hatch.CoordinateSystem,
            x,
            y);

    private static CadPoint3D ToWorldVector(
        CadHatchPrimitive hatch,
        double x,
        double y) =>
        CadSnapshotCompiler.ToHatchWorldVector(
            hatch.CoordinateSystem,
            x,
            y);

    private static double DistanceToSegment(
        CadPoint3D point,
        CadPoint3D start,
        CadPoint3D end)
    {
        CadPoint3D segment = end - start;
        double squaredLength = CadPoint3D.Dot(segment, segment);
        if (!double.IsFinite(squaredLength) || squaredLength <= 0.0)
        {
            return (point - start).Length;
        }
        double amount = Math.Clamp(
            CadPoint3D.Dot(point - start, segment) / squaredLength,
            0.0,
            1.0);
        return (point - (start + (segment * amount))).Length;
    }

    private static bool ContainsBounds(CadBounds3D outer, CadBounds3D inner) =>
        !outer.IsEmpty && !inner.IsEmpty &&
        inner.Min.X >= outer.Min.X && inner.Max.X <= outer.Max.X &&
        inner.Min.Y >= outer.Min.Y && inner.Max.Y <= outer.Max.Y &&
        inner.Min.Z >= outer.Min.Z && inner.Max.Z <= outer.Max.Z;

    private static bool IntersectsBounds(CadBounds3D first, CadBounds3D second) =>
        !first.IsEmpty && !second.IsEmpty &&
        first.Min.X <= second.Max.X && first.Max.X >= second.Min.X &&
        first.Min.Y <= second.Max.Y && first.Max.Y >= second.Min.Y &&
        first.Min.Z <= second.Max.Z && first.Max.Z >= second.Min.Z;

    private static CadPointHitResult UnsupportedPoint() =>
        new(CadPointHitStatus.UnsupportedGeometry, double.NaN);

    private static CadBoundsHitResult BoundsHit() =>
        new(CadBoundsHitStatus.Hit);

    private static CadBoundsHitResult BoundsMiss() =>
        new(CadBoundsHitStatus.Miss);

    private static CadBoundsHitResult BoundsUnsupported() =>
        new(CadBoundsHitStatus.UnsupportedGeometry);
}
