using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using Border = Microsoft.UI.Xaml.Controls.Border;
using Button = Microsoft.UI.Xaml.Controls.Button;
using TextBox = Microsoft.UI.Xaml.Controls.TextBox;

namespace ProGPU.Samples;

public class GlyphRunShowcaseVisual : FrameworkElement, IAnimatedElement
{
    private float _time = 0f;

    public void Update(float delta)
    {
        UpdateTime(delta);
    }

    public string TargetText { get; set; } = "GPU Instanced Glyph Rendering Engine";
    public TtfFont TargetFont { get; set; } = (AppState._font ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont)!;
    public float FontSize { get; set; } = 28f;
    public float Tracking { get; set; } = 2f;
    public float WaveAmplitude { get; set; } = 20f;
    public float WaveFrequency { get; set; } = 0.015f;
    public string LayoutMode { get; set; } = "Wave"; // "Wave" or "Circular"
    public bool Animate { get; set; } = true;

    public GlyphRunShowcaseVisual()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HeightConstraint = 450f;
    }

    public void UpdateTime(float delta)
    {
        if (Animate)
        {
            _time += delta * 4f;
            Invalidate();
        }
    }

    public override void OnRender(DrawingContext context)
    {
        // Card background
        context.DrawRectangle(ThemeManager.GetBrush("CardBackground"), new Pen(ThemeManager.GetBrush("ControlBorder"), 1f), new Rect(0, 0, Size.X, Size.Y));

        if (string.IsNullOrEmpty(TargetText) || TargetFont == null) return;

        var brush = ThemeManager.GetBrush("TextPrimary");

        if (LayoutMode == "Wave")
        {
            // 1. WAVE LAYOUT: All characters rendered in a single, high-performance instanced DrawGlyphRun call.
            int count = TargetText.Length;
            var glyphIndices = new ushort[count];
            var glyphPositions = new Vector2[count];

            float cursorX = 30f;
            float baselineY = Size.Y / 2f;

            for (int i = 0; i < count; i++)
            {
                char c = TargetText[i];
                ushort glyphIdx = TargetFont.GetGlyphIndex(c);
                glyphIndices[i] = glyphIdx;

                float advance = TargetFont.GetAdvanceWidth(glyphIdx, FontSize);

                // Wave effect offset per character
                float waveOffset = MathF.Sin(cursorX * WaveFrequency + _time) * WaveAmplitude;
                glyphPositions[i] = new Vector2(cursorX, baselineY + waveOffset);

                cursorX += advance + Tracking;
            }

            // Draw all characters as a single run
            context.DrawGlyphRun(glyphIndices, glyphPositions, TargetFont, FontSize, brush, Vector2.Zero);
        }
        else if (LayoutMode == "Circular")
        {
            // 2. CIRCULAR LAYOUT: Draw each character rotated and positioned along a circle.
            // Demonstrates individual glyph rotation transforms via custom matrix transforms.
            float centerX = Size.X / 2f;
            float centerY = Size.Y / 2f;
            float radius = MathF.Min(Size.X, Size.Y) * 0.35f;

            // Compute total angular length of text to center it
            float totalWidth = 0f;
            var glyphIndices = new ushort[TargetText.Length];
            var advances = new float[TargetText.Length];
            for (int i = 0; i < TargetText.Length; i++)
            {
                char c = TargetText[i];
                glyphIndices[i] = TargetFont.GetGlyphIndex(c);
                advances[i] = TargetFont.GetAdvanceWidth(glyphIndices[i], FontSize) + Tracking;
                totalWidth += advances[i];
            }

            // Calculate angular start so the text centers at the top/sides
            float totalArcAngle = totalWidth / radius;
            float startAngle = -MathF.PI / 2f - (totalArcAngle / 2f) + (_time * 0.05f);

            float currentAngle = startAngle;

            for (int i = 0; i < TargetText.Length; i++)
            {
                ushort glyphIdx = glyphIndices[i];
                if (glyphIdx == 0 && TargetText[i] == ' ')
                {
                    currentAngle += advances[i] / radius;
                    continue;
                }

                // Place glyph on circle circumference
                float gx = centerX + radius * MathF.Cos(currentAngle);
                float gy = centerY + radius * MathF.Sin(currentAngle);

                // Rotate glyph outwards / perpendicular to circle (face outward)
                float rotationAngle = currentAngle + MathF.PI / 2f;

                var singleIndex = new ushort[] { glyphIdx };
                var singlePos = new Vector2[] { Vector2.Zero };

                // Create transformation matrix for this specific glyph
                var rotMat = Matrix4x4.CreateRotationZ(rotationAngle);

                context.DrawGlyphRun(singleIndex, singlePos, TargetFont, FontSize, brush, new Vector2(gx, gy), rotMat);

                currentAngle += advances[i] / radius;
            }

            // Draw a central circle outline to visualize the circular guide
            context.DrawEllipse(null, new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.07f)), 1.5f), new Vector2(centerX, centerY), radius, radius);
        }
    }
}

