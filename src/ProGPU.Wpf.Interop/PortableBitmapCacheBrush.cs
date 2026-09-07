namespace ProGPU.Wpf.Interop;

/// <summary>Publishes cache-brush state without depending on host WPF types.</summary>
public interface IPortableBitmapCacheBrushSource
{
    bool TryGetPortableBitmapCacheBrush(out PortableBitmapCacheBrush brush);
}

/// <summary>
/// A cached visual source, not a tile brush. InternalTarget is the host-resolved
/// visual (including any host-owned wrapper); null paints nothing. A null
/// BitmapCache selects the target cache or the backend default cache policy.
/// Target/cache objects must publish their respective typed portable contracts.
/// Matrices are ignored unless their Has flag is set. Producers retain ownership
/// and publish invalidation when references or values change. Invalid numeric
/// values are rejected by consumers, never silently replaced with defaults.
/// </summary>
public readonly record struct PortableBitmapCacheBrush(
    object? InternalTarget,
    object? BitmapCache = null,
    double Opacity = 1,
    bool HasTransform = false,
    PortableMatrix3x2 Transform = default,
    bool HasRelativeTransform = false,
    PortableMatrix3x2 RelativeTransform = default);
