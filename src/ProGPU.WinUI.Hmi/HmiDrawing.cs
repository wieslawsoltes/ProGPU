using System.Numerics;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.WinUI.Hmi;

/// <summary>Shared retained-vector primitives; no bitmap snapshots, WebViews or per-control GPU devices.</summary>
internal static class HmiDrawing
{
    internal static readonly Brush Text = new ThemeResourceBrush("TextPrimary");
    internal static readonly Brush Muted = new ThemeResourceBrush("TextSecondary");
    internal static readonly Brush Background = new ThemeResourceBrush("CardBackground");
    internal static readonly Brush Border = new ThemeResourceBrush("ControlBorder");
    internal static readonly Brush Accent = new ThemeResourceBrush("SystemAccentColor");
    internal static readonly Brush Active = new SolidColorBrush(new Vector4(0.13f, 0.66f, 0.48f, 1));
    internal static readonly Brush Warning = new SolidColorBrush(new Vector4(0.90f, 0.57f, 0.12f, 1));
    internal static readonly Brush Danger = new SolidColorBrush(new Vector4(0.87f, 0.23f, 0.24f, 1));
    internal static readonly Brush Transparent = new SolidColorBrush(Vector4.Zero);
    private static readonly Pen Outline = new(Border, 2);
    private static readonly Pen Fine = new(Muted, 1);
    private static readonly Pen AccentLine = new(Accent, 2);
    private static readonly Pen ActiveLine = new(Active, 3);
    private static readonly Pen InactiveLine = new(Muted, 3);
    private static readonly Pen Needle = new(Text, 3);

