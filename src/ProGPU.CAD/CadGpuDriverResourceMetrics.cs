using ProGPU.Backend;

namespace ProGPU.CAD;

/// <summary>
/// Diagnostic resource telemetry for the complete native WebGPU backend
/// instance that contains a CAD renderer.
/// </summary>
/// <remarks>
/// This is not CAD-exclusive accounting. Registry values describe native
/// handle-table storage, not buffer or texture payload bytes. Physical device
/// allocation is currently available only when the backend can query the
/// system-default Metal device. Capture locks the context and calls native
/// diagnostics, so it belongs in explicit profiling samples, never a frame or
/// scene-update hot path.
/// </remarks>
public readonly record struct CadGpuDriverResourceMetrics
{
    public ulong CommandBufferCount { get; init; }
    public ulong BufferCount { get; init; }
    public ulong TextureCount { get; init; }
    public ulong TextureViewCount { get; init; }
    public ulong BindGroupCount { get; init; }
    public ulong BindGroupLayoutCount { get; init; }
    public ulong ShaderModuleCount { get; init; }
    public ulong RenderPipelineCount { get; init; }
    public ulong ComputePipelineCount { get; init; }
    public ulong NativeRegistrySlotBytes { get; init; }
    public bool PhysicalDeviceAllocatedBytesAvailable { get; init; }
    public ulong PhysicalDeviceAllocatedBytes { get; init; }

    /// <summary>
    /// Attempts one explicit backend diagnostic capture. Unsupported backends
    /// return false and a default result rather than synthetic zero counters.
    /// </summary>
    public static bool TryCapture(
        WgpuContext context,
        out CadGpuDriverResourceMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryCaptureNativeResourceSnapshot(out var snapshot))
        {
            metrics = default;
            return false;
        }

        metrics = FromNative(snapshot);
        return true;
    }

    /// <summary>
    /// Projects one immutable backend snapshot without calling the driver.
    /// </summary>
    public static CadGpuDriverResourceMetrics FromNative(
        in WgpuNativeResourceSnapshot snapshot)
    {
        return new CadGpuDriverResourceMetrics
        {
            CommandBufferCount = snapshot.CommandBuffers.AllocatedSlots,
            BufferCount = snapshot.Buffers.AllocatedSlots,
            TextureCount = snapshot.Textures.AllocatedSlots,
            TextureViewCount = snapshot.TextureViews.AllocatedSlots,
            BindGroupCount = snapshot.BindGroups.AllocatedSlots,
            BindGroupLayoutCount = snapshot.BindGroupLayouts.AllocatedSlots,
            ShaderModuleCount = snapshot.ShaderModules.AllocatedSlots,
            RenderPipelineCount = snapshot.RenderPipelines.AllocatedSlots,
            ComputePipelineCount = snapshot.ComputePipelines.AllocatedSlots,
            NativeRegistrySlotBytes = SaturatingSum(
                snapshot.CommandBuffers,
                snapshot.Buffers,
                snapshot.Textures,
                snapshot.TextureViews,
                snapshot.BindGroups,
                snapshot.BindGroupLayouts,
                snapshot.ShaderModules,
                snapshot.RenderPipelines,
                snapshot.ComputePipelines),
            PhysicalDeviceAllocatedBytesAvailable =
                snapshot.MetalAllocatedBytesAvailable,
            PhysicalDeviceAllocatedBytes = snapshot.MetalAllocatedBytesAvailable
                ? snapshot.MetalAllocatedBytes
                : 0,
        };
    }

    private static ulong SaturatingSum(
        params ReadOnlySpan<WgpuRegistrySnapshot> registries)
    {
        ulong result = 0;
        foreach (WgpuRegistrySnapshot registry in registries)
        {
            ulong bytes = SaturatingMultiply(
                registry.AllocatedSlots,
                registry.ElementSize);
            result = SaturatingAdd(result, bytes);
        }

        return result;
    }

    private static ulong SaturatingMultiply(ulong left, ulong right) =>
        left != 0 && ulong.MaxValue / left < right
            ? ulong.MaxValue
            : left * right;

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
}
