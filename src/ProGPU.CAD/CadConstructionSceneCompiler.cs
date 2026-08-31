using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.CAD;

public readonly record struct CadConstructionSceneStatistics(
    int SourceEntityCount,
    int RecordedEntityCount,
    int RecordedCommandCount,
    int UnsupportedLineTypeCount)
{
    public int LoweredLineTypeEntityCount { get; init; }
    public int LoweredLineTypeFigureCount { get; init; }
    public int LoweredLineTypePlacementCount { get; init; }
    public int LineTypePatternStepCount { get; init; }
    public int LineTypeSourceSegmentCount { get; init; }
}

/// <summary>
/// A viewport-specific retained overlay for unbounded RAY and XLINE entities.
/// </summary>
public sealed class CadRecordedConstructionScene
{
    private readonly CadDiagnostic[] _diagnostics;

    public ulong ContentGeneration { get; }
    public CadBounds3D PlanClipBounds { get; }
    public DrawingContext DrawingContext { get; }
    public CadConstructionSceneStatistics Statistics { get; }
    public ReadOnlyMemory<CadDiagnostic> Diagnostics => _diagnostics;

    internal CadRecordedConstructionScene(
        ulong contentGeneration,
        CadBounds3D planClipBounds,
        DrawingContext drawingContext,
        CadConstructionSceneStatistics statistics,
        CadDiagnostic[] diagnostics)
    {
        ContentGeneration = contentGeneration;
        PlanClipBounds = planClipBounds;
        DrawingContext = drawingContext;
        Statistics = statistics;
        _diagnostics = diagnostics;
    }

    public GpuPicture CreatePicture()
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext target = recorder.BeginRecording(new Rect(0, 0, 1, 1));
        target.Append(DrawingContext);
        return recorder.EndRecording();
    }
}

