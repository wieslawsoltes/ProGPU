namespace ProGPU.CAD;

public static partial class CadObjectSnapQuery
{
    private const double IntersectionToleranceFactor =
        1.4210854715202004e-14;
    private const double IntersectionParameterTolerance = 1e-12;

    private static void EvaluateIntersections(
        CadDocumentSnapshot snapshot,
        ReadOnlySpan<int> entityIndices,
        ref SearchState search)
    {
        ReadOnlySpan<CadEntityHeader> entities = snapshot.Entities.Span;
        for (int firstPosition = 0;
             firstPosition < entityIndices.Length - 1;
             firstPosition++)
        {
            int firstEntityIndex = entityIndices[firstPosition];
            CadEntityHeader firstHeader = entities[firstEntityIndex];
            for (int secondPosition = firstPosition + 1;
                 secondPosition < entityIndices.Length;
                 secondPosition++)
            {
                if (search.EvaluatedEntityPairCount >=
                    MaximumIntersectionEntityPairs)
                {
                    return;
                }

                search.EvaluatedEntityPairCount++;
                int secondEntityIndex = entityIndices[secondPosition];
                CadEntityHeader secondHeader = entities[secondEntityIndex];
                if (!EvaluateIntersectionPair(
                    snapshot,
                    firstHeader,
                    firstEntityIndex,
                    secondHeader,
                    secondEntityIndex,
                    ref search))
                {
                    return;
                }
            }
        }
    }

    private static bool EvaluateIntersectionPair(
        CadDocumentSnapshot snapshot,
        CadEntityHeader firstHeader,
        int firstEntityIndex,
        CadEntityHeader secondHeader,
        int secondEntityIndex,
        ref SearchState search)
    {
        if (!CanEnumerateIntersectionCurves(firstHeader.Kind) ||
            !CanEnumerateIntersectionCurves(secondHeader.Kind))
        {
            search.UnsupportedGeometryCount++;
            return true;
        }

        var firstEnumerator = new IntersectionCurveEnumerator(
            snapshot,
            firstHeader);
        bool unsupported = false;
        int ordinal = 0;
        while (firstEnumerator.MoveNext(
            out IntersectionCurve first,
            out bool firstUnsupported))
        {
            unsupported |= firstUnsupported;
            var secondEnumerator = new IntersectionCurveEnumerator(
                snapshot,
                secondHeader);
            while (secondEnumerator.MoveNext(
                out IntersectionCurve second,
                out bool secondUnsupported))
            {
                unsupported |= secondUnsupported;
                if (search.EvaluatedIntersectionComponentPairCount >=
                    MaximumIntersectionComponentPairs)
                {
                    search.AreIntersectionComponentsTruncated = true;
                    return false;
                }
                search.EvaluatedIntersectionComponentPairCount++;
                IntersectionStatus status = Intersect(
                    first,
                    second,
                    out CadPoint3D firstPoint,
                    out CadPoint3D secondPoint,
                    out int pointCount);
                if (status == IntersectionStatus.Unsupported)
                {
                    unsupported = true;
                    continue;
                }

                if (pointCount >= 1)
                {
                    search.Consider(
                        CadObjectSnapKind.Intersection,
                        firstPoint,
                        firstEntityIndex,
                        firstHeader.Handle,
                        secondEntityIndex,
                        secondHeader.Handle,
                        ordinal++);
                }
                if (pointCount >= 2)
                {
                    search.Consider(
                        CadObjectSnapKind.Intersection,
                        secondPoint,
                        firstEntityIndex,
                        firstHeader.Handle,
                        secondEntityIndex,
                        secondHeader.Handle,
                        ordinal++);
                }
            }
            unsupported |= secondEnumerator.Unsupported;
        }
        unsupported |= firstEnumerator.Unsupported;
        if (unsupported)
        {
            search.UnsupportedGeometryCount++;
        }
        return true;
    }

    private static bool CanEnumerateIntersectionCurves(CadEntityKind kind) =>
        kind is CadEntityKind.Line or
            CadEntityKind.Circle or
            CadEntityKind.Arc or
            CadEntityKind.Ellipse or
            CadEntityKind.LightweightPolyline or
            CadEntityKind.Polyline2D or
            CadEntityKind.Polyline3D or
            CadEntityKind.Ray or
            CadEntityKind.XLine;

