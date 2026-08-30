using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace ProGPU.CAD;

public sealed partial class CadSnapshotCompiler
{
    private readonly record struct CadMultiLeaderLineContract(
        MultiLeaderPathType PathType,
        ACadSharp.Color Color,
        LineType LineType,
        LineWeightType LineWeight,
        BlockRecord? ArrowBlock,
        double ArrowSize);

    private readonly record struct CadMultiLeaderPathResult(
        CadEntityHeader Header,
        CadLeaderArrowExpansion? CustomArrow);

    private static CadMultiLeaderLineContract ResolveMultiLeaderLineContract(
        MultiLeader source,
        MultiLeaderObjectContextData.LeaderLine line)
    {
        MultiLeaderStyle style = source.Style ?? throw new ArgumentException(
            "MULTILEADER has no MLEADERSTYLE.");
        MultiLeaderPropertyOverrideFlags entityFlags = source.PropertyOverrideFlags;
        LeaderLinePropertOverrideFlags lineFlags = line.OverrideFlags;

        MultiLeaderPathType pathType = entityFlags.HasFlag(MultiLeaderPropertyOverrideFlags.PathType)
            ? source.PathType
            : style.PathType;
        ACadSharp.Color color = entityFlags.HasFlag(MultiLeaderPropertyOverrideFlags.LineColor)
            ? source.LineColor
            : style.LineColor;
        LineType lineType = entityFlags.HasFlag(MultiLeaderPropertyOverrideFlags.LeaderLineType)
            ? source.LeaderLineType
            : style.LeaderLineType;
        LineWeightType lineWeight = entityFlags.HasFlag(MultiLeaderPropertyOverrideFlags.LeaderLineWeight)
            ? source.LeaderLineWeight
            : style.LeaderLineWeight;
        BlockRecord? arrowBlock = entityFlags.HasFlag(MultiLeaderPropertyOverrideFlags.Arrowhead)
            ? source.Arrowhead
            : style.Arrowhead;
        double arrowSize = entityFlags.HasFlag(MultiLeaderPropertyOverrideFlags.ArrowheadSize)
            ? source.ContextData.ArrowheadSize
            : style.ArrowheadSize * style.ScaleFactor;

        if (lineFlags.HasFlag(LeaderLinePropertOverrideFlags.PathType))
        {
            pathType = line.PathType;
        }
        if (lineFlags.HasFlag(LeaderLinePropertOverrideFlags.LineColor))
        {
            color = line.LineColor;
        }
        if (lineFlags.HasFlag(LeaderLinePropertOverrideFlags.LineType))
        {
            lineType = line.LineType ?? throw new ArgumentException(
                "MULTILEADER line linetype override resolves to no linetype.");
        }
        if (lineFlags.HasFlag(LeaderLinePropertOverrideFlags.LineWeight))
        {
            lineWeight = line.LineWeight;
        }
        if (lineFlags.HasFlag(LeaderLinePropertOverrideFlags.Arrowhead))
        {
            arrowBlock = line.Arrowhead;
        }
        if (lineFlags.HasFlag(LeaderLinePropertOverrideFlags.ArrowheadSize))
        {
            arrowSize = line.ArrowheadSize;
        }

        if (lineType is null)
        {
            throw new ArgumentException("MULTILEADER effective linetype is missing.");
        }
        if (!double.IsFinite(arrowSize) || arrowSize < 0.0)
        {
            throw new ArgumentException("MULTILEADER effective arrow size must be finite and non-negative.");
        }
        return new CadMultiLeaderLineContract(
            pathType,
            color,
            lineType,
            lineWeight,
            arrowBlock,
            arrowSize);
    }

    private static CadResolvedStyle ResolveMultiLeaderStyle(
        in CadMultiLeaderLineContract contract,
        Layer effectiveLayer,
        in CadResolvedStyle entityStyle,
        CadSnapshotOptions options)
    {
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
        LineType lineType = contract.LineType.Name.Equals(
            LineType.ByLayerName,
            StringComparison.OrdinalIgnoreCase)
            ? effectiveLayer.LineType
            : contract.LineType.Name.Equals(
                LineType.ByBlockName,
                StringComparison.OrdinalIgnoreCase)
                ? entityStyle.LineType
                : contract.LineType;
        return new CadResolvedStyle(
            color,
            lineWeight,
            lineType,
            entityStyle.Transparency,
            entityStyle.LineTypeScale,
            entityStyle.DefaultLineWeightMillimeters);
    }

