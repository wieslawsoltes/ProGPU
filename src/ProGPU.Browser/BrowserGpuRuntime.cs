using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProGPU.Browser;

public static partial class BrowserGpuRuntime
{
    private static BrowserDispatcherCounters _counters;

    public static BrowserGpuCapabilities? Capabilities { get; private set; }
    public static BrowserDispatcherCounters Counters => _counters;

    public static async Task<BrowserGpuCapabilities> InitializeAsync(
        BrowserAppHostOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new BrowserAppHostOptions();
        if (!OperatingSystem.IsBrowser())
            throw new PlatformNotSupportedException("The ProGPU browser runtime requires browser-wasm.");

        var request = new BrowserInitializationRequest(
            options.CanvasSelector,
            options.ExecutionMode.ToString(),
            options.SyncReadbackMode.ToString(),
            options.GpuProfile.ToString(),
            options.PowerPreference,
            options.EnableDiagnostics);
        var json = JsonSerializer.Serialize(request, BrowserJsonContext.Default.BrowserInitializationRequest);
        var result = await InitializeCoreAsync(json).WaitAsync(cancellationToken).ConfigureAwait(false);
        var capabilities = JsonSerializer.Deserialize(result, BrowserJsonContext.Default.BrowserGpuCapabilities)
            ?? throw new InvalidOperationException("The browser WebGPU initializer returned no capabilities.");
        Capabilities = capabilities;
        _counters = default;
        return capabilities;
    }

    public static unsafe void Dispatch(BrowserGpuCommandEncoder commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (Capabilities?.IsSupported != true)
            throw new InvalidOperationException("Browser WebGPU has not been initialized.");
        commands.Seal();
        DispatchCore(commands.Address, commands.Length);
        _counters = _counters with
        {
            Frames = _counters.Frames + (commands.ContainsFrame ? 1 : 0),
            CommandDispatches = _counters.CommandDispatches + 1,
            CommandBytes = _counters.CommandBytes + commands.Length
        };
    }

    public static unsafe void DispatchUpload(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        fixed (byte* pointer = bytes)
        {
            DispatchUploadCore((nint)pointer, bytes.Length);
        }
        _counters = _counters with
        {
            UploadDispatches = _counters.UploadDispatches + 1,
            UploadBytes = _counters.UploadBytes + bytes.Length
        };
    }

    [JSImport("initialize", "progpu-browser")]
    private static partial Task<string> InitializeCoreAsync(string requestJson);

    [JSImport("dispatch", "progpu-browser")]
    private static partial void DispatchCore(nint address, int length);

    [JSImport("dispatchUpload", "progpu-browser")]
    private static partial void DispatchUploadCore(nint address, int length);

}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BrowserInitializationRequest))]
[JsonSerializable(typeof(BrowserGpuCapabilities))]
internal sealed partial class BrowserJsonContext : JsonSerializerContext;
