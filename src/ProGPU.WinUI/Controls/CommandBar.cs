using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Controls;

public enum CommandBarDefaultLabelPosition
{
    Bottom = 0,
    Right = 1,
    Collapsed = 2
}

public enum CommandBarOverflowButtonVisibility
{
    Auto = 0,
    Visible = 1,
    Collapsed = 2
}

[ContentProperty(Name = nameof(PrimaryCommands))]
public class CommandBar : AppBar
{
    private readonly StackPanel _primaryHost;
    private readonly AppBarButton _overflowButton;
    private readonly CommandBarFlyout _overflowFlyout;

    public static readonly DependencyProperty CommandBarOverflowPresenterStyleProperty = DependencyProperty.Register(
        nameof(CommandBarOverflowPresenterStyle), typeof(Style), typeof(CommandBar), new PropertyMetadata(null));

    public static readonly DependencyProperty DefaultLabelPositionProperty = DependencyProperty.Register(
        nameof(DefaultLabelPosition), typeof(CommandBarDefaultLabelPosition), typeof(CommandBar),
        new PropertyMetadata(
            CommandBarDefaultLabelPosition.Bottom,
            OnPresentationPropertyChanged));

    public static readonly DependencyProperty OverflowButtonVisibilityProperty = DependencyProperty.Register(
        nameof(OverflowButtonVisibility), typeof(CommandBarOverflowButtonVisibility), typeof(CommandBar),
        new PropertyMetadata(
            CommandBarOverflowButtonVisibility.Auto,
            OnPresentationPropertyChanged));

    public static readonly DependencyProperty IsDynamicOverflowEnabledProperty = DependencyProperty.Register(
        nameof(IsDynamicOverflowEnabled), typeof(bool), typeof(CommandBar), new PropertyMetadata(true));

    public CommandBar()
    {
        PrimaryCommands = new ObservableCollection<ICommandBarElement>();
        SecondaryCommands = new ObservableCollection<ICommandBarElement>();
        PrimaryCommands.CollectionChanged += OnCommandsChanged;
        SecondaryCommands.CollectionChanged += OnCommandsChanged;

        _primaryHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        _overflowFlyout = new CommandBarFlyout();
        _overflowButton = new AppBarButton
        {
            Label = "•••",
            LabelPosition = CommandBarLabelPosition.Default,
            IsCompact = true,
            Flyout = _overflowFlyout
        };
        Background = new ThemeResourceBrush("Transparent");
        BorderBrush = new ThemeResourceBrush("Transparent");
        Padding = new Thickness(0);
        Content = _primaryHost;
        RebuildPresentation();
    }

    public ObservableCollection<ICommandBarElement> PrimaryCommands { get; }
    public ObservableCollection<ICommandBarElement> SecondaryCommands { get; }
    public CommandBarTemplateSettings CommandBarTemplateSettings { get; } = new();

    public Style? CommandBarOverflowPresenterStyle
    {
        get => GetValue(CommandBarOverflowPresenterStyleProperty) as Style;
        set => SetValue(CommandBarOverflowPresenterStyleProperty, value);
    }

    public CommandBarDefaultLabelPosition DefaultLabelPosition
    {
        get => (CommandBarDefaultLabelPosition)(GetValue(DefaultLabelPositionProperty) ?? CommandBarDefaultLabelPosition.Bottom);
        set => SetValue(DefaultLabelPositionProperty, value);
    }

    public CommandBarOverflowButtonVisibility OverflowButtonVisibility
    {
        get => (CommandBarOverflowButtonVisibility)(GetValue(OverflowButtonVisibilityProperty) ?? CommandBarOverflowButtonVisibility.Auto);
        set => SetValue(OverflowButtonVisibilityProperty, value);
    }

    public bool IsDynamicOverflowEnabled
    {
        get => (bool)(GetValue(IsDynamicOverflowEnabledProperty) ?? true);
        set => SetValue(IsDynamicOverflowEnabledProperty, value);
    }

    private void OnCommandsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        RebuildPresentation();
    }

    private void RebuildPresentation()
    {
        _primaryHost.ClearChildren();
        foreach (ICommandBarElement command in PrimaryCommands)
        {
            ApplyLabelPosition(command);
            if (command is FrameworkElement element)
            {
                _primaryHost.AddChild(element);
            }
        }

        _overflowFlyout.Hide();
        _overflowFlyout.PrimaryCommands.Clear();
        _overflowFlyout.SecondaryCommands.Clear();
        foreach (ICommandBarElement command in SecondaryCommands)
        {
            _overflowFlyout.SecondaryCommands.Add(command);
        }

        bool showOverflow = OverflowButtonVisibility switch
        {
            CommandBarOverflowButtonVisibility.Visible => true,
            CommandBarOverflowButtonVisibility.Collapsed => false,
            _ => SecondaryCommands.Count > 0
        };
        if (showOverflow)
        {
            _primaryHost.AddChild(_overflowButton);
        }
        InvalidateMeasure();
        InvalidateArrange();
        Invalidate();
    }

    private void ApplyLabelPosition(ICommandBarElement command)
    {
        if (command is AppBarButton button)
        {
            button.OwningCommandBarLabelPosition = DefaultLabelPosition;
        }
        else if (command is AppBarToggleButton toggleButton)
        {
            toggleButton.OwningCommandBarLabelPosition = DefaultLabelPosition;
        }
    }

    private static void OnPresentationPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((CommandBar)dependencyObject).RebuildPresentation();
    }
}

public class CommandBarOverflowPresenter : ItemsControl
{
}
