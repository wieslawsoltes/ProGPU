using ProGPU.Vector;

namespace Microsoft.UI.Xaml;

public partial class FrameworkElement
{
    public static readonly DependencyProperty AllowFocusOnInteractionProperty = DependencyProperty.Register(
        nameof(AllowFocusOnInteraction), typeof(bool), typeof(FrameworkElement), new PropertyMetadata(true));

    public static readonly DependencyProperty FocusVisualMarginProperty = DependencyProperty.Register(
        nameof(FocusVisualMargin), typeof(Thickness), typeof(FrameworkElement),
        new PropertyMetadata(default(Thickness)) { AffectsRender = true });

    public static readonly DependencyProperty AllowFocusWhenDisabledProperty = DependencyProperty.Register(
        nameof(AllowFocusWhenDisabled), typeof(bool), typeof(FrameworkElement), new PropertyMetadata(false));

    public static readonly DependencyProperty FocusVisualPrimaryBrushProperty =
        DependencyProperty.Register(
            nameof(FocusVisualPrimaryBrush),
            typeof(Brush),
            typeof(FrameworkElement),
            new PropertyMetadata(null) { AffectsRender = true });

    public static readonly DependencyProperty FocusVisualSecondaryBrushProperty =
        DependencyProperty.Register(
            nameof(FocusVisualSecondaryBrush),
            typeof(Brush),
            typeof(FrameworkElement),
            new PropertyMetadata(null) { AffectsRender = true });

    public static readonly DependencyProperty FocusVisualPrimaryThicknessProperty =
        DependencyProperty.Register(
            nameof(FocusVisualPrimaryThickness),
            typeof(Thickness),
            typeof(FrameworkElement),
            new PropertyMetadata(new Thickness(2f)) { AffectsRender = true });

    public static readonly DependencyProperty FocusVisualSecondaryThicknessProperty =
        DependencyProperty.Register(
            nameof(FocusVisualSecondaryThickness),
            typeof(Thickness),
            typeof(FrameworkElement),
            new PropertyMetadata(new Thickness(1f)) { AffectsRender = true });

    public bool AllowFocusOnInteraction
    {
        get => (bool)(GetValue(AllowFocusOnInteractionProperty) ?? true);
        set => SetValue(AllowFocusOnInteractionProperty, value);
    }

    public Thickness FocusVisualMargin
    {
        get => (Thickness)(GetValue(FocusVisualMarginProperty) ?? default(Thickness));
        set => SetValue(FocusVisualMarginProperty, value);
    }

    public bool AllowFocusWhenDisabled
    {
        get => (bool)(GetValue(AllowFocusWhenDisabledProperty) ?? false);
        set => SetValue(AllowFocusWhenDisabledProperty, value);
    }

    public Brush? FocusVisualPrimaryBrush
    {
        get => GetValue(FocusVisualPrimaryBrushProperty) as Brush;
        set => SetValue(FocusVisualPrimaryBrushProperty, value);
    }

    public Brush? FocusVisualSecondaryBrush
    {
        get => GetValue(FocusVisualSecondaryBrushProperty) as Brush;
        set => SetValue(FocusVisualSecondaryBrushProperty, value);
    }

    public Thickness FocusVisualPrimaryThickness
    {
        get => (Thickness)(
            GetValue(FocusVisualPrimaryThicknessProperty) ??
            new Thickness(2f));
        set => SetValue(FocusVisualPrimaryThicknessProperty, value);
    }

    public Thickness FocusVisualSecondaryThickness
    {
        get => (Thickness)(
            GetValue(FocusVisualSecondaryThicknessProperty) ??
            new Thickness(1f));
        set => SetValue(FocusVisualSecondaryThicknessProperty, value);
    }
}
