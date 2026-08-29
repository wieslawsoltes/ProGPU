using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using Silk.NET.WebGPU;
using System.Runtime.CompilerServices;

namespace ProGPU.Direct2D;

/// <summary>
/// Alternates one genuine Direct2D/D3D11 producer with the same-adapter Dawn
/// D3D12 consumer without copying pixels through the CPU.
/// </summary>
public sealed unsafe class ProGpuDirect2DSurface :
    IProGpuContextTextureLeaseSource,
    IProGpuInvalidatingTextureSource,
    IDisposable
{
    private const uint DefaultMutexTimeoutMilliseconds = 5_000U;

    private readonly object _gate = new();
    private readonly DawnGpuContext _dawn;
    private readonly DawnExplicitSharedTextureAccess _access;
    private nint _nativeSurface;
    private bool _drawing;
    private bool _disposeRequested;
    private bool _resourcesDisposed;
    private int _leaseCount;
    private ulong _contentVersion;

    private ProGpuDirect2DSurface(
        DawnGpuContext dawn,
        DawnExplicitSharedTextureAccess access,
        nint nativeSurface,
        in ProGpuDirect2DSurfaceDescriptor descriptor)
    {
        _dawn = dawn;
        _access = access;
        _nativeSurface = nativeSurface;
        Descriptor = descriptor;
        _contentVersion = descriptor.ContentVersion;
    }

    public event EventHandler? TextureChanged;

    public ProGpuDirect2DSurfaceDescriptor Descriptor { get; private set; }

    public ulong ContentVersion
    {
        get
        {
            lock (_gate)
            {
                return _contentVersion;
            }
        }
    }

    public static ProGpuDirect2DSurface Create(
        DawnGpuContext dawn,
        ProGpuDirect2DSurfaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(dawn);
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Direct2D COM provider is available only on Windows.");
        }
        if (dawn.Context.IsDisposed ||
            dawn.Context.AdapterBackendType != BackendType.D3D12)
        {
            throw new NotSupportedException(
                "Direct2D sharing requires a live Dawn D3D12 context.");
        }
        ValidateOptions(options);
        if (ProGpuDirect2DNative.GetAbiVersion() !=
            ProGpuDirect2DNative.AbiVersion)
        {
            throw new NotSupportedException(
                "The installed ProGPU Direct2D native ABI does not match the managed provider.");
        }

        ProGpuDirect2DNative.SurfaceOptions nativeOptions =
            CreateNativeOptions(options);
        nint nativeSurface = 0;
        int nativeHResult = 0;
        ProGpuDirect2DStatus status =
            ProGpuDirect2DNative.SurfaceCreate(
                &nativeOptions,
                &nativeSurface,
                &nativeHResult);
        ThrowIfFailed("surface creation", status, nativeHResult);

        var owner = new NativeSurfaceOwner(nativeSurface);
        try
        {
            ProGpuDirect2DSurfaceDescriptor descriptor =
                ReadDescriptor(nativeSurface);
            ValidateDescriptor(descriptor, options);
            var externalDescriptor =
                new ProGpuExternalTextureDescriptor(
                    ProGpuExternalTextureHandleKind.DxgiSharedHandle,
                    descriptor.SharedNtHandle,
                    descriptor.Width,
                    descriptor.Height,
                    TextureFormat.Bgra8Unorm,
                    TextureUsage.TextureBinding |
                    TextureUsage.RenderAttachment,
                    GpuTextureAlphaMode.Premultiplied,
                    IsInitialized: descriptor.ContentVersion != 0U)
                {
                    UsesKeyedMutex = true
                };
            if (!dawn.TryImportDxgiSharedTexture(
                    in externalDescriptor,
                    owner,
                    out DawnExplicitSharedTextureAccess access))
            {
                throw new NotSupportedException(
                    "The Dawn D3D12 device rejected the Direct2D DXGI allocation; the adapter, format, usage, or shared-memory feature does not match.");
            }
            owner = null!;
            return new ProGpuDirect2DSurface(
                dawn,
                access,
                nativeSurface,
                in descriptor);
        }
        finally
        {
            owner?.Dispose();
        }
    }

    public ProGpuDirect2DComReference AcquireInterface(
        ProGpuDirect2DInterfaceKind kind)
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            nint value = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceGetInterface(
                    _nativeSurface,
                    kind,
                    &value);
            ThrowIfFailed(
                $"{kind} query",
                status,
                ProGpuDirect2DNative.SurfaceGetLastHResult(
                    _nativeSurface));
            if (value == 0)
            {
                throw new InvalidOperationException(
                    $"Direct2D returned a null {kind} interface.");
            }
            return new ProGpuDirect2DComReference(value, kind);
        }
    }

    /// <summary>
    /// Tries to create a genuine Microsoft Win2D CanvasDevice over this
    /// surface's exact WinRT IDirect3DDevice. The installed Win2D component
    /// must be registered in the process package graph, and the calling thread
    /// must already be initialized for Windows Runtime use.
    /// </summary>
    public bool TryAcquireMicrosoftWin2DCanvasDevice(
        out ProGpuDirect2DComReference? canvasDevice,
        out int nativeHResult)
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            nint value = 0;
            int resultHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceTryGetWin2DCanvasDevice(
                    _nativeSurface,
                    &value,
                    &resultHResult);
            nativeHResult = resultHResult;
            if (status == ProGpuDirect2DStatus.Win2DRuntimeUnavailable)
            {
                canvasDevice = null;
                return false;
            }
            ThrowIfFailed(
                "Microsoft Win2D CanvasDevice activation",
                status,
                nativeHResult);
            if (value == 0)
            {
                throw new InvalidOperationException(
                    "Win2D activation succeeded without returning a CanvasDevice.");
            }
            canvasDevice = new ProGpuDirect2DComReference(
                value,
                ProGpuDirect2DInterfaceKind.Win2DCanvasDevice);
            return true;
        }
    }

    public ProGpuDirect2DDrawingSession BeginDrawing(
        uint timeoutMilliseconds = DefaultMutexTimeoutMilliseconds)
    {
        ProGpuDirect2DComReference context;
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (_drawing)
            {
                throw new InvalidOperationException(
                    "A Direct2D drawing session is already active.");
            }
            if (_leaseCount != 0)
            {
                throw new InvalidOperationException(
                    "Direct2D cannot acquire the allocation while deferred ProGPU texture leases are active.");
            }

            context = AcquireInterface(
                ProGpuDirect2DInterfaceKind.D2D1DeviceContext1);
            _drawing = true;
        }

        DawnExplicitSharedTextureAccess? accessToDispose = null;
        bool dawnAccessEnded = true;
        try
        {
            _access.EndAccess();
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceBeginDraw(
                    _nativeSurface,
                    Descriptor.InitialAcquireKey,
                    timeoutMilliseconds);
            if (status != ProGpuDirect2DStatus.Success)
            {
                _access.BeginAccess(_contentVersion != 0U);
                dawnAccessEnded = false;
                ThrowIfFailed(
                    "BeginDraw",
                    status,
                    ProGpuDirect2DNative.SurfaceGetLastHResult(
                        _nativeSurface));
            }
            return new ProGpuDirect2DDrawingSession(this, context);
        }
        catch
        {
            lock (_gate)
            {
                _drawing = false;
                if (dawnAccessEnded)
                {
                    _disposeRequested = true;
                }
                accessToDispose = TryTakeResourcesForDisposal();
            }
            context.Dispose();
            accessToDispose?.Dispose();
            throw;
        }
    }

    public bool TryGetGpuTexture(out GpuTexture texture) =>
        TryGetGpuTexture(_dawn.Context, out texture);

    public bool TryGetGpuTexture(
        WgpuContext requiredContext,
        out GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(requiredContext);
        lock (_gate)
        {
            if (_disposeRequested || _resourcesDisposed || _drawing ||
                !ReferenceEquals(requiredContext, _dawn.Context))
            {
                texture = null!;
                return false;
            }
            texture = _access.Texture;
            return true;
        }
    }

    public bool TryAcquireGpuTextureLease(
        out IProGpuTextureLease lease) =>
        TryAcquireGpuTextureLease(_dawn.Context, out lease);

    public bool TryAcquireGpuTextureLease(
        WgpuContext requiredContext,
        out IProGpuTextureLease lease)
    {
        ArgumentNullException.ThrowIfNull(requiredContext);
        lock (_gate)
        {
            if (_disposeRequested || _resourcesDisposed || _drawing ||
                !ReferenceEquals(requiredContext, _dawn.Context))
            {
                lease = null!;
                return false;
            }
            checked
            {
                _leaseCount++;
            }
            lease = new BorrowedTextureLease(this, _access.Texture);
            return true;
        }
    }

    public void Dispose()
    {
        DawnExplicitSharedTextureAccess? access = null;
        lock (_gate)
        {
            if (_disposeRequested)
            {
                return;
            }
            _disposeRequested = true;
            access = TryTakeResourcesForDisposal();
        }
        access?.Dispose();
    }

    internal void CompleteDrawing()
    {
        EventHandler? changed = null;
        DawnExplicitSharedTextureAccess? accessToDispose = null;
        Exception? failure = null;
        nint nativeSurface;
        lock (_gate)
        {
            if (!_drawing || _resourcesDisposed)
            {
                return;
            }
            nativeSurface = _nativeSurface;
        }

        ulong tag1 = 0U;
        ulong tag2 = 0U;
        int nativeHResult = 0;
        ProGpuDirect2DStatus status =
            ProGpuDirect2DStatus.InvalidArgument;
        try
        {
            status = ProGpuDirect2DNative.SurfaceEndDraw(
                nativeSurface,
                Descriptor.InitialReleaseKey,
                &tag1,
                &tag2,
                &nativeHResult);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        ProGpuDirect2DSurfaceDescriptor descriptor = default;
        if (failure is null &&
            status == ProGpuDirect2DStatus.Success)
        {
            try
            {
                _access.BeginAccess(initialized: true);
                descriptor = ReadDescriptor(nativeSurface);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }
        else if (failure is null)
        {
            failure = new ProGpuDirect2DException(
                $"EndDraw (tags {tag1}/{tag2})",
                status,
                nativeHResult);
        }

        lock (_gate)
        {
            _drawing = false;
            if (failure is null)
            {
                Descriptor = descriptor;
                _contentVersion = descriptor.ContentVersion;
                if (!_disposeRequested)
                {
                    changed = TextureChanged;
                }
            }
            else
            {
                _disposeRequested = true;
            }
            accessToDispose = TryTakeResourcesForDisposal();
        }
        accessToDispose?.Dispose();
        changed?.Invoke(this, EventArgs.Empty);
        if (failure is not null)
        {
            throw failure;
        }
    }

    private void ReleaseLease()
    {
        DawnExplicitSharedTextureAccess? access = null;
        lock (_gate)
        {
            if (_leaseCount <= 0)
            {
                return;
            }
            _leaseCount--;
            access = TryTakeResourcesForDisposal();
        }
        access?.Dispose();
    }

    private DawnExplicitSharedTextureAccess? TryTakeResourcesForDisposal()
    {
        if (!_disposeRequested || _resourcesDisposed || _drawing ||
            _leaseCount != 0)
        {
            return null;
        }
        _resourcesDisposed = true;
        _nativeSurface = 0;
        return _access;
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(
            _disposeRequested || _resourcesDisposed,
            this);
    }

    private static void ValidateOptions(
        ProGpuDirect2DSurfaceOptions options)
    {
        if (options.Width == 0U || options.Height == 0U ||
            options.Width > 16_384U || options.Height > 16_384U)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Direct2D dimensions must be between 1 and 16384 pixels.");
        }
        if (!float.IsFinite(options.DpiX) || options.DpiX <= 0.0F ||
            !float.IsFinite(options.DpiY) || options.DpiY <= 0.0F)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Direct2D DPI must be finite and positive.");
        }
        const ProGpuDirect2DSurfaceFlags knownFlags =
            ProGpuDirect2DSurfaceFlags.EnableDebug |
            ProGpuDirect2DSurfaceFlags.AllowWarpFallback |
            ProGpuDirect2DSurfaceFlags.ForceWarp;
        if ((options.Flags & ~knownFlags) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Direct2D surface flags contain an unknown bit.");
        }
        if ((options.Flags & ProGpuDirect2DSurfaceFlags.ForceWarp) != 0 &&
            options.AdapterLuid.HasValue)
        {
            throw new ArgumentException(
                "A forced WARP device cannot select a hardware adapter LUID.",
                nameof(options));
        }
    }

    private static ProGpuDirect2DNative.SurfaceOptions CreateNativeOptions(
        ProGpuDirect2DSurfaceOptions options)
    {
        long luid = options.AdapterLuid.GetValueOrDefault();
        return new ProGpuDirect2DNative.SurfaceOptions
        {
            StructSize = (uint)Unsafe.SizeOf<
                ProGpuDirect2DNative.SurfaceOptions>(),
            Flags = (uint)options.Flags,
            Width = options.Width,
            Height = options.Height,
            DpiX = options.DpiX,
            DpiY = options.DpiY,
            AdapterLuidLow = unchecked((uint)luid),
            AdapterLuidHigh = unchecked((int)(luid >> 32))
        };
    }

    private static ProGpuDirect2DSurfaceDescriptor ReadDescriptor(
        nint nativeSurface)
    {
        var native = new ProGpuDirect2DNative.SurfaceDescriptor
        {
            StructSize = (uint)Unsafe.SizeOf<
                ProGpuDirect2DNative.SurfaceDescriptor>()
        };
        ProGpuDirect2DStatus status =
            ProGpuDirect2DNative.SurfaceGetDescriptor(
                nativeSurface,
                &native);
        ThrowIfFailed(
            "descriptor query",
            status,
            ProGpuDirect2DNative.SurfaceGetLastHResult(nativeSurface));
        long adapterLuid =
            (long)native.AdapterLuidHigh << 32 |
            native.AdapterLuidLow;
        return new ProGpuDirect2DSurfaceDescriptor(
            (ProGpuDirect2DDescriptorFlags)native.Flags,
            native.Width,
            native.Height,
            native.DpiX,
            native.DpiY,
            native.DxgiFormat,
            native.AlphaMode,
            adapterLuid,
            (nint)native.SharedNtHandle,
            native.InitialAcquireKey,
            native.InitialReleaseKey,
            native.ContentVersion);
    }

    private static void ValidateDescriptor(
        in ProGpuDirect2DSurfaceDescriptor descriptor,
        ProGpuDirect2DSurfaceOptions options)
    {
        const ProGpuDirect2DDescriptorFlags required =
            ProGpuDirect2DDescriptorFlags.KeyedMutex |
            ProGpuDirect2DDescriptorFlags.NtHandle;
        if ((descriptor.Flags & required) != required ||
            descriptor.Width != options.Width ||
            descriptor.Height != options.Height ||
            descriptor.DpiX != options.DpiX ||
            descriptor.DpiY != options.DpiY ||
            descriptor.DxgiFormat !=
                ProGpuDirect2DNative.DxgiFormatB8G8R8A8Unorm ||
            descriptor.AlphaMode !=
                ProGpuDirect2DNative.D2D1AlphaModePremultiplied ||
            descriptor.SharedNtHandle == 0)
        {
            throw new NotSupportedException(
                "The native Direct2D surface descriptor does not satisfy the typed BGRA premultiplied keyed-mutex contract.");
        }
        if (options.AdapterLuid is long requestedLuid &&
            descriptor.AdapterLuid != requestedLuid)
        {
            throw new NotSupportedException(
                "The native Direct2D surface was created on a different adapter LUID.");
        }
    }

    private static void ThrowIfFailed(
        string operation,
        ProGpuDirect2DStatus status,
        int nativeHResult)
    {
        if (status != ProGpuDirect2DStatus.Success)
        {
            throw new ProGpuDirect2DException(
                operation,
                status,
                nativeHResult);
        }
    }

    private sealed class NativeSurfaceOwner : IDisposable
    {
        private nint _surface;

        internal NativeSurfaceOwner(nint surface)
        {
            _surface = surface;
        }

        public void Dispose()
        {
            nint surface = Interlocked.Exchange(ref _surface, 0);
            if (surface != 0)
            {
                ProGpuDirect2DNative.SurfaceDestroy(surface);
            }
        }
    }

    private sealed class BorrowedTextureLease : IProGpuTextureLease
    {
        private ProGpuDirect2DSurface? _owner;

        internal BorrowedTextureLease(
            ProGpuDirect2DSurface owner,
            GpuTexture texture)
        {
            _owner = owner;
            Texture = texture;
        }

        public GpuTexture Texture { get; }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseLease();
        }
    }
}

public sealed class ProGpuDirect2DDrawingSession : IDisposable
{
    private ProGpuDirect2DSurface? _owner;

    internal ProGpuDirect2DDrawingSession(
        ProGpuDirect2DSurface owner,
        ProGpuDirect2DComReference deviceContext)
    {
        _owner = owner;
        DeviceContext = deviceContext;
    }

    public ProGpuDirect2DComReference DeviceContext { get; }

    public void Dispose()
    {
        ProGpuDirect2DSurface? owner =
            Interlocked.Exchange(ref _owner, null);
        if (owner is null)
        {
            return;
        }
        try
        {
            owner.CompleteDrawing();
        }
        finally
        {
            DeviceContext.Dispose();
        }
    }
}
