using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Tables;
using ACadSharp.XData;

namespace ProGPU.CAD;

public sealed partial class CadSnapshotCompiler
{
    private const double LeaderVertexTolerance = 1e-10;

    private readonly record struct CadLeaderArrowExpansion(
        BlockRecord Block,
        CadAffineTransform3D Transform);

    private readonly record struct CadLeaderDimensionContract(
        double ScaleFactor,
        double ArrowSize,
        double AnnotationGap,
        ACadSharp.Color Color,
        LineWeightType LineWeight,
        LineType LineType,
        BlockRecord? ArrowBlock);

    private static CadResolvedStyle ResolveLeaderStyle(
        Leader source,
        Layer effectiveLayer,
        in CadResolvedStyle entityStyle,
        CadSnapshotOptions options,
        out CadLeaderDimensionContract contract)
    {
        DimensionStyle dimensionStyle = source.Style ?? throw new ArgumentException(
            "LEADER has no dimension style.");
        contract = ResolveLeaderDimensionContract(source, dimensionStyle);
        ACadSharp.Color color = contract.Color.IsByLayer
            ? effectiveLayer.Color
            : contract.Color.IsByBlock
                ? entityStyle.Color
                : contract.Color;
        color = ResolveBackgroundAdaptiveColor(color, options.DrawingBackgroundColor);
        LineWeightType lineWeight = contract.LineWeight switch
        {
            LineWeightType.ByLayer => effectiveLayer.LineWeight,
            LineWeightType.ByBlock => entityStyle.LineWeight,
            _ => contract.LineWeight,
        };
        LineType authoredLineType = contract.LineType;
        LineType lineType = authoredLineType.Name.Equals(
            LineType.ByLayerName,
            StringComparison.OrdinalIgnoreCase)
            ? effectiveLayer.LineType
            : authoredLineType.Name.Equals(
                LineType.ByBlockName,
                StringComparison.OrdinalIgnoreCase)
                ? entityStyle.LineType
                : authoredLineType;
        return new CadResolvedStyle(
            color,
            lineWeight,
            lineType,
            entityStyle.Transparency,
            entityStyle.LineTypeScale,
            entityStyle.DefaultLineWeightMillimeters);
    }

