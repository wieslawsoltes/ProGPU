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

    /// <summary>Source resolution, not target/device scale. Legacy providers use 96 DPI.</summary>
    double DpiX => 96.0;

    /// <summary>Source resolution, not target/device scale. Legacy providers use 96 DPI.</summary>
    double DpiY => 96.0;

    bool TryGetPortableNativeImage(out object? nativeImage);
}
