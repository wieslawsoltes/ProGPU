using System.Numerics;

namespace ProGPU.CAD;

/// <summary>
/// Measures rational B-spline paths in WCS and emits exact rational Bezier
/// subcurves for visible A-aligned linetype spans.
/// </summary>
/// <remarks>
/// This clean-room implementation follows the public NURBS knot-insertion and
/// homogeneous-coordinate contracts. For B non-empty knot spans, degree P,
/// Q visited linetype descriptors, F output figures, and E emitted Bezier
/// pieces, conversion is O(B * P^2), measurement is O(128 * B * P), pattern
/// traversal is O(Q), and exact subcurve extraction is O(E * P^2). Storage is
/// O(B * P + 128 * B + E * P). The fixed 128-bin, eight-point Gauss-Legendre
/// maps affect only distance-to-parameter inversion; emitted curve geometry is
/// obtained by homogeneous de Casteljau subdivision and is not flattened.
/// Compact periodic records are expanded through <see cref="CadCanonicalSpline"/>.
/// Closed nonperiodic records add one degree-elevated linear Bezier seam. Both
/// forms use the same closed-path pattern planner as other CAD loop geometry.
/// </remarks>
internal static class CadNurbsLineTypeLowerer
{
    private const int ArcLengthBinCount = 128;
    private const int ArcLengthMapSize = ArcLengthBinCount + 1;

    private enum CountStatus : byte
    {
        Success,
        FigureLimitExceeded,
        PatternStepLimitExceeded,
        PlacementLimitExceeded,
    }

    public static bool TryValidate(
        CadDocumentSnapshot snapshot,
        in CadSplinePrimitive spline,
        out int spanCount)
    {
        spanCount = 0;
        if (!CadSplineCanonicalizer.TryCreate(snapshot, spline, out CadCanonicalSpline canonical))
        {
            return false;
        }

        int degree = canonical.Degree;
        int controlPointCount = canonical.ControlPointCount;
        int domainEndIndex = controlPointCount;

        // An internal multiplicity greater than the degree is a geometric
        // discontinuity, not one uninterrupted linetype path.
        int runStart = degree + 1;
        while (runStart < domainEndIndex)
        {
            int runEnd = runStart + 1;
            while (runEnd < domainEndIndex &&
                canonical.GetKnot(runEnd) == canonical.GetKnot(runStart))
            {
                runEnd++;
            }

            if (runEnd - runStart > degree)
            {
                return false;
            }

            runStart = runEnd;
        }

        for (int i = degree; i < controlPointCount; i++)
        {
            if (canonical.GetKnot(i + 1) > canonical.GetKnot(i))
            {
                spanCount++;
            }
        }

        if (canonical.HasClosingEdge)
        {
            spanCount++;
        }

        return spanCount != 0;
    }

