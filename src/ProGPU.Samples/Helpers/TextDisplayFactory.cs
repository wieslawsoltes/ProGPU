using Thickness = Microsoft.UI.Xaml.Thickness;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;

namespace ProGPU.Samples;

internal static class TextDisplayFactory
{
    private const int MaximumRetainedElements = 512;
    private static readonly object s_lock = new();
    private static readonly Queue<PooledTextDisplay> s_pool = new();

    private sealed class PooledTextDisplay : Border
    {
        public Run Run { get; } = new("lol?");
        public SolidColorBrush ForegroundBrush { get; } = new(Vector4.One);

        public PooledTextDisplay()
        {
            var textBlock = new RichTextBlock
            {
                Font = AppState._font,
                FontSize = 14f,
                Foreground = ForegroundBrush
            };
            textBlock.Inlines.Add(Run);
            Child = textBlock;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            Width = 80f;
            Height = 40f;
            CenterPoint = new Vector3(40f, 20f, 0f);
        }
    }

    public static Border Rent()
    {
        PooledTextDisplay? border = null;
        lock (s_lock)
        {
            if (s_pool.Count > 0)
            {
                border = s_pool.Dequeue();
            }
        }

        if (border == null)
        {
            border = new PooledTextDisplay();
        }
        return border;
    }

    public static void Return(Border border)
    {
        if (border is not PooledTextDisplay pooled) return;
        lock (s_lock)
        {
            if (s_pool.Count < MaximumRetainedElements)
                s_pool.Enqueue(pooled);
        }
    }

    public static void SetText(Border border, string text)
    {
        if (border is PooledTextDisplay pooled &&
            !string.Equals(pooled.Run.Text, text, System.StringComparison.Ordinal))
            pooled.Run.Text = text;
    }

    public static void SetForegroundColor(Border border, Vector4 color)
    {
        if (border is PooledTextDisplay pooled && pooled.ForegroundBrush.Color != color)
        {
            pooled.ForegroundBrush.Color = color;
            border.Invalidate();
        }
    }

    public static void SetBackground(Border border, Brush? brush)
    {
        border.Background = brush;
        border.Invalidate();
    }

    public static void SetPadding(Border border, Microsoft.UI.Xaml.Thickness padding)
    {
        border.Padding = padding;
        border.Invalidate();
    }

}
