using System;
using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using ProGPU.Scene;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Controls;

[ContentProperty(Name = nameof(Items))]
public class MenuFlyout : FlyoutBase
{
    public static readonly DependencyProperty MenuFlyoutPresenterStyleProperty = DependencyProperty.Register(
        nameof(MenuFlyoutPresenterStyle), typeof(Style), typeof(MenuFlyout), new PropertyMetadata(null));

    public ObservableCollection<MenuFlyoutItemBase> Items { get; } = new();

    public Style? MenuFlyoutPresenterStyle
    {
        get => GetValue(MenuFlyoutPresenterStyleProperty) as Style;
        set => SetValue(MenuFlyoutPresenterStyleProperty, value);
    }

    protected override Control CreatePresenter()
    {
        var presenter = new MenuFlyoutPresenter
        {
            Style = MenuFlyoutPresenterStyle,
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = 8,
            Padding = new Thickness(5),
            MinWidth = 210
        };
        for (var index = 0; index < Items.Count; index++)
        {
            MenuFlyoutItemBase item = Items[index];
            item.OwningFlyout = this;
            if (item.Parent is Panel oldPanel)
            {
                oldPanel.Children.Remove(item);
            }
            presenter.Items.Add(item);
        }
        for (var index = 0; index < Items.Count; index++)
        {
            presenter.ItemsHost?.Children.Add(Items[index]);
        }
        return presenter;
    }

    internal void SelectRadioItem(RadioMenuFlyoutItem selected)
    {
        foreach (MenuFlyoutItemBase item in Items)
        {
            if (item is RadioMenuFlyoutItem radio &&
                !ReferenceEquals(radio, selected) &&
                string.Equals(radio.GroupName, selected.GroupName, StringComparison.Ordinal))
            {
                radio.IsChecked = false;
            }
        }
        selected.IsChecked = true;
    }
}

public class MenuFlyoutItemBase : Control
{
    private bool _pointerOver;

    protected MenuFlyoutItemBase()
    {
        Background = new ThemeResourceBrush("Transparent");
        Foreground = new ThemeResourceBrush("TextPrimary");
        CornerRadius = 5;
        MinHeight = 30;
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    internal MenuFlyout? OwningFlyout { get; set; }

    public override void OnPointerEntered(PointerRoutedEventArgs args)
    {
        _pointerOver = true;
        UpdateBackground();
        base.OnPointerEntered(args);
    }

    public override void OnPointerExited(PointerRoutedEventArgs args)
    {
        _pointerOver = false;
        UpdateBackground();
        base.OnPointerExited(args);
    }

    public override void OnRender(DrawingContext context)
    {
        context.DrawRoundedRectangle(
            Background,
            BorderBrush is null || BorderThickness.Left <= 0
                ? null
                : new Pen(BorderBrush, BorderThickness.Left),
            new Rect(Vector2.Zero, Size),
            (float)CornerRadius.TopLeft);
        base.OnRender(context);
    }

    private void UpdateBackground()
    {
        Background = new ThemeResourceBrush(
            _pointerOver ? "ControlBackgroundHover" : "Transparent");
    }
}

[ContentProperty(Name = nameof(Text))]
public class MenuFlyoutItem : MenuFlyoutItemBase
{
    private readonly Grid _layout;
    private readonly TextBlock _selectionText;
    private readonly TextBlock _labelText;
    private readonly TextBlock _acceleratorText;

    public static readonly DependencyProperty TextProperty =
        Register<string?>(nameof(Text), null, OnPresentationPropertyChanged);
    public static readonly DependencyProperty CommandProperty = Register<ICommand?>(nameof(Command), null);
    public static readonly DependencyProperty CommandParameterProperty = Register<object?>(nameof(CommandParameter), null);
    public static readonly DependencyProperty IconProperty =
        Register<IconElement?>(nameof(Icon), null, OnPresentationPropertyChanged);
    public static readonly DependencyProperty KeyboardAcceleratorTextOverrideProperty =
        Register<string?>(
            nameof(KeyboardAcceleratorTextOverride),
            null,
            OnPresentationPropertyChanged);