    public static CadLineTypeLoweringResult Lower(
        CadDocumentSnapshot snapshot,
        in CadSplinePrimitive spline,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        int maxFigures,
        int maxPatternSteps,
        int maxArcMapsPerEntity,
        int maxPlacements)
    {
        if (!TryValidate(snapshot, spline, out int spanCount))
        {
            return Unsupported();
        }

        if (spanCount > maxArcMapsPerEntity)
        {
            return new CadLineTypeLoweringResult(
                CadLineTypeLoweringStatus.ArcMapLimitExceeded,
                null,
                Matrix4x4.Identity,
                0,
                0,
                spanCount);
        }

        int degree = spline.Degree;
        var bezierPoints = new HomogeneousPoint[checked(spanCount * (degree + 1))];
        var spans = new BezierSpan[spanCount];
        if (!CadSplineCanonicalizer.TryCreate(snapshot, spline, out CadCanonicalSpline canonical) ||
            !TryExtractBezierSpans(canonical, bezierPoints, spans))
        {
            return Unsupported();
        }

        var arcMaps = new double[checked(spanCount * ArcLengthMapSize)];
        double totalLength = 0.0;
        int measuredSpanCount = 0;
        for (int i = 0; i < spans.Length; i++)
        {
            BezierSpan sourceSpan = spans[i];
            double length = BuildArcLengthMap(
                bezierPoints.AsSpan(sourceSpan.ControlPointOffset, degree + 1),
                arcMaps.AsSpan(measuredSpanCount * ArcLengthMapSize, ArcLengthMapSize));
            if (!double.IsFinite(length) || length < 0.0)
            {
                return Unsupported();
            }

            // Constant spans consume neither pattern distance nor a retained
            // output piece, but still count against the source/map preflight.
            if (length == 0.0)
            {
                continue;
            }

            spans[measuredSpanCount] = sourceSpan with
            {
                Length = length,
                PathOffset = totalLength,
                ArcMapOffset = measuredSpanCount * ArcLengthMapSize,
            };
            totalLength += length;
            measuredSpanCount++;
        }

        if (!double.IsFinite(totalLength) || totalLength <= 0.0)
        {
            return new CadLineTypeLoweringResult(
                CadLineTypeLoweringStatus.Continuous,
                null,
                Matrix4x4.Identity,
                0,
                0,
                spanCount);
        }

        ReadOnlySpan<BezierSpan> measuredSpans = spans.AsSpan(0, measuredSpanCount);

        CountStatus countStatus = TryCount(
            totalLength,
            elements,
            patternLength,
            scale,
            canonical.IsLoop,
            maxFigures,
            maxPatternSteps,
            maxPlacements,
            out int figureCount,
            out int placementCount,
            out int patternStepCount);
        if (countStatus != CountStatus.Success)
        {
            return new CadLineTypeLoweringResult(
                countStatus switch
                {
                    CountStatus.FigureLimitExceeded =>
                        CadLineTypeLoweringStatus.FigureLimitExceeded,
                    CountStatus.PlacementLimitExceeded =>
                        CadLineTypeLoweringStatus.PlacementLimitExceeded,
                    _ => CadLineTypeLoweringStatus.PatternStepLimitExceeded,
                },
                null,
                Matrix4x4.Identity,
                figureCount,
                patternStepCount,
                spanCount,
                null,
                placementCount);
        }

        if (figureCount == 0 && placementCount == 0)
        {
            return new CadLineTypeLoweringResult(
                CadLineTypeLoweringStatus.Continuous,
                null,
                Matrix4x4.Identity,
                0,
                patternStepCount,
                spanCount);
        }

        int pieceCount = CountOutputPieces(
            totalLength,
            measuredSpans,
            elements,
            patternLength,
            scale,
            canonical.IsLoop);
        int controlPointCount = checked((pieceCount * degree) + figureCount);
        int knotCount = checked(controlPointCount + (figureCount * (degree + 1)));
        var fragments = new CadLineTypeSplineFragment[figureCount];
        var outputPoints = new Vector2[controlPointCount];
        var outputKnots = new double[knotCount];
        var outputWeights = new double[controlPointCount];
        var placements = new CadLineTypePlacement[placementCount];
        FillOutput(
            snapshot.RebaseOrigin,
            totalLength,
            degree,
            measuredSpans,
            bezierPoints,
            arcMaps,
            elements,
            patternLength,
            scale,
            canonical.IsLoop,
            fragments,
            outputPoints,
            outputKnots,
            outputWeights,
            placements);

        return new CadLineTypeLoweringResult(
            CadLineTypeLoweringStatus.Lowered,
            null,
            Matrix4x4.Identity,
            figureCount,
            patternStepCount,
            spanCount,
            placements,
            placementCount,
            fragments,
            outputPoints,
            outputKnots,
            outputWeights);
    }

    private static CadLineTypeLoweringResult Unsupported() =>
        new(
            CadLineTypeLoweringStatus.UnsupportedEntity,
            null,
            Matrix4x4.Identity,
            0,
            0,
            0);

