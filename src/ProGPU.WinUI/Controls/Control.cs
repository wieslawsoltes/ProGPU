using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Microsoft.UI.Xaml.Controls;

public class Control : FrameworkElement, ITemplatedControl
{
    public static readonly DependencyProperty IsTemplateFocusTargetProperty = DependencyProperty.RegisterAttached(
        "IsTemplateFocusTarget", typeof(bool), typeof(Control), new PropertyMetadata(false));

    public static bool GetIsTemplateFocusTarget(FrameworkElement element) =>
        (bool)(element.GetValue(IsTemplateFocusTargetProperty) ?? false);

    public static void SetIsTemplateFocusTarget(FrameworkElement element, bool value) =>
        element.SetValue(IsTemplateFocusTargetProperty, value);

    public static readonly DependencyProperty IsTemplateKeyTipTargetProperty =
        DependencyProperty.RegisterAttached(
            "IsTemplateKeyTipTarget", typeof(bool), typeof(Control), new PropertyMetadata(false));

    public static bool GetIsTemplateKeyTipTarget(DependencyObject element) =>
        (bool)(element.GetValue(IsTemplateKeyTipTargetProperty) ?? false);

    public static void SetIsTemplateKeyTipTarget(DependencyObject element, bool value) =>
        element.SetValue(IsTemplateKeyTipTargetProperty, value);

    public static readonly DependencyProperty TabNavigationProperty = DependencyProperty.Register(
        nameof(TabNavigation), typeof(KeyboardNavigationMode), typeof(Control),
        new PropertyMetadata(KeyboardNavigationMode.Local));

    public static readonly DependencyProperty IsFocusEngagementEnabledProperty =
        DependencyProperty.Register(
            nameof(IsFocusEngagementEnabled), typeof(bool), typeof(Control),
            new PropertyMetadata(false));

    public static readonly DependencyProperty ElementSoundModeProperty = DependencyProperty.Register(
        nameof(ElementSoundMode), typeof(ElementSoundMode), typeof(Control),
        new PropertyMetadata(ElementSoundMode.Default));

    public KeyboardNavigationMode TabNavigation
    {
        get => (KeyboardNavigationMode)(GetValue(TabNavigationProperty) ?? KeyboardNavigationMode.Local);
        set => SetValue(TabNavigationProperty, value);
    }

    public bool IsFocusEngagementEnabled
    {
        get => (bool)(GetValue(IsFocusEngagementEnabledProperty) ?? false);
        set => SetValue(IsFocusEngagementEnabledProperty, value);
    }

    public ElementSoundMode ElementSoundMode
    {
        get => (ElementSoundMode)(GetValue(ElementSoundModeProperty) ?? ElementSoundMode.Default);
        set => SetValue(ElementSoundModeProperty, value);
    }

    public static readonly DependencyProperty FontFamilyProperty =
        DependencyProperty.Register(
            nameof(FontFamily),
            typeof(FontFamily),
            typeof(Control),
            new PropertyMetadata(FontFamily.XamlAutoFontFamily) { AffectsMeasure = true, AffectsRender = true });

    public FontFamily FontFamily
    {
        get => (FontFamily)(GetValue(FontFamilyProperty) ?? FontFamily.XamlAutoFontFamily);
        set => SetValue(FontFamilyProperty, value);
    }

    public static readonly DependencyProperty FontWeightProperty =
        DependencyProperty.Register(
            nameof(FontWeight),
            typeof(FontWeight),
            typeof(Control),
            new PropertyMetadata(Microsoft.UI.Text.FontWeights.Normal) { AffectsMeasure = true, AffectsRender = true });

    public FontWeight FontWeight
    {
        get => (FontWeight)(GetValue(FontWeightProperty) ?? Microsoft.UI.Text.FontWeights.Normal);
        set => SetValue(FontWeightProperty, value);
    }

    public static readonly DependencyProperty FontSizeProperty =
        DependencyProperty.Register(
            nameof(FontSize),
            typeof(double),
            typeof(Control),
            new PropertyMetadata(14d) { AffectsMeasure = true, AffectsRender = true });

