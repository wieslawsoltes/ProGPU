using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform;
using Silk.NET.Windowing;
using SilkRectangle = Silk.NET.Maths.Rectangle<int>;

namespace Avalonia.SilkNet;

internal sealed class SilkNetScreenImpl : IScreenImpl
{
    private readonly WindowImpl _owner;
    private IReadOnlyList<Screen>? _screens;

    internal SilkNetScreenImpl(WindowImpl owner)
    {
        _owner = owner;
    }

    public int ScreenCount => AllScreens.Count;

    public IReadOnlyList<Screen> AllScreens =>
        _screens ??= ReadScreens();

    public Action? Changed { get; set; }

    public Screen? ScreenFromWindow(IWindowBaseImpl window) =>
        ScreenFromTopLevel(window);

    public Screen? ScreenFromTopLevel(ITopLevelImpl topLevel)
    {
        PixelPoint center = topLevel.PointToScreen(
            new Point(
                topLevel.ClientSize.Width / 2,
                topLevel.ClientSize.Height / 2));
        return ScreenFromPoint(center);
    }

    public Screen? ScreenFromPoint(PixelPoint point)
    {
        foreach (Screen screen in AllScreens)
        {
            if (screen.Bounds.Contains(point))
                return screen;
        }

        return AllScreens.Count == 0 ? null : AllScreens[0];
    }

    public Screen? ScreenFromRect(PixelRect rect)
    {
        foreach (Screen screen in AllScreens)
        {
            if (screen.Bounds.Intersects(rect))
                return screen;
        }

        return AllScreens.Count == 0 ? null : AllScreens[0];
    }

    public Task<bool> RequestScreenDetails() =>
        Task.FromResult(true);

    internal void Invalidate()
    {
        _screens = null;
        Changed?.Invoke();
    }

    private IReadOnlyList<Screen> ReadScreens()
    {
        try
        {
            return Monitor
                .GetMonitors(_owner.NativeWindow)
                .Select(
                    (monitor, index) =>
                        (Screen)new SilkNetScreen(
                            monitor,
                            index == 0,
                            _owner.RenderScaling))
                .ToArray();
        }
        catch (PlatformNotSupportedException)
        {
            return Array.Empty<Screen>();
        }
    }

    private sealed class SilkNetScreen : PlatformScreen
    {
        internal SilkNetScreen(
            IMonitor monitor,
            bool primary,
            double scaling)
            : base(
                new PlatformHandle(
                    (IntPtr)monitor.Index,
                    "SilkMonitor"))
        {
            DisplayName = monitor.Name;
            Scaling = scaling > 0 ? scaling : 1;
            SilkRectangle bounds = monitor.Bounds;
            Bounds = new PixelRect(
                bounds.Origin.X,
                bounds.Origin.Y,
                bounds.Size.X,
                bounds.Size.Y);
            WorkingArea = Bounds;
            IsPrimary = primary;
            CurrentOrientation =
                Bounds.Width >= Bounds.Height
                    ? ScreenOrientation.Landscape
                    : ScreenOrientation.Portrait;
        }

    }
}
