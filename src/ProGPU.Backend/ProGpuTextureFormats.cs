using Silk.NET.WebGPU;

namespace ProGPU.Backend;

/// <summary>
/// ProGPU-owned texture-format values that are present in the current WebGPU
/// specification but are not yet declared by the pinned Silk.NET WebGPU ABI.
/// </summary>
/// <remarks>
/// These values are private transport tokens. A backend must translate them
/// explicitly and advertise the corresponding capability before a texture is
/// created or imported. They must never be passed to an older native WebGPU
/// ABI unchanged.
/// </remarks>
public static class ProGpuTextureFormats
{
    private const int ExtensionBase = 0x5052_0000;

    /// <summary>
    /// Normalized unsigned 16-bit single-channel texture format.
    /// Requires WebGPU <c>texture-formats-tier1</c>.
    /// </summary>
    public static readonly TextureFormat R16Unorm =
        (TextureFormat)(ExtensionBase + 1);

    /// <summary>
    /// Normalized unsigned 16-bit two-channel texture format.
    /// Requires WebGPU <c>texture-formats-tier1</c>.
    /// </summary>
    public static readonly TextureFormat RG16Unorm =
        (TextureFormat)(ExtensionBase + 2);

    public static bool RequiresTextureFormatsTier1(
        TextureFormat format) =>
        format == R16Unorm ||
        format == RG16Unorm;
}
