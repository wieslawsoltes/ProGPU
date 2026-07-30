using Microsoft.UI.Xaml.Controls.Primitives;
using ProGPU.Scene;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Controls;

public enum CommandBarLabelPosition
{
    Default = 0,
    Collapsed = 1
}

public interface ICommandBarElement
{
    bool IsCompact { get; set; }
    bool IsInOverflow { get; }
    int DynamicOverflowOrder { get; set; }
}

/// <summary>A command button with a label and an icon.</summary>
public class AppBarButton : Button, ICommandBarElement
{
    private readonly StackPanel _presentation;
    private readonly TextBlock _labelText;
    private CommandBarDefaultLabelPosition _owningCommandBarLabelPosition =
        CommandBarDefaultLabelPosition.Bottom;

    public AppBarButtonTemplateSettings TemplateSettings { get; } = new();

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(IconElement), typeof(AppBarButton),
        new PropertyMetadata(null, OnPresentationPropertyChanged));

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(AppBarButton),
        new PropertyMetadata(false, OnPresentationPropertyChanged));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(AppBarButton),
        new PropertyMetadata(string.Empty, OnPresentationPropertyChanged));

    public static readonly DependencyProperty LabelPositionProperty = DependencyProperty.Register(
        nameof(LabelPosition), typeof(CommandBarLabelPosition), typeof(AppBarButton),
        new PropertyMetadata(CommandBarLabelPosition.Default, OnPresentationPropertyChanged));

    public static readonly DependencyProperty IsInOverflowProperty = DependencyProperty.Register(
        nameof(IsInOverflow), typeof(bool), typeof(AppBarButton), new PropertyMetadata(false));

    public static readonly DependencyProperty DynamicOverflowOrderProperty = DependencyProperty.Register(
        nameof(DynamicOverflowOrder), typeof(int), typeof(AppBarButton), new PropertyMetadata(0));

    public AppBarButton()
    {
        _presentation = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _labelText = new TextBlock
        {
            FontSize = 13,
            Foreground = new ThemeResourceBrush("TextPrimary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Content = _presentation;
        RebuildPresentation();
    }

    public IconElement? Icon
    {
        get => GetValue(IconProperty) as IconElement;
        set => SetValue(IconProperty, value);
    }

    public bool IsCompact
    {
        get => (bool)(GetValue(IsCompactProperty) ?? false);
        set => SetValue(IsCompactProperty, value);
    }

    public string Label
    {
        get => (string?)GetValue(LabelProperty) ?? string.Empty;
        set => SetValue(LabelProperty, value);
    }

    public CommandBarLabelPosition LabelPosition
    {
        get => (CommandBarLabelPosition)(GetValue(LabelPositionProperty) ?? CommandBarLabelPosition.Default);
        set => SetValue(LabelPositionProperty, value);
    }

    internal CommandBarDefaultLabelPosition OwningCommandBarLabelPosition
    {
        get => _owningCommandBarLabelPosition;
        set
        {
            if (_owningCommandBarLabelPosition == value)
            {
                return;
            }
            _owningCommandBarLabelPosition = value;
            RebuildPresentation();
        }
    }

    internal bool IsLabelVisible =>
        LabelPosition != CommandBarLabelPosition.Collapsed &&
        _owningCommandBarLabelPosition != CommandBarDefaultLabelPosition.Collapsed &&
        Label.Length > 0;

    public bool IsInOverflow => (bool)(GetValue(IsInOverflowProperty) ?? false);

    public int DynamicOverflowOrder
    {
        get => (int)(GetValue(DynamicOverflowOrderProperty) ?? 0);
        set => SetValue(DynamicOverflowOrderProperty, value);
    }

    protected override void OnPropertyChanged(
        DependencyProperty dependencyProperty,
        object? oldValue,
        object? newValue)
    {
        base.OnPropertyChanged(dependencyProperty, oldValue, newValue);
        if (_labelText is null)
        {
            return;
        }
        if (dependencyProperty == FontProperty)
        {
            _labelText.Font = Font;
        }
        else if (dependencyProperty == ForegroundProperty)
        {
            _labelText.Foreground = Foreground;
        }
    }

    private void RebuildPresentation()
    {
        _presentation.ClearChildren();
        if (Icon is not null)
        {
            _presentation.AddChild(Icon);
        }
        _labelText.Text = Label;
        if (IsLabelVisible)
        {
            _presentation.AddChild(_labelText);
        }
        Padding = IsCompact
            ? new Thickness(7, 5)
            : new Thickness(10, 6);
        InvalidateMeasure();
        Invalidate();
    }

    private static void OnPresentationPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((AppBarButton)dependencyObject).RebuildPresentation();
    }
}

/// <summary>A toggleable app-bar command with a label and an icon.</summary>
public class AppBarToggleButton : ToggleButton, ICommandBarElement
{
    private readonly StackPanel _presentation;
    private readonly TextBlock _labelText;
    private CommandBarDefaultLabelPosition _owningCommandBarLabelPosition =
        CommandBarDefaultLabelPosition.Bottom;

