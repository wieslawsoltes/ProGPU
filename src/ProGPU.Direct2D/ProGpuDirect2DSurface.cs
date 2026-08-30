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
    /// rectangle. Provider-created bitmap images are supported by the current
    /// typed surface contract; effects and command lists can use this same
    /// native ABI when their typed resource factories are added.
    /// </summary>
    public ProGpuDirect2DComReference CreateImageBrush(
        ProGpuDirect2DComReference image,
        ProGpuDirect2DImageBrushProperties properties,
        float opacity = 1.0F,
        Matrix3x2? transform = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.InterfaceKind != ProGpuDirect2DInterfaceKind.D2D1Bitmap &&
            image.InterfaceKind != ProGpuDirect2DInterfaceKind.D2D1Bitmap1 &&
            image.InterfaceKind !=
                ProGpuDirect2DInterfaceKind.D2D1CommandList)
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
                if (status == ProGpuDirect2DStatus.DeviceLost)
                {
                    _disposeRequested = true;
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

    private static void ValidateGeometry(
        ProGpuDirect2DComReference geometry,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(geometry, parameterName);
        if (!IsGeometryKind(geometry.InterfaceKind))
        {
            throw new ArgumentException(
                "The COM reference must own a Direct2D geometry.",
                parameterName);
        }
    }

    private static void ValidateStrokeStyle(
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