    internal static void Draw(DrawingContext dc, HmiSymbol symbol, Vector2 size, double value, double min, double max,
        bool active, HmiQuality quality, IReadOnlyList<HmiTagSample> history, bool alarm, float phase)
    {
        float w = size.X, h = size.Y;
        if (!float.IsFinite(w) || !float.IsFinite(h) || w < 8 || h < 8) return;
        float fraction = max > min ? (float)Math.Clamp((value - min) / (max - min), 0, 1) : 0;
        Brush status = quality == HmiQuality.Good ? active ? Active : Muted : Warning;
        Pen statusLine = active && quality == HmiQuality.Good ? ActiveLine : InactiveLine;
        if (symbol is not HmiSymbol.Label and not HmiSymbol.Pipe)
        {
            dc.FillRoundedRectangle(Border, new Rect(0, 0, w, h), 7);
            dc.FillRoundedRectangle(Background, new Rect(1, 1, w - 2, h - 2), 6);
        }
        float top = Math.Min(35, h * 0.25f), bottom = Math.Max(top + 1, h - 32);
        float bodyHeight = Math.Max(1, bottom - top);
        var center = new Vector2(w / 2, (top + bottom) / 2);
        float radius = Math.Max(2, Math.Min(w * 0.3f, bodyHeight * 0.42f));
        switch (symbol)
        {
            case HmiSymbol.Tank:
                float tw = w * 0.63f, tx = (w - tw) / 2;
                dc.FillRoundedRectangle(Border, new Rect(tx, top, tw, bodyHeight), 10);
                dc.FillRoundedRectangle(Background, new Rect(tx + 3, top + 3, Math.Max(1, tw - 6), Math.Max(1, bodyHeight - 6)), 8);
                float fill = Math.Max(0, (bodyHeight - 8) * fraction);
                if (fill > 0) dc.FillRoundedRectangle(quality == HmiQuality.Good ? Accent : Warning, new Rect(tx + 4, bottom - 4 - fill, Math.Max(1, tw - 8), fill), 5);
                dc.DrawEllipse(Background, Outline, new Vector2(w / 2, top + 10), tw / 2, 10);
                for (int i = 0; i <= 5; i++) dc.DrawLine(Fine, new Vector2(tx + tw + 5, top + i * bodyHeight / 5), new Vector2(tx + tw + 12, top + i * bodyHeight / 5));
                break;
            case HmiSymbol.Pump:
            case HmiSymbol.Motor:
                dc.DrawEllipse(Background, Outline, center, radius, radius);
                for (int i = 0; i < 3; i++)
                {
                    float angle = i * MathF.Tau / 3 + (active ? phase * MathF.Tau : 0);
                    dc.DrawLine(statusLine, center, center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius * 0.8f);
                }
                dc.DrawLine(Outline, new Vector2(center.X - radius, center.Y + radius + 6), new Vector2(center.X + radius, center.Y + radius + 6));
                if (symbol == HmiSymbol.Motor) for (int i = -2; i <= 2; i++) dc.DrawLine(Fine, new Vector2(center.X + i * radius / 3, center.Y - radius), new Vector2(center.X + i * radius / 3, center.Y - radius - 7));
                break;
            case HmiSymbol.Valve:
                float vx = Math.Max(4, w * 0.2f), vy = Math.Max(4, bodyHeight * 0.25f);
                var leftTop = center + new Vector2(-vx, -vy); var leftBottom = center + new Vector2(-vx, vy);
                var rightTop = center + new Vector2(vx, -vy); var rightBottom = center + new Vector2(vx, vy);
                dc.DrawLine(statusLine, leftTop, leftBottom); dc.DrawLine(statusLine, leftBottom, rightTop);
                dc.DrawLine(statusLine, rightTop, rightBottom); dc.DrawLine(statusLine, rightBottom, leftTop);
                dc.DrawLine(Outline, center, center + new Vector2(0, -vy - 12));
                dc.DrawLine(statusLine, center + new Vector2(-vx / 2, -vy - 12), center + new Vector2(vx / 2, -vy - 12));
                break;
            case HmiSymbol.Pipe:
                dc.FillRoundedRectangle(Border, new Rect(0, h * 0.38f, w, Math.Max(2, h * 0.24f)), 3);
                dc.FillRoundedRectangle(status, new Rect(0, h * 0.44f, w, Math.Max(1, h * 0.12f)), 2);
                if (active) for (float x = phase * 32; x < w - 8; x += 32)
                {
                    dc.DrawLine(Fine, new Vector2(x, h * 0.36f), new Vector2(x + 7, h * 0.5f));
                    dc.DrawLine(Fine, new Vector2(x + 7, h * 0.5f), new Vector2(x, h * 0.64f));
                }
                break;
            case HmiSymbol.Gauge:
                radius = Math.Max(2, Math.Min(w * 0.36f, bodyHeight * 0.75f));
                center = new Vector2(w / 2, bottom - 2);
                for (int i = 0; i <= 20; i++)
                {
                    float angle = MathF.PI + i * MathF.PI / 20;
                    var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                    dc.DrawLine(i >= 17 ? ActiveLine : Fine, center + direction * radius * 0.85f, center + direction * radius);
                }
                float needleAngle = MathF.PI + fraction * MathF.PI;
                dc.DrawLine(Needle, center, center + new Vector2(MathF.Cos(needleAngle), MathF.Sin(needleAngle)) * radius * 0.76f);
                dc.DrawEllipse(Accent, null, center, 5, 5);
                break;
            case HmiSymbol.BarGraph:
                dc.FillRoundedRectangle(Border, new Rect(16, center.Y - 10, Math.Max(1, w - 32), 20), 4);
                if (fraction > 0) dc.FillRoundedRectangle(Accent, new Rect(16, center.Y - 10, Math.Max(1, (w - 32) * fraction), 20), 4);
                break;
            case HmiSymbol.Indicator:
                dc.DrawEllipse(status, Outline, center, radius, radius);
                break;
            case HmiSymbol.ToggleSwitch:
                float sw = Math.Min(62, w * 0.35f), sy = Math.Max(6, h / 2 - 10);
                dc.FillRoundedRectangle(status, new Rect(w - sw - 12, sy, sw, 20), 10);
                dc.DrawEllipse(Background, null, new Vector2(w - sw - 2 + (active ? sw - 20 : 0), sy + 10), 8, 8);
                break;
            case HmiSymbol.Conveyor:
                dc.FillRoundedRectangle(Border, new Rect(12, center.Y - 13, Math.Max(1, w - 24), 26), 12);
                for (float x = 25; x < w - 20; x += 28) dc.DrawEllipse(Background, Outline, new Vector2(x, center.Y), 8, 8);
                for (float x = 20 + phase * 35; active && x < w - 20; x += 35) dc.DrawLine(statusLine, new Vector2(x, center.Y - 16), new Vector2(Math.Min(x + 12, w - 12), center.Y - 16));
                break;
            case HmiSymbol.Trend:
                Trend(dc, history, w, top, bottom, min, max);
                break;
            case HmiSymbol.AlarmBanner:
            case HmiSymbol.AlarmList:
                dc.FillRoundedRectangle(alarm ? Danger : Active, new Rect(0, 0, 5, h), 2);
                break;
            case HmiSymbol.PushButton:
            case HmiSymbol.NavigationButton:
            case HmiSymbol.RecipeButton:
                dc.FillRoundedRectangle(Accent, new Rect(0, h - 4, w, 4), 2);
                break;
        }
        if (quality != HmiQuality.Good) dc.FillRoundedRectangle(Warning, new Rect(0, h - 3, w, 3), 1);
    }
    private static void Trend(DrawingContext dc, IReadOnlyList<HmiTagSample> samples, float width, float top, float bottom, double min, double max)
    {
        float left = 18, right = Math.Max(left + 1, width - 18);
        for (int i = 0; i <= 4; i++) dc.DrawLine(Fine, new Vector2(left, top + (bottom - top) * i / 4), new Vector2(right, top + (bottom - top) * i / 4));
        if (samples.Count < 2 || max <= min) return;
        // Min/max buckets retain spikes while limiting emitted GPU geometry to the plot width.
        int buckets = Math.Max(1, Math.Min(512, (int)(right - left)));
        int stride = Math.Max(1, (samples.Count + buckets - 1) / buckets);
        Vector2? previous = null;
        for (int first = 0; first < samples.Count; first += stride)
        {
            int end = Math.Min(samples.Count, first + stride);
            double low = double.PositiveInfinity, high = double.NegativeInfinity;
            bool gap = false;
            for (int i = first; i < end; i++)
            {
                if (samples[i].Quality != HmiQuality.Good || samples[i].Value.Type != HmiTagType.Number) { gap = true; continue; }
                double value = samples[i].Value.Number;
                low = Math.Min(low, value); high = Math.Max(high, value);
            }
            if (!double.IsFinite(low) || gap) { previous = null; continue; }
            float x = left + first * (right - left) / (samples.Count - 1);
            float Y(double value) => bottom - (float)Math.Clamp((value - min) / (max - min), 0, 1) * (bottom - top);
            var current = new Vector2(x, Y(samples[end - 1].Value.Number));
            dc.DrawLine(AccentLine, new Vector2(x, Y(low)), new Vector2(x, Y(high)));
            if (previous is { } last) dc.DrawLine(AccentLine, last, current);
            previous = current;
        }
    }
}
