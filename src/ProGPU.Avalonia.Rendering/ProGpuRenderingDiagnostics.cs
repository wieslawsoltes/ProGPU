using System;
using ProGPU.Scene;

namespace Avalonia.ProGpu
{
    /// <summary>
    /// Provides opt-in per-frame diagnostics for the ProGPU Avalonia renderer.
    /// </summary>
    public static class ProGpuRenderingDiagnostics
    {
        /// <summary>
        /// Raised after a top-level Avalonia frame has been rendered by ProGPU.
        /// The event has no production overhead beyond a null check when there are
        /// no subscribers.
        /// </summary>
        public static event Action<CompositorMetrics>? FrameRendered;

        internal static void ReportFrame(CompositorMetrics metrics)
        {
            FrameRendered?.Invoke(metrics);
        }
    }
}
