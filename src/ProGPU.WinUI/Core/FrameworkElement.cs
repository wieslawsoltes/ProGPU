using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Input;
using ProGPU.Layout;
using ProGPU.Scene;
using Microsoft.UI.Input;
using Windows.Devices.Input;

namespace Microsoft.UI.Xaml;

public class RoutedEventArgs : EventArgs
{
    public bool Handled { get; set; }
    public object? OriginalSource { get; set; }
}

public class KeyRoutedEventArgs : RoutedEventArgs
{
    public Key Key { get; set; }
}

public class CharacterReceivedRoutedEventArgs : RoutedEventArgs
{
    public char Character { get; set; }
}

public class PointerRoutedEventArgs : RoutedEventArgs
{
    public Vector2 Position { get; set; }       // Position relative to the element
    public Vector2 ScreenPosition { get; set; } // Position relative to the screen
    public bool IsLeftButtonPressed { get; set; }
    public bool IsMiddleButtonPressed { get; set; }
    public bool IsRightButtonPressed { get; set; }
    public float WheelDelta { get; set; }
    public float WheelDeltaX { get; set; }
    public bool IsPreciseScrolling { get; set; }
    public Pointer Pointer { get; set; } = new(1, Windows.Devices.Input.PointerDeviceType.Mouse, false);
    public ulong Timestamp { get; set; }
    public bool IsPrimary { get; set; } = true;
    public float Pressure { get; set; }
    public Rect ContactRect { get; set; }
    public VirtualKeyModifiers KeyModifiers { get; set; }
    public bool IsCanceled { get; set; }
    internal IReadOnlyList<PointerPoint>? IntermediatePoints { get; set; }

    public PointerPoint GetCurrentPoint(FrameworkElement? relativeTo)
    {
        var position = InputSystem.GetLocalPosition(relativeTo, ScreenPosition);
        return CreatePoint(position);
    }

    public IReadOnlyList<PointerPoint> GetIntermediatePoints(FrameworkElement? relativeTo)
    {
        if (IntermediatePoints == null || IntermediatePoints.Count == 0)
        {
            return new[] { GetCurrentPoint(relativeTo) };
        }

        var result = new PointerPoint[IntermediatePoints.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var point = IntermediatePoints[index];
            result[index] = new PointerPoint(
                point.PointerId,
                point.Timestamp,
                InputSystem.GetLocalPosition(relativeTo, point.RawPosition),
                point.RawPosition,
                point.PointerDevice.PointerDeviceType,
                point.IsInContact,
                point.Properties);
        }
        return result;
    }

    private PointerPoint CreatePoint(Vector2 position) => new(
        Pointer.PointerId,
        Timestamp,
        position,
        ScreenPosition,
        Pointer.LegacyPointerDeviceType,
        Pointer.IsInContact,
        new PointerPointProperties
        {
            IsLeftButtonPressed = IsLeftButtonPressed,
            IsMiddleButtonPressed = IsMiddleButtonPressed,
            IsRightButtonPressed = IsRightButtonPressed,
            IsPrimary = IsPrimary,
            IsCanceled = IsCanceled,
            Pressure = Pressure,
            ContactRect = new Windows.Foundation.Rect(
                ContactRect.X,
                ContactRect.Y,
                ContactRect.Width,
                ContactRect.Height),
            MouseWheelDelta = (int)WheelDelta
        });
}

