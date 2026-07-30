using ProGPU.Backend;

namespace ProGPU.Media.Playback;

/// <summary>
/// Lazily imports one native decoder allocation into the WebGPU device chosen
/// by the first renderer. Import is O(1), performs no pixel copy, and transfers
/// the native owner to the imported texture only after a successful import.
/// </summary>
public sealed class ExternalMediaGpuFrame :
    IMediaGpuFrame,
    IProGpuContextTextureLeaseSource
{
    private readonly object _gate = new();
    private readonly ProGpuExternalTextureDescriptor
        _externalDescriptor;
    private IDisposable? _nativeOwner;
    private GpuTexture? _texture;
    private int _references = 1;
    private int _disposeRequested;

    public ExternalMediaGpuFrame(
        in MediaGpuFrameDescriptor descriptor,
        in ProGpuExternalTextureDescriptor externalDescriptor,
        IDisposable nativeOwner)
    {
        ArgumentNullException.ThrowIfNull(nativeOwner);
        if (externalDescriptor.Width != descriptor.Width ||
            externalDescriptor.Height != descriptor.Height)
        {
            throw new ArgumentException(
                "External texture dimensions must match the media frame descriptor.",
                nameof(externalDescriptor));
        }

        Descriptor = descriptor;
        _externalDescriptor = externalDescriptor;
        _nativeOwner = nativeOwner;
    }

    public MediaGpuFrameDescriptor Descriptor { get; }

    public bool TryGetGpuTexture(out GpuTexture texture)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposeRequested) == 0 &&
                _texture is { IsDisposed: false } current)
            {
                texture = current;
                return true;
            }
        }
        texture = null!;
        return false;
    }

    public bool TryGetGpuTexture(
        WgpuContext requiredContext,
        out GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(requiredContext);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposeRequested) != 0)
            {
                texture = null!;
                return false;
            }
            return TryImportLocked(requiredContext, out texture);
        }
    }

    public bool TryAcquireGpuTextureLease(
        out IProGpuTextureLease lease)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposeRequested) != 0 ||
                _texture is not { IsDisposed: false } texture)
            {
                lease = null!;
                return false;
            }
            checked
            {
                _references++;
            }
            lease = new Lease(this, texture);
            return true;
        }
    }

    public bool TryAcquireGpuTextureLease(
        WgpuContext requiredContext,
        out IProGpuTextureLease lease)
    {
        ArgumentNullException.ThrowIfNull(requiredContext);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposeRequested) != 0 ||
                !TryImportLocked(
                    requiredContext,
                    out GpuTexture texture))
            {
                lease = null!;
                return false;
            }
            checked
            {
                _references++;
            }
            lease = new Lease(this, texture);
            return true;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) == 0)
        {
            ReleaseReference();
        }
    }

    private bool TryImportLocked(
        WgpuContext requiredContext,
        out GpuTexture texture)
    {
        if (_texture is { IsDisposed: false } current)
        {
            if (!current.Context.SharesDeviceWith(requiredContext))
            {
                texture = null!;
                return false;
            }
            texture = current;
            return true;
        }

        IDisposable? owner = _nativeOwner;
        if (owner is null ||
            !requiredContext.TryImportExternalTexture(
                in _externalDescriptor,
                owner,
                out texture))
        {
            texture = null!;
            return false;
        }

        _nativeOwner = null;
        _texture = texture;
        return true;
    }

    private void ReleaseReference()
    {
        GpuTexture? texture = null;
        IDisposable? nativeOwner = null;
        lock (_gate)
        {
            _references--;
            if (_references != 0)
            {
                return;
            }
            texture = _texture;
            _texture = null;
            nativeOwner = _nativeOwner;
            _nativeOwner = null;
        }

        texture?.Dispose();
        nativeOwner?.Dispose();
    }

    private sealed class Lease : IProGpuTextureLease
    {
        private ExternalMediaGpuFrame? _owner;

        internal Lease(
            ExternalMediaGpuFrame owner,
            GpuTexture texture)
        {
            _owner = owner;
            Texture = texture;
        }

        public GpuTexture Texture { get; }

        public void Dispose()
        {
            ExternalMediaGpuFrame? owner =
                Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseReference();
        }
    }
}
