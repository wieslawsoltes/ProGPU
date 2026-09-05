using System.Numerics;
using System.Text;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.CAD;

public sealed class CadLayoutSceneOptions
{
    public const int DefaultMaxCompositedViewports = 16_384;

    public float PhysicalDpi { get; init; } = 96.0f;
    public float LineWeightScale { get; init; } = 1.0f;
    public CadPrintLineWeightMode LineWeightMode { get; init; } =
        CadPrintLineWeightMode.ObjectLineWeights;
    public bool IncludeViewportFrames { get; init; } = true;
    public bool IncludeNonPlottableLayers { get; init; } = true;
    public bool DrawViewportsFirst { get; init; } = true;
    public int MaxCompositedViewports { get; init; } = DefaultMaxCompositedViewports;
    public ICadRasterImageSourceResolver? RasterImageSourceResolver { get; init; }
    public WgpuContext? RasterImageContext { get; init; }
}

public readonly record struct CadLayoutSceneStatistics(
    int ActiveViewportCount,
    int ModelSceneVariantCount,
    int RecordedCommandCount,
    CadPlanSceneStatistics PaperSceneStatistics)
{
    public int ModelSceneRecordedEntityCount { get; init; }
    public int ModelSceneRecordedCommandCount { get; init; }
}

/// <summary>One retained paper-space scene with clipped model-space viewport replays.</summary>
public sealed class CadRecordedLayoutScene : IDisposable
{
    private GpuPicture? _picture;
    private readonly CadDiagnostic[] _diagnostics;

    public ulong ContentGeneration { get; }
    public string LayoutName { get; }
    public CadPoint3D RebaseOrigin { get; }
    public CadLayoutSceneStatistics Statistics { get; }
    public ReadOnlyMemory<CadDiagnostic> Diagnostics => _diagnostics;
    public bool IsDisposed => _picture is null;

    internal CadRecordedLayoutScene(
        GpuPicture picture,
        ulong contentGeneration,
        string layoutName,
        CadPoint3D rebaseOrigin,
        CadLayoutSceneStatistics statistics,
        CadDiagnostic[] diagnostics)
    {
        _picture = picture;
        ContentGeneration = contentGeneration;
        LayoutName = layoutName;
        RebaseOrigin = rebaseOrigin;
        Statistics = statistics;
        _diagnostics = diagnostics;
    }

    public GpuPicture CreatePicture() =>
        (_picture ?? throw new ObjectDisposedException(nameof(CadRecordedLayoutScene))).Clone();

    public void Dispose()
    {
        _picture?.Dispose();
        _picture = null;
    }
}

/// <summary>
/// Composes orthographic paper-space VIEWPORTs from immutable retained scenes.
/// </summary>
/// <remarks>
/// For V active viewports, E model entities, P paper entities, and U unique viewport-frozen
/// layer sets, with B total referenced boundary segments, compilation is
/// O(U*E + P + V + B) time and O(U*E + P + V + B) retained storage.
/// Camera-only replay is O(Pc + V), where Pc is the paper command count, without rebuilding
/// model geometry. Perspective, depth clipping, unsupported or malformed boundary kinds,
/// hidden/rendered modes, and non-top view directions fail explicitly.
/// </remarks>
public sealed class CadLayoutSceneCompiler
{
    private const uint HidePlotModeFlag = 2_048U;
    private const double DirectionTolerance = 1e-12;
    private const double TwoPi = Math.PI * 2.0;

    private readonly record struct PaperClip(
        Rect Rectangle,
        PathGeometry? Geometry,
        Matrix4x4 GeometryTransform);

