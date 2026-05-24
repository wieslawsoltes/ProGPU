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

public static class ComputeFxPage
{
        public static FrameworkElement Create()
        {
            var grid = new ProGPU.WinUI.Grid();
            grid.ColumnDefinitions.Add(new GridLength(280, GridUnitType.Absolute)); // Compute adjust sliders
            grid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));      // WebGPU offscreen effect canvas
    
            var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };
            var computeTitle = new RichTextBlock { Font = Program._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 5) };
            computeTitle.Inlines.Add(new Bold(new Run("WGSL Compute Accelerator")));
            leftStack.AddChild(computeTitle);
    
            var computeDesc = new RichTextBlock { Font = Program._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
            computeDesc.Inlines.Add(new Run("Adjust dynamic WGSL pixel processors running in parallel with the scene compositing passes."));
            leftStack.AddChild(computeDesc);
    
            // Sliders for compute
            var blurLabel = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
            blurLabel.Inlines.Add(new Bold(new Run($"Backdrop Blur: {Program._blurRadius:F1} px")));
            leftStack.AddChild(blurLabel);
    
            var blurSlider = new ProGPU.WinUI.Slider { Minimum = 0f, Maximum = 20f, Value = Program._blurRadius, Width = 250f, Margin = new Thickness(0, 0, 0, 15) };
            blurSlider.ValueChanged += (s, e) =>
            {
                Program._blurRadius = blurSlider.Value;
                blurLabel.Inlines.Clear();
                blurLabel.Inlines.Add(new Bold(new Run($"Backdrop Blur: {Program._blurRadius:F1} px")));
                blurLabel.Invalidate();
            };
            leftStack.AddChild(blurSlider);
    
            var shadowLabel = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
            shadowLabel.Inlines.Add(new Bold(new Run($"Shadow Radius: {Program._shadowRadius:F1} px")));
            leftStack.AddChild(shadowLabel);
    
            var shadowSlider = new ProGPU.WinUI.Slider { Minimum = 0f, Maximum = 20f, Value = Program._shadowRadius, Width = 250f, Margin = new Thickness(0, 0, 0, 15) };
            shadowSlider.ValueChanged += (s, e) =>
            {
                Program._shadowRadius = shadowSlider.Value;
                shadowLabel.Inlines.Clear();
                shadowLabel.Inlines.Add(new Bold(new Run($"Shadow Radius: {Program._shadowRadius:F1} px")));
                shadowLabel.Invalidate();
            };
            leftStack.AddChild(shadowSlider);
    
            var offsetXLabel = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
            offsetXLabel.Inlines.Add(new Bold(new Run($"Shadow Offset X: {Program._shadowOffset.X:F1} px")));
            leftStack.AddChild(offsetXLabel);
    
            var offsetXSlider = new ProGPU.WinUI.Slider { Minimum = -20f, Maximum = 20f, Value = Program._shadowOffset.X, Width = 250f, Margin = new Thickness(0, 0, 0, 15) };
            offsetXSlider.ValueChanged += (s, e) =>
            {
                Program._shadowOffset.X = offsetXSlider.Value;
                offsetXLabel.Inlines.Clear();
                offsetXLabel.Inlines.Add(new Bold(new Run($"Shadow Offset X: {Program._shadowOffset.X:F1} px")));
                offsetXLabel.Invalidate();
            };
            leftStack.AddChild(offsetXSlider);
    
            var offsetYLabel = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
            offsetYLabel.Inlines.Add(new Bold(new Run($"Shadow Offset Y: {Program._shadowOffset.Y:F1} px")));
            leftStack.AddChild(offsetYLabel);
    
            var offsetYSlider = new ProGPU.WinUI.Slider { Minimum = -20f, Maximum = 20f, Value = Program._shadowOffset.Y, Width = 250f, Margin = new Thickness(0, 0, 0, 15) };
            offsetYSlider.ValueChanged += (s, e) =>
            {
                Program._shadowOffset.Y = offsetYSlider.Value;
                offsetYLabel.Inlines.Clear();
                offsetYLabel.Inlines.Add(new Bold(new Run($"Shadow Offset Y: {Program._shadowOffset.Y:F1} px")));
                offsetYLabel.Invalidate();
            };
            leftStack.AddChild(offsetYSlider);
    
            // Toggle Cogs Animation Button
            var toggleAnimBtn = new Button { Width = 185f, Height = 34f, CornerRadius = 6f, Margin = new Thickness(0, 10, 0, 0) };
            var toggleBtnText = new RichTextBlock { Font = Program._font, FontSize = 12f };
            toggleBtnText.Inlines.Add(new Run(Program._animateGear ? "Stop Vector Rotation" : "Start Vector Rotation"));
            toggleAnimBtn.Content = toggleBtnText;
    
            toggleAnimBtn.Click += (s, e) =>
            {
                Program._animateGear = !Program._animateGear;
                toggleBtnText.Inlines.Clear();
                toggleBtnText.Inlines.Add(new Run(Program._animateGear ? "Stop Vector Rotation" : "Start Vector Rotation"));
                toggleBtnText.Invalidate();
            };
            leftStack.AddChild(toggleAnimBtn);
    
            grid.AddChild(leftStack);
            ProGPU.WinUI.Grid.SetColumn(leftStack, 0);
    
            // Center WebGPU texture offscreen render Canvas (Column 1)
            Program._gearCanvasVisual = new GearCanvasVisual(Program._font!)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
    
            var displayCanvas = new GpuTextureCanvas(Program._canvasSourceTexture!, Program._canvasShadowTexture!, Program._canvasBlurTexture!);
            
            var canvasContainer = new Border
            {
                CornerRadius = 8f,
                Background = new SolidColorBrush(0x0C0C12FF),
                BorderBrush = new SolidColorBrush(0x222230FF),
                BorderThickness = new Thickness(1f),
                Margin = new Thickness(5),
                Child = displayCanvas
            };
    
            grid.AddChild(canvasContainer);
            ProGPU.WinUI.Grid.SetColumn(canvasContainer, 1);
    
            return grid;
        }
}
