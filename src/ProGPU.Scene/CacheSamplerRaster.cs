using System;
using ProGPU.Backend;

namespace ProGPU.Scene;

/// <summary>
/// An owned raw cache texture and its exact retained source/device generation.
/// The texture is sampled directly in normalized coordinates; consumer opacity,
/// transforms and effect-input dimensions do not belong to this raster.
/// </summary>
public sealed class CacheSamplerRaster : IDisposable
{
    private GpuPicture? _picture;
    private bool _disposed;

    internal CacheSamplerRaster(GpuTexture texture, GpuPicture picture,
        CacheSamplerRasterFrame frame, object sourceIdentity, ulong sourceRevision,
        WgpuDeviceIdentity deviceIdentity, bool enableClearType)
    {
        Texture = texture; _picture = picture; Frame = frame;
        SourceIdentity = sourceIdentity; SourceRevision = sourceRevision;
        DeviceIdentity = deviceIdentity; EnableClearType = enableClearType;
    }

    /// <summary>Borrowed from this owner; disposing the raster retires the texture.</summary>
    public GpuTexture Texture { get; }
    public CacheSamplerRasterFrame Frame { get; }
    public object SourceIdentity { get; }
    public ulong SourceRevision { get; }
    public WgpuDeviceIdentity DeviceIdentity { get; }
    public bool EnableClearType { get; }
    public bool IsDisposed => _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // GpuTexture uses the context's original submission retirement queues.
        try { Texture.Dispose(); }
        finally { _picture?.Dispose(); _picture = null; }
    }
}