    private static CadEntityHeader CompileLeader(
        Leader source,
        ulong rootHandle,
        CadAffineTransform3D parentTransform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        in CadLeaderDimensionContract dimensionContract,
        CadSnapshotOptions options,
        List<CadLeaderPrimitive> leaders,
        List<CadSplinePrimitive> splines,
        List<CadPoint3D> splineControlPoints,
        List<double> splineKnots,
        ref int retainedLeaderControlPoints,
        out CadLeaderArrowExpansion? customArrow)
    {
        customArrow = null;
        int vertexCount = source.Vertices.Count;
        if (vertexCount < 2)
        {
            throw new ArgumentException("LEADER requires at least two vertices.");
        }
        if (vertexCount > options.MaxLeaderVerticesPerEntity)
        {
            throw new CadUnsupportedEntityException(
                $"LEADER vertex count {vertexCount} exceeds the configured per-entity limit of {options.MaxLeaderVerticesPerEntity}.");
        }

        var points = new CadPoint3D[vertexCount];
        for (int index = 0; index < points.Length; index++)
        {
            points[index] = ToPoint(source.Vertices[index]);
            EnsureFinite(points[index]);
        }

        bool associated = TryResolveLeaderEndpoint(
            source,
            dimensionContract,
            points[^2],
            out CadPoint3D endpoint);
        if (associated)
        {
            points[^1] = endpoint;
        }
        for (int index = 1; index < points.Length; index++)
        {
            CadPoint3D delta = points[index] - points[index - 1];
            if (Math.Abs(delta.X) <= LeaderVertexTolerance &&
                Math.Abs(delta.Y) <= LeaderVertexTolerance &&
                Math.Abs(delta.Z) <= LeaderVertexTolerance)
            {
                throw new ArgumentException(
                    "LEADER consecutive vertices must be geometrically distinct.");
            }
        }

        int controlPointStart = splineControlPoints.Count;
        int knotStart = splineKnots.Count;
        int pathSplineIndex = splines.Count;
        int leaderPrimitiveStart = leaders.Count;
        bool chargedControlPoints = false;
        bool splineFit = source.PathType == LeaderPathType.Spline;
        int requiredControlPoints = splineFit
            ? checked(((vertexCount - 1) * 3) + 1)
            : vertexCount;
        if (requiredControlPoints > options.MaxLeaderControlPoints - retainedLeaderControlPoints)
        {
            throw new CadUnsupportedEntityException(
                $"LEADER retained control points exceed the configured document limit of {options.MaxLeaderControlPoints}.");
        }

        try
        {
            if (splineFit)
            {
                AppendSplineFitLeader(
                    source,
                    points,
                    parentTransform,
                    hasTransform,
                    splineControlPoints,
                    splineKnots);
            }
            else
            {
                AppendStraightLeader(
                    points,
                    parentTransform,
                    hasTransform,
                    splineControlPoints,
                    splineKnots);
            }

            int controlPointCount = splineControlPoints.Count - controlPointStart;
            int knotCount = splineKnots.Count - knotStart;
            retainedLeaderControlPoints = checked(retainedLeaderControlPoints + controlPointCount);
            chargedControlPoints = true;
            splines.Add(new CadSplinePrimitive(
                controlPointStart,
                controlPointCount,
                knotStart,
                knotCount,
                0,
                0,
                splineFit ? 3 : 1,
                IsClosed: false,
                IsPeriodic: false));

            CadBounds3D bounds = CadBounds3D.Empty;
            for (int index = controlPointStart; index < splineControlPoints.Count; index++)
            {
                bounds = bounds.Include(splineControlPoints[index]);
            }

            bool hasArrow = TryCreateLeaderArrow(
                source,
                dimensionContract,
                points,
                parentTransform,
                hasTransform,
                out CadPoint3D tip,
                out CadPoint3D firstBase,
                out CadPoint3D secondBase,
                out CadLeaderArrowExpansion? expansion);
            customArrow = expansion;
            bool hasDefaultArrow = hasArrow && expansion is null;
            if (hasDefaultArrow)
            {
                bounds = bounds.Include(tip).Include(firstBase).Include(secondBase);
            }

            int primitiveIndex = leaders.Count;
            leaders.Add(new CadLeaderPrimitive(
                pathSplineIndex,
                tip,
                firstBase,
                secondBase,
                hasDefaultArrow,
                splineFit,
                associated));
            return new CadEntityHeader(
                rootHandle,
                CadEntityKind.Leader,
                layerIndex,
                styleIndex,
                primitiveIndex,
                bounds);
        }
        catch
        {
            if (leaders.Count > leaderPrimitiveStart)
            {
                leaders.RemoveRange(leaderPrimitiveStart, leaders.Count - leaderPrimitiveStart);
            }
            if (splines.Count > pathSplineIndex)
            {
                splines.RemoveRange(pathSplineIndex, splines.Count - pathSplineIndex);
            }
            if (splineControlPoints.Count > controlPointStart)
            {
                int removed = splineControlPoints.Count - controlPointStart;
                splineControlPoints.RemoveRange(controlPointStart, removed);
                if (chargedControlPoints)
                {
                    retainedLeaderControlPoints -= removed;
                }
            }
            if (splineKnots.Count > knotStart)
            {
                splineKnots.RemoveRange(knotStart, splineKnots.Count - knotStart);
            }
            throw;
        }
    }

    private static void AppendStraightLeader(
        ReadOnlySpan<CadPoint3D> points,
        CadAffineTransform3D transform,
        bool hasTransform,
        List<CadPoint3D> controls,
        List<double> knots)
    {
        double distance = 0.0;
        knots.Add(0.0);
        knots.Add(0.0);
        controls.Add(TransformLeaderPoint(points[0], transform, hasTransform));
        for (int index = 1; index < points.Length; index++)
        {
            distance += (points[index] - points[index - 1]).Length;
            if (!double.IsFinite(distance) || distance <= 0.0)
            {
                throw new ArithmeticException("LEADER path length exceeds the supported numeric range.");
            }
            controls.Add(TransformLeaderPoint(points[index], transform, hasTransform));
            if (index < points.Length - 1)
            {
                knots.Add(distance);
            }
        }
        knots.Add(distance);
        knots.Add(distance);
    }