    private static IntersectionStatus Intersect(
        IntersectionCurve first,
        IntersectionCurve second,
        out CadPoint3D firstPoint,
        out CadPoint3D secondPoint,
        out int pointCount)
    {
        firstPoint = default;
        secondPoint = default;
        pointCount = 0;
        if (!NearlyEqual(first.PlaneZ, second.PlaneZ))
        {
            return IntersectionStatus.None;
        }

        if (first.Kind == IntersectionCurveKind.Linear &&
            second.Kind == IntersectionCurveKind.Linear)
        {
            return IntersectLinearLinear(
                first,
                second,
                out firstPoint,
                out pointCount);
        }
        if (first.Kind == IntersectionCurveKind.Linear &&
            second.Kind == IntersectionCurveKind.Circular)
        {
            return IntersectLinearCircular(
                first,
                second,
                out firstPoint,
                out secondPoint,
                out pointCount);
        }
        if (first.Kind == IntersectionCurveKind.Circular &&
            second.Kind == IntersectionCurveKind.Linear)
        {
            return IntersectLinearCircular(
                second,
                first,
                out firstPoint,
                out secondPoint,
                out pointCount);
        }
        if (first.Kind == IntersectionCurveKind.Linear &&
            second.Kind == IntersectionCurveKind.Elliptical)
        {
            return IntersectLinearElliptical(
                first,
                second,
                out firstPoint,
                out secondPoint,
                out pointCount);
        }
        if (first.Kind == IntersectionCurveKind.Elliptical &&
            second.Kind == IntersectionCurveKind.Linear)
        {
            return IntersectLinearElliptical(
                second,
                first,
                out firstPoint,
                out secondPoint,
                out pointCount);
        }
        if (first.Kind == IntersectionCurveKind.Circular &&
            second.Kind == IntersectionCurveKind.Circular)
        {
            return IntersectCircularCircular(
                first,
                second,
                out firstPoint,
                out secondPoint,
                out pointCount);
        }

        return IntersectionStatus.Unsupported;
    }

    private static IntersectionStatus IntersectLinearLinear(
        IntersectionCurve first,
        IntersectionCurve second,
        out CadPoint3D point,
        out int pointCount)
    {
        point = default;
        pointCount = 0;
        double firstLength = Hypot(first.Direction.X, first.Direction.Y);
        double secondLength = Hypot(second.Direction.X, second.Direction.Y);
        if (!(firstLength > 0.0) || !(secondLength > 0.0) ||
            !double.IsFinite(firstLength) || !double.IsFinite(secondLength))
        {
            return IntersectionStatus.Unsupported;
        }

        double firstX = first.Direction.X / firstLength;
        double firstY = first.Direction.Y / firstLength;
        double secondX = second.Direction.X / secondLength;
        double secondY = second.Direction.Y / secondLength;
        double determinant = Cross(
            firstX,
            firstY,
            secondX,
            secondY);
        double deltaX = second.Origin.X - first.Origin.X;
        double deltaY = second.Origin.Y - first.Origin.Y;
        if (Math.Abs(determinant) <= IntersectionToleranceFactor)
        {
            double deltaLength = Hypot(deltaX, deltaY);
            double collinear = Cross(deltaX, deltaY, firstX, firstY);
            if (Math.Abs(collinear) >
                IntersectionToleranceFactor * Math.Max(1.0, deltaLength))
            {
                return IntersectionStatus.None;
            }
            return IntersectCollinearLinear(
                first,
                second,
                firstX,
                firstY,
                firstLength,
                deltaX,
                deltaY,
                out point,
                out pointCount);
        }

        double firstParameter = Cross(
            deltaX,
            deltaY,
            secondX,
            secondY) /
            (firstLength * determinant);
        double secondParameter = Cross(
            deltaX,
            deltaY,
            firstX,
            firstY) /
            (secondLength * determinant);
        if (!ContainsLinearParameter(first, firstParameter) ||
            !ContainsLinearParameter(second, secondParameter))
        {
            return IntersectionStatus.None;
        }

        firstParameter = ClampLinearParameter(first, firstParameter);
        point = new CadPoint3D(
            first.Origin.X + (first.Direction.X * firstParameter),
            first.Origin.Y + (first.Direction.Y * firstParameter),
            first.PlaneZ);
        pointCount = IsFinite(point) ? 1 : 0;
        return pointCount == 1
            ? IntersectionStatus.Found
            : IntersectionStatus.Unsupported;
    }

