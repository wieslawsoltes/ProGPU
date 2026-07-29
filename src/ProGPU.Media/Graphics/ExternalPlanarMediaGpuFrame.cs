using ProGPU.Backend;

namespace ProGPU.Media.Playback;

/// <summary>
/// Lazily imports two native decoder planes into one WebGPU device domain.
/// Native capture-buffer ownership is shared by both imported textures and is
/// released only after the frame and every luma/chroma texture lease end.
/// Import is O(1) and performs no pixel copy or CPU color conversion.
/// </summary>
public sealed class ExternalPlanarMediaGpuFrame :
    IMediaGpuPlanarFrame
{
    private readonly object _gate = new();
    private readonly ProGpuExternalTextureDescriptor
        _lumaDescriptor;
    private readonly ProGpuExternalTextureDescriptor
        _chromaDescriptor;
    private readonly SharedNativeOwner _nativeOwner;
    private GpuTexture? _lumaTexture;
    private GpuTexture? _chromaTexture;
    private int _references = 1;
    private int _disposeRequested;

    public ExternalPlanarMediaGpuFrame(
        in MediaGpuFrameDescriptor descriptor,
        in ProGpuExternalTextureDescriptor lumaDescriptor,
        in ProGpuExternalTextureDescriptor chromaDescriptor,
        IDisposable nativeOwner)
    {
        ArgumentNullException.ThrowIfNull(nativeOwner);
        if (descriptor.PixelFormat is not
            (MediaVideoPixelFormat.Nv12 or
             MediaVideoPixelFormat.P010))
        {
            throw new ArgumentException(
                "A planar external frame requires NV12 or P010 media metadata.",
                nameof(descriptor));
        }
        if (lumaDescriptor.Width != descriptor.Width ||
            lumaDescriptor.Height != descriptor.Height ||
            chromaDescriptor.Width !=
                (descriptor.Width + 1) / 2 ||
            chromaDescriptor.Height !=
                (descriptor.Height + 1) / 2)
        {
            throw new ArgumentException(
                "External plane dimensions do not match the media frame.",
                nameof(lumaDescriptor));
        }

        Descriptor = descriptor;
        _lumaDescriptor = lumaDescriptor;
        _chromaDescriptor = chromaDescriptor;
        _nativeOwner =
            new SharedNativeOwner(nativeOwner);
    }

    public MediaGpuFrameDescriptor Descriptor { get; }

    public bool TryGetGpuTexture(out GpuTexture texture)
    {
        texture = null!;
        return false;
    }

    public bool TryAcquireGpuTextureLease(
        out IProGpuTextureLease lease)
    {
        lease = null!;
        return false;
    }

    public bool TryAcquireGpuPlaneTextureLeases(
        WgpuContext requiredContext,
        out IProGpuTextureLease lumaLease,
        out IProGpuTextureLease chromaLease)
    {
        ArgumentNullException.ThrowIfNull(requiredContext);
        lock (_gate)
        {
            if (Volatile.Read(
                    ref _disposeRequested) != 0 ||
                !TryImportPlane(
                    requiredContext,
                    in _lumaDescriptor,
                    ref _lumaTexture,
                    out GpuTexture luma) ||
                !TryImportPlane(
                    requiredContext,
                    in _chromaDescriptor,
                    ref _chromaTexture,
                    out GpuTexture chroma))
            {
                lumaLease = null!;
                chromaLease = null!;
                return false;
            }

            checked
            {
                _references += 2;
            }
            lumaLease = new PlaneLease(this, luma);
            chromaLease =
                new PlaneLease(this, chroma);
            return true;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(
                ref _disposeRequested,
                1) == 0)
        {
            ReleaseReference();
        }
    }

    private bool TryImportPlane(
        WgpuContext requiredContext,
        in ProGpuExternalTextureDescriptor descriptor,
        ref GpuTexture? retainedTexture,
        out GpuTexture texture)
    {
        if (retainedTexture is
            { IsDisposed: false } current)
        {
            if (!current.Context.SharesDeviceWith(
                    requiredContext))
            {
                texture = null!;
                return false;
            }
            texture = current;
            return true;
        }

        IDisposable owner =
            _nativeOwner.Acquire();
        try
        {
            if (!requiredContext
                    .TryImportExternalTexture(
                        in descriptor,
                        owner,
                        out texture))
            {
                owner.Dispose();
                texture = null!;
                return false;
            }
        }
        catch
        {
            owner.Dispose();
            throw;
        }

        retainedTexture = texture;
        return true;
    }

    private void ReleaseReference()
    {
        GpuTexture? luma = null;
        GpuTexture? chroma = null;
        lock (_gate)
        {
            _references--;
            if (_references != 0)
            {
                return;
            }
            luma = _lumaTexture;
            chroma = _chromaTexture;
            _lumaTexture = null;
            _chromaTexture = null;
        }

        try
        {
            chroma?.Dispose();
        }
        finally
        {
            try
            {
                luma?.Dispose();
            }
            finally
            {
                _nativeOwner.Dispose();
            }
        }
    }

    private sealed class PlaneLease :
        IProGpuTextureLease
    {
        private ExternalPlanarMediaGpuFrame? _owner;

        internal PlaneLease(
            ExternalPlanarMediaGpuFrame owner,
            GpuTexture texture)
        {
            _owner = owner;
            Texture = texture;
        }

        public GpuTexture Texture { get; }

        public void Dispose()
        {
            ExternalPlanarMediaGpuFrame? owner =
                Interlocked.Exchange(
                    ref _owner,
                    null);
            owner?.ReleaseReference();
        }
    }

    private sealed class SharedNativeOwner :
        IDisposable
    {
        private readonly object _gate = new();
        private IDisposable? _value;
        private int _references = 1;

        internal SharedNativeOwner(IDisposable value)
        {
            _value = value;
        }

        internal IDisposable Acquire()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(
                    _value is null,
                    this);
                checked
                {
                    _references++;
                }
                return new Reference(this);
            }
        }

        public void Dispose()
        {
            IDisposable? value = null;
            lock (_gate)
            {
                _references--;
                if (_references == 0)
                {
                    value = _value;
                    _value = null;
                }
            }
            value?.Dispose();
        }

        private sealed class Reference :
            IDisposable
        {
            private SharedNativeOwner? _owner;

            internal Reference(
                SharedNativeOwner owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                SharedNativeOwner? owner =
                    Interlocked.Exchange(
                        ref _owner,
                        null);
                owner?.Dispose();
            }
        }
    }
}
