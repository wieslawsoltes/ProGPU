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
    public const int DefaultMaxLineTypePatterns = 65_536;
    public const int DefaultMaxLineTypeElements = 1_000_000;
    public const int DefaultMaxHatchLoops = 1_000_000;
    public const int DefaultMaxHatchSegments = 5_000_000;
    public const int DefaultMaxHatchPatterns = 1_000_000;
    public const int DefaultMaxHatchPatternFamilies = 1_000_000;
    public const int DefaultMaxHatchPatternDashes = 6_000_000;
    public const int DefaultMaxHatchTopologyVisits = 10_000_000;
    public const int DefaultMaxHatchSplineSourceValues = 10_000_000;

    public double DefaultLineWeightMillimeters { get; init; } = 0.25;
    public int DiagnosticLimit { get; init; } = DefaultDiagnosticLimit;
    public int MaxBlockNestingDepth { get; init; } = DefaultMaxBlockNestingDepth;
    public int MaxBlockArrayInstances { get; init; } = DefaultMaxBlockArrayInstances;
    public int MaxExpandedEntities { get; init; } = DefaultMaxExpandedEntities;
    public int MaxTextCodeUnitsPerEntity { get; init; } = DefaultMaxTextCodeUnitsPerEntity;
    public int MaxTextGlyphs { get; init; } = DefaultMaxTextGlyphs;
    public int MaxLineTypePatterns { get; init; } = DefaultMaxLineTypePatterns;
    public int MaxLineTypeElements { get; init; } = DefaultMaxLineTypeElements;
    public int MaxHatchLoops { get; init; } = DefaultMaxHatchLoops;
    public int MaxHatchSegments { get; init; } = DefaultMaxHatchSegments;
    public int MaxHatchPatterns { get; init; } = DefaultMaxHatchPatterns;
    public int MaxHatchPatternFamilies { get; init; } = DefaultMaxHatchPatternFamilies;
    public int MaxHatchPatternDashes { get; init; } = DefaultMaxHatchPatternDashes;
    public int MaxHatchTopologyVisits { get; init; } = DefaultMaxHatchTopologyVisits;
    public int MaxHatchSplineSourceValues { get; init; } = DefaultMaxHatchSplineSourceValues;
    public bool IncludeNonPlottableLayers { get; init; } = true;
    public ICadTextFontResolver? TextFontResolver { get; init; }
    public ICadShxFontResolver? ShxFontResolver { get; init; }
    public CadColor32 DrawingBackgroundColor { get; init; } = new(0, 0, 0);
}

/// <summary>Compiles the mutable ACadSharp graph into immutable ProGPU CAD streams.</summary>
public sealed partial class CadSnapshotCompiler
{
    private const double TwoPi = Math.PI * 2.0;

    [Flags]
    private enum TextDecorationFlags : byte
    {
        None = 0,
        Overline = 1 << 0,
        Underline = 1 << 1,
        StrikeThrough = 1 << 2,
    }

    private readonly record struct DecodedTextContent(
        string Text,
        TextDecorationFlags[]? Decorations);

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
        ICadShxFontResolver? shxFontResolver = options.ShxFontResolver is CadShxFontCatalog catalog
            ? catalog.CreateResolverSnapshot()
            : options.ShxFontResolver;
        ICadShxShapeResolver? shxShapeResolver = shxFontResolver as ICadShxShapeResolver;
        var layers = new List<CadLayerSnapshot>();
        var layerIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var styles = new List<CadStrokeStyle>();
        var styleIndices = new Dictionary<CadStrokeStyle, int>();
        var lineTypePatterns = new List<CadLineTypePattern>();
        var lineTypePatternIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lineTypeElements = new List<CadLineTypeElement>();
        var lineTypeTextResources = new List<CadLineTypeTextResource>();
        var lineTypeShapeResources = new List<CadLineTypeShapeResource>();
        var entities = new List<CadEntityHeader>(document.Entities.Count);
        var lines = new List<CadLinePrimitive>();
        var circles = new List<CadCirclePrimitive>();
        var arcs = new List<CadArcPrimitive>();
        var ellipses = new List<CadEllipsePrimitive>();
        var faces = new List<CadFacePrimitive>();
        var splines = new List<CadSplinePrimitive>();
        var polylines = new List<CadPolylinePrimitive>();
        var polylines3D = new List<CadPolyline3DPrimitive>();
        var hatches = new List<CadHatchPrimitive>();
        var hatchPatterns = new List<CadHatchPattern>();
        var hatchPatternFamilies = new List<CadHatchPatternFamily>();
        var hatchPatternDashes = new List<double>();
        var hatchLoops = new List<CadHatchLoop>();
        var hatchSegments = new List<CadHatchSegment>();
        var texts = new List<CadTextPrimitive>();
        var textGlyphRuns = new List<CadTextGlyphRun>();
        var textDecorations = new List<CadTextDecoration>();
        var mtexts = new List<CadMTextPrimitive>();
        var mtextGlyphRuns = new List<CadMTextGlyphRun>();
        var mtextBackgrounds = new List<CadMTextRectangle>();
        var mtextDecorations = new List<CadMTextRectangle>();
        var mtextStrokes = new List<CadMTextStroke>();
        var textGlyphIndices = new List<ushort>();
        var textGlyphPositions = new List<Vector2>();
        var textFonts = new List<TtfFont>();
        var textFontIndices = new Dictionary<TtfFont, int>(ReferenceEqualityComparer.Instance);
        var shxTexts = new List<CadShxTextPrimitive>();
        var shxMTexts = new List<CadShxMTextPrimitive>();
        var shxMTextGlyphRuns = new List<CadShxMTextGlyphRun>();
        var shxGlyphInstances = new List<CadShxGlyphInstance>();
        var shxShapes = new List<CadShxShapePrimitive>();
        var shxDecorationSegments = new List<CadShxDecorationSegment>();
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
        var hatchTopologyBudget = new CadHatchTopologyBudget(
            options.MaxHatchTopologyVisits);
        var hatchSplineSourceBudget = new CadHatchSplineSourceBudget(
            options.MaxHatchSplineSourceValues);
        var activeBlocks = new HashSet<BlockRecord>(ReferenceEqualityComparer.Instance);
        double globalLineTypeScale = document.Header.LineTypeScale;
        if (!double.IsFinite(globalLineTypeScale) || globalLineTypeScale <= 0.0)
        {
            throw new ArgumentException(
                "Drawing LTSCALE must be finite and positive.",
                nameof(document));
        }