    private static IntersectionStatus IntersectCollinearLinear(
        IntersectionCurve first,
        IntersectionCurve second,
        double firstUnitX,
        double firstUnitY,
        double firstLength,
        double deltaX,
        double deltaY,
        out CadPoint3D point,
        out int pointCount)
    {
        point = default;
        pointCount = 0;
        double offset = Dot(
            deltaX,
            deltaY,
            firstUnitX,
            firstUnitY) / firstLength;
        double ratio = Dot(
            second.Direction.X,
            second.Direction.Y,
            firstUnitX,
            firstUnitY) / firstLength;
        if (ratio == 0.0 || !double.IsFinite(ratio) ||
            !double.IsFinite(offset))
        {
            return IntersectionStatus.Unsupported;
        }

        double mappedFirst = MapLinearBound(
            offset,
            ratio,
            second.MinimumParameter);
        double mappedSecond = MapLinearBound(
            offset,
            ratio,
            second.MaximumParameter);
        double mappedMinimum = Math.Min(mappedFirst, mappedSecond);
        double mappedMaximum = Math.Max(mappedFirst, mappedSecond);
        double overlapMinimum = Math.Max(
            first.MinimumParameter,
            mappedMinimum);
        double overlapMaximum = Math.Min(
            first.MaximumParameter,
            mappedMaximum);
        if (overlapMaximum < overlapMinimum)
        {
            return IntersectionStatus.None;
        }
        if (overlapMaximum > overlapMinimum)
        {
            return IntersectionStatus.Unsupported;
        }

        point = new CadPoint3D(
            first.Origin.X + (first.Direction.X * overlapMinimum),
            first.Origin.Y + (first.Direction.Y * overlapMinimum),
            first.PlaneZ);
        pointCount = IsFinite(point) ? 1 : 0;
        return pointCount == 1
            ? IntersectionStatus.Found
            : IntersectionStatus.Unsupported;
    }

    private static double MapLinearBound(
        double offset,
        double ratio,
        double bound)
    {
        if (double.IsPositiveInfinity(bound))
        {
            return Math.CopySign(double.PositiveInfinity, ratio);
        }
        if (double.IsNegativeInfinity(bound))
        {
            return Math.CopySign(double.PositiveInfinity, -ratio);
        }
        return offset + (ratio * bound);
    }

    private static IntersectionStatus IntersectLinearCircular(
        IntersectionCurve linear,
        IntersectionCurve circular,
        out CadPoint3D firstPoint,
        out CadPoint3D secondPoint,
        out int pointCount)
    {
        firstPoint = default;
        secondPoint = default;
        pointCount = 0;
        double length = Hypot(linear.Direction.X, linear.Direction.Y);
        if (!(length > 0.0) || !double.IsFinite(length))
        {
            return IntersectionStatus.Unsupported;
        }

        double unitX = linear.Direction.X / length;
        double unitY = linear.Direction.Y / length;
        double deltaX = linear.Origin.X - circular.Center.X;
        double deltaY = linear.Origin.Y - circular.Center.Y;
        double projection = Dot(deltaX, deltaY, unitX, unitY);
        double perpendicular = Cross(deltaX, deltaY, unitX, unitY);
        double scale = Math.Max(
            1.0,
            Math.Max(Math.Abs(circular.Radius), Math.Abs(perpendicular)));
        double scaledRadius = circular.Radius / scale;
        double scaledPerpendicular = perpendicular / scale;
        double heightSquared =
            (scaledRadius * scaledRadius) -
            (scaledPerpendicular * scaledPerpendicular);
        double tolerance = IntersectionToleranceFactor *
            Math.Max(
                1.0,
                (scaledRadius * scaledRadius) +
                (scaledPerpendicular * scaledPerpendicular));
        if (heightSquared < -tolerance)
        {
            return IntersectionStatus.None;
        }
        if (heightSquared < 0.0)
        {
            heightSquared = 0.0;
        }

        double height = Math.Sqrt(heightSquared) * scale;
        double firstParameter = (-projection - height) / length;
        double secondParameter = (-projection + height) / length;
        AddLinearCircularPoint(
            linear,
            circular,
            firstParameter,
            ref firstPoint,
            ref secondPoint,
            ref pointCount);
        if (height > IntersectionToleranceFactor * scale)
        {
            AddLinearCircularPoint(
                linear,
                circular,
                secondParameter,
                ref firstPoint,
                ref secondPoint,
                ref pointCount);
        }
        return IntersectionStatus.Found;
    }

    private static void AddLinearCircularPoint(
        IntersectionCurve linear,
        IntersectionCurve circular,
        double parameter,
        ref CadPoint3D firstPoint,
        ref CadPoint3D secondPoint,
        ref int pointCount)
    {
        if (!ContainsLinearParameter(linear, parameter))
        {
            return;
        }
        parameter = ClampLinearParameter(linear, parameter);
        var point = new CadPoint3D(
            linear.Origin.X + (linear.Direction.X * parameter),
            linear.Origin.Y + (linear.Direction.Y * parameter),
            linear.PlaneZ);
        if (!IsFinite(point) || !ContainsCircularPoint(circular, point))
        {
            return;
        }
        AddDistinctPoint(
            point,
            ref firstPoint,
            ref secondPoint,
            ref pointCount);
    }

