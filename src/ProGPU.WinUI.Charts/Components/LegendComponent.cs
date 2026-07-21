using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Vector;
using ProGPU.Text;
using ProGPU.Layout;
using ProGPU.Scene;
using Microsoft.UI.Xaml;

namespace ProGPU.WinUI.Charts.Components
{
    public static class LegendComponent
    {
        public static void Draw(DrawingContext context, ChartGPUOptions options, TtfFont defaultFont, Rect bounds)
        {
            if (options?.Legend == null || !options.Legend.Show || options.Series == null || options.Series.Count == 0) return;

            var textBrush = ThemeManager.GetBrush("TextPrimary");
            var palette = options.Palette ?? new string[] { "#0078D4", "#107C41", "#D83B01", "#A8003F", "#5C2D91" };

            // 1. First Pass: Compute total width of all legend items combined to center them horizontally
            float totalWidth = 0f;
            var itemWidths = new List<float>(options.Series.Count);
            
            for (int i = 0; i < options.Series.Count; i++)
            {
                var series = options.Series[i];
                string name = series.Name ?? $"Series {i}";
                
                // Measure series name text width
                var layout = new TextLayout(name, defaultFont, 10f, float.PositiveInfinity, ProGPU.Text.TextAlignment.Left, null);
                float itemW = 12f + 6f + layout.MeasuredSize.X + 20f; // Indicator (12px) + Gap (6px) + Text + Item Gap (20px)
                itemWidths.Add(itemW);
                totalWidth += itemW;
            }

            // Remove trailing gap
            if (totalWidth > 0f) totalWidth -= 20f;

            // 2. Second Pass: Draw centered legend items
            float startX = bounds.X + (bounds.Width - totalWidth) / 2f;
            float startY = bounds.Y + 8f; // 8px down from top margin

            for (int i = 0; i < options.Series.Count; i++)
            {
                var series = options.Series[i];
                string name = series.Name ?? $"Series {i}";
                string colorStr = series.Color ?? palette[i % palette.Count];
                var indicatorBrush = new SolidColorBrush(ChartUtils.ParseCssColor(colorStr));

                // Draw color circle marker
                float circleX = startX + 6f;
                float circleY = startY + 6f;
                context.FillCircle(indicatorBrush, new Vector2(circleX, circleY), 3.5f);

                // Draw series text label
                context.DrawText(name, defaultFont, 10f, textBrush, new Vector2(startX + 18f, startY));

                startX += itemWidths[i];
            }
        }
    }
}
