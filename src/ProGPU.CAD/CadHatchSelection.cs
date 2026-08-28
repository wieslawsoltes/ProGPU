namespace ProGPU.CAD;

/// <summary>Exact top-plane selection for retained Normal HATCH fills.</summary>
/// <remarks>
/// Point containment evaluates direction-aware half-open ray crossings over the
/// retained line and elliptic-arc segments in O(S) time and O(1) storage.
/// Patterned point selection evaluates the retained affine row and dash grammar
/// with bounded nearest-row visits. Window selection uses exact analytic
/// bounds. Patterned crossing queries, curved outside-proximity, and
/// non-horizontal filled-surface queries return UnsupportedGeometry rather than
/// using a flattened or iterative approximation.
/// </remarks>
internal static class CadHatchSelection
{
    private const double TwoPi = Math.PI * 2.0;
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
        ReadOnlySpan<CadHatchLoop> loops = snapshot.HatchLoops.Span.Slice(
            hatch.LoopOffset,
            hatch.LoopCount);
        ReadOnlySpan<CadHatchSegment> segments = snapshot.HatchSegments.Span;
        for (int loopIndex = 0; loopIndex < loops.Length; loopIndex++)
        {
            CadHatchLoop loop = loops[loopIndex];
            int end = checked(loop.SegmentOffset + loop.SegmentCount);
            for (int i = loop.SegmentOffset; i < end; i++)
            {
                CadHatchSegment segment = segments[i];
                if (segment.Kind != CadHatchSegmentKind.Line)
                {
                    continue;
                }
                minimum = Math.Min(
                    minimum,
                    DistanceToSegment(
                        point,
                        ToWorldPoint(hatch, segment.StartX, segment.StartY),
                        ToWorldPoint(hatch, segment.EndX, segment.EndY)));
            }
        }
        if (minimum <= tolerance)
        {
            return new CadPointHitResult(CadPointHitStatus.Hit, minimum);
        }
        if (hatch.HasCurvedSegments)
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
        for (int loopIndex = 0; loopIndex < loops.Length; loopIndex++)
        {
            CadHatchLoop loop = loops[loopIndex];
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
        bool parity = false;
        bool boundary = false;
        ReadOnlySpan<CadHatchLoop> loops = snapshot.HatchLoops.Span.Slice(
            hatch.LoopOffset,
            hatch.LoopCount);
        ReadOnlySpan<CadHatchSegment> segments = snapshot.HatchSegments.Span;
        for (int loopIndex = 0; loopIndex < loops.Length; loopIndex++)
        {
            CadHatchLoop loop = loops[loopIndex];
            int end = checked(loop.SegmentOffset + loop.SegmentCount);
            for (int i = loop.SegmentOffset; i < end; i++)
            {
                CadHatchSegment segment = segments[i];
                if (segment.Kind == CadHatchSegmentKind.Line)
                {
                    AccumulateLineCrossing(
                        ToWorldPoint(hatch, segment.StartX, segment.StartY),
                        ToWorldPoint(hatch, segment.EndX, segment.EndY),
                        queryX,
                        queryY,
                        ref parity,
                        ref boundary);
                    continue;
                }
                if (!TryAccumulateArcCrossings(
                    ToWorldPoint(hatch, segment.CenterX, segment.CenterY),
                    ToWorldVector(hatch, segment.CosineAxisX, segment.CosineAxisY),
                    ToWorldVector(hatch, segment.SineAxisX, segment.SineAxisY),
                    segment.StartParameter,
                    segment.SweepParameter,
                    queryX,
                    queryY,
                    ref parity,
                    ref boundary))
                {
                    contains = false;
                    return false;
                }
            }
        }
        contains = boundary || parity;
        return true;
    }

    private static void AccumulateLineCrossing(
        CadPoint3D start,
        CadPoint3D end,
        double queryX,
        double queryY,
        ref bool parity,
        ref bool boundary)
    {
        double scale = Math.Max(
            1.0,
            Math.Max(
                Math.Max(Math.Abs(start.X), Math.Abs(start.Y)),
                Math.Max(
                    Math.Max(Math.Abs(end.X), Math.Abs(end.Y)),
                    Math.Max(Math.Abs(queryX), Math.Abs(queryY)))));
        double tolerance = 1e-12 * scale;
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double cross = ((queryX - start.X) * dy) - ((queryY - start.Y) * dx);
        double dot = ((queryX - start.X) * dx) + ((queryY - start.Y) * dy);
        double squaredLength = (dx * dx) + (dy * dy);
        if (Math.Abs(cross) <= tolerance * Math.Max(1.0, Math.Abs(dx) + Math.Abs(dy)) &&
            dot >= -tolerance && dot <= squaredLength + tolerance)
        {
            boundary = true;
            return;
        }

        bool upward = dy > 0.0;
        bool crosses = upward
            ? queryY >= start.Y && queryY < end.Y
            : dy < 0.0 && queryY > end.Y && queryY <= start.Y;
        if (!crosses)
        {
            return;
        }
        double intersectionX = start.X + ((queryY - start.Y) * dx / dy);
        if (intersectionX > queryX)
        {
            parity = !parity;
        }
    }

