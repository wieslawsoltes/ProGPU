using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.WinUI;

public class HyperlinkButton : Button
{
    public new FrameworkElement? Content
    {
        get => base.Content;
        set
        {
            base.Content = value;
            UpdateContentForeground();
        }
    }

    public new Brush? Foreground
    {
        get => base.Foreground;
        set
        {
            base.Foreground = value;
            UpdateContentForeground();
            Invalidate();
        }
    }

    public HyperlinkButton()
    {
        Background = null;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0, 2, 0, 2);
        Foreground = new SolidColorBrush(0x0078D4FF); // Standard Fluent Segoe Blue accent
    }

    private void UpdateContentForeground()
    {
        if (Content is TextVisual tv)
        {
            tv.Brush = Foreground;
        }
    }

    public override void OnRender(DrawingContext context)
    {
        // Transparent/no background unless background is explicitly set
        if (Background != null)
        {
            context.DrawRectangle(Background, null, new Rect(Vector2.Zero, Size));
        }

        // Active focus indicator
        if (IsEnabled && IsFocused)
        {
            var focusPen = new Pen(new SolidColorBrush(0x0078D4FF), 1f);
            context.DrawRectangle(null, focusPen, new Rect(0f, 0f, Size.X, Size.Y));
        }

        // Underline on hover
        if (IsEnabled && IsPointerOver && Content != null)
        {
            float leftInset = BorderThickness.Left + Padding.Left;
            float topInset = BorderThickness.Top + Padding.Top;
            float rightInset = BorderThickness.Right + Padding.Right;
            float bottomInset = BorderThickness.Bottom + Padding.Bottom;

            float childW = Math.Min(Size.X - (leftInset + rightInset), Content.DesiredSize.X);
            float childH = Math.Min(Size.Y - (topInset + bottomInset), Content.DesiredSize.Y);

            float childX = leftInset + (Size.X - (leftInset + rightInset) - childW) / 2f;
            float childY = topInset + (Size.Y - (topInset + bottomInset) - childH) / 2f;

            var accentBrush = Foreground ?? new SolidColorBrush(0x0078D4FF);
            // Draw a 1px solid rectangle line as underline
            context.DrawRectangle(accentBrush, null, new Rect(childX, childY + childH + 1f, childW, 1f));
        }

        // Bypassing base.OnRender to avoid drawing standard button borders/shadows/overlays
    }
}
