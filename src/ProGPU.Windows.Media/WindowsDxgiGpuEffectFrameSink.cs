using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using Silk.NET.WebGPU;

namespace ProGPU.Windows.Media;

/// <summary>
/// Bounded D3D11/Dawn/Media Foundation frame bridge for the built-in
/// affine and Gaussian export effects and generated color frames.
/// </summary>
/// <remarks>
/// Three source and three encoder targets are allocated once. Each frame
/// performs one D3D11 GPU copy into the source ring and either one affine
/// WebGPU render or two separable Gaussian passes into an encoder target.
/// One intermediate texture is allocated lazily and retained. Generated
/// colors replace the D3D11 copy with a WebGPU attachment clear and skip
/// spatial blur because a clamped constant field is invariant. Pixel storage
/// is never mapped or copied to the CPU. IMFTrackedSample returns targets
/// asynchronously, so residency is O(width * height * ring-size) and
/// independent of clip duration.
/// </remarks>
internal sealed unsafe class
    WindowsDxgiGpuEffectFrameSink :
    IDisposable
{
    private const int RingSize = 3;
    private const uint MutexTimeoutMilliseconds = 10_000;
    private readonly object _gate = new();
    private readonly nint _d3dDevice;
    private readonly nint _d3dContext;
    private readonly nint _readbackTexture;
    private readonly WgpuContext _gpuContext;
    private readonly uint _width;
    private readonly uint _height;
    private readonly Queue<Slot> _availableSources = new();
    private readonly Queue<Slot> _availableTargets = new();
    private readonly Slot[] _sources;
    private readonly Slot[] _targets;
    private Exception? _callbackFailure;
    private int _outstandingTargets;
    private bool _disposed;
    private bool _resourcesReleased;
    private GpuTexture? _blurIntermediate;

    internal WindowsDxgiGpuEffectFrameSink(
        DawnGpuContext dawn,
        nint d3dDevice,
        nint d3dContext,
        uint width,
        uint height)
    {
        ArgumentNullException.ThrowIfNull(dawn);
        _d3dDevice = d3dDevice;
        _d3dContext = d3dContext;
        _gpuContext = dawn.Context;
        _width = width;
        _height = height;
        _sources = new Slot[RingSize];
        _targets = new Slot[RingSize];
        try
        {
            for (int index = 0;
                 index < RingSize;
                 index++)
            {
                Slot source =
                    CreateSlot(
                        dawn,
                        d3dDevice,
                        width,
                        height,
                        target: false);
                _sources[index] = source;
                _availableSources.Enqueue(source);

                Slot target =
                    CreateSlot(
                        dawn,
                        d3dDevice,
                        width,
                        height,
                        target: true);
                _targets[index] = target;
                _availableTargets.Enqueue(target);
            }
            _readbackTexture =
                WindowsMediaNative.CreateBgraReadbackTexture(
                    _d3dDevice,
                    width,
                    height);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void ProcessAndWrite(
        nint decodedSample,
        nint sinkWriter,
        uint sinkStream,
        long timestamp,
        long duration,
        in WindowsGpuVideoEffectPlan effectPlan,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        Slot source =
            Rent(
                _availableSources,
                cancellationToken);
        Slot? target = null;
        bool sourceReturned = false;
        nint decodedTexture = 0;
        try
        {
            PrepareForD3D(source);
            decodedTexture =
                WindowsMediaNative.GetSampleD3D11Texture(
                    decodedSample);
            WindowsMediaNative.CopyD3D11Texture(
                _d3dContext,
                source.Texture,
                decodedTexture);
            WindowsMediaNative.ReleaseKeyedMutex(
                source.KeyedMutex);
            source.MutexOwned = false;
            source.Access.BeginAccess(initialized: true);

            target =
                Rent(
                    _availableTargets,
                    cancellationToken);
            Render(
                source.Access.Texture,
                target.Access.Texture.ViewPtr,
                target.Access.Texture.Format,
                effectPlan,
                applySpatialEffect: true);

            source.Access.EndAccess();
            ReturnSource(source);
            sourceReturned = true;

            Slot submittedTarget = target;
            target = null;
            SubmitTarget(
                submittedTarget,
                sinkWriter,
                sinkStream,
                timestamp,
                duration);
        }
        finally
        {
            WindowsMediaNative.Release(decodedTexture);
            if (!sourceReturned)
            {
                RecoverSource(source);
            }
            if (target is not null)
            {
                RecoverTargetBeforeTracking(target);
            }
        }
    }

    /// <summary>
    /// Applies the fused WebGPU effect pass and reads the final BGRA target
    /// through one retained D3D11 staging texture for PNG encoding.
    /// </summary>
    internal byte[] ProcessAndReadback(
        nint decodedSample,
        in WindowsGpuVideoEffectPlan effectPlan,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        Slot source =
            Rent(
                _availableSources,
                cancellationToken);
        Slot? target = null;
        bool sourceReturned = false;
        nint decodedTexture = 0;
        try
        {
            PrepareForD3D(source);
            decodedTexture =
                WindowsMediaNative.GetSampleD3D11Texture(
                    decodedSample);
            WindowsMediaNative.CopyD3D11Texture(
                _d3dContext,
                source.Texture,
                decodedTexture);
            WindowsMediaNative.ReleaseKeyedMutex(
                source.KeyedMutex);
            source.MutexOwned = false;
            source.Access.BeginAccess(initialized: true);

            target =
                Rent(
                    _availableTargets,
                    cancellationToken);
            Render(
                source.Access.Texture,
                target.Access.Texture.ViewPtr,
                target.Access.Texture.Format,
                effectPlan,
                applySpatialEffect: true);

            source.Access.EndAccess();
            ReturnSource(source);
            sourceReturned = true;

            byte[] pixels =
                AllocateReadback();
            Slot readbackTarget = target;
            target = null;
            ReadTarget(
                readbackTarget,
                pixels);
            return pixels;
        }
        finally
        {
            WindowsMediaNative.Release(decodedTexture);
            if (!sourceReturned)
            {
                RecoverSource(source);
            }
            if (target is not null)
            {
                RecoverTargetBeforeTracking(target);
            }
        }
    }

    /// <summary>
    /// Generates one solid-color frame on the GPU, applies the same fused
    /// WebGPU effects as decoded video, and submits a tracked DXGI sample.
    /// </summary>
    internal void ProcessColorAndWrite(
        uint argbColor,
        nint sinkWriter,
        uint sinkStream,
        long timestamp,
        long duration,
        in WindowsGpuVideoEffectPlan effectPlan,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        Slot source =
            Rent(
                _availableSources,
                cancellationToken);
        Slot? target = null;
        bool sourceReturned = false;
        try
        {
            if (!source.Access.IsAccessActive)
            {
                source.Access.BeginAccess(
                    initialized: true);
            }
            GpuTextureClearer.Clear(
                source.Access.Texture,
                ToWebGpuColor(argbColor));
            target =
                Rent(
                    _availableTargets,
                    cancellationToken);
            Render(
                source.Access.Texture,
                target.Access.Texture.ViewPtr,
                target.Access.Texture.Format,
                effectPlan,
                applySpatialEffect: false);

            ReturnSource(source);
            sourceReturned = true;

            Slot submittedTarget = target;
            target = null;
            SubmitTarget(
                submittedTarget,
                sinkWriter,
                sinkStream,
                timestamp,
                duration);
        }
        finally
        {
            if (!sourceReturned)
            {
                ReturnSource(source);
            }
            if (target is not null)
            {
                RecoverTargetBeforeTracking(target);
            }
        }
    }

    /// <summary>
    /// Generates a solid frame and applies effects on WebGPU before the final
    /// retained-staging readback required by PNG encoding.
    /// </summary>
    internal byte[] ProcessColorAndReadback(
        uint argbColor,
        in WindowsGpuVideoEffectPlan effectPlan,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        Slot source =
            Rent(
                _availableSources,
                cancellationToken);
        Slot? target = null;
        bool sourceReturned = false;
        try
        {
            if (!source.Access.IsAccessActive)
            {
                source.Access.BeginAccess(
                    initialized: true);
            }
            GpuTextureClearer.Clear(
                source.Access.Texture,
                ToWebGpuColor(argbColor));
            target =
                Rent(
                    _availableTargets,
                    cancellationToken);
            Render(
                source.Access.Texture,
                target.Access.Texture.ViewPtr,
                target.Access.Texture.Format,
                effectPlan,
                applySpatialEffect: false);
            ReturnSource(source);
            sourceReturned = true;

            byte[] pixels =
                AllocateReadback();
            Slot readbackTarget = target;
            target = null;
            ReadTarget(
                readbackTarget,
                pixels);
            return pixels;
        }
        finally
        {
            if (!sourceReturned)
            {
                ReturnSource(source);
            }
            if (target is not null)
            {
                RecoverTargetBeforeTracking(target);
            }
        }
    }

    public void Dispose()
    {
        bool release;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            release = _outstandingTargets == 0;
        }
        if (release)
        {
            ReleaseResources();
        }
    }

    private Slot Rent(
        Queue<Slot> queue,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            while (queue.Count == 0)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
                if (_callbackFailure is not null)
                {
                    throw new InvalidOperationException(
                        "Media Foundation could not return a DXGI encoder target.",
                        _callbackFailure);
                }
                ObjectDisposedException.ThrowIf(
                    _disposed,
                    this);
                Monitor.Wait(_gate, 10);
            }
            return queue.Dequeue();
        }
    }

    private void PrepareForD3D(
        Slot source)
    {
        if (source.Access.IsAccessActive)
        {
            source.Access.EndAccess();
        }
        if (!WindowsMediaNative.TryAcquireKeyedMutex(
                source.KeyedMutex,
                MutexTimeoutMilliseconds))
        {
            throw new TimeoutException(
                "Timed out acquiring a decoded-frame staging texture from Dawn.");
        }
        source.MutexOwned = true;
    }

    private void SubmitTarget(
        Slot target,
        nint sinkWriter,
        uint sinkStream,
        long timestamp,
        long duration)
    {
        bool targetRegistered = false;
        bool targetTracked = false;
        nint encodedSample = 0;
        try
        {
            target.Access.EndAccess();
            if (!WindowsMediaNative.TryAcquireKeyedMutex(
                    target.KeyedMutex,
                    MutexTimeoutMilliseconds))
            {
                throw new TimeoutException(
                    "Timed out acquiring the WebGPU encoder target from Dawn.");
            }
            target.MutexOwned = true;
            lock (_gate)
            {
                _outstandingTargets++;
                targetRegistered = true;
            }
            encodedSample =
                WindowsMediaNative.CreateTrackedDxgiSample(
                    target.Texture,
                    timestamp,
                    duration,
                    target.Callback!.NativePointer);
            targetTracked = true;
            WindowsMediaNative.WriteSinkSample(
                sinkWriter,
                sinkStream,
                encodedSample);
        }
        finally
        {
            WindowsMediaNative.Release(encodedSample);
            if (!targetTracked)
            {
                if (targetRegistered)
                {
                    ReturnTargetFromCallback(target);
                }
                else
                {
                    RecoverTargetBeforeTracking(target);
                }
            }
        }
    }

    private static Color ToWebGpuColor(
        uint argbColor)
    {
        const double scale = 1d / byte.MaxValue;
        return new Color
        {
            R = ((argbColor >> 16) & 0xff) * scale,
            G = ((argbColor >> 8) & 0xff) * scale,
            B = (argbColor & 0xff) * scale,
            A = ((argbColor >> 24) & 0xff) * scale
        };
    }

    private void Render(
        GpuTexture source,
        TextureView* destinationView,
        TextureFormat destinationFormat,
        in WindowsGpuVideoEffectPlan effectPlan,
        bool applySpatialEffect)
    {
        if (!applySpatialEffect ||
            !effectPlan.HasSpatialEffect)
        {
            GpuTextureBlitter.Blit(
                source,
                destinationView,
                destinationFormat,
                effectPlan.ColorTransform);
            return;
        }

        _blurIntermediate ??=
            new GpuTexture(
                _gpuContext,
                _width,
                _height,
                TextureFormat.Bgra8Unorm,
                TextureUsage.TextureBinding |
                    TextureUsage.RenderAttachment,
                "Windows Media Gaussian Intermediate",
                alphaMode:
                    GpuTextureAlphaMode.Straight);
        GpuTextureGaussianBlur.Blur(
            source,
            _blurIntermediate,
            destinationView,
            destinationFormat,
            effectPlan.BlurStandardDeviation,
            effectPlan.ColorTransform);
    }

    private byte[] AllocateReadback() =>
        GC.AllocateUninitializedArray<byte>(
            checked(
                (int)_width *
                (int)_height *
                4));

    private void ReadTarget(
        Slot target,
        Span<byte> destination)
    {
        target.Access.EndAccess();
        try
        {
            if (!WindowsMediaNative.TryAcquireKeyedMutex(
                    target.KeyedMutex,
                    MutexTimeoutMilliseconds))
            {
                throw new TimeoutException(
                    "Timed out acquiring the WebGPU thumbnail target from Dawn.");
            }
            target.MutexOwned = true;
            WindowsMediaNative.ReadBgraTexture(
                _d3dContext,
                target.Texture,
                _readbackTexture,
                _width,
                _height,
                destination);
        }
        finally
        {
            RecoverTargetBeforeTracking(target);
        }
    }

    private void RecoverSource(
        Slot source)
    {
        try
        {
            if (source.MutexOwned)
            {
                WindowsMediaNative.ReleaseKeyedMutex(
                    source.KeyedMutex);
                source.MutexOwned = false;
            }
            if (!source.Access.IsAccessActive)
            {
                source.Access.BeginAccess(
                    initialized: true);
            }
        }
        finally
        {
            ReturnSource(source);
        }
    }

    private void ReturnSource(
        Slot source)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _availableSources.Enqueue(source);
            }
            Monitor.PulseAll(_gate);
        }
    }

    private void RecoverTargetBeforeTracking(
        Slot target)
    {
        try
        {
            if (target.MutexOwned)
            {
                WindowsMediaNative.ReleaseKeyedMutex(
                    target.KeyedMutex);
                target.MutexOwned = false;
            }
            if (!target.Access.IsAccessActive)
            {
                target.Access.BeginAccess(
                    initialized: true);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _availableTargets.Enqueue(target);
                }
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void ReturnTargetFromCallback(
        Slot target)
    {
        bool releaseResources = false;
        try
        {
            if (target.MutexOwned)
            {
                WindowsMediaNative.ReleaseKeyedMutex(
                    target.KeyedMutex);
                target.MutexOwned = false;
            }
            target.Access.BeginAccess(
                initialized: true);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _callbackFailure ??= exception;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_outstandingTargets > 0)
                {
                    _outstandingTargets--;
                }
                if (!_disposed &&
                    _callbackFailure is null)
                {
                    _availableTargets.Enqueue(target);
                }
                releaseResources =
                    _disposed &&
                    _outstandingTargets == 0;
                Monitor.PulseAll(_gate);
            }
            if (releaseResources)
            {
                ReleaseResources();
            }
        }
    }

    private Slot CreateSlot(
        DawnGpuContext dawn,
        nint d3dDevice,
        uint width,
        uint height,
        bool target)
    {
        nint texture =
            WindowsMediaNative.CreateSharedVideoTexture(
                d3dDevice,
                width,
                height,
                out nint sharedHandle,
                out nint keyedMutex);
        DawnExplicitSharedTextureAccess? access = null;
        try
        {
            var descriptor =
                new ProGpuExternalTextureDescriptor(
                    ProGpuExternalTextureHandleKind
                        .DxgiSharedHandle,
                    sharedHandle,
                    width,
                    height,
                    TextureFormat.Bgra8Unorm,
                    TextureUsage.TextureBinding |
                    TextureUsage.RenderAttachment,
                    GpuTextureAlphaMode.Straight,
                    IsInitialized: false)
                {
                    UsesKeyedMutex = true
                };
            var owner = new NoopOwner();
            if (!dawn.TryImportDxgiRenderTarget(
                    in descriptor,
                    owner,
                    out access))
            {
                owner.Dispose();
                throw new NotSupportedException(
                    "The active Dawn D3D12 device cannot import a keyed-mutex DXGI render target.");
            }
            return new Slot(
                texture,
                sharedHandle,
                keyedMutex,
                access,
                target
                    ? new WindowsMediaTrackedSampleCallback(
                        () =>
                            ReturnTargetFromCallback(
                                FindTargetByAccess(
                                    access)))
                    : null);
        }
        catch
        {
            access?.Dispose();
            WindowsMediaNative.Release(keyedMutex);
            WindowsMediaNative.CloseSharedHandle(
                sharedHandle);
            WindowsMediaNative.Release(texture);
            throw;
        }
    }

    private Slot FindTargetByAccess(
        DawnExplicitSharedTextureAccess access)
    {
        for (int index = 0;
             index < _targets.Length;
             index++)
        {
            Slot? slot = _targets[index];
            if (slot is not null &&
                ReferenceEquals(
                    slot.Access,
                    access))
            {
                return slot;
            }
        }
        throw new InvalidOperationException(
            "The tracked DXGI target no longer belongs to this sink.");
    }

    private static void DisposeSlots(
        Slot[] slots)
    {
        for (int index = 0;
             index < slots.Length;
             index++)
        {
            Slot? slot = slots[index];
            if (slot is null)
            {
                continue;
            }
            slot.Callback?.Dispose();
            slot.Access.Dispose();
            WindowsMediaNative.Release(
                slot.KeyedMutex);
            WindowsMediaNative.CloseSharedHandle(
                slot.SharedHandle);
            WindowsMediaNative.Release(
                slot.Texture);
        }
    }

    private void ReleaseResources()
    {
        lock (_gate)
        {
            if (_resourcesReleased)
            {
                return;
            }
            _resourcesReleased = true;
        }
        DisposeSlots(_targets);
        DisposeSlots(_sources);
        _blurIntermediate?.Dispose();
        _blurIntermediate = null;
        WindowsMediaNative.Release(
            _readbackTexture);
    }

    private sealed class Slot
    {
        internal Slot(
            nint texture,
            nint sharedHandle,
            nint keyedMutex,
            DawnExplicitSharedTextureAccess access,
            WindowsMediaTrackedSampleCallback? callback)
        {
            Texture = texture;
            SharedHandle = sharedHandle;
            KeyedMutex = keyedMutex;
            Access = access;
            Callback = callback;
        }

        internal nint Texture { get; }
        internal nint SharedHandle { get; }
        internal nint KeyedMutex { get; }
        internal DawnExplicitSharedTextureAccess Access { get; }
        internal WindowsMediaTrackedSampleCallback?
            Callback { get; }
        internal bool MutexOwned { get; set; }
    }

    private sealed class NoopOwner :
        IDisposable
    {
        public void Dispose()
        {
        }
    }
}
