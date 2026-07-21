using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Numerics;
using Silk.NET.Input;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using Windows.Devices.Input;

namespace Microsoft.UI.Xaml.Controls;

public class ComboBox : Control
{
    private bool _isDropDownOpen;
    private ComboBoxItem? _selectedItem;
    private string _placeholderText = "Select item...";
    private float _fontSize = 14f;
    private Border? _dropDownPopup;
    private uint _pendingTouchPointerId;
    public Border? DropDownPopup => _dropDownPopup;

    public ObservableCollection<ComboBoxItem> Items { get; }

    public bool IsDropDownOpen
    {
        get
        {
            // If the popup is closed from the outside, keep state synced
            if (_isDropDownOpen && _dropDownPopup != null && !PopupService.ActivePopups.Contains(_dropDownPopup))
            {
                _isDropDownOpen = false;
            }
            return _isDropDownOpen;
        }
        set
        {
            if (_isDropDownOpen != value)
            {
                if (value)
                {
                    DropDownOpening?.Invoke(this, EventArgs.Empty);
                }
                _isDropDownOpen = value;
                UpdatePopupState();
            }
        }
    }

    public ComboBoxItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem != value)
            {
                if (_selectedItem != null) _selectedItem.IsSelected = false;
                _selectedItem = value;
                if (_selectedItem != null) _selectedItem.IsSelected = true;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }
        }
    }

    public string PlaceholderText
    {
        get => _placeholderText;
        set { if (_placeholderText != value) { _placeholderText = value; Invalidate(); } }
    }

    protected override void OnPropertyChanged(Microsoft.UI.Xaml.DependencyProperty dp, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(dp, oldValue, newValue);
        if (dp == FontProperty)
        {
            Invalidate();
        }
    }

    public float FontSize
    {
        get => _fontSize;
        set { if (_fontSize != value) { _fontSize = value; Invalidate(); } }
    }

    public event EventHandler? SelectionChanged;
    public event EventHandler? DropDownOpening;

    public ComboBox()
    {
        Items = new ObservableCollection<ComboBoxItem>();
        Items.CollectionChanged += OnItemsChanged;
        CornerRadius = 4f;
        Padding = new Thickness(10, 6, 32, 6); // Extra right padding for arrow
        HeightConstraint = 32f;
        WidthConstraint = 180f;

        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            SetDefaultStyle(defaultStyle);
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (ComboBoxItem item in e.NewItems)
            {
                item.Selected += OnItemSelected;
            }
        }
        if (e.OldItems != null)
        {
            foreach (ComboBoxItem item in e.OldItems)
            {
                item.Selected -= OnItemSelected;
            }
        }
        if (IsDropDownOpen)
        {
            UpdatePopupState();
        }
    }

    private void OnItemSelected(object? sender, EventArgs e)
    {
        if (sender is ComboBoxItem item)
        {
            SelectedItem = item;
            IsDropDownOpen = false;
        }
    }

    private Vector2 GetAbsolutePosition()
    {
        return Vector2.Transform(Vector2.Zero, GetGlobalTransformMatrix());
    }

    private Rect LogicalToPhysical(Rect rect) =>
        FlowDirection == FlowDirection.RightToLeft
            ? new Rect(Size.X - rect.Right, rect.Y, rect.Width, rect.Height)
            : rect;

    private ProGPU.Text.TextShapingOptions GetTextShapingOptions() =>
        ProGPU.Text.TextShapingOptions.Default.WithDirection(
            FlowDirection == FlowDirection.RightToLeft
                ? ProGPU.Text.Shaping.ShapingDirection.RightToLeft
                : ProGPU.Text.Shaping.ShapingDirection.LeftToRight);

    private void UpdatePopupState()
    {
        if (_isDropDownOpen)
        {
            if (_dropDownPopup == null)
            {
                var stack = new StackPanel 
                { 
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(2f, 2f, 14f, 2f)
                };
                foreach (var item in Items)
                {
                    stack.AddChild(item);
                }

                var scrollViewer = new ScrollViewer
                {
                    Content = stack,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };

                _dropDownPopup = new Border
                {
                    Background = new ThemeResourceBrush("CardBackground"),
                    BorderBrush = new ThemeResourceBrush("ControlBorder"),
                    BorderThickness = new Thickness(1f),
                    CornerRadius = 4f,
                    Child = scrollViewer
                };
            }
            else
            {
                var scrollViewer = (ScrollViewer)_dropDownPopup.Child!;
                var stack = (StackPanel)scrollViewer.Content!;
                stack.ClearChildren();
                foreach (var item in Items)
                {
                    stack.AddChild(item);
                }
            }

            var absPos = GetAbsolutePosition();
            float mainH = HeightConstraint ?? 32f;
            _dropDownPopup.Width = Size.X;
            _dropDownPopup.Height = Math.Min(300f, Items.Count * 32f + 2f);
            _dropDownPopup.FlowDirection = FlowDirection;

            // Force theme synchronization right before showing the popup
            _dropDownPopup.NotifyThemeChanged();

            PopupService.ShowPopup(_dropDownPopup, new Vector2(absPos.X, absPos.Y + mainH + 2f), this);
        }
        else
        {
            if (_dropDownPopup != null)
            {
                PopupService.HidePopup(_dropDownPopup);
            }
        }
        Invalidate();
    }

    public override void OnVisualStateChanged()
    {
        // Automatically collapse dropdown when focus is lost
        if (!IsFocused && !IsPointerPressed && IsDropDownOpen)
        {
            bool focusIsWithinPopup = false;
            var focused = InputSystem.FocusedElement;
            if (focused != null && _dropDownPopup != null)
            {
                Visual? current = focused;
                while (current != null)
                {
                    if (current == _dropDownPopup)
                    {
                        focusIsWithinPopup = true;
                        break;
                    }
                    current = current.Parent;
                }
            }

            if (!focusIsWithinPopup)
            {
                IsDropDownOpen = false;
            }
        }
        base.OnVisualStateChanged();
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            if (e.Pointer.PointerDeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen)
            {
                _pendingTouchPointerId = e.Pointer.PointerId;
                e.Handled = true;
                base.OnPointerPressed(e);
                return;
            }

            // Toggle dropdown if clicked on the header button area
            if (e.GetCurrentPoint(this).Position.Y < 32f)
            {
                IsDropDownOpen = !IsDropDownOpen;
                e.Handled = true;
            }

            base.OnPointerPressed(e); // Sets focus to this ComboBox
        }
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (_pendingTouchPointerId == e.Pointer.PointerId)
        {
            if (IsEnabled && IsPointerPressed && IsPointerOver && e.GetCurrentPoint(this).Position.Y < 32f)
            {
                IsDropDownOpen = !IsDropDownOpen;
                e.Handled = true;
            }
            _pendingTouchPointerId = 0;
        }
        base.OnPointerReleased(e);
    }

    public override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        if (_pendingTouchPointerId == e.Pointer.PointerId) _pendingTouchPointerId = 0;
        base.OnPointerCanceled(e);
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (IsEnabled && IsFocused)
        {
            int count = Items.Count;
            int currentIdx = SelectedItem != null ? Items.IndexOf(SelectedItem) : -1;

            if (e.Key == Key.Down)
            {
                if (count > 0)
                {
                    int nextIdx = (currentIdx + 1) % count;
                    SelectedItem = Items[nextIdx];
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                if (count > 0)
                {
                    int prevIdx = currentIdx - 1;
                    if (prevIdx < 0) prevIdx = count - 1;
                    SelectedItem = Items[prevIdx];
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                IsDropDownOpen = !IsDropDownOpen;
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (IsDropDownOpen)
                {
                    IsDropDownOpen = false;
                    e.Handled = true;
                }
            }
        }
        base.OnKeyDown(e);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = WidthConstraint ?? Math.Max(120f, availableSize.X);
        float h = HeightConstraint ?? 32f;
        return new Vector2(w, h);
    }



    public override Brush? GetCurrentBackground()
    {
        if (!IsEnabled) return ThemeManager.GetBrush("ComboBoxBackgroundDisabled") ?? Background;
        if (IsDropDownOpen) return ThemeManager.GetBrush("ComboBoxBackgroundPressed") ?? ThemeManager.GetBrush("CardBackground");
        return base.GetCurrentBackground();
    }

    public override Brush? GetCurrentBorderBrush()
    {
        if (!IsEnabled) return ThemeManager.GetBrush("ComboBoxBorderBrushDisabled") ?? BorderBrush;
        if (IsDropDownOpen) return ThemeManager.GetBrush("ComboBoxBorderBrushFocused") ?? ThemeManager.GetBrush("SystemAccentColor");
        return base.GetCurrentBorderBrush();
    }

    public override void OnRender(DrawingContext context)
    {
        base.OnRender(context); // Draw template background child first

        var activeFamily = ActualThemeFamily;
        var activeTheme = ActualTheme;

        float headerH = HeightConstraint ?? 32f;
        Rect headerRect = new Rect(0, 0, Size.X, headerH);

        if (!HasTemplate)
        {
            // ComboBox main button card
            Brush? bg = GetCurrentBackground();
            Brush? borderBrush = GetCurrentBorderBrush();
            Pen pen = new Pen(borderBrush ?? ThemeManager.GetBrush("ControlBorder"), BorderThickness.Left > 0 ? BorderThickness.Left : 1f);

            // Draw header background shape
            context.DrawRoundedRectangle(bg, pen, headerRect, CornerRadius);
        }

        // Draw active Selected Text or Placeholder Text
        var activeFont = GetActiveFont();
        if (activeFont != null)
        {
            float textY = (headerH - FontSize) / 2f;
            string textToDraw = SelectedItem != null ? SelectedItem.Text : PlaceholderText;
            Brush textBrush = SelectedItem != null 
                ? (Foreground ?? ThemeManager.GetBrush("TextPrimary")) 
                : ThemeManager.GetBrush("TextSecondary");

            Rect logicalTextBounds = new Rect(
                Padding.Left,
                textY,
                Math.Max(0f, Size.X - Padding.Left - Padding.Right),
                FontSize);
            Rect textBounds = LogicalToPhysical(logicalTextBounds);
            context.DrawText(
                textToDraw,
                activeFont,
                FontSize,
                textBrush,
                new Vector2(textBounds.X, textY),
                Matrix4x4.Identity,
                textBounds,
                textShapingOptions: GetTextShapingOptions(),
                textAlignment: FlowDirection == FlowDirection.RightToLeft
                    ? ProGPU.Text.TextAlignment.Right
                    : ProGPU.Text.TextAlignment.Left);

            if (activeFamily == VisualThemeFamily.macOS)
            {
                float capW = 22f;
                float capH = headerH - 4f;
                Rect capRect = LogicalToPhysical(new Rect(Size.X - capW - 2f, 2f, capW, capH));
                
                // Draw capsule background using central theme tokens
                Brush capBg = ThemeManager.GetBrush("ControlBackground", activeTheme, activeFamily);
                context.FillRoundedRectangle(capBg, capRect, 4f);
                
                // Draw capsule border using central theme tokens
                Pen capPen = ThemeManager.GetPen("ControlBorder", 0.5f, activeTheme, activeFamily);
                context.DrawRoundedRectangle(null, capPen, capRect, 4f);

                Brush arrowBrush;
                if (IsPointerOver || IsDropDownOpen)
                {
                    arrowBrush = ThemeManager.GetBrush("SystemAccentColor", activeTheme, activeFamily);
                }
                else
                {
                    arrowBrush = ThemeManager.GetBrush("TextSecondary", activeTheme, activeFamily);
                }

                DrawDropDownChevron(context, arrowBrush, capRect.X + capRect.Width * 0.5f, headerH * 0.5f);
            }
            else
            {
                float arrowX = FlowDirection == FlowDirection.RightToLeft ? 16f : Size.X - 16f;
                DrawDropDownChevron(context, ThemeManager.GetBrush("TextSecondary"), arrowX, headerH * 0.5f);
            }
        }

        base.OnRender(context);
    }

    private static void DrawDropDownChevron(DrawingContext context, Brush brush, float centerX, float centerY)
    {
        var pen = new Pen(brush, 1.5f);
        context.DrawLine(pen, new Vector2(centerX - 3.5f, centerY - 1.5f), new Vector2(centerX, centerY + 2f));
        context.DrawLine(pen, new Vector2(centerX, centerY + 2f), new Vector2(centerX + 3.5f, centerY - 1.5f));
    }
}