/// <summary>
/// Clips unbounded CAD construction geometry to one explicit WCS-XY plan window.
/// </summary>
/// <remarks>
/// Continuous compilation is O(U) for U visible construction entities. Patterned
/// compilation adds O(E + Q) per visible entity for E pattern descriptors and Q
/// descriptors intersecting the clip. The parametric slab clip is allocation-free
/// per entity and never fabricates a large model-space endpoint. A camera change
/// requires rebuilding only this overlay; the ordinary finite retained picture
/// remains reusable.
/// </remarks>
public sealed class CadConstructionSceneCompiler
{
    public CadRecordedConstructionScene Compile(
        CadDocumentSnapshot snapshot,
        CadBounds3D planClipBounds,
        CadPlanSceneOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (planClipBounds.IsEmpty)
        {
            throw new ArgumentException(
                "The construction-geometry plan clip cannot be empty.",
                nameof(planClipBounds));
        }
        options ??= new CadPlanSceneOptions();
        ValidateOptions(options);

        var context = new DrawingContext();
        ReadOnlySpan<CadEntityHeader> entities = snapshot.Entities.Span;
        ReadOnlySpan<CadLayerSnapshot> layers = snapshot.Layers.Span;
        ReadOnlySpan<CadStrokeStyle> styles = snapshot.Styles.Span;
        ReadOnlySpan<CadLineTypePattern> patterns = snapshot.LineTypePatterns.Span;
        Pen[] pens = CreatePens(styles, options);
        var diagnostics = new List<CadDiagnostic>();
        var warnedLineTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnedLineTypeSubstitutions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int sourceCount = 0;
        int recordedCount = 0;
        int unsupportedLineTypes = 0;
        int loweredLineTypeEntities = 0;
        int loweredLineTypeFigures = 0;
        int loweredLineTypePlacements = 0;
        int lineTypeFigureBudgetUsed = 0;
        int lineTypePatternSteps = 0;
        int lineTypeSourceSegments = 0;
        int lineTypePlacementBudgetUsed = 0;
        int activeStyleIndex = -1;
        PathGeometry? activePath = null;

        for (int entityIndex = 0; entityIndex < entities.Length; entityIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CadEntityHeader entity = entities[entityIndex];
            if (entity.Kind is not (CadEntityKind.Ray or CadEntityKind.XLine))
            {
                continue;
            }
            sourceCount++;
            if (!options.IncludeNonPlottableLayers &&
                !layers[entity.LayerIndex].IsPlottable)
            {
                continue;
            }

            CadStrokeStyle style = styles[entity.StyleIndex];
            CadLineTypePattern pattern = patterns[style.LineTypePatternIndex];
            CadConstructionLinePrimitive line =
                snapshot.ConstructionLines.Span[entity.PrimitiveIndex];
            if (!TryClipPlanInterval(
                    line,
                    planClipBounds,
                    entity.Kind == CadEntityKind.Ray,
                    out double parameterMinimum,
                    out double parameterMaximum,
                    out bool hasProjectedDirection,
                    out CadPoint3D start,
                    out CadPoint3D end))
            {
                continue;
            }

            Pen pen = pens[entity.StyleIndex];
            if (hasProjectedDirection && pattern.Kind is
                CadLineTypePatternKind.Simple or CadLineTypePatternKind.Complex)
            {
                int remainingFigures = options.MaxLineTypeFigures - lineTypeFigureBudgetUsed;
                int remainingPatternSteps =
                    options.MaxLineTypePatternSteps - lineTypePatternSteps;
                int remainingSourceSegments =
                    options.MaxLineTypeSourceSegments - lineTypeSourceSegments;
                int remainingPlacements =
                    options.MaxLineTypePlacements - lineTypePlacementBudgetUsed;
                CadLineTypeLoweringResult result =
                    CadLineTypeLowerer.LowerConstructionInterval(
                        snapshot,
                        line,
                        style,
                        pattern,
                        parameterMinimum,
                        parameterMaximum,
                        Math.Max(0, remainingFigures),
                        Math.Max(0, remainingPatternSteps),
                        Math.Max(0, remainingSourceSegments),
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
                    if (CadPlanSceneCompiler.HasLineTypeSubstitution(snapshot, pattern) &&
                        warnedLineTypeSubstitutions.Add(pattern.Name))
                    {
                        diagnostics.Add(new CadDiagnostic(
                            CadDiagnosticSeverity.Warning,
                            "CADCON002",
                            $"Construction linetype '{pattern.Name}' uses a host-resolved text or SHX substitution."));
                    }
                    if (result.PlacementCount == 0)
                    {
                        AppendToActivePath(result.Path, entity.StyleIndex);
                    }
                    else
                    {
                        FlushActivePath();
                        if (result.Path is not null && result.FigureCount != 0)
                        {
                            context.DrawPath(null, pen, result.Path);
                        }
                        CadPlanSceneCompiler.RecordLineTypePlacements(
                            context,
                            pen,
                            snapshot,
                            style,
                            pattern,
                            result);
                    }
                    loweredLineTypeEntities++;
                    loweredLineTypeFigures = checked(
                        loweredLineTypeFigures + result.FigureCount);
                    loweredLineTypePlacements = checked(
                        loweredLineTypePlacements + result.PlacementCount);
                    recordedCount++;
                    continue;
                }

                if (result.Status != CadLineTypeLoweringStatus.Continuous)
                {
                    string reason = result.Status switch
                    {
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
                        _ => "the authored phase interval cannot be represented exactly",
                    };
                    AddUnsupportedLineTypeDiagnostic(pattern.Name, reason);
                }
            }
            else if (pattern.Kind == CadLineTypePatternKind.UnsupportedAlignment)
            {
                AddUnsupportedLineTypeDiagnostic(
                    pattern.Name,
                    $"alignment '{pattern.Alignment}' is not the documented AutoCAD A alignment");
            }
            else if (!hasProjectedDirection &&
                pattern.Kind == CadLineTypePatternKind.Complex)
            {
                AddUnsupportedLineTypeDiagnostic(
                    pattern.Name,
                    "its 3D tangent has a point projection, so embedded text/shape orientation is undefined");
            }

            if (start.X == end.X && start.Y == end.Y)
            {
                FlushActivePath();
                Span<Vector2> point = stackalloc Vector2[1];
                point[0] = Project(start, snapshot.RebaseOrigin);
                context.DrawPointBatch(pen.Brush, point, radius: 0.0f, round: true);
            }
            else
            {
                if (activePath is null || activeStyleIndex != entity.StyleIndex)
                {
                    FlushActivePath();
                    activeStyleIndex = entity.StyleIndex;
                    activePath = new PathGeometry();
                }
                var figure = new PathFigure(Project(start, snapshot.RebaseOrigin))
                {
                    IsFilled = false,
                };
                figure.Segments.Add(new LineSegment(Project(end, snapshot.RebaseOrigin)));
                activePath.Figures.Add(figure);
            }
            recordedCount++;
        }

        FlushActivePath();

        context.TrimRetainedCommandCapacity();
        return new CadRecordedConstructionScene(
            snapshot.ContentGeneration,
            planClipBounds,
            context,
            new CadConstructionSceneStatistics(
                sourceCount,
                recordedCount,
                context.Commands.Count,
                unsupportedLineTypes)
            {
                LoweredLineTypeEntityCount = loweredLineTypeEntities,
                LoweredLineTypeFigureCount = loweredLineTypeFigures,
                LoweredLineTypePlacementCount = loweredLineTypePlacements,
                LineTypePatternStepCount = lineTypePatternSteps,
                LineTypeSourceSegmentCount = lineTypeSourceSegments,
            },
            diagnostics.ToArray());

        void AppendToActivePath(PathGeometry? source, int styleIndex)
        {
            if (source is null || source.Figures.Count == 0)
            {
                return;
            }
            if (activePath is null || activeStyleIndex != styleIndex)
            {
                FlushActivePath();
                activeStyleIndex = styleIndex;
                activePath = new PathGeometry();
            }
            foreach (PathFigure figure in source.Figures)
            {
                activePath.Figures.Add(figure);
            }
        }

        void FlushActivePath()
        {
            if (activePath is null)
            {
                return;
            }
            context.DrawPath(null, pens[activeStyleIndex], activePath);
            activePath = null;
            activeStyleIndex = -1;
        }

        void AddUnsupportedLineTypeDiagnostic(string lineTypeName, string reason)
        {
            string key = $"{lineTypeName}\0{reason}";
            if (!warnedLineTypes.Add(key))
            {
                return;
            }
            unsupportedLineTypes++;
            diagnostics.Add(new CadDiagnostic(
                CadDiagnosticSeverity.Warning,
                "CADCON001",
                $"Construction linetype '{lineTypeName}' is recorded as a continuous stroke because {reason}."));
        }
    }

