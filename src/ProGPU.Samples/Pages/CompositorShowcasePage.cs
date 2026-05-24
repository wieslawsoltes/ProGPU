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

public static class CompositorShowcasePage
{
        public static FrameworkElement Create()
        {
            var grid = new ProGPU.WinUI.Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new GridLength(50, GridUnitType.Absolute));   // Header description
            grid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Columns
    
            var descText = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 10) };
            descText.Inlines.Add(new Bold(new Run("Composition Subsystem & Multi-Column Document Nesting\n")));
            descText.Inlines.Add(new Run("This page showcases CPU-tessellated multi-stop gradients, dynamic clipping masks, real-time spring transformations, and interactive UI controls seamlessly embedded inline using the FlowDocument InlineUIContainer pipeline."));
            grid.AddChild(descText);
            ProGPU.WinUI.Grid.SetRow(descText, 0);
    
            var columnsGrid = new ProGPU.WinUI.Grid();
            columnsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
            columnsGrid.ColumnDefinitions.Add(new GridLength(1.2f, GridUnitType.Star));
    
            // COLUMN 0: COMPOSITION & GRADIENT ART
            var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6) };
            
            var artCard = new Border
            {
                Background = new SolidColorBrush(0x1F1F24FA),
                BorderBrush = new SolidColorBrush(0xFFFFFF15),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                Padding = new Thickness(16f),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var artStack = new StackPanel { Orientation = Orientation.Vertical };
            var artHeader = new RichTextBlock { Font = Program._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 8) };
            artHeader.Inlines.Add(new Bold(new Run("High-Performance Tessellated Vector Gradients")));
            artStack.AddChild(artHeader);
    
            var artVisual = new Program.GradientArtVisual();
            artStack.AddChild(artVisual);
            artCard.Child = artStack;
            leftStack.AddChild(artCard);
    
            // Spring transform controller card
            var springCard = new Border
            {
                Background = new SolidColorBrush(0x1F1F24FA),
                BorderBrush = new SolidColorBrush(0xFFFFFF15),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                Padding = new Thickness(16f)
            };
            var springStack = new StackPanel { Orientation = Orientation.Vertical };
            var springHeader = new RichTextBlock { Font = Program._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 8) };
            springHeader.Inlines.Add(new Bold(new Run("Spring & Matrix Composition Transformation")));
            springStack.AddChild(springHeader);
    
            var springWidget = new Program.SpringInteractiveCardWidget(Program._font!);
            springStack.AddChild(springWidget);
            springCard.Child = springStack;
            leftStack.AddChild(springCard);
    
            columnsGrid.AddChild(leftStack);
            ProGPU.WinUI.Grid.SetColumn(leftStack, 0);
    
            // COLUMN 1: INTERACTIVE FLOW DOCUMENT WITH INLINE WIDGETS
            var rightStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6) };
            
            var docCard = new Border
            {
                Background = new SolidColorBrush(0x1F1F24FA),
                BorderBrush = new SolidColorBrush(0xFFFFFF15),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                Padding = new Thickness(16f),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            var docStack = new StackPanel { Orientation = Orientation.Vertical };
            var docHeader = new RichTextBlock { Font = Program._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 8) };
            docHeader.Inlines.Add(new Bold(new Run("FlowDocument Interactive Nesting")));
            docStack.AddChild(docHeader);
    
            var flowDoc = new FlowDocument
            {
                Font = Program._font,
                FontSize = 11.5f,
                ColumnCount = 2,
                ColumnGap = 16f,
                Height = 440f,
                Foreground = new SolidColorBrush(0xDDDDDDFF)
            };
    
            // Embedded Controls definitions
            var embedBtn = new Button { Width = 80f, Height = 22f, CornerRadius = 4f, Margin = new Thickness(0) };
            embedBtn.Content = new RichTextBlock { Font = Program._font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            ((RichTextBlock)embedBtn.Content).Inlines.Add(new Run("Click Me!"));
            embedBtn.Click += (s, e) => {
                System.Console.WriteLine("Embedded Button Clicked!");
            };
    
            var embedToggle = new ToggleSwitch { Margin = new Thickness(0), Width = 45f, Height = 20f };
            var embedProgress = new ProgressBar { Minimum = 0f, Maximum = 100f, Value = 65f, Width = 80f, Height = 12f, CornerRadius = 3f };
    
            var p1 = new Paragraph(
                new Bold(new Run("Inline UI Embedding:\n")),
                new Run("We can embed framework elements directly inside the text streams. For example, a fully functional button: "),
                new InlineUIContainer(embedBtn),
                new Run(" or a live toggle switch control: "),
                new InlineUIContainer(embedToggle),
                new Run(" that layout, measure, wrap, and arrange seamlessly.")
            ) { MarginBottom = 10f, TextAlignment = TextAlignment.Justify };
    
            var p2 = new Paragraph(
                new Bold(new Run("Document Links & Stats:\n")),
                new Run("This document also flows live progress bars: "),
                new InlineUIContainer(embedProgress),
                new Run(" inline alongside styled runs. Try selecting text, or interact with elements directly! Links can also be clicked, e.g. "),
                new Hyperlink(new Bold(new Run("ProGPU Website"))) { Uri = "https://github.com/wieslawsoltes/ProGPU" },
                new Run(" to visit the repository or trigger routed event bubbles.")
            ) { MarginBottom = 10f, TextAlignment = TextAlignment.Justify };
    
            flowDoc.Paragraphs.Add(p1);
            flowDoc.Paragraphs.Add(p2);
    
            var docBorder = new Border
            {
                Background = new SolidColorBrush(0x0C0C12FF),
                BorderBrush = new SolidColorBrush(0xFFFFFF15),
                BorderThickness = new Thickness(1f),
                CornerRadius = 6f,
                Child = flowDoc
            };
            docStack.AddChild(docBorder);
            docCard.Child = docStack;
            rightStack.AddChild(docCard);
    
            columnsGrid.AddChild(rightStack);
            ProGPU.WinUI.Grid.SetColumn(rightStack, 1);
    
            grid.AddChild(columnsGrid);
            ProGPU.WinUI.Grid.SetRow(columnsGrid, 1);
    
            return grid;
        }
}
