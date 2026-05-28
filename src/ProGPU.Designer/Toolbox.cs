namespace ProGPU.Designer;

using System;
using System.Collections.Generic;
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

public class ToolboxItem : Border
{
    private readonly string _controlName;
    private readonly string _displayName;
    private readonly ProGPU.Text.TtfFont? _font;

    public string ControlName => _controlName;
    public string DisplayName => _displayName;

    public ToolboxItem(string controlName, string displayName, string icon, ProGPU.Text.TtfFont? font)
    {
        _controlName = controlName;
        _displayName = displayName;
        _font = font;

        Margin = new Thickness(4);
        Padding = new Thickness(12, 8, 12, 8);
        CornerRadius = 6f;
        Background = new ThemeResourceBrush("ControlBackground");
        BorderBrush = new ThemeResourceBrush("ControlBorder");
        BorderThickness = new Thickness(1f);
        HorizontalAlignment = HorizontalAlignment.Stretch;

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        
        var iconText = new RichTextBlock
        {
            Font = font,
            FontSize = 14f,
            Foreground = new ThemeResourceBrush("SystemAccentColor"),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        iconText.Inlines.Add(new Run(icon));
        panel.AddChild(iconText);

        var nameText = new RichTextBlock
        {
            Font = font,
            FontSize = 12f,
            Foreground = new ThemeResourceBrush("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        nameText.Inlines.Add(new Run(displayName));
        panel.AddChild(nameText);

        Child = panel;
    }

    public override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        Background = new ThemeResourceBrush("ControlBackgroundHover");
        BorderBrush = new ThemeResourceBrush("ControlBorderHover");
        base.OnPointerEntered(e);
    }

    public override void OnPointerExited(PointerRoutedEventArgs e)
    {
        if (!DragDropManager.IsDragging)
        {
            Background = new ThemeResourceBrush("ControlBackground");
            BorderBrush = new ThemeResourceBrush("ControlBorder");
        }
        base.OnPointerExited(e);
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (e.IsLeftButtonPressed)
        {
            var dp = new DataPackage();
            dp.SetData(StandardDataFormats.Tool, _controlName);
            
            FrameworkElement? dragVisual = null;
            
            // Try to instantiate actual control for visual preview
            try
            {
                Type? controlType = null;
                string[] searchNamespaces = {
                    "Microsoft.UI.Xaml.Controls",
                    "Microsoft.UI.Xaml",
                    "ProGPU.Designer"
                };

                foreach (var ns in searchNamespaces)
                {
                    var typeName = $"{ns}.{_controlName}";
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        controlType = assembly.GetType(typeName);
                        if (controlType != null) break;
                    }
                    if (controlType != null) break;
                }

                if (controlType != null && typeof(FrameworkElement).IsAssignableFrom(controlType))
                {
                    dragVisual = Activator.CreateInstance(controlType) as FrameworkElement;
                }
            }
            catch {}

            if (dragVisual != null)
            {
                dragVisual.Width = 120f;
                dragVisual.Height = 36f;
                dragVisual.Opacity = 0.7f;
                dragVisual.IsHitTestVisible = false;

                if (dragVisual is Button button)
                {
                    var richText = new RichTextBlock { Font = _font ?? PopupService.DefaultFont };
                    richText.Inlines.Add(new Run(_controlName));
                    button.Content = richText;
                }
                else if (dragVisual is TextBlock textBlock)
                {
                    textBlock.Text = _controlName;
                }
                else if (dragVisual is CheckBox checkBox)
                {
                    var richText = new RichTextBlock { Font = _font ?? PopupService.DefaultFont };
                    richText.Inlines.Add(new Run(_controlName));
                    checkBox.Content = richText;
                }
                else if (dragVisual is RadioButton radioButton)
                {
                    var richText = new RichTextBlock { Font = _font ?? PopupService.DefaultFont };
                    richText.Inlines.Add(new Run(_controlName));
                    radioButton.Content = richText;
                }
                else if (dragVisual is ToggleSwitch toggleSwitch)
                {
                    var richText = new RichTextBlock { Font = _font ?? PopupService.DefaultFont };
                    richText.Inlines.Add(new Run(_controlName));
                    toggleSwitch.Content = richText;
                }
                else if (dragVisual is ComboBox comboBox)
                {
                    comboBox.PlaceholderText = _controlName;
                }
            }
            else
            {
                // Fallback to standard sleek Border visual
                dragVisual = new Border
                {
                    Width = 140f,
                    Height = 36f,
                    Background = new ThemeResourceBrush("SystemAccentColor"),
                    BorderBrush = new ThemeResourceBrush("ControlBorder"),
                    BorderThickness = new Thickness(1f),
                    CornerRadius = 6f,
                    Opacity = 0.75f,
                    Padding = new Thickness(10, 8, 10, 8)
                };
                
                var visualText = new RichTextBlock
                {
                    Font = _font,
                    FontSize = 12f,
                    Foreground = new ThemeResourceBrush("TextPrimary"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                visualText.Inlines.Add(new Run(_controlName));
                ((Border)dragVisual).Child = visualText;
            }

            DragDropManager.StartDrag(this, dp, DragDropEffects.Copy, dragVisual);
            e.Handled = true;
        }
        base.OnPointerPressed(e);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (DragDropManager.IsDragging)
        {
            Background = new ThemeResourceBrush("ControlBackground");
            BorderBrush = new ThemeResourceBrush("ControlBorder");
        }
        base.OnPointerReleased(e);
    }
}

public class Toolbox : Border
{
    private readonly StackPanel _listPanel;
    private readonly ScrollViewer _scrollViewer;
    private readonly List<ToolboxItem> _allItems = new();

    public Toolbox(ProGPU.Text.TtfFont? font)
    {
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

        var headerPanel = new StackPanel
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
        titleText.Inlines.Add(new Bold(new Run("Toolbox")));
        headerPanel.AddChild(titleText);

        var searchBox = new TextBox
        {
            PlaceholderText = "Search...",
            WidthConstraint = 228f,
            HeightConstraint = 28f,
            Margin = new Thickness(4, 0, 4, 12),
            Font = font
        };
        headerPanel.AddChild(searchBox);

        mainGrid.AddChild(headerPanel);
        Grid.SetRow(headerPanel, 0);

        _listPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var controls = new (string Name, string DisplayName, string Icon)[]
        {
            // Controls (16)
            ("Button", "Button", "🔘"),
            ("TextBox", "TextBox", "📝"),
            ("TextBlock", "TextBlock", "🔤"),
            ("ComboBox", "ComboBox", "🔽"),
            ("Slider", "Slider", "🎛️"),
            ("CheckBox", "CheckBox", "☑️"),
            ("RadioButton", "RadioButton", "🔘"),
            ("ProgressBar", "ProgressBar", "📊"),
            ("ProgressRing", "ProgressRing", "🔄"),
            ("RatingControl", "RatingControl", "⭐"),
            ("ToggleSwitch", "ToggleSwitch", "🎚️"),
            ("CalendarView", "CalendarView", "📅"),
            ("DatePicker", "DatePicker", "📆"),
            ("PasswordBox", "PasswordBox", "🔒"),
            ("TreeView", "TreeView", "🌲"),
            ("DataGrid", "DataGrid", "🔢"),
            
            // Panels (6)
            ("StackPanel", "StackPanel", "🥞"),
            ("Grid", "Grid", "🌐"),
            ("Canvas", "Canvas", "🎨"),
            ("Border", "Border", "🔲"),
            ("ScrollViewer", "ScrollViewer", "📜"),
            ("SplitView", "SplitView", "📖")
        };

        foreach (var ctrl in controls)
        {
            var item = new ToolboxItem(ctrl.Name, ctrl.DisplayName, ctrl.Icon, font);
            _allItems.Add(item);
            _listPanel.AddChild(item);
        }

        searchBox.TextChanged += (s, e) =>
        {
            string query = searchBox.Text.Trim();
            
            // Remove all item children
            while (_listPanel.Children.Count > 0)
            {
                _listPanel.RemoveChild(_listPanel.Children[0]);
            }
            
            // Re-add matching items
            foreach (var item in _allItems)
            {
                if (string.IsNullOrEmpty(query) || 
                    item.ControlName.Contains(query, StringComparison.OrdinalIgnoreCase) || 
                    item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    _listPanel.AddChild(item);
                }
            }
            
            // Reset vertical offset to ensure results are visible and not scrolled out of view
            _scrollViewer.VerticalOffset = 0f;
            
            _listPanel.InvalidateMeasure();
            _listPanel.InvalidateArrange();
            _listPanel.Invalidate();
        };

        _scrollViewer = new ScrollViewer
        {
            Content = _listPanel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        mainGrid.AddChild(_scrollViewer);
        Grid.SetRow(_scrollViewer, 1);

        Child = mainGrid;
    }
}
