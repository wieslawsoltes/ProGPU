using ProGPU.Backend;
using ProGPU.Scene;
using Silk.NET.WebGPU;

namespace Avalonia.ProGpu;

partial class DrawingContextImpl
{
    internal static CompositorOptions BackendCompositorOptions =>
        AvaloniaGpuDevicePool.Options;

    internal static WgpuContext GetOrCreateStandaloneGpuContext(
        TextureFormat preferredFormat) =>
        AvaloniaGpuDevicePool.GetOrCreateStandalone(
            preferredFormat);

    internal static GpuTexture GetOffscreenTexture(
        OffscreenTextureCache cache,
        WgpuContext context,
        uint width,
        uint height,
        TextureFormat format) =>
        AvaloniaGpuDevicePool.GetOffscreenTexture(
            cache,
            context,
            width,
            height,
            format);

    internal static GpuTextureReadbackBuffer
        GetOffscreenReadbackBuffer(
            OffscreenTextureCache cache,
            WgpuContext context) =>
        cache.CachedReadbackBuffer ??=
            new GpuTextureReadbackBuffer(context);

    internal static void RenderToTexture(
        ProGPU.Scene.DrawingContext commands,
        GpuTexture texture,
        Vector dpi,
        bool isTextureFresh = false) =>
        AvaloniaGpuDevicePool.RenderRecordedCommands(
            commands,
            texture,
            loadExistingContents: !isTextureFresh);

    public static void InvalidateForContext(
        WgpuContext context) =>
        AvaloniaGpuDevicePool.Invalidate(context);

    private void ReportFrame(Compositor compositor)
    {
        CompositorMetrics metrics = compositor.Metrics;
        metrics.PresentationPath = _presentationPath;
        metrics.RecordedCommandCount =
            DrawingContext.Commands.Count;
        metrics.RecordedCommandCapacity =
            DrawingContext.Commands.Capacity;
        metrics.RetainedCompositionPictureCount =
            _resources.CompositionPictureCount;
        metrics.RetainedCompositionPictureHits =
            _resources.CompositionPictureHits;
        metrics.RetainedCompositionPictureMisses =
            _resources.CompositionPictureMisses;
        metrics.RetainedCompositionPictureCompilations =
            _resources.CompositionPictureCompilations;
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
        metrics.RetainedCompositionSceneCount =
            _resources.CompositionSceneCount;
        metrics.RetainedCompositionSceneNodeCount =
            _resources.CompositionSceneNodeCount;
        metrics.RetainedCompositionFallbackNodeCount =
            _resources.CompositionFallbackNodeCount;
        metrics.RetainedCompositionCustomVisualNodeCount =
            _resources.CompositionCustomVisualNodeCount;
        metrics.RetainedCompositionCustomVisualCompilations =
            _resources.CompositionCustomVisualCompilations;
        metrics.RetainedCompositionSceneFullSynchronizations =
            _resources.CompositionSceneFullSynchronizations;
        metrics.RetainedCompositionSceneIncrementalSynchronizations =
            _resources.CompositionSceneIncrementalSynchronizations;
        metrics.RetainedCompositionTopologySynchronizations =
            _resources.CompositionTopologySynchronizations;
        metrics.RetainedCompositionAdornerSynchronizations =
            _resources.CompositionAdornerSynchronizations;
        metrics.RetainedCompositionSceneUnchangedReuses =
            _resources.CompositionSceneUnchangedReuses;
        metrics.RetainedCompositionLayoutClipSynchronizations =
            _resources.CompositionLayoutClipSynchronizations;
        metrics.RetainedCompositionGeometryClipSynchronizations =
            _resources.CompositionGeometryClipSynchronizations;
        metrics.RetainedCompositionBitmapCacheSynchronizations =
            _resources.CompositionBitmapCacheSynchronizations;
        metrics.RetainedCompositionEffectSynchronizations =
            _resources.CompositionEffectSynchronizations;
        metrics.RetainedCompositionOpacityMaskSynchronizations =
            _resources.CompositionOpacityMaskSynchronizations;
        metrics.RetainedCompositionInheritedDrawingOptionsSynchronizations =
            _resources.CompositionInheritedDrawingOptionsSynchronizations;
        metrics.RetainedCompositionComplexAppearanceSynchronizations =
            _resources.CompositionComplexAppearanceSynchronizations;
        if (_compositionBackend is not null)
        {
            _compositionBackend.ReadMetrics(
                out metrics.RetainedCompositionServerBackendRenderCount,
                out metrics.RetainedCompositionSceneCount,
                out metrics.RetainedCompositionSceneNodeCount,
                out metrics.RetainedCompositionFallbackNodeCount,
                out metrics.RetainedCompositionCustomVisualNodeCount,
                out metrics.RetainedCompositionCustomVisualCompilations,
                out metrics.RetainedCompositionSceneFullSynchronizations,
                out metrics.RetainedCompositionSceneIncrementalSynchronizations,
                out metrics.RetainedCompositionTopologySynchronizations,
                out metrics.RetainedCompositionAdornerSynchronizations,
                out metrics.RetainedCompositionSceneUnchangedReuses,
                out metrics.RetainedCompositionLayoutClipSynchronizations,
                out metrics.RetainedCompositionGeometryClipSynchronizations,
                out metrics.RetainedCompositionBitmapCacheSynchronizations,
                out metrics.RetainedCompositionEffectSynchronizations,
                out metrics.RetainedCompositionOpacityMaskSynchronizations,
                out metrics
                    .RetainedCompositionInheritedDrawingOptionsSynchronizations,
                out metrics.RetainedCompositionComplexAppearanceSynchronizations);
        }
#endif
        ProGpuRenderingDiagnostics.ReportFrame(metrics);
    }
}
