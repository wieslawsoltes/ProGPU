using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using Border = Microsoft.UI.Xaml.Controls.Border;
using Button = Microsoft.UI.Xaml.Controls.Button;
using System.Windows;
using System.Windows.Media;

namespace ProGPU.Samples;

public class WpfShowcaseVisual : FrameworkElement
{
    public float TranslationX { get; set; } = 0f;
    public float TranslationY { get; set; } = 0f;
    public float RotationAngle { get; set; } = 0f;
    public float ScaleX { get; set; } = 1.0f;
    public float ScaleY { get; set; } = 1.0f;
    public float PenWidth { get; set; } = 2f;
    public float OpacityValue { get; set; } = 1.0f;
    public System.Windows.Media.Color DrawingColor { get; set; } = System.Windows.Media.Color.FromArgb(255, 0, 120, 212); // Segoe Blue

    public WpfShowcaseVisual()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HeightConstraint = 450f;
    }

    public override void OnRender(ProGPU.Scene.DrawingContext nativeContext)
    {
        // 1. Outer background (using native ProGPU.Vector and ProGPU.Scene structures)
        nativeContext.DrawRectangle(
            new ProGPU.Vector.SolidColorBrush(new Vector4(0.07f, 0.07f, 0.09f, 1f)),
            new ProGPU.Vector.Pen(new ProGPU.Vector.SolidColorBrush(new Vector4(0.2f, 0.2f, 0.25f, 1f)), 1f),
            new ProGPU.Scene.Rect(0, 0, Size.X, Size.Y)
        );

        // 2. Wrap ProGPU context with WPF DrawingContext shim
        using (var dc = new System.Windows.Media.DrawingContext(nativeContext))
        {
            var matrix = System.Windows.Media.Matrix.Identity;
            matrix.Translate(Size.X / 2.0 + TranslationX, Size.Y / 2.0 + TranslationY);
            matrix.Rotate(RotationAngle);
            matrix.Scale(ScaleX, ScaleX); // Uniform scaling

            dc.PushTransform(new System.Windows.Media.MatrixTransform(matrix));
            dc.PushOpacity(OpacityValue);

            var fillBrush = new System.Windows.Media.SolidColorBrush(DrawingColor);
            var borderPen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.Gold, PenWidth);

            // Draw grid pattern
            var gridPen = new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 255, 255, 255)), 1.0);
            for (int i = -200; i <= 200; i += 40)
            {
                dc.DrawLine(gridPen, new System.Windows.Point(i, -200), new System.Windows.Point(i, 200));
                dc.DrawLine(gridPen, new System.Windows.Point(-200, i), new System.Windows.Point(200, i));
            }

            // Draw coordinate axes
            var xAxisPen = new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 255, 80, 80)), 1.5);
            var yAxisPen = new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 80, 255, 80)), 1.5);
            dc.DrawLine(xAxisPen, new System.Windows.Point(-220, 0), new System.Windows.Point(220, 0));
            dc.DrawLine(yAxisPen, new System.Windows.Point(0, -220), new System.Windows.Point(0, 220));

            // Draw WPF rect outline
            dc.DrawRectangle(null, new System.Windows.Media.Pen(fillBrush, PenWidth), new System.Windows.Rect(-120, -120, 240, 240));

            // Draw WPF filled rounded rect
            dc.DrawRoundedRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(70, DrawingColor.R, DrawingColor.G, DrawingColor.B)), null, new System.Windows.Rect(-90, -90, 180, 180), 16, 16);

            // Draw WPF ellipse outline
            dc.DrawEllipse(null, borderPen, new System.Windows.Point(0, 0), 60, 60);

            // Draw WPF StreamGeometry (star shape)
            var streamGeom = new System.Windows.Media.StreamGeometry();
            using (var ctx = streamGeom.Open())
            {
                ctx.BeginFigure(new System.Windows.Point(0, -45), isFilled: true, isClosed: true);
                ctx.LineTo(new System.Windows.Point(12, -15), isStroked: true, isSmoothJoin: true);
                ctx.LineTo(new System.Windows.Point(45, -15), isStroked: true, isSmoothJoin: true);
                ctx.LineTo(new System.Windows.Point(18, 5), isStroked: true, isSmoothJoin: true);
                ctx.LineTo(new System.Windows.Point(28, 38), isStroked: true, isSmoothJoin: true);
                ctx.LineTo(new System.Windows.Point(0, 18), isStroked: true, isSmoothJoin: true);
                ctx.LineTo(new System.Windows.Point(-28, 38), isStroked: true, isSmoothJoin: true);
                ctx.LineTo(new System.Windows.Point(-18, 5), isStroked: true, isSmoothJoin: true);
                ctx.LineTo(new System.Windows.Point(-45, -15), isStroked: true, isSmoothJoin: true);
                ctx.LineTo(new System.Windows.Point(-12, -15), isStroked: true, isSmoothJoin: true);
            }
            dc.DrawGeometry(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gold), null, streamGeom);

            // Draw WPF FormattedText
            var font = new System.Windows.Media.FontFamily("Arial");
            var typeface = new System.Windows.Media.Typeface(font);
            var text = new System.Windows.Media.FormattedText(
                "WPF DrawingContext Shim (ProGPU)",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                13,
                System.Windows.Media.Brushes.White
            );
            dc.DrawText(text, new System.Windows.Point(-text.Width / 2.0, -155));

            dc.Pop();
            dc.Pop();
        }
    }
}

