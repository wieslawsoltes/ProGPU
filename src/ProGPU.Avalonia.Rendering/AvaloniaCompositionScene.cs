using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Threading;
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
using Avalonia.Platform;
using Avalonia.Rendering.Composition.Server;
#endif
using ProGPU.Scene;
using ProGPU.Text;
using ProGpuRect = ProGPU.Scene.Rect;
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
using AvaloniaRenderOptions = Avalonia.Media.RenderOptions;
using AvaloniaTextOptions = Avalonia.Media.TextOptions;
#endif

namespace Avalonia.ProGpu;

#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
internal enum AvaloniaCompositionEffectKind : byte
{
    None,
    Blur,
    DropShadow
}

/// <summary>
/// Mirrors an Avalonia server visual tree with stable ProGPU visual identities.
/// Synchronization is O(V) for V server nodes when the target revision changes
/// and O(1) for an unchanged target. Command recording is limited to nodes whose
/// retained revision changed.
/// </summary>
internal sealed class AvaloniaCompositionScene : IDisposable
{
    private static long s_nextOwnerId;
    private static readonly bool s_traceFallbacks =
        string.Equals(
            Environment.GetEnvironmentVariable(
                "PROGPU_AVALONIA_TRACE_COMPOSITION_FALLBACKS"),
            "1",
            StringComparison.Ordinal);
    private readonly AvaloniaCompositionStateStore<AvaloniaCompositionVisual>
        _visuals = new();
    private readonly bool _requireNativeCompositionScene;
    private readonly Dictionary<long, CompositionFallbackReason>
        _tracedFallbacks = new();
    private readonly DrawingContext _ordinaryRecordingContext = new();
    private readonly long _ownerId = Interlocked.Increment(ref s_nextOwnerId);
    private ulong _targetRevision = ulong.MaxValue;
    private ulong _synchronizationGeneration;
    private long _rootId;
    private int _visitedVisuals;
    private int _renderedVisuals;

    internal AvaloniaCompositionVisual? Root { get; private set; }
    internal int NodeCount => _visuals.Count;
    internal int FallbackNodeCount { get; private set; }
    internal int CustomVisualNodeCount { get; private set; }
    internal long CustomVisualCompilationCount { get; private set; }
    internal long FullSynchronizationCount { get; private set; }
    internal long IncrementalSynchronizationCount { get; private set; }
    internal long TopologySynchronizationCount { get; private set; }
    internal long AdornerSynchronizationCount { get; private set; }
    internal long UnchangedReuseCount { get; private set; }
    internal long LayoutClipSynchronizationCount { get; private set; }
    internal long GeometryClipSynchronizationCount { get; private set; }
    internal long BitmapCacheSynchronizationCount { get; private set; }
    internal long EffectSynchronizationCount { get; private set; }
    internal long OpacityMaskSynchronizationCount { get; private set; }
    internal long InheritedDrawingOptionsSynchronizationCount
    {
        get;
        private set;
    }
    internal long ComplexAppearanceSynchronizationCount { get; private set; }

    internal AvaloniaCompositionScene(bool requireNativeCompositionScene)
    {
        _requireNativeCompositionScene = requireNativeCompositionScene;
    }

    internal bool TrySynchronize(
        ServerCompositionTarget target,
        ServerCompositionVisual sourceRoot,
        LtrbRect clip,
        DrawingContextImpl renderer,
        out int visitedVisuals,
        out int renderedVisuals)
    {
        if (Root != null &&
            _rootId == sourceRoot.RetainedId &&
            _targetRevision == target.Revision)
        {
            UnchangedReuseCount++;
            visitedVisuals = _visitedVisuals;
            renderedVisuals = _renderedVisuals;
            return true;
        }

        ulong targetRevision = target.Revision;
        if (Root != null &&
            _rootId == sourceRoot.RetainedId &&
            !target.RetainedSceneRequiresFullSync)
        {
            bool incremental = true;
            bool topologyChanged = false;
            bool refreshAllAdornerDependencies = false;
            bool refreshChangedAdorners = false;
            IReadOnlyList<RetainedCompositionVisualDelta> changes =
                target.RetainedChangedVisuals;
            ulong topologySynchronizationGeneration = 0;
            for (int index = 0; index < changes.Count; index++)
            {
                if ((changes[index].Changes &
                        RetainedCompositionVisualChanges.Topology) == 0)
                {
                    continue;
                }

                topologySynchronizationGeneration =
                    topologySynchronizationGeneration != 0
                        ? topologySynchronizationGeneration
                        : NextSynchronizationGeneration();
                if (!TrySynchronizeTopologyDelta(
                        changes[index],
                        targetRevision,
                        topologySynchronizationGeneration,
                        clip,
                        renderer))
                {
                    incremental = false;
                    break;
                }

                topologyChanged = true;
                refreshAllAdornerDependencies = true;
            }

            for (int index = 0; index < changes.Count; index++)
            {
                RetainedCompositionVisualChanges visualChanges =
                    changes[index].Changes;
                refreshAllAdornerDependencies |=
                    (visualChanges &
                        (RetainedCompositionVisualChanges.LayoutClip |
                         RetainedCompositionVisualChanges.GeometryClip)) != 0;
                refreshChangedAdorners |=
                    (visualChanges &
                        (RetainedCompositionVisualChanges.Adorner |
                         RetainedCompositionVisualChanges.Transform)) != 0;
                if (!incremental ||
                    !TrySynchronizeChangedVisual(
                        changes[index],
                        targetRevision,
                        clip,
                        renderer))
                {
                    incremental = false;
                    break;
                }
            }

            if (incremental && topologyChanged)
                RefreshMirrorAccountingAfterTopologyChange();

            if (incremental &&
                (refreshAllAdornerDependencies
                    ? !TryRefreshAllAdornerDependencies()
                    : refreshChangedAdorners &&
                      !TryRefreshChangedAdornerDependencies(changes)))
            {
                incremental = false;
            }

            if (incremental)
            {
                IncrementalSynchronizationCount++;
                _targetRevision = targetRevision;
                target.CompleteRetainedSceneSynchronization(targetRevision);
                visitedVisuals = _visitedVisuals;
                renderedVisuals = _renderedVisuals;
                return true;
            }
        }

        ulong synchronizationGeneration = NextSynchronizationGeneration();
        int visited = 0;
        int rendered = 0;
        if (!TrySynchronizeVisual(
                sourceRoot,
                targetRevision,
                synchronizationGeneration,
                clip,
                renderer,
                inheritedRenderOptions: default,
                inheritedTextOptions: default,
                inheritedDisablesSubpixelText: false,
                ancestorsRenderable: true,
                ref visited,
                ref rendered,
                out AvaloniaCompositionVisual root))
        {
            visitedVisuals = 0;
            renderedVisuals = 0;
            return false;
        }

        RemoveStaleVisuals(synchronizationGeneration);
        if (!TryCaptureAndRefreshAllAdornerDependencies())
        {
            visitedVisuals = 0;
            renderedVisuals = 0;
            return false;
        }
        FullSynchronizationCount++;
        int fallbackNodeCount = 0;
        int customVisualNodeCount = 0;
        for (int index = 0; index < _visuals.AllocatedSlotCount; index++)
        {
            if (!_visuals.TryGetAt(
                    index,
                    out _,
                    out _,
                    out AvaloniaCompositionVisual? visual))
            {
                continue;
            }

            if (visual.IsFallback)
                fallbackNodeCount++;
            if (visual.IsCustomVisual)
                customVisualNodeCount++;
        }
        FallbackNodeCount = fallbackNodeCount;
        CustomVisualNodeCount = customVisualNodeCount;
        Root = root;
        _rootId = sourceRoot.RetainedId;
        _targetRevision = targetRevision;
        _visitedVisuals = visited;
        _renderedVisuals = rendered;
        visitedVisuals = visited;
        renderedVisuals = rendered;
        target.CompleteRetainedSceneSynchronization(targetRevision);
        return true;
    }

    private bool TrySynchronizeTopologyDelta(
        in RetainedCompositionVisualDelta delta,
        ulong targetRevision,
        ulong synchronizationGeneration,
        LtrbRect clip,
        DrawingContextImpl renderer)
    {
        if (!TryGetVisual(
                delta,
                out AvaloniaCompositionVisual? target))
        {
            // A descendant inside a flattened fallback has no independent
            // ProGPU handle. Its nearest fallback ancestor will consume the
            // same transaction through the ordinary changed-visual path.
            for (ServerCompositionVisual? candidate = delta.Source.Parent;
                 candidate != null;
                 candidate = candidate.Parent)
            {
                if (TryGetVisual(
                        candidate,
                        out AvaloniaCompositionVisual? fallback) &&
                    fallback.IsFallback)
                {
                    return true;
                }
            }

            return false;
        }

        if (target.IsFallback)
            return true;

        IReadOnlyList<ServerCompositionVisual>? sourceChildren =
            delta.TopologyChildren;
        if (sourceChildren == null)
            return false;

        bool parentRenderable = IsEffectivelyRenderable(target);
        target.BeginChildSynchronization(sourceChildren.Count);
        for (int index = 0; index < sourceChildren.Count; index++)
        {
            ServerCompositionVisual sourceChild = sourceChildren[index];
            bool created = false;
            if (!TryGetVisual(
                    sourceChild,
                    out AvaloniaCompositionVisual? child))
            {
                int visited = 0;
                int rendered = 0;
                if (!TrySynchronizeVisual(
                        sourceChild,
                        targetRevision,
                        synchronizationGeneration,
                        clip,
                        renderer,
                        target.EffectiveRenderOptions,
                        target.EffectiveTextOptions,
                        target.DisablesSubpixelText,
                        parentRenderable,
                        ref visited,
                        ref rendered,
                        out child))
                {
                    return false;
                }

                created = true;
            }

            ProGPU.Scene.ContainerVisual? previousParent = child.Parent;
            target.AddSynchronizedChild(child);
            if (!created &&
                !ReferenceEquals(previousParent, target))
            {
                bool inheritedStateChanged =
                    child.SynchronizeDrawingOptions(
                        child.LocalRenderOptions,
                        child.LocalTextOptions,
                        target.EffectiveRenderOptions,
                        target.EffectiveTextOptions,
                        target.DisablesSubpixelText,
                        out bool effectiveOptionsChanged);
                if (inheritedStateChanged &&
                    (!TryRefreshDrawingOptionsContent(
                        child,
                        targetRevision,
                        clip,
                        renderer,
                        effectiveOptionsChanged,
                        parentRenderable &&
                            child.IsVisible &&
                            child.Opacity > 0.003f) ||
                     !TryRefreshInheritedDrawingOptionsChildren(
                        child,
                        targetRevision,
                        clip,
                        renderer,
                        parentRenderable &&
                            child.IsVisible &&
                            child.Opacity > 0.003f)))
                {
                    return false;
                }
            }
        }

        target.EndChildSynchronization();
        TopologySynchronizationCount++;
        return true;
    }