    private static IntersectionStatus IntersectLinearElliptical(
        IntersectionCurve linear,
        IntersectionCurve ellipse,
        out CadPoint3D firstPoint,
        out CadPoint3D secondPoint,
        out int pointCount)
    {
        firstPoint = default;
        secondPoint = default;
        pointCount = 0;
        double determinant = Cross(
            ellipse.AxisX.X,
            ellipse.AxisX.Y,
            ellipse.AxisY.X,
            ellipse.AxisY.Y);
        if (determinant == 0.0 || !double.IsFinite(determinant))
        {
            return IntersectionStatus.Unsupported;
        }

        double originX = linear.Origin.X - ellipse.Center.X;
        double originY = linear.Origin.Y - ellipse.Center.Y;
        ToEllipseCoordinates(
            originX,
            originY,
            ellipse,
            determinant,
            out double localOriginX,
            out double localOriginY);
        ToEllipseCoordinates(
            linear.Direction.X,
            linear.Direction.Y,
            ellipse,
            determinant,
            out double localDirectionX,
            out double localDirectionY);
        double a = Dot(
            localDirectionX,
            localDirectionY,
            localDirectionX,
            localDirectionY);
        double b = 2.0 * Dot(
            localOriginX,
            localOriginY,
            localDirectionX,
            localDirectionY);
        double c = Dot(
            localOriginX,
            localOriginY,
            localOriginX,
            localOriginY) - 1.0;
        if (!TrySolveQuadratic(
                a,
                b,
                c,
                out double firstParameter,
                out double secondParameter,
                out int rootCount))
        {
            return IntersectionStatus.Unsupported;
        }
        if (rootCount == 0)
        {
            return IntersectionStatus.None;
        }

        AddLinearEllipticalPoint(
            linear,
            ellipse,
            determinant,
            firstParameter,
            ref firstPoint,
            ref secondPoint,
            ref pointCount);
        if (rootCount == 2)
        {
            AddLinearEllipticalPoint(
                linear,
                ellipse,
                determinant,
                secondParameter,
                ref firstPoint,
                ref secondPoint,
                ref pointCount);
        }
        return IntersectionStatus.Found;
    }

    private static void AddLinearEllipticalPoint(
        IntersectionCurve linear,
        IntersectionCurve ellipse,
        double determinant,
        double parameter,
        ref CadPoint3D firstPoint,
        ref CadPoint3D secondPoint,
        ref int pointCount)
    {
        if (!ContainsLinearParameter(linear, parameter))
        {
            return;
        }
        parameter = ClampLinearParameter(linear, parameter);
        var point = new CadPoint3D(
            linear.Origin.X + (linear.Direction.X * parameter),
            linear.Origin.Y + (linear.Direction.Y * parameter),
            linear.PlaneZ);
        double deltaX = point.X - ellipse.Center.X;
        double deltaY = point.Y - ellipse.Center.Y;
        ToEllipseCoordinates(
            deltaX,
            deltaY,
            ellipse,
            determinant,
            out double localX,
            out double localY);
        double parameterAngle = Math.Atan2(localY, localX);
        if (!IsFinite(point) || !ContainsAngle(
                ellipse.StartParameter,
                ellipse.Sweep,
                parameterAngle))
        {
            return;
        }
        AddDistinctPoint(
            point,
            ref firstPoint,
            ref secondPoint,
            ref pointCount);
    }

    private static IntersectionStatus IntersectCircularCircular(
        IntersectionCurve first,
        IntersectionCurve second,
        out CadPoint3D firstPoint,
        out CadPoint3D secondPoint,
        out int pointCount)
    {
        firstPoint = default;
        secondPoint = default;
        pointCount = 0;
        double deltaX = second.Center.X - first.Center.X;
        double deltaY = second.Center.Y - first.Center.Y;
        double distance = Hypot(deltaX, deltaY);
        if (!double.IsFinite(distance))
        {
            return IntersectionStatus.Unsupported;
        }

        double scale = Math.Max(
            1.0,
            Math.Max(distance, Math.Max(first.Radius, second.Radius)));
        double tolerance = IntersectionToleranceFactor * scale;
        if (distance <= tolerance)
        {
            return Math.Abs(first.Radius - second.Radius) <= tolerance
                ? IntersectionStatus.Unsupported
                : IntersectionStatus.None;
        }
        if (distance > first.Radius + second.Radius + tolerance ||
            distance < Math.Abs(first.Radius - second.Radius) - tolerance)
        {
            return IntersectionStatus.None;
        }

        double scaledDistance = distance / scale;
        double scaledFirstRadius = first.Radius / scale;
        double scaledSecondRadius = second.Radius / scale;
        double alongScaled =
            ((scaledFirstRadius * scaledFirstRadius) -
             (scaledSecondRadius * scaledSecondRadius) +
             (scaledDistance * scaledDistance)) /
            (2.0 * scaledDistance);
        double heightSquared =
            (scaledFirstRadius * scaledFirstRadius) -
            (alongScaled * alongScaled);
        double heightTolerance = IntersectionToleranceFactor * Math.Max(
            1.0,
            (scaledFirstRadius * scaledFirstRadius) +
            (alongScaled * alongScaled));
        if (heightSquared < -heightTolerance)
        {
            return IntersectionStatus.None;
        }
        if (heightSquared < 0.0)
        {
            heightSquared = 0.0;
        }

        double unitX = deltaX / distance;
        double unitY = deltaY / distance;
        double along = alongScaled * scale;
        double baseX = first.Center.X + (unitX * along);
        double baseY = first.Center.Y + (unitY * along);
        double height = Math.Sqrt(heightSquared) * scale;
        AddCircularCircularPoint(
            first,
            second,
            new CadPoint3D(
                baseX - (unitY * height),
                baseY + (unitX * height),
                first.PlaneZ),
            ref firstPoint,
            ref secondPoint,
            ref pointCount);
        if (height > tolerance)
        {
            AddCircularCircularPoint(
                first,
                second,
                new CadPoint3D(
                    baseX + (unitY * height),
                    baseY - (unitX * height),
                    first.PlaneZ),
                ref firstPoint,
                ref secondPoint,
                ref pointCount);
        }
        return IntersectionStatus.Found;
    }

