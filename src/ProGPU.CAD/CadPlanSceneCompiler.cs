using System.Buffers;
using System.Numerics;
using ProGPU.Backend;
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
    public CadPrintLineWeightMode LineWeightMode { get; init; } =
        CadPrintLineWeightMode.ObjectLineWeights;
    public bool IncludeNonPlottableLayers { get; init; } = true;
    public bool IncludeViewportFrames { get; init; } = true;
    public bool ReportDeferredConstructionGeometry { get; init; } = true;
    public bool ReportDeferredPointMarkers { get; init; } = true;
    public IReadOnlyCollection<string>? ExcludedLayerNames { get; init; }
    public int MaxLineTypeFigures { get; init; } = DefaultMaxLineTypeFigures;
    public int MaxLineTypePatternSteps { get; init; } = DefaultMaxLineTypePatternSteps;
    public int MaxLineTypeSourceSegments { get; init; } = DefaultMaxLineTypeSourceSegments;
    public int MaxLineTypeArcMapsPerEntity { get; init; } = DefaultMaxLineTypeArcMapsPerEntity;
    public int MaxLineTypePlacements { get; init; } = DefaultMaxLineTypePlacements;
    public int MaxHatchPatternAuxiliaryRecords { get; init; } =
        DefaultMaxHatchPatternAuxiliaryRecords;
    public ICadRasterImageSourceResolver? RasterImageSourceResolver { get; init; }
    public WgpuContext? RasterImageContext { get; init; }
}

public readonly record struct CadPlanSceneStatistics(
    int RecordedEntityCount,
    int RecordedCommandCount,
    int UnsupportedLineTypeCount,
    int LoweredLineTypeEntityCount,
    int LoweredLineTypeFigureCount,
    int LoweredLineTypePlacementCount,
    int LineTypePatternStepCount,
    int LineTypeSourceSegmentCount)
{
    public int UnsupportedRasterImageCount { get; init; }
    public int ModelerGeometryWireframeCount { get; init; }
    public int DeferredModelerSurfaceCount { get; init; }

    public CadPlanSceneStatistics(
        int recordedEntityCount,
        int recordedCommandCount,
        int unsupportedLineTypeCount,
        int loweredLineTypeEntityCount,
        int loweredLineTypeFigureCount,
        int loweredLineTypePlacementCount,
        int lineTypePatternStepCount,
        int lineTypeSourceSegmentCount,
        int unsupportedRasterImageCount)
        : this(
            recordedEntityCount,
            recordedCommandCount,
            unsupportedLineTypeCount,
            loweredLineTypeEntityCount,
            loweredLineTypeFigureCount,
            loweredLineTypePlacementCount,
            lineTypePatternStepCount,
            lineTypeSourceSegmentCount)
    {
        UnsupportedRasterImageCount = unsupportedRasterImageCount;
    }
}

/// <summary>A retained top/WCS-XY projection ready for ordinary ProGPU compilation.</summary>
public sealed class CadRecordedPlanScene : IDisposable
{
    private readonly CadDiagnostic[] _diagnostics;

    public ulong ContentGeneration { get; }
    public CadPoint3D RebaseOrigin { get; }
    public DrawingContext DrawingContext { get; }
    public CadPlanSceneStatistics Statistics { get; }
    public ReadOnlyMemory<CadDiagnostic> Diagnostics => _diagnostics;

    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Freezes the recorded CAD commands and side buffers into an independently
    /// owned picture suitable for repeated camera-only replay.
    /// </summary>
    public GpuPicture CreatePicture()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var recorder = new GpuPictureRecorder();
        DrawingContext target = recorder.BeginRecording(new Rect(0, 0, 1, 1));
        try
        {
            target.Append(DrawingContext);
            return recorder.EndRecording();
        }
        catch
        {
            target.Clear();
            throw;
        }
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

    /// <summary>
    /// Releases texture leases held by the mutable recording after callers have
    /// created their independently leased <see cref="GpuPicture"/> snapshots.
    /// </summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }
        IsDisposed = true;
        DrawingContext.Clear();
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
/// Raster IMAGE compilation adds O(I + V) work for I visible instances and V
/// retained clip vertices. Resource resolution and lease acquisition occur once
/// while recording; camera-only picture replay performs no decode or upload.
/// A later camera or viewport change can reuse the recorded scene.
/// </remarks>
public sealed class CadPlanSceneCompiler
{
    private const double TwoPi = Math.PI * 2.0;

    private sealed class RecordingLeaseGuard(DrawingContext context) : IDisposable
    {
        private bool _completed;

        public void Complete() => _completed = true;

        public void Dispose()
        {
            if (!_completed)
            {
                context.Clear();
            }
        }
    }

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
        var viewportBoundaryHandles = new HashSet<ulong>();
        foreach (CadViewportPrimitive viewport in snapshot.Viewports.Span)
        {
            if (viewport.BoundaryHandle != 0)
            {
                viewportBoundaryHandles.Add(viewport.BoundaryHandle);
            }
        }
        HashSet<string>? excludedLayerNames = options.ExcludedLayerNames is { Count: > 0 }
            ? new HashSet<string>(
                options.ExcludedLayerNames,
                StringComparer.OrdinalIgnoreCase)
            : null;
        ICadRasterImageSourceResolver? rasterImageResolver =
            options.RasterImageSourceResolver is CadRasterImageCatalog catalog
                ? catalog.CreateResolverSnapshot()
                : options.RasterImageSourceResolver;
        var context = new DrawingContext();
        context.EnsureCommandCapacity(checked(
            Math.Max(
                0,
                entities.Length - snapshot.ConstructionLines.Length - snapshot.Meshes3D.Length) +
            snapshot.Wipeouts.Length +
            Math.Max(0, snapshot.TextGlyphRuns.Length - snapshot.Texts.Length) +
            snapshot.TextDecorations.Length +
            snapshot.MTextGlyphRuns.Length +
            snapshot.MTextBackgrounds.Length +
            snapshot.MTextDecorations.Length +
            snapshot.MTextStrokes.Length +
            snapshot.ShxGlyphInstances.Length +
            snapshot.ShxDecorationSegments.Length +
            snapshot.MLineStrokes.Length +
            snapshot.MLineFillTriangles.Length));
        Pen[] pens = CreatePens(styles, options);
        var widePolylinePens = new Dictionary<(int StyleIndex, double Width), Pen>();
        var mtextBrushes = new Dictionary<uint, Brush>();
        var diagnostics = new List<CadDiagnostic>();
        var warnedLineTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnedLineTypeSubstitutions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnedRasterImageResources = new HashSet<int>();
        bool warnedConstructionGeometry = false;
        bool warnedPointMarkers = false;
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
        int unsupportedRasterImages = 0;
        int modelerGeometryWireframes = 0;
        int deferredModelerSurfaces = 0;

