using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;

namespace ProGPU.CAD;

/// <summary>Compiles one generation-matched paper layout into a physical print plan.</summary>
/// <remarks>
/// Compilation retains one clipped layout picture and is O(U*E + P + V), using the
/// layout-scene bounds described by <see cref="CadLayoutSceneCompiler"/>. Page-picture
/// creation remains one clip plus one replay and allocates no raster target.
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

        EnsureOpaqueStyles(snapshot.ModelSpace, cancellationToken);
        EnsureOpaqueStyles(snapshot.PaperSpace, cancellationToken);
        CadPrintPlanOptions printOptions = lowered.PrintOptions;
        CadPrintPlanCompiler.ValidateOptions(printOptions);
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
        Matrix4x4 contentToPage = CreateLayoutContentToPage(
            snapshot.PaperSpace.RebaseOrigin,
            printableArea,
            pixelsPerPaperMillimeter,
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
                printOptions.PaperWidthMillimeters,
                printOptions.PaperHeightMillimeters,
                0.0));
        return new CadPrintPlan(
            contentPicture,
            snapshot.ContentGeneration,
            printOptions,
            pageSize,
            printableArea,
            plotBounds,
            pixelsPerPaperMillimeter,
            modelUnitsPerMillimeter: 1.0,
            contentToPage,
            sceneStatistics,
            scene.Diagnostics.ToArray());
    }

    private static Matrix4x4 CreateLayoutContentToPage(
        CadPoint3D rebaseOrigin,
        CadPrintPixelRect printableArea,
        float scale,
        CadPrintPlanOptions options)
    {
        double offsetX = options.PlotOffsetXMillimeters * scale;
        double offsetY = options.PlotOffsetYMillimeters * scale;
        double translationX = printableArea.X + offsetX + (rebaseOrigin.X * scale);
        double translationY = printableArea.Bottom - offsetY - (rebaseOrigin.Y * scale);
        if (!double.IsFinite(translationX) || !double.IsFinite(translationY) ||
            translationX < float.MinValue || translationX > float.MaxValue ||
            translationY < float.MinValue || translationY > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The paper-layout placement exceeds finite page coordinates.");
        }

        return new Matrix4x4(
            scale, 0, 0, 0,
            0, -scale, 0, 0,
            0, 0, 1, 0,
            (float)translationX, (float)translationY, 0, 1);
    }

    private static void EnsureOpaqueStyles(
        CadDocumentSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        foreach (CadStrokeStyle style in snapshot.Styles.Span)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (style.Alpha != byte.MaxValue)
            {
                throw new NotSupportedException(
                    "CADPAGE118: The pinned page-setup contract does not expose Plot Transparency, so transparent retained styles cannot be lowered without guessing output policy.");
            }
        }
    }
}
