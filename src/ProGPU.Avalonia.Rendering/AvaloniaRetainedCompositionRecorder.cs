#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
using System;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition.Drawing;
using Avalonia.Rendering.Composition.Server;
using ProGPU.Backend;
using ProGPU.Scene;

namespace Avalonia.ProGpu;

partial class DrawingContextImpl
{
    internal bool TryRenderRetainedCompositionTarget(
        AvaloniaCompositionScene scene,
        ServerCompositionTarget target,
        ServerCompositionVisual root,
        LtrbRect clip,
        ProGpuCompositionServerBackend backend,
        out int visitedVisuals,
        out int renderedVisuals)
    {
        EnsureAvailable();
        if (!scene.TrySynchronize(
                target,
                root,
                clip,
                this,
                out visitedVisuals,
                out renderedVisuals) ||
            scene.Root is null)
        {
            return false;
        }

        _compositionBackend = backend;
        DrawingContext.DrawVisual(
            scene.Root,
            ToProGpuMatrix(CommandTransform));
        return true;
    }

    bool ICompositionVisualTreeDrawingContextFeature.TryRender(
        ServerCompositionTarget target,
        ServerCompositionVisual root,
        LtrbRect clip,
        out int visitedVisuals,
        out int renderedVisuals)
    {
        EnsureAvailable();
        AvaloniaCompositionScene scene =
            _resources.GetOrCreateCompositionScene(target.Id);
        if (!scene.TrySynchronize(
                target,
                root,
                clip,
                this,
                out visitedVisuals,
                out renderedVisuals) ||
            scene.Root is null)
        {
            _resources.RemoveCompositionScene(target.Id);
            return false;
        }

        DrawingContext.DrawVisual(
            scene.Root,
            ToProGpuMatrix(CommandTransform));
        return true;
    }

    internal void RecordRetainedCompositionVisual(
        ServerCompositionVisual source,
        LtrbRect clip,
        RenderOptions renderOptions,
        TextOptions textOptions,
        ProGPU.Scene.DrawingContext destination)
    {
        RecordInto(
            destination,
            renderOptions,
            textOptions,
            () => source.RenderRetainedContent(this, clip));
    }

    internal (int visited, int rendered)
        RecordRetainedCompositionSubtree(
            ServerCompositionVisual source,
            ProGPU.Scene.DrawingContext destination)
    {
        (int visited, int rendered) result = default;
        RecordInto(
            destination,
            default,
            default,
            () =>
            {
                result = source.Render(
                    this,
                    LtrbRect.Infinite,
                    dirtyRects: null,
                    renderChildren: true,
                    skipRootVisualTransform: false,
                    renderingToBitmapCache: false);
            });
        return result;
    }

    bool ICompositionRenderDataDrawingContextFeature.TryRender(
        ServerCompositionRenderData renderData)
    {
        EnsureAvailable();
        ArgumentNullException.ThrowIfNull(renderData);
        renderData.Render(this);
        return true;
    }

    private void RecordInto(
        ProGPU.Scene.DrawingContext destination,
        RenderOptions renderOptions,
        TextOptions textOptions,
        Action record)
    {
        ProGPU.Scene.DrawingContext previousCommands =
            DrawingContext;
        Matrix previousTransform = _transform;
        double previousOpacity = _opacity;
        RenderOptions previousRenderOptions = RenderOptions;
        TextOptions previousTextOptions = TextOptions;
        RenderCommandPresentationDependencies previousDependencies =
            _presentationDependencies;
        bool previousRetained = _insideRetainedVisual;

        destination.Clear();
        DrawingContext = destination;
        _transform =
            _physicalScale?.Invert() ??
            Matrix.Identity;
        _opacity = 1d;
        RenderOptions = renderOptions;
        TextOptions = textOptions;
        _presentationDependencies =
            RenderCommandPresentationDependencies.TextureSampling |
            RenderCommandPresentationDependencies.TextRendering |
            RenderCommandPresentationDependencies.TextHinting;
        _insideRetainedVisual = true;
        try
        {
            record();
            // Large custom visuals initially grow through pooled scratch
            // storage. Retain the observed high-water capacity once so a
            // subsequent Clear does not return the buffer and force another
            // rent on the next animation frame. Unlike trimming to Count,
            // this also avoids reallocating when command counts fluctuate.
            destination.EnsureCommandCapacity(
                destination.Commands.Count);
        }
        finally
        {
            _insideRetainedVisual = previousRetained;
            _presentationDependencies = previousDependencies;
            TextOptions = previousTextOptions;
            RenderOptions = previousRenderOptions;
            _opacity = previousOpacity;
            _transform = previousTransform;
            DrawingContext = previousCommands;
        }
    }
}
#endif
