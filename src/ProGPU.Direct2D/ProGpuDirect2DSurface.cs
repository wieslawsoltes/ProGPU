using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Wpf.Interop;
using Silk.NET.WebGPU;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

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
        MicrosoftWin2D,
        CommandList
    }

    private enum GeometryDerivation
    {
        Simplify,
        Outline,
        Widen
    }

    private enum VectorPrimitive
    {
        Line,
        Rectangle,
        RoundedRectangle,
        Ellipse
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
    private static readonly Guid D2D1GeometryInterfaceId =
        new("2CD906A1-12E2-11DC-9FED-001143A055F9");
    private static readonly Guid D2D1StrokeStyle1InterfaceId =
        new("10A72A66-E91C-43F4-993F-DDF4B82B0B4A");
    private static readonly Guid D2D1BitmapBrush1InterfaceId =
        new("41343A53-E41A-49A2-91CD-21793BBB62E5");
    private static readonly Guid D2D1ImageBrushInterfaceId =
        new("FE9E984D-3F95-407C-B5DB-CB94D4E8F87C");
    private static readonly Guid D2D1CommandListInterfaceId =
        new("B4F34A19-2383-4D76-94F6-EC343657C3DC");
    private static readonly Guid D2D1EffectInterfaceId =
        new("28211A43-7D89-476F-8181-2D6159B220AD");
    private static readonly Guid D2D1ImageInterfaceId =
        new("65019F75-8DA2-497C-B32C-DFA34E48EDE6");
    private static readonly Guid DWriteTextFormat1InterfaceId =
        new("5F174B49-0D8B-4CFB-8BCA-F1CCE9D06C67");
    private static readonly Guid DWriteTextLayout4InterfaceId =
        new("05A9BF42-223F-4441-B5FB-8263685F55E9");
    private static readonly Guid DWriteTypographyInterfaceId =
        new("55F1112B-1DC2-4B3C-9541-F46894ED85B6");
    private static readonly Guid DWriteFontFaceReferenceInterfaceId =
        new("5E7FA7CA-DDE3-424C-89F0-9FCD6FED58CD");
    private static readonly Guid D2D1SvgDocumentInterfaceId =
        new("86B88E4D-AFA4-4D7B-88E4-68A51C4A0AEC");

    private readonly object _gate = new();
    private readonly DawnGpuContext _dawn;
    private readonly DawnExplicitSharedTextureAccess _access;
    private readonly ProGpuDirect2DResourceDomain _resourceDomain;
    private nint _nativeSurface;
    private ProducerKind _producer;
    private bool _disposeRequested;
    private bool _resourcesDisposed;
    private bool _deviceLost;
    private int _deviceLossHResult;
    private int _deviceLostNotificationQueued;
    private int _leaseCount;
    private uint _typedDrawScopeDepth;
    private ulong _contentVersion;

    private ProGpuDirect2DSurface(
        DawnGpuContext dawn,
        DawnExplicitSharedTextureAccess access,
        nint nativeSurface,
        in ProGpuDirect2DSurfaceDescriptor descriptor,
        in ProGpuDirect2DDeviceLossState deviceLossState)
    {
        _dawn = dawn;
        _access = access;
        _nativeSurface = nativeSurface;
        Descriptor = descriptor;
        _contentVersion = descriptor.ContentVersion;
        DeviceLossCapabilities = deviceLossState.Flags &
            ProGpuDirect2DDeviceLossFlags.RemovalEventRegistered;
        ResourceGeneration = deviceLossState.ResourceGeneration;
        _resourceDomain = new ProGpuDirect2DResourceDomain(
            ResourceGeneration);
    }

    public event EventHandler? TextureChanged;

    /// <summary>
    /// Raised once after this Direct2D/D3D11 device domain becomes terminal.
    /// Create a new Dawn context and Direct2D surface, then rebuild every
    /// resource associated with <see cref="ResourceGeneration"/>.
    /// </summary>
    public event EventHandler<ProGpuDirect2DDeviceLostEventArgs>? DeviceLost;

    public ProGpuDirect2DSurfaceDescriptor Descriptor { get; private set; }

    public ProGpuDirect2DDeviceLossFlags DeviceLossCapabilities { get; }

    public ulong ResourceGeneration { get; }

    public bool IsDeviceLost
    {
        get
        {
            lock (_gate)
            {
                return _deviceLost;
            }
        }
    }

    public int DeviceLossHResult
    {
        get
        {
            lock (_gate)
            {
                return _deviceLossHResult;
            }
        }
    }

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
        ThrowIfFailedDuringCreate(
            "surface creation",
            status,
            nativeHResult);

        var owner = new NativeSurfaceOwner(nativeSurface);
        try
        {
            ProGpuDirect2DSurfaceDescriptor descriptor =
                ReadDescriptor(nativeSurface);
            ProGpuDirect2DDeviceLossState deviceLossState =
                ReadDeviceLossState(nativeSurface);
            if (deviceLossState.ResourceGeneration == 0U ||
                deviceLossState.IsDeviceLost)
            {
                throw new NotSupportedException(
                    "The native Direct2D device domain was lost or did not publish a resource generation during creation.");
            }
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
                in descriptor,
                in deviceLossState);
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
            return new ProGpuDirect2DComReference(
                value,
                kind,
                _resourceDomain);
        }
    }

    public void SetBrushProperties(
        ProGpuDirect2DComReference brush,
        ProGpuDirect2DBrushProperties properties)
    {
        ValidateBrush(brush, nameof(brush));
        if (!float.IsFinite(properties.Opacity) || properties.Opacity is < 0.0F or > 1.0F)
            throw new ArgumentOutOfRangeException(nameof(properties));
        ProGpuDirect2DNative.NativeBrushProperties native = new()
        {
            Opacity = properties.Opacity,
            Transform = CreateNativeMatrix(properties.Transform)
        };
        bool added = false;
        try
        {
            brush.DangerousAddRef(ref added);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int hr = 0;
                ProGpuDirect2DStatus status = ProGpuDirect2DNative.BrushSetProperties(
                    _nativeSurface, brush.DangerousGetHandle(), &native, &hr);
                ThrowIfFailed("ID2D1Brush property update", status, hr);
            }
        }
        finally { if (added) brush.DangerousRelease(); }
    }

    public ProGpuDirect2DBrushProperties GetBrushProperties(
        ProGpuDirect2DComReference brush)
    {
        ValidateBrush(brush, nameof(brush));
        bool added = false;
        try
        {
            brush.DangerousAddRef(ref added);
            lock (_gate)
            {
                ThrowIfUnavailable();
                ProGpuDirect2DNative.NativeBrushProperties native = default;
                int hr = 0;
                ProGpuDirect2DStatus status = ProGpuDirect2DNative.BrushGetProperties(
                    _nativeSurface, brush.DangerousGetHandle(), &native, &hr);
                ThrowIfFailed("ID2D1Brush property query", status, hr);
                return new(native.Opacity, new Matrix3x2(
                    native.Transform.M11, native.Transform.M12,
                    native.Transform.M21, native.Transform.M22,
                    native.Transform.M31, native.Transform.M32));
            }
        }
        finally { if (added) brush.DangerousRelease(); }
    }

    public void SetSolidColorBrushColor(
        ProGpuDirect2DComReference brush,
        ProGpuDirect2DColor color)
    {
        ValidateBrushKind(brush, ProGpuDirect2DInterfaceKind.D2D1SolidColorBrush, nameof(brush));
        ValidateColor(color);
        ProGpuDirect2DNative.NativeColorF native = new()
        {
            Red = color.Red, Green = color.Green, Blue = color.Blue, Alpha = color.Alpha
        };
        bool added = false;
        try
        {
            brush.DangerousAddRef(ref added);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int hr = 0;
                ProGpuDirect2DStatus status = ProGpuDirect2DNative.SolidColorBrushSetColor(
                    _nativeSurface, brush.DangerousGetHandle(), &native, &hr);
                ThrowIfFailed("ID2D1SolidColorBrush color update", status, hr);
            }
        }
        finally { if (added) brush.DangerousRelease(); }
    }

    public ProGpuDirect2DColor GetSolidColorBrushColor(ProGpuDirect2DComReference brush)
    {
        ValidateBrushKind(brush, ProGpuDirect2DInterfaceKind.D2D1SolidColorBrush, nameof(brush));
        bool added = false;
        try
        {
            brush.DangerousAddRef(ref added);
            lock (_gate)
            {
                ThrowIfUnavailable();
                ProGpuDirect2DNative.NativeColorF native = default;
                int hr = 0;
                ProGpuDirect2DStatus status = ProGpuDirect2DNative.SolidColorBrushGetColor(
                    _nativeSurface, brush.DangerousGetHandle(), &native, &hr);
                ThrowIfFailed("ID2D1SolidColorBrush color query", status, hr);
                return new(native.Red, native.Green, native.Blue, native.Alpha);
            }
        }
        finally { if (added) brush.DangerousRelease(); }
    }

    public void SetLinearGradientBrushProperties(
        ProGpuDirect2DComReference brush,
        ProGpuDirect2DLinearGradientProperties properties)
    {
        ValidateBrushKind(brush, ProGpuDirect2DInterfaceKind.D2D1LinearGradientBrush, nameof(brush));
        ValidatePoint(properties.StartPoint, nameof(properties));
        ValidatePoint(properties.EndPoint, nameof(properties));
        ProGpuDirect2DNative.NativeLinearGradientBrushProperties native = new()
        {
            StartPoint = new() { X = properties.StartPoint.X, Y = properties.StartPoint.Y },
            EndPoint = new() { X = properties.EndPoint.X, Y = properties.EndPoint.Y }
        };
        bool added = false;
        try
        {
            brush.DangerousAddRef(ref added);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int hr = 0;
                ProGpuDirect2DStatus status = ProGpuDirect2DNative.LinearGradientBrushSetProperties(
                    _nativeSurface, brush.DangerousGetHandle(), &native, &hr);
                ThrowIfFailed("ID2D1LinearGradientBrush property update", status, hr);
            }
        }
        finally { if (added) brush.DangerousRelease(); }
    }

    public ProGpuDirect2DLinearGradientProperties GetLinearGradientBrushProperties(
        ProGpuDirect2DComReference brush)
    {
        ValidateBrushKind(brush, ProGpuDirect2DInterfaceKind.D2D1LinearGradientBrush, nameof(brush));
        bool added = false;
        try
        {
            brush.DangerousAddRef(ref added);
            lock (_gate)
            {
                ThrowIfUnavailable();
                ProGpuDirect2DNative.NativeLinearGradientBrushProperties native = default;
                int hr = 0;
                ProGpuDirect2DStatus status = ProGpuDirect2DNative.LinearGradientBrushGetProperties(
                    _nativeSurface, brush.DangerousGetHandle(), &native, &hr);
                ThrowIfFailed("ID2D1LinearGradientBrush property query", status, hr);
                return new(new(native.StartPoint.X, native.StartPoint.Y), new(native.EndPoint.X, native.EndPoint.Y));
            }
        }
        finally { if (added) brush.DangerousRelease(); }
    }

    public void SetRadialGradientBrushProperties(
        ProGpuDirect2DComReference brush,
        ProGpuDirect2DRadialGradientProperties properties)
    {
        ValidateBrushKind(brush, ProGpuDirect2DInterfaceKind.D2D1RadialGradientBrush, nameof(brush));
        ValidatePoint(properties.Center, nameof(properties));
        ValidatePoint(properties.GradientOriginOffset, nameof(properties));
        if (!float.IsFinite(properties.RadiusX) || properties.RadiusX < 0.0F ||
            !float.IsFinite(properties.RadiusY) || properties.RadiusY < 0.0F)
            throw new ArgumentOutOfRangeException(nameof(properties));
        ProGpuDirect2DNative.NativeRadialGradientBrushProperties native = new()
        {
            Center = new() { X = properties.Center.X, Y = properties.Center.Y },
            GradientOriginOffset = new() { X = properties.GradientOriginOffset.X, Y = properties.GradientOriginOffset.Y },
            RadiusX = properties.RadiusX,
            RadiusY = properties.RadiusY
        };
        bool added = false;
        try
        {
            brush.DangerousAddRef(ref added);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int hr = 0;
                ProGpuDirect2DStatus status = ProGpuDirect2DNative.RadialGradientBrushSetProperties(
                    _nativeSurface, brush.DangerousGetHandle(), &native, &hr);
                ThrowIfFailed("ID2D1RadialGradientBrush property update", status, hr);
            }
        }
        finally { if (added) brush.DangerousRelease(); }
    }

    public ProGpuDirect2DRadialGradientProperties GetRadialGradientBrushProperties(
        ProGpuDirect2DComReference brush)
    {
        ValidateBrushKind(brush, ProGpuDirect2DInterfaceKind.D2D1RadialGradientBrush, nameof(brush));
        bool added = false;
        try
        {
            brush.DangerousAddRef(ref added);
            lock (_gate)
            {
                ThrowIfUnavailable();
                ProGpuDirect2DNative.NativeRadialGradientBrushProperties native = default;
                int hr = 0;
                ProGpuDirect2DStatus status = ProGpuDirect2DNative.RadialGradientBrushGetProperties(
                    _nativeSurface, brush.DangerousGetHandle(), &native, &hr);
                ThrowIfFailed("ID2D1RadialGradientBrush property query", status, hr);
                return new(
                    new(native.Center.X, native.Center.Y),
                    new(native.GradientOriginOffset.X, native.GradientOriginOffset.Y),
                    native.RadiusX,
                    native.RadiusY);
            }
        }
        finally { if (added) brush.DangerousRelease(); }
    }

    /// <summary>
    /// Polls the native removal event and persistent Direct2D device-domain
    /// state. Device loss is terminal for this surface and its resource
    /// generation.
    /// </summary>
    public ProGpuDirect2DDeviceLossState PollDeviceLoss()
    {
        DawnExplicitSharedTextureAccess? accessToDispose = null;
        ProGpuDirect2DDeviceLossState state;
        lock (_gate)
        {
            if (_resourcesDisposed)
            {
                return new ProGpuDirect2DDeviceLossState(
                    _deviceLost
                        ? ProGpuDirect2DDeviceLossFlags.DeviceLost
                        : ProGpuDirect2DDeviceLossFlags.None,
                    _deviceLossHResult,
                    ResourceGeneration);
            }
            state = ReadDeviceLossState(_nativeSurface);
            ObserveDeviceLoss(state);
            accessToDispose = TryTakeResourcesForDisposal();
        }
        accessToDispose?.Dispose();
        return state;
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
                ProGpuDirect2DInterfaceKind.Win2DCanvasDevice,
                _resourceDomain);
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
                ProGpuDirect2DInterfaceKind.D2D1SolidColorBrush,
                _resourceDomain);
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
    /// Uploads one immutable premultiplied BGRA8 image as a genuine
    /// same-device ID2D1Bitmap1. The pinned source span is consumed
    /// synchronously and is not retained by Direct2D.
    /// </summary>
    public ProGpuDirect2DComReference CreateBitmap(
        ReadOnlySpan<byte> bgra8PremultipliedPixels,
        uint width,
        uint height,
        uint stride = 0U,
        float dpiX = 96.0F,
        float dpiY = 96.0F)
    {
        ulong rowByteCount = checked((ulong)width * 4U);
        if (stride == 0U && rowByteCount <= uint.MaxValue)
        {
            stride = (uint)rowByteCount;
        }
        ulong requiredByteCount = height == 0U
            ? 0U
            : checked((ulong)stride * (height - 1U) + rowByteCount);
        if (width == 0U || height == 0U ||
            rowByteCount > uint.MaxValue || stride < rowByteCount ||
            (ulong)bgra8PremultipliedPixels.Length < requiredByteCount ||
            !float.IsFinite(dpiX) || !float.IsFinite(dpiY) ||
            dpiX <= 0.0F || dpiY <= 0.0F)
        {
            throw new ArgumentException(
                "A Direct2D bitmap requires nonzero dimensions, positive finite DPI, and a complete BGRA8 row span.",
                nameof(bgra8PremultipliedPixels));
        }

        var properties = new ProGpuDirect2DNative.NativeBitmapProperties
        {
            Width = width,
            Height = height,
            Stride = stride,
            DpiX = dpiX,
            DpiY = dpiY
        };
        lock (_gate)
        {
            ThrowIfUnavailable();
            fixed (byte* pixelPointer = bgra8PremultipliedPixels)
            {
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceCreateBitmap(
                        _nativeSurface,
                        &properties,
                        pixelPointer,
                        checked((ulong)bgra8PremultipliedPixels.Length),
                        &value,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1Bitmap1 creation",
                    status,
                    nativeHResult);
                return CreateRequiredComReference(
                    value,
                    ProGpuDirect2DInterfaceKind.D2D1Bitmap1,
                    "ID2D1Bitmap1 creation");
            }
        }
    }

    public ProGpuDirect2DBitmapDescriptor GetBitmapDescriptor(
        ProGpuDirect2DComReference bitmap)
    {
        ValidateBitmap1(bitmap, nameof(bitmap));
        bool referenceAdded = false;
        try
        {
            bitmap.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                ProGpuDirect2DNative.NativeBitmapDescriptor descriptor =
                    default;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.BitmapGetDescriptor(
                        _nativeSurface,
                        bitmap.DangerousGetHandle(),
                        &descriptor,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1Bitmap1 descriptor query",
                    status,
                    nativeHResult);
                return new ProGpuDirect2DBitmapDescriptor(
                    descriptor.PixelWidth,
                    descriptor.PixelHeight,
                    descriptor.Width,
                    descriptor.Height,
                    descriptor.DpiX,
                    descriptor.DpiY,
                    descriptor.DxgiFormat,
                    descriptor.AlphaMode,
                    descriptor.Options);
            }
        }
        finally
        {
            if (referenceAdded)
            {
                bitmap.DangerousRelease();
            }
        }
    }

    public void CopyBitmapFromMemory(
        ProGpuDirect2DComReference bitmap,
        ReadOnlySpan<byte> sourceData,
        uint sourcePitch,
        ProGpuDirect2DRectU? destinationRectangle = null)
    {
        ValidateBitmap1(bitmap, nameof(bitmap));
        if (sourceData.IsEmpty || sourcePitch == 0U)
        {
            throw new ArgumentException(
                "A Direct2D bitmap upload requires nonempty bytes and a nonzero pitch.",
                nameof(sourceData));
        }
        ProGpuDirect2DRectU nativeRectangle = default;
        ProGpuDirect2DRectU* rectanglePointer = null;
        if (destinationRectangle is ProGpuDirect2DRectU rectangle)
        {
            ValidateCopyRectangle(rectangle, nameof(destinationRectangle));
            nativeRectangle = rectangle;
            rectanglePointer = &nativeRectangle;
        }
        bool referenceAdded = false;
        try
        {
            bitmap.DangerousAddRef(ref referenceAdded);
            fixed (byte* sourcePointer = sourceData)
            {
                lock (_gate)
                {
                    ThrowIfUnavailable();
                    int nativeHResult = 0;
                    ProGpuDirect2DStatus status =
                        ProGpuDirect2DNative.BitmapCopyFromMemory(
                            _nativeSurface,
                            bitmap.DangerousGetHandle(),
                            rectanglePointer,
                            sourcePointer,
                            checked((ulong)sourceData.Length),
                            sourcePitch,
                            &nativeHResult);
                    ThrowIfFailed(
                        "ID2D1Bitmap1::CopyFromMemory",
                        status,
                        nativeHResult);
                }
            }
        }
        finally
        {
            if (referenceAdded)
            {
                bitmap.DangerousRelease();
            }
        }
    }

    public void CopyBitmapFromBitmap(
        ProGpuDirect2DComReference bitmap,
        ProGpuDirect2DComReference sourceBitmap,
        ProGpuDirect2DPointU? destinationPoint = null,
        ProGpuDirect2DRectU? sourceRectangle = null)
    {
        ValidateBitmap1(bitmap, nameof(bitmap));
        ValidateBitmap1(sourceBitmap, nameof(sourceBitmap));
        ProGpuDirect2DPointU nativePoint = default;
        ProGpuDirect2DPointU* pointPointer = null;
        if (destinationPoint is ProGpuDirect2DPointU point)
        {
            nativePoint = point;
            pointPointer = &nativePoint;
        }
        ProGpuDirect2DRectU nativeRectangle = default;
        ProGpuDirect2DRectU* rectanglePointer = null;
        if (sourceRectangle is ProGpuDirect2DRectU rectangle)
        {
            ValidateCopyRectangle(rectangle, nameof(sourceRectangle));
            nativeRectangle = rectangle;
            rectanglePointer = &nativeRectangle;
        }
        bool bitmapReferenceAdded = false;
        bool sourceReferenceAdded = false;
        try
        {
            bitmap.DangerousAddRef(ref bitmapReferenceAdded);
            sourceBitmap.DangerousAddRef(ref sourceReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.BitmapCopyFromBitmap(
                        _nativeSurface,
                        bitmap.DangerousGetHandle(),
                        pointPointer,
                        sourceBitmap.DangerousGetHandle(),
                        rectanglePointer,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1Bitmap1::CopyFromBitmap",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (sourceReferenceAdded)
            {
                sourceBitmap.DangerousRelease();
            }
            if (bitmapReferenceAdded)
            {
                bitmap.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Creates a genuine ID2D1BitmapBrush1 over a provider-created bitmap.
    /// </summary>
    public ProGpuDirect2DComReference CreateBitmapBrush(
        ProGpuDirect2DComReference bitmap,
        float opacity = 1.0F,
        Matrix3x2? transform = null) =>
        CreateBitmapBrush(
            bitmap,
            new ProGpuDirect2DBitmapBrushProperties(
                ProGpuDirect2DExtendMode.Clamp,
                ProGpuDirect2DExtendMode.Clamp,
                ProGpuDirect2DInterpolationMode.Linear),
            opacity,
            transform);

    public ProGpuDirect2DComReference CreateBitmapBrush(
        ProGpuDirect2DComReference bitmap,
        ProGpuDirect2DBitmapBrushProperties properties,
        float opacity = 1.0F,
        Matrix3x2? transform = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ValidateResourceDomain(bitmap, nameof(bitmap));
        if (bitmap.InterfaceKind != ProGpuDirect2DInterfaceKind.D2D1Bitmap1)
        {
            throw new ArgumentException(
                "The COM reference must own ID2D1Bitmap1.",
                nameof(bitmap));
        }
        ValidateBitmapBrushProperties(properties);
        ProGpuDirect2DNative.NativeBrushProperties nativeBrushProperties =
            CreateNativeBrushProperties(opacity, transform);
        bool referenceAdded = false;
        try
        {
            bitmap.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceCreateBitmapBrush(
                        _nativeSurface,
                        bitmap.DangerousGetHandle(),
                        &properties,
                        &nativeBrushProperties,
                        &value,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1BitmapBrush1 creation",
                    status,
                    nativeHResult);
                return CreateRequiredComReference(
                    value,
                    ProGpuDirect2DInterfaceKind.D2D1BitmapBrush1,
                    "ID2D1BitmapBrush1 creation");
            }
        }
        finally
        {
            if (referenceAdded)
            {
                bitmap.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Creates a genuine ID2D1ImageBrush with an explicit image-space source
    /// rectangle over a provider-created bitmap, command list, or effect
    /// output image.
    /// </summary>
    public ProGpuDirect2DComReference CreateImageBrush(
        ProGpuDirect2DComReference image,
        ProGpuDirect2DImageBrushProperties properties,
        float opacity = 1.0F,
        Matrix3x2? transform = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateResourceDomain(image, nameof(image));
        if (!IsImageKind(image.InterfaceKind))
        {
            throw new ArgumentException(
                "The COM reference must own a provider-created ID2D1Image.",
                nameof(image));
        }
        ValidateImageBrushProperties(properties);
        ProGpuDirect2DNative.NativeBrushProperties nativeBrushProperties =
            CreateNativeBrushProperties(opacity, transform);
        bool referenceAdded = false;
        try
        {
            image.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceCreateImageBrush(
                        _nativeSurface,
                        image.DangerousGetHandle(),
                        &properties,
                        &nativeBrushProperties,
                        &value,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1ImageBrush creation",
                    status,
                    nativeHResult);
                return CreateRequiredComReference(
                    value,
                    ProGpuDirect2DInterfaceKind.D2D1ImageBrush,
                    "ID2D1ImageBrush creation");
            }
        }
        finally
        {
            if (referenceAdded)
            {
                image.DangerousRelease();
            }
        }
    }

    public void SetBitmapBrushProperties(
        ProGpuDirect2DComReference brush,
        ProGpuDirect2DBitmapBrushProperties properties)
    {
        ValidateBrushKind(
            brush,
            ProGpuDirect2DInterfaceKind.D2D1BitmapBrush1,
            nameof(brush));
        ValidateBitmapBrushProperties(properties);
        bool referenceAdded = false;
        try
        {
            brush.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.BitmapBrushSetProperties(
                        _nativeSurface,
                        brush.DangerousGetHandle(),
                        &properties,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1BitmapBrush1 property update",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (referenceAdded)
            {
                brush.DangerousRelease();
            }
        }
    }

    public ProGpuDirect2DBitmapBrushProperties GetBitmapBrushProperties(
        ProGpuDirect2DComReference brush)
    {
        ValidateBrushKind(
            brush,
            ProGpuDirect2DInterfaceKind.D2D1BitmapBrush1,
            nameof(brush));
        bool referenceAdded = false;
        try
        {
            brush.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                ProGpuDirect2DBitmapBrushProperties properties = default;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.BitmapBrushGetProperties(
                        _nativeSurface,
                        brush.DangerousGetHandle(),
                        &properties,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1BitmapBrush1 property query",
                    status,
                    nativeHResult);
                return properties;
            }
        }
        finally
        {
            if (referenceAdded)
            {
                brush.DangerousRelease();
            }
        }
    }

    public void SetBitmapBrushBitmap(
        ProGpuDirect2DComReference brush,
        ProGpuDirect2DComReference? bitmap)
    {
        ValidateBrushKind(
            brush,
            ProGpuDirect2DInterfaceKind.D2D1BitmapBrush1,
            nameof(brush));
        if (bitmap is not null)
        {
            ValidateResourceDomain(bitmap, nameof(bitmap));
            if (bitmap.InterfaceKind != ProGpuDirect2DInterfaceKind.D2D1Bitmap1)
            {
                throw new ArgumentException(
                    "The COM reference must own ID2D1Bitmap1.",
                    nameof(bitmap));
            }
        }
        bool brushReferenceAdded = false;
        bool bitmapReferenceAdded = false;
        try
        {
            brush.DangerousAddRef(ref brushReferenceAdded);
            bitmap?.DangerousAddRef(ref bitmapReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.BitmapBrushSetBitmap(
                        _nativeSurface,
                        brush.DangerousGetHandle(),
                        bitmap?.DangerousGetHandle() ?? 0,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1BitmapBrush1 bitmap update",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (bitmapReferenceAdded)
            {
                bitmap!.DangerousRelease();
            }
            if (brushReferenceAdded)
            {
                brush.DangerousRelease();
            }
        }
    }

    public ProGpuDirect2DComReference? GetBitmapBrushBitmap(
        ProGpuDirect2DComReference brush)
    {
        ValidateBrushKind(
            brush,
            ProGpuDirect2DInterfaceKind.D2D1BitmapBrush1,
            nameof(brush));
        bool referenceAdded = false;
        try
        {
            brush.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.BitmapBrushGetBitmap(
                        _nativeSurface,
                        brush.DangerousGetHandle(),
                        &value,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1BitmapBrush1 bitmap query",
                    status,
                    nativeHResult);
                return value == 0
                    ? null
                    : new ProGpuDirect2DComReference(
                        value,
                        ProGpuDirect2DInterfaceKind.D2D1Bitmap1,
                        _resourceDomain);
            }
        }
        finally
        {
            if (referenceAdded)
            {
                brush.DangerousRelease();
            }
        }
    }

    public void SetImageBrushProperties(
        ProGpuDirect2DComReference brush,
        ProGpuDirect2DImageBrushProperties properties)
    {
        ValidateBrushKind(
            brush,
            ProGpuDirect2DInterfaceKind.D2D1ImageBrush,
            nameof(brush));
        ValidateImageBrushProperties(properties);
        bool referenceAdded = false;
        try
        {
            brush.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.ImageBrushSetProperties(
                        _nativeSurface,
                        brush.DangerousGetHandle(),
                        &properties,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1ImageBrush property update",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (referenceAdded)
            {
                brush.DangerousRelease();
            }
        }
    }

    public ProGpuDirect2DImageBrushProperties GetImageBrushProperties(
        ProGpuDirect2DComReference brush)
    {
        ValidateBrushKind(
            brush,
            ProGpuDirect2DInterfaceKind.D2D1ImageBrush,
            nameof(brush));
        bool referenceAdded = false;
        try
        {
            brush.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                ProGpuDirect2DImageBrushProperties properties = default;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.ImageBrushGetProperties(
                        _nativeSurface,
                        brush.DangerousGetHandle(),
                        &properties,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1ImageBrush property query",
                    status,
                    nativeHResult);
                return properties;
            }
        }
        finally
        {
            if (referenceAdded)
            {
                brush.DangerousRelease();
            }
        }
    }

    public void SetImageBrushImage(
        ProGpuDirect2DComReference brush,
        ProGpuDirect2DComReference? image)
    {
        ValidateBrushKind(
            brush,
            ProGpuDirect2DInterfaceKind.D2D1ImageBrush,
            nameof(brush));
        if (image is not null)
        {
            ValidateResourceDomain(image, nameof(image));
            if (!IsImageKind(image.InterfaceKind))
            {
                throw new ArgumentException(
                    "The COM reference must own a provider-created ID2D1Image.",
                    nameof(image));
            }
        }
        bool brushReferenceAdded = false;
        bool imageReferenceAdded = false;
        try
        {
            brush.DangerousAddRef(ref brushReferenceAdded);
            image?.DangerousAddRef(ref imageReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.ImageBrushSetImage(
                        _nativeSurface,
                        brush.DangerousGetHandle(),
                        image?.DangerousGetHandle() ?? 0,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1ImageBrush image update",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (imageReferenceAdded)
            {
                image!.DangerousRelease();
            }
            if (brushReferenceAdded)
            {
                brush.DangerousRelease();
            }
        }
    }

    public ProGpuDirect2DComReference? GetImageBrushImage(
        ProGpuDirect2DComReference brush)
    {
        ValidateBrushKind(
            brush,
            ProGpuDirect2DInterfaceKind.D2D1ImageBrush,
            nameof(brush));
        bool referenceAdded = false;
        try
        {
            brush.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.ImageBrushGetImage(
                        _nativeSurface,
                        brush.DangerousGetHandle(),
                        &value,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1ImageBrush image query",
                    status,
                    nativeHResult);
                return value == 0
                    ? null
                    : new ProGpuDirect2DComReference(
                        value,
                        ProGpuDirect2DInterfaceKind.D2D1Image,
                        _resourceDomain);
            }
        }
        finally
        {
            if (referenceAdded)
            {
                brush.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Creates an open genuine ID2D1CommandList in this surface's exact
    /// Direct2D device domain. Use BeginCommandListDrawing to record and close
    /// it before drawing or using it as an image source.
    /// </summary>
    public ProGpuDirect2DComReference CreateCommandList()
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            nint value = 0;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceCreateCommandList(
                    _nativeSurface,
                    &value,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1CommandList creation",
                status,
                nativeHResult);
            return CreateRequiredComReference(
                value,
                ProGpuDirect2DInterfaceKind.D2D1CommandList,
                "ID2D1CommandList creation");
        }
    }

    /// <summary>
    /// Streams a closed same-domain command list through the native
    /// ID2D1CommandSink1 preflight and returns pointer-free structural counts.
    /// Unsupported operation classes are reported in the summary rather than
    /// silently accepted.
    /// </summary>
    public ProGpuDirect2DCommandStreamSummary GetCommandListStreamSummary(
        ProGpuDirect2DComReference commandList) =>
        ReadCommandListStreamSummary(
            commandList,
            ProGpuDirect2DNative.CommandStreamOptions.None);

    /// <summary>
    /// Requires the command list to use only operation classes admitted by
    /// the current ProGPU translation preflight. Resource conversion remains a
    /// separate typed stage. Unsupported operations fail closed with the
    /// native E_NOTIMPL HRESULT.
    /// </summary>
    public ProGpuDirect2DCommandStreamSummary
        ValidateCommandListOperationSet(
            ProGpuDirect2DComReference commandList) =>
        ReadCommandListStreamSummary(
            commandList,
            ProGpuDirect2DNative.CommandStreamOptions
                .RequireSupportedOperations);

    private ProGpuDirect2DCommandStreamSummary ReadCommandListStreamSummary(
        ProGpuDirect2DComReference commandList,
        ProGpuDirect2DNative.CommandStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(commandList);
        ValidateResourceDomain(commandList, nameof(commandList));
        if (commandList.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.D2D1CommandList)
        {
            throw new ArgumentException(
                "The COM reference must own ID2D1CommandList.",
                nameof(commandList));
        }

        bool referenceAdded = false;
        try
        {
            commandList.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                ProGpuDirect2DNative.NativeCommandStreamSummary summary =
                    new()
                    {
                        StructSize = (uint)Unsafe.SizeOf<
                            ProGpuDirect2DNative.NativeCommandStreamSummary>()
                    };
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.CommandListGetStreamSummary(
                        _nativeSurface,
                        commandList.DangerousGetHandle(),
                        options,
                        &summary,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1CommandList::Stream preflight",
                    status,
                    nativeHResult);
                return new ProGpuDirect2DCommandStreamSummary(
                    summary.Flags,
                    summary.TotalCommandCount,
                    summary.StateChangeCount,
                    summary.ClearCount,
                    summary.DrawCount,
                    summary.FillCount,
                    summary.TextDrawCount,
                    summary.ImageDrawCount,
                    summary.ClipPushCount,
                    summary.ClipPopCount,
                    summary.LayerPushCount,
                    summary.LayerPopCount,
                    summary.UnsupportedOperationCount,
                    summary.MaxScopeDepth);
            }
        }
        finally
        {
            if (referenceAdded)
            {
                commandList.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Runs the exact caller-buffer size pass for native translation of a
    /// closed same-domain command list into ProGPU's pointer-free semantic
    /// scene stream. Unsupported Direct2D state, resources, and operations
    /// fail closed rather than producing a reduced scene.
    /// </summary>
    public ProGpuDirect2DSceneStreamResult MeasureCommandListSceneStream(
        ProGpuDirect2DComReference commandList,
        ulong sceneId,
        ulong generation) =>
        BuildCommandListSceneStream(
            commandList,
            sceneId,
            generation,
            Span<byte>.Empty,
            measureOnly: true);

    /// <summary>
    /// Translates a closed same-domain command list directly into the caller's
    /// destination span. Call <see cref="MeasureCommandListSceneStream"/> to
    /// obtain the exact required size. No managed staging array, pixel
    /// readback, or COM reference is stored in the resulting stream.
    /// </summary>
    public ProGpuDirect2DSceneStreamResult WriteCommandListSceneStream(
        ProGpuDirect2DComReference commandList,
        ulong sceneId,
        ulong generation,
        Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            throw new ArgumentException(
                "The scene-stream destination must not be empty.",
                nameof(destination));
        }
        return BuildCommandListSceneStream(
            commandList,
            sceneId,
            generation,
            destination,
            measureOnly: false);
    }

    private ProGpuDirect2DSceneStreamResult BuildCommandListSceneStream(
        ProGpuDirect2DComReference commandList,
        ulong sceneId,
        ulong generation,
        Span<byte> destination,
        bool measureOnly)
    {
        ArgumentNullException.ThrowIfNull(commandList);
        ArgumentOutOfRangeException.ThrowIfZero(sceneId);
        ArgumentOutOfRangeException.ThrowIfZero(generation);
        ValidateResourceDomain(commandList, nameof(commandList));
        if (commandList.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.D2D1CommandList)
        {
            throw new ArgumentException(
                "The COM reference must own ID2D1CommandList.",
                nameof(commandList));
        }

        bool referenceAdded = false;
        try
        {
            commandList.DangerousAddRef(ref referenceAdded);
            fixed (byte* destinationPointer = destination)
            {
                lock (_gate)
                {
                    ThrowIfUnavailable();
                    ProGpuDirect2DNative.NativeSceneStreamResult result =
                        new()
                        {
                            StructSize = (uint)Unsafe.SizeOf<
                                ProGpuDirect2DNative.NativeSceneStreamResult>()
                        };
                    int nativeHResult = 0;
                    ProGpuDirect2DStatus status =
                        ProGpuDirect2DNative.CommandListBuildSceneStream(
                            _nativeSurface,
                            commandList.DangerousGetHandle(),
                            sceneId,
                            generation,
                            destinationPointer,
                            (ulong)destination.Length,
                            &result,
                            &nativeHResult);
                    ProGpuDirect2DSceneStreamResult managedResult =
                        ToManagedSceneStreamResult(result);
                    if (status == ProGpuDirect2DStatus.InsufficientBuffer)
                    {
                        if (measureOnly)
                        {
                            return managedResult;
                        }
                        throw new ArgumentException(
                            $"The destination span has {destination.Length} " +
                            $"bytes; {result.RequiredBytes} bytes are required.",
                            nameof(destination));
                    }
                    ThrowIfFailed(
                        "ID2D1CommandList semantic scene translation",
                        status,
                        nativeHResult);
                    return managedResult;
                }
            }
        }
        finally
        {
            if (referenceAdded)
            {
                commandList.DangerousRelease();
            }
        }
    }

    private static ProGpuDirect2DSceneStreamResult
        ToManagedSceneStreamResult(
            ProGpuDirect2DNative.NativeSceneStreamResult result) =>
        new(
            result.Flags,
            result.RequiredBytes,
            result.WrittenBytes,
            result.CommandCount,
            result.ResourceCount,
            result.BrushCount,
            result.TranslatedDrawCount,
            result.FailureCallbackIndex,
            result.FailureReason,
            result.ClearColor,
            result.SceneId,
            result.Generation);

    /// <summary>
    /// Creates a genuine same-device ID2D1SvgDocument from UTF-8 XML. The
    /// caller span stays pinned only for the synchronous Direct2D parse and is
    /// neither retained nor copied into an intermediate managed array.
    /// </summary>
    public ProGpuDirect2DComReference CreateSvgDocument(
        ReadOnlySpan<byte> utf8Xml,
        Vector2 viewportSize)
    {
        if (utf8Xml.Length > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(utf8Xml),
                "A Direct2D SVG document is limited to 64 MiB of UTF-8 XML.");
        }
        ValidatePositiveSize(viewportSize, nameof(viewportSize));
        lock (_gate)
        {
            ThrowIfUnavailable();
            fixed (byte* xmlPointer = utf8Xml)
            {
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status = ProGpuDirect2DNative
                    .SurfaceCreateSvgDocument(
                        _nativeSurface,
                        xmlPointer,
                        checked((uint)utf8Xml.Length),
                        viewportSize.X,
                        viewportSize.Y,
                        &value,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1SvgDocument creation",
                    status,
                    nativeHResult);
                return CreateRequiredComReference(
                    value,
                    ProGpuDirect2DInterfaceKind.D2D1SvgDocument,
                    "ID2D1SvgDocument creation");
            }
        }
    }

    /// <summary>
    /// Creates a genuine registered ID2D1Effect by CLSID in this surface's
    /// exact Direct2D device domain. The CLSID may identify a system built-in
    /// effect or an application effect registered on the owned factory.
    /// </summary>
    public ProGpuDirect2DComReference CreateEffect(Guid effectId)
    {
        if (effectId == Guid.Empty)
        {
            throw new ArgumentException(
                "A Direct2D effect CLSID cannot be empty.",
                nameof(effectId));
        }
        ProGpuDirect2DNative.NativeGuid nativeEffectId =
            ProGpuDirect2DNative.NativeGuid.FromGuid(effectId);
        lock (_gate)
        {
            ThrowIfUnavailable();
            nint value = 0;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceCreateEffect(
                    _nativeSurface,
                    &nativeEffectId,
                    &value,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1Effect creation",
                status,
                nativeHResult);
            return CreateRequiredComReference(
                value,
                ProGpuDirect2DInterfaceKind.D2D1Effect,
                "ID2D1Effect creation");
        }
    }

    /// <summary>
    /// Sets or clears one image input. Direct2D retains the selected image;
    /// caller ownership is borrowed only for the duration of this call.
    /// </summary>
    public void SetEffectInput(
        ProGpuDirect2DComReference effect,
        uint inputIndex,
        ProGpuDirect2DComReference? image,
        bool invalidate = true)
    {
        ValidateEffect(effect, nameof(effect));
        if (image is not null)
        {
            ValidateResourceDomain(image, nameof(image));
            if (!IsImageKind(image.InterfaceKind))
            {
                throw new ArgumentException(
                    "The COM reference must own a provider-created ID2D1Image.",
                    nameof(image));
            }
        }

        bool effectReferenceAdded = false;
        bool imageReferenceAdded = false;
        try
        {
            effect.DangerousAddRef(ref effectReferenceAdded);
            image?.DangerousAddRef(ref imageReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.EffectSetInput(
                        _nativeSurface,
                        effect.DangerousGetHandle(),
                        inputIndex,
                        image?.DangerousGetHandle() ?? 0,
                        invalidate ? 1U : 0U,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1Effect image-input binding",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (imageReferenceAdded)
            {
                image!.DangerousRelease();
            }
            if (effectReferenceAdded)
            {
                effect.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Connects one effect output directly to another effect input without an
    /// intermediate managed image or pixel copy.
    /// </summary>
    public void SetEffectInputEffect(
        ProGpuDirect2DComReference effect,
        uint inputIndex,
        ProGpuDirect2DComReference inputEffect,
        bool invalidate = true)
    {
        ValidateEffect(effect, nameof(effect));
        ValidateEffect(inputEffect, nameof(inputEffect));
        bool effectReferenceAdded = false;
        bool inputReferenceAdded = false;
        try
        {
            effect.DangerousAddRef(ref effectReferenceAdded);
            inputEffect.DangerousAddRef(ref inputReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.EffectSetInputEffect(
                        _nativeSurface,
                        effect.DangerousGetHandle(),
                        inputIndex,
                        inputEffect.DangerousGetHandle(),
                        invalidate ? 1U : 0U,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1Effect effect-input binding",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (inputReferenceAdded)
            {
                inputEffect.DangerousRelease();
            }
            if (effectReferenceAdded)
            {
                effect.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Sets one fixed-layout ID2D1Properties value. The span is pinned and
    /// consumed synchronously; Direct2D does not retain its address.
    /// </summary>
    public void SetEffectValue(
        ProGpuDirect2DComReference effect,
        uint propertyIndex,
        ProGpuDirect2DEffectPropertyType propertyType,
        ReadOnlySpan<byte> data)
    {
        ValidateEffect(effect, nameof(effect));
        ValidateEffectProperty(propertyType, data.Length);
        bool effectReferenceAdded = false;
        try
        {
            effect.DangerousAddRef(ref effectReferenceAdded);
            fixed (byte* dataPointer = data)
            {
                lock (_gate)
                {
                    ThrowIfUnavailable();
                    int nativeHResult = 0;
                    ProGpuDirect2DStatus status =
                        ProGpuDirect2DNative.EffectSetValue(
                            _nativeSurface,
                            effect.DangerousGetHandle(),
                            propertyIndex,
                            propertyType,
                            dataPointer,
                            checked((uint)data.Length),
                            &nativeHResult);
                    ThrowIfFailed(
                        "ID2D1Effect property update",
                        status,
                        nativeHResult);
                }
            }
        }
        finally
        {
            if (effectReferenceAdded)
            {
                effect.DangerousRelease();
            }
        }
    }

    public void SetEffectFloat(
        ProGpuDirect2DComReference effect,
        uint propertyIndex,
        float value)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "A Direct2D effect float must be finite.");
        }
        SetEffectUnmanagedValue(
            effect,
            propertyIndex,
            ProGpuDirect2DEffectPropertyType.Float,
            value);
    }

    public void SetEffectVector4(
        ProGpuDirect2DComReference effect,
        uint propertyIndex,
        Vector4 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z) || !float.IsFinite(value.W))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "A Direct2D effect vector must be finite.");
        }
        SetEffectUnmanagedValue(
            effect,
            propertyIndex,
            ProGpuDirect2DEffectPropertyType.Vector4,
            value);
    }

    public void SetEffectEnum(
        ProGpuDirect2DComReference effect,
        uint propertyIndex,
        uint value) => SetEffectUnmanagedValue(
            effect,
            propertyIndex,
            ProGpuDirect2DEffectPropertyType.Enum,
            value);

    /// <summary>
    /// Returns the effect's current output as a caller-owned ID2D1Image. The
    /// output may feed another effect, DrawImage, or CreateImageBrush.
    /// </summary>
    public ProGpuDirect2DComReference GetEffectOutput(
        ProGpuDirect2DComReference effect)
    {
        ValidateEffect(effect, nameof(effect));
        bool referenceAdded = false;
        try
        {
            effect.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.EffectGetOutput(
                        _nativeSurface,
                        effect.DangerousGetHandle(),
                        &value,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1Effect output query",
                    status,
                    nativeHResult);
                return CreateRequiredComReference(
                    value,
                    ProGpuDirect2DInterfaceKind.D2D1Image,
                    "ID2D1Effect output query");
            }
        }
        finally
        {
            if (referenceAdded)
            {
                effect.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Creates a genuine device-context-domain ID2D1Layer. Omit size to let
    /// Direct2D choose the backing-store dimensions for each push.
    /// </summary>
    public ProGpuDirect2DComReference CreateLayer(Vector2? size = null)
    {
        ProGpuDirect2DNative.NativeSizeF nativeSize = default;
        ProGpuDirect2DNative.NativeSizeF* nativeSizePointer = null;
        if (size.HasValue)
        {
            ValidatePoint(size.Value, nameof(size));
            if (size.Value.X <= 0.0F || size.Value.Y <= 0.0F)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(size),
                    "Explicit Direct2D layer dimensions must be positive.");
            }
            nativeSize = new ProGpuDirect2DNative.NativeSizeF
            {
                Width = size.Value.X,
                Height = size.Value.Y
            };
            nativeSizePointer = &nativeSize;
        }

        lock (_gate)
        {
            ThrowIfUnavailable();
            nint value = 0;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceCreateLayer(
                    _nativeSurface,
                    nativeSizePointer,
                    &value,
                    &nativeHResult);
            ThrowIfFailed("ID2D1Layer creation", status, nativeHResult);
            return CreateRequiredComReference(
                value,
                ProGpuDirect2DInterfaceKind.D2D1Layer,
                "ID2D1Layer creation");
        }
    }

    /// <summary>
    /// Creates a genuine default ID2D1DrawingStateBlock1 for repeated typed
    /// save/restore operations within Direct2D drawing sessions.
    /// </summary>
    public ProGpuDirect2DComReference CreateDrawingStateBlock()
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            nint value = 0;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceCreateDrawingStateBlock(
                    _nativeSurface,
                    &value,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1DrawingStateBlock1 creation",
                status,
                nativeHResult);
            return CreateRequiredComReference(
                value,
                ProGpuDirect2DInterfaceKind.D2D1DrawingStateBlock1,
                "ID2D1DrawingStateBlock1 creation");
        }
    }

    /// <summary>
    /// Creates a genuine IDWriteTextFormat1 from explicit UTF-16 family and
    /// locale names. This resource is device independent and may be wrapped
    /// as a Microsoft Win2D CanvasTextFormat without a CanvasDevice.
    /// </summary>
    public ProGpuDirect2DComReference CreateTextFormat(
        string fontFamily,
        string localeName,
        ProGpuDirect2DTextFormatProperties properties)
    {
        ArgumentException.ThrowIfNullOrEmpty(fontFamily);
        ArgumentException.ThrowIfNullOrEmpty(localeName);
        if (fontFamily.Contains('\0') || localeName.Contains('\0'))
        {
            throw new ArgumentException(
                "DirectWrite family and locale names cannot contain embedded NUL characters.");
        }
        ValidateTextFormatProperties(properties);
        ProGpuDirect2DNative.NativeTextFormatProperties nativeProperties =
            new()
            {
                StructSize = (uint)Unsafe.SizeOf<
                    ProGpuDirect2DNative.NativeTextFormatProperties>(),
                FontWeight = properties.FontWeight,
                FontStyle = properties.FontStyle,
                FontStretch = properties.FontStretch,
                FontSize = properties.FontSize,
                TextAlignment = properties.TextAlignment,
                ParagraphAlignment = properties.ParagraphAlignment,
                WordWrapping = properties.WordWrapping,
                ReadingDirection = properties.ReadingDirection,
                FlowDirection = properties.FlowDirection,
                IncrementalTabStop = properties.IncrementalTabStop
            };
        fixed (char* fontFamilyPointer = fontFamily)
        fixed (char* localeNamePointer = localeName)
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceCreateTextFormat(
                        _nativeSurface,
                        fontFamilyPointer,
                        checked((uint)fontFamily.Length),
                        localeNamePointer,
                        checked((uint)localeName.Length),
                        &nativeProperties,
                        &value,
                        &nativeHResult);
                ThrowIfFailed(
                    "IDWriteTextFormat1 creation",
                    status,
                    nativeHResult);
                return CreateRequiredComReference(
                    value,
                    ProGpuDirect2DInterfaceKind.DWriteTextFormat1,
                    "IDWriteTextFormat1 creation");
            }
        }
    }

    /// <summary>
    /// Creates a retained genuine IDWriteTextLayout4. DirectWrite copies the
    /// caller text during this synchronous creation call, so the returned
    /// layout can be reused without retaining the input span.
    /// </summary>
    public ProGpuDirect2DComReference CreateTextLayout(
        ReadOnlySpan<char> text,
        ProGpuDirect2DComReference textFormat,
        float maximumWidth,
        float maximumHeight)
    {
        ArgumentNullException.ThrowIfNull(textFormat);
        ValidateResourceDomain(textFormat, nameof(textFormat));
        if (textFormat.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.DWriteTextFormat1)
        {
            throw new ArgumentException(
                "The COM reference must own an IDWriteTextFormat1.",
                nameof(textFormat));
        }
        if (!float.IsFinite(maximumWidth) || maximumWidth <= 0.0F)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumWidth),
                "DirectWrite text-layout width must be positive and finite.");
        }
        if (!float.IsFinite(maximumHeight) || maximumHeight <= 0.0F)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumHeight),
                "DirectWrite text-layout height must be positive and finite.");
        }

        bool formatReferenceAdded = false;
        try
        {
            textFormat.DangerousAddRef(ref formatReferenceAdded);
            fixed (char* textPointer = text)
            {
                lock (_gate)
                {
                    ThrowIfUnavailable();
                    nint value = 0;
                    int nativeHResult = 0;
                    ProGpuDirect2DStatus status =
                        ProGpuDirect2DNative.SurfaceCreateTextLayout(
                            _nativeSurface,
                            textPointer,
                            checked((uint)text.Length),
                            textFormat.DangerousGetHandle(),
                            maximumWidth,
                            maximumHeight,
                            &value,
                            &nativeHResult);
                    ThrowIfFailed(
                        "IDWriteTextLayout4 creation",
                        status,
                        nativeHResult);
                    return CreateRequiredComReference(
                        value,
                        ProGpuDirect2DInterfaceKind.DWriteTextLayout4,
                        "IDWriteTextLayout4 creation");
                }
            }
        }
        finally
        {
            if (formatReferenceAdded)
            {
                textFormat.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Applies selected mutable DirectWrite formatting to one UTF-16 range in
    /// a retained IDWriteTextLayout4. When <see
    /// cref="ProGpuDirect2DTextRangeFormatFlags.DrawingEffect"/> is selected,
    /// a genuine same-domain Direct2D brush sets the range color/brush; a null
    /// brush clears the drawing effect and restores the draw-call default.
    /// </summary>
    public void SetTextLayoutRangeFormat(
        ProGpuDirect2DComReference textLayout,
        uint rangeStart,
        uint rangeLength,
        ProGpuDirect2DTextRangeFormat formatting,
        ProGpuDirect2DComReference? drawingEffectBrush = null)
    {
        ArgumentNullException.ThrowIfNull(textLayout);
        ValidateResourceDomain(textLayout, nameof(textLayout));
        if (textLayout.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.DWriteTextLayout4)
        {
            throw new ArgumentException(
                "The COM reference must own an IDWriteTextLayout4.",
                nameof(textLayout));
        }
        ValidateTextRangeFormat(
            rangeStart,
            rangeLength,
            formatting,
            drawingEffectBrush);

        ProGpuDirect2DNative.NativeTextRangeFormat nativeFormatting =
            new()
            {
                StructSize = (uint)Unsafe.SizeOf<
                    ProGpuDirect2DNative.NativeTextRangeFormat>(),
                Flags = formatting.Flags,
                RangeStart = rangeStart,
                RangeLength = rangeLength,
                FontWeight = formatting.FontWeight,
                FontStyle = formatting.FontStyle,
                FontStretch = formatting.FontStretch,
                FontSize = formatting.FontSize,
                Underline = formatting.Underline ? 1U : 0U,
                Strikethrough = formatting.Strikethrough ? 1U : 0U
            };
        bool layoutReferenceAdded = false;
        bool brushReferenceAdded = false;
        try
        {
            textLayout.DangerousAddRef(ref layoutReferenceAdded);
            drawingEffectBrush?.DangerousAddRef(ref brushReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.TextLayoutSetRangeFormat(
                        _nativeSurface,
                        textLayout.DangerousGetHandle(),
                        &nativeFormatting,
                        drawingEffectBrush?.DangerousGetHandle() ?? 0,
                        &nativeHResult);
                ThrowIfFailed(
                    "IDWriteTextLayout4 range formatting",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (brushReferenceAdded)
            {
                drawingEffectBrush!.DangerousRelease();
            }
            if (layoutReferenceAdded)
            {
                textLayout.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Creates one genuine device-independent IDWriteTypography from a pinned
    /// OpenType feature span consumed synchronously by DirectWrite.
    /// </summary>
    public ProGpuDirect2DComReference CreateTypography(
        ReadOnlySpan<ProGpuDirect2DTypographyFeature> features)
    {
        if (features.IsEmpty || features.Length > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(features),
                "DirectWrite typography requires 1 through 4096 features.");
        }
        foreach (ProGpuDirect2DTypographyFeature feature in features)
        {
            if (feature.NameTag == 0U)
            {
                throw new ArgumentException(
                    "OpenType typography feature tags must be nonzero.",
                    nameof(features));
            }
        }

        fixed (ProGpuDirect2DTypographyFeature* featurePointer = features)
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceCreateTypography(
                        _nativeSurface,
                        featurePointer,
                        checked((uint)features.Length),
                        &value,
                        &nativeHResult);
                ThrowIfFailed(
                    "IDWriteTypography creation",
                    status,
                    nativeHResult);
                return CreateRequiredComReference(
                    value,
                    ProGpuDirect2DInterfaceKind.DWriteTypography,
                    "IDWriteTypography creation");
            }
        }
    }

    /// <summary>
    /// Resolves one installed system family and returns a genuine,
    /// device-independent IDWriteFontFaceReference. The family span is
    /// consumed synchronously and is not retained.
    /// </summary>
    public ProGpuDirect2DComReference CreateSystemFontFaceReference(
        ReadOnlySpan<char> fontFamily) =>
        CreateSystemFontFaceReference(
            fontFamily,
            new ProGpuDirect2DFontFaceProperties(
                400U,
                ProGpuDirect2DFontStyle.Normal,
                ProGpuDirect2DFontStretch.Normal));

    public ProGpuDirect2DComReference CreateSystemFontFaceReference(
        ReadOnlySpan<char> fontFamily,
        ProGpuDirect2DFontFaceProperties properties)
    {
        if (fontFamily.IsEmpty)
        {
            throw new ArgumentException(
                "A DirectWrite system font family is required.",
                nameof(fontFamily));
        }
        if (fontFamily.Contains('\0'))
        {
            throw new ArgumentException(
                "A DirectWrite system font family cannot contain an embedded NUL.",
                nameof(fontFamily));
        }
        ValidateFontFaceProperties(properties);
        ProGpuDirect2DNative.NativeFontFaceProperties nativeProperties =
            new()
            {
                StructSize = (uint)Unsafe.SizeOf<
                    ProGpuDirect2DNative.NativeFontFaceProperties>(),
                FontWeight = properties.FontWeight,
                FontStyle = properties.FontStyle,
                FontStretch = properties.FontStretch
            };
        fixed (char* familyPointer = fontFamily)
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status = ProGpuDirect2DNative
                    .SurfaceCreateSystemFontFaceReference(
                        _nativeSurface,
                        familyPointer,
                        checked((uint)fontFamily.Length),
                        &nativeProperties,
                        &value,
                        &nativeHResult);
                ThrowIfFailed(
                    "IDWriteFontFaceReference system-font resolution",
                    status,
                    nativeHResult);
                return CreateRequiredComReference(
                    value,
                    ProGpuDirect2DInterfaceKind.DWriteFontFaceReference,
                    "IDWriteFontFaceReference system-font resolution");
            }
        }
    }

    /// <summary>
    /// Creates a genuine IDWriteFontFace5 used by shaped glyph-run drawing
    /// from a caller-owned IDWriteFontFaceReference.
    /// </summary>
    public ProGpuDirect2DComReference CreateFontFace(
        ProGpuDirect2DComReference fontFaceReference)
    {
        ArgumentNullException.ThrowIfNull(fontFaceReference);
        ValidateResourceDomain(fontFaceReference, nameof(fontFaceReference));
        if (fontFaceReference.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.DWriteFontFaceReference)
        {
            throw new ArgumentException(
                "The COM reference must own an IDWriteFontFaceReference.",
                nameof(fontFaceReference));
        }

        bool referenceAdded = false;
        try
        {
            fontFaceReference.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status = ProGpuDirect2DNative
                    .FontFaceReferenceCreateFontFace(
                        _nativeSurface,
                        fontFaceReference.DangerousGetHandle(),
                        &value,
                        &nativeHResult);
                ThrowIfFailed(
                    "IDWriteFontFace5 creation",
                    status,
                    nativeHResult);
                return CreateRequiredComReference(
                    value,
                    ProGpuDirect2DInterfaceKind.DWriteFontFace5,
                    "IDWriteFontFace5 creation");
            }
        }
        finally
        {
            if (referenceAdded)
            {
                fontFaceReference.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Applies one genuine IDWriteTypography to a nonempty UTF-16 range in a
    /// retained IDWriteTextLayout4.
    /// </summary>
    public void SetTextLayoutTypography(
        ProGpuDirect2DComReference textLayout,
        uint rangeStart,
        uint rangeLength,
        ProGpuDirect2DComReference typography)
    {
        ArgumentNullException.ThrowIfNull(textLayout);
        ArgumentNullException.ThrowIfNull(typography);
        ValidateResourceDomain(textLayout, nameof(textLayout));
        ValidateResourceDomain(typography, nameof(typography));
        if (textLayout.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.DWriteTextLayout4)
        {
            throw new ArgumentException(
                "The COM reference must own an IDWriteTextLayout4.",
                nameof(textLayout));
        }
        if (typography.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.DWriteTypography)
        {
            throw new ArgumentException(
                "The COM reference must own an IDWriteTypography.",
                nameof(typography));
        }
        if (rangeLength == 0U || rangeStart > uint.MaxValue - rangeLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rangeLength),
                "DirectWrite typography requires a nonempty, nonoverflowing range.");
        }

        bool layoutReferenceAdded = false;
        bool typographyReferenceAdded = false;
        try
        {
            textLayout.DangerousAddRef(ref layoutReferenceAdded);
            typography.DangerousAddRef(ref typographyReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.TextLayoutSetTypography(
                        _nativeSurface,
                        textLayout.DangerousGetHandle(),
                        typography.DangerousGetHandle(),
                        rangeStart,
                        rangeLength,
                        &nativeHResult);
                ThrowIfFailed(
                    "IDWriteTextLayout4 typography assignment",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (typographyReferenceAdded)
            {
                typography.DangerousRelease();
            }
            if (layoutReferenceAdded)
            {
                textLayout.DangerousRelease();
            }
        }
    }

    internal void SaveDrawingState(
        ProGpuDirect2DComReference drawingStateBlock) =>
        ApplyDrawingState(drawingStateBlock, restore: false);

    internal void RestoreDrawingState(
        ProGpuDirect2DComReference drawingStateBlock) =>
        ApplyDrawingState(drawingStateBlock, restore: true);

    internal void DrawText(
        ReadOnlySpan<char> text,
        ProGpuDirect2DComReference textFormat,
        ProGpuDirect2DRect layoutRectangle,
        ProGpuDirect2DComReference defaultFillBrush,
        ProGpuDirect2DDrawTextOptions options,
        ProGpuDirect2DMeasuringMode measuringMode)
    {
        ArgumentNullException.ThrowIfNull(textFormat);
        ArgumentNullException.ThrowIfNull(defaultFillBrush);
        ValidateResourceDomain(textFormat, nameof(textFormat));
        ValidateResourceDomain(defaultFillBrush, nameof(defaultFillBrush));
        if (textFormat.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.DWriteTextFormat1)
        {
            throw new ArgumentException(
                "The COM reference must own an IDWriteTextFormat1.",
                nameof(textFormat));
        }
        if (!IsBrushKind(defaultFillBrush.InterfaceKind))
        {
            throw new ArgumentException(
                "The COM reference must own a Direct2D brush.",
                nameof(defaultFillBrush));
        }
        ValidateRectangle(layoutRectangle);
        const ProGpuDirect2DDrawTextOptions knownOptions =
            ProGpuDirect2DDrawTextOptions.NoSnap |
            ProGpuDirect2DDrawTextOptions.Clip |
            ProGpuDirect2DDrawTextOptions.EnableColorFont |
            ProGpuDirect2DDrawTextOptions.DisableColorBitmapSnapping;
        if ((options & ~knownOptions) != 0 ||
            measuringMode > ProGpuDirect2DMeasuringMode.GdiNatural)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Direct2D text drawing options contain an unknown value.");
        }

        bool formatReferenceAdded = false;
        bool brushReferenceAdded = false;
        try
        {
            textFormat.DangerousAddRef(ref formatReferenceAdded);
            defaultFillBrush.DangerousAddRef(ref brushReferenceAdded);
            fixed (char* textPointer = text)
            {
                lock (_gate)
                {
                    ValidateTypedDrawingProducer();
                    int nativeHResult = 0;
                    ProGpuDirect2DStatus status =
                        ProGpuDirect2DNative.SurfaceDrawText(
                            _nativeSurface,
                            textPointer,
                            checked((uint)text.Length),
                            textFormat.DangerousGetHandle(),
                            &layoutRectangle,
                            defaultFillBrush.DangerousGetHandle(),
                            options,
                            measuringMode,
                            &nativeHResult);
                    ThrowIfFailed(
                        "ID2D1RenderTarget DrawText",
                        status,
                        nativeHResult);
                }
            }
        }
        finally
        {
            if (brushReferenceAdded)
            {
                defaultFillBrush.DangerousRelease();
            }
            if (formatReferenceAdded)
            {
                textFormat.DangerousRelease();
            }
        }
    }

    internal void DrawTextLayout(
        Vector2 origin,
        ProGpuDirect2DComReference textLayout,
        ProGpuDirect2DComReference defaultFillBrush,
        ProGpuDirect2DDrawTextOptions options)
    {
        ArgumentNullException.ThrowIfNull(textLayout);
        ArgumentNullException.ThrowIfNull(defaultFillBrush);
        ValidateResourceDomain(textLayout, nameof(textLayout));
        ValidateResourceDomain(defaultFillBrush, nameof(defaultFillBrush));
        if (textLayout.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.DWriteTextLayout4)
        {
            throw new ArgumentException(
                "The COM reference must own an IDWriteTextLayout4.",
                nameof(textLayout));
        }
        if (!IsBrushKind(defaultFillBrush.InterfaceKind))
        {
            throw new ArgumentException(
                "The COM reference must own a Direct2D brush.",
                nameof(defaultFillBrush));
        }
        const ProGpuDirect2DDrawTextOptions knownOptions =
            ProGpuDirect2DDrawTextOptions.NoSnap |
            ProGpuDirect2DDrawTextOptions.Clip |
            ProGpuDirect2DDrawTextOptions.EnableColorFont |
            ProGpuDirect2DDrawTextOptions.DisableColorBitmapSnapping;
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y))
        {
            throw new ArgumentOutOfRangeException(
                nameof(origin),
                "Direct2D text-layout origin must be finite.");
        }
        if ((options & ~knownOptions) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Direct2D text-layout options contain an unknown value.");
        }

        bool layoutReferenceAdded = false;
        bool brushReferenceAdded = false;
        try
        {
            textLayout.DangerousAddRef(ref layoutReferenceAdded);
            defaultFillBrush.DangerousAddRef(ref brushReferenceAdded);
            lock (_gate)
            {
                ValidateTypedDrawingProducer();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceDrawTextLayout(
                        _nativeSurface,
                        origin.X,
                        origin.Y,
                        textLayout.DangerousGetHandle(),
                        defaultFillBrush.DangerousGetHandle(),
                        options,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1RenderTarget DrawTextLayout",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (brushReferenceAdded)
            {
                defaultFillBrush.DangerousRelease();
            }
            if (layoutReferenceAdded)
            {
                textLayout.DangerousRelease();
            }
        }
    }

    internal void DrawGlyphRun(
        Vector2 baselineOrigin,
        float fontEmSize,
        ProGpuDirect2DComReference fontFace,
        ReadOnlySpan<ushort> glyphIndices,
        ReadOnlySpan<float> glyphAdvances,
        ReadOnlySpan<ProGpuDirect2DGlyphOffset> glyphOffsets,
        ProGpuDirect2DComReference foregroundBrush,
        bool isSideways,
        uint bidiLevel,
        ProGpuDirect2DMeasuringMode measuringMode)
    {
        ArgumentNullException.ThrowIfNull(fontFace);
        ArgumentNullException.ThrowIfNull(foregroundBrush);
        ValidateResourceDomain(fontFace, nameof(fontFace));
        ValidateResourceDomain(foregroundBrush, nameof(foregroundBrush));
        if (fontFace.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.DWriteFontFace5)
        {
            throw new ArgumentException(
                "The COM reference must own an IDWriteFontFace5.",
                nameof(fontFace));
        }
        if (!IsBrushKind(foregroundBrush.InterfaceKind))
        {
            throw new ArgumentException(
                "The COM reference must own a Direct2D brush.",
                nameof(foregroundBrush));
        }
        if (!float.IsFinite(baselineOrigin.X) ||
            !float.IsFinite(baselineOrigin.Y) ||
            !float.IsFinite(fontEmSize) || fontEmSize <= 0.0F ||
            glyphIndices.IsEmpty || glyphIndices.Length > 1 << 20 ||
            !glyphAdvances.IsEmpty &&
                glyphAdvances.Length != glyphIndices.Length ||
            !glyphOffsets.IsEmpty && glyphOffsets.Length != glyphIndices.Length ||
            bidiLevel > 125U ||
            measuringMode > ProGpuDirect2DMeasuringMode.GdiNatural)
        {
            throw new ArgumentOutOfRangeException(
                nameof(glyphIndices),
                "The DirectWrite glyph run contains invalid bounds, counts, bidi state, or measuring mode.");
        }
        foreach (float advance in glyphAdvances)
        {
            if (!float.IsFinite(advance))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(glyphAdvances),
                    "DirectWrite glyph advances must be finite.");
            }
        }
        foreach (ProGpuDirect2DGlyphOffset offset in glyphOffsets)
        {
            if (!float.IsFinite(offset.AdvanceOffset) ||
                !float.IsFinite(offset.AscenderOffset))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(glyphOffsets),
                    "DirectWrite glyph offsets must be finite.");
            }
        }

        bool faceReferenceAdded = false;
        bool brushReferenceAdded = false;
        try
        {
            fontFace.DangerousAddRef(ref faceReferenceAdded);
            foregroundBrush.DangerousAddRef(ref brushReferenceAdded);
            fixed (ushort* indicesPointer = glyphIndices)
            fixed (float* advancesPointer = glyphAdvances)
            fixed (ProGpuDirect2DGlyphOffset* offsetsPointer = glyphOffsets)
            {
                lock (_gate)
                {
                    ValidateTypedDrawingProducer();
                    int nativeHResult = 0;
                    ProGpuDirect2DStatus status = ProGpuDirect2DNative
                        .SurfaceDrawGlyphRun(
                            _nativeSurface,
                            baselineOrigin.X,
                            baselineOrigin.Y,
                            fontEmSize,
                            fontFace.DangerousGetHandle(),
                            indicesPointer,
                            checked((uint)glyphIndices.Length),
                            advancesPointer,
                            checked((uint)glyphAdvances.Length),
                            offsetsPointer,
                            checked((uint)glyphOffsets.Length),
                            isSideways ? 1U : 0U,
                            bidiLevel,
                            foregroundBrush.DangerousGetHandle(),
                            measuringMode,
                            &nativeHResult);
                    ThrowIfFailed(
                        "ID2D1DeviceContext DrawGlyphRun",
                        status,
                        nativeHResult);
                }
            }
        }
        finally
        {
            if (brushReferenceAdded)
            {
                foregroundBrush.DangerousRelease();
            }
            if (faceReferenceAdded)
            {
                fontFace.DangerousRelease();
            }
        }
    }

    internal ProGpuDirect2DColorGlyphPath DrawColorGlyphRun(
        Vector2 baselineOrigin,
        float fontEmSize,
        ProGpuDirect2DComReference fontFace,
        ReadOnlySpan<ushort> glyphIndices,
        ReadOnlySpan<float> glyphAdvances,
        ReadOnlySpan<ProGpuDirect2DGlyphOffset> glyphOffsets,
        ProGpuDirect2DComReference foregroundBrush,
        uint colorPaletteIndex,
        bool isSideways,
        uint bidiLevel,
        ProGpuDirect2DMeasuringMode measuringMode)
    {
        ArgumentNullException.ThrowIfNull(fontFace);
        ArgumentNullException.ThrowIfNull(foregroundBrush);
        ValidateResourceDomain(fontFace, nameof(fontFace));
        ValidateResourceDomain(foregroundBrush, nameof(foregroundBrush));
        if (fontFace.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.DWriteFontFace5)
        {
            throw new ArgumentException(
                "The COM reference must own an IDWriteFontFace5.",
                nameof(fontFace));
        }
        if (!IsBrushKind(foregroundBrush.InterfaceKind))
        {
            throw new ArgumentException(
                "The COM reference must own a Direct2D brush.",
                nameof(foregroundBrush));
        }
        if (!float.IsFinite(baselineOrigin.X) ||
            !float.IsFinite(baselineOrigin.Y) ||
            !float.IsFinite(fontEmSize) || fontEmSize <= 0.0F ||
            glyphIndices.IsEmpty || glyphIndices.Length > 1 << 20 ||
            !glyphAdvances.IsEmpty &&
                glyphAdvances.Length != glyphIndices.Length ||
            !glyphOffsets.IsEmpty && glyphOffsets.Length != glyphIndices.Length ||
            bidiLevel > 125U ||
            measuringMode > ProGpuDirect2DMeasuringMode.GdiNatural)
        {
            throw new ArgumentOutOfRangeException(
                nameof(glyphIndices),
                "The DirectWrite color glyph run contains invalid bounds, counts, bidi state, or measuring mode.");
        }
        foreach (float advance in glyphAdvances)
        {
            if (!float.IsFinite(advance))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(glyphAdvances),
                    "DirectWrite glyph advances must be finite.");
            }
        }
        foreach (ProGpuDirect2DGlyphOffset offset in glyphOffsets)
        {
            if (!float.IsFinite(offset.AdvanceOffset) ||
                !float.IsFinite(offset.AscenderOffset))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(glyphOffsets),
                    "DirectWrite glyph offsets must be finite.");
            }
        }

        bool faceReferenceAdded = false;
        bool brushReferenceAdded = false;
        try
        {
            fontFace.DangerousAddRef(ref faceReferenceAdded);
            foregroundBrush.DangerousAddRef(ref brushReferenceAdded);
            fixed (ushort* indicesPointer = glyphIndices)
            fixed (float* advancesPointer = glyphAdvances)
            fixed (ProGpuDirect2DGlyphOffset* offsetsPointer = glyphOffsets)
            {
                lock (_gate)
                {
                    ValidateTypedDrawingProducer();
                    ProGpuDirect2DColorGlyphPath selectedPath = default;
                    int nativeHResult = 0;
                    ProGpuDirect2DStatus status = ProGpuDirect2DNative
                        .SurfaceDrawColorGlyphRun(
                            _nativeSurface,
                            baselineOrigin.X,
                            baselineOrigin.Y,
                            fontEmSize,
                            fontFace.DangerousGetHandle(),
                            indicesPointer,
                            checked((uint)glyphIndices.Length),
                            advancesPointer,
                            checked((uint)glyphAdvances.Length),
                            offsetsPointer,
                            checked((uint)glyphOffsets.Length),
                            isSideways ? 1U : 0U,
                            bidiLevel,
                            foregroundBrush.DangerousGetHandle(),
                            colorPaletteIndex,
                            measuringMode,
                            &selectedPath,
                            &nativeHResult);
                    ThrowIfFailed(
                        "DirectWrite/Direct2D color glyph drawing",
                        status,
                        nativeHResult);
                    if (selectedPath is <
                            ProGpuDirect2DColorGlyphPath.DeviceContext7 or >
                            ProGpuDirect2DColorGlyphPath.MonochromeNoColor)
                    {
                        throw new InvalidOperationException(
                            "The native color-glyph path diagnostic is invalid.");
                    }
                    return selectedPath;
                }
            }
        }
        finally
        {
            if (brushReferenceAdded)
            {
                foregroundBrush.DangerousRelease();
            }
            if (faceReferenceAdded)
            {
                fontFace.DangerousRelease();
            }
        }
    }

    internal void DrawSvgDocument(
        ProGpuDirect2DComReference svgDocument,
        Vector2 viewportSize,
        Vector2 origin)
    {
        ArgumentNullException.ThrowIfNull(svgDocument);
        ValidateResourceDomain(svgDocument, nameof(svgDocument));
        if (svgDocument.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.D2D1SvgDocument)
        {
            throw new ArgumentException(
                "The COM reference must own an ID2D1SvgDocument.",
                nameof(svgDocument));
        }
        ValidatePositiveSize(viewportSize, nameof(viewportSize));
        ValidatePoint(origin, nameof(origin));
        bool referenceAdded = false;
        try
        {
            svgDocument.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ValidateTypedDrawingProducer();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status = ProGpuDirect2DNative
                    .SurfaceDrawSvgDocument(
                        _nativeSurface,
                        svgDocument.DangerousGetHandle(),
                        viewportSize.X,
                        viewportSize.Y,
                        origin.X,
                        origin.Y,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1DeviceContext5 DrawSvgDocument",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (referenceAdded)
            {
                svgDocument.DangerousRelease();
            }
        }
    }

    internal uint PushLayer(
        ProGpuDirect2DComReference layer,
        ProGpuDirect2DLayerParameters parameters,
        ProGpuDirect2DComReference? geometricMask,
        ProGpuDirect2DComReference? opacityBrush)
    {
        ValidateLayer(layer, nameof(layer));
        ValidateLayerParameters(parameters);
        if (geometricMask is not null)
        {
            ValidateGeometry(geometricMask, nameof(geometricMask));
        }
        if (opacityBrush is not null)
        {
            ValidateResourceDomain(opacityBrush, nameof(opacityBrush));
            if (!IsBrushKind(opacityBrush.InterfaceKind))
            {
                throw new ArgumentException(
                    "The COM reference must own a Direct2D brush.",
                    nameof(opacityBrush));
            }
        }

        ProGpuDirect2DNative.NativeLayerParameters nativeParameters = new()
        {
            ContentBounds = parameters.ContentBounds,
            MaskAntialiasMode = parameters.MaskAntialiasMode,
            MaskTransform = CreateNativeMatrix(
                parameters.MaskTransform ?? Matrix3x2.Identity),
            Opacity = parameters.Opacity,
            Options = parameters.Options
        };
        bool layerReferenceAdded = false;
        bool maskReferenceAdded = false;
        bool brushReferenceAdded = false;
        try
        {
            layer.DangerousAddRef(ref layerReferenceAdded);
            geometricMask?.DangerousAddRef(ref maskReferenceAdded);
            opacityBrush?.DangerousAddRef(ref brushReferenceAdded);
            lock (_gate)
            {
                ValidateTypedDrawingProducer();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfacePushLayer(
                        _nativeSurface,
                        &nativeParameters,
                        geometricMask?.DangerousGetHandle() ?? 0,
                        opacityBrush?.DangerousGetHandle() ?? 0,
                        layer.DangerousGetHandle(),
                        &nativeHResult);
                ThrowIfFailed("ID2D1Layer push", status, nativeHResult);
                return checked(++_typedDrawScopeDepth);
            }
        }
        finally
        {
            if (brushReferenceAdded)
            {
                opacityBrush!.DangerousRelease();
            }
            if (maskReferenceAdded)
            {
                geometricMask!.DangerousRelease();
            }
            if (layerReferenceAdded)
            {
                layer.DangerousRelease();
            }
        }
    }

    internal void PopLayer(uint expectedDepth)
    {
        lock (_gate)
        {
            ValidateTypedDrawingProducer();
            if (expectedDepth == 0U ||
                expectedDepth != _typedDrawScopeDepth)
            {
                throw new InvalidOperationException(
                    "Direct2D layer scopes must be disposed once in LIFO order.");
            }
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfacePopLayer(
                    _nativeSurface,
                    &nativeHResult);
            ThrowIfFailed("ID2D1Layer pop", status, nativeHResult);
            --_typedDrawScopeDepth;
        }
    }

    internal uint PushAxisAlignedClip(
        ProGpuDirect2DRect clipRectangle,
        ProGpuDirect2DAntialiasMode antialiasMode)
    {
        ValidateRectangle(clipRectangle);
        ValidateAntialiasMode(antialiasMode, nameof(antialiasMode));
        lock (_gate)
        {
            ValidateTypedDrawingProducer();
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfacePushAxisAlignedClip(
                    _nativeSurface,
                    &clipRectangle,
                    antialiasMode,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1DeviceContext::PushAxisAlignedClip",
                status,
                nativeHResult);
            return checked(++_typedDrawScopeDepth);
        }
    }

    internal void PopAxisAlignedClip(uint expectedDepth)
    {
        lock (_gate)
        {
            ValidateTypedDrawingProducer();
            if (expectedDepth == 0U ||
                expectedDepth != _typedDrawScopeDepth)
            {
                throw new InvalidOperationException(
                    "Direct2D clip and layer scopes must be disposed once in LIFO order.");
            }
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfacePopAxisAlignedClip(
                    _nativeSurface,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1DeviceContext::PopAxisAlignedClip",
                status,
                nativeHResult);
            --_typedDrawScopeDepth;
        }
    }

    internal void DrawBitmap(
        ProGpuDirect2DComReference bitmap,
        ProGpuDirect2DRect? destinationRectangle,
        float opacity,
        ProGpuDirect2DInterpolationMode interpolationMode,
        ProGpuDirect2DRect? sourceRectangle,
        Matrix4x4? perspectiveTransform)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ValidateResourceDomain(bitmap, nameof(bitmap));
        if (bitmap.InterfaceKind is not
                ProGpuDirect2DInterfaceKind.D2D1Bitmap and not
                ProGpuDirect2DInterfaceKind.D2D1Bitmap1)
        {
            throw new ArgumentException(
                "The COM reference must own a genuine ID2D1Bitmap.",
                nameof(bitmap));
        }
        if (destinationRectangle is ProGpuDirect2DRect destination)
        {
            ValidateRectangle(destination);
        }
        if (sourceRectangle is ProGpuDirect2DRect source)
        {
            ValidateRectangle(source);
        }
        ValidateOpacity(opacity, nameof(opacity));
        ValidateInterpolationMode(interpolationMode, nameof(interpolationMode));

        ProGpuDirect2DRect nativeDestination =
            destinationRectangle.GetValueOrDefault();
        ProGpuDirect2DRect* destinationPointer = destinationRectangle.HasValue
            ? &nativeDestination
            : null;
        ProGpuDirect2DRect nativeSource = sourceRectangle.GetValueOrDefault();
        ProGpuDirect2DRect* sourcePointer = sourceRectangle.HasValue
            ? &nativeSource
            : null;
        ProGpuDirect2DNative.NativeMatrix4X4F nativePerspective = default;
        ProGpuDirect2DNative.NativeMatrix4X4F* perspectivePointer = null;
        if (perspectiveTransform is Matrix4x4 perspective)
        {
            nativePerspective = CreateNativeMatrix(perspective);
            perspectivePointer = &nativePerspective;
        }

        bool referenceAdded = false;
        try
        {
            bitmap.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ValidateTypedDrawingProducer();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceDrawBitmap(
                        _nativeSurface,
                        bitmap.DangerousGetHandle(),
                        destinationPointer,
                        opacity,
                        interpolationMode,
                        sourcePointer,
                        perspectivePointer,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1DeviceContext::DrawBitmap",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (referenceAdded)
            {
                bitmap.DangerousRelease();
            }
        }
    }

    internal void DrawImage(
        ProGpuDirect2DComReference image,
        Vector2? targetOffset,
        ProGpuDirect2DRect? imageRectangle,
        ProGpuDirect2DInterpolationMode interpolationMode,
        ProGpuDirect2DCompositeMode compositeMode)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateResourceDomain(image, nameof(image));
        if (!IsImageKind(image.InterfaceKind))
        {
            throw new ArgumentException(
                "The COM reference must own a genuine ID2D1Image.",
                nameof(image));
        }
        if (targetOffset is Vector2 offset)
        {
            ValidatePoint(offset, nameof(targetOffset));
        }
        if (imageRectangle is ProGpuDirect2DRect rectangle)
        {
            ValidateRectangle(rectangle);
        }
        ValidateInterpolationMode(interpolationMode, nameof(interpolationMode));
        ValidateCompositeMode(compositeMode, nameof(compositeMode));

        ProGpuDirect2DNative.NativePoint2F nativeOffset = default;
        ProGpuDirect2DNative.NativePoint2F* offsetPointer = null;
        if (targetOffset is Vector2 offsetValue)
        {
            nativeOffset = CreateNativePoint(offsetValue);
            offsetPointer = &nativeOffset;
        }
        ProGpuDirect2DRect nativeRectangle = imageRectangle.GetValueOrDefault();
        ProGpuDirect2DRect* rectanglePointer = imageRectangle.HasValue
            ? &nativeRectangle
            : null;

        bool referenceAdded = false;
        try
        {
            image.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ValidateTypedDrawingProducer();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceDrawImage(
                        _nativeSurface,
                        image.DangerousGetHandle(),
                        offsetPointer,
                        rectanglePointer,
                        interpolationMode,
                        compositeMode,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1DeviceContext::DrawImage",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (referenceAdded)
            {
                image.DangerousRelease();
            }
        }
    }

    public ProGpuDirect2DComReference CreateRectangleGeometry(
        ProGpuDirect2DRect rectangle)
    {
        ValidateRectangle(rectangle);
        lock (_gate)
        {
            ThrowIfUnavailable();
            nint value = 0;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceCreateRectangleGeometry(
                    _nativeSurface,
                    &rectangle,
                    &value,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1RectangleGeometry creation",
                status,
                nativeHResult);
            return CreateRequiredComReference(
                value,
                ProGpuDirect2DInterfaceKind.D2D1RectangleGeometry,
                "ID2D1RectangleGeometry creation");
        }
    }

    public ProGpuDirect2DComReference CreateRoundedRectangleGeometry(
        ProGpuDirect2DRect rectangle,
        float radiusX,
        float radiusY)
    {
        ValidateRectangle(rectangle);
        ValidateRadii(radiusX, radiusY);
        lock (_gate)
        {
            ThrowIfUnavailable();
            nint value = 0;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative
                    .SurfaceCreateRoundedRectangleGeometry(
                        _nativeSurface,
                        &rectangle,
                        radiusX,
                        radiusY,
                        &value,
                        &nativeHResult);
            ThrowIfFailed(
                "ID2D1RoundedRectangleGeometry creation",
                status,
                nativeHResult);
            return CreateRequiredComReference(
                value,
                ProGpuDirect2DInterfaceKind.D2D1RoundedRectangleGeometry,
                "ID2D1RoundedRectangleGeometry creation");
        }
    }

    public ProGpuDirect2DComReference CreateEllipseGeometry(
        Vector2 center,
        float radiusX,
        float radiusY)
    {
        ValidatePoint(center, nameof(center));
        ValidateRadii(radiusX, radiusY);
        ProGpuDirect2DNative.NativePoint2F nativeCenter =
            CreateNativePoint(center);
        lock (_gate)
        {
            ThrowIfUnavailable();
            nint value = 0;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceCreateEllipseGeometry(
                    _nativeSurface,
                    &nativeCenter,
                    radiusX,
                    radiusY,
                    &value,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1EllipseGeometry creation",
                status,
                nativeHResult);
            return CreateRequiredComReference(
                value,
                ProGpuDirect2DInterfaceKind.D2D1EllipseGeometry,
                "ID2D1EllipseGeometry creation");
        }
    }

    public ProGpuDirect2DComReference CreatePathGeometry(
        ProGpuDirect2DFillMode fillMode,
        ReadOnlySpan<ProGpuDirect2DPathFigure> figures,
        ReadOnlySpan<ProGpuDirect2DPathSegment> segments)
    {
        if (fillMode is not ProGpuDirect2DFillMode.Alternate and
            not ProGpuDirect2DFillMode.Winding)
        {
            throw new ArgumentOutOfRangeException(nameof(fillMode));
        }
        lock (_gate)
        {
            ThrowIfUnavailable();
            fixed (ProGpuDirect2DPathFigure* figurePointer = figures)
            fixed (ProGpuDirect2DPathSegment* segmentPointer = segments)
            {
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceCreatePathGeometry(
                        _nativeSurface,
                        fillMode,
                        figurePointer,
                        checked((uint)figures.Length),
                        segmentPointer,
                        checked((uint)segments.Length),
                        &value,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1PathGeometry1 creation",
                    status,
                    nativeHResult);
                return CreateRequiredComReference(
                    value,
                    ProGpuDirect2DInterfaceKind.D2D1PathGeometry1,
                    "ID2D1PathGeometry1 creation");
            }
        }
    }

    public ProGpuDirect2DComReference CreateTransformedGeometry(
        ProGpuDirect2DComReference geometry,
        Matrix3x2 transform)
    {
        ValidateGeometry(geometry, nameof(geometry));
        ProGpuDirect2DNative.NativeMatrix3X2F nativeTransform =
            CreateNativeMatrix(transform);
        bool referenceAdded = false;
        try
        {
            geometry.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceCreateTransformedGeometry(
                        _nativeSurface,
                        geometry.DangerousGetHandle(),
                        &nativeTransform,
                        &value,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1TransformedGeometry creation",
                    status,
                    nativeHResult);
                return CreateRequiredComReference(
                    value,
                    ProGpuDirect2DInterfaceKind.D2D1TransformedGeometry,
                    "ID2D1TransformedGeometry creation");
            }
        }
        finally
        {
            if (referenceAdded)
            {
                geometry.DangerousRelease();
            }
        }
    }

    public ProGpuDirect2DComReference CombineGeometry(
        ProGpuDirect2DComReference geometryA,
        ProGpuDirect2DComReference geometryB,
        ProGpuDirect2DCombineMode combineMode,
        Matrix3x2? geometryBTransform = null,
        float flatteningTolerance = 0.25F)
    {
        ValidateGeometry(geometryA, nameof(geometryA));
        ValidateGeometry(geometryB, nameof(geometryB));
        if (combineMode < ProGpuDirect2DCombineMode.Union ||
            combineMode > ProGpuDirect2DCombineMode.Exclude)
        {
            throw new ArgumentOutOfRangeException(nameof(combineMode));
        }
        if (!float.IsFinite(flatteningTolerance) ||
            flatteningTolerance <= 0.0F)
        {
            throw new ArgumentOutOfRangeException(
                nameof(flatteningTolerance));
        }
        ProGpuDirect2DNative.NativeMatrix3X2F nativeTransform = default;
        ProGpuDirect2DNative.NativeMatrix3X2F* transformPointer = null;
        if (geometryBTransform is Matrix3x2 transform)
        {
            nativeTransform = CreateNativeMatrix(transform);
            transformPointer = &nativeTransform;
        }

        bool firstReferenceAdded = false;
        bool secondReferenceAdded = false;
        try
        {
            geometryA.DangerousAddRef(ref firstReferenceAdded);
            geometryB.DangerousAddRef(ref secondReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceCombineGeometry(
                        _nativeSurface,
                        geometryA.DangerousGetHandle(),
                        geometryB.DangerousGetHandle(),
                        combineMode,
                        transformPointer,
                        flatteningTolerance,
                        &value,
                        &nativeHResult);
                ThrowIfFailed(
                    "Direct2D geometry combination",
                    status,
                    nativeHResult);
                return CreateRequiredComReference(
                    value,
                    ProGpuDirect2DInterfaceKind.D2D1PathGeometry1,
                    "Direct2D geometry combination");
            }
        }
        finally
        {
            if (secondReferenceAdded)
            {
                geometryB.DangerousRelease();
            }
            if (firstReferenceAdded)
            {
                geometryA.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Returns ID2D1Geometry bounds in this surface's resource domain.
    /// </summary>
    public ProGpuDirect2DRect GetGeometryBounds(
        ProGpuDirect2DComReference geometry,
        Matrix3x2? transform = null)
    {
        ValidateGeometry(geometry, nameof(geometry));
        ProGpuDirect2DNative.NativeMatrix3X2F nativeTransform = default;
        ProGpuDirect2DNative.NativeMatrix3X2F* transformPointer = null;
        if (transform is Matrix3x2 value)
        {
            nativeTransform = CreateNativeMatrix(value);
            transformPointer = &nativeTransform;
        }
        bool referenceAdded = false;
        try
        {
            geometry.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                ProGpuDirect2DRect bounds = default;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.GeometryGetBounds(
                        _nativeSurface,
                        geometry.DangerousGetHandle(),
                        transformPointer,
                        &bounds,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1Geometry::GetBounds",
                    status,
                    nativeHResult);
                return bounds;
            }
        }
        finally
        {
            if (referenceAdded)
            {
                geometry.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Returns ID2D1Geometry widened bounds using an optional genuine
    /// ID2D1StrokeStyle1 from the same resource generation.
    /// </summary>
    public ProGpuDirect2DRect GetGeometryWidenedBounds(
        ProGpuDirect2DComReference geometry,
        float strokeWidth,
        ProGpuDirect2DComReference? strokeStyle = null,
        Matrix3x2? transform = null,
        float flatteningTolerance = 0.25F)
    {
        ValidateGeometry(geometry, nameof(geometry));
        ValidateOptionalStrokeStyle(strokeStyle, nameof(strokeStyle));
        ValidateStrokeWidth(strokeWidth, nameof(strokeWidth));
        ValidateFlatteningTolerance(
            flatteningTolerance,
            nameof(flatteningTolerance));
        ProGpuDirect2DNative.NativeMatrix3X2F nativeTransform = default;
        ProGpuDirect2DNative.NativeMatrix3X2F* transformPointer = null;
        if (transform is Matrix3x2 value)
        {
            nativeTransform = CreateNativeMatrix(value);
            transformPointer = &nativeTransform;
        }
        bool geometryReferenceAdded = false;
        bool styleReferenceAdded = false;
        try
        {
            geometry.DangerousAddRef(ref geometryReferenceAdded);
            strokeStyle?.DangerousAddRef(ref styleReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                ProGpuDirect2DRect bounds = default;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.GeometryGetWidenedBounds(
                        _nativeSurface,
                        geometry.DangerousGetHandle(),
                        strokeWidth,
                        strokeStyle?.DangerousGetHandle() ?? 0,
                        transformPointer,
                        flatteningTolerance,
                        &bounds,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1Geometry::GetWidenedBounds",
                    status,
                    nativeHResult);
                return bounds;
            }
        }
        finally
        {
            if (styleReferenceAdded)
            {
                strokeStyle!.DangerousRelease();
            }
            if (geometryReferenceAdded)
            {
                geometry.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Performs exact native ID2D1Geometry fill hit testing.
    /// </summary>
    public bool GeometryFillContainsPoint(
        ProGpuDirect2DComReference geometry,
        Vector2 point,
        Matrix3x2? transform = null,
        float flatteningTolerance = 0.25F)
    {
        ValidateGeometry(geometry, nameof(geometry));
        ValidatePoint(point, nameof(point));
        ValidateFlatteningTolerance(
            flatteningTolerance,
            nameof(flatteningTolerance));
        ProGpuDirect2DNative.NativePoint2F nativePoint =
            CreateNativePoint(point);
        ProGpuDirect2DNative.NativeMatrix3X2F nativeTransform = default;
        ProGpuDirect2DNative.NativeMatrix3X2F* transformPointer = null;
        if (transform is Matrix3x2 value)
        {
            nativeTransform = CreateNativeMatrix(value);
            transformPointer = &nativeTransform;
        }
        bool referenceAdded = false;
        try
        {
            geometry.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                uint contains = 0U;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.GeometryFillContainsPoint(
                        _nativeSurface,
                        geometry.DangerousGetHandle(),
                        &nativePoint,
                        transformPointer,
                        flatteningTolerance,
                        &contains,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1Geometry::FillContainsPoint",
                    status,
                    nativeHResult);
                return contains != 0U;
            }
        }
        finally
        {
            if (referenceAdded)
            {
                geometry.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Performs exact native ID2D1Geometry stroke hit testing using an
    /// optional genuine ID2D1StrokeStyle1 from the same resource generation.
    /// </summary>
    public bool GeometryStrokeContainsPoint(
        ProGpuDirect2DComReference geometry,
        Vector2 point,
        float strokeWidth,
        ProGpuDirect2DComReference? strokeStyle = null,
        Matrix3x2? transform = null,
        float flatteningTolerance = 0.25F)
    {
        ValidateGeometry(geometry, nameof(geometry));
        ValidateOptionalStrokeStyle(strokeStyle, nameof(strokeStyle));
        ValidatePoint(point, nameof(point));
        ValidateStrokeWidth(strokeWidth, nameof(strokeWidth));
        ValidateFlatteningTolerance(
            flatteningTolerance,
            nameof(flatteningTolerance));
        ProGpuDirect2DNative.NativePoint2F nativePoint =
            CreateNativePoint(point);
        ProGpuDirect2DNative.NativeMatrix3X2F nativeTransform = default;
        ProGpuDirect2DNative.NativeMatrix3X2F* transformPointer = null;
        if (transform is Matrix3x2 value)
        {
            nativeTransform = CreateNativeMatrix(value);
            transformPointer = &nativeTransform;
        }
        bool geometryReferenceAdded = false;
        bool styleReferenceAdded = false;
        try
        {
            geometry.DangerousAddRef(ref geometryReferenceAdded);
            strokeStyle?.DangerousAddRef(ref styleReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                uint contains = 0U;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.GeometryStrokeContainsPoint(
                        _nativeSurface,
                        geometry.DangerousGetHandle(),
                        &nativePoint,
                        strokeWidth,
                        strokeStyle?.DangerousGetHandle() ?? 0,
                        transformPointer,
                        flatteningTolerance,
                        &contains,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1Geometry::StrokeContainsPoint",
                    status,
                    nativeHResult);
                return contains != 0U;
            }
        }
        finally
        {
            if (styleReferenceAdded)
            {
                strokeStyle!.DangerousRelease();
            }
            if (geometryReferenceAdded)
            {
                geometry.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Compares two genuine ID2D1Geometry resources in the same native
    /// factory and resource generation.
    /// </summary>
    public ProGpuDirect2DGeometryRelation CompareGeometry(
        ProGpuDirect2DComReference geometry,
        ProGpuDirect2DComReference inputGeometry,
        Matrix3x2? inputTransform = null,
        float flatteningTolerance = 0.25F)
    {
        ValidateGeometry(geometry, nameof(geometry));
        ValidateGeometry(inputGeometry, nameof(inputGeometry));
        ValidateFlatteningTolerance(
            flatteningTolerance,
            nameof(flatteningTolerance));
        ProGpuDirect2DNative.NativeMatrix3X2F nativeTransform = default;
        ProGpuDirect2DNative.NativeMatrix3X2F* transformPointer = null;
        if (inputTransform is Matrix3x2 value)
        {
            nativeTransform = CreateNativeMatrix(value);
            transformPointer = &nativeTransform;
        }
        bool geometryReferenceAdded = false;
        bool inputReferenceAdded = false;
        try
        {
            geometry.DangerousAddRef(ref geometryReferenceAdded);
            inputGeometry.DangerousAddRef(ref inputReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                ProGpuDirect2DGeometryRelation relation =
                    ProGpuDirect2DGeometryRelation.Unknown;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.GeometryCompare(
                        _nativeSurface,
                        geometry.DangerousGetHandle(),
                        inputGeometry.DangerousGetHandle(),
                        transformPointer,
                        flatteningTolerance,
                        &relation,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1Geometry::CompareWithGeometry",
                    status,
                    nativeHResult);
                return relation;
            }
        }
        finally
        {
            if (inputReferenceAdded)
            {
                inputGeometry.DangerousRelease();
            }
            if (geometryReferenceAdded)
            {
                geometry.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Computes native ID2D1Geometry area without CPU geometry replay.
    /// </summary>
    public float ComputeGeometryArea(
        ProGpuDirect2DComReference geometry,
        Matrix3x2? transform = null,
        float flatteningTolerance = 0.25F) =>
        ComputeGeometryScalar(
            geometry,
            transform,
            flatteningTolerance,
            computeLength: false);

    /// <summary>
    /// Computes native ID2D1Geometry length without CPU geometry replay.
    /// </summary>
    public float ComputeGeometryLength(
        ProGpuDirect2DComReference geometry,
        Matrix3x2? transform = null,
        float flatteningTolerance = 0.25F) =>
        ComputeGeometryScalar(
            geometry,
            transform,
            flatteningTolerance,
            computeLength: true);

    /// <summary>
    /// Samples a point and unit tangent from a genuine ID2D1Geometry.
    /// </summary>
    public ProGpuDirect2DPointAndTangent ComputeGeometryPointAtLength(
        ProGpuDirect2DComReference geometry,
        float length,
        Matrix3x2? transform = null,
        float flatteningTolerance = 0.25F)
    {
        ValidateGeometry(geometry, nameof(geometry));
        if (!float.IsFinite(length) || length < 0.0F)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
        ValidateFlatteningTolerance(
            flatteningTolerance,
            nameof(flatteningTolerance));
        ProGpuDirect2DNative.NativeMatrix3X2F nativeTransform = default;
        ProGpuDirect2DNative.NativeMatrix3X2F* transformPointer = null;
        if (transform is Matrix3x2 value)
        {
            nativeTransform = CreateNativeMatrix(value);
            transformPointer = &nativeTransform;
        }
        bool referenceAdded = false;
        try
        {
            geometry.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                ProGpuDirect2DNative.NativePoint2F point = default;
                ProGpuDirect2DNative.NativePoint2F tangent = default;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.GeometryComputePointAtLength(
                        _nativeSurface,
                        geometry.DangerousGetHandle(),
                        length,
                        transformPointer,
                        flatteningTolerance,
                        &point,
                        &tangent,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1Geometry::ComputePointAtLength",
                    status,
                    nativeHResult);
                return new ProGpuDirect2DPointAndTangent(
                    new Vector2(point.X, point.Y),
                    new Vector2(tangent.X, tangent.Y));
            }
        }
        finally
        {
            if (referenceAdded)
            {
                geometry.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Materializes ID2D1Geometry::Simplify into a genuine provider-owned
    /// ID2D1PathGeometry1 without exposing a caller COM sink.
    /// </summary>
    public ProGpuDirect2DComReference SimplifyGeometry(
        ProGpuDirect2DComReference geometry,
        ProGpuDirect2DGeometrySimplificationOption option =
            ProGpuDirect2DGeometrySimplificationOption.CubicsAndLines,
        Matrix3x2? transform = null,
        float flatteningTolerance = 0.25F)
    {
        if (option < ProGpuDirect2DGeometrySimplificationOption.CubicsAndLines ||
            option > ProGpuDirect2DGeometrySimplificationOption.Lines)
        {
            throw new ArgumentOutOfRangeException(nameof(option));
        }
        return CreateDerivedGeometry(
            GeometryDerivation.Simplify,
            geometry,
            option,
            0.0F,
            null,
            transform,
            flatteningTolerance);
    }

    /// <summary>
    /// Materializes ID2D1Geometry::Outline into a genuine provider-owned
    /// ID2D1PathGeometry1 without exposing a caller COM sink.
    /// </summary>
    public ProGpuDirect2DComReference OutlineGeometry(
        ProGpuDirect2DComReference geometry,
        Matrix3x2? transform = null,
        float flatteningTolerance = 0.25F) =>
        CreateDerivedGeometry(
            GeometryDerivation.Outline,
            geometry,
            default,
            0.0F,
            null,
            transform,
            flatteningTolerance);

    /// <summary>
    /// Materializes ID2D1Geometry::Widen into a genuine provider-owned
    /// ID2D1PathGeometry1 without exposing a caller COM sink.
    /// </summary>
    public ProGpuDirect2DComReference WidenGeometry(
        ProGpuDirect2DComReference geometry,
        float strokeWidth,
        ProGpuDirect2DComReference? strokeStyle = null,
        Matrix3x2? transform = null,
        float flatteningTolerance = 0.25F) =>
        CreateDerivedGeometry(
            GeometryDerivation.Widen,
            geometry,
            default,
            strokeWidth,
            strokeStyle,
            transform,
            flatteningTolerance);

    /// <summary>
    /// Tessellates directly into caller-owned storage. Returns false and the
    /// required count when the destination is too short; the immutable
    /// geometry can then be submitted again with an adequately sized span.
    /// </summary>
    public bool TryTessellateGeometry(
        ProGpuDirect2DComReference geometry,
        Span<ProGpuDirect2DTriangle> triangles,
        out uint requiredTriangleCount,
        Matrix3x2? transform = null,
        float flatteningTolerance = 0.25F)
    {
        ValidateGeometry(geometry, nameof(geometry));
        ValidateFlatteningTolerance(
            flatteningTolerance,
            nameof(flatteningTolerance));
        ProGpuDirect2DNative.NativeMatrix3X2F nativeTransform = default;
        ProGpuDirect2DNative.NativeMatrix3X2F* transformPointer = null;
        if (transform is Matrix3x2 value)
        {
            nativeTransform = CreateNativeMatrix(value);
            transformPointer = &nativeTransform;
        }
        bool referenceAdded = false;
        try
        {
            geometry.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                fixed (ProGpuDirect2DTriangle* trianglePointer = triangles)
                {
                    uint nativeTriangleCount = 0U;
                    int nativeHResult = 0;
                    ProGpuDirect2DStatus status =
                        ProGpuDirect2DNative.GeometryTessellate(
                            _nativeSurface,
                            geometry.DangerousGetHandle(),
                            transformPointer,
                            flatteningTolerance,
                            trianglePointer,
                            checked((uint)triangles.Length),
                            &nativeTriangleCount,
                            &nativeHResult);
                    requiredTriangleCount = nativeTriangleCount;
                    if (status == ProGpuDirect2DStatus.InsufficientBuffer)
                    {
                        return false;
                    }
                    ThrowIfFailed(
                        "ID2D1Geometry::Tessellate",
                        status,
                        nativeHResult);
                    if (nativeTriangleCount > (uint)triangles.Length)
                    {
                        throw new InvalidOperationException(
                            "Direct2D tessellation succeeded beyond the caller span.");
                    }
                    return true;
                }
            }
        }
        finally
        {
            if (referenceAdded)
            {
                geometry.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Creates a cached filled ID2D1GeometryRealization in this device domain.
    /// </summary>
    public ProGpuDirect2DComReference CreateFilledGeometryRealization(
        ProGpuDirect2DComReference geometry,
        float flatteningTolerance = 0.25F) =>
        CreateGeometryRealization(
            geometry,
            flatteningTolerance,
            0.0F,
            null,
            stroked: false);

    /// <summary>
    /// Creates a cached stroked ID2D1GeometryRealization in this device domain.
    /// </summary>
    public ProGpuDirect2DComReference CreateStrokedGeometryRealization(
        ProGpuDirect2DComReference geometry,
        float strokeWidth,
        ProGpuDirect2DComReference? strokeStyle = null,
        float flatteningTolerance = 0.25F) =>
        CreateGeometryRealization(
            geometry,
            flatteningTolerance,
            strokeWidth,
            strokeStyle,
            stroked: true);

    /// <summary>
    /// Creates a genuine factory-domain ID2D1StrokeStyle1. A custom dash
    /// pattern is pinned and submitted as one contiguous span; Direct2D owns
    /// its copied style state after this method returns.
    /// </summary>
    public ProGpuDirect2DComReference CreateStrokeStyle(
        ProGpuDirect2DStrokeStyleProperties properties,
        ReadOnlySpan<float> customDashes = default)
    {
        ValidateStrokeStyle(properties, customDashes);
        lock (_gate)
        {
            ThrowIfUnavailable();
            fixed (float* dashPointer = customDashes)
            {
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceCreateStrokeStyle(
                        _nativeSurface,
                        &properties,
                        dashPointer,
                        checked((uint)customDashes.Length),
                        &value,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1StrokeStyle1 creation",
                    status,
                    nativeHResult);
                return CreateRequiredComReference(
                    value,
                    ProGpuDirect2DInterfaceKind.D2D1StrokeStyle1,
                    "ID2D1StrokeStyle1 creation");
            }
        }
    }

    /// <summary>
    /// Creates a genuine Direct2D geometry from the neutral primitive contract
    /// already published by source-built WPF.
    /// </summary>
    public ProGpuDirect2DComReference CreateGeometry(
        PortablePrimitiveGeometry geometry)
    {
        ProGpuDirect2DComReference result;
        switch (geometry.Kind)
        {
            case PortablePrimitiveGeometryKind.Line:
            {
                Span<ProGpuDirect2DPathFigure> figures =
                    stackalloc ProGpuDirect2DPathFigure[1]
                    {
                        new(
                            ConvertPoint(geometry.Point1),
                            0U,
                            1U,
                            ProGpuDirect2DPathFigureFlags.None)
                    };
                Span<ProGpuDirect2DPathSegment> segments =
                    stackalloc ProGpuDirect2DPathSegment[1]
                    {
                        ProGpuDirect2DPathSegment.Line(
                            ConvertPoint(geometry.Point2))
                    };
                result = CreatePathGeometry(
                    ProGpuDirect2DFillMode.Winding,
                    figures,
                    segments);
                break;
            }
            case PortablePrimitiveGeometryKind.Rectangle:
            {
                ProGpuDirect2DRect rectangle = ConvertRect(geometry.Rect);
                float radiusX = ConvertFiniteFloat(
                    geometry.RadiusX,
                    nameof(geometry));
                float radiusY = ConvertFiniteFloat(
                    geometry.RadiusY,
                    nameof(geometry));
                result = radiusX == 0.0F && radiusY == 0.0F
                    ? CreateRectangleGeometry(rectangle)
                    : CreateRoundedRectangleGeometry(
                        rectangle,
                        radiusX,
                        radiusY);
                break;
            }
            case PortablePrimitiveGeometryKind.Ellipse:
                result = CreateEllipseGeometry(
                    ConvertPoint(geometry.Point1),
                    ConvertFiniteFloat(geometry.RadiusX, nameof(geometry)),
                    ConvertFiniteFloat(geometry.RadiusY, nameof(geometry)));
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(geometry),
                    "The portable primitive geometry kind is unknown.");
        }
        return ApplyPortableTransform(result, geometry.Transform);
    }

    /// <summary>
    /// Creates path, transformed, and boolean-combined genuine Direct2D
    /// geometries from the same typed DTO consumed by LibreWPF retained replay.
    /// </summary>
    public ProGpuDirect2DComReference CreateGeometry(
        PortableGeometryPath geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.Kind == PortableGeometryPathKind.Combined)
        {
            if (geometry.PathA is null || geometry.PathB is null ||
                geometry.CombineOperation < 0 ||
                geometry.CombineOperation > 3)
            {
                throw new ArgumentException(
                    "A combined portable geometry requires two paths and a valid combine operation.",
                    nameof(geometry));
            }
            using ProGpuDirect2DComReference pathA =
                CreateGeometry(geometry.PathA);
            using ProGpuDirect2DComReference pathB =
                CreateGeometry(geometry.PathB);
            ProGpuDirect2DComReference combined = CombineGeometry(
                pathA,
                pathB,
                (ProGpuDirect2DCombineMode)geometry.CombineOperation);
            return ApplyPortableTransform(combined, geometry.Transform);
        }
        if (geometry.Kind != PortableGeometryPathKind.Path)
        {
            throw new ArgumentOutOfRangeException(nameof(geometry));
        }
        if (geometry.FillRule is not PortableFillRule.EvenOdd and
            not PortableFillRule.Nonzero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(geometry),
                "The portable geometry fill rule is unknown.");
        }

        PortablePathFigure[] sourceFigures =
            geometry.Figures ?? Array.Empty<PortablePathFigure>();
        int segmentCount = 0;
        foreach (PortablePathFigure figure in sourceFigures)
        {
            ArgumentNullException.ThrowIfNull(figure);
            checked
            {
                segmentCount += figure.Segments?.Length ?? 0;
            }
        }

        ProGpuDirect2DPathFigure[]? rentedFigures = null;
        ProGpuDirect2DPathSegment[]? rentedSegments = null;
        Span<ProGpuDirect2DPathFigure> figures =
            sourceFigures.Length <= 32
                ? stackalloc ProGpuDirect2DPathFigure[sourceFigures.Length]
                : (rentedFigures = ArrayPool<ProGpuDirect2DPathFigure>
                    .Shared.Rent(sourceFigures.Length))
                    .AsSpan(0, sourceFigures.Length);
        Span<ProGpuDirect2DPathSegment> segments =
            segmentCount <= 128
                ? stackalloc ProGpuDirect2DPathSegment[segmentCount]
                : (rentedSegments = ArrayPool<ProGpuDirect2DPathSegment>
                    .Shared.Rent(segmentCount))
                    .AsSpan(0, segmentCount);
        try
        {
            int segmentOffset = 0;
            for (int figureIndex = 0;
                 figureIndex < sourceFigures.Length;
                 ++figureIndex)
            {
                PortablePathFigure sourceFigure = sourceFigures[figureIndex];
                PortablePathSegment[] sourceSegments =
                    sourceFigure.Segments ?? Array.Empty<PortablePathSegment>();
                ProGpuDirect2DPathFigureFlags figureFlags =
                    (sourceFigure.IsFilled
                        ? ProGpuDirect2DPathFigureFlags.Filled
                        : ProGpuDirect2DPathFigureFlags.None) |
                    (sourceFigure.IsClosed
                        ? ProGpuDirect2DPathFigureFlags.Closed
                        : ProGpuDirect2DPathFigureFlags.None);
                figures[figureIndex] = new ProGpuDirect2DPathFigure(
                    ConvertPoint(sourceFigure.StartPoint),
                    checked((uint)segmentOffset),
                    checked((uint)sourceSegments.Length),
                    figureFlags);
                for (int index = 0; index < sourceSegments.Length; ++index)
                {
                    segments[segmentOffset++] =
                        ConvertSegment(sourceSegments[index]);
                }
            }
            ProGpuDirect2DComReference path = CreatePathGeometry(
                geometry.FillRule == PortableFillRule.EvenOdd
                    ? ProGpuDirect2DFillMode.Alternate
                    : ProGpuDirect2DFillMode.Winding,
                figures,
                segments);
            return ApplyPortableTransform(path, geometry.Transform);
        }
        finally
        {
            if (rentedSegments is not null)
            {
                ArrayPool<ProGpuDirect2DPathSegment>.Shared.Return(
                    rentedSegments,
                    clearArray: false);
            }
            if (rentedFigures is not null)
            {
                ArrayPool<ProGpuDirect2DPathFigure>.Shared.Return(
                    rentedFigures,
                    clearArray: false);
            }
        }
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

    public bool TryAcquireMicrosoftWin2DGeometry(
        ProGpuDirect2DComReference nativeGeometry,
        out ProGpuDirect2DComReference? canvasGeometry,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapper(
            nativeGeometry,
            ProGpuDirect2DInterfaceKind.D2D1Geometry,
            ProGpuDirect2DInterfaceKind.Win2DCanvasGeometry,
            "Microsoft Win2D CanvasGeometry wrapping",
            out canvasGeometry,
            out nativeHResult);

    public bool TryAcquireMicrosoftWin2DNativeGeometry(
        ProGpuDirect2DComReference canvasGeometry,
        out ProGpuDirect2DComReference? nativeGeometry,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapperNativeResource(
            canvasGeometry,
            ProGpuDirect2DInterfaceKind.Win2DCanvasGeometry,
            D2D1GeometryInterfaceId,
            ProGpuDirect2DInterfaceKind.D2D1Geometry,
            "Microsoft Win2D CanvasGeometry native-resource query",
            out nativeGeometry,
            out nativeHResult);

    public bool TryAcquireMicrosoftWin2DStrokeStyle(
        ProGpuDirect2DComReference nativeStrokeStyle,
        out ProGpuDirect2DComReference? canvasStrokeStyle,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapper(
            nativeStrokeStyle,
            ProGpuDirect2DInterfaceKind.D2D1StrokeStyle1,
            ProGpuDirect2DInterfaceKind.Win2DCanvasStrokeStyle,
            "Microsoft Win2D CanvasStrokeStyle wrapping",
            out canvasStrokeStyle,
            out nativeHResult);

    public bool TryAcquireMicrosoftWin2DNativeStrokeStyle(
        ProGpuDirect2DComReference canvasStrokeStyle,
        out ProGpuDirect2DComReference? nativeStrokeStyle,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapperNativeResource(
            canvasStrokeStyle,
            ProGpuDirect2DInterfaceKind.Win2DCanvasStrokeStyle,
            D2D1StrokeStyle1InterfaceId,
            ProGpuDirect2DInterfaceKind.D2D1StrokeStyle1,
            "Microsoft Win2D CanvasStrokeStyle native-resource query",
            out nativeStrokeStyle,
            out nativeHResult);

    public bool TryAcquireMicrosoftWin2DBitmap(
        ProGpuDirect2DComReference nativeBitmap,
        out ProGpuDirect2DComReference? canvasBitmap,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapper(
            nativeBitmap,
            ProGpuDirect2DInterfaceKind.D2D1Bitmap1,
            ProGpuDirect2DInterfaceKind.Win2DCanvasBitmap,
            "Microsoft Win2D CanvasBitmap wrapping",
            out canvasBitmap,
            out nativeHResult);

    public bool TryAcquireMicrosoftWin2DNativeBitmap(
        ProGpuDirect2DComReference canvasBitmap,
        out ProGpuDirect2DComReference? nativeBitmap,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapperNativeResource(
            canvasBitmap,
            ProGpuDirect2DInterfaceKind.Win2DCanvasBitmap,
            D2D1Bitmap1InterfaceId,
            ProGpuDirect2DInterfaceKind.D2D1Bitmap1,
            "Microsoft Win2D CanvasBitmap native-resource query",
            out nativeBitmap,
            out nativeHResult);

    public bool TryAcquireMicrosoftWin2DImageBrush(
        ProGpuDirect2DComReference nativeBrush,
        out ProGpuDirect2DComReference? canvasBrush,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapper(
            nativeBrush,
            ProGpuDirect2DInterfaceKind.D2D1ImageBrush,
            ProGpuDirect2DInterfaceKind.Win2DCanvasImageBrush,
            "Microsoft Win2D CanvasImageBrush wrapping",
            out canvasBrush,
            out nativeHResult);

    public bool TryAcquireMicrosoftWin2DNativeImageBrush(
        ProGpuDirect2DComReference canvasBrush,
        out ProGpuDirect2DComReference? nativeBrush,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DNativeImageBrush(
            canvasBrush,
            ProGpuDirect2DInterfaceKind.D2D1BitmapBrush1,
            out nativeBrush,
            out nativeHResult);

    /// <summary>
    /// Reverse-unwraps a CanvasImageBrush as the explicitly requested native
    /// Direct2D brush kind. Win2D uses ID2D1BitmapBrush1 for a bitmap without a
    /// source rectangle and ID2D1ImageBrush when a source rectangle or general
    /// image is present.
    /// </summary>
    public bool TryAcquireMicrosoftWin2DNativeImageBrush(
        ProGpuDirect2DComReference canvasBrush,
        ProGpuDirect2DInterfaceKind nativeBrushKind,
        out ProGpuDirect2DComReference? nativeBrush,
        out int nativeHResult)
    {
        Guid interfaceId = nativeBrushKind switch
        {
            ProGpuDirect2DInterfaceKind.D2D1BitmapBrush1 =>
                D2D1BitmapBrush1InterfaceId,
            ProGpuDirect2DInterfaceKind.D2D1ImageBrush =>
                D2D1ImageBrushInterfaceId,
            _ => throw new ArgumentOutOfRangeException(
                nameof(nativeBrushKind),
                "CanvasImageBrush can be queried only as ID2D1BitmapBrush1 or ID2D1ImageBrush.")
        };
        return TryAcquireMicrosoftWin2DWrapperNativeResource(
            canvasBrush,
            ProGpuDirect2DInterfaceKind.Win2DCanvasImageBrush,
            interfaceId,
            nativeBrushKind,
            "Microsoft Win2D CanvasImageBrush native-resource query",
            out nativeBrush,
            out nativeHResult);
    }

    public bool TryAcquireMicrosoftWin2DCommandList(
        ProGpuDirect2DComReference nativeCommandList,
        out ProGpuDirect2DComReference? canvasCommandList,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapper(
            nativeCommandList,
            ProGpuDirect2DInterfaceKind.D2D1CommandList,
            ProGpuDirect2DInterfaceKind.Win2DCanvasCommandList,
            "Microsoft Win2D CanvasCommandList wrapping",
            out canvasCommandList,
            out nativeHResult);

    public bool TryAcquireMicrosoftWin2DNativeCommandList(
        ProGpuDirect2DComReference canvasCommandList,
        out ProGpuDirect2DComReference? nativeCommandList,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapperNativeResource(
            canvasCommandList,
            ProGpuDirect2DInterfaceKind.Win2DCanvasCommandList,
            D2D1CommandListInterfaceId,
            ProGpuDirect2DInterfaceKind.D2D1CommandList,
            "Microsoft Win2D CanvasCommandList native-resource query",
            out nativeCommandList,
            out nativeHResult);

    /// <summary>
    /// Wraps a provider-created IDWriteTextFormat1 as a genuine Microsoft
    /// Win2D CanvasTextFormat. Text formats are device-independent, so this
    /// path deliberately supplies no CanvasDevice and zero DPI.
    /// </summary>
    public bool TryAcquireMicrosoftWin2DTextFormat(
        ProGpuDirect2DComReference nativeTextFormat,
        out ProGpuDirect2DComReference? canvasTextFormat,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapper(
            nativeTextFormat,
            ProGpuDirect2DInterfaceKind.DWriteTextFormat1,
            ProGpuDirect2DInterfaceKind.Win2DCanvasTextFormat,
            "Microsoft Win2D CanvasTextFormat wrapping",
            out canvasTextFormat,
            out nativeHResult);

    /// <summary>
    /// Reverse-unwraps a genuine Microsoft Win2D CanvasTextFormat and returns
    /// its exact IDWriteTextFormat1 with one caller-owned COM reference.
    /// </summary>
    public bool TryAcquireMicrosoftWin2DNativeTextFormat(
        ProGpuDirect2DComReference canvasTextFormat,
        out ProGpuDirect2DComReference? nativeTextFormat,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapperNativeResource(
            canvasTextFormat,
            ProGpuDirect2DInterfaceKind.Win2DCanvasTextFormat,
            DWriteTextFormat1InterfaceId,
            ProGpuDirect2DInterfaceKind.DWriteTextFormat1,
            "Microsoft Win2D CanvasTextFormat native-resource query",
            out nativeTextFormat,
            out nativeHResult);

    /// <summary>
    /// Wraps a provider-created IDWriteTextLayout4 as a genuine Microsoft
    /// Win2D CanvasTextLayout in this surface's exact CanvasDevice domain.
    /// </summary>
    public bool TryAcquireMicrosoftWin2DTextLayout(
        ProGpuDirect2DComReference nativeTextLayout,
        out ProGpuDirect2DComReference? canvasTextLayout,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapper(
            nativeTextLayout,
            ProGpuDirect2DInterfaceKind.DWriteTextLayout4,
            ProGpuDirect2DInterfaceKind.Win2DCanvasTextLayout,
            "Microsoft Win2D CanvasTextLayout wrapping",
            out canvasTextLayout,
            out nativeHResult);

    /// <summary>
    /// Reverse-unwraps a genuine Microsoft Win2D CanvasTextLayout and returns
    /// its exact IDWriteTextLayout4 with one caller-owned COM reference.
    /// </summary>
    public bool TryAcquireMicrosoftWin2DNativeTextLayout(
        ProGpuDirect2DComReference canvasTextLayout,
        out ProGpuDirect2DComReference? nativeTextLayout,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapperNativeResource(
            canvasTextLayout,
            ProGpuDirect2DInterfaceKind.Win2DCanvasTextLayout,
            DWriteTextLayout4InterfaceId,
            ProGpuDirect2DInterfaceKind.DWriteTextLayout4,
            "Microsoft Win2D CanvasTextLayout native-resource query",
            out nativeTextLayout,
            out nativeHResult);

    /// <summary>
    /// Wraps a provider-created device-independent IDWriteTypography as a
    /// genuine Microsoft Win2D CanvasTypography.
    /// </summary>
    public bool TryAcquireMicrosoftWin2DTypography(
        ProGpuDirect2DComReference nativeTypography,
        out ProGpuDirect2DComReference? canvasTypography,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapper(
            nativeTypography,
            ProGpuDirect2DInterfaceKind.DWriteTypography,
            ProGpuDirect2DInterfaceKind.Win2DCanvasTypography,
            "Microsoft Win2D CanvasTypography wrapping",
            out canvasTypography,
            out nativeHResult);

    /// <summary>
    /// Reverse-unwraps a genuine Microsoft Win2D CanvasTypography and returns
    /// its exact IDWriteTypography with one caller-owned COM reference.
    /// </summary>
    public bool TryAcquireMicrosoftWin2DNativeTypography(
        ProGpuDirect2DComReference canvasTypography,
        out ProGpuDirect2DComReference? nativeTypography,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapperNativeResource(
            canvasTypography,
            ProGpuDirect2DInterfaceKind.Win2DCanvasTypography,
            DWriteTypographyInterfaceId,
            ProGpuDirect2DInterfaceKind.DWriteTypography,
            "Microsoft Win2D CanvasTypography native-resource query",
            out nativeTypography,
            out nativeHResult);

    /// <summary>
    /// Wraps a provider-created device-independent IDWriteFontFaceReference as
    /// a genuine Microsoft Win2D CanvasFontFace.
    /// </summary>
    public bool TryAcquireMicrosoftWin2DFontFace(
        ProGpuDirect2DComReference nativeFontFaceReference,
        out ProGpuDirect2DComReference? canvasFontFace,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapper(
            nativeFontFaceReference,
            ProGpuDirect2DInterfaceKind.DWriteFontFaceReference,
            ProGpuDirect2DInterfaceKind.Win2DCanvasFontFace,
            "Microsoft Win2D CanvasFontFace wrapping",
            out canvasFontFace,
            out nativeHResult);

    /// <summary>
    /// Reverse-unwraps a genuine Microsoft Win2D CanvasFontFace and returns
    /// its exact IDWriteFontFaceReference with one caller-owned COM reference.
    /// </summary>
    public bool TryAcquireMicrosoftWin2DNativeFontFaceReference(
        ProGpuDirect2DComReference canvasFontFace,
        out ProGpuDirect2DComReference? nativeFontFaceReference,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapperNativeResource(
            canvasFontFace,
            ProGpuDirect2DInterfaceKind.Win2DCanvasFontFace,
            DWriteFontFaceReferenceInterfaceId,
            ProGpuDirect2DInterfaceKind.DWriteFontFaceReference,
            "Microsoft Win2D CanvasFontFace native-resource query",
            out nativeFontFaceReference,
            out nativeHResult);

    /// <summary>
    /// Wraps a provider-created ID2D1SvgDocument as a genuine Microsoft Win2D
    /// CanvasSvgDocument in this surface's exact CanvasDevice domain.
    /// </summary>
    public bool TryAcquireMicrosoftWin2DSvgDocument(
        ProGpuDirect2DComReference nativeSvgDocument,
        out ProGpuDirect2DComReference? canvasSvgDocument,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapper(
            nativeSvgDocument,
            ProGpuDirect2DInterfaceKind.D2D1SvgDocument,
            ProGpuDirect2DInterfaceKind.Win2DCanvasSvgDocument,
            "Microsoft Win2D CanvasSvgDocument wrapping",
            out canvasSvgDocument,
            out nativeHResult);

    /// <summary>
    /// Reverse-unwraps a genuine Microsoft Win2D CanvasSvgDocument and returns
    /// its exact ID2D1SvgDocument with one caller-owned COM reference.
    /// </summary>
    public bool TryAcquireMicrosoftWin2DNativeSvgDocument(
        ProGpuDirect2DComReference canvasSvgDocument,
        out ProGpuDirect2DComReference? nativeSvgDocument,
        out int nativeHResult) =>
        TryAcquireMicrosoftWin2DWrapperNativeResource(
            canvasSvgDocument,
            ProGpuDirect2DInterfaceKind.Win2DCanvasSvgDocument,
            D2D1SvgDocumentInterfaceId,
            ProGpuDirect2DInterfaceKind.D2D1SvgDocument,
            "Microsoft Win2D CanvasSvgDocument native-resource query",
            out nativeSvgDocument,
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
        ValidateResourceDomain(nativeResource, nameof(nativeResource));
        if (!IsCompatibleInterfaceKind(
                nativeResource.InterfaceKind,
                expectedNativeKind))
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
        ValidateResourceDomain(wrapper, nameof(wrapper));
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

    private ProGpuDirect2DComReference CreateRequiredComReference(
        nint value,
        ProGpuDirect2DInterfaceKind kind,
        string operation)
    {
        if (value == 0)
        {
            throw new InvalidOperationException(
                $"{operation} succeeded without returning a COM interface.");
        }
        return new ProGpuDirect2DComReference(
            value,
            kind,
            _resourceDomain);
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
                interfaceKind,
                _resourceDomain);
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
                    "A Direct2D, Win2D, or command-list producer session is already active.");
            }
            if (_leaseCount != 0)
            {
                throw new InvalidOperationException(
                    "Direct2D cannot acquire the allocation while deferred ProGPU texture leases are active.");
            }

            context = AcquireInterface(
                ProGpuDirect2DInterfaceKind.D2D1DeviceContext1);
            _producer = ProducerKind.Direct2D;
            _typedDrawScopeDepth = 0U;
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
    /// Begins recording into an open same-domain command list. This scope does
    /// not acquire or modify the shared texture and never advances its content
    /// version. Disposal restores the shared bitmap target and closes the
    /// command list.
    /// </summary>
    public ProGpuDirect2DCommandListDrawingSession BeginCommandListDrawing(
        ProGpuDirect2DComReference commandList)
    {
        ArgumentNullException.ThrowIfNull(commandList);
        ValidateResourceDomain(commandList, nameof(commandList));
        if (commandList.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.D2D1CommandList)
        {
            throw new ArgumentException(
                "The COM reference must own ID2D1CommandList.",
                nameof(commandList));
        }

        ProGpuDirect2DComReference? context = null;
        bool commandListReferenceAdded = false;
        bool producerClaimed = false;
        try
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                if (_producer != ProducerKind.None)
                {
                    throw new InvalidOperationException(
                        "A Direct2D, Win2D, or command-list producer session is already active.");
                }
                if (_leaseCount != 0)
                {
                    throw new InvalidOperationException(
                        "Direct2D command-list recording cannot overlap deferred ProGPU texture leases.");
                }
                commandList.DangerousAddRef(ref commandListReferenceAdded);
                context = AcquireInterface(
                    ProGpuDirect2DInterfaceKind.D2D1DeviceContext1);
                _producer = ProducerKind.CommandList;
                _typedDrawScopeDepth = 0U;
                producerClaimed = true;
            }
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceBeginCommandListDraw(
                    _nativeSurface,
                    commandList.DangerousGetHandle());
            ThrowIfFailed(
                "ID2D1CommandList BeginDraw",
                status,
                ProGpuDirect2DNative.SurfaceGetLastHResult(
                    _nativeSurface));
            return new ProGpuDirect2DCommandListDrawingSession(
                this,
                context!,
                commandList,
                commandListReferenceAdded);
        }
        catch
        {
            if (producerClaimed)
            {
                lock (_gate)
                {
                    _producer = ProducerKind.None;
                }
            }
            context?.Dispose();
            if (commandListReferenceAdded)
            {
                commandList.DangerousRelease();
            }
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
                    "A Direct2D, Win2D, or command-list producer session is already active.");
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
                ProGpuDirect2DInterfaceKind.Win2DCanvasRenderTarget,
                _resourceDomain);
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
            _disposeRequested = true;
            access = TryTakeResourcesForDisposal();
        }
        access?.Dispose();
    }

    internal void CompleteDirect2DDrawing() =>
        CompleteProducerAccess(ProducerKind.Direct2D);

    internal void CompleteMicrosoftWin2DProducerAccess() =>
        CompleteProducerAccess(ProducerKind.MicrosoftWin2D);

    internal void CompleteCommandListDrawing()
    {
        DawnExplicitSharedTextureAccess? accessToDispose = null;
        nint nativeSurface;
        lock (_gate)
        {
            if (_producer != ProducerKind.CommandList || _resourcesDisposed)
            {
                return;
            }
            nativeSurface = _nativeSurface;
        }

        ulong tag1 = 0U;
        ulong tag2 = 0U;
        int nativeHResult = 0;
        ProGpuDirect2DStatus? status = null;
        ExceptionDispatchInfo? failure = null;
        try
        {
            status = ProGpuDirect2DNative.SurfaceEndCommandListDraw(
                nativeSurface,
                &tag1,
                &tag2,
                &nativeHResult);
            if (status != ProGpuDirect2DStatus.Success)
            {
                failure = ExceptionDispatchInfo.Capture(
                    new ProGpuDirect2DException(
                        $"ID2D1CommandList EndDraw (tags {tag1}/{tag2})",
                        status.Value,
                        nativeHResult));
            }
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            lock (_gate)
            {
                _producer = ProducerKind.None;
                _typedDrawScopeDepth = 0U;
                if (status == ProGpuDirect2DStatus.DeviceLost)
                {
                    ObserveDeviceLoss(new ProGpuDirect2DDeviceLossState(
                        ProGpuDirect2DDeviceLossFlags.DeviceLost,
                        nativeHResult,
                        ResourceGeneration));
                }
                accessToDispose = TryTakeResourcesForDisposal();
            }
            accessToDispose?.Dispose();
        }
        failure?.Throw();
    }

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
            _typedDrawScopeDepth = 0U;
            if (status == ProGpuDirect2DStatus.DeviceLost)
            {
                ObserveDeviceLoss(new ProGpuDirect2DDeviceLossState(
                    ProGpuDirect2DDeviceLossFlags.DeviceLost,
                    nativeHResult,
                    ResourceGeneration));
            }
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
        if (!_disposeRequested && !_resourcesDisposed)
        {
            ObserveDeviceLoss(ReadDeviceLossState(_nativeSurface));
        }
        if (_deviceLost)
        {
            throw new ProGpuDirect2DException(
                "Direct2D device-domain availability",
                ProGpuDirect2DStatus.DeviceLost,
                _deviceLossHResult);
        }
        ObjectDisposedException.ThrowIf(
            _disposeRequested || _resourcesDisposed,
            this);
    }

    private void ObserveDeviceLoss(
        in ProGpuDirect2DDeviceLossState state)
    {
        if (!state.IsDeviceLost)
        {
            return;
        }
        bool queueNotification = false;
        lock (_gate)
        {
            if (_deviceLost)
            {
                return;
            }
            _deviceLost = true;
            _deviceLossHResult = state.ReasonHResult;
            _resourceDomain.MarkDeviceLost(state.ReasonHResult);
            _disposeRequested = true;
            queueNotification = Interlocked.Exchange(
                ref _deviceLostNotificationQueued,
                1) == 0;
        }
        if (queueNotification)
        {
            ThreadPool.QueueUserWorkItem(
                static owner => owner!.PublishDeviceLost(),
                this,
                preferLocal: false);
        }
    }

    private void PublishDeviceLost()
    {
        DawnExplicitSharedTextureAccess? accessToDispose;
        lock (_gate)
        {
            accessToDispose = TryTakeResourcesForDisposal();
        }
        accessToDispose?.Dispose();
        var eventArgs = new ProGpuDirect2DDeviceLostEventArgs(
            DeviceLossHResult,
            ResourceGeneration);
        _dawn.Context.ReportDeviceLost(
            DeviceLostReason.Unknown,
            $"The Direct2D/D3D11 device domain for resource generation {ResourceGeneration} was lost (HRESULT 0x{unchecked((uint)eventArgs.ReasonHResult):X8}). Create a new Dawn context and rebuild device-dependent resources.");
        try
        {
            DeviceLost?.Invoke(this, eventArgs);
        }
        catch
        {
            // Device-loss propagation is terminal and must not be suppressed
            // or tear down the process because a notification handler failed.
        }
    }

    private void ValidateTypedDrawingProducer()
    {
        ThrowIfUnavailable();
        if (_producer is not ProducerKind.Direct2D and
            not ProducerKind.CommandList)
        {
            throw new InvalidOperationException(
                "A typed Direct2D or command-list drawing session must be active.");
        }
    }

    private void ApplyDrawingState(
        ProGpuDirect2DComReference drawingStateBlock,
        bool restore)
    {
        ValidateDrawingStateBlock(drawingStateBlock);
        bool referenceAdded = false;
        try
        {
            drawingStateBlock.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ValidateTypedDrawingProducer();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status = restore
                    ? ProGpuDirect2DNative.SurfaceRestoreDrawingState(
                        _nativeSurface,
                        drawingStateBlock.DangerousGetHandle(),
                        &nativeHResult)
                    : ProGpuDirect2DNative.SurfaceSaveDrawingState(
                        _nativeSurface,
                        drawingStateBlock.DangerousGetHandle(),
                        &nativeHResult);
                ThrowIfFailed(
                    restore
                        ? "ID2D1DrawingStateBlock1 restore"
                        : "ID2D1DrawingStateBlock1 save",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (referenceAdded)
            {
                drawingStateBlock.DangerousRelease();
            }
        }
    }

    private void SetEffectUnmanagedValue<T>(
        ProGpuDirect2DComReference effect,
        uint propertyIndex,
        ProGpuDirect2DEffectPropertyType propertyType,
        T value)
        where T : unmanaged
    {
        SetEffectValue(
            effect,
            propertyIndex,
            propertyType,
            new ReadOnlySpan<byte>(&value, sizeof(T)));
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

    private void ValidateGradientStopCollection(
        ProGpuDirect2DComReference gradientStopCollection)
    {
        ArgumentNullException.ThrowIfNull(gradientStopCollection);
        ValidateResourceDomain(
            gradientStopCollection,
            nameof(gradientStopCollection));
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

    private static void ValidatePositiveSize(
        Vector2 size,
        string parameterName)
    {
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) ||
            size.X <= 0.0F || size.Y <= 0.0F)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Direct2D sizes must be finite and positive.");
        }
    }

    private static void ValidateRectangle(ProGpuDirect2DRect rectangle)
    {
        if (!float.IsFinite(rectangle.X) ||
            !float.IsFinite(rectangle.Y) ||
            !float.IsFinite(rectangle.Width) ||
            !float.IsFinite(rectangle.Height) ||
            rectangle.Width < 0.0F || rectangle.Height < 0.0F ||
            !float.IsFinite(rectangle.X + rectangle.Width) ||
            !float.IsFinite(rectangle.Y + rectangle.Height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(rectangle),
                "Direct2D rectangles must be finite and nonnegative in size.");
        }
    }

    private static void ValidateRadii(float radiusX, float radiusY)
    {
        if (!float.IsFinite(radiusX) || !float.IsFinite(radiusY) ||
            radiusX < 0.0F || radiusY < 0.0F)
        {
            throw new ArgumentOutOfRangeException(
                nameof(radiusX),
                "Direct2D radii must be finite and nonnegative.");
        }
    }

    private static void ValidateOpacity(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0.0F || value > 1.0F)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Direct2D opacity must be finite and between zero and one.");
        }
    }

    private static void ValidateAntialiasMode(
        ProGpuDirect2DAntialiasMode value,
        string parameterName)
    {
        if (value is < ProGpuDirect2DAntialiasMode.PerPrimitive or >
            ProGpuDirect2DAntialiasMode.Aliased)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Direct2D antialias mode is unknown.");
        }
    }

    private static void ValidateTextAntialiasMode(
        ProGpuDirect2DTextAntialiasMode value,
        string parameterName)
    {
        if (value is < ProGpuDirect2DTextAntialiasMode.Default or >
            ProGpuDirect2DTextAntialiasMode.Aliased)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Direct2D text antialias mode is unknown.");
        }
    }

    private static void ValidatePrimitiveBlend(
        ProGpuDirect2DPrimitiveBlend value,
        string parameterName)
    {
        if (value is < ProGpuDirect2DPrimitiveBlend.SourceOver or >
            ProGpuDirect2DPrimitiveBlend.Maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Direct2D primitive blend is unknown.");
        }
    }

    private static void ValidateUnitMode(
        ProGpuDirect2DUnitMode value,
        string parameterName)
    {
        if (value is < ProGpuDirect2DUnitMode.Dips or >
            ProGpuDirect2DUnitMode.Pixels)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Direct2D unit mode is unknown.");
        }
    }

    private static void ValidateInterpolationMode(
        ProGpuDirect2DInterpolationMode value,
        string parameterName)
    {
        if (value is < ProGpuDirect2DInterpolationMode.NearestNeighbor or >
            ProGpuDirect2DInterpolationMode.HighQualityCubic)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Direct2D interpolation mode is unknown.");
        }
    }

    private static void ValidateCompositeMode(
        ProGpuDirect2DCompositeMode value,
        string parameterName)
    {
        if (value is < ProGpuDirect2DCompositeMode.SourceOver or >
            ProGpuDirect2DCompositeMode.MaskInvert)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Direct2D composite mode is unknown.");
        }
    }

    private float ComputeGeometryScalar(
        ProGpuDirect2DComReference geometry,
        Matrix3x2? transform,
        float flatteningTolerance,
        bool computeLength)
    {
        ValidateGeometry(geometry, nameof(geometry));
        ValidateFlatteningTolerance(
            flatteningTolerance,
            nameof(flatteningTolerance));
        ProGpuDirect2DNative.NativeMatrix3X2F nativeTransform = default;
        ProGpuDirect2DNative.NativeMatrix3X2F* transformPointer = null;
        if (transform is Matrix3x2 value)
        {
            nativeTransform = CreateNativeMatrix(value);
            transformPointer = &nativeTransform;
        }
        bool referenceAdded = false;
        try
        {
            geometry.DangerousAddRef(ref referenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                float result = 0.0F;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status = computeLength
                    ? ProGpuDirect2DNative.GeometryComputeLength(
                        _nativeSurface,
                        geometry.DangerousGetHandle(),
                        transformPointer,
                        flatteningTolerance,
                        &result,
                        &nativeHResult)
                    : ProGpuDirect2DNative.GeometryComputeArea(
                        _nativeSurface,
                        geometry.DangerousGetHandle(),
                        transformPointer,
                        flatteningTolerance,
                        &result,
                        &nativeHResult);
                ThrowIfFailed(
                    computeLength
                        ? "ID2D1Geometry::ComputeLength"
                        : "ID2D1Geometry::ComputeArea",
                    status,
                    nativeHResult);
                return result;
            }
        }
        finally
        {
            if (referenceAdded)
            {
                geometry.DangerousRelease();
            }
        }
    }

    private ProGpuDirect2DComReference CreateDerivedGeometry(
        GeometryDerivation derivation,
        ProGpuDirect2DComReference geometry,
        ProGpuDirect2DGeometrySimplificationOption simplificationOption,
        float strokeWidth,
        ProGpuDirect2DComReference? strokeStyle,
        Matrix3x2? transform,
        float flatteningTolerance)
    {
        ValidateGeometry(geometry, nameof(geometry));
        ValidateFlatteningTolerance(
            flatteningTolerance,
            nameof(flatteningTolerance));
        if (derivation == GeometryDerivation.Widen)
        {
            ValidateStrokeWidth(strokeWidth, nameof(strokeWidth));
            ValidateOptionalStrokeStyle(strokeStyle, nameof(strokeStyle));
        }
        ProGpuDirect2DNative.NativeMatrix3X2F nativeTransform = default;
        ProGpuDirect2DNative.NativeMatrix3X2F* transformPointer = null;
        if (transform is Matrix3x2 matrix)
        {
            nativeTransform = CreateNativeMatrix(matrix);
            transformPointer = &nativeTransform;
        }
        bool geometryReferenceAdded = false;
        bool styleReferenceAdded = false;
        try
        {
            geometry.DangerousAddRef(ref geometryReferenceAdded);
            strokeStyle?.DangerousAddRef(ref styleReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                string operation = derivation switch
                {
                    GeometryDerivation.Simplify =>
                        "ID2D1Geometry::Simplify",
                    GeometryDerivation.Outline =>
                        "ID2D1Geometry::Outline",
                    GeometryDerivation.Widen =>
                        "ID2D1Geometry::Widen",
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(derivation))
                };
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status = derivation switch
                {
                    GeometryDerivation.Simplify =>
                        ProGpuDirect2DNative.GeometrySimplify(
                            _nativeSurface,
                            geometry.DangerousGetHandle(),
                            simplificationOption,
                            transformPointer,
                            flatteningTolerance,
                            &value,
                            &nativeHResult),
                    GeometryDerivation.Outline =>
                        ProGpuDirect2DNative.GeometryOutline(
                            _nativeSurface,
                            geometry.DangerousGetHandle(),
                            transformPointer,
                            flatteningTolerance,
                            &value,
                            &nativeHResult),
                    GeometryDerivation.Widen =>
                        ProGpuDirect2DNative.GeometryWiden(
                            _nativeSurface,
                            geometry.DangerousGetHandle(),
                            strokeWidth,
                            strokeStyle?.DangerousGetHandle() ?? 0,
                            transformPointer,
                            flatteningTolerance,
                            &value,
                            &nativeHResult),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(derivation))
                };
                ThrowIfFailed(operation, status, nativeHResult);
                return CreateRequiredComReference(
                    value,
                    ProGpuDirect2DInterfaceKind.D2D1PathGeometry1,
                    operation);
            }
        }
        finally
        {
            if (styleReferenceAdded)
            {
                strokeStyle!.DangerousRelease();
            }
            if (geometryReferenceAdded)
            {
                geometry.DangerousRelease();
            }
        }
    }

    private ProGpuDirect2DComReference CreateGeometryRealization(
        ProGpuDirect2DComReference geometry,
        float flatteningTolerance,
        float strokeWidth,
        ProGpuDirect2DComReference? strokeStyle,
        bool stroked)
    {
        ValidateGeometry(geometry, nameof(geometry));
        ValidateFlatteningTolerance(
            flatteningTolerance,
            nameof(flatteningTolerance));
        if (stroked)
        {
            ValidateStrokeWidth(strokeWidth, nameof(strokeWidth));
            ValidateOptionalStrokeStyle(strokeStyle, nameof(strokeStyle));
        }
        bool geometryReferenceAdded = false;
        bool styleReferenceAdded = false;
        try
        {
            geometry.DangerousAddRef(ref geometryReferenceAdded);
            strokeStyle?.DangerousAddRef(ref styleReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                nint value = 0;
                int nativeHResult = 0;
                ProGpuDirect2DStatus status = stroked
                    ? ProGpuDirect2DNative
                        .SurfaceCreateStrokedGeometryRealization(
                            _nativeSurface,
                            geometry.DangerousGetHandle(),
                            flatteningTolerance,
                            strokeWidth,
                            strokeStyle?.DangerousGetHandle() ?? 0,
                            &value,
                            &nativeHResult)
                    : ProGpuDirect2DNative
                        .SurfaceCreateFilledGeometryRealization(
                            _nativeSurface,
                            geometry.DangerousGetHandle(),
                            flatteningTolerance,
                            &value,
                            &nativeHResult);
                string operation = stroked
                    ? "stroked ID2D1GeometryRealization creation"
                    : "filled ID2D1GeometryRealization creation";
                ThrowIfFailed(operation, status, nativeHResult);
                return CreateRequiredComReference(
                    value,
                    ProGpuDirect2DInterfaceKind.D2D1GeometryRealization,
                    operation);
            }
        }
        finally
        {
            if (styleReferenceAdded)
            {
                strokeStyle!.DangerousRelease();
            }
            if (geometryReferenceAdded)
            {
                geometry.DangerousRelease();
            }
        }
    }

    internal void DrawGeometryRealization(
        ProGpuDirect2DComReference realization,
        ProGpuDirect2DComReference brush)
    {
        ValidateGeometryRealization(realization);
        ArgumentNullException.ThrowIfNull(brush);
        ValidateResourceDomain(brush, nameof(brush));
        if (!IsBrushKind(brush.InterfaceKind))
        {
            throw new ArgumentException(
                "The COM reference must own a genuine ID2D1Brush.",
                nameof(brush));
        }
        bool realizationReferenceAdded = false;
        bool brushReferenceAdded = false;
        try
        {
            realization.DangerousAddRef(ref realizationReferenceAdded);
            brush.DangerousAddRef(ref brushReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceDrawGeometryRealization(
                        _nativeSurface,
                        realization.DangerousGetHandle(),
                        brush.DangerousGetHandle(),
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1DeviceContext1::DrawGeometryRealization",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (brushReferenceAdded)
            {
                brush.DangerousRelease();
            }
            if (realizationReferenceAdded)
            {
                realization.DangerousRelease();
            }
        }
    }

    internal void Clear(ProGpuDirect2DColor? color)
    {
        ProGpuDirect2DNative.NativeColorF nativeColor = default;
        ProGpuDirect2DNative.NativeColorF* colorPointer = null;
        if (color is ProGpuDirect2DColor value)
        {
            ValidateColor(value);
            nativeColor = new ProGpuDirect2DNative.NativeColorF
            {
                Red = value.Red,
                Green = value.Green,
                Blue = value.Blue,
                Alpha = value.Alpha
            };
            colorPointer = &nativeColor;
        }
        lock (_gate)
        {
            ThrowIfUnavailable();
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceClear(
                    _nativeSurface,
                    colorPointer,
                    &nativeHResult);
            ThrowIfFailed("ID2D1DeviceContext::Clear", status, nativeHResult);
        }
    }

    internal void SetTransform(Matrix3x2 transform)
    {
        ProGpuDirect2DNative.NativeMatrix3X2F nativeTransform =
            CreateNativeMatrix(transform);
        lock (_gate)
        {
            ThrowIfUnavailable();
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceSetTransform(
                    _nativeSurface,
                    &nativeTransform,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1DeviceContext::SetTransform",
                status,
                nativeHResult);
        }
    }

    internal Matrix3x2 GetTransform()
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            ProGpuDirect2DNative.NativeMatrix3X2F transform = default;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceGetTransform(
                    _nativeSurface,
                    &transform,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1DeviceContext::GetTransform",
                status,
                nativeHResult);
            return new Matrix3x2(
                transform.M11,
                transform.M12,
                transform.M21,
                transform.M22,
                transform.M31,
                transform.M32);
        }
    }

    internal void SetAntialiasMode(ProGpuDirect2DAntialiasMode mode)
    {
        ValidateAntialiasMode(mode, nameof(mode));
        lock (_gate)
        {
            ValidateTypedDrawingProducer();
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceSetAntialiasMode(
                    _nativeSurface,
                    mode,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1RenderTarget::SetAntialiasMode",
                status,
                nativeHResult);
        }
    }

    internal ProGpuDirect2DAntialiasMode GetAntialiasMode()
    {
        lock (_gate)
        {
            ValidateTypedDrawingProducer();
            ProGpuDirect2DAntialiasMode mode = default;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceGetAntialiasMode(
                    _nativeSurface,
                    &mode,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1RenderTarget::GetAntialiasMode",
                status,
                nativeHResult);
            return mode;
        }
    }

    internal void SetTextAntialiasMode(ProGpuDirect2DTextAntialiasMode mode)
    {
        ValidateTextAntialiasMode(mode, nameof(mode));
        lock (_gate)
        {
            ValidateTypedDrawingProducer();
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceSetTextAntialiasMode(
                    _nativeSurface,
                    mode,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1RenderTarget::SetTextAntialiasMode",
                status,
                nativeHResult);
        }
    }

    internal ProGpuDirect2DTextAntialiasMode GetTextAntialiasMode()
    {
        lock (_gate)
        {
            ValidateTypedDrawingProducer();
            ProGpuDirect2DTextAntialiasMode mode = default;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceGetTextAntialiasMode(
                    _nativeSurface,
                    &mode,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1RenderTarget::GetTextAntialiasMode",
                status,
                nativeHResult);
            return mode;
        }
    }

    internal void SetPrimitiveBlend(ProGpuDirect2DPrimitiveBlend blend)
    {
        ValidatePrimitiveBlend(blend, nameof(blend));
        lock (_gate)
        {
            ValidateTypedDrawingProducer();
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceSetPrimitiveBlend(
                    _nativeSurface,
                    blend,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1DeviceContext::SetPrimitiveBlend",
                status,
                nativeHResult);
        }
    }

    internal ProGpuDirect2DPrimitiveBlend GetPrimitiveBlend()
    {
        lock (_gate)
        {
            ValidateTypedDrawingProducer();
            ProGpuDirect2DPrimitiveBlend blend = default;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceGetPrimitiveBlend(
                    _nativeSurface,
                    &blend,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1DeviceContext::GetPrimitiveBlend",
                status,
                nativeHResult);
            return blend;
        }
    }

    internal void SetUnitMode(ProGpuDirect2DUnitMode mode)
    {
        ValidateUnitMode(mode, nameof(mode));
        lock (_gate)
        {
            ValidateTypedDrawingProducer();
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceSetUnitMode(
                    _nativeSurface,
                    mode,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1DeviceContext::SetUnitMode",
                status,
                nativeHResult);
        }
    }

    internal ProGpuDirect2DUnitMode GetUnitMode()
    {
        lock (_gate)
        {
            ValidateTypedDrawingProducer();
            ProGpuDirect2DUnitMode mode = default;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.SurfaceGetUnitMode(
                    _nativeSurface,
                    &mode,
                    &nativeHResult);
            ThrowIfFailed(
                "ID2D1DeviceContext::GetUnitMode",
                status,
                nativeHResult);
            return mode;
        }
    }

    internal void SetTags(ProGpuDirect2DTags tags)
    {
        lock (_gate)
        {
            ValidateTypedDrawingProducer();
            int nativeHResult = 0;
            ProGpuDirect2DStatus status = ProGpuDirect2DNative.SurfaceSetTags(
                _nativeSurface,
                tags.Tag1,
                tags.Tag2,
                &nativeHResult);
            ThrowIfFailed(
                "ID2D1RenderTarget::SetTags",
                status,
                nativeHResult);
        }
    }

    internal ProGpuDirect2DTags GetTags()
    {
        lock (_gate)
        {
            ValidateTypedDrawingProducer();
            ulong tag1 = 0U;
            ulong tag2 = 0U;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status = ProGpuDirect2DNative.SurfaceGetTags(
                _nativeSurface,
                &tag1,
                &tag2,
                &nativeHResult);
            ThrowIfFailed(
                "ID2D1RenderTarget::GetTags",
                status,
                nativeHResult);
            return new ProGpuDirect2DTags(tag1, tag2);
        }
    }

    internal void SetDpi(Vector2 dpi)
    {
        bool resetToDefault = dpi == Vector2.Zero;
        if (!float.IsFinite(dpi.X) || !float.IsFinite(dpi.Y) ||
            !resetToDefault && (dpi.X <= 0.0F || dpi.Y <= 0.0F))
        {
            throw new ArgumentOutOfRangeException(
                nameof(dpi),
                "Direct2D DPI must be positive and finite, or both zero to restore 96 DPI.");
        }
        lock (_gate)
        {
            ValidateTypedDrawingProducer();
            int nativeHResult = 0;
            ProGpuDirect2DStatus status = ProGpuDirect2DNative.SurfaceSetDpi(
                _nativeSurface,
                dpi.X,
                dpi.Y,
                &nativeHResult);
            ThrowIfFailed(
                "ID2D1RenderTarget::SetDpi",
                status,
                nativeHResult);
        }
    }

    internal Vector2 GetDpi()
    {
        lock (_gate)
        {
            ValidateTypedDrawingProducer();
            float dpiX = 0.0F;
            float dpiY = 0.0F;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status = ProGpuDirect2DNative.SurfaceGetDpi(
                _nativeSurface,
                &dpiX,
                &dpiY,
                &nativeHResult);
            ThrowIfFailed(
                "ID2D1RenderTarget::GetDpi",
                status,
                nativeHResult);
            return new Vector2(dpiX, dpiY);
        }
    }

    internal void DrawLine(
        Vector2 point0,
        Vector2 point1,
        ProGpuDirect2DComReference brush,
        float strokeWidth,
        ProGpuDirect2DComReference? strokeStyle)
    {
        ValidatePoint(point0, nameof(point0));
        ValidatePoint(point1, nameof(point1));
        DrawStrokePrimitive(
            VectorPrimitive.Line,
            default,
            point0,
            point1,
            0.0F,
            0.0F,
            brush,
            strokeWidth,
            strokeStyle);
    }

    internal void DrawRectangle(
        ProGpuDirect2DRect rectangle,
        ProGpuDirect2DComReference brush,
        float strokeWidth,
        ProGpuDirect2DComReference? strokeStyle)
    {
        ValidateRectangle(rectangle);
        DrawStrokePrimitive(
            VectorPrimitive.Rectangle,
            rectangle,
            default,
            default,
            0.0F,
            0.0F,
            brush,
            strokeWidth,
            strokeStyle);
    }

    internal void FillRectangle(
        ProGpuDirect2DRect rectangle,
        ProGpuDirect2DComReference brush)
    {
        ValidateRectangle(rectangle);
        FillPrimitive(
            VectorPrimitive.Rectangle,
            rectangle,
            default,
            0.0F,
            0.0F,
            brush);
    }

    internal void DrawRoundedRectangle(
        ProGpuDirect2DRect rectangle,
        float radiusX,
        float radiusY,
        ProGpuDirect2DComReference brush,
        float strokeWidth,
        ProGpuDirect2DComReference? strokeStyle)
    {
        ValidateRectangle(rectangle);
        ValidateRadii(radiusX, radiusY);
        DrawStrokePrimitive(
            VectorPrimitive.RoundedRectangle,
            rectangle,
            default,
            default,
            radiusX,
            radiusY,
            brush,
            strokeWidth,
            strokeStyle);
    }

    internal void FillRoundedRectangle(
        ProGpuDirect2DRect rectangle,
        float radiusX,
        float radiusY,
        ProGpuDirect2DComReference brush)
    {
        ValidateRectangle(rectangle);
        ValidateRadii(radiusX, radiusY);
        FillPrimitive(
            VectorPrimitive.RoundedRectangle,
            rectangle,
            default,
            radiusX,
            radiusY,
            brush);
    }

    internal void DrawEllipse(
        Vector2 center,
        float radiusX,
        float radiusY,
        ProGpuDirect2DComReference brush,
        float strokeWidth,
        ProGpuDirect2DComReference? strokeStyle)
    {
        ValidatePoint(center, nameof(center));
        ValidateRadii(radiusX, radiusY);
        DrawStrokePrimitive(
            VectorPrimitive.Ellipse,
            default,
            center,
            default,
            radiusX,
            radiusY,
            brush,
            strokeWidth,
            strokeStyle);
    }

    internal void FillEllipse(
        Vector2 center,
        float radiusX,
        float radiusY,
        ProGpuDirect2DComReference brush)
    {
        ValidatePoint(center, nameof(center));
        ValidateRadii(radiusX, radiusY);
        FillPrimitive(
            VectorPrimitive.Ellipse,
            default,
            center,
            radiusX,
            radiusY,
            brush);
    }

    internal void DrawGeometry(
        ProGpuDirect2DComReference geometry,
        ProGpuDirect2DComReference brush,
        float strokeWidth,
        ProGpuDirect2DComReference? strokeStyle)
    {
        ValidateGeometry(geometry, nameof(geometry));
        ValidateBrush(brush, nameof(brush));
        ValidateStrokeWidth(strokeWidth, nameof(strokeWidth));
        ValidateOptionalStrokeStyle(strokeStyle, nameof(strokeStyle));
        bool geometryReferenceAdded = false;
        bool brushReferenceAdded = false;
        bool styleReferenceAdded = false;
        try
        {
            geometry.DangerousAddRef(ref geometryReferenceAdded);
            brush.DangerousAddRef(ref brushReferenceAdded);
            strokeStyle?.DangerousAddRef(ref styleReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceDrawGeometry(
                        _nativeSurface,
                        geometry.DangerousGetHandle(),
                        brush.DangerousGetHandle(),
                        strokeWidth,
                        strokeStyle?.DangerousGetHandle() ?? 0,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1DeviceContext::DrawGeometry",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (styleReferenceAdded)
            {
                strokeStyle!.DangerousRelease();
            }
            if (brushReferenceAdded)
            {
                brush.DangerousRelease();
            }
            if (geometryReferenceAdded)
            {
                geometry.DangerousRelease();
            }
        }
    }

    internal void FillGeometry(
        ProGpuDirect2DComReference geometry,
        ProGpuDirect2DComReference brush,
        ProGpuDirect2DComReference? opacityBrush)
    {
        ValidateGeometry(geometry, nameof(geometry));
        ValidateBrush(brush, nameof(brush));
        if (opacityBrush is not null)
        {
            ValidateBrush(opacityBrush, nameof(opacityBrush));
        }
        bool geometryReferenceAdded = false;
        bool brushReferenceAdded = false;
        bool opacityReferenceAdded = false;
        try
        {
            geometry.DangerousAddRef(ref geometryReferenceAdded);
            brush.DangerousAddRef(ref brushReferenceAdded);
            opacityBrush?.DangerousAddRef(ref opacityReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status =
                    ProGpuDirect2DNative.SurfaceFillGeometry(
                        _nativeSurface,
                        geometry.DangerousGetHandle(),
                        brush.DangerousGetHandle(),
                        opacityBrush?.DangerousGetHandle() ?? 0,
                        &nativeHResult);
                ThrowIfFailed(
                    "ID2D1DeviceContext::FillGeometry",
                    status,
                    nativeHResult);
            }
        }
        finally
        {
            if (opacityReferenceAdded)
            {
                opacityBrush!.DangerousRelease();
            }
            if (brushReferenceAdded)
            {
                brush.DangerousRelease();
            }
            if (geometryReferenceAdded)
            {
                geometry.DangerousRelease();
            }
        }
    }

    private void DrawStrokePrimitive(
        VectorPrimitive primitive,
        ProGpuDirect2DRect rectangle,
        Vector2 point0,
        Vector2 point1,
        float radiusX,
        float radiusY,
        ProGpuDirect2DComReference brush,
        float strokeWidth,
        ProGpuDirect2DComReference? strokeStyle)
    {
        ValidateBrush(brush, nameof(brush));
        ValidateStrokeWidth(strokeWidth, nameof(strokeWidth));
        ValidateOptionalStrokeStyle(strokeStyle, nameof(strokeStyle));
        bool brushReferenceAdded = false;
        bool styleReferenceAdded = false;
        try
        {
            brush.DangerousAddRef(ref brushReferenceAdded);
            strokeStyle?.DangerousAddRef(ref styleReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status = primitive switch
                {
                    VectorPrimitive.Line =>
                        ProGpuDirect2DNative.SurfaceDrawLine(
                            _nativeSurface,
                            CreateNativePoint(point0),
                            CreateNativePoint(point1),
                            brush.DangerousGetHandle(),
                            strokeWidth,
                            strokeStyle?.DangerousGetHandle() ?? 0,
                            &nativeHResult),
                    VectorPrimitive.Rectangle =>
                        ProGpuDirect2DNative.SurfaceDrawRectangle(
                            _nativeSurface,
                            &rectangle,
                            brush.DangerousGetHandle(),
                            strokeWidth,
                            strokeStyle?.DangerousGetHandle() ?? 0,
                            &nativeHResult),
                    VectorPrimitive.RoundedRectangle =>
                        ProGpuDirect2DNative.SurfaceDrawRoundedRectangle(
                            _nativeSurface,
                            &rectangle,
                            radiusX,
                            radiusY,
                            brush.DangerousGetHandle(),
                            strokeWidth,
                            strokeStyle?.DangerousGetHandle() ?? 0,
                            &nativeHResult),
                    VectorPrimitive.Ellipse =>
                        ProGpuDirect2DNative.SurfaceDrawEllipse(
                            _nativeSurface,
                            CreateNativePoint(point0),
                            radiusX,
                            radiusY,
                            brush.DangerousGetHandle(),
                            strokeWidth,
                            strokeStyle?.DangerousGetHandle() ?? 0,
                            &nativeHResult),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(primitive))
                };
                string operation = primitive switch
                {
                    VectorPrimitive.Line => "ID2D1DeviceContext::DrawLine",
                    VectorPrimitive.Rectangle =>
                        "ID2D1DeviceContext::DrawRectangle",
                    VectorPrimitive.RoundedRectangle =>
                        "ID2D1DeviceContext::DrawRoundedRectangle",
                    VectorPrimitive.Ellipse =>
                        "ID2D1DeviceContext::DrawEllipse",
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(primitive))
                };
                ThrowIfFailed(operation, status, nativeHResult);
            }
        }
        finally
        {
            if (styleReferenceAdded)
            {
                strokeStyle!.DangerousRelease();
            }
            if (brushReferenceAdded)
            {
                brush.DangerousRelease();
            }
        }
    }

    private void FillPrimitive(
        VectorPrimitive primitive,
        ProGpuDirect2DRect rectangle,
        Vector2 center,
        float radiusX,
        float radiusY,
        ProGpuDirect2DComReference brush)
    {
        ValidateBrush(brush, nameof(brush));
        bool brushReferenceAdded = false;
        try
        {
            brush.DangerousAddRef(ref brushReferenceAdded);
            lock (_gate)
            {
                ThrowIfUnavailable();
                int nativeHResult = 0;
                ProGpuDirect2DStatus status = primitive switch
                {
                    VectorPrimitive.Rectangle =>
                        ProGpuDirect2DNative.SurfaceFillRectangle(
                            _nativeSurface,
                            &rectangle,
                            brush.DangerousGetHandle(),
                            &nativeHResult),
                    VectorPrimitive.RoundedRectangle =>
                        ProGpuDirect2DNative.SurfaceFillRoundedRectangle(
                            _nativeSurface,
                            &rectangle,
                            radiusX,
                            radiusY,
                            brush.DangerousGetHandle(),
                            &nativeHResult),
                    VectorPrimitive.Ellipse =>
                        ProGpuDirect2DNative.SurfaceFillEllipse(
                            _nativeSurface,
                            CreateNativePoint(center),
                            radiusX,
                            radiusY,
                            brush.DangerousGetHandle(),
                            &nativeHResult),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(primitive))
                };
                string operation = primitive switch
                {
                    VectorPrimitive.Rectangle =>
                        "ID2D1DeviceContext::FillRectangle",
                    VectorPrimitive.RoundedRectangle =>
                        "ID2D1DeviceContext::FillRoundedRectangle",
                    VectorPrimitive.Ellipse =>
                        "ID2D1DeviceContext::FillEllipse",
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(primitive))
                };
                ThrowIfFailed(operation, status, nativeHResult);
            }
        }
        finally
        {
            if (brushReferenceAdded)
            {
                brush.DangerousRelease();
            }
        }
    }

    private static void ValidateFlatteningTolerance(
        float value,
        string parameterName)
    {
        if (!float.IsFinite(value) || value <= 0.0F)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Direct2D flattening tolerance must be finite and positive.");
        }
    }

    private static void ValidateStrokeWidth(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0.0F)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Direct2D stroke width must be finite and nonnegative.");
        }
    }

    private void ValidateGeometry(
        ProGpuDirect2DComReference geometry,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(geometry, parameterName);
        ValidateResourceDomain(geometry, parameterName);
        if (!IsGeometryKind(geometry.InterfaceKind))
        {
            throw new ArgumentException(
                "The COM reference must own a Direct2D geometry.",
                parameterName);
        }
    }

    private void ValidateBrush(
        ProGpuDirect2DComReference brush,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(brush, parameterName);
        ValidateResourceDomain(brush, parameterName);
        if (!IsBrushKind(brush.InterfaceKind))
        {
            throw new ArgumentException(
                "The COM reference must own a genuine ID2D1Brush.",
                parameterName);
        }
    }

    private void ValidateBitmap1(
        ProGpuDirect2DComReference bitmap,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(bitmap, parameterName);
        ValidateResourceDomain(bitmap, parameterName);
        if (bitmap.InterfaceKind != ProGpuDirect2DInterfaceKind.D2D1Bitmap1)
        {
            throw new ArgumentException(
                "The COM reference must own ID2D1Bitmap1.",
                parameterName);
        }
    }

    private static void ValidateCopyRectangle(
        ProGpuDirect2DRectU rectangle,
        string parameterName)
    {
        if (rectangle.Width == 0U || rectangle.Height == 0U ||
            (ulong)rectangle.X + rectangle.Width > uint.MaxValue ||
            (ulong)rectangle.Y + rectangle.Height > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Direct2D bitmap copy rectangles must be nonempty and nonoverflowing.");
        }
    }

    private void ValidateBrushKind(
        ProGpuDirect2DComReference brush,
        ProGpuDirect2DInterfaceKind kind,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(brush, parameterName);
        ValidateResourceDomain(brush, parameterName);
        if (brush.InterfaceKind != kind)
        {
            throw new ArgumentException(
                $"The COM reference must own {kind}.",
                parameterName);
        }
    }

    private void ValidateOptionalStrokeStyle(
        ProGpuDirect2DComReference? strokeStyle,
        string parameterName)
    {
        if (strokeStyle is null)
        {
            return;
        }
        ValidateResourceDomain(strokeStyle, parameterName);
        if (strokeStyle.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.D2D1StrokeStyle1)
        {
            throw new ArgumentException(
                "The COM reference must own an ID2D1StrokeStyle1.",
                parameterName);
        }
    }

    private void ValidateGeometryRealization(
        ProGpuDirect2DComReference realization)
    {
        ArgumentNullException.ThrowIfNull(realization);
        ValidateResourceDomain(realization, nameof(realization));
        if (realization.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.D2D1GeometryRealization)
        {
            throw new ArgumentException(
                "The COM reference must own an ID2D1GeometryRealization.",
                nameof(realization));
        }
    }

    private void ValidateLayer(
        ProGpuDirect2DComReference layer,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(layer, parameterName);
        ValidateResourceDomain(layer, parameterName);
        if (layer.InterfaceKind != ProGpuDirect2DInterfaceKind.D2D1Layer)
        {
            throw new ArgumentException(
                "The COM reference must own an ID2D1Layer.",
                parameterName);
        }
    }

    private void ValidateDrawingStateBlock(
        ProGpuDirect2DComReference drawingStateBlock)
    {
        ArgumentNullException.ThrowIfNull(drawingStateBlock);
        ValidateResourceDomain(
            drawingStateBlock,
            nameof(drawingStateBlock));
        if (drawingStateBlock.InterfaceKind !=
            ProGpuDirect2DInterfaceKind.D2D1DrawingStateBlock1)
        {
            throw new ArgumentException(
                "The COM reference must own an ID2D1DrawingStateBlock1.",
                nameof(drawingStateBlock));
        }
    }

    private static void ValidateLayerParameters(
        ProGpuDirect2DLayerParameters parameters)
    {
        ValidateRectangle(parameters.ContentBounds);
        const ProGpuDirect2DLayerOptions knownOptions =
            ProGpuDirect2DLayerOptions.InitializeFromBackground |
            ProGpuDirect2DLayerOptions.IgnoreAlpha;
        if (!float.IsFinite(parameters.Opacity) ||
            parameters.Opacity < 0.0F || parameters.Opacity > 1.0F ||
            parameters.MaskAntialiasMode >
                ProGpuDirect2DAntialiasMode.Aliased ||
            (parameters.Options & ~knownOptions) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameters),
                "Direct2D layer opacity, antialias mode, or options are invalid.");
        }
        if (parameters.MaskTransform.HasValue)
        {
            _ = CreateNativeMatrix(parameters.MaskTransform.Value);
        }
    }

    private static void ValidateTextFormatProperties(
        ProGpuDirect2DTextFormatProperties properties)
    {
        if (properties.FontWeight < 1U || properties.FontWeight > 999U ||
            properties.FontStyle > ProGpuDirect2DFontStyle.Italic ||
            properties.FontStretch <
                ProGpuDirect2DFontStretch.UltraCondensed ||
            properties.FontStretch >
                ProGpuDirect2DFontStretch.UltraExpanded ||
            !float.IsFinite(properties.FontSize) ||
            properties.FontSize <= 0.0F ||
            properties.TextAlignment >
                ProGpuDirect2DTextAlignment.Justified ||
            properties.ParagraphAlignment >
                ProGpuDirect2DParagraphAlignment.Center ||
            properties.WordWrapping >
                ProGpuDirect2DWordWrapping.Character ||
            properties.ReadingDirection >
                ProGpuDirect2DReadingDirection.BottomToTop ||
            properties.FlowDirection >
                ProGpuDirect2DFlowDirection.RightToLeft ||
            !float.IsFinite(properties.IncrementalTabStop) ||
            properties.IncrementalTabStop < 0.0F)
        {
            throw new ArgumentOutOfRangeException(
                nameof(properties),
                "DirectWrite text-format state contains an invalid value.");
        }
    }

    private static void ValidateFontFaceProperties(
        ProGpuDirect2DFontFaceProperties properties)
    {
        if (properties.FontWeight < 1U || properties.FontWeight > 999U ||
            properties.FontStyle > ProGpuDirect2DFontStyle.Italic ||
            properties.FontStretch <
                ProGpuDirect2DFontStretch.UltraCondensed ||
            properties.FontStretch >
                ProGpuDirect2DFontStretch.UltraExpanded)
        {
            throw new ArgumentOutOfRangeException(
                nameof(properties),
                "DirectWrite font-face matching state contains an invalid value.");
        }
    }

    private void ValidateTextRangeFormat(
        uint rangeStart,
        uint rangeLength,
        ProGpuDirect2DTextRangeFormat formatting,
        ProGpuDirect2DComReference? drawingEffectBrush)
    {
        const ProGpuDirect2DTextRangeFormatFlags knownFlags =
            ProGpuDirect2DTextRangeFormatFlags.FontSize |
            ProGpuDirect2DTextRangeFormatFlags.FontWeight |
            ProGpuDirect2DTextRangeFormatFlags.FontStyle |
            ProGpuDirect2DTextRangeFormatFlags.FontStretch |
            ProGpuDirect2DTextRangeFormatFlags.Underline |
            ProGpuDirect2DTextRangeFormatFlags.Strikethrough |
            ProGpuDirect2DTextRangeFormatFlags.DrawingEffect;
        if (formatting.Flags == ProGpuDirect2DTextRangeFormatFlags.None ||
            (formatting.Flags & ~knownFlags) != 0 ||
            rangeLength == 0U || rangeStart > uint.MaxValue - rangeLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(formatting),
                "DirectWrite range formatting needs known flags and a nonempty, nonoverflowing range.");
        }
        if ((formatting.Flags &
                ProGpuDirect2DTextRangeFormatFlags.FontSize) != 0 &&
            (!float.IsFinite(formatting.FontSize) ||
                formatting.FontSize <= 0.0F) ||
            (formatting.Flags &
                ProGpuDirect2DTextRangeFormatFlags.FontWeight) != 0 &&
            (formatting.FontWeight < 1U || formatting.FontWeight > 999U) ||
            (formatting.Flags &
                ProGpuDirect2DTextRangeFormatFlags.FontStyle) != 0 &&
            formatting.FontStyle > ProGpuDirect2DFontStyle.Italic ||
            (formatting.Flags &
                ProGpuDirect2DTextRangeFormatFlags.FontStretch) != 0 &&
            (formatting.FontStretch <
                ProGpuDirect2DFontStretch.UltraCondensed ||
             formatting.FontStretch >
                ProGpuDirect2DFontStretch.UltraExpanded))
        {
            throw new ArgumentOutOfRangeException(
                nameof(formatting),
                "DirectWrite range formatting contains an invalid selected value.");
        }
        bool appliesDrawingEffect = (formatting.Flags &
            ProGpuDirect2DTextRangeFormatFlags.DrawingEffect) != 0;
        if (!appliesDrawingEffect && drawingEffectBrush is not null)
        {
            throw new ArgumentException(
                "A drawing-effect brush requires the DrawingEffect flag.",
                nameof(drawingEffectBrush));
        }
        if (drawingEffectBrush is not null &&
            !IsBrushKind(drawingEffectBrush.InterfaceKind))
        {
            throw new ArgumentException(
                "The drawing effect must own a genuine ID2D1Brush.",
                nameof(drawingEffectBrush));
        }
        if (drawingEffectBrush is not null)
        {
            ValidateResourceDomain(
                drawingEffectBrush,
                nameof(drawingEffectBrush));
        }
    }

    private void ValidateStrokeStyle(
        ProGpuDirect2DStrokeStyleProperties properties,
        ReadOnlySpan<float> customDashes)
    {
        if (properties.StartCap > ProGpuDirect2DCapStyle.Triangle ||
            properties.EndCap > ProGpuDirect2DCapStyle.Triangle ||
            properties.DashCap > ProGpuDirect2DCapStyle.Triangle ||
            properties.LineJoin > ProGpuDirect2DLineJoin.MiterOrBevel ||
            !float.IsFinite(properties.MiterLimit) ||
            properties.MiterLimit <= 0.0F ||
            properties.DashStyle > ProGpuDirect2DDashStyle.Custom ||
            !float.IsFinite(properties.DashOffset) ||
            properties.TransformType >
                ProGpuDirect2DStrokeTransformType.Hairline)
        {
            throw new ArgumentOutOfRangeException(
                nameof(properties),
                "Direct2D stroke-style metadata is invalid.");
        }
        if ((properties.DashStyle == ProGpuDirect2DDashStyle.Custom) !=
            !customDashes.IsEmpty)
        {
            throw new ArgumentException(
                "Custom Direct2D dash styles require a nonempty dash span, and predefined styles reject one.",
                nameof(customDashes));
        }
        bool hasPositiveDash = false;
        foreach (float dash in customDashes)
        {
            if (!float.IsFinite(dash) || dash < 0.0F)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(customDashes),
                    "Direct2D custom dash lengths must be finite and nonnegative.");
            }
            hasPositiveDash |= dash > 0.0F;
        }
        if (!customDashes.IsEmpty && !hasPositiveDash)
        {
            throw new ArgumentException(
                "A custom Direct2D dash pattern must contain a positive length.",
                nameof(customDashes));
        }
    }

    private static void ValidateBitmapBrushProperties(
        ProGpuDirect2DBitmapBrushProperties properties)
    {
        if (properties.ExtendModeX > ProGpuDirect2DExtendMode.Mirror ||
            properties.ExtendModeY > ProGpuDirect2DExtendMode.Mirror ||
            properties.InterpolationMode >
                ProGpuDirect2DInterpolationMode.HighQualityCubic)
        {
            throw new ArgumentOutOfRangeException(
                nameof(properties),
                "Direct2D bitmap-brush tiling or interpolation metadata is invalid.");
        }
    }

    private static void ValidateImageBrushProperties(
        ProGpuDirect2DImageBrushProperties properties)
    {
        ValidateRectangle(properties.SourceRectangle);
        if (properties.SourceRectangle.Width <= 0.0F ||
            properties.SourceRectangle.Height <= 0.0F ||
            properties.ExtendModeX > ProGpuDirect2DExtendMode.Mirror ||
            properties.ExtendModeY > ProGpuDirect2DExtendMode.Mirror ||
            properties.InterpolationMode >
                ProGpuDirect2DInterpolationMode.HighQualityCubic)
        {
            throw new ArgumentOutOfRangeException(
                nameof(properties),
                "Direct2D image-brush source, tiling, or interpolation metadata is invalid.");
        }
    }

    private void ValidateEffect(
        ProGpuDirect2DComReference effect,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(effect, parameterName);
        ValidateResourceDomain(effect, parameterName);
        if (effect.InterfaceKind != ProGpuDirect2DInterfaceKind.D2D1Effect)
        {
            throw new ArgumentException(
                "The COM reference must own an ID2D1Effect.",
                parameterName);
        }
    }

    private static void ValidateEffectProperty(
        ProGpuDirect2DEffectPropertyType propertyType,
        int dataSize)
    {
        int expectedSize = propertyType switch
        {
            ProGpuDirect2DEffectPropertyType.Bool or
            ProGpuDirect2DEffectPropertyType.UInt32 or
            ProGpuDirect2DEffectPropertyType.Int32 or
            ProGpuDirect2DEffectPropertyType.Float or
            ProGpuDirect2DEffectPropertyType.Enum => sizeof(uint),
            ProGpuDirect2DEffectPropertyType.Vector2 => sizeof(float) * 2,
            ProGpuDirect2DEffectPropertyType.Vector3 => sizeof(float) * 3,
            ProGpuDirect2DEffectPropertyType.Vector4 or
            ProGpuDirect2DEffectPropertyType.Clsid => sizeof(float) * 4,
            ProGpuDirect2DEffectPropertyType.Matrix3X2 => sizeof(float) * 6,
            ProGpuDirect2DEffectPropertyType.Matrix4X3 => sizeof(float) * 12,
            ProGpuDirect2DEffectPropertyType.Matrix4X4 => sizeof(float) * 16,
            ProGpuDirect2DEffectPropertyType.Matrix5X4 => sizeof(float) * 20,
            ProGpuDirect2DEffectPropertyType.Blob when dataSize > 0 =>
                dataSize,
            _ => 0
        };
        if (expectedSize == 0 || expectedSize != dataSize)
        {
            throw new ArgumentException(
                "The Direct2D effect property payload size does not match its fixed-layout type.",
                nameof(dataSize));
        }
    }

    private static bool IsImageKind(ProGpuDirect2DInterfaceKind kind) =>
        kind is ProGpuDirect2DInterfaceKind.D2D1Bitmap or
            ProGpuDirect2DInterfaceKind.D2D1Bitmap1 or
            ProGpuDirect2DInterfaceKind.D2D1CommandList or
            ProGpuDirect2DInterfaceKind.D2D1Image;

    private static bool IsBrushKind(ProGpuDirect2DInterfaceKind kind) =>
        kind is ProGpuDirect2DInterfaceKind.D2D1SolidColorBrush or
            ProGpuDirect2DInterfaceKind.D2D1LinearGradientBrush or
            ProGpuDirect2DInterfaceKind.D2D1RadialGradientBrush or
            ProGpuDirect2DInterfaceKind.D2D1BitmapBrush1 or
            ProGpuDirect2DInterfaceKind.D2D1ImageBrush;

    private static bool IsGeometryKind(ProGpuDirect2DInterfaceKind kind) =>
        kind >= ProGpuDirect2DInterfaceKind.D2D1Geometry &&
        kind <= ProGpuDirect2DInterfaceKind.D2D1TransformedGeometry;

    private static bool IsCompatibleInterfaceKind(
        ProGpuDirect2DInterfaceKind actual,
        ProGpuDirect2DInterfaceKind expected) =>
        actual == expected ||
        (expected == ProGpuDirect2DInterfaceKind.D2D1Geometry &&
         IsGeometryKind(actual)) ||
        (expected == ProGpuDirect2DInterfaceKind.D2D1ImageBrush &&
         actual == ProGpuDirect2DInterfaceKind.D2D1BitmapBrush1);

    private static ProGpuDirect2DNative.NativePoint2F CreateNativePoint(
        Vector2 point) =>
        new() { X = point.X, Y = point.Y };

    private static ProGpuDirect2DNative.NativeMatrix3X2F CreateNativeMatrix(
        Matrix3x2 matrix)
    {
        if (!float.IsFinite(matrix.M11) ||
            !float.IsFinite(matrix.M12) ||
            !float.IsFinite(matrix.M21) ||
            !float.IsFinite(matrix.M22) ||
            !float.IsFinite(matrix.M31) ||
            !float.IsFinite(matrix.M32))
        {
            throw new ArgumentOutOfRangeException(
                nameof(matrix),
                "Direct2D transforms must be finite.");
        }
        return new ProGpuDirect2DNative.NativeMatrix3X2F
        {
            M11 = matrix.M11,
            M12 = matrix.M12,
            M21 = matrix.M21,
            M22 = matrix.M22,
            M31 = matrix.M31,
            M32 = matrix.M32
        };
    }

    private static ProGpuDirect2DNative.NativeMatrix4X4F CreateNativeMatrix(
        Matrix4x4 matrix)
    {
        if (!float.IsFinite(matrix.M11) ||
            !float.IsFinite(matrix.M12) ||
            !float.IsFinite(matrix.M13) ||
            !float.IsFinite(matrix.M14) ||
            !float.IsFinite(matrix.M21) ||
            !float.IsFinite(matrix.M22) ||
            !float.IsFinite(matrix.M23) ||
            !float.IsFinite(matrix.M24) ||
            !float.IsFinite(matrix.M31) ||
            !float.IsFinite(matrix.M32) ||
            !float.IsFinite(matrix.M33) ||
            !float.IsFinite(matrix.M34) ||
            !float.IsFinite(matrix.M41) ||
            !float.IsFinite(matrix.M42) ||
            !float.IsFinite(matrix.M43) ||
            !float.IsFinite(matrix.M44))
        {
            throw new ArgumentOutOfRangeException(
                nameof(matrix),
                "Direct2D perspective transforms must be finite.");
        }
        return new ProGpuDirect2DNative.NativeMatrix4X4F
        {
            M11 = matrix.M11,
            M12 = matrix.M12,
            M13 = matrix.M13,
            M14 = matrix.M14,
            M21 = matrix.M21,
            M22 = matrix.M22,
            M23 = matrix.M23,
            M24 = matrix.M24,
            M31 = matrix.M31,
            M32 = matrix.M32,
            M33 = matrix.M33,
            M34 = matrix.M34,
            M41 = matrix.M41,
            M42 = matrix.M42,
            M43 = matrix.M43,
            M44 = matrix.M44
        };
    }

    private void ValidateResourceDomain(
        ProGpuDirect2DComReference resource,
        string parameterName)
    {
        if (resource.ResourceGeneration != ResourceGeneration)
        {
            throw new ArgumentException(
                $"The COM reference belongs to Direct2D resource generation {resource.ResourceGeneration}, but this surface owns generation {ResourceGeneration}. Recreate the resource on the current device domain.",
                parameterName);
        }
    }

    private static float ConvertFiniteFloat(double value, string parameterName)
    {
        float result = (float)value;
        if (!double.IsFinite(value) || !float.IsFinite(result))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Portable geometry values must fit finite Direct2D floats.");
        }
        return result;
    }

    private static Vector2 ConvertPoint(PortablePoint point) =>
        new(
            ConvertFiniteFloat(point.X, nameof(point)),
            ConvertFiniteFloat(point.Y, nameof(point)));

    private static ProGpuDirect2DRect ConvertRect(PortableRect rectangle)
    {
        if (rectangle.IsEmpty)
        {
            throw new ArgumentException(
                "An empty portable rectangle cannot create a Direct2D geometry.",
                nameof(rectangle));
        }
        return new ProGpuDirect2DRect(
            ConvertFiniteFloat(rectangle.X, nameof(rectangle)),
            ConvertFiniteFloat(rectangle.Y, nameof(rectangle)),
            ConvertFiniteFloat(rectangle.Width, nameof(rectangle)),
            ConvertFiniteFloat(rectangle.Height, nameof(rectangle)));
    }

    private static Matrix3x2 ConvertMatrix(PortableMatrix3x2 matrix) =>
        new(
            ConvertFiniteFloat(matrix.M11, nameof(matrix)),
            ConvertFiniteFloat(matrix.M12, nameof(matrix)),
            ConvertFiniteFloat(matrix.M21, nameof(matrix)),
            ConvertFiniteFloat(matrix.M22, nameof(matrix)),
            ConvertFiniteFloat(matrix.OffsetX, nameof(matrix)),
            ConvertFiniteFloat(matrix.OffsetY, nameof(matrix)));

    private static ProGpuDirect2DPathSegment ConvertSegment(
        PortablePathSegment segment)
    {
        if (segment.Kind == PortablePathSegmentKind.Arc &&
            segment.SweepDirection is not PortableSweepDirection.Clockwise and
            not PortableSweepDirection.Counterclockwise)
        {
            throw new ArgumentOutOfRangeException(
                nameof(segment),
                "The portable arc sweep direction is unknown.");
        }
        if (segment.Kind == PortablePathSegmentKind.Arc &&
            (segment.Size.Width < 0.0 || segment.Size.Height < 0.0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(segment),
                "Portable arc radii must be nonnegative.");
        }
        ProGpuDirect2DPathSegmentFlags flags =
            (segment.IsStroked
                ? ProGpuDirect2DPathSegmentFlags.None
                : ProGpuDirect2DPathSegmentFlags.ForceUnstroked) |
            (segment.IsSmoothJoin
                ? ProGpuDirect2DPathSegmentFlags.ForceRoundLineJoin
                : ProGpuDirect2DPathSegmentFlags.None);
        return segment.Kind switch
        {
            PortablePathSegmentKind.Line =>
                ProGpuDirect2DPathSegment.Line(
                    ConvertPoint(segment.Point1),
                    flags),
            PortablePathSegmentKind.QuadraticBezier =>
                ProGpuDirect2DPathSegment.Quadratic(
                    ConvertPoint(segment.Point1),
                    ConvertPoint(segment.Point2),
                    flags),
            PortablePathSegmentKind.CubicBezier =>
                ProGpuDirect2DPathSegment.Cubic(
                    ConvertPoint(segment.Point1),
                    ConvertPoint(segment.Point2),
                    ConvertPoint(segment.Point3),
                    flags),
            PortablePathSegmentKind.Arc =>
                ProGpuDirect2DPathSegment.Arc(
                    ConvertPoint(segment.Point1),
                    new Vector2(
                        ConvertFiniteFloat(
                            segment.Size.Width,
                            nameof(segment)),
                        ConvertFiniteFloat(
                            segment.Size.Height,
                            nameof(segment))),
                    ConvertFiniteFloat(
                        segment.RotationAngle,
                        nameof(segment)),
                    (segment.SweepDirection ==
                        PortableSweepDirection.Clockwise
                            ? ProGpuDirect2DArcFlags.Clockwise
                            : ProGpuDirect2DArcFlags.None) |
                    (segment.IsLargeArc
                        ? ProGpuDirect2DArcFlags.Large
                        : ProGpuDirect2DArcFlags.None),
                    flags),
            _ => throw new ArgumentOutOfRangeException(nameof(segment))
        };
    }

    private ProGpuDirect2DComReference ApplyPortableTransform(
        ProGpuDirect2DComReference geometry,
        PortableMatrix3x2 transform)
    {
        if (transform.IsIdentity)
        {
            return geometry;
        }
        try
        {
            return CreateTransformedGeometry(
                geometry,
                ConvertMatrix(transform));
        }
        finally
        {
            geometry.Dispose();
        }
    }

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
        ProGpuDirect2DNative.NativeMatrix3X2F nativeMatrix =
            CreateNativeMatrix(matrix);
        return new ProGpuDirect2DNative.NativeBrushProperties
        {
            Opacity = opacity,
            Transform = nativeMatrix
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
        ThrowIfFailedDuringCreate(
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

    private static ProGpuDirect2DDeviceLossState ReadDeviceLossState(
        nint nativeSurface)
    {
        var native = new ProGpuDirect2DNative.DeviceLossState
        {
            StructSize = (uint)Unsafe.SizeOf<
                ProGpuDirect2DNative.DeviceLossState>()
        };
        ProGpuDirect2DStatus status =
            ProGpuDirect2DNative.SurfaceGetDeviceLossState(
                nativeSurface,
                &native);
        ThrowIfFailedDuringCreate(
            "device-loss state query",
            status,
            ProGpuDirect2DNative.SurfaceGetLastHResult(nativeSurface));
        return new ProGpuDirect2DDeviceLossState(
            (ProGpuDirect2DDeviceLossFlags)native.Flags,
            native.ReasonHResult,
            native.ResourceGeneration);
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

    private void ThrowIfFailed(
        string operation,
        ProGpuDirect2DStatus status,
        int nativeHResult)
    {
        if (status == ProGpuDirect2DStatus.DeviceLost)
        {
            ObserveDeviceLoss(new ProGpuDirect2DDeviceLossState(
                ProGpuDirect2DDeviceLossFlags.DeviceLost,
                nativeHResult,
                ResourceGeneration));
        }
        ThrowIfFailedDuringCreate(operation, status, nativeHResult);
    }

    private static void ThrowIfFailedDuringCreate(
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

    public void Clear(ProGpuDirect2DColor? color = null) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .Clear(color);

    public Matrix3x2 Transform
    {
        get => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
            .GetTransform();
        set => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
            .SetTransform(value);
    }

    public ProGpuDirect2DAntialiasMode AntialiasMode
    {
        get => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession))).GetAntialiasMode();
        set => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession))).SetAntialiasMode(value);
    }

    public ProGpuDirect2DTextAntialiasMode TextAntialiasMode
    {
        get => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession))).GetTextAntialiasMode();
        set => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession))).SetTextAntialiasMode(value);
    }

    public ProGpuDirect2DPrimitiveBlend PrimitiveBlend
    {
        get => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession))).GetPrimitiveBlend();
        set => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession))).SetPrimitiveBlend(value);
    }

    public ProGpuDirect2DUnitMode UnitMode
    {
        get => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession))).GetUnitMode();
        set => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession))).SetUnitMode(value);
    }

    public ProGpuDirect2DTags Tags
    {
        get => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession))).GetTags();
        set => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession))).SetTags(value);
    }

    public Vector2 Dpi
    {
        get => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession))).GetDpi();
        set => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession))).SetDpi(value);
    }

    public ProGpuDirect2DAxisAlignedClipScope PushAxisAlignedClip(
        ProGpuDirect2DRect clipRectangle,
        ProGpuDirect2DAntialiasMode antialiasMode =
            ProGpuDirect2DAntialiasMode.PerPrimitive)
    {
        ProGpuDirect2DSurface owner = _owner ??
            throw new ObjectDisposedException(
                nameof(ProGpuDirect2DDrawingSession));
        uint depth = owner.PushAxisAlignedClip(
            clipRectangle,
            antialiasMode);
        return new ProGpuDirect2DAxisAlignedClipScope(owner, depth);
    }

    public void DrawBitmap(
        ProGpuDirect2DComReference bitmap,
        ProGpuDirect2DRect? destinationRectangle = null,
        float opacity = 1.0F,
        ProGpuDirect2DInterpolationMode interpolationMode =
            ProGpuDirect2DInterpolationMode.Linear,
        ProGpuDirect2DRect? sourceRectangle = null,
        Matrix4x4? perspectiveTransform = null) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .DrawBitmap(
            bitmap,
            destinationRectangle,
            opacity,
            interpolationMode,
            sourceRectangle,
            perspectiveTransform);

    public void DrawImage(
        ProGpuDirect2DComReference image,
        Vector2? targetOffset = null,
        ProGpuDirect2DRect? imageRectangle = null,
        ProGpuDirect2DInterpolationMode interpolationMode =
            ProGpuDirect2DInterpolationMode.Linear,
        ProGpuDirect2DCompositeMode compositeMode =
            ProGpuDirect2DCompositeMode.SourceOver) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .DrawImage(
            image,
            targetOffset,
            imageRectangle,
            interpolationMode,
            compositeMode);

    public void DrawLine(
        Vector2 point0,
        Vector2 point1,
        ProGpuDirect2DComReference brush,
        float strokeWidth = 1.0F,
        ProGpuDirect2DComReference? strokeStyle = null) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .DrawLine(point0, point1, brush, strokeWidth, strokeStyle);

    public void DrawRectangle(
        ProGpuDirect2DRect rectangle,
        ProGpuDirect2DComReference brush,
        float strokeWidth = 1.0F,
        ProGpuDirect2DComReference? strokeStyle = null) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .DrawRectangle(rectangle, brush, strokeWidth, strokeStyle);

    public void FillRectangle(
        ProGpuDirect2DRect rectangle,
        ProGpuDirect2DComReference brush) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .FillRectangle(rectangle, brush);

    public void DrawRoundedRectangle(
        ProGpuDirect2DRect rectangle,
        float radiusX,
        float radiusY,
        ProGpuDirect2DComReference brush,
        float strokeWidth = 1.0F,
        ProGpuDirect2DComReference? strokeStyle = null) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .DrawRoundedRectangle(
            rectangle,
            radiusX,
            radiusY,
            brush,
            strokeWidth,
            strokeStyle);

    public void FillRoundedRectangle(
        ProGpuDirect2DRect rectangle,
        float radiusX,
        float radiusY,
        ProGpuDirect2DComReference brush) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .FillRoundedRectangle(rectangle, radiusX, radiusY, brush);

    public void DrawEllipse(
        Vector2 center,
        float radiusX,
        float radiusY,
        ProGpuDirect2DComReference brush,
        float strokeWidth = 1.0F,
        ProGpuDirect2DComReference? strokeStyle = null) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .DrawEllipse(
            center,
            radiusX,
            radiusY,
            brush,
            strokeWidth,
            strokeStyle);

    public void FillEllipse(
        Vector2 center,
        float radiusX,
        float radiusY,
        ProGpuDirect2DComReference brush) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .FillEllipse(center, radiusX, radiusY, brush);

    public void DrawGeometry(
        ProGpuDirect2DComReference geometry,
        ProGpuDirect2DComReference brush,
        float strokeWidth = 1.0F,
        ProGpuDirect2DComReference? strokeStyle = null) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .DrawGeometry(geometry, brush, strokeWidth, strokeStyle);

    public void FillGeometry(
        ProGpuDirect2DComReference geometry,
        ProGpuDirect2DComReference brush,
        ProGpuDirect2DComReference? opacityBrush = null) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .FillGeometry(geometry, brush, opacityBrush);


    public ProGpuDirect2DLayerScope PushLayer(
        ProGpuDirect2DComReference layer,
        ProGpuDirect2DLayerParameters parameters,
        ProGpuDirect2DComReference? geometricMask = null,
        ProGpuDirect2DComReference? opacityBrush = null)
    {
        ProGpuDirect2DSurface owner = _owner ??
            throw new ObjectDisposedException(
                nameof(ProGpuDirect2DDrawingSession));
        uint depth = owner.PushLayer(
            layer,
            parameters,
            geometricMask,
            opacityBrush);
        return new ProGpuDirect2DLayerScope(owner, depth);
    }

    public void SaveDrawingState(
        ProGpuDirect2DComReference drawingStateBlock) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .SaveDrawingState(drawingStateBlock);

    public void RestoreDrawingState(
        ProGpuDirect2DComReference drawingStateBlock) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .RestoreDrawingState(drawingStateBlock);

    public void DrawText(
        ReadOnlySpan<char> text,
        ProGpuDirect2DComReference textFormat,
        ProGpuDirect2DRect layoutRectangle,
        ProGpuDirect2DComReference defaultFillBrush,
        ProGpuDirect2DDrawTextOptions options =
            ProGpuDirect2DDrawTextOptions.None,
        ProGpuDirect2DMeasuringMode measuringMode =
            ProGpuDirect2DMeasuringMode.Natural) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .DrawText(
            text,
            textFormat,
            layoutRectangle,
            defaultFillBrush,
            options,
            measuringMode);

    public void DrawTextLayout(
        Vector2 origin,
        ProGpuDirect2DComReference textLayout,
        ProGpuDirect2DComReference defaultFillBrush,
        ProGpuDirect2DDrawTextOptions options =
            ProGpuDirect2DDrawTextOptions.None) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .DrawTextLayout(
            origin,
            textLayout,
            defaultFillBrush,
            options);

    public void DrawGlyphRun(
        Vector2 baselineOrigin,
        float fontEmSize,
        ProGpuDirect2DComReference fontFace,
        ReadOnlySpan<ushort> glyphIndices,
        ReadOnlySpan<float> glyphAdvances,
        ReadOnlySpan<ProGpuDirect2DGlyphOffset> glyphOffsets,
        ProGpuDirect2DComReference foregroundBrush,
        bool isSideways = false,
        uint bidiLevel = 0U,
        ProGpuDirect2DMeasuringMode measuringMode =
            ProGpuDirect2DMeasuringMode.Natural) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .DrawGlyphRun(
            baselineOrigin,
            fontEmSize,
            fontFace,
            glyphIndices,
            glyphAdvances,
            glyphOffsets,
            foregroundBrush,
            isSideways,
            bidiLevel,
            measuringMode);

    public ProGpuDirect2DColorGlyphPath DrawColorGlyphRun(
        Vector2 baselineOrigin,
        float fontEmSize,
        ProGpuDirect2DComReference fontFace,
        ReadOnlySpan<ushort> glyphIndices,
        ReadOnlySpan<float> glyphAdvances,
        ReadOnlySpan<ProGpuDirect2DGlyphOffset> glyphOffsets,
        ProGpuDirect2DComReference foregroundBrush,
        uint colorPaletteIndex = 0U,
        bool isSideways = false,
        uint bidiLevel = 0U,
        ProGpuDirect2DMeasuringMode measuringMode =
            ProGpuDirect2DMeasuringMode.Natural) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .DrawColorGlyphRun(
            baselineOrigin,
            fontEmSize,
            fontFace,
            glyphIndices,
            glyphAdvances,
            glyphOffsets,
            foregroundBrush,
            colorPaletteIndex,
            isSideways,
            bidiLevel,
            measuringMode);

    public void DrawSvgDocument(
        ProGpuDirect2DComReference svgDocument,
        Vector2 viewportSize,
        Vector2 origin = default) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .DrawSvgDocument(svgDocument, viewportSize, origin);

    public void DrawGeometryRealization(
        ProGpuDirect2DComReference realization,
        ProGpuDirect2DComReference brush) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DDrawingSession)))
        .DrawGeometryRealization(realization, brush);

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
/// Owns one exclusive ID2D1CommandList recording transaction. The caller owns
/// the command-list reference; this scope keeps an additional reference until
/// EndDraw restores the shared target and closes the list.
/// </summary>
public sealed class ProGpuDirect2DCommandListDrawingSession : IDisposable
{
    private ProGpuDirect2DSurface? _owner;
    private readonly bool _commandListReferenceAdded;