    public AppBarToggleButtonTemplateSettings TemplateSettings { get; } = new();

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(IconElement), typeof(AppBarToggleButton),
        new PropertyMetadata(null, OnPresentationPropertyChanged));

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(AppBarToggleButton),
        new PropertyMetadata(false, OnPresentationPropertyChanged));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(AppBarToggleButton),
        new PropertyMetadata(string.Empty, OnPresentationPropertyChanged));

    public static readonly DependencyProperty LabelPositionProperty = DependencyProperty.Register(
        nameof(LabelPosition), typeof(CommandBarLabelPosition), typeof(AppBarToggleButton),
        new PropertyMetadata(CommandBarLabelPosition.Default, OnPresentationPropertyChanged));

    public static readonly DependencyProperty IsInOverflowProperty = DependencyProperty.Register(
        nameof(IsInOverflow), typeof(bool), typeof(AppBarToggleButton), new PropertyMetadata(false));

    public static readonly DependencyProperty DynamicOverflowOrderProperty = DependencyProperty.Register(
        nameof(DynamicOverflowOrder), typeof(int), typeof(AppBarToggleButton), new PropertyMetadata(0));

    public AppBarToggleButton()
    {
        _presentation = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _labelText = new TextBlock
        {
            FontSize = 13,
            Foreground = new ThemeResourceBrush("TextPrimary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Content = _presentation;
        RebuildPresentation();
    }

    public IconElement? Icon
    {
        get => GetValue(IconProperty) as IconElement;
        set => SetValue(IconProperty, value);
    }

    public bool IsCompact
    {
        get => (bool)(GetValue(IsCompactProperty) ?? false);
        set => SetValue(IsCompactProperty, value);
    }

    public string Label
    {
        get => (string?)GetValue(LabelProperty) ?? string.Empty;
        set => SetValue(LabelProperty, value);
    }

    public CommandBarLabelPosition LabelPosition
    {
        get => (CommandBarLabelPosition)(
            GetValue(LabelPositionProperty) ?? CommandBarLabelPosition.Default);
        set => SetValue(LabelPositionProperty, value);
    }

    internal CommandBarDefaultLabelPosition OwningCommandBarLabelPosition
    {
        get => _owningCommandBarLabelPosition;
        set
        {
            if (_owningCommandBarLabelPosition == value)
            {
                return;
            }
            _owningCommandBarLabelPosition = value;
            RebuildPresentation();
        }
    }

    internal bool IsLabelVisible =>
        LabelPosition != CommandBarLabelPosition.Collapsed &&
        _owningCommandBarLabelPosition != CommandBarDefaultLabelPosition.Collapsed &&
        Label.Length > 0;

    public bool IsInOverflow => (bool)(GetValue(IsInOverflowProperty) ?? false);

    public int DynamicOverflowOrder
    {
        get => (int)(GetValue(DynamicOverflowOrderProperty) ?? 0);
        set => SetValue(DynamicOverflowOrderProperty, value);
    }

    protected override void OnPropertyChanged(
        DependencyProperty dependencyProperty,
        object? oldValue,
        object? newValue)
    {
        base.OnPropertyChanged(dependencyProperty, oldValue, newValue);
        if (_labelText is null)
        {
            return;
        }
        if (dependencyProperty == FontProperty)
        {
            _labelText.Font = Font;
        }
        else if (dependencyProperty == ForegroundProperty)
        {
            _labelText.Foreground = Foreground;
        }
    }

    private void RebuildPresentation()
    {
        _presentation.ClearChildren();
        if (Icon is not null)
        {
            _presentation.AddChild(Icon);
        }
        _labelText.Text = Label;
        if (IsLabelVisible)
        {
            _presentation.AddChild(_labelText);
        }
        Padding = IsCompact
            ? new Thickness(7, 5)
            : new Thickness(10, 6);
        InvalidateMeasure();
        Invalidate();
    }

    private static void OnPresentationPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((AppBarToggleButton)dependencyObject).RebuildPresentation();
    }
}

/// <summary>Separates groups of app-bar commands.</summary>
public sealed class AppBarSeparator : Control, ICommandBarElement
{
    private readonly Brush _separatorBrush = new ThemeResourceBrush("ControlBorder");

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(AppBarSeparator), new PropertyMetadata(false));

    public static readonly DependencyProperty IsInOverflowProperty = DependencyProperty.Register(
        nameof(IsInOverflow), typeof(bool), typeof(AppBarSeparator), new PropertyMetadata(false));

    public static readonly DependencyProperty DynamicOverflowOrderProperty = DependencyProperty.Register(
        nameof(DynamicOverflowOrder), typeof(int), typeof(AppBarSeparator), new PropertyMetadata(0));

    public bool IsCompact { get => (bool)(GetValue(IsCompactProperty) ?? false); set => SetValue(IsCompactProperty, value); }
    public bool IsInOverflow => (bool)(GetValue(IsInOverflowProperty) ?? false);
    public int DynamicOverflowOrder { get => (int)(GetValue(DynamicOverflowOrderProperty) ?? 0); set => SetValue(DynamicOverflowOrderProperty, value); }

    public AppBarSeparator()
    {
        Width = 9;
        MinHeight = 28;
    }

    public override void OnRender(DrawingContext context)
    {
        context.DrawLine(
            new Pen(_separatorBrush, 1),
            new global::System.Numerics.Vector2(Size.X * 0.5f, 4),
            new global::System.Numerics.Vector2(Size.X * 0.5f, Math.Max(4, Size.Y - 4)));
        base.OnRender(context);
    }
}
