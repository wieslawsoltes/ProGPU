namespace ProGPU.Wpf.Interop;

/// <summary>
/// Supplies a backend-owned image resource to a portable presentation image.
/// The provider retains ownership of the returned resource and must keep it
/// alive for the lifetime of the presentation image that references it.
/// </summary>
public interface IPortableNativeImageSource
{
    int PixelWidth { get; }

    int PixelHeight { get; }

    bool TryGetPortableNativeImage(out object? nativeImage);
}
