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
    private const int MaximumBoxPartitionCount = (6 * MaximumSplineDegree) + 2;
    private const double CoordinateToleranceFactor = 1.4210854715202004e-14;

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
                !TryDistanceToSpan(span, point, out double distance))
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
            if (!TryDistanceToSpan(closing, point, out double distance))
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
            if (!TryTestSpanBounds(span, selectionBounds, mode, out bool hit))
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
            if (!TryTestSpanBounds(closing, selectionBounds, mode, out bool hit))
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

    private static bool TryDistanceToSpan(
        ReadOnlySpan<CadHomogeneousPoint> points,
        CadPoint3D point,
        out double distance)
    {
        distance = double.PositiveInfinity;
        int degree = points.Length - 1;
        double coordinateScale = 1.0;
        double weightScale = 0.0;
        Span<CadPoint3D> translated = stackalloc CadPoint3D[MaximumSplineDegree + 1];
        for (int i = 0; i < points.Length; i++)
        {
            CadPoint3D cartesian = points[i].Cartesian;
            if (!AreFinite(cartesian) || !double.IsFinite(points[i].W) || points[i].W <= 0.0)
            {
                return false;
            }
            translated[i] = cartesian - point;
            coordinateScale = Math.Max(
                coordinateScale,
                Math.Max(
                    Math.Abs(translated[i].X),
                    Math.Max(Math.Abs(translated[i].Y), Math.Abs(translated[i].Z))));
            weightScale = Math.Max(weightScale, points[i].W);
        }
        if (!double.IsFinite(coordinateScale) || !(weightScale > 0.0))
        {
            return false;
        }

        Span<double> x = stackalloc double[MaximumSplineDegree + 1];
        Span<double> y = stackalloc double[MaximumSplineDegree + 1];
        Span<double> z = stackalloc double[MaximumSplineDegree + 1];
        Span<double> w = stackalloc double[MaximumSplineDegree + 1];
        Span<CadHomogeneousPoint> normalizedPoints =
            stackalloc CadHomogeneousPoint[MaximumSplineDegree + 1];
        for (int i = 0; i <= degree; i++)
        {
            w[i] = points[i].W / weightScale;
            x[i] = (translated[i].X / coordinateScale) * w[i];
            y[i] = (translated[i].Y / coordinateScale) * w[i];
            z[i] = (translated[i].Z / coordinateScale) * w[i];
            normalizedPoints[i] = new CadHomogeneousPoint(
                x[i],
                y[i],
                z[i],
                w[i]);
        }

        Span<double> stationary = stackalloc double[MaximumStationaryDegree + 1];
        int stationaryDegree = (3 * degree) - 1;
        stationary[..(stationaryDegree + 1)].Clear();
        Span<double> axis = stackalloc double[MaximumSplineDegree + 1];
        Span<double> derivativeAxis = stackalloc double[MaximumSplineDegree];
        Span<double> derivativeWeight = stackalloc double[MaximumSplineDegree];
        Span<double> firstProduct = stackalloc double[(2 * MaximumSplineDegree)];
        Span<double> secondProduct = stackalloc double[(2 * MaximumSplineDegree)];
        Span<double> derivativeNumerator = stackalloc double[(2 * MaximumSplineDegree)];
        Span<double> axisStationary =
            stackalloc double[MaximumStationaryDegree + 1];
        for (int i = 0; i < degree; i++)
        {
            derivativeWeight[i] = degree * (w[i + 1] - w[i]);
        }

        for (int component = 0; component < 3; component++)
        {
            ReadOnlySpan<double> source = component switch
            {
                0 => x[..(degree + 1)],
                1 => y[..(degree + 1)],
                _ => z[..(degree + 1)],
            };
            source.CopyTo(axis);
            for (int i = 0; i < degree; i++)
            {
                derivativeAxis[i] = degree * (axis[i + 1] - axis[i]);
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
            for (int i = 0; i < 2 * degree; i++)
            {
                derivativeNumerator[i] = firstProduct[i] - secondProduct[i];
            }
            if (!TryMultiplyBernstein(
                    axis[..(degree + 1)],
                    derivativeNumerator[..(2 * degree)],
                    axisStationary[..(stationaryDegree + 1)]))
            {
                return false;
            }
            for (int i = 0; i <= stationaryDegree; i++)
            {
                stationary[i] += axisStationary[i];
            }
        }

        Span<double> roots = stackalloc double[MaximumStationaryDegree];
        if (!CadBernsteinPolynomial.TryCollectRoots(
                stationary[..(stationaryDegree + 1)],
                roots,
                out int rootCount))
        {
            return false;
        }

        double minimumNormalized = double.PositiveInfinity;
        if (!TryIncludeDistance(normalizedPoints[..(degree + 1)], 0.0, ref minimumNormalized) ||
            !TryIncludeDistance(normalizedPoints[..(degree + 1)], 1.0, ref minimumNormalized))
        {
            return false;
        }
        for (int i = 0; i < rootCount; i++)
        {
            if (!TryIncludeDistance(
                    normalizedPoints[..(degree + 1)],
                    roots[i],
                    ref minimumNormalized))
            {
                return false;
            }
        }

        distance = minimumNormalized * coordinateScale;
        return !double.IsNaN(distance);
    }

    private static bool TryIncludeDistance(
        ReadOnlySpan<CadHomogeneousPoint> points,
        double parameter,
        ref double minimumNormalized)
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
        minimumNormalized = Math.Min(
            minimumNormalized,
            normalized.Length);
        return true;
    }

    private static bool TryTestSpanBounds(
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
