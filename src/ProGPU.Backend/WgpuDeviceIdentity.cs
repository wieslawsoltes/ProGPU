namespace ProGPU.Backend;

/// <summary>
/// Immutable identity of one WebGPU device-resource ownership domain.
/// Shared surfaces use the same token. Retaining this token retains no context,
/// native device, resource cache or lease, and does not prove a device is live.
/// </summary>
public sealed class WgpuDeviceIdentity
{
    internal WgpuDeviceIdentity() { }
}
