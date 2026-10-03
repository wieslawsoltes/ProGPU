using Silk.NET.WebGPU;

namespace ProGPU.Backend;

/// <summary>
/// Optional query of an actual live device's texture limits. Unknown external
/// providers need not implement this capability. They must not infer limits
/// from adapter metadata, requested features, pointer values or platform names.
/// </summary>
/// <remarks>
/// The caller holds the device's existing lifetime and rendering synchronization.
/// Providers return false with zero outputs when the exact device is unavailable.
/// This query performs no queue submission, waits, device replacement or fallback.
/// </remarks>
public unsafe interface IWebGpuTextureLimitsSource
{
    bool TryGetTextureLimits(Device* device, out uint maximumTextureWidth, out uint maximumTextureHeight);
}
