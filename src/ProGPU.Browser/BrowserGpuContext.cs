using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace ProGPU.Browser;

/// <summary>
/// Owns the browser dispatcher and the ordinary ProGPU context that is wired to it.
/// Renderer code consumes <see cref="Context"/> without browser-specific branches.
/// </summary>
public unsafe sealed class BrowserGpuContext : IDisposable
{
    private bool _disposed;

    private BrowserGpuContext(BrowserWebGpuApi api, WgpuContext context)
    {
        Api = api;
        Context = context;
    }

    public BrowserWebGpuApi Api { get; }
    public WgpuContext Context { get; }

    public static BrowserGpuContext Create(BrowserGpuCapabilities? capabilities = null)
    {
        capabilities ??= BrowserGpuRuntime.Capabilities
            ?? throw new InvalidOperationException("Browser WebGPU must be initialized before creating a renderer context.");
        if (!capabilities.IsSupported)
            throw new PlatformNotSupportedException("The current browser does not expose a usable navigator.gpu device.");

        var api = new BrowserWebGpuApi();
        var context = new WgpuContext();
        context.InitializeExternal(
            api,
            BrowserWebGpuApi.DeviceHandle,
            BrowserWebGpuApi.QueueHandle,
            BrowserWebGpuApi.SurfaceHandle,
            ParseTextureFormat(capabilities.CanvasFormat),
            supportsReadOnlyAndReadWriteStorageTextures: capabilities.ActiveProfile == BrowserGpuProfile.Full);
        return new BrowserGpuContext(api, context);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Context.Dispose();
        Api.Dispose();
        _disposed = true;
    }

    private static TextureFormat ParseTextureFormat(string format) => format switch
    {
        "bgra8unorm" => TextureFormat.Bgra8Unorm,
        "rgba8unorm" => TextureFormat.Rgba8Unorm,
        _ => throw new NotSupportedException($"The browser canvas format '{format}' is unsupported.")
    };
}