    private void RefreshMirrorAccountingAfterTopologyChange()
    {
        ulong synchronizationGeneration = NextSynchronizationGeneration();
        int visited = 0;
        int rendered = 0;
        if (Root is { } root)
        {
            MarkReachableMirror(
                root,
                synchronizationGeneration,
                ancestorsRenderable: true,
                ref visited,
                ref rendered);
        }

        RemoveStaleVisuals(synchronizationGeneration);
        _visitedVisuals = visited;
        _renderedVisuals = rendered;

        int fallbackNodeCount = 0;
        int customVisualNodeCount = 0;
        for (int index = 0; index < _visuals.AllocatedSlotCount; index++)
        {
            if (!_visuals.TryGetAt(
                    index,
                    out _,
                    out _,
                    out AvaloniaCompositionVisual? visual))
            {
                continue;
            }

            if (visual.IsFallback)
                fallbackNodeCount++;
            if (visual.IsCustomVisual)
                customVisualNodeCount++;
        }

        FallbackNodeCount = fallbackNodeCount;
        CustomVisualNodeCount = customVisualNodeCount;
    }

    private static void MarkReachableMirror(
        AvaloniaCompositionVisual visual,
        ulong synchronizationGeneration,
        bool ancestorsRenderable,
        ref int visited,
        ref int rendered)
    {
        visual.SeenSynchronizationGeneration = synchronizationGeneration;
        bool isRenderable =
            ancestorsRenderable &&
            visual.IsVisible &&
            visual.Opacity > 0.003f;
        if (visual.IsFallback)
        {
            visited += Math.Max(1, visual.FallbackVisitedVisuals);
            if (isRenderable)
                rendered += Math.Max(1, visual.FallbackRenderedVisuals);
            return;
        }

        visited++;
        if (isRenderable)
            rendered++;
        IReadOnlyList<ProGPU.Scene.Visual> children = visual.Children;
        for (int index = 0; index < children.Count; index++)
        {
            if (children[index] is AvaloniaCompositionVisual child)
            {
                MarkReachableMirror(
                    child,
                    synchronizationGeneration,
                    isRenderable,
                    ref visited,
                    ref rendered);
            }
        }
    }

    private bool TryCaptureAndRefreshAllAdornerDependencies()
    {
        for (int index = 0; index < _visuals.AllocatedSlotCount; index++)
        {
            if (!_visuals.TryGetAt(
                    index,
                    out _,
                    out _,
                    out AvaloniaCompositionVisual? visual) ||
                visual.IsFallback ||
                visual.Source is not { } source)
            {
                continue;
            }

            AvaloniaCompositionVisual? adornedVisual = null;
            if (source.AdornedVisual != null &&
                !TryGetVisual(source.AdornedVisual, out adornedVisual))
            {
                return false;
            }

            visual.SetAdornerDependency(
                source.AdornerIsClipped,
                adornedVisual);
        }

        return TryRefreshAllAdornerDependencies();
    }

    private bool TryRefreshAllAdornerDependencies()
    {
        for (int index = 0; index < _visuals.AllocatedSlotCount; index++)
        {
            if (!_visuals.TryGetAt(
                    index,
                    out _,
                    out _,
                    out AvaloniaCompositionVisual? visual) ||
                visual.IsFallback ||
                !visual.HasAdornerDependency)
            {
                continue;
            }

            if (!visual.TrySynchronizeAdornerClips())
                return false;
        }

        return true;
    }

    private bool TryRefreshChangedAdornerDependencies(
        IReadOnlyList<RetainedCompositionVisualDelta> changes)
    {
        for (int visualIndex = 0;
             visualIndex < _visuals.AllocatedSlotCount;
             visualIndex++)
        {
            if (!_visuals.TryGetAt(
                    visualIndex,
                    out _,
                    out _,
                    out AvaloniaCompositionVisual? visual) ||
                visual.IsFallback ||
                !visual.HasAdornerDependency)
            {
                continue;
            }

            bool mustRefresh = false;
            for (int changeIndex = 0;
                 changeIndex < changes.Count;
                 changeIndex++)
            {
                RetainedCompositionVisualChanges visualChanges =
                    changes[changeIndex].Changes;
                if ((visualChanges &
                        (RetainedCompositionVisualChanges.Adorner |
                         RetainedCompositionVisualChanges.Transform)) == 0 ||
                    !TryGetVisual(
                        changes[changeIndex],
                        out AvaloniaCompositionVisual? changedVisual))
                {
                    continue;
                }

                if (((visualChanges &
                        RetainedCompositionVisualChanges.Adorner) != 0 &&
                     ReferenceEquals(visual, changedVisual)) ||
                    ((visualChanges &
                        RetainedCompositionVisualChanges.Transform) != 0 &&
                     (ReferenceEquals(visual, changedVisual) ||
                      visual.AdornerPathContains(changedVisual))))
                {
                    mustRefresh = true;
                    break;
                }
            }

            if (mustRefresh && !visual.TrySynchronizeAdornerClips())
                return false;
        }

        return true;
    }

    private bool TrySynchronizeChangedVisual(
        in RetainedCompositionVisualDelta delta,
        ulong targetRevision,
        LtrbRect clip,
        DrawingContextImpl renderer)
    {
        ServerCompositionVisual source = delta.Source;
        for (ServerCompositionVisual? candidate = source;
             candidate != null;
             candidate = candidate.Parent)
        {
            if (TryGetVisual(
                    candidate,
                    out AvaloniaCompositionVisual? fallback) &&
                fallback.IsFallback)
            {
                fallback.SynchronizeFallbackState(candidate);
                if (IsEffectivelyRenderable(fallback) &&
                    fallback.SourceRevision != targetRevision)
                {
                    (int fallbackVisited, int fallbackRendered) =
                        renderer.RecordRetainedCompositionSubtree(
                            candidate,
                            fallback.GetOrCreateCommands());
                    fallback.SourceRevision = targetRevision;
                    fallback.FallbackVisitedVisuals = fallbackVisited;
                    fallback.FallbackRenderedVisuals = fallbackRendered;
                    fallback.InvalidateRecordedContent();
                }

                return true;
            }
        }

        RetainedCompositionVisualChanges changes = delta.Changes;
        if (!TryGetVisual(
                delta,
                out AvaloniaCompositionVisual? target) ||
            target.IsFallback ||
            ((changes & RetainedCompositionVisualChanges.Effect) != 0 &&
                delta.EffectKind ==
                    RetainedCompositionEffectKind.Unsupported) ||
            ((changes & RetainedCompositionVisualChanges.OpacityMask) != 0 &&
                delta.OpacityMaskBrush is { } opacityMask &&
                !DrawingContextImpl.SupportsRetainedCompositionOpacityMask(
                    opacityMask)) ||
            ((changes & RetainedCompositionVisualChanges.GeometryClip) != 0 &&
                delta.GeometryClip is not null and not AvaloniaPathAdapter))
        {
            return false;
        }

        bool wasLocallyRenderable =
            target.IsVisible && target.Opacity > 0.003f;

        bool drawingOptionsChanged = false;
        bool inheritedDrawingStateChanged = false;
        if ((changes & RetainedCompositionVisualChanges.Adorner) != 0)
        {
            AvaloniaCompositionVisual? adornedVisual = null;
            if (delta.AdornedVisual != null &&
                !TryGetVisual(delta.AdornedVisual, out adornedVisual))
            {
                return false;
            }

            target.SetAdornerDependency(
                delta.AdornerIsClipped,
                adornedVisual);
            AdornerSynchronizationCount++;
        }
        bool bitmapCachePolicyChanged = false;
        if ((changes &
                RetainedCompositionVisualChanges.PrimitiveAppearance) != 0)
        {
            target.SynchronizePrimitiveAppearance(
                delta.IsVisible,
                delta.Opacity);
        }
        if ((changes & RetainedCompositionVisualChanges.LayoutClip) != 0)
        {
            LayoutClipSynchronizationCount++;
            target.SynchronizeLayoutClip(
                delta.Size,
                delta.ClipToBounds);
        }
        if ((changes & RetainedCompositionVisualChanges.GeometryClip) != 0)
        {
            GeometryClipSynchronizationCount++;
            target.SynchronizeGeometryClip(
                delta.GeometryClip is AvaloniaPathAdapter geometry
                    ? geometry.Path
                    : null);
        }
        if ((changes & RetainedCompositionVisualChanges.BitmapCache) != 0)
        {
            BitmapCacheSynchronizationCount++;
            bitmapCachePolicyChanged = target.SynchronizeBitmapCache(
                delta.HasBitmapCache,
                delta.BitmapCacheRenderScale,
                delta.BitmapCacheSnapsToDevicePixels,
                delta.BitmapCacheEnableClearType);
        }
        if ((changes & RetainedCompositionVisualChanges.Effect) != 0)
        {
            EffectSynchronizationCount++;
            target.SynchronizeEffect(
                delta.EffectKind switch
                {
                    RetainedCompositionEffectKind.Blur =>
                        AvaloniaCompositionEffectKind.Blur,
                    RetainedCompositionEffectKind.DropShadow =>
                        AvaloniaCompositionEffectKind.DropShadow,
                    _ => AvaloniaCompositionEffectKind.None
                },
                delta.EffectRadius,
                delta.EffectOffset,
                delta.EffectColor,
                delta.EffectOpacity,
                delta.HasEffectOutputBounds,
                delta.EffectOutputBounds,
                delta.Size);
        }
        if ((changes & RetainedCompositionVisualChanges.OpacityMask) != 0)
        {
            OpacityMaskSynchronizationCount++;
            target.SynchronizeOpacityMask(
                delta.OpacityMaskBrush,
                delta.HasOpacityMaskBounds,
                delta.OpacityMaskBounds,
                renderer);
        }
        if ((changes & RetainedCompositionVisualChanges.Transform) != 0)
            target.SynchronizeTransform(delta.Transform);
        if ((changes & RetainedCompositionVisualChanges.Bounds) != 0)
        {
            target.SynchronizeLocalBounds(
                delta.HasLocalBounds,
                delta.LocalBounds);
        }

        bool hasInheritedDrawingOptions =
            (changes &
                RetainedCompositionVisualChanges
                    .InheritedDrawingOptions) != 0;
        if (hasInheritedDrawingOptions || bitmapCachePolicyChanged)
        {
            InheritedDrawingOptionsSynchronizationCount++;
            inheritedDrawingStateChanged =
                target.SynchronizeDrawingOptions(
                    hasInheritedDrawingOptions
                        ? delta.RenderOptions
                        : target.LocalRenderOptions,
                    hasInheritedDrawingOptions
                        ? delta.TextOptions
                        : target.LocalTextOptions,
                    GetInheritedRenderOptions(target),
                    GetInheritedTextOptions(target),
                    GetInheritedDisablesSubpixelText(target),
                    out drawingOptionsChanged);
        }
        if (inheritedDrawingStateChanged &&
            !TryRefreshInheritedDrawingOptionsSubtree(
                target,
                targetRevision,
                clip,
                renderer,
                drawingOptionsChanged))
        {
            return false;
        }
        bool isEffectivelyRenderable = IsEffectivelyRenderable(target);
        if (isEffectivelyRenderable &&
            target.SourceRevision != delta.ContentRevision)
        {
            bool contentChanged = RecordRetainedCompositionVisual(
                renderer,
                source,
                clip,
                target.EffectiveRenderOptions,
                target.EffectiveTextOptions,
                target);
            target.SourceRevision = delta.ContentRevision;
            if (contentChanged)
                target.InvalidateRecordedContent();
        }

        bool isLocallyRenderable =
            target.IsVisible && target.Opacity > 0.003f;
        if (!wasLocallyRenderable && isLocallyRenderable &&
            isEffectivelyRenderable &&
            !TryMaterializeDeferredSubtree(
                source,
                targetRevision,
                clip,
                renderer,
                ancestorsRenderable: true))
        {
            return false;
        }

        target.StateRevision = delta.Revision;
        return true;
    }

