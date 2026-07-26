#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Platform;
using Avalonia.Rendering.Composition.Server;
using ProGPU.Backend;

namespace Avalonia.ProGpu;

/// <summary>
/// Device-context-owned Avalonia composition backend. Target scene state lives
/// here instead of on transient drawing contexts, while the current target
/// drawing context remains the typed WebGPU command encoder.
/// </summary>
internal sealed class ProGpuCompositionServerBackend :
    ICompositionServerBackend,
    IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<long, TargetScene> _targets = new();
    private readonly bool _requireNativeCompositionScene;
    private long _renderCount;
    private bool _disposed;

    private sealed class TargetScene
    {
        internal TargetScene(
            WgpuContext context,
            AvaloniaCompositionScene scene)
        {
            Context = context;
            Scene = scene;
        }

        internal WgpuContext Context { get; }
        internal AvaloniaCompositionScene Scene { get; }
    }

    internal ProGpuCompositionServerBackend(
        bool requireNativeCompositionScene)
    {
        _requireNativeCompositionScene = requireNativeCompositionScene;
    }

    public bool TryRender(
        ServerCompositionTarget target,
        ServerCompositionVisual root,
        IDrawingContextImpl context,
        LtrbRect clip,
        out int visitedVisuals,
        out int renderedVisuals)
    {
        if (context is not DrawingContextImpl renderer)
        {
            if (_requireNativeCompositionScene)
            {
                throw new InvalidOperationException(
                    "The strict ProGPU composition backend received a " +
                    "non-ProGPU drawing context.");
            }

            visitedVisuals = 0;
            renderedVisuals = 0;
            return false;
        }

        TargetScene targetScene = GetOrCreateTargetScene(
            target.Id,
            renderer.GpuContext);
        if (!renderer.TryRenderRetainedCompositionTarget(
                targetScene.Scene,
                target,
                root,
                clip,
                this,
                out visitedVisuals,
                out renderedVisuals))
        {
            ReleaseTarget(target.Id);
            return false;
        }

        Interlocked.Increment(ref _renderCount);
        return true;
    }

    public void ReleaseTarget(long targetId)
    {
        TargetScene? removed = null;
        lock (_sync)
        {
            if (_targets.Remove(targetId, out TargetScene? target))
                removed = target;
        }

        removed?.Scene.Dispose();
    }

    internal void ReadMetrics(
        out long renderCount,
        out int sceneCount,
        out int sceneNodeCount,
        out int fallbackNodeCount,
        out int customVisualNodeCount,
        out long customVisualCompilationCount,
        out long fullSynchronizationCount,
        out long incrementalSynchronizationCount,
        out long unchangedReuseCount)
    {
        renderCount = Interlocked.Read(ref _renderCount);
        sceneCount = 0;
        sceneNodeCount = 0;
        fallbackNodeCount = 0;
        customVisualNodeCount = 0;
        customVisualCompilationCount = 0;
        fullSynchronizationCount = 0;
        incrementalSynchronizationCount = 0;
        unchangedReuseCount = 0;

        lock (_sync)
        {
            foreach (TargetScene target in _targets.Values)
            {
                AvaloniaCompositionScene scene = target.Scene;
                sceneCount++;
                sceneNodeCount += scene.NodeCount;
                fallbackNodeCount += scene.FallbackNodeCount;
                customVisualNodeCount += scene.CustomVisualNodeCount;
                customVisualCompilationCount +=
                    scene.CustomVisualCompilationCount;
                fullSynchronizationCount +=
                    scene.FullSynchronizationCount;
                incrementalSynchronizationCount +=
                    scene.IncrementalSynchronizationCount;
                unchangedReuseCount += scene.UnchangedReuseCount;
            }
        }
    }

    private TargetScene GetOrCreateTargetScene(
        long targetId,
        WgpuContext context)
    {
        TargetScene? replaced = null;
        TargetScene result;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_targets.TryGetValue(targetId, out TargetScene? current) &&
                ReferenceEquals(current.Context, context))
            {
                return current;
            }

            if (current != null)
            {
                _targets.Remove(targetId);
                replaced = current;
            }

            result = new TargetScene(
                context,
                new AvaloniaCompositionScene(
                    _requireNativeCompositionScene));
            _targets.Add(targetId, result);
        }

        replaced?.Scene.Dispose();
        return result;
    }

    public void Dispose()
    {
        TargetScene[] targets;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            targets = new TargetScene[_targets.Count];
            _targets.Values.CopyTo(targets, 0);
            _targets.Clear();
        }

        foreach (TargetScene target in targets)
            target.Scene.Dispose();
    }
}
#endif
