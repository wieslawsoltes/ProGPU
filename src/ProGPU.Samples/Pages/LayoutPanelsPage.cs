using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Windowing;
using Silk.NET.Input;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using ProGPU.Compute;
using ProGPU.Virtualization;
using ProGPU.WinUI;
using Button = ProGPU.WinUI.Button;
using StackPanel = ProGPU.WinUI.StackPanel;

namespace ProGPU.Samples;

public static class LayoutPanelsPage
{
        public static FrameworkElement Create()
        {
            var grid = new ProGPU.WinUI.Grid { Margin = new Thickness(10) };
            grid.RowDefinitions.Add(new GridLength(50, GridUnitType.Absolute));   // Header description
            grid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));       // Pivot tab showcase
    
            var descText = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 10) };
            descText.Inlines.Add(new Run("This page showcases standard WinUI layout panels enclosed inside a premium sliding "));
            descText.Inlines.Add(new Bold(new Run("Pivot")));
            descText.Inlines.Add(new Run(" control. Hover tabs or click to switch with smooth slide animations."));
            grid.AddChild(descText);
            ProGPU.WinUI.Grid.SetRow(descText, 0);
    
            // 1. Pivot Control
            var pivot = new Pivot
            {
                Font = Program._font,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
    
            // Tab 1: Grid & Stack Panels
            var showroomGrid = new ProGPU.WinUI.Grid();
            showroomGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
            showroomGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
    
            // Column 0: 2x2 Grid cell attachments
            var innerGrid = new ProGPU.WinUI.Grid();
            innerGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
            innerGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
            innerGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
            innerGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
    
            var card1 = new Border { Margin = new Thickness(4), Background = new SolidColorBrush(0xFF555520), CornerRadius = 6f };
            var cardText1 = new RichTextBlock { Font = Program._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            cardText1.Inlines.Add(new Run("Cell (0, 0)"));
            card1.Child = cardText1;
            innerGrid.AddChild(card1);
            ProGPU.WinUI.Grid.SetRow(card1, 0);
            ProGPU.WinUI.Grid.SetColumn(card1, 0);
    
            var card2 = new Border { Margin = new Thickness(4), Background = new SolidColorBrush(0x00FF8820), CornerRadius = 6f };
            var cardText2 = new RichTextBlock { Font = Program._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            cardText2.Inlines.Add(new Run("Cell (0, 1)"));
            card2.Child = cardText2;
            innerGrid.AddChild(card2);
            ProGPU.WinUI.Grid.SetRow(card2, 0);
            ProGPU.WinUI.Grid.SetColumn(card2, 1);
    
            var card3 = new Border { Margin = new Thickness(4), Background = new SolidColorBrush(0x00E5FF20), CornerRadius = 6f };
            var cardText3 = new RichTextBlock { Font = Program._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            cardText3.Inlines.Add(new Run("Cell (1, 0)"));
            card3.Child = cardText3;
            innerGrid.AddChild(card3);
            ProGPU.WinUI.Grid.SetRow(card3, 1);
            ProGPU.WinUI.Grid.SetColumn(card3, 0);
    
            var card4 = new Border { Margin = new Thickness(4), Background = new SolidColorBrush(0xA100FF20), CornerRadius = 6f };
            var cardText4 = new RichTextBlock { Font = Program._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            cardText4.Inlines.Add(new Run("Cell (1, 1)"));
            card4.Child = cardText4;
            innerGrid.AddChild(card4);
            ProGPU.WinUI.Grid.SetRow(card4, 1);
            ProGPU.WinUI.Grid.SetColumn(card4, 1);
    
            var leftGroup = new Border
            {
                Margin = new Thickness(5),
                Background = new SolidColorBrush(0xFFFFFF08),
                BorderBrush = new SolidColorBrush(0xFFFFFF15),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                Padding = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            leftGroup.Child = innerGrid;
            showroomGrid.AddChild(leftGroup);
            ProGPU.WinUI.Grid.SetColumn(leftGroup, 0);
    
            // Column 1: StackPanel layout
            var rightStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };
            var stackTitle = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(8, 0, 0, 8) };
            stackTitle.Inlines.Add(new Bold(new Run("Vertical Stack Panel")));
            rightStack.AddChild(stackTitle);
    
            for (int i = 1; i <= 3; i++)
            {
                var item = new Border
                {
                    Height = 32f,
                    Margin = new Thickness(4),
                    Background = new SolidColorBrush(0xFFFFFF15),
                    CornerRadius = 4f,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                var itemText = new RichTextBlock { Font = Program._font, FontSize = 11f, Margin = new Thickness(10, 8, 0, 0) };
                itemText.Inlines.Add(new Run($"Stack Item #{i}"));
                item.Child = itemText;
                rightStack.AddChild(item);
            }
    
            var horizontalStackTitle = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(8, 12, 0, 8) };
            horizontalStackTitle.Inlines.Add(new Bold(new Run("Horizontal Flow Row")));
            rightStack.AddChild(horizontalStackTitle);
    
            var horzFlow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Stretch };
            for (int i = 1; i <= 3; i++)
            {
                var item = new Border
                {
                    Width = 72f,
                    Height = 28f,
                    Margin = new Thickness(4),
                    Background = new SolidColorBrush(0x00E5FF25),
                    CornerRadius = 4f
                };
                var itemText = new RichTextBlock { Font = Program._font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                itemText.Inlines.Add(new Run($"Flow #{i}"));
                item.Child = itemText;
                horzFlow.AddChild(item);
            }
            rightStack.AddChild(horzFlow);
    
            var rightGroup = new Border
            {
                Margin = new Thickness(5),
                Background = new SolidColorBrush(0xFFFFFF08),
                BorderBrush = new SolidColorBrush(0xFFFFFF15),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                Padding = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            rightGroup.Child = rightStack;
            showroomGrid.AddChild(rightGroup);
            ProGPU.WinUI.Grid.SetColumn(rightGroup, 1);
    
            var pivotItem1 = new PivotItem("Recursive Grids & Stacks", showroomGrid);
            pivot.Items.Add(pivotItem1);
    
            // Tab 2: Canvas Absolute Layout
            var canvasPanel = new Canvas { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            var canvasDesc = new RichTextBlock { Font = Program._font, FontSize = 11f, Margin = new Thickness(8, 4, 0, 0) };
            canvasDesc.Inlines.Add(new Bold(new Run("Absolute Canvas Coordinates:")));
            canvasPanel.AddChild(canvasDesc);
            Canvas.SetLeft(canvasDesc, 8f);
            Canvas.SetTop(canvasDesc, 4f);
    
            // Renders overlapping absolute positioned panels
            var cardColors = new uint[] { 0xFF5555CC, 0x00FF88CC, 0x00E5FFCC };
            for (int i = 0; i < 3; i++)
            {
                var overlappingCard = new Border
                {
                    Width = 160f,
                    Height = 60f,
                    Background = new SolidColorBrush(cardColors[i]),
                    CornerRadius = 6f
                };
                var overlappingText = new RichTextBlock { Font = Program._font, FontSize = 11f, Margin = new Thickness(12, 20, 0, 0) };
                overlappingText.Inlines.Add(new Bold(new Run($"Absolute Panel #{i + 1}")));
                overlappingCard.Child = overlappingText;
                
                canvasPanel.AddChild(overlappingCard);
                Canvas.SetLeft(overlappingCard, 50f + i * 110f);
                Canvas.SetTop(overlappingCard, 45f + i * 25f);
            }
    
            var canvasGroup = new Border
            {
                Margin = new Thickness(5),
                Background = new SolidColorBrush(0xFFFFFF08),
                BorderBrush = new SolidColorBrush(0xFFFFFF15),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            canvasGroup.Child = canvasPanel;
    
            var pivotItem2 = new PivotItem("Absolute Canvas Positions", canvasGroup);
            pivot.Items.Add(pivotItem2);
    
            // Tab 3: TabView Control
            var tabViewContainer = new TabView
            {
                Font = Program._font,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
    
            // Create standard default tabs
            var tabItem1 = new TabViewItem("Home Tab")
            {
                Content = new Border
                {
                    Background = new SolidColorBrush(0x13131AFF),
                    CornerRadius = 8f,
                    Padding = new Thickness(20),
                    Child = new RichTextBlock
                    {
                        Font = Program._font,
                        FontSize = 14f,
                        Inlines = { new Bold(new Run("Welcome to your TabView Home Page!\n\n")), new Run("This TabView supports adding new tabs by clicking the '+' button on the right, and closing existing ones using the 'x' close buttons.") }
                    }
                }
            };
    
            var tabItem2 = new TabViewItem("Analytics")
            {
                Content = new Border
                {
                    Background = new SolidColorBrush(0x0C0C12FF),
                    CornerRadius = 8f,
                    Padding = new Thickness(20),
                    Child = new RichTextBlock
                    {
                        Font = Program._font,
                        FontSize = 14f,
                        Inlines = { new Bold(new Run("Real-Time Graphics Analytics Data\n\n")), new Run("WebGL/WebGPU performance is locked at a stable 60 FPS under massive parallel draw call buffers.") }
                    }
                }
            };
    
            tabViewContainer.TabItems.Add(tabItem1);
            tabViewContainer.TabItems.Add(tabItem2);
    
            int nextTabId = 3;
            tabViewContainer.TabAddRequested += (s, e) =>
            {
                var newTab = new TabViewItem($"New Tab #{nextTabId}")
                {
                    Content = new Border
                    {
                        Background = new SolidColorBrush(0x13131AFF),
                        CornerRadius = 8f,
                        Padding = new Thickness(20),
                        Child = new RichTextBlock
                        {
                            Font = Program._font,
                            FontSize = 14f,
                            Inlines = { new Bold(new Run($"Active Dynamic Tab Room #{nextTabId}\n\n")), new Run("TabView leverages viewport virtualization logic to dynamically balance graphics render loads.") }
                        }
                    }
                };
                nextTabId++;
                tabViewContainer.TabItems.Add(newTab);
                tabViewContainer.SelectedItem = newTab;
            };
    
            var pivotItem3 = new PivotItem("TabView Dynamic Pages", tabViewContainer);
            pivot.Items.Add(pivotItem3);
    
            grid.AddChild(pivot);
            ProGPU.WinUI.Grid.SetRow(pivot, 1);
    
            return grid;
        }
}
