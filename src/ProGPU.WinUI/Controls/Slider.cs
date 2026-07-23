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

public class Slider : RangeBase
{
    private bool _isDragging;

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header),
            typeof(object),
            typeof(Slider),
            new PropertyMetadata(null) { AffectsMeasure = true, AffectsRender = true });

    public static readonly DependencyProperty HeaderTemplateProperty =
        DependencyProperty.Register(
            nameof(HeaderTemplate),
            typeof(DataTemplate),
            typeof(Slider),
            new PropertyMetadata(null) { AffectsMeasure = true, AffectsRender = true });

    public static readonly DependencyProperty IsThumbToolTipEnabledProperty =
        DependencyProperty.Register(
            nameof(IsThumbToolTipEnabled), typeof(bool), typeof(Slider),
            new PropertyMetadata(true));

    public bool IsThumbToolTipEnabled
    {
        get => (bool)(GetValue(IsThumbToolTipEnabledProperty) ?? true);
        set => SetValue(IsThumbToolTipEnabledProperty, value);
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

    public Slider()
    {
        Maximum = 100d;
        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            SetDefaultStyle(defaultStyle);
        }
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            _isDragging = true;
            InputSystem.CapturePointer(this);
            UpdateValueFromPos(e.Position.X);
            base.OnPointerPressed(e);
        }
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            InputSystem.ReleasePointerCapture();
        }
        base.OnPointerReleased(e);
    }

    public override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        _isDragging = false;
        base.OnPointerCanceled(e);
    }

    public override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        _isDragging = false;
        base.OnPointerCaptureLost(e);
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (_isDragging && IsEnabled)
        {
            UpdateValueFromPos(e.Position.X);
        }
        base.OnPointerMoved(e);
    }

    private void UpdateValueFromPos(float localX)
    {
        float thumbRadius = ActualThemeFamily == VisualThemeFamily.macOS ? 7f : 8f;
        float width = Size.X;
        float trackWidth = width - 2 * thumbRadius;
        if (trackWidth <= 0f) return;

        float pct = (localX - thumbRadius) / trackWidth;
        pct = Math.Clamp(pct, 0f, 1f);
        Value = Minimum + pct * (Maximum - Minimum);
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
