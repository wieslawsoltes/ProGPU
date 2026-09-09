namespace ProGPU.Wpf.Interop;

/// <summary>Authoritative replacement for a visual's own point-hit coverage,
/// not drawing/descendant bounds and not geometry-region coverage.</summary>
public interface IPortablePointHitRegionSource
{
    bool TryGetPortablePointHitRegion(out PortableRect rectangle);
}
