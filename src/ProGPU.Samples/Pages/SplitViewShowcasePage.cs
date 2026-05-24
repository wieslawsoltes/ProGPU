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

public static class SplitViewShowcasePage
{
        public static FrameworkElement Create()
        {
            var grid = new ProGPU.WinUI.Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new GridLength(50, GridUnitType.Absolute));   // Header description
            grid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Main workspace
    
            var descText = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 10) };
            descText.Inlines.Add(new Bold(new Run("SplitView Responsive Layout Demonstration\n")));
            descText.Inlines.Add(new Run("Demonstrates collapsible navigation side panes with customizable display modes, positioning, and width metrics. Adjust states in real time."));
            grid.AddChild(descText);
            ProGPU.WinUI.Grid.SetRow(descText, 0);
    
            // Define SplitView
            var splitView = new SplitView
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                PaneWidth = 200f,
                CompactPaneLength = 60f,
                IsPaneOpen = true,
                DisplayMode = SplitViewDisplayMode.CompactInline
            };
    
            // 1. Pane content
            var paneBorder = new Border
            {
                Background = new SolidColorBrush(0x1F1F24FA),
                BorderBrush = new SolidColorBrush(0xFFFFFF15),
                BorderThickness = new Thickness(0, 0, 1f, 0),
                Padding = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            var paneStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };
            var paneHeader = new RichTextBlock { Font = AppState._font, FontSize = 13f, Margin = new Thickness(0, 0, 0, 10) };
            paneHeader.Inlines.Add(new Bold(new Run("Navigation Pane")));
            paneStack.AddChild(paneHeader);
    
            for (int i = 1; i <= 4; i++)
            {
                var pBtn = new Button
                {
                    Width = 180f,
                    Height = 32f,
                    CornerRadius = 4f,
                    Margin = new Thickness(0, 0, 0, 8),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                var btnLabel = new RichTextBlock { Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                btnLabel.Inlines.Add(new Run($"Pane Option {i}"));
                pBtn.Content = btnLabel;
                paneStack.AddChild(pBtn);
            }
            paneBorder.Child = paneStack;
            splitView.Pane = paneBorder;
    
            // 2. Main content of SplitView
            var contentGrid = new ProGPU.WinUI.Grid { Margin = new Thickness(12) };
            contentGrid.ColumnDefinitions.Add(new GridLength(300, GridUnitType.Absolute)); // Controls
            contentGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Preview card
    
            var ctrlStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };
            
            var ctrlTitle = new RichTextBlock { Font = AppState._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 10) };
            ctrlTitle.Inlines.Add(new Bold(new Run("SplitView Controllers")));
            ctrlStack.AddChild(ctrlTitle);
    
            // Toggle Pane Button
            var togglePaneBtn = new Button { Width = 200f, Height = 32f, CornerRadius = 6f, Margin = new Thickness(0, 0, 0, 15) };
            var toggleText = new RichTextBlock { Font = AppState._font, FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            toggleText.Inlines.Add(new Run("Collapse / Expand Pane"));
            togglePaneBtn.Content = toggleText;
            togglePaneBtn.Click += (s, e) =>
            {
                splitView.IsPaneOpen = !splitView.IsPaneOpen;
            };
            ctrlStack.AddChild(togglePaneBtn);
    
            // ComboBox for DisplayMode
            var modeLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
            modeLabel.Inlines.Add(new Run("SplitView DisplayMode:"));
            ctrlStack.AddChild(modeLabel);
    
            var modeCombo = new ComboBox { Font = AppState._font, Width = 200f, Margin = new Thickness(0, 0, 0, 15) };
            var inlineItem = new ComboBoxItem("Inline");
            var overlayItem = new ComboBoxItem("Overlay");
            var compactInlineItem = new ComboBoxItem("CompactInline");
            var compactOverlayItem = new ComboBoxItem("CompactOverlay");
            modeCombo.Items.Add(inlineItem);
            modeCombo.Items.Add(overlayItem);
            modeCombo.Items.Add(compactInlineItem);
            modeCombo.Items.Add(compactOverlayItem);
            modeCombo.SelectedItem = compactInlineItem;
            
            modeCombo.SelectionChanged += (s, e) =>
            {
                if (modeCombo.SelectedItem != null)
                {
                    splitView.DisplayMode = modeCombo.SelectedItem.Text switch
                    {
                        "Inline" => SplitViewDisplayMode.Inline,
                        "Overlay" => SplitViewDisplayMode.Overlay,
                        "CompactInline" => SplitViewDisplayMode.CompactInline,
                        "CompactOverlay" => SplitViewDisplayMode.CompactOverlay,
                        _ => SplitViewDisplayMode.Inline
                    };
                }
            };
            ctrlStack.AddChild(modeCombo);
    
            // ComboBox for Placement
            var placeLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
            placeLabel.Inlines.Add(new Run("Pane Placement:"));
            ctrlStack.AddChild(placeLabel);
    
            var placeCombo = new ComboBox { Font = AppState._font, Width = 200f, Margin = new Thickness(0, 0, 0, 15) };
            var leftItem = new ComboBoxItem("Left");
            var rightItem = new ComboBoxItem("Right");
            placeCombo.Items.Add(leftItem);
            placeCombo.Items.Add(rightItem);
            placeCombo.SelectedItem = leftItem;
    
            placeCombo.SelectionChanged += (s, e) =>
            {
                if (placeCombo.SelectedItem != null)
                {
                    splitView.PanePlacement = placeCombo.SelectedItem.Text switch
                    {
                        "Left" => PanePlacement.Left,
                        "Right" => PanePlacement.Right,
                        _ => PanePlacement.Left
                    };
                    paneBorder.BorderThickness = splitView.PanePlacement == PanePlacement.Left ? new Thickness(0, 0, 1f, 0) : new Thickness(1f, 0, 0, 0);
                }
            };
            ctrlStack.AddChild(placeCombo);
    
            contentGrid.AddChild(ctrlStack);
            ProGPU.WinUI.Grid.SetColumn(ctrlStack, 0);
    
            // Preview Card
            var previewCard = new Border
            {
                Background = new SolidColorBrush(0x1F1F24FA),
                BorderBrush = new SolidColorBrush(0xFFFFFF15),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                Padding = new Thickness(24f),
                Margin = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            var previewText = new RichTextBlock
            {
                Font = AppState._font,
                FontSize = 13f,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            previewText.Inlines.Add(new Bold(new Run("Layout Workspace Canvas\n\n")));
            previewText.Inlines.Add(new Run("Observe how the main Content area dynamically scales and translates depending on the collapsible pane's metrics and display configurations. In overlay modes, the pane hovers above this content without pushing it."));
            previewCard.Child = previewText;
            contentGrid.AddChild(previewCard);
            ProGPU.WinUI.Grid.SetColumn(previewCard, 1);
    
            splitView.Content = contentGrid;
            grid.AddChild(splitView);
            ProGPU.WinUI.Grid.SetRow(splitView, 1);
    
            return grid;
        }
}
