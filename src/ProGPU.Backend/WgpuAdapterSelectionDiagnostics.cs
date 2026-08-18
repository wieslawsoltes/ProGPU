using Silk.NET.WebGPU;

namespace ProGPU.Backend;

/// <summary>
/// Describes the WebGPU adapter selected for a context and the constraints
/// that produced that selection.
/// </summary>
public sealed record WgpuAdapterSelectionDiagnostics(
    string Name,
    BackendType BackendType,
    AdapterType AdapterType,
    string DriverDescription,
    uint VendorId,
    uint DeviceId,
    bool RequiredCompatibleSurface,
    WgpuAdapterSelectionReason SelectionReason)
{
    public static WgpuAdapterSelectionDiagnostics Unknown { get; } = new(
        string.Empty,
        BackendType.Undefined,
        AdapterType.Unknown,
        string.Empty,
        0,
        0,
        false,
        WgpuAdapterSelectionReason.Unknown);
}

public enum WgpuAdapterSelectionReason
{
    Unknown,
    HighPerformance,
    HighPerformanceSurfaceCompatible,
    ExternalBrowserHost,
    ExternalNativeHost
}
