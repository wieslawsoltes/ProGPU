namespace ProGPU.Backend;

public interface IProGpuTextureSource
{
    bool TryGetGpuTexture(out GpuTexture texture);
}

/// <summary>
/// Keeps a caller-owned GPU texture alive while deferred render commands use it.
/// Disposing the lease releases only the borrow; ownership of the texture stays
/// with the source that created the lease.
/// </summary>
public interface IProGpuTextureLease : IDisposable
{
    GpuTexture Texture { get; }
}

/// <summary>
/// Provides a typed lifetime lease for a GPU texture that can be referenced by
/// deferred render commands without copying or taking ownership of the texture.
/// </summary>
public interface IProGpuTextureLeaseSource : IProGpuTextureSource
{
    bool TryAcquireGpuTextureLease(out IProGpuTextureLease lease);
}

/// <summary>
/// Reports content-identity changes for a retained texture source. Consumers
/// use this notification to rebuild only the affected retained command stream;
/// the texture itself continues to cross the boundary through a typed lease.
/// </summary>
public interface IProGpuInvalidatingTextureSource : IProGpuTextureLeaseSource
{
    event EventHandler? TextureChanged;
}

/// <summary>
/// Materializes and leases a texture in the context that will consume it.
/// CPU-backed or otherwise portable image sources use this seam to avoid
/// allocating a texture before a presentation host has selected its device.
/// </summary>
public interface IProGpuContextTextureLeaseSource : IProGpuTextureLeaseSource
{
    bool TryGetGpuTexture(WgpuContext requiredContext, out GpuTexture texture);

    bool TryAcquireGpuTextureLease(
        WgpuContext requiredContext,
        out IProGpuTextureLease lease);
}

/// <summary>
/// Atomically leases two texture planes in the WebGPU device domain that will
/// consume them. This is the framework-neutral ownership seam for native
/// multi-plane allocations such as NV12 and P010; callers attach the
/// appropriate color-conversion metadata separately.
/// </summary>
public interface IProGpuPlanarTextureLeaseSource
{
    bool TryAcquireGpuPlaneTextureLeases(
        WgpuContext requiredContext,
        out IProGpuTextureLease lumaLease,
        out IProGpuTextureLease chromaLease);
}
