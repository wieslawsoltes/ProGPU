namespace ProGPU.Wpf.Interop;

/// <summary>
/// Publishes reflection-free, package-neutral WPF BitmapCache state.
/// </summary>
public interface IPortableBitmapCacheSource
{
    bool TryGetPortableBitmapCache(out PortableBitmapCache cache);
}

/// <summary>
/// Immutable current-value snapshot of a WPF BitmapCache resource.
/// </summary>
public readonly record struct PortableBitmapCache(
    double RenderAtScale,
    bool SnapsToDevicePixels,
    bool EnableClearType);
