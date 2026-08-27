using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Text;
using System.Numerics;

namespace ProGPU.CAD;

public sealed class CadSnapshotOptions
{
    public const int DefaultDiagnosticLimit = 256;
    public const int DefaultMaxBlockNestingDepth = 32;
    public const int DefaultMaxBlockArrayInstances = 1_000_000;
    public const int DefaultMaxExpandedEntities = 5_000_000;
    public const int DefaultMaxTextCodeUnitsPerEntity = 65_536;
    public const int DefaultMaxTextGlyphs = 4_000_000;

    public double DefaultLineWeightMillimeters { get; init; } = 0.25;
    public int DiagnosticLimit { get; init; } = DefaultDiagnosticLimit;
    public int MaxBlockNestingDepth { get; init; } = DefaultMaxBlockNestingDepth;
    public int MaxBlockArrayInstances { get; init; } = DefaultMaxBlockArrayInstances;
    public int MaxExpandedEntities { get; init; } = DefaultMaxExpandedEntities;
    public int MaxTextCodeUnitsPerEntity { get; init; } = DefaultMaxTextCodeUnitsPerEntity;
    public int MaxTextGlyphs { get; init; } = DefaultMaxTextGlyphs;
    public bool IncludeNonPlottableLayers { get; init; } = true;
    public ICadTextFontResolver? TextFontResolver { get; init; }
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
        var ellipses = new List<CadEllipsePrimitive>();
        var faces = new List<CadFacePrimitive>();
        var splines = new List<CadSplinePrimitive>();
        var polylines = new List<CadPolylinePrimitive>();
        var polylines3D = new List<CadPolyline3DPrimitive>();
        var texts = new List<CadTextPrimitive>();
        var textGlyphRuns = new List<CadTextGlyphRun>();
        var textGlyphIndices = new List<ushort>();
        var textGlyphPositions = new List<Vector2>();
        var textFonts = new List<TtfFont>();
        var textFontIndices = new Dictionary<TtfFont, int>(ReferenceEqualityComparer.Instance);
        var polylineVertices = new List<CadPolylineVertex>();
        var polyline3DPoints = new List<CadPoint3D>();
        var splineControlPoints = new List<CadPoint3D>();
        var splineKnots = new List<double>();
        var splineWeights = new List<double>();
        var diagnostics = new List<CadDiagnostic>(Math.Min(options.DiagnosticLimit, 16));
        CadBounds3D documentBounds = CadBounds3D.Empty;
        int visibleCount = 0;
        int expandedCount = 0;
        int unsupportedCount = 0;
        int invalidCount = 0;
        var activeBlocks = new HashSet<BlockRecord>(ReferenceEqualityComparer.Instance);

        foreach (Entity entity in document.Entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entity.IsInvisible || !entity.Layer.IsOn ||
                (!options.IncludeNonPlottableLayers && !entity.Layer.PlotFlag))
            {
                continue;
            }

