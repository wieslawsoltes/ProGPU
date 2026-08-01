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
using SkiaSharp;

namespace ProGPU.Samples;

public class SkiaSharpShimVisual : FrameworkElement
{
    public SkiaSharpShimVisual()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HeightConstraint = 400f;
    }

    public override void OnRender(DrawingContext context)
    {
        // Background card outline
        context.DrawRectangle(ThemeManager.GetBrush("CardBackground"), new Pen(ThemeManager.GetBrush("ControlBorder"), 1f), new Rect(0, 0, Size.X, Size.Y));

        float cellWidth = Size.X / 3f;
        float centerY = Size.Y / 2f;

        // Create canvas wrapper
        using var canvas = new SKCanvas(context, Size.X, Size.Y);

        // 1. Column 1: Basic Primitives
        float x0 = 0f;
        context.DrawText("Skia Primitives", AppState.GetFont()!, 12f, ThemeManager.GetBrush("TextSecondary"), new Vector2(x0 + 15f, 15f));

        using (var paintFill = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(0, 120, 212) }) // Blue
        using (var paintStroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(255, 255, 255), StrokeWidth = 2f })
        {
            // Rect
            canvas.DrawRect(new SKRect(x0 + 20f, centerY - 80f, x0 + cellWidth - 20f, centerY - 20f), paintFill);
            canvas.DrawRect(new SKRect(x0 + 20f, centerY - 80f, x0 + cellWidth - 20f, centerY - 20f), paintStroke);

            // Circle
            paintFill.Color = new SKColor(16, 124, 65); // Green
            canvas.DrawCircle(x0 + cellWidth / 2f, centerY + 40f, 35f, paintFill);
            canvas.DrawCircle(x0 + cellWidth / 2f, centerY + 40f, 35f, paintStroke);
        }

        // 2. Column 2: Complex Paths
        float x1 = cellWidth;
        context.DrawText("Skia Custom Paths", AppState.GetFont()!, 12f, ThemeManager.GetBrush("TextSecondary"), new Vector2(x1 + 15f, 15f));

        using (var builder = new SKPathBuilder())
        {
            builder.MoveTo(x1 + 20f, centerY - 60f);
            builder.LineTo(x1 + cellWidth / 2f, centerY - 90f);
            builder.LineTo(x1 + cellWidth - 20f, centerY - 60f);
            builder.QuadTo(x1 + cellWidth / 2f, centerY, x1 + 20f, centerY - 60f);
            builder.Close();

            // Add circle path
            builder.AddCircle(x1 + cellWidth / 2f, centerY + 35f, 30f);
            using var path = builder.Detach();

            using (var paint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = new SKColor(220, 60, 60, 180) // Translucent Red
            })
            {
                canvas.DrawPath(path, paint);
            }

            using (var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(247, 99, 12), // Orange
                StrokeWidth = 2.5f
            })
            {
                canvas.DrawPath(path, paint);
            }
        }

        // 3. Column 3: Shader Gradients
        float x2 = cellWidth * 2f;
        context.DrawText("Skia Shaders (Gradients)", AppState.GetFont()!, 12f, ThemeManager.GetBrush("TextSecondary"), new Vector2(x2 + 15f, 15f));

        // Linear Gradient
        var linearColors = new SKColor[] { new SKColor(255, 0, 128), new SKColor(0, 128, 255) };
        var linearShader = SKShader.CreateLinearGradient(
            new SKPoint(x2 + 20f, centerY - 80f),
            new SKPoint(x2 + cellWidth - 20f, centerY - 20f),
            linearColors,
            null,
            SKShaderTileMode.Clamp
        );

        using (var paintLinear = new SKPaint { Shader = linearShader, Style = SKPaintStyle.Fill })
        {
            canvas.DrawRoundRect(new SKRect(x2 + 20f, centerY - 80f, x2 + cellWidth - 20f, centerY - 20f), 12f, 12f, paintLinear);
        }

        // Radial Gradient
        var radialColors = new SKColor[] { new SKColor(255, 230, 0), new SKColor(255, 100, 0), new SKColor(0, 0, 0, 0) };
        var radialShader = SKShader.CreateRadialGradient(
            new SKPoint(x2 + cellWidth / 2f, centerY + 40f),
            35f,
            radialColors,
            null,
            SKShaderTileMode.Clamp
        );

        using (var paintRadial = new SKPaint { Shader = radialShader, Style = SKPaintStyle.Fill })
        {
            canvas.DrawCircle(x2 + cellWidth / 2f, centerY + 40f, 35f, paintRadial);
        }
    }
}

public static class SkiaSharpShimPage
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
        title.Inlines.Add(new Bold(new Run("SkiaSharp API Shim")));
        mainStack.AddChild(title);

        var desc = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 24) };
        desc.Inlines.Add(new Run("This page showcases drawing vector components using our custom SkiaSharp shim. Developers can swap their SkiaSharp dependency to ProGPU and continue calling identical SKCanvas, SKPaint, SKPath, and SKShader APIs. Drawing calls translate directly to native WebGPU instructions."));
        mainStack.AddChild(desc);

        // Visual Showcase
        var visual = new SkiaSharpShimVisual();
        mainStack.AddChild(visual);

        return scrollViewer;
    }
}