    private static CountStatus TryCount(
        double pathLength,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        bool isClosed,
        int figureLimit,
        int patternStepLimit,
        int placementLimit,
        out int figureCount,
        out int placementCount,
        out int patternStepCount)
    {
        figureCount = 0;
        placementCount = 0;
        var patternSpans = new CadLineTypeLowerer.PatternSpanEnumerator(
            pathLength,
            isClosed,
            elements,
            patternLength,
            scale,
            patternStepLimit);
        while (patternSpans.MoveNext())
        {
            if (patternSpans.Current.IsContent)
            {
                if (++placementCount > placementLimit)
                {
                    patternStepCount = patternSpans.PatternStepCount;
                    return CountStatus.PlacementLimitExceeded;
                }
            }
            else if (++figureCount > figureLimit)
            {
                patternStepCount = patternSpans.PatternStepCount;
                return CountStatus.FigureLimitExceeded;
            }
        }

        patternStepCount = patternSpans.PatternStepCount;
        return patternSpans.PatternStepLimitExceeded
            ? CountStatus.PatternStepLimitExceeded
            : CountStatus.Success;
    }

    private static int CountOutputPieces(
        double pathLength,
        ReadOnlySpan<BezierSpan> spans,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        bool isClosed)
    {
        int count = 0;
        var patternSpans = new CadLineTypeLowerer.PatternSpanEnumerator(
            pathLength,
            isClosed,
            elements,
            patternLength,
            scale,
            int.MaxValue);
        while (patternSpans.MoveNext())
        {
            CadLineTypeLowerer.PatternSpan current = patternSpans.Current;
            if (current.IsContent)
            {
                continue;
            }

            if (current.IsPoint)
            {
                count = checked(count + 1);
                continue;
            }

            int first = FindSpan(spans, current.Start, preferNextAtBoundary: true);
            int last = FindSpan(spans, current.End, preferNextAtBoundary: false);
            count = checked(count + last - first + 1);
        }

        return count;
    }

    private static void FillOutput(
        CadPoint3D rebaseOrigin,
        double pathLength,
        int degree,
        ReadOnlySpan<BezierSpan> spans,
        ReadOnlySpan<HomogeneousPoint> bezierPoints,
        ReadOnlySpan<double> arcMaps,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        bool isClosed,
        Span<CadLineTypeSplineFragment> fragments,
        Span<Vector2> outputPoints,
        Span<double> outputKnots,
        Span<double> outputWeights,
        Span<CadLineTypePlacement> placements)
    {
        var scratch = new HomogeneousPoint[checked((degree + 1) * 3)];
        int fragmentIndex = 0;
        int pointIndex = 0;
        int knotIndex = 0;
        int placementIndex = 0;
        var patternSpans = new CadLineTypeLowerer.PatternSpanEnumerator(
            pathLength,
            isClosed,
            elements,
            patternLength,
            scale,
            int.MaxValue);
        while (patternSpans.MoveNext())
        {
            CadLineTypeLowerer.PatternSpan current = patternSpans.Current;
            if (current.IsContent)
            {
                int spanIndex = FindSpan(spans, current.Start, preferNextAtBoundary: true);
                ReadOnlySpan<HomogeneousPoint> controlPoints = bezierPoints.Slice(
                    spans[spanIndex].ControlPointOffset,
                    degree + 1);
                double t = ParameterAtDistance(
                    spans[spanIndex],
                    current.Start,
                    arcMaps,
                    controlPoints);
                CadPoint3D point = EvaluatePoint(controlPoints, t);
                CadPoint3D tangent = EvaluateDerivative(controlPoints, t);
                placements[placementIndex++] = new CadLineTypePlacement(
                    current.ElementIndex,
                    Project(point, rebaseOrigin),
                    ToVector2(tangent));
                continue;
            }

            double start = current.Start;
            double end = current.IsPoint ? current.Start : current.End;
            int firstSpan = FindSpan(spans, start, preferNextAtBoundary: true);
            int lastSpan = current.IsPoint
                ? firstSpan
                : FindSpan(spans, end, preferNextAtBoundary: false);
            int pieceCount = lastSpan - firstSpan + 1;
            int fragmentPointOffset = pointIndex;
            int fragmentKnotOffset = knotIndex;
            for (int spanIndex = firstSpan; spanIndex <= lastSpan; spanIndex++)
            {
                BezierSpan span = spans[spanIndex];
                double localStart = spanIndex == firstSpan
                    ? Math.Clamp(start - span.PathOffset, 0.0, span.Length)
                    : 0.0;
                double localEnd = spanIndex == lastSpan
                    ? Math.Clamp(end - span.PathOffset, 0.0, span.Length)
                    : span.Length;
                ReadOnlySpan<HomogeneousPoint> spanPoints = bezierPoints.Slice(
                    span.ControlPointOffset,
                    degree + 1);
                double t0 = InvertArcDistance(span, localStart, arcMaps, spanPoints);
                double t1 = current.IsPoint
                    ? t0
                    : InvertArcDistance(span, localEnd, arcMaps, spanPoints);
                Span<HomogeneousPoint> piece = scratch.AsSpan(0, degree + 1);
                ExtractSubcurve(
                    bezierPoints.Slice(span.ControlPointOffset, degree + 1),
                    t0,
                    t1,
                    piece,
                    scratch.AsSpan(degree + 1, degree + 1),
                    scratch.AsSpan((degree + 1) * 2, degree + 1));
                int sourceStart = spanIndex == firstSpan ? 0 : 1;
                for (int i = sourceStart; i <= degree; i++)
                {
                    HomogeneousPoint value = piece[i];
                    CadPoint3D point = value.Cartesian;
                    outputPoints[pointIndex] = Project(point, rebaseOrigin);
                    outputWeights[pointIndex] = value.W;
                    pointIndex++;
                }
            }

            for (int i = 0; i <= degree; i++)
            {
                outputKnots[knotIndex++] = 0.0;
            }
            for (int piece = 1; piece < pieceCount; piece++)
            {
                for (int i = 0; i < degree; i++)
                {
                    outputKnots[knotIndex++] = piece;
                }
            }
            for (int i = 0; i <= degree; i++)
            {
                outputKnots[knotIndex++] = pieceCount;
            }

            int fragmentPointCount = (pieceCount * degree) + 1;
            int fragmentKnotCount = fragmentPointCount + degree + 1;
            fragments[fragmentIndex++] = new CadLineTypeSplineFragment(
                fragmentPointOffset,
                fragmentPointCount,
                fragmentKnotOffset,
                fragmentKnotCount,
                fragmentPointOffset,
                fragmentPointCount,
                degree);
        }
    }

