namespace ProGPU.Wpf.Interop;

/// <summary>
/// Describes one live video frame published by a source-built presentation
/// media player. <see cref="NativeImage"/> is backend-owned and must implement
/// the consumer's typed texture-lease contract; the interop assembly keeps the
/// protocol neutral and never inspects the object by reflection.
/// </summary>
public readonly record struct PortableMediaPlayerFrame(
    int PixelWidth,
    int PixelHeight,
    ulong ContentVersion,
    object NativeImage);

/// <summary>
/// Publishes the current live video frame for canonical WPF DrawVideo replay.
/// A false result means that no frame is currently ready and is not a request
/// for a CPU fallback.
/// </summary>
public interface IPortableMediaPlayerSource
{
    bool TryGetPortableMediaPlayerFrame(
        out PortableMediaPlayerFrame frame);
}
