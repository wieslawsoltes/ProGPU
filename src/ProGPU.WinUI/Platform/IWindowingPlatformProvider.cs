using System.Numerics;
using Microsoft.UI;
using Windows.Graphics;

namespace ProGPU.WinUI.Platform;

public readonly record struct WindowingDisplayAreaInfo(
    DisplayId DisplayId,
    RectInt32 OuterBounds,
    RectInt32 WorkArea,
    bool IsPrimary);

public interface IWindowingDisplayAreaProvider
{
    event EventHandler? DisplayAreasChanged;

    IReadOnlyList<WindowingDisplayAreaInfo> GetDisplayAreas();
}

public interface IAppWindowPlatformProvider
{
    bool TrySetIcon(WindowId windowId, IconId iconId);

    bool TrySetIcon(WindowId windowId, string iconPath);

    bool TrySetTaskbarIcon(WindowId windowId, IconId iconId);

    bool TrySetTaskbarIcon(WindowId windowId, string iconPath);

    bool TrySetTitleBarIcon(WindowId windowId, IconId iconId);

    bool TrySetTitleBarIcon(WindowId windowId, string iconPath);

    bool TryMoveInZOrder(
        WindowId windowId,
        WindowId belowWindowId,
        bool atTop,
        bool atBottom);
}

public interface IContentCoordinatePlatformProvider
{
    bool TryGetLocalToScreenTransform(
        WindowId windowId,
        out Matrix3x2 localToScreen);
}

public static class WindowingPlatformServices
{
    private static IContentCoordinatePlatformProvider?
        s_contentCoordinates;

    public static IWindowingDisplayAreaProvider? DisplayAreas { get; set; }

    public static IAppWindowPlatformProvider? AppWindows { get; set; }

    public static IContentCoordinatePlatformProvider? ContentCoordinates
    {
        get => Volatile.Read(
            ref s_contentCoordinates);
        set => Volatile.Write(
            ref s_contentCoordinates,
            value);
    }
}