    public MenuFlyoutItem()
    {
        Padding = new Thickness(8, 5);
        _layout = new Grid();
        _layout.ColumnDefinitions.Add(new GridLength(22, GridUnitType.Absolute));
        _layout.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
        _layout.ColumnDefinitions.Add(new GridLength(70, GridUnitType.Absolute));
        _selectionText = new TextBlock
        {
            FontSize = 12,
            Foreground = new ThemeResourceBrush("TextPrimary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _labelText = new TextBlock
        {
            FontSize = 13,
            Foreground = new ThemeResourceBrush("TextPrimary"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        _acceleratorText = new TextBlock
        {
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        AddChild(_layout);
        RebuildPresentation();
    }

    public string? Text { get => GetValue(TextProperty) as string; set => SetValue(TextProperty, value); }
    public ICommand? Command { get => GetValue(CommandProperty) as ICommand; set => SetValue(CommandProperty, value); }
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }
    public IconElement? Icon { get => GetValue(IconProperty) as IconElement; set => SetValue(IconProperty, value); }
    public string? KeyboardAcceleratorTextOverride { get => GetValue(KeyboardAcceleratorTextOverrideProperty) as string; set => SetValue(KeyboardAcceleratorTextOverrideProperty, value); }
    public MenuFlyoutItemTemplateSettings TemplateSettings { get; } = new();

    public event RoutedEventHandler? Click;

    public override void OnPointerReleased(PointerRoutedEventArgs args)
    {
        if (IsEnabled && IsPointerPressed && IsPointerOver) Invoke();
        base.OnPointerReleased(args);
    }

    protected virtual void Invoke()
    {
        ExecuteAndDismiss();
    }

    protected void ExecuteAndDismiss()
    {
        if (Command?.CanExecute(CommandParameter) == true) Command.Execute(CommandParameter);
        Click?.Invoke(this, new RoutedEventArgs { OriginalSource = this });
        OwningFlyout?.Hide();
    }

    protected virtual bool IsSelectionVisible => false;

    protected void RebuildPresentation()
    {
        _layout.ClearChildren();
        _selectionText.Text = IsSelectionVisible ? "✓" : string.Empty;
        if (Icon is not null)
        {
            _layout.AddChild(Icon);
            Grid.SetColumn(Icon, 0);
        }
        else
        {
            _layout.AddChild(_selectionText);
            Grid.SetColumn(_selectionText, 0);
        }
        _labelText.Text = Text ?? string.Empty;
        _layout.AddChild(_labelText);
        Grid.SetColumn(_labelText, 1);
        _acceleratorText.Text = KeyboardAcceleratorTextOverride ?? string.Empty;
        _layout.AddChild(_acceleratorText);
        Grid.SetColumn(_acceleratorText, 2);
        InvalidateMeasure();
        Invalidate();
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        Vector2 contentAvailable = new(
            float.IsInfinity(availableSize.X)
                ? float.PositiveInfinity
                : Math.Max(0, availableSize.X - Padding.Horizontal),
            float.IsInfinity(availableSize.Y)
                ? float.PositiveInfinity
                : Math.Max(0, availableSize.Y - Padding.Vertical));
        _layout.Measure(contentAvailable);
        return new Vector2(
            Math.Max(MinWidth, _layout.DesiredSize.X + Padding.Horizontal),
            Math.Max(MinHeight, _layout.DesiredSize.Y + Padding.Vertical));
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        _layout.Arrange(new Rect(
            arrangeRect.X + Padding.Left,
            arrangeRect.Y + Padding.Top,
            Math.Max(0, arrangeRect.Width - Padding.Horizontal),
            Math.Max(0, arrangeRect.Height - Padding.Vertical)));
    }

    private static void OnPresentationPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is MenuFlyoutItem item && item._layout is not null)
        {
            item.RebuildPresentation();
        }
    }

    private static DependencyProperty Register<T>(
        string name,
        T defaultValue,
        PropertyChangedCallback? callback = null) =>
        DependencyProperty.Register(name, typeof(T), typeof(MenuFlyoutItem),
            new PropertyMetadata(defaultValue, callback)
            {
                AffectsMeasure = true,
                AffectsRender = true
            });
}

public class ToggleMenuFlyoutItem : MenuFlyoutItem
{
    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
        nameof(IsChecked), typeof(bool), typeof(ToggleMenuFlyoutItem),
        new PropertyMetadata(false, OnIsCheckedPropertyChanged)
        {
            AffectsRender = true
        });

    public bool IsChecked { get => (bool)(GetValue(IsCheckedProperty) ?? false); set => SetValue(IsCheckedProperty, value); }

    protected override bool IsSelectionVisible => IsChecked;

    protected override void Invoke()
    {
        IsChecked = !IsChecked;
        base.Invoke();
    }

    private static void OnIsCheckedPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((ToggleMenuFlyoutItem)dependencyObject).RebuildPresentation();
    }
}

public class RadioMenuFlyoutItem : ToggleMenuFlyoutItem
{
    public static readonly DependencyProperty GroupNameProperty =
        DependencyProperty.Register(
            nameof(GroupName),
            typeof(string),
            typeof(RadioMenuFlyoutItem),
            new PropertyMetadata(string.Empty));

    public string GroupName
    {
        get => GetValue(GroupNameProperty) as string ?? string.Empty;
        set => SetValue(GroupNameProperty, value ?? string.Empty);
    }

    protected override void Invoke()
    {
        OwningFlyout?.SelectRadioItem(this);
        if (OwningFlyout is null)
        {
            IsChecked = true;
        }
        ExecuteAndDismiss();
    }
}

public class MenuFlyoutSeparator : MenuFlyoutItemBase
{
    private readonly Brush _separatorBrush = new ThemeResourceBrush("ControlBorder");