public static class GlyphRunShowcasePage
{
    public static FrameworkElement Create()
    {
        var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };

        var title = new RichTextBlock { Font = AppState._font, FontSize = 15f, Margin = new Thickness(0, 0, 0, 5) };
        title.Inlines.Add(new Bold(new Run("GPU Low-Level Glyph API Showcase")));
        leftStack.AddChild(title);

        var desc = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
        desc.Inlines.Add(new Run("Exposes the instanced DrawGlyphRun API, letting you customize tracking, offsets, waves, and complex glyph paths directly on the GPU."));
        leftStack.AddChild(desc);

        var visual = new GlyphRunShowcaseVisual();

        // 1. Text input
        var textLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        textLabel.Inlines.Add(new Run("Custom Text Input:"));
        leftStack.AddChild(textLabel);

        var textBox = new TextBox
        {
            Text = visual.TargetText,
            WidthConstraint = 260f,
            HeightConstraint = 32f,
            Margin = new Thickness(0, 0, 0, 15)
        };
        textBox.TextChanged += (s, e) =>
        {
            visual.TargetText = textBox.Text;
            visual.Invalidate();
        };
        leftStack.AddChild(textBox);

        // 2. Font Selector
        var fontLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        fontLabel.Inlines.Add(new Run("Select Font Family:"));
        leftStack.AddChild(fontLabel);

