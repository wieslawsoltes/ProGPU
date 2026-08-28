using System.Buffers;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.CAD;

public sealed class CadPlanSceneOptions
{
    public const int DefaultMaxLineTypeFigures = 1_000_000;
    public const int DefaultMaxLineTypePatternSteps = 4_000_000;
    public const int DefaultMaxLineTypeSourceSegments = 1_000_000;
    public const int DefaultMaxLineTypeArcMapsPerEntity = 16_384;
    public const int DefaultMaxLineTypePlacements = 1_000_000;
    public const int DefaultMaxHatchPatternAuxiliaryRecords = 65_536;

    public float PhysicalDpi { get; init; } = 96.0f;
    public float LineWeightScale { get; init; } = 1.0f;
    public bool IncludeNonPlottableLayers { get; init; } = true;
    public int MaxLineTypeFigures { get; init; } = DefaultMaxLineTypeFigures;
    public int MaxLineTypePatternSteps { get; init; } = DefaultMaxLineTypePatternSteps;
    public int MaxLineTypeSourceSegments { get; init; } = DefaultMaxLineTypeSourceSegments;
    public int MaxLineTypeArcMapsPerEntity { get; init; } = DefaultMaxLineTypeArcMapsPerEntity;
    public int MaxLineTypePlacements { get; init; } = DefaultMaxLineTypePlacements;
    public int MaxHatchPatternAuxiliaryRecords { get; init; } =
        DefaultMaxHatchPatternAuxiliaryRecords;
}

public readonly record struct CadPlanSceneStatistics(
    int RecordedEntityCount,
    int RecordedCommandCount,
    int UnsupportedLineTypeCount,
    int LoweredLineTypeEntityCount,
    int LoweredLineTypeFigureCount,
    int LoweredLineTypePlacementCount,
    int LineTypePatternStepCount,
    int LineTypeSourceSegmentCount);

/// <summary>A retained top/WCS-XY projection ready for ordinary ProGPU compilation.</summary>
public sealed class CadRecordedPlanScene
{
    private readonly CadDiagnostic[] _diagnostics;

    public ulong ContentGeneration { get; }
    public CadPoint3D RebaseOrigin { get; }
    public DrawingContext DrawingContext { get; }
    public CadPlanSceneStatistics Statistics { get; }
    public ReadOnlyMemory<CadDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Freezes the recorded CAD commands and side buffers into an independently
    /// owned picture suitable for repeated camera-only replay.
    /// </summary>
    public GpuPicture CreatePicture()
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext target = recorder.BeginRecording(new Rect(0, 0, 1, 1));
        target.Append(DrawingContext);
        return recorder.EndRecording();
    }

    internal CadRecordedPlanScene(
        ulong contentGeneration,
        CadPoint3D rebaseOrigin,
        DrawingContext drawingContext,
        CadPlanSceneStatistics statistics,
        CadDiagnostic[] diagnostics)
    {
        ContentGeneration = contentGeneration;
        RebaseOrigin = rebaseOrigin;
        DrawingContext = drawingContext;
        Statistics = statistics;
        _diagnostics = diagnostics;
    }
}

/// <summary>
/// Records an exact orthographic WCS-XY projection into retained ProGPU primitives.
/// </summary>
/// <remarks>
/// This original ProGPU compiler consumes the immutable CAD snapshot and the existing
/// ProGPU-owned analytic line, circle, path-arc, and spline recording APIs. It performs
/// no viewport tessellation and owns no camera state. Recording is O(N + P), where N is
/// the entity count and P is the total spline control/knot/weight data copied into the
/// retained context. Large WCS coordinates are rebased before their checked float
/// conversion. Text adds O(R + G + D) retained commands for R contiguous
/// TrueType runs, G drawable SHX paths, and D decoration/mask/separator
/// primitives; it does not copy or reinterpret glyph streams. CAD linetypes add
/// O(F + P + C) retained analytic
/// figures, placement commands, and exact rational fragment values for F visible
/// dash/dot figures, P embedded text/shape placements, and C emitted spline
/// control/knot/weight values, bounded before allocation by options. Complex payloads
/// are shaped or interpreted once per referenced definition and shared by all P
/// occurrences.
/// Normal HATCH compilation adds O(L + S) retained boundary work for L loops
/// and S analytic segments plus O(1) procedural brush work for a supported
/// continuous pattern family; it never emits geometry per pattern line.
/// A later
/// camera or viewport change can reuse the recorded scene.
/// </remarks>
public sealed class CadPlanSceneCompiler
{
    private const double TwoPi = Math.PI * 2.0;