    public MenuFlyoutSeparator()
    {
        Height = 9;
        MinHeight = 9;
        IsEnabled = false;
    }

    public override void OnRender(DrawingContext context)
    {
        context.DrawLine(
            new Pen(_separatorBrush, 1),
            new Vector2(8, Size.Y * 0.5f),
            new Vector2(Math.Max(8, Size.X - 8), Size.Y * 0.5f));
        base.OnRender(context);
    }
}

[ContentProperty(Name = nameof(Items))]
public sealed class MenuFlyoutSubItem : MenuFlyoutItemBase
{
    private readonly Grid _layout;
    private readonly TextBlock _labelText;
    private readonly TextBlock _chevronText;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MenuFlyoutSubItem), new PropertyMetadata(null));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(IconElement), typeof(MenuFlyoutSubItem), new PropertyMetadata(null));
    public static readonly DependencyProperty AreCheckStatesEnabledProperty =
        DependencyProperty.Register(
            nameof(AreCheckStatesEnabled),
            typeof(bool),
            typeof(MenuFlyoutSubItem),
            new PropertyMetadata(false));

    public MenuFlyoutSubItem()
    {
        Padding = new Thickness(12, 5);
        _layout = new Grid();
        _layout.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
        _layout.ColumnDefinitions.Add(new GridLength(22, GridUnitType.Absolute));
        _labelText = new TextBlock
        {
            FontSize = 13,
            Foreground = new ThemeResourceBrush("TextPrimary"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        _chevronText = new TextBlock
        {
            Text = "›",
            FontSize = 16,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        _layout.AddChild(_labelText);
        _layout.AddChild(_chevronText);
        Grid.SetColumn(_chevronText, 1);
        AddChild(_layout);
    }

    public ObservableCollection<MenuFlyoutItemBase> Items { get; } = new();
    public string? Text { get => GetValue(TextProperty) as string; set => SetValue(TextProperty, value); }
    public IconElement? Icon { get => GetValue(IconProperty) as IconElement; set => SetValue(IconProperty, value); }
    public bool AreCheckStatesEnabled
    {
        get => (bool)(GetValue(AreCheckStatesEnabledProperty) ?? false);
        set => SetValue(AreCheckStatesEnabledProperty, value);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs args)
    {
        if (IsEnabled && IsPointerPressed && IsPointerOver)
        {
            ShowSubMenu();
            args.Handled = true;
        }
        base.OnPointerReleased(args);
    }

    protected override void OnPropertyChanged(
        DependencyProperty dependencyProperty,
        object? oldValue,
        object? newValue)
    {
        base.OnPropertyChanged(dependencyProperty, oldValue, newValue);
        if (dependencyProperty == TextProperty && _labelText is not null)
        {
            _labelText.Text = Text ?? string.Empty;
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        _layout.Measure(new Vector2(
            float.IsInfinity(availableSize.X)
                ? float.PositiveInfinity
                : Math.Max(0, availableSize.X - Padding.Horizontal),
            float.IsInfinity(availableSize.Y)
                ? float.PositiveInfinity
                : Math.Max(0, availableSize.Y - Padding.Vertical)));
        return new Vector2(
            Math.Max(MinWidth, _layout.DesiredSize.X + Padding.Horizontal),
            Math.Max(MinHeight, _layout.DesiredSize.Y + Padding.Vertical));
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        _layout.Arrange(new Rect(
            arrangeRect.X + Padding.Left,
            arrangeRect.Y + Padding.Top,
            Math.Max(0, arrangeRect.Width - Padding.Horizontal),
            Math.Max(0, arrangeRect.Height - Padding.Vertical)));
    }

    private void ShowSubMenu()
    {
        var flyout = new MenuFlyout
        {
            Placement = FlyoutPlacementMode.RightEdgeAlignedTop
        };
        foreach (MenuFlyoutItemBase item in Items)
        {
            flyout.Items.Add(item);
        }
        flyout.ShowAt(this);
    }
}

public class MenuFlyoutPresenter : ItemsControl
{
    public static readonly DependencyProperty IsDefaultShadowEnabledProperty = DependencyProperty.Register(
        nameof(IsDefaultShadowEnabled), typeof(bool), typeof(MenuFlyoutPresenter), new PropertyMetadata(true));

    public MenuFlyoutPresenterTemplateSettings TemplateSettings { get; } = new();
    public bool IsDefaultShadowEnabled { get => (bool)(GetValue(IsDefaultShadowEnabledProperty) ?? true); set => SetValue(IsDefaultShadowEnabledProperty, value); }

    public override void OnRender(DrawingContext context)
    {
        context.DrawRoundedRectangle(
            Background,
            BorderBrush is null || BorderThickness.Left <= 0
                ? null
                : new Pen(BorderBrush, BorderThickness.Left),
            new Rect(Vector2.Zero, Size),
            (float)CornerRadius.TopLeft);
        base.OnRender(context);
    }
}
