namespace ProGPU.Backend;

/// <summary>A native desktop-point rectangle; it is not a framebuffer-pixel rectangle.</summary>
public readonly record struct NativeWindowBounds(double X, double Y, double Width, double Height);

/// <summary>
/// A synchronous read of a live native window's actual geometry. Window and
/// ContentView are borrowed identity values, not leases or pointers the caller
/// may dereference. Currently available for owned Cocoa windows only.
/// </summary>
/// <param name="Window">The exact attached native window identity.</param>
/// <param name="ContentView">The actual Cocoa content-view identity at capture.</param>
/// <param name="CocoaWindowNumber">The positive AppKit window-device number, not an inferred global identifier.</param>
/// <param name="ContentBounds">Actual content-view bounds in global top-left desktop points.</param>
/// <param name="FrameBounds">Actual window frame in the same desktop-point coordinates.</param>
/// <param name="BackingScale">Native backing-store scale, separate from all desktop origins and extents.</param>
public readonly record struct NativeWindowGeometrySnapshot(
    NativeWindowHandle Window,
    nint ContentView,
    long CocoaWindowNumber,
    NativeWindowBounds ContentBounds,
    NativeWindowBounds FrameBounds,
    double BackingScale);

internal readonly record struct CocoaGeometryOwner(nint Window, nint View, long WindowNumber);

internal readonly record struct CocoaGeometryReading(
    CocoaMenuRect ContentScreenBounds,
    CocoaMenuRect FrameScreenBounds,
    CocoaMenuRect PrimaryScreenBounds,
    double BackingScale);

internal interface ICocoaWindowGeometryOperations
{
    bool TryRetain(nint window, out CocoaGeometryOwner owner);
    bool TryRead(in CocoaGeometryOwner owner, out CocoaGeometryReading reading);
    bool IsCurrent(in CocoaGeometryOwner owner);
    void Release(in CocoaGeometryOwner owner);
}

internal static class CocoaWindowGeometry
{
    internal static bool TryCapture<T>(NativeWindowHandle window, ref T operations,
        out NativeWindowGeometrySnapshot snapshot) where T : struct, ICocoaWindowGeometryOperations
    {
        snapshot = default;
        if (window.Kind != NativeWindowKind.Cocoa || !window.IsValid || window.Display != 0 ||
            !operations.TryRetain(window.Handle, out CocoaGeometryOwner owner))
            return false;

        NativeWindowGeometrySnapshot candidate;
        try
        {
            if (owner.Window != window.Handle || owner.View == 0 || owner.WindowNumber <= 0 ||
                !operations.TryRead(owner, out CocoaGeometryReading reading) ||
                !double.IsFinite(reading.BackingScale) || reading.BackingScale <= 0 ||
                !TryMapRectangle(reading.ContentScreenBounds, reading.PrimaryScreenBounds, out NativeWindowBounds content) ||
                !TryMapRectangle(reading.FrameScreenBounds, reading.PrimaryScreenBounds, out NativeWindowBounds frame) ||
                !operations.IsCurrent(owner))
                return false;

            candidate = new(window, owner.View, owner.WindowNumber, content, frame, reading.BackingScale);
        }
        finally
        {
            operations.Release(owner);
        }

        // Publish only after temporary native leases have also been released.
        snapshot = candidate;
        return true;
    }

    internal static bool IsSameOwner(in CocoaGeometryOwner expected, in CocoaGeometryOwner current) =>
        expected.Window != 0 && expected.View != 0 && expected.WindowNumber > 0 && expected == current;

    internal static bool TryMapRectangle(CocoaMenuRect bounds, CocoaMenuRect primary, out NativeWindowBounds result)
    {
        result = default;
        if (!IsFiniteRectangle(bounds) || !IsFiniteRectangle(primary))
            return false;

        // NSScreen.screens[0], not the keyboard-focus-dependent mainScreen,
        // owns the desktop origin. Preserve doubles and negative monitor origins.
        double x = bounds.X - primary.X;
        double y = primary.Y + primary.Height - (bounds.Y + bounds.Height);
        if (!double.IsFinite(x) || !double.IsFinite(y))
            return false;
        result = new(x, y, bounds.Width, bounds.Height);
        return true;
    }

    private static bool IsFiniteRectangle(CocoaMenuRect bounds) =>
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
        bounds.Width > 0 && bounds.Height > 0 &&
        double.IsFinite(bounds.X + bounds.Width) && double.IsFinite(bounds.Y + bounds.Height);
}
