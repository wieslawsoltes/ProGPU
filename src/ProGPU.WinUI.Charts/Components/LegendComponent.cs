using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Vector;
using ProGPU.Text;
using ProGPU.Layout;
using ProGPU.Scene;
using Microsoft.UI.Xaml;

namespace ProGPU.WinUI.Charts.Components
{
    public static class LegendComponent
    {
        private static readonly string[] s_defaultPalette =
            ["#0078D4", "#107C41", "#D83B01", "#A8003F", "#5C2D91"];
        private static readonly ConditionalWeakTable<
            ChartGPUOptions,
            LegendLayoutCache> s_layoutCaches = new();

        /// <summary>
        /// Retains shaped measurement and immutable marker state until the
        /// series identity, label, color, palette value, or font changes.
        /// Validation is O(S) and allocation-free; rebuilding is O(S + G)
        /// time and O(S + G) retained storage for S series and G label glyphs.
        /// </summary>
        private sealed class LegendLayoutCache
        {
            private SeriesConfig?[] _series = [];
            private string?[] _configuredNames = [];
            private string[] _drawNames = [];
            private string[] _colorSources = [];
            private float[] _itemWidths = [];
            private SolidColorBrush[] _indicatorBrushes = [];
            private TtfFont? _font;

            public float TotalWidth { get; private set; }
            public int Count => _series.Length;

            public string GetName(int index) => _drawNames[index];
            public float GetItemWidth(int index) => _itemWidths[index];
            public SolidColorBrush GetIndicatorBrush(int index) =>
                _indicatorBrushes[index];

            public void Update(
                ChartGPUOptions options,
                TtfFont font,
                IReadOnlyList<string> palette)
            {
                var series = options.Series!;
                if (IsCurrent(series, font, palette))
                {
                    return;
                }

                int count = series.Count;
                _series = new SeriesConfig[count];
                _configuredNames = new string?[count];
                _drawNames = new string[count];
                _colorSources = new string[count];
                _itemWidths = new float[count];
                _indicatorBrushes = new SolidColorBrush[count];
                _font = font;
                TotalWidth = 0f;

                for (int index = 0; index < count; index++)
                {
                    SeriesConfig item = series[index];
                    string name = item.Name ?? $"Series {index}";
                    string color = ResolveColor(item, index, palette);
                    var layout = new TextLayout(
                        name,
                        font,
                        10f,
                        float.PositiveInfinity,
                        ProGPU.Text.TextAlignment.Left,
                        null);
                    float itemWidth =
                        12f + 6f + layout.MeasuredSize.X + 20f;

                    _series[index] = item;
                    _configuredNames[index] = item.Name;
                    _drawNames[index] = name;
                    _colorSources[index] = color;
                    _itemWidths[index] = itemWidth;
                    _indicatorBrushes[index] =
                        new SolidColorBrush(
                            ChartUtils.ParseCssColor(color));
                    TotalWidth += itemWidth;
                }

                if (TotalWidth > 0f)
                {
                    TotalWidth -= 20f;
                }
            }

            private bool IsCurrent(
                IReadOnlyList<SeriesConfig> series,
                TtfFont font,
                IReadOnlyList<string> palette)
            {
                if (!ReferenceEquals(_font, font) ||
                    _series.Length != series.Count)
                {
                    return false;
                }

                for (int index = 0; index < series.Count; index++)
                {
                    SeriesConfig item = series[index];
                    if (!ReferenceEquals(_series[index], item) ||
                        !string.Equals(
                            _configuredNames[index],
                            item.Name,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            _colorSources[index],
                            ResolveColor(item, index, palette),
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                return true;
            }

            private static string ResolveColor(
                SeriesConfig series,
                int index,
                IReadOnlyList<string> palette)
            {
                if (series.Color is { } color)
                {
                    return color;
                }

                return palette[index % palette.Count];
            }
        }

        public static void Draw(DrawingContext context, ChartGPUOptions options, TtfFont defaultFont, Rect bounds)
        {
            if (options?.Legend == null || !options.Legend.Show || options.Series == null || options.Series.Count == 0) return;

            var textBrush = ThemeManager.GetBrush("TextPrimary");
            IReadOnlyList<string> palette =
                options.Palette is { Count: > 0 } configuredPalette
                    ? configuredPalette
                    : s_defaultPalette;
            LegendLayoutCache cache = s_layoutCaches.GetValue(
                options,
                static _ => new LegendLayoutCache());
            cache.Update(options, defaultFont, palette);

            // 2. Second Pass: Draw centered legend items
            float startX =
                bounds.X + (bounds.Width - cache.TotalWidth) / 2f;
            float startY = bounds.Y + 8f; // 8px down from top margin

            for (int i = 0; i < cache.Count; i++)
            {
                // Draw color circle marker
                float circleX = startX + 6f;
                float circleY = startY + 6f;
                context.FillCircle(
                    cache.GetIndicatorBrush(i),
                    new Vector2(circleX, circleY),
                    3.5f);

                // Draw series text label
                context.DrawText(
                    cache.GetName(i),
                    defaultFont,
                    10f,
                    textBrush,
                    new Vector2(startX + 18f, startY));

                startX += cache.GetItemWidth(i);
            }
        }
    }
}
