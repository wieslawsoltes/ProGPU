using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Microsoft.UI.Xaml;

public partial class UIElement
{
    private RectangleGeometry? _clipGeometry;
    private long _clipRectCallbackToken;

    public static readonly DependencyProperty ClipProperty = DependencyProperty.Register(
        nameof(Clip), typeof(RectangleGeometry), typeof(UIElement),
        new PropertyMetadata(null, OnClipChanged) { AffectsRender = true });

    public static readonly DependencyProperty OpacityProperty = DependencyProperty.Register(
        nameof(Opacity), typeof(double), typeof(UIElement),
        new PropertyMetadata(1d, static (d, e) =>
        {
            ((ProGPU.Scene.Visual)(UIElement)d).Opacity =
                (float)(double)(e.NewValue ?? 1d);
        })
        {
            AffectsRender = true
        });

    public static readonly DependencyProperty ContextFlyoutProperty = DependencyProperty.Register(
        nameof(ContextFlyout), typeof(FlyoutBase), typeof(UIElement), new PropertyMetadata(null));

    public static readonly DependencyProperty XYFocusKeyboardNavigationProperty = DependencyProperty.Register(
        nameof(XYFocusKeyboardNavigation), typeof(XYFocusKeyboardNavigationMode), typeof(UIElement),
        new PropertyMetadata(XYFocusKeyboardNavigationMode.Auto));

    public static readonly DependencyProperty UseLayoutRoundingProperty = DependencyProperty.Register(
        nameof(UseLayoutRounding), typeof(bool), typeof(UIElement),
        new PropertyMetadata(true) { AffectsMeasure = true, AffectsArrange = true, AffectsRender = true });

    public static readonly DependencyProperty IsAccessKeyScopeProperty = DependencyProperty.Register(
        nameof(IsAccessKeyScope), typeof(bool), typeof(UIElement), new PropertyMetadata(false));

    public static readonly DependencyProperty IsHitTestVisibleProperty =
        DependencyProperty.Register(
            nameof(IsHitTestVisible),
            typeof(bool),
            typeof(UIElement),
            new PropertyMetadata(true));

    public static readonly DependencyProperty IsTabStopProperty =
        DependencyProperty.Register(
            nameof(IsTabStop),
            typeof(bool),
            typeof(UIElement),
            new PropertyMetadata(true));

    public static readonly DependencyProperty TabFocusNavigationProperty =
        DependencyProperty.Register(
            nameof(TabFocusNavigation),
            typeof(KeyboardNavigationMode),
            typeof(UIElement),
            new PropertyMetadata(KeyboardNavigationMode.Local));

    public static readonly DependencyProperty TabIndexProperty = DependencyProperty.Register(
        nameof(TabIndex), typeof(int), typeof(UIElement), new PropertyMetadata(int.MaxValue));

    public RectangleGeometry? Clip
    {
        get => GetValue(ClipProperty) as RectangleGeometry;
        set => SetValue(ClipProperty, value);
    }

    public new double Opacity
    {
        get => (double)(GetValue(OpacityProperty) ?? 1d);
        set => SetValue(OpacityProperty, value);
    }

    public FlyoutBase? ContextFlyout
    {
        get => GetValue(ContextFlyoutProperty) as FlyoutBase;
        set => SetValue(ContextFlyoutProperty, value);
    }

    public XYFocusKeyboardNavigationMode XYFocusKeyboardNavigation
    {
        get => (XYFocusKeyboardNavigationMode)(GetValue(XYFocusKeyboardNavigationProperty) ?? XYFocusKeyboardNavigationMode.Auto);
        set => SetValue(XYFocusKeyboardNavigationProperty, value);
    }

    public bool UseLayoutRounding
    {
        get => (bool)(GetValue(UseLayoutRoundingProperty) ?? true);
        set => SetValue(UseLayoutRoundingProperty, value);
    }

    public bool IsAccessKeyScope
    {
        get => (bool)(GetValue(IsAccessKeyScopeProperty) ?? false);
        set => SetValue(IsAccessKeyScopeProperty, value);
    }

    public bool IsHitTestVisible
    {
        get => (bool)(GetValue(IsHitTestVisibleProperty) ?? true);
        set => SetValue(IsHitTestVisibleProperty, value);
    }

    public bool IsTabStop
    {
        get => (bool)(GetValue(IsTabStopProperty) ?? true);
        set => SetValue(IsTabStopProperty, value);
    }

    public KeyboardNavigationMode TabFocusNavigation
    {
        get => (KeyboardNavigationMode)(
            GetValue(TabFocusNavigationProperty) ??
            KeyboardNavigationMode.Local);
        set => SetValue(TabFocusNavigationProperty, value);
    }

    public int TabIndex
    {
        get => (int)(GetValue(TabIndexProperty) ?? int.MaxValue);
        set => SetValue(TabIndexProperty, value);
    }

    private static void OnClipChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var element = (UIElement)dependencyObject;
        if (element._clipGeometry != null && element._clipRectCallbackToken != 0)
            element._clipGeometry.UnregisterPropertyChangedCallback(RectangleGeometry.RectProperty, element._clipRectCallbackToken);

        element._clipGeometry = args.NewValue as RectangleGeometry;
        element._clipRectCallbackToken = 0;
        if (element._clipGeometry != null)
        {
            element._clipRectCallbackToken = element._clipGeometry.RegisterPropertyChangedCallback(
                RectangleGeometry.RectProperty,
                (geometry, _) => UpdateClipBounds((RectangleGeometry)geometry, element));
        }
        UpdateClipBounds(element._clipGeometry, element);
    }

    private static void UpdateClipBounds(RectangleGeometry? geometry, UIElement element)
    {
        element.ClipBounds = geometry == null || geometry.Rect.IsEmpty ? null : geometry.Rect;
    }
}
