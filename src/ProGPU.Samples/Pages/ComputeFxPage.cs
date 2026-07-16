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

public static class ComputeFxPage
{
        public static FrameworkElement Create()
        {
            MainWindowController.EnsureEffectResources();

            var grid = new Microsoft.UI.Xaml.Controls.Grid();
            grid.ColumnDefinitions.Add(new GridLength(280, GridUnitType.Absolute)); // Compute adjust sliders
            grid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));      // WebGPU offscreen effect canvas
    
            var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };
            var computeTitle = new RichTextBlock { Font = AppState._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 5) };
            computeTitle.Inlines.Add(new Bold(new Run("WGSL Compute Accelerator")));
            leftStack.AddChild(computeTitle);
    
            var computeDesc = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
            computeDesc.Inlines.Add(new Run("Adjust dynamic WGSL pixel processors running in parallel with the scene compositing passes."));
            leftStack.AddChild(computeDesc);
    
            // Sliders for compute
            var blurLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
            blurLabel.Inlines.Add(new Bold(new Run($"Backdrop Blur: {AppState._blurRadius:F1} px")));
            leftStack.AddChild(blurLabel);
    
            var blurSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0f, Maximum = 20f, Value = AppState._blurRadius, Width = 250f, Margin = new Thickness(0, 0, 0, 15) };
            blurSlider.ValueChanged += (s, e) =>
            {
                AppState._blurRadius = blurSlider.Value;
                blurLabel.Inlines.Clear();
                blurLabel.Inlines.Add(new Bold(new Run($"Backdrop Blur: {AppState._blurRadius:F1} px")));
                blurLabel.Invalidate();
            };
            leftStack.AddChild(blurSlider);
    
            var shadowLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
            shadowLabel.Inlines.Add(new Bold(new Run($"Shadow Radius: {AppState._shadowRadius:F1} px")));
            leftStack.AddChild(shadowLabel);
    
            var shadowSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0f, Maximum = 20f, Value = AppState._shadowRadius, Width = 250f, Margin = new Thickness(0, 0, 0, 15) };
            shadowSlider.ValueChanged += (s, e) =>
            {
                AppState._shadowRadius = shadowSlider.Value;
                shadowLabel.Inlines.Clear();
                shadowLabel.Inlines.Add(new Bold(new Run($"Shadow Radius: {AppState._shadowRadius:F1} px")));
                shadowLabel.Invalidate();
            };
            leftStack.AddChild(shadowSlider);
    
            var offsetXLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
            offsetXLabel.Inlines.Add(new Bold(new Run($"Shadow Offset X: {AppState._shadowOffset.X:F1} px")));
            leftStack.AddChild(offsetXLabel);
    
            var offsetXSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = -20f, Maximum = 20f, Value = AppState._shadowOffset.X, Width = 250f, Margin = new Thickness(0, 0, 0, 15) };
            offsetXSlider.ValueChanged += (s, e) =>
            {
                AppState._shadowOffset.X = offsetXSlider.Value;
                offsetXLabel.Inlines.Clear();
                offsetXLabel.Inlines.Add(new Bold(new Run($"Shadow Offset X: {AppState._shadowOffset.X:F1} px")));
                offsetXLabel.Invalidate();
            };
            leftStack.AddChild(offsetXSlider);
    
            var offsetYLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
            offsetYLabel.Inlines.Add(new Bold(new Run($"Shadow Offset Y: {AppState._shadowOffset.Y:F1} px")));
            leftStack.AddChild(offsetYLabel);
    
            var offsetYSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = -20f, Maximum = 20f, Value = AppState._shadowOffset.Y, Width = 250f, Margin = new Thickness(0, 0, 0, 15) };
            offsetYSlider.ValueChanged += (s, e) =>
            {
                AppState._shadowOffset.Y = offsetYSlider.Value;
                offsetYLabel.Inlines.Clear();
                offsetYLabel.Inlines.Add(new Bold(new Run($"Shadow Offset Y: {AppState._shadowOffset.Y:F1} px")));
                offsetYLabel.Invalidate();
            };
            leftStack.AddChild(offsetYSlider);
    
            // Toggle Cogs Animation Button
            var toggleAnimBtn = new Button { Width = 185f, Height = 34f, CornerRadius = 6f, Margin = new Thickness(0, 10, 0, 0) };
            var toggleBtnText = new RichTextBlock { Font = AppState._font, FontSize = 12f };
            toggleBtnText.Inlines.Add(new Run(AppState._animateGear ? "Stop Vector Rotation" : "Start Vector Rotation"));
            toggleAnimBtn.Content = toggleBtnText;
    
            toggleAnimBtn.Click += (s, e) =>
            {
                AppState._animateGear = !AppState._animateGear;
                toggleBtnText.Inlines.Clear();
                toggleBtnText.Inlines.Add(new Run(AppState._animateGear ? "Stop Vector Rotation" : "Start Vector Rotation"));
                toggleBtnText.Invalidate();
            };
            leftStack.AddChild(toggleAnimBtn);
    
            grid.AddChild(leftStack);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(leftStack, 0);
    
            // Center WebGPU texture offscreen render Canvas (Column 1)
            AppState._gearCanvasVisual ??= new GearCanvasVisual(AppState._font!)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
    
            var displayCanvas = new GpuTextureCanvas(AppState._canvasSourceTexture!, AppState._canvasShadowTexture!, AppState._canvasBlurTexture!);
            
            var canvasContainer = new Border
            {
                CornerRadius = 8f,
                Background = new SolidColorBrush(0x0C0C12FF),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1f),
                Margin = new Thickness(5),
                Child = displayCanvas
            };
    
            grid.AddChild(canvasContainer);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(canvasContainer, 1);
    
            return grid;
        }
}
