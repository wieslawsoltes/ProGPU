namespace ProGPU.CAD;

/// <summary>One homogeneous control point for positive-weight rational curves.</summary>
internal readonly record struct CadHomogeneousPoint(
    double X,
    double Y,
    double Z,
    double W)
{
    public CadPoint3D Cartesian => new(X / W, Y / W, Z / W);

    public static CadHomogeneousPoint FromCartesian(
        CadPoint3D point,
        double weight) =>
        new(point.X * weight, point.Y * weight, point.Z * weight, weight);

    public static CadHomogeneousPoint Lerp(
        CadHomogeneousPoint start,
        CadHomogeneousPoint end,
        double amount)
    {
        double inverse = 1.0 - amount;
        return new CadHomogeneousPoint(
            (start.X * inverse) + (end.X * amount),
            (start.Y * inverse) + (end.Y * amount),
            (start.Z * inverse) + (end.Z * amount),
            (start.W * inverse) + (end.W * amount));
    }

    public static CadHomogeneousPoint operator +(
        CadHomogeneousPoint left,
        CadHomogeneousPoint right) =>
        new(
            left.X + right.X,
            left.Y + right.Y,
            left.Z + right.Z,
            left.W + right.W);

    public static CadHomogeneousPoint operator -(
        CadHomogeneousPoint left,
        CadHomogeneousPoint right) =>
        new(
            left.X - right.X,
            left.Y - right.Y,
            left.Z - right.Z,
            left.W - right.W);

    public static CadHomogeneousPoint operator *(
        CadHomogeneousPoint value,
        double scale) =>
        new(
            value.X * scale,
            value.Y * scale,
            value.Z * scale,
            value.W * scale);
}

/// <summary>
/// Extracts exact rational Bezier spans from one validated canonical NURBS.
/// </summary>
/// <remarks>
/// For degree P, one extraction retains the bounded two-sided control window,
/// performs at most 2P knot insertions, and costs O(P^2) time with O(P) bounded
/// stack storage. Positive weights remain positive under knot insertion and de
/// Casteljau subdivision. The helper is shared by retained linetype lowering,
/// HATCH boundary compilation, viewport clipping, and geometry selection so
/// all consume the same open, closed, and periodic curve topology.
/// </remarks>
internal static class CadRationalBezier
{
    private const int MaximumDegree = 10;
    private const int MaximumExtractionControlPointCount =
        (MaximumDegree * 4) + 1;
    private const int MaximumExtractionKnotCount =
        (MaximumDegree * 5) + 2;

    public static bool TryExtractSpan(
        in CadCanonicalSpline canonical,
        int sourceSpan,
        Span<CadHomogeneousPoint> destination) =>
        TryExtractSpan<CadCanonicalSpline>(canonical, sourceSpan, destination);

    /// <summary>
    /// Normalizes one homogeneous quadratic Bezier to the shared path contract,
    /// whose endpoint weights are one and whose middle weight remains positive.
    /// </summary>
    public static bool TryGetCanonicalQuadraticWeight(
        ReadOnlySpan<CadHomogeneousPoint> controls,
        out double weight)
    {
        weight = 0.0;
        if (controls.Length != 3 ||
            !HasFinitePositiveWeights(controls))
        {
            return false;
        }

        double logarithm = Math.Log(controls[1].W) -
            (0.5 * (Math.Log(controls[0].W) + Math.Log(controls[2].W)));
        weight = Math.Exp(logarithm);
        float retainedWeight = (float)weight;
        return double.IsFinite(weight) && weight > 0.0 &&
            float.IsFinite(retainedWeight) && retainedWeight > 0.0f;
    }

    /// <summary>
    /// Normalizes one homogeneous cubic Bezier to the shared path contract,
    /// whose endpoint weights are one and whose two interior weights are positive.
    /// </summary>
    public static bool TryGetCanonicalCubicWeights(
        ReadOnlySpan<CadHomogeneousPoint> controls,
        out double weight1,
        out double weight2)
    {
        weight1 = 0.0;
        weight2 = 0.0;
        if (controls.Length != 4 ||
            !HasFinitePositiveWeights(controls))
        {
            return false;
        }

        double logarithm0 = Math.Log(controls[0].W);
        double logarithm3 = Math.Log(controls[3].W);
        weight1 = Math.Exp(
            Math.Log(controls[1].W) -
            ((2.0 / 3.0) * logarithm0) -
            ((1.0 / 3.0) * logarithm3));
        weight2 = Math.Exp(
            Math.Log(controls[2].W) -
            ((1.0 / 3.0) * logarithm0) -
            ((2.0 / 3.0) * logarithm3));
        float retainedWeight1 = (float)weight1;
        float retainedWeight2 = (float)weight2;
        return double.IsFinite(weight1) && weight1 > 0.0 &&
            double.IsFinite(weight2) && weight2 > 0.0 &&
            float.IsFinite(retainedWeight1) && retainedWeight1 > 0.0f &&
            float.IsFinite(retainedWeight2) && retainedWeight2 > 0.0f;
    }