    private static void AddCircularCircularPoint(
        IntersectionCurve first,
        IntersectionCurve second,
        CadPoint3D point,
        ref CadPoint3D firstPoint,
        ref CadPoint3D secondPoint,
        ref int pointCount)
    {
        if (!IsFinite(point) ||
            !ContainsCircularPoint(first, point) ||
            !ContainsCircularPoint(second, point))
        {
            return;
        }
        AddDistinctPoint(
            point,
            ref firstPoint,
            ref secondPoint,
            ref pointCount);
    }

    private static bool ContainsCircularPoint(
        IntersectionCurve curve,
        CadPoint3D point) =>
        ContainsAngle(
            curve.StartParameter,
            curve.Sweep,
            Math.Atan2(
                point.Y - curve.Center.Y,
                point.X - curve.Center.X));

    private static bool ContainsAngle(
        double start,
        double sweep,
        double angle)
    {
        if (Math.Abs(sweep) >= TwoPi - FullSweepTolerance)
        {
            return true;
        }
        double extent = Math.Abs(sweep);
        double relative = sweep >= 0.0
            ? NormalizePositive(angle - start)
            : NormalizePositive(start - angle);
        return relative <= extent + IntersectionParameterTolerance;
    }

    private static double NormalizePositive(double angle)
    {
        double normalized = angle % TwoPi;
        return normalized < 0.0 ? normalized + TwoPi : normalized;
    }

    private static bool TrySolveQuadratic(
        double a,
        double b,
        double c,
        out double first,
        out double second,
        out int count)
    {
        first = default;
        second = default;
        count = 0;
        if (!double.IsFinite(a) || !double.IsFinite(b) ||
            !double.IsFinite(c) || !(a > 0.0))
        {
            return false;
        }

        double coefficientScale = Math.Max(
            Math.Abs(a),
            Math.Max(Math.Abs(b), Math.Abs(c)));
        if (!(coefficientScale > 0.0) ||
            !double.IsFinite(coefficientScale))
        {
            return false;
        }
        a /= coefficientScale;
        b /= coefficientScale;
        c /= coefficientScale;
        double fourAC = 4.0 * a * c;
        double discriminant = Math.FusedMultiplyAdd(b, b, -fourAC);
        double tolerance = IntersectionToleranceFactor *
            Math.Max(1.0, (b * b) + Math.Abs(fourAC));
        if (discriminant < -tolerance)
        {
            return true;
        }
        if (discriminant <= tolerance)
        {
            first = -b / (2.0 * a);
            count = double.IsFinite(first) ? 1 : 0;
            return count == 1;
        }

        double squareRoot = Math.Sqrt(discriminant);
        double q = -0.5 * (b + Math.CopySign(squareRoot, b));
        first = q / a;
        second = c / q;
        if (!double.IsFinite(first) || !double.IsFinite(second))
        {
            return false;
        }
        if (second < first)
        {
            (first, second) = (second, first);
        }
        count = 2;
        return true;
    }

    private static void ToEllipseCoordinates(
        double x,
        double y,
        IntersectionCurve ellipse,
        double determinant,
        out double localX,
        out double localY)
    {
        localX = Cross(x, y, ellipse.AxisY.X, ellipse.AxisY.Y) /
            determinant;
        localY = Cross(ellipse.AxisX.X, ellipse.AxisX.Y, x, y) /
            determinant;
    }

