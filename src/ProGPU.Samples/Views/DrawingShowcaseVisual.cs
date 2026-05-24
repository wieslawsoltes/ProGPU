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
        // Background card outline
        context.DrawRectangle(ThemeManager.GetBrush("CardBackground"), new Pen(ThemeManager.GetBrush("ControlBorder"), 1f), new Rect(0, 0, Size.X, Size.Y));

        // Partition into 4 equal column cells
        float cellWidth = Size.X / 4f;
        float centerY = Size.Y / 2f;

        // -------------------------------------------------------------
        // Column 1: Lines & Splines (Cell 0)
        // -------------------------------------------------------------
        float x0 = 0f;
        context.DrawText("Lines & Curves", AppState.GetFont()!, 12f, ThemeManager.GetBrush("TextSecondary"), new Vector2(x0 + 10f, 10f));
        
        // 1A. Solid Lines
        context.DrawLine(new Pen(ThemeManager.GetBrush("SystemAccentColor"), 3f), new Vector2(x0 + 20f, centerY - 60f), new Vector2(x0 + cellWidth - 20f, centerY + 60f));
        context.DrawLine(new Pen(ThemeManager.GetBrush("TextPrimary"), 1.5f), new Vector2(x0 + 20f, centerY + 60f), new Vector2(x0 + cellWidth - 20f, centerY - 60f));

        // 1B. Quadratic Bezier Curve
        context.DrawQuadraticBezier(
            new Pen(new SolidColorBrush(new Vector4(1f, 0.6f, 0f, 1f)), 2.5f), // Orange
            new Vector2(x0 + 20f, centerY + 20f),
            new Vector2(x0 + cellWidth / 2f, centerY - 80f),
            new Vector2(x0 + cellWidth - 20f, centerY + 20f)
        );

        // 1C. Cubic Bezier Curve
        context.DrawCubicBezier(
            new Pen(new SolidColorBrush(new Vector4(0.9f, 0.1f, 0.6f, 1f)), 2f), // Hot Pink
            new Vector2(x0 + 30f, centerY - 20f),
            new Vector2(x0 + 50f, centerY + 80f),
            new Vector2(x0 + cellWidth - 50f, centerY - 80f),
            new Vector2(x0 + cellWidth - 30f, centerY + 20f)
        );

        // -------------------------------------------------------------
        // Column 2: Rounded Rects & Clipping (Cell 1)
        // -------------------------------------------------------------
        float x1 = cellWidth;
        context.DrawText("Rounded Rects & Clipping", AppState.GetFont()!, 12f, ThemeManager.GetBrush("TextSecondary"), new Vector2(x1 + 10f, 10f));
        
        var linearGrad = new LinearGradientBrush(
            new Vector2(0f, 0f), new Vector2(1f, 1f),
            new GradientStop[] {
                new GradientStop(new Vector4(0.06f, 0.45f, 0.9f, 0.8f), 0f),
                new GradientStop(new Vector4(0.45f, 0.1f, 0.75f, 0.8f), 0.5f),
                new GradientStop(new Vector4(0.85f, 0.15f, 0.4f, 0.8f), 1f)
            }
        );
        
        // Background Rounded Rectangle
        context.DrawRoundedRectangle(linearGrad, new Pen(ThemeManager.GetBrush("ControlBorder"), 1.5f), new Rect(x1 + 15f, centerY - 65f, cellWidth - 30f, 130f), 12f);

        // Nested shape demonstrating GPU-bound clipping bounds
        var clipRect = new Rect(x1 + 35f, centerY - 40f, cellWidth - 70f, 80f);
        context.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.4f)), 1f), clipRect, 6f);
        
        // Push clip, draw overlapping larger items, and pop clip
        context.PushClip(clipRect);
        
        // Large rectangle extending outside clip boundaries
        context.DrawRectangle(new SolidColorBrush(new Vector4(0.88f, 0.06f, 0.25f, 0.7f)), null, new Rect(x1 + 10f, centerY - 50f, cellWidth - 20f, 100f));
        // Intersecting white circle centered in clipped region
        context.DrawCircle(new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.8f)), null, new Vector2(x1 + cellWidth / 2f, centerY), 30f);
        
        context.PopClip();

        // -------------------------------------------------------------
        // Column 3: Circles, Ellipses & Opacity Layering (Cell 2)
        // -------------------------------------------------------------
        float x2 = cellWidth * 2f;
        context.DrawText("Circles & Opacity Layering", AppState.GetFont()!, 12f, ThemeManager.GetBrush("TextSecondary"), new Vector2(x2 + 10f, 10f));
        
        var radialGrad = new RadialGradientBrush(
            new Vector2(0.5f, 0.5f), 0.5f,
            new GradientStop[] {
                new GradientStop(new Vector4(1f, 0.8f, 0.2f, 1f), 0f),       // Yellow
                new GradientStop(new Vector4(1f, 0.4f, 0.1f, 1f), 0.6f),     // Orange
                new GradientStop(new Vector4(0.8f, 0.1f, 0.1f, 1f), 1f)      // Red
            }
        );

        // 3A. Radial Gradient Circle
        context.DrawCircle(radialGrad, new Pen(ThemeManager.GetBrush("TextPrimary"), 1.5f), new Vector2(x2 + cellWidth / 2f, centerY - 45f), 35f);

        // 3B. Stroke Ellipse
        context.DrawEllipse(null, new Pen(ThemeManager.GetBrush("SystemAccentColor"), 2f), new Vector2(x2 + cellWidth / 2f, centerY + 5f), 50f, 20f);

        // 3C. Overlapping opacity-layered circles (blending backdrops)
        context.PushOpacity(0.5f);
        
        context.DrawCircle(new SolidColorBrush(new Vector4(0f, 0.47f, 0.83f, 1f)), null, new Vector2(x2 + cellWidth / 2f - 20f, centerY + 50f), 28f);
        context.DrawCircle(new SolidColorBrush(new Vector4(0.06f, 0.69f, 0.32f, 1f)), null, new Vector2(x2 + cellWidth / 2f + 20f, centerY + 50f), 28f);
        
        context.PopOpacity();

        // -------------------------------------------------------------
        // Column 4: GPGPU Complex Path Rasterization (Cell 3)
        // -------------------------------------------------------------
        float x3 = cellWidth * 3f;
        context.DrawText("Complex Vector Paths", AppState.GetFont()!, 12f, ThemeManager.GetBrush("TextSecondary"), new Vector2(x3 + 10f, 10f));

        var ribbonGrad = new LinearGradientBrush(
            new Vector2(0f, 0f), new Vector2(1f, 0f),
            new GradientStop[] {
                new GradientStop(new Vector4(0f, 0.8f, 0.6f, 1f), 0f),       // Teal
                new GradientStop(new Vector4(0f, 0.4f, 0.9f, 1f), 1f)        // Royal Blue
            }
        );

        // 4A. Complex Vector Ribbon Path (Filled)
        var ribbonPath = new PathGeometry();
        var ribbonFig = new PathFigure(new Vector2(x3 + 20f, centerY - 30f));
        ribbonFig.Segments.Add(new QuadraticBezierSegment(new Vector2(x3 + cellWidth / 2f, centerY - 80f), new Vector2(x3 + cellWidth - 20f, centerY - 30f)));
        ribbonFig.Segments.Add(new LineSegment(new Vector2(x3 + cellWidth - 20f, centerY - 10f)));
        ribbonFig.Segments.Add(new QuadraticBezierSegment(new Vector2(x3 + cellWidth / 2f, centerY - 60f), new Vector2(x3 + 20f, centerY - 10f)));
        ribbonFig.Segments.Add(new LineSegment(new Vector2(x3 + 20f, centerY - 30f)));
        ribbonPath.Figures.Add(ribbonFig);
        context.DrawPath(ribbonGrad, null, ribbonPath);

        // 4B. 5-Pointed Star Vector Path (Filled and Stroked)
        var starPath = new PathGeometry();
        float cx = x3 + cellWidth / 2f;
        float cy = centerY + 45f;
        float rOuter = 35f;
        float rInner = 14f;
        
        var starFigure = new PathFigure(new Vector2(cx, cy - rOuter));
        for (int i = 1; i < 10; i++)
        {
            float angle = i * MathF.PI / 5f;
            float r = (i % 2 == 0) ? rOuter : rInner;
            starFigure.Segments.Add(new LineSegment(new Vector2(cx + MathF.Sin(angle) * r, cy - MathF.Cos(angle) * r)));
        }
        starFigure.Segments.Add(new LineSegment(new Vector2(cx, cy - rOuter))); // Close loop
        starPath.Figures.Add(starFigure);

        var starBrush = new SolidColorBrush(new Vector4(0.88f, 0.06f, 0.25f, 0.3f)); // Translucent red fill
        var starPen = new Pen(ThemeManager.GetBrush("SystemAccentColor"), 1.5f);
        context.DrawPath(starBrush, starPen, starPath);
    }
}