    private static void AppendSplineFitLeader(
        Leader source,
        ReadOnlySpan<CadPoint3D> points,
        CadAffineTransform3D transform,
        bool hasTransform,
        List<CadPoint3D> controls,
        List<double> knots)
    {
        int segmentCount = points.Length - 1;
        var parameters = new double[points.Length];
        for (int index = 1; index < points.Length; index++)
        {
            parameters[index] = parameters[index - 1] +
                (points[index] - points[index - 1]).Length;
            if (!double.IsFinite(parameters[index]) ||
                parameters[index] <= parameters[index - 1])
            {
                throw new ArithmeticException("LEADER spline parameterization is not finite and increasing.");
            }
        }

        var tangents = new CadPoint3D[points.Length];
        tangents[0] = (points[1] - points[0]) / parameters[1];
        for (int index = 1; index < points.Length - 1; index++)
        {
            double interval = parameters[index + 1] - parameters[index - 1];
            tangents[index] = (points[index + 1] - points[index - 1]) / interval;
        }

        CadPoint3D horizontal = ToPoint(source.HorizontalDirection);
        EnsureFinite(horizontal);
        if (horizontal.Length <= LeaderVertexTolerance)
        {
            horizontal = points[^1] - points[^2];
        }
        horizontal = horizontal.Normalize();
        tangents[^1] = source.HookLineDirection == HookLineDirection.Same
            ? horizontal
            : horizontal * -1.0;

        for (int index = 0; index < 4; index++)
        {
            knots.Add(parameters[0]);
        }
        controls.Add(TransformLeaderPoint(points[0], transform, hasTransform));
        for (int segment = 0; segment < segmentCount; segment++)
        {
            double interval = parameters[segment + 1] - parameters[segment];
            CadPoint3D firstControl = points[segment] +
                (tangents[segment] * (interval / 3.0));
            CadPoint3D secondControl = points[segment + 1] -
                (tangents[segment + 1] * (interval / 3.0));
            controls.Add(TransformLeaderPoint(firstControl, transform, hasTransform));
            controls.Add(TransformLeaderPoint(secondControl, transform, hasTransform));
            controls.Add(TransformLeaderPoint(points[segment + 1], transform, hasTransform));
            if (segment + 1 < segmentCount)
            {
                knots.Add(parameters[segment + 1]);
                knots.Add(parameters[segment + 1]);
                knots.Add(parameters[segment + 1]);
            }
        }
        for (int index = 0; index < 4; index++)
        {
            knots.Add(parameters[^1]);
        }
    }

    private static bool TryResolveLeaderEndpoint(
        Leader source,
        in CadLeaderDimensionContract dimensionContract,
        CadPoint3D penultimate,
        out CadPoint3D endpoint)
    {
        endpoint = default;
        Entity? annotation = source.AssociatedAnnotation;
        if (annotation is null)
        {
            return false;
        }

        CadPoint3D location;
        bool usesGap;
        switch (annotation)
        {
            case Insert insert:
                location = ToPoint(insert.InsertPoint);
                usesGap = false;
                break;
            case MText text:
                location = ToPoint(text.InsertPoint);
                usesGap = true;
                break;
            case Tolerance tolerance:
                location = ToPoint(tolerance.InsertionPoint);
                usesGap = true;
                break;
            default:
                return false;
        }

        endpoint = location + ToPoint(source.AnnotationOffset);
        if (usesGap)
        {
            CadPoint3D horizontal = ToPoint(source.HorizontalDirection);
            EnsureFinite(horizontal);
            if (horizontal.Length <= LeaderVertexTolerance)
            {
                horizontal = endpoint - penultimate;
            }
            horizontal = horizontal.Normalize();
            double gap = dimensionContract.AnnotationGap * dimensionContract.ScaleFactor;
            if (!double.IsFinite(gap))
            {
                throw new ArgumentException("LEADER annotation gap must be finite.");
            }
            endpoint += horizontal *
                (source.HookLineDirection == HookLineDirection.Same ? gap : -gap);
        }
        EnsureFinite(endpoint);
        return true;
    }