    public static bool TryExtractSpan<TSpline>(
        in TSpline canonical,
        int sourceSpan,
        Span<CadHomogeneousPoint> destination)
        where TSpline : struct, ICadCanonicalSpline
    {
        if (!TryExtractSpanCore(canonical, sourceSpan, destination))
        {
            return false;
        }
        if (canonical.IsPeriodic &&
            sourceSpan == canonical.ControlPointCount - 1)
        {
            Span<CadHomogeneousPoint> firstSpan =
                stackalloc CadHomogeneousPoint[MaximumDegree + 1];
            if (!TryExtractSpanCore(
                    canonical,
                    canonical.Degree,
                    firstSpan[..(canonical.Degree + 1)]))
            {
                return false;
            }
            destination[canonical.Degree] = firstSpan[0];
        }
        return true;
    }

    private static bool TryExtractSpanCore<TSpline>(
        in TSpline canonical,
        int sourceSpan,
        Span<CadHomogeneousPoint> destination)
        where TSpline : struct, ICadCanonicalSpline
    {
        int degree = canonical.Degree;
        if (degree < 1 || degree > MaximumDegree ||
            sourceSpan < degree || sourceSpan >= canonical.ControlPointCount ||
            destination.Length < degree + 1)
        {
            return false;
        }

        double start = canonical.GetKnot(sourceSpan);
        double end = canonical.GetKnot(sourceSpan + 1);
        if (!(end > start))
        {
            return false;
        }

        Span<CadHomogeneousPoint> pointsA =
            stackalloc CadHomogeneousPoint[MaximumExtractionControlPointCount];
        Span<CadHomogeneousPoint> pointsB =
            stackalloc CadHomogeneousPoint[MaximumExtractionControlPointCount];
        Span<double> knotsA = stackalloc double[MaximumExtractionKnotCount];
        Span<double> knotsB = stackalloc double[MaximumExtractionKnotCount];
        int firstControlPoint = sourceSpan - degree;
        int lastControlPoint = Math.Min(
            canonical.ControlPointCount - 1,
            sourceSpan + degree);
        int pointCount = lastControlPoint - firstControlPoint + 1;
        int knotCount = pointCount + degree + 1;
        for (int i = 0; i < pointCount; i++)
        {
            int sourceIndex = firstControlPoint + i;
            pointsA[i] = CadHomogeneousPoint.FromCartesian(
                canonical.GetControlPoint(sourceIndex),
                canonical.GetWeight(sourceIndex));
        }
        for (int i = 0; i < knotCount; i++)
        {
            knotsA[i] = canonical.GetKnot(firstControlPoint + i);
        }

        Span<CadHomogeneousPoint> currentPoints = pointsA;
        Span<CadHomogeneousPoint> nextPoints = pointsB;
        Span<double> currentKnots = knotsA;
        Span<double> nextKnots = knotsB;
        while (CountMultiplicity(currentKnots[..knotCount], start) < degree + 1)
        {
            if (!InsertKnot(
                    currentPoints[..pointCount],
                    currentKnots[..knotCount],
                    degree,
                    start,
                    nextPoints,
                    nextKnots))
            {
                return false;
            }
            pointCount++;
            knotCount++;
            Span<CadHomogeneousPoint> pointSwap = currentPoints;
            currentPoints = nextPoints;
            nextPoints = pointSwap;
            Span<double> knotSwap = currentKnots;
            currentKnots = nextKnots;
            nextKnots = knotSwap;
        }
        while (CountMultiplicity(currentKnots[..knotCount], end) < degree + 1)
        {
            if (!InsertKnot(
                    currentPoints[..pointCount],
                    currentKnots[..knotCount],
                    degree,
                    end,
                    nextPoints,
                    nextKnots))
            {
                return false;
            }
            pointCount++;
            knotCount++;
            Span<CadHomogeneousPoint> pointSwap = currentPoints;
            currentPoints = nextPoints;
            nextPoints = pointSwap;
            Span<double> knotSwap = currentKnots;
            currentKnots = nextKnots;
            nextKnots = knotSwap;
        }

        int isolatedSpan = -1;
        for (int i = degree; i < pointCount; i++)
        {
            if (currentKnots[i] == start && currentKnots[i + 1] == end)
            {
                isolatedSpan = i;
                break;
            }
        }
        if (isolatedSpan < degree)
        {
            return false;
        }

        currentPoints.Slice(isolatedSpan - degree, degree + 1)
            .CopyTo(destination);
        return true;
    }

