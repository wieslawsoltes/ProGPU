using System;
using System.Threading;
using ProGPU.Scene;

namespace Avalonia.ProGpu;

/// <summary>
/// Opt-in observation point for completed top-level ProGPU frames.
/// </summary>
public static class ProGpuRenderingDiagnostics
{
    private static Action<CompositorMetrics>? _frameRendered;

    /// <summary>
    /// Raised after ProGPU completes a top-level Avalonia frame.
    /// </summary>
    public static event Action<CompositorMetrics> FrameRendered
    {
        add => AddHandler(value);
        remove => RemoveHandler(value);
    }

    internal static void ReportFrame(CompositorMetrics metrics)
    {
        Volatile.Read(ref _frameRendered)?.Invoke(metrics);
    }

    private static void AddHandler(Action<CompositorMetrics> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Action<CompositorMetrics>? current;
        Action<CompositorMetrics>? replacement;
        do
        {
            current = Volatile.Read(ref _frameRendered);
            replacement =
                (Action<CompositorMetrics>?)Delegate.Combine(
                    current,
                    handler);
        }
        while (!ReferenceEquals(
            Interlocked.CompareExchange(
                ref _frameRendered,
                replacement,
                current),
            current));
    }

    private static void RemoveHandler(Action<CompositorMetrics> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Action<CompositorMetrics>? current;
        Action<CompositorMetrics>? replacement;
        do
        {
            current = Volatile.Read(ref _frameRendered);
            replacement =
                (Action<CompositorMetrics>?)Delegate.Remove(
                    current,
                    handler);
        }
        while (!ReferenceEquals(
            Interlocked.CompareExchange(
                ref _frameRendered,
                replacement,
                current),
            current));
    }
}
