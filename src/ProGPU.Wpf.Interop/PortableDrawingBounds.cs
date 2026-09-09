namespace ProGPU.Wpf.Interop;

public interface IPortableDrawingBoundsSource
{
    /// <summary>
    /// Publishes authoritative drawing bounds. A successful result with
    /// <see cref="PortableRect.IsEmpty"/> means known empty content, not missing
    /// metadata. A false result is unavailable and must not suppress a drawing.
    /// Finite zero-sized rectangles remain distinct from empty content; consumers
    /// must validate dimensions before using bounds for image/brush mapping.
    /// </summary>
    bool TryGetPortableDrawingBounds(out PortableRect bounds);
}
