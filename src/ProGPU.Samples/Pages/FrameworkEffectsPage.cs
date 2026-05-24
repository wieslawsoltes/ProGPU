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

public static class FrameworkEffectsPage
{
        public static FrameworkElement Create()
        {
            var grid = new ProGPU.WinUI.Grid();
            grid.ColumnDefinitions.Add(new GridLength(300, GridUnitType.Absolute)); // Adjustments panel
            grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));      // Effect Showroom
    
            // ================= LEFT: Adjustments Panel =================
            var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };
            
            var title = new RichTextBlock { Font = Program._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 5) };
            title.Inlines.Add(new Bold(new Run("Framework UI Effects Pipeline")));
            leftStack.AddChild(title);
    
            var desc = new RichTextBlock { Font = Program._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
            desc.Inlines.Add(new Run("Real-time GPU multi-pass rendering and compute filter processing applied directly to standard layout elements."));
            leftStack.AddChild(desc);
    
            // Slider for Blur Radius
            var blurLabel = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
            blurLabel.Inlines.Add(new Bold(new Run($"Glass Blur Radius: {Program._fxBlurRadius:F1} px")));
            leftStack.AddChild(blurLabel);
    
            var blurSlider = new ProGPU.WinUI.Slider { Minimum = 0f, Maximum = 20f, Value = Program._fxBlurRadius, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
            leftStack.AddChild(blurSlider);
    
            // Slider for Shadow Blur Radius
            var shadowBlurLabel = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
            shadowBlurLabel.Inlines.Add(new Bold(new Run($"Shadow Blur: {Program._fxShadowRadius:F1} px")));
            leftStack.AddChild(shadowBlurLabel);
    
            var shadowBlurSlider = new ProGPU.WinUI.Slider { Minimum = 0f, Maximum = 30f, Value = Program._fxShadowRadius, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
            leftStack.AddChild(shadowBlurSlider);
    
            // Sliders for Shadow Offset
            var offsetXLabel = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
            offsetXLabel.Inlines.Add(new Bold(new Run($"Shadow Offset X: {Program._fxShadowOffset.X:F1} px")));
            leftStack.AddChild(offsetXLabel);
    
            var offsetXSlider = new ProGPU.WinUI.Slider { Minimum = -30f, Maximum = 30f, Value = Program._fxShadowOffset.X, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
            leftStack.AddChild(offsetXSlider);
    
            var offsetYLabel = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
            offsetYLabel.Inlines.Add(new Bold(new Run($"Shadow Offset Y: {Program._fxShadowOffset.Y:F1} px")));
            leftStack.AddChild(offsetYLabel);
    
            var offsetYSlider = new ProGPU.WinUI.Slider { Minimum = -30f, Maximum = 30f, Value = Program._fxShadowOffset.Y, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
            leftStack.AddChild(offsetYSlider);
    
            // Neon Glow Presets
            var colorLabel = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 8) };
            colorLabel.Inlines.Add(new Bold(new Run("Neon Glow Theme Color:")));
            leftStack.AddChild(colorLabel);
    
            var colorButtonsStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
            
            var pinkBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(0xFF1493FF) };
            var pinkText = new RichTextBlock { Font = Program._font, FontSize = 10f }; pinkText.Inlines.Add(new Run("Pink")); pinkBtn.Content = pinkText;
            colorButtonsStack.AddChild(pinkBtn);
    
            var cyanBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(0x00BFFFFF) };
            var cyanText = new RichTextBlock { Font = Program._font, FontSize = 10f }; cyanText.Inlines.Add(new Run("Cyan")); cyanBtn.Content = cyanText;
            colorButtonsStack.AddChild(cyanBtn);
    
            var greenBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(0x32CD32FF) };
            var greenText = new RichTextBlock { Font = Program._font, FontSize = 10f }; greenText.Inlines.Add(new Run("Green")); greenBtn.Content = greenText;
            colorButtonsStack.AddChild(greenBtn);
    
            leftStack.AddChild(colorButtonsStack);
            grid.AddChild(leftStack);
            ProGPU.WinUI.Grid.SetColumn(leftStack, 0);
    
            // ================= RIGHT: Effect Showroom =================
            var showroomGrid = new ProGPU.WinUI.Grid { Margin = new Thickness(12) };
            showroomGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
            showroomGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
            showroomGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
    
            // 1. Classic Soft Drop Shadow Card
            var col1Stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10) };
            var shadowCard = new Border
            {
                Background = new SolidColorBrush(0x1a1a24ff),
                BorderBrush = new SolidColorBrush(0x3c3c4aff),
                BorderThickness = new Thickness(1.5f),
                CornerRadius = 12f,
                Padding = new Thickness(20),
                Child = col1Stack
            };
    
            var shadowCardHeader = new RichTextBlock { Font = Program._font, FontSize = 15f, Margin = new Thickness(0, 0, 0, 10) };
            shadowCardHeader.Inlines.Add(new Bold(new Run("Classic Drop Shadow")));
            col1Stack.AddChild(shadowCardHeader);
    
            var shadowCardText = new RichTextBlock { Font = Program._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
            shadowCardText.Inlines.Add(new Run("Soft, natural-looking ambient occlusion drop shadow. Perfect for floating visual cards and buttons."));
            col1Stack.AddChild(shadowCardText);
    
            var shadowCardInput = new TextBox { Font = Program._font, Text = "Interactive input with shadow", Width = 200f, Height = 32f, Margin = new Thickness(0, 0, 0, 15) };
            col1Stack.AddChild(shadowCardInput);
    
            var shadowCardBtn = new Button { Width = 150f, Height = 34f, CornerRadius = 6f, Background = new SolidColorBrush(0x0078D4FF) };
            var shadowCardBtnText = new RichTextBlock { Font = Program._font, FontSize = 11f };
            shadowCardBtnText.Inlines.Add(new Bold(new Run("Shadow Button")));
            shadowCardBtn.Content = shadowCardBtnText;
            col1Stack.AddChild(shadowCardBtn);
    
            // Apply drop shadow to classic card
            var classicShadow = new DropShadowEffect(Program._fxShadowRadius, Program._fxShadowOffset, Program._fxShadowColor);
            shadowCard.Effect = classicShadow;
    
            // Apply drop shadow to the button inside
            shadowCardBtn.Effect = new DropShadowEffect(6f, new Vector2(0f, 3f), new Vector4(0f, 0f, 0f, 0.4f));
    
            showroomGrid.AddChild(shadowCard);
            ProGPU.WinUI.Grid.SetColumn(shadowCard, 0);
    
            // 2. Neon Ambient Glow Card
            var col2Stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10) };
            var neonCard = new Border
            {
                Background = new SolidColorBrush(0x14141aff),
                BorderBrush = new SolidColorBrush(0x5c1493ff),
                BorderThickness = new Thickness(1.5f),
                CornerRadius = 12f,
                Padding = new Thickness(20),
                Child = col2Stack
            };
    
            var neonCardHeader = new RichTextBlock { Font = Program._font, FontSize = 15f, Margin = new Thickness(0, 0, 0, 10) };
            neonCardHeader.Inlines.Add(new Bold(new Run("Neon Ambient Glow")) { Foreground = new SolidColorBrush(0xFF1493FF) });
            col2Stack.AddChild(neonCardHeader);
    
            var neonCardText = new RichTextBlock { Font = Program._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
            neonCardText.Inlines.Add(new Run("Highly saturated glowing backlights. Adjust the sliders to watch the real-time bloom scatter."));
            col2Stack.AddChild(neonCardText);
    
            var neonCardInput = new TextBox { Font = Program._font, Text = "Cyan neon glow focus", Width = 200f, Height = 32f, Margin = new Thickness(0, 0, 0, 15) };
            col2Stack.AddChild(neonCardInput);
    
            var neonCardBtn = new Button { Width = 150f, Height = 34f, CornerRadius = 6f, Background = new SolidColorBrush(0xFF1493FF) };
            var neonCardBtnText = new RichTextBlock { Font = Program._font, FontSize = 11f };
            neonCardBtnText.Inlines.Add(new Bold(new Run("Neon Button")));
            neonCardBtn.Content = neonCardBtnText;
            col2Stack.AddChild(neonCardBtn);
    
            // Apply neon glow to the card
            var neonShadow = new DropShadowEffect(Program._fxShadowRadius + 5f, Vector2.Zero, Program._fxNeonColor);
            neonCard.Effect = neonShadow;
    
            // Apply neon glow to the textBox inside
            neonCardInput.Effect = new DropShadowEffect(8f, Vector2.Zero, new Vector4(0f, 0.8f, 1f, 0.85f));
    
            showroomGrid.AddChild(neonCard);
            ProGPU.WinUI.Grid.SetColumn(neonCard, 1);
    
            // 3. Frosted Glass & Backdrop Blur Card
            var col3Stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10) };
            var blurCard = new Border
            {
                Background = new SolidColorBrush(0xffffff18), // semi-transparent white
                BorderBrush = new SolidColorBrush(0xffffff30),
                BorderThickness = new Thickness(1.5f),
                CornerRadius = 12f,
                Padding = new Thickness(20),
                Child = col3Stack
            };
    
            var blurCardHeader = new RichTextBlock { Font = Program._font, FontSize = 15f, Margin = new Thickness(0, 0, 0, 10) };
            blurCardHeader.Inlines.Add(new Bold(new Run("Backdrop Glass Blur")));
            col3Stack.AddChild(blurCardHeader);
    
            var blurCardText = new RichTextBlock { Font = Program._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
            blurCardText.Inlines.Add(new Run("Premium hardware-accelerated Gaussian blur filter. Watch the layout children blur beautifully."));
            col3Stack.AddChild(blurCardText);
    
            var blurCardInput = new TextBox { Font = Program._font, Text = "Blurred input control", Width = 200f, Height = 32f, Margin = new Thickness(0, 0, 0, 15) };
            col3Stack.AddChild(blurCardInput);
    
            var blurCardBtn = new Button { Width = 150f, Height = 34f, CornerRadius = 6f, Background = new SolidColorBrush(0xFFFFFF30) };
            var blurCardBtnText = new RichTextBlock { Font = Program._font, FontSize = 11f };
            blurCardBtnText.Inlines.Add(new Bold(new Run("Blur Button")));
            blurCardBtn.Content = blurCardBtnText;
            col3Stack.AddChild(blurCardBtn);
    
            // Apply blur effect to card
            var cardBlur = new BlurEffect(Program._fxBlurRadius);
            blurCard.Effect = cardBlur;
    
            showroomGrid.AddChild(blurCard);
            ProGPU.WinUI.Grid.SetColumn(blurCard, 2);
    
            grid.AddChild(showroomGrid);
            ProGPU.WinUI.Grid.SetColumn(showroomGrid, 1);
    
            // ================= Interactivity Hookups =================
            blurSlider.ValueChanged += (s, e) =>
            {
                Program._fxBlurRadius = blurSlider.Value;
                blurLabel.Inlines.Clear();
                blurLabel.Inlines.Add(new Bold(new Run($"Glass Blur Radius: {Program._fxBlurRadius:F1} px")));
                blurLabel.Invalidate();
                
                cardBlur.BlurRadius = Program._fxBlurRadius;
                blurCard.Invalidate();
            };
    
            shadowBlurSlider.ValueChanged += (s, e) =>
            {
                Program._fxShadowRadius = shadowBlurSlider.Value;
                shadowBlurLabel.Inlines.Clear();
                shadowBlurLabel.Inlines.Add(new Bold(new Run($"Shadow Blur: {Program._fxShadowRadius:F1} px")));
                shadowBlurLabel.Invalidate();
    
                classicShadow.BlurRadius = Program._fxShadowRadius;
                neonShadow.BlurRadius = Program._fxShadowRadius + 5f;
                shadowCard.Invalidate();
                neonCard.Invalidate();
            };
    
            offsetXSlider.ValueChanged += (s, e) =>
            {
                Program._fxShadowOffset.X = offsetXSlider.Value;
                offsetXLabel.Inlines.Clear();
                offsetXLabel.Inlines.Add(new Bold(new Run($"Shadow Offset X: {Program._fxShadowOffset.X:F1} px")));
                offsetXLabel.Invalidate();
    
                classicShadow.Offset = Program._fxShadowOffset;
                shadowCard.Invalidate();
            };
    
            offsetYSlider.ValueChanged += (s, e) =>
            {
                Program._fxShadowOffset.Y = offsetYSlider.Value;
                offsetYLabel.Inlines.Clear();
                offsetYLabel.Inlines.Add(new Bold(new Run($"Shadow Offset Y: {Program._fxShadowOffset.Y:F1} px")));
                offsetYLabel.Invalidate();
    
                classicShadow.Offset = Program._fxShadowOffset;
                shadowCard.Invalidate();
            };
    
            pinkBtn.Click += (s, e) =>
            {
                Program._fxNeonColor = new Vector4(0.85f, 0.08f, 0.52f, 0.8f);
                neonShadow.Color = Program._fxNeonColor;
                neonCard.Invalidate();
            };
    
            cyanBtn.Click += (s, e) =>
            {
                Program._fxNeonColor = new Vector4(0.0f, 0.75f, 1.0f, 0.8f);
                neonShadow.Color = Program._fxNeonColor;
                neonCard.Invalidate();
            };
    
            greenBtn.Click += (s, e) =>
            {
                Program._fxNeonColor = new Vector4(0.2f, 0.8f, 0.2f, 0.8f);
                neonShadow.Color = Program._fxNeonColor;
                neonCard.Invalidate();
            };
    
            return grid;
        }
}
