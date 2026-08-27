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
/// conversion. Text adds O(R) retained commands for R contiguous font runs; it
/// does not copy or expand glyph streams. A later camera or viewport change can
/// reuse the recorded scene.
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
            if (UsesStroke(entity.Kind) &&
                !IsContinuous(style.LineTypeName) &&
                warnedLineTypes.Add(style.LineTypeName))
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
                case CadEntityKind.Ellipse:
                    RecordEllipse(context, pen, snapshot.Ellipses.Span[entity.PrimitiveIndex], snapshot.RebaseOrigin);
                    break;
                case CadEntityKind.Solid:
                    RecordSolid(context, pen.Brush, snapshot.Faces.Span[entity.PrimitiveIndex], snapshot.RebaseOrigin);
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

    private static bool IsContinuous(string name) =>
        name.Equals("Continuous", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase);

    private static bool UsesStroke(CadEntityKind kind) =>
        kind is not (CadEntityKind.Solid or CadEntityKind.Text);

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
