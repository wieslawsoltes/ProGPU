using Microsoft.UI;
using Microsoft.UI.Input;
using Windows.Foundation;
using Windows.Graphics;

namespace ProGPU.WinUI.Platform;

/// <summary>
/// Delivers native non-client input to an existing per-window source.
/// </summary>
public static class InputNonClientPointerSourceRegistration
{
    public static bool NotifyPointerEntered(
        WindowId windowId,
        NonClientRegionKind regionKind,
        PointerDeviceType pointerDeviceType,
        bool isPointInRegion,
        Point point) =>
        NotifyPointer(
            windowId,
            NonClientPointerEventKind.Entered,
            regionKind,
            pointerDeviceType,
            isPointInRegion,
            point);

    public static bool NotifyPointerExited(
        WindowId windowId,
        NonClientRegionKind regionKind,
        PointerDeviceType pointerDeviceType,
        bool isPointInRegion,
        Point point) =>
        NotifyPointer(
            windowId,
            NonClientPointerEventKind.Exited,
            regionKind,
            pointerDeviceType,
            isPointInRegion,
            point);

    public static bool NotifyPointerMoved(
        WindowId windowId,
        NonClientRegionKind regionKind,
        PointerDeviceType pointerDeviceType,
        bool isPointInRegion,
        Point point) =>
        NotifyPointer(
            windowId,
            NonClientPointerEventKind.Moved,
            regionKind,
            pointerDeviceType,
            isPointInRegion,
            point);

    public static bool NotifyPointerPressed(
        WindowId windowId,
        NonClientRegionKind regionKind,
        PointerDeviceType pointerDeviceType,
        bool isPointInRegion,
        Point point) =>
        NotifyPointer(
            windowId,
            NonClientPointerEventKind.Pressed,
            regionKind,
            pointerDeviceType,
            isPointInRegion,
            point);

    public static bool NotifyPointerReleased(
        WindowId windowId,
        NonClientRegionKind regionKind,
        PointerDeviceType pointerDeviceType,
        bool isPointInRegion,
        Point point) =>
        NotifyPointer(
            windowId,
            NonClientPointerEventKind.Released,
            regionKind,
            pointerDeviceType,
            isPointInRegion,
            point);

    public static bool NotifyCaptionTapped(
        WindowId windowId,
        PointerDeviceType pointerDeviceType,
        Point point) =>
        InputNonClientPointerSource.TryGetExisting(
            windowId,
            out InputNonClientPointerSource source) &&
        source.RaiseCaptionTapped(
            pointerDeviceType,
            point);

    public static bool NotifyEnteringMoveSize(
        WindowId windowId,
        MoveSizeOperation operation,
        PointInt32 pointerScreenPoint,
        out WindowId moveSizeWindowId)
    {
        moveSizeWindowId = windowId;
        return InputNonClientPointerSource
            .TryGetExisting(
                windowId,
                out InputNonClientPointerSource
                    source) &&
            source.RaiseEnteringMoveSize(
                operation,
                pointerScreenPoint,
                ref moveSizeWindowId);
    }

    public static bool NotifyEnteredMoveSize(
        WindowId windowId,
        MoveSizeOperation operation,
        PointInt32 pointerScreenPoint) =>
        InputNonClientPointerSource.TryGetExisting(
            windowId,
            out InputNonClientPointerSource source) &&
        source.RaiseEnteredMoveSize(
            operation,
            pointerScreenPoint);

    public static bool NotifyExitedMoveSize(
        WindowId windowId,
        MoveSizeOperation operation,
        PointInt32 pointerScreenPoint) =>
        InputNonClientPointerSource.TryGetExisting(
            windowId,
            out InputNonClientPointerSource source) &&
        source.RaiseExitedMoveSize(
            operation,
            pointerScreenPoint);

    public static bool NotifyWindowRectChanging(
        WindowId windowId,
        MoveSizeOperation operation,
        PointInt32 pointerScreenPoint,
        RectInt32 oldWindowRect,
        ref RectInt32 newWindowRect,
        out bool allowRectChange,
        ref bool showWindow)
    {
        allowRectChange = true;
        return InputNonClientPointerSource
            .TryGetExisting(
                windowId,
                out InputNonClientPointerSource
                    source) &&
            source.RaiseWindowRectChanging(
                operation,
                pointerScreenPoint,
                oldWindowRect,
                ref newWindowRect,
                out allowRectChange,
                ref showWindow);
    }

    public static bool NotifyWindowRectChanged(
        WindowId windowId,
        MoveSizeOperation operation,
        PointInt32 pointerScreenPoint,
        RectInt32 oldWindowRect,
        RectInt32 newWindowRect) =>
        InputNonClientPointerSource.TryGetExisting(
            windowId,
            out InputNonClientPointerSource source) &&
        source.RaiseWindowRectChanged(
            operation,
            pointerScreenPoint,
            oldWindowRect,
            newWindowRect);

    public static bool IsPointInRegion(
        WindowId windowId,
        NonClientRegionKind regionKind,
        Point point) =>
        InputNonClientPointerSource.TryGetExisting(
            windowId,
            out InputNonClientPointerSource source) &&
        source.IsPointInRegion(
            regionKind,
            point);

    private static bool NotifyPointer(
        WindowId windowId,
        NonClientPointerEventKind eventKind,
        NonClientRegionKind regionKind,
        PointerDeviceType pointerDeviceType,
        bool isPointInRegion,
        Point point) =>
        InputNonClientPointerSource.TryGetExisting(
            windowId,
            out InputNonClientPointerSource source) &&
        source.RaisePointer(
            eventKind,
            regionKind,
            pointerDeviceType,
            isPointInRegion,
            point);
}
