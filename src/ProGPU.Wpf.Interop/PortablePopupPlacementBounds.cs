namespace ProGPU.Wpf.Interop;

public enum PortablePopupPlacementBoundsKind
{
    OwnerSurface = 1,
    NativeScreen = 2
}

/// <summary>
/// Placement limits in the same desktop coordinate space as the query rectangle.
/// OwnerSurface requests source-built WPF's owner-client bounds, not a monitor.
/// NativeScreen supplies the selected monitor and its work area. This contract
/// does not imply framebuffer pixels or apply any DPI transform.
/// </summary>
public readonly record struct PortablePopupPlacementBounds(
    PortablePopupPlacementBoundsKind Kind,
    PortableRect Screen,
    PortableRect WorkArea);

/// <summary>
/// Streaming monitor selection: greatest positive rectangle overlap, otherwise
/// shortest rectangle-to-rectangle distance. Ties prefer the primary monitor,
/// then inventory order. O(M) time, O(1) space; no allocation or GPU work.
/// Callers must supply one consistent desktop coordinate space for all monitors.
/// </summary>
public struct PortablePopupMonitorSelection
{
    private readonly PortableRect _target;
    private readonly bool _validTarget;
    private bool _hasSelection;
    private bool _primary;
    private double _area;
    private double _distance;
    private PortablePopupPlacementBounds _selection;

    public PortablePopupMonitorSelection(PortableRect target)
    {
        this = default;
        _target = target;
        _validTarget = IsFiniteRectangle(target, allowZeroSize: true);
    }

    public void Consider(PortableRect screen, PortableRect workArea, bool isPrimary)
    {
        if (!_validTarget || !IsValidMonitorBounds(screen, workArea))
            return;

        double overlapX = Math.Min(_target.X + _target.Width, screen.X + screen.Width) - Math.Max(_target.X, screen.X);
        double overlapY = Math.Min(_target.Y + _target.Height, screen.Y + screen.Height) - Math.Max(_target.Y, screen.Y);
        double area = Math.Max(0, overlapX) * Math.Max(0, overlapY);
        double distance = double.Hypot(Math.Max(0, -overlapX), Math.Max(0, -overlapY));
        if (!double.IsFinite(area) || !double.IsFinite(distance)) return;

        if (!_hasSelection || area > _area ||
            (area == _area && (distance < _distance ||
                (distance == _distance && isPrimary && !_primary))))
        {
            _hasSelection = true;
            _primary = isPrimary;
            _area = area;
            _distance = distance;
            _selection = new(PortablePopupPlacementBoundsKind.NativeScreen, screen, workArea);
        }
    }

    public readonly bool TryGetBounds(out PortablePopupPlacementBounds bounds)
    {
        bounds = _selection;
        return _hasSelection;
    }

    public static bool IsValidMonitorBounds(PortableRect screen, PortableRect workArea) =>
        IsFiniteRectangle(screen) && IsFiniteRectangle(workArea) &&
        workArea.X >= screen.X && workArea.Y >= screen.Y &&
        workArea.X + workArea.Width <= screen.X + screen.Width &&
        workArea.Y + workArea.Height <= screen.Y + screen.Height;

    public static bool IsFiniteRectangle(PortableRect rect, bool allowZeroSize = false) =>
        !rect.IsEmpty && double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
        rect.Width >= 0 && rect.Height >= 0 &&
        (allowZeroSize || (rect.Width > 0 && rect.Height > 0)) &&
        double.IsFinite(rect.X + rect.Width) && double.IsFinite(rect.Y + rect.Height);
}
