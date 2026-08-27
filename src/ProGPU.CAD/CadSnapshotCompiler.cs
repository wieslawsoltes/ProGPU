using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Extensions;
using CSMath;

namespace ProGPU.CAD;

public sealed class CadSnapshotOptions
{
    public const int DefaultDiagnosticLimit = 256;

    public double DefaultLineWeightMillimeters { get; init; } = 0.25;
    public int DiagnosticLimit { get; init; } = DefaultDiagnosticLimit;
    public bool IncludeNonPlottableLayers { get; init; } = true;
}

/// <summary>Compiles the mutable ACadSharp graph into immutable ProGPU CAD streams.</summary>
public sealed class CadSnapshotCompiler
{
    private const double TwoPi = Math.PI * 2.0;

    public CadDocumentSnapshot Compile(
        CadDocumentSession session,
        CadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        options ??= new CadSnapshotOptions();
        ValidateOptions(options);

        return session.Capture(
            (document, generation) => Compile(document, generation, options, cancellationToken));
    }

    private static CadDocumentSnapshot Compile(
        CadDocument document,
        ulong generation,
        CadSnapshotOptions options,
        CancellationToken cancellationToken)
    {
        var layers = new List<CadLayerSnapshot>();
        var layerIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var styles = new List<CadStrokeStyle>();
        var styleIndices = new Dictionary<CadStrokeStyle, int>();
        var entities = new List<CadEntityHeader>(document.Entities.Count);
        var lines = new List<CadLinePrimitive>();
        var circles = new List<CadCirclePrimitive>();
        var arcs = new List<CadArcPrimitive>();
        var splines = new List<CadSplinePrimitive>();
        var polylines = new List<CadPolylinePrimitive>();
        var polylineVertices = new List<CadPolylineVertex>();
        var splineControlPoints = new List<CadPoint3D>();
        var splineKnots = new List<double>();
        var splineWeights = new List<double>();
        var diagnostics = new List<CadDiagnostic>(Math.Min(options.DiagnosticLimit, 16));
        CadBounds3D documentBounds = CadBounds3D.Empty;
        int visibleCount = 0;
        int unsupportedCount = 0;
        int invalidCount = 0;

        foreach (Entity entity in document.Entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entity.IsInvisible || !entity.Layer.IsOn ||
                (!options.IncludeNonPlottableLayers && !entity.Layer.PlotFlag))
            {
                continue;
            }

            visibleCount++;
            int layerIndex = InternLayer(entity, layers, layerIndices);
            int styleIndex = InternStyle(entity, options, styles, styleIndices);

            try
            {
                CadEntityHeader? header = entity switch
                {
                    Line line => CompileLine(line, layerIndex, styleIndex, lines),
                    Arc arc => CompileArc(arc, layerIndex, styleIndex, arcs),
                    Circle circle => CompileCircle(circle, layerIndex, styleIndex, circles),
                    Spline spline => CompileSpline(
                        spline,
                        layerIndex,
                        styleIndex,
                        splines,
                        splineControlPoints,
                        splineKnots,
                        splineWeights),
                    LwPolyline polyline => CompilePolyline(
                        polyline,
                        layerIndex,
                        styleIndex,
                        polylines,
                        polylineVertices),
                    _ => null,
                };

                if (header is CadEntityHeader value)
                {
                    entities.Add(value);
                    documentBounds = documentBounds.Union(value.Bounds);
                }
                else
                {
                    unsupportedCount++;
                    AddDiagnostic(
                        diagnostics,
                        options.DiagnosticLimit,
                        new CadDiagnostic(
                            CadDiagnosticSeverity.Information,
                            "CADSNAP001",
                            $"Entity {entity.Handle:X} ({entity.ObjectName}) is not yet represented in the analytic snapshot."));
                }
            }
            catch (CadUnsupportedEntityException exception)
            {
                unsupportedCount++;
                AddDiagnostic(
                    diagnostics,
                    options.DiagnosticLimit,
                    new CadDiagnostic(
                        CadDiagnosticSeverity.Information,
                        "CADSNAP003",
                        $"Entity {entity.Handle:X} ({entity.ObjectName}) is not yet supported: {exception.Message}"));
            }
            catch (Exception exception) when (
                exception is ArgumentException or ArithmeticException or InvalidOperationException)
            {
                invalidCount++;
                AddDiagnostic(
                    diagnostics,
                    options.DiagnosticLimit,
                    new CadDiagnostic(
                        CadDiagnosticSeverity.Warning,
                        "CADSNAP002",
                        $"Entity {entity.Handle:X} ({entity.ObjectName}) was rejected: {exception.Message}"));
            }
        }