        using var recordingLeaseGuard = new RecordingLeaseGuard(context);
        foreach (CadEntityHeader entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entity.IsVisible || !layers[entity.LayerIndex].IsVisible ||
                (!options.IncludeNonPlottableLayers &&
                    !layers[entity.LayerIndex].IsPlottable))
            {
                continue;
            }
            if (excludedLayerNames?.Contains(layers[entity.LayerIndex].Name) == true)
            {
                continue;
            }
            if (entity.Kind == CadEntityKind.Viewport)
            {
                CadViewportPrimitive viewport =
                    snapshot.Viewports.Span[entity.PrimitiveIndex];
                if (viewport.RepresentsPaper ||
                    viewport.HasNonRectangularBoundary ||
                    !options.IncludeViewportFrames)
                {
                    continue;
                }
            }
            if (!options.IncludeViewportFrames &&
                viewportBoundaryHandles.Contains(entity.Handle))
            {
                continue;
            }
            if (entity.Kind is CadEntityKind.Ray or CadEntityKind.XLine)
            {
                if (options.ReportDeferredConstructionGeometry &&
                    !warnedConstructionGeometry)
                {
                    diagnostics.Add(new CadDiagnostic(
                        CadDiagnosticSeverity.Information,
                        "CADSCENE004",
                        "Unbounded RAY/XLINE geometry requires CadConstructionSceneCompiler and an explicit plan clip."));
                    warnedConstructionGeometry = true;
                }
                continue;
            }
            if (entity.Kind == CadEntityKind.Mesh3D)
            {
                // The exact finite edge representation is already recorded in
                // this plan scene. Filled faces belong to the depth-aware 3D scene.
                continue;
            }