    private static void AddDistinctPoint(
        CadPoint3D point,
        ref CadPoint3D first,
        ref CadPoint3D second,
        ref int count)
    {
        if (count == 0)
        {
            first = point;
            count = 1;
            return;
        }
        double scale = Math.Max(
            1.0,
            Math.Max(
                Math.Max(Math.Abs(point.X), Math.Abs(point.Y)),
                Math.Max(Math.Abs(first.X), Math.Abs(first.Y))));
        double tolerance = IntersectionToleranceFactor * scale;
        if (Math.Abs(point.X - first.X) <= tolerance &&
            Math.Abs(point.Y - first.Y) <= tolerance)
        {
            return;
        }
        second = point;
        count = 2;
    }

    private static bool ContainsLinearParameter(
        IntersectionCurve curve,
        double parameter) =>
        double.IsFinite(parameter) &&
        parameter >= curve.MinimumParameter - IntersectionParameterTolerance &&
        parameter <= curve.MaximumParameter + IntersectionParameterTolerance;

    private static double ClampLinearParameter(
        IntersectionCurve curve,
        double parameter)
    {
        if (double.IsFinite(curve.MinimumParameter) &&
            parameter < curve.MinimumParameter)
        {
            return curve.MinimumParameter;
        }
        if (double.IsFinite(curve.MaximumParameter) &&
            parameter > curve.MaximumParameter)
        {
            return curve.MaximumParameter;
        }
        return parameter;
    }

    private static double Cross(
        double firstX,
        double firstY,
        double secondX,
        double secondY) =>
        Math.FusedMultiplyAdd(firstX, secondY, -(firstY * secondX));

    private static double Dot(
        double firstX,
        double firstY,
        double secondX,
        double secondY) =>
        Math.FusedMultiplyAdd(firstX, secondX, firstY * secondY);

    private static double Hypot(double x, double y)
    {
        double maximum = Math.Max(Math.Abs(x), Math.Abs(y));
        if (maximum == 0.0)
        {
            return 0.0;
        }
        double scaledX = x / maximum;
        double scaledY = y / maximum;
        return maximum * Math.Sqrt(
            (scaledX * scaledX) + (scaledY * scaledY));
    }

    private static bool NearlyEqual(double first, double second)
    {
        double scale = Math.Max(
            1.0,
            Math.Max(Math.Abs(first), Math.Abs(second)));
        return Math.Abs(first - second) <=
            IntersectionToleranceFactor * scale;
    }

    private static bool TryCreateLinearCurve(
        CadPoint3D origin,
        CadPoint3D direction,
        double minimumParameter,
        double maximumParameter,
        out IntersectionCurve curve)
    {
        curve = default;
        if (!IsFinite(origin) || !IsFinite(direction))
        {
            return false;
        }
        double xyLength = Hypot(direction.X, direction.Y);
        double vectorScale = Math.Max(xyLength, Math.Abs(direction.Z));
        if (!(xyLength > 0.0) || !double.IsFinite(vectorScale) ||
            Math.Abs(direction.Z) >
                IntersectionToleranceFactor * vectorScale)
        {
            return false;
        }
        curve = new IntersectionCurve(
            IntersectionCurveKind.Linear,
            origin,
            direction,
            minimumParameter,
            maximumParameter,
            default,
            0.0,
            default,
            default,
            0.0,
            0.0,
            origin.Z);
        return true;
    }

    private static bool TryCreateCircularCurve(
        CadPoint3D center,
        CadPoint3D cosineAxis,
        CadPoint3D sineAxis,
        double startParameter,
        double sweep,
        out IntersectionCurve curve)
    {
        curve = default;
        if (!IsFinite(center) || !IsFinite(cosineAxis) ||
            !IsFinite(sineAxis) || !double.IsFinite(startParameter) ||
            !double.IsFinite(sweep))
        {
            return false;
        }
        double cosineLength = Hypot(cosineAxis.X, cosineAxis.Y);
        double sineLength = Hypot(sineAxis.X, sineAxis.Y);
        double cosineScale = Math.Max(cosineLength, Math.Abs(cosineAxis.Z));
        double sineScale = Math.Max(sineLength, Math.Abs(sineAxis.Z));
        double axisScale = Math.Max(cosineLength, sineLength);
        if (!(cosineLength > 0.0) || !(sineLength > 0.0) ||
            Math.Abs(cosineAxis.Z) >
                IntersectionToleranceFactor * cosineScale ||
            Math.Abs(sineAxis.Z) >
                IntersectionToleranceFactor * sineScale ||
            Math.Abs(cosineLength - sineLength) >
                IntersectionToleranceFactor * axisScale ||
            Math.Abs(Dot(
                cosineAxis.X / cosineLength,
                cosineAxis.Y / cosineLength,
                sineAxis.X / sineLength,
                sineAxis.Y / sineLength)) >
                IntersectionToleranceFactor)
        {
            return false;
        }

        double orientation = Cross(
            cosineAxis.X,
            cosineAxis.Y,
            sineAxis.X,
            sineAxis.Y);
        if (orientation == 0.0 || !double.IsFinite(orientation))
        {
            return false;
        }
        double radius = (cosineLength + sineLength) * 0.5;
        CadPoint3D startPoint = center +
            (cosineAxis * Math.Cos(startParameter)) +
            (sineAxis * Math.Sin(startParameter));
        double worldStart = Math.Atan2(
            startPoint.Y - center.Y,
            startPoint.X - center.X);
        double worldSweep = sweep * Math.CopySign(1.0, orientation);
        curve = new IntersectionCurve(
            IntersectionCurveKind.Circular,
            default,
            default,
            0.0,
            0.0,
            center,
            radius,
            default,
            default,
            worldStart,
            worldSweep,
            center.Z);
        return double.IsFinite(radius) && radius > 0.0 &&
            IsFinite(startPoint) && double.IsFinite(worldSweep);
    }

