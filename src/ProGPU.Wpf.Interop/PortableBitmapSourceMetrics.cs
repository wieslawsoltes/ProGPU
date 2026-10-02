namespace ProGPU.Wpf.Interop;

/// <summary>
/// Original bitmap dimensions and source resolution, independent of target DPI.
/// Values are retained without normalization; consumers validate their supported
/// dimensions and finite positive resolution before deriving source DIP bounds.
/// </summary>
public readonly record struct PortableBitmapSourceMetrics(
    int PixelWidth,
    int PixelHeight,
    double DpiX,
    double DpiY);

/// <summary>
/// Optional metadata-only bitmap capability. Implementations return one coherent
/// source snapshot without copying pixels, creating a texture or rendering.
/// A false result does not supply usable metrics. This does not transfer ownership
/// or replace source revision/invalidation tracking for retained image content.
/// </summary>
public interface IPortableBitmapSourceMetricsSource
{
    bool TryGetPortableBitmapSourceMetrics(out PortableBitmapSourceMetrics metrics);
}
