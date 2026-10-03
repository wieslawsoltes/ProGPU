using System;
using ProGPU.Backend;

namespace ProGPU.Scene;

/// <summary>
/// An owned raw cache texture and its exact retained source/device generation.
/// The texture is sampled directly in normalized coordinates; consumer opacity,
/// transforms and effect-input dimensions do not belong to this raster.
/// </summary>
public sealed class CacheSamplerRaster : IProGpuTextureLeaseSource, IDisposable
{
    private GpuPicture? _picture;
    private int _ownerReleased;
    private int _references = 1;

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
    public bool IsDisposed => System.Threading.Volatile.Read(ref _ownerReleased) != 0;

    public bool TryGetGpuTexture(out GpuTexture texture)
    {
        texture = Texture;
        return !IsDisposed && !texture.IsDisposed;
    }

    public bool TryAcquireGpuTextureLease(out IProGpuTextureLease lease)
    {
        lease = null!;
        if (IsDisposed) return false;
        if (!TryAcquireRetained(out var candidate)) return false;
        if (!IsDisposed) { lease = candidate; return true; }
        candidate.Dispose();
        return false;
    }

    internal Lease AcquireSamplerLease()
    {
        if (!TryAcquireGpuTextureLease(out var lease)) throw new ObjectDisposedException(nameof(CacheSamplerRaster));
        return (Lease)lease;
    }

    private bool TryAcquireRetained(out Lease lease)
    {
        lease = null!;
        for (;;)
        {
            int references = System.Threading.Volatile.Read(ref _references);
            if (references == 0 || Texture.IsDisposed) return false;
            if (System.Threading.Interlocked.CompareExchange(ref _references, checked(references + 1), references) != references)
                continue;
            lease = new Lease(this);
            return true;
        }
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _ownerReleased, 1) == 0) Release();
    }

    private void Release()
    {
        if (System.Threading.Interlocked.Decrement(ref _references) != 0) return;
        // GpuTexture uses the context's original submission retirement queues.
        try { Texture.Dispose(); }
        finally { _picture?.Dispose(); _picture = null; }
    }

    internal sealed class Lease(CacheSamplerRaster owner) : IProGpuTextureLease, IProGpuTextureLeaseSource
    {
        private CacheSamplerRaster? _owner = owner;
        public GpuTexture Texture => _owner?.Texture ?? throw new ObjectDisposedException(nameof(Lease));
        public bool TryGetGpuTexture(out GpuTexture texture)
        {
            texture = _owner?.Texture!;
            return texture is not null && !texture.IsDisposed;
        }
        public bool TryAcquireGpuTextureLease(out IProGpuTextureLease lease)
        {
            lease = null!;
            CacheSamplerRaster? owner = _owner;
            if (owner is null || !owner.TryAcquireRetained(out var retained)) return false;
            lease = retained;
            return true;
        }
        public void Dispose() => System.Threading.Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
