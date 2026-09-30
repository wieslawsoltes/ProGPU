using System.Numerics;
using ProGPU.Scene;

namespace ProGPU.WinUI.Hmi;

/// <summary>Neutral engineering-context band; does not evaluate alarms or infer permissives.</summary>
internal static class HmiOperatingBandRenderer
{
    internal static void Draw(DrawingContext context, Rect bounds, HmiPalette p, double value,
        double minimum, double maximum, double low, double high, bool unknown)
    {
        if (bounds.Width < 12 || bounds.Height < 8 || maximum <= minimum) return;
        float X(double v) => bounds.X + (float)Math.Clamp((v - minimum) / (maximum - minimum), 0, 1) * bounds.Width;
        float center = bounds.Y + bounds.Height / 2;
        context.DrawLine(p.Grid, new(bounds.X, center), new(bounds.Right, center));
        context.FillRoundedRectangle(p.Body, new Rect(X(low), center - 4, X(high) - X(low), 8), 1);
        context.DrawLine(p.Outline, new(X(low), center - 4), new(X(low), center + 4));
        context.DrawLine(p.Outline, new(X(high), center - 4), new(X(high), center + 4));
        if (!unknown)
        {
            float x = X(value);
            context.DrawLine(p.Strong, new(x, center - 6), new(x, center + 6));
            context.DrawEllipse(p.Text, null, new Vector2(x, center - 6), 2, 2);
        }
    }
}