    public CadRecordedLayoutScene Compile(
        CadLayoutSnapshot snapshot,
        CadLayoutSceneOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new CadLayoutSceneOptions();
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        CadDocumentSnapshot model = snapshot.ModelSpace;
        CadDocumentSnapshot paper = snapshot.PaperSpace;
        if (model.ContentGeneration != snapshot.ContentGeneration ||
            paper.ContentGeneration != snapshot.ContentGeneration)
        {
            throw new InvalidOperationException(
                "Layout model-space and paper-space snapshots must share one content generation.");
        }

        using CadRecordedPlanScene paperScene = new CadPlanSceneCompiler().Compile(
            paper,
            CreatePlanOptions(options, excludedLayerNames: null, options.IncludeViewportFrames),
            cancellationToken);
        var recorder = new GpuPictureRecorder();
        DrawingContext context = recorder.BeginRecording(new Rect(0, 0, 1, 1));
        var modelPictures = new Dictionary<string, GpuPicture>(StringComparer.Ordinal);
        var diagnostics = new List<CadDiagnostic>(paperScene.Diagnostics.Length);
        diagnostics.AddRange(paperScene.Diagnostics.Span);
        Dictionary<ulong, int> boundaryEntityIndices =
            CreateBoundaryEntityIndex(paper);
        int activeViewportCount = 0;
        int modelSceneRecordedEntityCount = 0;
        int modelSceneRecordedCommandCount = 0;
        try
        {
            if (!options.DrawViewportsFirst)
            {
                context.Append(paperScene.DrawingContext);
            }
            ReadOnlySpan<CadViewportPrimitive> viewports = paper.Viewports.Span;
            for (int viewportIndex = 0; viewportIndex < viewports.Length; viewportIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CadViewportPrimitive viewport = viewports[viewportIndex];
                if (viewport.RepresentsPaper || !viewport.IsOn)
                {
                    continue;
                }
                if (++activeViewportCount > options.MaxCompositedViewports)
                {
                    throw Unsupported(
                        "CADVIEW001",
                        $"Active VIEWPORT count exceeds the configured limit of {options.MaxCompositedViewports}.");
                }

                if (activeViewportCount == 1)
                {
                    ValidateModelOverlaySupport(model);
                }
                ValidateViewport(viewport, viewportIndex);
                string[] frozenLayers = GetCanonicalFrozenLayers(paper, viewport);
                string cacheKey = CreateFrozenLayerKey(frozenLayers);
                if (!modelPictures.TryGetValue(cacheKey, out GpuPicture? modelPicture))
                {
                    using CadRecordedPlanScene modelScene = new CadPlanSceneCompiler().Compile(
                        model,
                        CreatePlanOptions(options, frozenLayers, includeViewportFrames: false),
                        cancellationToken);
                    modelPicture = modelScene.CreatePicture();
                    modelPictures.Add(cacheKey, modelPicture);
                    modelSceneRecordedEntityCount = checked(
                        modelSceneRecordedEntityCount +
                        modelScene.Statistics.RecordedEntityCount);
                    modelSceneRecordedCommandCount = checked(
                        modelSceneRecordedCommandCount +
                        modelScene.Statistics.RecordedCommandCount);
                    diagnostics.AddRange(modelScene.Diagnostics.Span);
                }

                PaperClip clip = CreatePaperClip(
                    paper,
                    viewport,
                    viewportIndex,
                    boundaryEntityIndices);
                if (clip.Geometry is null)
                {
                    context.PushClip(clip.Rectangle);
                }
                else
                {
                    context.PushGeometryClip(clip.Geometry, clip.GeometryTransform);
                }
                context.DrawPictureTransformed(
                    modelPicture,
                    CreateModelToPaperTransform(
                        viewport,
                        model.RebaseOrigin,
                        paper.RebaseOrigin));
                if (clip.Geometry is null)
                {
                    context.PopClip();
                }
                else
                {
                    context.PopGeometryClip();
                }
            }

            if (options.DrawViewportsFirst)
            {
                context.Append(paperScene.DrawingContext);
            }
            int recordedCommandCount = context.Commands.Count;
            GpuPicture picture = recorder.EndRecording();
            return new CadRecordedLayoutScene(
                picture,
                snapshot.ContentGeneration,
                snapshot.LayoutName,
                paper.RebaseOrigin,
                new CadLayoutSceneStatistics(
                    activeViewportCount,
                    modelPictures.Count,
                    recordedCommandCount,
                    paperScene.Statistics)
                {
                    ModelSceneRecordedEntityCount = modelSceneRecordedEntityCount,
                    ModelSceneRecordedCommandCount = modelSceneRecordedCommandCount,
                },
                diagnostics.ToArray());
        }
        catch
        {
            context.Clear();
            throw;
        }
        finally
        {
            foreach (GpuPicture modelPicture in modelPictures.Values)
            {
                modelPicture.Dispose();
            }
        }
    }