    internal ProGpuDirect2DCommandListDrawingSession(
        ProGpuDirect2DSurface owner,
        ProGpuDirect2DComReference deviceContext,
        ProGpuDirect2DComReference commandList,
        bool commandListReferenceAdded)
    {
        _owner = owner;
        DeviceContext = deviceContext;
        CommandList = commandList;
        _commandListReferenceAdded = commandListReferenceAdded;
    }

    public ProGpuDirect2DComReference DeviceContext { get; }

    public ProGpuDirect2DComReference CommandList { get; }

    public void Clear(ProGpuDirect2DColor? color = null) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .Clear(color);

    public Matrix3x2 Transform
    {
        get => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
            .GetTransform();
        set => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
            .SetTransform(value);
    }

    public ProGpuDirect2DAntialiasMode AntialiasMode
    {
        get => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession))).GetAntialiasMode();
        set => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession))).SetAntialiasMode(value);
    }

    public ProGpuDirect2DTextAntialiasMode TextAntialiasMode
    {
        get => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession))).GetTextAntialiasMode();
        set => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession))).SetTextAntialiasMode(value);
    }

    public ProGpuDirect2DPrimitiveBlend PrimitiveBlend
    {
        get => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession))).GetPrimitiveBlend();
        set => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession))).SetPrimitiveBlend(value);
    }

    public ProGpuDirect2DUnitMode UnitMode
    {
        get => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession))).GetUnitMode();
        set => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession))).SetUnitMode(value);
    }

    public ProGpuDirect2DTags Tags
    {
        get => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession))).GetTags();
        set => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession))).SetTags(value);
    }

    public Vector2 Dpi
    {
        get => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession))).GetDpi();
        set => (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession))).SetDpi(value);
    }

    public ProGpuDirect2DAxisAlignedClipScope PushAxisAlignedClip(
        ProGpuDirect2DRect clipRectangle,
        ProGpuDirect2DAntialiasMode antialiasMode =
            ProGpuDirect2DAntialiasMode.PerPrimitive)
    {
        ProGpuDirect2DSurface owner = _owner ??
            throw new ObjectDisposedException(
                nameof(ProGpuDirect2DCommandListDrawingSession));
        uint depth = owner.PushAxisAlignedClip(
            clipRectangle,
            antialiasMode);
        return new ProGpuDirect2DAxisAlignedClipScope(owner, depth);
    }

    public void DrawBitmap(
        ProGpuDirect2DComReference bitmap,
        ProGpuDirect2DRect? destinationRectangle = null,
        float opacity = 1.0F,
        ProGpuDirect2DInterpolationMode interpolationMode =
            ProGpuDirect2DInterpolationMode.Linear,
        ProGpuDirect2DRect? sourceRectangle = null,
        Matrix4x4? perspectiveTransform = null) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .DrawBitmap(
            bitmap,
            destinationRectangle,
            opacity,
            interpolationMode,
            sourceRectangle,
            perspectiveTransform);

    public void DrawImage(
        ProGpuDirect2DComReference image,
        Vector2? targetOffset = null,
        ProGpuDirect2DRect? imageRectangle = null,
        ProGpuDirect2DInterpolationMode interpolationMode =
            ProGpuDirect2DInterpolationMode.Linear,
        ProGpuDirect2DCompositeMode compositeMode =
            ProGpuDirect2DCompositeMode.SourceOver) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .DrawImage(
            image,
            targetOffset,
            imageRectangle,
            interpolationMode,
            compositeMode);

    public void DrawLine(
        Vector2 point0,
        Vector2 point1,
        ProGpuDirect2DComReference brush,
        float strokeWidth = 1.0F,
        ProGpuDirect2DComReference? strokeStyle = null) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .DrawLine(point0, point1, brush, strokeWidth, strokeStyle);

    public void DrawRectangle(
        ProGpuDirect2DRect rectangle,
        ProGpuDirect2DComReference brush,
        float strokeWidth = 1.0F,
        ProGpuDirect2DComReference? strokeStyle = null) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .DrawRectangle(rectangle, brush, strokeWidth, strokeStyle);

    public void FillRectangle(
        ProGpuDirect2DRect rectangle,
        ProGpuDirect2DComReference brush) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .FillRectangle(rectangle, brush);

    public void DrawRoundedRectangle(
        ProGpuDirect2DRect rectangle,
        float radiusX,
        float radiusY,
        ProGpuDirect2DComReference brush,
        float strokeWidth = 1.0F,
        ProGpuDirect2DComReference? strokeStyle = null) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .DrawRoundedRectangle(
            rectangle,
            radiusX,
            radiusY,
            brush,
            strokeWidth,
            strokeStyle);

    public void FillRoundedRectangle(
        ProGpuDirect2DRect rectangle,
        float radiusX,
        float radiusY,
        ProGpuDirect2DComReference brush) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .FillRoundedRectangle(rectangle, radiusX, radiusY, brush);

    public void DrawEllipse(
        Vector2 center,
        float radiusX,
        float radiusY,
        ProGpuDirect2DComReference brush,
        float strokeWidth = 1.0F,
        ProGpuDirect2DComReference? strokeStyle = null) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .DrawEllipse(
            center,
            radiusX,
            radiusY,
            brush,
            strokeWidth,
            strokeStyle);

    public void FillEllipse(
        Vector2 center,
        float radiusX,
        float radiusY,
        ProGpuDirect2DComReference brush) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .FillEllipse(center, radiusX, radiusY, brush);

    public void DrawGeometry(
        ProGpuDirect2DComReference geometry,
        ProGpuDirect2DComReference brush,
        float strokeWidth = 1.0F,
        ProGpuDirect2DComReference? strokeStyle = null) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .DrawGeometry(geometry, brush, strokeWidth, strokeStyle);

    public void FillGeometry(
        ProGpuDirect2DComReference geometry,
        ProGpuDirect2DComReference brush,
        ProGpuDirect2DComReference? opacityBrush = null) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .FillGeometry(geometry, brush, opacityBrush);


    public ProGpuDirect2DLayerScope PushLayer(
        ProGpuDirect2DComReference layer,
        ProGpuDirect2DLayerParameters parameters,
        ProGpuDirect2DComReference? geometricMask = null,
        ProGpuDirect2DComReference? opacityBrush = null)
    {
        ProGpuDirect2DSurface owner = _owner ??
            throw new ObjectDisposedException(
                nameof(ProGpuDirect2DCommandListDrawingSession));
        uint depth = owner.PushLayer(
            layer,
            parameters,
            geometricMask,
            opacityBrush);
        return new ProGpuDirect2DLayerScope(owner, depth);
    }

    public void SaveDrawingState(
        ProGpuDirect2DComReference drawingStateBlock) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .SaveDrawingState(drawingStateBlock);

    public void RestoreDrawingState(
        ProGpuDirect2DComReference drawingStateBlock) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .RestoreDrawingState(drawingStateBlock);

    public void DrawText(
        ReadOnlySpan<char> text,
        ProGpuDirect2DComReference textFormat,
        ProGpuDirect2DRect layoutRectangle,
        ProGpuDirect2DComReference defaultFillBrush,
        ProGpuDirect2DDrawTextOptions options =
            ProGpuDirect2DDrawTextOptions.None,
        ProGpuDirect2DMeasuringMode measuringMode =
            ProGpuDirect2DMeasuringMode.Natural) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .DrawText(
            text,
            textFormat,
            layoutRectangle,
            defaultFillBrush,
            options,
            measuringMode);

    public void DrawTextLayout(
        Vector2 origin,
        ProGpuDirect2DComReference textLayout,
        ProGpuDirect2DComReference defaultFillBrush,
        ProGpuDirect2DDrawTextOptions options =
            ProGpuDirect2DDrawTextOptions.None) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .DrawTextLayout(
            origin,
            textLayout,
            defaultFillBrush,
            options);

    public void DrawGlyphRun(
        Vector2 baselineOrigin,
        float fontEmSize,
        ProGpuDirect2DComReference fontFace,
        ReadOnlySpan<ushort> glyphIndices,
        ReadOnlySpan<float> glyphAdvances,
        ReadOnlySpan<ProGpuDirect2DGlyphOffset> glyphOffsets,
        ProGpuDirect2DComReference foregroundBrush,
        bool isSideways = false,
        uint bidiLevel = 0U,
        ProGpuDirect2DMeasuringMode measuringMode =
            ProGpuDirect2DMeasuringMode.Natural) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .DrawGlyphRun(
            baselineOrigin,
            fontEmSize,
            fontFace,
            glyphIndices,
            glyphAdvances,
            glyphOffsets,
            foregroundBrush,
            isSideways,
            bidiLevel,
            measuringMode);

    public ProGpuDirect2DColorGlyphPath DrawColorGlyphRun(
        Vector2 baselineOrigin,
        float fontEmSize,
        ProGpuDirect2DComReference fontFace,
        ReadOnlySpan<ushort> glyphIndices,
        ReadOnlySpan<float> glyphAdvances,
        ReadOnlySpan<ProGpuDirect2DGlyphOffset> glyphOffsets,
        ProGpuDirect2DComReference foregroundBrush,
        uint colorPaletteIndex = 0U,
        bool isSideways = false,
        uint bidiLevel = 0U,
        ProGpuDirect2DMeasuringMode measuringMode =
            ProGpuDirect2DMeasuringMode.Natural) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .DrawColorGlyphRun(
            baselineOrigin,
            fontEmSize,
            fontFace,
            glyphIndices,
            glyphAdvances,
            glyphOffsets,
            foregroundBrush,
            colorPaletteIndex,
            isSideways,
            bidiLevel,
            measuringMode);

    public void DrawSvgDocument(
        ProGpuDirect2DComReference svgDocument,
        Vector2 viewportSize,
        Vector2 origin = default) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .DrawSvgDocument(svgDocument, viewportSize, origin);

    public void DrawGeometryRealization(
        ProGpuDirect2DComReference realization,
        ProGpuDirect2DComReference brush) =>
        (_owner ?? throw new ObjectDisposedException(
            nameof(ProGpuDirect2DCommandListDrawingSession)))
        .DrawGeometryRealization(realization, brush);

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
            owner.CompleteCommandListDrawing();
        }
        finally
        {
            DeviceContext.Dispose();
            if (_commandListReferenceAdded)
            {
                CommandList.DangerousRelease();
            }
        }
    }
}