    private static CadMultiLeaderPathResult CompileMultiLeaderPath(
        ReadOnlySpan<CadPoint3D> sourcePoints,
        CadPoint3D planeNormal,
        ulong rootHandle,
        CadAffineTransform3D parentTransform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        in CadMultiLeaderLineContract contract,
        int leaderRootIndex,
        int leaderLineIndex,
        bool isDogleg,
        CadSnapshotOptions options,
        List<CadMultiLeaderPrimitive> multiLeaders,
        List<CadSplinePrimitive> splines,
        List<CadPoint3D> splineControlPoints,
        List<double> splineKnots,
        ref int retainedControlPoints)
    {
        if (sourcePoints.Length < 2)
        {
            throw new ArgumentException("MULTILEADER retained path requires at least two points.");
        }
        if (sourcePoints.Length > options.MaxMultiLeaderVerticesPerPath)
        {
            throw new CadUnsupportedEntityException(
                $"MULTILEADER path vertex count {sourcePoints.Length} exceeds the configured per-path limit of {options.MaxMultiLeaderVerticesPerPath}.");
        }
        for (int index = 0; index < sourcePoints.Length; index++)
        {
            EnsureFinite(sourcePoints[index]);
            if (index > 0 && (sourcePoints[index] - sourcePoints[index - 1]).Length <= LeaderVertexTolerance)
            {
                throw new ArgumentException("MULTILEADER consecutive path points must be geometrically distinct.");
            }
        }

        bool splineFit = !isDogleg && contract.PathType == MultiLeaderPathType.Spline;
        int requiredControls = splineFit
            ? checked(((sourcePoints.Length - 1) * 3) + 1)
            : sourcePoints.Length;
        if (requiredControls > options.MaxMultiLeaderControlPoints - retainedControlPoints)
        {
            throw new CadUnsupportedEntityException(
                $"MULTILEADER retained control points exceed the configured document limit of {options.MaxMultiLeaderControlPoints}.");
        }

        int controlStart = splineControlPoints.Count;
        int knotStart = splineKnots.Count;
        int splineIndex = splines.Count;
        int primitiveStart = multiLeaders.Count;
        bool charged = false;
        try
        {
            if (splineFit)
            {
                AppendSplineFitMultiLeader(
                    sourcePoints,
                    parentTransform,
                    hasTransform,
                    splineControlPoints,
                    splineKnots);
            }
            else
            {
                AppendStraightLeader(
                    sourcePoints,
                    parentTransform,
                    hasTransform,
                    splineControlPoints,
                    splineKnots);
            }

            int controlCount = splineControlPoints.Count - controlStart;
            int knotCount = splineKnots.Count - knotStart;
            retainedControlPoints = checked(retainedControlPoints + controlCount);
            charged = true;
            splines.Add(new CadSplinePrimitive(
                controlStart,
                controlCount,
                knotStart,
                knotCount,
                0,
                0,
                splineFit ? 3 : 1,
                IsClosed: false,
                IsPeriodic: false));

            CadBounds3D bounds = CadBounds3D.Empty;
            for (int index = controlStart; index < splineControlPoints.Count; index++)
            {
                bounds = bounds.Include(splineControlPoints[index]);
            }

            CadLeaderArrowExpansion? customArrow = null;
            CadPoint3D tip = default;
            CadPoint3D firstBase = default;
            CadPoint3D secondBase = default;
            bool hasDefaultArrow = !isDogleg && TryCreateMultiLeaderArrow(
                sourcePoints,
                planeNormal,
                contract.ArrowSize,
                contract.ArrowBlock,
                parentTransform,
                hasTransform,
                out tip,
                out firstBase,
                out secondBase,
                out customArrow) && customArrow is null;
            if (hasDefaultArrow)
            {
                bounds = bounds.Include(tip).Include(firstBase).Include(secondBase);
            }

            int primitiveIndex = multiLeaders.Count;
            multiLeaders.Add(new CadMultiLeaderPrimitive(
                splineIndex,
                tip,
                firstBase,
                secondBase,
                hasDefaultArrow,
                splineFit,
                isDogleg,
                leaderRootIndex,
                leaderLineIndex));
            return new CadMultiLeaderPathResult(
                new CadEntityHeader(
                    rootHandle,
                    CadEntityKind.MultiLeader,
                    layerIndex,
                    styleIndex,
                    primitiveIndex,
                    bounds),
                customArrow);
        }
        catch
        {
            if (multiLeaders.Count > primitiveStart)
            {
                multiLeaders.RemoveRange(primitiveStart, multiLeaders.Count - primitiveStart);
            }
            if (splines.Count > splineIndex)
            {
                splines.RemoveRange(splineIndex, splines.Count - splineIndex);
            }
            if (splineControlPoints.Count > controlStart)
            {
                int removed = splineControlPoints.Count - controlStart;
                splineControlPoints.RemoveRange(controlStart, removed);
                if (charged)
                {
                    retainedControlPoints -= removed;
                }
            }
            if (splineKnots.Count > knotStart)
            {
                splineKnots.RemoveRange(knotStart, splineKnots.Count - knotStart);
            }
            throw;
        }
    }