public partial class FrameworkElement
{
    public static readonly Microsoft.UI.Xaml.DependencyProperty FlowDirectionProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "FlowDirection",
            typeof(Microsoft.UI.Xaml.FlowDirection),
            typeof(FrameworkElement),
            new Microsoft.UI.Xaml.PropertyMetadata(
                Microsoft.UI.Xaml.FlowDirection.LeftToRight,
                static (d, e) =>
                {
                    var element = (FrameworkElement)d;
                    var direction = (Microsoft.UI.Xaml.FlowDirection)(e.NewValue ?? Microsoft.UI.Xaml.FlowDirection.LeftToRight);
                    element.IsRightToLeftLayout = direction == Microsoft.UI.Xaml.FlowDirection.RightToLeft;
                },
                isInheritable: true)
            {
                AffectsMeasure = true,
                AffectsArrange = true,
                AffectsRender = true
            });

    public Microsoft.UI.Xaml.FlowDirection FlowDirection
    {
        get => (Microsoft.UI.Xaml.FlowDirection)(GetValue(FlowDirectionProperty) ?? Microsoft.UI.Xaml.FlowDirection.LeftToRight);
        set => SetValue(FlowDirectionProperty, value);
    }

    protected override Matrix4x4 GetCoordinateFrameTransform()
    {
        if (FlowDirection != Microsoft.UI.Xaml.FlowDirection.RightToLeft)
        {
            return Matrix4x4.Identity;
        }

        // WinUI defines (0, 0) at the top-right of an RTL FrameworkElement.
        // Keep this separate from the render transform: text and glyph content
        // must not be reflected even though the element's coordinate frame is.
        return Matrix4x4.CreateScale(-1f, 1f, 1f) *
               Matrix4x4.CreateTranslation(Size.X, 0f, 0f);
    }

    public static readonly Microsoft.UI.Xaml.DependencyProperty DataContextProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "DataContext",
            typeof(object),
            typeof(FrameworkElement),
            new Microsoft.UI.Xaml.PropertyMetadata(null, null, isInheritable: true));

    public object? DataContext
    {
        get => GetValue(DataContextProperty);
        set => SetValue(DataContextProperty, value);
    }

    public static readonly Microsoft.UI.Xaml.DependencyProperty IsPointerOverProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "IsPointerOver",
            typeof(bool),
            typeof(FrameworkElement),
            new Microsoft.UI.Xaml.PropertyMetadata(false, (d, e) => {
                var fe = (FrameworkElement)d;
                if (fe is Microsoft.UI.Xaml.Controls.Control c)
                {
                    c.OnVisualStateChanged();
                }
                else
                {
                    fe.Invalidate();
                }
                fe.OnPropertyChanged("IsPointerOver");
            }));

    public bool IsPointerOver
    {
        get => (bool)(GetValue(IsPointerOverProperty) ?? false);
        protected set => SetValue(IsPointerOverProperty, value);
    }

    public static readonly Microsoft.UI.Xaml.DependencyProperty IsPointerPressedProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "IsPointerPressed",
            typeof(bool),
            typeof(FrameworkElement),
            new Microsoft.UI.Xaml.PropertyMetadata(false, (d, e) => {
                var fe = (FrameworkElement)d;
                if (fe is Microsoft.UI.Xaml.Controls.Control c)
                {
                    c.OnVisualStateChanged();
                }
                else
                {
                    fe.Invalidate();
                }
                fe.OnPropertyChanged("IsPointerPressed");
            }));

    public bool IsPointerPressed
    {
        get => (bool)(GetValue(IsPointerPressedProperty) ?? false);
        protected set => SetValue(IsPointerPressedProperty, value);
    }

    public static readonly Microsoft.UI.Xaml.DependencyProperty RequestedThemeProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "RequestedTheme",
            typeof(ElementTheme),
            typeof(FrameworkElement),
            new Microsoft.UI.Xaml.PropertyMetadata(ElementTheme.Default, (d, e) => {
                ((FrameworkElement)d).NotifyThemeChanged();
            }));

    public ElementTheme RequestedTheme
    {
        get => (ElementTheme)(GetValue(RequestedThemeProperty) ?? ElementTheme.Default);
        set => SetValue(RequestedThemeProperty, value);
    }

    public static readonly Microsoft.UI.Xaml.DependencyProperty RequestedThemeFamilyProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "RequestedThemeFamily",
            typeof(VisualThemeFamily),
            typeof(FrameworkElement),
            new Microsoft.UI.Xaml.PropertyMetadata(VisualThemeFamily.Default, (d, e) => {
                ((FrameworkElement)d).NotifyThemeChanged();
            }));

    public VisualThemeFamily RequestedThemeFamily
    {
        get => (VisualThemeFamily)(GetValue(RequestedThemeFamilyProperty) ?? VisualThemeFamily.Default);
        set => SetValue(RequestedThemeFamilyProperty, value);
    }

    public static readonly Microsoft.UI.Xaml.DependencyProperty AllowDropProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "AllowDrop",
            typeof(bool),
            typeof(FrameworkElement),
            new Microsoft.UI.Xaml.PropertyMetadata(false, null));

    public bool AllowDrop
    {
        get => (bool)(GetValue(AllowDropProperty) ?? false);
        set => SetValue(AllowDropProperty, value);
    }

    public ElementTheme ActualTheme
    {
        get
        {
            var theme = RequestedTheme;
            if (theme != ElementTheme.Default)
            {
                return theme;
            }

            var p = Parent as FrameworkElement;
            while (p != null)
            {
                var pTheme = p.RequestedTheme;
                if (pTheme != ElementTheme.Default)
                {
                    return pTheme;
                }
                p = p.Parent as FrameworkElement;
            }

            return ThemeManager.CurrentTheme;
        }
    }

    public VisualThemeFamily ActualThemeFamily
    {
        get
        {
            var family = RequestedThemeFamily;
            if (family != VisualThemeFamily.Default)
            {
                return family;
            }

            var p = Parent as FrameworkElement;
            while (p != null)
            {
                var pFamily = p.RequestedThemeFamily;
                if (pFamily != VisualThemeFamily.Default)
                {
                    return pFamily;
                }
                p = p.Parent as FrameworkElement;
            }

            return ThemeManager.CurrentThemeFamily;
        }
    }


    public static readonly Microsoft.UI.Xaml.DependencyProperty FontProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "Font",
            typeof(ProGPU.Text.TtfFont),
            typeof(FrameworkElement),
            new Microsoft.UI.Xaml.PropertyMetadata(null, null, isInheritable: true));

    public ProGPU.Text.TtfFont? Font
    {
        get => (GetValue(FontProperty) as ProGPU.Text.TtfFont) ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        set => SetValue(FontProperty, value);
    }

    public ProGPU.Text.TtfFont? GetActiveFont()
    {
        return Font;
    }

    protected override void RaisePropertyChanged(string propertyName)
    {
        base.RaisePropertyChanged(propertyName);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (propertyName != null)
        {
            RaisePropertyChanged(propertyName);
        }
    }

    protected override void OnPropertyChanged(Microsoft.UI.Xaml.DependencyProperty dp, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(dp, oldValue, newValue);

        string name = dp.Name;
        
        // 1. Layout-affecting properties (require Measure, Arrange, and Paint)
        if (name == "Width" || name == "Height" || name == "MinWidth" || name == "MaxWidth" || 
            name == "MinHeight" || name == "MaxHeight" || name == "Margin" || name == "Padding" || 
            name == "HorizontalAlignment" || name == "VerticalAlignment" || name == "Visibility" || name == "FlowDirection" ||
            name == "Font" || name == "FontSize" || name == "Text" || name == "Content" || 
            name == "Child" || name == "Glyph" || name == "GlyphName" || name == "Symbol" || 
            name == "Header" || name == "Orientation" || name == "Dock" || name == "Spacing" || 
            name == "ItemsSource" || name == "Items" || name == "SelectedIndex" || name == "SelectedItem" ||
            name == "Value" || name == "Minimum" || name == "Maximum" || name == "IsExpanded" ||
            name == "BorderThickness")
        {
            InvalidateMeasure();
            InvalidateArrange();
            Invalidate();
        }
        // 2. Rendering/Paint-affecting properties (require Repaint only)
        else if (name == "Background" || name == "BorderBrush" || name == "Foreground" || 
                 name == "PlaceholderText" || name == "CornerRadius" || name == "IsSelected" || 
                 name == "IsChecked" || name == "IsOn" || name == "IsEnabled" || name == "IsActive" ||
                 name == "ShadowColor" || name == "ShadowOpacity" || name == "TintBrush")
        {
            Invalidate();
        }
    }

    public IList<KeyboardAccelerator> KeyboardAccelerators { get; } = new List<KeyboardAccelerator>();

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(); } }
    }

    private object? _tag;
    public object? Tag
    {
        get => _tag;
        set { if (_tag != value) { _tag = value; OnPropertyChanged(); } }
    }

    private bool _isHitTestVisible = true;
    public bool IsHitTestVisible
    {
        get => _isHitTestVisible;
        set { if (_isHitTestVisible != value) { _isHitTestVisible = value; OnPropertyChanged(); } }
    }

    public static readonly Microsoft.UI.Xaml.DependencyProperty IsEnabledProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "IsEnabled",
            typeof(bool),
            typeof(FrameworkElement),
            new Microsoft.UI.Xaml.PropertyMetadata(true, (d, e) => {
                ((FrameworkElement)d).OnIsEnabledChanged((bool)(e.NewValue ?? true));
            }));

    public bool IsEnabled
    {
        get
        {
            if (!(bool)(GetValue(IsEnabledProperty) ?? true)) return false;
            
            var p = Parent as FrameworkElement;
            while (p != null)
            {
                if (!(bool)(p.GetValue(IsEnabledProperty) ?? true)) return false;
                p = p.Parent as FrameworkElement;
            }
            return true;
        }
        set => SetValue(IsEnabledProperty, value);
    }

    protected virtual void OnIsEnabledChanged(bool enabled)
    {
        OnPropertyChanged(nameof(IsEnabled));
        Invalidate();

        foreach (var child in Children)
        {
            if (child is FrameworkElement fe)
            {
                fe.OnIsEnabledChanged(enabled);
            }
        }
    }

    public object? ToolTip
    {
        get => Microsoft.UI.Xaml.Controls.ToolTipService.GetToolTip(this);
        set
        {
            if (Equals(ToolTip, value)) return;
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(this, value);
            OnPropertyChanged();
        }
    }

    public static readonly DependencyProperty StyleProperty =
        DependencyProperty.Register(
            nameof(Style),
            typeof(Style),
            typeof(FrameworkElement),
            new PropertyMetadata(null, OnStyleChanged));

    private Style? _style;
    private bool _isUsingDefaultStyle = false;

    public Style? Style
    {
        get => (Style?)GetValue(StyleProperty);
        set => SetValue(StyleProperty, value);
    }

    public void SetDefaultStyle(Style defaultStyle)
    {
        _style = defaultStyle;
        _isUsingDefaultStyle = true;
        ApplyStyle();
    }

    private static void OnStyleChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var element = (FrameworkElement)dependencyObject;
        element._style = args.NewValue as Style;
        element._isUsingDefaultStyle = false;
        element.ApplyStyle();
        element.OnPropertyChanged(nameof(Style));
    }

    protected override void OnThemeChanged()
    {
        base.OnThemeChanged();
        if (_isUsingDefaultStyle)
        {
            var defaultStyle = ThemeManager.GetDefaultStyle(GetType(), ActualThemeFamily);
            if (defaultStyle != null)
            {
                _style = defaultStyle;
            }
        }
        ApplyStyle();
    }

    private void ApplyStyle()
    {
        ClearStyleValues();
        if (_style == null) return;
        var orderedStyles = new Stack<Style>();
        var visitedStyles = new HashSet<Style>(ReferenceEqualityComparer.Instance);
        for (var current = _style; current != null; current = current.BasedOn)
        {
            if (!visitedStyles.Add(current)) break;
            orderedStyles.Push(current);
        }

        while (orderedStyles.Count != 0)
        {
            var style = orderedStyles.Pop();
            if (style.TargetType is not { } targetType || !targetType.IsAssignableFrom(GetType())) continue;
            foreach (var setter in style.Setters)
            {
                if (string.IsNullOrEmpty(setter.Property)) continue;

                var dp = DependencyProperty.Lookup(GetType(), setter.Property);
                if (dp != null)
                {
                    var val = setter.Value;
                    if (val is StaticResourceRef staticRef)
                    {
                        val = new ThemeResource(staticRef.ResourceKey);
                    }

                    if (val is ThemeResource themeResource)
                    {
                        SetStyleValue(dp, themeResource);
                    }
                    else
                    {
                        var converted = XamlValueConverter.ConvertTo(dp.PropertyType, val);
                        SetStyleValue(dp, converted);
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Unsupported style setter '{setter.Property}' on {GetType().Name}: no dependency property is registered.");
                }
            }
        }
    }

    public static readonly Microsoft.UI.Xaml.DependencyProperty MarginProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "Margin",
            typeof(Microsoft.UI.Xaml.Thickness),
            typeof(FrameworkElement),
            new Microsoft.UI.Xaml.PropertyMetadata(default(Microsoft.UI.Xaml.Thickness), (d, e) => {
                var fe = (FrameworkElement)d;
                var t = (Microsoft.UI.Xaml.Thickness)(e.NewValue ?? default(Microsoft.UI.Xaml.Thickness));
                fe.SetMarginLayout(t);
            }));

    private void SetMarginLayout(Microsoft.UI.Xaml.Thickness t)
    {
        base.Margin = t;
    }

    public override Microsoft.UI.Xaml.Thickness Margin
    {
        get => (Microsoft.UI.Xaml.Thickness)(GetValue(MarginProperty) ?? default(Microsoft.UI.Xaml.Thickness));
        set => SetValue(MarginProperty, value);
    }

    public static readonly Microsoft.UI.Xaml.DependencyProperty PaddingProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "Padding",
            typeof(Microsoft.UI.Xaml.Thickness),
            typeof(FrameworkElement),
            new Microsoft.UI.Xaml.PropertyMetadata(default(Microsoft.UI.Xaml.Thickness), (d, e) => {
                var fe = (FrameworkElement)d;
                var t = (Microsoft.UI.Xaml.Thickness)(e.NewValue ?? default(Microsoft.UI.Xaml.Thickness));
                fe.SetPaddingLayout(t);
            }));

    private void SetPaddingLayout(Microsoft.UI.Xaml.Thickness t)
    {
        base.Padding = t;
    }

    public override Microsoft.UI.Xaml.Thickness Padding
    {
        get => (Microsoft.UI.Xaml.Thickness)(GetValue(PaddingProperty) ?? default(Microsoft.UI.Xaml.Thickness));
        set => SetValue(PaddingProperty, value);
    }

    public static readonly Microsoft.UI.Xaml.DependencyProperty HorizontalAlignmentProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "HorizontalAlignment",
            typeof(Microsoft.UI.Xaml.HorizontalAlignment),
            typeof(FrameworkElement),
            new Microsoft.UI.Xaml.PropertyMetadata(Microsoft.UI.Xaml.HorizontalAlignment.Stretch, (d, e) => {
                var fe = (FrameworkElement)d;
                fe.SetHorizontalAlignmentLayout((Microsoft.UI.Xaml.HorizontalAlignment)(e.NewValue ?? Microsoft.UI.Xaml.HorizontalAlignment.Stretch));
            }));

    private void SetHorizontalAlignmentLayout(Microsoft.UI.Xaml.HorizontalAlignment val)
    {
        base.HorizontalAlignment = val;
    }

    public override Microsoft.UI.Xaml.HorizontalAlignment HorizontalAlignment
    {
        get => (Microsoft.UI.Xaml.HorizontalAlignment)(GetValue(HorizontalAlignmentProperty) ?? Microsoft.UI.Xaml.HorizontalAlignment.Stretch);
        set => SetValue(HorizontalAlignmentProperty, value);
    }

    public static readonly Microsoft.UI.Xaml.DependencyProperty VerticalAlignmentProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "VerticalAlignment",
            typeof(Microsoft.UI.Xaml.VerticalAlignment),
            typeof(FrameworkElement),
            new Microsoft.UI.Xaml.PropertyMetadata(Microsoft.UI.Xaml.VerticalAlignment.Stretch, (d, e) => {
                var fe = (FrameworkElement)d;
                fe.SetVerticalAlignmentLayout((Microsoft.UI.Xaml.VerticalAlignment)(e.NewValue ?? Microsoft.UI.Xaml.VerticalAlignment.Stretch));
            }));

    private void SetVerticalAlignmentLayout(Microsoft.UI.Xaml.VerticalAlignment val)
    {
        base.VerticalAlignment = val;
    }

    public override Microsoft.UI.Xaml.VerticalAlignment VerticalAlignment
    {
        get => (Microsoft.UI.Xaml.VerticalAlignment)(GetValue(VerticalAlignmentProperty) ?? Microsoft.UI.Xaml.VerticalAlignment.Stretch);
        set => SetValue(VerticalAlignmentProperty, value);
    }

    public static readonly Microsoft.UI.Xaml.DependencyProperty WidthProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "Width",
            typeof(float),
            typeof(FrameworkElement),
            new Microsoft.UI.Xaml.PropertyMetadata(float.NaN, (d, e) => {
                var fe = (FrameworkElement)d;
                var val = (float)(e.NewValue ?? float.NaN);
                fe.SetWidthConstraintLayout(float.IsNaN(val) ? null : val);
            }));

    private void SetWidthConstraintLayout(float? val)
    {
        base.WidthConstraint = val;
    }

    public float Width
    {
        get => (float)(GetValue(WidthProperty) ?? float.NaN);
        set => SetValue(WidthProperty, value);
    }

    public static readonly Microsoft.UI.Xaml.DependencyProperty HeightProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "Height",
            typeof(float),
            typeof(FrameworkElement),
            new Microsoft.UI.Xaml.PropertyMetadata(float.NaN, (d, e) => {
                var fe = (FrameworkElement)d;
                var val = (float)(e.NewValue ?? float.NaN);
                fe.SetHeightConstraintLayout(float.IsNaN(val) ? null : val);
            }));

    private void SetHeightConstraintLayout(float? val)
    {
        base.HeightConstraint = val;
    }

    public float Height
    {
        get => (float)(GetValue(HeightProperty) ?? float.NaN);
        set => SetValue(HeightProperty, value);
    }

    public static readonly DependencyProperty MinWidthProperty = DependencyProperty.Register(
        nameof(MinWidth), typeof(float), typeof(FrameworkElement),
        new PropertyMetadata(0f, static (d, e) =>
            ((FrameworkElement)d).MinimumWidthConstraint = (float)(e.NewValue ?? 0f))
        { AffectsMeasure = true, AffectsArrange = true });

    public float MinWidth
    {
        get => (float)(GetValue(MinWidthProperty) ?? 0f);
        set => SetValue(MinWidthProperty, Math.Max(0f, value));
    }

    public static readonly DependencyProperty MinHeightProperty = DependencyProperty.Register(
        nameof(MinHeight), typeof(float), typeof(FrameworkElement),
        new PropertyMetadata(0f, static (d, e) =>
            ((FrameworkElement)d).MinimumHeightConstraint = (float)(e.NewValue ?? 0f))
        { AffectsMeasure = true, AffectsArrange = true });

    public float MinHeight
    {
        get => (float)(GetValue(MinHeightProperty) ?? 0f);
        set => SetValue(MinHeightProperty, Math.Max(0f, value));
    }

    public static readonly DependencyProperty MaxWidthProperty = DependencyProperty.Register(
        nameof(MaxWidth), typeof(float), typeof(FrameworkElement),
        new PropertyMetadata(float.PositiveInfinity, static (d, e) =>
            ((FrameworkElement)d).MaximumWidthConstraint = (float)(e.NewValue ?? float.PositiveInfinity))
        { AffectsMeasure = true, AffectsArrange = true });

    public float MaxWidth
    {
        get => (float)(GetValue(MaxWidthProperty) ?? float.PositiveInfinity);
        set => SetValue(MaxWidthProperty, Math.Max(0f, value));
    }

    public static readonly DependencyProperty MaxHeightProperty = DependencyProperty.Register(
        nameof(MaxHeight), typeof(float), typeof(FrameworkElement),
        new PropertyMetadata(float.PositiveInfinity, static (d, e) =>
            ((FrameworkElement)d).MaximumHeightConstraint = (float)(e.NewValue ?? float.PositiveInfinity))
        { AffectsMeasure = true, AffectsArrange = true });

    public float MaxHeight
    {
        get => (float)(GetValue(MaxHeightProperty) ?? float.PositiveInfinity);
        set => SetValue(MaxHeightProperty, Math.Max(0f, value));
    }

    // Routed Events
    public event EventHandler<PointerRoutedEventArgs>? PointerPressed;
    public event EventHandler<PointerRoutedEventArgs>? PointerReleased;
    public event EventHandler<PointerRoutedEventArgs>? PointerMoved;
    public event EventHandler<PointerRoutedEventArgs>? PointerEntered;
    public event EventHandler<PointerRoutedEventArgs>? PointerExited;
    public event EventHandler<PointerRoutedEventArgs>? PointerWheelChanged;
    public event EventHandler<PointerRoutedEventArgs>? PointerCanceled;
    public event EventHandler<PointerRoutedEventArgs>? PointerCaptureLost;

    public event TappedEventHandler? Tapped;
    public event DoubleTappedEventHandler? DoubleTapped;
    public event RightTappedEventHandler? RightTapped;
    public event HoldingEventHandler? Holding;
    public event ManipulationStartingEventHandler? ManipulationStarting;
    public event ManipulationStartedEventHandler? ManipulationStarted;
    public event ManipulationDeltaEventHandler? ManipulationDelta;
    public event ManipulationInertiaStartingEventHandler? ManipulationInertiaStarting;
    public event ManipulationCompletedEventHandler? ManipulationCompleted;

    public event EventHandler<KeyRoutedEventArgs>? KeyDown;
    public event EventHandler<KeyRoutedEventArgs>? KeyUp;
    public event EventHandler<CharacterReceivedRoutedEventArgs>? CharacterReceived;
    public event EventHandler<TextInputRoutedEventArgs>? TextInput;

    public static readonly DependencyProperty ManipulationModeProperty =
        DependencyProperty.Register(
            nameof(ManipulationMode),
            typeof(ManipulationModes),
            typeof(FrameworkElement),
            new PropertyMetadata(ManipulationModes.System));

    public ManipulationModes ManipulationMode
    {
        get => (ManipulationModes)(GetValue(ManipulationModeProperty) ?? ManipulationModes.System);
        set => SetValue(ManipulationModeProperty, value);
    }

    public bool IsTapEnabled { get; set; } = true;
    public bool IsDoubleTapEnabled { get; set; } = true;
    public bool IsRightTapEnabled { get; set; } = true;
    public bool IsHoldingEnabled { get; set; } = true;

    public bool CapturePointer(Pointer pointer) => InputSystem.CapturePointer(this, pointer);
    public void ReleasePointerCapture(Pointer pointer) => InputSystem.ReleasePointerCapture(this, pointer);
    public void ReleasePointerCaptures() => InputSystem.ReleasePointerCaptures(this);

    // Drag & Drop Events
    public event EventHandler<DragEventArgs>? DragEnter;
    public event EventHandler<DragEventArgs>? DragOver;
    public event EventHandler<DragEventArgs>? DragLeave;
    public event EventHandler<DragEventArgs>? Drop;

    // Helper methods to trigger routed events
    public virtual void OnPointerPressed(PointerRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        if (IsEnabled)
        {
            IsPointerPressed = true;
        }
        PointerPressed?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnPointerPressed(e);
        }
    }

    public virtual void OnPointerReleased(PointerRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        if (IsEnabled)
        {
            IsPointerPressed = false;
        }
        PointerReleased?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnPointerReleased(e);
        }
    }

    public virtual void OnPointerMoved(PointerRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        PointerMoved?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnPointerMoved(e);
        }
    }

    public virtual void OnPointerEntered(PointerRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        if (IsEnabled)
        {
            IsPointerOver = true;
        }
        PointerEntered?.Invoke(this, e);
    }

    public virtual void OnPointerExited(PointerRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        if (IsEnabled)
        {
            IsPointerOver = false;
            IsPointerPressed = false;
        }
        PointerExited?.Invoke(this, e);
    }

    public virtual void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        PointerWheelChanged?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnPointerWheelChanged(e);
        }
    }

    public virtual void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        IsPointerPressed = false;
        PointerCanceled?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe) parentFe.OnPointerCanceled(e);
    }

    public virtual void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        IsPointerPressed = false;
        PointerCaptureLost?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe) parentFe.OnPointerCaptureLost(e);
    }

    public virtual void OnTapped(TappedRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        Tapped?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parent) parent.OnTapped(e);
    }
    public virtual void OnDoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        DoubleTapped?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parent) parent.OnDoubleTapped(e);
    }
    public virtual void OnRightTapped(RightTappedRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        RightTapped?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parent) parent.OnRightTapped(e);
    }
    public virtual void OnHolding(HoldingRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        Holding?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parent) parent.OnHolding(e);
    }

    public virtual void OnManipulationStarting(ManipulationStartingRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        ManipulationStarting?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parent) parent.OnManipulationStarting(e);
    }
    public virtual void OnManipulationStarted(ManipulationStartedRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        ManipulationStarted?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parent) parent.OnManipulationStarted(e);
    }
    public virtual void OnManipulationDelta(ManipulationDeltaRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        ManipulationDelta?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parent) parent.OnManipulationDelta(e);
    }
    public virtual void OnManipulationInertiaStarting(ManipulationInertiaStartingRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        ManipulationInertiaStarting?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parent) parent.OnManipulationInertiaStarting(e);
    }
    public virtual void OnManipulationCompleted(ManipulationCompletedRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        ManipulationCompleted?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parent) parent.OnManipulationCompleted(e);
    }

    public virtual void OnTextInput(TextInputRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        TextInput?.Invoke(this, e);
        if (this is ITextInputClient client && !e.Handled) client.OnTextInput(e);
        if (!e.Handled && Parent is FrameworkElement parentFe) parentFe.OnTextInput(e);
    }

    public virtual void OnKeyDown(KeyRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        KeyDown?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnKeyDown(e);
        }
    }

    public virtual void OnKeyUp(KeyRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        KeyUp?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnKeyUp(e);
        }
    }

    public virtual void OnCharacterReceived(CharacterReceivedRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        CharacterReceived?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnCharacterReceived(e);
        }
    }

    public virtual void OnDragEnter(DragEventArgs e)
    {
        e.OriginalSource ??= this;
        DragEnter?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnDragEnter(e);
        }
    }

    public virtual void OnDragOver(DragEventArgs e)
    {
        e.OriginalSource ??= this;
        DragOver?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnDragOver(e);
        }
    }

    public virtual void OnDragLeave(DragEventArgs e)
    {
        e.OriginalSource ??= this;
        DragLeave?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnDragLeave(e);
        }
    }

    public virtual void OnDrop(DragEventArgs e)
    {
        e.OriginalSource ??= this;
        Drop?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnDrop(e);
        }
    }
}