    private static bool TryAccumulateArcCrossings(
        CadPoint3D center,
        CadPoint3D cosineAxis,
        CadPoint3D sineAxis,
        double start,
        double sweep,
        double queryX,
        double queryY,
        ref bool parity,
        ref bool boundary)
    {
        double cosineY = cosineAxis.Y;
        double sineY = sineAxis.Y;
        double amplitude = new CadPoint3D(cosineY, sineY, 0.0).Length;
        if (!double.IsFinite(amplitude) || amplitude == 0.0)
        {
            return false;
        }
        double normalized = (queryY - center.Y) / amplitude;
        double tolerance = 1e-12 * Math.Max(1.0, Math.Abs(normalized));
        if (normalized < -1.0 - tolerance || normalized > 1.0 + tolerance)
        {
            return true;
        }

        normalized = Math.Clamp(normalized, -1.0, 1.0);
        double phase = Math.Atan2(sineY, cosineY);
        double delta = Math.Acos(normalized);
        double first = phase + delta;
        double second = phase - delta;
        if (!TryAccumulateArcRoot(
            center,
            cosineAxis,
            sineAxis,
            start,
            sweep,
            first,
            queryX,
            queryY,
            ref parity,
            ref boundary))
        {
            return false;
        }
        if (NormalizePositive(first - second) <= 1e-12 ||
            NormalizePositive(second - first) <= 1e-12)
        {
            return true;
        }
        return TryAccumulateArcRoot(
            center,
            cosineAxis,
            sineAxis,
            start,
            sweep,
            second,
            queryX,
            queryY,
            ref parity,
            ref boundary);
    }

    private static bool TryAccumulateArcRoot(
        CadPoint3D center,
        CadPoint3D cosineAxis,
        CadPoint3D sineAxis,
        double start,
        double sweep,
        double parameter,
        double queryX,
        double queryY,
        ref bool parity,
        ref bool boundary)
    {
        if (!TryGetProgress(parameter, start, sweep, out double progress))
        {
            return true;
        }
        double cosine = Math.Cos(parameter);
        double sine = Math.Sin(parameter);
        double x = center.X + (cosineAxis.X * cosine) + (sineAxis.X * sine);
        double y = center.Y + (cosineAxis.Y * cosine) + (sineAxis.Y * sine);
        double scale = Math.Max(
            1.0,
            Math.Max(
                Math.Max(Math.Abs(x), Math.Abs(y)),
                Math.Max(Math.Abs(queryX), Math.Abs(queryY))));
        double tolerance = 1e-11 * scale;
        if (Math.Abs(x - queryX) <= tolerance && Math.Abs(y - queryY) <= tolerance)
        {
            boundary = true;
            return true;
        }
        double derivativeY =
            (-cosineAxis.Y * sine) + (sineAxis.Y * cosine);
        derivativeY *= Math.CopySign(1.0, sweep);
        if (Math.Abs(derivativeY) <= tolerance)
        {
            return true;
        }
        double span = Math.Min(Math.Abs(sweep), TwoPi);
        if (span >= TwoPi - 1e-12 && progress <= 1e-12 && derivativeY < 0.0)
        {
            // A full closed arc has one geometric endpoint represented at both
            // progress 0 and 2pi. Downward half-open crossings own the end.
            progress = span;
        }
        bool include = derivativeY > 0.0
            ? progress < span - 1e-12
            : progress > 1e-12;
        if (include && x > queryX)
        {
            parity = !parity;
        }
        return true;
    }

    private static bool TryGetProgress(
        double parameter,
        double start,
        double sweep,
        out double progress)
    {
        double span = Math.Min(Math.Abs(sweep), TwoPi);
        progress = sweep >= 0.0
            ? NormalizePositive(parameter - start)
            : NormalizePositive(start - parameter);
        if (progress <= span + 1e-12)
        {
            progress = Math.Clamp(progress, 0.0, span);
            return true;
        }
        return false;
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

    private static double NormalizePositive(double angle)
    {
        double normalized = angle % TwoPi;
        return normalized < 0.0 ? normalized + TwoPi : normalized;
    }

    private static CadPointHitResult UnsupportedPoint() =>
        new(CadPointHitStatus.UnsupportedGeometry, double.NaN);

    private static CadBoundsHitResult BoundsHit() =>
        new(CadBoundsHitStatus.Hit);

    private static CadBoundsHitResult BoundsMiss() =>
        new(CadBoundsHitStatus.Miss);

    private static CadBoundsHitResult BoundsUnsupported() =>
        new(CadBoundsHitStatus.UnsupportedGeometry);
}
