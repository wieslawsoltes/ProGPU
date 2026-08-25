using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Platform;

namespace Avalonia.SilkNet;

internal sealed class SilkNetScreenImpl : IScreenImpl, IDisposable
{
    private readonly WindowImpl _owner;
    private readonly SilkNetMonitorProvider _provider;
    private IReadOnlyList<Screen>? _screens;

    internal SilkNetScreenImpl(WindowImpl owner)
    {
        _owner = owner;
        _provider = owner.Platform.Monitors;
        _provider.Changed += Invalidate;
    }

    public int ScreenCount => AllScreens.Count;

    public IReadOnlyList<Screen> AllScreens =>
        _screens ??= ReadScreens();

    public Action? Changed { get; set; }

    public Screen? ScreenFromWindow(IWindowBaseImpl window) =>
        ScreenFromTopLevel(window);

    public Screen? ScreenFromTopLevel(ITopLevelImpl topLevel)
    {
        if (topLevel is not IWindowBaseImpl window)
            return null;

        PixelSize size = PixelSize.FromSize(
            window.FrameSize ?? window.ClientSize,
            window.DesktopScaling);
        return ScreenFromRect(
            new PixelRect(window.Position, size));
    }

    public Screen? ScreenFromPoint(PixelPoint point)
    {
        foreach (Screen screen in AllScreens)
        {
            if (SilkNetScreenGeometry.Contains(
                    screen.Bounds,
                    point))
                return screen;
        }

        return null;
    }

    public Screen? ScreenFromRect(PixelRect rect)
    {
        Screen? best = null;
        double largestArea = 0;
        foreach (Screen screen in AllScreens)
        {
            double area = SilkNetScreenGeometry.IntersectionArea(
                screen.Bounds,
                rect);
            if (area > largestArea)
            {
                largestArea = area;
                best = screen;
            }
        }

        return best;
    }

    public Task<bool> RequestScreenDetails() =>
        Task.FromResult(true);

    public void Dispose()
    {
        _provider.Changed -= Invalidate;
        _screens = null;
        Changed = null;
    }

    internal void Invalidate()
    {
        _screens = null;
        Changed?.Invoke();
    }

    private IReadOnlyList<Screen> ReadScreens()
    {
        IReadOnlyList<SilkNetMonitorInfo> monitors =
            _provider.ReadMonitors();
        var screens = new Screen[monitors.Count];
        for (int index = 0; index < monitors.Count; index++)
            screens[index] = new SilkNetScreen(monitors[index]);
        return screens;
    }

    private sealed class SilkNetScreen : PlatformScreen
    {
        internal SilkNetScreen(SilkNetMonitorInfo monitor)
            : base(new PlatformHandle(
                monitor.Handle,
                "GLFWmonitor"))
        {
            DisplayName = monitor.Name;
            Scaling = monitor.Scaling;
            Bounds = monitor.Bounds;
            WorkingArea = monitor.WorkingArea;
            IsPrimary = monitor.IsPrimary;
            CurrentOrientation =
                Bounds.Width >= Bounds.Height
                    ? ScreenOrientation.Landscape
                    : ScreenOrientation.Portrait;
        }
    }
}

internal static class SilkNetScreenGeometry
{
    internal static bool Contains(
        PixelRect bounds,
        PixelPoint point) =>
        point.X >= bounds.X &&
        point.X < bounds.X + bounds.Width &&
        point.Y >= bounds.Y &&
        point.Y < bounds.Y + bounds.Height;

    internal static double IntersectionArea(
        PixelRect left,
        PixelRect right)
    {
        int intersectionLeft = Math.Max(left.X, right.X);
        int intersectionTop = Math.Max(left.Y, right.Y);
        int intersectionRight = Math.Min(
            left.X + left.Width,
            right.X + right.Width);
        int intersectionBottom = Math.Min(
            left.Y + left.Height,
            right.Y + right.Height);
        return Math.Max(0, intersectionRight - intersectionLeft) *
            (double)Math.Max(
                0,
                intersectionBottom - intersectionTop);
    }
}