    public double FontSize
    {
        get => (double)(GetValue(FontSizeProperty) ?? 14d);
        set => SetValue(FontSizeProperty, value);
    }

    public static readonly Microsoft.UI.Xaml.DependencyProperty BackgroundProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "Background",
            typeof(Brush),
            typeof(Control),
            new Microsoft.UI.Xaml.PropertyMetadata(null) { AffectsRender = true });

    public static readonly Microsoft.UI.Xaml.DependencyProperty BackgroundSizingProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            nameof(BackgroundSizing),
            typeof(BackgroundSizing),
            typeof(Control),
            new Microsoft.UI.Xaml.PropertyMetadata(BackgroundSizing.InnerBorderEdge)
            {
                AffectsRender = true
            });

    public static readonly Microsoft.UI.Xaml.DependencyProperty ForegroundProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "Foreground",
            typeof(Brush),
            typeof(Control),
            new Microsoft.UI.Xaml.PropertyMetadata(null) { AffectsRender = true });

    public static readonly Microsoft.UI.Xaml.DependencyProperty BorderBrushProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "BorderBrush",
            typeof(Brush),
            typeof(Control),
            new Microsoft.UI.Xaml.PropertyMetadata(null) { AffectsRender = true });

    public static readonly Microsoft.UI.Xaml.DependencyProperty BorderThicknessProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "BorderThickness",
            typeof(Thickness),
            typeof(Control),
            new Microsoft.UI.Xaml.PropertyMetadata(default(Thickness)) { AffectsMeasure = true, AffectsArrange = true, AffectsRender = true });

    public static readonly Microsoft.UI.Xaml.DependencyProperty CornerRadiusProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "CornerRadius",
            typeof(CornerRadius),
            typeof(Control),
            new Microsoft.UI.Xaml.PropertyMetadata(default(CornerRadius)) { AffectsRender = true });

    public static readonly Microsoft.UI.Xaml.DependencyProperty TemplateProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            "Template",
            typeof(ControlTemplate),
            typeof(Control),
            new Microsoft.UI.Xaml.PropertyMetadata(null, (d, e) => ((Control)d).ApplyTemplate()));





    private FrameworkElement? _templateRoot;

    public ControlTemplate? Template
    {
        get => GetValue(TemplateProperty) as ControlTemplate;
        set => SetValue(TemplateProperty, value);
    }

    public override bool HasTemplate => _templateRoot != null;

    public Brush? Background
    {
        get => GetValue(BackgroundProperty) as Brush;
        set => SetValue(BackgroundProperty, value);
    }

    public BackgroundSizing BackgroundSizing
    {
        get => (BackgroundSizing)(
            GetValue(BackgroundSizingProperty) ??
            BackgroundSizing.InnerBorderEdge);
        set => SetValue(BackgroundSizingProperty, value);
    }

    public Brush? Foreground
    {
        get => GetValue(ForegroundProperty) as Brush;
        set => SetValue(ForegroundProperty, value);
    }

    public Brush? BorderBrush
    {
        get => GetValue(BorderBrushProperty) as Brush;
        set => SetValue(BorderBrushProperty, value);
    }

    public Thickness BorderThickness
    {
        get => (Thickness)(GetValue(BorderThicknessProperty) ?? default(Thickness));
        set => SetValue(BorderThicknessProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)(GetValue(CornerRadiusProperty) ?? default(CornerRadius));
        set => SetValue(CornerRadiusProperty, value);
    }

    public static readonly DependencyProperty HorizontalContentAlignmentProperty =
        DependencyProperty.Register(
            "HorizontalContentAlignment",
            typeof(HorizontalAlignment),
            typeof(Control),
            new PropertyMetadata(HorizontalAlignment.Center, (d, e) => {
                var c = (Control)d;
                c.Invalidate();
                c.InvalidateMeasure();
            }));

    public HorizontalAlignment HorizontalContentAlignment
    {
        get => (HorizontalAlignment)(GetValue(HorizontalContentAlignmentProperty) ?? HorizontalAlignment.Center);
        set => SetValue(HorizontalContentAlignmentProperty, value);
    }

    public static readonly DependencyProperty VerticalContentAlignmentProperty =
        DependencyProperty.Register(
            "VerticalContentAlignment",
            typeof(VerticalAlignment),
            typeof(Control),
            new PropertyMetadata(VerticalAlignment.Center, (d, e) => {
                var c = (Control)d;
                c.Invalidate();
                c.InvalidateMeasure();
            }));

    public VerticalAlignment VerticalContentAlignment
    {
        get => (VerticalAlignment)(GetValue(VerticalContentAlignmentProperty) ?? VerticalAlignment.Center);
        set => SetValue(VerticalContentAlignmentProperty, value);
    }

    public static readonly DependencyProperty IsFocusedProperty =
        DependencyProperty.Register(
            "IsFocused",
            typeof(bool),
            typeof(Control),
            new PropertyMetadata(false, (d, e) => {
                var c = (Control)d;
                c.OnVisualStateChanged();
                c.OnPropertyChanged("IsFocused");
            }));

    public bool IsFocused
    {
        get => (bool)(GetValue(IsFocusedProperty) ?? false);
        internal set => SetValue(IsFocusedProperty, value);
    }

    internal bool IsFocusedVisualStateActive => IsFocused &&
        (InputSystem.IsKeyboardFocusActive || this is ITextInputClient);

    internal bool IsKeyboardFocusVisualVisible =>
        IsFocused && InputSystem.IsKeyboardFocusActive && UseSystemFocusVisuals;

    private bool _isTabStop = true;
    public bool IsTabStop
    {
        get => _isTabStop;
        set { if (_isTabStop != value) { _isTabStop = value; OnPropertyChanged(); } }
    }

    public bool ApplyTemplate()
    {
        if (_templateRoot != null)
        {
            Microsoft.UI.Xaml.Data.BindingOperations.ClearBindingsForContext(this);
            TemplateBinding.ClearBindingsForSource(this);
            Microsoft.UI.Xaml.Markup.XamlTemplateFactory.ReleaseSubtree(_templateRoot);
            RemoveChild(_templateRoot);
            _templateRoot = null;
        }

        var generatedTemplateRoot = Microsoft.UI.Xaml.Markup.XamlTemplateFactory.Build(Template, this);
        if (generatedTemplateRoot == null && Template?.Factory is { } templateFactory)
        {
            generatedTemplateRoot = templateFactory(this);
            if (generatedTemplateRoot != null)
            {
                Microsoft.UI.Xaml.Markup.XamlTemplateFactory.SetTemplateContext(
                    generatedTemplateRoot,
                    this);
            }
        }
        if (generatedTemplateRoot != null)
        {
            _templateRoot = generatedTemplateRoot;
            if (_templateRoot != null)
            {
                AddChild(_templateRoot);
                
                // Resolve ContentPresenters inside the visual tree automatically
                ResolveContentPresenters(_templateRoot);

                OnApplyTemplate();
                OnVisualStateChanged();
                InvalidateMeasure();
                return true;
            }
        }

        InvalidateMeasure();
        return false;
    }

    protected virtual void OnApplyTemplate()
    {
    }

    public FrameworkElement? GetTemplateChild(string name)
    {
        if (_templateRoot == null) return null;
        if (Microsoft.UI.Xaml.Markup.XamlTemplateFactory.FindName(
                _templateRoot,
                name) is FrameworkElement named)
            return named;
        return FindNameInTree(_templateRoot, name);
    }

    private FrameworkElement? FindNameInTree(FrameworkElement element, string name)
    {
        if (element.Name == name) return element;

        if (element is ContainerVisual container)
        {
            var children = container.Children;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is FrameworkElement childFe)
                {
                    var found = FindNameInTree(childFe, name);
                    if (found != null) return found;
                }
            }
        }

        return null;
    }

    private void ResolveContentPresenters(FrameworkElement element)
    {
        if (element is ContentPresenter presenter)
        {
            var targetDp = DependencyProperty.Lookup(presenter.GetType(), "Content");
            var sourceDp = DependencyProperty.Lookup(this.GetType(), "Content");
            if (targetDp != null && sourceDp != null)
            {
                TemplateBinding.Bind(presenter, targetDp, this, sourceDp);
            }

            var targetHoriz = DependencyProperty.Lookup(presenter.GetType(), "HorizontalContentAlignment");
            var sourceHoriz = DependencyProperty.Lookup(this.GetType(), "HorizontalContentAlignment");
            if (targetHoriz != null && sourceHoriz != null)
            {
                TemplateBinding.Bind(presenter, targetHoriz, this, sourceHoriz);
            }

            var targetVert = DependencyProperty.Lookup(presenter.GetType(), "VerticalContentAlignment");
            var sourceVert = DependencyProperty.Lookup(this.GetType(), "VerticalContentAlignment");
            if (targetVert != null && sourceVert != null)
            {
                TemplateBinding.Bind(presenter, targetVert, this, sourceVert);
            }
        }

        if (element is ContainerVisual container)
        {
            var children = container.Children;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is FrameworkElement childFe)
                {
                    ResolveContentPresenters(childFe);
                }
            }
        }
    }

    public Vector2 MeasureTemplate(Vector2 availableSize)
    {
        if (_templateRoot != null)
        {
            float borderH = BorderThickness.Horizontal;
            float borderV = BorderThickness.Vertical;

            Vector2 inset = new Vector2(borderH, borderV);
            Vector2 childAvailable = new Vector2(
                Math.Max(0f, availableSize.X - inset.X),
                Math.Max(0f, availableSize.Y - inset.Y)
            );

            _templateRoot.Measure(childAvailable);
            return _templateRoot.DesiredSize + inset;
        }
        return Vector2.Zero;
    }

    public void ArrangeTemplate(Rect arrangeRect)
    {
        if (_templateRoot != null)
        {
            float leftInset = BorderThickness.Left;
            float topInset = BorderThickness.Top;
            float rightInset = BorderThickness.Right;
            float bottomInset = BorderThickness.Bottom;

            Rect childRect = new Rect(
                arrangeRect.X + leftInset,
                arrangeRect.Y + topInset,
                Math.Max(0f, arrangeRect.Width - (leftInset + rightInset)),
                Math.Max(0f, arrangeRect.Height - (topInset + bottomInset))
            );
            _templateRoot.Arrange(childRect);
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (HasTemplate)
        {
            return MeasureTemplate(availableSize);
        }
        return base.MeasureOverride(availableSize);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        if (HasTemplate)
        {
            ArrangeTemplate(arrangeRect);
            return;
        }
        base.ArrangeOverride(arrangeRect);
    }


    protected virtual string GetThemePrefix()
    {
        if (this is Button && Style != null)
        {
            foreach (var setter in Style.Setters)
            {
                if (setter.Value?.ToString()?.Contains("AccentButton") == true)
                {
                    return "AccentButton";
                }
            }
        }

        Type? type = GetType();
        while (type != null && type != typeof(Control) && type != typeof(FrameworkElement))
        {
            string name = type.Name;
            if (name == "TextBox" || name == "PasswordBox" || name == "Button" || 
                name == "ComboBox" || name == "ComboBoxItem" || name == "ListBox" || 
                name == "ListBoxItem" || name == "CheckBox" || 
                name == "RadioButton" || name == "ToggleSwitch" || name == "Slider" || 
                name == "ProgressBar" || name == "ProgressRing" || name == "RatingControl" || 
                name == "CalendarView" || name == "ContentDialog" || name == "TextBlock" || 
                name == "RichTextBlock")
            {
                return name;
            }
            type = type.BaseType;
        }

        return GetType().Name;
    }

    public virtual Brush? GetCurrentBackground()
    {
        string prefix = GetThemePrefix();
        var theme = ActualTheme;
        if (!IsEnabled) return (ThemeManager.GetResource($"{prefix}BackgroundDisabled", theme) as Brush) ?? Background;
        if (IsPointerPressed) return (ThemeManager.GetResource($"{prefix}BackgroundPressed", theme) as Brush) ?? Background;
        if (IsPointerOver) return (ThemeManager.GetResource($"{prefix}BackgroundPointerOver", theme) as Brush) ?? Background;
        if (IsFocusedVisualStateActive) return (ThemeManager.GetResource($"{prefix}BackgroundFocused", theme) as Brush) ?? (ThemeManager.GetResource($"{prefix}BackgroundPressed", theme) as Brush) ?? Background;
        return Background;
    }

    public virtual Brush? GetCurrentForeground()
    {
        string prefix = GetThemePrefix();
        var theme = ActualTheme;
        if (!IsEnabled) return (ThemeManager.GetResource($"{prefix}ForegroundDisabled", theme) as Brush) ?? Foreground;
        if (IsPointerPressed) return (ThemeManager.GetResource($"{prefix}ForegroundPressed", theme) as Brush) ?? Foreground;
        if (IsPointerOver) return (ThemeManager.GetResource($"{prefix}ForegroundPointerOver", theme) as Brush) ?? Foreground;
        if (IsFocusedVisualStateActive) return (ThemeManager.GetResource($"{prefix}ForegroundFocused", theme) as Brush) ?? Foreground;
        return Foreground;
    }

    public virtual Brush? GetCurrentBorderBrush()
    {
        string prefix = GetThemePrefix();
        var theme = ActualTheme;
        if (!IsEnabled) return (ThemeManager.GetResource($"{prefix}BorderBrushDisabled", theme) as Brush) ?? BorderBrush;
        if (IsPointerPressed) return (ThemeManager.GetResource($"{prefix}BorderBrushPressed", theme) as Brush) ?? BorderBrush;
        if (IsPointerOver) return (ThemeManager.GetResource($"{prefix}BorderBrushPointerOver", theme) as Brush) ?? BorderBrush;
        if (IsFocusedVisualStateActive) return (ThemeManager.GetResource($"{prefix}BorderBrushFocused", theme) as Brush) ?? (ThemeManager.GetResource($"{prefix}BorderBrushPressed", theme) as Brush) ?? BorderBrush;
        return BorderBrush;
    }

    public virtual void OnVisualStateChanged()
    {
        string stateName = !IsEnabled
            ? "Disabled"
            : IsPointerPressed && IsPointerOver
                ? "Pressed"
                : IsPointerOver
                    ? "PointerOver"
                    : "Normal";
        VisualStateManager.GoToState(this, stateName, useTransitions: true);
        OnPropertyChanged(nameof(Background));
        OnPropertyChanged(nameof(Foreground));
        OnPropertyChanged(nameof(BorderBrush));
        Invalidate();
    }

    protected override void OnIsEnabledChanged(bool enabled)
    {
        base.OnIsEnabledChanged(enabled);
        OnVisualStateChanged();
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled && AllowFocusOnInteraction)
        {
            // Also acquire focus if we are hit-test visible, enabled, and IsTabStop
            if (IsHitTestVisible && IsTabStop)
            {
                InputSystem.SetFocus(this);
            }
        }
        base.OnPointerPressed(e);
    }

    public override void OnRender(DrawingContext context)
    {
        base.OnRender(context);

        // Draw dynamic high-contrast keyboard focus visual borders 2px outside the control
        if (IsKeyboardFocusVisualVisible)
        {
            var accentColor = ThemeManager.GetBrush("SystemAccentColor");
            var margin = FocusVisualMargin;
            context.DrawRectangle(
                null,
                new Pen(accentColor, 1.5f),
                new Rect(
                    -2f - margin.Left,
                    -2f - margin.Top,
                    Size.X + 4f + margin.Horizontal,
                    Size.Y + 4f + margin.Vertical));
        }
    }
}
