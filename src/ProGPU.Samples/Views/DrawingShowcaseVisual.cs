using System;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.WinUI;

namespace ProGPU.Samples;

public class DrawingShowcaseVisual : FrameworkElement
{
    public DrawingShowcaseVisual()
    {
        HorizontalAlignment = ProGPU.Layout.HorizontalAlignment.Stretch;
        VerticalAlignment = ProGPU.Layout.VerticalAlignment.Stretch;
        HeightConstraint = 350f;
    }

    public override void OnRender(DrawingContext context)
    {
        // background border
        context.DrawRectangle(ThemeManager.GetBrush("CardBackground"), new Pen(ThemeManager.GetBrush("ControlBorder"), 1f), new Rect(0, 0, Size.X, Size.Y));

        // Let's divide into regions to draw different shapes
        float cellWidth = Size.X / 4f;
        float centerY = Size.Y / 2f;

        // 1. Drawing Lines (Cell 0)
        float x0 = 0f;
        context.DrawText("Lines", AppState.GetFont()!, 12f, ThemeManager.GetBrush("TextSecondary"), new Vector2(x0 + 10f, 10f));
        context.DrawLine(new Pen(ThemeManager.GetBrush("SystemAccentColor"), 3f), new Vector2(x0 + 20f, centerY - 50f), new Vector2(x0 + cellWidth - 20f, centerY + 50f));
        context.DrawLine(new Pen(ThemeManager.GetBrush("TextPrimary"), 1f), new Vector2(x0 + 20f, centerY + 50f), new Vector2(x0 + cellWidth - 20f, centerY - 50f));

        // 2. Drawing Rounded Rectangles (Cell 1)
        float x1 = cellWidth;
        context.DrawText("Rounded Rects", AppState.GetFont()!, 12f, ThemeManager.GetBrush("TextSecondary"), new Vector2(x1 + 10f, 10f));
        
        var linearGrad = new LinearGradientBrush(
            new Vector2(0f, 0f), new Vector2(1f, 1f),
            new GradientStop[] {
                new GradientStop(new Vector4(0f, 0.47f, 0.83f, 1f), 0f),      // Blue
                new GradientStop(new Vector4(0.5f, 0.1f, 0.8f, 1f), 0.5f),    // Purple
                new GradientStop(new Vector4(0.9f, 0.2f, 0.4f, 1f), 1f)       // Magenta
            }
        );
        context.DrawRoundedRectangle(linearGrad, new Pen(ThemeManager.GetBrush("TextPrimary"), 2f), new Rect(x1 + 20f, centerY - 60f, cellWidth - 40f, 120f), 15f);

        // 3. Drawing Circles & Ellipses (Cell 2)
        float x2 = cellWidth * 2f;
        context.DrawText("Circles & Ellipses", AppState.GetFont()!, 12f, ThemeManager.GetBrush("TextSecondary"), new Vector2(x2 + 10f, 10f));
        
        var radialGrad = new RadialGradientBrush(
            new Vector2(0.5f, 0.5f), 0.5f,
            new GradientStop[] {
                new GradientStop(new Vector4(1f, 0.8f, 0.2f, 1f), 0f),       // Yellow
                new GradientStop(new Vector4(1f, 0.4f, 0.1f, 1f), 0.6f),     // Orange
                new GradientStop(new Vector4(0.8f, 0.1f, 0.1f, 1f), 1f)      // Red
            }
        );
        context.DrawCircle(radialGrad, new Pen(ThemeManager.GetBrush("TextPrimary"), 1.5f), new Vector2(x2 + cellWidth / 2f, centerY - 30f), 40f);
        context.DrawEllipse(ThemeManager.GetBrush("SystemAccentColor"), new Pen(ThemeManager.GetBrush("TextPrimary"), 1f), new Vector2(x2 + cellWidth / 2f, centerY + 45f), 55f, 25f);

        // 4. Combined Graphics Art (Cell 3)
        float x3 = cellWidth * 3f;
        context.DrawText("Dynamic WebGPU Art", AppState.GetFont()!, 12f, ThemeManager.GetBrush("TextSecondary"), new Vector2(x3 + 10f, 10f));
        
        // Multi-layered visual geometry overlay using radial & linear brushes
        var artBg = new LinearGradientBrush(
            new Vector2(0f, 1f), new Vector2(0f, 0f),
            new GradientStop[] {
                new GradientStop(new Vector4(0.1f, 0.1f, 0.15f, 0.9f), 0f),
                new GradientStop(new Vector4(0.05f, 0.05f, 0.08f, 0.9f), 1f)
            }
        );
        context.DrawRoundedRectangle(artBg, null, new Rect(x3 + 20f, centerY - 70f, cellWidth - 40f, 140f), 8f);
        
        // Dynamic circles intersecting
        context.DrawCircle(new SolidColorBrush(new Vector4(0f, 0.8f, 0.6f, 0.4f)), null, new Vector2(x3 + cellWidth / 2f - 15f, centerY), 35f);
        context.DrawCircle(new SolidColorBrush(new Vector4(0f, 0.4f, 0.9f, 0.4f)), null, new Vector2(x3 + cellWidth / 2f + 15f, centerY), 35f);
    }
}
