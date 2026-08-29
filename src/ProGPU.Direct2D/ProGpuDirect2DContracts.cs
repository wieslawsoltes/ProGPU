using Microsoft.Win32.SafeHandles;

namespace ProGPU.Direct2D;

[Flags]
public enum ProGpuDirect2DSurfaceFlags : uint
{
    None = 0,
    EnableDebug = 1U << 0,
    AllowWarpFallback = 1U << 1,
    ForceWarp = 1U << 2
}

[Flags]
public enum ProGpuDirect2DDescriptorFlags : uint
{
    None = 0,
    KeyedMutex = 1U << 0,
    NtHandle = 1U << 1,
    SoftwareAdapter = 1U << 2
}

public enum ProGpuDirect2DInterfaceKind
{
    D3D11Device = 1,
    D3D11DeviceContext = 2,
    DxgiAdapter1 = 3,
    DxgiDevice = 4,
    DxgiSurface = 5,
    DxgiKeyedMutex = 6,
    D3D11Texture2D = 7,
    D2D1Factory1 = 8,
    D2D1Factory2 = 9,
    D2D1Device = 10,
    D2D1Device1 = 11,
    D2D1DeviceContext = 12,
    D2D1DeviceContext1 = 13,
    D2D1Bitmap = 14,
    D2D1Bitmap1 = 15,
    WinRtDirect3D11Device = 16,
    Win2DCanvasDevice = 17,
    Win2DCanvasRenderTarget = 18
}

public enum ProGpuDirect2DStatus
{
    Success = 0,
    InvalidArgument = 1,
    OutOfMemory = 2,
    AdapterNotFound = 3,
    DeviceCreationFailed = 4,
    ResourceCreationFailed = 5,
    SynchronizationFailed = 6,
    AccessAlreadyAcquired = 7,
    AccessNotAcquired = 8,
    DeviceLost = 9,
    DrawAlreadyActive = 10,
    DrawNotActive = 11,
    DrawFailed = 12,
    InterfaceNotSupported = 13,
    Win2DRuntimeUnavailable = 14,
    WindowsRuntimeNotInitialized = 15
}

public sealed record ProGpuDirect2DSurfaceOptions(
    uint Width,
    uint Height,
    float DpiX = 96.0F,
    float DpiY = 96.0F,
    ProGpuDirect2DSurfaceFlags Flags =
        ProGpuDirect2DSurfaceFlags.AllowWarpFallback,
    long? AdapterLuid = null);

public readonly record struct ProGpuDirect2DSurfaceDescriptor(
    ProGpuDirect2DDescriptorFlags Flags,
    uint Width,
    uint Height,
    float DpiX,
    float DpiY,
    uint DxgiFormat,
    uint AlphaMode,
    long AdapterLuid,
    nint SharedNtHandle,
    ulong InitialAcquireKey,
    ulong InitialReleaseKey,
    ulong ContentVersion);

public sealed class ProGpuDirect2DException : Exception
{
    internal ProGpuDirect2DException(
        string operation,
        ProGpuDirect2DStatus status,
        int nativeHResult)
        : base($"Direct2D {operation} failed with {status} (0x{nativeHResult:X8}).")
    {
        Status = status;
        NativeHResult = nativeHResult;
        HResult = nativeHResult;
    }

    public ProGpuDirect2DStatus Status { get; }

    public int NativeHResult { get; }
}

/// <summary>
/// Owns one caller reference to a genuine Windows COM interface.
/// </summary>
public sealed class ProGpuDirect2DComReference : SafeHandleZeroOrMinusOneIsInvalid
{
    internal ProGpuDirect2DComReference(
        nint value,
        ProGpuDirect2DInterfaceKind kind)
        : this(value, kind, null)
    {
    }

    private ProGpuDirect2DComReference(
        nint value,
        ProGpuDirect2DInterfaceKind kind,
        Guid? queriedInterfaceId)
        : base(ownsHandle: true)
    {
        InterfaceKind = kind;
        QueriedInterfaceId = queriedInterfaceId;
        SetHandle(value);
    }

    public ProGpuDirect2DInterfaceKind InterfaceKind { get; }

    public Guid? QueriedInterfaceId { get; }

    /// <summary>
    /// Queries this genuine COM object for any interface supported by the
    /// installed Windows runtime. The returned safe handle owns one reference.
    /// </summary>
    public unsafe ProGpuDirect2DComReference QueryInterface(Guid interfaceId)
    {
        bool referenceAdded = false;
        try
        {
            DangerousAddRef(ref referenceAdded);
            ProGpuDirect2DNative.NativeGuid nativeInterfaceId =
                ProGpuDirect2DNative.NativeGuid.FromGuid(interfaceId);
            nint result = 0;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.ComQueryInterface(
                    DangerousGetHandle(),
                    &nativeInterfaceId,
                    &result,
                    &nativeHResult);
            if (status != ProGpuDirect2DStatus.Success)
            {
                throw new ProGpuDirect2DException(
                    $"QueryInterface({interfaceId:D})",
                    status,
                    nativeHResult);
            }
            if (result == 0)
            {
                throw new InvalidOperationException(
                    "COM QueryInterface succeeded without returning an interface.");
            }
            return new ProGpuDirect2DComReference(
                result,
                InterfaceKind,
                interfaceId);
        }
        finally
        {
            if (referenceAdded)
            {
                DangerousRelease();
            }
        }
    }

    protected override bool ReleaseHandle()
    {
        ProGpuDirect2DNative.ComRelease(handle);
        return true;
    }
}
