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
        var bezierPoints = new CadHomogeneousPoint[checked(spanCount * (degree + 1))];
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
        ReadOnlySpan<CadHomogeneousPoint> bezierPoints,
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
        var scratch = new CadHomogeneousPoint[checked((degree + 1) * 3)];
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
                ReadOnlySpan<CadHomogeneousPoint> controlPoints = bezierPoints.Slice(
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
                ReadOnlySpan<CadHomogeneousPoint> spanPoints = bezierPoints.Slice(
                    span.ControlPointOffset,
                    degree + 1);
                double t0 = InvertArcDistance(span, localStart, arcMaps, spanPoints);
                double t1 = current.IsPoint
                    ? t0
                    : InvertArcDistance(span, localEnd, arcMaps, spanPoints);
                Span<CadHomogeneousPoint> piece = scratch.AsSpan(0, degree + 1);
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
                    CadHomogeneousPoint value = piece[i];
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
        Span<CadHomogeneousPoint> destination,
        Span<BezierSpan> spans)
    {
        int degree = canonical.Degree;
        int outputSpanIndex = 0;
        for (int sourceSpan = degree; sourceSpan < canonical.ControlPointCount; sourceSpan++)
        {
            double start = canonical.GetKnot(sourceSpan);
            double end = canonical.GetKnot(sourceSpan + 1);
            if (!(end > start))
            {
                continue;
            }

            int destinationOffset = outputSpanIndex * (degree + 1);
            if (!CadRationalBezier.TryExtractSpan(
                    canonical,
                    sourceSpan,
                    destination.Slice(destinationOffset, degree + 1)))
            {
                return false;
            }
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
            CadRationalBezier.CreateElevatedLine(
                end,
                start,
                destination.Slice(destinationOffset, degree + 1));

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

    private static double BuildArcLengthMap(
        ReadOnlySpan<CadHomogeneousPoint> points,
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
        ReadOnlySpan<CadHomogeneousPoint> points,
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
        ReadOnlySpan<CadHomogeneousPoint> points) =>
        InvertArcDistance(
            span,
            Math.Clamp(pathDistance - span.PathOffset, 0.0, span.Length),
            arcMaps,
            points);

    private static double InvertArcDistance(
        in BezierSpan span,
        double distance,
        ReadOnlySpan<double> arcMaps,
        ReadOnlySpan<CadHomogeneousPoint> points)
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
        ReadOnlySpan<CadHomogeneousPoint> source,
        double start,
        double end,
        Span<CadHomogeneousPoint> destination,
        Span<CadHomogeneousPoint> left,
        Span<CadHomogeneousPoint> right)
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
        ReadOnlySpan<CadHomogeneousPoint> source,
        double parameter,
        Span<CadHomogeneousPoint> left,
        Span<CadHomogeneousPoint> right) =>
        CadRationalBezier.Subdivide(source, parameter, left, right);

    private static CadPoint3D EvaluatePoint(
        ReadOnlySpan<CadHomogeneousPoint> points,
        double parameter) =>
        CadRationalBezier.EvaluateHomogeneous(points, parameter).Cartesian;

    private static CadPoint3D EvaluateDerivative(
        ReadOnlySpan<CadHomogeneousPoint> points,
        double parameter)
    {
        int degree = points.Length - 1;
        Span<CadHomogeneousPoint> derivative = stackalloc CadHomogeneousPoint[degree];
        for (int i = 0; i < degree; i++)
        {
            derivative[i] = (points[i + 1] - points[i]) * degree;
        }

        Span<CadHomogeneousPoint> work = stackalloc CadHomogeneousPoint[points.Length];
        points.CopyTo(work);
        for (int remaining = degree; remaining > 0; remaining--)
        {
            for (int i = 0; i < remaining; i++)
            {
                work[i] = CadHomogeneousPoint.Lerp(work[i], work[i + 1], parameter);
            }
        }
        for (int remaining = degree - 1; remaining > 0; remaining--)
        {
            for (int i = 0; i < remaining; i++)
            {
                derivative[i] = CadHomogeneousPoint.Lerp(
                    derivative[i],
                    derivative[i + 1],
                    parameter);
            }
        }

        CadHomogeneousPoint value = work[0];
        CadHomogeneousPoint delta = derivative[0];
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
}
