using System;
using System.Runtime.CompilerServices;
using ProGPU.Backend;

namespace ProGPU.Scene;

/// <summary>
/// An owned raw cache texture and its exact retained source/device generation.
/// The texture is sampled directly in normalized coordinates; consumer opacity,
/// transforms and effect-input dimensions do not belong to this raster.
/// </summary>
public sealed class CacheSamplerRaster : IProGpuTextureLeaseSource, IDisposable, ICacheSamplerRetirementParticipant
{
    private static readonly ConditionalWeakTable<WgpuContext, CacheSamplerRetirement> s_retirements = new();
    static CacheSamplerRaster() => WgpuContext.Disposing += RetireContext;
    internal static void EnsureContextAcceptsCapture(WgpuContext context) =>
        s_retirements.GetValue(context, static owner => new(owner.QueueExternalTextureOwnerDisposal)).EnsureAcceptsCapture();
    private GpuPicture? _picture;
    private readonly CacheSamplerRetirement _retirementOwner;
    private int _ownerReleased;
    private int _references = 1;
    private readonly int _creatingThread = Environment.CurrentManagedThreadId;
    private int _payloadRetired;
    private bool _registered;
    private bool _retirementInProgress;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? _retirementFailure;

    internal CacheSamplerRaster(GpuTexture texture, GpuPicture picture,
        CacheSamplerRasterFrame frame, object sourceIdentity, ulong sourceRevision,
        WgpuDeviceIdentity deviceIdentity, bool enableClearType)
    {
        Texture = texture; _picture = picture; Frame = frame;
        SourceIdentity = sourceIdentity; SourceRevision = sourceRevision;
        DeviceIdentity = deviceIdentity; EnableClearType = enableClearType;
        // The owning context retires surviving parameter/recording generations
        // before its final resource drain. Shutdown never relies on a later GC.
        _retirementOwner = s_retirements.GetValue(texture.Context, static owner => new(owner.QueueExternalTextureOwnerDisposal));
        _retirementOwner.Add(this);
        _registered = true;
    }

    /// <summary>Borrowed from this owner; disposing the raster retires the texture.</summary>
    public GpuTexture Texture { get; }
    public CacheSamplerRasterFrame Frame { get; }
    public object SourceIdentity { get; }
    public ulong SourceRevision { get; }
    public WgpuDeviceIdentity DeviceIdentity { get; }
    public bool EnableClearType { get; }
    public bool IsDisposed => System.Threading.Volatile.Read(ref _ownerReleased) != 0 ||
        System.Threading.Volatile.Read(ref _payloadRetired) != 0;

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
            if (references == 0 || System.Threading.Volatile.Read(ref _payloadRetired) != 0 || Texture.IsDisposed) return false;
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
        if (System.Threading.Volatile.Read(ref _payloadRetired) != 0) return;
        if (Environment.CurrentManagedThreadId == _creatingThread) RetireResources();
        else _retirementOwner.Queue(new Retirement(this));
    }

    private static void RetireContext(WgpuContext context)
    {
        if (s_retirements.TryGetValue(context, out CacheSamplerRetirement? owner)) owner.RetireAll();
    }

    void ICacheSamplerRetirementParticipant.Retire() => RetireResources();

    private void RetireResources()
    {
        lock (Texture.Context.RenderLock)
        {
            _retirementFailure?.Throw();
            if (_retirementInProgress)
                throw new InvalidOperationException("A cache source callback cannot complete its own retirement reentrantly.");
            if (_payloadRetired != 0) return;
            if (Environment.CurrentManagedThreadId != _creatingThread)
            {
                _retirementOwner.RetainFailure(this);
                throw new InvalidOperationException("Owned cache source retirement requires its creating thread.");
            }
            // Invalidate acquisition before source callbacks can reenter. Try
            // both payload releases and preserve the first failure; an uncertain
            // release remains latched and blocks successful context shutdown.
            System.Threading.Volatile.Write(ref _payloadRetired, 1);
            _retirementInProgress = true;
            GpuPicture? picture = System.Threading.Interlocked.Exchange(ref _picture, null);
            try
            {
                try { Texture.Dispose(); }
                catch (Exception error) { _retirementFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error); }
                try { picture?.Dispose(); }
                catch (Exception error) { _retirementFailure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error); }
            }
            finally { _retirementInProgress = false; }
            if (_retirementFailure is not null)
            {
                _retirementOwner.RetainFailure(this, picture);
                _retirementFailure.Throw();
            }
            _retirementOwner.Remove(this);
            GC.SuppressFinalize(this);
        }
    }

    private sealed class Retirement(CacheSamplerRaster owner) : IDisposable
    {
        public void Dispose() => owner.RetireResources();
    }

    ~CacheSamplerRaster()
    {
        // The finalizer only transfers an abandoned owned payload to its
        // existing context drain. It never invokes source lease callbacks.
        try
        {
            if (!_registered) return; // Failed construction still belongs to its caller.
            if (System.Threading.Volatile.Read(ref _payloadRetired) == 0)
                _retirementOwner.Queue(new Retirement(this));
        }
        catch { }
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
            CacheSamplerRaster? owner = System.Threading.Volatile.Read(ref _owner);
            if (owner is null || !owner.TryAcquireRetained(out var retained)) return false;
            if (!ReferenceEquals(owner, System.Threading.Volatile.Read(ref _owner)))
            {
                retained.Dispose();
                return false;
            }
            lease = retained;
            return true;
        }
        internal void QueueRetirement()
        {
            // A parameter finalizer is not a source/render-thread drain. Retain
            // this entire lease until the existing context retirement owner
            // releases it, including any source leases in the recorded picture.
            CacheSamplerRaster? owner = System.Threading.Volatile.Read(ref _owner);
            if (owner is null) return;
            owner._retirementOwner.Queue(this);
        }
        public void Dispose() => System.Threading.Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
