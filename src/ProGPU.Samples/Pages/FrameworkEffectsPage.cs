using Thickness = Microsoft.UI.Xaml.Thickness;
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
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Button = Microsoft.UI.Xaml.Controls.Button;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;

namespace ProGPU.Samples;

public static class FrameworkEffectsPage
{
        public static FrameworkElement Create()
        {
            // ================= LEFT: Adjustments Panel =================
            var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };
            
            var title = new RichTextBlock { Font = AppState._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 5) };
            title.Inlines.Add(new Bold(new Run("Framework UI Effects Pipeline")));
            leftStack.AddChild(title);
    
            var desc = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
            desc.Inlines.Add(new Run("Real-time GPU multi-pass rendering and compute filter processing applied directly to standard layout elements."));
            leftStack.AddChild(desc);
    
            // Slider for Blur Radius
            var blurLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
            blurLabel.Inlines.Add(new Bold(new Run($"Glass Blur Radius: {AppState._fxBlurRadius:F1} px")));
            leftStack.AddChild(blurLabel);
    
            var blurSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0f, Maximum = 20f, Value = AppState._fxBlurRadius, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
            leftStack.AddChild(blurSlider);
    
            // Slider for Shadow Blur Radius
            var shadowBlurLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
            shadowBlurLabel.Inlines.Add(new Bold(new Run($"Shadow Blur: {AppState._fxShadowRadius:F1} px")));
            leftStack.AddChild(shadowBlurLabel);
    
            var shadowBlurSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0f, Maximum = 30f, Value = AppState._fxShadowRadius, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
            leftStack.AddChild(shadowBlurSlider);
    
            // Sliders for Shadow Offset
            var offsetXLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
            offsetXLabel.Inlines.Add(new Bold(new Run($"Shadow Offset X: {AppState._fxShadowOffset.X:F1} px")));
            leftStack.AddChild(offsetXLabel);
    
            var offsetXSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = -30f, Maximum = 30f, Value = AppState._fxShadowOffset.X, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
            leftStack.AddChild(offsetXSlider);
    
            var offsetYLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
            offsetYLabel.Inlines.Add(new Bold(new Run($"Shadow Offset Y: {AppState._fxShadowOffset.Y:F1} px")));
            leftStack.AddChild(offsetYLabel);
    
            var offsetYSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = -30f, Maximum = 30f, Value = AppState._fxShadowOffset.Y, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
            leftStack.AddChild(offsetYSlider);
    
            // Neon Glow Presets
            var colorLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 8) };
            colorLabel.Inlines.Add(new Bold(new Run("Neon Glow Theme Color:")));
            leftStack.AddChild(colorLabel);
    
            var colorButtonsStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
            
            var pinkBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(0xFF1493FF) };
            var pinkText = new RichTextBlock { Font = AppState._font, FontSize = 10f }; pinkText.Inlines.Add(new Run("Pink")); pinkBtn.Content = pinkText;
            colorButtonsStack.AddChild(pinkBtn);
    
            var cyanBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(0x00BFFFFF) };
            var cyanText = new RichTextBlock { Font = AppState._font, FontSize = 10f }; cyanText.Inlines.Add(new Run("Cyan")); cyanBtn.Content = cyanText;
            colorButtonsStack.AddChild(cyanBtn);
    
            var greenBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(0x32CD32FF) };
            var greenText = new RichTextBlock { Font = AppState._font, FontSize = 10f }; greenText.Inlines.Add(new Run("Green")); greenBtn.Content = greenText;
            colorButtonsStack.AddChild(greenBtn);
    
            leftStack.AddChild(colorButtonsStack);
            // ================= RIGHT: Effect Showroom =================
            var showroomGrid = new Microsoft.UI.Xaml.Controls.Grid { Margin = new Thickness(12) };
            showroomGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
            showroomGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
            showroomGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
    
            // 1. Classic Soft Drop Shadow Card
            var col1Stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10) };
            var shadowCard = new Border
            {
                Background = new ThemeResourceBrush("CardBackground"),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1.5f),
                CornerRadius = 12f,
                Padding = new Thickness(20),
                Child = col1Stack
            };
    
            var shadowCardHeader = new RichTextBlock { Font = AppState._font, FontSize = 15f, Margin = new Thickness(0, 0, 0, 10) };
            shadowCardHeader.Inlines.Add(new Bold(new Run("Classic Drop Shadow")));
            col1Stack.AddChild(shadowCardHeader);
    
            var shadowCardText = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
            shadowCardText.Inlines.Add(new Run("Soft, natural-looking ambient occlusion drop shadow. Perfect for floating visual cards and buttons."));
            col1Stack.AddChild(shadowCardText);
    
            var shadowCardInput = new TextBox { Font = AppState._font, Text = "Interactive input with shadow", Width = 200f, Height = 32f, Margin = new Thickness(0, 0, 0, 15) };
            col1Stack.AddChild(shadowCardInput);
    
            var shadowCardBtn = new Button { Width = 150f, Height = 34f, CornerRadius = 6f, Background = new SolidColorBrush(0x0078D4FF) };
            var shadowCardBtnText = new RichTextBlock { Font = AppState._font, FontSize = 11f };
            shadowCardBtnText.Inlines.Add(new Bold(new Run("Shadow Button")));
            shadowCardBtn.Content = shadowCardBtnText;
            col1Stack.AddChild(shadowCardBtn);
    
            // Apply drop shadow to classic card
            var classicShadow = new DropShadowEffect(AppState._fxShadowRadius, AppState._fxShadowOffset, AppState._fxShadowColor);
            shadowCard.Effect = classicShadow;
    
            // Apply drop shadow to the button inside
            shadowCardBtn.Effect = new DropShadowEffect(6f, new Vector2(0f, 3f), new Vector4(0f, 0f, 0f, 0.4f));
    
            showroomGrid.AddChild(shadowCard);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(shadowCard, 0);
    
            // 2. Neon Ambient Glow Card
            var col2Stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10) };
            var neonCard = new Border
            {
                Background = new ThemeResourceBrush("CardBackground"),
                BorderBrush = new SolidColorBrush(0x5c1493ff),
                BorderThickness = new Thickness(1.5f),
                CornerRadius = 12f,
                Padding = new Thickness(20),
                Child = col2Stack
            };
    
            var neonCardHeader = new RichTextBlock { Font = AppState._font, FontSize = 15f, Margin = new Thickness(0, 0, 0, 10) };
            neonCardHeader.Inlines.Add(new Bold(new Run("Neon Ambient Glow")) { Foreground = new SolidColorBrush(0xFF1493FF) });
            col2Stack.AddChild(neonCardHeader);
    
            var neonCardText = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
            neonCardText.Inlines.Add(new Run("Highly saturated glowing backlights. Adjust the sliders to watch the real-time bloom scatter."));
            col2Stack.AddChild(neonCardText);
    
            var neonCardInput = new TextBox { Font = AppState._font, Text = "Cyan neon glow focus", Width = 200f, Height = 32f, Margin = new Thickness(0, 0, 0, 15) };
            col2Stack.AddChild(neonCardInput);
    
            var neonCardBtn = new Button { Width = 150f, Height = 34f, CornerRadius = 6f, Background = new SolidColorBrush(0xFF1493FF) };
            var neonCardBtnText = new RichTextBlock { Font = AppState._font, FontSize = 11f };
            neonCardBtnText.Inlines.Add(new Bold(new Run("Neon Button")));
            neonCardBtn.Content = neonCardBtnText;
            col2Stack.AddChild(neonCardBtn);
    
            // Apply neon glow to the card
            var neonShadow = new DropShadowEffect(AppState._fxShadowRadius + 5f, Vector2.Zero, AppState._fxNeonColor);
            neonCard.Effect = neonShadow;
    
            // Apply neon glow to the textBox inside
            neonCardInput.Effect = new DropShadowEffect(8f, Vector2.Zero, new Vector4(0f, 0.8f, 1f, 0.85f));
    
            showroomGrid.AddChild(neonCard);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(neonCard, 1);
    
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
    
            var blurCardHeader = new RichTextBlock { Font = AppState._font, FontSize = 15f, Margin = new Thickness(0, 0, 0, 10) };
            blurCardHeader.Inlines.Add(new Bold(new Run("Backdrop Glass Blur")));
            col3Stack.AddChild(blurCardHeader);
    
            var blurCardText = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
            blurCardText.Inlines.Add(new Run("Premium hardware-accelerated Gaussian blur filter. Watch the layout children blur beautifully."));
            col3Stack.AddChild(blurCardText);
    
            var blurCardInput = new TextBox { Font = AppState._font, Text = "Blurred input control", Width = 200f, Height = 32f, Margin = new Thickness(0, 0, 0, 15) };
            col3Stack.AddChild(blurCardInput);
    
            var blurCardBtn = new Button { Width = 150f, Height = 34f, CornerRadius = 6f, Background = new SolidColorBrush(0xFFFFFF30) };
            var blurCardBtnText = new RichTextBlock { Font = AppState._font, FontSize = 11f };
            blurCardBtnText.Inlines.Add(new Bold(new Run("Blur Button")));
            blurCardBtn.Content = blurCardBtnText;
            col3Stack.AddChild(blurCardBtn);
    
            // Apply blur effect to card
            var cardBlur = new BlurEffect(AppState._fxBlurRadius);
            blurCard.Effect = cardBlur;
    
            showroomGrid.AddChild(blurCard);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(blurCard, 2);
    
            // ================= Interactivity Hookups =================
            blurSlider.ValueChanged += (s, e) =>
            {
                AppState._fxBlurRadius = blurSlider.Value;
                blurLabel.Inlines.Clear();
                blurLabel.Inlines.Add(new Bold(new Run($"Glass Blur Radius: {AppState._fxBlurRadius:F1} px")));
                blurLabel.Invalidate();
                
                cardBlur.BlurRadius = AppState._fxBlurRadius;
                blurCard.Invalidate();
            };
    
            shadowBlurSlider.ValueChanged += (s, e) =>
            {
                AppState._fxShadowRadius = shadowBlurSlider.Value;
                shadowBlurLabel.Inlines.Clear();
                shadowBlurLabel.Inlines.Add(new Bold(new Run($"Shadow Blur: {AppState._fxShadowRadius:F1} px")));
                shadowBlurLabel.Invalidate();
    
                classicShadow.BlurRadius = AppState._fxShadowRadius;
                neonShadow.BlurRadius = AppState._fxShadowRadius + 5f;
                shadowCard.Invalidate();
                neonCard.Invalidate();
            };
    
            offsetXSlider.ValueChanged += (s, e) =>
            {
                AppState._fxShadowOffset.X = offsetXSlider.Value;
                offsetXLabel.Inlines.Clear();
                offsetXLabel.Inlines.Add(new Bold(new Run($"Shadow Offset X: {AppState._fxShadowOffset.X:F1} px")));
                offsetXLabel.Invalidate();
    
                classicShadow.Offset = AppState._fxShadowOffset;
                shadowCard.Invalidate();
            };
    
            offsetYSlider.ValueChanged += (s, e) =>
            {
                AppState._fxShadowOffset.Y = offsetYSlider.Value;
                offsetYLabel.Inlines.Clear();
                offsetYLabel.Inlines.Add(new Bold(new Run($"Shadow Offset Y: {AppState._fxShadowOffset.Y:F1} px")));
                offsetYLabel.Invalidate();
    
                classicShadow.Offset = AppState._fxShadowOffset;
                shadowCard.Invalidate();
            };
    
            pinkBtn.Click += (s, e) =>
            {
                AppState._fxNeonColor = new Vector4(0.85f, 0.08f, 0.52f, 0.8f);
                neonShadow.Color = AppState._fxNeonColor;
                neonCard.Invalidate();
            };
    
            cyanBtn.Click += (s, e) =>
            {
                AppState._fxNeonColor = new Vector4(0.0f, 0.75f, 1.0f, 0.8f);
                neonShadow.Color = AppState._fxNeonColor;
                neonCard.Invalidate();
            };
    
            greenBtn.Click += (s, e) =>
            {
                AppState._fxNeonColor = new Vector4(0.2f, 0.8f, 0.2f, 0.8f);
                neonShadow.Color = AppState._fxNeonColor;
                neonCard.Invalidate();
            };
    
            return new ResponsiveSplitView
            {
                OpenPaneLength = 300f,
                PaneContent = leftStack,
                MainContent = showroomGrid
            };
        }
}
