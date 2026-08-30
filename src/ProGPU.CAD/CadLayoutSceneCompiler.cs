using System.Numerics;
using System.Text;
using ProGPU.Backend;
using ProGPU.Scene;

namespace ProGPU.CAD;

public sealed class CadLayoutSceneOptions
{
    public const int DefaultMaxCompositedViewports = 16_384;

    public float PhysicalDpi { get; init; } = 96.0f;
    public float LineWeightScale { get; init; } = 1.0f;
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
/// Composes rectangular orthographic paper-space VIEWPORTs from immutable retained scenes.
/// </summary>
/// <remarks>
/// For V active viewports, E model entities, P paper entities, and U unique viewport-frozen
/// layer sets, compilation is O(U*E + P + V) time and O(U*E + P + V) retained storage.
/// Camera-only replay is O(Pc + V), where Pc is the paper command count, without rebuilding
/// model geometry. Perspective, depth clipping, non-rectangular boundaries, hidden/rendered
/// modes, and non-top view directions fail explicitly until their exact contracts are present.
/// </remarks>
public sealed class CadLayoutSceneCompiler
{
    private const uint HidePlotModeFlag = 2_048U;
    private const double DirectionTolerance = 1e-12;

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

                Rect clip = CreatePaperClip(viewport, paper.RebaseOrigin);
                context.PushClip(clip);
                context.DrawPictureTransformed(
                    modelPicture,
                    CreateModelToPaperTransform(
                        viewport,
                        model.RebaseOrigin,
                        paper.RebaseOrigin));
                context.PopClip();
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
        if (viewport.HasNonRectangularBoundary)
        {
            throw Unsupported(
                "CADVIEW004",
                $"VIEWPORT {index} uses a non-rectangular clipping boundary.");
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

    private static Rect CreatePaperClip(
        in CadViewportPrimitive viewport,
        CadPoint3D paperRebaseOrigin) =>
        new(
            ToFloat(viewport.Center.X - (viewport.Width * 0.5) - paperRebaseOrigin.X),
            ToFloat(viewport.Center.Y - (viewport.Height * 0.5) - paperRebaseOrigin.Y),
            ToFloat(viewport.Width),
            ToFloat(viewport.Height));

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
            options.MaxCompositedViewports <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Physical DPI, lineweight scale, and viewport budget must be positive.");
        }
    }
}
