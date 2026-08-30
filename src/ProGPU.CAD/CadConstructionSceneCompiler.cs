using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.CAD;

public readonly record struct CadConstructionSceneStatistics(
    int SourceEntityCount,
    int RecordedEntityCount,
    int RecordedCommandCount,
    int UnsupportedLineTypeCount);

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
/// Compilation is O(U) time and storage for U visible construction entities. The
/// parametric slab clip is allocation-free per entity and never fabricates a large
/// model-space endpoint. A camera change requires rebuilding only this overlay;
/// the ordinary finite retained picture remains reusable.
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
        int sourceCount = 0;
        int recordedCount = 0;
        int unsupportedLineTypes = 0;
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
            if (pattern.Kind != CadLineTypePatternKind.Continuous)
            {
                if (warnedLineTypes.Add(pattern.Name))
                {
                    unsupportedLineTypes++;
                    diagnostics.Add(new CadDiagnostic(
                        CadDiagnosticSeverity.Information,
                        "CADCON001",
                        $"Construction linetype '{pattern.Name}' is not recorded because its unbounded phase-origin contract is not yet supported."));
                }
                continue;
            }

            CadConstructionLinePrimitive line =
                snapshot.ConstructionLines.Span[entity.PrimitiveIndex];
            if (!TryClipPlan(
                    line,
                    planClipBounds,
                    entity.Kind == CadEntityKind.Ray,
                    out CadPoint3D start,
                    out CadPoint3D end))
            {
                continue;
            }

            Pen pen = pens[entity.StyleIndex];
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
                unsupportedLineTypes),
            diagnostics.ToArray());

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
    }

    internal static bool TryClipPlan(
        CadConstructionLinePrimitive line,
        CadBounds3D bounds,
        bool isRay,
        out CadPoint3D start,
        out CadPoint3D end)
    {
        double minimum = isRay ? 0.0 : double.NegativeInfinity;
        double maximum = double.PositiveInfinity;
        bool hasProjectedDirection = line.Direction.X != 0.0 || line.Direction.Y != 0.0;
        if (!hasProjectedDirection)
        {
            bool inside = line.BasePoint.X >= bounds.Min.X &&
                line.BasePoint.X <= bounds.Max.X &&
                line.BasePoint.Y >= bounds.Min.Y &&
                line.BasePoint.Y <= bounds.Max.Y;
            start = line.BasePoint;
            end = line.BasePoint;
            return inside;
        }

        if (!ClipAxis(line.BasePoint.X, line.Direction.X, bounds.Min.X, bounds.Max.X, ref minimum, ref maximum) ||
            !ClipAxis(line.BasePoint.Y, line.Direction.Y, bounds.Min.Y, bounds.Max.Y, ref minimum, ref maximum))
        {
            start = default;
            end = default;
            return false;
        }
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
        {
            throw new InvalidOperationException(
                "The construction-line clip interval exceeds finite retained coordinates.");
        }

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
    }
}
