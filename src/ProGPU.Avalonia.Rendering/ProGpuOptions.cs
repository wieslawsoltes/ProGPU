namespace Avalonia.ProGpu
{
    /// <summary>
    /// Configures ProGPU-specific rendering behavior without extending
    /// Avalonia.Skia's compatibility option contract.
    /// </summary>
    public sealed class ProGpuOptions
    {
        /// <summary>
        /// Fails rendering when the source-built Avalonia compositor encounters
        /// a visual subtree that cannot be represented by the retained ProGPU
        /// scene.
        /// </summary>
        /// <remarks>
        /// Conformance and release-validation lanes enable this option to
        /// prevent a passing run from silently measuring Avalonia's flattened
        /// subtree fallback. It has no effect on the Avalonia 11 renderer lane.
        /// </remarks>
        public bool RequireNativeCompositionScene { get; set; }

        /// <summary>
        /// Uses the typed Dawn/WebGPUSharp Metal drawable path when Avalonia's
        /// native macOS windowing backend supplies an <c>IMetalPlatformSurface</c>.
        /// </summary>
        /// <remarks>
        /// The renderer imports the drawable IOSurface into Dawn and exchanges
        /// Metal timeline events with Avalonia's command queue. No framebuffer
        /// readback or full-size presentation copy is performed.
        /// </remarks>
        public bool UseDawnMetalPresentation { get; set; } = true;

        /// <summary>
        /// Fails render-target creation instead of falling back to Avalonia's
        /// CPU framebuffer surface when direct Dawn Metal presentation is
        /// requested but unavailable.
        /// </summary>
        public bool RequireDawnMetalPresentation { get; set; }

        /// <summary>
        /// Uses WebGPUSharp/Dawn to present directly through Avalonia's native
        /// HWND or XID surface when Silk.NET windowing is not selected.
        /// </summary>
        /// <remarks>
        /// Windows renders to Dawn's D3D12 swapchain and Avalonia X11 renders
        /// to Dawn's Vulkan swapchain. Avalonia continues to own windowing,
        /// input, lifetime, accessibility, and platform services. The path
        /// performs neither CPU framebuffer readback nor a full-frame copy.
        /// </remarks>
        public bool UseDawnNativePresentation { get; set; } = true;

        /// <summary>
        /// Fails render-target creation instead of falling back to Avalonia's
        /// CPU framebuffer when direct Dawn HWND/XID presentation is
        /// unavailable.
        /// </summary>
        public bool RequireDawnNativePresentation { get; set; }
    }
}
