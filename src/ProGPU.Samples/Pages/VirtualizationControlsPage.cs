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
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace ProGPU.Samples;

public static class VirtualizationControlsPage
{
    public static FrameworkElement Create()
    {
        var mainGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        mainGrid.RowDefinitions.Add(new GridLength(60, GridUnitType.Absolute));   // Header
        mainGrid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));       // Tabs Content

        // Header Title
        var headerStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(20, 10, 20, 10) };
        var title = new RichTextBlock { FontSize = 22f };
        title.Inlines.Add(new Bold(new Run("ItemsControl Virtualization Panels") { Foreground = new ThemeResourceBrush("SystemAccentColor") }));
        headerStack.AddChild(title);

        var desc = new RichTextBlock { FontSize = 12f, Margin = new Thickness(0, 2, 0, 0) };
        desc.Inlines.Add(new Run("Showcases WinUI virtualizing panels coupled with ItemsControl. Rendering thousands of cells at locked 60 FPS using zero-allocation coordinate recycling.") { Foreground = new ThemeResourceBrush("TextSecondary") });
        headerStack.AddChild(desc);

        mainGrid.AddChild(headerStack);
        Grid.SetRow(headerStack, 0);

        // Tab Pivot
        var pivot = new Pivot
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(20, 0, 20, 20)
        };

        pivot.Items.Add(new PivotItem("Uniform Virtual Grid", CreateVirtualGridTab()));
        pivot.Items.Add(new PivotItem("Virtualizing Stack Panel", CreateVirtualStackTab()));

        mainGrid.AddChild(pivot);
        Grid.SetRow(pivot, 1);

        return mainGrid;
    }

    private class GridDemoItem
    {
        public int Index { get; set; }
        public string Title { get; set; } = string.Empty;
        public string IconPath { get; set; } = string.Empty;
        public Brush ColorBrush { get; set; } = new SolidColorBrush(new Vector4(0.5f, 0.5f, 0.5f, 1f));
    }

    private static FrameworkElement CreateVirtualGridTab()
    {
        var mainGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        mainGrid.RowDefinitions.Add(new GridLength(60, GridUnitType.Absolute));   // Sizing controls row
        mainGrid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));       // Grid view

        // Top Row: Sizing Controls
        var controlsStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

        var sizeLabel = new RichTextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        sizeLabel.Inlines.Add(new Bold(new Run("GRID CELL WIDTH & HEIGHT:") { FontSize = 11f, Foreground = new ThemeResourceBrush("TextSecondary") }));
        controlsStack.AddChild(sizeLabel);

        var sizeCombo = new ComboBox
        {
            PlaceholderText = "Sizing: Medium (100x110)",
            WidthConstraint = 220f,
            HeightConstraint = 32f
        };
        sizeCombo.Items.Add(new ComboBoxItem { Text = "Compact (70x80)", Tag = new Vector2(70f, 80f) });
        sizeCombo.Items.Add(new ComboBoxItem { Text = "Medium (100x110)", Tag = new Vector2(100f, 110f) });
        sizeCombo.Items.Add(new ComboBoxItem { Text = "Large (140x150)", Tag = new Vector2(140f, 150f) });
        controlsStack.AddChild(sizeCombo);
        mainGrid.AddChild(controlsStack);
        Grid.SetRow(controlsStack, 0);

        // Grid Border & Panel setup using ItemsControl
        var gridBorder = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var itemsControl = new ItemsControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var virtualGridPanel = new UniformVirtualizingGridPanel
        {
            ItemWidth = 100f,
            ItemHeight = 110f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        itemsControl.ItemsPanel = virtualGridPanel;

        sizeCombo.SelectionChanged += (s, e) =>
        {
            if (sizeCombo.SelectedItem?.Tag is Vector2 dims)
            {
                virtualGridPanel.ItemWidth = dims.X;
                virtualGridPanel.ItemHeight = dims.Y;
                virtualGridPanel.ForceRebind();
            }
        };

        // Wire container template & binder
        itemsControl.ItemTemplate = () =>
        {
            var cardBorder = new Border
            {
                CornerRadius = 6f,
                Padding = new Thickness(4),
                Background = new ThemeResourceBrush("PageBackground"),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1f),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var cardStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            cardBorder.Child = cardStack;

            var label = new RichTextBlock
            {
                FontSize = 10f,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            cardStack.AddChild(label);

            return cardBorder;
        };

        itemsControl.BindVisualCallback = (vis, itemObj, idx) =>
        {
            var card = (Border)vis;
            var info = (GridDemoItem)itemObj;

            // Dynamically scale card constraints based on panel dims
            card.WidthConstraint = virtualGridPanel.ItemWidth - 8f;
            card.HeightConstraint = virtualGridPanel.ItemHeight - 8f;

            var cardStack = (StackPanel)card.Child!;
            var label = (RichTextBlock)cardStack.Children[0];

            label.Inlines.Clear();
            label.Inlines.Add(new Bold(new Run(info.Title) { FontSize = 12f, Foreground = info.ColorBrush }));
            label.Inlines.Add(new Run($"\nIdx: #{idx}") { Foreground = new ThemeResourceBrush("TextSecondary"), FontSize = 10f });
            label.Invalidate();
        };

        // Create 1,000 demo items
        var items = new List<GridDemoItem>();
        var icons = new[]
        {
            "M 2 10 L 18 10 M 10 2 L 10 18",                  // Plus
            "M 3 17 L 17 3 M 3 3 L 17 17",                    // Close/X
            "M 2 12 L 8 18 L 20 6",                           // Check
            "M 12 2 L 2 22 L 22 22 Z M 12 8 L 12 14 M 12 18 L 12 19", // Warning/Triangle
            "M 12 2 A 10 10 0 1 0 12 22 A 10 10 0 1 0 12 2 Z M 9 12 L 15 12" // Info/Minus
        };

        var colors = new[]
        {
            new SolidColorBrush(new Vector4(0.0f, 0.47f, 0.83f, 1f)), // Blue
            new SolidColorBrush(new Vector4(0.0f, 0.62f, 0.40f, 1f)), // Green
            new SolidColorBrush(new Vector4(0.9f, 0.35f, 0.15f, 1f)), // Orange
            new SolidColorBrush(new Vector4(0.8f, 0.12f, 0.20f, 1f)), // Red
            new SolidColorBrush(new Vector4(0.5f, 0.2f, 0.6f, 1f))    // Purple
        };

        for (int i = 0; i < 1000; i++)
        {
            items.Add(new GridDemoItem
            {
                Index = i,
                Title = $"Card #{i}",
                IconPath = icons[i % icons.Length],
                ColorBrush = colors[i % colors.Length]
            });
        }

        itemsControl.ItemsSource = items;
        gridBorder.Child = itemsControl;
        mainGrid.AddChild(gridBorder);
        Grid.SetRow(gridBorder, 1);

        return mainGrid;
    }

    private class StackDemoItem
    {
        public int Index { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string GlyphSymbol { get; set; } = string.Empty;
        public Brush BadgeColor { get; set; } = new SolidColorBrush(new Vector4(0.5f, 0.5f, 0.5f, 1f));
    }

    private static FrameworkElement CreateVirtualStackTab()
    {
        var mainGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        mainGrid.RowDefinitions.Add(new GridLength(60, GridUnitType.Absolute));   // Controls row
        mainGrid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));       // Timeline viewport

        // Top Row: Sizing Controls
        var controlsStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

        var orientLabel = new RichTextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        orientLabel.Inlines.Add(new Bold(new Run("LAYOUT ORIENTATION:") { FontSize = 11f, Foreground = new ThemeResourceBrush("TextSecondary") }));
        controlsStack.AddChild(orientLabel);

        var orientBtn = new Button
        {
            HeightConstraint = 32f,
            CornerRadius = 4f,
            Background = new ThemeResourceBrush("SystemAccentColor"),
            WidthConstraint = 220f
        };
        var btnRun = new Run("Switch to Horizontal Layout") { FontSize = 12f, Foreground = new ThemeResourceBrush("TextOnAccent") };
        orientBtn.Content = new RichTextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Inlines = { new Bold(btnRun) } };
        controlsStack.AddChild(orientBtn);
        mainGrid.AddChild(controlsStack);
        Grid.SetRow(controlsStack, 0);

        var listBorder = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var itemsControl = new ItemsControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var virtualStack = new VirtualizingStackPanel
        {
            Orientation = Orientation.Vertical,
            ItemHeight = 56f,
            ItemWidth = 140f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        itemsControl.ItemsPanel = virtualStack;

        orientBtn.Click += (s, e) =>
        {
            if (virtualStack.Orientation == Orientation.Vertical)
            {
                virtualStack.Orientation = Orientation.Horizontal;
                btnRun.Text = "Switch to Vertical Layout";
            }
            else
            {
                virtualStack.Orientation = Orientation.Vertical;
                btnRun.Text = "Switch to Horizontal Layout";
            }
            virtualStack.ScrollOffset = 0f;
            virtualStack.ForceRebind();
            orientBtn.Invalidate();
        };

        // Container visual template
        itemsControl.ItemTemplate = () =>
        {
            var rowBorder = new Border
            {
                Background = new ThemeResourceBrush("PageBackground"),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1f),
                CornerRadius = 6f,
                Padding = new Thickness(12, 6, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            rowBorder.Child = stack;

            var textStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center
            };

            var textTitle = new RichTextBlock { FontSize = 12f };
            var textDesc = new RichTextBlock { FontSize = 10f, Foreground = new ThemeResourceBrush("TextSecondary"), Margin = new Thickness(0, 1, 0, 0) };

            textStack.AddChild(textTitle);
            textStack.AddChild(textDesc);
            stack.AddChild(textStack);

            return rowBorder;
        };

        itemsControl.BindVisualCallback = (vis, itemObj, idx) =>
        {
            var row = (Border)vis;
            var info = (StackDemoItem)itemObj;

            var stack = (StackPanel)row.Child!;
            var textStack = (StackPanel)stack.Children[0];
            var textTitle = (RichTextBlock)textStack.Children[0];
            var textDesc = (RichTextBlock)textStack.Children[1];

            textTitle.Inlines.Clear();
            textTitle.Inlines.Add(new Bold(new Run(info.Title) { Foreground = info.BadgeColor }));
            textTitle.Invalidate();

            textDesc.Inlines.Clear();
            textDesc.Inlines.Add(new Run(info.Description));
            textDesc.Invalidate();

            // Sizing adaptation based on vertical / horizontal orientation
            if (virtualStack.Orientation == Orientation.Vertical)
            {
                row.WidthConstraint = null; // Stretches horizontally
                row.HeightConstraint = 48f;
                stack.Orientation = Orientation.Horizontal;
            }
            else
            {
                row.WidthConstraint = 130f;
                row.HeightConstraint = null; // Stretches vertically
                stack.Orientation = Orientation.Vertical;
            }
        };

        // Create 5,000 news feed items
        var listItems = new List<StackDemoItem>();
        var titles = new[] { "System Initialized", "High Latency Warning", "WebGPU Crash Recovered", "Database Sync Active", "Garbage Collection Completed", "Settings Configured" };
        var descs = new[] { "Successfully spawned GPGPU pipeline.", "Execution threshold exceeded limit.", "WebGPU adapter context restarted.", "Re-synced index name tables.", "Cleaned active layout render caches.", "Adjusted subpixel DPI scales." };
        var colors = new[]
        {
            new SolidColorBrush(new Vector4(0.0f, 0.47f, 0.83f, 1f)),
            new SolidColorBrush(new Vector4(0.9f, 0.65f, 0.0f, 1f)),
            new SolidColorBrush(new Vector4(0.8f, 0.12f, 0.20f, 1f)),
            new SolidColorBrush(new Vector4(0.0f, 0.62f, 0.40f, 1f)),
            new SolidColorBrush(new Vector4(0.5f, 0.2f, 0.6f, 1f)),
            new SolidColorBrush(new Vector4(0.4f, 0.4f, 0.4f, 1f))
        };

        for (int i = 0; i < 5000; i++)
        {
            int r = i % titles.Length;
            listItems.Add(new StackDemoItem
            {
                Index = i,
                Title = $"{titles[r]} #{i}",
                Description = descs[r],
                BadgeColor = colors[r]
            });
        }

        itemsControl.ItemsSource = listItems;
        listBorder.Child = itemsControl;
        mainGrid.AddChild(listBorder);
        Grid.SetRow(listBorder, 1);

        return mainGrid;
    }
}
