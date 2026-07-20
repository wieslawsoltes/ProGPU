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
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using Border = Microsoft.UI.Xaml.Controls.Border;
using Button = Microsoft.UI.Xaml.Controls.Button;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ProGPU.Samples;

public class GdiShowcaseVisual : FrameworkElement
{
    private System.Drawing.Bitmap? _bitmap;

    public float PenWidth { get; set; } = 2f;
    public float TranslationX { get; set; } = 0f;
    public float TranslationY { get; set; } = 0f;
    public float RotationAngle { get; set; } = 0f;
    public float GdiScale { get; set; } = 1.0f;
    public bool Antialias { get; set; } = true;
    public System.Drawing.Color DrawingColor { get; set; } = System.Drawing.Color.FromArgb(0, 120, 212); // Blue

    public GdiShowcaseVisual()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HeightConstraint = 450f;
    }

    public override void OnRender(DrawingContext context)
    {
        int w = (int)Math.Max(1f, Size.X);
        int h = (int)Math.Max(1f, Size.Y);

        if (_bitmap == null || _bitmap.Width != w || _bitmap.Height != h)
        {
            _bitmap?.Dispose();
            _bitmap = new System.Drawing.Bitmap(w, h);
        }

        using (var g = System.Drawing.Graphics.FromImage(_bitmap))
        {
            g.Clear(System.Drawing.Color.FromArgb(18, 18, 24));

            g.SmoothingMode = Antialias ? SmoothingMode.AntiAlias : SmoothingMode.None;

            g.TranslateTransform(w / 2f + TranslationX, h / 2f + TranslationY);
            g.RotateTransform(RotationAngle);
            g.ScaleTransform(GdiScale, GdiScale);

            // Draw a grid pattern
            using (var gridPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(30, 255, 255, 255), 1f))
            {
                for (int i = -200; i <= 200; i += 40)
                {
                    g.DrawLine(gridPen, i, -200, i, 200);
                    g.DrawLine(gridPen, -200, i, 200, i);
                }
            }

            // Draw coordinate axes
            using (var axisPenX = new System.Drawing.Pen(System.Drawing.Color.FromArgb(120, 255, 80, 80), 2f))
            using (var axisPenY = new System.Drawing.Pen(System.Drawing.Color.FromArgb(120, 80, 255, 80), 2f))
            {
                g.DrawLine(axisPenX, -220, 0, 220, 0);
                g.DrawLine(axisPenY, 0, -220, 0, 220);
            }

            // Draw a rectangle
            using (var rectPen = new System.Drawing.Pen(DrawingColor, PenWidth))
            {
                g.DrawRectangle(rectPen, -120, -120, 240, 240);
            }

            // Draw filled ellipse
            using (var fillBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(150, DrawingColor.R, DrawingColor.G, DrawingColor.B)))
            {
                g.FillEllipse(fillBrush, -60, -60, 120, 120);
            }

            // Draw a polygon (star shape)
            var points = new System.Drawing.PointF[]
            {
                new System.Drawing.PointF(0, -95),
                new System.Drawing.PointF(27, -30),
                new System.Drawing.PointF(90, -30),
                new System.Drawing.PointF(37, 10),
                new System.Drawing.PointF(57, 75),
                new System.Drawing.PointF(0, 35),
                new System.Drawing.PointF(-57, 75),
                new System.Drawing.PointF(-37, 10),
                new System.Drawing.PointF(-90, -30),
                new System.Drawing.PointF(-27, -30)
            };
            using (var starPen = new System.Drawing.Pen(System.Drawing.Color.Gold, 2.5f))
            {
                g.DrawPolygon(starPen, points);
            }

            // Draw text string using Font
            using (var textBrush = new System.Drawing.SolidBrush(System.Drawing.Color.White))
            using (var font = new System.Drawing.Font(AppState._font!, 13f))
            {
                g.DrawString("GPU-Accelerated GDI+ Shim", font, textBrush, new System.Drawing.PointF(-92, -155));
            }
        }

        _bitmap.Flush();

        context.DrawTexture(_bitmap.GpuTexture, new Rect(0, 0, Size.X, Size.Y));
    }

    public void Clean()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}

public static class GdiShowcasePage
{
    public static FrameworkElement Create()
    {
        var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };

        var title = new RichTextBlock { Font = AppState._font, FontSize = 15f, Margin = new Thickness(0, 0, 0, 5) };
        title.Inlines.Add(new Bold(new Run("GPU GDI+ (System.Drawing.Common) Showcase")));
        leftStack.AddChild(title);

        var desc = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
        desc.Inlines.Add(new Run("Interact with standard GDI drawing primitives. The calls execute on a System.Drawing.Bitmap backed by a native GpuTexture."));
        leftStack.AddChild(desc);

        var visual = new GdiShowcaseVisual();

