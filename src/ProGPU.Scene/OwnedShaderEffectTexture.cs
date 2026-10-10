using System;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using ProGPU.Backend;

namespace ProGPU.Scene;

/// <summary>
/// Owns one immutable shader-input texture generation. This type does not copy,
/// render or freeze mutable pixels: the caller completes the texture before
/// publication and must not write to it or dispose it after ownership transfer.
/// Independent leases retain the exact texture through deferred rendering.
/// </summary>
public sealed class OwnedShaderEffectTexture : IProGpuTextureLeaseSource, IDisposable,
    ICacheSamplerRetirementParticipant
{
    private static readonly ConditionalWeakTable<GpuTexture, object> s_ownedTextures = new();
    private readonly CacheSamplerRetirement _retirementOwner;
    private readonly int _creatingThread = Environment.CurrentManagedThreadId;
    private int _ownerReleased;
    private int _references = 1;
    private int _payloadRetired;
    private bool _registered;
    private bool _retirementInProgress;
    private ExceptionDispatchInfo? _retirementFailure;

    /// <summary>
    /// Transfers exclusive ownership only if construction succeeds. On failure
    /// the caller still owns the texture. A borrowed or still-mutable source
    /// must first produce a separate immutable generation; this is not a clone.
    /// </summary>
    public OwnedShaderEffectTexture(GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        Texture = texture;
        _retirementOwner = CacheSamplerRaster.GetRetirementOwner(texture.Context);
        lock (texture.Context.RenderLock)
        {
            ObjectDisposedException.ThrowIf(texture.IsDisposed || texture.Context.IsDisposed, texture);
            _retirementOwner.EnsureAcceptsCapture();
            // A marker, not the owner, avoids keeping an abandoned owner alive
            // merely because a caller still has a borrowed texture reference.
            s_ownedTextures.Add(texture, new object());
            try { _retirementOwner.Add(this); }
            catch { s_ownedTextures.Remove(texture); throw; }
            _registered = true;
        }
    }

    /// <summary>Borrowed exact texture; do not mutate or dispose it directly.</summary>
    public GpuTexture Texture { get; }
    public bool IsDisposed => Volatile.Read(ref _ownerReleased) != 0 || Volatile.Read(ref _payloadRetired) != 0;

    public bool TryGetGpuTexture(out GpuTexture texture)
    {
        texture = Texture;
        return !IsDisposed && !texture.IsDisposed && !texture.Context.IsDisposed;
    }

    public bool TryAcquireGpuTextureLease(out IProGpuTextureLease lease)
    {
        lease = null!;
        if (IsDisposed || !TryAcquireRetained(out var candidate)) return false;
        if (!IsDisposed) { lease = candidate; return true; }
        candidate.Dispose();
        return false;
    }

    private bool TryAcquireRetained(out Lease lease)
    {
        lease = null!;
        for (;;)
        {
            int references = Volatile.Read(ref _references);
            if (references == 0 || Volatile.Read(ref _payloadRetired) != 0 ||
                Texture.IsDisposed || Texture.Context.IsDisposed) return false;
            if (Interlocked.CompareExchange(ref _references, checked(references + 1), references) != references)
                continue;
            if (Volatile.Read(ref _payloadRetired) != 0 || Texture.IsDisposed || Texture.Context.IsDisposed)
            {
                Release();
                return false;
            }
            try { lease = new Lease(this); }
            catch { Release(); throw; }
            return true;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ownerReleased, 1) == 0) Release();
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0 || Volatile.Read(ref _payloadRetired) != 0) return;
        if (Environment.CurrentManagedThreadId == _creatingThread) RetireResources();
        else _retirementOwner.Queue(new Retirement(this));
    }

    void ICacheSamplerRetirementParticipant.Retire() => RetireResources();

    private void RetireResources()
    {
        lock (Texture.Context.RenderLock)
        {
            _retirementFailure?.Throw();
            if (_retirementInProgress)
                throw new InvalidOperationException("An owned shader texture cannot complete retirement reentrantly.");
            if (_payloadRetired != 0) return;
            if (Environment.CurrentManagedThreadId != _creatingThread)
            {
                _retirementOwner.RetainFailure(this);
                throw new InvalidOperationException("Owned shader texture retirement requires its creating thread.");
            }

            Volatile.Write(ref _payloadRetired, 1);
            _retirementInProgress = true;
            try { Texture.Dispose(); }
            catch (Exception error) { _retirementFailure = ExceptionDispatchInfo.Capture(error); }
            finally { _retirementInProgress = false; }
            if (_retirementFailure is not null)
            {
                _retirementOwner.RetainFailure(this, Texture);
                _retirementFailure.Throw();
            }
            s_ownedTextures.Remove(Texture);
            _retirementOwner.Remove(this);
            GC.SuppressFinalize(this);
        }
    }

    private sealed class Retirement(OwnedShaderEffectTexture owner) : IDisposable
    {
        public void Dispose() => owner.RetireResources();
    }

    ~OwnedShaderEffectTexture()
    {
        // Finalization only transfers ownership to the established context
        // drain. No source or texture-disposal callbacks run on this thread.
        try
        {
            if (_registered && Volatile.Read(ref _payloadRetired) == 0)
                _retirementOwner.Queue(new Retirement(this));
        }
        catch { }
    }

    internal sealed class Lease(OwnedShaderEffectTexture owner) : IProGpuTextureLease, IProGpuTextureLeaseSource
    {
        private OwnedShaderEffectTexture? _owner = owner;
        public GpuTexture Texture => _owner?.Texture ?? throw new ObjectDisposedException(nameof(Lease));
        public bool TryGetGpuTexture(out GpuTexture texture)
        {
            OwnedShaderEffectTexture? owner = Volatile.Read(ref _owner);
            texture = owner?.Texture!;
            return owner is not null && Volatile.Read(ref owner._payloadRetired) == 0 &&
                !texture.IsDisposed && !texture.Context.IsDisposed;
        }
        public bool TryAcquireGpuTextureLease(out IProGpuTextureLease lease)
        {
            lease = null!;
            OwnedShaderEffectTexture? owner = Volatile.Read(ref _owner);
            if (owner is null || !owner.TryAcquireRetained(out var candidate)) return false;
            if (!ReferenceEquals(owner, Volatile.Read(ref _owner)))
            {
                candidate.Dispose();
                return false;
            }
            lease = candidate;
            return true;
        }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