    /// <summary>
    /// Clips one normalized construction primitive to a finite WCS-XY plan
    /// window without fabricating a distant endpoint.
    /// </summary>
    public static bool TryClipPlan(
        CadConstructionLinePrimitive line,
        CadBounds3D bounds,
        bool isRay,
        out CadPoint3D start,
        out CadPoint3D end) =>
        TryClipPlanInterval(
            line,
            bounds,
            isRay,
            out _,
            out _,
            out _,
            out start,
            out end);

    private static bool TryClipPlanInterval(
        CadConstructionLinePrimitive line,
        CadBounds3D bounds,
        bool isRay,
        out double parameterMinimum,
        out double parameterMaximum,
        out bool hasProjectedDirection,
        out CadPoint3D start,
        out CadPoint3D end)
    {
        double minimum = isRay ? 0.0 : double.NegativeInfinity;
        double maximum = double.PositiveInfinity;
        hasProjectedDirection = line.Direction.X != 0.0 || line.Direction.Y != 0.0;
        if (!hasProjectedDirection)
        {
            bool inside = line.BasePoint.X >= bounds.Min.X &&
                line.BasePoint.X <= bounds.Max.X &&
                line.BasePoint.Y >= bounds.Min.Y &&
                line.BasePoint.Y <= bounds.Max.Y;
            parameterMinimum = 0.0;
            parameterMaximum = 0.0;
            start = line.BasePoint;
            end = line.BasePoint;
            return inside;
        }

        if (!ClipAxis(line.BasePoint.X, line.Direction.X, bounds.Min.X, bounds.Max.X, ref minimum, ref maximum) ||
            !ClipAxis(line.BasePoint.Y, line.Direction.Y, bounds.Min.Y, bounds.Max.Y, ref minimum, ref maximum))
        {
            parameterMinimum = 0.0;
            parameterMaximum = 0.0;
            start = default;
            end = default;
            return false;
        }
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
        {
            throw new InvalidOperationException(
                "The construction-line clip interval exceeds finite retained coordinates.");
        }

        parameterMinimum = minimum;
        parameterMaximum = maximum;
        start = line.BasePoint + (line.Direction * minimum);
        end = line.BasePoint + (line.Direction * maximum);
        return true;
    }

    private static bool ClipAxis(
        double origin,
        double direction,
        double boundMinimum,
        double boundMaximum,
        ref double parameterMinimum,
        ref double parameterMaximum)
    {
        if (direction == 0.0)
        {
            return origin >= boundMinimum && origin <= boundMaximum;
        }

        double first = (boundMinimum - origin) / direction;
        double second = (boundMaximum - origin) / direction;
        if (first > second)
        {
            (first, second) = (second, first);
        }
        parameterMinimum = Math.Max(parameterMinimum, first);
        parameterMaximum = Math.Min(parameterMaximum, second);
        return parameterMinimum <= parameterMaximum;
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
            float thickness = options.LineWeightMode ==
                    CadPrintLineWeightMode.DeviceHairline ||
                style.IsHairline
                ? Pen.HairlineThickness
                : checked((float)(style.LineWeightMillimeters *
                    options.PhysicalDpi * options.LineWeightScale / 25.4));
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

    private static Vector2 Project(CadPoint3D point, CadPoint3D origin)
    {
        float x = checked((float)(point.X - origin.X));
        float y = checked((float)(point.Y - origin.Y));
        if (!float.IsFinite(x) || !float.IsFinite(y))
        {
            throw new InvalidOperationException(
                "A clipped construction-line coordinate exceeds the retained float range.");
        }
        return new Vector2(x, y);
    }

    private static void ValidateOptions(CadPlanSceneOptions options)
    {
        if (!float.IsFinite(options.PhysicalDpi) || options.PhysicalDpi <= 0.0f ||
            !float.IsFinite(options.LineWeightScale) || options.LineWeightScale <= 0.0f ||
            !Enum.IsDefined(options.LineWeightMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Physical DPI and lineweight scale must be finite and positive.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxLineTypeFigures, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxLineTypePatternSteps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxLineTypeSourceSegments, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxLineTypePlacements, 1);
    }
}