/// <summary>
/// Allocation-free LIFO scope for one typed ID2D1Layer push. Dispose before
/// the owning drawing session; out-of-order or duplicate disposal fails
/// closed without popping a different layer.
/// </summary>
public ref struct ProGpuDirect2DLayerScope
{
    private ProGpuDirect2DSurface? _owner;
    private readonly uint _depth;

    internal ProGpuDirect2DLayerScope(
        ProGpuDirect2DSurface owner,
        uint depth)
    {
        _owner = owner;
        _depth = depth;
    }

    public void Dispose()
    {
        ProGpuDirect2DSurface? owner = _owner;
        if (owner is null)
        {
            return;
        }
        owner.PopLayer(_depth);
        _owner = null;
    }
}

/// <summary>
/// Allocation-free LIFO scope for one axis-aligned Direct2D clip. Clip and
/// layer scopes share one depth sequence, so cross-kind disposal is checked.
/// </summary>
public ref struct ProGpuDirect2DAxisAlignedClipScope
{
    private ProGpuDirect2DSurface? _owner;
    private readonly uint _depth;

    internal ProGpuDirect2DAxisAlignedClipScope(
        ProGpuDirect2DSurface owner,
        uint depth)
    {
        _owner = owner;
        _depth = depth;
    }

    public void Dispose()
    {
        ProGpuDirect2DSurface? owner = _owner;
        if (owner is null)
        {
            return;
        }
        owner.PopAxisAlignedClip(_depth);
        _owner = null;
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
