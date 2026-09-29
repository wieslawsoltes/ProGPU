using System.Numerics;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.WinUI.Hmi;

internal static class HmiTrendDrawing
{
    private static readonly Pen Trace = new(HmiDrawing.Accent, 2);
    internal static DateTimeOffset WindowStart(DateTimeOffset end, double seconds) =>
        end.UtcTicks - DateTimeOffset.MinValue.UtcTicks < seconds * TimeSpan.TicksPerSecond
            ? DateTimeOffset.MinValue : end.AddSeconds(-seconds);

    internal static void Draw(DrawingContext context, Vector2 size, IReadOnlyList<HmiTagSample> samples,
        DateTimeOffset end, HmiTrendOptions options, double minimum, double maximum, HmiTrendBucket[] scratch)
    {
        if (size.X < 48 || size.Y < 64 || maximum <= minimum || samples.Count == 0) return;
        var start = WindowStart(end, options.WindowSeconds);
        if (end <= start) return;
        float left = 18, right = size.X - 18;
        float top = Math.Min(35, size.Y * 0.25f), bottom = Math.Max(top + 1, size.Y - 32);
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
            if (!bucket.BreakBefore && previous is { } before) context.DrawLine(Trace, before, first);
            float x = (first.X + last.X) / 2;
            if (bucket.Maximum != bucket.Minimum)
                context.DrawLine(Trace, new Vector2(x, Y(bucket.Minimum)), new Vector2(x, Y(bucket.Maximum)));
            if (first != last) context.DrawLine(Trace, first, last);
            else context.DrawEllipse(HmiDrawing.Accent, null, first, 1.5f, 1.5f);
            previous = last;
        }
    }
}
