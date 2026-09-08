using System.Runtime.Intrinsics;

namespace ProGPU.Wpf.Interop;

/// <summary>
/// Maps source client DIPs to the platform desktop coordinate space. The origin
/// is already a desktop coordinate and is never multiplied by the client scale.
/// This is not the source's framebuffer DPI transform.
/// </summary>
public readonly record struct PortableDesktopTransform
{
    public PortableDesktopTransform(double originX, double originY, double scaleX, double scaleY)
    {
        if (!double.IsFinite(originX)) throw new ArgumentOutOfRangeException(nameof(originX));
        if (!double.IsFinite(originY)) throw new ArgumentOutOfRangeException(nameof(originY));
        if (!double.IsFinite(scaleX) || scaleX <= 0) throw new ArgumentOutOfRangeException(nameof(scaleX));
        if (!double.IsFinite(scaleY) || scaleY <= 0) throw new ArgumentOutOfRangeException(nameof(scaleY));
        OriginX = originX;
        OriginY = originY;
        ScaleX = scaleX;
        ScaleY = scaleY;
    }

    public double OriginX { get; }
    public double OriginY { get; }
    public double ScaleX { get; }
    public double ScaleY { get; }

    public static PortableDesktopTransform Identity { get; } = new(0, 0, 1, 1);

    /// <summary>The zero-initialized value is deliberately invalid, not an inferred platform scale.</summary>
    public bool IsValid => double.IsFinite(OriginX) && double.IsFinite(OriginY) &&
        double.IsFinite(ScaleX) && ScaleX > 0 && double.IsFinite(ScaleY) && ScaleY > 0;

    public PortablePoint ClientToDesktop(PortablePoint point)
    {
        RequireValid();
        // Independent x/y lanes, O(1) time/storage, no allocation or device work.
        var mapped = Vector128.Create(point.X, point.Y) * Vector128.Create(ScaleX, ScaleY) +
            Vector128.Create(OriginX, OriginY);
        return new PortablePoint(mapped.GetElement(0), mapped.GetElement(1));
    }

    public PortablePoint DesktopToClient(PortablePoint point)
    {
        RequireValid();
        var mapped = (Vector128.Create(point.X, point.Y) - Vector128.Create(OriginX, OriginY)) /
            Vector128.Create(ScaleX, ScaleY);
        return new PortablePoint(mapped.GetElement(0), mapped.GetElement(1));
    }

    private void RequireValid()
    {
        if (!IsValid) throw new InvalidOperationException("Desktop geometry must be supplied before coordinate conversion.");
    }
}

/// <summary>
/// Optional typed source capability. Hosts publish a complete origin/scale snapshot
/// on their source thread; missing capability must not be replaced by reflected setters.
/// </summary>
public interface IPortableDesktopGeometryHost
{
    PortableDesktopTransform DesktopTransform { get; }

    void SetDesktopTransform(in PortableDesktopTransform transform);
}
