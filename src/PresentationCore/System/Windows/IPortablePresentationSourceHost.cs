using System;

namespace System.Windows;

public delegate bool PortableHitTestAllBufferOverride(double x, double y, Span<object?> results, out int resultCount);

public delegate bool PortableGeometryHitTestBufferOverride(
    double minX,
    double minY,
    double maxX,
    double maxY,
    Span<object?> results,
    out int resultCount);

public interface IPortablePresentationSourceHost : IDisposable
{
    event EventHandler? RenderRequested;

    event EventHandler? CursorRequested;

    object? RootVisual { get; set; }

    object? CompositionTarget { get; }

    IntPtr Handle { get; }

    object? RequestedCursor { get; }

    string? RequestedCursorName { get; }

    Func<double, double, object?>? HitTestOverride { get; set; }

    Func<double, double, object?[]?>? HitTestAllOverride { get; set; }

    PortableHitTestAllBufferOverride? HitTestAllBufferOverride { get; set; }

    Func<double, double, double, double, object?[]?>? HitTestBoundsOverride { get; set; }

    PortableGeometryHitTestBufferOverride? HitTestBoundsBufferOverride { get; set; }

    Func<double, double, double, double, object?[]?>? HitTestEllipseBoundsOverride { get; set; }

    PortableGeometryHitTestBufferOverride? HitTestEllipseBoundsBufferOverride { get; set; }

    void SetDeviceScale(double dpiScaleX, double dpiScaleY);

    void SetClientSize(double width, double height);

    /// <summary>
    /// Sets this source's origin in the owning portable host's device-pixel coordinate space.
    /// The main presentation source uses (0, 0); composited popup sources use their retained
    /// popup position so client/screen conversions remain correct for nested popups.
    /// </summary>
    void SetClientOrigin(double x, double y)
    {
    }

    bool TryUpdateRootVisualClientSize(out double width, out double height);

    bool DispatchHwndSourceHook(int message, IntPtr wParam, IntPtr lParam, out IntPtr result, out bool handled);
}
