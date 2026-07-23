using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

[ContentProperty(Name = "Content")]
public class ToggleSwitch : ContentControl
{
    public ToggleSwitchTemplateSettings TemplateSettings { get; } = new();

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header),
            typeof(object),
            typeof(ToggleSwitch),
            new PropertyMetadata(null) { AffectsMeasure = true, AffectsRender = true });

    public static readonly DependencyProperty HeaderTemplateProperty =
        DependencyProperty.Register(
            nameof(HeaderTemplate),
            typeof(DataTemplate),
            typeof(ToggleSwitch),
            new PropertyMetadata(null) { AffectsMeasure = true, AffectsRender = true });

    public static readonly DependencyProperty OnContentProperty =
        DependencyProperty.Register(
            nameof(OnContent),
            typeof(object),
            typeof(ToggleSwitch),
            new PropertyMetadata("On") { AffectsMeasure = true, AffectsRender = true });

    public static readonly DependencyProperty OffContentProperty =
        DependencyProperty.Register(
            nameof(OffContent),
            typeof(object),
            typeof(ToggleSwitch),
            new PropertyMetadata("Off") { AffectsMeasure = true, AffectsRender = true });

    public static readonly DependencyProperty OnContentTemplateProperty =
        DependencyProperty.Register(
            nameof(OnContentTemplate),
            typeof(DataTemplate),
            typeof(ToggleSwitch),
            new PropertyMetadata(null) { AffectsMeasure = true, AffectsRender = true });

    public static readonly DependencyProperty OffContentTemplateProperty =
        DependencyProperty.Register(
            nameof(OffContentTemplate),
            typeof(DataTemplate),
            typeof(ToggleSwitch),
            new PropertyMetadata(null) { AffectsMeasure = true, AffectsRender = true });

    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.Register(
            "IsOn",
            typeof(bool),
            typeof(ToggleSwitch),
            new PropertyMetadata(false, (d, e) => ((ToggleSwitch)d).OnIsOnChanged((bool)(e.NewValue ?? false))));

    public bool IsOn
    {
        get => (bool)(GetValue(IsOnProperty) ?? false);
        set => SetValue(IsOnProperty, value);
    }

    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public DataTemplate? HeaderTemplate
    {
        get => GetValue(HeaderTemplateProperty) as DataTemplate;
        set => SetValue(HeaderTemplateProperty, value);
    }

    public object? OnContent
    {
        get => GetValue(OnContentProperty);
        set => SetValue(OnContentProperty, value);
    }

    public object? OffContent
    {
        get => GetValue(OffContentProperty);
        set => SetValue(OffContentProperty, value);
    }

    public DataTemplate? OnContentTemplate
    {
        get => GetValue(OnContentTemplateProperty) as DataTemplate;
        set => SetValue(OnContentTemplateProperty, value);
    }

    public DataTemplate? OffContentTemplate
    {
        get => GetValue(OffContentTemplateProperty) as DataTemplate;
        set => SetValue(OffContentTemplateProperty, value);
    }

    public event EventHandler? Toggled;

    public ToggleSwitch()
    {
        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            SetDefaultStyle(defaultStyle);
        }
    }

    private void OnIsOnChanged(bool isOn)
    {
        Toggled?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(IsOn));
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (IsEnabled && IsPointerPressed && IsPointerOver)
        {
            IsOn = !IsOn;
        }
        base.OnPointerReleased(e);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        return base.MeasureOverride(availableSize);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        base.ArrangeOverride(arrangeRect);
    }

    public override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
    }
}
