using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using Silk.NET.WebGPU;
using System.Numerics;
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
    private enum ProducerKind
    {
        None,
        Direct2D,
        MicrosoftWin2D
    }

    private const uint DefaultMutexTimeoutMilliseconds = 5_000U;
    private static readonly Guid D2D1Device1InterfaceId =
        new("D21768E1-23A4-4823-A14B-7C3EBA85D658");
    private static readonly Guid D2D1Bitmap1InterfaceId =
        new("A898A84C-3873-4588-B08B-EBBF978DF041");
    private static readonly Guid D2D1SolidColorBrushInterfaceId =
        new("2CD906A9-12E2-11DC-9FED-001143A055F9");
    private static readonly Guid D2D1LinearGradientBrushInterfaceId =
        new("2CD906AB-12E2-11DC-9FED-001143A055F9");
    private static readonly Guid D2D1RadialGradientBrushInterfaceId =
        new("2CD906AC-12E2-11DC-9FED-001143A055F9");

    private readonly object _gate = new();
    private readonly DawnGpuContext _dawn;
    private readonly DawnExplicitSharedTextureAccess _access;
    private nint _nativeSurface;
    private ProducerKind _producer;
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
    /// Tries to acquire a genuine Microsoft Win2D CanvasDevice wrapping this
    /// surface's exact ID2D1Device1 through ICanvasFactoryNative. The installed
    /// Win2D component must be registered in the process package graph, and the
    /// calling thread must already be initialized for Windows Runtime use.
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

    /// <summary>
    /// Uses the genuine Win2D CanvasDevice's official
    /// ICanvasResourceWrapperNative contract to return its exact
    /// ID2D1Device1 with one caller-owned COM reference.
    /// </summary>
    public bool TryAcquireMicrosoftWin2DNativeDevice(
        out ProGpuDirect2DComReference? nativeDevice,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DNativeResource(
            ProGpuDirect2DNative.Win2DResourceKind.CanvasDevice,
            D2D1Device1InterfaceId,
            ProGpuDirect2DInterfaceKind.D2D1Device1,
            "Microsoft Win2D CanvasDevice native-resource query",
            out nativeDevice,
            out nativeHResult);

    /// <summary>
    /// Uses the genuine Win2D CanvasRenderTarget's official
    /// ICanvasResourceWrapperNative contract with this surface's exact
    /// CanvasDevice and DPI to return its target ID2D1Bitmap1 with one
    /// caller-owned COM reference.
    /// </summary>
    public bool TryAcquireMicrosoftWin2DNativeBitmap(
        out ProGpuDirect2DComReference? nativeBitmap,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DNativeResource(
            ProGpuDirect2DNative.Win2DResourceKind.CanvasRenderTarget,
            D2D1Bitmap1InterfaceId,
            ProGpuDirect2DInterfaceKind.D2D1Bitmap1,
            "Microsoft Win2D CanvasRenderTarget native-resource query",
            out nativeBitmap,
            out nativeHResult);

    /// <summary>
    /// Creates a genuine device-context-domain ID2D1SolidColorBrush. The
    /// returned safe handle owns one COM reference and may be used with the
    /// ID2D1DeviceContext exposed by this surface.
    /// </summary>
    public ProGpuDirect2DComReference CreateSolidColorBrush(
        ProGpuDirect2DColor color)
    {
        ValidateColor(color);
        lock (_gate)
        {
            ThrowIfUnavailable();
            var nativeColor = new ProGpuDirect2DNative.NativeColorF
            {
                Red = color.Red,
                Green = color.Green,
                Blue = color.Blue,
                Alpha = color.Alpha
            };
            nint value = 0;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceCreateSolidColorBrush(
                    _nativeSurface,
                    &nativeColor,
                    &value,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1SolidColorBrush creation",
                status,
                nativeHResult);
            if (value == 0)
            {
                throw new InvalidOperationException(
                    "Direct2D brush creation succeeded without returning an interface.");
            }
            return new ProGpuDirect2DComReference(
                value,
                ProGpuDirect2DInterfaceKind.D2D1SolidColorBrush);
        }
    }

    /// <summary>
    /// Creates a genuine ID2D1GradientStopCollection1 without copying the
    /// caller's blittable stop span into an intermediate managed array.
    /// </summary>
    public ProGpuDirect2DComReference CreateGradientStopCollection(
        ReadOnlySpan<ProGpuDirect2DGradientStop> stops,
        ProGpuDirect2DColorSpace preInterpolationSpace =
            ProGpuDirect2DColorSpace.SRgb,
        ProGpuDirect2DColorSpace postInterpolationSpace =
            ProGpuDirect2DColorSpace.SRgb,
        ProGpuDirect2DBufferPrecision bufferPrecision =
            ProGpuDirect2DBufferPrecision.Precision8UIntNormalized,
        ProGpuDirect2DExtendMode extendMode =
            ProGpuDirect2DExtendMode.Clamp,
        ProGpuDirect2DColorInterpolationMode interpolationMode =
            ProGpuDirect2DColorInterpolationMode.Premultiplied)
    {
        ValidateGradientStops(stops);
        ValidateGradientOptions(
            preInterpolationSpace,
            postInterpolationSpace,
            bufferPrecision,
            extendMode,
            interpolationMode);
        lock (_gate)
        {
            ThrowIfUnavailable();
            fixed (ProGpuDirect2DGradientStop* stopPointer = stops)
            {
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceCreateGradientStopCollection(
                        _nativeSurface,
                        stopPointer,
                        checked((uint)stops.Length),
                        preInterpolationSpace,
                        postInterpolationSpace,
                        bufferPrecision,
                        extendMode,
                        interpolationMode,
                        &value,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1GradientStopCollection1 creation",
                    status,
                    nativeHResult);
                return CreateRequiredComReference(
                    value,
                    ProGpuDirect2DInterfaceKind.D2D1GradientStopCollection1,
                    "Direct2D gradient-stop collection creation");
            }
        }
    }

    public ProGpuDirect2DComReference CreateLinearGradientBrush(
        ProGpuDirect2DComReference gradientStopCollection,
        Vector2 startPoint,
        Vector2 endPoint,
        float opacity = 1.0F,
        Matrix3x2? transform = null)
    {
        ValidateGradientStopCollection(gradientStopCollection);
        ValidatePoint(startPoint, nameof(startPoint));
        ValidatePoint(endPoint, nameof(endPoint));
        ProGpuDirect2DNative.NativeBrushProperties nativeBrushProperties =
            CreateNativeBrushProperties(opacity, transform);
        var nativeProperties =
            new ProGpuDirect2DNative.NativeLinearGradientBrushProperties
            {
                StartPoint = CreateNativePoint(startPoint),
                EndPoint = CreateNativePoint(endPoint)
            };
        return CreateGradientBrush(
            gradientStopCollection,
            &nativeProperties,
            &nativeBrushProperties,
            radial: false);
    }

    public ProGpuDirect2DComReference CreateRadialGradientBrush(
        ProGpuDirect2DComReference gradientStopCollection,
        Vector2 center,
        Vector2 gradientOriginOffset,
        float radiusX,
        float radiusY,
        float opacity = 1.0F,
        Matrix3x2? transform = null)
    {
        ValidateGradientStopCollection(gradientStopCollection);
        ValidatePoint(center, nameof(center));
        ValidatePoint(gradientOriginOffset, nameof(gradientOriginOffset));
        if (!float.IsFinite(radiusX) || !float.IsFinite(radiusY))
        {
            throw new ArgumentOutOfRangeException(
                nameof(radiusX),
                "Direct2D gradient radii must be finite.");
        }
        ProGpuDirect2DNative.NativeBrushProperties nativeBrushProperties =
            CreateNativeBrushProperties(opacity, transform);
        var nativeProperties =
            new ProGpuDirect2DNative.NativeRadialGradientBrushProperties
            {
                Center = CreateNativePoint(center),
                GradientOriginOffset = CreateNativePoint(
                    gradientOriginOffset),
                RadiusX = radiusX,
                RadiusY = radiusY
            };
        return CreateGradientBrush(
            gradientStopCollection,
            &nativeProperties,
            &nativeBrushProperties,
            radial: true);
    }

    /// <summary>
    /// Wraps a provider-created ID2D1SolidColorBrush as a genuine Microsoft
    /// Win2D CanvasSolidColorBrush through ICanvasFactoryNative. The returned
    /// safe handle owns one COM reference.
    /// </summary>
    public bool TryAcquireMicrosoftWin2DSolidColorBrush(
        ProGpuDirect2DComReference nativeBrush,
        out ProGpuDirect2DComReference? canvasBrush,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapper(
            nativeBrush,
            ProGpuDirect2DInterfaceKind.D2D1SolidColorBrush,
            ProGpuDirect2DInterfaceKind.Win2DCanvasSolidColorBrush,
            "Microsoft Win2D CanvasSolidColorBrush wrapping",
            out canvasBrush,
            out nativeHResult);

    /// <summary>
    /// Reverse-unwraps a genuine Microsoft Win2D CanvasSolidColorBrush through
    /// ICanvasResourceWrapperNative and returns its exact
    /// ID2D1SolidColorBrush with one caller-owned COM reference.
    /// </summary>
    public bool TryAcquireMicrosoftWin2DNativeSolidColorBrush(
        ProGpuDirect2DComReference canvasBrush,
        out ProGpuDirect2DComReference? nativeBrush,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapperNativeResource(
            canvasBrush,
            ProGpuDirect2DInterfaceKind.Win2DCanvasSolidColorBrush,
            D2D1SolidColorBrushInterfaceId,
            ProGpuDirect2DInterfaceKind.D2D1SolidColorBrush,
            "Microsoft Win2D CanvasSolidColorBrush native-resource query",
            out nativeBrush,
            out nativeHResult);

    public bool TryAcquireMicrosoftWin2DLinearGradientBrush(
        ProGpuDirect2DComReference nativeBrush,
        out ProGpuDirect2DComReference? canvasBrush,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapper(
            nativeBrush,
            ProGpuDirect2DInterfaceKind.D2D1LinearGradientBrush,
            ProGpuDirect2DInterfaceKind.Win2DCanvasLinearGradientBrush,
            "Microsoft Win2D CanvasLinearGradientBrush wrapping",
            out canvasBrush,
            out nativeHResult);

    public bool TryAcquireMicrosoftWin2DNativeLinearGradientBrush(
        ProGpuDirect2DComReference canvasBrush,
        out ProGpuDirect2DComReference? nativeBrush,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapperNativeResource(
            canvasBrush,
            ProGpuDirect2DInterfaceKind.Win2DCanvasLinearGradientBrush,
            D2D1LinearGradientBrushInterfaceId,
            ProGpuDirect2DInterfaceKind.D2D1LinearGradientBrush,
            "Microsoft Win2D CanvasLinearGradientBrush native-resource query",
            out nativeBrush,
            out nativeHResult);

    public bool TryAcquireMicrosoftWin2DRadialGradientBrush(
        ProGpuDirect2DComReference nativeBrush,
        out ProGpuDirect2DComReference? canvasBrush,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapper(
            nativeBrush,
            ProGpuDirect2DInterfaceKind.D2D1RadialGradientBrush,
            ProGpuDirect2DInterfaceKind.Win2DCanvasRadialGradientBrush,
            "Microsoft Win2D CanvasRadialGradientBrush wrapping",
            out canvasBrush,
            out nativeHResult);

    public bool TryAcquireMicrosoftWin2DNativeRadialGradientBrush(
        ProGpuDirect2DComReference canvasBrush,
        out ProGpuDirect2DComReference? nativeBrush,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapperNativeResource(
            canvasBrush,
            ProGpuDirect2DInterfaceKind.Win2DCanvasRadialGradientBrush,
            D2D1RadialGradientBrushInterfaceId,
            ProGpuDirect2DInterfaceKind.D2D1RadialGradientBrush,
            "Microsoft Win2D CanvasRadialGradientBrush native-resource query",
            out nativeBrush,
            out nativeHResult);

    private ProGpuDirect2DComReference CreateGradientBrush(
        ProGpuDirect2DComReference gradientStopCollection,
        void* properties,
        ProGpuDirect2DNative.NativeBrushProperties* brushProperties,
        bool radial)
    {
        bool referenceAdded = false;
        try
        {
            gradientStopCollection.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status = radial
                    ? ProGpuDirect2DNative.SurfaceCreateRadialGradientBrush(
                        _nativeSurface,
                        (ProGpuDirect2DNative
                            .NativeRadialGradientBrushProperties*)properties,
                        brushProperties,
                        gradientStopCollection.DangerousGetHandle(),
                        &value,
                        &nativeHResult)
                    : ProGpuDirect2DNative.SurfaceCreateLinearGradientBrush(
                        _nativeSurface,
                        (ProGpuDirect2DNative
                            .NativeLinearGradientBrushProperties*)properties,
                        brushProperties,
                        gradientStopCollection.DangerousGetHandle(),
                        &value,
                        &nativeHResult);
                string operation = radial
                    ? "ID2D1RadialGradientBrush creation"
                    : "ID2D1LinearGradientBrush creation";
                ThrowIfFailed(operation, status, nativeHResult);
                return CreateRequiredComReference(
                    value,
                    radial
                        ? ProGpuDirect2DInterfaceKind.D2D1RadialGradientBrush
                        : ProGpuDirect2DInterfaceKind.D2D1LinearGradientBrush,
                    operation);
            }
        }
        finally
        {
            if (referenceAdded)
            {
                gradientStopCollection.DangerousRelease();
            }
        }
    }

    private bool TryAcquireMicrosoftWin2DWrapper(
        ProGpuDirect2DComReference nativeResource,
        ProGpuDirect2DInterfaceKind expectedNativeKind,
        ProGpuDirect2DInterfaceKind wrapperKind,
        string operation,
        out ProGpuDirect2DComReference? wrapper,
        out int nativeHResult)
    {
        ArgumentNullException.ThrowIfNull(nativeResource);
        if (nativeResource.InterfaceKind != expectedNativeKind)
        {
            throw new ArgumentException(
                $"The COM reference must own {expectedNativeKind}.",
                nameof(nativeResource));
        }

        bool referenceAdded = false;
        try
        {
            nativeResource.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                nint value = 0;
                int resultHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceTryGetOrCreateWin2DWrapper(
                        _nativeSurface,
                        nativeResource.DangerousGetHandle(),
                        0.0F,
                        &value,
                        &resultHResult);
                nativeHResult = resultHResult;
                if (status == ProGpuDirect2DStatus.Win2DRuntimeUnavailable)
                {
                    wrapper = null;
                    return false;
                }
                ThrowIfFailed(operation, status, nativeHResult);
                wrapper = CreateRequiredComReference(
                    value,
                    wrapperKind,
                    operation);
                return true;
            }
        }
        finally
        {
            if (referenceAdded)
            {
                nativeResource.DangerousRelease();
            }
        }
    }

    private bool TryAcquireMicrosoftWin2DWrapperNativeResource(
        ProGpuDirect2DComReference wrapper,
        ProGpuDirect2DInterfaceKind expectedWrapperKind,
        Guid interfaceId,
        ProGpuDirect2DInterfaceKind nativeKind,
        string operation,
        out ProGpuDirect2DComReference? nativeResource,
        out int nativeHResult)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        if (wrapper.InterfaceKind != expectedWrapperKind)
        {
            throw new ArgumentException(
                $"The COM reference must own {expectedWrapperKind}.",
                nameof(wrapper));
        }

        bool referenceAdded = false;
        try
        {
            wrapper.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                ProGpuDirect2DNative.NativeGuid nativeInterfaceId =
                    ProGpuDirect2DNative.NativeGuid.FromGuid(interfaceId);
                nint value = 0;
                int resultHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative
                        .SurfaceTryGetWin2DWrapperNativeResource(
                            _nativeSurface,
                            wrapper.DangerousGetHandle(),
                            0.0F,
                            &nativeInterfaceId,
                            &value,
                            &resultHResult);
                nativeHResult = resultHResult;
                if (status == ProGpuDirect2DStatus.Win2DRuntimeUnavailable)
                {
                    nativeResource = null;
                    return false;
                }
                ThrowIfFailed(operation, status, nativeHResult);
                nativeResource = CreateRequiredComReference(
                    value,
                    nativeKind,
                    operation);
                return true;
            }
        }
        finally
        {
            if (referenceAdded)
            {
                wrapper.DangerousRelease();
            }
        }
    }

    private static ProGpuDirect2DComReference CreateRequiredComReference(
        nint value,
        ProGpuDirect2DInterfaceKind kind,
        string operation)
    {
        if (value == 0)
        {
            throw new InvalidOperationException(
                $"{operation} succeeded without returning a COM interface.");
        }
        return new ProGpuDirect2DComReference(value, kind);
    }

    private bool TryAcquireMicrosoftWin2DNativeResource(
        ProGpuDirect2DNative.Win2DResourceKind resourceKind,
        Guid interfaceId,
        ProGpuDirect2DInterfaceKind interfaceKind,
        string operation,
        out ProGpuDirect2DComReference? nativeResource,
        out int nativeHResult)
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            ProGpuDirect2DNative.NativeGuid nativeInterfaceId =
                ProGpuDirect2DNative.NativeGuid.FromGuid(interfaceId);
            nint value = 0;
            int resultHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceTryGetWin2DNativeResource(
                    _nativeSurface,
                    resourceKind,
                    &nativeInterfaceId,
                    &value,
                    &resultHResult);
            nativeHResult = resultHResult;
            if (status == ProGpuDirect2DStatus.Win2DRuntimeUnavailable)
            {
                nativeResource = null;
                return false;
            }
            ThrowIfFailed(operation, status, nativeHResult);
            if (value == 0)
            {
                throw new InvalidOperationException(
                    $"{operation} succeeded without returning a COM interface.");
            }
            nativeResource = new ProGpuDirect2DComReference(
                value,
                interfaceKind);
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
            if (_producer != ProducerKind.None)
            {
                throw new InvalidOperationException(
                    "A Direct2D or Win2D producer session is already active.");
            }
            if (_leaseCount != 0)
            {
                throw new InvalidOperationException(
                    "Direct2D cannot acquire the allocation while deferred ProGPU texture leases are active.");
            }

            context = AcquireInterface(
                ProGpuDirect2DInterfaceKind.D2D1DeviceContext1);
            _producer = ProducerKind.Direct2D;
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
                _producer = ProducerKind.None;
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

    /// <summary>
    /// Tries to acquire exclusive producer ownership and a genuine Microsoft
    /// Win2D CanvasRenderTarget wrapping this surface's exact ID2D1Bitmap1.
    /// The caller must create, use, and dispose its Win2D CanvasDrawingSession
    /// before disposing the returned outer producer scope.
    /// </summary>
    public bool TryBeginMicrosoftWin2DProducerAccess(
        out ProGpuMicrosoftWin2DProducerAccess? producerAccess,
        out int nativeHResult,
        uint timeoutMilliseconds = DefaultMutexTimeoutMilliseconds)
    {
        ProGpuDirect2DComReference renderTarget;
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (_producer != ProducerKind.None)
            {
                throw new InvalidOperationException(
                    "A Direct2D or Win2D producer session is already active.");
            }
            if (_leaseCount != 0)
            {
                throw new InvalidOperationException(
                    "Win2D cannot acquire the allocation while deferred ProGPU texture leases are active.");
            }

            nint value = 0;
            int resultHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceTryGetWin2DCanvasRenderTarget(
                    _nativeSurface,
                    &value,
                    &resultHResult);
            nativeHResult = resultHResult;
            if (status == ProGpuDirect2DStatus.Win2DRuntimeUnavailable)
            {
                producerAccess = null;
                return false;
            }
            ThrowIfFailed(
                "Microsoft Win2D CanvasRenderTarget wrapping",
                status,
                nativeHResult);
            if (value == 0)
            {
                throw new InvalidOperationException(
                    "Win2D wrapping succeeded without returning a CanvasRenderTarget.");
            }
            renderTarget = new ProGpuDirect2DComReference(
                value,
                ProGpuDirect2DInterfaceKind.Win2DCanvasRenderTarget);
            _producer = ProducerKind.MicrosoftWin2D;
        }

        DawnExplicitSharedTextureAccess? accessToDispose = null;
        bool dawnAccessEnded = true;
        try
        {
            _access.EndAccess();
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceAcquire(
                    _nativeSurface,
                    Descriptor.InitialAcquireKey,
                    timeoutMilliseconds);
            if (status != ProGpuDirect2DStatus.Success)
            {
                _access.BeginAccess(_contentVersion != 0U);
                dawnAccessEnded = false;
                ThrowIfFailed(
                    "Microsoft Win2D producer acquisition",
                    status,
                    ProGpuDirect2DNative.SurfaceGetLastHResult(
                        _nativeSurface));
            }
            producerAccess = new ProGpuMicrosoftWin2DProducerAccess(
                this,
                renderTarget);
            return true;
        }
        catch
        {
            lock (_gate)
            {
                _producer = ProducerKind.None;
                if (dawnAccessEnded)
                {
                    _disposeRequested = true;
                }
                accessToDispose = TryTakeResourcesForDisposal();
            }
            renderTarget.Dispose();
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
            if (_disposeRequested || _resourcesDisposed ||
                _producer != ProducerKind.None ||
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
            if (_disposeRequested || _resourcesDisposed ||
                _producer != ProducerKind.None ||
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

    internal void CompleteDirect2DDrawing() =>
        CompleteProducerAccess(ProducerKind.Direct2D);

    internal void CompleteMicrosoftWin2DProducerAccess() =>
        CompleteProducerAccess(ProducerKind.MicrosoftWin2D);

    private void CompleteProducerAccess(ProducerKind expectedProducer)
    {
        EventHandler? changed = null;
        DawnExplicitSharedTextureAccess? accessToDispose = null;
        Exception? failure = null;
        nint nativeSurface;
        lock (_gate)
        {
            if (_producer != expectedProducer || _resourcesDisposed)
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
            if (expectedProducer == ProducerKind.Direct2D)
            {
                status = ProGpuDirect2DNative.SurfaceEndDraw(
                    nativeSurface,
                    Descriptor.InitialReleaseKey,
                    &tag1,
                    &tag2,
                    &nativeHResult);
            }
            else
            {
                status = ProGpuDirect2DNative.SurfaceRelease(
                    nativeSurface,
                    Descriptor.InitialReleaseKey);
                nativeHResult =
                    ProGpuDirect2DNative.SurfaceGetLastHResult(nativeSurface);
            }
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
            string operation = expectedProducer == ProducerKind.Direct2D
                ? $"EndDraw (tags {tag1}/{tag2})"
                : "Microsoft Win2D producer release";
            failure = new ProGpuDirect2DException(
                operation,
                status,
                nativeHResult);
        }

        lock (_gate)
        {
            _producer = ProducerKind.None;
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
        if (!_disposeRequested || _resourcesDisposed ||
            _producer != ProducerKind.None ||
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

    private static void ValidateColor(ProGpuDirect2DColor color)
    {
        if (!float.IsFinite(color.Red) ||
            !float.IsFinite(color.Green) ||
            !float.IsFinite(color.Blue) ||
            !float.IsFinite(color.Alpha))
        {
            throw new ArgumentOutOfRangeException(
                nameof(color),
                "Direct2D color channels must be finite.");
        }
    }

    private static void ValidateGradientStops(
        ReadOnlySpan<ProGpuDirect2DGradientStop> stops)
    {
        if (stops.IsEmpty)
        {
            throw new ArgumentException(
                "A Direct2D gradient requires at least one stop.",
                nameof(stops));
        }
        foreach (ref readonly ProGpuDirect2DGradientStop stop in stops)
        {
            if (!float.IsFinite(stop.Position))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stops),
                    "Direct2D gradient-stop positions must be finite.");
            }
            ValidateColor(stop.Color);
        }
    }

    private static void ValidateGradientOptions(
        ProGpuDirect2DColorSpace preInterpolationSpace,
        ProGpuDirect2DColorSpace postInterpolationSpace,
        ProGpuDirect2DBufferPrecision bufferPrecision,
        ProGpuDirect2DExtendMode extendMode,
        ProGpuDirect2DColorInterpolationMode interpolationMode)
    {
        if (preInterpolationSpace < ProGpuDirect2DColorSpace.Custom ||
            preInterpolationSpace > ProGpuDirect2DColorSpace.ScRgb ||
            postInterpolationSpace < ProGpuDirect2DColorSpace.Custom ||
            postInterpolationSpace > ProGpuDirect2DColorSpace.ScRgb ||
            bufferPrecision < ProGpuDirect2DBufferPrecision.Unknown ||
            bufferPrecision > ProGpuDirect2DBufferPrecision.Precision32Float ||
            extendMode < ProGpuDirect2DExtendMode.Clamp ||
            extendMode > ProGpuDirect2DExtendMode.Mirror ||
            interpolationMode <
                ProGpuDirect2DColorInterpolationMode.Straight ||
            interpolationMode >
                ProGpuDirect2DColorInterpolationMode.Premultiplied)
        {
            throw new ArgumentOutOfRangeException(
                nameof(preInterpolationSpace),
                "Direct2D gradient options contain an unknown enum value.");
        }
    }

    private static void ValidateGradientStopCollection(
        ProGpuDirect2DComReference gradientStopCollection)
    {
        ArgumentNullException.ThrowIfNull(gradientStopCollection);
        if (gradientStopCollection.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.D2D1GradientStopCollection1)
        {
            throw new ArgumentException(
                "The COM reference must own an ID2D1GradientStopCollection1.",
                nameof(gradientStopCollection));
        }
    }

    private static void ValidatePoint(Vector2 point, string parameterName)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Direct2D points must be finite.");
        }
    }

    private static ProGpuDirect2DNative.NativePoint2F CreateNativePoint(
        Vector2 point) =>
        new() { X = point.X, Y = point.Y };

    private static ProGpuDirect2DNative.NativeBrushProperties
        CreateNativeBrushProperties(
            float opacity,
            Matrix3x2? transform)
    {
        Matrix3x2 matrix = transform ?? Matrix3x2.Identity;
        if (!float.IsFinite(opacity) ||
            !float.IsFinite(matrix.M11) ||
            !float.IsFinite(matrix.M12) ||
            !float.IsFinite(matrix.M21) ||
            !float.IsFinite(matrix.M22) ||
            !float.IsFinite(matrix.M31) ||
            !float.IsFinite(matrix.M32))
        {
            throw new ArgumentOutOfRangeException(
                nameof(opacity),
                "Direct2D brush opacity and transform values must be finite.");
        }
        return new ProGpuDirect2DNative.NativeBrushProperties
        {
            Opacity = opacity,
            Transform = new ProGpuDirect2DNative.NativeMatrix3X2F
            {
                M11 = matrix.M11,
                M12 = matrix.M12,
                M21 = matrix.M21,
                M22 = matrix.M22,
                M31 = matrix.M31,
                M32 = matrix.M32
            }
        };
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
            owner.CompleteDirect2DDrawing();
        }
        finally
        {
            DeviceContext.Dispose();
        }
    }
}

/// <summary>
/// Owns exclusive cross-API producer access while Microsoft Win2D draws into
/// the returned genuine CanvasRenderTarget COM object.
/// </summary>
public sealed class ProGpuMicrosoftWin2DProducerAccess : IDisposable
{
    private ProGpuDirect2DSurface? _owner;

    internal ProGpuMicrosoftWin2DProducerAccess(
        ProGpuDirect2DSurface owner,
        ProGpuDirect2DComReference canvasRenderTarget)
    {
        _owner = owner;
        CanvasRenderTarget = canvasRenderTarget;
    }

    public ProGpuDirect2DComReference CanvasRenderTarget { get; }

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
            owner.CompleteMicrosoftWin2DProducerAccess();
        }
        finally
        {
            CanvasRenderTarget.Dispose();
        }
    }
}