    private static bool TryCreateEllipticalCurve(
        CadEllipsePrimitive ellipse,
        out IntersectionCurve curve)
    {
        curve = default;
        if (!IsFinite(ellipse.Center) || !IsFinite(ellipse.MajorAxis) ||
            !IsFinite(ellipse.MinorAxis) ||
            !double.IsFinite(ellipse.StartParameter) ||
            !double.IsFinite(ellipse.SweepParameter))
        {
            return false;
        }
        double majorLength = Hypot(ellipse.MajorAxis.X, ellipse.MajorAxis.Y);
        double minorLength = Hypot(ellipse.MinorAxis.X, ellipse.MinorAxis.Y);
        double majorScale = Math.Max(majorLength, Math.Abs(ellipse.MajorAxis.Z));
        double minorScale = Math.Max(minorLength, Math.Abs(ellipse.MinorAxis.Z));
        double determinant = Cross(
            ellipse.MajorAxis.X,
            ellipse.MajorAxis.Y,
            ellipse.MinorAxis.X,
            ellipse.MinorAxis.Y);
        if (!(majorLength > 0.0) || !(minorLength > 0.0) ||
            Math.Abs(ellipse.MajorAxis.Z) >
                IntersectionToleranceFactor * majorScale ||
            Math.Abs(ellipse.MinorAxis.Z) >
                IntersectionToleranceFactor * minorScale ||
            Math.Abs(determinant) <=
                IntersectionToleranceFactor * majorLength * minorLength)
        {
            return false;
        }
        curve = new IntersectionCurve(
            IntersectionCurveKind.Elliptical,
            default,
            default,
            0.0,
            0.0,
            ellipse.Center,
            0.0,
            ellipse.MajorAxis,
            ellipse.MinorAxis,
            ellipse.StartParameter,
            ellipse.SweepParameter,
            ellipse.Center.Z);
        return true;
    }

    private enum IntersectionStatus : byte
    {
        None = 0,
        Found = 1,
        Unsupported = 2,
    }

    private enum IntersectionCurveKind : byte
    {
        Linear = 1,
        Circular = 2,
        Elliptical = 3,
    }

    private readonly record struct IntersectionCurve(
        IntersectionCurveKind Kind,
        CadPoint3D Origin,
        CadPoint3D Direction,
        double MinimumParameter,
        double MaximumParameter,
        CadPoint3D Center,
        double Radius,
        CadPoint3D AxisX,
        CadPoint3D AxisY,
        double StartParameter,
        double Sweep,
        double PlaneZ);