    internal static Matrix4x4 CreateModelToPaperTransform(
        in CadViewportPrimitive viewport,
        CadPoint3D modelRebaseOrigin,
        CadPoint3D paperRebaseOrigin)
    {
        float scale = ToFloat(viewport.Height / viewport.ViewHeight);
        Matrix4x4 transform = Matrix4x4.CreateTranslation(
            ToFloat(modelRebaseOrigin.X - viewport.ViewTarget.X),
            ToFloat(modelRebaseOrigin.Y - viewport.ViewTarget.Y),
            0.0f);
        transform *= Matrix4x4.CreateRotationZ(ToFloat(viewport.TwistAngle));
        transform *= Matrix4x4.CreateTranslation(
            ToFloat(-viewport.ViewCenterX),
            ToFloat(-viewport.ViewCenterY),
            0.0f);
        transform *= Matrix4x4.CreateScale(scale, scale, 1.0f);
        transform *= Matrix4x4.CreateTranslation(
            ToFloat(viewport.Center.X - paperRebaseOrigin.X),
            ToFloat(viewport.Center.Y - paperRebaseOrigin.Y),
            0.0f);
        return transform;
    }

    private static CadPlanSceneOptions CreatePlanOptions(
        CadLayoutSceneOptions options,
        IReadOnlyCollection<string>? excludedLayerNames,
        bool includeViewportFrames) =>
        new()
        {
            PhysicalDpi = options.PhysicalDpi,
            LineWeightScale = options.LineWeightScale,
            LineWeightMode = options.LineWeightMode,
            IncludeNonPlottableLayers = options.IncludeNonPlottableLayers,
            IncludeViewportFrames = includeViewportFrames,
            ReportDeferredConstructionGeometry = false,
            ReportDeferredPointMarkers = false,
            ExcludedLayerNames = excludedLayerNames,
            RasterImageSourceResolver = options.RasterImageSourceResolver,
            RasterImageContext = options.RasterImageContext,
        };

    private static void ValidateViewport(in CadViewportPrimitive viewport, int index)
    {
        if (viewport.IsPerspective)
        {
            throw Unsupported("CADVIEW002", $"VIEWPORT {index} uses perspective projection.");
        }
        if (viewport.HasFrontClip || viewport.HasBackClip)
        {
            throw Unsupported("CADVIEW003", $"VIEWPORT {index} uses depth clipping.");
        }
        if ((viewport.StatusFlags & HidePlotModeFlag) != 0 ||
            viewport.RenderMode is not (0 or 1) ||
            viewport.ShadePlotMode is not (0 or 1))
        {
            throw Unsupported(
                "CADVIEW005",
                $"VIEWPORT {index} requires hidden-line or rendered output.");
        }
        if (Math.Abs(viewport.ViewDirection.X) > DirectionTolerance ||
            Math.Abs(viewport.ViewDirection.Y) > DirectionTolerance ||
            Math.Abs(viewport.ViewDirection.Z - 1.0) > DirectionTolerance)
        {
            throw Unsupported(
                "CADVIEW006",
                $"VIEWPORT {index} is not an orthographic WCS top view.");
        }
    }

