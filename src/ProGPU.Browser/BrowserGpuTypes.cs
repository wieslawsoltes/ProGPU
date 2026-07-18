using System.Text.Json.Serialization;

namespace ProGPU.Browser;

public enum BrowserExecutionMode
{
    Auto,
    MainThread,
    Worker,
    IsolatedWorker
}

public enum BrowserSyncReadbackMode
{
    Disabled,
    IsolatedWorkerOnly
}

public enum BrowserGpuProfile
{
    Portable,
    Full
}

public sealed record BrowserAppHostOptions
{
    public string CanvasSelector { get; init; } = "#progpu-canvas";
    public BrowserExecutionMode ExecutionMode { get; init; } = BrowserExecutionMode.Auto;
    public BrowserSyncReadbackMode SyncReadbackMode { get; init; } = BrowserSyncReadbackMode.IsolatedWorkerOnly;
    public BrowserGpuProfile GpuProfile { get; init; } = BrowserGpuProfile.Portable;
    public string PowerPreference { get; init; } = "high-performance";
    public bool EnableDiagnostics { get; init; } = true;
}

public sealed record BrowserGpuCapabilities
{
    public bool IsSupported { get; init; }
    public string AdapterName { get; init; } = string.Empty;
    public string CanvasFormat { get; init; } = "bgra8unorm";
    public BrowserExecutionMode ExecutionMode { get; init; }
    public BrowserGpuProfile RequestedProfile { get; init; }
    public BrowserGpuProfile ActiveProfile { get; init; }
    public bool IsCrossOriginIsolated { get; init; }
    public bool SupportsSharedArrayBuffer { get; init; }
    public bool SupportsOffscreenCanvas { get; init; }
    public bool SupportsBgra8UnormStorage { get; init; }
    public bool SupportsTimestampQuery { get; init; }
    public string[] Features { get; init; } = [];
    public string[] Diagnostics { get; init; } = [];
}

internal sealed record BrowserInitializationRequest(
    [property: JsonPropertyName("canvasSelector")] string CanvasSelector,
    [property: JsonPropertyName("executionMode")] string ExecutionMode,
    [property: JsonPropertyName("syncReadbackMode")] string SyncReadbackMode,
    [property: JsonPropertyName("gpuProfile")] string GpuProfile,
    [property: JsonPropertyName("powerPreference")] string PowerPreference,
    [property: JsonPropertyName("enableDiagnostics")] bool EnableDiagnostics);

public readonly record struct BrowserDispatcherCounters(
    long Frames,
    long CommandDispatches,
    long UploadDispatches,
    long CommandBytes,
    long UploadBytes);