public static class WpfShowcasePage
{
    public static FrameworkElement Create()
    {
        var grid = new Microsoft.UI.Xaml.Controls.Grid();
        grid.ColumnDefinitions.Add(new GridLength(300, GridUnitType.Absolute)); // Left adjust pane
        grid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));      // Right Visual preview

        var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };

        var title = new RichTextBlock { Font = AppState._font, FontSize = 15f, Margin = new Thickness(0, 0, 0, 5) };
        title.Inlines.Add(new Bold(new Run("WPF DrawingContext Showcase")));
        leftStack.AddChild(title);

        var desc = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
        desc.Inlines.Add(new Run("Draws standard WPF graphics primitives. The calls are routed on the fly into high-performance zero-copy WebGPU pipeline."));
        leftStack.AddChild(desc);

        var visual = new WpfShowcaseVisual();

        // Color Selection Grid
        var colorLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 5) };
        colorLabel.Inlines.Add(new Run("Drawing Color:"));
        leftStack.AddChild(colorLabel);

        var colorGrid = new Microsoft.UI.Xaml.Controls.Grid { Margin = new Thickness(0, 0, 0, 15) };
        colorGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        colorGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        colorGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        colorGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        var cBlue = new Button { Content = new RichTextBlock { Font = AppState._font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center }, Margin = new Thickness(2) };
        ((RichTextBlock)cBlue.Content).Inlines.Add(new Run("Blue"));
        cBlue.Click += (s, e) => { visual.DrawingColor = System.Windows.Media.Color.FromArgb(255, 0, 120, 212); visual.Invalidate(); };

        var cGreen = new Button { Content = new RichTextBlock { Font = AppState._font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center }, Margin = new Thickness(2) };
        ((RichTextBlock)cGreen.Content).Inlines.Add(new Run("Green"));
        cGreen.Click += (s, e) => { visual.DrawingColor = System.Windows.Media.Color.FromArgb(255, 16, 124, 65); visual.Invalidate(); };

        var cRed = new Button { Content = new RichTextBlock { Font = AppState._font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center }, Margin = new Thickness(2) };
        ((RichTextBlock)cRed.Content).Inlines.Add(new Run("Red"));
        cRed.Click += (s, e) => { visual.DrawingColor = System.Windows.Media.Color.FromArgb(255, 220, 60, 60); visual.Invalidate(); };

        var cOrange = new Button { Content = new RichTextBlock { Font = AppState._font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center }, Margin = new Thickness(2) };
        ((RichTextBlock)cOrange.Content).Inlines.Add(new Run("Orange"));
        cOrange.Click += (s, e) => { visual.DrawingColor = System.Windows.Media.Color.FromArgb(255, 247, 99, 12); visual.Invalidate(); };

        colorGrid.AddChild(cBlue); Microsoft.UI.Xaml.Controls.Grid.SetColumn(cBlue, 0);
        colorGrid.AddChild(cGreen); Microsoft.UI.Xaml.Controls.Grid.SetColumn(cGreen, 1);
        colorGrid.AddChild(cRed); Microsoft.UI.Xaml.Controls.Grid.SetColumn(cRed, 2);
        colorGrid.AddChild(cOrange); Microsoft.UI.Xaml.Controls.Grid.SetColumn(cOrange, 3);
        leftStack.AddChild(colorGrid);

        // Pen Width Slider
        var widthLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        widthLabel.Inlines.Add(new Run($"Pen Stroke Width: {visual.PenWidth:F0}px"));
        leftStack.AddChild(widthLabel);

        var widthSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 1f, Maximum = 20f, Value = visual.PenWidth, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        widthSlider.ValueChanged += (s, e) =>
        {
            visual.PenWidth = (float)widthSlider.Value;
            widthLabel.Inlines.Clear();
            widthLabel.Inlines.Add(new Run($"Pen Stroke Width: {visual.PenWidth:F0}px"));
            widthLabel.Invalidate();
            visual.Invalidate();
        };
        leftStack.AddChild(widthSlider);

        // Opacity Slider
        var opacityLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        opacityLabel.Inlines.Add(new Run($"Opacity: {visual.OpacityValue:F2}"));
        leftStack.AddChild(opacityLabel);

        var opacitySlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0f, Maximum = 1f, Value = visual.OpacityValue, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        opacitySlider.ValueChanged += (s, e) =>
        {
            visual.OpacityValue = (float)opacitySlider.Value;
            opacityLabel.Inlines.Clear();
            opacityLabel.Inlines.Add(new Run($"Opacity: {visual.OpacityValue:F2}"));
            opacityLabel.Invalidate();
            visual.Invalidate();
        };
        leftStack.AddChild(opacitySlider);

        // Translate X Slider
        var txLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        txLabel.Inlines.Add(new Run($"Translate X: {visual.TranslationX:F0}px"));
        leftStack.AddChild(txLabel);

        var txSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = -200f, Maximum = 200f, Value = visual.TranslationX, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        txSlider.ValueChanged += (s, e) =>
        {
            visual.TranslationX = (float)txSlider.Value;
            txLabel.Inlines.Clear();
            txLabel.Inlines.Add(new Run($"Translate X: {visual.TranslationX:F0}px"));
            txLabel.Invalidate();
            visual.Invalidate();
        };
        leftStack.AddChild(txSlider);

        // Translate Y Slider
        var tyLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        tyLabel.Inlines.Add(new Run($"Translate Y: {visual.TranslationY:F0}px"));
        leftStack.AddChild(tyLabel);

        var tySlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = -200f, Maximum = 200f, Value = visual.TranslationY, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        tySlider.ValueChanged += (s, e) =>
        {
            visual.TranslationY = (float)tySlider.Value;
            tyLabel.Inlines.Clear();
            tyLabel.Inlines.Add(new Run($"Translate Y: {visual.TranslationY:F0}px"));
            tyLabel.Invalidate();
            visual.Invalidate();
        };
        leftStack.AddChild(tySlider);

        // Rotate Transform
        var rotLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        rotLabel.Inlines.Add(new Run($"Rotate Angle: {visual.RotationAngle:F0}°"));
        leftStack.AddChild(rotLabel);

        var rotSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0f, Maximum = 360f, Value = visual.RotationAngle, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        rotSlider.ValueChanged += (s, e) =>
        {
            visual.RotationAngle = (float)rotSlider.Value;
            rotLabel.Inlines.Clear();
            rotLabel.Inlines.Add(new Run($"Rotate Angle: {visual.RotationAngle:F0}°"));
            rotLabel.Invalidate();
            visual.Invalidate();
        };
        leftStack.AddChild(rotSlider);

        // Scale Transform
        var scaleLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        scaleLabel.Inlines.Add(new Run($"Scale Factor: {visual.ScaleX:F2}x"));
        leftStack.AddChild(scaleLabel);

        var scaleSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0.2f, Maximum = 3.0f, Value = visual.ScaleX, Width = 260f, Margin = new Thickness(0, 0, 0, 20) };
        scaleSlider.ValueChanged += (s, e) =>
        {
            visual.ScaleX = (float)scaleSlider.Value;
            visual.ScaleY = (float)scaleSlider.Value;
            scaleLabel.Inlines.Clear();
            scaleLabel.Inlines.Add(new Run($"Scale Factor: {visual.ScaleX:F2}x"));
            scaleLabel.Invalidate();
            visual.Invalidate();
        };
        leftStack.AddChild(scaleSlider);

        // Reset Transform Button
        var resetBtn = new Button { Width = 260f, Height = 32f, CornerRadius = 6f };
        var resetText = new RichTextBlock { Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        resetText.Inlines.Add(new Run("Reset Matrix Transforms"));
        resetBtn.Content = resetText;
        resetBtn.Click += (s, e) =>
        {
            txSlider.Value = 0f;
            tySlider.Value = 0f;
            rotSlider.Value = 0f;
            scaleSlider.Value = 1.0f;
            opacitySlider.Value = 1.0f;
            visual.TranslationX = 0f;
            visual.TranslationY = 0f;
            visual.RotationAngle = 0f;
            visual.ScaleX = 1.0f;
            visual.ScaleY = 1.0f;
            visual.OpacityValue = 1.0f;
            visual.Invalidate();
        };
        leftStack.AddChild(resetBtn);

        grid.AddChild(leftStack);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(leftStack, 0);

        var previewContainer = new Border
        {
            CornerRadius = 8f,
            Background = new ProGPU.Vector.SolidColorBrush(0x0C0C12FF),
            BorderBrush = new ProGPU.Vector.ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            Margin = new Thickness(12),
            Child = visual
        };

        grid.AddChild(previewContainer);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(previewContainer, 1);

        return grid;
    }
}
