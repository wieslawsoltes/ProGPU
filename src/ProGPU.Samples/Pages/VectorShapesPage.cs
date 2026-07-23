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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;

namespace ProGPU.Samples;

public static class VectorShapesPage
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
        title.Inlines.Add(new Bold(new Run("Hardware Vector Shapes & Transforms")));
        mainStack.AddChild(title);

        var desc = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 24) };
        desc.Inlines.Add(new Run("Exquisite hardware-accelerated vector shapes built on high-performance compute rasterizers with full support for nested dynamic geometric transforms. Real-time CPU-SIMD coordinate mapping eliminates low-level drawing state push/pop costs."));
        mainStack.AddChild(desc);

        // Grid split layout
        var splitGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        splitGrid.ColumnDefinitions.Add(new GridLength(1.1f, GridUnitType.Star));     // Left: Controls
        splitGrid.ColumnDefinitions.Add(new GridLength(20f, GridUnitType.Absolute));  // Gap
        splitGrid.ColumnDefinitions.Add(new GridLength(1.5f, GridUnitType.Star));     // Right: Viewport

        // --- Column 0: Interactive Transform Controls Panel ---
        var controlsCard = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };

        var controlsStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };
        
        var panelTitle = new RichTextBlock { Font = AppState._font, FontSize = 15f, Margin = new Thickness(0, 0, 0, 16) };
        panelTitle.Inlines.Add(new Bold(new Run("Transform Parameters")));
        controlsStack.AddChild(panelTitle);

        // 1. Rotation Slider
        var rotLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 4, 0, 4) };
        rotLabel.Inlines.Add(new Run("Star Rotation: "));
        var rotValText = new Run("0°");
        rotLabel.Inlines.Add(new Bold(rotValText));
        controlsStack.AddChild(rotLabel);

        var rotSlider = new Microsoft.UI.Xaml.Controls.Slider
        {
            Minimum = 0f,
            Maximum = 360f,
            Value = 0f,
            Margin = new Thickness(0, 0, 0, 16)
        };
        controlsStack.AddChild(rotSlider);

        // 2. Skew Slider
        var skewLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 4, 0, 4) };
        skewLabel.Inlines.Add(new Run("Rectangle Skew X: "));
        var skewValText = new Run("0°");
        skewLabel.Inlines.Add(new Bold(skewValText));
        controlsStack.AddChild(skewLabel);

        var skewSlider = new Microsoft.UI.Xaml.Controls.Slider
        {
            Minimum = -45f,
            Maximum = 45f,
            Value = 0f,
            Margin = new Thickness(0, 0, 0, 16)
        };
        controlsStack.AddChild(skewSlider);

        // 3. Scale Slider
        var scaleLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 4, 0, 4) };
        scaleLabel.Inlines.Add(new Run("Uniform Geometry Scale: "));
        var scaleValText = new Run("1.0x");
        scaleLabel.Inlines.Add(new Bold(scaleValText));
        controlsStack.AddChild(scaleLabel);

        var scaleSlider = new Microsoft.UI.Xaml.Controls.Slider
        {
            Minimum = 0.5f,
            Maximum = 2.0f,
            Value = 1.0f,
            Margin = new Thickness(0, 0, 0, 16)
        };
        controlsStack.AddChild(scaleSlider);

        // 4. Stroke Thickness Slider
        var strokeLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 4, 0, 4) };
        strokeLabel.Inlines.Add(new Run("Stroke Thickness: "));
        var strokeValText = new Run("3.0px");
        strokeLabel.Inlines.Add(new Bold(strokeValText));
        controlsStack.AddChild(strokeLabel);

        var strokeSlider = new Microsoft.UI.Xaml.Controls.Slider
        {
            Minimum = 0f,
            Maximum = 12f,
            Value = 3.0f,
            Margin = new Thickness(0, 0, 0, 8)
        };
        controlsStack.AddChild(strokeSlider);

        controlsCard.Child = controlsStack;
        splitGrid.AddChild(controlsCard);
        Grid.SetColumn(controlsCard, 0);

        // --- Column 2: The Viewport Canvas Showcase ---
        var viewportCard = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var viewportCanvas = new Canvas
        {
            Width = 420f,
            Height = 360f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 1. Line
        var line = new Line
        {
            X1 = 20f, Y1 = 20f,
            X2 = 400f, Y2 = 20f,
            Stroke = new SolidColorBrush(new Vector4(0.5f, 0.5f, 0.6f, 0.7f)),
            StrokeThickness = 3.0f
        };
        viewportCanvas.Children.Add(line);

        // 2. Ellipse
        var ellipse = new Ellipse
        {
            Width = 100f,
            Height = 100f,
            Fill = new SolidColorBrush(new Vector4(0.0f, 0.55f, 0.85f, 0.45f)),
            Stroke = new ThemeResourceBrush("SystemAccentColor"),
            StrokeThickness = 3.0f
        };
        Canvas.SetLeft(ellipse, 30f);
        Canvas.SetTop(ellipse, 50f);
        viewportCanvas.Children.Add(ellipse);

        // 3. Rectangle (with rounded corners)
        var rect = new Rectangle
        {
            Width = 120f,
            Height = 80f,
            RadiusX = 16f,
            RadiusY = 16f,
            Fill = new SolidColorBrush(new Vector4(0.9f, 0.28f, 0.38f, 0.45f)),
            Stroke = new SolidColorBrush(new Vector4(0.9f, 0.2f, 0.3f, 1.0f)),
            StrokeThickness = 3.0f
        };
        Canvas.SetLeft(rect, 160f);
        Canvas.SetTop(rect, 60f);
        viewportCanvas.Children.Add(rect);

        // 4. Polygon (Hexagon)
        var hex = new Polygon
        {
            Fill = new SolidColorBrush(new Vector4(0.1f, 0.75f, 0.38f, 0.4f)),
            Stroke = new SolidColorBrush(new Vector4(0.1f, 0.75f, 0.3f, 1.0f)),
            StrokeThickness = 3.0f
        };
        hex.Points.Add(new Vector2(40f, 0f));
        hex.Points.Add(new Vector2(80f, 25f));
        hex.Points.Add(new Vector2(80f, 75f));
        hex.Points.Add(new Vector2(40f, 100f));
        hex.Points.Add(new Vector2(0f, 75f));
        hex.Points.Add(new Vector2(0f, 25f));
        Canvas.SetLeft(hex, 40f);
        Canvas.SetTop(hex, 190f);
        viewportCanvas.Children.Add(hex);

        // 5. Polyline (Wave sparkline)
        var wave = new Polyline
        {
            Stroke = new SolidColorBrush(new Vector4(0.95f, 0.6f, 0.0f, 1.0f)),
            StrokeThickness = 3.0f
        };
        wave.Points.Add(new Vector2(0f, 50f));
        wave.Points.Add(new Vector2(20f, 10f));
        wave.Points.Add(new Vector2(40f, 85f));
        wave.Points.Add(new Vector2(60f, 25f));
        wave.Points.Add(new Vector2(80f, 90f));
        wave.Points.Add(new Vector2(100f, 35f));
        Canvas.SetLeft(wave, 160f);
        Canvas.SetTop(wave, 200f);
        viewportCanvas.Children.Add(wave);

        // 6. SVG Path (A stunning 5-point Star)
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Fill = new SolidColorBrush(new Vector4(0.55f, 0.2f, 0.85f, 0.4f)),
            Stroke = new SolidColorBrush(new Vector4(0.55f, 0.15f, 0.85f, 1.0f)),
            StrokeThickness = 3.0f,
            Width = 90f,
            Height = 90f
        };
        string starSvg = "M 45 0 L 57 34 L 90 34 L 63 56 L 74 90 L 45 68 L 16 90 L 27 56 L 0 34 L 33 34 Z";
        path.Data = Microsoft.UI.Xaml.Media.PathGeometry.Parse(starSvg);
        Canvas.SetLeft(path, 290f);
        Canvas.SetTop(path, 180f);
        viewportCanvas.Children.Add(path);

        // --- Setup Dynamic Transforms ---
        var ellipseScale = new ScaleTransform { ScaleX = 1f, ScaleY = 1f, CenterX = 50f, CenterY = 50f };
        ellipse.DefiningGeometry.Transform = ellipseScale;

        var rectSkew = new SkewTransform { AngleX = 0f, AngleY = 0f, CenterX = 60f, CenterY = 40f };
        rect.DefiningGeometry.Transform = rectSkew;

        var starRotate = new RotateTransform { Angle = 0f, CenterX = 45f, CenterY = 45f };
        var starScale = new ScaleTransform { ScaleX = 1f, ScaleY = 1f, CenterX = 45f, CenterY = 45f };
        var starGroup = new TransformGroup();
        starGroup.Children.Add(starRotate);
        starGroup.Children.Add(starScale);
        path.Data.Transform = starGroup;

        // --- Slider Event Bindings ---

        // Rotation
        rotSlider.ValueChanged += (s, ev) =>
        {
            float val = (float)rotSlider.Value;
            starRotate.Angle = val;
            rotValText.Text = $"{val:F0}°";
            path.Invalidate();
            rotLabel.Invalidate();
        };

        // Skew
        skewSlider.ValueChanged += (s, ev) =>
        {
            float val = (float)skewSlider.Value;
            rectSkew.AngleX = val;
            skewValText.Text = $"{val:F0}°";
            rect.Invalidate();
            skewLabel.Invalidate();
        };

        // Scale
        scaleSlider.ValueChanged += (s, ev) =>
        {
            float val = (float)scaleSlider.Value;
            ellipseScale.ScaleX = val;
            ellipseScale.ScaleY = val;
            starScale.ScaleX = val;
            starScale.ScaleY = val;
            scaleValText.Text = $"{val:F2}x";
            ellipse.Invalidate();
            path.Invalidate();
            scaleLabel.Invalidate();
        };

        // Stroke Thickness
        strokeSlider.ValueChanged += (s, ev) =>
        {
            float thick = (float)strokeSlider.Value;
            strokeValText.Text = $"{thick:F1}px";

            line.StrokeThickness = thick;
            ellipse.StrokeThickness = thick;
            rect.StrokeThickness = thick;
            hex.StrokeThickness = thick;
            wave.StrokeThickness = thick;
            path.StrokeThickness = thick;

            viewportCanvas.Invalidate();
            strokeLabel.Invalidate();
        };

        viewportCard.Child = viewportCanvas;
        splitGrid.AddChild(viewportCard);
        Grid.SetColumn(viewportCard, 2);

        mainStack.AddChild(splitGrid);

        return scrollViewer;
    }
}
