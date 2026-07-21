using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

public static class PopupService
{
    public static List<FrameworkElement> ActivePopups { get; } = new();
    public static ProGPU.Text.TtfFont? DefaultFont { get; set; }
    
    private static readonly Dictionary<FrameworkElement, FrameworkElement> PopupOwners = new();

    public static void ShowPopup(FrameworkElement popup, Vector2 offset, FrameworkElement? owner = null)
    {
        if (ActivePopups.Contains(popup)) return;

        if (owner != null && !popup.IsPropertySetLocally(FrameworkElement.FlowDirectionProperty))
        {
            popup.FlowDirection = owner.FlowDirection;
        }

        popup.Offset = offset;
        
        // Dynamic boundary constraint checking against root window dimensions
        var windowSize = InputSystem.Root?.Size ?? new Vector2(1280f, 800f);
        float availW = Math.Max(50f, windowSize.X - offset.X - 10f);
        float availH = Math.Max(50f, windowSize.Y - offset.Y - 10f);

        // Constrain popup height & width based on actual screen space
        popup.Measure(new Vector2(availW, availH));
        
        float w = float.IsNaN(popup.Width) ? Math.Min(popup.DesiredSize.X, availW) : popup.Width;
        float h = float.IsNaN(popup.Height) ? Math.Min(popup.DesiredSize.Y, availH) : popup.Height;

        popup.Arrange(new Rect(offset, new Vector2(w, h)));

        ActivePopups.Add(popup);
        if (owner != null)
        {
            PopupOwners[popup] = owner;
        }
        
        // Request visual tree invalidation if root is available
        InputSystem.Root?.Invalidate();
    }

    public static void HidePopup(FrameworkElement popup)
    {
        if (ActivePopups.Remove(popup))
        {
            PopupOwners.Remove(popup);
            InputSystem.Root?.Invalidate();
        }
    }

    public static FrameworkElement? GetOwner(FrameworkElement popup)
    {
        if (PopupOwners.TryGetValue(popup, out var owner))
        {
            return owner;
        }
        return null;
    }

    internal static void ReplaceForHotReload(FrameworkElement oldPopup, FrameworkElement newPopup)
    {
        var index = ActivePopups.IndexOf(oldPopup);
        if (index < 0)
        {
            throw new InvalidOperationException("The popup is no longer active.");
        }

        newPopup.Offset = oldPopup.Offset;
        ActivePopups[index] = newPopup;
        if (PopupOwners.Remove(oldPopup, out var owner))
        {
            PopupOwners[newPopup] = owner;
        }

        InputSystem.Root?.Invalidate();
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
                float availW = Math.Max(50f, windowSize.X - popup.Offset.X - 10f);
                float availH = Math.Max(50f, windowSize.Y - popup.Offset.Y - 10f);

                popup.Measure(new Vector2(availW, availH));
                
                float w = float.IsNaN(popup.Width) ? Math.Min(popup.DesiredSize.X, availW) : popup.Width;
                float h = float.IsNaN(popup.Height) ? Math.Min(popup.DesiredSize.Y, availH) : popup.Height;

                popup.Arrange(new Rect(popup.Offset, new Vector2(w, h)));
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
