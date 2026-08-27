using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.CAD;

public enum CadPrintScaleMode : byte
{
    FitToPrintableArea = 0,
    ModelUnitsPerMillimeter = 1,
}

public enum CadPrintPlacementMode : byte
{
    Centered = 0,
    PrintableAreaOffset = 1,
}

public readonly record struct CadPrintPixelSize(int Width, int Height);

public readonly record struct CadPrintPixelRect(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);

    public int Bottom => checked(Y + Height);

    internal Rect ToRect() => new(X, Y, Width, Height);
}

public sealed class CadPrintPlanOptions
{
    public const long DefaultMaxPagePixelCount = 268_435_456;

    public double PaperWidthMillimeters { get; init; } = 210.0;

    public double PaperHeightMillimeters { get; init; } = 297.0;

    public double MarginLeftMillimeters { get; init; } = 10.0;

    public double MarginTopMillimeters { get; init; } = 10.0;

    public double MarginRightMillimeters { get; init; } = 10.0;

    public double MarginBottomMillimeters { get; init; } = 10.0;

    public float OutputDpi { get; init; } = 300.0f;

    public CadBounds3D? PlotBounds { get; init; }

    public CadPrintScaleMode ScaleMode { get; init; } =
        CadPrintScaleMode.FitToPrintableArea;

    /// <summary>Drawing units represented by one physical paper millimeter.</summary>
    public double ModelUnitsPerMillimeter { get; init; } = 1.0;

    public CadPrintPlacementMode PlacementMode { get; init; } =
        CadPrintPlacementMode.Centered;

    public double PlotOffsetXMillimeters { get; init; }

    public double PlotOffsetYMillimeters { get; init; }

    /// <summary>
    /// Optional multiplier for the CAD-authored physical lineweight. The default
    /// preserves lineweight independently of plot scale.
    /// </summary>
    public float LineWeightScale { get; init; } = 1.0f;

    public long MaxPagePixelCount { get; init; } = DefaultMaxPagePixelCount;
}

/// <summary>
/// One immutable physical-page mapping over a retained CAD picture.
/// </summary>
/// <remarks>
/// The plan owns its device-independent content picture. Page-picture creation
/// records only a printable-area clip and one transformed picture replay. Preview
/// and output can therefore consume the same generation, scale, clip, physical
/// lineweights, analytic paths, and shaped glyph runs without recompiling geometry.
/// </remarks>
public sealed class CadPrintPlan : IDisposable
{
    private GpuPicture? _contentPicture;
    private readonly CadDiagnostic[] _diagnostics;

    public ulong ContentGeneration { get; }

    public double PaperWidthMillimeters { get; }

    public double PaperHeightMillimeters { get; }

    public float OutputDpi { get; }

    public CadPrintPixelSize PageSizePixels { get; }

    public CadPrintPixelRect PrintableAreaPixels { get; }

    public CadBounds3D PlotBounds { get; }

    public CadPrintScaleMode ScaleMode { get; }

    public CadPrintPlacementMode PlacementMode { get; }

    public float PixelsPerModelUnit { get; }

    public double ModelUnitsPerMillimeter { get; }

    public Matrix4x4 ContentToPage { get; }

    public CadPlanSceneStatistics SceneStatistics { get; }

    public ReadOnlyMemory<CadDiagnostic> Diagnostics => _diagnostics;

    public bool IsDisposed => _contentPicture is null;

    internal CadPrintPlan(
        GpuPicture contentPicture,
        ulong contentGeneration,
        CadPrintPlanOptions options,
        CadPrintPixelSize pageSizePixels,
        CadPrintPixelRect printableAreaPixels,
        CadBounds3D plotBounds,
        float pixelsPerModelUnit,
        double modelUnitsPerMillimeter,
        Matrix4x4 contentToPage,
        CadPlanSceneStatistics sceneStatistics,
        CadDiagnostic[] diagnostics)
    {
        _contentPicture = contentPicture;
        ContentGeneration = contentGeneration;
        PaperWidthMillimeters = options.PaperWidthMillimeters;
        PaperHeightMillimeters = options.PaperHeightMillimeters;
        OutputDpi = options.OutputDpi;
        PageSizePixels = pageSizePixels;
        PrintableAreaPixels = printableAreaPixels;
        PlotBounds = plotBounds;
        ScaleMode = options.ScaleMode;
        PlacementMode = options.PlacementMode;
        PixelsPerModelUnit = pixelsPerModelUnit;
        ModelUnitsPerMillimeter = modelUnitsPerMillimeter;
        ContentToPage = contentToPage;
        SceneStatistics = sceneStatistics;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Creates an independently owned retained page picture in output-pixel
    /// coordinates. The picture remains valid after this plan is disposed.
    /// </summary>
    public GpuPicture CreatePagePicture()
    {
        GpuPicture contentPicture = _contentPicture ??
            throw new ObjectDisposedException(nameof(CadPrintPlan));
        var recorder = new GpuPictureRecorder();
        DrawingContext context = recorder.BeginRecording(new Rect(
            0,
            0,
            PageSizePixels.Width,
            PageSizePixels.Height));
        context.PushClip(PrintableAreaPixels.ToRect());
        context.DrawPicture(contentPicture, ContentToPage);
        context.PopClip();
        return recorder.EndRecording();
    }

    public void Dispose()
    {
        _contentPicture?.Dispose();
        _contentPicture = null;
    }
}

/// <summary>
/// Compiles deterministic physical-page geometry from one immutable CAD snapshot.
/// </summary>
/// <remarks>
/// Compilation is O(E + C) time for E retained entity headers and C recorded scene
/// commands, with O(C) retained picture storage. It performs no raster allocation.
/// The explicit page-pixel budget bounds later raster/export targets.
/// </remarks>
public sealed class CadPrintPlanCompiler
{
    private const double MillimetersPerInch = 25.4;
    private const int MaximumExactFloatPixelCoordinate = 16_777_216;