        // 1. Antialiasing checkbox-like ToggleButton or simple Button
        var aaBtn = new Button { Width = 260f, Height = 32f, CornerRadius = 6f, Margin = new Thickness(0, 0, 0, 15) };
        var aaText = new RichTextBlock { Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        aaText.Inlines.Add(new Run("Smoothing Mode: "));
        aaText.Inlines.Add(new Bold(new Run("AntiAlias")) { Foreground = new ThemeResourceBrush("SystemAccentColor") });
        aaBtn.Content = aaText;
        aaBtn.Click += (s, e) =>
        {
            visual.Antialias = !visual.Antialias;
            aaText.Inlines.Clear();
            aaText.Inlines.Add(new Run("Smoothing Mode: "));
            aaText.Inlines.Add(new Bold(new Run(visual.Antialias ? "AntiAlias" : "None")) { Foreground = new ThemeResourceBrush("SystemAccentColor") });
            aaText.Invalidate();
            visual.Invalidate();
        };
        leftStack.AddChild(aaBtn);

        // 2. Color Selection Grid
        var colorLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 5) };
        colorLabel.Inlines.Add(new Run("Drawing Pen/Brush Color:"));
        leftStack.AddChild(colorLabel);

        var colorGrid = new Microsoft.UI.Xaml.Controls.Grid { Margin = new Thickness(0, 0, 0, 15) };
        colorGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        colorGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        colorGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        colorGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        var cBlue = new Button { Content = new RichTextBlock { Font = AppState._font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center }, Margin = new Thickness(2) };
        ((RichTextBlock)cBlue.Content).Inlines.Add(new Run("Blue"));
        cBlue.Click += (s, e) => { visual.DrawingColor = System.Drawing.Color.FromArgb(0, 120, 212); visual.Invalidate(); };

        var cGreen = new Button { Content = new RichTextBlock { Font = AppState._font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center }, Margin = new Thickness(2) };
        ((RichTextBlock)cGreen.Content).Inlines.Add(new Run("Green"));
        cGreen.Click += (s, e) => { visual.DrawingColor = System.Drawing.Color.FromArgb(16, 124, 65); visual.Invalidate(); };

        var cRed = new Button { Content = new RichTextBlock { Font = AppState._font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center }, Margin = new Thickness(2) };
        ((RichTextBlock)cRed.Content).Inlines.Add(new Run("Red"));
        cRed.Click += (s, e) => { visual.DrawingColor = System.Drawing.Color.FromArgb(220, 60, 60); visual.Invalidate(); };

        var cOrange = new Button { Content = new RichTextBlock { Font = AppState._font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center }, Margin = new Thickness(2) };
        ((RichTextBlock)cOrange.Content).Inlines.Add(new Run("Orange"));
        cOrange.Click += (s, e) => { visual.DrawingColor = System.Drawing.Color.FromArgb(247, 99, 12); visual.Invalidate(); };

        colorGrid.AddChild(cBlue); Microsoft.UI.Xaml.Controls.Grid.SetColumn(cBlue, 0);
        colorGrid.AddChild(cGreen); Microsoft.UI.Xaml.Controls.Grid.SetColumn(cGreen, 1);
        colorGrid.AddChild(cRed); Microsoft.UI.Xaml.Controls.Grid.SetColumn(cRed, 2);
        colorGrid.AddChild(cOrange); Microsoft.UI.Xaml.Controls.Grid.SetColumn(cOrange, 3);
        leftStack.AddChild(colorGrid);

        // 3. Pen Width Slider
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

        // 4. Translate X Slider
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

        // 5. Translate Y Slider
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

        // 6. Rotate Transform
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

        // 7. Scale Transform
        var scaleLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        scaleLabel.Inlines.Add(new Run($"Scale Factor: {visual.GdiScale:F2}x"));
        leftStack.AddChild(scaleLabel);

        var scaleSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0.2f, Maximum = 3.0f, Value = visual.GdiScale, Width = 260f, Margin = new Thickness(0, 0, 0, 20) };
        scaleSlider.ValueChanged += (s, e) =>
        {
            visual.GdiScale = (float)scaleSlider.Value;
            scaleLabel.Inlines.Clear();
            scaleLabel.Inlines.Add(new Run($"Scale Factor: {visual.GdiScale:F2}x"));
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
            visual.TranslationX = 0f;
            visual.TranslationY = 0f;
            visual.RotationAngle = 0f;
            visual.GdiScale = 1.0f;
            visual.Invalidate();
        };
        leftStack.AddChild(resetBtn);

        var previewContainer = new Border
        {
            CornerRadius = 8f,
            Background = new SolidColorBrush(0x0C0C12FF),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            Margin = new Thickness(12),
            Child = visual
        };

        return new ResponsiveSplitView
        {
            OpenPaneLength = 300f,
            PaneContent = leftStack,
            MainContent = previewContainer
        };
    }
}
