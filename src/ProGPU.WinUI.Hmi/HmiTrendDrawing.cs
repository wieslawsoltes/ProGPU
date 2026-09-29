using System.Numerics;
using ProGPU.Hmi;
using ProGPU.Scene;

namespace ProGPU.WinUI.Hmi;

internal static class HmiTrendDrawing
{
    internal static DateTimeOffset WindowStart(DateTimeOffset end, double seconds) =>
        end.UtcTicks - DateTimeOffset.MinValue.UtcTicks < seconds * TimeSpan.TicksPerSecond
            ? DateTimeOffset.MinValue : end.AddSeconds(-seconds);

    internal static void Draw(DrawingContext context, Vector2 size, IReadOnlyList<HmiTagSample> samples,
        DateTimeOffset end, HmiTrendOptions options, double minimum, double maximum, HmiTrendBucket[] scratch,
        HmiColorScheme colorScheme = HmiColorScheme.Light)
    {
        if (size.X < 48 || size.Y < 80 || maximum <= minimum) return;
        var palette = HmiPalette.Get(colorScheme);
        float left = 16, right = size.X - 16, top = 40, bottom = size.Y - 38;
        if (bottom <= top) return;
        for (int i = 0; i <= 4; i++)
        {
            float y = top + i * (bottom - top) / 4;
            context.DrawLine(palette.Grid, new(left, y), new(right, y));
        }
        for (int i = 1; i < 6; i++)
        {
            float x = left + i * (right - left) / 6;
            context.DrawLine(palette.Grid, new(x, top), new(x, bottom));
        }
        var start = WindowStart(end, options.WindowSeconds);
        if (end <= start || samples.Count == 0) return;
        int count = Math.Clamp((int)(right - left), 1, scratch.Length);
        var buckets = scratch.AsSpan(0, count);
        HmiTrendReducer.Reduce(samples, start, end, buckets, TimeSpan.FromSeconds(options.MaximumGapSeconds));
        Vector2? previous = null;
        float Y(double value) => bottom - (float)Math.Clamp((value - minimum) / (maximum - minimum), 0, 1) * (bottom - top);
        foreach (var bucket in buckets)
        {
            if (bucket.ContainsGap) { previous = null; continue; }
            if (bucket.Count == 0) continue;
            var first = new Vector2(left + (float)bucket.FirstX * (right - left), Y(bucket.First));
            var last = new Vector2(left + (float)bucket.LastX * (right - left), Y(bucket.Last));
            if (!bucket.BreakBefore && previous is { } before) context.DrawLine(palette.AccentLine, before, first);
            float x = (first.X + last.X) / 2;
            if (bucket.Maximum != bucket.Minimum)
                context.DrawLine(palette.AccentLine, new(x, Y(bucket.Minimum)), new(x, Y(bucket.Maximum)));
            if (first != last) context.DrawLine(palette.AccentLine, first, last);
            else context.DrawEllipse(palette.Accent, null, first, 1.5f, 1.5f);
            previous = last;
        }
    }
}
