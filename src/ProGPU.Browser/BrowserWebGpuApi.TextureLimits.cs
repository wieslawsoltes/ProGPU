using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace ProGPU.Browser;

public unsafe sealed partial class BrowserWebGpuApi : IWebGpuTextureLimitsSource
{
    private readonly double _textureLimitsGeneration;
    private bool _textureLimitsRevoked;

    /// <summary>
    /// Reads limits only from the default runtime's current device. The Silk
    /// pointer is an opaque browser token, not native memory or adapter proof.
    /// Custom dispatchers and worker transports without a live device query
    /// return false with zero outputs.
    /// </summary>
    public bool TryGetTextureLimits(Device* device, out uint maximumTextureWidth,
        out uint maximumTextureHeight)
    {
        maximumTextureWidth = 0;
        maximumTextureHeight = 0;
        if (_disposed || _textureLimitsRevoked || device != DeviceHandle ||
            _textureLimitsGeneration == 0 ||
            !BrowserGpuRuntime.TryGetTextureDimension2D(_textureLimitsGeneration, out uint maximum))
            return false;
        maximumTextureWidth = maximum;
        maximumTextureHeight = maximum;
        return true;
    }
}
