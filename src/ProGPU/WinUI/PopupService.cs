using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;

namespace ProGPU.WinUI;

public static class PopupService
{
    public static List<FrameworkElement> ActivePopups { get; } = new();
    public static ProGPU.Text.TtfFont? DefaultFont { get; set; }

    public static void ShowPopup(FrameworkElement popup, Vector2 offset)
    {
        if (ActivePopups.Contains(popup)) return;

        popup.Offset = offset;
        // Measure with unlimited size
        popup.Measure(new Vector2(10000f, 10000f));
        // Arrange at the target offset with its desired size
        popup.Arrange(new Rect(offset, popup.DesiredSize));

        ActivePopups.Add(popup);
        
        // Request visual tree invalidation if root is available
        InputSystem.Root?.Invalidate();
    }

    public static void HidePopup(FrameworkElement popup)
    {
        if (ActivePopups.Remove(popup))
        {
            InputSystem.Root?.Invalidate();
        }
    }

    public static void MeasureAndArrangePopups(Vector2 windowSize)
    {
        for (int i = 0; i < ActivePopups.Count; i++)
        {
            var popup = ActivePopups[i];
            if (popup is ContentDialog)
            {
                popup.Measure(windowSize);
                popup.Arrange(new Rect(Vector2.Zero, windowSize));
            }
            else
            {
                popup.Measure(new Vector2(10000f, 10000f));
                popup.Arrange(new Rect(popup.Offset, popup.DesiredSize));
            }
        }
    }

    public static void DismissNonDialogPopups()
    {
        for (int i = ActivePopups.Count - 1; i >= 0; i--)
        {
            var popup = ActivePopups[i];
            if (popup is not ContentDialog)
            {
                HidePopup(popup);
            }
        }
    }

    public static void Clear()
    {
        if (ActivePopups.Count > 0)
        {
            ActivePopups.Clear();
            InputSystem.Root?.Invalidate();
        }
    }
}