    private ref struct IntersectionCurveEnumerator
    {
        private readonly CadDocumentSnapshot _snapshot;
        private readonly CadEntityHeader _header;
        private int _index;

        public bool Unsupported { get; private set; }

        public IntersectionCurveEnumerator(
            CadDocumentSnapshot snapshot,
            CadEntityHeader header)
        {
            _snapshot = snapshot;
            _header = header;
            _index = 0;
            Unsupported = false;
        }

        public bool MoveNext(
            out IntersectionCurve curve,
            out bool unsupported)
        {
            curve = default;
            unsupported = false;
            if (_index < 0)
            {
                return false;
            }

            switch (_header.Kind)
            {
                case CadEntityKind.Line:
                {
                    if (_index++ != 0)
                    {
                        _index = -1;
                        return false;
                    }
                    CadLinePrimitive line =
                        _snapshot.Lines.Span[_header.PrimitiveIndex];
                    if (TryCreateLinearCurve(
                            line.Start,
                            line.End - line.Start,
                            0.0,
                            1.0,
                            out curve))
                    {
                        return true;
                    }
                    break;
                }
                case CadEntityKind.Ray:
                case CadEntityKind.XLine:
                {
                    if (_index++ != 0)
                    {
                        _index = -1;
                        return false;
                    }
                    CadConstructionLinePrimitive line =
                        _snapshot.ConstructionLines.Span[_header.PrimitiveIndex];
                    if (TryCreateLinearCurve(
                            line.BasePoint,
                            line.Direction,
                            _header.Kind == CadEntityKind.Ray
                                ? 0.0
                                : double.NegativeInfinity,
                            double.PositiveInfinity,
                            out curve))
                    {
                        return true;
                    }
                    break;
                }
                case CadEntityKind.Circle:
                {
                    if (_index++ != 0)
                    {
                        _index = -1;
                        return false;
                    }
                    CadCirclePrimitive circle =
                        _snapshot.Circles.Span[_header.PrimitiveIndex];
                    if (TryCreateCircularCurve(
                            circle.Center,
                            circle.CoordinateSystem.XAxis * circle.Radius,
                            circle.CoordinateSystem.YAxis * circle.Radius,
                            0.0,
                            TwoPi,
                            out curve))
                    {
                        return true;
                    }
                    break;
                }
                case CadEntityKind.Arc:
                {
                    if (_index++ != 0)
                    {
                        _index = -1;
                        return false;
                    }
                    CadArcPrimitive arc =
                        _snapshot.Arcs.Span[_header.PrimitiveIndex];
                    if (TryCreateCircularCurve(
                            arc.Center,
                            arc.CoordinateSystem.XAxis * arc.Radius,
                            arc.CoordinateSystem.YAxis * arc.Radius,
                            arc.StartAngle,
                            arc.SweepAngle,
                            out curve))
                    {
                        return true;
                    }
                    break;
                }
                case CadEntityKind.Ellipse:
                {
                    if (_index++ != 0)
                    {
                        _index = -1;
                        return false;
                    }
                    if (TryCreateEllipticalCurve(
                            _snapshot.Ellipses.Span[_header.PrimitiveIndex],
                            out curve))
                    {
                        return true;
                    }
                    break;
                }
                case CadEntityKind.LightweightPolyline:
                case CadEntityKind.Polyline2D:
                    return MoveNextPolyline2D(out curve, out unsupported);
                case CadEntityKind.Polyline3D:
                    return MoveNextPolyline3D(out curve, out unsupported);
            }

            Unsupported = true;
            unsupported = true;
            _index = -1;
            return false;
        }

        private bool MoveNextPolyline2D(
            out IntersectionCurve curve,
            out bool unsupported)
        {
            curve = default;
            unsupported = false;
            CadPolylinePrimitive polyline =
                _snapshot.Polylines.Span[_header.PrimitiveIndex];
            ReadOnlySpan<CadPolylineVertex> vertices =
                _snapshot.PolylineVertices.Span.Slice(
                    polyline.VertexOffset,
                    polyline.VertexCount);
            int segmentCount = vertices.Length < 2
                ? 0
                : polyline.IsClosed
                    ? vertices.Length
                    : vertices.Length - 1;
            while (_index < segmentCount)
            {
                int segmentIndex = _index++;
                CadPolylineVertex start = vertices[segmentIndex];
                CadPolylineVertex end =
                    vertices[(segmentIndex + 1) % vertices.Length];
                if (start.Bulge == 0.0)
                {
                    CadPoint3D worldStart = ToWorld(polyline, start);
                    CadPoint3D worldEnd = ToWorld(polyline, end);
                    if (TryCreateLinearCurve(
                            worldStart,
                            worldEnd - worldStart,
                            0.0,
                            1.0,
                            out curve))
                    {
                        return true;
                    }
                    Unsupported = true;
                    unsupported = true;
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
                    if (TryCreateCircularCurve(
                            ToWorld(polyline, centerX, centerY),
                            polyline.CoordinateSystem.XAxis * radius,
                            polyline.CoordinateSystem.YAxis * radius,
                            startAngle,
                            sweep,
                            out curve))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                }
                catch (ArithmeticException)
                {
                }
                Unsupported = true;
                unsupported = true;
            }
            _index = -1;
            return false;
        }

        private bool MoveNextPolyline3D(
            out IntersectionCurve curve,
            out bool unsupported)
        {
            curve = default;
            unsupported = false;
            CadPolyline3DPrimitive polyline =
                _snapshot.Polylines3D.Span[_header.PrimitiveIndex];
            ReadOnlySpan<CadPoint3D> points =
                _snapshot.Polyline3DPoints.Span.Slice(
                    polyline.PointOffset,
                    polyline.PointCount);
            int segmentCount = points.Length < 2
                ? 0
                : polyline.IsClosed
                    ? points.Length
                    : points.Length - 1;
            while (_index < segmentCount)
            {
                int segmentIndex = _index++;
                CadPoint3D start = points[segmentIndex];
                CadPoint3D end = points[(segmentIndex + 1) % points.Length];
                if (TryCreateLinearCurve(
                        start,
                        end - start,
                        0.0,
                        1.0,
                        out curve))
                {
                    return true;
                }
                Unsupported = true;
                unsupported = true;
            }
            _index = -1;
            return false;
        }
    }
}
