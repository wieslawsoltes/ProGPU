namespace ProGPU.CAD;

internal readonly record struct CadFaceSurfaceTriangle(
    CadPoint3D First,
    CadPoint3D Second,
    CadPoint3D Third,
    CadPoint3D Normal);

/// <summary>
/// Allocation-free exact triangle lowering for retained SOLID and 3DFACE
/// surfaces, including signed SOLID thickness and crossed quadrilaterals.
/// </summary>
/// <remarks>
/// A zero-thickness face emits at most two triangles. A simple extruded quad
/// emits twelve, an extruded triangle emits eight, and a crossed extruded SOLID
/// emits two independent eight-triangle lobes. Work and scratch are O(1).
/// </remarks>
internal static class CadFaceSurfaceTopology
{
    public const int MaximumTriangleCount = 16;
    private const double IntersectionEndpointTolerance = 1e-12;

    public static int BuildTriangles(
        CadEntityKind kind,
        in CadFacePrimitive face,
        Span<CadFaceSurfaceTriangle> destination)
    {
        if (destination.Length < MaximumTriangleCount)
        {
            throw new ArgumentException(
                $"A face triangle destination requires at least {MaximumTriangleCount} entries.",
                nameof(destination));
        }

        return kind switch
        {
            CadEntityKind.Solid => BuildSolid(face, destination),
            CadEntityKind.Face3D => BuildFace3D(face, destination),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static int BuildFace3D(
        in CadFacePrimitive face,
        Span<CadFaceSurfaceTriangle> destination)
    {
        int count = 0;
        TryAppendTriangle(
            destination,
            ref count,
            face.First,
            face.Second,
            face.Third,
            expectedNormal: null);
        if (face.Fourth != face.Third)
        {
            TryAppendTriangle(
                destination,
                ref count,
                face.First,
                face.Third,
                face.Fourth,
                expectedNormal: null);
        }
        return count;
    }

    private static int BuildSolid(
        in CadFacePrimitive face,
        Span<CadFaceSurfaceTriangle> destination)
    {
        Span<CadPoint3D> contourPoints = stackalloc CadPoint3D[6];
        Span<int> contourLengths = stackalloc int[2];
        int contourCount = BuildSolidContours(
            face,
            contourPoints,
            contourLengths);
        if (contourCount == 0)
        {
            return 0;
        }

        if (!TryGetContourNormal(
                contourPoints[..contourLengths[0]],
                out CadPoint3D referenceNormal))
        {
            return 0;
        }

        int count = 0;
        int contourOffset = 0;
        for (int contourIndex = 0; contourIndex < contourCount; contourIndex++)
        {
            int contourLength = contourLengths[contourIndex];
            ReadOnlySpan<CadPoint3D> sourceContour = contourPoints.Slice(
                contourOffset,
                contourLength);
            contourOffset += contourLength;
            if (!TryGetContourNormal(sourceContour, out CadPoint3D contourNormal))
            {
                continue;
            }

            Span<CadPoint3D> contour = stackalloc CadPoint3D[4];
            if (CadPoint3D.Dot(contourNormal, referenceNormal) >= 0.0)
            {
                sourceContour.CopyTo(contour);
            }
            else
            {
                for (int i = 0; i < contourLength; i++)
                {
                    contour[i] = sourceContour[contourLength - 1 - i];
                }
            }

            if (face.Extrusion == CadPoint3D.Zero)
            {
                AppendCap(
                    contour[..contourLength],
                    CadPoint3D.Zero,
                    referenceNormal,
                    destination,
                    ref count);
                continue;
            }

            double extrusionDirection = CadPoint3D.Dot(
                referenceNormal,
                face.Extrusion);
            double sign = extrusionDirection < 0.0 ? -1.0 : 1.0;
            AppendCap(
                contour[..contourLength],
                CadPoint3D.Zero,
                referenceNormal * -sign,
                destination,
                ref count);
            AppendCap(
                contour[..contourLength],
                face.Extrusion,
                referenceNormal * sign,
                destination,
                ref count);

            for (int edge = 0; edge < contourLength; edge++)
            {
                CadPoint3D first = contour[edge];
                CadPoint3D second = contour[(edge + 1) % contourLength];
                CadPoint3D firstTop = first + face.Extrusion;
                CadPoint3D secondTop = second + face.Extrusion;
                CadPoint3D expectedNormal = CadPoint3D.Cross(
                    second - first,
                    face.Extrusion) * sign;
                TryAppendTriangle(
                    destination,
                    ref count,
                    first,
                    second,
                    secondTop,
                    expectedNormal);
                TryAppendTriangle(
                    destination,
                    ref count,
                    first,
                    secondTop,
                    firstTop,
                    expectedNormal);
            }
        }
        return count;
    }

    private static void AppendCap(
        ReadOnlySpan<CadPoint3D> contour,
        CadPoint3D offset,
        CadPoint3D expectedNormal,
        Span<CadFaceSurfaceTriangle> destination,
        ref int count)
    {
        if (contour.Length == 4 && UsesAlternateQuadDiagonal(contour))
        {
            TryAppendTriangle(
                destination,
                ref count,
                contour[1] + offset,
                contour[2] + offset,
                contour[3] + offset,
                expectedNormal);
            TryAppendTriangle(
                destination,
                ref count,
                contour[1] + offset,
                contour[3] + offset,
                contour[0] + offset,
                expectedNormal);
            return;
        }

        CadPoint3D first = contour[0] + offset;
        for (int i = 1; i < contour.Length - 1; i++)
        {
            TryAppendTriangle(
                destination,
                ref count,
                first,
                contour[i] + offset,
                contour[i + 1] + offset,
                expectedNormal);
        }
    }

    private static bool UsesAlternateQuadDiagonal(
        ReadOnlySpan<CadPoint3D> contour)
    {
        if (!TryGetReferenceNormal(contour, out CadPoint3D normal))
        {
            return false;
        }

        int projectionAxis = DominantAxis(normal);
        Span<int> signs = stackalloc int[4];
        int positiveCount = 0;
        int negativeCount = 0;
        for (int vertex = 0; vertex < 4; vertex++)
        {
            CadPoint3D previous = contour[(vertex + 3) & 3];
            CadPoint3D current = contour[vertex];
            CadPoint3D next = contour[(vertex + 1) & 3];
            Project(
                current - previous,
                projectionAxis,
                out double incomingX,
                out double incomingY);
            Project(
                next - current,
                projectionAxis,
                out double outgoingX,
                out double outgoingY);
            double turn = (incomingX * outgoingY) - (incomingY * outgoingX);
            int sign = turn > 0.0 ? 1 : turn < 0.0 ? -1 : 0;
            signs[vertex] = sign;
            positiveCount += sign > 0 ? 1 : 0;
            negativeCount += sign < 0 ? 1 : 0;
        }

        int minoritySign = positiveCount == 1 && negativeCount >= 2
            ? 1
            : negativeCount == 1 && positiveCount >= 2
                ? -1
                : 0;
        if (minoritySign == 0)
        {
            return false;
        }
        int concaveVertex = signs.IndexOf(minoritySign);
        return concaveVertex is 1 or 3;
    }

    private static void TryAppendTriangle(
        Span<CadFaceSurfaceTriangle> destination,
        ref int count,
        CadPoint3D first,
        CadPoint3D second,
        CadPoint3D third,
        CadPoint3D? expectedNormal)
    {
        if (!CadMesh3DTopology.TryComputeFlatNormal(
                first,
                second,
                third,
                out CadPoint3D normal))
        {
            return;
        }
        if (expectedNormal.HasValue &&
            CadPoint3D.Dot(normal, expectedNormal.Value) < 0.0)
        {
            (second, third) = (third, second);
            normal *= -1.0;
        }
        destination[count++] = new CadFaceSurfaceTriangle(
            first,
            second,
            third,
            normal);
    }

    internal static int BuildSolidContours(
        in CadFacePrimitive face,
        Span<CadPoint3D> points,
        Span<int> lengths)
    {
        if (face.Fourth == face.Third)
        {
            points[0] = face.First;
            points[1] = face.Second;
            points[2] = face.Third;
            lengths[0] = 3;
            return 1;
        }

        Span<CadPoint3D> perimeter = stackalloc CadPoint3D[4]
        {
            face.First,
            face.Second,
            face.Third,
            face.Fourth,
        };
        if (TryGetReferenceNormal(perimeter, out CadPoint3D normal))
        {
            int projectionAxis = DominantAxis(normal);
            if (TryProperIntersection(
                    perimeter[0],
                    perimeter[1],
                    perimeter[2],
                    perimeter[3],
                    projectionAxis,
                    out CadPoint3D intersection))
            {
                points[0] = perimeter[1];
                points[1] = perimeter[2];
                points[2] = intersection;
                points[3] = perimeter[3];
                points[4] = perimeter[0];
                points[5] = intersection;
                lengths[0] = 3;
                lengths[1] = 3;
                return 2;
            }
            if (TryProperIntersection(
                    perimeter[1],
                    perimeter[2],
                    perimeter[3],
                    perimeter[0],
                    projectionAxis,
                    out intersection))
            {
                points[0] = perimeter[0];
                points[1] = perimeter[1];
                points[2] = intersection;
                points[3] = perimeter[2];
                points[4] = perimeter[3];
                points[5] = intersection;
                lengths[0] = 3;
                lengths[1] = 3;
                return 2;
            }
        }

        perimeter.CopyTo(points);
        lengths[0] = 4;
        return 1;
    }

    private static bool TryGetReferenceNormal(
        ReadOnlySpan<CadPoint3D> points,
        out CadPoint3D normal)
    {
        for (int second = 1; second < points.Length - 1; second++)
        {
            for (int third = second + 1; third < points.Length; third++)
            {
                if (CadMesh3DTopology.TryComputeFlatNormal(
                        points[0],
                        points[second],
                        points[third],
                        out normal))
                {
                    return true;
                }
            }
        }
        normal = default;
        return false;
    }

    private static bool TryGetContourNormal(
        ReadOnlySpan<CadPoint3D> points,
        out CadPoint3D normal)
    {
        CadPoint3D origin = points[0];
        double maximum = 0.0;
        for (int i = 1; i < points.Length; i++)
        {
            CadPoint3D delta = points[i] - origin;
            maximum = Math.Max(
                maximum,
                Math.Max(
                    Math.Abs(delta.X),
                    Math.Max(Math.Abs(delta.Y), Math.Abs(delta.Z))));
        }
        if (!double.IsFinite(maximum) || maximum == 0.0)
        {
            normal = default;
            return false;
        }

        CadPoint3D areaVector = CadPoint3D.Zero;
        for (int i = 1; i < points.Length; i++)
        {
            CadPoint3D first = (points[i] - origin) / maximum;
            CadPoint3D second = (points[(i + 1) % points.Length] - origin) / maximum;
            areaVector += CadPoint3D.Cross(first, second);
        }
        if (areaVector == CadPoint3D.Zero)
        {
            normal = default;
            return false;
        }

        normal = areaVector.Normalize();
        return true;
    }

    private static int DominantAxis(CadPoint3D normal)
    {
        double x = Math.Abs(normal.X);
        double y = Math.Abs(normal.Y);
        double z = Math.Abs(normal.Z);
        return x >= y && x >= z ? 0 : y >= z ? 1 : 2;
    }

    private static bool TryProperIntersection(
        CadPoint3D first,
        CadPoint3D firstEnd,
        CadPoint3D second,
        CadPoint3D secondEnd,
        int projectionAxis,
        out CadPoint3D intersection)
    {
        Project(
            firstEnd - first,
            projectionAxis,
            out double firstDeltaX,
            out double firstDeltaY);
        Project(
            secondEnd - second,
            projectionAxis,
            out double secondDeltaX,
            out double secondDeltaY);
        Project(second - first, projectionAxis, out double offsetX, out double offsetY);
        double maximum = Math.Max(
            Math.Max(Math.Abs(firstDeltaX), Math.Abs(firstDeltaY)),
            Math.Max(
                Math.Max(Math.Abs(secondDeltaX), Math.Abs(secondDeltaY)),
                Math.Max(Math.Abs(offsetX), Math.Abs(offsetY))));
        if (!double.IsFinite(maximum) || maximum == 0.0)
        {
            intersection = default;
            return false;
        }
        firstDeltaX /= maximum;
        firstDeltaY /= maximum;
        secondDeltaX /= maximum;
        secondDeltaY /= maximum;
        offsetX /= maximum;
        offsetY /= maximum;
        double denominator =
            (firstDeltaX * secondDeltaY) - (firstDeltaY * secondDeltaX);
        if (!double.IsFinite(denominator) || denominator == 0.0)
        {
            intersection = default;
            return false;
        }

        double firstParameter =
            ((offsetX * secondDeltaY) - (offsetY * secondDeltaX)) / denominator;
        double secondParameter =
            ((offsetX * firstDeltaY) - (offsetY * firstDeltaX)) / denominator;
        if (!double.IsFinite(firstParameter) ||
            !double.IsFinite(secondParameter) ||
            firstParameter <= IntersectionEndpointTolerance ||
            firstParameter >= 1.0 - IntersectionEndpointTolerance ||
            secondParameter <= IntersectionEndpointTolerance ||
            secondParameter >= 1.0 - IntersectionEndpointTolerance)
        {
            intersection = default;
            return false;
        }

        intersection = first + ((firstEnd - first) * firstParameter);
        return true;
    }

    private static void Project(
        CadPoint3D point,
        int projectionAxis,
        out double x,
        out double y)
    {
        if (projectionAxis == 0)
        {
            x = point.Y;
            y = point.Z;
        }
        else if (projectionAxis == 1)
        {
            x = point.X;
            y = point.Z;
        }
        else
        {
            x = point.X;
            y = point.Y;
        }
    }
}
