using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ProGPU.Backend;

namespace ProGPU.Scene;

/// <summary>
/// An owned raw cache texture and its exact retained source/device generation.
/// The texture is sampled directly in normalized coordinates; consumer opacity,
/// transforms and effect-input dimensions do not belong to this raster.
/// </summary>
public sealed class CacheSamplerRaster : IProGpuTextureLeaseSource, IDisposable
{
    private static readonly ConditionalWeakTable<WgpuContext, ContextRetirement> s_retirements = new();
    static CacheSamplerRaster() => WgpuContext.Disposing += RetireContext;
    internal static void EnsureContextAcceptsCapture(WgpuContext context) =>
        s_retirements.GetValue(context, static _ => new()).EnsureAcceptsCapture();
    private GpuPicture? _picture;
    private readonly ContextRetirement _retirementOwner;
    private int _ownerReleased;
    private int _references = 1;
    private readonly int _creatingThread = Environment.CurrentManagedThreadId;
    private int _payloadRetired;
    private bool _registered;
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
        _retirementOwner = s_retirements.GetValue(texture.Context, static _ => new());
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
        lock (Texture.Context.RenderLock)
        {
            if (_payloadRetired != 0) return;
            if (Environment.CurrentManagedThreadId == _creatingThread) RetireResources();
            else Texture.Context.QueueExternalTextureOwnerDisposal(new Retirement(this));
        }
    }

    private static void RetireContext(WgpuContext context)
    {
        if (s_retirements.TryGetValue(context, out ContextRetirement? owner)) owner.RetireAll();
    }

    private void RetireResources()
    {
        lock (Texture.Context.RenderLock)
        {
            _retirementFailure?.Throw();
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
            GpuPicture? picture = System.Threading.Interlocked.Exchange(ref _picture, null);
            try { Texture.Dispose(); }
            catch (Exception error) { _retirementFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error); }
            try { picture?.Dispose(); }
            catch (Exception error) { _retirementFailure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error); }
            if (_retirementFailure is not null)
            {
                _retirementOwner.RetainFailure(this);
                _retirementFailure.Throw();
            }
            _retirementOwner.Remove(this);
            GC.SuppressFinalize(this);
        }
    }

    private sealed class ContextRetirement
    {
        // Accessed only under the exact context's RenderLock. The weak context
        // key and weak generation entries must not turn a source-cache weak key
        // into a strong retained history through Raster.SourceIdentity. Track
        // resurrection so shutdown can also see a queued/finalizing generation.
        private readonly List<WeakReference<CacheSamplerRaster>> _rasters = [];
        // An uncertain release is not ordinary abandoned history. Its exact
        // owner stays strongly pending so GC cannot turn failure into success.
        private readonly HashSet<CacheSamplerRaster> _failed = [];
        private bool _retiring;
        internal void EnsureAcceptsCapture()
        {
            if (_retiring) throw new ObjectDisposedException(nameof(CacheSamplerRaster), "The owning context is retiring cache sources.");
        }
        internal void Add(CacheSamplerRaster raster)
        {
            EnsureAcceptsCapture();
            _rasters.RemoveAll(static entry => !entry.TryGetTarget(out _));
            _rasters.Add(new(raster, trackResurrection: true));
        }
        internal void RetainFailure(CacheSamplerRaster raster) => _failed.Add(raster);
        internal void Remove(CacheSamplerRaster raster)
        {
            _failed.Remove(raster);
            _rasters.RemoveAll(entry => !entry.TryGetTarget(out var target) || ReferenceEquals(target, raster));
        }
        internal void RetireAll()
        {
            _retiring = true;
            var snapshot = new HashSet<CacheSamplerRaster>(_failed);
            foreach (var entry in _rasters)
                if (entry.TryGetTarget(out var raster)) snapshot.Add(raster);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
            foreach (CacheSamplerRaster raster in snapshot)
            {
                try { raster.RetireResources(); }
                catch (Exception error)
                {
                    failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
                }
            }
            failure?.Throw();
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
            lock (Texture.Context.RenderLock)
            {
                if (_payloadRetired == 0)
                    Texture.Context.QueueExternalTextureOwnerDisposal(new Retirement(this));
            }
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
            lock (owner.Texture.Context.RenderLock)
            {
                if (owner._payloadRetired != 0) Dispose();
                else owner.Texture.Context.QueueExternalTextureOwnerDisposal(this);
            }
        }
        public void Dispose() => System.Threading.Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