        return new CadDocumentSnapshot(
            generation,
            documentBounds,
            new CadSnapshotStatistics(
                document.Entities.Count,
                visibleCount,
                unsupportedCount,
                invalidCount),
            layers.ToArray(),
            styles.ToArray(),
            entities.ToArray(),
            lines.ToArray(),
            circles.ToArray(),
            arcs.ToArray(),
            splines.ToArray(),
            polylines.ToArray(),
            polylineVertices.ToArray(),
            splineControlPoints.ToArray(),
            splineKnots.ToArray(),
            splineWeights.ToArray(),
            diagnostics.ToArray());
    }

    private static CadEntityHeader CompileLine(
        Line line,
        int layerIndex,
        int styleIndex,
        List<CadLinePrimitive> destination)
    {
        CadPoint3D start = ToPoint(line.StartPoint);
        CadPoint3D end = ToPoint(line.EndPoint);
        EnsureFinite(start);
        EnsureFinite(end);
        int primitiveIndex = destination.Count;
        destination.Add(new CadLinePrimitive(start, end));
        return new CadEntityHeader(
            line.Handle,
            CadEntityKind.Line,
            layerIndex,
            styleIndex,
            primitiveIndex,
            CadBounds3D.FromPoint(start).Include(end));
    }

    private static CadEntityHeader CompileCircle(
        Circle circle,
        int layerIndex,
        int styleIndex,
        List<CadCirclePrimitive> destination)
    {
        ValidateRadius(circle.Radius);
        CadCoordinateSystem basis = CadCoordinateSystem.FromNormal(ToPoint(circle.Normal));
        CadPoint3D center = basis.Transform(ToPoint(circle.Center));
        EnsureFinite(center);
        int primitiveIndex = destination.Count;
        destination.Add(new CadCirclePrimitive(center, basis, circle.Radius));
        return new CadEntityHeader(
            circle.Handle,
            CadEntityKind.Circle,
            layerIndex,
            styleIndex,
            primitiveIndex,
            CadBounds3D.Circle(center, basis, circle.Radius));
    }

    private static CadEntityHeader CompileArc(
        Arc arc,
        int layerIndex,
        int styleIndex,
        List<CadArcPrimitive> destination)
    {
        ValidateRadius(arc.Radius);
        if (!double.IsFinite(arc.StartAngle) || !double.IsFinite(arc.EndAngle))
        {
            throw new ArgumentException("Arc angles must be finite.");
        }

        CadCoordinateSystem basis = CadCoordinateSystem.FromNormal(ToPoint(arc.Normal));
        CadPoint3D center = basis.Transform(ToPoint(arc.Center));
        EnsureFinite(center);
        double start = NormalizeAngle(arc.StartAngle);
        double sweep = NormalizePositiveSweep(arc.StartAngle, arc.EndAngle);
        int primitiveIndex = destination.Count;
        destination.Add(new CadArcPrimitive(center, basis, arc.Radius, start, sweep));
        return new CadEntityHeader(
            arc.Handle,
            CadEntityKind.Arc,
            layerIndex,
            styleIndex,
            primitiveIndex,
            CadBounds3D.Arc(center, basis, arc.Radius, start, sweep));
    }

    private static CadEntityHeader CompileSpline(
        Spline spline,
        int layerIndex,
        int styleIndex,
        List<CadSplinePrimitive> destination,
        List<CadPoint3D> controlPoints,
        List<double> knots,
        List<double> weights)
    {
        if (spline.Degree < 1 || spline.Degree > Spline.MaxDegree ||
            spline.ControlPoints.Count < spline.Degree + 1 ||
            spline.Knots.Count == 0)
        {
            throw new ArgumentException("Spline degree, control points, or knot vector is invalid.");
        }

        if (spline.Weights.Count != 0 && spline.Weights.Count != spline.ControlPoints.Count)
        {
            throw new ArgumentException("Spline weight count must be zero or match its control-point count.");
        }

        CadBounds3D bounds = CadBounds3D.Empty;
        foreach (XYZ value in spline.ControlPoints)
        {
            CadPoint3D point = ToPoint(value);
            EnsureFinite(point);
            bounds = bounds.Include(point);
        }

        foreach (double knot in spline.Knots)
        {
            if (!double.IsFinite(knot))
            {
                throw new ArgumentException("Spline knots must be finite.");
            }
        }

        foreach (double weight in spline.Weights)
        {
            if (!double.IsFinite(weight) || weight <= 0.0)
            {
                throw new ArgumentException("Spline weights must be finite and positive.");
            }
        }

        int controlOffset = controlPoints.Count;
        controlPoints.AddRange(spline.ControlPoints.Select(ToPoint));
        int knotOffset = knots.Count;
        knots.AddRange(spline.Knots);
        int weightOffset = weights.Count;
        weights.AddRange(spline.Weights);
        int primitiveIndex = destination.Count;
        destination.Add(new CadSplinePrimitive(
            controlOffset,
            spline.ControlPoints.Count,
            knotOffset,
            spline.Knots.Count,
            weightOffset,
            spline.Weights.Count,
            spline.Degree,
            spline.IsClosed));
        return new CadEntityHeader(
            spline.Handle,
            CadEntityKind.Spline,
            layerIndex,
            styleIndex,
            primitiveIndex,
            bounds);
    }

    private static CadEntityHeader CompilePolyline(
        LwPolyline polyline,
        int layerIndex,
        int styleIndex,
        List<CadPolylinePrimitive> destination,
        List<CadPolylineVertex> vertices)
    {
        if (polyline.Vertices.Count < 2)
        {
            throw new ArgumentException("A lightweight polyline must contain at least two vertices.");
        }

        if (polyline.ConstantWidth != 0.0 ||
            polyline.Vertices.Any(vertex => vertex.StartWidth != 0.0 || vertex.EndWidth != 0.0))
        {
            throw new CadUnsupportedEntityException(
                "Wide lightweight polylines require filled-outline lowering and cannot be treated as cosmetic strokes.");
        }

        if (!double.IsFinite(polyline.Elevation))
        {
            throw new ArgumentException("Polyline elevation must be finite.");
        }

        CadCoordinateSystem basis = CadCoordinateSystem.FromNormal(ToPoint(polyline.Normal));
        LwPolyline.Vertex first = polyline.Vertices[0];
        double localOriginX = first.Location.X;
        double localOriginY = first.Location.Y;
        CadPoint3D worldOrigin = basis.Transform(
            new CadPoint3D(localOriginX, localOriginY, polyline.Elevation));
        EnsureFinite(worldOrigin);

        var normalizedVertices = new CadPolylineVertex[polyline.Vertices.Count];
        for (int i = 0; i < polyline.Vertices.Count; i++)
        {
            LwPolyline.Vertex vertex = polyline.Vertices[i];
            double x = vertex.Location.X - localOriginX;
            double y = vertex.Location.Y - localOriginY;
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(vertex.Bulge))
            {
                throw new ArgumentException("Polyline locations and bulges must be finite.");
            }

            normalizedVertices[i] = new CadPolylineVertex(x, y, vertex.Bulge);
        }

        ReadOnlySpan<CadPolylineVertex> added = normalizedVertices;
        CadBounds3D bounds = CadBounds3D.Empty;
        int segmentCount = polyline.IsClosed ? added.Length : added.Length - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            CadPolylineVertex start = added[i];
            CadPolylineVertex end = added[(i + 1) % added.Length];
            CadPoint3D worldStart = TransformPolylinePoint(worldOrigin, basis, start);
            CadPoint3D worldEnd = TransformPolylinePoint(worldOrigin, basis, end);
            if (start.Bulge == 0.0)
            {
                bounds = bounds.Union(CadBounds3D.FromPoint(worldStart).Include(worldEnd));
                continue;
            }

            GetBulgeArc(start, end, out double centerX, out double centerY, out double radius, out double startAngle, out double sweep);
            CadPoint3D center = worldOrigin + (basis.XAxis * centerX) + (basis.YAxis * centerY);
            bounds = bounds.Union(CadBounds3D.Arc(center, basis, radius, startAngle, sweep));
        }

        int vertexOffset = vertices.Count;
        vertices.AddRange(normalizedVertices);
        int primitiveIndex = destination.Count;
        destination.Add(new CadPolylinePrimitive(
            worldOrigin,
            basis,
            vertexOffset,
            polyline.Vertices.Count,
            polyline.IsClosed));
        return new CadEntityHeader(
            polyline.Handle,
            CadEntityKind.LightweightPolyline,
            layerIndex,
            styleIndex,
            primitiveIndex,
            bounds);
    }

    internal static void GetBulgeArc(
        CadPolylineVertex start,
        CadPolylineVertex end,
        out double centerX,
        out double centerY,
        out double radius,
        out double startAngle,
        out double sweep)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double scale = Math.Max(Math.Abs(dx), Math.Abs(dy));
        double chord = scale == 0.0
            ? 0.0
            : scale * Math.Sqrt(((dx / scale) * (dx / scale)) + ((dy / scale) * (dy / scale)));
        double bulge = start.Bulge;
        if (!double.IsFinite(chord) || chord <= 0.0 || bulge == 0.0)
        {
            throw new ArgumentException("A bulge arc requires distinct endpoints and a non-zero bulge.");
        }

        double centerFactor = ((1.0 / bulge) - bulge) * 0.25;
        centerX = start.X + (dx * 0.5) - (dy * centerFactor);
        centerY = start.Y + (dy * 0.5) + (dx * centerFactor);
        double absoluteBulge = Math.Abs(bulge);
        radius = (chord * 0.25) * (absoluteBulge + (1.0 / absoluteBulge));
        startAngle = Math.Atan2(start.Y - centerY, start.X - centerX);
        sweep = 4.0 * Math.Atan(bulge);
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY) ||
            !double.IsFinite(radius) || !double.IsFinite(startAngle) || !double.IsFinite(sweep))
        {
            throw new ArithmeticException("Polyline bulge geometry exceeds the supported numeric range.");
        }
    }

    private static CadPoint3D TransformPolylinePoint(
        CadPoint3D worldOrigin,
        CadCoordinateSystem basis,
        CadPolylineVertex vertex) =>
        worldOrigin + (basis.XAxis * vertex.X) + (basis.YAxis * vertex.Y);

    private static int InternLayer(
        Entity entity,
        List<CadLayerSnapshot> layers,
        Dictionary<string, int> indices)
    {
        string name = entity.Layer.Name;
        if (indices.TryGetValue(name, out int index))
        {
            return index;
        }

        index = layers.Count;
        indices.Add(name, index);
        layers.Add(new CadLayerSnapshot(name, entity.Layer.IsOn, entity.Layer.PlotFlag));
        return index;
    }

    private static int InternStyle(
        Entity entity,
        CadSnapshotOptions options,
        List<CadStrokeStyle> styles,
        Dictionary<CadStrokeStyle, int> indices)
    {
        ACadSharp.Color color = entity.GetActiveColor();
        LineWeightType lineWeight = entity.GetActiveLineWeightType();
        double millimeters = lineWeight is LineWeightType.Default or LineWeightType.ByLayer or LineWeightType.ByBlock
            ? options.DefaultLineWeightMillimeters
            : lineWeight.GetLineWeightValue();
        short transparency = entity.Transparency.Value;
        byte alpha = transparency is < 0 or > 90
            ? byte.MaxValue
            : (byte)Math.Round(255.0 * (100.0 - transparency) / 100.0);
        CadStrokeStyle style = new(
            color.R,
            color.G,
            color.B,
            alpha,
            millimeters,
            lineWeight == LineWeightType.W0,
            entity.GetActiveLineType().Name,
            entity.LineTypeScale);

        if (indices.TryGetValue(style, out int index))
        {
            return index;
        }

        index = styles.Count;
        indices.Add(style, index);
        styles.Add(style);
        return index;
    }

    private static double NormalizePositiveSweep(double start, double end)
    {
        double sweep = (end - start) % TwoPi;
        if (sweep < 0.0)
        {
            sweep += TwoPi;
        }

        return sweep == 0.0 ? TwoPi : sweep;
    }

    private static double NormalizeAngle(double angle)
    {
        double normalized = angle % TwoPi;
        return normalized < 0.0 ? normalized + TwoPi : normalized;
    }

    private static void ValidateRadius(double radius)
    {
        if (!double.IsFinite(radius) || radius <= 0.0)
        {
            throw new ArgumentException("A circle or arc radius must be finite and positive.");
        }
    }

    private static void ValidateOptions(CadSnapshotOptions options)
    {
        if (!double.IsFinite(options.DefaultLineWeightMillimeters) ||
            options.DefaultLineWeightMillimeters <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Default lineweight must be finite and positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.DiagnosticLimit);
    }

    private static void AddDiagnostic(
        List<CadDiagnostic> diagnostics,
        int limit,
        CadDiagnostic diagnostic)
    {
        if (diagnostics.Count < limit)
        {
            diagnostics.Add(diagnostic);
        }
    }

    private static CadPoint3D ToPoint(XYZ point) => new(point.X, point.Y, point.Z);

    private static void EnsureFinite(CadPoint3D point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
        {
            throw new ArgumentException("CAD coordinates must be finite.");
        }
    }

    private sealed class CadUnsupportedEntityException : Exception
    {
        public CadUnsupportedEntityException(string message)
            : base(message)
        {
        }
    }
}