    public CadRecordedPlanScene Compile(
        CadDocumentSnapshot snapshot,
        CadPlanSceneOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new CadPlanSceneOptions();
        ValidateOptions(options);

        ReadOnlySpan<CadEntityHeader> entities = snapshot.Entities.Span;
        ReadOnlySpan<CadLayerSnapshot> layers = snapshot.Layers.Span;
        ReadOnlySpan<CadStrokeStyle> styles = snapshot.Styles.Span;
        ReadOnlySpan<CadLineTypePattern> lineTypePatterns = snapshot.LineTypePatterns.Span;
        var context = new DrawingContext();
        context.EnsureCommandCapacity(checked(
            entities.Length +
            Math.Max(0, snapshot.TextGlyphRuns.Length - snapshot.Texts.Length) +
            snapshot.TextDecorations.Length +
            snapshot.MTextGlyphRuns.Length +
            snapshot.MTextBackgrounds.Length +
            snapshot.MTextDecorations.Length +
            snapshot.MTextStrokes.Length +
            snapshot.ShxGlyphInstances.Length +
            snapshot.ShxDecorationSegments.Length));
        Pen[] pens = CreatePens(styles, options);
        var mtextBrushes = new Dictionary<uint, Brush>();
        var diagnostics = new List<CadDiagnostic>();
        var warnedLineTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnedLineTypeSubstitutions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int recorded = 0;
        int unsupportedLineTypes = 0;
        int loweredLineTypeEntities = 0;
        int loweredLineTypeFigures = 0;
        int loweredLineTypePlacements = 0;
        int lineTypeFigureBudgetUsed = 0;
        int lineTypePatternSteps = 0;
        int lineTypeSourceSegments = 0;
        int lineTypePlacementBudgetUsed = 0;
        int hatchPatternAuxiliaryRecords = 0;

        foreach (CadEntityHeader entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!options.IncludeNonPlottableLayers &&
                !layers[entity.LayerIndex].IsPlottable)
            {
                continue;
            }

            CadStrokeStyle style = styles[entity.StyleIndex];
            Pen pen = pens[entity.StyleIndex];
            bool recordedLineType = false;
            if (UsesStroke(entity.Kind))
            {
                CadLineTypePattern pattern = lineTypePatterns[style.LineTypePatternIndex];
                if (pattern.Kind is CadLineTypePatternKind.Simple or CadLineTypePatternKind.Complex)
                {
                    int remainingFigures = options.MaxLineTypeFigures - lineTypeFigureBudgetUsed;
                    int remainingPatternSteps =
                        options.MaxLineTypePatternSteps - lineTypePatternSteps;
                    int remainingSourceSegments =
                        options.MaxLineTypeSourceSegments - lineTypeSourceSegments;
                    int remainingPlacements =
                        options.MaxLineTypePlacements - lineTypePlacementBudgetUsed;
                    CadLineTypeLoweringResult result = CadLineTypeLowerer.Lower(
                        snapshot,
                        entity,
                        style,
                        pattern,
                        Math.Max(0, remainingFigures),
                        Math.Max(0, remainingPatternSteps),
                        Math.Max(0, remainingSourceSegments),
                        options.MaxLineTypeArcMapsPerEntity,
                        Math.Max(0, remainingPlacements));
                    lineTypeFigureBudgetUsed = checked(
                        lineTypeFigureBudgetUsed +
                        Math.Min(Math.Max(0, remainingFigures), result.FigureCount));
                    lineTypePatternSteps = checked(
                        lineTypePatternSteps +
                        Math.Min(Math.Max(0, remainingPatternSteps), result.PatternStepCount));
                    lineTypeSourceSegments = checked(
                        lineTypeSourceSegments +
                        Math.Min(Math.Max(0, remainingSourceSegments), result.SourceSegmentCount));
                    lineTypePlacementBudgetUsed = checked(
                        lineTypePlacementBudgetUsed +
                        Math.Min(Math.Max(0, remainingPlacements), result.PlacementCount));
                    if (result.Status == CadLineTypeLoweringStatus.Lowered)
                    {
                        if (HasLineTypeSubstitution(snapshot, pattern) &&
                            warnedLineTypeSubstitutions.Add(pattern.Name))
                        {
                            diagnostics.Add(new CadDiagnostic(
                                CadDiagnosticSeverity.Warning,
                                "CADSCENE003",
                                $"Linetype '{pattern.Name}' uses a host-resolved text or SHX substitution."));
                        }
                        if (result.Path is not null)
                        {
                            context.DrawPath(null, pen, result.Path, result.Transform);
                        }
                        RecordLineTypeSplineFragments(context, pen, result);
                        RecordLineTypePlacements(
                            context,
                            pen,
                            snapshot,
                            style,
                            pattern,
                            result);
                        loweredLineTypeEntities++;
                        loweredLineTypeFigures = checked(
                            loweredLineTypeFigures + result.FigureCount);
                        loweredLineTypePlacements = checked(
                            loweredLineTypePlacements + result.PlacementCount);
                        recordedLineType = true;
                    }
                    else if (result.Status is
                        CadLineTypeLoweringStatus.UnsupportedEntity or
                        CadLineTypeLoweringStatus.FigureLimitExceeded or
                        CadLineTypeLoweringStatus.PatternStepLimitExceeded or
                        CadLineTypeLoweringStatus.SourceSegmentLimitExceeded or
                        CadLineTypeLoweringStatus.ArcMapLimitExceeded or
                        CadLineTypeLoweringStatus.PlacementLimitExceeded or
                        CadLineTypeLoweringStatus.UnresolvedComplexElement)
                    {
                        string reason = result.Status switch
                        {
                            CadLineTypeLoweringStatus.UnsupportedEntity =>
                                $"entity kind {entity.Kind} has no exact analytic linetype splitter",
                            CadLineTypeLoweringStatus.FigureLimitExceeded =>
                                $"the configured {options.MaxLineTypeFigures}-figure document limit was reached",
                            CadLineTypeLoweringStatus.PatternStepLimitExceeded =>
                                $"the configured {options.MaxLineTypePatternSteps}-step pattern traversal limit was reached",
                            CadLineTypeLoweringStatus.SourceSegmentLimitExceeded =>
                                $"the configured {options.MaxLineTypeSourceSegments}-segment document traversal limit was reached",
                            CadLineTypeLoweringStatus.PlacementLimitExceeded =>
                                $"the configured {options.MaxLineTypePlacements}-placement document limit was reached",
                            CadLineTypeLoweringStatus.UnresolvedComplexElement =>
                                "an embedded text/shape resource or persisted rotation contract is unresolved",
                            _ =>
                                $"the configured {options.MaxLineTypeArcMapsPerEntity}-arc per-entity map limit was reached",
                        };
                        AddUnsupportedLineTypeDiagnostic(
                            pattern.Name,
                            reason,
                            "CADSCENE002");
                    }
                }
                else if (pattern.Kind != CadLineTypePatternKind.Continuous)
                {
                    string reason = pattern.Kind == CadLineTypePatternKind.Complex
                        ? "embedded text/shape elements require complex-linetype lowering"
                        : $"alignment '{pattern.Alignment}' is not the documented AutoCAD A alignment";
                    AddUnsupportedLineTypeDiagnostic(
                        pattern.Name,
                        reason,
                        "CADSCENE001");
                }
            }

            if (recordedLineType)
            {
                recorded++;
                continue;
            }

            switch (entity.Kind)
            {
                case CadEntityKind.Line:
                    RecordLine(context, pen, snapshot.Lines.Span[entity.PrimitiveIndex], snapshot.RebaseOrigin);
                    break;
                case CadEntityKind.Circle:
                    RecordCircle(context, pen, snapshot.Circles.Span[entity.PrimitiveIndex], snapshot.RebaseOrigin);
                    break;
                case CadEntityKind.Arc:
                    RecordArc(context, pen, snapshot.Arcs.Span[entity.PrimitiveIndex], snapshot.RebaseOrigin);
                    break;
                case CadEntityKind.Ellipse:
                    RecordEllipse(context, pen, snapshot.Ellipses.Span[entity.PrimitiveIndex], snapshot.RebaseOrigin);
                    break;
                case CadEntityKind.Solid:
                    RecordSolid(context, pen.Brush, snapshot.Faces.Span[entity.PrimitiveIndex], snapshot.RebaseOrigin);
                    break;
                case CadEntityKind.Hatch:
                    RecordHatch(
                        context,
                        pen.Brush,
                        snapshot,
                        snapshot.Hatches.Span[entity.PrimitiveIndex],
                        options.MaxHatchPatternAuxiliaryRecords,
                        ref hatchPatternAuxiliaryRecords);
                    break;
                case CadEntityKind.Face3D:
                    RecordFace3D(context, pen, snapshot.Faces.Span[entity.PrimitiveIndex], snapshot.RebaseOrigin);
                    break;
                case CadEntityKind.Spline:
                    RecordSpline(context, pen, snapshot, snapshot.Splines.Span[entity.PrimitiveIndex]);
                    break;
                case CadEntityKind.LightweightPolyline:
                    RecordPolyline(context, pen, snapshot, snapshot.Polylines.Span[entity.PrimitiveIndex]);
                    break;
                case CadEntityKind.Polyline2D:
                    RecordPolyline(context, pen, snapshot, snapshot.Polylines.Span[entity.PrimitiveIndex]);
                    break;
                case CadEntityKind.Polyline3D:
                    RecordPolyline3D(context, pen, snapshot, snapshot.Polylines3D.Span[entity.PrimitiveIndex]);
                    break;
                case CadEntityKind.Text:
                    RecordText(
                        context,
                        pen.Brush,
                        snapshot,
                        snapshot.Texts.Span[entity.PrimitiveIndex]);
                    break;
                case CadEntityKind.MText:
                    RecordMText(
                        context,
                        snapshot,
                        snapshot.MTexts.Span[entity.PrimitiveIndex],
                        mtextBrushes);
                    break;
                case CadEntityKind.ShxText:
                    RecordShxText(
                        context,
                        pen,
                        snapshot,
                        snapshot.ShxTexts.Span[entity.PrimitiveIndex]);
                    break;
                case CadEntityKind.ShxMText:
                    RecordShxMText(
                        context,
                        pen,
                        snapshot,
                        snapshot.ShxMTexts.Span[entity.PrimitiveIndex],
                        mtextBrushes);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown CAD entity kind {entity.Kind}.");
            }

            recorded++;
        }

