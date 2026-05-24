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

public static class ImageRepeatShowcasePage
{
        public static FrameworkElement Create()
        {
            var grid = new ProGPU.WinUI.Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new GridLength(50, GridUnitType.Absolute));   // Header description
            grid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Main content Grid
    
            var descText = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 10) };
            descText.Inlines.Add(new Bold(new Run("Image Stretching & Button Extensions Showcase\n")));
            descText.Inlines.Add(new Run("Exhibits the uncompressed BMP local loader supporting None, Fill, Uniform, UniformToFill stretch structures, together with high-fidelity RepeatButtons and HyperlinkButtons."));
            grid.AddChild(descText);
            ProGPU.WinUI.Grid.SetRow(descText, 0);
    
            var contentGrid = new ProGPU.WinUI.Grid();
            contentGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star)); // Left column: Image Stretch
            contentGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star)); // Right column: Buttons
    
            // COLUMN 0: IMAGE STRETCH CARD
            var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6) };
            var imgCard = new Border
            {
                Background = new SolidColorBrush(0x1F1F24FA),
                BorderBrush = new SolidColorBrush(0xFFFFFF15),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                Padding = new Thickness(16f),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var imgStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };
            var imgHeader = new RichTextBlock { Font = Program._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 10) };
            imgHeader.Inlines.Add(new Bold(new Run("Pure C# BMP Rendering & Stretching")));
            imgStack.AddChild(imgHeader);
    
            // Instantiate Image control
            var testImage = new Image
            {
                Width = 300f,
                Height = 200f,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 0, 15)
            };
    
            // Pass Program._canvasSourceTexture as the fallback source texture
            testImage.Source = Program._canvasSourceTexture;
    
            imgStack.AddChild(testImage);
    
            // ComboBox for Stretch Mode
            var stretchLabel = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
            stretchLabel.Inlines.Add(new Run("Image Stretch Mode:"));
            imgStack.AddChild(stretchLabel);
    
            var stretchCombo = new ComboBox { Font = Program._font, Width = 200f, Margin = new Thickness(0, 0, 0, 10) };
            var noneItem = new ComboBoxItem("None");
            var fillItem = new ComboBoxItem("Fill");
            var uniformItem = new ComboBoxItem("Uniform");
            var uniformToFillItem = new ComboBoxItem("UniformToFill");
            stretchCombo.Items.Add(noneItem);
            stretchCombo.Items.Add(fillItem);
            stretchCombo.Items.Add(uniformItem);
            stretchCombo.Items.Add(uniformToFillItem);
            stretchCombo.SelectedItem = uniformItem;
    
            stretchCombo.SelectionChanged += (s, e) =>
            {
                if (stretchCombo.SelectedItem != null)
                {
                    testImage.Stretch = stretchCombo.SelectedItem.Text switch
                    {
                        "None" => Stretch.None,
                        "Fill" => Stretch.Fill,
                        "Uniform" => Stretch.Uniform,
                        "UniformToFill" => Stretch.UniformToFill,
                        _ => Stretch.Uniform
                    };
                }
            };
            imgStack.AddChild(stretchCombo);
            
            imgCard.Child = imgStack;
            leftStack.AddChild(imgCard);
            contentGrid.AddChild(leftStack);
            ProGPU.WinUI.Grid.SetColumn(leftStack, 0);
    
            // COLUMN 1: EXTENSION BUTTONS CARD
            var rightStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6) };
            var btnCard = new Border
            {
                Background = new SolidColorBrush(0x1F1F24FA),
                BorderBrush = new SolidColorBrush(0xFFFFFF15),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                Padding = new Thickness(16f),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var btnStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };
            var btnHeader = new RichTextBlock { Font = Program._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 10) };
            btnHeader.Inlines.Add(new Bold(new Run("Interactive Button Extensions")));
            btnStack.AddChild(btnHeader);
    
            // RepeatButton demonstration
            var repeatLabel = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 4) };
            repeatLabel.Inlines.Add(new Bold(new Run($"Hold button counter: {Program._repeatCount}")));
            btnStack.AddChild(repeatLabel);
    
            var repeatBtnStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 20) };
            
            var decBtn = new RepeatButton { Width = 80f, Height = 32f, CornerRadius = 4f, Margin = new Thickness(0, 0, 8, 0) };
            var decLabel = new RichTextBlock { Font = Program._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            decLabel.Inlines.Add(new Run("- Decrement"));
            decBtn.Content = decLabel;
            decBtn.Click += (s, e) =>
            {
                Program._repeatCount--;
                repeatLabel.Inlines.Clear();
                repeatLabel.Inlines.Add(new Bold(new Run($"Hold button counter: {Program._repeatCount}")));
                repeatLabel.Invalidate();
            };
            repeatBtnStack.AddChild(decBtn);
    
            var incBtn = new RepeatButton { Width = 80f, Height = 32f, CornerRadius = 4f };
            var incLabel = new RichTextBlock { Font = Program._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            incLabel.Inlines.Add(new Run("+ Increment"));
            incBtn.Content = incLabel;
            incBtn.Click += (s, e) =>
            {
                Program._repeatCount++;
                repeatLabel.Inlines.Clear();
                repeatLabel.Inlines.Add(new Bold(new Run($"Hold button counter: {Program._repeatCount}")));
                repeatLabel.Invalidate();
            };
            repeatBtnStack.AddChild(incBtn);
            btnStack.AddChild(repeatBtnStack);
    
            // HyperlinkButton demonstration
            var linkLabel = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 4) };
            linkLabel.Inlines.Add(new Bold(new Run("Hyperlink Button Hover & Click:")));
            btnStack.AddChild(linkLabel);
    
            var hyperBtn = new HyperlinkButton { Height = 28f, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
            var hyperText = new RichTextBlock { Font = Program._font, FontSize = 12f, Foreground = ThemeManager.GetBrush("SystemAccentColor") };
            hyperText.Inlines.Add(new Run("Visit ProGPU cross-platform github hub"));
            hyperBtn.Content = hyperText;
    
            var clickFeedback = new RichTextBlock { Font = Program._font, FontSize = 11f, Foreground = new SolidColorBrush(0x00E5FF25) };
            clickFeedback.Inlines.Add(new Run(""));
            
            hyperBtn.Click += (s, e) =>
            {
                clickFeedback.Inlines.Clear();
                clickFeedback.Inlines.Add(new Run("Routed hyperlink event triggered successfully!"));
                clickFeedback.Invalidate();
            };
    
            btnStack.AddChild(hyperBtn);
            btnStack.AddChild(clickFeedback);
    
            btnCard.Child = btnStack;
            rightStack.AddChild(btnCard);
            contentGrid.AddChild(rightStack);
            ProGPU.WinUI.Grid.SetColumn(rightStack, 1);
    
                grid.AddChild(contentGrid);
            ProGPU.WinUI.Grid.SetRow(contentGrid, 1);
    
            return grid;
        }
}
