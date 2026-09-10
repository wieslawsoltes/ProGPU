using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;

namespace ProGPU.CAD;

/// <summary>Compiles one generation-matched paper layout into a physical print plan.</summary>
/// <remarks>
/// Compilation retains one clipped layout picture and is O(U*E + P + V + B), using the
/// layout-scene bounds described by <see cref="CadLayoutSceneCompiler"/>, where B is the
/// total referenced viewport-boundary segment count. Page-picture creation remains one
/// clip plus one replay and allocates no raster target.
/// </remarks>
public sealed class CadLayoutPrintPlanCompiler
{
    private const double MillimetersPerInch = 25.4;
    private readonly ICadRasterImageSourceResolver? _rasterImageSourceResolver;
    private readonly WgpuContext? _rasterImageContext;

    public CadLayoutPrintPlanCompiler()
    {
    }

    public CadLayoutPrintPlanCompiler(
        ICadRasterImageSourceResolver? rasterImageSourceResolver,
        WgpuContext? rasterImageContext = null)
    {
        _rasterImageSourceResolver = rasterImageSourceResolver;
        _rasterImageContext = rasterImageContext;
    }

    public CadPrintPlan Compile(
        CadLayoutSnapshot snapshot,
        CadPageSetupSnapshot pageSetup,
        CadPageSetupPrintOptionsCompilerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(pageSetup);
        cancellationToken.ThrowIfCancellationRequested();
        if (pageSetup.ContentGeneration != snapshot.ContentGeneration)
        {
            throw new InvalidOperationException(
                $"Page setup generation {pageSetup.ContentGeneration} does not match layout generation {snapshot.ContentGeneration}.");
        }
        if (pageSetup.SourceKind != CadPageSetupSourceKind.Layout ||
            pageSetup.TargetSpace != CadPageTargetSpace.Paper ||
            !string.Equals(pageSetup.Name, snapshot.LayoutName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The page setup must be the matching paper-layout setup.",
                nameof(pageSetup));
        }
        if (!snapshot.ModelSpace.IsPlotOrderCompatible ||
            !snapshot.PaperSpace.IsPlotOrderCompatible)
        {
            throw new InvalidOperationException(
                "The layout snapshot contains persisted draw-order overrides but was not captured for plotting.");
        }

        CadPageSetupPrintOptionsResult lowered =
            new CadPageSetupPrintOptionsCompiler().Compile(
                pageSetup,
                options,
                cancellationToken);
        if (!lowered.IsSupported || lowered.PrintOptions is null)
        {
            string reason = lowered.Diagnostics.IsEmpty
                ? "The paper-layout page setup cannot be lowered."
                : lowered.Diagnostics.Span[0].Message;
            throw new NotSupportedException(reason);
        }

        CadPrintPlanOptions printOptions = lowered.PrintOptions;
        CadPrintPlanCompiler.ValidateOptions(printOptions);
        CadPrintPlanCompiler.ValidateTransparency(
            snapshot.ModelSpace,
            printOptions.TransparencyMode,
            cancellationToken);
        CadPrintPlanCompiler.ValidateTransparency(
            snapshot.PaperSpace,
            printOptions.TransparencyMode,
            cancellationToken);
        CadPrintPixelSize pageSize = CadPrintPlanCompiler.CreatePageSize(printOptions);
        CadPrintPixelRect placementArea = CadPrintPlanCompiler.CreatePlacementArea(
            printOptions,
            pageSize);
        CadPrintPixelRect printableArea = CadPrintPlanCompiler.CreatePrintableArea(
            placementArea,
            pageSize,
            CadPrintPlanCompiler.IsUpsideDown(printOptions.Rotation));
        float pixelsPerPaperMillimeter = checked(
            (float)(printOptions.OutputDpi / MillimetersPerInch));
        float pixelsPerPaperUnit = checked((float)(
            pixelsPerPaperMillimeter /
            printOptions.ModelUnitsPerMillimeter));
        Matrix4x4 contentToPage = CreateLayoutContentToPage(
            snapshot.PaperSpace.RebaseOrigin,
            printableArea,
            pixelsPerPaperUnit,
            printOptions);
        if (CadPrintPlanCompiler.IsUpsideDown(printOptions.Rotation))
        {
            contentToPage *= CadPrintPlanCompiler.CreateUpsideDownTransform(pageSize);
        }

        using CadRecordedLayoutScene scene = new CadLayoutSceneCompiler().Compile(
            snapshot,
            new CadLayoutSceneOptions
            {
                PhysicalDpi = printOptions.OutputDpi,
                LineWeightScale = printOptions.LineWeightScale,
                LineWeightMode = printOptions.LineWeightMode,
                IncludeViewportFrames = pageSetup.PlotViewportBorders,
                IncludeNonPlottableLayers = false,
                DrawViewportsFirst = pageSetup.DrawViewportsFirst,
                RasterImageSourceResolver = printOptions.RasterImageSourceResolver ??
                    _rasterImageSourceResolver,
                RasterImageContext = printOptions.RasterImageContext ??
                    _rasterImageContext,
            },
            cancellationToken);
        GpuPicture contentPicture = scene.CreatePicture();
        CadPlanSceneStatistics sceneStatistics = scene.Statistics.PaperSceneStatistics with
        {
            RecordedEntityCount = checked(
                scene.Statistics.PaperSceneStatistics.RecordedEntityCount +
                scene.Statistics.ModelSceneRecordedEntityCount),
            RecordedCommandCount = scene.Statistics.RecordedCommandCount,
        };
        var plotBounds = new CadBounds3D(
            new CadPoint3D(0.0, 0.0, 0.0),
            new CadPoint3D(
                printOptions.PaperWidthMillimeters *
                    printOptions.ModelUnitsPerMillimeter,
                printOptions.PaperHeightMillimeters *
                    printOptions.ModelUnitsPerMillimeter,
                0.0));
        return new CadPrintPlan(
            contentPicture,
            snapshot.ContentGeneration,
            printOptions,
            pageSize,
            printableArea,
            plotBounds,
            pixelsPerPaperUnit,
            printOptions.ModelUnitsPerMillimeter,
            contentToPage,
            sceneStatistics,
            scene.Diagnostics.ToArray());
    }

    private static Matrix4x4 CreateLayoutContentToPage(
        CadPoint3D rebaseOrigin,
        CadPrintPixelRect printableArea,
        float pixelsPerPaperUnit,
        CadPrintPlanOptions options)
    {
        double offsetX = options.PlotOffsetXMillimeters *
            options.ModelUnitsPerMillimeter *
            pixelsPerPaperUnit;
        double offsetY = options.PlotOffsetYMillimeters *
            options.ModelUnitsPerMillimeter *
            pixelsPerPaperUnit;
        double translationX = printableArea.X + offsetX +
            (rebaseOrigin.X * pixelsPerPaperUnit);
        double translationY = printableArea.Bottom - offsetY -
            (rebaseOrigin.Y * pixelsPerPaperUnit);
        if (!double.IsFinite(translationX) || !double.IsFinite(translationY) ||
            translationX < float.MinValue || translationX > float.MaxValue ||
            translationY < float.MinValue || translationY > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The paper-layout placement exceeds finite page coordinates.");
        }

        return new Matrix4x4(
            pixelsPerPaperUnit, 0, 0, 0,
            0, -pixelsPerPaperUnit, 0, 0,
            0, 0, 1, 0,
            (float)translationX, (float)translationY, 0, 1);
    }

}
