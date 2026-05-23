using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.Text;

namespace ProGPU.WinUI;

public static class AdornerLayer
{
    public static void Render(DrawingContext context, float screenWidth, float screenHeight)
    {
        if (!DevToolsService.IsDevToolsActive) return;

        if (DevToolsService.HoveredElement != null && DevToolsService.HoveredElement != DevToolsService.InspectedElement)
        {
            RenderElementHighlight(context, DevToolsService.HoveredElement, new Vector4(0.298f, 0.686f, 0.314f, 0.2f), new Vector4(0.298f, 0.686f, 0.314f, 1f)); // Green for hover
        }

        if (DevToolsService.InspectedElement != null)
        {
            RenderElementHighlight(context, DevToolsService.InspectedElement, new Vector4(0f, 0.47f, 0.83f, 0.25f), new Vector4(0f, 0.47f, 0.83f, 1f)); // Blue for select
        }
    }

    private static void RenderElementHighlight(DrawingContext context, FrameworkElement fe, Vector4 fillColor, Vector4 borderColor)
    {
        Vector2 absPos = fe.Offset;
        Visual? p = fe.Parent;
        while (p != null)
        {
            absPos += p.Offset;
            p = p.Parent;
        }

        Vector2 size = fe.Size;
        if (size.X <= 0 || size.Y <= 0) return;

        // Draw translucent content overlay
        var fillBrush = new SolidColorBrush(fillColor);
        var borderPen = new Pen(new SolidColorBrush(borderColor), 1.5f);
        Rect rect = new Rect(absPos, size);
        context.DrawRectangle(fillBrush, borderPen, rect);

        // Draw dynamic sizing label pill at top-left
        var font = PopupService.DefaultFont;
        if (font != null)
        {
            string label = $"{fe.GetType().Name} ({fe.Size.X:N0} × {fe.Size.Y:N0})";
            float fontSize = 9f;
            var textLayout = new TextLayout(label, font, fontSize);
            Vector2 labelSize = textLayout.MeasuredSize + new Vector2(8f, 4f);

            // Draw label background pill
            float pillX = absPos.X;
            float pillY = absPos.Y - labelSize.Y - 2f;
            if (pillY < 2f) pillY = absPos.Y + 2f; // Keep on screen

            var pillRect = new Rect(new Vector2(pillX, pillY), labelSize);
            context.DrawRectangle(new SolidColorBrush(0x13131AF0), new Pen(new SolidColorBrush(borderColor), 1f), pillRect);

            // Draw label text
            context.DrawText(label, font, fontSize, new SolidColorBrush(0xFFFFFFFF), new Vector2(pillX + 4f, pillY + 2f));
        }
    }
}