    private static void AppendSplineFitMultiLeader(
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
        }

        var tangents = new CadPoint3D[points.Length];
        tangents[0] = (points[1] - points[0]) / parameters[1];
        for (int index = 1; index < points.Length - 1; index++)
        {
            tangents[index] = (points[index + 1] - points[index - 1]) /
                (parameters[index + 1] - parameters[index - 1]);
        }
        tangents[^1] = (points[^1] - points[^2]) /
            (parameters[^1] - parameters[^2]);

        for (int index = 0; index < 4; index++)
        {
            knots.Add(parameters[0]);
        }
        controls.Add(TransformLeaderPoint(points[0], transform, hasTransform));
        for (int segment = 0; segment < segmentCount; segment++)
        {
            double interval = parameters[segment + 1] - parameters[segment];
            controls.Add(TransformLeaderPoint(
                points[segment] + (tangents[segment] * (interval / 3.0)),
                transform,
                hasTransform));
            controls.Add(TransformLeaderPoint(
                points[segment + 1] - (tangents[segment + 1] * (interval / 3.0)),
                transform,
                hasTransform));
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

    private static bool TryCreateMultiLeaderArrow(
        ReadOnlySpan<CadPoint3D> points,
        CadPoint3D planeNormal,
        double size,
        BlockRecord? block,
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
        if (size <= 0.0)
        {
            return false;
        }

        CadPoint3D direction = points[1] - points[0];
        double length = direction.Length;
        if (!double.IsFinite(length) || length <= LeaderVertexTolerance)
        {
            return false;
        }
        direction /= length;
        if (planeNormal.Length <= LeaderVertexTolerance)
        {
            planeNormal = new CadPoint3D(0.0, 0.0, 1.0);
        }
        planeNormal = planeNormal.Normalize();
        CadPoint3D perpendicular = CadPoint3D.Cross(planeNormal, direction);
        if (perpendicular.Length <= LeaderVertexTolerance)
        {
            throw new ArgumentException("MULTILEADER arrow direction is parallel to its plane normal.");
        }
        perpendicular = perpendicular.Normalize();

        CadPoint3D sourceTip = points[0];
        if (block is not null)
        {
            CadPoint3D basePoint = ToPoint(block.BlockEntity.BasePoint);
            CadPoint3D xAxis = direction * size;
            CadPoint3D yAxis = perpendicular * size;
            CadPoint3D zAxis = planeNormal * size;
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

    private static MText CreateMultiLeaderMText(
        MultiLeader source,
        in CadResolvedStyle entityStyle)
    {
        MultiLeaderObjectContextData context = source.ContextData;
        MultiLeaderStyle style = source.Style ?? throw new ArgumentException(
            "MULTILEADER has no MLEADERSTYLE.");
        if (!context.HasTextContents || string.IsNullOrEmpty(context.TextLabel))
        {
            throw new CadUnsupportedEntityException(
                "MULTILEADER MTEXT content is declared but its embedded text payload is absent.");
        }
        if (context.FlowDirection is FlowDirectionType.Vertical)
        {
            throw new CadUnsupportedEntityException(
                "Vertical MULTILEADER MTEXT requires vertical shaping and glyph orientation.");
        }

        double height = context.TextHeight;
        if (!double.IsFinite(height) || height <= 0.0)
        {
            height = style.TextHeight * style.ScaleFactor;
        }
        if (!double.IsFinite(height) || height <= 0.0)
        {
            throw new ArgumentException(
                "MULTILEADER effective text height must be finite and positive.");
        }

        XYZ direction = context.Direction;
        if (!double.IsFinite(direction.X) || !double.IsFinite(direction.Y) ||
            !double.IsFinite(direction.Z) || direction.GetLength() <= LeaderVertexTolerance)
        {
            double rotation = context.TextRotation;
            if (!double.IsFinite(rotation))
            {
                throw new ArgumentException("MULTILEADER text rotation must be finite.");
            }
            direction = new XYZ(Math.Cos(rotation), Math.Sin(rotation), 0.0);
        }

        AttachmentPointType attachment = context.TextAttachmentPoint switch
        {
            TextAttachmentPointType.Left => AttachmentPointType.TopLeft,
            TextAttachmentPointType.Center => AttachmentPointType.TopCenter,
            TextAttachmentPointType.Right => AttachmentPointType.TopRight,
            _ => throw new CadUnsupportedEntityException(
                $"MULTILEADER text attachment point {context.TextAttachmentPoint} is reserved."),
        };
        LineSpacingStyleType spacingStyle = context.LineSpacing switch
        {
            LineSpacingStyle.AtLeast => LineSpacingStyleType.AtLeast,
            LineSpacingStyle.Exactly => LineSpacingStyleType.Exact,
            _ => LineSpacingStyleType.AtLeast,
        };
        double spacing = context.LineSpacingFactor;
        if (!double.IsFinite(spacing) || spacing is < 0.25 or > 4.0)
        {
            spacing = 1.0;
        }

        ACadSharp.Color textColor = source.PropertyOverrideFlags.HasFlag(
            MultiLeaderPropertyOverrideFlags.TextColor)
            ? context.TextColor
            : style.TextColor;
        if (textColor.IsByBlock)
        {
            textColor = entityStyle.Color;
        }

        BackgroundFillFlags backgroundFlags = BackgroundFillFlags.None;
        if (context.BackgroundFillEnabled)
        {
            backgroundFlags |= context.BackgroundMaskFillOn
                ? BackgroundFillFlags.UseDrawingWindowColor
                : BackgroundFillFlags.UseBackgroundFillColor;
        }
        bool textFrame = source.PropertyOverrideFlags.HasFlag(
            MultiLeaderPropertyOverrideFlags.TextFrame)
            ? source.TextFrame
            : style.TextFrame;
        if (textFrame)
        {
            backgroundFlags |= BackgroundFillFlags.TextFrame;
        }
        double backgroundScale = context.BackgroundScaleFactor;
        if (!double.IsFinite(backgroundScale) || backgroundScale is < 1.0 or > 5.0)
        {
            backgroundScale = 1.5;
        }

        var mtext = new MText(context.TextLabel)
        {
            Layer = source.Layer,
            Color = textColor,
            LineType = source.LineType,
            LineWeight = source.LineWeight,
            LineTypeScale = source.LineTypeScale,
            Transparency = source.Transparency,
            InsertPoint = context.TextLocation,
            AlignmentPoint = direction,
            Normal = context.TextNormal.GetLength() > LeaderVertexTolerance
                ? context.TextNormal
                : XYZ.AxisZ,
            AttachmentPoint = attachment,
            DrawingDirection = DrawingDirectionType.LeftToRight,
            Height = height,
            RectangleWidth = Math.Max(0.0, context.BoundaryWidth),
            RectangleHeight = Math.Max(0.0, context.BoundaryHeight),
            LineSpacing = spacing,
            LineSpacingStyle = spacingStyle,
            Style = context.TextStyle ?? style.TextStyle,
            BackgroundFillFlags = backgroundFlags,
            BackgroundColor = context.BackgroundFillColor,
            BackgroundScale = backgroundScale,
        };
        if (context.BackgroundTransparency != 0)
        {
            mtext.BackgroundTransparency = Transparency.FromAlphaValue(
                context.BackgroundTransparency);
        }

        if (context.ColumnType != 0)
        {
            mtext.ColumnData.ColumnType = context.ColumnType switch
            {
                1 => ColumnType.StaticColumns,
                2 => ColumnType.DynamicColumns,
                _ => throw new CadUnsupportedEntityException(
                    $"MULTILEADER MTEXT column type {context.ColumnType} is reserved."),
            };
            mtext.ColumnData.ColumnCount = context.ColumnSizes.Count > 0
                ? context.ColumnSizes.Count
                : 1;
            mtext.ColumnData.FlowReversed = context.ColumnFlowReversed;
            mtext.ColumnData.Width = context.ColumnWidth;
            mtext.ColumnData.Gutter = context.ColumnGutter;
            foreach (double columnHeight in context.ColumnSizes)
            {
                mtext.ColumnData.Heights.Add(columnHeight);
            }
        }
        return mtext;
    }

    private static CadAffineTransform3D CreateMultiLeaderBlockTransform(
        MultiLeader source,
        out BlockRecord block)
    {
        MultiLeaderObjectContextData context = source.ContextData;
        MultiLeaderStyle style = source.Style ?? throw new ArgumentException(
            "MULTILEADER has no MLEADERSTYLE.");
        block = context.BlockContent ??
            (source.PropertyOverrideFlags.HasFlag(MultiLeaderPropertyOverrideFlags.BlockContent)
                ? source.BlockContent
                : style.BlockContent) ??
            throw new CadUnsupportedEntityException(
                "MULTILEADER block content is declared but its block definition is absent.");
        Matrix4 matrix = context.TransformationMatrix;
        if (!double.IsFinite(matrix.M00) || !double.IsFinite(matrix.M01) ||
            !double.IsFinite(matrix.M02) || !double.IsFinite(matrix.M03) ||
            !double.IsFinite(matrix.M10) || !double.IsFinite(matrix.M11) ||
            !double.IsFinite(matrix.M12) || !double.IsFinite(matrix.M13) ||
            !double.IsFinite(matrix.M20) || !double.IsFinite(matrix.M21) ||
            !double.IsFinite(matrix.M22) || !double.IsFinite(matrix.M23) ||
            !double.IsFinite(matrix.M30) || !double.IsFinite(matrix.M31) ||
            !double.IsFinite(matrix.M32) || !double.IsFinite(matrix.M33) ||
            Math.Abs(matrix.M03) > LeaderVertexTolerance ||
            Math.Abs(matrix.M13) > LeaderVertexTolerance ||
            Math.Abs(matrix.M23) > LeaderVertexTolerance ||
            Math.Abs(matrix.M33 - 1.0) > LeaderVertexTolerance)
        {
            throw new CadUnsupportedEntityException(
                "MULTILEADER block content requires its persisted finite affine transformation matrix.");
        }

        var transform = new CadAffineTransform3D(
            new CadPoint3D(matrix.M00, matrix.M01, matrix.M02),
            new CadPoint3D(matrix.M10, matrix.M11, matrix.M12),
            new CadPoint3D(matrix.M20, matrix.M21, matrix.M22),
            new CadPoint3D(matrix.M30, matrix.M31, matrix.M32));
        EnsureFinite(transform);
        if (transform.XAxis.Length <= LeaderVertexTolerance ||
            transform.YAxis.Length <= LeaderVertexTolerance ||
            transform.ZAxis.Length <= LeaderVertexTolerance)
        {
            throw new CadUnsupportedEntityException(
                "MULTILEADER block content transformation is singular.");
        }
        return transform;
    }
}