    public CadPrintPlan Compile(
        CadDocumentSnapshot snapshot,
        CadPrintPlanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new CadPrintPlanOptions();
        ValidateOptions(options);

        CadPrintPixelSize pageSize = CreatePageSize(options);
        CadPrintPixelRect printableArea = CreatePrintableArea(options, pageSize);
        CadBounds3D plotBounds = ResolvePlotBounds(snapshot, options, cancellationToken);
        float pixelsPerModelUnit = ResolvePixelsPerModelUnit(
            plotBounds,
            printableArea,
            options);
        double modelUnitsPerMillimeter =
            options.OutputDpi / (MillimetersPerInch * pixelsPerModelUnit);
        Matrix4x4 contentToPage = CreateContentToPage(
            snapshot.RebaseOrigin,
            plotBounds,
            printableArea,
            pixelsPerModelUnit,
            options);

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            snapshot,
            new CadPlanSceneOptions
            {
                PhysicalDpi = options.OutputDpi,
                LineWeightScale = options.LineWeightScale,
                IncludeNonPlottableLayers = false,
            },
            cancellationToken);
        GpuPicture contentPicture = scene.CreatePicture();
        return new CadPrintPlan(
            contentPicture,
            snapshot.ContentGeneration,
            options,
            pageSize,
            printableArea,
            plotBounds,
            pixelsPerModelUnit,
            modelUnitsPerMillimeter,
            contentToPage,
            scene.Statistics,
            scene.Diagnostics.ToArray());
    }

    private static CadPrintPixelSize CreatePageSize(CadPrintPlanOptions options)
    {
        int width = MillimetersToPixels(options.PaperWidthMillimeters, options.OutputDpi);
        int height = MillimetersToPixels(options.PaperHeightMillimeters, options.OutputDpi);
        long pixelCount = (long)width * height;
        if (width <= 0 ||
            height <= 0 ||
            width > MaximumExactFloatPixelCoordinate ||
            height > MaximumExactFloatPixelCoordinate ||
            pixelCount > options.MaxPagePixelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The physical page exceeds the configured exact page-pixel budget.");
        }

        return new CadPrintPixelSize(width, height);
    }

    private static CadPrintPixelRect CreatePrintableArea(
        CadPrintPlanOptions options,
        CadPrintPixelSize pageSize)
    {
        int left = MillimetersToPixels(options.MarginLeftMillimeters, options.OutputDpi);
        int top = MillimetersToPixels(options.MarginTopMillimeters, options.OutputDpi);
        int right = MillimetersToPixels(options.MarginRightMillimeters, options.OutputDpi);
        int bottom = MillimetersToPixels(options.MarginBottomMillimeters, options.OutputDpi);
        int width = checked(pageSize.Width - left - right);
        int height = checked(pageSize.Height - top - bottom);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException(
                "The page margins leave no positive printable area.",
                nameof(options));
        }