            visibleCount++;
            CompileEntityTree(
                entity,
                CadAffineTransform3D.Identity,
                false,
                entity.Handle,
                inheritedLayer: null,
                byBlockStyle: null,
                depth: 0);
        }

        return new CadDocumentSnapshot(
            generation,
            documentBounds,
            new CadSnapshotStatistics(
                document.Entities.Count,
                visibleCount,
                expandedCount,
                unsupportedCount,
                invalidCount),
            layers.ToArray(),
            styles.ToArray(),
            entities.ToArray(),
            lines.ToArray(),
            circles.ToArray(),
            arcs.ToArray(),
            ellipses.ToArray(),
            faces.ToArray(),
            splines.ToArray(),
            polylines.ToArray(),
            polylines3D.ToArray(),
            texts.ToArray(),
            textGlyphRuns.ToArray(),
            textGlyphIndices.ToArray(),
            textGlyphPositions.ToArray(),
            textFonts.ToArray(),
            polylineVertices.ToArray(),
            polyline3DPoints.ToArray(),
            splineControlPoints.ToArray(),
            splineKnots.ToArray(),
            splineWeights.ToArray(),
            diagnostics.ToArray());

        void CompileEntityTree(
            Entity entity,
            CadAffineTransform3D transform,
            bool hasTransform,
            ulong rootHandle,
            Layer? inheritedLayer,
            CadResolvedStyle? byBlockStyle,
            int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Layer effectiveLayer = inheritedLayer is not null && IsLayerZero(entity.Layer)
                ? inheritedLayer
                : entity.Layer;
            if (entity.IsInvisible || !effectiveLayer.IsOn ||
                (!options.IncludeNonPlottableLayers && !effectiveLayer.PlotFlag))
            {
                return;
            }

            try
            {
                if (expandedCount >= options.MaxExpandedEntities)
                {
                    throw new CadSnapshotExpansionLimitException(
                        $"Expanded entity count exceeds the configured limit of {options.MaxExpandedEntities}.");
                }

                expandedCount++;

                CadResolvedStyle resolvedStyle = ResolveStyle(
                    entity,
                    effectiveLayer,
                    byBlockStyle,
                    options);
                if (entity is Insert insert)
                {
                    CompileInsert(
                        insert,
                        transform,
                        rootHandle,
                        effectiveLayer,
                        resolvedStyle,
                        depth);
                    return;
                }

                int layerIndex = InternLayer(effectiveLayer, layers, layerIndices);
                int styleIndex = InternStyle(resolvedStyle, styles, styleIndices);
                CadEntityHeader? header = entity switch
                {
                    Line line => CompileLine(line, rootHandle, transform, hasTransform, layerIndex, styleIndex, lines),
                    Arc arc => CompileArc(arc, rootHandle, transform, hasTransform, layerIndex, styleIndex, arcs),
                    Circle circle => CompileCircle(circle, rootHandle, transform, hasTransform, layerIndex, styleIndex, circles),
                    Ellipse ellipse => CompileEllipse(ellipse, rootHandle, transform, hasTransform, layerIndex, styleIndex, ellipses),
                    Solid solid => CompileSolid(solid, rootHandle, transform, hasTransform, layerIndex, styleIndex, faces),
                    Face3D face => CompileFace3D(face, rootHandle, transform, hasTransform, layerIndex, styleIndex, faces),
                    Spline spline => CompileSpline(
                        spline,
                        rootHandle,
                        transform,
                        hasTransform,
                        layerIndex,
                        styleIndex,
                        splines,
                        splineControlPoints,
                        splineKnots,
                        splineWeights),
                    LwPolyline polyline => CompilePolyline(
                        polyline,
                        rootHandle,
                        transform,
                        hasTransform,
                        layerIndex,
                        styleIndex,
                        polylines,
                        polylineVertices),
                    Polyline2D polyline => CompilePolyline2D(
                        polyline,
                        rootHandle,
                        transform,
                        hasTransform,
                        layerIndex,
                        styleIndex,
                        polylines,
                        polylineVertices),
                    Polyline3D polyline => CompilePolyline3D(
                        polyline,
                        rootHandle,
                        transform,
                        hasTransform,
                        layerIndex,
                        styleIndex,
                        polylines3D,
                        polyline3DPoints),
                    TextEntity text => CompileText(
                        text,
                        rootHandle,
                        transform,
                        hasTransform,
                        layerIndex,
                        styleIndex,
                        options,
                        diagnostics,
                        texts,
                        textGlyphRuns,
                        textGlyphIndices,
                        textGlyphPositions,
                        textFonts,
                        textFontIndices),
                    MText => throw new CadUnsupportedEntityException(
                        "MTEXT requires inline-format, paragraph, column, background, and attachment lowering."),
                    _ => null,
                };

                if (header is CadEntityHeader value)
                {
                    entities.Add(value);
                    documentBounds = documentBounds.Union(value.Bounds);
                    return;
                }

                unsupportedCount++;
                AddDiagnostic(
                    diagnostics,
                    options.DiagnosticLimit,
                    new CadDiagnostic(
                        CadDiagnosticSeverity.Information,
                        "CADSNAP001",
                        $"Entity path {FormatEntityPath(rootHandle, entity.Handle)} ({entity.ObjectName}) is not yet represented in the analytic snapshot."));
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
                        $"Entity path {FormatEntityPath(rootHandle, entity.Handle)} ({entity.ObjectName}) is not yet supported: {exception.Message}"));
            }
            catch (Exception exception) when (
                (exception is ArgumentException or ArithmeticException or InvalidOperationException) &&
                exception is not CadSnapshotExpansionLimitException)
            {
                invalidCount++;
                AddDiagnostic(
                    diagnostics,
                    options.DiagnosticLimit,
                    new CadDiagnostic(
                        CadDiagnosticSeverity.Warning,
                        "CADSNAP002",
                        $"Entity path {FormatEntityPath(rootHandle, entity.Handle)} ({entity.ObjectName}) was rejected: {exception.Message}"));
            }
        }

        void CompileInsert(
            Insert insert,
            CadAffineTransform3D parentTransform,
            ulong rootHandle,
            Layer effectiveLayer,
            CadResolvedStyle resolvedStyle,
            int depth)
        {
            if (depth >= options.MaxBlockNestingDepth)
            {
                throw new CadUnsupportedEntityException(
                    $"Block nesting exceeds the configured depth of {options.MaxBlockNestingDepth}.");
            }

            BlockRecord block = insert.Block ?? throw new ArgumentException(
                "INSERT has no block definition.");
            if ((block.Flags & (BlockTypeFlags.XRef | BlockTypeFlags.XRefOverlay | BlockTypeFlags.XRefDependent)) != 0 ||
                block.BlockEntity.IsUnloaded)
            {
                throw new CadUnsupportedEntityException(
                    "External-reference blocks require an explicit resolved XRef snapshot.");
            }

            if (block.EvaluationGraph is not null)
            {
                throw new CadUnsupportedEntityException(
                    "Dynamic blocks require evaluation-state lowering before expansion.");
            }

            int columnCount = insert.ColumnCount;
            int rowCount = insert.RowCount;
            if (columnCount < 1 || rowCount < 1)
            {
                throw new ArgumentException(
                    "INSERT row and column counts must be positive.");
            }

            if (!double.IsFinite(insert.ColumnSpacing) ||
                !double.IsFinite(insert.RowSpacing))
            {
                throw new ArgumentException(
                    "INSERT row and column spacing must be finite.");
            }

            long instanceCount = checked((long)columnCount * rowCount);
            if (instanceCount > options.MaxBlockArrayInstances)
            {
                throw new CadUnsupportedEntityException(
                    $"MINSERT instance count {instanceCount} exceeds the configured limit of {options.MaxBlockArrayInstances}.");
            }

            if (!activeBlocks.Add(block))
            {
                throw new CadUnsupportedEntityException(
                    $"Recursive block cycle detected at '{block.Name}'.");
            }

            try
            {
                CadAffineTransform3D localTransform = CreateInsertTransform(insert);
                CadAffineTransform3D baseInstanceTransform = parentTransform.Compose(localTransform);
                CadPoint3D columnStep = parentTransform.TransformVector(
                    localTransform.XAxis / insert.XScale) * insert.ColumnSpacing;
                CadPoint3D rowStep = parentTransform.TransformVector(
                    localTransform.YAxis / insert.YScale) * insert.RowSpacing;
                EnsureFinite(baseInstanceTransform);
                EnsureFinite(columnStep);
                EnsureFinite(rowStep);
                for (int row = 0; row < rowCount; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CadPoint3D rowTranslation = baseInstanceTransform.Translation + (rowStep * row);
                    for (int column = 0; column < columnCount; column++)
                    {
                        if ((column & 255) == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        CadPoint3D translation = rowTranslation + (columnStep * column);
                        EnsureFinite(translation);
                        var instanceTransform = new CadAffineTransform3D(
                            baseInstanceTransform.XAxis,
                            baseInstanceTransform.YAxis,
                            baseInstanceTransform.ZAxis,
                            translation);
                        foreach (Entity child in block.Entities)
                        {
                            CompileEntityTree(
                                child,
                                instanceTransform,
                                true,
                                rootHandle,
                                effectiveLayer,
                                resolvedStyle,
                                depth + 1);
                        }
                    }
                }

                if (insert.Attributes.Count > 0)
                {
                    unsupportedCount = checked(unsupportedCount + insert.Attributes.Count);
                    AddDiagnostic(
                        diagnostics,
                        options.DiagnosticLimit,
                        new CadDiagnostic(
                            CadDiagnosticSeverity.Information,
                            "CADSNAP004",
                            $"INSERT path {FormatEntityPath(rootHandle, insert.Handle)} contains {insert.Attributes.Count} attribute values; text lowering is not yet enabled."));
                }
            }
            finally
            {
                activeBlocks.Remove(block);
            }
        }
    }

    private static CadEntityHeader CompileLine(
        Line line,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        List<CadLinePrimitive> destination)
    {
        CadPoint3D start = TransformPoint(transform, hasTransform, ToPoint(line.StartPoint));
        CadPoint3D end = TransformPoint(transform, hasTransform, ToPoint(line.EndPoint));
        EnsureFinite(start);
        EnsureFinite(end);
        int primitiveIndex = destination.Count;
        destination.Add(new CadLinePrimitive(start, end));
        return new CadEntityHeader(
            handle,
            CadEntityKind.Line,
            layerIndex,
            styleIndex,
            primitiveIndex,
            CadBounds3D.FromPoint(start).Include(end));
    }

    private static CadEntityHeader CompileCircle(
        Circle circle,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        List<CadCirclePrimitive> destination)
    {
        ValidateRadius(circle.Radius);
        CadCoordinateSystem localBasis = CadCoordinateSystem.FromNormal(ToPoint(circle.Normal));
        CadPoint3D center = TransformPoint(
            transform,
            hasTransform,
            localBasis.Transform(ToPoint(circle.Center)));
        CadCoordinateSystem basis = hasTransform
            ? TransformBasis(transform, localBasis)
            : localBasis;
        EnsureFinite(center);
        int primitiveIndex = destination.Count;
        destination.Add(new CadCirclePrimitive(center, basis, circle.Radius));
        return new CadEntityHeader(
            handle,
            CadEntityKind.Circle,
            layerIndex,
            styleIndex,
            primitiveIndex,
            CadBounds3D.Circle(center, basis, circle.Radius));
    }

    private static CadEntityHeader CompileArc(
        Arc arc,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        List<CadArcPrimitive> destination)
    {
        ValidateRadius(arc.Radius);
        if (!double.IsFinite(arc.StartAngle) || !double.IsFinite(arc.EndAngle))
        {
            throw new ArgumentException("Arc angles must be finite.");
        }

        CadCoordinateSystem localBasis = CadCoordinateSystem.FromNormal(ToPoint(arc.Normal));
        CadPoint3D center = TransformPoint(
            transform,
            hasTransform,
            localBasis.Transform(ToPoint(arc.Center)));
        CadCoordinateSystem basis = hasTransform
            ? TransformBasis(transform, localBasis)
            : localBasis;
        EnsureFinite(center);
        double start = NormalizeAngle(arc.StartAngle);
        double sweep = NormalizePositiveSweep(arc.StartAngle, arc.EndAngle);
        int primitiveIndex = destination.Count;
        destination.Add(new CadArcPrimitive(center, basis, arc.Radius, start, sweep));
        return new CadEntityHeader(
            handle,
            CadEntityKind.Arc,
            layerIndex,
            styleIndex,
            primitiveIndex,
            CadBounds3D.Arc(center, basis, arc.Radius, start, sweep));
    }

    private static CadEntityHeader CompileEllipse(
        Ellipse ellipse,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        List<CadEllipsePrimitive> destination)
    {
        if (ellipse.Thickness != 0.0)
        {
            throw new CadUnsupportedEntityException(
                "Extruded ellipses require 3D side-surface lowering.");
        }

        CadPoint3D center = ToPoint(ellipse.Center);
        CadPoint3D majorAxis = ToPoint(ellipse.MajorAxisEndPoint);
        CadPoint3D normal = ToPoint(ellipse.Normal).Normalize();
        EnsureFinite(center);
        EnsureFinite(majorAxis);
        double majorLength = majorAxis.Length;
        if (!double.IsFinite(majorLength) || majorLength <= 0.0 ||
            !double.IsFinite(ellipse.RadiusRatio) ||
            ellipse.RadiusRatio <= 0.0 || ellipse.RadiusRatio > 1.0)
        {
            throw new ArgumentException(
                "Ellipse axes and radius ratio must be finite and positive, with ratio at most one.");
        }

        double perpendicularError = Math.Abs(CadPoint3D.Dot(normal, majorAxis)) / majorLength;
        if (perpendicularError > 1e-10)
        {
            throw new ArgumentException("Ellipse normal and major axis must be perpendicular.");
        }

        if (!double.IsFinite(ellipse.StartParameter) || !double.IsFinite(ellipse.EndParameter))
        {
            throw new ArgumentException("Ellipse parameters must be finite.");
        }

        CadPoint3D minorAxis = CadPoint3D.Cross(normal, majorAxis).Normalize() *
            (majorLength * ellipse.RadiusRatio);
        if (hasTransform)
        {
            center = transform.TransformPoint(center);
            majorAxis = transform.TransformVector(majorAxis);
            minorAxis = transform.TransformVector(minorAxis);
        }
        EnsureFinite(center);
        EnsureFinite(majorAxis);
        EnsureFinite(minorAxis);
        double start = NormalizeAngle(ellipse.StartParameter);
        double sweep = NormalizePositiveSweep(ellipse.StartParameter, ellipse.EndParameter);
        int primitiveIndex = destination.Count;
        destination.Add(new CadEllipsePrimitive(center, majorAxis, minorAxis, start, sweep));
        return new CadEntityHeader(
            handle,
            CadEntityKind.Ellipse,
            layerIndex,
            styleIndex,
            primitiveIndex,
            CadBounds3D.EllipseArc(center, majorAxis, minorAxis, start, sweep));
    }

    private static CadEntityHeader CompileSolid(
        Solid solid,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        List<CadFacePrimitive> destination)
    {
        if (solid.Thickness != 0.0)
        {
            throw new CadUnsupportedEntityException(
                "Extruded solids require 3D side-surface lowering.");
        }

        CadCoordinateSystem basis = CadCoordinateSystem.FromNormal(ToPoint(solid.Normal));
        CadPoint3D first = TransformPoint(transform, hasTransform, basis.Transform(ToPoint(solid.FirstCorner)));
        CadPoint3D second = TransformPoint(transform, hasTransform, basis.Transform(ToPoint(solid.SecondCorner)));
        CadPoint3D third = TransformPoint(transform, hasTransform, basis.Transform(ToPoint(solid.ThirdCorner)));
        CadPoint3D fourth = TransformPoint(transform, hasTransform, basis.Transform(ToPoint(solid.FourthCorner)));
        return AddFace(
            handle,
            CadEntityKind.Solid,
            layerIndex,
            styleIndex,
            destination,
            first,
            second,
            third,
            fourth,
            0);
    }

    private static CadEntityHeader CompileFace3D(
        Face3D face,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        List<CadFacePrimitive> destination)
    {
        int invisibleEdges = (int)face.Flags;
        if ((invisibleEdges & ~0xF) != 0)
        {
            throw new ArgumentException("A 3DFACE contains unsupported invisible-edge flags.");
        }

        return AddFace(
            handle,
            CadEntityKind.Face3D,
            layerIndex,
            styleIndex,
            destination,
            TransformPoint(transform, hasTransform, ToPoint(face.FirstCorner)),
            TransformPoint(transform, hasTransform, ToPoint(face.SecondCorner)),
            TransformPoint(transform, hasTransform, ToPoint(face.ThirdCorner)),
            TransformPoint(transform, hasTransform, ToPoint(face.FourthCorner)),
            (byte)invisibleEdges);
    }

    private static CadEntityHeader AddFace(
        ulong handle,
        CadEntityKind kind,
        int layerIndex,
        int styleIndex,
        List<CadFacePrimitive> destination,
        CadPoint3D first,
        CadPoint3D second,
        CadPoint3D third,
        CadPoint3D fourth,
        byte invisibleEdgeMask)
    {
        EnsureFinite(first);
        EnsureFinite(second);
        EnsureFinite(third);
        EnsureFinite(fourth);
        int primitiveIndex = destination.Count;
        destination.Add(new CadFacePrimitive(
            first,
            second,
            third,
            fourth,
            invisibleEdgeMask));
        return new CadEntityHeader(
            handle,
            kind,
            layerIndex,
            styleIndex,
            primitiveIndex,
            CadBounds3D.FromPoint(first).Include(second).Include(third).Include(fourth));
    }

    private static CadEntityHeader CompileSpline(
        Spline spline,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
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
        var transformedControlPoints = new CadPoint3D[spline.ControlPoints.Count];
        int transformedIndex = 0;
        foreach (XYZ value in spline.ControlPoints)
        {
            CadPoint3D point = TransformPoint(transform, hasTransform, ToPoint(value));
            EnsureFinite(point);
            transformedControlPoints[transformedIndex++] = point;
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
        controlPoints.AddRange(transformedControlPoints);
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
            handle,
            CadEntityKind.Spline,
            layerIndex,
            styleIndex,
            primitiveIndex,
            bounds);
    }

    private static CadEntityHeader CompilePolyline(
        LwPolyline polyline,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
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
        CadPoint3D worldOrigin = TransformPoint(
            transform,
            hasTransform,
            basis.Transform(new CadPoint3D(localOriginX, localOriginY, polyline.Elevation)));
        if (hasTransform)
        {
            basis = TransformBasis(transform, basis);
        }
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

        return AddPlanarPolyline(
            handle,
            CadEntityKind.LightweightPolyline,
            layerIndex,
            styleIndex,
            worldOrigin,
            basis,
            polyline.IsClosed,
            normalizedVertices,
            destination,
            vertices);
    }

    private static CadEntityHeader CompilePolyline2D(
        Polyline2D polyline,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        List<CadPolylinePrimitive> destination,
        List<CadPolylineVertex> vertices)
    {
        if (polyline.Vertices.Count < 2)
        {
            throw new ArgumentException("A 2D polyline must contain at least two vertices.");
        }

        if (polyline.StartWidth != 0.0 || polyline.EndWidth != 0.0 ||
            polyline.Vertices.Any(vertex => vertex.StartWidth != 0.0 || vertex.EndWidth != 0.0))
        {
            throw new CadUnsupportedEntityException(
                "Wide 2D polylines require filled-outline lowering and cannot be treated as cosmetic strokes.");
        }

        if (polyline.Thickness != 0.0)
        {
            throw new CadUnsupportedEntityException(
                "Extruded 2D polylines require 3D side-surface lowering.");
        }

        if (polyline.SmoothSurface != SmoothSurfaceType.NoSmooth ||
            (polyline.Flags & (PolylineFlags.CurveFit | PolylineFlags.SplineFit)) != 0)
        {
            throw new CadUnsupportedEntityException(
                "Curve-fit and spline-fit legacy polylines require fitted-vertex semantic lowering.");
        }

        if (!double.IsFinite(polyline.Elevation))
        {
            throw new ArgumentException("Polyline elevation must be finite.");
        }

        CadCoordinateSystem basis = CadCoordinateSystem.FromNormal(ToPoint(polyline.Normal));
        Vertex2D first = polyline.Vertices[0];
        double localOriginX = first.Location.X;
        double localOriginY = first.Location.Y;
        CadPoint3D worldOrigin = TransformPoint(
            transform,
            hasTransform,
            basis.Transform(new CadPoint3D(localOriginX, localOriginY, polyline.Elevation)));
        if (hasTransform)
        {
            basis = TransformBasis(transform, basis);
        }
        EnsureFinite(worldOrigin);

        var normalizedVertices = new CadPolylineVertex[polyline.Vertices.Count];
        for (int i = 0; i < polyline.Vertices.Count; i++)
        {
            Vertex2D vertex = polyline.Vertices[i];
            double x = vertex.Location.X - localOriginX;
            double y = vertex.Location.Y - localOriginY;
            if (!double.IsFinite(x) || !double.IsFinite(y) ||
                !double.IsFinite(vertex.Location.Z) || !double.IsFinite(vertex.Bulge))
            {
                throw new ArgumentException("Polyline locations and bulges must be finite.");
            }

            normalizedVertices[i] = new CadPolylineVertex(x, y, vertex.Bulge);
        }

        return AddPlanarPolyline(
            handle,
            CadEntityKind.Polyline2D,
            layerIndex,
            styleIndex,
            worldOrigin,
            basis,
            polyline.IsClosed,
            normalizedVertices,
            destination,
            vertices);
    }

    private static CadEntityHeader AddPlanarPolyline(
        ulong handle,
        CadEntityKind kind,
        int layerIndex,
        int styleIndex,
        CadPoint3D worldOrigin,
        CadCoordinateSystem basis,
        bool isClosed,
        CadPolylineVertex[] normalizedVertices,
        List<CadPolylinePrimitive> destination,
        List<CadPolylineVertex> vertices)
    {
        ReadOnlySpan<CadPolylineVertex> added = normalizedVertices;
        CadBounds3D bounds = CadBounds3D.Empty;
        int segmentCount = isClosed ? added.Length : added.Length - 1;
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
            normalizedVertices.Length,
            isClosed));
        return new CadEntityHeader(
            handle,
            kind,
            layerIndex,
            styleIndex,
            primitiveIndex,
            bounds);
    }

    private static CadEntityHeader CompilePolyline3D(
        Polyline3D polyline,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        List<CadPolyline3DPrimitive> destination,
        List<CadPoint3D> points)
    {
        if (polyline.Vertices.Count < 2)
        {
            throw new ArgumentException("A 3D polyline must contain at least two vertices.");
        }

        if (polyline.StartWidth != 0.0 || polyline.EndWidth != 0.0 ||
            polyline.Thickness != 0.0 ||
            polyline.Vertices.Any(vertex =>
                vertex.StartWidth != 0.0 || vertex.EndWidth != 0.0 || vertex.Bulge != 0.0))
        {
            throw new CadUnsupportedEntityException(
                "Width, thickness, and bulge are not valid retained centerline semantics for a 3D polyline.");
        }

        if (polyline.SmoothSurface != SmoothSurfaceType.NoSmooth ||
            (polyline.Flags & (PolylineFlags.CurveFit | PolylineFlags.SplineFit)) != 0)
        {
            throw new CadUnsupportedEntityException(
                "Curve-fit and spline-fit 3D polylines require fitted-vertex semantic lowering.");
        }

        var normalizedPoints = new CadPoint3D[polyline.Vertices.Count];
        CadBounds3D bounds = CadBounds3D.Empty;
        for (int i = 0; i < polyline.Vertices.Count; i++)
        {
            Vertex3D vertex = polyline.Vertices[i];
            CadPoint3D point = TransformPoint(transform, hasTransform, ToPoint(vertex.Location));
            EnsureFinite(point);
            normalizedPoints[i] = point;
            bounds = bounds.Include(point);
        }

        int pointOffset = points.Count;
        points.AddRange(normalizedPoints);
        int primitiveIndex = destination.Count;
        destination.Add(new CadPolyline3DPrimitive(
            pointOffset,
            polyline.Vertices.Count,
            polyline.IsClosed));
        return new CadEntityHeader(
            handle,
            CadEntityKind.Polyline3D,
            layerIndex,
            styleIndex,
            primitiveIndex,
            bounds);
    }

    private static CadEntityHeader CompileText(
        TextEntity text,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        CadSnapshotOptions options,
        List<CadDiagnostic> diagnostics,
        List<CadTextPrimitive> destination,
        List<CadTextGlyphRun> runs,
        List<ushort> glyphIndices,
        List<Vector2> glyphPositions,
        List<TtfFont> fonts,
        Dictionary<TtfFont, int> fontIndices)
    {
        if (text.Thickness != 0.0)
        {
            throw new CadUnsupportedEntityException(
                "Extruded TEXT requires 3D side-surface lowering.");
        }

        if (string.IsNullOrEmpty(text.Value) ||
            text.Value.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new CadUnsupportedEntityException(
                "TEXT must contain one non-empty logical line.");
        }

        if (text.Value.Length > options.MaxTextCodeUnitsPerEntity)
        {
            throw new CadSnapshotExpansionLimitException(
                $"TEXT path {FormatEntityPath(handle, text.Handle)} exceeds the configured per-entity limit of {options.MaxTextCodeUnitsPerEntity} UTF-16 code units.");
        }

        if (text.HorizontalAlignment is TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Fit)
        {
            throw new CadUnsupportedEntityException(
                "Aligned and fit TEXT require two-point advance scaling.");
        }

        TextStyle cadStyle = text.Style;
        if (cadStyle.IsShapeFile ||
            cadStyle.Filename.EndsWith(".shx", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(cadStyle.BigFontFilename))
        {
            throw new CadUnsupportedEntityException(
                "SHX and Big Font TEXT require the bounded ProGPU SHX font source.");
        }

        if (cadStyle.Flags.HasFlag(StyleFlags.VerticalText))
        {
            throw new CadUnsupportedEntityException(
                "Vertical TrueType STYLE requires vertical shaping and glyph-orientation lowering.");
        }

        string content = DecodeTextContent(text.Value);

        ICadTextFontResolver resolver = options.TextFontResolver ??
            throw new CadUnsupportedEntityException(
                "TrueType TEXT requires a host text-font resolver.");
        bool isBold = cadStyle.TrueType.HasFlag(FontFlags.Bold);
        bool isItalic = cadStyle.TrueType.HasFlag(FontFlags.Italic);
        CadTextFontResolution fontResolution = resolver.Resolve(new CadTextFontRequest(
            cadStyle.Name,
            cadStyle.Filename,
            cadStyle.BigFontFilename,
            isBold,
            isItalic));
        TtfFont font = fontResolution.Font ?? throw new CadUnsupportedEntityException(
                $"Text style '{cadStyle.Name}' could not resolve a TrueType font.");
        double height = text.Height;
        double widthFactor = text.WidthFactor;
        double oblique = text.ObliqueAngle;
        if (!double.IsFinite(height) || height <= 0.0 ||
            !double.IsFinite(widthFactor) || widthFactor <= 0.0 ||
            !double.IsFinite(text.Rotation) ||
            !double.IsFinite(oblique) || Math.Abs(oblique) >= Math.PI * 0.5)
        {
            throw new ArgumentException(
                "TEXT height, width, rotation, and oblique angle must define a finite non-degenerate transform.");
        }

        var layout = new TextLayout(content, font, 1.0f, float.PositiveInfinity);
        if (layout.Glyphs.Count == 0 || font.UnitsPerEm == 0)
        {
            throw new CadUnsupportedEntityException(
                "TEXT shaping produced no drawable glyph run.");
        }

        if (layout.Glyphs.Count > options.MaxTextGlyphs - glyphIndices.Count)
        {
            throw new CadSnapshotExpansionLimitException(
                $"Retained TEXT glyph count exceeds the configured document limit of {options.MaxTextGlyphs}.");
        }

        double ascent = (double)font.Ascender / font.UnitsPerEm;
        double descent = (double)font.Descender / font.UnitsPerEm;
        double width = layout.ContentSize.X;
        double horizontalOffset = text.HorizontalAlignment switch
        {
            TextHorizontalAlignment.Center or TextHorizontalAlignment.Middle => -width * 0.5,
            TextHorizontalAlignment.Right => -width,
            _ => 0.0,
        };
        TextVerticalAlignmentType verticalAlignment = text.HorizontalAlignment == TextHorizontalAlignment.Middle
            ? TextVerticalAlignmentType.Middle
            : text.VerticalAlignment;
        double verticalOffset = verticalAlignment switch
        {
            TextVerticalAlignmentType.Top => ascent,
            TextVerticalAlignmentType.Middle => (ascent + descent) * 0.5,
            TextVerticalAlignmentType.Bottom => descent,
            _ => 0.0,
        };

        bool usesAlignmentPoint = text.HorizontalAlignment != TextHorizontalAlignment.Left ||
            text.VerticalAlignment != TextVerticalAlignmentType.Baseline;
        CadCoordinateSystem basis = CadCoordinateSystem.FromNormal(ToPoint(text.Normal));
        CadPoint3D anchor = basis.Transform(ToPoint(
            usesAlignmentPoint ? text.AlignmentPoint : text.InsertPoint));
        double cosine = Math.Cos(text.Rotation);
        double sine = Math.Sin(text.Rotation);
        CadPoint3D horizontal = (basis.XAxis * cosine) + (basis.YAxis * sine);
        CadPoint3D vertical = (basis.XAxis * -sine) + (basis.YAxis * cosine);
        TextMirrorFlag mirror = text.Mirror;
        double mirrorX = mirror.HasFlag(TextMirrorFlag.Backward) ? -1.0 : 1.0;
        double mirrorY = mirror.HasFlag(TextMirrorFlag.UpsideDown) ? -1.0 : 1.0;
        CadPoint3D xAxis = horizontal * (height * widthFactor * mirrorX);
        CadPoint3D yAxis =
            (horizontal * (-height * Math.Tan(oblique) * mirrorY)) +
            (vertical * (-height * mirrorY));
        if (hasTransform)
        {
            anchor = transform.TransformPoint(anchor);
            xAxis = transform.TransformVector(xAxis);
            yAxis = transform.TransformVector(yAxis);
        }
        EnsureFinite(anchor);
        EnsureFinite(xAxis);
        EnsureFinite(yAxis);

        var compiledGlyphIndices = new ushort[layout.Glyphs.Count];
        var compiledGlyphPositions = new Vector2[layout.Glyphs.Count];
        var compiledRuns = new List<(int Offset, int Count, TtfFont Font)>();
        TtfFont? runFont = null;
        int currentRunOffset = 0;
        double minimumX = horizontalOffset;
        double maximumX = horizontalOffset + width;
        double minimumY = verticalOffset - ascent;
        double maximumY = verticalOffset - descent;
        for (int i = 0; i < layout.Glyphs.Count; i++)
        {
            TextRunGlyph glyph = layout.Glyphs[i];
            TtfFont glyphFont = glyph.Font ?? font;
            if (runFont is not null && !ReferenceEquals(runFont, glyphFont))
            {
                compiledRuns.Add((
                    currentRunOffset,
                    i - currentRunOffset,
                    runFont));
                currentRunOffset = i;
            }
            runFont = glyphFont;

            float x = checked((float)(glyph.Position.X + horizontalOffset));
            float y = checked((float)(glyph.Position.Y - ascent + verticalOffset));
            if (!float.IsFinite(x) || !float.IsFinite(y))
            {
                throw new ArithmeticException("TEXT glyph positions exceed the retained numeric range.");
            }
            compiledGlyphIndices[i] = glyph.GlyphIndex;
            compiledGlyphPositions[i] = new Vector2(x, y);

            if (glyphFont.UnitsPerEm != 0 && glyphFont.TryGetGlyphBounds(
                glyph.GlyphIndex,
                out short xMin,
                out short yMin,
                out short xMax,
                out short yMax))
            {
                double scale = 1.0 / glyphFont.UnitsPerEm;
                minimumX = Math.Min(minimumX, x + (xMin * scale));
                maximumX = Math.Max(maximumX, x + (xMax * scale));
                minimumY = Math.Min(minimumY, y - (yMax * scale));
                maximumY = Math.Max(maximumY, y - (yMin * scale));
            }
        }

        if (runFont is not null)
        {
            compiledRuns.Add((
                currentRunOffset,
                compiledGlyphIndices.Length - currentRunOffset,
                runFont));
        }

        CadBounds3D bounds = CadBounds3D.FromPoint(
            TransformTextPoint(anchor, xAxis, yAxis, minimumX, minimumY));
        bounds = bounds
            .Include(TransformTextPoint(anchor, xAxis, yAxis, maximumX, minimumY))
            .Include(TransformTextPoint(anchor, xAxis, yAxis, minimumX, maximumY))
            .Include(TransformTextPoint(anchor, xAxis, yAxis, maximumX, maximumY));
        int glyphOffset = glyphIndices.Count;
        int runOffset = runs.Count;
        glyphIndices.AddRange(compiledGlyphIndices);
        glyphPositions.AddRange(compiledGlyphPositions);
        for (int i = 0; i < compiledRuns.Count; i++)
        {
            (int offset, int count, TtfFont runTypeFace) = compiledRuns[i];
            runs.Add(new CadTextGlyphRun(
                glyphOffset + offset,
                count,
                InternTextFont(runTypeFace, fonts, fontIndices)));
        }
        int primitiveIndex = destination.Count;
        destination.Add(new CadTextPrimitive(
            anchor,
            xAxis,
            yAxis,
            glyphOffset,
            compiledGlyphIndices.Length,
            runOffset,
            runs.Count - runOffset));
        if (fontResolution.IsSubstitution)
        {
            AddDiagnostic(
                diagnostics,
                options.DiagnosticLimit,
                new CadDiagnostic(
                    CadDiagnosticSeverity.Warning,
                    "CADSNAP005",
                    $"TEXT path {FormatEntityPath(handle, text.Handle)} substitutes '{cadStyle.Filename}' with '{font.FamilyName}'."));
        }

        return new CadEntityHeader(
            handle,
            CadEntityKind.Text,
            layerIndex,
            styleIndex,
            primitiveIndex,
            bounds);
    }

    private static int InternTextFont(
        TtfFont font,
        List<TtfFont> fonts,
        Dictionary<TtfFont, int> indices)
    {
        if (indices.TryGetValue(font, out int index))
        {
            return index;
        }

        index = fonts.Count;
        fonts.Add(font);
        indices.Add(font, index);
        return index;
    }

    private static CadPoint3D TransformTextPoint(
        CadPoint3D origin,
        CadPoint3D xAxis,
        CadPoint3D yAxis,
        double x,
        double y) => origin + (xAxis * x) + (yAxis * y);

    private static string DecodeTextContent(string source)
    {
        bool requiresDecoding = source.Contains("%%", StringComparison.Ordinal) ||
            source.Contains("\\U+", StringComparison.OrdinalIgnoreCase);
        if (!requiresDecoding)
        {
            EnsureValidUtf16(source);
            return source;
        }

        var decoded = new char[source.Length];
        int written = 0;
        for (int i = 0; i < source.Length; i++)
        {
            char value = source[i];
            if (value == '\\' && i + 2 < source.Length &&
                (source[i + 1] is 'U' or 'u') && source[i + 2] == '+')
            {
                if (i + 6 >= source.Length)
                {
                    throw new CadUnsupportedEntityException(
                        "TEXT contains a truncated DXF Unicode escape.");
                }

                int scalar = 0;
                for (int digit = 0; digit < 4; digit++)
                {
                    int hex = HexValue(source[i + 3 + digit]);
                    if (hex < 0)
                    {
                        throw new CadUnsupportedEntityException(
                            "TEXT contains an invalid DXF Unicode escape.");
                    }

                    scalar = (scalar << 4) | hex;
                }

                decoded[written++] = (char)scalar;
                i += 6;
                continue;
            }

            if (value == '%' && i + 1 < source.Length && source[i + 1] == '%')
            {
                if (i + 2 >= source.Length)
                {
                    throw new CadUnsupportedEntityException(
                        "TEXT contains a truncated AutoCAD control code.");
                }

                char code = char.ToLowerInvariant(source[i + 2]);
                decoded[written++] = code switch
                {
                    'd' => '\u00B0',
                    'p' => '\u00B1',
                    'c' => '\u2205',
                    '%' => '%',
                    'o' or 'u' or 'k' => throw new CadUnsupportedEntityException(
                        "TEXT overline, underline, and strike-through control codes require retained decoration runs."),
                    >= '0' and <= '9' => throw new CadUnsupportedEntityException(
                        "Numeric AutoCAD TEXT control codes require font-specific character mapping."),
                    _ => throw new CadUnsupportedEntityException(
                        $"TEXT contains unsupported AutoCAD control code '%%{source[i + 2]}'."),
                };
                i += 2;
                continue;
            }

            decoded[written++] = value;
        }

        EnsureValidUtf16(decoded.AsSpan(0, written));
        return new string(decoded, 0, written);
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1,
    };

    private static void EnsureValidUtf16(ReadOnlySpan<char> value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    throw new CadUnsupportedEntityException(
                        "TEXT contains an unpaired UTF-16 surrogate.");
                }

                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                throw new CadUnsupportedEntityException(
                    "TEXT contains an unpaired UTF-16 surrogate.");
            }
        }
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
        Layer layer,
        List<CadLayerSnapshot> layers,
        Dictionary<string, int> indices)
    {
        string name = layer.Name;
        if (indices.TryGetValue(name, out int index))
        {
            return index;
        }

        index = layers.Count;
        indices.Add(name, index);
        layers.Add(new CadLayerSnapshot(name, layer.IsOn, layer.PlotFlag));
        return index;
    }

    private static int InternStyle(
        CadResolvedStyle resolved,
        List<CadStrokeStyle> styles,
        Dictionary<CadStrokeStyle, int> indices)
    {
        ACadSharp.Color color = resolved.Color;
        LineWeightType lineWeight = resolved.LineWeight;
        double millimeters = lineWeight is LineWeightType.Default or LineWeightType.ByLayer or LineWeightType.ByBlock
            ? resolved.DefaultLineWeightMillimeters
            : lineWeight.GetLineWeightValue();
        short transparency = resolved.Transparency;
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
            resolved.LineTypeName,
            resolved.LineTypeScale);

        if (indices.TryGetValue(style, out int index))
        {
            return index;
        }

        index = styles.Count;
        indices.Add(style, index);
        styles.Add(style);
        return index;
    }

    private static CadResolvedStyle ResolveStyle(
        Entity entity,
        Layer effectiveLayer,
        CadResolvedStyle? byBlock,
        CadSnapshotOptions options)
    {
        ACadSharp.Color color = entity.Color.IsByLayer
            ? effectiveLayer.Color
            : entity.Color.IsByBlock
                ? byBlock?.Color ?? ACadSharp.Color.Default
                : entity.Color;
        LineWeightType lineWeight = entity.LineWeight switch
        {
            LineWeightType.ByLayer => effectiveLayer.LineWeight,
            LineWeightType.ByBlock => byBlock?.LineWeight ?? LineWeightType.Default,
            _ => entity.LineWeight,
        };
        string lineTypeName = entity.LineType.Name.Equals(
            LineType.ByLayerName,
            StringComparison.OrdinalIgnoreCase)
            ? effectiveLayer.LineType.Name
            : entity.LineType.Name.Equals(
                LineType.ByBlockName,
                StringComparison.OrdinalIgnoreCase)
                ? byBlock?.LineTypeName ?? LineType.Continuous.Name
                : entity.LineType.Name;
        short transparency = entity.Transparency.IsByLayer
            ? (short)0
            : entity.Transparency.IsByBlock
                ? byBlock?.Transparency ?? (short)0
                : entity.Transparency.Value;
        if (!double.IsFinite(entity.LineTypeScale) || entity.LineTypeScale <= 0.0)
        {
            throw new ArgumentException("Entity linetype scale must be finite and positive.");
        }

        return new CadResolvedStyle(
            color,
            lineWeight,
            lineTypeName,
            transparency,
            entity.LineTypeScale,
            options.DefaultLineWeightMillimeters);
    }

    private static CadAffineTransform3D CreateInsertTransform(Insert insert)
    {
        CadPoint3D insertion = ToPoint(insert.InsertPoint);
        CadPoint3D basePoint = ToPoint(insert.Block.BlockEntity.BasePoint);
        if (!double.IsFinite(insert.Rotation) ||
            !double.IsFinite(insert.XScale) || insert.XScale == 0.0 ||
            !double.IsFinite(insert.YScale) || insert.YScale == 0.0 ||
            !double.IsFinite(insert.ZScale) || insert.ZScale == 0.0)
        {
            throw new ArgumentException(
                "INSERT rotation and non-zero scale factors must be finite.");
        }

        EnsureFinite(insertion);
        EnsureFinite(basePoint);
        CadCoordinateSystem basis = CadCoordinateSystem.FromNormal(ToPoint(insert.Normal));
        double cosine = Math.Cos(insert.Rotation);
        double sine = Math.Sin(insert.Rotation);
        CadPoint3D xAxis = (basis.XAxis * (cosine * insert.XScale)) +
            (basis.YAxis * (sine * insert.XScale));
        CadPoint3D yAxis = (basis.XAxis * (-sine * insert.YScale)) +
            (basis.YAxis * (cosine * insert.YScale));
        CadPoint3D zAxis = basis.ZAxis * insert.ZScale;
        CadPoint3D translation = insertion -
            (xAxis * basePoint.X) -
            (yAxis * basePoint.Y) -
            (zAxis * basePoint.Z);
        EnsureFinite(xAxis);
        EnsureFinite(yAxis);
        EnsureFinite(zAxis);
        EnsureFinite(translation);
        return new CadAffineTransform3D(xAxis, yAxis, zAxis, translation);
    }

    private static CadCoordinateSystem TransformBasis(
        CadAffineTransform3D transform,
        CadCoordinateSystem basis) =>
        new(
            transform.TransformVector(basis.XAxis),
            transform.TransformVector(basis.YAxis),
            transform.TransformVector(basis.ZAxis));

    private static CadPoint3D TransformPoint(
        CadAffineTransform3D transform,
        bool hasTransform,
        CadPoint3D point) =>
        hasTransform ? transform.TransformPoint(point) : point;

    private static bool IsLayerZero(Layer layer) =>
        layer.Name.Equals(Layer.DefaultName, StringComparison.OrdinalIgnoreCase);

    private static string FormatEntityPath(ulong rootHandle, ulong currentHandle) =>
        rootHandle == currentHandle
            ? $"{rootHandle:X}"
            : $"{rootHandle:X}/.../{currentHandle:X}";

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
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxBlockNestingDepth,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxBlockArrayInstances,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxExpandedEntities,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxTextCodeUnitsPerEntity,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxTextGlyphs,
            1);
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

    private static void EnsureFinite(CadAffineTransform3D transform)
    {
        EnsureFinite(transform.XAxis);
        EnsureFinite(transform.YAxis);
        EnsureFinite(transform.ZAxis);
        EnsureFinite(transform.Translation);
    }

    private readonly record struct CadResolvedStyle(
        ACadSharp.Color Color,
        LineWeightType LineWeight,
        string LineTypeName,
        short Transparency,
        double LineTypeScale,
        double DefaultLineWeightMillimeters);

    private sealed class CadUnsupportedEntityException : Exception
    {
        public CadUnsupportedEntityException(string message)
            : base(message)
        {
        }
    }

    private sealed class CadSnapshotExpansionLimitException : InvalidOperationException
    {
        public CadSnapshotExpansionLimitException(string message)
            : base(message)
        {
        }
    }
}
