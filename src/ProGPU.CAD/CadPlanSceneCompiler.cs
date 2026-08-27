using System.Buffers;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.CAD;

public sealed class CadPlanSceneOptions
{
    public float PhysicalDpi { get; init; } = 96.0f;
    public float LineWeightScale { get; init; } = 1.0f;
}

public readonly record struct CadPlanSceneStatistics(
    int RecordedEntityCount,
    int RecordedCommandCount,
    int UnsupportedLineTypeCount);

/// <summary>A retained top/WCS-XY projection ready for ordinary ProGPU compilation.</summary>
public sealed class CadRecordedPlanScene
{
    private readonly CadDiagnostic[] _diagnostics;

    public ulong ContentGeneration { get; }
    public CadPoint3D RebaseOrigin { get; }
    public DrawingContext DrawingContext { get; }
    public CadPlanSceneStatistics Statistics { get; }
    public ReadOnlyMemory<CadDiagnostic> Diagnostics => _diagnostics;

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
/// conversion. A later camera or viewport change can reuse the recorded scene.
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
        ReadOnlySpan<CadStrokeStyle> styles = snapshot.Styles.Span;
        var context = new DrawingContext();
        context.EnsureCommandCapacity(entities.Length);
        Pen[] pens = CreatePens(styles, options);
        var diagnostics = new List<CadDiagnostic>();
        var warnedLineTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int recorded = 0;
        int unsupportedLineTypes = 0;

        foreach (CadEntityHeader entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CadStrokeStyle style = styles[entity.StyleIndex];
            if (!IsContinuous(style.LineTypeName) && warnedLineTypes.Add(style.LineTypeName))
            {
                unsupportedLineTypes++;
                diagnostics.Add(new CadDiagnostic(
                    CadDiagnosticSeverity.Warning,
                    "CADSCENE001",
                    $"Linetype '{style.LineTypeName}' is recorded as a continuous stroke until CAD dash-pattern lowering is enabled."));
            }

            Pen pen = pens[entity.StyleIndex];
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
                case CadEntityKind.Spline:
                    RecordSpline(context, pen, snapshot, snapshot.Splines.Span[entity.PrimitiveIndex]);
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
            new CadPlanSceneStatistics(recorded, context.Commands.Count, unsupportedLineTypes),
            diagnostics.ToArray());
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

    private static void RecordSpline(
        DrawingContext context,
        Pen pen,
        CadDocumentSnapshot snapshot,
        CadSplinePrimitive spline)
    {
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

    private static Matrix4x4 CreateProjectionTransform(
        CadPoint3D center,
        CadCoordinateSystem basis,
        CadPoint3D origin) =>
        new(
            ToFloat(basis.XAxis.X), ToFloat(basis.XAxis.Y), 0.0f, 0.0f,
            ToFloat(basis.YAxis.X), ToFloat(basis.YAxis.Y), 0.0f, 0.0f,
            0.0f, 0.0f, 1.0f, 0.0f,
            ToFloat(center.X - origin.X), ToFloat(center.Y - origin.Y), 0.0f, 1.0f);

    private static Vector2 Project(CadPoint3D point, CadPoint3D origin) =>
        new(ToFloat(point.X - origin.X), ToFloat(point.Y - origin.Y));

    private static float ToFloat(double value)
    {
        float converted = (float)value;
        if (!float.IsFinite(converted))
        {
            throw new InvalidOperationException("A rebased CAD coordinate exceeds the retained float range.");
        }

        return converted;
    }

    private static bool IsContinuous(string name) =>
        name.Equals("Continuous", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase);

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
    }
}
