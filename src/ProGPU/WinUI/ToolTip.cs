using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.Text;

namespace ProGPU.WinUI;

public class ToolTip : Control
{
    private object? _content;

    public object? Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                _content = value;
                Invalidate();
            }
        }
    }

    public ToolTip()
    {
        Background = new SolidColorBrush(0x1F1F24FA); // Mica-dark translucent
        BorderBrush = new SolidColorBrush(0xFFFFFF15); // Translucent border
        BorderThickness = new Thickness(1f);
        CornerRadius = 4f;
        Padding = new Thickness(8f, 4f, 8f, 4f);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (Content == null) return Vector2.Zero;

        Vector2 size = Vector2.Zero;
        if (Content is FrameworkElement fe)
        {
            fe.Measure(availableSize);
            size = fe.DesiredSize;
        }
        else
        {
            string text = Content.ToString() ?? string.Empty;
            var font = PopupService.DefaultFont;
            if (font != null)
            {
                // Measure text size dynamically using TextLayout.MeasuredSize
                var textLayout = new TextLayout(text, font, 12f);
                size = textLayout.MeasuredSize;
            }
            else
            {
                size = new Vector2(80f, 16f); // Fallback
            }
        }

        return size + new Vector2(Padding.Left + Padding.Right, Padding.Top + Padding.Bottom);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        var finalSize = arrangeRect.Size;
        if (Content is FrameworkElement fe)
        {
            var contentRect = new Rect(
                Padding.Left,
                Padding.Top,
                finalSize.X - Padding.Left - Padding.Right,
                finalSize.Y - Padding.Top - Padding.Bottom
            );
            fe.Arrange(contentRect);
        }
    }

    public override void OnRender(DrawingContext context)
    {
        // 1. Draw rounded container card background and borders
        var rect = new Rect(Vector2.Zero, Size);
        
        // Soft ambient and shadow overlays
        context.FillRoundedRectangle(new SolidColorBrush(0x0000002A), new Rect(rect.X, rect.Y + 2f, rect.Width, rect.Height), CornerRadius);
        context.DrawRoundedRectangle(Background, new Pen(BorderBrush ?? new SolidColorBrush(0xFFFFFF15), BorderThickness.Left), rect, CornerRadius);

        // 2. Render content
        if (Content != null)
        {
            if (Content is not FrameworkElement)
            {
                string text = Content.ToString() ?? string.Empty;
                var font = PopupService.DefaultFont;
                if (font != null)
                {
                    context.DrawText(
                        text,
                        font,
                        12f,
                        new SolidColorBrush(0xFFFFFFE0), // Soft white
                        new Vector2(Padding.Left, Padding.Top)
                    );
                }
            }
        }

        base.OnRender(context);
    }
}
