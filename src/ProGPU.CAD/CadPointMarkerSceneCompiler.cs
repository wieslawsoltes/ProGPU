using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.CAD;

/// <summary>
/// The finite view used to regenerate AutoCAD POINT marker sizes.
/// </summary>
public readonly record struct CadPointMarkerView(
    float ViewportHeightPixels,
    double ModelUnitsPerPixel)
{
    public static CadPointMarkerView FromViewport(CadPlanViewport viewport) =>
        new(viewport.ViewportSize.Y, 1.0 / viewport.Zoom);
}

public readonly record struct CadPointMarkerSceneStatistics(
    int RecordedPointCount,
    int RecordedCommandCount);

/// <summary>A retained, view-resolved POINT marker overlay.</summary>
public sealed class CadRecordedPointMarkerScene
{
    public ulong ContentGeneration { get; }
    public CadPoint3D RebaseOrigin { get; }
    public DrawingContext DrawingContext { get; }
    public CadPointMarkerSceneStatistics Statistics { get; }

    internal CadRecordedPointMarkerScene(
        ulong contentGeneration,
        CadPoint3D rebaseOrigin,
        DrawingContext drawingContext,
        CadPointMarkerSceneStatistics statistics)
    {
        ContentGeneration = contentGeneration;
        RebaseOrigin = rebaseOrigin;
        DrawingContext = drawingContext;
        Statistics = statistics;
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
/// Regenerates documented PDMODE 2-4 and 32/64/96 enclosure combinations from
/// immutable POINT records. PDSIZE zero and negative values are resolved from
/// the supplied finite viewport; positive values remain drawing-unit sizes.
/// </summary>
/// <remarks>
/// Compilation is O(P) time and coalesces each contiguous style range into one
/// retained analytic path, with an individual-command fallback only for the rare
/// affine-sheared circle enclosure. Camera-only replay performs no regeneration;
/// callers rebuild this small overlay when viewport height or zoom changes.
/// Existing line, ellipse, and polyline commands keep managed/native replay on
/// the shared picture contract without a CAD-specific shader or ABI record.
/// </remarks>
public sealed class CadPointMarkerSceneCompiler
{
    public CadRecordedPointMarkerScene Compile(
        CadDocumentSnapshot snapshot,
        CadPointMarkerView view,
        CadPlanSceneOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateView(view);
        options ??= new CadPlanSceneOptions();
        ValidateOptions(options);

        ReadOnlySpan<CadEntityHeader> entities = snapshot.Entities.Span;
        ReadOnlySpan<CadLayerSnapshot> layers = snapshot.Layers.Span;
        ReadOnlySpan<CadStrokeStyle> styles = snapshot.Styles.Span;
        Pen?[] pens = new Pen?[styles.Length];
        var context = new DrawingContext();
        int recorded = 0;
        int currentStyleIndex = -1;
        Pen? currentPen = null;
        PathGeometry? currentPath = null;

        foreach (CadEntityHeader entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entity.Kind != CadEntityKind.Point ||
                (!options.IncludeNonPlottableLayers &&
                    !layers[entity.LayerIndex].IsPlottable))
            {
                continue;
            }

            CadPointPrimitive point = snapshot.Points.Span[entity.PrimitiveIndex];
            if (point.DisplayMode is 0 or 1)
            {
                continue;
            }

            Pen pen = pens[entity.StyleIndex] ??=
                CreatePen(styles[entity.StyleIndex], options);
            if (currentStyleIndex != entity.StyleIndex)
            {
                FlushPath();
                currentStyleIndex = entity.StyleIndex;
                currentPen = pen;
            }
            double markerSize = ResolveMarkerSize(point.DisplaySize, view);
            currentPath ??= new PathGeometry();
            if (!TryAppendMarker(
                    currentPath,
                    point,
                    snapshot.RebaseOrigin,
                    markerSize))
            {
                FlushPath();
                RecordMarker(
                    context,
                    pen,
                    point,
                    snapshot.RebaseOrigin,
                    markerSize);
            }
            recorded++;
        }

        FlushPath();

        context.TrimRetainedCommandCapacity();
        return new CadRecordedPointMarkerScene(
            snapshot.ContentGeneration,
            snapshot.RebaseOrigin,
            context,
            new CadPointMarkerSceneStatistics(recorded, context.Commands.Count));

        void FlushPath()
        {
            if (currentPath is { Figures.Count: > 0 } path && currentPen is not null)
            {
                context.DrawPath(null, currentPen, path);
            }
            currentPath = null;
        }
    }

    private static bool TryAppendMarker(
        PathGeometry path,
        in CadPointPrimitive point,
        in CadPoint3D rebaseOrigin,
        double markerSize)
    {
        Vector2 center = Project(point.Position, rebaseOrigin);
        float halfSize = ToFloat(markerSize * 0.5);
        Vector2 xAxis = ProjectVector(point.MarkerXAxis) * halfSize;
        Vector2 yAxis = ProjectVector(point.MarkerYAxis) * halfSize;
        int baseMode = point.DisplayMode & 31;
        int enclosureMode = point.DisplayMode & 96;
        if ((enclosureMode & 32) != 0 && !CanRepresentEllipseAsArc(xAxis, yAxis))
        {
            return false;
        }

        switch (baseMode)
        {
            case 0:
            case 1:
                break;
            case 2:
                AppendLine(path, center - xAxis, center + xAxis);
                AppendLine(path, center - yAxis, center + yAxis);
                break;
            case 3:
                AppendLine(
                    path,
                    center - xAxis - yAxis,
                    center + xAxis + yAxis);
                AppendLine(
                    path,
                    center - xAxis + yAxis,
                    center + xAxis - yAxis);
                break;
            case 4:
                AppendLine(path, center - yAxis, center + yAxis);
                break;
            default:
                throw new InvalidOperationException(
                    $"Snapshot contains unvalidated PDMODE {point.DisplayMode}.");
        }

        if ((enclosureMode & 32) != 0)
        {
            AppendEllipse(path, center, xAxis, yAxis);
        }
        if ((enclosureMode & 64) != 0)
        {
            var square = new PathFigure(center - xAxis - yAxis, isClosed: true)
            {
                IsFilled = false,
            };
            square.Segments.Add(new LineSegment(center + xAxis - yAxis));
            square.Segments.Add(new LineSegment(center + xAxis + yAxis));
            square.Segments.Add(new LineSegment(center - xAxis + yAxis));
            path.Figures.Add(square);
        }
        return true;
    }

    private static void AppendLine(PathGeometry path, Vector2 start, Vector2 end)
    {
        var figure = new PathFigure(start)
        {
            IsFilled = false,
        };
        figure.Segments.Add(new LineSegment(end));
        path.Figures.Add(figure);
    }

    private static void AppendEllipse(
        PathGeometry path,
        Vector2 center,
        Vector2 xAxis,
        Vector2 yAxis)
    {
        float radiusX = xAxis.Length();
        float radiusY = yAxis.Length();
        float rotation = MathF.Atan2(xAxis.Y, xAxis.X) * (180.0f / MathF.PI);
        SweepDirection sweep = Cross(xAxis, yAxis) >= 0.0f
            ? SweepDirection.Counterclockwise
            : SweepDirection.Clockwise;
        var figure = new PathFigure(center + xAxis)
        {
            IsFilled = false,
            IsClosed = true,
        };
        figure.Segments.Add(new ArcSegment(
            center - xAxis,
            new Vector2(radiusX, radiusY),
            rotation,
            isLargeArc: false,
            sweep));
        figure.Segments.Add(new ArcSegment(
            center + xAxis,
            new Vector2(radiusX, radiusY),
            rotation,
            isLargeArc: false,
            sweep));
        path.Figures.Add(figure);
    }

    private static bool CanRepresentEllipseAsArc(Vector2 xAxis, Vector2 yAxis)
    {
        float xLength = xAxis.Length();
        float yLength = yAxis.Length();
        if (!(xLength > 0.0f) || !(yLength > 0.0f) ||
            !float.IsFinite(xLength) || !float.IsFinite(yLength))
        {
            return false;
        }
        float normalizedDot = MathF.Abs(Vector2.Dot(xAxis, yAxis)) /
            (xLength * yLength);
        return normalizedDot <= 1e-5f;
    }

    private static float Cross(Vector2 first, Vector2 second) =>
        (first.X * second.Y) - (first.Y * second.X);

    private static void RecordMarker(
        DrawingContext context,
        Pen pen,
        in CadPointPrimitive point,
        in CadPoint3D rebaseOrigin,
        double markerSize)
    {
        Vector2 center = Project(point.Position, rebaseOrigin);
        float halfSize = ToFloat(markerSize * 0.5);
        Vector2 xAxis = ProjectVector(point.MarkerXAxis) * halfSize;
        Vector2 yAxis = ProjectVector(point.MarkerYAxis) * halfSize;
        int baseMode = point.DisplayMode & 31;
        int enclosureMode = point.DisplayMode & 96;

        switch (baseMode)
        {
            case 0:
            case 1:
                break;
            case 2:
                context.DrawLine(pen, center - xAxis, center + xAxis);
                context.DrawLine(pen, center - yAxis, center + yAxis);
                break;
            case 3:
                context.DrawLine(
                    pen,
                    center - xAxis - yAxis,
                    center + xAxis + yAxis);
                context.DrawLine(
                    pen,
                    center - xAxis + yAxis,
                    center + xAxis - yAxis);
                break;
            case 4:
                context.DrawLine(pen, center - yAxis, center + yAxis);
                break;
            default:
                throw new InvalidOperationException(
                    $"Snapshot contains unvalidated PDMODE {point.DisplayMode}.");
        }

        if ((enclosureMode & 32) != 0)
        {
            context.DrawEllipse(
                null,
                pen,
                Vector2.Zero,
                1.0f,
                1.0f,
                CreateAffineTransform(center, xAxis, yAxis));
        }
        if ((enclosureMode & 64) != 0)
        {
            Span<Vector2> square = stackalloc Vector2[4];
            square[0] = center - xAxis - yAxis;
            square[1] = center + xAxis - yAxis;
            square[2] = center + xAxis + yAxis;
            square[3] = center - xAxis + yAxis;
            context.DrawPolyline(pen, square, isClosed: true);
        }
    }

    private static double ResolveMarkerSize(
        double displaySize,
        in CadPointMarkerView view)
    {
        if (displaySize > 0.0)
        {
            return displaySize;
        }

        double percentage = displaySize == 0.0
            ? 5.0
            : Math.Abs(displaySize);
        double size = view.ViewportHeightPixels * view.ModelUnitsPerPixel *
            percentage / 100.0;
        if (!(size > 0.0) || !double.IsFinite(size))
        {
            throw new ArgumentOutOfRangeException(
                nameof(view),
                "The POINT marker size resolved outside the finite positive range.");
        }
        return size;
    }

    private static Pen CreatePen(
        in CadStrokeStyle style,
        CadPlanSceneOptions options)
    {
        var brush = new SolidColorBrush(new Vector4(
            style.Red / 255.0f,
            style.Green / 255.0f,
            style.Blue / 255.0f,
            style.Alpha / 255.0f));
        float thickness = options.LineWeightMode ==
                CadPrintLineWeightMode.DeviceHairline ||
            style.IsHairline
            ? Pen.HairlineThickness
            : checked((float)(
                style.LineWeightMillimeters *
                options.PhysicalDpi *
                options.LineWeightScale /
                25.4));
        return new Pen(
            brush,
            thickness,
            lineJoin: PenLineJoin.Round,
            startLineCap: PenLineCap.Round,
            endLineCap: PenLineCap.Round,
            strokeTransformMode: PenStrokeTransformMode.Fixed);
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

    private static Vector2 Project(
        in CadPoint3D point,
        in CadPoint3D origin) =>
        new(ToFloat(point.X - origin.X), ToFloat(point.Y - origin.Y));

    private static Vector2 ProjectVector(in CadPoint3D vector) =>
        new(ToFloat(vector.X), ToFloat(vector.Y));

    private static float ToFloat(double value)
    {
        float result = checked((float)value);
        if (!float.IsFinite(result))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "CAD marker geometry exceeds the finite float range.");
        }
        return result;
    }

    private static void ValidateView(in CadPointMarkerView view)
    {
        if (!float.IsFinite(view.ViewportHeightPixels) ||
            view.ViewportHeightPixels <= 0.0f ||
            !double.IsFinite(view.ModelUnitsPerPixel) ||
            view.ModelUnitsPerPixel <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(view),
                "POINT marker view dimensions must be finite and positive.");
        }
    }

    private static void ValidateOptions(CadPlanSceneOptions options)
    {
        if (!float.IsFinite(options.PhysicalDpi) || options.PhysicalDpi <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(options.PhysicalDpi));
        }
        if (!float.IsFinite(options.LineWeightScale) || options.LineWeightScale <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(options.LineWeightScale));
        }
        if (!Enum.IsDefined(options.LineWeightMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options.LineWeightMode));
        }
    }
}
