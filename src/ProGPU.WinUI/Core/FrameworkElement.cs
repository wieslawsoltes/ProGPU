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
}

public partial class FrameworkElement
{
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
            name == "HorizontalAlignment" || name == "VerticalAlignment" || name == "Visibility" || 
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

    private object? _toolTip;
    public object? ToolTip
    {
        get => _toolTip;
        set { if (_toolTip != value) { _toolTip = value; OnPropertyChanged(); } }
    }

    private Style? _style;
    private bool _isUsingDefaultStyle = false;

    public Style? Style
    {
        get => _style;
        set
        {
            if (_style != value)
            {
                _style = value;
                _isUsingDefaultStyle = false;
                ApplyStyle();
                OnPropertyChanged();
            }
        }
    }

    public void SetDefaultStyle(Style defaultStyle)
    {
        _style = defaultStyle;
        _isUsingDefaultStyle = true;
        ApplyStyle();
    }

    private object? ConvertValue(Type targetType, object? value)
    {
        if (value == null) return null;

        var valType = value.GetType();
        if (targetType.IsAssignableFrom(valType))
        {
            return value;
        }



        // 1. Enum conversion
        if (targetType.IsEnum && value is string strEnum)
        {
            return Enum.Parse(targetType, strEnum, true);
        }

        // 2. Boolean conversion
        if (targetType == typeof(bool) && value is string strBool)
        {
            return bool.Parse(strBool);
        }

        // 3. Thickness conversion
        if (targetType == typeof(Thickness))
        {
            if (value is float fVal) return new Thickness(fVal);
            if (value is double dVal) return new Thickness((float)dVal);
            if (value is int iVal) return new Thickness(iVal);
            if (value is string strThick)
            {
                var parts = strThick.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 1 && float.TryParse(parts[0], out float uniform))
                    return new Thickness(uniform);
                if (parts.Length == 2 && float.TryParse(parts[0], out float h) && float.TryParse(parts[1], out float v))
                    return new Thickness(h, v);
                if (parts.Length == 4 && float.TryParse(parts[0], out float l) && float.TryParse(parts[1], out float t) && float.TryParse(parts[2], out float r) && float.TryParse(parts[3], out float b))
                    return new Thickness(l, t, r, b);
            }
        }

        // 4. CornerRadius conversion (which is defined as a float in ProGPU)
        if (targetType == typeof(float) && value is string strFloat)
        {
            if (float.TryParse(strFloat, out float parsedFloat))
            {
                return parsedFloat;
            }
        }

        // 5. Brush conversion
        if (targetType == typeof(ProGPU.Vector.Brush) && value is string strBrush)
        {
            if (strBrush.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
            {
                return new ProGPU.Vector.SolidColorBrush(new Vector4(0f, 0f, 0f, 0f));
            }
            if (strBrush.StartsWith("#"))
            {
                var hex = strBrush.Substring(1);
                if (hex.Length == 6) hex = "FF" + hex;
                if (hex.Length == 8)
                {
                    uint rgba = Convert.ToUInt32(hex, 16);
                    float a = ((rgba >> 24) & 0xFF) / 255.0f;
                    float r = ((rgba >> 16) & 0xFF) / 255.0f;
                    float g = ((rgba >> 8) & 0xFF) / 255.0f;
                    float b = (rgba & 0xFF) / 255.0f;
                    return new ProGPU.Vector.SolidColorBrush(new Vector4(r, g, b, a));
                }
            }
        }

        // 6. Vector4 color conversion
        if (targetType == typeof(Vector4) && value is string strColor)
        {
            if (strColor.StartsWith("#"))
            {
                var hex = strColor.Substring(1);
                if (hex.Length == 6) hex = "FF" + hex;
                if (hex.Length == 8)
                {
                    uint rgba = Convert.ToUInt32(hex, 16);
                    float a = ((rgba >> 24) & 0xFF) / 255.0f;
                    float r = ((rgba >> 16) & 0xFF) / 255.0f;
                    float g = ((rgba >> 8) & 0xFF) / 255.0f;
                    float b = (rgba & 0xFF) / 255.0f;
                    return new Vector4(r, g, b, a);
                }
            }
        }

        // 7. Numeric standard conversions
        if (targetType == typeof(float)) return Convert.ToSingle(value);
        if (targetType == typeof(double)) return Convert.ToDouble(value);
        if (targetType == typeof(int)) return Convert.ToInt32(value);

        return Convert.ChangeType(value, targetType);
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
        if (!_style.TargetType.IsAssignableFrom(GetType())) return;

        foreach (var setter in _style.Setters)
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
                    var converted = ConvertValue(dp.PropertyType, val);
                    SetStyleValue(dp, converted);
                }
            }
            else
            {
                var prop = GetType().GetProperty(setter.Property, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (prop != null && prop.CanWrite)
                {
                    try
                    {
                        var val = setter.Value;
                        if (val is StaticResourceRef staticRef)
                        {
                            val = ThemeManager.GetResource(staticRef.ResourceKey, this.ActualTheme);
                        }
                        else if (val is ThemeResource themeResource)
                        {
                            val = ThemeManager.GetResource(themeResource.ResourceKey, this.ActualTheme);
                        }
                        var convertedValue = ConvertValue(prop.PropertyType, val);
                        prop.SetValue(this, convertedValue);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error applying style property '{setter.Property}' on {GetType().Name}: {ex.Message}");
                    }
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

    // Routed Events
    public event EventHandler<PointerRoutedEventArgs>? PointerPressed;
    public event EventHandler<PointerRoutedEventArgs>? PointerReleased;
    public event EventHandler<PointerRoutedEventArgs>? PointerMoved;
    public event EventHandler<PointerRoutedEventArgs>? PointerEntered;
    public event EventHandler<PointerRoutedEventArgs>? PointerExited;
    public event EventHandler<PointerRoutedEventArgs>? PointerWheelChanged;

    public event EventHandler<KeyRoutedEventArgs>? KeyDown;
    public event EventHandler<KeyRoutedEventArgs>? KeyUp;
    public event EventHandler<CharacterReceivedRoutedEventArgs>? CharacterReceived;

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

