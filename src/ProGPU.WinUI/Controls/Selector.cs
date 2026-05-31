using Microsoft.UI.Xaml;
using System;

namespace Microsoft.UI.Xaml.Controls;

public class Selector : ItemsControl
{
    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(
            "SelectedItem",
            typeof(object),
            typeof(Selector),
            new PropertyMetadata(null, OnSelectedItemChanged));

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public static readonly DependencyProperty SelectedIndexProperty =
        DependencyProperty.Register(
            "SelectedIndex",
            typeof(int),
            typeof(Selector),
            new PropertyMetadata(-1, OnSelectedIndexChanged));

    public int SelectedIndex
    {
        get => (int)(GetValue(SelectedIndexProperty) ?? -1);
        set => SetValue(SelectedIndexProperty, value);
    }

    public event EventHandler? SelectionChanged;

    private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var selector = (Selector)d;
        selector.OnSelectedItemChanged(e.OldValue, e.NewValue);
    }

    protected virtual void OnSelectedItemChanged(object? oldValue, object? newValue)
    {
        int index = newValue != null ? Items.IndexOf(newValue) : -1;
        if (SelectedIndex != index)
        {
            SelectedIndex = index;
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var selector = (Selector)d;
        selector.OnSelectedIndexChanged((int)(e.OldValue ?? -1), (int)(e.NewValue ?? -1));
    }

    protected virtual void OnSelectedIndexChanged(int oldValue, int newValue)
    {
        object? item = (newValue >= 0 && newValue < Items.Count) ? Items[newValue] : null;
        if (SelectedItem != item)
        {
            SelectedItem = item;
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }
}
