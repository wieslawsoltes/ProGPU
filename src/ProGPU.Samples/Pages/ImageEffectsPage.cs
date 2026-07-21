using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using Image = Microsoft.UI.Xaml.Controls.Image;

namespace ProGPU.Samples;

public static class ImageEffectsPage
{
    public static FrameworkElement Create()
    {
        MainWindowController.EnsureEffectResources();

        var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };
        
        var title = new RichTextBlock { Font = AppState._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 5) };
        title.Inlines.Add(new Bold(new Run("GPU Image Effects & Shaders")));
        leftStack.AddChild(title);

        var desc = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
        desc.Inlines.Add(new Run("Modify dynamic WGSL fragment shader parameters to process live image streams in real time."));
        leftStack.AddChild(desc);

        // Instantiated Image Control for the preview
        var previewImage = new Image
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Stretch = Stretch.Uniform,
            Source = AppState._canvasSourceTexture
        };

        // Sliders config
        // 1. Brightness
        var brightnessLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        brightnessLabel.Inlines.Add(new Run($"Brightness: {previewImage.Brightness * 100f:F0}%"));
        leftStack.AddChild(brightnessLabel);

        var brightnessSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = -1f, Maximum = 1f, Value = previewImage.Brightness, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        brightnessSlider.ValueChanged += (s, e) =>
        {
            previewImage.Brightness = brightnessSlider.Value;
            brightnessLabel.Inlines.Clear();
            brightnessLabel.Inlines.Add(new Run($"Brightness: {previewImage.Brightness * 100f:F0}%"));
            brightnessLabel.Invalidate();
        };
        leftStack.AddChild(brightnessSlider);

        // 2. Contrast
        var contrastLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        contrastLabel.Inlines.Add(new Run($"Contrast: {previewImage.Contrast * 100f:F0}%"));
        leftStack.AddChild(contrastLabel);

        var contrastSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0f, Maximum = 2f, Value = previewImage.Contrast, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        contrastSlider.ValueChanged += (s, e) =>
        {
            previewImage.Contrast = contrastSlider.Value;
            contrastLabel.Inlines.Clear();
            contrastLabel.Inlines.Add(new Run($"Contrast: {previewImage.Contrast * 100f:F0}%"));
            contrastLabel.Invalidate();
        };
        leftStack.AddChild(contrastSlider);

        // 3. Saturation
        var saturationLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        saturationLabel.Inlines.Add(new Run($"Saturation: {previewImage.Saturation * 100f:F0}%"));
        leftStack.AddChild(saturationLabel);

        var saturationSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0f, Maximum = 2f, Value = previewImage.Saturation, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        saturationSlider.ValueChanged += (s, e) =>
        {
            previewImage.Saturation = saturationSlider.Value;
            saturationLabel.Inlines.Clear();
            saturationLabel.Inlines.Add(new Run($"Saturation: {previewImage.Saturation * 100f:F0}%"));
            saturationLabel.Invalidate();
        };
        leftStack.AddChild(saturationSlider);

        // 4. Grayscale
        var grayscaleLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        grayscaleLabel.Inlines.Add(new Run($"Grayscale: {previewImage.Grayscale * 100f:F0}%"));
        leftStack.AddChild(grayscaleLabel);

        var grayscaleSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0f, Maximum = 1f, Value = previewImage.Grayscale, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        grayscaleSlider.ValueChanged += (s, e) =>
        {
            previewImage.Grayscale = grayscaleSlider.Value;
            grayscaleLabel.Inlines.Clear();
            grayscaleLabel.Inlines.Add(new Run($"Grayscale: {previewImage.Grayscale * 100f:F0}%"));
            grayscaleLabel.Invalidate();
        };
        leftStack.AddChild(grayscaleSlider);

        // 5. Sepia
        var sepiaLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        sepiaLabel.Inlines.Add(new Run($"Sepia: {previewImage.Sepia * 100f:F0}%"));
        leftStack.AddChild(sepiaLabel);

        var sepiaSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0f, Maximum = 1f, Value = previewImage.Sepia, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        sepiaSlider.ValueChanged += (s, e) =>
        {
            previewImage.Sepia = sepiaSlider.Value;
            sepiaLabel.Inlines.Clear();
            sepiaLabel.Inlines.Add(new Run($"Sepia: {previewImage.Sepia * 100f:F0}%"));
            sepiaLabel.Invalidate();
        };
        leftStack.AddChild(sepiaSlider);

        // 6. Invert
        var invertLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        invertLabel.Inlines.Add(new Run($"Invert: {previewImage.Invert * 100f:F0}%"));
        leftStack.AddChild(invertLabel);

        var invertSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0f, Maximum = 1f, Value = previewImage.Invert, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        invertSlider.ValueChanged += (s, e) =>
        {
            previewImage.Invert = invertSlider.Value;
            invertLabel.Inlines.Clear();
            invertLabel.Inlines.Add(new Run($"Invert: {previewImage.Invert * 100f:F0}%"));
            invertLabel.Invalidate();
        };
        leftStack.AddChild(invertSlider);

        // 7. Blur Sigma
        var blurLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        blurLabel.Inlines.Add(new Run($"Blur Sigma: {previewImage.BlurSigma:F1}"));
        leftStack.AddChild(blurLabel);

        var blurSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0f, Maximum = 8f, Value = previewImage.BlurSigma, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        blurSlider.ValueChanged += (s, e) =>
        {
            previewImage.BlurSigma = blurSlider.Value;
            blurLabel.Inlines.Clear();
            blurLabel.Inlines.Add(new Run($"Blur Sigma: {previewImage.BlurSigma:F1}"));
            blurLabel.Invalidate();
        };
        leftStack.AddChild(blurSlider);

        // Right Preview Area (Column 1)
        var previewContainer = new Border
        {
            CornerRadius = 8f,
            Background = new SolidColorBrush(0x0C0C12FF),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            Margin = new Thickness(12),
            Child = previewImage
        };

        return new ResponsiveSplitView
        {
            OpenPaneLength = 300f,
            PaneContent = leftStack,
            MainContent = previewContainer
        };
    }
}