        var fontCombo = new ComboBox { Font = AppState._font, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        fontCombo.Items.Add(new ComboBoxItem("Inter (UI Default)"));
        fontCombo.Items.Add(new ComboBoxItem("Arial"));
        fontCombo.Items.Add(new ComboBoxItem("Times New Roman"));
        fontCombo.Items.Add(new ComboBoxItem("Georgia"));
        fontCombo.Items.Add(new ComboBoxItem("Courier New"));
        fontCombo.Items.Add(new ComboBoxItem("Comic Sans"));
        fontCombo.SelectedItem = fontCombo.Items[0];
        fontCombo.SelectionChanged += (s, e) =>
        {
            if (fontCombo.SelectedItem != null)
            {
                visual.TargetFont = (fontCombo.SelectedItem.Text switch
                {
                    "Inter (UI Default)" => AppState._font ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont,
                    "Arial" => AppState._fontArial ?? AppState._font ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont,
                    "Times New Roman" => AppState._fontTimes ?? AppState._font ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont,
                    "Georgia" => AppState._fontGeorgia ?? AppState._font ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont,
                    "Courier New" => AppState._fontCourier ?? AppState._font ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont,
                    "Comic Sans" => AppState._fontComic ?? AppState._font ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont,
                    _ => AppState._font ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont
                })!;
                visual.Invalidate();
            }
        };
        leftStack.AddChild(fontCombo);

        // 3. Layout Mode Selector
        var modeLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        modeLabel.Inlines.Add(new Run("Layout Placement Mode:"));
        leftStack.AddChild(modeLabel);

        var modeCombo = new ComboBox { Font = AppState._font, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        modeCombo.Items.Add(new ComboBoxItem("Wave Text (Instanced Run)"));
        modeCombo.Items.Add(new ComboBoxItem("Circular Text (Per-Glyph Rotation)"));
        modeCombo.SelectedItem = modeCombo.Items[0];
        modeCombo.SelectionChanged += (s, e) =>
        {
            if (modeCombo.SelectedItem != null)
            {
                visual.LayoutMode = modeCombo.SelectedItem.Text.Contains("Wave") ? "Wave" : "Circular";
                visual.Invalidate();
            }
        };
        leftStack.AddChild(modeCombo);

        // 4. FontSize Slider
        var fsLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        fsLabel.Inlines.Add(new Run($"Font Size: {visual.FontSize:F0}pt"));
        leftStack.AddChild(fsLabel);

        var fsSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 10f, Maximum = 70f, Value = visual.FontSize, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        fsSlider.ValueChanged += (s, e) =>
        {
            visual.FontSize = (float)fsSlider.Value;
            fsLabel.Inlines.Clear();
            fsLabel.Inlines.Add(new Run($"Font Size: {visual.FontSize:F0}pt"));
            fsLabel.Invalidate();
            visual.Invalidate();
        };
        leftStack.AddChild(fsSlider);

        // 5. Tracking Slider
        var trackingLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        trackingLabel.Inlines.Add(new Run($"Tracking (Letter Space): {visual.Tracking:F0}px"));
        leftStack.AddChild(trackingLabel);

        var trackingSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = -5f, Maximum = 30f, Value = visual.Tracking, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        trackingSlider.ValueChanged += (s, e) =>
        {
            visual.Tracking = (float)trackingSlider.Value;
            trackingLabel.Inlines.Clear();
            trackingLabel.Inlines.Add(new Run($"Tracking (Letter Space): {visual.Tracking:F0}px"));
            trackingLabel.Invalidate();
            visual.Invalidate();
        };
        leftStack.AddChild(trackingSlider);

        // 6. Wave Amplitude Slider
        var ampLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        ampLabel.Inlines.Add(new Run($"Wave Amplitude: {visual.WaveAmplitude:F0}px"));
        leftStack.AddChild(ampLabel);

        var ampSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0f, Maximum = 80f, Value = visual.WaveAmplitude, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        ampSlider.ValueChanged += (s, e) =>
        {
            visual.WaveAmplitude = (float)ampSlider.Value;
            ampLabel.Inlines.Clear();
            ampLabel.Inlines.Add(new Run($"Wave Amplitude: {visual.WaveAmplitude:F0}px"));
            ampLabel.Invalidate();
            visual.Invalidate();
        };
        leftStack.AddChild(ampSlider);

        // 7. Wave Frequency Slider
        var freqLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        freqLabel.Inlines.Add(new Run($"Wave Frequency: {visual.WaveFrequency:F3}"));
        leftStack.AddChild(freqLabel);

        var freqSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0.005f, Maximum = 0.04f, Value = visual.WaveFrequency, Width = 260f, Margin = new Thickness(0, 0, 0, 15) };
        freqSlider.ValueChanged += (s, e) =>
        {
            visual.WaveFrequency = (float)freqSlider.Value;
            freqLabel.Inlines.Clear();
            freqLabel.Inlines.Add(new Run($"Wave Frequency: {visual.WaveFrequency:F3}"));
            freqLabel.Invalidate();
            visual.Invalidate();
        };
        leftStack.AddChild(freqSlider);

        // 8. Animation Toggle Button
        var animBtn = new Button { Width = 260f, Height = 32f, CornerRadius = 6f };
        var animText = new RichTextBlock { Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        animText.Inlines.Add(new Run("Animation: "));
        animText.Inlines.Add(new Bold(new Run("Running")) { Foreground = new ThemeResourceBrush("SystemAccentColor") });
        animBtn.Content = animText;
        animBtn.Click += (s, e) =>
        {
            visual.Animate = !visual.Animate;
            animText.Inlines.Clear();
            animText.Inlines.Add(new Run("Animation: "));
            animText.Inlines.Add(new Bold(new Run(visual.Animate ? "Running" : "Paused")) { Foreground = new ThemeResourceBrush("SystemAccentColor") });
            animText.Invalidate();
        };
        leftStack.AddChild(animBtn);

        var previewContainer = new Border
        {
            CornerRadius = 8f,
            Background = new SolidColorBrush(0x0C0C12FF),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            Margin = new Thickness(12),
            Child = visual
        };

        // We can hook a global loop or animation tick to this visual in the Page lifecycle
        // Since FrameworkElement has layout animation tick, we can hook it up via MainWindowController OnWindowUpdate.
        // Handled automatically via IAnimatedElement traversal in UpdateSampleAnimations.

        return new ResponsiveSplitView
        {
            OpenPaneLength = 300f,
            PaneContent = leftStack,
            MainContent = previewContainer
        };
    }
}
