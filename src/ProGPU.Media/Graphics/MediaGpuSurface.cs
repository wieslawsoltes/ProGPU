using ProGPU.Backend;

namespace ProGPU.Media.Playback;

public enum MediaTransferMode
{
    NativeZeroCopy,
    GpuCopy,
    CpuUpload
}

public enum MediaVideoPixelFormat
{
    Unknown,
    Rgba8,
    Bgra8,
    Nv12,
    P010
}

public enum MediaColorPrimaries
{
    Unknown,
    Bt709,
    Bt2020,
    DisplayP3
}

public enum MediaTransferFunction
{
    Unknown,
    Srgb,
    Bt709,
    Pq,
    Hlg,
    Linear
}

public enum MediaMatrixCoefficients
{
    Unknown,
    Identity,
    Bt601,
    Bt709,
    Bt2020NonConstantLuminance
}

public readonly record struct MediaColorInfo(
    MediaColorPrimaries Primaries,
    MediaTransferFunction TransferFunction,
    MediaMatrixCoefficients Matrix,
    bool FullRange);

public readonly record struct MediaGpuFrameDescriptor(
    long Sequence,
    TimeSpan PresentationTime,
    TimeSpan Duration,
    uint Width,
    uint Height,
    MediaVideoPixelFormat PixelFormat,
    MediaTransferMode TransferMode,
    MediaColorInfo ColorInfo);

/// <summary>
/// Owns a decoded frame until disposal. Implementations must make texture
/// acquisition safe against concurrent disposal and keep native decoder
/// storage alive for every returned texture lease.
/// </summary>
public interface IMediaGpuFrame :
    IProGpuTextureLeaseSource,
    IDisposable
{
    MediaGpuFrameDescriptor Descriptor { get; }
}

/// <summary>
/// Optional two-plane frame contract for native NV12 and P010 decoder
/// allocations. Both leases must be acquired atomically and belong to the
/// requested WebGPU device domain.
/// </summary>
public interface IMediaGpuPlanarFrame :
    IMediaGpuFrame,
    IProGpuPlanarTextureLeaseSource
{
}

/// <summary>
/// Lock-free latest-frame surface. Publishing transfers frame ownership.
/// Rendering acquires a typed GPU lease and never reads pixels back to the CPU.
/// </summary>
public sealed class MediaGpuSurface :
    IProGpuContextTextureLeaseSource,
    IProGpuInvalidatingTextureSource,
    IProGpuPlanarTextureLeaseSource,
    IDisposable
{
    private IMediaGpuFrame? _current;
    private long _version;
    private int _disposed;

    public event EventHandler? FrameAvailable;

    event EventHandler? IProGpuInvalidatingTextureSource.TextureChanged
    {
        add => FrameAvailable += value;
        remove => FrameAvailable -= value;
    }

    public long Version => Volatile.Read(ref _version);

    public MediaGpuFrameDescriptor CurrentDescriptor =>
        Volatile.Read(ref _current)?.Descriptor ?? default;

    public void Publish(IMediaGpuFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        IMediaGpuFrame? previous =
            Interlocked.Exchange(ref _current, frame);
        if (ReferenceEquals(previous, frame))
        {
            return;
        }
        Interlocked.Increment(ref _version);
        previous?.Dispose();
        FrameAvailable?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        IMediaGpuFrame? previous =
            Interlocked.Exchange(ref _current, null);
        if (previous is null)
        {
            return;
        }

        Interlocked.Increment(ref _version);
        previous.Dispose();
        FrameAvailable?.Invoke(this, EventArgs.Empty);
    }

    public bool TryGetGpuTexture(out GpuTexture texture)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            IMediaGpuFrame? frame = Volatile.Read(ref _current);
            if (frame is null)
            {
                break;
            }

            try
            {
                if (frame.TryGetGpuTexture(out texture))
                {
                    return true;
                }
            }
            catch (ObjectDisposedException)
            {
                // A newer frame won publication. Retry against that frame.
            }
        }

        texture = null!;
        return false;
    }

    public bool TryAcquireGpuTextureLease(
        out IProGpuTextureLease lease)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            IMediaGpuFrame? frame = Volatile.Read(ref _current);
            if (frame is null)
            {
                break;
            }

            try
            {
                if (frame.TryAcquireGpuTextureLease(out lease))
                {
                    return true;
                }
            }
            catch (ObjectDisposedException)
            {
                // A newer frame won publication. Retry against that frame.
            }
        }

        lease = null!;
        return false;
    }

    public bool TryGetGpuTexture(
        WgpuContext requiredContext,
        out GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(requiredContext);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            IMediaGpuFrame? frame = Volatile.Read(ref _current);
            if (frame is null)
            {
                break;
            }

            try
            {
                bool found =
                    frame is IProGpuContextTextureLeaseSource contextSource
                        ? contextSource.TryGetGpuTexture(
                            requiredContext,
                            out texture)
                        : frame.TryGetGpuTexture(out texture);
                if (found)
                {
                    return true;
                }
            }
            catch (ObjectDisposedException)
            {
                // A newer frame won publication. Retry against that frame.
            }
        }

        texture = null!;
        return false;
    }

    public bool TryAcquireGpuTextureLease(
        WgpuContext requiredContext,
        out IProGpuTextureLease lease)
    {
        ArgumentNullException.ThrowIfNull(requiredContext);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            IMediaGpuFrame? frame = Volatile.Read(ref _current);
            if (frame is null)
            {
                break;
            }

            try
            {
                bool found =
                    frame is IProGpuContextTextureLeaseSource contextSource
                        ? contextSource.TryAcquireGpuTextureLease(
                            requiredContext,
                            out lease)
                        : frame.TryAcquireGpuTextureLease(out lease);
                if (found)
                {
                    return true;
                }
            }
            catch (ObjectDisposedException)
            {
                // A newer frame won publication. Retry against that frame.
            }
        }

        lease = null!;
        return false;
    }

    public bool TryAcquireGpuPlaneTextureLeases(
        WgpuContext requiredContext,
        out IProGpuTextureLease lumaLease,
        out IProGpuTextureLease chromaLease)
    {
        ArgumentNullException.ThrowIfNull(requiredContext);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            IMediaGpuFrame? frame =
                Volatile.Read(ref _current);
            if (frame is not IMediaGpuPlanarFrame planarFrame)
            {
                break;
            }

            try
            {
                if (planarFrame.TryAcquireGpuPlaneTextureLeases(
                        requiredContext,
                        out lumaLease,
                        out chromaLease))
                {
                    return true;
                }
            }
            catch (ObjectDisposedException)
            {
                // A newer frame won publication. Retry against that frame.
            }
        }

        lumaLease = null!;
        chromaLease = null!;
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Clear();
        }
    }
}