        foreach (Entity entity in document.Entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entity.IsInvisible || !entity.Layer.IsOn ||
                (!options.IncludeNonPlottableLayers && !entity.Layer.PlotFlag) ||
                IsHiddenAttribute(entity))
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
            globalLineTypeScale,
            documentBounds,
            new CadSnapshotStatistics(
                document.Entities.Count,
                visibleCount,
                expandedCount,
                unsupportedCount,
                invalidCount),
            layers.ToArray(),
            styles.ToArray(),
            lineTypePatterns.ToArray(),
            lineTypeElements.ToArray(),
            lineTypeTextResources.ToArray(),
            lineTypeShapeResources.ToArray(),
            entities.ToArray(),
            lines.ToArray(),
            circles.ToArray(),
            arcs.ToArray(),
            ellipses.ToArray(),
            faces.ToArray(),
            splines.ToArray(),
            polylines.ToArray(),
            polylines3D.ToArray(),
            hatches.ToArray(),
            hatchPatterns.ToArray(),
            hatchPatternFamilies.ToArray(),
            hatchPatternDashes.ToArray(),
            hatchLoops.ToArray(),
            hatchSegments.ToArray(),
            texts.ToArray(),
            textGlyphRuns.ToArray(),
            textDecorations.ToArray(),
            mtexts.ToArray(),
            mtextGlyphRuns.ToArray(),
            mtextBackgrounds.ToArray(),
            mtextDecorations.ToArray(),
            mtextStrokes.ToArray(),
            textGlyphIndices.ToArray(),
            textGlyphPositions.ToArray(),
            textFonts.ToArray(),
            shxTexts.ToArray(),
            shxMTexts.ToArray(),
            shxMTextGlyphRuns.ToArray(),
            shxGlyphInstances.ToArray(),
            shxShapes.ToArray(),
            shxDecorationSegments.ToArray(),
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
            if (IsHiddenAttribute(entity))
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
                    options,
                    globalLineTypeScale);
                if (entity is Insert insert)
                {
                    CompileInsert(
                        insert,
                        transform,
                        hasTransform,
                        rootHandle,
                        effectiveLayer,
                        resolvedStyle,
                        depth);
                    return;
                }
                if (entity is Dimension dimension)
                {
                    CompileDimension(
                        dimension,
                        transform,
                        hasTransform,
                        rootHandle,
                        effectiveLayer,
                        resolvedStyle,
                        depth);
                    return;
                }

                int layerIndex = InternLayer(effectiveLayer, layers, layerIndices);
                int styleIndex = InternStyle(
                    resolvedStyle,
                    styles,
                    styleIndices,
                    lineTypePatterns,
                    lineTypePatternIndices,
                    lineTypeElements,
                    lineTypeTextResources,
                    lineTypeShapeResources,
                    textGlyphRuns,
                    textGlyphIndices,
                    textGlyphPositions,
                    textFonts,
                    textFontIndices,
                    shxGlyphInstances,
                    shxFontResolver,
                    options);
                CadEntityHeader? header = entity switch
                {
                    AttributeBase attributeEntity => CompileAttribute(
                        attributeEntity,
                        rootHandle,
                        transform,
                        hasTransform,
                        layerIndex,
                        styleIndex,
                        resolvedStyle,
                        effectiveLayer.Color),
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
                    Hatch hatch => CompileHatch(
                        hatch,
                        rootHandle,
                        transform,
                        hasTransform,
                        layerIndex,
                        styleIndex,
                        options,
                        hatches,
                        hatchPatterns,
                        hatchPatternFamilies,
                        hatchPatternDashes,
                        hatchLoops,
                        hatchSegments,
                        hatchTopologyBudget,
                        hatchSplineSourceBudget),
                    Shape shape => CompileShxShape(
                        shape,
                        rootHandle,
                        transform,
                        hasTransform,
                        layerIndex,
                        styleIndex,
                        options,
                        diagnostics,
                        shxShapes,
                        shxShapeResolver),
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
                        textDecorations,
                        textGlyphIndices,
                        textGlyphPositions,
                        textFonts,
                        textFontIndices,
                        shxTexts,
                        shxGlyphInstances,
                        shxDecorationSegments,
                        shxFontResolver),
                    MText mtext => CompileMText(
                        mtext,
                        rootHandle,
                        transform,
                        hasTransform,
                        layerIndex,
                        styleIndex,
                        resolvedStyle,
                        effectiveLayer.Color,
                        options,
                        diagnostics,
                        mtexts,
                        mtextGlyphRuns,
                        mtextBackgrounds,
                        mtextDecorations,
                        mtextStrokes,
                        textGlyphIndices,
                        textGlyphPositions,
                        textFonts,
                        textFontIndices,
                        shxMTexts,
                        shxMTextGlyphRuns,
                        shxGlyphInstances,
                        shxFontResolver),
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
                (exception is ArgumentException or ArithmeticException or InvalidOperationException or FormatException) &&
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
            bool parentHasTransform,
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
                            if (child is AttributeDefinition definition &&
                                !IsConstantAttribute(definition))
                            {
                                continue;
                            }

                            CompileEntityTree(
                                child,
                                instanceTransform,
                                true,
                                rootHandle,
                                effectiveLayer,
                                resolvedStyle,
                                depth + 1);
                        }

                        if (insert.Attributes.Count == 0)
                        {
                            continue;
                        }

                        // ATTRIB geometry is persisted in the INSERT's containing
                        // coordinate system after its own block transform is baked.
                        // Only ancestor composition and this MINSERT cell offset
                        // remain. Work is O(A) for A references in each array cell.
                        CadPoint3D attributeTranslation = parentTransform.Translation +
                            (rowStep * row) +
                            (columnStep * column);
                        EnsureFinite(attributeTranslation);
                        var attributeTransform = new CadAffineTransform3D(
                            parentTransform.XAxis,
                            parentTransform.YAxis,
                            parentTransform.ZAxis,
                            attributeTranslation);
                        bool attributeHasTransform = parentHasTransform || row != 0 || column != 0;
                        foreach (AttributeEntity attribute in insert.Attributes)
                        {
                            if (IsConstantAttribute(attribute))
                            {
                                continue;
                            }

                            CompileEntityTree(
                                attribute,
                                attributeTransform,
                                attributeHasTransform,
                                rootHandle,
                                effectiveLayer,
                                resolvedStyle,
                                depth + 1);
                        }
                    }
                }
            }
            finally
            {
                activeBlocks.Remove(block);
            }
        }

        void CompileDimension(
            Dimension dimension,
            CadAffineTransform3D parentTransform,
            bool parentHasTransform,
            ulong rootHandle,
            Layer effectiveLayer,
            CadResolvedStyle resolvedStyle,
            int depth)
        {
            if (depth >= options.MaxBlockNestingDepth)
            {
                throw new CadUnsupportedEntityException(
                    $"Dimension-picture nesting exceeds the configured depth of {options.MaxBlockNestingDepth}.");
            }

            BlockRecord block = dimension.Block ?? throw new ArgumentException(
                "DIMENSION has no persisted picture block.");
            if (block.Entities.Count == 0)
            {
                throw new CadUnsupportedEntityException(
                    "DIMENSION persisted picture block is empty; layout regeneration is intentionally not performed during snapshot capture.");
            }
            if ((block.Flags & (BlockTypeFlags.XRef | BlockTypeFlags.XRefOverlay | BlockTypeFlags.XRefDependent)) != 0 ||
                block.BlockEntity.IsUnloaded)
            {
                throw new CadUnsupportedEntityException(
                    "External-reference dimension pictures require an explicit resolved XRef snapshot.");
            }

            if (block.EvaluationGraph is not null)
            {
                throw new CadUnsupportedEntityException(
                    "Dynamic dimension pictures require evaluation-state lowering before expansion.");
            }

            if (!activeBlocks.Add(block))
            {
                throw new CadUnsupportedEntityException(
                    $"Recursive dimension-picture block cycle detected at '{block.Name}'.");
            }

            try
            {
                // DIMENSION group 12 is persisted in the entity OCS but denotes the
                // relative WCS displacement of its already-authored picture block.
                // A generated picture uses zero displacement; definition point 10
                // must not be reapplied to its absolute microspace geometry.
                CadCoordinateSystem basis = CadCoordinateSystem.FromNormal(
                    ToPoint(dimension.Normal));
                CadPoint3D displacement = basis.Transform(
                    ToPoint(dimension.InsertionPoint));
                EnsureFinite(displacement);
                CadAffineTransform3D localTransform = displacement == CadPoint3D.Zero
                    ? CadAffineTransform3D.Identity
                    : new CadAffineTransform3D(
                        CadAffineTransform3D.Identity.XAxis,
                        CadAffineTransform3D.Identity.YAxis,
                        CadAffineTransform3D.Identity.ZAxis,
                        displacement);
                CadAffineTransform3D pictureTransform = parentTransform.Compose(localTransform);
                EnsureFinite(pictureTransform);
                bool pictureHasTransform = parentHasTransform || displacement != CadPoint3D.Zero;

                foreach (Entity child in block.Entities)
                {
                    // Anonymous dimension pictures persist definition/control
                    // points as non-plotting POINT records. They are construction
                    // metadata, not PDMODE glyphs belonging to the dimension picture.
                    if (child is Point &&
                        child.Layer.Name.Equals(
                            Layer.DefpointsName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    CompileEntityTree(
                        child,
                        pictureTransform,
                        pictureHasTransform,
                        rootHandle,
                        effectiveLayer,
                        resolvedStyle,
                        depth + 1);
                }
            }
            finally
            {
                activeBlocks.Remove(block);
            }
        }

        CadEntityHeader CompileAttribute(
            AttributeBase attribute,
            ulong handle,
            CadAffineTransform3D transform,
            bool hasTransform,
            int layerIndex,
            int styleIndex,
            CadResolvedStyle entityStyle,
            ACadSharp.Color layerColor)
        {
            return attribute.AttributeType switch
            {
                AttributeType.SingleLine => CompileText(
                    attribute,
                    handle,
                    transform,
                    hasTransform,
                    layerIndex,
                    styleIndex,
                    options,
                    diagnostics,
                    texts,
                    textGlyphRuns,
                    textDecorations,
                    textGlyphIndices,
                    textGlyphPositions,
                    textFonts,
                    textFontIndices,
                    shxTexts,
                    shxGlyphInstances,
                    shxDecorationSegments,
                    shxFontResolver),
                AttributeType.MultiLine or AttributeType.ConstantMultiLine
                    when attribute.MText is not null => CompileMText(
                        attribute.MText,
                        handle,
                        transform,
                        hasTransform,
                        layerIndex,
                        styleIndex,
                        entityStyle,
                        layerColor,
                        options,
                        diagnostics,
                        mtexts,
                        mtextGlyphRuns,
                        mtextBackgrounds,
                        mtextDecorations,
                        mtextStrokes,
                        textGlyphIndices,
                        textGlyphPositions,
                        textFonts,
                        textFontIndices,
                        shxMTexts,
                        shxMTextGlyphRuns,
                        shxGlyphInstances,
                        shxFontResolver),
                AttributeType.MultiLine or AttributeType.ConstantMultiLine =>
                    throw new CadUnsupportedEntityException(
                        "Multiline attribute has no embedded MTEXT payload."),
                _ => throw new CadUnsupportedEntityException(
                    $"Attribute type {(int)attribute.AttributeType} is not supported."),
            };
        }
    }

    private static bool IsConstantAttribute(AttributeBase attribute) =>
        (attribute.Flags & AttributeFlags.Constant) != 0 ||
        attribute.AttributeType == AttributeType.ConstantMultiLine;

    private static bool IsHiddenAttribute(Entity entity) =>
        entity is AttributeBase attribute &&
        (attribute.Flags & AttributeFlags.Hidden) != 0;

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
            spline.IsClosed,
            spline.IsPeriodic));
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
            polyline.Flags.HasFlag(LwPolylineFlags.Plinegen),
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
            polyline.Flags.HasFlag(PolylineFlags.ContinuousLinetypePattern),
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
        bool isLineTypeContinuous,
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
            isClosed,
            isLineTypeContinuous));
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

    private static CadEntityHeader CompileShxShape(
        Shape shape,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        CadSnapshotOptions options,
        List<CadDiagnostic> diagnostics,
        List<CadShxShapePrimitive> destination,
        ICadShxShapeResolver? shxShapeResolver)
    {
        if (shape.Thickness != 0.0)
        {
            throw new CadUnsupportedEntityException(
                "SHAPE thickness requires the 3D extrusion and hidden-surface contract.");
        }
        if (!double.IsFinite(shape.Size) || shape.Size <= 0.0 ||
            !double.IsFinite(shape.RelativeXScale) || shape.RelativeXScale <= 0.0 ||
            !double.IsFinite(shape.Rotation) ||
            !double.IsFinite(shape.ObliqueAngle) ||
            Math.Abs(shape.ObliqueAngle) >= Math.PI * 0.5)
        {
            throw new ArgumentException(
                "SHAPE size, relative X scale, rotation, and oblique angle must define a finite non-degenerate transform.");
        }

        ICadShxShapeResolver resolver = shxShapeResolver ??
            throw new CadUnsupportedEntityException(
                "SHAPE requires a host resolver that supports standalone SHX shape identities.");
        CadShxShapeResolution resolution = resolver.ResolveShape(new CadShxShapeRequest(
            shape.ShapeName,
            shape.ShapeNumber,
            shape.ShapeStyle?.Filename ?? string.Empty));
        CadShxGlyphCache cache = resolution.GlyphCache ??
            throw new CadUnsupportedEntityException(
                $"SHX shape '{shape.ShapeName}' number {shape.ShapeNumber} could not be resolved.");

        CadShxGlyph glyph;
        try
        {
            glyph = cache.GetGlyph(resolution.ShapeNumber);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or NotSupportedException or
                KeyNotFoundException or ArgumentOutOfRangeException)
        {
            throw new CadUnsupportedEntityException(exception.Message);
        }
        if (!glyph.HasGeometry)
        {
            throw new CadUnsupportedEntityException(
                $"SHX shape {resolution.ShapeNumber} has no drawable geometry.");
        }

        CadCoordinateSystem basis = CadCoordinateSystem.FromNormal(ToPoint(shape.Normal));
        double cosine = Math.Cos(shape.Rotation);
        double sine = Math.Sin(shape.Rotation);
        CadPoint3D horizontal = (basis.XAxis * cosine) + (basis.YAxis * sine);
        CadPoint3D vertical = (basis.XAxis * -sine) + (basis.YAxis * cosine);
        CadPoint3D origin = ToPoint(shape.InsertionPoint);
        CadPoint3D xAxis = horizontal * (shape.Size * shape.RelativeXScale);
        CadPoint3D yAxis =
            (horizontal * (shape.Size * Math.Tan(shape.ObliqueAngle))) +
            (vertical * shape.Size);
        if (hasTransform)
        {
            origin = transform.TransformPoint(origin);
            xAxis = transform.TransformVector(xAxis);
            yAxis = transform.TransformVector(yAxis);
        }
        EnsureFinite(origin);
        EnsureFinite(xAxis);
        EnsureFinite(yAxis);

        double minimumX = glyph.BoundsMin.X;
        double minimumY = glyph.BoundsMin.Y;
        double maximumX = glyph.BoundsMax.X;
        double maximumY = glyph.BoundsMax.Y;
        CadBounds3D bounds = CadBounds3D.FromPoint(
            TransformTextPoint(origin, xAxis, yAxis, minimumX, minimumY));
        bounds = bounds
            .Include(TransformTextPoint(origin, xAxis, yAxis, maximumX, minimumY))
            .Include(TransformTextPoint(origin, xAxis, yAxis, minimumX, maximumY))
            .Include(TransformTextPoint(origin, xAxis, yAxis, maximumX, maximumY));

        int primitiveIndex = destination.Count;
        destination.Add(new CadShxShapePrimitive(origin, xAxis, yAxis, glyph));
        if (resolution.IsSubstitution)
        {
            AddDiagnostic(
                diagnostics,
                options.DiagnosticLimit,
                new CadDiagnostic(
                    CadDiagnosticSeverity.Warning,
                    "CADSNAP007",
                    $"SHAPE path {FormatEntityPath(handle, shape.Handle)} substitutes its SHX file with '{resolution.ResolvedFontName}'."));
        }
        return new CadEntityHeader(
            handle,
            CadEntityKind.ShxShape,
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
        List<CadTextDecoration> decorations,
        List<ushort> glyphIndices,
        List<Vector2> glyphPositions,
        List<TtfFont> fonts,
        Dictionary<TtfFont, int> fontIndices,
        List<CadShxTextPrimitive> shxTexts,
        List<CadShxGlyphInstance> shxGlyphInstances,
        List<CadShxDecorationSegment> shxDecorationSegments,
        ICadShxFontResolver? shxFontResolver)
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

        bool isTwoPointAlignment = text.HorizontalAlignment is
            TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Fit;
        if (isTwoPointAlignment &&
            text.VerticalAlignment != TextVerticalAlignmentType.Baseline)
        {
            throw new CadUnsupportedEntityException(
                "Aligned and fit TEXT require baseline vertical alignment.");
        }

        TextStyle cadStyle = text.Style;
        bool usesShx = cadStyle.IsShapeFile ||
            cadStyle.Filename.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
        if (usesShx || !string.IsNullOrWhiteSpace(cadStyle.BigFontFilename))
        {
            return CompileShxText(
                text,
                handle,
                transform,
                hasTransform,
                layerIndex,
                styleIndex,
                options,
                diagnostics,
                shxTexts,
                shxGlyphInstances,
                shxDecorationSegments,
                glyphIndices.Count,
                shxFontResolver);
        }

        if (cadStyle.Flags.HasFlag(StyleFlags.VerticalText))
        {
            throw new CadUnsupportedEntityException(
                "Vertical TrueType STYLE requires vertical shaping and glyph-orientation lowering.");
        }

        DecodedTextContent decodedContent = DecodeTextContent(text.Value);
        string content = decodedContent.Text;

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

        if (layout.Glyphs.Count > options.MaxTextGlyphs -
            glyphIndices.Count - shxGlyphInstances.Count)
        {
            throw new CadSnapshotExpansionLimitException(
                $"Retained TEXT glyph count exceeds the configured document limit of {options.MaxTextGlyphs}.");
        }

        double ascent = (double)font.Ascender / font.UnitsPerEm;
        double descent = (double)font.Descender / font.UnitsPerEm;
        double width = layout.ContentSize.X;
        if (!double.IsFinite(width) || (isTwoPointAlignment && width <= 0.0))
        {
            throw new CadUnsupportedEntityException(
                "Aligned and fit TEXT require a finite positive shaped advance.");
        }

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

        CadCoordinateSystem basis = CadCoordinateSystem.FromNormal(ToPoint(text.Normal));
        CadPoint3D anchor;
        double cosine;
        double sine;
        double xScale;
        double effectiveHeight = height;
        if (isTwoPointAlignment)
        {
            CadPoint3D start = ToPoint(text.InsertPoint);
            CadPoint3D end = ToPoint(text.AlignmentPoint);
            double deltaX = end.X - start.X;
            double deltaY = end.Y - start.Y;
            double deltaZ = end.Z - start.Z;
            double baselineLength = new CadPoint3D(deltaX, deltaY, 0.0).Length;
            if (!double.IsFinite(deltaZ) || !double.IsFinite(baselineLength) ||
                baselineLength <= 0.0 ||
                Math.Abs(deltaZ) > Math.Max(1.0, baselineLength) * 1e-12)
            {
                throw new ArgumentException(
                    "Aligned and fit TEXT require two distinct coplanar OCS baseline points.");
            }

            cosine = deltaX / baselineLength;
            sine = deltaY / baselineLength;
            xScale = baselineLength / width;
            if (text.HorizontalAlignment == TextHorizontalAlignment.Aligned)
            {
                effectiveHeight = xScale / widthFactor;
            }

            anchor = basis.Transform(
                text.Mirror.HasFlag(TextMirrorFlag.Backward) ? end : start);
        }
        else
        {
            bool usesAlignmentPoint = text.HorizontalAlignment != TextHorizontalAlignment.Left ||
                text.VerticalAlignment != TextVerticalAlignmentType.Baseline;
            anchor = basis.Transform(ToPoint(
                usesAlignmentPoint ? text.AlignmentPoint : text.InsertPoint));
            cosine = Math.Cos(text.Rotation);
            sine = Math.Sin(text.Rotation);
            xScale = height * widthFactor;
        }

        if (!double.IsFinite(effectiveHeight) || effectiveHeight <= 0.0 ||
            !double.IsFinite(xScale) || xScale <= 0.0)
        {
            throw new ArithmeticException(
                "TEXT alignment scaling exceeds the supported numeric range.");
        }

        CadPoint3D horizontal = (basis.XAxis * cosine) + (basis.YAxis * sine);
        CadPoint3D vertical = (basis.XAxis * -sine) + (basis.YAxis * cosine);
        TextMirrorFlag mirror = text.Mirror;
        double mirrorX = mirror.HasFlag(TextMirrorFlag.Backward) ? -1.0 : 1.0;
        double mirrorY = mirror.HasFlag(TextMirrorFlag.UpsideDown) ? -1.0 : 1.0;
        CadPoint3D xAxis = horizontal * (xScale * mirrorX);
        CadPoint3D yAxis =
            (horizontal * (-effectiveHeight * Math.Tan(oblique) * mirrorY)) +
            (vertical * (-effectiveHeight * mirrorY));
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

        List<CadTextDecoration>? compiledDecorations = CompileTextDecorations(
            decodedContent,
            layout,
            font,
            horizontalOffset,
            verticalOffset);
        if (compiledDecorations is not null)
        {
            for (int i = 0; i < compiledDecorations.Count; i++)
            {
                CadTextDecoration decoration = compiledDecorations[i];
                minimumX = Math.Min(minimumX, decoration.X);
                maximumX = Math.Max(maximumX, decoration.X + decoration.Width);
                minimumY = Math.Min(minimumY, decoration.Y);
                maximumY = Math.Max(maximumY, decoration.Y + decoration.Height);
            }
        }

        CadBounds3D bounds = CadBounds3D.FromPoint(
            TransformTextPoint(anchor, xAxis, yAxis, minimumX, minimumY));
        bounds = bounds
            .Include(TransformTextPoint(anchor, xAxis, yAxis, maximumX, minimumY))
            .Include(TransformTextPoint(anchor, xAxis, yAxis, minimumX, maximumY))
            .Include(TransformTextPoint(anchor, xAxis, yAxis, maximumX, maximumY));
        int glyphOffset = glyphIndices.Count;
        int runOffset = runs.Count;
        int decorationOffset = decorations.Count;
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
        if (compiledDecorations is not null)
        {
            decorations.AddRange(compiledDecorations);
        }
        int primitiveIndex = destination.Count;
        destination.Add(new CadTextPrimitive(
            anchor,
            xAxis,
            yAxis,
            glyphOffset,
            compiledGlyphIndices.Length,
            runOffset,
            runs.Count - runOffset,
            decorationOffset,
            decorations.Count - decorationOffset));
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

    private static CadEntityHeader CompileShxText(
        TextEntity text,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        CadSnapshotOptions options,
        List<CadDiagnostic> diagnostics,
        List<CadShxTextPrimitive> destination,
        List<CadShxGlyphInstance> glyphInstances,
        List<CadShxDecorationSegment> decorationSegments,
        int retainedTrueTypeGlyphCount,
        ICadShxFontResolver? shxFontResolver)
    {
        TextStyle cadStyle = text.Style;
        if (!string.IsNullOrWhiteSpace(cadStyle.BigFontFilename))
        {
            throw new CadUnsupportedEntityException(
                "Big Font TEXT requires the distinct bounded Big Font container and character-range contract.");
        }
        bool isVertical = cadStyle.Flags.HasFlag(StyleFlags.VerticalText);
        if (isVertical &&
            (text.HorizontalAlignment != TextHorizontalAlignment.Left ||
             text.VerticalAlignment != TextVerticalAlignmentType.Baseline))
        {
            throw new CadUnsupportedEntityException(
                "Vertical SHX TEXT currently requires the documented default top-center insertion contract; non-default justification requires a verified vertical placement contract.");
        }

        ICadShxFontResolver resolver = shxFontResolver ??
            throw new CadUnsupportedEntityException(
                "Standard SHX TEXT requires a host SHX font resolver.");
        CadShxFontResolution fontResolution = resolver.Resolve(new CadShxFontRequest(
            cadStyle.Name,
            cadStyle.Filename,
            cadStyle.BigFontFilename));
        CadShxGlyphCache cache = fontResolution.GlyphCache ??
            throw new CadUnsupportedEntityException(
                $"Text style '{cadStyle.Name}' could not resolve a standard SHX font.");

        CadShxTextLayout layout;
        try
        {
            layout = new CadShxTextLayout(
                text.Value,
                cache,
                isVertical ? CadShxOrientation.Vertical : CadShxOrientation.Horizontal,
                new CadShxTextLayoutOptions
                {
                    MaxCodeUnits = options.MaxTextCodeUnitsPerEntity,
                    MaxGlyphs = options.MaxTextGlyphs,
                });
        }
        catch (Exception exception) when (
            exception is InvalidDataException or NotSupportedException or
                KeyNotFoundException or ArgumentOutOfRangeException)
        {
            throw new CadUnsupportedEntityException(exception.Message);
        }

        ReadOnlySpan<CadShxGlyphPlacement> placements = layout.Glyphs.Span;
        if (isVertical)
        {
            for (int i = 0; i < placements.Length; i++)
            {
                CadShxGlyphPlacement placement = placements[i];
                if (placement.Decorations != CadShxTextDecoration.None)
                {
                    throw new CadUnsupportedEntityException(
                        "Decorated vertical SHX TEXT requires independently verified vertical decoration placement.");
                }
                if (Math.Abs(placement.Glyph.Advance.X) >
                        Math.Max(1.0, Math.Abs(placement.Glyph.Advance.Y)) * 1e-6 ||
                    placement.Glyph.Advance.Y > 0.0f)
                {
                    throw new CadUnsupportedEntityException(
                        $"Vertical standard SHX TEXT requires downward Y-only character advances; " +
                        $"font '{cache.Font.Name}' shape {placement.Glyph.ShapeNumber} produced " +
                        $"({placement.Glyph.Advance.X:R}, {placement.Glyph.Advance.Y:R}).");
                }
            }
        }
        else
        {
            for (int i = 0; i < placements.Length; i++)
            {
                CadShxGlyphPlacement placement = placements[i];
                if (Math.Abs(placement.Glyph.Advance.Y) >
                        Math.Max(1.0, Math.Abs(placement.Glyph.Advance.X)) * 1e-6 ||
                    placement.Glyph.Advance.X < 0.0f)
                {
                    throw new CadUnsupportedEntityException(
                        $"Horizontal standard SHX TEXT requires nonnegative X-only character advances; " +
                        $"font '{cache.Font.Name}' shape {placement.Glyph.ShapeNumber} produced " +
                        $"({placement.Glyph.Advance.X:R}, {placement.Glyph.Advance.Y:R}).");
                }
            }
        }
        if (placements.Length > options.MaxTextGlyphs -
            retainedTrueTypeGlyphCount - glyphInstances.Count)
        {
            throw new CadSnapshotExpansionLimitException(
                $"Retained TEXT glyph count exceeds the configured document limit of {options.MaxTextGlyphs}.");
        }

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

        double flowLength = isVertical ? -layout.Advance.Y : layout.Advance.X;
        double crossAdvance = isVertical ? layout.Advance.X : layout.Advance.Y;
        if (!double.IsFinite(flowLength) || flowLength <= 0.0 ||
            Math.Abs(crossAdvance) > Math.Max(1.0, flowLength) * 1e-6)
        {
            throw new CadUnsupportedEntityException(
                $"{(isVertical ? "Vertical" : "Horizontal")} standard SHX TEXT requires a finite positive axis-aligned advance; " +
                $"font '{cache.Font.Name}' produced ({layout.Advance.X:R}, {layout.Advance.Y:R}).");
        }

        double horizontalOffset = isVertical ? 0.0 : text.HorizontalAlignment switch
        {
            TextHorizontalAlignment.Center or TextHorizontalAlignment.Middle => -flowLength * 0.5,
            TextHorizontalAlignment.Right => -flowLength,
            _ => 0.0,
        };
        TextVerticalAlignmentType verticalAlignment = text.HorizontalAlignment == TextHorizontalAlignment.Middle
            ? TextVerticalAlignmentType.Middle
            : text.VerticalAlignment;
        double verticalOffset = isVertical ? 0.0 : verticalAlignment switch
        {
            TextVerticalAlignmentType.Top => -cache.Font.Above,
            TextVerticalAlignmentType.Middle => -(cache.Font.Above - cache.Font.Below) * 0.5,
            TextVerticalAlignmentType.Bottom => cache.Font.Below,
            _ => 0.0,
        };

        bool isTwoPointAlignment = !isVertical && text.HorizontalAlignment is
            TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Fit;
        CadCoordinateSystem basis = CadCoordinateSystem.FromNormal(ToPoint(text.Normal));
        CadPoint3D anchor;
        double cosine;
        double sine;
        double xScale;
        double yScale = height / cache.Font.Above;
        if (isTwoPointAlignment)
        {
            CadPoint3D start = ToPoint(text.InsertPoint);
            CadPoint3D end = ToPoint(text.AlignmentPoint);
            double deltaX = end.X - start.X;
            double deltaY = end.Y - start.Y;
            double deltaZ = end.Z - start.Z;
            double baselineLength = new CadPoint3D(deltaX, deltaY, 0.0).Length;
            if (!double.IsFinite(deltaZ) || !double.IsFinite(baselineLength) ||
                baselineLength <= 0.0 ||
                Math.Abs(deltaZ) > Math.Max(1.0, baselineLength) * 1e-12)
            {
                throw new ArgumentException(
                    "Aligned and fit TEXT require two distinct coplanar OCS baseline points.");
            }

            cosine = deltaX / baselineLength;
            sine = deltaY / baselineLength;
            xScale = baselineLength / flowLength;
            if (text.HorizontalAlignment == TextHorizontalAlignment.Aligned)
            {
                yScale = xScale / widthFactor;
            }
            anchor = basis.Transform(
                text.Mirror.HasFlag(TextMirrorFlag.Backward) ? end : start);
        }
        else
        {
            bool usesAlignmentPoint = !isVertical &&
                (text.HorizontalAlignment != TextHorizontalAlignment.Left ||
                 text.VerticalAlignment != TextVerticalAlignmentType.Baseline);
            anchor = basis.Transform(ToPoint(
                usesAlignmentPoint ? text.AlignmentPoint : text.InsertPoint));
            cosine = Math.Cos(text.Rotation);
            sine = Math.Sin(text.Rotation);
            xScale = yScale * widthFactor;
        }

        if (!double.IsFinite(xScale) || xScale <= 0.0 ||
            !double.IsFinite(yScale) || yScale <= 0.0)
        {
            throw new ArithmeticException(
                "SHX TEXT alignment scaling exceeds the supported numeric range.");
        }

        CadPoint3D horizontal = (basis.XAxis * cosine) + (basis.YAxis * sine);
        CadPoint3D vertical = (basis.XAxis * -sine) + (basis.YAxis * cosine);
        TextMirrorFlag mirror = text.Mirror;
        double mirrorX = mirror.HasFlag(TextMirrorFlag.Backward) ? -1.0 : 1.0;
        double mirrorY = mirror.HasFlag(TextMirrorFlag.UpsideDown) ? -1.0 : 1.0;
        CadPoint3D xAxis = horizontal * (xScale * mirrorX);
        CadPoint3D yAxis =
            (horizontal * (yScale * Math.Tan(oblique) * mirrorY)) +
            (vertical * (yScale * mirrorY));
        if (hasTransform)
        {
            anchor = transform.TransformPoint(anchor);
            xAxis = transform.TransformVector(xAxis);
            yAxis = transform.TransformVector(yAxis);
        }
        EnsureFinite(anchor);
        EnsureFinite(xAxis);
        EnsureFinite(yAxis);

        double minimumX;
        double maximumX;
        double minimumY;
        double maximumY;
        if (isVertical)
        {
            minimumX = Math.Min(0.0, layout.BoundsMin.X);
            maximumX = Math.Max(0.0, layout.BoundsMax.X);
            minimumY = Math.Min(layout.Advance.Y, layout.BoundsMin.Y);
            maximumY = Math.Max(0.0, layout.BoundsMax.Y);
        }
        else
        {
            minimumX = Math.Min(horizontalOffset, layout.BoundsMin.X + horizontalOffset);
            maximumX = Math.Max(
                horizontalOffset + flowLength,
                layout.BoundsMax.X + horizontalOffset);
            minimumY = Math.Min(
                verticalOffset - cache.Font.Below,
                layout.BoundsMin.Y + verticalOffset);
            maximumY = Math.Max(
                verticalOffset + cache.Font.Above,
                layout.BoundsMax.Y + verticalOffset);
        }
        CadBounds3D bounds = CadBounds3D.FromPoint(
            TransformTextPoint(anchor, xAxis, yAxis, minimumX, minimumY));
        bounds = bounds
            .Include(TransformTextPoint(anchor, xAxis, yAxis, maximumX, minimumY))
            .Include(TransformTextPoint(anchor, xAxis, yAxis, minimumX, maximumY))
            .Include(TransformTextPoint(anchor, xAxis, yAxis, maximumX, maximumY));

        int glyphOffset = glyphInstances.Count;
        for (int i = 0; i < placements.Length; i++)
        {
            CadShxGlyphPlacement placement = placements[i];
            float x = checked((float)(placement.Origin.X + horizontalOffset));
            float y = checked((float)(placement.Origin.Y + verticalOffset));
            if (!float.IsFinite(x) || !float.IsFinite(y))
            {
                throw new ArithmeticException(
                    "SHX TEXT glyph positions exceed the retained numeric range.");
            }
            glyphInstances.Add(new CadShxGlyphInstance(placement.Glyph, x, y));
        }

        int decorationOffset = decorationSegments.Count;
        int decorationCount = isVertical
            ? 0
            : AppendShxDecorations(
                placements,
                cache.Font,
                horizontalOffset,
                verticalOffset,
                decorationSegments);
        int primitiveIndex = destination.Count;
        destination.Add(new CadShxTextPrimitive(
            anchor,
            xAxis,
            yAxis,
            glyphOffset,
            placements.Length,
            decorationOffset,
            decorationCount));
        if (fontResolution.IsSubstitution)
        {
            string resolved = string.IsNullOrWhiteSpace(fontResolution.ResolvedFontName)
                ? cache.Font.Name
                : fontResolution.ResolvedFontName;
            AddDiagnostic(
                diagnostics,
                options.DiagnosticLimit,
                new CadDiagnostic(
                    CadDiagnosticSeverity.Warning,
                    "CADSNAP006",
                    $"TEXT path {FormatEntityPath(handle, text.Handle)} substitutes '{cadStyle.Filename}' with SHX font '{resolved}'."));
        }

        return new CadEntityHeader(
            handle,
            CadEntityKind.ShxText,
            layerIndex,
            styleIndex,
            primitiveIndex,
            bounds);
    }

    private struct ShxDecorationAccumulator
    {
        public bool IsActive;
        public double Start;
        public double End;
    }

    private static int AppendShxDecorations(
        ReadOnlySpan<CadShxGlyphPlacement> placements,
        CadShxFont font,
        double horizontalOffset,
        double verticalOffset,
        List<CadShxDecorationSegment> destination)
    {
        CadShxTextDecoration used = CadShxTextDecoration.None;
        for (int i = 0; i < placements.Length; i++)
        {
            used |= placements[i].Decorations;
        }
        if (used == CadShxTextDecoration.None)
        {
            return 0;
        }

        int initialCount = destination.Count;
        var overline = new ShxDecorationAccumulator();
        var underline = new ShxDecorationAccumulator();
        var strikeThrough = new ShxDecorationAccumulator();
        double overlineY = verticalOffset + font.Above;
        double underlineY = verticalOffset - font.Below;
        double strikeThroughY = verticalOffset + ((font.Above - font.Below) * 0.5);
        for (int i = 0; i < placements.Length; i++)
        {
            CadShxGlyphPlacement placement = placements[i];
            double start = horizontalOffset + placement.Origin.X;
            double end = start + placement.Glyph.Advance.X;
            UpdateShxDecoration(
                ref overline,
                placement.Decorations.HasFlag(CadShxTextDecoration.Overline),
                start,
                end,
                overlineY,
                destination);
            UpdateShxDecoration(
                ref underline,
                placement.Decorations.HasFlag(CadShxTextDecoration.Underline),
                start,
                end,
                underlineY,
                destination);
            UpdateShxDecoration(
                ref strikeThrough,
                placement.Decorations.HasFlag(CadShxTextDecoration.StrikeThrough),
                start,
                end,
                strikeThroughY,
                destination);
        }

        FlushShxDecoration(ref overline, overlineY, destination);
        FlushShxDecoration(ref underline, underlineY, destination);
        FlushShxDecoration(ref strikeThrough, strikeThroughY, destination);
        return destination.Count - initialCount;
    }

    private static void UpdateShxDecoration(
        ref ShxDecorationAccumulator accumulator,
        bool enabled,
        double start,
        double end,
        double y,
        List<CadShxDecorationSegment> destination)
    {
        if (!enabled)
        {
            FlushShxDecoration(ref accumulator, y, destination);
            return;
        }

        if (!accumulator.IsActive)
        {
            accumulator.IsActive = true;
            accumulator.Start = start;
        }
        accumulator.End = end;
    }

    private static void FlushShxDecoration(
        ref ShxDecorationAccumulator accumulator,
        double y,
        List<CadShxDecorationSegment> destination)
    {
        if (!accumulator.IsActive)
        {
            return;
        }

        if (accumulator.Start != accumulator.End)
        {
            float start = checked((float)accumulator.Start);
            float end = checked((float)accumulator.End);
            float ordinate = checked((float)y);
            if (!float.IsFinite(start) || !float.IsFinite(end) || !float.IsFinite(ordinate))
            {
                throw new ArithmeticException(
                    "SHX TEXT decoration coordinates exceed the retained numeric range.");
            }
            destination.Add(new CadShxDecorationSegment(
                start,
                ordinate,
                end,
                ordinate));
        }
        accumulator = default;
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

    private struct DecorationAccumulator
    {
        public bool IsActive;
        public double Start;
        public double End;
    }

    private static List<CadTextDecoration>? CompileTextDecorations(
        DecodedTextContent content,
        TextLayout layout,
        TtfFont font,
        double horizontalOffset,
        double verticalOffset)
    {
        TextDecorationFlags[]? sourceFlags = content.Decorations;
        if (sourceFlags is null)
        {
            return null;
        }

        TextDecorationFlags usedFlags = TextDecorationFlags.None;
        for (int i = 0; i < content.Text.Length; i++)
        {
            usedFlags |= sourceFlags[i];
        }
        if (usedFlags == TextDecorationFlags.None)
        {
            return null;
        }

        if (font.UnitsPerEm == 0)
        {
            throw new CadUnsupportedEntityException(
                "Decorated TEXT requires finite OpenType font metrics.");
        }

        double unitsPerEm = font.UnitsPerEm;
        double underlineThickness = (font.UnderlineThickness ?? 0) / unitsPerEm;
        double strikeThickness = (font.StrikeoutThickness ?? 0) / unitsPerEm;
        if ((usedFlags & (TextDecorationFlags.Overline | TextDecorationFlags.Underline)) != 0 &&
            underlineThickness <= 0.0)
        {
            throw new CadUnsupportedEntityException(
                "Overlined and underlined TEXT require a valid OpenType post-table underline thickness.");
        }
        if (usedFlags.HasFlag(TextDecorationFlags.Underline) &&
            !font.UnderlinePosition.HasValue)
        {
            throw new CadUnsupportedEntityException(
                "Underlined TEXT requires a valid OpenType post-table underline position.");
        }
        if (usedFlags.HasFlag(TextDecorationFlags.StrikeThrough) &&
            (!font.StrikeoutPosition.HasValue || strikeThickness <= 0.0))
        {
            throw new CadUnsupportedEntityException(
                "Strike-through TEXT requires valid OpenType OS/2 strikeout metrics.");
        }

        int textLength = content.Text.Length;
        var clusterStarts = new bool[textLength];
        for (int i = 0; i < layout.Glyphs.Count; i++)
        {
            int cluster = layout.Glyphs[i].Cluster;
            if ((uint)cluster >= (uint)textLength)
            {
                throw new InvalidOperationException(
                    "TEXT shaping returned a cluster outside the decoded UTF-16 range.");
            }
            clusterStarts[cluster] = true;
        }

        var clusterEnds = new int[textLength];
        int nextCluster = textLength;
        for (int i = textLength - 1; i >= 0; i--)
        {
            if (!clusterStarts[i])
            {
                continue;
            }
            clusterEnds[i] = nextCluster;
            nextCluster = i;
        }

        var result = new List<CadTextDecoration>();
        var overline = new DecorationAccumulator();
        var underline = new DecorationAccumulator();
        var strikeThrough = new DecorationAccumulator();
        for (int glyphIndex = 0; glyphIndex < layout.Glyphs.Count;)
        {
            TextRunGlyph first = layout.Glyphs[glyphIndex];
            int cluster = first.Cluster;
            int clusterEnd = clusterEnds[cluster];
            TextDecorationFlags flags = sourceFlags[cluster];
            for (int i = cluster + 1; i < clusterEnd; i++)
            {
                if (sourceFlags[i] != flags)
                {
                    throw new CadUnsupportedEntityException(
                        "A TEXT decoration boundary splits one shaped glyph cluster.");
                }
            }

            double left = first.Position.X;
            double right = first.Position.X + Math.Max(0.0f, first.Glyph.Advance);
            int glyphEnd = glyphIndex + 1;
            while (glyphEnd < layout.Glyphs.Count &&
                   layout.Glyphs[glyphEnd].Cluster == cluster)
            {
                TextRunGlyph glyph = layout.Glyphs[glyphEnd++];
                left = Math.Min(left, glyph.Position.X);
                right = Math.Max(
                    right,
                    glyph.Position.X + Math.Max(0.0f, glyph.Glyph.Advance));
            }

            left += horizontalOffset;
            right += horizontalOffset;
            UpdateDecorationAccumulator(
                ref overline,
                flags.HasFlag(TextDecorationFlags.Overline),
                left,
                right,
                TextDecorationFlags.Overline,
                font,
                underlineThickness,
                strikeThickness,
                verticalOffset,
                result);
            UpdateDecorationAccumulator(
                ref underline,
                flags.HasFlag(TextDecorationFlags.Underline),
                left,
                right,
                TextDecorationFlags.Underline,
                font,
                underlineThickness,
                strikeThickness,
                verticalOffset,
                result);
            UpdateDecorationAccumulator(
                ref strikeThrough,
                flags.HasFlag(TextDecorationFlags.StrikeThrough),
                left,
                right,
                TextDecorationFlags.StrikeThrough,
                font,
                underlineThickness,
                strikeThickness,
                verticalOffset,
                result);
            glyphIndex = glyphEnd;
        }

        FlushDecorationAccumulator(
            ref overline,
            TextDecorationFlags.Overline,
            font,
            underlineThickness,
            strikeThickness,
            verticalOffset,
            result);
        FlushDecorationAccumulator(
            ref underline,
            TextDecorationFlags.Underline,
            font,
            underlineThickness,
            strikeThickness,
            verticalOffset,
            result);
        FlushDecorationAccumulator(
            ref strikeThrough,
            TextDecorationFlags.StrikeThrough,
            font,
            underlineThickness,
            strikeThickness,
            verticalOffset,
            result);
        return result;
    }

    private static void UpdateDecorationAccumulator(
        ref DecorationAccumulator accumulator,
        bool enabled,
        double left,
        double right,
        TextDecorationFlags kind,
        TtfFont font,
        double underlineThickness,
        double strikeThickness,
        double verticalOffset,
        List<CadTextDecoration> destination)
    {
        if (!enabled || right <= left)
        {
            FlushDecorationAccumulator(
                ref accumulator,
                kind,
                font,
                underlineThickness,
                strikeThickness,
                verticalOffset,
                destination);
            return;
        }

        const double mergeTolerance = 1e-5;
        if (accumulator.IsActive && left <= accumulator.End + mergeTolerance)
        {
            accumulator.Start = Math.Min(accumulator.Start, left);
            accumulator.End = Math.Max(accumulator.End, right);
            return;
        }

        FlushDecorationAccumulator(
            ref accumulator,
            kind,
            font,
            underlineThickness,
            strikeThickness,
            verticalOffset,
            destination);
        accumulator.IsActive = true;
        accumulator.Start = left;
        accumulator.End = right;
    }

    private static void FlushDecorationAccumulator(
        ref DecorationAccumulator accumulator,
        TextDecorationFlags kind,
        TtfFont font,
        double underlineThickness,
        double strikeThickness,
        double verticalOffset,
        List<CadTextDecoration> destination)
    {
        if (!accumulator.IsActive)
        {
            return;
        }

        double top;
        double thickness;
        switch (kind)
        {
            case TextDecorationFlags.Overline:
                top = verticalOffset - ((double)font.Ascender / font.UnitsPerEm);
                thickness = underlineThickness;
                break;
            case TextDecorationFlags.Underline:
                top = verticalOffset - ((double)font.UnderlinePosition!.Value / font.UnitsPerEm);
                thickness = underlineThickness;
                break;
            case TextDecorationFlags.StrikeThrough:
                top = verticalOffset - ((double)font.StrikeoutPosition!.Value / font.UnitsPerEm);
                thickness = strikeThickness;
                break;
            default:
                throw new InvalidOperationException("Unknown TEXT decoration kind.");
        }

        float x = checked((float)accumulator.Start);
        float y = checked((float)top);
        float width = checked((float)(accumulator.End - accumulator.Start));
        float height = checked((float)thickness);
        if (!float.IsFinite(x) || !float.IsFinite(y) ||
            !float.IsFinite(width) || !float.IsFinite(height))
        {
            throw new ArithmeticException(
                "TEXT decoration geometry exceeds the retained numeric range.");
        }

        destination.Add(new CadTextDecoration(x, y, width, height));
        accumulator = default;
    }

    private static DecodedTextContent DecodeTextContent(string source)
    {
        bool requiresDecoding = source.Contains("%%", StringComparison.Ordinal) ||
            source.Contains("\\U+", StringComparison.OrdinalIgnoreCase);
        if (!requiresDecoding)
        {
            EnsureValidUtf16(source);
            return new DecodedTextContent(source, null);
        }

        var decoded = new char[source.Length];
        var decorations = new TextDecorationFlags[source.Length];
        TextDecorationFlags activeDecorations = TextDecorationFlags.None;
        bool hasDecorations = false;
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

                decoded[written] = (char)scalar;
                decorations[written++] = activeDecorations;
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
                if (code is 'o' or 'u' or 'k')
                {
                    TextDecorationFlags decoration = code switch
                    {
                        'o' => TextDecorationFlags.Overline,
                        'u' => TextDecorationFlags.Underline,
                        _ => TextDecorationFlags.StrikeThrough,
                    };
                    activeDecorations ^= decoration;
                    hasDecorations = true;
                    i += 2;
                    continue;
                }

                if (code is >= '0' and <= '9')
                {
                    if (i + 4 >= source.Length ||
                        source[i + 3] is < '0' or > '9' ||
                        source[i + 4] is < '0' or > '9')
                    {
                        throw new CadUnsupportedEntityException(
                            "TEXT numeric control codes require exactly three decimal digits.");
                    }

                    int scalar = ((source[i + 2] - '0') * 100) +
                        ((source[i + 3] - '0') * 10) +
                        (source[i + 4] - '0');
                    decoded[written] = (char)scalar;
                    decorations[written++] = activeDecorations;
                    i += 4;
                    continue;
                }

                decoded[written] = code switch
                {
                    'd' => '\u00B0',
                    'p' => '\u00B1',
                    'c' => '\u2205',
                    '%' => '%',
                    _ => throw new CadUnsupportedEntityException(
                        $"TEXT contains unsupported AutoCAD control code '%%{source[i + 2]}'."),
                };
                decorations[written++] = activeDecorations;
                i += 2;
                continue;
            }

            decoded[written] = value;
            decorations[written++] = activeDecorations;
        }

        EnsureValidUtf16(decoded.AsSpan(0, written));
        return new DecodedTextContent(
            new string(decoded, 0, written),
            hasDecorations ? decorations : null);
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
        Dictionary<CadStrokeStyle, int> indices,
        List<CadLineTypePattern> lineTypePatterns,
        Dictionary<string, int> lineTypePatternIndices,
        List<CadLineTypeElement> lineTypeElements,
        List<CadLineTypeTextResource> lineTypeTextResources,
        List<CadLineTypeShapeResource> lineTypeShapeResources,
        List<CadTextGlyphRun> textGlyphRuns,
        List<ushort> textGlyphIndices,
        List<Vector2> textGlyphPositions,
        List<TtfFont> textFonts,
        Dictionary<TtfFont, int> textFontIndices,
        List<CadShxGlyphInstance> shxGlyphInstances,
        ICadShxFontResolver? shxFontResolver,
        CadSnapshotOptions options)
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
        int lineTypePatternIndex = InternLineTypePattern(
            resolved.LineType,
            lineTypePatterns,
            lineTypePatternIndices,
            lineTypeElements,
            lineTypeTextResources,
            lineTypeShapeResources,
            textGlyphRuns,
            textGlyphIndices,
            textGlyphPositions,
            textFonts,
            textFontIndices,
            shxGlyphInstances,
            shxFontResolver,
            options);
        CadStrokeStyle style = new(
            color.R,
            color.G,
            color.B,
            alpha,
            millimeters,
            lineWeight == LineWeightType.W0,
            resolved.LineType.Name,
            resolved.LineTypeScale,
            lineTypePatternIndex);

        if (indices.TryGetValue(style, out int index))
        {
            return index;
        }

        index = styles.Count;
        indices.Add(style, index);
        styles.Add(style);
        return index;
    }

    private static int InternLineTypePattern(
        LineType lineType,
        List<CadLineTypePattern> patterns,
        Dictionary<string, int> indices,
        List<CadLineTypeElement> elements,
        List<CadLineTypeTextResource> textResources,
        List<CadLineTypeShapeResource> shapeResources,
        List<CadTextGlyphRun> textGlyphRuns,
        List<ushort> textGlyphIndices,
        List<Vector2> textGlyphPositions,
        List<TtfFont> textFonts,
        Dictionary<TtfFont, int> textFontIndices,
        List<CadShxGlyphInstance> shxGlyphInstances,
        ICadShxFontResolver? shxFontResolver,
        CadSnapshotOptions options)
    {
        string name = lineType.Name;
        if (indices.TryGetValue(name, out int existing))
        {
            return existing;
        }

        if (patterns.Count >= options.MaxLineTypePatterns)
        {
            throw new CadSnapshotExpansionLimitException(
                $"Referenced linetype count exceeds the configured limit of {options.MaxLineTypePatterns}.");
        }

        int elementOffset = elements.Count;
        int textResourceOffset = textResources.Count;
        int shapeResourceOffset = shapeResources.Count;
        int textRunOffset = textGlyphRuns.Count;
        int textGlyphOffset = textGlyphIndices.Count;
        int textFontOffset = textFonts.Count;
        int shxGlyphOffset = shxGlyphInstances.Count;
        int elementCount = 0;
        bool hasComplexElement = false;
        double patternLength = 0.0;
        double firstLength = 0.0;
        try
        {
            foreach (LineType.Segment segment in lineType.Segments)
            {
                if (elements.Count >= options.MaxLineTypeElements)
                {
                    throw new CadSnapshotExpansionLimitException(
                        $"Referenced linetype element count exceeds the configured limit of {options.MaxLineTypeElements}.");
                }

                double length = segment.Length;
                if (!double.IsFinite(length))
                {
                    throw new ArgumentException(
                        $"Linetype '{name}' contains a non-finite element length.");
                }

                if (elementCount == 0)
                {
                    firstLength = length;
                }

                patternLength += Math.Abs(length);
                if (!double.IsFinite(patternLength))
                {
                    throw new ArgumentException(
                        $"Linetype '{name}' pattern length exceeds the finite CAD range.");
                }

                byte complexTypeFlags = checked((byte)segment.Flags);
                hasComplexElement |= complexTypeFlags != 0;
                elements.Add(CompileLineTypeElement(
                    segment,
                    complexTypeFlags,
                    textResources,
                    shapeResources,
                    textGlyphRuns,
                    textGlyphIndices,
                    textGlyphPositions,
                    textFonts,
                    textFontIndices,
                    shxGlyphInstances,
                    shxFontResolver,
                    options));
                elementCount++;
            }

            bool namedContinuous = IsContinuousLineTypeName(name);
            CadLineTypePatternKind kind;
            if (namedContinuous || elementCount == 0)
            {
                kind = CadLineTypePatternKind.Continuous;
            }
            else if (lineType.Alignment != 'A')
            {
                kind = CadLineTypePatternKind.UnsupportedAlignment;
            }
            else
            {
                if (elementCount < 2 || firstLength < 0.0 || patternLength <= 0.0)
                {
                    throw new ArgumentException(
                        $"A-aligned linetype '{name}' requires at least two elements, a non-negative first element, and a positive pattern length.");
                }

                kind = hasComplexElement
                    ? CadLineTypePatternKind.Complex
                    : CadLineTypePatternKind.Simple;
            }

            int index = patterns.Count;
            patterns.Add(new CadLineTypePattern(
                name,
                lineType.Alignment,
                elementOffset,
                elementCount,
                patternLength,
                kind));
            indices.Add(name, index);
            return index;
        }
        catch
        {
            if (elements.Count > elementOffset)
            {
                elements.RemoveRange(elementOffset, elements.Count - elementOffset);
            }
            if (textResources.Count > textResourceOffset)
            {
                textResources.RemoveRange(
                    textResourceOffset,
                    textResources.Count - textResourceOffset);
            }
            if (shapeResources.Count > shapeResourceOffset)
            {
                shapeResources.RemoveRange(
                    shapeResourceOffset,
                    shapeResources.Count - shapeResourceOffset);
            }
            if (textGlyphRuns.Count > textRunOffset)
            {
                textGlyphRuns.RemoveRange(textRunOffset, textGlyphRuns.Count - textRunOffset);
            }
            if (textGlyphIndices.Count > textGlyphOffset)
            {
                textGlyphIndices.RemoveRange(textGlyphOffset, textGlyphIndices.Count - textGlyphOffset);
                textGlyphPositions.RemoveRange(textGlyphOffset, textGlyphPositions.Count - textGlyphOffset);
            }
            if (textFonts.Count > textFontOffset)
            {
                for (int i = textFonts.Count - 1; i >= textFontOffset; i--)
                {
                    textFontIndices.Remove(textFonts[i]);
                }
                textFonts.RemoveRange(textFontOffset, textFonts.Count - textFontOffset);
            }
            if (shxGlyphInstances.Count > shxGlyphOffset)
            {
                shxGlyphInstances.RemoveRange(shxGlyphOffset, shxGlyphInstances.Count - shxGlyphOffset);
            }

            throw;
        }
    }

    private static CadLineTypeElement CompileLineTypeElement(
        LineType.Segment segment,
        byte complexTypeFlags,
        List<CadLineTypeTextResource> textResources,
        List<CadLineTypeShapeResource> shapeResources,
        List<CadTextGlyphRun> textGlyphRuns,
        List<ushort> textGlyphIndices,
        List<Vector2> textGlyphPositions,
        List<TtfFont> textFonts,
        Dictionary<TtfFont, int> textFontIndices,
        List<CadShxGlyphInstance> shxGlyphInstances,
        ICadShxFontResolver? shxFontResolver,
        CadSnapshotOptions options)
    {
        int textResourceOffset = textResources.Count;
        int shapeResourceOffset = shapeResources.Count;
        int runOffsetBefore = textGlyphRuns.Count;
        int trueTypeGlyphOffsetBefore = textGlyphIndices.Count;
        int textFontOffset = textFonts.Count;
        int shxGlyphOffsetBefore = shxGlyphInstances.Count;
        if (complexTypeFlags == 0)
        {
            return new CadLineTypeElement(segment.Length, complexTypeFlags);
        }

        if (!double.IsFinite(segment.Scale) || segment.Scale == 0.0 ||
            !double.IsFinite(segment.Rotation) ||
            !double.IsFinite(segment.Offset.X) ||
            !double.IsFinite(segment.Offset.Y))
        {
            throw new ArgumentException(
                "Complex linetype scale, rotation, and offsets must be finite and scale must be non-zero.");
        }

        CadLineTypeRotationMode rotationMode =
            segment.Flags.HasFlag(LineTypeShapeFlags.RotationIsAbsolute)
                ? CadLineTypeRotationMode.Absolute
                : CadLineTypeRotationMode.Relative;
        CadLineTypeElement Unresolved() => new(
            segment.Length,
            complexTypeFlags,
            CadLineTypeElementKind.UnresolvedComplex,
            rotationMode,
            segment.Rotation,
            segment.Offset.X,
            segment.Offset.Y,
            -1);

        if (segment.Length != 0.0 || segment.IsText == segment.IsShape || segment.Style is null)
        {
            return Unresolved();
        }

        try
        {
            if (segment.IsShape)
            {
                if (shxFontResolver is null || segment.ShapeNumber <= 0)
                {
                    return Unresolved();
                }

                TextStyle style = segment.Style;
                CadShxFontResolution resolution = shxFontResolver.Resolve(new CadShxFontRequest(
                    style.Name,
                    style.Filename,
                    style.BigFontFilename));
                CadShxGlyphCache? cache = resolution.GlyphCache;
                if (cache is null)
                {
                    return Unresolved();
                }

                CadShxGlyph glyph = cache.GetGlyph(checked((ushort)segment.ShapeNumber));
                int resourceIndex = shapeResources.Count;
                shapeResources.Add(new CadLineTypeShapeResource(
                    glyph,
                    segment.Scale,
                    resolution.IsSubstitution));
                return new CadLineTypeElement(
                    segment.Length,
                    complexTypeFlags,
                    CadLineTypeElementKind.ShxShape,
                    rotationMode,
                    segment.Rotation,
                    segment.Offset.X,
                    segment.Offset.Y,
                    resourceIndex);
            }

            string source = segment.Text;
            if (source.Length == 0 || source.IndexOfAny(['\r', '\n']) >= 0 ||
                source.Length > options.MaxTextCodeUnitsPerEntity)
            {
                return Unresolved();
            }

            TextStyle textStyle = segment.Style;
            if (!string.IsNullOrWhiteSpace(textStyle.BigFontFilename))
            {
                return Unresolved();
            }
            if (!double.IsFinite(textStyle.Height) || textStyle.Height < 0.0 ||
                !double.IsFinite(textStyle.Width) || textStyle.Width <= 0.0 ||
                !double.IsFinite(textStyle.ObliqueAngle) ||
                Math.Abs(textStyle.ObliqueAngle) >= Math.PI * 0.5)
            {
                return Unresolved();
            }

            double height = textStyle.Height == 0.0
                ? segment.Scale
                : textStyle.Height * segment.Scale;
            if (!double.IsFinite(height) || height == 0.0)
            {
                return Unresolved();
            }

            bool usesShx = textStyle.IsShapeFile ||
                textStyle.Filename.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
            int resource = textResources.Count;
            if (usesShx)
            {
                if (shxFontResolver is null)
                {
                    return Unresolved();
                }
                CadShxFontResolution resolution = shxFontResolver.Resolve(new CadShxFontRequest(
                    textStyle.Name,
                    textStyle.Filename,
                    textStyle.BigFontFilename));
                CadShxGlyphCache? cache = resolution.GlyphCache;
                if (cache is null || !cache.Font.IsTextFont || cache.Font.Above == 0)
                {
                    return Unresolved();
                }
                var layout = new CadShxTextLayout(
                    source,
                    cache,
                    CadShxOrientation.Horizontal,
                    new CadShxTextLayoutOptions
                    {
                        MaxCodeUnits = options.MaxTextCodeUnitsPerEntity,
                        MaxGlyphs = options.MaxTextGlyphs,
                    });
                ReadOnlySpan<CadShxGlyphPlacement> placements = layout.Glyphs.Span;
                if (placements.Length > options.MaxTextGlyphs -
                    textGlyphIndices.Count - shxGlyphInstances.Count)
                {
                    throw new CadSnapshotExpansionLimitException(
                        $"Retained linetype text glyph count exceeds the configured document limit of {options.MaxTextGlyphs}.");
                }
                int glyphOffset = shxGlyphInstances.Count;
                for (int i = 0; i < placements.Length; i++)
                {
                    CadShxGlyphPlacement placement = placements[i];
                    if (placement.Decorations != CadShxTextDecoration.None)
                    {
                        return Unresolved();
                    }
                }
                for (int i = 0; i < placements.Length; i++)
                {
                    CadShxGlyphPlacement placement = placements[i];
                    shxGlyphInstances.Add(new CadShxGlyphInstance(
                        placement.Glyph,
                        placement.Origin.X,
                        placement.Origin.Y));
                }
                double yScale = height / cache.Font.Above;
                textResources.Add(new CadLineTypeTextResource(
                    CadLineTypeElementKind.ShxText,
                    glyphOffset,
                    placements.Length,
                    0,
                    0,
                    yScale * textStyle.Width,
                    yScale,
                    textStyle.ObliqueAngle,
                    textStyle.MirrorFlag.HasFlag(TextMirrorFlag.Backward),
                    textStyle.MirrorFlag.HasFlag(TextMirrorFlag.UpsideDown),
                    resolution.IsSubstitution));
                return new CadLineTypeElement(
                    segment.Length,
                    complexTypeFlags,
                    CadLineTypeElementKind.ShxText,
                    rotationMode,
                    segment.Rotation,
                    segment.Offset.X,
                    segment.Offset.Y,
                    resource);
            }

            ICadTextFontResolver? textResolver = options.TextFontResolver;
            if (textResolver is null ||
                textStyle.Flags.HasFlag(StyleFlags.VerticalText))
            {
                return Unresolved();
            }
            CadTextFontResolution fontResolution = textResolver.Resolve(new CadTextFontRequest(
                textStyle.Name,
                textStyle.Filename,
                textStyle.BigFontFilename,
                textStyle.TrueType.HasFlag(FontFlags.Bold),
                textStyle.TrueType.HasFlag(FontFlags.Italic)));
            TtfFont? font = fontResolution.Font;
            if (font is null || font.UnitsPerEm == 0)
            {
                return Unresolved();
            }
            DecodedTextContent decoded = DecodeTextContent(source);
            if (decoded.Decorations is not null)
            {
                return Unresolved();
            }
            var textLayout = new TextLayout(decoded.Text, font, 1.0f, float.PositiveInfinity);
            if (textLayout.Glyphs.Count == 0)
            {
                return Unresolved();
            }
            if (textLayout.Glyphs.Count > options.MaxTextGlyphs -
                textGlyphIndices.Count - shxGlyphInstances.Count)
            {
                throw new CadSnapshotExpansionLimitException(
                    $"Retained linetype text glyph count exceeds the configured document limit of {options.MaxTextGlyphs}.");
            }

            int trueTypeGlyphOffset = textGlyphIndices.Count;
            int runOffset = textGlyphRuns.Count;
            TtfFont? runFont = null;
            int currentRunOffset = 0;
            double ascent = (double)font.Ascender / font.UnitsPerEm;
            for (int i = 0; i < textLayout.Glyphs.Count; i++)
            {
                TextRunGlyph glyph = textLayout.Glyphs[i];
                TtfFont glyphFont = glyph.Font ?? font;
                if (runFont is not null && !ReferenceEquals(runFont, glyphFont))
                {
                    textGlyphRuns.Add(new CadTextGlyphRun(
                        trueTypeGlyphOffset + currentRunOffset,
                        i - currentRunOffset,
                        InternTextFont(runFont, textFonts, textFontIndices)));
                    currentRunOffset = i;
                }
                runFont = glyphFont;
                textGlyphIndices.Add(glyph.GlyphIndex);
                textGlyphPositions.Add(new Vector2(
                    glyph.Position.X,
                    checked((float)(glyph.Position.Y - ascent))));
            }
            if (runFont is not null)
            {
                textGlyphRuns.Add(new CadTextGlyphRun(
                    trueTypeGlyphOffset + currentRunOffset,
                    textLayout.Glyphs.Count - currentRunOffset,
                    InternTextFont(runFont, textFonts, textFontIndices)));
            }
            textResources.Add(new CadLineTypeTextResource(
                CadLineTypeElementKind.TrueTypeText,
                trueTypeGlyphOffset,
                textLayout.Glyphs.Count,
                runOffset,
                textGlyphRuns.Count - runOffset,
                height * textStyle.Width,
                height,
                textStyle.ObliqueAngle,
                textStyle.MirrorFlag.HasFlag(TextMirrorFlag.Backward),
                textStyle.MirrorFlag.HasFlag(TextMirrorFlag.UpsideDown),
                fontResolution.IsSubstitution));
            return new CadLineTypeElement(
                segment.Length,
                complexTypeFlags,
                CadLineTypeElementKind.TrueTypeText,
                rotationMode,
                segment.Rotation,
                segment.Offset.X,
                segment.Offset.Y,
                resource);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or NotSupportedException or
                KeyNotFoundException or ArgumentOutOfRangeException or
                ArithmeticException)
        {
            if (textResources.Count > textResourceOffset)
            {
                textResources.RemoveRange(textResourceOffset, textResources.Count - textResourceOffset);
            }
            if (shapeResources.Count > shapeResourceOffset)
            {
                shapeResources.RemoveRange(shapeResourceOffset, shapeResources.Count - shapeResourceOffset);
            }
            if (textGlyphRuns.Count > runOffsetBefore)
            {
                textGlyphRuns.RemoveRange(runOffsetBefore, textGlyphRuns.Count - runOffsetBefore);
            }
            if (textGlyphIndices.Count > trueTypeGlyphOffsetBefore)
            {
                textGlyphIndices.RemoveRange(
                    trueTypeGlyphOffsetBefore,
                    textGlyphIndices.Count - trueTypeGlyphOffsetBefore);
                textGlyphPositions.RemoveRange(
                    trueTypeGlyphOffsetBefore,
                    textGlyphPositions.Count - trueTypeGlyphOffsetBefore);
            }
            if (textFonts.Count > textFontOffset)
            {
                for (int i = textFonts.Count - 1; i >= textFontOffset; i--)
                {
                    textFontIndices.Remove(textFonts[i]);
                }
                textFonts.RemoveRange(textFontOffset, textFonts.Count - textFontOffset);
            }
            if (shxGlyphInstances.Count > shxGlyphOffsetBefore)
            {
                shxGlyphInstances.RemoveRange(
                    shxGlyphOffsetBefore,
                    shxGlyphInstances.Count - shxGlyphOffsetBefore);
            }
            return Unresolved();
        }
    }

    private static CadResolvedStyle ResolveStyle(
        Entity entity,
        Layer effectiveLayer,
        CadResolvedStyle? byBlock,
        CadSnapshotOptions options,
        double globalLineTypeScale)
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
        LineType lineType = entity.LineType.Name.Equals(
            LineType.ByLayerName,
            StringComparison.OrdinalIgnoreCase)
            ? effectiveLayer.LineType
            : entity.LineType.Name.Equals(
                LineType.ByBlockName,
                StringComparison.OrdinalIgnoreCase)
                ? byBlock?.LineType ?? LineType.Continuous
                : entity.LineType;
        short transparency = entity.Transparency.IsByLayer
            ? (short)0
            : entity.Transparency.IsByBlock
                ? byBlock?.Transparency ?? (short)0
                : entity.Transparency.Value;
        if (!double.IsFinite(entity.LineTypeScale) || entity.LineTypeScale <= 0.0)
        {
            throw new ArgumentException("Entity linetype scale must be finite and positive.");
        }

        double effectiveLineTypeScale = entity.LineTypeScale * globalLineTypeScale;
        if (!double.IsFinite(effectiveLineTypeScale) || effectiveLineTypeScale <= 0.0)
        {
            throw new ArgumentException(
                "The product of drawing and entity linetype scales must be finite and positive.");
        }

        return new CadResolvedStyle(
            color,
            lineWeight,
            lineType,
            transparency,
            effectiveLineTypeScale,
            options.DefaultLineWeightMillimeters);
    }

    private static bool IsContinuousLineTypeName(string name) =>
        name.Equals(LineType.ContinuousName, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(LineType.ByLayerName, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(LineType.ByBlockName, StringComparison.OrdinalIgnoreCase);

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
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxLineTypePatterns,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxLineTypeElements,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxHatchLoops,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxHatchSegments,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxHatchPatterns,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxHatchPatternFamilies,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxHatchPatternDashes,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxHatchTopologyVisits,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxHatchSplineSourceValues,
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
        LineType LineType,
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
