namespace ProGPU.Wpf.Interop;

/// <summary>Authoritative replacement for a visual's own point-hit coverage,
/// not drawing/descendant bounds and not geometry-region coverage.</summary>
public interface IPortablePointHitRegionSource
{
    /// <summary>A successful Empty result suppresses own point hits only. A zero-sized
    /// rectangle remains a real source rectangle; false means unavailable, not empty.</summary>
    bool TryGetPortablePointHitRegion(out PortableRect rectangle);
}