    private static bool TryExtractBezierSpans(
        in CadCanonicalSpline canonical,
        Span<HomogeneousPoint> destination,
        Span<BezierSpan> spans)
    {
        int degree = canonical.Degree;
        int maxControlPointCount = (degree * 3) + 3;
        int maxKnotCount = (degree * 4) + 4;
        var pointsA = new HomogeneousPoint[maxControlPointCount];
        var pointsB = new HomogeneousPoint[maxControlPointCount];
        var knotsA = new double[maxKnotCount];
        var knotsB = new double[maxKnotCount];
        int outputSpanIndex = 0;
        for (int sourceSpan = degree; sourceSpan < canonical.ControlPointCount; sourceSpan++)
        {
            double start = canonical.GetKnot(sourceSpan);
            double end = canonical.GetKnot(sourceSpan + 1);
            if (!(end > start))
            {
                continue;
            }

            int pointCount = degree + 1;
            int knotCount = (degree * 2) + 2;
            for (int i = 0; i <= degree; i++)
            {
                int sourceIndex = sourceSpan - degree + i;
                double weight = canonical.GetWeight(sourceIndex);
                pointsA[i] = HomogeneousPoint.FromCartesian(
                    canonical.GetControlPoint(sourceIndex),
                    weight);
            }
            for (int i = 0; i < knotCount; i++)
            {
                knotsA[i] = canonical.GetKnot(sourceSpan - degree + i);
            }

            HomogeneousPoint[] currentPoints = pointsA;
            HomogeneousPoint[] nextPoints = pointsB;
            double[] currentKnots = knotsA;
            double[] nextKnots = knotsB;
            while (CountMultiplicity(currentKnots.AsSpan(0, knotCount), start) < degree + 1)
            {
                if (!InsertKnot(
                    currentPoints.AsSpan(0, pointCount),
                    currentKnots.AsSpan(0, knotCount),
                    degree,
                    start,
                    nextPoints,
                    nextKnots))
                {
                    return false;
                }
                pointCount++;
                knotCount++;
                (currentPoints, nextPoints) = (nextPoints, currentPoints);
                (currentKnots, nextKnots) = (nextKnots, currentKnots);
            }
            while (CountMultiplicity(currentKnots.AsSpan(0, knotCount), end) < degree + 1)
            {
                if (!InsertKnot(
                    currentPoints.AsSpan(0, pointCount),
                    currentKnots.AsSpan(0, knotCount),
                    degree,
                    end,
                    nextPoints,
                    nextKnots))
                {
                    return false;
                }
                pointCount++;
                knotCount++;
                (currentPoints, nextPoints) = (nextPoints, currentPoints);
                (currentKnots, nextKnots) = (nextKnots, currentKnots);
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

            int destinationOffset = outputSpanIndex * (degree + 1);
            currentPoints.AsSpan(isolatedSpan - degree, degree + 1)
                .CopyTo(destination.Slice(destinationOffset, degree + 1));
            spans[outputSpanIndex] = new BezierSpan(
                destinationOffset,
                start,
                end,
                0.0,
                0.0,
                outputSpanIndex * ArcLengthMapSize);
            outputSpanIndex++;
        }

        if (canonical.HasClosingEdge && outputSpanIndex != 0)
        {
            CadPoint3D start = destination[0].Cartesian;
            CadPoint3D end = destination[(outputSpanIndex * (degree + 1)) - 1].Cartesian;
            int destinationOffset = outputSpanIndex * (degree + 1);
            for (int i = 0; i <= degree; i++)
            {
                double amount = (double)i / degree;
                destination[destinationOffset + i] = HomogeneousPoint.FromCartesian(
                    end + ((start - end) * amount),
                    1.0);
            }

            spans[outputSpanIndex] = new BezierSpan(
                destinationOffset,
                0.0,
                1.0,
                0.0,
                0.0,
                outputSpanIndex * ArcLengthMapSize);
            outputSpanIndex++;
        }

        return outputSpanIndex == spans.Length;
    }

    private static bool InsertKnot(
        ReadOnlySpan<HomogeneousPoint> points,
        ReadOnlySpan<double> knots,
        int degree,
        double knot,
        Span<HomogeneousPoint> outputPoints,
        Span<double> outputKnots)
    {
        int n = points.Length - 1;
        int span = FindInsertionSpan(knots, n, degree, knot);
        int multiplicity = CountMultiplicity(knots, knot);
        if (span < degree || multiplicity > degree)
        {
            return false;
        }

        knots.Slice(0, span + 1).CopyTo(outputKnots);
        outputKnots[span + 1] = knot;
        knots.Slice(span + 1).CopyTo(outputKnots.Slice(span + 2));
        points.Slice(0, span - degree + 1).CopyTo(outputPoints);
        points.Slice(span - multiplicity).CopyTo(outputPoints.Slice(span - multiplicity + 1));
        for (int i = span - degree + 1; i <= span - multiplicity; i++)
        {
            double denominator = knots[i + degree] - knots[i];
            if (!(denominator > 0.0))
            {
                return false;
            }
            double alpha = (knot - knots[i]) / denominator;
            outputPoints[i] = HomogeneousPoint.Lerp(points[i - 1], points[i], alpha);
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

    private static int CountMultiplicity(ReadOnlySpan<double> knots, double knot)
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

    private static double BuildArcLengthMap(
        ReadOnlySpan<HomogeneousPoint> points,
        Span<double> destination)
    {
        destination[0] = 0.0;
        double cumulative = 0.0;
        for (int i = 0; i < ArcLengthBinCount; i++)
        {
            double start = (double)i / ArcLengthBinCount;
            double end = (double)(i + 1) / ArcLengthBinCount;
            cumulative += IntegrateArcLength(points, start, end);
            destination[i + 1] = cumulative;
        }
        return cumulative;
    }

    private static double IntegrateArcLength(
        ReadOnlySpan<HomogeneousPoint> points,
        double start,
        double end)
    {
        ReadOnlySpan<double> nodes =
        [
            0.1834346424956498,
            0.5255324099163290,
            0.7966664774136267,
            0.9602898564975363,
        ];
        ReadOnlySpan<double> weights =
        [
            0.3626837833783620,
            0.3137066458778873,
            0.2223810344533745,
            0.1012285362903763,
        ];
        double midpoint = (start + end) * 0.5;
        double half = (end - start) * 0.5;
        double sum = 0.0;
        for (int i = 0; i < nodes.Length; i++)
        {
            double delta = half * nodes[i];
            sum += weights[i] *
                (EvaluateDerivative(points, midpoint - delta).Length +
                 EvaluateDerivative(points, midpoint + delta).Length);
        }
        return half * sum;
    }

    private static int FindSpan(
        ReadOnlySpan<BezierSpan> spans,
        double distance,
        bool preferNextAtBoundary)
    {
        int low = 0;
        int high = spans.Length - 1;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            double end = spans[middle].PathOffset + spans[middle].Length;
            if (distance < end || (!preferNextAtBoundary && distance == end))
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }
        return low;
    }

    private static double ParameterAtDistance(
        in BezierSpan span,
        double pathDistance,
        ReadOnlySpan<double> arcMaps,
        ReadOnlySpan<HomogeneousPoint> points) =>
        InvertArcDistance(
            span,
            Math.Clamp(pathDistance - span.PathOffset, 0.0, span.Length),
            arcMaps,
            points);

    private static double InvertArcDistance(
        in BezierSpan span,
        double distance,
        ReadOnlySpan<double> arcMaps,
        ReadOnlySpan<HomogeneousPoint> points)
    {
        if (!(span.Length > 0.0) || distance <= 0.0)
        {
            return 0.0;
        }
        if (distance >= span.Length)
        {
            return 1.0;
        }

        ReadOnlySpan<double> map = arcMaps.Slice(span.ArcMapOffset, ArcLengthMapSize);
        int low = 0;
        int high = ArcLengthBinCount;
        while (low + 1 < high)
        {
            int middle = low + ((high - low) / 2);
            if (map[middle] <= distance)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }
        double binLength = map[low + 1] - map[low];
        double fraction = binLength > 0.0 ? (distance - map[low]) / binLength : 0.0;
        double binStart = (double)low / ArcLengthBinCount;
        double binEnd = (double)(low + 1) / ArcLengthBinCount;
        double parameter = Math.Clamp(
            (low + fraction) / ArcLengthBinCount,
            binStart,
            binEnd);
        double targetInBin = distance - map[low];
        for (int i = 0; i < 8; i++)
        {
            double measured = IntegrateArcLength(points, binStart, parameter);
            double error = measured - targetInBin;
            if (Math.Abs(error) <= Math.Max(1e-12, span.Length * 1e-12))
            {
                break;
            }
            if (error > 0.0)
            {
                binEnd = parameter;
            }
            else
            {
                binStart = parameter;
            }

            double speed = EvaluateDerivative(points, parameter).Length;
            double candidate = speed > 0.0 ? parameter - (error / speed) : double.NaN;
            parameter = double.IsFinite(candidate) && candidate > binStart && candidate < binEnd
                ? candidate
                : (binStart + binEnd) * 0.5;
        }
        return Math.Clamp(parameter, 0.0, 1.0);
    }

    private static void ExtractSubcurve(
        ReadOnlySpan<HomogeneousPoint> source,
        double start,
        double end,
        Span<HomogeneousPoint> destination,
        Span<HomogeneousPoint> left,
        Span<HomogeneousPoint> right)
    {
        Subdivide(source, start, left, right);
        if (start >= 1.0)
        {
            right.Fill(source[^1]);
        }
        double relativeEnd = start >= 1.0
            ? 0.0
            : Math.Clamp((end - start) / (1.0 - start), 0.0, 1.0);
        Subdivide(right, relativeEnd, destination, left);
    }

    private static void Subdivide(
        ReadOnlySpan<HomogeneousPoint> source,
        double parameter,
        Span<HomogeneousPoint> left,
        Span<HomogeneousPoint> right)
    {
        int degree = source.Length - 1;
        Span<HomogeneousPoint> work = degree <= 10
            ? stackalloc HomogeneousPoint[degree + 1]
            : new HomogeneousPoint[degree + 1];
        source.CopyTo(work);
        left[0] = work[0];
        right[degree] = work[degree];
        for (int level = 1; level <= degree; level++)
        {
            for (int i = 0; i <= degree - level; i++)
            {
                work[i] = HomogeneousPoint.Lerp(work[i], work[i + 1], parameter);
            }
            left[level] = work[0];
            right[degree - level] = work[degree - level];
        }
    }

    private static CadPoint3D EvaluatePoint(
        ReadOnlySpan<HomogeneousPoint> points,
        double parameter)
    {
        Span<HomogeneousPoint> work = stackalloc HomogeneousPoint[points.Length];
        points.CopyTo(work);
        for (int remaining = points.Length - 1; remaining > 0; remaining--)
        {
            for (int i = 0; i < remaining; i++)
            {
                work[i] = HomogeneousPoint.Lerp(work[i], work[i + 1], parameter);
            }
        }
        return work[0].Cartesian;
    }

    private static CadPoint3D EvaluateDerivative(
        ReadOnlySpan<HomogeneousPoint> points,
        double parameter)
    {
        int degree = points.Length - 1;
        Span<HomogeneousPoint> derivative = stackalloc HomogeneousPoint[degree];
        for (int i = 0; i < degree; i++)
        {
            derivative[i] = (points[i + 1] - points[i]) * degree;
        }

        Span<HomogeneousPoint> work = stackalloc HomogeneousPoint[points.Length];
        points.CopyTo(work);
        for (int remaining = degree; remaining > 0; remaining--)
        {
            for (int i = 0; i < remaining; i++)
            {
                work[i] = HomogeneousPoint.Lerp(work[i], work[i + 1], parameter);
            }
        }
        for (int remaining = degree - 1; remaining > 0; remaining--)
        {
            for (int i = 0; i < remaining; i++)
            {
                derivative[i] = HomogeneousPoint.Lerp(
                    derivative[i],
                    derivative[i + 1],
                    parameter);
            }
        }

        HomogeneousPoint value = work[0];
        HomogeneousPoint delta = derivative[0];
        double denominator = value.W * value.W;
        if (!(denominator > 0.0))
        {
            return CadPoint3D.Zero;
        }
        return new CadPoint3D(
            ((delta.X * value.W) - (value.X * delta.W)) / denominator,
            ((delta.Y * value.W) - (value.Y * delta.W)) / denominator,
            ((delta.Z * value.W) - (value.Z * delta.W)) / denominator);
    }

    private static Vector2 Project(CadPoint3D point, CadPoint3D origin) =>
        new(ToFloat(point.X - origin.X), ToFloat(point.Y - origin.Y));

    private static Vector2 ToVector2(CadPoint3D value) =>
        new(ToFloat(value.X), ToFloat(value.Y));

    private static float ToFloat(double value)
    {
        float converted = (float)value;
        if (!float.IsFinite(converted))
        {
            throw new InvalidOperationException(
                "A rebased CAD linetype coordinate exceeds the retained float range.");
        }
        return converted;
    }

    private readonly record struct BezierSpan(
        int ControlPointOffset,
        double KnotStart,
        double KnotEnd,
        double Length,
        double PathOffset,
        int ArcMapOffset);

    private readonly record struct HomogeneousPoint(
        double X,
        double Y,
        double Z,
        double W)
    {
        public CadPoint3D Cartesian => new(X / W, Y / W, Z / W);

        public static HomogeneousPoint FromCartesian(CadPoint3D point, double weight) =>
            new(point.X * weight, point.Y * weight, point.Z * weight, weight);

        public static HomogeneousPoint Lerp(
            HomogeneousPoint start,
            HomogeneousPoint end,
            double amount) =>
            start + ((end - start) * amount);

        public static HomogeneousPoint operator +(
            HomogeneousPoint left,
            HomogeneousPoint right) =>
            new(
                left.X + right.X,
                left.Y + right.Y,
                left.Z + right.Z,
                left.W + right.W);

        public static HomogeneousPoint operator -(
            HomogeneousPoint left,
            HomogeneousPoint right) =>
            new(
                left.X - right.X,
                left.Y - right.Y,
                left.Z - right.Z,
                left.W - right.W);

        public static HomogeneousPoint operator *(
            HomogeneousPoint value,
            double scale) =>
            new(
                value.X * scale,
                value.Y * scale,
                value.Z * scale,
                value.W * scale);
    }
}
