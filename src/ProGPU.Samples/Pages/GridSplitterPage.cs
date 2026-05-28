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

public static class GridSplitterPage
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
        title.Inlines.Add(new Bold(new Run("GridSplitter Control")));
        mainStack.AddChild(title);

        var desc = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 20) };
        desc.Inlines.Add(new Run("GridSplitter enables live redistribution of column or row sizes inside a parent Grid. Try clicking and dragging the split bars below. It supports star-proportional weight scaling."));
        mainStack.AddChild(desc);

        // Section A: Vertical GridSplitter (Resizing Columns)
        var columnSectionTitle = new RichTextBlock { Font = AppState._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 8) };
        columnSectionTitle.Inlines.Add(new Bold(new Run("1. Columns Resize (Vertical Separator Bar)")));
        mainStack.AddChild(columnSectionTitle);

        var colCard = new Border
        {
            Height = 200f,
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(15),
            Margin = new Thickness(0, 0, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var colGrid = new Grid();
        colGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Left col
        colGrid.ColumnDefinitions.Add(new GridLength(6f, GridUnitType.Absolute));   // Splitter col
        colGrid.ColumnDefinitions.Add(new GridLength(1.5f, GridUnitType.Star));     // Right col

        // Left Panel
        var leftPanel = new Border { Background = new SolidColorBrush(0xFF5555CC), CornerRadius = 4f };
        var leftText = new RichTextBlock { Font = AppState._font, FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        leftText.Inlines.Add(new Bold(new Run("Star 1.0 Column")));
        leftPanel.Child = leftText;
        colGrid.AddChild(leftPanel);
        Grid.SetColumn(leftPanel, 0);

        // Splitter
        var colSplitter = new GridSplitter 
        { 
            HorizontalAlignment = HorizontalAlignment.Stretch, 
            VerticalAlignment = VerticalAlignment.Stretch 
        };
        colGrid.AddChild(colSplitter);
        Grid.SetColumn(colSplitter, 1);

        // Right Panel
        var rightPanel = new Border { Background = new SolidColorBrush(0x2E6BCCCC), CornerRadius = 4f };
        var rightText = new RichTextBlock { Font = AppState._font, FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        rightText.Inlines.Add(new Bold(new Run("Star 1.5 Column")));
        rightPanel.Child = rightText;
        colGrid.AddChild(rightPanel);
        Grid.SetColumn(rightPanel, 2);

        colCard.Child = colGrid;
        mainStack.AddChild(colCard);

        // Section B: Horizontal GridSplitter (Resizing Rows)
        var rowSectionTitle = new RichTextBlock { Font = AppState._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 8) };
        rowSectionTitle.Inlines.Add(new Bold(new Run("2. Rows Resize (Horizontal Separator Bar)")));
        mainStack.AddChild(rowSectionTitle);

        var rowCard = new Border
        {
            Height = 220f,
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(15),
            Margin = new Thickness(0, 0, 0, 20),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var rowGrid = new Grid();
        rowGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Top row
        rowGrid.RowDefinitions.Add(new GridLength(6f, GridUnitType.Absolute));   // Splitter row
        rowGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Bottom row

        // Top Panel
        var topPanel = new Border { Background = new SolidColorBrush(0xFF7A40CC), CornerRadius = 4f };
        var topText = new RichTextBlock { Font = AppState._font, FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        topText.Inlines.Add(new Bold(new Run("Star 1.0 Top Row")));
        topPanel.Child = topText;
        rowGrid.AddChild(topPanel);
        Grid.SetRow(topPanel, 0);

        // Splitter
        var rowSplitter = new GridSplitter 
        { 
            HorizontalAlignment = HorizontalAlignment.Stretch, 
            VerticalAlignment = VerticalAlignment.Stretch 
        };
        rowGrid.AddChild(rowSplitter);
        Grid.SetRow(rowSplitter, 1);

        // Bottom Panel
        var bottomPanel = new Border { Background = new SolidColorBrush(0x00FF88CC), CornerRadius = 4f };
        var bottomText = new RichTextBlock { Font = AppState._font, FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        bottomText.Inlines.Add(new Bold(new Run("Star 1.0 Bottom Row")));
        bottomPanel.Child = bottomText;
        rowGrid.AddChild(bottomPanel);
        Grid.SetRow(bottomPanel, 2);

        rowCard.Child = rowGrid;
        mainStack.AddChild(rowCard);

        return scrollViewer;
    }
}
