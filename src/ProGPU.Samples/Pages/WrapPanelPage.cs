using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;

namespace ProGPU.Samples;

public static class WrapPanelPage
{
    public static FrameworkElement Create()
    {
        var scrollViewer = new ScrollViewer
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var mainStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        scrollViewer.Content = mainStack;

        // Header Title
        var title = new RichTextBlock { Font = AppState._font, FontSize = 22f, Margin = new Thickness(0, 0, 0, 6) };
        title.Inlines.Add(new Bold(new Run("WrapPanel Control")));
        mainStack.AddChild(title);

        var desc = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 20) };
        desc.Inlines.Add(new Run("WrapPanel positions child elements sequentially from left to right or top to bottom. Elements automatically wrap to the next row or column when they hit the panel's boundary."));
        mainStack.AddChild(desc);

        // Control Panel Card
        var controlCard = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(15),
            Margin = new Thickness(0, 0, 0, 20),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var controlStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Stretch };
        
        // WrapPanel to demo
        var wrapPanel = new WrapPanel
        {
            HorizontalSpacing = 8f,
            VerticalSpacing = 8f,
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Populate initially
        var rand = new Random();
        uint[] baseColors = { 0xFF5555CC, 0x00FF88CC, 0x00E5FFCC, 0xA100FFCC, 0xFF7A40CC, 0x2E6BCCCC };
        void AddSampleItem(string text)
        {
            var itemBorder = new Border
            {
                Width = 90f,
                Height = 45f,
                Background = new SolidColorBrush(baseColors[rand.Next(baseColors.Length)]),
                CornerRadius = 6f
            };
            var itemText = new RichTextBlock
            {
                Font = AppState._font,
                FontSize = 11f,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            itemText.Inlines.Add(new Bold(new Run(text)));
            itemBorder.Child = itemText;
            wrapPanel.Children.Add(itemBorder);
        }

        for (int i = 1; i <= 10; i++)
        {
            AddSampleItem($"Item {i}");
        }

        // Toggle Orientation Button
        var toggleBtn = new Button { Width = 150f, Height = 32f, CornerRadius = 6f, Margin = new Thickness(0, 0, 15, 0) };
        var toggleTxt = new RichTextBlock { Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        toggleTxt.Inlines.Add(new Bold(new Run("Orientation: Horizontal")));
        toggleBtn.Content = toggleTxt;
        toggleBtn.Click += (s, e) =>
        {
            if (wrapPanel.Orientation == Orientation.Horizontal)
            {
                wrapPanel.Orientation = Orientation.Vertical;
                toggleTxt.Inlines.Clear();
                toggleTxt.Inlines.Add(new Bold(new Run("Orientation: Vertical")));
                wrapPanel.HeightConstraint = 250f; // Constrain height in vertical mode so it wraps
            }
            else
            {
                wrapPanel.Orientation = Orientation.Horizontal;
                toggleTxt.Inlines.Clear();
                toggleTxt.Inlines.Add(new Bold(new Run("Orientation: Horizontal")));
                wrapPanel.HeightConstraint = null; // Unconstrain height
            }
            toggleTxt.Invalidate();
        };
        controlStack.AddChild(toggleBtn);

        // Add Item Button
        int itemsCounter = 11;
        var addBtn = new Button { Width = 110f, Height = 32f, CornerRadius = 6f, Margin = new Thickness(0, 0, 20, 0) };
        var addTxt = new RichTextBlock { Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        addTxt.Inlines.Add(new Bold(new Run("Add Item")));
        addBtn.Content = addTxt;
        addBtn.Click += (s, e) =>
        {
            AddSampleItem($"Item {itemsCounter++}");
            wrapPanel.InvalidateMeasure();
            wrapPanel.Invalidate();
        };
        controlStack.AddChild(addBtn);

        // Horizontal Spacing Slider
        var horizStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 15, 0) };
        var horizLabel = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(0, 6, 8, 0) };
        horizLabel.Inlines.Add(new Run("H-Spacing: 8"));
        var horizSlider = new Slider { Minimum = 0f, Maximum = 40f, Value = 8f, WidthConstraint = 100f };
        horizSlider.ValueChanged += (s, e) =>
        {
            wrapPanel.HorizontalSpacing = horizSlider.Value;
            horizLabel.Inlines.Clear();
            horizLabel.Inlines.Add(new Run($"H-Spacing: {(int)horizSlider.Value}"));
            horizLabel.Invalidate();
        };
        horizStack.AddChild(horizLabel);
        horizStack.AddChild(horizSlider);
        controlStack.AddChild(horizStack);

        // Vertical Spacing Slider
        var vertStack = new StackPanel { Orientation = Orientation.Horizontal };
        var vertLabel = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(0, 6, 8, 0) };
        vertLabel.Inlines.Add(new Run("V-Spacing: 8"));
        var vertSlider = new Slider { Minimum = 0f, Maximum = 40f, Value = 8f, WidthConstraint = 100f };
        vertSlider.ValueChanged += (s, e) =>
        {
            wrapPanel.VerticalSpacing = vertSlider.Value;
            vertLabel.Inlines.Clear();
            vertLabel.Inlines.Add(new Run($"V-Spacing: {(int)vertSlider.Value}"));
            vertLabel.Invalidate();
        };
        vertStack.AddChild(vertLabel);
        vertStack.AddChild(vertSlider);
        controlStack.AddChild(vertStack);

        controlCard.Child = controlStack;
        mainStack.AddChild(controlCard);

        // Showcase Card containing the WrapPanel
        var wrapShowcase = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(15),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        wrapShowcase.Child = wrapPanel;
        mainStack.AddChild(wrapShowcase);

        return scrollViewer;
    }
}
