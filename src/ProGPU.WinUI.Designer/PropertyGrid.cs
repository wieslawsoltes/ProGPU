namespace ProGPU.WinUI.Designer;

using System;
using System.Text;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Vector;
using ProGPU.Layout;
using Thickness = Microsoft.UI.Xaml.Thickness;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;
using System.Numerics;

public class PropertyItem : IDataGridValueProvider
{
    private string _value = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Value
    {
        get => _value;
        set
        {
            if (_value != value)
            {
                _value = value;
                try
                {
                    OnChanged?.Invoke(_value);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PropertyGrid] Error setting {Name}: {ex.Message}");
                }
            }
        }
    }
    public Action<string>? OnChanged { get; set; }
    public Type PropertyType { get; set; } = typeof(string);

    public PropertyItem(string name, string value, Action<string> onChanged)
    {
        Name = name;
        _value = value;
        OnChanged = onChanged;
    }

    public PropertyItem(string name, string value, Type propertyType, Action<string> onChanged)
    {
        Name = name;
        _value = value;
        PropertyType = propertyType;
        OnChanged = onChanged;
    }

    public bool TryGetDataGridValue(string propertyName, out object? value)
    {
        switch (propertyName)
        {
            case nameof(Name):
                value = Name;
                return true;
            case nameof(Value):
                value = Value;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public bool TrySetDataGridValue(string propertyName, object? value)
    {
        if (propertyName == nameof(Value))
        {
            Value = value?.ToString() ?? string.Empty;
            return true;
        }

        return false;
    }

    public Type? GetDataGridValueType(string propertyName)
    {
        return propertyName switch
        {
            nameof(Name) => typeof(string),
            nameof(Value) => typeof(string),
            _ => null
        };
    }
}

public class PropertyGrid : Border
{
    private FrameworkElement? _selectedElement;
    private readonly DataGrid _dataGrid;
    private readonly ProGPU.Text.TtfFont? _font;
    private readonly RichTextBlock _titleText;

    public new event Action? PropertyChanged;

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

        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(GridLength.Auto);
        mainGrid.RowDefinitions.Add(GridLength.Star(1f));

        _titleText = new RichTextBlock
        {
            Font = font,
            FontSize = 14f,
            Margin = new Thickness(4, 4, 4, 12),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _titleText.Inlines.Add(new Bold(new Run("Properties")));
        Grid.SetRow(_titleText, 0);
        mainGrid.AddChild(_titleText);

        _dataGrid = new DataGrid
        {
            Font = font,
            FontSize = 11f,
            RowHeight = 26f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _dataGrid.Columns.Add(new DataGridColumn("Property", "110", "Name"));
        _dataGrid.Columns.Add(new DataGridColumn("Value", "*", "Value"));

        Grid.SetRow(_dataGrid, 1);
        mainGrid.AddChild(_dataGrid);

        Child = mainGrid;

        RefreshProperties();
    }

    public void RefreshProperties()
    {
        _dataGrid.ClearItems();

        if (_selectedElement == null)
        {
            _titleText.Inlines.Clear();
            _titleText.Inlines.Add(new Bold(new Run("Properties (No Selection)")));
            return;
        }

        string typeName = _selectedElement.GetType().Name;
        _titleText.Inlines.Clear();
        _titleText.Inlines.Add(new Bold(new Run($"Properties: {typeName}")));

        // Name
        _dataGrid.AddItem(new PropertyItem("Name", _selectedElement.Name ?? "", val =>
        {
            _selectedElement.Name = val;
            PropertyChanged?.Invoke();
        }));

        // Canvas.Left
        float left = Canvas.GetLeft(_selectedElement);
        _dataGrid.AddItem(new PropertyItem("Canvas.Left", left.ToString("F1"), val =>
        {
            if (float.TryParse(val, out float l))
            {
                Canvas.SetLeft(_selectedElement, l);
                _selectedElement.InvalidateArrange();
                PropertyChanged?.Invoke();
            }
        }));

        // Canvas.Top
        float top = Canvas.GetTop(_selectedElement);
        _dataGrid.AddItem(new PropertyItem("Canvas.Top", top.ToString("F1"), val =>
        {
            if (float.TryParse(val, out float t))
            {
                Canvas.SetTop(_selectedElement, t);
                _selectedElement.InvalidateArrange();
                PropertyChanged?.Invoke();
            }
        }));

        // Width
        float w = float.IsNaN(_selectedElement.Width) ? _selectedElement.Size.X : _selectedElement.Width;
        _dataGrid.AddItem(new PropertyItem("Width", w.ToString("F1"), val =>
        {
            if (float.TryParse(val, out float widthVal))
            {
                _selectedElement.Width = widthVal;
                _selectedElement.InvalidateMeasure();
                _selectedElement.InvalidateArrange();
                PropertyChanged?.Invoke();
            }
        }));

        // Height
        float h = float.IsNaN(_selectedElement.Height) ? _selectedElement.Size.Y : _selectedElement.Height;
        _dataGrid.AddItem(new PropertyItem("Height", h.ToString("F1"), val =>
        {
            if (float.TryParse(val, out float heightVal))
            {
                _selectedElement.Height = heightVal;
                _selectedElement.InvalidateMeasure();
                _selectedElement.InvalidateArrange();
                PropertyChanged?.Invoke();
            }
        }));

        // Opacity
        _dataGrid.AddItem(new PropertyItem("Opacity", _selectedElement.Opacity.ToString("F2"), val =>
        {
            if (float.TryParse(val, out float op))
            {
                _selectedElement.Opacity = Math.Clamp(op, 0f, 1f);
                _selectedElement.Invalidate();
                PropertyChanged?.Invoke();
            }
        }));

        // Visibility
        _dataGrid.AddItem(new PropertyItem("Visibility", _selectedElement.Visibility.ToString(), typeof(Visibility), val =>
        {
            if (Enum.TryParse<Visibility>(val, out var vis))
            {
                _selectedElement.Visibility = vis;
                _selectedElement.InvalidateMeasure();
                _selectedElement.InvalidateArrange();
                _selectedElement.Invalidate();
                PropertyChanged?.Invoke();
            }
        }));

        // CornerRadius
        if (TryGetDependencyProperty("CornerRadius", typeof(float), out var crProp))
        {
            float crVal = _selectedElement.GetValue(crProp) is float f ? f : 0f;
            _dataGrid.AddItem(new PropertyItem("CornerRadius", crVal.ToString("F1"), val =>
            {
                if (float.TryParse(val, out float fcr))
                {
                    _selectedElement.SetValue(crProp, fcr);
                    _selectedElement.Invalidate();
                    PropertyChanged?.Invoke();
                }
            }));
        }

        // Text
        if (_selectedElement is TextBox selectedTextBox)
        {
            _dataGrid.AddItem(new PropertyItem("Text", selectedTextBox.Text, val =>
            {
                selectedTextBox.Text = val;
                selectedTextBox.InvalidateMeasure();
                selectedTextBox.Invalidate();
                PropertyChanged?.Invoke();
            }));
        }
        else if (TryGetDependencyProperty("Text", typeof(string), out var textProp))
        {
            string txtVal = _selectedElement.GetValue(textProp) as string ?? "";
            _dataGrid.AddItem(new PropertyItem("Text", txtVal, val =>
            {
                _selectedElement.SetValue(textProp, val);
                _selectedElement.InvalidateMeasure();
                _selectedElement.Invalidate();
                PropertyChanged?.Invoke();
            }));
        }

        // Content (for strings/buttons)
        var contentProp = DependencyProperty.Lookup(_selectedElement.GetType(), "Content");
        if (contentProp != null)
        {
            var contentVal = _selectedElement.GetValue(contentProp);
            string contentStr = "";
            if (contentVal is string s) contentStr = s;
            else if (contentVal is RichTextBlock rtb)
            {
                var sb = new StringBuilder();
                foreach (var inline in rtb.Inlines)
                {
                    if (inline is Run r) sb.Append(r.Text);
                }
                contentStr = sb.ToString();
            }

            _dataGrid.AddItem(new PropertyItem("Content", contentStr, val =>
            {
                if (contentVal is RichTextBlock richText)
                {
                    richText.Inlines.Clear();
                    richText.Inlines.Add(new Run(val));
                    richText.Invalidate();
                }
                else
                {
                    _selectedElement.SetValue(contentProp, val);
                }
                _selectedElement.InvalidateMeasure();
                _selectedElement.InvalidateArrange();
                PropertyChanged?.Invoke();
            }));
        }

        // Minimum
        if (TryGetDependencyProperty("Minimum", typeof(float), out var minProp))
        {
            float minVal = _selectedElement.GetValue(minProp) is float value ? value : 0f;
            _dataGrid.AddItem(new PropertyItem("Minimum", minVal.ToString("F1"), val =>
            {
                if (float.TryParse(val, out float fval))
                {
                    _selectedElement.SetValue(minProp, fval);
                    _selectedElement.Invalidate();
                    PropertyChanged?.Invoke();
                }
            }));
        }

        // Maximum
        if (TryGetDependencyProperty("Maximum", typeof(float), out var maxProp))
        {
            float maxVal = _selectedElement.GetValue(maxProp) is float value ? value : 0f;
            _dataGrid.AddItem(new PropertyItem("Maximum", maxVal.ToString("F1"), val =>
            {
                if (float.TryParse(val, out float fval))
                {
                    _selectedElement.SetValue(maxProp, fval);
                    _selectedElement.Invalidate();
                    PropertyChanged?.Invoke();
                }
            }));
        }

        // Value
        if (TryGetDependencyProperty("Value", typeof(float), out var valProp))
        {
            float valVal = _selectedElement.GetValue(valProp) is float value ? value : 0f;
            _dataGrid.AddItem(new PropertyItem("Value", valVal.ToString("F1"), val =>
            {
                if (float.TryParse(val, out float fval))
                {
                    _selectedElement.SetValue(valProp, fval);
                    _selectedElement.Invalidate();
                    PropertyChanged?.Invoke();
                }
            }));
        }

        // IsChecked
        if (TryGetDependencyProperty("IsChecked", typeof(bool), out var isCheckedProp))
        {
            bool isCheckedVal = _selectedElement.GetValue(isCheckedProp) is bool value && value;
            _dataGrid.AddItem(new PropertyItem("IsChecked", isCheckedVal.ToString(), typeof(bool), val =>
            {
                if (bool.TryParse(val, out bool bval))
                {
                    _selectedElement.SetValue(isCheckedProp, bval);
                    _selectedElement.Invalidate();
                    PropertyChanged?.Invoke();
                }
            }));
        }

        // IsOn
        if (TryGetDependencyProperty("IsOn", typeof(bool), out var isOnProp))
        {
            bool isOnVal = _selectedElement.GetValue(isOnProp) is bool value && value;
            _dataGrid.AddItem(new PropertyItem("IsOn", isOnVal.ToString(), typeof(bool), val =>
            {
                if (bool.TryParse(val, out bool bval))
                {
                    _selectedElement.SetValue(isOnProp, bval);
                    _selectedElement.Invalidate();
                    PropertyChanged?.Invoke();
                }
            }));
        }

        // Orientation
        if (TryGetDependencyProperty("Orientation", out var orientProp) && orientProp.PropertyType.IsEnum)
        {
            var orientVal = _selectedElement.GetValue(orientProp);
            _dataGrid.AddItem(new PropertyItem("Orientation", orientVal?.ToString() ?? string.Empty, orientProp.PropertyType, val =>
            {
                if (Enum.TryParse(orientProp.PropertyType, val, out var eval))
                {
                    _selectedElement.SetValue(orientProp, eval);
                    _selectedElement.InvalidateMeasure();
                    PropertyChanged?.Invoke();
                }
            }));
        }

        // HorizontalAlignment
        _dataGrid.AddItem(new PropertyItem("HorizontalAlignment", _selectedElement.HorizontalAlignment.ToString(), typeof(HorizontalAlignment), val =>
        {
            if (Enum.TryParse<HorizontalAlignment>(val, out var align))
            {
                _selectedElement.HorizontalAlignment = align;
                _selectedElement.InvalidateMeasure();
                _selectedElement.InvalidateArrange();
                PropertyChanged?.Invoke();
            }
        }));

        // VerticalAlignment
        _dataGrid.AddItem(new PropertyItem("VerticalAlignment", _selectedElement.VerticalAlignment.ToString(), typeof(VerticalAlignment), val =>
        {
            if (Enum.TryParse<VerticalAlignment>(val, out var align))
            {
                _selectedElement.VerticalAlignment = align;
                _selectedElement.InvalidateMeasure();
                _selectedElement.InvalidateArrange();
                PropertyChanged?.Invoke();
            }
        }));

        // HorizontalContentAlignment
        if (TryGetDependencyProperty("HorizontalContentAlignment", typeof(HorizontalAlignment), out var horizContentProp))
        {
            var alignVal = _selectedElement.GetValue(horizContentProp);
            _dataGrid.AddItem(new PropertyItem("HorizontalContentAlignment", alignVal?.ToString() ?? string.Empty, typeof(HorizontalAlignment), val =>
            {
                if (Enum.TryParse<HorizontalAlignment>(val, out var align))
                {
                    _selectedElement.SetValue(horizContentProp, align);
                    _selectedElement.InvalidateMeasure();
                    _selectedElement.InvalidateArrange();
                    PropertyChanged?.Invoke();
                }
            }));
        }

        // VerticalContentAlignment
        if (TryGetDependencyProperty("VerticalContentAlignment", typeof(VerticalAlignment), out var vertContentProp))
        {
            var alignVal = _selectedElement.GetValue(vertContentProp);
            _dataGrid.AddItem(new PropertyItem("VerticalContentAlignment", alignVal?.ToString() ?? string.Empty, typeof(VerticalAlignment), val =>
            {
                if (Enum.TryParse<VerticalAlignment>(val, out var align))
                {
                    _selectedElement.SetValue(vertContentProp, align);
                    _selectedElement.InvalidateMeasure();
                    _selectedElement.InvalidateArrange();
                    PropertyChanged?.Invoke();
                }
            }));
        }

        // Background
        if (TryGetBrushDependencyProperty("Background", out var bgProp))
        {
            var bgVal = _selectedElement.GetValue(bgProp);
            string bgStr = GetBrushString(bgVal as Brush);
            _dataGrid.AddItem(new PropertyItem("Background", bgStr, typeof(Brush), val =>
            {
                var converted = ConvertValueToBrush(val);
                _selectedElement.SetValue(bgProp, converted);
                _selectedElement.Invalidate();
                PropertyChanged?.Invoke();
            }));
        }

        // Foreground
        if (TryGetBrushDependencyProperty("Foreground", out var fgProp))
        {
            var fgVal = _selectedElement.GetValue(fgProp);
            string fgStr = GetBrushString(fgVal as Brush);
            _dataGrid.AddItem(new PropertyItem("Foreground", fgStr, typeof(Brush), val =>
            {
                var converted = ConvertValueToBrush(val);
                _selectedElement.SetValue(fgProp, converted);
                _selectedElement.Invalidate();
                PropertyChanged?.Invoke();
            }));
        }

        // BorderBrush
        if (TryGetBrushDependencyProperty("BorderBrush", out var borderBrushProp))
        {
            var borderVal = _selectedElement.GetValue(borderBrushProp);
            string borderStr = GetBrushString(borderVal as Brush);
            _dataGrid.AddItem(new PropertyItem("BorderBrush", borderStr, typeof(Brush), val =>
            {
                var converted = ConvertValueToBrush(val);
                _selectedElement.SetValue(borderBrushProp, converted);
                _selectedElement.Invalidate();
                PropertyChanged?.Invoke();
            }));
        }

        // Grid.Row & Grid.Column (if parent is Grid)
        if (_selectedElement.Parent is Grid)
        {
            int gridRow = Grid.GetRow(_selectedElement);
            _dataGrid.AddItem(new PropertyItem("Grid.Row", gridRow.ToString(), val =>
            {
                if (int.TryParse(val, out int r))
                {
                    Grid.SetRow(_selectedElement, r);
                    if (_selectedElement.Parent is FrameworkElement parentFe)
                    {
                        parentFe.InvalidateMeasure();
                        parentFe.InvalidateArrange();
                        parentFe.Invalidate();
                    }
                    PropertyChanged?.Invoke();
                }
            }));

            int gridCol = Grid.GetColumn(_selectedElement);
            _dataGrid.AddItem(new PropertyItem("Grid.Column", gridCol.ToString(), val =>
            {
                if (int.TryParse(val, out int c))
                {
                    Grid.SetColumn(_selectedElement, c);
                    if (_selectedElement.Parent is FrameworkElement parentFe)
                    {
                        parentFe.InvalidateMeasure();
                        parentFe.InvalidateArrange();
                        parentFe.Invalidate();
                    }
                    PropertyChanged?.Invoke();
                }
            }));
        }

        // DockPanel.Dock (if parent is DockPanel)
        if (_selectedElement.Parent is DockPanel)
        {
            var dock = DockPanel.GetDock(_selectedElement);
            _dataGrid.AddItem(new PropertyItem("DockPanel.Dock", dock.ToString(), typeof(Dock), val =>
            {
                if (Enum.TryParse<Dock>(val, out var dk))
                {
                    DockPanel.SetDock(_selectedElement, dk);
                    if (_selectedElement.Parent is FrameworkElement parentFe)
                    {
                        parentFe.InvalidateMeasure();
                        parentFe.InvalidateArrange();
                        parentFe.Invalidate();
                    }
                    PropertyChanged?.Invoke();
                }
            }));
        }

        // Grid Definition Editing
        if (_selectedElement is Grid selectedGrid)
        {
            string colStr = GetGridLengthsString(selectedGrid.ColumnDefinitions);
            _dataGrid.AddItem(new PropertyItem("ColumnDefinitions", colStr, val =>
            {
                selectedGrid.ColumnDefinitions.Clear();
                foreach (var gl in ParseGridLengths(val))
                {
                    selectedGrid.ColumnDefinitions.Add(gl);
                }
                selectedGrid.InvalidateMeasure();
                selectedGrid.InvalidateArrange();
                selectedGrid.Invalidate();
                PropertyChanged?.Invoke();
            }));

            string rowStr = GetGridLengthsString(selectedGrid.RowDefinitions);
            _dataGrid.AddItem(new PropertyItem("RowDefinitions", rowStr, val =>
            {
                selectedGrid.RowDefinitions.Clear();
                foreach (var gl in ParseGridLengths(val))
                {
                    selectedGrid.RowDefinitions.Add(gl);
                }
                selectedGrid.InvalidateMeasure();
                selectedGrid.InvalidateArrange();
                selectedGrid.Invalidate();
                PropertyChanged?.Invoke();
            }));
        }

        // WrapPanel Editing
        if (_selectedElement is WrapPanel selectedWrap)
        {
            _dataGrid.AddItem(new PropertyItem("ItemWidth", float.IsNaN(selectedWrap.ItemWidth) ? "" : selectedWrap.ItemWidth.ToString("F0"), val =>
            {
                if (float.TryParse(val, out float iw))
                {
                    selectedWrap.ItemWidth = iw;
                }
                else
                {
                    selectedWrap.ItemWidth = float.NaN;
                }
                selectedWrap.InvalidateMeasure();
                selectedWrap.InvalidateArrange();
                selectedWrap.Invalidate();
                PropertyChanged?.Invoke();
            }));

            _dataGrid.AddItem(new PropertyItem("ItemHeight", float.IsNaN(selectedWrap.ItemHeight) ? "" : selectedWrap.ItemHeight.ToString("F0"), val =>
            {
                if (float.TryParse(val, out float ih))
                {
                    selectedWrap.ItemHeight = ih;
                }
                else
                {
                    selectedWrap.ItemHeight = float.NaN;
                }
                selectedWrap.InvalidateMeasure();
                selectedWrap.InvalidateArrange();
                selectedWrap.Invalidate();
                PropertyChanged?.Invoke();
            }));

            _dataGrid.AddItem(new PropertyItem("HorizontalSpacing", selectedWrap.HorizontalSpacing.ToString("F0"), val =>
            {
                if (float.TryParse(val, out float hs))
                {
                    selectedWrap.HorizontalSpacing = hs;
                    selectedWrap.InvalidateMeasure();
                    selectedWrap.InvalidateArrange();
                    selectedWrap.Invalidate();
                    PropertyChanged?.Invoke();
                }
            }));

            _dataGrid.AddItem(new PropertyItem("VerticalSpacing", selectedWrap.VerticalSpacing.ToString("F0"), val =>
            {
                if (float.TryParse(val, out float vs))
                {
                    selectedWrap.VerticalSpacing = vs;
                    selectedWrap.InvalidateMeasure();
                    selectedWrap.InvalidateArrange();
                    selectedWrap.Invalidate();
                    PropertyChanged?.Invoke();
                }
            }));
        }

        // DockPanel Editing
        if (_selectedElement is DockPanel selectedDock)
        {
            _dataGrid.AddItem(new PropertyItem("LastChildFill", selectedDock.LastChildFill.ToString(), typeof(bool), val =>
            {
                if (bool.TryParse(val, out bool lcf))
                {
                    selectedDock.LastChildFill = lcf;
                    selectedDock.InvalidateMeasure();
                    selectedDock.InvalidateArrange();
                    selectedDock.Invalidate();
                    PropertyChanged?.Invoke();
                }
            }));
        }
    }

    private bool TryGetDependencyProperty(string name, out DependencyProperty property)
    {
        property = null!;
        if (_selectedElement == null)
        {
            return false;
        }

        var dependencyProperty = DependencyProperty.Lookup(_selectedElement.GetType(), name);
        if (dependencyProperty == null)
        {
            return false;
        }

        property = dependencyProperty;
        return true;
    }

    private bool TryGetDependencyProperty(string name, Type propertyType, out DependencyProperty property)
    {
        property = null!;
        if (!TryGetDependencyProperty(name, out var dependencyProperty) ||
            dependencyProperty.PropertyType != propertyType)
        {
            return false;
        }

        property = dependencyProperty;
        return true;
    }

    private bool TryGetBrushDependencyProperty(string name, out DependencyProperty property)
    {
        property = null!;
        if (!TryGetDependencyProperty(name, out var dependencyProperty) ||
            !typeof(Brush).IsAssignableFrom(dependencyProperty.PropertyType))
        {
            return false;
        }

        property = dependencyProperty;
        return true;
    }

    private string GetBrushString(Brush? brush)
    {
        if (brush == null) return "";
        if (brush is ThemeResourceBrush tr) return tr.ResourceKey;
        if (brush is SolidColorBrush scb)
        {
            var col = scb.Color;
            byte r = (byte)Math.Clamp(Math.Round(col.X * 255f), 0, 255);
            byte g = (byte)Math.Clamp(Math.Round(col.Y * 255f), 0, 255);
            byte b = (byte)Math.Clamp(Math.Round(col.Z * 255f), 0, 255);
            byte a = (byte)Math.Clamp(Math.Round(col.W * 255f), 0, 255);
            if (a == 0 && r == 0 && g == 0 && b == 0) return "Transparent";
            return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
        }
        return brush.ToString() ?? "";
    }

    private Brush? ConvertValueToBrush(string val)
    {
        if (string.IsNullOrEmpty(val)) return null;
        if (val.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(new Vector4(0f, 0f, 0f, 0f));
        }
        if (val.StartsWith("#"))
        {
            try
            {
                var hex = val.Substring(1);
                if (hex.Length == 6) hex = "FF" + hex;
                if (hex.Length == 8)
                {
                    uint rgba = Convert.ToUInt32(hex, 16);
                    float a = ((rgba >> 24) & 0xFF) / 255.0f;
                    float r = ((rgba >> 16) & 0xFF) / 255.0f;
                    float g = ((rgba >> 8) & 0xFF) / 255.0f;
                    float b = (rgba & 0xFF) / 255.0f;
                    return new SolidColorBrush(new Vector4(r, g, b, a));
                }
            }
            catch {}
        }
        return new ThemeResourceBrush(val);
    }

    private string GetGridLengthsString(List<GridLength> list)
    {
        if (list == null || list.Count == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < list.Count; i++)
        {
            var gl = list[i];
            if (gl.UnitType == GridUnitType.Auto)
            {
                sb.Append("Auto");
            }
            else if (gl.UnitType == GridUnitType.Star)
            {
                if (gl.Value == 1f) sb.Append("*");
                else sb.Append($"{gl.Value}*");
            }
            else
            {
                sb.Append(gl.Value.ToString("F0"));
            }
            if (i < list.Count - 1) sb.Append(", ");
        }
        return sb.ToString();
    }

    private List<GridLength> ParseGridLengths(string val)
    {
        var list = new List<GridLength>();
        if (string.IsNullOrEmpty(val)) return list;
        var parts = val.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(GridLength.Auto);
            }
            else if (trimmed.EndsWith("*"))
            {
                float weight = 1f;
                if (trimmed.Length > 1)
                {
                    var numPart = trimmed.Substring(0, trimmed.Length - 1);
                    if (float.TryParse(numPart, out float w))
                    {
                        weight = w;
                    }
                }
                list.Add(GridLength.Star(weight));
            }
            else
            {
                if (float.TryParse(trimmed, out float px))
                {
                    list.Add(new GridLength(px, GridUnitType.Absolute));
                }
            }
        }
        return list;
    }
}
