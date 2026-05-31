using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

public class ListBox : Selector
{
    public ListBox()
    {
        Background = new ThemeResourceBrush("CardBackground");
        BorderBrush = new ThemeResourceBrush("ControlBorder");
        BorderThickness = new Thickness(1f);
        CornerRadius = 6f;

        var defaultStyle = ThemeManager.GetDefaultStyle(typeof(ItemsControl));
        if (defaultStyle != null)
        {
            Style = defaultStyle;
        }
    }

    public void AddItem(ListBoxItem item)
    {
        Items.Add(item);
        if (ItemsPanel != null)
        {
            ItemsPanel.Children.Add(item);
            item.Selected += (s, e) => { SelectedItem = item; };
        }
        InvalidateMeasure();
        Invalidate();
    }

    public void ClearItems()
    {
        SelectedItem = null;
        Items.Clear();
        if (ItemsPanel != null)
        {
            ItemsPanel.Children.Clear();
        }
        InvalidateMeasure();
        Invalidate();
    }

    protected override void OnSelectedItemChanged(object? oldValue, object? newValue)
    {
        if (oldValue is ListBoxItem oldItem)
        {
            oldItem.IsSelected = false;
        }
        if (newValue is ListBoxItem newItem)
        {
            newItem.IsSelected = true;
        }
        base.OnSelectedItemChanged(oldValue, newValue);
    }
}