    public static void CreateElevatedLine(
        CadPoint3D start,
        CadPoint3D end,
        Span<CadHomogeneousPoint> destination)
    {
        int degree = destination.Length - 1;
        if (degree < 1 || degree > MaximumDegree)
        {
            throw new ArgumentOutOfRangeException(nameof(destination));
        }

        for (int i = 0; i <= degree; i++)
        {
            double amount = (double)i / degree;
            destination[i] = CadHomogeneousPoint.FromCartesian(
                start + ((end - start) * amount),
                1.0);
        }
    }

    public static CadHomogeneousPoint EvaluateHomogeneous(
        ReadOnlySpan<CadHomogeneousPoint> points,
        double parameter)
    {
        Span<CadHomogeneousPoint> work =
            stackalloc CadHomogeneousPoint[MaximumDegree + 1];
        points.CopyTo(work);
        for (int remaining = points.Length - 1; remaining > 0; remaining--)
        {
            for (int i = 0; i < remaining; i++)
            {
                work[i] = CadHomogeneousPoint.Lerp(
                    work[i],
                    work[i + 1],
                    parameter);
            }
        }
        return work[0];
    }

    private static bool HasFinitePositiveWeights(
        ReadOnlySpan<CadHomogeneousPoint> controls)
    {
        for (int index = 0; index < controls.Length; index++)
        {
            if (!double.IsFinite(controls[index].W) || controls[index].W <= 0.0)
            {
                return false;
            }
        }
        return true;
    }

    public static void Subdivide(
        ReadOnlySpan<CadHomogeneousPoint> source,
        double parameter,
        Span<CadHomogeneousPoint> left,
        Span<CadHomogeneousPoint> right)
    {
        int degree = source.Length - 1;
        Span<CadHomogeneousPoint> work =
            stackalloc CadHomogeneousPoint[MaximumDegree + 1];
        source.CopyTo(work);
        left[0] = work[0];
        right[degree] = work[degree];
        for (int level = 1; level <= degree; level++)
        {
            for (int i = 0; i <= degree - level; i++)
            {
                work[i] = CadHomogeneousPoint.Lerp(
                    work[i],
                    work[i + 1],
                    parameter);
            }
            left[level] = work[0];
            right[degree - level] = work[degree - level];
        }
    }

    private static bool InsertKnot(
        ReadOnlySpan<CadHomogeneousPoint> points,
        ReadOnlySpan<double> knots,
        int degree,
        double knot,
        Span<CadHomogeneousPoint> outputPoints,
        Span<double> outputKnots)
    {
        int lastControlPoint = points.Length - 1;
        int span = FindInsertionSpan(knots, lastControlPoint, degree, knot);
        int multiplicity = CountMultiplicity(knots, knot);
        if (span < degree || multiplicity > degree)
        {
            return false;
        }

        knots[..(span + 1)].CopyTo(outputKnots);
        outputKnots[span + 1] = knot;
        knots[(span + 1)..].CopyTo(outputKnots[(span + 2)..]);
        points[..(span - degree + 1)].CopyTo(outputPoints);
        points[(span - multiplicity)..]
            .CopyTo(outputPoints[(span - multiplicity + 1)..]);
        for (int i = span - degree + 1; i <= span - multiplicity; i++)
        {
            double denominator = knots[i + degree] - knots[i];
            if (!(denominator > 0.0))
            {
                return false;
            }
            double alpha = (knot - knots[i]) / denominator;
            outputPoints[i] = CadHomogeneousPoint.Lerp(
                points[i - 1],
                points[i],
                alpha);
        }

        return true;
    }

    private static int FindInsertionSpan(
        ReadOnlySpan<double> knots,
        int lastControlPoint,
        int degree,
        double knot)
    {
        if (knot == knots[lastControlPoint + 1])
        {
            return lastControlPoint;
        }
        for (int i = degree; i <= lastControlPoint; i++)
        {
            if (knot >= knots[i] && knot < knots[i + 1])
            {
                return i;
            }
        }
        return -1;
    }

    private static int CountMultiplicity(
        ReadOnlySpan<double> knots,
        double knot)
    {
        int count = 0;
        for (int i = 0; i < knots.Length; i++)
        {
            if (knots[i] == knot)
            {
                count++;
            }
        }
        return count;
    }
}