        context.TrimRetainedCommandCapacity();
        return new CadRecordedPlanScene(
            snapshot.ContentGeneration,
            snapshot.RebaseOrigin,
            context,
            new CadPlanSceneStatistics(
                recorded,
                context.Commands.Count,
                unsupportedLineTypes,
                loweredLineTypeEntities,
                loweredLineTypeFigures,
                loweredLineTypePlacements,
                lineTypePatternSteps,
                lineTypeSourceSegments),
            diagnostics.ToArray());

        void AddUnsupportedLineTypeDiagnostic(
            string lineTypeName,
            string reason,
            string code)
        {
            string key = $"{lineTypeName}\0{reason}";
            if (!warnedLineTypes.Add(key))
            {
                return;
            }

            unsupportedLineTypes++;
            diagnostics.Add(new CadDiagnostic(
                CadDiagnosticSeverity.Warning,
                code,
                $"Linetype '{lineTypeName}' is recorded as a continuous stroke because {reason}."));
        }
    }

    private static Pen[] CreatePens(
        ReadOnlySpan<CadStrokeStyle> styles,
        CadPlanSceneOptions options)
    {
        var pens = new Pen[styles.Length];
        for (int i = 0; i < styles.Length; i++)
        {
            CadStrokeStyle style = styles[i];
            var brush = new SolidColorBrush(new Vector4(
                style.Red / 255.0f,
                style.Green / 255.0f,
                style.Blue / 255.0f,
                style.Alpha / 255.0f));
            float thickness = style.IsHairline
                ? Pen.HairlineThickness
                : checked((float)(style.LineWeightMillimeters * options.PhysicalDpi * options.LineWeightScale / 25.4));
            pens[i] = new Pen(
                brush,
                thickness,
                lineJoin: PenLineJoin.Round,
                startLineCap: PenLineCap.Round,
                endLineCap: PenLineCap.Round,
                strokeTransformMode: PenStrokeTransformMode.Fixed);
        }

        return pens;
    }

    private static void RecordLineTypePlacements(
        DrawingContext context,
        Pen pen,
        CadDocumentSnapshot snapshot,
        in CadStrokeStyle style,
        in CadLineTypePattern pattern,
        in CadLineTypeLoweringResult result)
    {
        if (result.PlacementCount == 0)
        {
            return;
        }

        ReadOnlySpan<CadLineTypePlacement> placements =
            result.Placements.AsSpan(0, result.PlacementCount);
        ReadOnlySpan<CadLineTypeElement> elements = snapshot.LineTypeElements.Span.Slice(
            pattern.ElementOffset,
            pattern.ElementCount);
        ReadOnlySpan<CadLineTypeTextResource> textResources =
            snapshot.LineTypeTextResources.Span;
        ReadOnlySpan<CadLineTypeShapeResource> shapeResources =
            snapshot.LineTypeShapeResources.Span;
        ReadOnlySpan<CadTextGlyphRun> runs = snapshot.TextGlyphRuns.Span;
        ReadOnlySpan<ProGPU.Text.TtfFont> fonts = snapshot.TextFonts.Span;
        ReadOnlySpan<CadShxGlyphInstance> shxGlyphs = snapshot.ShxGlyphInstances.Span;
        float effectiveScale = ToFloat(style.LineTypeScale);
        for (int i = 0; i < placements.Length; i++)
        {
            CadLineTypePlacement placement = placements[i];
            CadLineTypeElement element = elements[placement.ElementIndex];
            Vector2 origin = Vector2.Transform(placement.Origin, result.Transform);
            Vector2 tangent = Vector2.TransformNormal(placement.Tangent, result.Transform);
            float tangentLength = tangent.Length();
            if (!(tangentLength > 0.0f) || !float.IsFinite(tangentLength))
            {
                continue;
            }
            tangent /= tangentLength;
            Vector2 lineNormal = new(-tangent.Y, tangent.X);
            origin += (tangent * ToFloat(element.OffsetX * style.LineTypeScale)) +
                (lineNormal * ToFloat(element.OffsetY * style.LineTypeScale));

            Vector2 contentX;
            if (element.RotationMode == CadLineTypeRotationMode.Absolute)
            {
                contentX = new Vector2(
                    MathF.Cos(ToFloat(element.Rotation)),
                    MathF.Sin(ToFloat(element.Rotation)));
            }
            else
            {
                float cosine = MathF.Cos(ToFloat(element.Rotation));
                float sine = MathF.Sin(ToFloat(element.Rotation));
                contentX = new Vector2(
                    (tangent.X * cosine) - (tangent.Y * sine),
                    (tangent.X * sine) + (tangent.Y * cosine));
            }
            Vector2 contentUp = new(-contentX.Y, contentX.X);

            if (element.Kind == CadLineTypeElementKind.ShxShape)
            {
                CadLineTypeShapeResource shape = shapeResources[element.ResourceIndex];
                if (!shape.Glyph.HasGeometry)
                {
                    continue;
                }
                float shapeScale = ToFloat(shape.Scale) * effectiveScale;
                context.DrawPath(
                    null,
                    pen,
                    shape.Glyph.Path,
                    CreateAffineTransform(
                        origin,
                        contentX * shapeScale,
                        contentUp * shapeScale));
                continue;
            }

            CadLineTypeTextResource text = textResources[element.ResourceIndex];
            float mirrorX = text.IsBackward ? -1.0f : 1.0f;
            float mirrorY = text.IsUpsideDown ? -1.0f : 1.0f;
            float xScale = ToFloat(text.XScale) * effectiveScale;
            float yScale = ToFloat(text.YScale) * effectiveScale;
            float shear = MathF.Tan(ToFloat(text.ObliqueAngle));
            Vector2 xAxis = contentX * (xScale * mirrorX);
            Vector2 yAxis = text.Kind == CadLineTypeElementKind.TrueTypeText
                ? (contentX * (-yScale * shear * mirrorY)) +
                    (contentUp * (-yScale * mirrorY))
                : (contentX * (yScale * shear * mirrorY)) +
                    (contentUp * (yScale * mirrorY));
            Matrix4x4 transform = CreateAffineTransform(origin, xAxis, yAxis);
            if (text.Kind == CadLineTypeElementKind.TrueTypeText)
            {
                int runEnd = checked(text.RunOffset + text.RunCount);
                for (int runIndex = text.RunOffset; runIndex < runEnd; runIndex++)
                {
                    CadTextGlyphRun run = runs[runIndex];
                    context.DrawGlyphRunRange(
                        snapshot.TextGlyphIndexArray,
                        snapshot.TextGlyphPositionArray,
                        run.GlyphOffset,
                        run.GlyphCount,
                        fonts[run.FontIndex],
                        1.0f,
                        pen.Brush,
                        Vector2.Zero,
                        transform,
                        useVectorGlyphRendering: true);
                }
            }
            else
            {
                ReadOnlySpan<CadShxGlyphInstance> glyphs = shxGlyphs.Slice(
                    text.GlyphOffset,
                    text.GlyphCount);
                for (int glyphIndex = 0; glyphIndex < glyphs.Length; glyphIndex++)
                {
                    CadShxGlyphInstance glyph = glyphs[glyphIndex];
                    if (!glyph.Glyph.HasGeometry)
                    {
                        continue;
                    }
                    Vector2 glyphOrigin = origin +
                        (xAxis * glyph.X) +
                        (yAxis * glyph.Y);
                    context.DrawPath(
                        null,
                        pen,
                        glyph.Glyph.Path,
                        CreateAffineTransform(glyphOrigin, xAxis, yAxis));
                }
            }
        }
    }

    private static void RecordLineTypeSplineFragments(
        DrawingContext context,
        Pen pen,
        in CadLineTypeLoweringResult result)
    {
        if (result.SplineFragments is null)
        {
            return;
        }

        ReadOnlySpan<Vector2> points = result.SplineControlPoints;
        ReadOnlySpan<double> knots = result.SplineKnots;
        ReadOnlySpan<double> weights = result.SplineWeights;
        foreach (CadLineTypeSplineFragment fragment in result.SplineFragments)
        {
            context.DrawSpline(
                pen,
                points.Slice(fragment.ControlPointOffset, fragment.ControlPointCount),
                knots.Slice(fragment.KnotOffset, fragment.KnotCount),
                weights.Slice(fragment.WeightOffset, fragment.WeightCount),
                fragment.Degree,
                isClosed: false);
        }
    }

    private static bool HasLineTypeSubstitution(
        CadDocumentSnapshot snapshot,
        in CadLineTypePattern pattern)
    {
        ReadOnlySpan<CadLineTypeElement> elements = snapshot.LineTypeElements.Span.Slice(
            pattern.ElementOffset,
            pattern.ElementCount);
        ReadOnlySpan<CadLineTypeTextResource> textResources =
            snapshot.LineTypeTextResources.Span;
        ReadOnlySpan<CadLineTypeShapeResource> shapeResources =
            snapshot.LineTypeShapeResources.Span;
        for (int i = 0; i < elements.Length; i++)
        {
            CadLineTypeElement element = elements[i];
            if (element.Kind == CadLineTypeElementKind.ShxShape &&
                shapeResources[element.ResourceIndex].IsSubstitution)
            {
                return true;
            }
            if (element.Kind is CadLineTypeElementKind.TrueTypeText or CadLineTypeElementKind.ShxText &&
                textResources[element.ResourceIndex].IsSubstitution)
            {
                return true;
            }
        }
        return false;
    }

    private static Matrix4x4 CreateAffineTransform(
        Vector2 origin,
        Vector2 xAxis,
        Vector2 yAxis) =>
        new(
            xAxis.X, xAxis.Y, 0.0f, 0.0f,
            yAxis.X, yAxis.Y, 0.0f, 0.0f,
            0.0f, 0.0f, 1.0f, 0.0f,
            origin.X, origin.Y, 0.0f, 1.0f);

    private static void RecordLine(
        DrawingContext context,
        Pen pen,
        CadLinePrimitive line,
        CadPoint3D origin) =>
        context.DrawLine(pen, Project(line.Start, origin), Project(line.End, origin));

    private static void RecordCircle(
        DrawingContext context,
        Pen pen,
        CadCirclePrimitive circle,
        CadPoint3D origin)
    {
        Matrix4x4 transform = CreateProjectionTransform(circle.Center, circle.CoordinateSystem, origin);
        float radius = ToFloat(circle.Radius);
        context.DrawEllipse(null, pen, Vector2.Zero, radius, radius, transform);
    }

    private static void RecordArc(
        DrawingContext context,
        Pen pen,
        CadArcPrimitive arc,
        CadPoint3D origin)
    {
        Matrix4x4 transform = CreateProjectionTransform(arc.Center, arc.CoordinateSystem, origin);
        float radius = ToFloat(arc.Radius);
        if (arc.SweepAngle >= TwoPi - 1e-12)
        {
            context.DrawEllipse(null, pen, Vector2.Zero, radius, radius, transform);
            return;
        }

        Vector2 start = new(
            radius * MathF.Cos(ToFloat(arc.StartAngle)),
            radius * MathF.Sin(ToFloat(arc.StartAngle)));
        double endAngle = arc.StartAngle + arc.SweepAngle;
        Vector2 end = new(
            radius * MathF.Cos(ToFloat(endAngle)),
            radius * MathF.Sin(ToFloat(endAngle)));
        var path = new PathGeometry();
        var figure = new PathFigure(start)
        {
            IsFilled = false,
            IsClosed = false,
        };
        figure.Segments.Add(new ArcSegment(
            end,
            new Vector2(radius, radius),
            rotationAngle: 0.0f,
            isLargeArc: arc.SweepAngle > Math.PI,
            sweepDirection: SweepDirection.Counterclockwise));
        path.Figures.Add(figure);
        context.DrawPath(null, pen, path, transform);
    }

    private static void RecordEllipse(
        DrawingContext context,
        Pen pen,
        CadEllipsePrimitive ellipse,
        CadPoint3D origin)
    {
        Matrix4x4 transform = CreateProjectionTransform(
            ellipse.Center,
            ellipse.MajorAxis,
            ellipse.MinorAxis,
            origin);
        if (ellipse.SweepParameter >= TwoPi - 1e-12)
        {
            context.DrawEllipse(null, pen, Vector2.Zero, 1.0f, 1.0f, transform);
            return;
        }

        Vector2 start = new(
            MathF.Cos(ToFloat(ellipse.StartParameter)),
            MathF.Sin(ToFloat(ellipse.StartParameter)));
        double endParameter = ellipse.StartParameter + ellipse.SweepParameter;
        Vector2 end = new(
            MathF.Cos(ToFloat(endParameter)),
            MathF.Sin(ToFloat(endParameter)));
        var path = new PathGeometry();
        var figure = new PathFigure(start)
        {
            IsFilled = false,
            IsClosed = false,
        };
        figure.Segments.Add(new ArcSegment(
            end,
            Vector2.One,
            rotationAngle: 0.0f,
            isLargeArc: ellipse.SweepParameter > Math.PI,
            sweepDirection: SweepDirection.Counterclockwise));
        path.Figures.Add(figure);
        context.DrawPath(null, pen, path, transform);
    }

    private static void RecordSolid(
        DrawingContext context,
        Brush brush,
        CadFacePrimitive face,
        CadPoint3D origin)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(Project(face.First, origin), isClosed: true);
        figure.Segments.Add(new LineSegment(Project(face.Second, origin)));
        figure.Segments.Add(new LineSegment(Project(face.Third, origin)));
        if (face.Fourth != face.Third)
        {
            figure.Segments.Add(new LineSegment(Project(face.Fourth, origin)));
        }

        path.Figures.Add(figure);
        context.DrawPath(brush, null, path);
    }

    private static void RecordHatch(
        DrawingContext context,
        Brush brush,
        CadDocumentSnapshot snapshot,
        CadHatchPrimitive hatch,
        int maximumAuxiliaryRecords,
        ref int auxiliaryRecords)
    {
        var path = new PathGeometry { FillRule = FillRule.EvenOdd };
        ReadOnlySpan<CadHatchLoop> loops = snapshot.HatchLoops.Span.Slice(
            hatch.LoopOffset,
            hatch.LoopCount);
        ReadOnlySpan<CadHatchSegment> allSegments = snapshot.HatchSegments.Span;
        for (int loopIndex = 0; loopIndex < loops.Length; loopIndex++)
        {
            CadHatchLoop loop = loops[loopIndex];
            if (!loop.ContributesToFill)
            {
                continue;
            }
            ReadOnlySpan<CadHatchSegment> segments = allSegments.Slice(
                loop.SegmentOffset,
                loop.SegmentCount);
            var figure = new PathFigure(
                new Vector2(ToFloat(segments[0].StartX), ToFloat(segments[0].StartY)),
                isClosed: true);
            for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
            {
                AddHatchPathSegment(figure, segments[segmentIndex]);
            }
            path.Figures.Add(figure);
        }

        Matrix4x4 transform = CreateProjectionTransform(
            hatch.WorldOrigin,
            hatch.CoordinateSystem,
            snapshot.RebaseOrigin);
        Brush fill = hatch.PatternIndex < 0
            ? brush
            : CreateHatchPatternBrush(
                brush,
                snapshot,
                snapshot.HatchPatterns.Span[hatch.PatternIndex],
                maximumAuxiliaryRecords,
                ref auxiliaryRecords);
        context.DrawPath(fill, null, path, transform);
    }

    private static Brush CreateHatchPatternBrush(
        Brush styleBrush,
        CadDocumentSnapshot snapshot,
        in CadHatchPattern pattern,
        int maximumAuxiliaryRecords,
        ref int auxiliaryRecords)
    {
        SolidColorBrush solid = styleBrush as SolidColorBrush ??
            throw new InvalidOperationException(
                "CAD HATCH styles require a resolved solid-color brush.");
        ReadOnlySpan<CadHatchPatternFamily> families =
            snapshot.HatchPatternFamilies.Span.Slice(
                pattern.FamilyOffset,
                pattern.FamilyCount);
        bool simple = families.Length == 1 && families[0].DashCount == 0;
        bool simpleCross = families.Length == 2 &&
            families[0].DashCount == 0 && families[1].DashCount == 0 &&
            Math.Abs(families[0].Spacing - families[1].Spacing) <=
                Math.Max(1.0, families[0].Spacing) * 1e-10 &&
            Math.Abs(families[0].BasePointX - families[1].BasePointX) <= 1e-10 &&
            Math.Abs(families[0].BasePointY - families[1].BasePointY) <= 1e-10 &&
            Math.Abs((families[0].DirectionX * families[1].DirectionX) +
                (families[0].DirectionY * families[1].DirectionY)) <= 1e-10;
        if (!simple && !simpleCross)
        {
            int requiredRecords = checked(families.Length * 4);
            if (requiredRecords > maximumAuxiliaryRecords - auxiliaryRecords)
            {
                throw new InvalidOperationException(
                    $"Patterned HATCH brushes require more than the configured {maximumAuxiliaryRecords} retained auxiliary records.");
            }
            auxiliaryRecords += requiredRecords;
            var retainedFamilies = new HatchPatternLineFamily[families.Length];
            var retainedDashes = new float[CountPatternDashes(families)];
            int localDashOffset = 0;
            for (int i = 0; i < families.Length; i++)
            {
                CadHatchPatternFamily family = families[i];
                for (int dashIndex = 0; dashIndex < family.DashCount; dashIndex++)
                {
                    retainedDashes[localDashOffset + dashIndex] = ToFloat(
                        snapshot.HatchPatternDashes.Span[family.DashOffset + dashIndex]);
                }
                retainedFamilies[i] = new HatchPatternLineFamily(
                    new Vector2(ToFloat(family.BasePointX), ToFloat(family.BasePointY)),
                    new Vector2(ToFloat(family.DirectionX), ToFloat(family.DirectionY)),
                    ToFloat(family.TangentShift),
                    ToFloat(family.Spacing),
                    localDashOffset,
                    family.DashCount,
                    ToFloat(family.DashPeriod));
                localDashOffset += family.DashCount;
            }
            return new HatchPatternSetBrush(
                retainedFamilies,
                retainedDashes,
                thickness: 0f,
                color: solid.Color)
            {
                Opacity = styleBrush.Opacity,
            };
        }

        CadHatchPatternFamily first = families[0];
        float normalX = ToFloat(-first.DirectionY);
        float normalY = ToFloat(first.DirectionX);
        float spacing = ToFloat(first.Spacing);
        float baseX = ToFloat(first.BasePointX);
        float baseY = ToFloat(first.BasePointY);
        float translateX = -baseX + (normalX * spacing * 0.5f);
        float translateY = -baseY + (normalY * spacing * 0.5f);
        if (simpleCross)
        {
            float secondNormalX = ToFloat(-families[1].DirectionY);
            float secondNormalY = ToFloat(families[1].DirectionX);
            translateX += secondNormalX * spacing * 0.5f;
            translateY += secondNormalY * spacing * 0.5f;
        }

        Brush result = simpleCross
            ? new CrossHatchBrush(
                MathF.Atan2(normalY, normalX),
                spacing,
                thickness: 0.0f,
                color: solid.Color)
                {
                    CoordinateTransform = Matrix4x4.CreateTranslation(
                        translateX,
                        translateY,
                        0.0f),
                }
            : new HatchPatternBrush(
                MathF.Atan2(normalY, normalX),
                spacing,
                thickness: 0.0f,
                color: solid.Color)
                {
                    CoordinateTransform = Matrix4x4.CreateTranslation(
                        translateX,
                        translateY,
                        0.0f),
                };
        result.Opacity = styleBrush.Opacity;
        return result;
    }

    private static int CountPatternDashes(ReadOnlySpan<CadHatchPatternFamily> families)
    {
        int count = 0;
        for (int i = 0; i < families.Length; i++)
            count = checked(count + families[i].DashCount);
        return count;
    }

    private static void AddHatchPathSegment(
        PathFigure figure,
        CadHatchSegment segment)
    {
        if (segment.Kind == CadHatchSegmentKind.Line)
        {
            figure.Segments.Add(new LineSegment(
                new Vector2(ToFloat(segment.EndX), ToFloat(segment.EndY))));
            return;
        }
        if (segment.Kind == CadHatchSegmentKind.QuadraticBezier)
        {
            figure.Segments.Add(new QuadraticBezierSegment(
                new Vector2(ToFloat(segment.CenterX), ToFloat(segment.CenterY)),
                new Vector2(ToFloat(segment.EndX), ToFloat(segment.EndY))));
            return;
        }
        if (segment.Kind == CadHatchSegmentKind.CubicBezier)
        {
            figure.Segments.Add(new CubicBezierSegment(
                new Vector2(ToFloat(segment.CenterX), ToFloat(segment.CenterY)),
                new Vector2(ToFloat(segment.CosineAxisX), ToFloat(segment.CosineAxisY)),
                new Vector2(ToFloat(segment.EndX), ToFloat(segment.EndY))));
            return;
        }

        double radiusX = new CadPoint3D(
            segment.CosineAxisX,
            segment.CosineAxisY,
            0.0).Length;
        double radiusY = new CadPoint3D(
            segment.SineAxisX,
            segment.SineAxisY,
            0.0).Length;
        float rotationDegrees = ToFloat(
            Math.Atan2(segment.CosineAxisY, segment.CosineAxisX) * (180.0 / Math.PI));
        SweepDirection direction = segment.SweepParameter >= 0.0
            ? SweepDirection.Counterclockwise
            : SweepDirection.Clockwise;
        double sweep = Math.Abs(segment.SweepParameter);
        if (sweep >= TwoPi - 1e-12)
        {
            double middleParameter = segment.StartParameter + (segment.SweepParameter * 0.5);
            GetHatchEllipsePoint(segment, middleParameter, out float middleX, out float middleY);
            figure.Segments.Add(new ArcSegment(
                new Vector2(middleX, middleY),
                new Vector2(ToFloat(radiusX), ToFloat(radiusY)),
                rotationDegrees,
                isLargeArc: false,
                direction));
            figure.Segments.Add(new ArcSegment(
                new Vector2(ToFloat(segment.EndX), ToFloat(segment.EndY)),
                new Vector2(ToFloat(radiusX), ToFloat(radiusY)),
                rotationDegrees,
                isLargeArc: false,
                direction));
            return;
        }

        figure.Segments.Add(new ArcSegment(
            new Vector2(ToFloat(segment.EndX), ToFloat(segment.EndY)),
            new Vector2(ToFloat(radiusX), ToFloat(radiusY)),
            rotationDegrees,
            isLargeArc: sweep > Math.PI,
            direction));
    }

    private static void GetHatchEllipsePoint(
        CadHatchSegment segment,
        double parameter,
        out float x,
        out float y)
    {
        double cosine = Math.Cos(parameter);
        double sine = Math.Sin(parameter);
        x = ToFloat(
            segment.CenterX +
            (segment.CosineAxisX * cosine) +
            (segment.SineAxisX * sine));
        y = ToFloat(
            segment.CenterY +
            (segment.CosineAxisY * cosine) +
            (segment.SineAxisY * sine));
    }

    private static void RecordFace3D(
        DrawingContext context,
        Pen pen,
        CadFacePrimitive face,
        CadPoint3D origin)
    {
        var path = new PathGeometry();
        AddFaceEdge(path, face.First, face.Second, origin, face.InvisibleEdgeMask, 1);
        AddFaceEdge(path, face.Second, face.Third, origin, face.InvisibleEdgeMask, 2);
        AddFaceEdge(path, face.Third, face.Fourth, origin, face.InvisibleEdgeMask, 4);
        AddFaceEdge(path, face.Fourth, face.First, origin, face.InvisibleEdgeMask, 8);
        if (path.Figures.Count != 0)
        {
            context.DrawPath(null, pen, path);
        }
    }

    private static void AddFaceEdge(
        PathGeometry path,
        CadPoint3D start,
        CadPoint3D end,
        CadPoint3D origin,
        byte invisibleEdgeMask,
        byte edgeFlag)
    {
        if ((invisibleEdgeMask & edgeFlag) != 0 || start == end)
        {
            return;
        }

        var figure = new PathFigure(Project(start, origin))
        {
            IsFilled = false,
        };
        figure.Segments.Add(new LineSegment(Project(end, origin)));
        path.Figures.Add(figure);
    }

    private static void RecordSpline(
        DrawingContext context,
        Pen pen,
        CadDocumentSnapshot snapshot,
        CadSplinePrimitive spline)
    {
        if (spline.IsPeriodic &&
            CadSplineCanonicalizer.TryCreate(snapshot, spline, out CadCanonicalSpline canonical))
        {
            RecordPeriodicSpline(context, pen, snapshot.RebaseOrigin, canonical);
            return;
        }

        ReadOnlySpan<CadPoint3D> sourcePoints = snapshot.SplineControlPoints.Span.Slice(
            spline.ControlPointOffset,
            spline.ControlPointCount);
        Vector2[]? rented = null;
        Span<Vector2> points = sourcePoints.Length <= 256
            ? stackalloc Vector2[sourcePoints.Length]
            : (rented = ArrayPool<Vector2>.Shared.Rent(sourcePoints.Length))
                .AsSpan(0, sourcePoints.Length);
        try
        {
            for (int i = 0; i < sourcePoints.Length; i++)
            {
                points[i] = Project(sourcePoints[i], snapshot.RebaseOrigin);
            }

            ReadOnlySpan<double> knots = snapshot.SplineKnots.Span.Slice(
                spline.KnotOffset,
                spline.KnotCount);
            ReadOnlySpan<double> weights = spline.WeightCount == 0
                ? default
                : snapshot.SplineWeights.Span.Slice(spline.WeightOffset, spline.WeightCount);
            context.DrawSpline(pen, points, knots, weights, spline.Degree, spline.IsClosed);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<Vector2>.Shared.Return(rented);
            }
        }
    }

    private static void RecordPeriodicSpline(
        DrawingContext context,
        Pen pen,
        CadPoint3D rebaseOrigin,
        in CadCanonicalSpline spline)
    {
        int controlPointCount = spline.ControlPointCount;
        int knotCount = spline.KnotCount;
        Vector2[]? rentedPoints = null;
        double[]? rentedKnots = null;
        double[]? rentedWeights = null;
        Span<Vector2> points = controlPointCount <= 256
            ? stackalloc Vector2[controlPointCount]
            : (rentedPoints = ArrayPool<Vector2>.Shared.Rent(controlPointCount))
                .AsSpan(0, controlPointCount);
        Span<double> knots = knotCount <= 256
            ? stackalloc double[knotCount]
            : (rentedKnots = ArrayPool<double>.Shared.Rent(knotCount))
                .AsSpan(0, knotCount);
        Span<double> weights = default;
        if (spline.HasWeights)
        {
            rentedWeights = ArrayPool<double>.Shared.Rent(controlPointCount);
            weights = rentedWeights.AsSpan(0, controlPointCount);
        }

        try
        {
            for (int i = 0; i < controlPointCount; i++)
            {
                points[i] = Project(spline.GetControlPoint(i), rebaseOrigin);
                if (!weights.IsEmpty)
                {
                    weights[i] = spline.GetWeight(i);
                }
            }
            for (int i = 0; i < knotCount; i++)
            {
                knots[i] = spline.GetKnot(i);
            }

            // The expanded periodic NURBS already meets itself with its cyclic
            // basis. Asking DrawSpline to add another closing edge would alter
            // the source topology and can introduce a seam cap/join.
            context.DrawSpline(pen, points, knots, weights, spline.Degree, isClosed: false);
        }
        finally
        {
            if (rentedPoints is not null)
            {
                ArrayPool<Vector2>.Shared.Return(rentedPoints);
            }
            if (rentedKnots is not null)
            {
                ArrayPool<double>.Shared.Return(rentedKnots);
            }
            if (rentedWeights is not null)
            {
                ArrayPool<double>.Shared.Return(rentedWeights);
            }
        }
    }

    private static void RecordPolyline(
        DrawingContext context,
        Pen pen,
        CadDocumentSnapshot snapshot,
        CadPolylinePrimitive polyline)
    {
        ReadOnlySpan<CadPolylineVertex> vertices = snapshot.PolylineVertices.Span.Slice(
            polyline.VertexOffset,
            polyline.VertexCount);
        var path = new PathGeometry();
        var figure = new PathFigure(ToVector(vertices[0]))
        {
            IsFilled = false,
            IsClosed = polyline.IsClosed,
        };
        int segmentCount = polyline.IsClosed ? vertices.Length : vertices.Length - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            CadPolylineVertex start = vertices[i];
            CadPolylineVertex end = vertices[(i + 1) % vertices.Length];
            Vector2 endpoint = ToVector(end);
            if (start.Bulge == 0.0)
            {
                figure.Segments.Add(new LineSegment(endpoint));
                continue;
            }

            CadSnapshotCompiler.GetBulgeArc(
                start,
                end,
                out _,
                out _,
                out double radius,
                out _,
                out double sweep);
            float retainedRadius = ToFloat(radius);
            figure.Segments.Add(new ArcSegment(
                endpoint,
                new Vector2(retainedRadius, retainedRadius),
                rotationAngle: 0.0f,
                isLargeArc: Math.Abs(sweep) > Math.PI,
                sweepDirection: sweep >= 0.0
                    ? SweepDirection.Counterclockwise
                    : SweepDirection.Clockwise));
        }

        path.Figures.Add(figure);
        Matrix4x4 transform = CreateProjectionTransform(
            polyline.WorldOrigin,
            polyline.CoordinateSystem,
            snapshot.RebaseOrigin);
        context.DrawPath(null, pen, path, transform);
    }

    private static void RecordPolyline3D(
        DrawingContext context,
        Pen pen,
        CadDocumentSnapshot snapshot,
        CadPolyline3DPrimitive polyline)
    {
        ReadOnlySpan<CadPoint3D> points = snapshot.Polyline3DPoints.Span.Slice(
            polyline.PointOffset,
            polyline.PointCount);
        var path = new PathGeometry();
        var figure = new PathFigure(Project(points[0], snapshot.RebaseOrigin), polyline.IsClosed)
        {
            IsFilled = false,
        };
        for (int i = 1; i < points.Length; i++)
        {
            figure.Segments.Add(new LineSegment(Project(points[i], snapshot.RebaseOrigin)));
        }

        path.Figures.Add(figure);
        context.DrawPath(null, pen, path);
    }

    private static void RecordText(
        DrawingContext context,
        Brush brush,
        CadDocumentSnapshot snapshot,
        CadTextPrimitive text)
    {
        Matrix4x4 transform = CreateProjectionTransform(
            text.Origin,
            text.XAxis,
            text.YAxis,
            snapshot.RebaseOrigin);
        ReadOnlySpan<CadTextGlyphRun> runs = snapshot.TextGlyphRuns.Span;
        ReadOnlySpan<ProGPU.Text.TtfFont> fonts = snapshot.TextFonts.Span;
        int runEnd = checked(text.RunOffset + text.RunCount);
        for (int i = text.RunOffset; i < runEnd; i++)
        {
            CadTextGlyphRun run = runs[i];
            context.DrawGlyphRunRange(
                snapshot.TextGlyphIndexArray,
                snapshot.TextGlyphPositionArray,
                run.GlyphOffset,
                run.GlyphCount,
                fonts[run.FontIndex],
                1.0f,
                brush,
                Vector2.Zero,
                transform,
                useVectorGlyphRendering: true);
        }

        ReadOnlySpan<CadTextDecoration> decorations = snapshot.TextDecorations.Span;
        int decorationEnd = checked(text.DecorationOffset + text.DecorationCount);
        for (int i = text.DecorationOffset; i < decorationEnd; i++)
        {
            CadTextDecoration decoration = decorations[i];
            context.DrawRectangle(
                brush,
                null,
                new Rect(
                    decoration.X,
                    decoration.Y,
                    decoration.Width,
                    decoration.Height),
                transform);
        }
    }

    private static void RecordShxText(
        DrawingContext context,
        Pen pen,
        CadDocumentSnapshot snapshot,
        CadShxTextPrimitive text)
    {
        ReadOnlySpan<CadShxGlyphInstance> glyphs = snapshot.ShxGlyphInstances.Span.Slice(
            text.GlyphOffset,
            text.GlyphCount);
        for (int i = 0; i < glyphs.Length; i++)
        {
            CadShxGlyphInstance glyph = glyphs[i];
            if (!glyph.Glyph.HasGeometry)
            {
                continue;
            }

            CadPoint3D origin = text.Origin +
                (text.XAxis * glyph.X) +
                (text.YAxis * glyph.Y);
            Matrix4x4 transform = CreateProjectionTransform(
                origin,
                text.XAxis,
                text.YAxis,
                snapshot.RebaseOrigin);
            context.DrawPath(null, pen, glyph.Glyph.Path, transform);
        }

        ReadOnlySpan<CadShxDecorationSegment> decorations =
            snapshot.ShxDecorationSegments.Span.Slice(
                text.DecorationOffset,
                text.DecorationCount);
        for (int i = 0; i < decorations.Length; i++)
        {
            CadShxDecorationSegment decoration = decorations[i];
            CadPoint3D start = text.Origin +
                (text.XAxis * decoration.StartX) +
                (text.YAxis * decoration.StartY);
            CadPoint3D end = text.Origin +
                (text.XAxis * decoration.EndX) +
                (text.YAxis * decoration.EndY);
            context.DrawLine(
                pen,
                Project(start, snapshot.RebaseOrigin),
                Project(end, snapshot.RebaseOrigin));
        }
    }

    private static void RecordMText(
        DrawingContext context,
        CadDocumentSnapshot snapshot,
        in CadMTextPrimitive text,
        Dictionary<uint, Brush> brushes)
    {
        Matrix4x4 transform = CreateProjectionTransform(
            text.Origin,
            text.XAxis,
            text.YAxis,
            snapshot.RebaseOrigin);
        ReadOnlySpan<CadMTextRectangle> backgrounds = snapshot.MTextBackgrounds.Span.Slice(
            text.BackgroundOffset,
            text.BackgroundCount);
        for (int index = 0; index < backgrounds.Length; index++)
        {
            CadMTextRectangle rectangle = backgrounds[index];
            context.DrawRectangle(
                GetMTextBrush(brushes, rectangle.Red, rectangle.Green, rectangle.Blue, rectangle.Alpha),
                null,
                new Rect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height),
                transform);
        }

        ReadOnlySpan<CadMTextGlyphRun> runs = snapshot.MTextGlyphRuns.Span.Slice(
            text.RunOffset,
            text.RunCount);
        ReadOnlySpan<ProGPU.Text.TtfFont> fonts = snapshot.TextFonts.Span;
        for (int index = 0; index < runs.Length; index++)
        {
            CadMTextGlyphRun run = runs[index];
            context.DrawTransformedGlyphRunRange(
                snapshot.TextGlyphIndexArray,
                snapshot.TextGlyphPositionArray,
                run.GlyphOffset,
                run.GlyphCount,
                fonts[run.FontIndex],
                run.FontSize,
                GetMTextBrush(brushes, run.Red, run.Green, run.Blue, run.Alpha),
                Vector2.Zero,
                transform,
                useVectorGlyphRendering: true,
                fontScaleX: run.WidthScale,
                fontSkewX: run.SkewX);
        }

        ReadOnlySpan<CadMTextRectangle> decorations = snapshot.MTextDecorations.Span.Slice(
            text.DecorationOffset,
            text.DecorationCount);
        for (int index = 0; index < decorations.Length; index++)
        {
            CadMTextRectangle rectangle = decorations[index];
            context.DrawRectangle(
                GetMTextBrush(brushes, rectangle.Red, rectangle.Green, rectangle.Blue, rectangle.Alpha),
                null,
                new Rect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height),
                transform);
        }

        ReadOnlySpan<CadMTextStroke> strokes = snapshot.MTextStrokes.Span.Slice(
            text.StrokeOffset,
            text.StrokeCount);
        for (int index = 0; index < strokes.Length; index++)
        {
            CadMTextStroke stroke = strokes[index];
            double dx = stroke.EndX - stroke.StartX;
            double dy = stroke.EndY - stroke.StartY;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (!(length > 0.0) || !double.IsFinite(length)) continue;
            dx /= length;
            dy /= length;
            CadPoint3D strokeOrigin = text.Origin +
                (text.XAxis * stroke.StartX) +
                (text.YAxis * stroke.StartY);
            CadPoint3D along = (text.XAxis * dx) + (text.YAxis * dy);
            CadPoint3D across =
                ((text.XAxis * -dy) + (text.YAxis * dx)) * stroke.Thickness;
            Matrix4x4 strokeTransform = CreateProjectionTransform(
                strokeOrigin,
                along,
                across,
                snapshot.RebaseOrigin);
            context.DrawRectangle(
                GetMTextBrush(brushes, stroke.Red, stroke.Green, stroke.Blue, stroke.Alpha),
                null,
                new Rect(0.0f, -0.5f, ToFloat(length), 1.0f),
                strokeTransform);
        }
    }

    private static void RecordShxMText(
        DrawingContext context,
        Pen basePen,
        CadDocumentSnapshot snapshot,
        in CadShxMTextPrimitive text,
        Dictionary<uint, Brush> brushes)
    {
        Matrix4x4 entityTransform = CreateProjectionTransform(
            text.Origin,
            text.XAxis,
            text.YAxis,
            snapshot.RebaseOrigin);
        ReadOnlySpan<CadMTextRectangle> backgrounds = snapshot.MTextBackgrounds.Span.Slice(
            text.BackgroundOffset,
            text.BackgroundCount);
        for (int index = 0; index < backgrounds.Length; index++)
        {
            CadMTextRectangle rectangle = backgrounds[index];
            context.DrawRectangle(
                GetMTextBrush(brushes, rectangle.Red, rectangle.Green, rectangle.Blue, rectangle.Alpha),
                null,
                new Rect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height),
                entityTransform);
        }

        ReadOnlySpan<CadShxMTextGlyphRun> runs = snapshot.ShxMTextGlyphRuns.Span.Slice(
            text.RunOffset,
            text.RunCount);
        ReadOnlySpan<CadShxGlyphInstance> glyphs = snapshot.ShxGlyphInstances.Span;
        for (int runIndex = 0; runIndex < runs.Length; runIndex++)
        {
            CadShxMTextGlyphRun run = runs[runIndex];
            Brush brush = GetMTextBrush(
                brushes,
                run.Red,
                run.Green,
                run.Blue,
                run.Alpha);
            var pen = new Pen(
                brush,
                basePen.Thickness,
                lineJoin: basePen.LineJoin,
                miterLimit: basePen.MiterLimit,
                startLineCap: basePen.StartLineCap,
                endLineCap: basePen.EndLineCap,
                strokeTransformMode: basePen.StrokeTransformMode);
            CadPoint3D pathXAxis = text.XAxis * run.ScaleX;
            CadPoint3D pathYAxis =
                (text.XAxis * (run.ScaleY * run.SkewX)) +
                (text.YAxis * -run.ScaleY);
            int end = checked(run.GlyphOffset + run.GlyphCount);
            for (int glyphIndex = run.GlyphOffset; glyphIndex < end; glyphIndex++)
            {
                CadShxGlyphInstance glyph = glyphs[glyphIndex];
                if (!glyph.Glyph.HasGeometry) continue;
                CadPoint3D glyphOrigin = text.Origin +
                    (text.XAxis * glyph.X) +
                    (text.YAxis * glyph.Y);
                Matrix4x4 transform = CreateProjectionTransform(
                    glyphOrigin,
                    pathXAxis,
                    pathYAxis,
                    snapshot.RebaseOrigin);
                context.DrawPath(null, pen, glyph.Glyph.Path, transform);
            }
        }

        ReadOnlySpan<CadMTextRectangle> decorations = snapshot.MTextDecorations.Span.Slice(
            text.DecorationOffset,
            text.DecorationCount);
        for (int index = 0; index < decorations.Length; index++)
        {
            CadMTextRectangle rectangle = decorations[index];
            context.DrawRectangle(
                GetMTextBrush(brushes, rectangle.Red, rectangle.Green, rectangle.Blue, rectangle.Alpha),
                null,
                new Rect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height),
                entityTransform);
        }

        ReadOnlySpan<CadMTextStroke> strokes = snapshot.MTextStrokes.Span.Slice(
            text.StrokeOffset,
            text.StrokeCount);
        for (int index = 0; index < strokes.Length; index++)
        {
            CadMTextStroke stroke = strokes[index];
            double dx = stroke.EndX - stroke.StartX;
            double dy = stroke.EndY - stroke.StartY;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (!(length > 0.0) || !double.IsFinite(length)) continue;
            dx /= length;
            dy /= length;
            CadPoint3D strokeOrigin = text.Origin +
                (text.XAxis * stroke.StartX) +
                (text.YAxis * stroke.StartY);
            CadPoint3D along = (text.XAxis * dx) + (text.YAxis * dy);
            CadPoint3D across =
                ((text.XAxis * -dy) + (text.YAxis * dx)) * stroke.Thickness;
            Matrix4x4 strokeTransform = CreateProjectionTransform(
                strokeOrigin,
                along,
                across,
                snapshot.RebaseOrigin);
            context.DrawRectangle(
                GetMTextBrush(brushes, stroke.Red, stroke.Green, stroke.Blue, stroke.Alpha),
                null,
                new Rect(0.0f, -0.5f, ToFloat(length), 1.0f),
                strokeTransform);
        }
    }

    private static Brush GetMTextBrush(
        Dictionary<uint, Brush> brushes,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        uint key = ((uint)red << 24) | ((uint)green << 16) | ((uint)blue << 8) | alpha;
        if (brushes.TryGetValue(key, out Brush? brush)) return brush;
        brush = new SolidColorBrush(new Vector4(
            red / 255.0f,
            green / 255.0f,
            blue / 255.0f,
            alpha / 255.0f));
        brushes.Add(key, brush);
        return brush;
    }

    private static Matrix4x4 CreateProjectionTransform(
        CadPoint3D center,
        CadCoordinateSystem basis,
        CadPoint3D origin) =>
        new(
            ToFloat(basis.XAxis.X), ToFloat(basis.XAxis.Y), 0.0f, 0.0f,
            ToFloat(basis.YAxis.X), ToFloat(basis.YAxis.Y), 0.0f, 0.0f,
            0.0f, 0.0f, 1.0f, 0.0f,
            ToFloat(center.X - origin.X), ToFloat(center.Y - origin.Y), 0.0f, 1.0f);

    private static Matrix4x4 CreateProjectionTransform(
        CadPoint3D center,
        CadPoint3D xAxis,
        CadPoint3D yAxis,
        CadPoint3D origin) =>
        new(
            ToFloat(xAxis.X), ToFloat(xAxis.Y), 0.0f, 0.0f,
            ToFloat(yAxis.X), ToFloat(yAxis.Y), 0.0f, 0.0f,
            0.0f, 0.0f, 1.0f, 0.0f,
            ToFloat(center.X - origin.X), ToFloat(center.Y - origin.Y), 0.0f, 1.0f);

    private static Vector2 Project(CadPoint3D point, CadPoint3D origin) =>
        new(ToFloat(point.X - origin.X), ToFloat(point.Y - origin.Y));

    private static Vector2 ToVector(CadPolylineVertex vertex) =>
        new(ToFloat(vertex.X), ToFloat(vertex.Y));

    private static float ToFloat(double value)
    {
        float converted = (float)value;
        if (!float.IsFinite(converted))
        {
            throw new InvalidOperationException("A rebased CAD coordinate exceeds the retained float range.");
        }

        return converted;
    }

    private static bool UsesStroke(CadEntityKind kind) =>
        kind is not (CadEntityKind.Solid or CadEntityKind.Hatch or CadEntityKind.Text or CadEntityKind.ShxText or CadEntityKind.MText or CadEntityKind.ShxMText);

    private static void ValidateOptions(CadPlanSceneOptions options)
    {
        if (!float.IsFinite(options.PhysicalDpi) || options.PhysicalDpi <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Physical DPI must be finite and positive.");
        }

        if (!float.IsFinite(options.LineWeightScale) || options.LineWeightScale <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Lineweight scale must be finite and positive.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxHatchPatternAuxiliaryRecords,
            1);

        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxLineTypeFigures,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxLineTypePatternSteps,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxLineTypeSourceSegments,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxLineTypeArcMapsPerEntity,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxLineTypePlacements,
            1);
    }
}
