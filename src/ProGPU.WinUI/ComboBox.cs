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
using ProGPU.Text;

namespace Microsoft.UI.Xaml.Controls;

public class ComboBox : Control
{
    private bool _isDropDownOpen;
    private ComboBoxItem? _selectedItem;
    private string _placeholderText = "Select item...";
    private float _fontSize = 14f;
    private Border? _dropDownPopup;
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
        Vector2 pos = Offset;
        Visual? current = Parent;
        while (current != null)
        {
            pos += current.Offset;
            current = current.Parent;
        }
        return pos;
    }

    private void UpdatePopupState()
    {
        if (_isDropDownOpen)
        {
            if (_dropDownPopup == null)
            {
                var stack = new StackPanel { Orientation = Orientation.Vertical };
                foreach (var item in Items)
                {
                    stack.AddChild(item);
                }

                _dropDownPopup = new Border
                {
                    Background = new ThemeResourceBrush("CardBackground"),
                    BorderBrush = new ThemeResourceBrush("ControlBorder"),
                    BorderThickness = new Thickness(1f),
                    CornerRadius = 4f,
                    Child = stack
                };
            }
            else
            {
                var stack = (StackPanel)_dropDownPopup.Child!;
                stack.ClearChildren();
                foreach (var item in Items)
                {
                    stack.AddChild(item);
                }
            }

            var absPos = GetAbsolutePosition();
            float mainH = HeightConstraint ?? 32f;
            _dropDownPopup.Width = Size.X;
            _dropDownPopup.Height = Items.Count * 32f + 2f;

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
            // Toggle dropdown if clicked on the header button area
            if (e.Position.Y < 32f)
            {
                IsDropDownOpen = !IsDropDownOpen;
                e.Handled = true;
            }

            base.OnPointerPressed(e); // Sets focus to this ComboBox
        }
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



    public TtfFont? GetActiveFont()
    {
        if (Font != null) return Font;
        var p = Parent;
        while (p != null)
        {
            var prop = p.GetType().GetProperty("Font");
            if (prop != null && prop.GetValue(p) is TtfFont f) return f;
            p = p.Parent;
        }

        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in asm)
            {
                var type = assembly.GetType("ProGPU.Samples.Program");
                if (type != null)
                {
                    var method = type.GetMethod("GetFont");
                    if (method != null && method.Invoke(null, null) is TtfFont staticFont)
                    {
                        return staticFont;
                    }
                }
            }
        }
        catch { }
        return null;
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

            context.DrawText(textToDraw, activeFont, FontSize, textBrush, new Vector2(Padding.Left, textY));

            if (activeFamily == VisualThemeFamily.macOS)
            {
                Brush arrowBrush;
                if (IsPointerOver || IsDropDownOpen)
                {
                    arrowBrush = new SolidColorBrush(activeTheme == ElementTheme.Light ? new Vector4(0f, 0.478f, 1f, 1f) : new Vector4(0.04f, 0.52f, 1f, 1f));
                }
                else
                {
                    arrowBrush = new SolidColorBrush(new Vector4(0.55f, 0.55f, 0.57f, 1f));
                }

                context.DrawText("▲", activeFont, FontSize - 4f, arrowBrush, new Vector2(Size.X - 18f, textY - 3f));
                context.DrawText("▼", activeFont, FontSize - 4f, arrowBrush, new Vector2(Size.X - 18f, textY + 5f));
            }
            else
            {
                context.DrawText("▼", activeFont, FontSize - 2f, ThemeManager.GetBrush("TextSecondary"), new Vector2(Size.X - 22f, textY + 1f));
            }
        }

        base.OnRender(context);
    }
}
