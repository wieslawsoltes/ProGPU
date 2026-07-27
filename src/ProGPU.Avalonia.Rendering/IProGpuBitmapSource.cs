using Avalonia.Platform;
using ProGPU.Backend;

namespace Avalonia.ProGpu;

/// <summary>
/// Typed bitmap-to-texture boundary used by the ProGPU renderer.
/// </summary>
internal interface IProGpuBitmapSource : IBitmapImpl
{
    GpuTexture? Texture { get; }

    void EnsureGpuTexture();
}

/// <summary>
/// Resolves a lazily migrated texture for a specific WebGPU device domain.
/// </summary>
internal interface IPortableProGpuBitmapSource
{
    GpuTexture? GetTexture(WgpuContext requiredContext);
}
