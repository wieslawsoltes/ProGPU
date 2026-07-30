using Silk.NET.WebGPU;

namespace ProGPU.Backend;

public enum ProGpuExternalTextureHandleKind
{
    IOSurface,
    DxgiSharedHandle,
    AndroidHardwareBuffer,
    DmaBuf
}

public readonly record struct ProGpuDmaBufPlane(
    int FileDescriptor,
    ulong Offset,
    uint Stride);

/// <summary>
/// Fixed-size, allocation-free Linux DMA-BUF image description. Plane count
/// is limited to the four planes exposed by DRM/V4L2 formats.
/// </summary>
public readonly record struct ProGpuDmaBufDescriptor(
    uint DrmFormat,
    ulong DrmModifier,
    uint PlaneCount,
    ProGpuDmaBufPlane Plane0,
    ProGpuDmaBufPlane Plane1 = default,
    ProGpuDmaBufPlane Plane2 = default,
    ProGpuDmaBufPlane Plane3 = default)
{
    public ProGpuDmaBufPlane GetPlane(int index) =>
        index switch
        {
            0 => Plane0,
            1 => Plane1,
            2 => Plane2,
            3 => Plane3,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
}

/// <summary>
/// Package-neutral native allocation descriptor. Platform handles never leak
/// into framework facades; an importer validates its own device and format.
/// </summary>
public readonly record struct ProGpuExternalTextureDescriptor(
    ProGpuExternalTextureHandleKind HandleKind,
    nint Handle,
    uint Width,
    uint Height,
    TextureFormat Format,
    TextureUsage Usage,
    GpuTextureAlphaMode AlphaMode,
    bool IsInitialized)
{
    /// <summary>
    /// Indicates that a DXGI shared allocation uses
    /// <c>IDXGIKeyedMutex</c> synchronization.
    /// </summary>
    public bool UsesKeyedMutex { get; init; }

    /// <summary>
    /// Supplies DRM format, modifier, and plane layout when
    /// <see cref="HandleKind"/> is <see cref="ProGpuExternalTextureHandleKind.DmaBuf"/>.
    /// </summary>
    public ProGpuDmaBufDescriptor DmaBuf { get; init; }
}

public interface IProGpuExternalTextureImporter
{
    /// <summary>
    /// Imports one native allocation into the target WebGPU device. A
    /// successful call transfers <paramref name="nativeOwner"/> to the
    /// returned texture; failure leaves it caller-owned.
    /// </summary>
    bool TryImportExternalTexture(
        WgpuContext targetContext,
        in ProGpuExternalTextureDescriptor descriptor,
        IDisposable nativeOwner,
        out GpuTexture texture);
}