    private static bool TryCreateLeaderArrow(
        Leader source,
        in CadLeaderDimensionContract dimensionContract,
        ReadOnlySpan<CadPoint3D> points,
        CadAffineTransform3D parentTransform,
        bool hasTransform,
        out CadPoint3D tip,
        out CadPoint3D firstBase,
        out CadPoint3D secondBase,
        out CadLeaderArrowExpansion? customArrow)
    {
        tip = default;
        firstBase = default;
        secondBase = default;
        customArrow = null;
        double size = dimensionContract.ArrowSize * dimensionContract.ScaleFactor;
        if (!source.ArrowHeadEnabled || !double.IsFinite(size) || size <= 0.0)
        {
            return false;
        }

        CadPoint3D direction = points[1] - points[0];
        double firstLength = direction.Length;
        if (!double.IsFinite(firstLength) || firstLength < 2.0 * size)
        {
            return false;
        }
        direction /= firstLength;
        CadPoint3D normal = ToPoint(source.Normal);
        EnsureFinite(normal);
        normal = normal.Normalize();
        CadPoint3D perpendicular = CadPoint3D.Cross(normal, direction);
        if (perpendicular.Length <= LeaderVertexTolerance)
        {
            throw new ArgumentException("LEADER arrow direction is parallel to its normal.");
        }
        perpendicular = perpendicular.Normalize();

        CadPoint3D sourceTip = points[0];
        BlockRecord? block = dimensionContract.ArrowBlock;
        if (block is not null)
        {
            CadPoint3D basePoint = ToPoint(block.BlockEntity.BasePoint);
            CadPoint3D xAxis = direction * size;
            CadPoint3D yAxis = perpendicular * size;
            CadPoint3D zAxis = normal * size;
            var local = new CadAffineTransform3D(
                xAxis,
                yAxis,
                zAxis,
                sourceTip - (xAxis * basePoint.X) -
                    (yAxis * basePoint.Y) - (zAxis * basePoint.Z));
            CadAffineTransform3D world = hasTransform
                ? parentTransform.Compose(local)
                : local;
            EnsureFinite(world);
            customArrow = new CadLeaderArrowExpansion(block, world);
            return true;
        }

        CadPoint3D baseCenter = sourceTip + (direction * size);
        CadPoint3D halfWidth = perpendicular * (size / 6.0);
        tip = TransformLeaderPoint(sourceTip, parentTransform, hasTransform);
        firstBase = TransformLeaderPoint(baseCenter - halfWidth, parentTransform, hasTransform);
        secondBase = TransformLeaderPoint(baseCenter + halfWidth, parentTransform, hasTransform);
        return true;
    }

    private static CadPoint3D TransformLeaderPoint(
        CadPoint3D point,
        CadAffineTransform3D transform,
        bool hasTransform)
    {
        CadPoint3D result = hasTransform ? transform.TransformPoint(point) : point;
        EnsureFinite(result);
        return result;
    }

