namespace ProGPU.Designer;

using System;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Vector;
using ProGPU.Layout;
using Thickness = Microsoft.UI.Xaml.Thickness;
using HorizontalAlignment = ProGPU.Layout.HorizontalAlignment;
using VerticalAlignment = ProGPU.Layout.VerticalAlignment;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using Grid = Microsoft.UI.Xaml.Controls.Grid;
using System.Numerics;

public class PropertyGrid : Border
{
    private FrameworkElement? _selectedElement;
    private readonly StackPanel _mainStack;
    private readonly ScrollViewer _scrollViewer;
    private readonly ProGPU.Text.TtfFont? _font;
    private bool _isUpdating;

    public FrameworkElement? SelectedElement
    {
        get => _selectedElement;
        set
        {
            if (_selectedElement != value)
            {
                _selectedElement = value;
                RefreshProperties();
            }
        }
    }

    public PropertyGrid(ProGPU.Text.TtfFont? font)
    {
        _font = font;
        Background = new ThemeResourceBrush("CardBackground");
        BorderBrush = new ThemeResourceBrush("ControlBorder");
        BorderThickness = new Thickness(1f);
        CornerRadius = 8f;
        Padding = new Thickness(8);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _mainStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var titleText = new RichTextBlock
        {
            Font = font,
            FontSize = 14f,
            Margin = new Thickness(4, 4, 4, 12),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        titleText.Inlines.Add(new Bold(new Run("Properties")));
        _mainStack.AddChild(titleText);

        _scrollViewer = new ScrollViewer
        {
            Content = _mainStack,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        Child = _scrollViewer;
        
        RefreshProperties();
    }

    public void RefreshProperties()
    {
        while (_mainStack.Children.Count > 1)
        {
            _mainStack.RemoveChild(_mainStack.Children[1]);
        }

        if (_selectedElement == null)
        {
            var noSelectionText = new RichTextBlock
            {
                Font = _font,
                FontSize = 12f,
                Foreground = new ThemeResourceBrush("TextSecondary"),
                Margin = new Thickness(4, 10, 4, 4)
            };
            noSelectionText.Inlines.Add(new Run("No element selected"));
            _mainStack.AddChild(noSelectionText);
            return;
        }

        var typeHeader = new Border
        {
            Background = new ThemeResourceBrush("ControlBackground"),
            CornerRadius = 4f,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var typeText = new RichTextBlock { Font = _font, FontSize = 12f };
        typeText.Inlines.Add(new Bold(new Run("Type: ")));
        typeText.Inlines.Add(new Run(_selectedElement.GetType().Name));
        if (!string.IsNullOrEmpty(_selectedElement.Name))
        {
            typeText.Inlines.Add(new Run($" ({_selectedElement.Name})"));
        }
        typeHeader.Child = typeText;
        _mainStack.AddChild(typeHeader);

        // 1. Layout Properties
        var layoutStack = CreateCategoryGroup("Layout");
        AddPropertyRow(layoutStack, "Width", CreateFloatEditor(
            () => float.IsNaN(_selectedElement.Width) ? "" : _selectedElement.Width.ToString(),
            val => {
                if (float.TryParse(val, out float f)) _selectedElement.Width = f;
                else _selectedElement.Width = float.NaN;
                _selectedElement.InvalidateMeasure();
                _selectedElement.Invalidate();
            }
        ));
        AddPropertyRow(layoutStack, "Height", CreateFloatEditor(
            () => float.IsNaN(_selectedElement.Height) ? "" : _selectedElement.Height.ToString(),
            val => {
                if (float.TryParse(val, out float f)) _selectedElement.Height = f;
                else _selectedElement.Height = float.NaN;
                _selectedElement.InvalidateMeasure();
                _selectedElement.Invalidate();
            }
        ));
        AddPropertyRow(layoutStack, "Horz Align", CreateEnumEditor<HorizontalAlignment>(
            () => _selectedElement.HorizontalAlignment,
            val => {
                _selectedElement.HorizontalAlignment = val;
                _selectedElement.InvalidateArrange();
                _selectedElement.Invalidate();
            }
        ));
        AddPropertyRow(layoutStack, "Vert Align", CreateEnumEditor<VerticalAlignment>(
            () => _selectedElement.VerticalAlignment,
            val => {
                _selectedElement.VerticalAlignment = val;
                _selectedElement.InvalidateArrange();
                _selectedElement.Invalidate();
            }
        ));
        AddPropertyRow(layoutStack, "Margin", CreateThicknessEditor(
            () => _selectedElement.Margin,
            val => {
                _selectedElement.Margin = val;
                _selectedElement.InvalidateMeasure();
                _selectedElement.Invalidate();
            }
        ));
        AddPropertyRow(layoutStack, "Padding", CreateThicknessEditor(
            () => _selectedElement.Padding,
            val => {
                _selectedElement.Padding = val;
                _selectedElement.InvalidateMeasure();
                _selectedElement.Invalidate();
            }
        ));
        _mainStack.AddChild(layoutStack);

        // 2. Appearance Properties
        var appearanceStack = CreateCategoryGroup("Appearance");
        
        AddPropertyRow(appearanceStack, "Background", CreateBrushEditor(
            () => {
                if (_selectedElement is Control ctrl)
                {
                    if (ctrl.Background is SolidColorBrush scb)
                    {
                        var col = scb.Color;
                        return $"#{(byte)(col.W * 255):X2}{(byte)(col.X * 255):X2}{(byte)(col.Y * 255):X2}{(byte)(col.Z * 255):X2}";
                    }
                    else if (ctrl.Background is ThemeResourceBrush trb)
                    {
                        return trb.ResourceKey;
                    }
                }
                else if (_selectedElement is Border b)
                {
                    if (b.Background is SolidColorBrush scb)
                    {
                        var col = scb.Color;
                        return $"#{(byte)(col.W * 255):X2}{(byte)(col.X * 255):X2}{(byte)(col.Y * 255):X2}{(byte)(col.Z * 255):X2}";
                    }
                    else if (b.Background is ThemeResourceBrush trb)
                    {
                        return trb.ResourceKey;
                    }
                }
                return "";
            },
            val => {
                Brush? brush = null;
                if (val.StartsWith("#"))
                {
                    var hex = val.Substring(1);
                    if (hex.Length == 6) hex = "FF" + hex;
                    if (hex.Length == 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint rgba))
                    {
                        float a = ((rgba >> 24) & 0xFF) / 255f;
                        float r = ((rgba >> 16) & 0xFF) / 255f;
                        float g = ((rgba >> 8) & 0xFF) / 255f;
                        float b = (rgba & 0xFF) / 255f;
                        brush = new SolidColorBrush(new Vector4(r, g, b, a));
                    }
                }
                else if (!string.IsNullOrEmpty(val))
                {
                    brush = new ThemeResourceBrush(val);
                }

                if (_selectedElement is Control ctrl)
                {
                    ctrl.Background = brush;
                    ctrl.Invalidate();
                }
                else if (_selectedElement is Border border)
                {
                    border.Background = brush;
                    border.Invalidate();
                }
            }
        ));
        
        AddPropertyRow(appearanceStack, "Opacity", CreateFloatEditor(
            () => _selectedElement.Opacity.ToString("0.00"),
            val => {
                if (float.TryParse(val, out float f))
                {
                    _selectedElement.Opacity = Math.Clamp(f, 0f, 1f);
                    _selectedElement.Invalidate();
                }
            }
        ));

        var hasCornerRadius = _selectedElement.GetType().GetProperty("CornerRadius") != null;
        if (hasCornerRadius)
        {
            AddPropertyRow(appearanceStack, "CornerRadius", CreateFloatEditor(
                () => {
                    var prop = _selectedElement.GetType().GetProperty("CornerRadius");
                    return prop?.GetValue(_selectedElement)?.ToString() ?? "0";
                },
                val => {
                    if (float.TryParse(val, out float f))
                    {
                        var prop = _selectedElement.GetType().GetProperty("CornerRadius");
                        prop?.SetValue(_selectedElement, f);
                        _selectedElement.Invalidate();
                    }
                }
            ));
        }

        _mainStack.AddChild(appearanceStack);

        // 3. Content / Text Properties
        var textProp = _selectedElement.GetType().GetProperty("Text");
        var contentProp = _selectedElement.GetType().GetProperty("Content");
        
        if (textProp != null || contentProp != null)
        {
            var contentStack = CreateCategoryGroup("Content & Text");
            if (textProp != null && textProp.PropertyType == typeof(string))
            {
                AddPropertyRow(contentStack, "Text", CreateTextEditor(
                    () => (string)(textProp.GetValue(_selectedElement) ?? ""),
                    val => {
                        textProp.SetValue(_selectedElement, val);
                        _selectedElement.InvalidateMeasure();
                        _selectedElement.Invalidate();
                    }
                ));
            }
            else if (contentProp != null)
            {
                AddPropertyRow(contentStack, "Content", CreateTextEditor(
                    () => {
                        var val = contentProp.GetValue(_selectedElement);
                        return val is string s ? s : "";
                    },
                    val => {
                        if (contentProp.PropertyType == typeof(object) || contentProp.PropertyType == typeof(string))
                        {
                            contentProp.SetValue(_selectedElement, val);
                            _selectedElement.InvalidateMeasure();
                            _selectedElement.Invalidate();
                        }
                    }
                ));
            }
            _mainStack.AddChild(contentStack);
        }
    }

    private StackPanel CreateCategoryGroup(string title)
    {
        var categoryStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var catTitle = new RichTextBlock
        {
            Font = _font,
            FontSize = 12f,
            Foreground = new ThemeResourceBrush("SystemAccentColor"),
            Margin = new Thickness(4, 4, 4, 8)
        };
        catTitle.Inlines.Add(new Bold(new Run(title)));
        categoryStack.AddChild(catTitle);

        return categoryStack;
    }

    private void AddPropertyRow(StackPanel parent, string labelText, FrameworkElement editor)
    {
        var grid = new Grid { Margin = new Thickness(4, 3, 4, 3), HeightConstraint = 32f };
        grid.ColumnDefinitions.Add(new GridLength(90, GridUnitType.Absolute));
        grid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));

        var label = new RichTextBlock
        {
            Font = _font,
            FontSize = 11f,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        label.Inlines.Add(new Run(labelText));
        grid.AddChild(label);
        Grid.SetColumn(label, 0);

        editor.Font = _font;
        var fontSizeProp = editor.GetType().GetProperty("FontSize");
        if (fontSizeProp != null && fontSizeProp.CanWrite)
        {
            try { fontSizeProp.SetValue(editor, 11f); } catch { }
        }
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        editor.VerticalAlignment = VerticalAlignment.Center;
        grid.AddChild(editor);
        Grid.SetColumn(editor, 1);

        parent.AddChild(grid);
    }

    private TextBox CreateTextEditor(Func<string> getter, Action<string> setter)
    {
        var tb = new TextBox
        {
            Text = getter(),
            HeightConstraint = 26f
        };
        tb.TextChanged += (s, e) => {
            if (_isUpdating) return;
            _isUpdating = true;
            try { setter(tb.Text); } catch { }
            _isUpdating = false;
        };
        return tb;
    }

    private TextBox CreateFloatEditor(Func<string> getter, Action<string> setter)
    {
        var tb = new TextBox
        {
            Text = getter(),
            HeightConstraint = 26f
        };
        tb.TextChanged += (s, e) => {
            if (_isUpdating) return;
            _isUpdating = true;
            try { setter(tb.Text); } catch { }
            _isUpdating = false;
        };
        return tb;
    }

    private ComboBox CreateEnumEditor<T>(Func<T> getter, Action<T> setter) where T : struct, Enum
    {
        var cb = new ComboBox { HeightConstraint = 26f };
        var values = Enum.GetValues<T>();
        foreach (var val in values)
        {
            cb.Items.Add(new ComboBoxItem { Text = val.ToString() });
        }

        var currentVal = getter();
        foreach (var item in cb.Items)
        {
            if (item.Text == currentVal.ToString())
            {
                cb.SelectedItem = item;
                break;
            }
        }

        cb.SelectionChanged += (s, e) => {
            if (_isUpdating) return;
            if (cb.SelectedItem != null && Enum.TryParse<T>(cb.SelectedItem.Text, out var val))
            {
                _isUpdating = true;
                try { setter(val); } catch { }
                _isUpdating = false;
            }
        };
        return cb;
    }

    private TextBox CreateThicknessEditor(Func<ProGPU.Layout.Thickness> getter, Action<ProGPU.Layout.Thickness> setter)
    {
        var t = getter();
        var tb = new TextBox
        {
            Text = $"{t.Left},{t.Top},{t.Right},{t.Bottom}",
            HeightConstraint = 26f
        };
        tb.TextChanged += (s, e) => {
            if (_isUpdating) return;
            _isUpdating = true;
            try
            {
                var val = ProGPU.Layout.Thickness.Parse(tb.Text);
                setter(val);
            }
            catch { }
            _isUpdating = false;
        };
        return tb;
    }

    private TextBox CreateBrushEditor(Func<string> getter, Action<string> setter)
    {
        var tb = new TextBox
        {
            Text = getter(),
            HeightConstraint = 26f
        };
        tb.TextChanged += (s, e) => {
            if (_isUpdating) return;
            _isUpdating = true;
            try { setter(tb.Text); } catch { }
            _isUpdating = false;
        };
        return tb;
    }
}