        return new CadPrintPixelRect(left, top, width, height);
    }

    private static CadBounds3D ResolvePlotBounds(
        CadDocumentSnapshot snapshot,
        CadPrintPlanOptions options,
        CancellationToken cancellationToken)
    {
        if (options.PlotBounds is CadBounds3D requestedBounds)
        {
            if (requestedBounds.IsEmpty)
            {
                throw new ArgumentException(
                    "The requested plot bounds cannot be empty.",
                    nameof(options));
            }

            return requestedBounds;
        }

        ReadOnlySpan<CadEntityHeader> entities = snapshot.Entities.Span;
        ReadOnlySpan<CadLayerSnapshot> layers = snapshot.Layers.Span;
        CadBounds3D plottableBounds = CadBounds3D.Empty;
        for (int i = 0; i < entities.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CadEntityHeader entity = entities[i];
            if (layers[entity.LayerIndex].IsPlottable)
            {
                plottableBounds = plottableBounds.Union(entity.Bounds);
            }
        }

        if (plottableBounds.IsEmpty)
        {
            throw new InvalidOperationException(
                "The snapshot contains no visible entities on plottable layers.");
        }

        return plottableBounds;
    }

    private static float ResolvePixelsPerModelUnit(
        CadBounds3D plotBounds,
        CadPrintPixelRect printableArea,
        CadPrintPlanOptions options)
    {
        double scale;
        if (options.ScaleMode == CadPrintScaleMode.ModelUnitsPerMillimeter)
        {
            scale = options.OutputDpi /
                (MillimetersPerInch * options.ModelUnitsPerMillimeter);
        }
        else
        {
            double width = plotBounds.Max.X - plotBounds.Min.X;
            double height = plotBounds.Max.Y - plotBounds.Min.Y;
            double widthScale = width > 0.0
                ? printableArea.Width / width
                : double.PositiveInfinity;
            double heightScale = height > 0.0
                ? printableArea.Height / height
                : double.PositiveInfinity;
            scale = Math.Min(widthScale, heightScale);
            if (double.IsPositiveInfinity(scale))
            {
                scale = options.OutputDpi /
                    (MillimetersPerInch * options.ModelUnitsPerMillimeter);
            }
        }

        if (!double.IsFinite(scale) || scale <= 0.0 || scale > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The selected plot scale exceeds finite page coordinates.");
        }

        return (float)scale;
    }

    private static Matrix4x4 CreateContentToPage(
        CadPoint3D rebaseOrigin,
        CadBounds3D plotBounds,
        CadPrintPixelRect printableArea,
        float scale,
        CadPrintPlanOptions options)
    {
        double translationX;
        double translationY;
        if (options.PlacementMode == CadPrintPlacementMode.Centered)
        {
            CadPoint3D center = plotBounds.Center;
            double targetX = printableArea.X + (printableArea.Width * 0.5);
            double targetY = printableArea.Y + (printableArea.Height * 0.5);
            translationX = targetX - ((center.X - rebaseOrigin.X) * scale);
            translationY = targetY + ((center.Y - rebaseOrigin.Y) * scale);
        }
        else
        {
            double offsetX = options.PlotOffsetXMillimeters * options.OutputDpi /
                MillimetersPerInch;
            double offsetY = options.PlotOffsetYMillimeters * options.OutputDpi /
                MillimetersPerInch;
            double targetLeft = printableArea.X + offsetX;
            double targetBottom = printableArea.Bottom - offsetY;
            translationX = targetLeft - ((plotBounds.Min.X - rebaseOrigin.X) * scale);
            translationY = targetBottom + ((plotBounds.Min.Y - rebaseOrigin.Y) * scale);
        }

        if (!double.IsFinite(translationX) ||
            !double.IsFinite(translationY) ||
            translationX < float.MinValue ||
            translationX > float.MaxValue ||
            translationY < float.MinValue ||
            translationY > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The plot placement exceeds finite page coordinates.");
        }

        return new Matrix4x4(
            scale, 0, 0, 0,
            0, -scale, 0, 0,
            0, 0, 1, 0,
            (float)translationX, (float)translationY, 0, 1);
    }

    private static int MillimetersToPixels(double millimeters, float dpi)
    {
        double pixels = millimeters * dpi / MillimetersPerInch;
        if (!double.IsFinite(pixels) || pixels < 0.0 || pixels > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(millimeters),
                "The physical measurement exceeds finite page-pixel coordinates.");
        }

        return checked((int)Math.Round(pixels, MidpointRounding.AwayFromZero));
    }

    private static void ValidateOptions(CadPrintPlanOptions options)
    {
        if (!IsFinitePositive(options.PaperWidthMillimeters) ||
            !IsFinitePositive(options.PaperHeightMillimeters))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Paper dimensions must be finite and positive.");
        }
        if (!IsFiniteNonNegative(options.MarginLeftMillimeters) ||
            !IsFiniteNonNegative(options.MarginTopMillimeters) ||
            !IsFiniteNonNegative(options.MarginRightMillimeters) ||
            !IsFiniteNonNegative(options.MarginBottomMillimeters))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Page margins must be finite and non-negative.");
        }
        if (!float.IsFinite(options.OutputDpi) || options.OutputDpi <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Output DPI must be finite and positive.");
        }
        if (!Enum.IsDefined(options.ScaleMode) ||
            !Enum.IsDefined(options.PlacementMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Print scale and placement modes must be defined values.");
        }
        if (!IsFinitePositive(options.ModelUnitsPerMillimeter))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Model units per millimeter must be finite and positive.");
        }
        if (!double.IsFinite(options.PlotOffsetXMillimeters) ||
            !double.IsFinite(options.PlotOffsetYMillimeters))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Plot offsets must be finite.");
        }
        if (!float.IsFinite(options.LineWeightScale) || options.LineWeightScale <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Lineweight scale must be finite and positive.");
        }
        if (options.MaxPagePixelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The page-pixel budget must be positive.");
        }
    }

    private static bool IsFinitePositive(double value) =>
        double.IsFinite(value) && value > 0.0;

    private static bool IsFiniteNonNegative(double value) =>
        double.IsFinite(value) && value >= 0.0;
}
