#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia.Platform;
using Avalonia.Rendering.Composition.Server;
using ProGPU.Scene;
using AvaloniaRenderOptions = Avalonia.Media.RenderOptions;
using AvaloniaTextOptions = Avalonia.Media.TextOptions;
using ProGpuRect = ProGPU.Scene.Rect;

namespace Avalonia.ProGpu;

/// <summary>
/// Mirrors an Avalonia server visual tree with stable ProGPU visual identities.
/// Synchronization is O(V) for V server nodes when the target revision changes
/// and O(1) for an unchanged target. Command recording is limited to nodes whose
/// retained revision changed.
/// </summary>
internal sealed class AvaloniaCompositionScene : IDisposable
{
    private static readonly bool s_traceFallbacks =
        string.Equals(
            Environment.GetEnvironmentVariable(
                "PROGPU_AVALONIA_TRACE_COMPOSITION_FALLBACKS"),
            "1",
            StringComparison.Ordinal);
    private readonly Dictionary<long, AvaloniaCompositionVisual> _visuals = new();
    private readonly bool _requireNativeCompositionScene;
    private readonly Dictionary<long, CompositionFallbackReason>
        _tracedFallbacks = new();
    private readonly HashSet<long> _visited = new();
    private readonly List<long> _stale = new();
    private ulong _targetRevision = ulong.MaxValue;
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
    internal long UnchangedReuseCount { get; private set; }

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
            IReadOnlyList<ServerCompositionVisual> changes =
                target.RetainedChangedVisuals;
            for (int index = 0; index < changes.Count; index++)
            {
                if (!TrySynchronizeChangedVisual(
                        changes[index],
                        targetRevision,
                        clip,
                        renderer))
                {
                    incremental = false;
                    break;
                }
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

        _visited.Clear();
        int visited = 0;
        int rendered = 0;
        if (!TrySynchronizeVisual(
                sourceRoot,
                targetRevision,
                clip,
                renderer,
                ancestorsRenderable: true,
                ref visited,
                ref rendered,
                out AvaloniaCompositionVisual root))
        {
            visitedVisuals = 0;
            renderedVisuals = 0;
            return false;
        }

        RemoveStaleVisuals();
        FullSynchronizationCount++;
        int fallbackNodeCount = 0;
        int customVisualNodeCount = 0;
        foreach (AvaloniaCompositionVisual visual in _visuals.Values)
        {
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

    private bool TrySynchronizeChangedVisual(
        ServerCompositionVisual source,
        ulong targetRevision,
        LtrbRect clip,
        DrawingContextImpl renderer)
    {
        for (ServerCompositionVisual? candidate = source;
             candidate != null;
             candidate = candidate.Parent)
        {
            if (_visuals.TryGetValue(
                    candidate.RetainedId,
                    out AvaloniaCompositionVisual? fallback) &&
                fallback.IsFallback)
            {
                fallback.SynchronizeFallbackState(candidate);
                if (IsEffectivelyRenderable(candidate) &&
                    fallback.SourceRevision != targetRevision)
                {
                    (int fallbackVisited, int fallbackRendered) =
                        renderer.RecordRetainedCompositionSubtree(
                            candidate,
                            fallback.Commands);
                    fallback.SourceRevision = targetRevision;
                    fallback.FallbackVisitedVisuals = fallbackVisited;
                    fallback.FallbackRenderedVisuals = fallbackRendered;
                    fallback.InvalidateRecordedContent();
                }

                return true;
            }
        }

        if (!_visuals.TryGetValue(
                source.RetainedId,
                out AvaloniaCompositionVisual? target) ||
            target.IsFallback ||
            RequiresFallback(source))
        {
            return false;
        }

        bool wasLocallyRenderable =
            target.IsVisible && target.Opacity > 0.003f;
        bool drawingOptionsChanged = target.SynchronizeState(source, renderer);
        if (drawingOptionsChanged)
        {
            target.SourceRevision = ulong.MaxValue;
            return false;
        }
        bool isEffectivelyRenderable = IsEffectivelyRenderable(source);
        if (isEffectivelyRenderable &&
            target.SourceRevision != source.RetainedContentRevision)
        {
            RecordRetainedCompositionVisual(
                renderer,
                source,
                clip,
                target.EffectiveRenderOptions,
                target.EffectiveTextOptions,
                target.Commands);
            target.SourceRevision = source.RetainedContentRevision;
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

        return true;
    }

    private bool TrySynchronizeVisual(
        ServerCompositionVisual source,
        ulong targetRevision,
        LtrbRect clip,
        DrawingContextImpl renderer,
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

        _visited.Add(source.RetainedId);
        if (!_visuals.TryGetValue(source.RetainedId, out target!))
        {
            target = new AvaloniaCompositionVisual();
            _visuals.Add(source.RetainedId, target);
        }

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
            if (!wasFallback && !isRenderable)
            {
                target.Commands.Clear();
                target.SourceRevision = ulong.MaxValue;
                target.InvalidateRecordedContent();
            }
            if (isRenderable &&
                (!wasFallback || target.SourceRevision != targetRevision))
            {
                (int fallbackVisited, int fallbackRendered) =
                    renderer.RecordRetainedCompositionSubtree(
                        source,
                        target.Commands);
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
        bool drawingOptionsChanged = target.SynchronizeState(source, renderer);
        target.IsFallback = false;
        if (wasFallbackNode && !isRenderable)
        {
            target.Commands.Clear();
            target.SourceRevision = ulong.MaxValue;
            target.InvalidateRecordedContent();
        }
        if (isRenderable &&
            (wasFallbackNode ||
             drawingOptionsChanged ||
             target.SourceRevision != source.RetainedContentRevision))
        {
            RecordRetainedCompositionVisual(
                renderer,
                source,
                clip,
                target.EffectiveRenderOptions,
                target.EffectiveTextOptions,
                target.Commands);
            target.SourceRevision = source.RetainedContentRevision;
            target.InvalidateRecordedContent();
        }

        var sourceChildren = source.Children!.List;
        int childCount = sourceChildren.Count;
        target.BeginChildSynchronization(childCount);
        for (int index = 0; index < childCount; index++)
        {
            if (!TrySynchronizeVisual(
                    sourceChildren[index],
                    targetRevision,
                    clip,
                    renderer,
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

        if (!_visuals.TryGetValue(
                source.RetainedId,
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
                        target.Commands);
                target.SourceRevision = targetRevision;
                target.FallbackVisitedVisuals = fallbackVisited;
                target.FallbackRenderedVisuals = fallbackRendered;
                target.InvalidateRecordedContent();
            }

            return true;
        }

        if (target.SourceRevision != source.RetainedContentRevision)
        {
            RecordRetainedCompositionVisual(
                renderer,
                source,
                clip,
                target.EffectiveRenderOptions,
                target.EffectiveTextOptions,
                target.Commands);
            target.SourceRevision = source.RetainedContentRevision;
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

    private static bool IsEffectivelyRenderable(ServerCompositionVisual source)
    {
        for (ServerCompositionVisual? candidate = source;
             candidate != null;
             candidate = candidate.Parent)
        {
            if (!candidate.Visible || candidate.Opacity <= 0.003f)
                return false;
        }

        return true;
    }

    private void RecordRetainedCompositionVisual(
        DrawingContextImpl renderer,
        ServerCompositionVisual source,
        LtrbRect clip,
        AvaloniaRenderOptions renderOptions,
        AvaloniaTextOptions textOptions,
        DrawingContext destination)
    {
        renderer.RecordRetainedCompositionVisual(
            source,
            clip,
            renderOptions,
            textOptions,
            destination);
        if (source is ServerCompositionCustomVisual)
            CustomVisualCompilationCount++;
    }

    private static bool RequiresFallback(ServerCompositionVisual source) =>
        GetFallbackReason(source) != CompositionFallbackReason.None;

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
        if (source.Clip is not null and not GeometryImpl)
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
            if (candidate.Clip is not null and not GeometryImpl)
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

    private void RemoveStaleVisuals()
    {
        _stale.Clear();
        foreach (var entry in _visuals)
        {
            if (!_visited.Contains(entry.Key))
                _stale.Add(entry.Key);
        }

        for (int index = 0; index < _stale.Count; index++)
        {
            long id = _stale[index];
            if (_visuals.Remove(id, out AvaloniaCompositionVisual? visual))
                visual.Dispose();
            _tracedFallbacks.Remove(id);
        }
    }

    public void Dispose()
    {
        foreach (var visual in _visuals.Values)
            visual.Dispose();
        _visuals.Clear();
        _visited.Clear();
        _stale.Clear();
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
    private List<AvaloniaCompositionVisual>? _nextChildren;
    private List<ServerCompositionVisual>? _adornerPath;
    private List<VisualCompositeClip>? _adornerClips;
    private bool _cacheEnableClearType;
    private GpuPicture? _ownedOpacityMaskPicture;

    internal DrawingContext Commands { get; } = new();
    internal ulong SourceRevision { get; set; } = ulong.MaxValue;
    internal bool IsFallback { get; set; }
    internal bool IsCustomVisual { get; private set; }
    internal int FallbackVisitedVisuals { get; set; }
    internal int FallbackRenderedVisuals { get; set; }
    internal AvaloniaRenderOptions EffectiveRenderOptions { get; private set; }
    internal AvaloniaTextOptions EffectiveTextOptions { get; private set; }

    public override ProGpuRect? LocalRenderBounds => _localRenderBounds;

    internal bool SynchronizeState(
        ServerCompositionVisual source,
        DrawingContextImpl renderer)
    {
        AvaloniaRenderOptions effectiveRenderOptions =
            GetEffectiveRenderOptions(source);
        AvaloniaTextOptions effectiveTextOptions =
            GetEffectiveTextOptions(source);
        bool drawingOptionsChanged =
            EffectiveRenderOptions != effectiveRenderOptions ||
            EffectiveTextOptions != effectiveTextOptions;
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
        drawingOptionsChanged |=
            CacheAsLayer != hasBitmapCache ||
            LayerCacheRenderScale != cacheRenderScale ||
            LayerCacheSnapsToDevicePixels != cacheSnapsToDevicePixels ||
            _cacheEnableClearType != cacheEnableClearType;
        _cacheEnableClearType = cacheEnableClearType;
        EffectiveRenderOptions = effectiveRenderOptions;
        EffectiveTextOptions = effectiveTextOptions;
        IsCustomVisual = source is ServerCompositionCustomVisual;

        IsVisible = source.Visible;
        Opacity = source.Opacity;
        Size = new Vector2((float)source.Size.X, (float)source.Size.Y);
        Offset = Vector2.Zero;
        Scale = Vector3.One;
        Rotation = 0f;
        CenterPoint = Vector3.Zero;
        Transform = DrawingContextImpl.ToProGpuMatrix(
            source.RetainedOwnTransform ?? Avalonia.Matrix.Identity);
        ProGpuRect? clipBounds = source.ClipToBounds
            ? new ProGpuRect(0f, 0f, (float)source.Size.X, (float)source.Size.Y)
            : null;
        if (source.Clip is GeometryImpl geometry)
        {
            if (ProGPU.Vector.PrimitivePathGeometry
                .TryGetAxisAlignedRectangleBounds(
                    geometry.Path,
                    out Vector2 clipMin,
                    out Vector2 clipMax))
            {
                var geometryBounds = new ProGpuRect(
                    clipMin.X,
                    clipMin.Y,
                    clipMax.X - clipMin.X,
                    clipMax.Y - clipMin.Y);
                clipBounds = clipBounds is { } currentClip
                    ? Intersect(currentClip, geometryBounds)
                    : geometryBounds;
                GeometryClip = null;
            }
            else
            {
                GeometryClip = geometry.Path;
            }
        }
        else
        {
            GeometryClip = null;
        }
        ClipBounds = clipBounds;
        SynchronizeAdornerClips(source);
        SynchronizeEffect(source);
        if (source.OpacityMaskBrush is { } opacityMask &&
            source.SubTreeBounds is { } opacityMaskBounds)
        {
            var avaloniaMaskBounds = new Avalonia.Rect(
                opacityMaskBounds.Left,
                opacityMaskBounds.Top,
                opacityMaskBounds.Right - opacityMaskBounds.Left,
                opacityMaskBounds.Bottom - opacityMaskBounds.Top);
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
                (float)avaloniaMaskBounds.X,
                (float)avaloniaMaskBounds.Y,
                (float)avaloniaMaskBounds.Width,
                (float)avaloniaMaskBounds.Height);
        }
        else
        {
            OpacityMask = null;
            ReplaceOwnedOpacityMaskPicture(null);
            OpacityMaskBounds = null;
        }
        CacheAsLayer = hasBitmapCache;
        LayerCacheRenderScale = cacheRenderScale;
        LayerCacheSnapsToDevicePixels = cacheSnapsToDevicePixels;

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

        return drawingOptionsChanged;
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
        ClipBounds = null;
        GeometryClip = null;
        Effect = null;
        EffectContentBounds = null;
        EffectRasterPadding = null;
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
        _nextChildren?.Clear();
        if (count == 0)
            return;

        var nextChildren =
            _nextChildren ??= new List<AvaloniaCompositionVisual>(count);
        if (nextChildren.Capacity < count)
            nextChildren.Capacity = count;
    }

    internal void AddSynchronizedChild(AvaloniaCompositionVisual child) =>
        (_nextChildren ??= new List<AvaloniaCompositionVisual>()).Add(child);

    internal void EndChildSynchronization()
    {
        int nextChildCount = _nextChildren?.Count ?? 0;
        bool unchanged = Children.Count == nextChildCount;
        if (unchanged)
        {
            for (int index = 0; index < nextChildCount; index++)
            {
                if (!ReferenceEquals(Children[index], _nextChildren![index]))
                {
                    unchanged = false;
                    break;
                }
            }
        }

        if (unchanged)
            return;

        ClearChildren();
        for (int index = 0; index < nextChildCount; index++)
            AddChild(_nextChildren![index]);
    }

    internal void InvalidateRecordedContent() => Invalidate();

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
        switch (source.Effect)
        {
            case Avalonia.Media.IBlurEffect blur:
            {
                float radius = NormalizeNonNegative(blur.Radius);
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
                    source,
                    padding,
                    padding,
                    padding,
                    padding);
                break;
            }

            case Avalonia.Media.IDropShadowEffect shadow:
            {
                float radius = NormalizeNonNegative(shadow.BlurRadius);
                float sigma = BlurRadiusToSigma(radius);
                float offsetX = NormalizeFinite(shadow.OffsetX);
                float offsetY = NormalizeFinite(shadow.OffsetY);
                Avalonia.Media.Color color = shadow.Color;
                float alpha = Math.Clamp(
                    color.A / 255f * NormalizeOpacity(shadow.Opacity),
                    0f,
                    1f);
                var offset = new Vector2(offsetX, offsetY);
                var shadowColor = new Vector4(
                    color.R / 255f,
                    color.G / 255f,
                    color.B / 255f,
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
                    source,
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

    private static ProGpuRect GetEffectContentBounds(
        ServerCompositionVisual source,
        float left,
        float top,
        float right,
        float bottom)
    {
        if (source.SubTreeBounds is { } outputBounds)
        {
            float x = (float)outputBounds.Left + left;
            float y = (float)outputBounds.Top + top;
            float width = MathF.Max(
                0f,
                (float)(outputBounds.Right - outputBounds.Left) -
                left -
                right);
            float height = MathF.Max(
                0f,
                (float)(outputBounds.Bottom - outputBounds.Top) -
                top -
                bottom);
            if (width > 0f && height > 0f)
                return new ProGpuRect(x, y, width, height);
        }

        return new ProGpuRect(
            0f,
            0f,
            MathF.Max(0f, (float)source.Size.X),
            MathF.Max(0f, (float)source.Size.Y));
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

    private void SynchronizeAdornerClips(ServerCompositionVisual source)
    {
        _adornerPath?.Clear();
        _adornerClips?.Clear();
        if (!source.AdornerIsClipped ||
            source.AdornedVisual == null ||
            source.Parent?.Parent is not { } sharedAncestor)
        {
            SetOuterCompositeClips(
                _adornerClips is null
                    ? Array.Empty<VisualCompositeClip>()
                    : _adornerClips);
            return;
        }

        var adornerPath =
            _adornerPath ??= new List<ServerCompositionVisual>();
        var adornerClips =
            _adornerClips ??= new List<VisualCompositeClip>();
        for (ServerCompositionVisual? candidate = source.AdornedVisual;
             candidate != null;
             candidate = candidate.Parent)
        {
            adornerPath.Add(candidate);
            if (ReferenceEquals(candidate, sharedAncestor))
                break;
        }

        if (adornerPath.Count == 0 ||
            !ReferenceEquals(adornerPath[^1], sharedAncestor))
        {
            SetOuterCompositeClips(adornerClips);
            return;
        }

        Matrix4x4 relativeTransform = Matrix4x4.Identity;
        for (int index = adornerPath.Count - 1; index >= 0; index--)
        {
            ServerCompositionVisual candidate = adornerPath[index];
            if (!ReferenceEquals(candidate, sharedAncestor) &&
                candidate.RetainedOwnTransform is { } ownTransform)
            {
                relativeTransform =
                    DrawingContextImpl.ToProGpuMatrix(ownTransform) *
                    relativeTransform;
            }

            if (candidate.ClipToBounds)
            {
                adornerClips.Add(
                    new VisualCompositeClip(
                        new ProGpuRect(
                            0f,
                            0f,
                            (float)candidate.Size.X,
                            (float)candidate.Size.Y),
                        relativeTransform));
            }

            if (candidate.Clip is GeometryImpl geometry)
            {
                adornerClips.Add(
                    new VisualCompositeClip(
                        geometry.Path,
                        relativeTransform));
            }
        }

        SetOuterCompositeClips(adornerClips);
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
        Commands.Commands.Count != 0;

    public DrawingContext GetOrUpdateRenderCommandCache() => Commands;

    private static AvaloniaRenderOptions GetEffectiveRenderOptions(
        ServerCompositionVisual source)
    {
        AvaloniaRenderOptions effective = default;
        for (ServerCompositionVisual? candidate = source;
             candidate != null;
             candidate = candidate.Parent)
        {
            effective = candidate.RenderOptions.MergeWith(effective);
        }

        return effective;
    }

    private static AvaloniaTextOptions GetEffectiveTextOptions(
        ServerCompositionVisual source)
    {
        AvaloniaTextOptions effective = default;
        bool disableSubpixelText = false;
        for (ServerCompositionVisual? candidate = source;
             candidate != null;
             candidate = candidate.Parent)
        {
            effective = candidate.TextOptions.MergeWith(effective);
            if (candidate.CacheMode is ServerCompositionBitmapCache
                {
                    EnableClearType: false
                })
            {
                disableSubpixelText = true;
            }
        }

        if (disableSubpixelText &&
            effective.TextRenderingMode ==
            Avalonia.Media.TextRenderingMode.SubpixelAntialias)
        {
            effective = effective with
            {
                TextRenderingMode =
                    Avalonia.Media.TextRenderingMode.Antialias
            };
        }

        return effective;
    }

    public void Dispose()
    {
        ClearChildren();
        _nextChildren?.Clear();
        _adornerPath?.Clear();
        _adornerClips?.Clear();
        Effect = null;
        EffectContentBounds = null;
        EffectRasterPadding = null;
        ReplaceOwnedOpacityMaskPicture(null);
        Commands.Clear();
    }
}
#endif
