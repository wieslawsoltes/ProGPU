using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Controls;

public class ToggleButton : Button
{
    public static readonly DependencyProperty IsCheckedProperty =
        DependencyProperty.Register(
            "IsChecked",
            typeof(bool),
            typeof(ToggleButton),
            new PropertyMetadata(false, (d, e) => ((ToggleButton)d).OnCheckedChanged()));

    public bool IsChecked
    {
        get => (bool)(GetValue(IsCheckedProperty) ?? false);
        set => SetValue(IsCheckedProperty, value);
    }

    public event EventHandler? Checked;
    public event EventHandler? Unchecked;
    public event EventHandler? CheckedChanged;

    public ToggleButton()
    {
        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            SetDefaultStyle(defaultStyle);
        }
        UpdateForeground();
    }

    private void OnCheckedChanged()
    {
        Invalidate();
        UpdateForeground();
        CheckedChanged?.Invoke(this, EventArgs.Empty);
        if (IsChecked)
        {
            Checked?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Unchecked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateForeground()
    {
        if (IsChecked)
        {
            Foreground = new ThemeResourceBrush("AccentButtonForeground");
        }
        else
        {
            Foreground = new ThemeResourceBrush("ButtonForeground");
        }
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (IsEnabled && IsPointerPressed && IsPointerOver)
        {
            IsChecked = !IsChecked;
        }
        base.OnPointerReleased(e);
    }
}
