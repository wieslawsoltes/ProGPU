namespace ProGPU.Wpf.Interop;

/// <summary>
/// Describes synchronized content published for canonical WPF D3DImage MIL
/// replay. <see cref="NativeImage"/> is backend-owned and must implement the
/// consumer's typed texture-lease contract. Lease acquisition/release owns
/// any keyed-mutex, fence, or backend transition required for sampling.
/// </summary>
public readonly record struct PortableD3DImageFrame(
    int PixelWidth,
    int PixelHeight,
    ulong ContentVersion,
    object NativeImage);

/// <summary>
/// Publishes the current synchronized image for canonical TYPE_D3DIMAGE and
/// MilCmdD3DImagePresent replay. A false result means that no presentable
/// content is ready and never requests a CPU readback fallback.
/// </summary>
public interface IPortableD3DImageSource
{
    bool TryGetPortableD3DImageFrame(
        out PortableD3DImageFrame frame);
}