            CadStrokeStyle style = styles[entity.StyleIndex];
            Pen pen = pens[entity.StyleIndex];
            bool isWidePolyline = TryGetWidePolyline(
                snapshot,
                entity,
                out CadPolylinePrimitive widePolyline);
            Pen geometryPen = isWidePolyline
                ? GetWidePolylinePen(
                    entity.StyleIndex,
                    widePolyline.ConstantWidth,
                    pen,
                    widePolylinePens)
                : pen;
            bool recordedLineType = false;
            CadLineTypeLoweringResult? deferredImageFrame = null;
            CadLineTypePattern? deferredImageFramePattern = null;
            bool usesWipeoutFrame = entity.Kind == CadEntityKind.Wipeout &&
                snapshot.Wipeouts.Span[entity.PrimitiveIndex].DrawFrame;
            bool usesRasterImageFrame = entity.Kind == CadEntityKind.RasterImage &&
                snapshot.RasterImages.Span[entity.PrimitiveIndex].DrawFrame;
            if (UsesStroke(entity.Kind) || usesWipeoutFrame || usesRasterImageFrame)
            {
                CadLineTypePattern pattern = lineTypePatterns[style.LineTypePatternIndex];
                if (isWidePolyline &&
                    pattern.Kind is (
                        CadLineTypePatternKind.Simple or
                        CadLineTypePatternKind.Complex))
                {
                    AddUnsupportedLineTypeDiagnostic(
                        pattern.Name,
                        "wide-polyline dash, dot, and embedded-content caps require a dedicated filled-width linetype contract",
                        "CADSCENE009");
                }
                else if (pattern.Kind is CadLineTypePatternKind.Simple or CadLineTypePatternKind.Complex)
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
                        if (entity.Kind is CadEntityKind.Wipeout or CadEntityKind.RasterImage)
                        {
                            deferredImageFrame = result;
                            deferredImageFramePattern = pattern;
                        }
                        else
                        {
                            if (result.Path is not null)
                            {
                                context.DrawPath(
                                    null,
                                    geometryPen,
                                    result.Path,
                                    result.Transform);
                            }
                            RecordLineTypeSplineFragments(context, pen, result);
                            RecordLineTypePlacements(
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

            if (recordedLineType && entity.Kind is not
                (CadEntityKind.Wipeout or CadEntityKind.RasterImage))
            {
                if (entity.Kind == CadEntityKind.Leader)
                {
                    RecordLeaderArrow(
                        context,
                        pen.Brush,
                        snapshot,
                        snapshot.Leaders.Span[entity.PrimitiveIndex]);
                }
                else if (entity.Kind == CadEntityKind.MultiLeader)
                {
                    RecordMultiLeaderArrow(
                        context,
                        pen.Brush,
                        snapshot,
                        snapshot.MultiLeaders.Span[entity.PrimitiveIndex]);
                }
                recorded++;
                continue;
            }

            switch (entity.Kind)
            {
                case CadEntityKind.Point:
                    CadPointPrimitive point =
                        snapshot.Points.Span[entity.PrimitiveIndex];
                    if (point.DisplayMode == 0)
                    {
                        RecordPoint(
                            context,
                            pen.Brush,
                            point,
                            snapshot.RebaseOrigin);
                    }
                    else if (options.ReportDeferredPointMarkers && !warnedPointMarkers)
                    {
                        diagnostics.Add(new CadDiagnostic(
                            CadDiagnosticSeverity.Information,
                            "CADSCENE005",
                            "PDMODE marker geometry requires CadPointMarkerSceneCompiler and an explicit finite point-marker view."));
                        warnedPointMarkers = true;
                    }
                    break;
                case CadEntityKind.Line:
                    RecordLine(context, pen, snapshot.Lines.Span[entity.PrimitiveIndex], snapshot.RebaseOrigin);
                    break;
                case CadEntityKind.MLine:
                    RecordMLine(
                        context,
                        pens,
                        snapshot,
                        snapshot.MLines.Span[entity.PrimitiveIndex],
                        mtextBrushes,
                        lineTypePatterns,
                        options,
                        diagnostics,
                        warnedLineTypes,
                        warnedLineTypeSubstitutions,
                        ref unsupportedLineTypes,
                        ref loweredLineTypeEntities,
                        ref loweredLineTypeFigures,
                        ref loweredLineTypePlacements,
                        ref lineTypeFigureBudgetUsed,
                        ref lineTypePatternSteps,
                        ref lineTypeSourceSegments,
                        ref lineTypePlacementBudgetUsed);
                    break;
                case CadEntityKind.Leader:
                    RecordLeader(
                        context,
                        pen,
                        snapshot,
                        snapshot.Leaders.Span[entity.PrimitiveIndex]);
                    break;
                case CadEntityKind.MultiLeader:
                    RecordMultiLeader(
                        context,
                        pen,
                        snapshot,
                        snapshot.MultiLeaders.Span[entity.PrimitiveIndex]);
                    break;
                case CadEntityKind.Tolerance:
                    RecordTolerance(
                        context,
                        pen,
                        snapshot,
                        snapshot.Tolerances.Span[entity.PrimitiveIndex]);
                    break;
                case CadEntityKind.Viewport:
                    RecordViewportFrame(
                        context,
                        pen,
                        snapshot.Viewports.Span[entity.PrimitiveIndex],
                        snapshot.RebaseOrigin);
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
                    RecordSolid(context, pen, snapshot.Faces.Span[entity.PrimitiveIndex], snapshot.RebaseOrigin);
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
                case CadEntityKind.Wipeout:
                    RecordWipeout(
                        context,
                        pen,
                        GetMTextBrush(
                            mtextBrushes,
                            snapshot.Wipeouts.Span[entity.PrimitiveIndex].MaskColor.Red,
                            snapshot.Wipeouts.Span[entity.PrimitiveIndex].MaskColor.Green,
                            snapshot.Wipeouts.Span[entity.PrimitiveIndex].MaskColor.Blue,
                            snapshot.Wipeouts.Span[entity.PrimitiveIndex].MaskColor.Alpha),
                        snapshot,
                        snapshot.Wipeouts.Span[entity.PrimitiveIndex],
                        drawContinuousFrame: deferredImageFrame is null);
                    RecordDeferredImageFrame(
                        context,
                        pen,
                        snapshot,
                        style,
                        deferredImageFrame,
                        deferredImageFramePattern);
                    break;
                case CadEntityKind.RasterImage:
                    CadRasterImagePrimitive rasterImage =
                        snapshot.RasterImages.Span[entity.PrimitiveIndex];
                    bool imageAvailable = RecordRasterImage(
                        context,
                        pen,
                        style,
                        snapshot,
                        rasterImage,
                        rasterImageResolver,
                        options.RasterImageContext,
                        drawContinuousFrame: deferredImageFrame is null);
                    if (!imageAvailable &&
                        warnedRasterImageResources.Add(rasterImage.ResourceIndex))
                    {
                        unsupportedRasterImages++;
                        CadRasterImageResource resource =
                            snapshot.RasterImageResources.Span[rasterImage.ResourceIndex];
                        diagnostics.Add(new CadDiagnostic(
                            CadDiagnosticSeverity.Information,
                            "CADSCENE006",
                            !resource.IsLoaded
                                ? $"Raster IMAGEDEF '{resource.FileName}' is marked unloaded; its frame remains retained."
                                : $"Raster IMAGEDEF '{resource.FileName}' has no available typed texture lease; its frame remains retained."));
                    }
                    RecordDeferredImageFrame(
                        context,
                        pen,
                        snapshot,
                        style,
                        deferredImageFrame,
                        deferredImageFramePattern);
                    break;
                case CadEntityKind.ModelerGeometry:
                    CadModelerGeometryPrimitive modelerGeometry =
                        snapshot.ModelerGeometries.Span[entity.PrimitiveIndex];
                    deferredModelerSurfaces++;
                    if (RecordModelerGeometry(
                            context,
                            pen,
                            snapshot,
                            modelerGeometry))
                    {
                        modelerGeometryWireframes++;
                        diagnostics.Add(new CadDiagnostic(
                            CadDiagnosticSeverity.Information,
                            "CADSCENE007",
                            $"{modelerGeometry.Kind} handle {entity.Handle:X} is retained as batched display-wire topology; ACIS face tessellation remains deferred."));
                    }
                    else
                    {
                        diagnostics.Add(new CadDiagnostic(
                            CadDiagnosticSeverity.Information,
                            "CADSCENE008",
                            $"{modelerGeometry.Kind} handle {entity.Handle:X} retains its byte-exact ACIS payload but has no display-wire topology; surface tessellation remains deferred."));
                    }
                    break;
                case CadEntityKind.Face3D:
                    RecordFace3D(context, pen, snapshot.Faces.Span[entity.PrimitiveIndex], snapshot.RebaseOrigin);
                    break;
                case CadEntityKind.Spline:
                    RecordSpline(context, pen, snapshot, snapshot.Splines.Span[entity.PrimitiveIndex]);
                    break;
                case CadEntityKind.LightweightPolyline:
                    RecordPolyline(
                        context,
                        geometryPen,
                        snapshot,
                        snapshot.Polylines.Span[entity.PrimitiveIndex]);
                    break;
                case CadEntityKind.Polyline2D:
                    RecordPolyline(
                        context,
                        geometryPen,
                        snapshot,
                        snapshot.Polylines.Span[entity.PrimitiveIndex]);
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
                case CadEntityKind.ShxShape:
                    RecordShxShape(
                        context,
                        pen,
                        snapshot,
                        snapshot.ShxShapes.Span[entity.PrimitiveIndex]);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown CAD entity kind {entity.Kind}.");
            }

            recorded++;
        }

        context.TrimRetainedCommandCapacity();
        var scene = new CadRecordedPlanScene(
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
                lineTypeSourceSegments,
                unsupportedRasterImages)
            {
                ModelerGeometryWireframeCount = modelerGeometryWireframes,
                DeferredModelerSurfaceCount = deferredModelerSurfaces,
            },
            diagnostics.ToArray());
        recordingLeaseGuard.Complete();
        return scene;

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
            float thickness = options.LineWeightMode ==
                    CadPrintLineWeightMode.DeviceHairline ||
                style.IsHairline
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

    private static bool TryGetWidePolyline(
        CadDocumentSnapshot snapshot,
        CadEntityHeader entity,
        out CadPolylinePrimitive polyline)
    {
        if (entity.Kind is CadEntityKind.LightweightPolyline or
                CadEntityKind.Polyline2D)
        {
            polyline = snapshot.Polylines.Span[entity.PrimitiveIndex];
            return polyline.IsWide;
        }

        polyline = default;
        return false;
    }

    private static Pen GetWidePolylinePen(
        int styleIndex,
        double width,
        Pen source,
        Dictionary<(int StyleIndex, double Width), Pen> cache)
    {
        var key = (styleIndex, width);
        if (cache.TryGetValue(key, out Pen? retained))
        {
            return retained;
        }

        retained = new Pen(
            source.Brush,
            checked((float)width),
            lineJoin: PenLineJoin.Bevel,
            startLineCap: PenLineCap.Flat,
            endLineCap: PenLineCap.Flat,
            dashCap: PenLineCap.Flat,
            strokeTransformMode: PenStrokeTransformMode.Normal);
        cache.Add(key, retained);
        return retained;
    }

    internal static void RecordLineTypePlacements(
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

    internal static bool HasLineTypeSubstitution(
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

    private static void RecordLeader(
        DrawingContext context,
        Pen pen,
        CadDocumentSnapshot snapshot,
        in CadLeaderPrimitive leader)
    {
        RecordSpline(
            context,
            pen,
            snapshot,
            snapshot.Splines.Span[leader.PathSplineIndex]);
        RecordLeaderArrow(context, pen.Brush, snapshot, leader);
    }

    private static void RecordLeaderArrow(
        DrawingContext context,
        Brush brush,
        CadDocumentSnapshot snapshot,
        in CadLeaderPrimitive leader)
    {
        if (!leader.HasDefaultArrow)
        {
            return;
        }

        var path = new PathGeometry { FillRule = FillRule.Nonzero };
        var figure = new PathFigure(
            Project(leader.ArrowTip, snapshot.RebaseOrigin),
            isClosed: true);
        figure.Segments.Add(new LineSegment(
            Project(leader.ArrowFirstBase, snapshot.RebaseOrigin)));
        figure.Segments.Add(new LineSegment(
            Project(leader.ArrowSecondBase, snapshot.RebaseOrigin)));
        path.Figures.Add(figure);
        context.DrawPath(brush, null, path);
    }

    private static void RecordMultiLeader(
        DrawingContext context,
        Pen pen,
        CadDocumentSnapshot snapshot,
        in CadMultiLeaderPrimitive leader)
    {
        RecordSpline(
            context,
            pen,
            snapshot,
            snapshot.Splines.Span[leader.PathSplineIndex]);
        RecordMultiLeaderArrow(context, pen.Brush, snapshot, leader);
    }

    private static void RecordMultiLeaderArrow(
        DrawingContext context,
        Brush brush,
        CadDocumentSnapshot snapshot,
        in CadMultiLeaderPrimitive leader)
    {
        if (!leader.HasDefaultArrow)
        {
            return;
        }

        var path = new PathGeometry { FillRule = FillRule.Nonzero };
        var figure = new PathFigure(
            Project(leader.ArrowTip, snapshot.RebaseOrigin),
            isClosed: true);
        figure.Segments.Add(new LineSegment(
            Project(leader.ArrowFirstBase, snapshot.RebaseOrigin)));
        figure.Segments.Add(new LineSegment(
            Project(leader.ArrowSecondBase, snapshot.RebaseOrigin)));
        path.Figures.Add(figure);
        context.DrawPath(brush, null, path);
    }

    private static void RecordTolerance(
        DrawingContext context,
        Pen pen,
        CadDocumentSnapshot snapshot,
        in CadTolerancePrimitive tolerance)
    {
        var path = new PathGeometry();
        ReadOnlySpan<CadToleranceStroke> strokes =
            snapshot.ToleranceStrokes.Span.Slice(
                tolerance.StrokeOffset,
                tolerance.StrokeCount);
        for (int index = 0; index < strokes.Length; index++)
        {
            CadToleranceStroke stroke = strokes[index];
            var figure = new PathFigure(
                Project(stroke.Start, snapshot.RebaseOrigin),
                isClosed: false);
            figure.Segments.Add(new LineSegment(
                Project(stroke.End, snapshot.RebaseOrigin)));
            path.Figures.Add(figure);
        }
        context.DrawPath(null, pen, path);
    }

    private static void RecordMLine(
        DrawingContext context,
        Pen[] pens,
        CadDocumentSnapshot snapshot,
        CadMLinePrimitive mline,
        Dictionary<uint, Brush> brushes,
        ReadOnlySpan<CadLineTypePattern> lineTypePatterns,
        CadPlanSceneOptions options,
        List<CadDiagnostic> diagnostics,
        HashSet<string> warnedLineTypes,
        HashSet<string> warnedLineTypeSubstitutions,
        ref int unsupportedLineTypes,
        ref int loweredLineTypeEntities,
        ref int loweredLineTypeFigures,
        ref int loweredLineTypePlacements,
        ref int lineTypeFigureBudgetUsed,
        ref int lineTypePatternSteps,
        ref int lineTypeSourceSegments,
        ref int lineTypePlacementBudgetUsed)
    {
        ReadOnlySpan<CadMLineFillTriangle> triangles =
            snapshot.MLineFillTriangles.Span.Slice(
                mline.FillTriangleOffset,
                mline.FillTriangleCount);
        if (!triangles.IsEmpty)
        {
            CadColor32 color = triangles[0].Color;
            var path = new PathGeometry { FillRule = FillRule.Nonzero };
            for (int index = 0; index < triangles.Length; index++)
            {
                CadMLineFillTriangle triangle = triangles[index];
                var figure = new PathFigure(
                    Project(triangle.First, snapshot.RebaseOrigin),
                    isClosed: true);
                figure.Segments.Add(new LineSegment(
                    Project(triangle.Second, snapshot.RebaseOrigin)));
                figure.Segments.Add(new LineSegment(
                    Project(triangle.Third, snapshot.RebaseOrigin)));
                path.Figures.Add(figure);
            }
            context.DrawPath(
                GetMTextBrush(brushes, color.Red, color.Green, color.Blue, color.Alpha),
                null,
                path);
        }

        ReadOnlySpan<CadMLineElementPath> elementPaths =
            snapshot.MLineElementPaths.Span.Slice(
                mline.ElementPathOffset,
                mline.ElementPathCount);
        bool loweredAnyElement = false;
        for (int elementIndex = 0; elementIndex < elementPaths.Length; elementIndex++)
        {
            CadMLineElementPath element = elementPaths[elementIndex];
            CadStrokeStyle style = snapshot.Styles.Span[element.StyleIndex];
            CadLineTypePattern pattern = lineTypePatterns[style.LineTypePatternIndex];
            if (pattern.Kind is CadLineTypePatternKind.Simple or CadLineTypePatternKind.Complex)
            {
                int remainingFigures = options.MaxLineTypeFigures - lineTypeFigureBudgetUsed;
                int remainingSteps = options.MaxLineTypePatternSteps - lineTypePatternSteps;
                int remainingSources = options.MaxLineTypeSourceSegments - lineTypeSourceSegments;
                int remainingPlacements = options.MaxLineTypePlacements - lineTypePlacementBudgetUsed;
                CadLineTypeLoweringResult result = CadLineTypeLowerer.LowerMLineElement(
                    snapshot,
                    element,
                    style,
                    pattern,
                    Math.Max(0, remainingFigures),
                    Math.Max(0, remainingSteps),
                    Math.Max(0, remainingSources),
                    Math.Max(0, remainingPlacements));
                lineTypeFigureBudgetUsed = checked(lineTypeFigureBudgetUsed +
                    Math.Min(Math.Max(0, remainingFigures), result.FigureCount));
                lineTypePatternSteps = checked(lineTypePatternSteps +
                    Math.Min(Math.Max(0, remainingSteps), result.PatternStepCount));
                lineTypeSourceSegments = checked(lineTypeSourceSegments +
                    Math.Min(Math.Max(0, remainingSources), result.SourceSegmentCount));
                lineTypePlacementBudgetUsed = checked(lineTypePlacementBudgetUsed +
                    Math.Min(Math.Max(0, remainingPlacements), result.PlacementCount));
                if (result.Status == CadLineTypeLoweringStatus.Lowered)
                {
                    if (result.Path is not null)
                    {
                        context.DrawPath(null, pens[element.StyleIndex], result.Path);
                    }
                    RecordLineTypePlacements(
                        context,
                        pens[element.StyleIndex],
                        snapshot,
                        style,
                        pattern,
                        result);
                    if (HasLineTypeSubstitution(snapshot, pattern) &&
                        warnedLineTypeSubstitutions.Add(pattern.Name))
                    {
                        diagnostics.Add(new CadDiagnostic(
                            CadDiagnosticSeverity.Warning,
                            "CADSCENE003",
                            $"Linetype '{pattern.Name}' uses a host-resolved text or SHX substitution."));
                    }
                    loweredAnyElement = true;
                    loweredLineTypeFigures = checked(loweredLineTypeFigures + result.FigureCount);
                    loweredLineTypePlacements = checked(loweredLineTypePlacements + result.PlacementCount);
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
                        _ => "an embedded text/shape resource is unresolved",
                    };
                    string key = $"{pattern.Name}\0{reason}";
                    if (warnedLineTypes.Add(key))
                    {
                        unsupportedLineTypes++;
                        diagnostics.Add(new CadDiagnostic(
                            CadDiagnosticSeverity.Warning,
                            "CADSCENE002",
                            $"Linetype '{pattern.Name}' is recorded as a continuous stroke because {reason}."));
                    }
                }
            }
            else if (pattern.Kind != CadLineTypePatternKind.Continuous)
            {
                string reason = pattern.Kind == CadLineTypePatternKind.Complex
                    ? "embedded text/shape elements require complex-linetype lowering"
                    : $"alignment '{pattern.Alignment}' is not the documented AutoCAD A alignment";
                string key = $"{pattern.Name}\0{reason}";
                if (warnedLineTypes.Add(key))
                {
                    unsupportedLineTypes++;
                    diagnostics.Add(new CadDiagnostic(
                        CadDiagnosticSeverity.Warning,
                        "CADSCENE001",
                        $"Linetype '{pattern.Name}' is recorded as a continuous stroke because {reason}."));
                }
            }
            ReadOnlySpan<CadMLineStroke> strokes = snapshot.MLineStrokes.Span.Slice(
                element.StrokeOffset,
                element.StrokeCount);
            var path = new PathGeometry();
            for (int index = 0; index < strokes.Length; index++)
            {
                CadMLineStroke stroke = strokes[index];
                var figure = new PathFigure(
                    Project(stroke.Start, snapshot.RebaseOrigin),
                    isClosed: false)
                {
                    IsFilled = false,
                };
                figure.Segments.Add(new LineSegment(
                    Project(stroke.End, snapshot.RebaseOrigin)));
                path.Figures.Add(figure);
            }
            context.DrawPath(null, pens[element.StyleIndex], path);
        }
        if (loweredAnyElement)
        {
            loweredLineTypeEntities++;
        }
    }

    private static void RecordPoint(
        DrawingContext context,
        Brush brush,
        CadPointPrimitive point,
        CadPoint3D origin)
    {
        Span<Vector2> positions = stackalloc Vector2[1];
        positions[0] = Project(point.Position, origin);
        context.DrawPointBatch(brush, positions, radius: 0.0f, round: true);
    }

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
        Pen pen,
        CadFacePrimitive face,
        CadPoint3D origin)
    {
        if (face.Extrusion != CadPoint3D.Zero)
        {
            RecordExtrudedSolid(context, pen, face, origin);
            return;
        }

        var path = new PathGeometry { FillRule = FillRule.EvenOdd };
        var figure = new PathFigure(Project(face.First, origin), isClosed: true);
        figure.Segments.Add(new LineSegment(Project(face.Second, origin)));
        figure.Segments.Add(new LineSegment(Project(face.Third, origin)));
        if (face.Fourth != face.Third)
        {
            figure.Segments.Add(new LineSegment(Project(face.Fourth, origin)));
        }

        path.Figures.Add(figure);
        context.DrawPath(pen.Brush, null, path);
    }

    private static void RecordExtrudedSolid(
        DrawingContext context,
        Pen pen,
        CadFacePrimitive face,
        CadPoint3D origin)
    {
        Span<CadPoint3D> contourPoints = stackalloc CadPoint3D[6];
        Span<int> contourLengths = stackalloc int[2];
        int contourCount = CadFaceSurfaceTopology.BuildSolidContours(
            face,
            contourPoints,
            contourLengths);
        if (contourCount == 0)
        {
            return;
        }

        var path = new PathGeometry();
        int pointOffset = 0;
        bool displacedInPlan = face.Extrusion.X != 0.0 || face.Extrusion.Y != 0.0;
        for (int contourIndex = 0; contourIndex < contourCount; contourIndex++)
        {
            int contourLength = contourLengths[contourIndex];
            ReadOnlySpan<CadPoint3D> contour = contourPoints.Slice(
                pointOffset,
                contourLength);
            for (int edge = 0; edge < contourLength; edge++)
            {
                CadPoint3D start = contour[edge];
                CadPoint3D end = contour[(edge + 1) % contourLength];
                AddProjectedFaceEdge(path, start, end, origin);
                if (displacedInPlan)
                {
                    AddProjectedFaceEdge(
                        path,
                        start + face.Extrusion,
                        end + face.Extrusion,
                        origin);
                }
            }
            pointOffset += contourLength;
        }

        if (displacedInPlan)
        {
            for (int pointIndex = 0; pointIndex < pointOffset; pointIndex++)
            {
                CadPoint3D point = contourPoints[pointIndex];
                bool duplicate = false;
                for (int previous = 0; previous < pointIndex; previous++)
                {
                    if (contourPoints[previous] == point)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                {
                    AddProjectedFaceEdge(
                        path,
                        point,
                        point + face.Extrusion,
                        origin);
                }
            }
        }

        if (path.Figures.Count != 0)
        {
            context.DrawPath(null, pen, path);
        }
    }

    private static void AddProjectedFaceEdge(
        PathGeometry path,
        CadPoint3D start,
        CadPoint3D end,
        CadPoint3D origin)
    {
        Vector2 projectedStart = Project(start, origin);
        Vector2 projectedEnd = Project(end, origin);
        if (projectedStart == projectedEnd)
        {
            return;
        }

        var figure = new PathFigure(projectedStart)
        {
            IsFilled = false,
        };
        figure.Segments.Add(new LineSegment(projectedEnd));
        path.Figures.Add(figure);
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

    private static void RecordWipeout(
        DrawingContext context,
        Pen pen,
        Brush maskBrush,
        CadDocumentSnapshot snapshot,
        CadWipeoutPrimitive wipeout,
        bool drawContinuousFrame)
    {
        CadPoint3D plane = CadPoint3D.Cross(wipeout.UVector, wipeout.VVector);
        double planeLength = plane.Length;
        bool alignedWithPlan =
            Math.Abs(plane.X) <= planeLength * 1e-10 &&
            Math.Abs(plane.Y) <= planeLength * 1e-10;
        bool drawMask = wipeout.DrawMask &&
            (wipeout.ShowWhenNotAligned || alignedWithPlan);
        bool drawFrame = wipeout.DrawFrame && drawContinuousFrame;
        if (!drawMask && !drawFrame)
        {
            return;
        }

        ReadOnlySpan<CadWipeoutClipPoint> clip = wipeout.IsClipped
            ? snapshot.WipeoutClipPoints.Span.Slice(
                wipeout.ClipPointOffset,
                wipeout.ClipPointCount)
            : ReadOnlySpan<CadWipeoutClipPoint>.Empty;
        Matrix4x4 transform = CreateProjectionTransform(
            wipeout.Origin,
            wipeout.UVector,
            wipeout.VVector,
            snapshot.RebaseOrigin);
        bool frameMatchesMask = !wipeout.IsInverted;
        if (drawMask)
        {
            var maskPath = new PathGeometry { FillRule = FillRule.EvenOdd };
            if (!wipeout.IsClipped || wipeout.IsInverted)
            {
                AddWipeoutRectangle(maskPath, wipeout.Width, wipeout.Height);
            }
            if (wipeout.IsClipped)
            {
                AddWipeoutClip(maskPath, clip);
            }
            context.DrawPath(
                maskBrush,
                drawFrame && frameMatchesMask ? pen : null,
                maskPath,
                transform);
        }

        if (drawFrame && (!drawMask || !frameMatchesMask))
        {
            var framePath = new PathGeometry();
            if (wipeout.IsClipped)
            {
                AddWipeoutClip(framePath, clip);
            }
            else
            {
                AddWipeoutRectangle(framePath, wipeout.Width, wipeout.Height);
            }
            context.DrawPath(null, pen, framePath, transform);
        }
    }

    private static bool RecordRasterImage(
        DrawingContext context,
        Pen pen,
        in CadStrokeStyle style,
        CadDocumentSnapshot snapshot,
        in CadRasterImagePrimitive image,
        ICadRasterImageSourceResolver? resolver,
        WgpuContext? requiredContext,
        bool drawContinuousFrame)
    {
        CadPoint3D plane = CadPoint3D.Cross(image.UVector, image.VVector);
        double planeLength = plane.Length;
        bool alignedWithPlan =
            Math.Abs(plane.X) <= planeLength * 1e-10 &&
            Math.Abs(plane.Y) <= planeLength * 1e-10;
        bool drawImage = image.DrawImage &&
            (image.ShowWhenNotAligned || alignedWithPlan);
        bool drawFrame = image.DrawFrame && drawContinuousFrame;
        if (!drawImage && !drawFrame)
        {
            return true;
        }

        ReadOnlySpan<CadWipeoutClipPoint> clip = image.IsClipped
            ? snapshot.RasterImageClipPoints.Span.Slice(
                image.ClipPointOffset,
                image.ClipPointCount)
            : ReadOnlySpan<CadWipeoutClipPoint>.Empty;
        Matrix4x4 clipTransform = CreateProjectionTransform(
            image.Origin,
            image.UVector,
            image.VVector,
            snapshot.RebaseOrigin);
        bool imageAvailable = !drawImage;
        if (drawImage)
        {
            CadRasterImageResource resource =
                snapshot.RasterImageResources.Span[image.ResourceIndex];
            var request = new CadRasterImageRequest(snapshot.SourceName, resource);
            if (resource.IsLoaded && resolver is not null &&
                resolver.TryResolve(request, out IProGpuTextureLeaseSource source))
            {
                GpuTexture texture;
                bool retained = requiredContext is null
                    ? context.TryRetainTexture(source, out texture)
                    : context.TryRetainTexture(source, requiredContext, out texture);
                if (retained)
                {
                    PathGeometry? clipPath = null;
                    if (image.IsClipped)
                    {
                        clipPath = new PathGeometry { FillRule = FillRule.EvenOdd };
                        if (image.IsInverted)
                        {
                            AddRasterImageRectangle(clipPath, image.Width, image.Height);
                        }
                        AddRasterImageClip(clipPath, clip);
                        context.PushGeometryClip(clipPath, clipTransform);
                    }
                    bool pushedOpacity = style.Alpha != byte.MaxValue;
                    if (pushedOpacity)
                    {
                        context.PushOpacity(style.Alpha / 255.0f);
                    }

                    Matrix4x4 imageTransform = CreateProjectionTransform(
                        image.Origin + (image.VVector * image.Height),
                        image.UVector,
                        image.VVector * -1.0,
                        snapshot.RebaseOrigin);
                    var destination = new Rect(
                        0.0f,
                        0.0f,
                        ToFloat(image.Width),
                        ToFloat(image.Height));
                    var sourceRect = new Rect(
                        0.0f,
                        0.0f,
                        texture.Width,
                        texture.Height);
                    float brightness = (image.Brightness - 50) / 50.0f;
                    float contrast = image.Contrast / 50.0f;
                    bool needsEffect = image.Brightness != 50 ||
                        image.Contrast != 50 || image.Fade != 0 ||
                        !image.TransparencyIsOn;
                    TextureSamplingMode sampling = image.IsHighQuality
                        ? TextureSamplingMode.Linear
                        : TextureSamplingMode.Nearest;
                    if (needsEffect)
                    {
                        context.DrawImageWithEffect(
                            texture,
                            destination,
                            brightness: brightness,
                            contrast: contrast,
                            sourceRect: sourceRect,
                            samplingMode: sampling,
                            colorMatrix: CreateRasterImageColorMatrix(image),
                            transform: imageTransform);
                    }
                    else
                    {
                        context.DrawTexture(
                            texture,
                            destination,
                            sourceRect,
                            imageTransform,
                            sampling);
                    }

                    if (pushedOpacity)
                    {
                        context.PopOpacity();
                    }
                    if (clipPath is not null)
                    {
                        context.PopGeometryClip();
                    }
                    imageAvailable = true;
                }
            }
        }

        if (drawFrame)
        {
            var framePath = new PathGeometry();
            if (image.IsClipped)
            {
                AddRasterImageClip(framePath, clip);
            }
            else
            {
                AddRasterImageRectangle(framePath, image.Width, image.Height);
            }
            context.DrawPath(null, pen, framePath, clipTransform);
        }
        return imageAvailable;
    }

    private static ImageEffectColorMatrix CreateRasterImageColorMatrix(
        in CadRasterImagePrimitive image)
    {
        float retained = 1.0f - (image.Fade / 100.0f);
        Vector4 offset = new(
            (image.FadeColor.Red / 255.0f) * (1.0f - retained),
            (image.FadeColor.Green / 255.0f) * (1.0f - retained),
            (image.FadeColor.Blue / 255.0f) * (1.0f - retained),
            image.TransparencyIsOn ? 0.0f : 1.0f);
        return new ImageEffectColorMatrix(
            new Vector4(retained, 0.0f, 0.0f, 0.0f),
            new Vector4(0.0f, retained, 0.0f, 0.0f),
            new Vector4(0.0f, 0.0f, retained, 0.0f),
            image.TransparencyIsOn ? Vector4.UnitW : Vector4.Zero,
            offset);
    }

    private static void AddRasterImageRectangle(
        PathGeometry path,
        double width,
        double height)
    {
        var figure = new PathFigure(Vector2.Zero, isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(ToFloat(width), 0.0f)));
        figure.Segments.Add(new LineSegment(
            new Vector2(ToFloat(width), ToFloat(height))));
        figure.Segments.Add(new LineSegment(new Vector2(0.0f, ToFloat(height))));
        path.Figures.Add(figure);
    }

    private static void AddRasterImageClip(
        PathGeometry path,
        ReadOnlySpan<CadWipeoutClipPoint> points)
    {
        var figure = new PathFigure(
            new Vector2(ToFloat(points[0].U), ToFloat(points[0].V)),
            isClosed: true);
        for (int i = 1; i < points.Length; i++)
        {
            figure.Segments.Add(new LineSegment(
                new Vector2(ToFloat(points[i].U), ToFloat(points[i].V))));
        }
        path.Figures.Add(figure);
    }

    private static void RecordDeferredImageFrame(
        DrawingContext context,
        Pen pen,
        CadDocumentSnapshot snapshot,
        in CadStrokeStyle style,
        CadLineTypeLoweringResult? lowering,
        CadLineTypePattern? pattern)
    {
        if (lowering is not CadLineTypeLoweringResult frame ||
            pattern is not CadLineTypePattern framePattern)
        {
            return;
        }
        if (frame.Path is not null)
        {
            context.DrawPath(null, pen, frame.Path, frame.Transform);
        }
        RecordLineTypeSplineFragments(context, pen, frame);
        RecordLineTypePlacements(
            context,
            pen,
            snapshot,
            style,
            framePattern,
            frame);
    }

    private static void AddWipeoutRectangle(
        PathGeometry path,
        double width,
        double height)
    {
        var figure = new PathFigure(Vector2.Zero, isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(ToFloat(width), 0.0f)));
        figure.Segments.Add(new LineSegment(new Vector2(ToFloat(width), ToFloat(height))));
        figure.Segments.Add(new LineSegment(new Vector2(0.0f, ToFloat(height))));
        path.Figures.Add(figure);
    }

    private static void AddWipeoutClip(
        PathGeometry path,
        ReadOnlySpan<CadWipeoutClipPoint> points)
    {
        var figure = new PathFigure(
            new Vector2(ToFloat(points[0].U), ToFloat(points[0].V)),
            isClosed: true);
        for (int i = 1; i < points.Length; i++)
        {
            figure.Segments.Add(new LineSegment(
                new Vector2(ToFloat(points[i].U), ToFloat(points[i].V))));
        }
        path.Figures.Add(figure);
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
        if (segment.Kind == CadHatchSegmentKind.RationalQuadraticBezier)
        {
            figure.Segments.Add(new RationalQuadraticBezierSegment(
                new Vector2(ToFloat(segment.CenterX), ToFloat(segment.CenterY)),
                new Vector2(ToFloat(segment.EndX), ToFloat(segment.EndY)),
                ToPositiveFiniteFloat(segment.Weight),
                isStroked: false));
            return;
        }
        if (segment.Kind == CadHatchSegmentKind.RationalCubicBezier)
        {
            figure.Segments.Add(new RationalCubicBezierSegment(
                new Vector2(ToFloat(segment.CenterX), ToFloat(segment.CenterY)),
                new Vector2(ToFloat(segment.CosineAxisX), ToFloat(segment.CosineAxisY)),
                new Vector2(ToFloat(segment.EndX), ToFloat(segment.EndY)),
                ToPositiveFiniteFloat(segment.Weight),
                ToPositiveFiniteFloat(segment.Weight2),
                isStroked: false));
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

    private static float ToPositiveFiniteFloat(double value)
    {
        float result = ToFloat(value);
        if (result <= 0f)
        {
            throw new InvalidOperationException(
                "A retained rational quadratic weight is outside the positive finite float domain.");
        }
        return result;
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

    private static bool RecordModelerGeometry(
        DrawingContext context,
        Pen pen,
        CadDocumentSnapshot snapshot,
        CadModelerGeometryPrimitive geometry)
    {
        ReadOnlySpan<CadModelerGeometryWire> wires =
            snapshot.ModelerGeometryWires.Span.Slice(
                geometry.WireOffset,
                geometry.WireCount);
        int edgeCount = 0;
        for (int wireIndex = 0; wireIndex < wires.Length; wireIndex++)
        {
            edgeCount = checked(edgeCount + Math.Max(0, wires[wireIndex].PointCount - 1));
        }
        if (edgeCount == 0)
        {
            return false;
        }

        var edges = new Line3D[edgeCount];
        ReadOnlySpan<CadPoint3D> points = snapshot.ModelerGeometryPoints.Span;
        int destination = 0;
        for (int wireIndex = 0; wireIndex < wires.Length; wireIndex++)
        {
            CadModelerGeometryWire wire = wires[wireIndex];
            ReadOnlySpan<CadPoint3D> wirePoints = points.Slice(
                wire.PointOffset,
                wire.PointCount);
            for (int pointIndex = 1; pointIndex < wirePoints.Length; pointIndex++)
            {
                edges[destination++] = new Line3D(
                    Rebase(wirePoints[pointIndex - 1], snapshot.RebaseOrigin),
                    Rebase(wirePoints[pointIndex], snapshot.RebaseOrigin));
            }
        }
        context.DrawAcisSolid(pen, edges, Matrix4x4.Identity);
        return true;
    }

    private static Vector3 Rebase(CadPoint3D point, CadPoint3D origin) =>
        new(
            ToFloat(point.X - origin.X),
            ToFloat(point.Y - origin.Y),
            ToFloat(point.Z - origin.Z));

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

    private static void RecordShxShape(
        DrawingContext context,
        Pen pen,
        CadDocumentSnapshot snapshot,
        in CadShxShapePrimitive shape)
    {
        Matrix4x4 transform = CreateProjectionTransform(
            shape.Origin,
            shape.XAxis,
            shape.YAxis,
            snapshot.RebaseOrigin);
        context.DrawPath(null, pen, shape.Glyph.Path, transform);
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
        kind is not (CadEntityKind.Point or CadEntityKind.Solid or CadEntityKind.Hatch or CadEntityKind.Wipeout or CadEntityKind.RasterImage or CadEntityKind.Text or CadEntityKind.ShxText or CadEntityKind.MText or CadEntityKind.ShxMText or CadEntityKind.ShxShape);

    private static void RecordViewportFrame(
        DrawingContext context,
        Pen pen,
        in CadViewportPrimitive viewport,
        CadPoint3D rebaseOrigin)
    {
        double x = viewport.Center.X - (viewport.Width * 0.5) - rebaseOrigin.X;
        double y = viewport.Center.Y - (viewport.Height * 0.5) - rebaseOrigin.Y;
        context.DrawRectangle(
            null,
            pen,
            new Rect(
                ToFloat(x),
                ToFloat(y),
                ToFloat(viewport.Width),
                ToFloat(viewport.Height)));
    }

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
        if (!Enum.IsDefined(options.LineWeightMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Lineweight mode must be defined.");
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
