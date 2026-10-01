using W = WebGpuSharp;

namespace ProGPU.Backend.Dawn;

internal static class DawnDeviceLimits
{
    // All Dawn factories query the created device, not its adapter or a default
    // policy. Keep the exact snapshot, including zero/insufficient limits.
    internal static WgpuComputeLimits ToComputeLimits(in W.Limits limits) => new(
        limits.MaxStorageBufferBindingSize,
        limits.MaxStorageBuffersPerShaderStage,
        limits.MaxComputeInvocationsPerWorkgroup,
        limits.MaxComputeWorkgroupSizeX,
        limits.MaxComputeWorkgroupsPerDimension);
}
