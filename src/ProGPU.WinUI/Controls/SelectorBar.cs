using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Numerics;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using ProGPU.Scene;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Controls;

public sealed class SelectorBarSelectionChangedEventArgs : EventArgs
{
}

/// <summary>Represents one option in a <see cref="SelectorBar"/>.</summary>
public class SelectorBarItem : ItemContainer
{
    private readonly StackPanel _contentPanel;
    private readonly TextBlock _textBlock;
    private bool _pointerOver;

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(SelectorBarItem),
            new PropertyMetadata(string.Empty, OnPresentationPropertyChanged)
            {
                AffectsMeasure = true,
                AffectsRender = true
            });

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(
            nameof(Icon),
            typeof(IconElement),
            typeof(SelectorBarItem),
            new PropertyMetadata(null, OnPresentationPropertyChanged)
            {
                AffectsMeasure = true,
                AffectsRender = true
            });

    public SelectorBarItem()
    {
        CanUserSelect = ItemContainerUserSelectMode.UserCanSelect;
        CanUserInvoke = ItemContainerUserInvokeMode.UserCanInvoke;
        CornerRadius = 16;
        Padding = new Thickness(18, 6);
        MinHeight = 32;
        Background = new ThemeResourceBrush("Transparent");

        _contentPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _textBlock = new TextBlock
        {
            Text = string.Empty,
            FontSize = 14,
            Foreground = new ThemeResourceBrush("TextPrimary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Content = _contentPanel;
        RebuildPresentation();
    }

    public string Text
    {
        get => GetValue(TextProperty) as string ?? string.Empty;
        set => SetValue(TextProperty, value ?? string.Empty);
    }

    public IconElement? Icon
    {
        get => GetValue(IconProperty) as IconElement;
        set => SetValue(IconProperty, value);
    }

    internal event EventHandler? Invoked;
    internal event EventHandler? SelectionStateChanged;

    protected override void OnIsSelectedChanged(bool oldValue, bool newValue)
    {
        base.OnIsSelectedChanged(oldValue, newValue);
        UpdateVisualState();
        SelectionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPropertyChanged(
        DependencyProperty dependencyProperty,
        object? oldValue,
        object? newValue)
    {
        base.OnPropertyChanged(dependencyProperty, oldValue, newValue);
        if (_textBlock is null)
        {
            return;
        }
        if (dependencyProperty == FontProperty)
        {
            _textBlock.Font = Font;
        }
        else if (dependencyProperty == FontSizeProperty)
        {
            _textBlock.FontSize = (float)FontSize;
        }
        else if (dependencyProperty == ForegroundProperty)
        {
            _textBlock.Foreground = Foreground;
        }
    }

    public override void OnTapped(TappedRoutedEventArgs args)
    {
        if (IsEnabled &&
            CanUserSelect != ItemContainerUserSelectMode.UserCannotSelect)
        {
            Invoked?.Invoke(this, EventArgs.Empty);
            args.Handled = true;
        }
        base.OnTapped(args);
    }

    public override void OnPointerEntered(PointerRoutedEventArgs args)
    {
        _pointerOver = true;
        UpdateVisualState();
        base.OnPointerEntered(args);
    }

    public override void OnPointerExited(PointerRoutedEventArgs args)
    {
        _pointerOver = false;
        UpdateVisualState();
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

    private void RebuildPresentation()
    {
        _contentPanel.ClearChildren();
        if (Icon is not null)
        {
            _contentPanel.AddChild(Icon);
        }
        _textBlock.Text = Text;
        if (Text.Length > 0)
        {
            _contentPanel.AddChild(_textBlock);
        }
        InvalidateMeasure();
        Invalidate();
    }

    private void UpdateVisualState()
    {
        Background = new ThemeResourceBrush(
            IsSelected
                ? "SelectorBarItemBackgroundSelected"
                : _pointerOver
                    ? "SelectorBarItemBackgroundPointerOver"
                    : "Transparent");
    }

    private static void OnPresentationPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((SelectorBarItem)dependencyObject).RebuildPresentation();
    }
}

/// <summary>
/// Displays a small group of options with exactly one selected option.
/// </summary>
[ContentProperty(Name = nameof(Items))]
public class SelectorBar : Control
{
    private readonly ObservableCollection<SelectorBarItem> _items = new();
    private bool _selectionUpdating;

    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(
            nameof(Items),
            typeof(IList<SelectorBarItem>),
            typeof(SelectorBar),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(
            nameof(SelectedItem),
            typeof(SelectorBarItem),
            typeof(SelectorBar),
            new PropertyMetadata(null, OnSelectedItemPropertyChanged)
            {
                AffectsRender = true
            });

    public SelectorBar()
    {
        SetValue(ItemsProperty, _items);
        _items.CollectionChanged += OnItemsChanged;
        MinHeight = 40;
        Padding = new Thickness(4);
        CornerRadius = 20;
        Background = new ThemeResourceBrush("ControlBackground");
    }

    public IList<SelectorBarItem> Items =>
        (IList<SelectorBarItem>?)GetValue(ItemsProperty) ?? _items;

    public SelectorBarItem? SelectedItem
    {
        get => GetValue(SelectedItemProperty) as SelectorBarItem;
        set
        {
            if (value is not null && !_items.Contains(value))
            {
                throw new ArgumentException(
                    "SelectedItem must belong to this SelectorBar.",
                    nameof(value));
            }
            SetValue(SelectedItemProperty, value);
        }
    }

    public event Windows.Foundation.TypedEventHandler<
        SelectorBar,
        SelectorBarSelectionChangedEventArgs>?
        SelectionChanged;

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        Vector2 desired = new(Padding.Horizontal, Padding.Vertical);
        float maxHeight = 0;
        foreach (SelectorBarItem item in _items)
        {
            item.Measure(new Vector2(float.PositiveInfinity, availableSize.Y));
            desired.X += item.DesiredSize.X;
            maxHeight = Math.Max(maxHeight, item.DesiredSize.Y);
        }
        desired.Y += maxHeight;
        return desired;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        float contentWidth = Math.Max(0, arrangeRect.Width - Padding.Horizontal);
        float desiredWidth = 0;
        foreach (SelectorBarItem item in _items)
        {
            desiredWidth += item.DesiredSize.X;
        }
        float scale = desiredWidth > contentWidth && desiredWidth > 0
            ? contentWidth / desiredWidth
            : 1;
        float extraPerItem = _items.Count > 0 && desiredWidth < contentWidth
            ? (contentWidth - desiredWidth) / _items.Count
            : 0;
        float x = arrangeRect.X + Padding.Left;
        float y = arrangeRect.Y + Padding.Top;
        float height = Math.Max(0, arrangeRect.Height - Padding.Vertical);
        foreach (SelectorBarItem item in _items)
        {
            float width = item.DesiredSize.X * scale + extraPerItem;
            item.Arrange(new Rect(x, y, width, height));
            x += width;
        }
    }

    public override void OnKeyDown(KeyRoutedEventArgs args)
    {
        if (_items.Count > 0 &&
            args.Key is Silk.NET.Input.Key.Left or Silk.NET.Input.Key.Right)
        {
            int current = SelectedItem is null ? -1 : _items.IndexOf(SelectedItem);
            int direction = args.Key == Silk.NET.Input.Key.Left ? -1 : 1;
            for (int offset = 1; offset <= _items.Count; offset++)
            {
                int candidate = (current + direction * offset + _items.Count) % _items.Count;
                if (_items[candidate].IsEnabled)
                {
                    SelectedItem = _items[candidate];
                    args.Handled = true;
                    return;
                }
            }
        }
        base.OnKeyDown(args);
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

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.OldItems is not null)
        {
            foreach (SelectorBarItem item in args.OldItems)
            {
                item.Invoked -= OnItemInvoked;
                item.SelectionStateChanged -= OnItemSelectionStateChanged;
                item.IsSelected = false;
                RemoveChild(item);
                if (ReferenceEquals(SelectedItem, item))
                {
                    SelectedItem = null;
                }
            }
        }
        if (args.NewItems is not null)
        {
            foreach (SelectorBarItem item in args.NewItems)
            {
                item.Invoked += OnItemInvoked;
                item.SelectionStateChanged += OnItemSelectionStateChanged;
                AddChild(item);
                if (item.IsSelected)
                {
                    SelectedItem = item;
                }
            }
        }
        InvalidateMeasure();
        InvalidateArrange();
        Invalidate();
    }

    private void OnItemInvoked(object? sender, EventArgs args)
    {
        if (sender is SelectorBarItem item && item.IsEnabled)
        {
            SelectedItem = item;
        }
    }

    private void OnItemSelectionStateChanged(object? sender, EventArgs args)
    {
        if (_selectionUpdating || sender is not SelectorBarItem item)
        {
            return;
        }
        if (item.IsSelected)
        {
            SelectedItem = item;
        }
        else if (ReferenceEquals(SelectedItem, item))
        {
            SelectedItem = null;
        }
    }

    private void SynchronizeSelection(SelectorBarItem? selectedItem)
    {
        if (_selectionUpdating)
        {
            return;
        }
        _selectionUpdating = true;
        try
        {
            foreach (SelectorBarItem item in _items)
            {
                item.IsSelected = ReferenceEquals(item, selectedItem);
            }
        }
        finally
        {
            _selectionUpdating = false;
        }
        SelectionChanged?.Invoke(this, new SelectorBarSelectionChangedEventArgs());
        Invalidate();
    }

    private static void OnSelectedItemPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((SelectorBar)dependencyObject).SynchronizeSelection(
            args.NewValue as SelectorBarItem);
    }
}