    private bool TryRefreshInheritedDrawingOptionsSubtree(
        AvaloniaCompositionVisual root,
        ulong targetRevision,
        LtrbRect clip,
        DrawingContextImpl renderer,
        bool rootEffectiveOptionsChanged)
    {
        bool rootRenderable = IsEffectivelyRenderable(root);
        if (!TryRefreshDrawingOptionsContent(
                root,
                targetRevision,
                clip,
                renderer,
                rootEffectiveOptionsChanged,
                rootRenderable))
        {
            return false;
        }

        return TryRefreshInheritedDrawingOptionsChildren(
            root,
            targetRevision,
            clip,
            renderer,
            rootRenderable);
    }

    private bool TryRefreshInheritedDrawingOptionsChildren(
        AvaloniaCompositionVisual parent,
        ulong targetRevision,
        LtrbRect clip,
        DrawingContextImpl renderer,
        bool parentRenderable)
    {
        IReadOnlyList<ProGPU.Scene.Visual> children = parent.Children;
        for (int index = 0; index < children.Count; index++)
        {
            if (children[index] is not AvaloniaCompositionVisual child)
                continue;

            bool childRenderable =
                parentRenderable &&
                child.IsVisible &&
                child.Opacity > 0.003f;
            if (child.IsFallback)
            {
                if (!TryRefreshDrawingOptionsContent(
                        child,
                        targetRevision,
                        clip,
                        renderer,
                        effectiveOptionsChanged: true,
                        childRenderable))
                {
                    return false;
                }
                continue;
            }

            bool inheritedStateChanged =
                child.SynchronizeDrawingOptions(
                    child.LocalRenderOptions,
                    child.LocalTextOptions,
                    parent.EffectiveRenderOptions,
                    parent.EffectiveTextOptions,
                    parent.DisablesSubpixelText,
                    out bool effectiveOptionsChanged);
            if (!inheritedStateChanged)
                continue;

            if (!TryRefreshDrawingOptionsContent(
                    child,
                    targetRevision,
                    clip,
                    renderer,
                    effectiveOptionsChanged,
                    childRenderable) ||
                !TryRefreshInheritedDrawingOptionsChildren(
                    child,
                    targetRevision,
                    clip,
                    renderer,
                    childRenderable))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryRefreshDrawingOptionsContent(
        AvaloniaCompositionVisual target,
        ulong targetRevision,
        LtrbRect clip,
        DrawingContextImpl renderer,
        bool effectiveOptionsChanged,
        bool isRenderable)
    {
        if (!effectiveOptionsChanged)
            return true;

        if (!target.IsFallback)
        {
            // Ordinary retained commands carry a typed dependency mask for
            // inherited sampling and text presentation. The cache applies the
            // current effective values while expanding each command, so the
            // immutable geometry/glyph payload is reused and only compiled
            // scene state is invalidated.
            target.InvalidatePresentationState();
            return true;
        }

        if (target.Source is not { } source)
            return false;

        if (!isRenderable)
        {
            target.SourceRevision = ulong.MaxValue;
            return true;
        }

        (int fallbackVisited, int fallbackRendered) =
            renderer.RecordRetainedCompositionSubtree(
                source,
                target.GetOrCreateCommands());
        target.SourceRevision = targetRevision;
        target.FallbackVisitedVisuals = fallbackVisited;
        target.FallbackRenderedVisuals = fallbackRendered;
        target.InvalidateRecordedContent();
        return true;
    }

    private static AvaloniaRenderOptions GetInheritedRenderOptions(
        AvaloniaCompositionVisual target) =>
        target.Parent is AvaloniaCompositionVisual parent
            ? parent.EffectiveRenderOptions
            : default;

    private static AvaloniaTextOptions GetInheritedTextOptions(
        AvaloniaCompositionVisual target) =>
        target.Parent is AvaloniaCompositionVisual parent
            ? parent.EffectiveTextOptions
            : default;

    private static bool GetInheritedDisablesSubpixelText(
        AvaloniaCompositionVisual target) =>
        target.Parent is AvaloniaCompositionVisual parent &&
        parent.DisablesSubpixelText;

    private bool TrySynchronizeVisual(
        ServerCompositionVisual source,
        ulong targetRevision,
        ulong synchronizationGeneration,
        LtrbRect clip,
        DrawingContextImpl renderer,
        AvaloniaRenderOptions inheritedRenderOptions,
        AvaloniaTextOptions inheritedTextOptions,
        bool inheritedDisablesSubpixelText,
        bool ancestorsRenderable,
        ref int visited,
        ref int rendered,
        out AvaloniaCompositionVisual target)
    {
        visited++;
        bool isRenderable =
            ancestorsRenderable && source.Visible && source.Opacity > 0.003f;
        if (isRenderable)
            rendered++;

        if (!TryGetVisual(source, out target!))
        {
            target = new AvaloniaCompositionVisual();
            ulong handle = _visuals.Allocate(source.RetainedId, target);
            target.AttachSource(source, handle);
            source.RetainedBackendOwner = _ownerId;
            source.RetainedBackendHandle = handle;
        }
        target.SeenSynchronizationGeneration = synchronizationGeneration;

        CompositionFallbackReason fallbackReason = GetFallbackReason(source);
        if (fallbackReason != CompositionFallbackReason.None)
        {
            if (_requireNativeCompositionScene)
            {
                throw new InvalidOperationException(
                    "The retained ProGPU composition scene cannot represent " +
                    $"visual {source.RetainedId}: {fallbackReason}. " +
                    "Disable ProGpuOptions.RequireNativeCompositionScene only " +
                    "for an explicit flattened-fallback comparison.");
            }

            TraceFallback(source);
            bool wasFallback = target.IsFallback;
            target.SynchronizeFallbackState(source);
            target.IsFallback = true;
            target.StateRevision = source.RetainedRevision;
            if (!wasFallback && !isRenderable)
            {
                target.ClearCommands();
                target.SourceRevision = ulong.MaxValue;
                target.InvalidateRecordedContent();
            }
            if (isRenderable &&
                (!wasFallback || target.SourceRevision != targetRevision))
            {
                (int fallbackVisited, int fallbackRendered) =
                    renderer.RecordRetainedCompositionSubtree(
                        source,
                        target.GetOrCreateCommands());
                target.SourceRevision = targetRevision;
                target.FallbackVisitedVisuals = fallbackVisited;
                target.FallbackRenderedVisuals = fallbackRendered;
                target.InvalidateRecordedContent();
            }

            visited += Math.Max(0, target.FallbackVisitedVisuals - 1);
            rendered += Math.Max(0, target.FallbackRenderedVisuals -
                (source.Visible && source.Opacity > 0.003f ? 1 : 0));
            target.BeginChildSynchronization(0);
            target.EndChildSynchronization();
            return true;
        }

        bool wasFallbackNode = target.IsFallback;
        bool drawingOptionsChanged = target.SynchronizeState(
            source,
            renderer,
            source.Visible,
            source.Opacity,
            inheritedRenderOptions,
            inheritedTextOptions,
            inheritedDisablesSubpixelText,
            out _);
        target.IsFallback = false;
        target.StateRevision = source.RetainedRevision;
        if (wasFallbackNode && !isRenderable)
        {
            target.ClearCommands();
            target.SourceRevision = ulong.MaxValue;
            target.InvalidateRecordedContent();
        }
        if (isRenderable &&
            (wasFallbackNode ||
             drawingOptionsChanged ||
             target.SourceRevision != source.RetainedContentRevision))
        {
            bool contentChanged = RecordRetainedCompositionVisual(
                renderer,
                source,
                clip,
                target.EffectiveRenderOptions,
                target.EffectiveTextOptions,
                target);
            target.SourceRevision = source.RetainedContentRevision;
            if (wasFallbackNode || contentChanged)
                target.InvalidateRecordedContent();
            else if (drawingOptionsChanged)
                target.InvalidatePresentationState();
        }

        var sourceChildren = source.Children!.List;
        int childCount = sourceChildren.Count;
        target.BeginChildSynchronization(childCount);
        for (int index = 0; index < childCount; index++)
        {
            if (!TrySynchronizeVisual(
                    sourceChildren[index],
                    targetRevision,
                    synchronizationGeneration,
                    clip,
                    renderer,
                    target.EffectiveRenderOptions,
                    target.EffectiveTextOptions,
                    target.DisablesSubpixelText,
                    isRenderable,
                    ref visited,
                    ref rendered,
                    out AvaloniaCompositionVisual synchronizedChild))
            {
                return false;
            }

            target.AddSynchronizedChild(synchronizedChild);
        }

        target.EndChildSynchronization();
        return true;
    }

    private bool TryMaterializeDeferredSubtree(
        ServerCompositionVisual source,
        ulong targetRevision,
        LtrbRect clip,
        DrawingContextImpl renderer,
        bool ancestorsRenderable)
    {
        bool isRenderable =
            ancestorsRenderable && source.Visible && source.Opacity > 0.003f;
        if (!isRenderable)
            return true;

        if (!TryGetVisual(
                source,
                out AvaloniaCompositionVisual? target))
        {
            return false;
        }

        if (target.IsFallback)
        {
            if (target.SourceRevision != targetRevision)
            {
                (int fallbackVisited, int fallbackRendered) =
                    renderer.RecordRetainedCompositionSubtree(
                        source,
                        target.GetOrCreateCommands());
                target.SourceRevision = targetRevision;
                target.FallbackVisitedVisuals = fallbackVisited;
                target.FallbackRenderedVisuals = fallbackRendered;
                target.InvalidateRecordedContent();
            }

            return true;
        }

        if (target.SourceRevision != source.RetainedContentRevision)
        {
            bool contentChanged = RecordRetainedCompositionVisual(
                renderer,
                source,
                clip,
                target.EffectiveRenderOptions,
                target.EffectiveTextOptions,
                target);
            target.SourceRevision = source.RetainedContentRevision;
            if (contentChanged)
                target.InvalidateRecordedContent();
        }

        var children = source.Children!.List;
        for (int index = 0; index < children.Count; index++)
        {
            if (!TryMaterializeDeferredSubtree(
                    children[index],
                    targetRevision,
                    clip,
                    renderer,
                    isRenderable))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEffectivelyRenderable(AvaloniaCompositionVisual visual)
    {
        for (ProGPU.Scene.Visual? candidate = visual;
             candidate != null;
             candidate = candidate.Parent)
        {
            if (!candidate.IsVisible || candidate.Opacity <= 0.003f)
                return false;
        }

        return true;
    }

    private bool RecordRetainedCompositionVisual(
        DrawingContextImpl renderer,
        ServerCompositionVisual source,
        LtrbRect clip,
        AvaloniaRenderOptions renderOptions,
        AvaloniaTextOptions textOptions,
        AvaloniaCompositionVisual target)
    {
        if (source.RetainedOwnContentBounds is null)
        {
            bool contentChanged = target.HasRecordedCommands;
            target.ClearCommands();
            return contentChanged;
        }

        if (source is ServerCompositionCustomVisual)
        {
            renderer.RecordRetainedCompositionVisual(
                source,
                clip,
                renderOptions,
                textOptions,
                target.BeginCommandRecording());
            CustomVisualCompilationCount++;
            return true;
        }

        _ordinaryRecordingContext.Clear();
        try
        {
            renderer.RecordRetainedCompositionVisual(
                source,
                clip,
                renderOptions,
                textOptions,
                _ordinaryRecordingContext);
            if (target.TryCompleteCompactRecording(
                    _ordinaryRecordingContext,
                    out bool contentChanged))
            {
                return contentChanged;
            }

            renderer.RecordRetainedCompositionVisual(
                source,
                clip,
                renderOptions,
                textOptions,
                target.BeginCommandRecording());
            return true;
        }
        finally
        {
            _ordinaryRecordingContext.Clear();
        }
    }

    private static CompositionFallbackReason GetFallbackReason(
        ServerCompositionVisual source)
    {
        if (source.OpacityMaskBrush is { } opacityMask &&
            !DrawingContextImpl.SupportsRetainedCompositionOpacityMask(
                opacityMask))
        {
            return CompositionFallbackReason.UnsupportedOpacityMask;
        }

        if (source.Effect is { } effect &&
            !AvaloniaCompositionVisual.SupportsRetainedEffect(effect))
        {
            return CompositionFallbackReason.UnsupportedEffect;
        }
        if (source.Clip is not null and not AvaloniaPathAdapter)
            return CompositionFallbackReason.UnsupportedGeometryClip;
        if (source.AdornedVisual != null &&
            source.AdornerIsClipped &&
            !CanRepresentAdornerClip(source))
            return CompositionFallbackReason.ClippedAdorner;
        return CompositionFallbackReason.None;
    }

    private static bool CanRepresentAdornerClip(
        ServerCompositionVisual source)
    {
        ServerCompositionVisual? sharedAncestor = source.Parent?.Parent;
        if (sharedAncestor == null || source.AdornedVisual == null)
            return true;

        for (ServerCompositionVisual? candidate = source.AdornedVisual;
             candidate != null;
             candidate = candidate.Parent)
        {
            if (candidate.Clip is not null and not AvaloniaPathAdapter)
                return false;
            if (ReferenceEquals(candidate, sharedAncestor))
                return true;
        }

        return false;
    }

    private void TraceFallback(ServerCompositionVisual source)
    {
        if (!s_traceFallbacks)
            return;

        CompositionFallbackReason reason = GetFallbackReason(source);
        if (_tracedFallbacks.TryGetValue(source.RetainedId, out var previous) &&
            previous == reason)
        {
            return;
        }

        _tracedFallbacks[source.RetainedId] = reason;
        Console.Error.WriteLine(
            $"[Avalonia.ProGpu] composition fallback" +
            $" visual={source.RetainedId} reason={reason}");
    }

    private bool TryGetVisual(
        ServerCompositionVisual source,
        [NotNullWhen(true)] out AvaloniaCompositionVisual? target)
    {
        if (source.RetainedBackendOwner == _ownerId &&
            _visuals.TryGet(
                source.RetainedBackendHandle,
                source.RetainedId,
                out target))
        {
            return true;
        }

        target = null;
        return false;
    }

    private bool TryGetVisual(
        in RetainedCompositionVisualDelta delta,
        [NotNullWhen(true)] out AvaloniaCompositionVisual? target)
    {
        long backendOwner = delta.BackendOwner;
        ulong backendHandle = delta.BackendHandle;
        if (backendOwner == 0 &&
            backendHandle == 0 &&
            delta.Source.RetainedId == delta.RetainedId &&
            delta.Source.RetainedBackendOwner == _ownerId)
        {
            // An earlier parent-topology delta can materialize a newly
            // attached child in this same immutable transaction. Reconcile
            // only that unassigned identity transition; all visual state
            // continues to come from the delta snapshot.
            backendOwner = delta.Source.RetainedBackendOwner;
            backendHandle = delta.Source.RetainedBackendHandle;
        }

        if (backendOwner == _ownerId &&
            _visuals.TryGet(
                backendHandle,
                delta.RetainedId,
                out target) &&
            ReferenceEquals(target.Source, delta.Source))
        {
            return true;
        }

        target = null;
        return false;
    }

    private ulong NextSynchronizationGeneration()
    {
        unchecked
        {
            _synchronizationGeneration++;
        }

        if (_synchronizationGeneration == 0)
            _synchronizationGeneration = 1;
        return _synchronizationGeneration;
    }

    private void RemoveStaleVisuals(ulong synchronizationGeneration)
    {
        for (int index = 0; index < _visuals.AllocatedSlotCount; index++)
        {
            if (!_visuals.TryGetAt(
                    index,
                    out ulong handle,
                    out long retainedId,
                    out AvaloniaCompositionVisual? visual) ||
                visual.SeenSynchronizationGeneration ==
                    synchronizationGeneration)
            {
                continue;
            }

            visual.ClearBackendHandle(_ownerId, handle);
            if (_visuals.Release(handle, retainedId, out var released))
                released.Dispose();
            _tracedFallbacks.Remove(retainedId);
        }
    }

    public void Dispose()
    {
        _ordinaryRecordingContext.Clear();
        for (int index = 0; index < _visuals.AllocatedSlotCount; index++)
        {
            if (!_visuals.TryGetAt(
                    index,
                    out ulong handle,
                    out _,
                    out AvaloniaCompositionVisual? visual))
            {
                continue;
            }

            visual.ClearBackendHandle(_ownerId, handle);
            visual.Dispose();
        }
        _visuals.Clear();
        _tracedFallbacks.Clear();
        Root = null;
    }

    private enum CompositionFallbackReason
    {
        None,
        UnsupportedOpacityMask,
        UnsupportedEffect,
        UnsupportedGeometryClip,
        ClippedAdorner
    }
}

internal sealed class AvaloniaCompositionVisual : ContainerVisual,
    IIncrementalRenderCommandCache,
    IDisposable
{
    private ProGpuRect? _localRenderBounds;
    private ProGpuRect? _geometryClipBounds;
    private bool _clipToBounds;
    private int _synchronizedChildCount;
    private bool _adornerIsClipped;
    private AvaloniaCompositionVisual? _adornedVisual;
    private List<AvaloniaCompositionVisual>? _adornerPath;
    private List<VisualCompositeClip>? _adornerClips;
    private bool _cacheEnableClearType;
    private GpuPicture? _ownedOpacityMaskPicture;
    private AvaloniaRetainedCommandCache? _commands;

    internal ServerCompositionVisual? Source { get; private set; }
    internal ulong BackendHandle { get; private set; }
    internal ulong SeenSynchronizationGeneration { get; set; }
    internal ulong SourceRevision { get; set; } = ulong.MaxValue;
    internal ulong StateRevision { get; set; }
    internal bool IsFallback { get; set; }
    internal bool IsCustomVisual { get; private set; }
    internal bool HasAdornerDependency =>
        _adornerIsClipped && _adornedVisual != null;

    internal bool AdornerPathContains(
        AvaloniaCompositionVisual candidate)
    {
        if (_adornerPath == null)
            return false;

        for (int index = 0; index < _adornerPath.Count; index++)
        {
            if (ReferenceEquals(_adornerPath[index], candidate))
                return true;
        }

        return false;
    }
    internal int FallbackVisitedVisuals { get; set; }
    internal int FallbackRenderedVisuals { get; set; }
    internal AvaloniaRenderOptions LocalRenderOptions { get; private set; }
    internal AvaloniaTextOptions LocalTextOptions { get; private set; }
    internal AvaloniaRenderOptions EffectiveRenderOptions { get; private set; }
    internal AvaloniaTextOptions EffectiveTextOptions { get; private set; }
    internal bool DisablesSubpixelText { get; private set; }

    public override ProGpuRect? LocalRenderBounds => _localRenderBounds;
    internal bool HasRecordedCommands => _commands?.Count > 0;
    bool IIncrementalRenderCommandCache.CanCacheIncrementalPage =>
        !IsCustomVisual;
    IncrementalRenderPresentationState
        IIncrementalRenderCommandCache.IncrementalPresentationState =>
            GetIncrementalPresentationState();

    internal DrawingContext GetOrCreateCommands() =>
        (_commands ??= new AvaloniaRetainedCommandCache())
        .GetOrCreateContext();

    internal DrawingContext BeginCommandRecording()
    {
        AvaloniaRetainedCommandCache commands =
            _commands ??= new AvaloniaRetainedCommandCache();
        return commands.BeginRecording();
    }

    internal bool TryCompleteCompactRecording(
        DrawingContext source,
        out bool contentChanged) =>
        (_commands ??= new AvaloniaRetainedCommandCache())
        .TryCompactOrdinaryCommands(source, out contentChanged);

    internal void AttachSource(
        ServerCompositionVisual source,
        ulong backendHandle)
    {
        Source = source;
        BackendHandle = backendHandle;
    }

    internal void ClearBackendHandle(long ownerId, ulong backendHandle)
    {
        if (Source is { } source &&
            source.RetainedBackendOwner == ownerId &&
            source.RetainedBackendHandle == backendHandle)
        {
            source.RetainedBackendOwner = 0;
            source.RetainedBackendHandle = 0;
        }

        Source = null;
        BackendHandle = 0;
    }

    internal void ClearCommands()
    {
        _commands?.Clear();
        _commands = null;
    }

    internal bool SynchronizeState(
        ServerCompositionVisual source,
        DrawingContextImpl renderer,
        bool isVisible,
        float opacity,
        AvaloniaRenderOptions inheritedRenderOptions,
        AvaloniaTextOptions inheritedTextOptions,
        bool inheritedDisablesSubpixelText,
        out bool inheritedDrawingStateChanged)
    {
        bool hasBitmapCache =
            source.CacheMode is ServerCompositionBitmapCache;
        float cacheRenderScale = hasBitmapCache
            ? (float)Math.Max(
                0d,
                ((ServerCompositionBitmapCache)source.CacheMode!).RenderAtScale)
            : 1f;
        bool cacheSnapsToDevicePixels = hasBitmapCache &&
            ((ServerCompositionBitmapCache)source.CacheMode!)
            .SnapsToDevicePixels;
        bool cacheEnableClearType = hasBitmapCache &&
            ((ServerCompositionBitmapCache)source.CacheMode!)
            .EnableClearType;
        SynchronizeBitmapCache(
            hasBitmapCache,
            cacheRenderScale,
            cacheSnapsToDevicePixels,
            cacheEnableClearType);
        inheritedDrawingStateChanged = SynchronizeDrawingOptions(
            source.RenderOptions,
            source.TextOptions,
            inheritedRenderOptions,
            inheritedTextOptions,
            inheritedDisablesSubpixelText,
            out bool drawingOptionsChanged);
        IsCustomVisual = source is ServerCompositionCustomVisual;

        IsVisible = isVisible;
        Opacity = opacity;
        Offset = Vector2.Zero;
        Scale = Vector3.One;
        Rotation = 0f;
        CenterPoint = Vector3.Zero;
        SynchronizeTransform(source);
        SynchronizeGeometryClip(
            source.Clip is AvaloniaPathAdapter geometry
                ? geometry.Path
                : null);
        SynchronizeLayoutClip(
            new Vector2((float)source.Size.X, (float)source.Size.Y),
            source.ClipToBounds);
        SynchronizeEffect(source);
        if (source.SubTreeBounds is { } opacityMaskBounds)
        {
            SynchronizeOpacityMask(
                source.OpacityMaskBrush,
                true,
                new Vector4(
                    (float)opacityMaskBounds.Left,
                    (float)opacityMaskBounds.Top,
                    (float)(opacityMaskBounds.Right -
                        opacityMaskBounds.Left),
                    (float)(opacityMaskBounds.Bottom -
                        opacityMaskBounds.Top)),
                renderer);
        }
        else
        {
            SynchronizeOpacityMask(
                source.OpacityMaskBrush,
                false,
                default,
                renderer);
        }
        SynchronizeLocalBounds(source);

        return drawingOptionsChanged;
    }

    internal void SynchronizeOpacityMask(
        Avalonia.Media.IBrush? opacityMask,
        bool hasBounds,
        Vector4 bounds,
        DrawingContextImpl renderer)
    {
        if (opacityMask is not null && hasBounds)
        {
            var avaloniaMaskBounds = new Avalonia.Rect(
                bounds.X,
                bounds.Y,
                bounds.Z,
                bounds.W);
            if (DrawingContextImpl.SupportsRetainedCompositionBrush(
                    opacityMask))
            {
                ReplaceOwnedOpacityMaskPicture(null);
                OpacityMask = renderer.ConvertRetainedCompositionBrush(
                    opacityMask,
                    avaloniaMaskBounds);
            }
            else
            {
                OpacityMask = null;
                ReplaceOwnedOpacityMaskPicture(
                    renderer.RecordRetainedCompositionOpacityMask(
                        opacityMask,
                        avaloniaMaskBounds));
            }
            OpacityMaskBounds = new ProGpuRect(
                bounds.X,
                bounds.Y,
                bounds.Z,
                bounds.W);
        }
        else
        {
            OpacityMask = null;
            ReplaceOwnedOpacityMaskPicture(null);
            OpacityMaskBounds = null;
        }
    }

    internal void SynchronizeGeometryClip(
        ProGPU.Vector.PathGeometry? geometry)
    {
        _geometryClipBounds = null;
        if (geometry != null)
        {
            if (ProGPU.Vector.PrimitivePathGeometry
                .TryGetAxisAlignedRectangleBounds(
                    geometry,
                    out Vector2 clipMin,
                    out Vector2 clipMax))
            {
                var geometryBounds = new ProGpuRect(
                    clipMin.X,
                    clipMin.Y,
                    clipMax.X - clipMin.X,
                    clipMax.Y - clipMin.Y);
                _geometryClipBounds = geometryBounds;
                GeometryClip = null;
            }
            else
            {
                GeometryClip = geometry;
            }
        }
        else
        {
            GeometryClip = null;
        }

        UpdateCompositeClip();
    }

    internal void SynchronizePrimitiveAppearance(
        bool isVisible,
        float opacity)
    {
        IsVisible = isVisible;
        Opacity = opacity;
    }

    internal void SynchronizeLayoutClip(
        in Vector2 size,
        bool clipToBounds)
    {
        Size = size;
        _clipToBounds = clipToBounds;
        UpdateCompositeClip();
    }

    internal bool SynchronizeBitmapCache(
        bool hasBitmapCache,
        float renderScale,
        bool snapsToDevicePixels,
        bool enableClearType)
    {
        bool disabledSubpixelText =
            CacheAsLayer && !_cacheEnableClearType;
        bool disablesSubpixelText =
            hasBitmapCache && !enableClearType;

        CacheAsLayer = hasBitmapCache;
        LayerCacheRenderScale = renderScale;
        LayerCacheSnapsToDevicePixels = snapsToDevicePixels;
        _cacheEnableClearType = enableClearType;

        return disabledSubpixelText != disablesSubpixelText;
    }

    internal bool SynchronizeDrawingOptions(
        AvaloniaRenderOptions localRenderOptions,
        AvaloniaTextOptions localTextOptions,
        AvaloniaRenderOptions inheritedRenderOptions,
        AvaloniaTextOptions inheritedTextOptions,
        bool inheritedDisablesSubpixelText,
        out bool effectiveOptionsChanged)
    {
        AvaloniaRenderOptions effectiveRenderOptions =
            localRenderOptions.MergeWith(inheritedRenderOptions);
        AvaloniaTextOptions effectiveTextOptions =
            localTextOptions.MergeWith(inheritedTextOptions);
        bool disablesSubpixelText =
            inheritedDisablesSubpixelText ||
            (CacheAsLayer && !_cacheEnableClearType);
        if (disablesSubpixelText &&
            effectiveTextOptions.TextRenderingMode ==
                Avalonia.Media.TextRenderingMode.SubpixelAntialias)
        {
            effectiveTextOptions = effectiveTextOptions with
            {
                TextRenderingMode =
                    Avalonia.Media.TextRenderingMode.Antialias
            };
        }

        effectiveOptionsChanged =
            EffectiveRenderOptions != effectiveRenderOptions ||
            EffectiveTextOptions != effectiveTextOptions;
        bool inheritedStateChanged =
            effectiveOptionsChanged ||
            DisablesSubpixelText != disablesSubpixelText;

        LocalRenderOptions = localRenderOptions;
        LocalTextOptions = localTextOptions;
        EffectiveRenderOptions = effectiveRenderOptions;
        EffectiveTextOptions = effectiveTextOptions;
        DisablesSubpixelText = disablesSubpixelText;
        return inheritedStateChanged;
    }

    private void UpdateCompositeClip()
    {
        ProGpuRect? layoutBounds = _clipToBounds
            ? new ProGpuRect(0f, 0f, Size.X, Size.Y)
            : null;
        ClipBounds = _geometryClipBounds switch
        {
            { } geometryBounds when layoutBounds is { } currentLayout =>
                Intersect(currentLayout, geometryBounds),
            { } geometryBounds => geometryBounds,
            _ => layoutBounds
        };
    }

    internal void SynchronizeTransform(ServerCompositionVisual source)
    {
        Transform = DrawingContextImpl.ToProGpuMatrix(
            source.RetainedOwnTransform ?? Avalonia.Matrix.Identity);
    }

    internal void SynchronizeTransform(in Matrix3x2 transform)
    {
        Transform = new Matrix4x4(
            transform.M11,
            transform.M12,
            0f,
            0f,
            transform.M21,
            transform.M22,
            0f,
            0f,
            0f,
            0f,
            1f,
            0f,
            transform.M31,
            transform.M32,
            0f,
            1f);
    }

    internal void SynchronizeLocalBounds(ServerCompositionVisual source)
    {
        ProGpuRect? localBounds = source.RetainedOwnContentBounds is { } bounds
            ? new ProGpuRect(
                (float)bounds.Left,
                (float)bounds.Top,
                (float)(bounds.Right - bounds.Left),
                (float)(bounds.Bottom - bounds.Top))
            : null;
        if (_localRenderBounds != localBounds)
        {
            _localRenderBounds = localBounds;
            InvalidateVisualState();
        }
    }

    internal void SynchronizeLocalBounds(
        bool hasLocalBounds,
        in Vector4 bounds)
    {
        ProGpuRect? localBounds = hasLocalBounds
            ? new ProGpuRect(
                bounds.X,
                bounds.Y,
                bounds.Z,
                bounds.W)
            : null;
        if (_localRenderBounds != localBounds)
        {
            _localRenderBounds = localBounds;
            InvalidateVisualState();
        }
    }

    internal void SynchronizeFallbackState(ServerCompositionVisual source)
    {
        IsVisible = true;
        IsCustomVisual = false;
        Opacity = 1f;
        Size = new Vector2((float)source.Size.X, (float)source.Size.Y);
        Offset = Vector2.Zero;
        Scale = Vector3.One;
        Rotation = 0f;
        CenterPoint = Vector3.Zero;
        Transform = Matrix4x4.Identity;
        _geometryClipBounds = null;
        _clipToBounds = false;
        ClipBounds = null;
        GeometryClip = null;
        Effect = null;
        EffectContentBounds = null;
        EffectRasterPadding = null;
        _adornerIsClipped = false;
        _adornedVisual = null;
        _adornerPath?.Clear();
        _adornerClips?.Clear();
        SetOuterCompositeClips(
            _adornerClips is null
                ? Array.Empty<VisualCompositeClip>()
                : _adornerClips);
        OpacityMask = null;
        ReplaceOwnedOpacityMaskPicture(null);
        OpacityMaskBounds = null;
        CacheAsLayer = false;
        LayerCacheRenderScale = 1f;
        LayerCacheSnapsToDevicePixels = false;
        _cacheEnableClearType = false;

        ProGpuRect? localBounds = source.SubTreeBounds is { } bounds
            ? new ProGpuRect(
                (float)bounds.Left,
                (float)bounds.Top,
                (float)(bounds.Right - bounds.Left),
                (float)(bounds.Bottom - bounds.Top))
            : null;
        if (_localRenderBounds != localBounds)
        {
            _localRenderBounds = localBounds;
            InvalidateVisualState();
        }
    }

    internal void BeginChildSynchronization(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _synchronizedChildCount = 0;
    }

    internal void AddSynchronizedChild(AvaloniaCompositionVisual child)
    {
        int index = _synchronizedChildCount++;
        if (index < Children.Count &&
            ReferenceEquals(Children[index], child))
        {
            return;
        }

        InsertChild(index, child);
    }

    internal void EndChildSynchronization()
    {
        while (Children.Count > _synchronizedChildCount)
        {
            RemoveChild(Children[^1]);
        }
    }

    internal void InvalidateRecordedContent() => Invalidate();

    internal void InvalidatePresentationState() =>
        InvalidateVisualState();

    private static ProGpuRect Intersect(
        in ProGpuRect left,
        in ProGpuRect right)
    {
        float x = MathF.Max(left.X, right.X);
        float y = MathF.Max(left.Y, right.Y);
        float rightEdge = MathF.Min(
            left.X + left.Width,
            right.X + right.Width);
        float bottomEdge = MathF.Min(
            left.Y + left.Height,
            right.Y + right.Height);
        return new ProGpuRect(
            x,
            y,
            MathF.Max(0f, rightEdge - x),
            MathF.Max(0f, bottomEdge - y));
    }

    internal static bool SupportsRetainedEffect(
        Avalonia.Media.IEffect effect) =>
        effect is Avalonia.Media.IBlurEffect or
            Avalonia.Media.IDropShadowEffect;

    private void SynchronizeEffect(ServerCompositionVisual source)
    {
        bool hasOutputBounds = TryGetEffectOutputBounds(
            source,
            out Vector4 outputBounds);
        Vector2 size = new(
            (float)source.Size.X,
            (float)source.Size.Y);
        switch (source.Effect)
        {
            case Avalonia.Media.IBlurEffect blur:
                SynchronizeEffect(
                    AvaloniaCompositionEffectKind.Blur,
                    (float)blur.Radius,
                    default,
                    0,
                    0f,
                    hasOutputBounds,
                    outputBounds,
                    size);
                break;
            case Avalonia.Media.IDropShadowEffect shadow:
                SynchronizeEffect(
                    AvaloniaCompositionEffectKind.DropShadow,
                    (float)shadow.BlurRadius,
                    new Vector2(
                        (float)shadow.OffsetX,
                        (float)shadow.OffsetY),
                    shadow.Color.ToUInt32(),
                    (float)shadow.Opacity,
                    hasOutputBounds,
                    outputBounds,
                    size);
                break;
            default:
                SynchronizeEffect(
                    AvaloniaCompositionEffectKind.None,
                    0f,
                    default,
                    0,
                    0f,
                    hasOutputBounds,
                    outputBounds,
                    size);
                break;
        }
    }

    internal void SynchronizeEffect(
        AvaloniaCompositionEffectKind kind,
        float rawRadius,
        in Vector2 rawOffset,
        uint packedColor,
        float rawOpacity,
        bool hasOutputBounds,
        in Vector4 outputBounds,
        in Vector2 size)
    {
        switch (kind)
        {
            case AvaloniaCompositionEffectKind.Blur:
            {
                float radius = NormalizeNonNegative(rawRadius);
                float sigma = BlurRadiusToSigma(radius);
                if (Effect is not ProGPU.Scene.BlurEffect target)
                {
                    target = new ProGPU.Scene.BlurEffect(sigma);
                    Effect = target;
                }
                else
                {
                    target.BlurRadius = sigma;
                }

                float padding = GetAvaloniaEffectPadding(radius);
                EffectRasterPadding = padding;
                EffectContentBounds = GetEffectContentBounds(
                    hasOutputBounds,
                    outputBounds,
                    size,
                    padding,
                    padding,
                    padding,
                    padding);
                break;
            }
            case AvaloniaCompositionEffectKind.DropShadow:
            {
                float radius = NormalizeNonNegative(rawRadius);
                float sigma = BlurRadiusToSigma(radius);
                float offsetX = NormalizeFinite(rawOffset.X);
                float offsetY = NormalizeFinite(rawOffset.Y);
                float alpha = Math.Clamp(
                    ((packedColor >> 24) & 0xff) / 255f *
                        NormalizeOpacity(rawOpacity),
                    0f,
                    1f);
                var offset = new Vector2(offsetX, offsetY);
                var shadowColor = new Vector4(
                    ((packedColor >> 16) & 0xff) / 255f,
                    ((packedColor >> 8) & 0xff) / 255f,
                    (packedColor & 0xff) / 255f,
                    alpha);
                if (Effect is not ProGPU.Scene.DropShadowEffect target)
                {
                    target = new ProGPU.Scene.DropShadowEffect(
                        sigma,
                        offset,
                        shadowColor);
                    Effect = target;
                }
                else
                {
                    target.BlurRadius = sigma;
                    target.Offset = offset;
                    target.Color = shadowColor;
                }

                float padding = GetAvaloniaEffectPadding(radius);
                EffectRasterPadding = padding;
                EffectContentBounds = GetEffectContentBounds(
                    hasOutputBounds,
                    outputBounds,
                    size,
                    MathF.Max(0f, padding - offsetX),
                    MathF.Max(0f, padding - offsetY),
                    MathF.Max(0f, padding + offsetX),
                    MathF.Max(0f, padding + offsetY));
                break;
            }
            default:
                Effect = null;
                EffectContentBounds = null;
                EffectRasterPadding = null;
                break;
        }
    }

    private static bool TryGetEffectOutputBounds(
        ServerCompositionVisual source,
        out Vector4 outputBounds)
    {
        if (source.SubTreeBounds is { } bounds)
        {
            outputBounds = new Vector4(
                (float)bounds.Left,
                (float)bounds.Top,
                (float)(bounds.Right - bounds.Left),
                (float)(bounds.Bottom - bounds.Top));
            return true;
        }

        outputBounds = default;
        return false;
    }

    private static ProGpuRect GetEffectContentBounds(
        bool hasOutputBounds,
        in Vector4 outputBounds,
        in Vector2 size,
        float left,
        float top,
        float right,
        float bottom)
    {
        if (hasOutputBounds)
        {
            float x = outputBounds.X + left;
            float y = outputBounds.Y + top;
            float width = MathF.Max(
                0f,
                outputBounds.Z -
                left -
                right);
            float height = MathF.Max(
                0f,
                outputBounds.W -
                top -
                bottom);
            if (width > 0f && height > 0f)
                return new ProGpuRect(x, y, width, height);
        }

        return new ProGpuRect(
            0f,
            0f,
            MathF.Max(0f, size.X),
            MathF.Max(0f, size.Y));
    }

    private static float BlurRadiusToSigma(float radius) =>
        radius > 0f
            ? radius * 0.2886751345948129f + 0.5f
            : 0f;

    private static float GetAvaloniaEffectPadding(float radius) =>
        radius > 0f
            ? MathF.Ceiling(radius) + 1f
            : 0f;

    private static float NormalizeNonNegative(double value) =>
        double.IsFinite(value) && value > 0d
            ? (float)value
            : 0f;

    private static float NormalizeFinite(double value) =>
        double.IsFinite(value)
            ? (float)value
            : 0f;

    private static float NormalizeOpacity(double value) =>
        double.IsFinite(value)
            ? (float)value
            : 0f;

    internal void SetAdornerDependency(
        bool isClipped,
        AvaloniaCompositionVisual? adornedVisual)
    {
        _adornerIsClipped = isClipped;
        _adornedVisual = adornedVisual;
        if (HasAdornerDependency)
            return;

        _adornerPath?.Clear();
        _adornerClips?.Clear();
        SetOuterCompositeClips(
            _adornerClips is null
                ? Array.Empty<VisualCompositeClip>()
                : _adornerClips);
    }

    internal bool TrySynchronizeAdornerClips()
    {
        _adornerPath?.Clear();
        _adornerClips?.Clear();
        if (!HasAdornerDependency)
        {
            SetOuterCompositeClips(
                _adornerClips is null
                    ? Array.Empty<VisualCompositeClip>()
                    : _adornerClips);
            return true;
        }

        if (Parent?.Parent is not AvaloniaCompositionVisual sharedAncestor ||
            _adornedVisual is not { } adornedVisual)
        {
            return false;
        }

        var adornerPath =
            _adornerPath ??= new List<AvaloniaCompositionVisual>();
        var adornerClips =
            _adornerClips ??= new List<VisualCompositeClip>();
        for (AvaloniaCompositionVisual? candidate = adornedVisual;
             candidate != null;
             candidate = candidate.Parent as AvaloniaCompositionVisual)
        {
            if (candidate.Source == null || candidate.IsFallback)
                return false;

            adornerPath.Add(candidate);
            if (ReferenceEquals(candidate, sharedAncestor))
                break;
        }

        if (adornerPath.Count == 0 ||
            !ReferenceEquals(adornerPath[^1], sharedAncestor))
        {
            SetOuterCompositeClips(adornerClips);
            return false;
        }

        Matrix4x4 relativeTransform = Matrix4x4.Identity;
        for (int index = adornerPath.Count - 1; index >= 0; index--)
        {
            AvaloniaCompositionVisual candidate = adornerPath[index];
            if (!ReferenceEquals(candidate, sharedAncestor))
            {
                relativeTransform =
                    candidate.Transform *
                    relativeTransform;
            }

            if (candidate._clipToBounds)
            {
                adornerClips.Add(
                    new VisualCompositeClip(
                        new ProGpuRect(
                            0f,
                            0f,
                            candidate.Size.X,
                            candidate.Size.Y),
                        relativeTransform));
            }

            if (candidate._geometryClipBounds is { } geometryBounds)
            {
                adornerClips.Add(
                    new VisualCompositeClip(
                        geometryBounds,
                        relativeTransform));
            }
            else if (candidate.GeometryClip is { } geometry)
            {
                adornerClips.Add(
                    new VisualCompositeClip(
                        geometry,
                        relativeTransform));
            }
        }

        SetOuterCompositeClips(adornerClips);
        return true;
    }

    private void ReplaceOwnedOpacityMaskPicture(GpuPicture? picture)
    {
        if (ReferenceEquals(_ownedOpacityMaskPicture, picture))
            return;

        GpuPicture? replaced = _ownedOpacityMaskPicture;
        _ownedOpacityMaskPicture = picture;
        OpacityMaskPicture = picture;
        replaced?.Dispose();
    }

    bool IOwnedRenderCommandCache.HasRenderCommands =>
        _commands?.Count > 0;

    public DrawingContext GetOrUpdateRenderCommandCache() =>
        GetOrCreateCommands();

    int IOwnedRenderCommandCache.RenderCommandCount =>
        _commands?.Count ?? 0;

    RenderCommand IOwnedRenderCommandCache.GetRenderCommand(int index)
    {
        RenderCommand command =
            _commands?.GetCommand(index) ??
            throw new ArgumentOutOfRangeException(nameof(index));
        ApplyEffectiveDrawingOptions(ref command);
        return command;
    }

    private void ApplyEffectiveDrawingOptions(ref RenderCommand command)
    {
        RenderCommandPresentationDependencies dependencies =
            command.PresentationDependencies;
        if ((dependencies &
                RenderCommandPresentationDependencies.TextureSampling) != 0)
        {
            command.TextureSamplingMode = ResolveTextureSamplingMode();
        }
        if ((dependencies &
                RenderCommandPresentationDependencies.TextRendering) != 0)
        {
            command.TextRenderingMode = ResolveTextRenderingMode();
        }
        if ((dependencies &
                RenderCommandPresentationDependencies.TextHinting) != 0)
        {
            command.TextHintingMode = ResolveTextHintingMode();
        }
    }

    private IncrementalRenderPresentationState
        GetIncrementalPresentationState()
    {
        RenderCommandPresentationDependencies dependencies =
            _commands?.PresentationDependencies ??
            RenderCommandPresentationDependencies.None;
        return new IncrementalRenderPresentationState(
            dependencies,
            (dependencies &
                RenderCommandPresentationDependencies.TextureSampling) != 0
                ? ResolveTextureSamplingMode()
                : default,
            (dependencies &
                RenderCommandPresentationDependencies.TextRendering) != 0
                ? ResolveTextRenderingMode()
                : default,
            (dependencies &
                RenderCommandPresentationDependencies.TextHinting) != 0
                ? ResolveTextHintingMode()
                : default);
    }

    private TextureSamplingMode ResolveTextureSamplingMode() =>
        EffectiveRenderOptions.BitmapInterpolationMode ==
            Avalonia.Media.Imaging.BitmapInterpolationMode.None
            ? TextureSamplingMode.Nearest
            : TextureSamplingMode.Linear;

    private TextRenderingMode ResolveTextRenderingMode() =>
        EffectiveTextOptions.TextRenderingMode switch
        {
            Avalonia.Media.TextRenderingMode.Alias =>
                TextRenderingMode.Aliased,
            Avalonia.Media.TextRenderingMode.SubpixelAntialias =>
                TextRenderingMode.ClearType,
            _ => TextRenderingMode.Grayscale
        };

    private TextHintingMode ResolveTextHintingMode() =>
        EffectiveTextOptions.TextHintingMode switch
        {
            Avalonia.Media.TextHintingMode.None =>
                TextHintingMode.Animated,
            Avalonia.Media.TextHintingMode.Strong =>
                TextHintingMode.Fixed,
            _ => TextHintingMode.Auto
        };

    public void Dispose()
    {
        ClearChildren();
        _adornerIsClipped = false;
        _adornedVisual = null;
        _adornerPath?.Clear();
        _adornerClips?.Clear();
        Effect = null;
        EffectContentBounds = null;
        EffectRasterPadding = null;
        ReplaceOwnedOpacityMaskPicture(null);
        ClearCommands();
    }
}
#endif

/// <summary>
/// Retains ordinary Avalonia drawing commands without paying the 560-byte
/// general <see cref="RenderCommand"/> array stride for every rectangle or
/// glyph run. Unsupported commands remain in <see cref="DrawingContext"/> and
/// therefore preserve the complete ProGPU command contract.
/// </summary>
internal sealed class AvaloniaRetainedCommandCache
{
    private CompactAvaloniaCommand? _singleCommand;
    private CompactAvaloniaCommand[]? _multipleCommands;
    private int _compactCommandCount;
    private DrawingContext? _context;
    private RenderCommandPresentationDependencies
        _presentationDependencies;

    internal int Count =>
        _compactCommandCount != 0
            ? _compactCommandCount
            : _context?.Commands.Count ?? 0;
    internal object? CompactStorageIdentity =>
        _singleCommand ?? (object?)_multipleCommands;
    internal RenderCommandPresentationDependencies
        PresentationDependencies =>
            _compactCommandCount == 0 && _context is not null
                ? GetPresentationDependencies(_context.Commands)
                : _presentationDependencies;

    internal DrawingContext GetOrCreateContext() =>
        _context ??= new DrawingContext();

    internal DrawingContext BeginRecording()
    {
        _singleCommand = null;
        _multipleCommands = null;
        _compactCommandCount = 0;
        _presentationDependencies =
            RenderCommandPresentationDependencies.None;
        DrawingContext context = GetOrCreateContext();
        context.Clear();
        return context;
    }

    internal bool TryCompactOrdinaryCommands(DrawingContext context) =>
        TryCompactOrdinaryCommands(context, out _);

    internal bool TryCompactOrdinaryCommands(
        DrawingContext context,
        out bool contentChanged)
    {
        RenderCommandList source = context.Commands;
        int count = source.Count;
        int previousCount = Count;
        contentChanged = true;
        _presentationDependencies =
            GetPresentationDependencies(source);
        if (context.RetainedResourceCount != 0)
            return false;
        if (count == 0)
        {
            contentChanged = previousCount != 0;
            _singleCommand = null;
            _multipleCommands = null;
            _compactCommandCount = 0;
            if (_context is not null)
            {
                _context.Clear();
                _context.TrimRetainedCommandCapacity();
                _context = null;
            }

            return true;
        }

        if (_compactCommandCount == count)
        {
            bool updated = false;
            contentChanged = false;
            if (count == 1 && _singleCommand is not null)
            {
                updated = _singleCommand.TryUpdate(
                    source[0],
                    out contentChanged);
            }
            else if (_multipleCommands is { } existing &&
                     existing.Length == count)
            {
                updated = true;
                for (int index = 0; index < count; index++)
                {
                    if (!existing[index].TryUpdate(
                            source[index],
                            out bool commandChanged))
                    {
                        updated = false;
                        break;
                    }
                    contentChanged |= commandChanged;
                }
            }

            if (updated)
            {
                if (_context is not null)
                {
                    _context.Clear();
                    _context.TrimRetainedCommandCapacity();
                    _context = null;
                }

                return true;
            }
        }

        contentChanged = true;
        if (count == 1)
        {
            if (!CompactAvaloniaCommand.TryCreate(
                    source[0],
                    out CompactAvaloniaCommand? command))
            {
                return false;
            }

            _singleCommand = command;
            _multipleCommands = null;
        }
        else
        {
            var commands = new CompactAvaloniaCommand[count];
            for (int index = 0; index < count; index++)
            {
                if (!CompactAvaloniaCommand.TryCreate(
                        source[index],
                        out CompactAvaloniaCommand? command))
                {
                    return false;
                }

                commands[index] = command;
            }

            _singleCommand = null;
            _multipleCommands = commands;
        }

        _compactCommandCount = count;
        if (_context is not null)
        {
            _context.Clear();
            _context.TrimRetainedCommandCapacity();
            _context = null;
        }

        return true;
    }

    internal RenderCommand GetCommand(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (_compactCommandCount == 0)
            return _context?.Commands[index] ??
                throw new ArgumentOutOfRangeException(nameof(index));
        if (index >= _compactCommandCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _singleCommand is not null
            ? _singleCommand.Expand()
            : _multipleCommands![index].Expand();
    }

    internal void Clear()
    {
        _singleCommand = null;
        _multipleCommands = null;
        _compactCommandCount = 0;
        _presentationDependencies =
            RenderCommandPresentationDependencies.None;
        if (_context is not null)
        {
            _context.Clear();
            _context.TrimRetainedCommandCapacity();
            _context = null;
        }
    }

    private static RenderCommandPresentationDependencies
        GetPresentationDependencies(IReadOnlyList<RenderCommand> commands)
    {
        RenderCommandPresentationDependencies dependencies =
            RenderCommandPresentationDependencies.None;
        for (int index = 0; index < commands.Count; index++)
            dependencies |= commands[index].PresentationDependencies;
        return dependencies;
    }
}

internal abstract class CompactAvaloniaCommand
{
    internal abstract RenderCommand Expand();
    internal abstract bool TryUpdate(
        in RenderCommand command,
        out bool changed);

    internal static bool TryCreate(
        in RenderCommand command,
        [NotNullWhen(true)] out CompactAvaloniaCommand? compact)
    {
        if (command.UseGpuTransforms)
        {
            compact = null;
            return false;
        }

        switch (command.Type)
        {
            case RenderCommandType.DrawRect:
            case RenderCommandType.DrawPath:
            case RenderCommandType.DrawEllipse:
            case RenderCommandType.DrawRoundedRect:
                compact = new CompactAvaloniaVectorCommand(command);
                return true;
            case RenderCommandType.DrawGlyphRun:
                compact = new CompactAvaloniaGlyphRunCommand(command);
                return true;
            default:
                compact = null;
                return false;
        }
    }
}

internal sealed class CompactAvaloniaVectorCommand :
    CompactAvaloniaCommand
{
    private RenderCommandType _type;
    private int _hitTestId;
    private ProGpuRect _rect;
    private ProGPU.Vector.Brush? _brush;
    private ProGPU.Vector.Pen? _pen;
    private ProGPU.Vector.PathGeometry? _path;
    private RenderCommandGeometryCache? _geometryCache;
    private Matrix4x4 _transform;
    private Vector2 _position2;
    private Vector2 _fontTransform;
    private float _fontSize;
    private float _radiusX;
    private float _radiusY;
    private float _pathCoverageGamma;
    private uint _pathSampleGrid;
    private RenderCommandPresentationDependencies
        _presentationDependencies;
    private bool _isEdgeAliased;
    private bool _isPenThicknessLocal;
    private bool _useVectorGlyphRendering;
    private bool _hasFontTransform;

    internal CompactAvaloniaVectorCommand(in RenderCommand command)
    {
        if (!TryUpdate(command, out _))
        {
            throw new ArgumentException(
                "The command is not a compact Avalonia vector command.",
                nameof(command));
        }
    }

    internal override bool TryUpdate(
        in RenderCommand command,
        out bool changed)
    {
        if (command.UseGpuTransforms ||
            command.Type is not (
                RenderCommandType.DrawRect or
                RenderCommandType.DrawPath or
                RenderCommandType.DrawEllipse or
                RenderCommandType.DrawRoundedRect))
        {
            changed = false;
            return false;
        }

        changed =
            _type != command.Type ||
            _hitTestId != command.HitTestId ||
            _rect != command.Rect ||
            !ReferenceEquals(_brush, command.Brush) ||
            !ReferenceEquals(_pen, command.Pen) ||
            !ReferenceEquals(_path, command.Path) ||
            !ReferenceEquals(_geometryCache, command.GeometryCache) ||
            _transform != command.Transform ||
            _position2 != command.Position2 ||
            _fontTransform != command.FontTransform ||
            _fontSize != command.FontSize ||
            _radiusX != command.RadiusX ||
            _radiusY != command.RadiusY ||
            _pathCoverageGamma != command.PathCoverageGamma ||
            _pathSampleGrid != command.PathSampleGrid ||
            _presentationDependencies !=
                command.PresentationDependencies ||
            _isEdgeAliased != command.IsEdgeAliased ||
            _isPenThicknessLocal != command.IsPenThicknessLocal ||
            _useVectorGlyphRendering !=
                command.UseVectorGlyphRendering ||
            _hasFontTransform != command.HasFontTransform;

        _type = command.Type;
        _hitTestId = command.HitTestId;
        _rect = command.Rect;
        _brush = command.Brush;
        _pen = command.Pen;
        _path = command.Path;
        _geometryCache = command.GeometryCache;
        _transform = command.Transform;
        _position2 = command.Position2;
        _fontTransform = command.FontTransform;
        _fontSize = command.FontSize;
        _radiusX = command.RadiusX;
        _radiusY = command.RadiusY;
        _pathCoverageGamma = command.PathCoverageGamma;
        _pathSampleGrid = command.PathSampleGrid;
        _presentationDependencies = command.PresentationDependencies;
        _isEdgeAliased = command.IsEdgeAliased;
        _isPenThicknessLocal = command.IsPenThicknessLocal;
        _useVectorGlyphRendering =
            command.UseVectorGlyphRendering;
        _hasFontTransform = command.HasFontTransform;
        return true;
    }

    internal override RenderCommand Expand() =>
        new()
        {
            Type = _type,
            HitTestId = _hitTestId,
            Rect = _rect,
            Brush = _brush,
            Pen = _pen,
            Path = _path,
            GeometryCache = _geometryCache,
            Transform = _transform,
            Position2 = _position2,
            FontTransform = _fontTransform,
            FontSize = _fontSize,
            RadiusX = _radiusX,
            RadiusY = _radiusY,
            PathCoverageGamma = _pathCoverageGamma,
            PathSampleGrid = _pathSampleGrid,
            PresentationDependencies = _presentationDependencies,
            IsEdgeAliased = _isEdgeAliased,
            IsPenThicknessLocal = _isPenThicknessLocal,
            UseVectorGlyphRendering =
                _useVectorGlyphRendering,
            HasFontTransform = _hasFontTransform
        };
}

internal sealed class CompactAvaloniaGlyphRunCommand :
    CompactAvaloniaCommand
{
    private int _hitTestId;
    private ProGpuRect _rect;
    private ProGPU.Vector.Brush? _brush;
    private TtfFont? _font;
    private float _fontSize;
    private Vector2 _position;
    private Vector2 _fontTransform;
    private Matrix4x4 _transform;
    private float _rotation;
    private TextRenderingMode _textRenderingMode;
    private TextHintingMode _textHintingMode;
    private RenderCommandPresentationDependencies
        _presentationDependencies;
    private ushort[]? _glyphIndices;
    private Vector2[]? _glyphPositions;
    private int _glyphRangeStart;
    private int _glyphRangeCount;
    private bool _isBold;
    private bool _isItalic;
    private bool _hasFontTransform;
    private bool _useVectorGlyphRendering;
    private bool _preferGlyphAtlas;
    private bool _useLogicalGlyphAtlasResolution;

    internal CompactAvaloniaGlyphRunCommand(in RenderCommand command)
    {
        if (!TryUpdate(command, out _))
        {
            throw new ArgumentException(
                "The command is not a compact Avalonia glyph-run command.",
                nameof(command));
        }
    }

    internal override bool TryUpdate(
        in RenderCommand command,
        out bool changed)
    {
        if (command.UseGpuTransforms ||
            command.Type != RenderCommandType.DrawGlyphRun)
        {
            changed = false;
            return false;
        }

        RenderCommandPresentationDependencies dependencies =
            command.PresentationDependencies;
        changed =
            _hitTestId != command.HitTestId ||
            _rect != command.Rect ||
            !ReferenceEquals(_brush, command.Brush) ||
            !ReferenceEquals(_font, command.Font) ||
            _fontSize != command.FontSize ||
            _position != command.Position ||
            _fontTransform != command.FontTransform ||
            _transform != command.Transform ||
            _rotation != command.Rotation ||
            (_textRenderingMode != command.TextRenderingMode &&
                (dependencies &
                    RenderCommandPresentationDependencies.TextRendering) ==
                    0) ||
            (_textHintingMode != command.TextHintingMode &&
                (dependencies &
                    RenderCommandPresentationDependencies.TextHinting) ==
                    0) ||
            _presentationDependencies != dependencies ||
            !ReferenceEquals(_glyphIndices, command.GlyphIndices) ||
            !ReferenceEquals(_glyphPositions, command.GlyphPositions) ||
            _glyphRangeStart != command.GlyphRangeStart ||
            _glyphRangeCount != command.GlyphRangeCount ||
            _isBold != command.IsBold ||
            _isItalic != command.IsItalic ||
            _hasFontTransform != command.HasFontTransform ||
            _useVectorGlyphRendering !=
                command.UseVectorGlyphRendering ||
            _preferGlyphAtlas != command.PreferGlyphAtlas ||
            _useLogicalGlyphAtlasResolution !=
                command.UseLogicalGlyphAtlasResolution;

        _hitTestId = command.HitTestId;
        _rect = command.Rect;
        _brush = command.Brush;
        _font = command.Font;
        _fontSize = command.FontSize;
        _position = command.Position;
        _fontTransform = command.FontTransform;
        _transform = command.Transform;
        _rotation = command.Rotation;
        _textRenderingMode = command.TextRenderingMode;
        _textHintingMode = command.TextHintingMode;
        _presentationDependencies = command.PresentationDependencies;
        _glyphIndices = command.GlyphIndices;
        _glyphPositions = command.GlyphPositions;
        _glyphRangeStart = command.GlyphRangeStart;
        _glyphRangeCount = command.GlyphRangeCount;
        _isBold = command.IsBold;
        _isItalic = command.IsItalic;
        _hasFontTransform = command.HasFontTransform;
        _useVectorGlyphRendering =
            command.UseVectorGlyphRendering;
        _preferGlyphAtlas = command.PreferGlyphAtlas;
        _useLogicalGlyphAtlasResolution =
            command.UseLogicalGlyphAtlasResolution;
        return true;
    }

    internal override RenderCommand Expand() =>
        new()
        {
            Type = RenderCommandType.DrawGlyphRun,
            HitTestId = _hitTestId,
            Rect = _rect,
            Brush = _brush,
            Font = _font,
            FontSize = _fontSize,
            Position = _position,
            FontTransform = _fontTransform,
            Transform = _transform,
            Rotation = _rotation,
            TextRenderingMode = _textRenderingMode,
            TextHintingMode = _textHintingMode,
            PresentationDependencies = _presentationDependencies,
            GlyphIndices = _glyphIndices,
            GlyphPositions = _glyphPositions,
            GlyphRangeStart = _glyphRangeStart,
            GlyphRangeCount = _glyphRangeCount,
            IsBold = _isBold,
            IsItalic = _isItalic,
            HasFontTransform = _hasFontTransform,
            UseVectorGlyphRendering =
                _useVectorGlyphRendering,
            PreferGlyphAtlas = _preferGlyphAtlas,
            UseLogicalGlyphAtlasResolution =
                _useLogicalGlyphAtlasResolution
        };
}