    private static CadLeaderDimensionContract ResolveLeaderDimensionContract(
        Leader source,
        DimensionStyle style)
    {
        double scaleFactor = style.ScaleFactor;
        double arrowSize = style.ArrowSize;
        double annotationGap = style.DimensionLineGap;
        ACadSharp.Color color = style.DimensionLineColor;
        LineWeightType lineWeight = style.DimensionLineWeight;
        LineType lineType = style.LineType ?? source.LineType;
        BlockRecord? arrowBlock = style.LeaderArrow;

        if (!source.ExtendedData.TryGet(AppId.DefaultName, out ExtendedData data) ||
            data.Records.Count == 0 ||
            data.Records[0] is not ExtendedDataString header ||
            !header.Value.Equals(DimensionStyle.StyleOverrideEntryName, StringComparison.Ordinal))
        {
            return ValidateLeaderDimensionContract(
                scaleFactor,
                arrowSize,
                annotationGap,
                color,
                lineWeight,
                lineType,
                arrowBlock);
        }

        int index = 1;
        if (index >= data.Records.Count ||
            data.Records[index] is not ExtendedDataControlString { IsClosing: false })
        {
            throw new ArgumentException("LEADER DSTYLE override has no opening control record.");
        }
        index++;
        bool closed = false;
        while (index < data.Records.Count)
        {
            if (data.Records[index] is ExtendedDataControlString { IsClosing: true })
            {
                closed = true;
                index++;
                break;
            }
            if (index + 1 >= data.Records.Count ||
                data.Records[index] is not ExtendedDataInteger16 code)
            {
                throw new ArgumentException("LEADER DSTYLE override must contain code/value pairs.");
            }

            ExtendedDataRecord value = data.Records[index + 1];
            switch (code.Value)
            {
                case 40:
                    scaleFactor = ReadLeaderOverrideReal(code.Value, value);
                    break;
                case 41:
                    arrowSize = ReadLeaderOverrideReal(code.Value, value);
                    break;
                case 147:
                    annotationGap = ReadLeaderOverrideReal(code.Value, value);
                    break;
                case 176:
                    color = new ACadSharp.Color(ReadLeaderOverrideInteger(code.Value, value));
                    break;
                case 341:
                    arrowBlock = ResolveLeaderOverrideReference<BlockRecord>(source, code.Value, value);
                    break;
                case 345:
                    lineType = ResolveLeaderOverrideReference<LineType>(source, code.Value, value) ??
                        throw new ArgumentException("LEADER DIMLTYPE override resolves to no linetype.");
                    break;
                case 371:
                    lineWeight = (LineWeightType)ReadLeaderOverrideInteger(code.Value, value);
                    break;
            }
            index += 2;
        }
        if (!closed)
        {
            throw new ArgumentException("LEADER DSTYLE override has an invalid closing control record.");
        }

        return ValidateLeaderDimensionContract(
            scaleFactor,
            arrowSize,
            annotationGap,
            color,
            lineWeight,
            lineType,
            arrowBlock);
    }

    private static CadLeaderDimensionContract ValidateLeaderDimensionContract(
        double scaleFactor,
        double arrowSize,
        double annotationGap,
        ACadSharp.Color color,
        LineWeightType lineWeight,
        LineType lineType,
        BlockRecord? arrowBlock)
    {
        if (!double.IsFinite(scaleFactor) || scaleFactor < 0.0 ||
            !double.IsFinite(arrowSize) || arrowSize < 0.0 ||
            !double.IsFinite(annotationGap))
        {
            throw new ArgumentException(
                "LEADER dimension scale, arrow size, and annotation gap must be finite and non-negative where required.");
        }
        ArgumentNullException.ThrowIfNull(lineType);
        return new CadLeaderDimensionContract(
            scaleFactor,
            arrowSize,
            annotationGap,
            color,
            lineWeight,
            lineType,
            arrowBlock);
    }

    private static double ReadLeaderOverrideReal(
        short code,
        ExtendedDataRecord value) =>
        value is ExtendedDataReal real && double.IsFinite(real.Value)
            ? real.Value
            : throw new ArgumentException(
                $"LEADER DSTYLE code {code} requires a finite real value.");

    private static short ReadLeaderOverrideInteger(
        short code,
        ExtendedDataRecord value) =>
        value is ExtendedDataInteger16 integer
            ? integer.Value
            : throw new ArgumentException(
                $"LEADER DSTYLE code {code} requires a 16-bit integer value.");

    private static T? ResolveLeaderOverrideReference<T>(
        Leader source,
        short code,
        ExtendedDataRecord value)
        where T : CadObject
    {
        if (value is not ExtendedDataHandle handle)
        {
            throw new ArgumentException(
                $"LEADER DSTYLE code {code} requires an object-handle value.");
        }
        if (handle.Value == 0)
        {
            return null;
        }
        if (source.Document is null ||
            !source.Document.TryGetCadObject(handle.Value, out T resolved))
        {
            throw new ArgumentException(
                $"LEADER DSTYLE code {code} references unavailable handle {handle.Value:X}.");
        }
        return resolved;
    }
}
