using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.WinUI;

namespace ProGPU.Samples;

internal static class TextDisplayFactory
{
    private static readonly object s_lock = new();
    private static readonly Queue<Border> s_pool = new();

    public static Border Rent()
    {
        Border? border = null;
        lock (s_lock)
        {
            if (s_pool.Count > 0)
            {
                border = s_pool.Dequeue();
            }
        }

        if (border == null)
        {
            border = new Border();
            var textBlock = new RichTextBlock();
            border.Child = textBlock;
        }

        Reset(border);
        return border;
    }

    public static void Return(Border border)
    {
        Reset(border);
        lock (s_lock)
        {
            s_pool.Enqueue(border);
        }
    }

    public static void SetText(Border border, string text)
    {
        if (border.Child is RichTextBlock textBlock)
        {
            textBlock.Inlines.Clear();
            textBlock.Inlines.Add(new Run(text));
            textBlock.Invalidate();
        }
    }

    public static void SetForeground(Border border, Brush? brush)
    {
        if (border.Child is RichTextBlock textBlock)
        {
            textBlock.Foreground = brush;
            textBlock.Invalidate();
        }
    }

    public static void SetBackground(Border border, Brush? brush)
    {
        border.Background = brush;
        border.Invalidate();
    }

    public static void SetPadding(Border border, Thickness padding)
    {
        border.Padding = padding;
        border.Invalidate();
    }

    private static void Reset(Border border)
    {
        border.Background = null;
        border.Padding = default;
        border.BorderBrush = null;
        border.BorderThickness = default;
        border.CornerRadius = 0f;

        if (border.Child is RichTextBlock textBlock)
        {
            textBlock.Inlines.Clear();
            textBlock.Foreground = null;
            textBlock.Font = AppState._font; // Default system font
            textBlock.FontSize = 14f;
            textBlock.Invalidate();
        }

        border.HorizontalAlignment = HorizontalAlignment.Stretch;
        border.VerticalAlignment = VerticalAlignment.Stretch;
        border.Rotation = 0f;
        border.Scale = Vector3.One;
        border.CenterPoint = Vector3.Zero;
        border.Width = float.NaN;
        border.Height = float.NaN;

        Canvas.SetLeft(border, 0f);
        Canvas.SetTop(border, 0f);
    }
}