    private static void ValidateModelOverlaySupport(CadDocumentSnapshot model)
    {
        if (!model.ConstructionLines.IsEmpty)
        {
            throw Unsupported(
                "CADVIEW007",
                "Model space contains RAY/XLINE geometry that requires viewport-dependent finite clipping.");
        }

        foreach (CadPointPrimitive point in model.Points.Span)
        {
            if (point.DisplayMode != 0)
            {
                throw Unsupported(
                    "CADVIEW008",
                    "Model space contains PDMODE marker geometry that requires viewport-dependent sizing.");
            }
        }
    }

    private static string[] GetCanonicalFrozenLayers(
        CadDocumentSnapshot paper,
        in CadViewportPrimitive viewport)
    {
        if (viewport.FrozenLayerCount == 0)
        {
            return Array.Empty<string>();
        }

        ReadOnlySpan<CadViewportFrozenLayer> source =
            paper.ViewportFrozenLayers.Span.Slice(
                viewport.FrozenLayerOffset,
                viewport.FrozenLayerCount);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < source.Length; i++)
        {
            unique.Add(source[i].Name);
        }
        string[] result = unique.ToArray();
        Array.Sort(result, StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static string CreateFrozenLayerKey(string[] layerNames)
    {
        if (layerNames.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (string layerName in layerNames)
        {
            builder.Append(layerName.Length);
            builder.Append(':');
            builder.Append(layerName.ToUpperInvariant());
            builder.Append(';');
        }
        return builder.ToString();
    }

    private static Dictionary<ulong, int> CreateBoundaryEntityIndex(
        CadDocumentSnapshot paper)
    {
        var requestedHandles = new HashSet<ulong>();
        foreach (CadViewportPrimitive viewport in paper.Viewports.Span)
        {
            if (viewport.BoundaryHandle != 0)
            {
                requestedHandles.Add(viewport.BoundaryHandle);
            }
        }

        var result = new Dictionary<ulong, int>(requestedHandles.Count);
        ReadOnlySpan<CadEntityHeader> entities = paper.Entities.Span;
        for (int index = 0; index < entities.Length; index++)
        {
            ulong handle = entities[index].Handle;
            if (!requestedHandles.Contains(handle))
            {
                continue;
            }
            if (!result.TryAdd(handle, index))
            {
                result[handle] = -1;
            }
        }
        return result;
    }

    private static PaperClip CreatePaperClip(
        CadDocumentSnapshot paper,
        in CadViewportPrimitive viewport,
        int viewportIndex,
        IReadOnlyDictionary<ulong, int> boundaryEntityIndices)
    {
        if (!viewport.HasNonRectangularBoundary)
        {
            return new PaperClip(
                new Rect(
                    ToFloat(viewport.Center.X - (viewport.Width * 0.5) - paper.RebaseOrigin.X),
                    ToFloat(viewport.Center.Y - (viewport.Height * 0.5) - paper.RebaseOrigin.Y),
                    ToFloat(viewport.Width),
                    ToFloat(viewport.Height)),
                Geometry: null,
                GeometryTransform: Matrix4x4.Identity);
        }
        if (viewport.BoundaryHandle == 0 ||
            !boundaryEntityIndices.TryGetValue(viewport.BoundaryHandle, out int entityIndex) ||
            entityIndex < 0)
        {
            throw Unsupported(
                "CADVIEW004",
                $"VIEWPORT {viewportIndex} has a missing or ambiguous clipping-boundary entity.");
        }

        CadEntityHeader boundary = paper.Entities.Span[entityIndex];
        if (paper.Layers.Span[boundary.LayerIndex].IsFrozen)
        {
            throw Unsupported(
                "CADVIEW010",
                $"VIEWPORT {viewportIndex} has a clipping boundary on a frozen layer.");
        }

        return boundary.Kind switch
        {
            CadEntityKind.LightweightPolyline or CadEntityKind.Polyline2D =>
                CreatePolylineClip(paper, boundary, viewportIndex),
            CadEntityKind.Circle => CreateCircleClip(paper, boundary),
            CadEntityKind.Ellipse => CreateEllipseClip(paper, boundary, viewportIndex),
            CadEntityKind.Spline => CreateSplineClip(paper, boundary, viewportIndex),
            _ => throw Unsupported(
                "CADVIEW009",
                $"VIEWPORT {viewportIndex} clipping boundary kind {boundary.Kind} is not an exact supported closed path."),
        };
    }

    private static PaperClip CreatePolylineClip(
        CadDocumentSnapshot paper,
        in CadEntityHeader boundary,
        int viewportIndex)
    {
        CadPolylinePrimitive polyline =
            paper.Polylines.Span[boundary.PrimitiveIndex];
        ReadOnlySpan<CadPolylineVertex> vertices = paper.PolylineVertices.Span.Slice(
            polyline.VertexOffset,
            polyline.VertexCount);
        if (!polyline.IsClosed || vertices.Length < 3)
        {
            throw Unsupported(
                "CADVIEW011",
                $"VIEWPORT {viewportIndex} clipping polyline is not a closed contour with at least three vertices.");
        }

        double anchorX = vertices[0].X;
        double anchorY = vertices[0].Y;
        var path = new PathGeometry { FillRule = FillRule.Nonzero };
        var figure = new PathFigure(Vector2.Zero, isClosed: true)
        {
            IsFilled = true,
        };
        for (int index = 0; index < vertices.Length; index++)
        {
            CadPolylineVertex start = vertices[index];
            CadPolylineVertex end = vertices[(index + 1) % vertices.Length];
            var endpoint = new Vector2(
                ToFloat(end.X - anchorX),
                ToFloat(end.Y - anchorY));
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

        CadPoint3D origin = polyline.WorldOrigin +
            (polyline.CoordinateSystem.XAxis * anchorX) +
            (polyline.CoordinateSystem.YAxis * anchorY);
        return new PaperClip(
            default,
            path,
            CreateProjectionTransform(
                origin,
                polyline.CoordinateSystem.XAxis,
                polyline.CoordinateSystem.YAxis,
                paper.RebaseOrigin));
    }

    private static PaperClip CreateCircleClip(
        CadDocumentSnapshot paper,
        in CadEntityHeader boundary)
    {
        CadCirclePrimitive circle = paper.Circles.Span[boundary.PrimitiveIndex];
        PathGeometry path = CreateUnitCirclePath(circle.Radius);
        return new PaperClip(
            default,
            path,
            CreateProjectionTransform(
                circle.Center,
                circle.CoordinateSystem.XAxis,
                circle.CoordinateSystem.YAxis,
                paper.RebaseOrigin));
    }

    private static PaperClip CreateEllipseClip(
        CadDocumentSnapshot paper,
        in CadEntityHeader boundary,
        int viewportIndex)
    {
        CadEllipsePrimitive ellipse = paper.Ellipses.Span[boundary.PrimitiveIndex];
        if (ellipse.SweepParameter < TwoPi - 1e-12)
        {
            throw Unsupported(
                "CADVIEW011",
                $"VIEWPORT {viewportIndex} clipping ellipse is not closed.");
        }
        return new PaperClip(
            default,
            CreateUnitCirclePath(1.0),
            CreateProjectionTransform(
                ellipse.Center,
                ellipse.MajorAxis,
                ellipse.MinorAxis,
                paper.RebaseOrigin));
    }

    private static PaperClip CreateSplineClip(
        CadDocumentSnapshot paper,
        in CadEntityHeader boundary,
        int viewportIndex)
    {
        CadSplinePrimitive spline = paper.Splines.Span[boundary.PrimitiveIndex];
        if (!spline.IsClosed && !spline.IsPeriodic)
        {
            throw Unsupported(
                "CADVIEW011",
                $"VIEWPORT {viewportIndex} clipping spline is not closed.");
        }
        if (spline.Degree > 3)
        {
            throw Unsupported(
                "CADVIEW012",
                $"VIEWPORT {viewportIndex} degree-{spline.Degree} clipping spline requires a shared filled-path segment above cubic degree.");
        }
        if (!CadSplineCanonicalizer.TryCreate(paper, spline, out CadCanonicalSpline canonical))
        {
            throw Unsupported(
                "CADVIEW011",
                $"VIEWPORT {viewportIndex} clipping spline has invalid retained NURBS topology.");
        }

        Span<CadHomogeneousPoint> controls = stackalloc CadHomogeneousPoint[4];
        PathGeometry? path = null;
        PathFigure? figure = null;
        CadPoint3D anchor = default;
        int emittedSpanCount = 0;
        for (int sourceSpan = canonical.Degree;
             sourceSpan < canonical.ControlPointCount;
             sourceSpan++)
        {
            if (!(canonical.GetKnot(sourceSpan + 1) > canonical.GetKnot(sourceSpan)))
            {
                continue;
            }

            Span<CadHomogeneousPoint> span = controls[..(canonical.Degree + 1)];
            if (!CadRationalBezier.TryExtractSpan(canonical, sourceSpan, span))
            {
                throw Unsupported(
                    "CADVIEW011",
                    $"VIEWPORT {viewportIndex} clipping spline cannot isolate an exact Bezier span.");
            }
            if (emittedSpanCount == 0)
            {
                anchor = span[0].Cartesian;
                path = new PathGeometry { FillRule = FillRule.Nonzero };
                figure = new PathFigure(Vector2.Zero, isClosed: true)
                {
                    IsFilled = true,
                };
                path.Figures.Add(figure);
            }

            AddSplineClipSpan(
                figure!,
                span,
                canonical.Degree,
                anchor,
                viewportIndex);
            emittedSpanCount++;
        }
        if (emittedSpanCount == 0)
        {
            throw Unsupported(
                "CADVIEW011",
                $"VIEWPORT {viewportIndex} clipping spline has an empty parameter domain.");
        }

        return new PaperClip(
            default,
            path,
            CreateProjectionTransform(
                anchor,
                new CadPoint3D(1.0, 0.0, 0.0),
                new CadPoint3D(0.0, 1.0, 0.0),
                paper.RebaseOrigin));
    }

    private static void AddSplineClipSpan(
        PathFigure figure,
        ReadOnlySpan<CadHomogeneousPoint> controls,
        int degree,
        CadPoint3D anchor,
        int viewportIndex)
    {
        Vector2 endpoint = ProjectSplineClipPoint(controls[^1], anchor);
        switch (degree)
        {
            case 1:
                figure.Segments.Add(new LineSegment(
                    endpoint,
                    isSmoothJoin: true,
                    isStroked: false));
                return;
            case 2:
                Vector2 quadraticControl = ProjectSplineClipPoint(controls[1], anchor);
                if (!CadRationalBezier.TryGetCanonicalQuadraticWeight(
                        controls,
                        out double quadraticWeight))
                {
                    throw Unsupported(
                        "CADVIEW012",
                        $"VIEWPORT {viewportIndex} clipping spline has an unrepresentable quadratic weight.");
                }
                if (NearlyUnitSplineWeight(quadraticWeight))
                {
                    figure.Segments.Add(new QuadraticBezierSegment(
                        quadraticControl,
                        endpoint,
                        isSmoothJoin: true,
                        isStroked: false));
                    return;
                }
                float retainedQuadraticWeight = (float)quadraticWeight;
                EnsureWeightedSplineCoordinate(
                    quadraticControl,
                    retainedQuadraticWeight,
                    viewportIndex);
                figure.Segments.Add(new RationalQuadraticBezierSegment(
                    quadraticControl,
                    endpoint,
                    retainedQuadraticWeight,
                    isSmoothJoin: true,
                    isStroked: false));
                return;
            case 3:
                Vector2 cubicControl1 = ProjectSplineClipPoint(controls[1], anchor);
                Vector2 cubicControl2 = ProjectSplineClipPoint(controls[2], anchor);
                if (!CadRationalBezier.TryGetCanonicalCubicWeights(
                        controls,
                        out double cubicWeight1,
                        out double cubicWeight2))
                {
                    throw Unsupported(
                        "CADVIEW012",
                        $"VIEWPORT {viewportIndex} clipping spline has unrepresentable cubic weights.");
                }
                if (NearlyUnitSplineWeight(cubicWeight1) &&
                    NearlyUnitSplineWeight(cubicWeight2))
                {
                    figure.Segments.Add(new CubicBezierSegment(
                        cubicControl1,
                        cubicControl2,
                        endpoint,
                        isSmoothJoin: true,
                        isStroked: false));
                    return;
                }
                float retainedCubicWeight1 = (float)cubicWeight1;
                float retainedCubicWeight2 = (float)cubicWeight2;
                EnsureWeightedSplineCoordinate(
                    cubicControl1,
                    retainedCubicWeight1,
                    viewportIndex);
                EnsureWeightedSplineCoordinate(
                    cubicControl2,
                    retainedCubicWeight2,
                    viewportIndex);
                figure.Segments.Add(new RationalCubicBezierSegment(
                    cubicControl1,
                    cubicControl2,
                    endpoint,
                    retainedCubicWeight1,
                    retainedCubicWeight2,
                    isSmoothJoin: true,
                    isStroked: false));
                return;
            default:
                throw Unsupported(
                    "CADVIEW012",
                    $"VIEWPORT {viewportIndex} degree-{degree} clipping spline cannot be retained exactly.");
        }
    }

    private static Vector2 ProjectSplineClipPoint(
        CadHomogeneousPoint control,
        CadPoint3D anchor)
    {
        CadPoint3D point = control.Cartesian;
        return new Vector2(
            ToFloat(point.X - anchor.X),
            ToFloat(point.Y - anchor.Y));
    }

    private static void EnsureWeightedSplineCoordinate(
        Vector2 point,
        float weight,
        int viewportIndex)
    {
        if (!float.IsFinite(point.X * weight) ||
            !float.IsFinite(point.Y * weight))
        {
            throw Unsupported(
                "CADVIEW012",
                $"VIEWPORT {viewportIndex} clipping spline exceeds the shared-path weighted-coordinate range.");
        }
    }

    private static bool NearlyUnitSplineWeight(double weight) =>
        Math.Abs(weight - 1.0) <= Math.Max(1.0, Math.Abs(weight)) * 1e-13;

    private static PathGeometry CreateUnitCirclePath(double radius)
    {
        float retainedRadius = ToFloat(radius);
        var path = new PathGeometry { FillRule = FillRule.Nonzero };
        var figure = new PathFigure(
            new Vector2(retainedRadius, 0.0f),
            isClosed: true)
        {
            IsFilled = true,
        };
        figure.Segments.Add(new ArcSegment(
            new Vector2(-retainedRadius, 0.0f),
            new Vector2(retainedRadius, retainedRadius),
            rotationAngle: 0.0f,
            isLargeArc: false,
            SweepDirection.Counterclockwise));
        figure.Segments.Add(new ArcSegment(
            new Vector2(retainedRadius, 0.0f),
            new Vector2(retainedRadius, retainedRadius),
            rotationAngle: 0.0f,
            isLargeArc: false,
            SweepDirection.Counterclockwise));
        path.Figures.Add(figure);
        return path;
    }

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

    private static NotSupportedException Unsupported(string code, string message) =>
        new($"{code}: {message}");

    private static float ToFloat(double value)
    {
        float result = (float)value;
        if (!float.IsFinite(result))
        {
            throw new InvalidOperationException(
                "A rebased VIEWPORT coordinate exceeds the retained float range.");
        }
        return result;
    }

    private static void ValidateOptions(CadLayoutSceneOptions options)
    {
        if (!float.IsFinite(options.PhysicalDpi) || options.PhysicalDpi <= 0.0f ||
            !float.IsFinite(options.LineWeightScale) || options.LineWeightScale <= 0.0f ||
            !Enum.IsDefined(options.LineWeightMode) ||
            options.MaxCompositedViewports <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Physical DPI, lineweight scale, and viewport budget must be positive.");
        }
    }
}
