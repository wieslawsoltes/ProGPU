namespace ProGPU.CAD;

/// <summary>Exact retained-geometry selection for positive-weight CAD splines.</summary>
/// <remarks>
/// Each non-empty canonical NURBS knot span is isolated as one rational Bezier.
/// Point distance evaluates every endpoint and every real stationary root of
/// squared distance. Box selection partitions the parameter interval at every
/// real root against the six box planes, so membership is constant between
/// consecutive partitions. Roots are isolated in Bernstein form without curve
/// flattening. For B spans and degree P, ordinary work is O(B * P^2 * R), where
/// R is bounded root-subdivision work; storage is O(P log(1/e)) on the stack.
/// Numerically unresolved clustered roots return UnsupportedGeometry instead of
/// accepting an AABB or tessellation approximation.
/// </remarks>
internal static class CadSplineSelection
{
    private const int MaximumSplineDegree = 10;
    private const int MaximumStationaryDegree = (3 * MaximumSplineDegree) - 1;
    private const int MaximumTangentDegree = (2 * MaximumSplineDegree) - 1;
    private const int MaximumBoxPartitionCount = (6 * MaximumSplineDegree) + 2;
    private const double CoordinateToleranceFactor = 1.4210854715202004e-14;

    /// <summary>
    /// Extracts exact open-spline endpoints from the first and last non-empty
    /// rational Bezier spans without flattening or assuming clamped controls.
    /// </summary>
    internal static bool TryGetEndpoints(
        CadDocumentSnapshot snapshot,
        in CadSplinePrimitive spline,
        out CadPoint3D start,
        out CadPoint3D end)
    {
        start = default;
        end = default;
        if (spline.IsClosed || spline.IsPeriodic ||
            !CadSplineCanonicalizer.TryCreate(
                snapshot,
                spline,
                out CadCanonicalSpline canonical))
        {
            return false;
        }

        Span<CadHomogeneousPoint> controlPoints =
            stackalloc CadHomogeneousPoint[MaximumSplineDegree + 1];
        bool found = false;
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
            if (!CadRationalBezier.TryExtractSpan(canonical, sourceSpan, span))
            {
                return false;
            }
            if (!found)
            {
                start = span[0].Cartesian;
                found = true;
            }
            end = span[^1].Cartesian;
        }
        return found && AreFinite(start) && AreFinite(end);
    }

    /// <summary>
    /// Finds the exact closest point in the WCS XY projection across every
    /// canonical rational-Bezier span, retaining the selected point's WCS Z.
    /// </summary>
    internal static bool TryGetClosestPlanPoint(
        CadDocumentSnapshot snapshot,
        in CadSplinePrimitive spline,
        CadPoint3D point,
        out CadPoint3D closest)
    {
        closest = default;
        if (!CadSplineCanonicalizer.TryCreate(
                snapshot,
                spline,
                out CadCanonicalSpline canonical))
        {
            return false;
        }

        Span<CadHomogeneousPoint> controlPoints =
            stackalloc CadHomogeneousPoint[MaximumSplineDegree + 1];
        double minimumDistance = double.PositiveInfinity;
        CadPoint3D firstPoint = default;
        CadPoint3D lastPoint = default;
        bool hasSpan = false;
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
                !TryClosestPlanPointToBezier(
                    span,
                    point,
                    out CadPoint3D candidate,
                    out double distance))
            {
                return false;
            }
            if (!hasSpan)
            {
                firstPoint = span[0].Cartesian;
                hasSpan = true;
            }
            lastPoint = span[^1].Cartesian;
            if (distance < minimumDistance)
            {
                minimumDistance = distance;
                closest = candidate;
            }
        }
        if (!hasSpan)
        {
            return false;
        }
        if (canonical.HasClosingEdge)
        {
            Span<CadHomogeneousPoint> closing =
                controlPoints[..(canonical.Degree + 1)];
            CadRationalBezier.CreateElevatedLine(lastPoint, firstPoint, closing);
            if (!TryClosestPlanPointToBezier(
                    closing,
                    point,
                    out CadPoint3D candidate,
                    out double distance))
            {
                return false;
            }
            if (distance < minimumDistance)
            {
                closest = candidate;
            }
        }
        return AreFinite(closest);
    }

    public static CadPointHitResult HitTestPoint(
        CadDocumentSnapshot snapshot,
        in CadSplinePrimitive spline,
        CadPoint3D point,
        double tolerance)
    {
        if (!CadSplineCanonicalizer.TryCreate(
                snapshot,
                spline,
                out CadCanonicalSpline canonical))
        {
            return UnsupportedPoint();
        }

        Span<CadHomogeneousPoint> controlPoints =
            stackalloc CadHomogeneousPoint[MaximumSplineDegree + 1];
        double minimumDistance = double.PositiveInfinity;
        CadPoint3D firstPoint = default;
        CadPoint3D lastPoint = default;
        bool hasSpan = false;
        for (int sourceSpan = canonical.Degree;
             sourceSpan < canonical.ControlPointCount;
             sourceSpan++)
        {
            if (!(canonical.GetKnot(sourceSpan + 1) > canonical.GetKnot(sourceSpan)))
            {
                continue;
            }
            Span<CadHomogeneousPoint> span = controlPoints[..(canonical.Degree + 1)];
            if (!CadRationalBezier.TryExtractSpan(canonical, sourceSpan, span) ||
                !TryDistanceToBezier(span, point, out double distance))
            {
                return UnsupportedPoint();
            }

            if (!hasSpan)
            {
                firstPoint = span[0].Cartesian;
                hasSpan = true;
            }
            lastPoint = span[^1].Cartesian;
            minimumDistance = Math.Min(minimumDistance, distance);
        }

        if (!hasSpan)
        {
            return UnsupportedPoint();
        }
        if (canonical.HasClosingEdge)
        {
            Span<CadHomogeneousPoint> closing =
                controlPoints[..(canonical.Degree + 1)];
            CadRationalBezier.CreateElevatedLine(lastPoint, firstPoint, closing);
            if (!TryDistanceToBezier(closing, point, out double distance))
            {
                return UnsupportedPoint();
            }
            minimumDistance = Math.Min(minimumDistance, distance);
        }

        return new CadPointHitResult(
            minimumDistance <= tolerance
                ? CadPointHitStatus.Hit
                : CadPointHitStatus.Miss,
            minimumDistance);
    }

    public static CadBoundsHitResult HitTestBounds(
        CadDocumentSnapshot snapshot,
        in CadSplinePrimitive spline,
        CadBounds3D controlBounds,
        CadBounds3D selectionBounds,
        CadBoundsSelectionMode mode)
    {
        if (selectionBounds.IsEmpty)
        {
            return BoundsMiss();
        }
        if (!CadSplineCanonicalizer.TryCreate(
                snapshot,
                spline,
                out CadCanonicalSpline canonical))
        {
            return BoundsUnsupported();
        }
        if (mode == CadBoundsSelectionMode.Crossing &&
            !controlBounds.Intersects(selectionBounds))
        {
            return BoundsMiss();
        }
        if (ContainsBounds(selectionBounds, controlBounds))
        {
            return BoundsHit();
        }

        Span<CadHomogeneousPoint> controlPoints =
            stackalloc CadHomogeneousPoint[MaximumSplineDegree + 1];
        CadPoint3D firstPoint = default;
        CadPoint3D lastPoint = default;
        bool hasSpan = false;
        bool unresolved = false;
        for (int sourceSpan = canonical.Degree;
             sourceSpan < canonical.ControlPointCount;
             sourceSpan++)
        {
            if (!(canonical.GetKnot(sourceSpan + 1) > canonical.GetKnot(sourceSpan)))
            {
                continue;
            }
            Span<CadHomogeneousPoint> span = controlPoints[..(canonical.Degree + 1)];
            if (!CadRationalBezier.TryExtractSpan(canonical, sourceSpan, span))
            {
                unresolved = true;
                continue;
            }

            if (!hasSpan)
            {
                firstPoint = span[0].Cartesian;
                hasSpan = true;
            }
            lastPoint = span[^1].Cartesian;
            if (!TryTestBezierBounds(span, selectionBounds, mode, out bool hit))
            {
                unresolved = true;
                continue;
            }
            if (mode == CadBoundsSelectionMode.Crossing && hit)
            {
                return BoundsHit();
            }
            if (mode == CadBoundsSelectionMode.Window && !hit)
            {
                return BoundsMiss();
            }
        }

        if (!hasSpan)
        {
            return BoundsUnsupported();
        }
        if (canonical.HasClosingEdge)
        {
            Span<CadHomogeneousPoint> closing =
                controlPoints[..(canonical.Degree + 1)];
            CadRationalBezier.CreateElevatedLine(lastPoint, firstPoint, closing);
            if (!TryTestBezierBounds(closing, selectionBounds, mode, out bool hit))
            {
                unresolved = true;
            }
            else if (mode == CadBoundsSelectionMode.Crossing && hit)
            {
                return BoundsHit();
            }
            else if (mode == CadBoundsSelectionMode.Window && !hit)
            {
                return BoundsMiss();
            }
        }

        if (unresolved)
        {
            return BoundsUnsupported();
        }
        return mode == CadBoundsSelectionMode.Window
            ? BoundsHit()
            : BoundsMiss();
    }

    internal static bool TryDistanceToBezier(
        ReadOnlySpan<CadHomogeneousPoint> points,
        CadPoint3D point,
        out double distance) =>
        TryClosestPointToBezier(
            points,
            point,
            usePlanDistance: false,
            out _,
            out distance);

    internal static bool TryClosestPlanPointToBezier(
        ReadOnlySpan<CadHomogeneousPoint> points,
        CadPoint3D point,
        out CadPoint3D closest,
        out double distance) =>
        TryClosestPointToBezier(
            points,
            point,
            usePlanDistance: true,
            out closest,
            out distance);

    /// <summary>
    /// Collects every exact WCS-XY normal-foot candidate on one rational
    /// Bezier span. When every point is normal to the reference, the unique
    /// candidate closest to the cursor is returned.
    /// </summary>
    internal static bool TryCollectPlanPerpendicularPoints(
        ReadOnlySpan<CadHomogeneousPoint> points,
        CadPoint3D referencePoint,
        CadPoint3D queryPoint,
        Span<CadPoint3D> destination,
        out int pointCount)
    {
        pointCount = 0;
        Span<CadHomogeneousPoint> normalizedPoints =
            stackalloc CadHomogeneousPoint[MaximumSplineDegree + 1];
        Span<double> stationary =
            stackalloc double[MaximumStationaryDegree + 1];
        if (!TryBuildStationaryPolynomial(
                points,
                referencePoint,
                usePlanDistance: true,
                normalizedPoints,
                stationary,
                out int degree,
                out _,
                out bool isIdenticallyZero))
        {
            return false;
        }

        if (isIdenticallyZero)
        {
            if (destination.IsEmpty ||
                !TryClosestPlanPointToBezier(
                    points,
                    queryPoint,
                    out destination[0],
                    out _))
            {
                return false;
            }
            pointCount = 1;
            return true;
        }

        int stationaryDegree = (3 * degree) - 1;
        Span<double> roots = stackalloc double[MaximumStationaryDegree];
        if (!CadBernsteinPolynomial.TryCollectRoots(
                stationary[..(stationaryDegree + 1)],
                roots,
                out int rootCount) ||
            rootCount > destination.Length)
        {
            return false;
        }
        for (int index = 0; index < rootCount; index++)
        {
            CadHomogeneousPoint value =
                CadRationalBezier.EvaluateHomogeneous(points, roots[index]);
            if (!double.IsFinite(value.W) || !(value.W > 0.0))
            {
                pointCount = 0;
                return false;
            }
            CadPoint3D candidate = value.Cartesian;
            if (!AreFinite(candidate))
            {
                pointCount = 0;
                return false;
            }
            destination[pointCount++] = candidate;
        }
        return true;
    }

    /// <summary>
    /// Collects every exact WCS-XY tangency candidate from a reference point
    /// to one rational Bezier span. When the complete span is collinear with
    /// the reference, the unique candidate closest to the cursor is returned.
    /// </summary>
    internal static bool TryCollectPlanTangentPoints(
        ReadOnlySpan<CadHomogeneousPoint> points,
        CadPoint3D referencePoint,
        CadPoint3D queryPoint,
        Span<CadPoint3D> destination,
        out int pointCount)
    {
        pointCount = 0;
        int degree = points.Length - 1;
        if (degree < 1 || degree > MaximumSplineDegree)
        {
            return false;
        }

        double coordinateScale = 1.0;
        double weightScale = 0.0;
        Span<CadPoint3D> translated =
            stackalloc CadPoint3D[MaximumSplineDegree + 1];
        for (int index = 0; index < points.Length; index++)
        {
            CadPoint3D cartesian = points[index].Cartesian;
            if (!AreFinite(cartesian) || !double.IsFinite(points[index].W) ||
                !(points[index].W > 0.0))
            {
                return false;
            }
            CadPoint3D delta = cartesian - referencePoint;
            if (!AreFinite(delta))
            {
                return false;
            }
            translated[index] = delta;
            coordinateScale = Math.Max(
                coordinateScale,
                Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y)));
            weightScale = Math.Max(weightScale, points[index].W);
        }
        if (!double.IsFinite(coordinateScale) || !(weightScale > 0.0))
        {
            return false;
        }

        Span<double> x = stackalloc double[MaximumSplineDegree + 1];
        Span<double> y = stackalloc double[MaximumSplineDegree + 1];
        Span<double> derivativeX = stackalloc double[MaximumSplineDegree];
        Span<double> derivativeY = stackalloc double[MaximumSplineDegree];
        for (int index = 0; index <= degree; index++)
        {
            double weight = points[index].W / weightScale;
            x[index] = (translated[index].X / coordinateScale) * weight;
            y[index] = (translated[index].Y / coordinateScale) * weight;
            if (index < degree)
            {
                derivativeX[index] = degree *
                    (((translated[index + 1].X / coordinateScale) *
                      (points[index + 1].W / weightScale)) - x[index]);
                derivativeY[index] = degree *
                    (((translated[index + 1].Y / coordinateScale) *
                      (points[index + 1].W / weightScale)) - y[index]);
            }
        }

        int tangentDegree = (2 * degree) - 1;
        Span<double> firstProduct = stackalloc double[MaximumTangentDegree + 1];
        Span<double> secondProduct = stackalloc double[MaximumTangentDegree + 1];
        Span<double> tangent = stackalloc double[MaximumTangentDegree + 1];
        if (!TryMultiplyBernstein(
                x[..(degree + 1)],
                derivativeY[..degree],
                firstProduct[..(tangentDegree + 1)]) ||
            !TryMultiplyBernstein(
                y[..(degree + 1)],
                derivativeX[..degree],
                secondProduct[..(tangentDegree + 1)]))
        {
            return false;
        }

        double tangentScale = 0.0;
        double tangentReferenceScale = 0.0;
        for (int index = 0; index <= tangentDegree; index++)
        {
            tangent[index] = firstProduct[index] - secondProduct[index];
            tangentScale = Math.Max(tangentScale, Math.Abs(tangent[index]));
            tangentReferenceScale = Math.Max(
                tangentReferenceScale,
                Math.Max(
                    Math.Abs(firstProduct[index]),
                    Math.Abs(secondProduct[index])));
        }
        double tangentTolerance =
            tangentReferenceScale * CoordinateToleranceFactor *
            (degree + 1);
        if (tangentScale <= tangentTolerance)
        {
            if (destination.IsEmpty ||
                !TryClosestPlanPointToBezier(
                    points,
                    queryPoint,
                    out destination[0],
                    out _))
            {
                return false;
            }
            pointCount = 1;
            return true;
        }

        Span<double> roots = stackalloc double[MaximumTangentDegree];
        if (!CadBernsteinPolynomial.TryCollectRoots(
                tangent[..(tangentDegree + 1)],
                roots,
                out int rootCount) ||
            rootCount > destination.Length)
        {
            return false;
        }
        for (int index = 0; index < rootCount; index++)
        {
            CadHomogeneousPoint value =
                CadRationalBezier.EvaluateHomogeneous(points, roots[index]);
            if (!double.IsFinite(value.W) || !(value.W > 0.0))
            {
                pointCount = 0;
                return false;
            }
            CadPoint3D candidate = value.Cartesian;
            if (!AreFinite(candidate))
            {
                pointCount = 0;
                return false;
            }
            destination[pointCount++] = candidate;
        }
        return true;
    }

    private static bool TryClosestPointToBezier(
        ReadOnlySpan<CadHomogeneousPoint> points,
        CadPoint3D point,
        bool usePlanDistance,
        out CadPoint3D closest,
        out double distance)
    {
        closest = default;
        distance = double.PositiveInfinity;
        Span<CadHomogeneousPoint> normalizedPoints =
            stackalloc CadHomogeneousPoint[MaximumSplineDegree + 1];
        Span<double> stationary =
            stackalloc double[MaximumStationaryDegree + 1];
        if (!TryBuildStationaryPolynomial(
                points,
                point,
                usePlanDistance,
                normalizedPoints,
                stationary,
                out int degree,
                out double coordinateScale,
                out _))
        {
            return false;
        }

        int stationaryDegree = (3 * degree) - 1;
        Span<double> roots = stackalloc double[MaximumStationaryDegree];
        if (!CadBernsteinPolynomial.TryCollectRoots(
                stationary[..(stationaryDegree + 1)],
                roots,
                out int rootCount))
        {
            return false;
        }

        double minimumNormalized = double.PositiveInfinity;
        double bestParameter = 0.0;
        if (!TryIncludeDistance(
                normalizedPoints[..(degree + 1)],
                0.0,
                ref minimumNormalized,
                ref bestParameter) ||
            !TryIncludeDistance(
                normalizedPoints[..(degree + 1)],
                1.0,
                ref minimumNormalized,
                ref bestParameter))
        {
            return false;
        }
        for (int i = 0; i < rootCount; i++)
        {
            if (!TryIncludeDistance(
                    normalizedPoints[..(degree + 1)],
                    roots[i],
                    ref minimumNormalized,
                    ref bestParameter))
            {
                return false;
            }
        }

        CadHomogeneousPoint closestValue =
            CadRationalBezier.EvaluateHomogeneous(points, bestParameter);
        if (!double.IsFinite(closestValue.W) || !(closestValue.W > 0.0))
        {
            return false;
        }
        closest = closestValue.Cartesian;
        distance = minimumNormalized * coordinateScale;
        return AreFinite(closest) && !double.IsNaN(distance);
    }

    private static bool TryBuildStationaryPolynomial(
        ReadOnlySpan<CadHomogeneousPoint> points,
        CadPoint3D point,
        bool usePlanDistance,
        Span<CadHomogeneousPoint> normalizedPoints,
        Span<double> stationary,
        out int degree,
        out double coordinateScale,
        out bool isIdenticallyZero)
    {
        degree = points.Length - 1;
        coordinateScale = 1.0;
        isIdenticallyZero = false;
        if (degree < 1 || degree > MaximumSplineDegree ||
            normalizedPoints.Length < points.Length)
        {
            return false;
        }

        double weightScale = 0.0;
        Span<CadPoint3D> translated =
            stackalloc CadPoint3D[MaximumSplineDegree + 1];
        for (int index = 0; index < points.Length; index++)
        {
            CadPoint3D cartesian = points[index].Cartesian;
            if (!AreFinite(cartesian) || !double.IsFinite(points[index].W) ||
                points[index].W <= 0.0)
            {
                return false;
            }
            CadPoint3D delta = cartesian - point;
            translated[index] = usePlanDistance
                ? new CadPoint3D(delta.X, delta.Y, 0.0)
                : delta;
            coordinateScale = Math.Max(
                coordinateScale,
                Math.Max(
                    Math.Abs(translated[index].X),
                    Math.Max(
                        Math.Abs(translated[index].Y),
                        Math.Abs(translated[index].Z))));
            weightScale = Math.Max(weightScale, points[index].W);
        }
        if (!double.IsFinite(coordinateScale) || !(weightScale > 0.0))
        {
            return false;
        }

        Span<double> x = stackalloc double[MaximumSplineDegree + 1];
        Span<double> y = stackalloc double[MaximumSplineDegree + 1];
        Span<double> z = stackalloc double[MaximumSplineDegree + 1];
        Span<double> w = stackalloc double[MaximumSplineDegree + 1];
        for (int index = 0; index <= degree; index++)
        {
            w[index] = points[index].W / weightScale;
            x[index] = (translated[index].X / coordinateScale) * w[index];
            y[index] = (translated[index].Y / coordinateScale) * w[index];
            z[index] = (translated[index].Z / coordinateScale) * w[index];
            normalizedPoints[index] = new CadHomogeneousPoint(
                x[index],
                y[index],
                z[index],
                w[index]);
        }

        int stationaryDegree = (3 * degree) - 1;
        if (stationary.Length < stationaryDegree + 1)
        {
            return false;
        }
        stationary[..(stationaryDegree + 1)].Clear();
        Span<double> axis = stackalloc double[MaximumSplineDegree + 1];
        Span<double> derivativeAxis = stackalloc double[MaximumSplineDegree];
        Span<double> derivativeWeight = stackalloc double[MaximumSplineDegree];
        Span<double> firstProduct = stackalloc double[2 * MaximumSplineDegree];
        Span<double> secondProduct = stackalloc double[2 * MaximumSplineDegree];
        Span<double> derivativeNumerator =
            stackalloc double[2 * MaximumSplineDegree];
        Span<double> axisStationary =
            stackalloc double[MaximumStationaryDegree + 1];
        double stationaryReferenceScale = 0.0;
        for (int index = 0; index < degree; index++)
        {
            derivativeWeight[index] = degree * (w[index + 1] - w[index]);
        }

        int componentCount = usePlanDistance ? 2 : 3;
        for (int component = 0; component < componentCount; component++)
        {
            ReadOnlySpan<double> source = component switch
            {
                0 => x[..(degree + 1)],
                1 => y[..(degree + 1)],
                _ => z[..(degree + 1)],
            };
            source.CopyTo(axis);
            for (int index = 0; index < degree; index++)
            {
                derivativeAxis[index] =
                    degree * (axis[index + 1] - axis[index]);
            }

            if (!TryMultiplyBernstein(
                    derivativeAxis[..degree],
                    w[..(degree + 1)],
                    firstProduct[..(2 * degree)]) ||
                !TryMultiplyBernstein(
                    axis[..(degree + 1)],
                    derivativeWeight[..degree],
                    secondProduct[..(2 * degree)]))
            {
                return false;
            }
            for (int index = 0; index < 2 * degree; index++)
            {
                derivativeNumerator[index] =
                    firstProduct[index] - secondProduct[index];
            }
            if (!TryMultiplyBernstein(
                    axis[..(degree + 1)],
                    derivativeNumerator[..(2 * degree)],
                    axisStationary[..(stationaryDegree + 1)]))
            {
                return false;
            }
            for (int index = 0; index <= stationaryDegree; index++)
            {
                stationaryReferenceScale = Math.Max(
                    stationaryReferenceScale,
                    Math.Abs(axisStationary[index]));
                stationary[index] += axisStationary[index];
            }
        }

        double stationaryScale = 0.0;
        for (int index = 0; index <= stationaryDegree; index++)
        {
            stationaryScale = Math.Max(
                stationaryScale,
                Math.Abs(stationary[index]));
        }
        double stationaryTolerance =
            stationaryReferenceScale * CoordinateToleranceFactor *
            (degree + 1);
        isIdenticallyZero = stationaryScale <= stationaryTolerance;
        return true;
    }

    private static bool TryIncludeDistance(
        ReadOnlySpan<CadHomogeneousPoint> points,
        double parameter,
        ref double minimumNormalized,
        ref double bestParameter)
    {
        CadHomogeneousPoint value = CadRationalBezier.EvaluateHomogeneous(
            points,
            parameter);
        if (!double.IsFinite(value.W) || !(value.W > 0.0))
        {
            return false;
        }
        CadPoint3D normalized = value.Cartesian;
        if (!AreFinite(normalized))
        {
            return false;
        }
        double candidate = normalized.Length;
        if (candidate < minimumNormalized)
        {
            minimumNormalized = candidate;
            bestParameter = parameter;
        }
        return true;
    }

    internal static bool TryTestBezierBounds(
        ReadOnlySpan<CadHomogeneousPoint> points,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode,
        out bool hit)
    {
        int degree = points.Length - 1;
        Span<double> partitions = stackalloc double[MaximumBoxPartitionCount];
        int partitionCount = 0;
        partitions[partitionCount++] = 0.0;
        partitions[partitionCount++] = 1.0;
        Span<double> coefficients = stackalloc double[MaximumSplineDegree + 1];
        Span<double> roots = stackalloc double[MaximumSplineDegree];
        for (int axis = 0; axis < 3; axis++)
        {
            double minimum = Component(bounds.Min, axis);
            double maximum = Component(bounds.Max, axis);
            if (!TryAddPlaneRoots(
                    points,
                    axis,
                    minimum,
                    coefficients[..(degree + 1)],
                    roots[..degree],
                    partitions,
                    ref partitionCount) ||
                !TryAddPlaneRoots(
                    points,
                    axis,
                    maximum,
                    coefficients[..(degree + 1)],
                    roots[..degree],
                    partitions,
                    ref partitionCount))
            {
                hit = false;
                return false;
            }
        }

        InsertionSort(partitions[..partitionCount]);
        bool allInside = true;
        for (int i = 0; i < partitionCount; i++)
        {
            if (!TryEvaluateContained(points, partitions[i], bounds, out bool inside))
            {
                hit = false;
                return false;
            }
            if (inside && mode == CadBoundsSelectionMode.Crossing)
            {
                hit = true;
                return true;
            }
            allInside &= inside;

            if (i + 1 < partitionCount && partitions[i + 1] > partitions[i])
            {
                double midpoint =
                    (partitions[i] * 0.5) + (partitions[i + 1] * 0.5);
                if (!TryEvaluateContained(points, midpoint, bounds, out inside))
                {
                    hit = false;
                    return false;
                }
                if (inside && mode == CadBoundsSelectionMode.Crossing)
                {
                    hit = true;
                    return true;
                }
                allInside &= inside;
            }
        }

        hit = mode == CadBoundsSelectionMode.Window && allInside;
        return true;
    }

    internal static bool TryGetBezierBounds(
        ReadOnlySpan<CadHomogeneousPoint> points,
        out CadBounds3D bounds)
    {
        bounds = default;
        int degree = points.Length - 1;
        if (degree < 1 || degree > MaximumSplineDegree)
        {
            return false;
        }

        double coordinateScale = 1.0;
        double weightScale = 0.0;
        Span<CadPoint3D> cartesian = stackalloc CadPoint3D[MaximumSplineDegree + 1];
        for (int i = 0; i <= degree; i++)
        {
            if (!double.IsFinite(points[i].W) || !(points[i].W > 0.0))
            {
                return false;
            }
            cartesian[i] = points[i].Cartesian;
            if (!AreFinite(cartesian[i]))
            {
                return false;
            }
            coordinateScale = Math.Max(
                coordinateScale,
                Math.Max(
                    Math.Abs(cartesian[i].X),
                    Math.Max(Math.Abs(cartesian[i].Y), Math.Abs(cartesian[i].Z))));
            weightScale = Math.Max(weightScale, points[i].W);
        }
        if (!double.IsFinite(coordinateScale) || !(weightScale > 0.0))
        {
            return false;
        }

        Span<double> weights = stackalloc double[MaximumSplineDegree + 1];
        Span<double> derivativeWeights = stackalloc double[MaximumSplineDegree];
        for (int i = 0; i <= degree; i++)
        {
            weights[i] = points[i].W / weightScale;
        }
        for (int i = 0; i < degree; i++)
        {
            derivativeWeights[i] = degree * (weights[i + 1] - weights[i]);
        }

        bounds = CadBounds3D.FromPoint(cartesian[0]).Include(cartesian[degree]);
        Span<double> axis = stackalloc double[MaximumSplineDegree + 1];
        Span<double> derivativeAxis = stackalloc double[MaximumSplineDegree];
        Span<double> firstProduct = stackalloc double[2 * MaximumSplineDegree];
        Span<double> secondProduct = stackalloc double[2 * MaximumSplineDegree];
        Span<double> derivativeNumerator = stackalloc double[2 * MaximumSplineDegree];
        Span<double> reducedDerivative = stackalloc double[2 * MaximumSplineDegree];
        Span<double> roots = stackalloc double[(2 * MaximumSplineDegree) - 1];
        int elevatedDerivativeDegree = (2 * degree) - 1;
        int derivativeDegree = elevatedDerivativeDegree - 1;
        for (int component = 0; component < 3; component++)
        {
            for (int i = 0; i <= degree; i++)
            {
                axis[i] = (Component(cartesian[i], component) / coordinateScale) * weights[i];
            }
            for (int i = 0; i < degree; i++)
            {
                derivativeAxis[i] = degree * (axis[i + 1] - axis[i]);
            }
            if (!TryMultiplyBernstein(
                    derivativeAxis[..degree],
                    weights[..(degree + 1)],
                    firstProduct[..(elevatedDerivativeDegree + 1)]) ||
                !TryMultiplyBernstein(
                    axis[..(degree + 1)],
                    derivativeWeights[..degree],
                    secondProduct[..(elevatedDerivativeDegree + 1)]))
            {
                bounds = default;
                return false;
            }
            bool hasNonzeroDerivative = false;
            for (int i = 0; i <= elevatedDerivativeDegree; i++)
            {
                derivativeNumerator[i] = firstProduct[i] - secondProduct[i];
            }
            // N'W-NW' has degree at most 2P-2. The two products arrive in
            // degree 2P-1 Bernstein form, so reduce the mathematically
            // cancelled leading degree before root isolation.
            reducedDerivative[0] = derivativeNumerator[0];
            hasNonzeroDerivative = reducedDerivative[0] != 0.0;
            for (int i = 1; i <= derivativeDegree; i++)
            {
                reducedDerivative[i] =
                    ((elevatedDerivativeDegree * derivativeNumerator[i]) -
                     (i * reducedDerivative[i - 1])) /
                    (elevatedDerivativeDegree - i);
                hasNonzeroDerivative |= reducedDerivative[i] != 0.0;
            }
            if (!hasNonzeroDerivative)
            {
                continue;
            }
            if (!CadBernsteinPolynomial.TryCollectRoots(
                    reducedDerivative[..(derivativeDegree + 1)],
                    roots[..derivativeDegree],
                    out int rootCount))
            {
                bounds = default;
                return false;
            }
            for (int i = 0; i < rootCount; i++)
            {
                double parameter = roots[i];
                if (parameter <= 0.0 || parameter >= 1.0)
                {
                    continue;
                }
                CadHomogeneousPoint value = CadRationalBezier.EvaluateHomogeneous(
                    points,
                    parameter);
                if (!double.IsFinite(value.W) || !(value.W > 0.0))
                {
                    bounds = default;
                    return false;
                }
                CadPoint3D point = value.Cartesian;
                if (!AreFinite(point))
                {
                    bounds = default;
                    return false;
                }
                bounds = bounds.Include(point);
            }
        }
        return true;
    }

    private static bool TryAddPlaneRoots(
        ReadOnlySpan<CadHomogeneousPoint> points,
        int axis,
        double boundary,
        Span<double> coefficients,
        Span<double> rootScratch,
        Span<double> partitions,
        ref int partitionCount)
    {
        double coordinateScale = 1.0;
        double weightScale = 0.0;
        Span<double> translated = stackalloc double[MaximumSplineDegree + 1];
        for (int i = 0; i < points.Length; i++)
        {
            double homogeneous = HomogeneousComponent(points[i], axis);
            if (!double.IsFinite(homogeneous) ||
                !double.IsFinite(points[i].W) || points[i].W <= 0.0)
            {
                return false;
            }
            double cartesian = homogeneous / points[i].W;
            if (!double.IsFinite(cartesian))
            {
                return false;
            }
            translated[i] = cartesian - boundary;
            coordinateScale = Math.Max(coordinateScale, Math.Abs(translated[i]));
            weightScale = Math.Max(weightScale, points[i].W);
        }
        if (!double.IsFinite(coordinateScale) || !(weightScale > 0.0))
        {
            return false;
        }
        for (int i = 0; i < points.Length; i++)
        {
            coefficients[i] =
                (translated[i] / coordinateScale) * (points[i].W / weightScale);
        }
        if (!CadBernsteinPolynomial.TryCollectRoots(
                coefficients,
                rootScratch,
                out int rootCount))
        {
            return false;
        }
        for (int i = 0; i < rootCount; i++)
        {
            if (!TryAddPartition(rootScratch[i], partitions, ref partitionCount))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryEvaluateContained(
        ReadOnlySpan<CadHomogeneousPoint> points,
        double parameter,
        CadBounds3D bounds,
        out bool contained)
    {
        CadHomogeneousPoint value = CadRationalBezier.EvaluateHomogeneous(
            points,
            parameter);
        if (!double.IsFinite(value.W) || !(value.W > 0.0))
        {
            contained = false;
            return false;
        }
        CadPoint3D point = value.Cartesian;
        if (!AreFinite(point))
        {
            contained = false;
            return false;
        }
        contained = ContainsPoint(bounds, point);
        return true;
    }

    private static bool TryMultiplyBernstein(
        ReadOnlySpan<double> left,
        ReadOnlySpan<double> right,
        Span<double> destination)
    {
        int leftDegree = left.Length - 1;
        int rightDegree = right.Length - 1;
        int productDegree = leftDegree + rightDegree;
        if (destination.Length < productDegree + 1)
        {
            return false;
        }

        for (int output = 0; output <= productDegree; output++)
        {
            int firstLeft = Math.Max(0, output - rightDegree);
            int lastLeft = Math.Min(leftDegree, output);
            double denominator = Binomial(productDegree, output);
            int firstRight = output - firstLeft;
            double factor =
                (Binomial(leftDegree, firstLeft) *
                 Binomial(rightDegree, firstRight)) /
                denominator;
            double sum = 0.0;
            for (int leftIndex = firstLeft; leftIndex <= lastLeft; leftIndex++)
            {
                int rightIndex = output - leftIndex;
                sum += factor * left[leftIndex] * right[rightIndex];
                if (leftIndex < lastLeft)
                {
                    factor *=
                        ((double)(leftDegree - leftIndex) / (leftIndex + 1)) *
                        ((double)rightIndex / (rightDegree - rightIndex + 1));
                }
            }
            if (!double.IsFinite(sum))
            {
                return false;
            }
            destination[output] = sum;
        }
        return true;
    }

    private static double Binomial(int n, int k)
    {
        k = Math.Min(k, n - k);
        double value = 1.0;
        for (int i = 1; i <= k; i++)
        {
            value *= (double)(n - k + i) / i;
        }
        return value;
    }

    private static bool TryAddPartition(
        double value,
        Span<double> partitions,
        ref int count)
    {
        const double mergeTolerance = 2.842170943040401e-14;
        for (int i = 0; i < count; i++)
        {
            if (Math.Abs(partitions[i] - value) <= mergeTolerance)
            {
                partitions[i] = (partitions[i] * 0.5) + (value * 0.5);
                return true;
            }
        }
        if (count >= partitions.Length)
        {
            return false;
        }
        partitions[count++] = Math.Clamp(value, 0.0, 1.0);
        return true;
    }

    private static void InsertionSort(Span<double> values)
    {
        for (int i = 1; i < values.Length; i++)
        {
            double value = values[i];
            int destination = i;
            while (destination > 0 && values[destination - 1] > value)
            {
                values[destination] = values[destination - 1];
                destination--;
            }
            values[destination] = value;
        }
    }

    private static bool ContainsBounds(CadBounds3D outer, CadBounds3D inner) =>
        !outer.IsEmpty && !inner.IsEmpty &&
        ContainsPoint(outer, inner.Min) &&
        ContainsPoint(outer, inner.Max);

    private static bool ContainsPoint(CadBounds3D bounds, CadPoint3D point) =>
        ContainsCoordinate(point.X, bounds.Min.X, bounds.Max.X) &&
        ContainsCoordinate(point.Y, bounds.Min.Y, bounds.Max.Y) &&
        ContainsCoordinate(point.Z, bounds.Min.Z, bounds.Max.Z);

    private static bool ContainsCoordinate(
        double value,
        double minimum,
        double maximum)
    {
        double tolerance = CoordinateToleranceFactor * Math.Max(
            1.0,
            Math.Max(Math.Abs(value), Math.Max(Math.Abs(minimum), Math.Abs(maximum))));
        return value >= minimum - tolerance && value <= maximum + tolerance;
    }

    private static double HomogeneousComponent(
        CadHomogeneousPoint point,
        int axis) => axis switch
        {
            0 => point.X,
            1 => point.Y,
            _ => point.Z,
        };

    private static double Component(CadPoint3D point, int axis) => axis switch
    {
        0 => point.X,
        1 => point.Y,
        _ => point.Z,
    };

    private static bool AreFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);

    private static CadPointHitResult UnsupportedPoint() =>
        new(CadPointHitStatus.UnsupportedGeometry, double.NaN);

    private static CadBoundsHitResult BoundsHit() =>
        new(CadBoundsHitStatus.Hit);

    private static CadBoundsHitResult BoundsMiss() =>
        new(CadBoundsHitStatus.Miss);

    private static CadBoundsHitResult BoundsUnsupported() =>
        new(CadBoundsHitStatus.UnsupportedGeometry);
}
