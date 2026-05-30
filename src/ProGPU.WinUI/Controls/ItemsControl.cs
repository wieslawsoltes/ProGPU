using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using Microsoft.UI.Xaml;

namespace Microsoft.UI.Xaml.Controls;

public class ItemsControl : Control
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            "ItemsSource",
            typeof(IEnumerable),
            typeof(ItemsControl),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty ItemsPanelProperty =
        DependencyProperty.Register(
            "ItemsPanel",
            typeof(Panel),
            typeof(ItemsControl),
            new PropertyMetadata(null, OnItemsPanelChanged));

    public Panel? ItemsPanel
    {
        get => (Panel?)GetValue(ItemsPanelProperty);
        set => SetValue(ItemsPanelProperty, value);
    }

    // High-performance callback delegates matching WinUI templates
    public Func<Visual>? ItemTemplate { get; set; }
    public Action<Visual, object, int>? BindVisualCallback { get; set; }

    public List<object> Items { get; } = new();

    public ItemsControl()
    {
        // Default panel is a StackPanel
        ItemsPanel = new StackPanel();

        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            Style = defaultStyle;
        }
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        var scrollViewer = GetTemplateChild("ScrollViewer") as ScrollViewer;
        if (scrollViewer != null && ItemsPanel != null)
        {
            scrollViewer.Content = ItemsPanel;
        }
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ic = (ItemsControl)d;
        ic.Items.Clear();
        if (e.NewValue is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                ic.Items.Add(item);
            }
        }
        ic.RefreshItems();
    }

    private static void OnItemsPanelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ic = (ItemsControl)d;
        if (e.OldValue is Panel oldPanel)
        {
            if (ic.HasTemplate)
            {
                var scrollViewer = ic.GetTemplateChild("ScrollViewer") as ScrollViewer;
                if (scrollViewer != null && scrollViewer.Content == oldPanel)
                {
                    scrollViewer.Content = null;
                }
            }
            else
            {
                ic.RemoveChild(oldPanel);
            }
        }
        if (e.NewValue is Panel newPanel)
        {
            if (ic.HasTemplate)
            {
                var scrollViewer = ic.GetTemplateChild("ScrollViewer") as ScrollViewer;
                if (scrollViewer != null)
                {
                    scrollViewer.Content = newPanel;
                }
            }
            else
            {
                ic.AddChild(newPanel);
            }
        }
        ic.RefreshItems();
    }

    public void RefreshItems()
    {
        if (ItemsPanel == null) return;

        if (ItemsPanel is VirtualizingPanel vp)
        {
            vp.ForceRebind();
        }
        else
        {
            ItemsPanel.Children.Clear();
            if (ItemTemplate != null)
            {
                int index = 0;
                foreach (var item in Items)
                {
                    var container = ItemTemplate();
                    BindVisualCallback?.Invoke(container, item, index);
                    ItemsPanel.Children.Add(container);
                    index++;
                }
            }
        }
        InvalidateMeasure();
        Invalidate();
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (HasTemplate)
        {
            return base.MeasureOverride(availableSize);
        }
        if (ItemsPanel != null)
        {
            ItemsPanel.Measure(availableSize);
            return ItemsPanel.DesiredSize;
        }
        return Vector2.Zero;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        if (HasTemplate)
        {
            base.ArrangeOverride(arrangeRect);
            return;
        }
        if (ItemsPanel != null)
        {
            ItemsPanel.Arrange(arrangeRect);
        }
    }
}
