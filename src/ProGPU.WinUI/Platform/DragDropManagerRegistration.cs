using Microsoft.UI.Input.DragDrop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace ProGPU.WinUI.Platform;

public readonly record struct
    DragDropVisualSnapshot(
        SoftwareBitmap? Bitmap,
        Point AnchorPoint,
        string Caption,
        bool IsCaptionVisible,
        bool IsContentVisible,
        bool IsGlyphVisible);

/// <summary>
/// Drives an active content-island drag session from a native host.
/// </summary>
public static class DragDropManagerRegistration
{
    public static bool TryGetVisual(
        DragDropManager manager,
        uint pointerId,
        out DragDropVisualSnapshot visual)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return manager.TryGetVisual(
            pointerId,
            out visual);
    }

    public static Task<DataPackageOperation>
        NotifyOverAsync(
            DragDropManager manager,
            uint pointerId,
            Point position,
            DragDropModifiers modifiers)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return manager.NotifyOver(
            pointerId,
            position,
            modifiers);
    }

    public static Task NotifyLeaveAsync(
        DragDropManager manager,
        uint pointerId,
        Point position,
        DragDropModifiers modifiers)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return manager.NotifyLeave(
            pointerId,
            position,
            modifiers);
    }

    public static Task<DataPackageOperation>
        NotifyDropAsync(
            DragDropManager manager,
            uint pointerId,
            Point position,
            DragDropModifiers modifiers)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return manager.NotifyDrop(
            pointerId,
            position,
            modifiers);
    }

    public static bool Cancel(
        DragDropManager manager,
        uint pointerId)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return manager.Cancel(pointerId);
    }
}
