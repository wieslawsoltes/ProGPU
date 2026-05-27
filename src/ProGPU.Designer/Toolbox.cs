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
    private readonly ProGPU.Text.TtfFont? _font;

    public string ControlName => _controlName;

    public ToolboxItem(string controlName, string displayName, string icon, ProGPU.Text.TtfFont? font)
    {
        _controlName = controlName;
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
            dp.Properties["ToolType"] = _controlName;
            
            var dragVisual = new Border
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
            dragVisual.Child = visualText;

            DragDropManager.StartDrag(this, dp, dragVisual);
            e.Handled = true;
        }
        base.OnPointerPressed(e);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (DragDropManager.IsDragging)
        {
            DragDropManager.EndDrag();
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

    public Toolbox(ProGPU.Text.TtfFont? font)
    {
        Background = new ThemeResourceBrush("CardBackground");
        BorderBrush = new ThemeResourceBrush("ControlBorder");
        BorderThickness = new Thickness(1f);
        CornerRadius = 8f;
        Padding = new Thickness(8);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _listPanel = new StackPanel
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
        _listPanel.AddChild(titleText);

        var controls = new (string Name, string DisplayName, string Icon)[]
        {
            ("Button", "Button", "🔘"),
            ("TextBox", "TextBox", "📝"),
            ("TextBlock", "TextBlock", "🔤"),
            ("ComboBox", "ComboBox", "🔽"),
            ("Slider", "Slider", "🎛️"),
            ("CheckBox", "CheckBox", "☑️"),
            ("RadioButton", "RadioButton", "🔘"),
            ("Border", "Border", "🔲"),
            ("StackPanel", "StackPanel", "🥞"),
            ("Grid", "Grid", "🌐")
        };

        foreach (var ctrl in controls)
        {
            var item = new ToolboxItem(ctrl.Name, ctrl.DisplayName, ctrl.Icon, font);
            _listPanel.AddChild(item);
        }

        _scrollViewer = new ScrollViewer
        {
            Content = _listPanel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        Child = _scrollViewer;
    }
}
