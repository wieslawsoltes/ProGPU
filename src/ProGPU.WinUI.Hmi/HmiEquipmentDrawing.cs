using System.Numerics;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.WinUI.Hmi;

/// <summary>Resolution-independent equipment glyphs expressed through the existing retained GPU drawing context.</summary>
internal static class HmiEquipmentDrawing
{
    private static readonly Pen Outline = new(HmiDrawing.Muted, 2);
    private static readonly Pen Accent = new(HmiDrawing.Accent, 3);
    private static readonly Pen Active = new(HmiDrawing.Active, 3);
    private static readonly Pen Warning = new(HmiDrawing.Warning, 3);
    private static readonly Pen Fault = new(HmiDrawing.Danger, 3);
    private static readonly Brush Maintenance = new SolidColorBrush(new Vector4(0.42f, 0.42f, 0.70f, 1));
    private static readonly Pen MaintenanceLine = new(Maintenance, 3);

    internal static Brush ToneBrush(HmiVisualTone tone, Brush fallback) => tone switch
    {
        HmiVisualTone.Running => HmiDrawing.Active,
        HmiVisualTone.Warning or HmiVisualTone.Unknown => HmiDrawing.Warning,
        HmiVisualTone.Fault => HmiDrawing.Danger,
        HmiVisualTone.Maintenance => Maintenance,
        _ => fallback
    };
    internal static Pen TonePen(HmiVisualTone tone, Pen fallback) => tone switch
    {
        HmiVisualTone.Running => Active,
        HmiVisualTone.Warning or HmiVisualTone.Unknown => Warning,
        HmiVisualTone.Fault => Fault,
        HmiVisualTone.Maintenance => MaintenanceLine,
        _ => fallback
    };
    internal static void Draw(DrawingContext dc, HmiSymbol symbol, float width, float top, float bottom, float fraction, Brush status, float phase)
    {
        if (width < 48 || bottom - top < 32) return;
        float left = width * 0.15f, right = width * 0.85f;
        float height = Math.Max(8, bottom - top);
        float cy = (top + bottom) / 2;
        float cx = width / 2;
        float radius = Math.Max(3, Math.Min(width * 0.28f, height * 0.42f));
        switch (symbol)
        {
            case HmiSymbol.HeatExchanger:
                dc.DrawEllipse(HmiDrawing.Background, Outline, new Vector2(cx, cy), radius * 1.2f, radius);
                Polyline(dc, Accent, new Vector2(left, cy - radius / 2), new Vector2(cx + radius / 2, cy - radius / 2), new Vector2(cx - radius / 2, cy), new Vector2(cx + radius / 2, cy + radius / 2), new Vector2(right, cy + radius / 2));
                dc.DrawLine(Outline, new Vector2(cx, top), new Vector2(cx, cy - radius));
                dc.DrawLine(Outline, new Vector2(cx, cy + radius), new Vector2(cx, bottom));
                break;
            case HmiSymbol.Filter:
                Polyline(dc, Outline, new Vector2(left, top + 4), new Vector2(right, top + 4), new Vector2(right, bottom - 4), new Vector2(left, bottom - 4), new Vector2(left, top + 4));
                for (int i = 0; i < 5; i++)
                {
                    float x = left + (right - left) * (i + 1) / 6;
                    dc.DrawLine(Outline, new Vector2(x, top + 8), new Vector2(x, bottom - 8));
                }
                dc.DrawLine(Accent, new Vector2(left - 12, cy), new Vector2(left, cy));
                dc.DrawLine(Accent, new Vector2(right, cy), new Vector2(right + 12, cy));
                break;
            case HmiSymbol.Compressor:
                dc.DrawEllipse(HmiDrawing.Background, Outline, new Vector2(cx, cy), radius, radius);
                Polyline(dc, Accent, new Vector2(cx - radius * 0.55f, cy - radius * 0.7f), new Vector2(cx + radius * 0.7f, cy), new Vector2(cx - radius * 0.55f, cy + radius * 0.7f), new Vector2(cx - radius * 0.55f, cy - radius * 0.7f));
                dc.DrawLine(Outline, new Vector2(left - 8, cy), new Vector2(cx - radius, cy));
                dc.DrawLine(Outline, new Vector2(cx + radius, cy), new Vector2(right + 8, cy));
                break;
            case HmiSymbol.Fan:
                dc.DrawEllipse(HmiDrawing.Background, Outline, new Vector2(cx, cy), radius, radius);
                for (int i = 0; i < 4; i++)
                {
                    float angle = i * MathF.PI / 2 + phase * MathF.Tau;
                    var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                    var normal = new Vector2(-direction.Y, direction.X);
                    Polyline(dc, Active, new Vector2(cx, cy), new Vector2(cx, cy) + direction * radius * 0.78f + normal * radius * 0.25f,
                        new Vector2(cx, cy) + direction * radius * 0.8f - normal * radius * 0.2f, new Vector2(cx, cy));
                }
                dc.DrawEllipse(status, null, new Vector2(cx, cy), 5, 5);
                break;
            case HmiSymbol.Heater:
                for (int i = 0; i < 3; i++)
                {
                    float x = left + (right - left) * (i + 1) / 4;
                    Polyline(dc, Warning, new Vector2(x, bottom - 4), new Vector2(x - 8, cy + height * 0.2f), new Vector2(x + 8, cy - height * 0.2f), new Vector2(x, top + 4));
                    Polyline(dc, Warning, new Vector2(x - 5, top + 11), new Vector2(x, top + 4), new Vector2(x + 5, top + 11));
                }
                break;
            case HmiSymbol.Thermometer:
                float bulb = Math.Min(14, height * 0.13f), tube = Math.Max(3, bulb * 0.38f);
                dc.FillRoundedRectangle(HmiDrawing.Border, new Rect(cx - tube - 3, top + 3, tube * 2 + 6, height - bulb), tube + 3);
                dc.FillRoundedRectangle(HmiDrawing.Background, new Rect(cx - tube, top + 6, tube * 2, Math.Max(1, height - bulb - 6)), tube);
                float fill = Math.Max(1, (height - bulb - 10) * fraction);
                dc.FillRoundedRectangle(HmiDrawing.Accent, new Rect(cx - tube + 1, bottom - bulb - fill, Math.Max(1, tube * 2 - 2), fill), tube);
                dc.DrawEllipse(HmiDrawing.Accent, Outline, new Vector2(cx, bottom - bulb), bulb, bulb);
                for (int i = 0; i <= 4; i++) dc.DrawLine(Outline, new Vector2(cx + bulb + 5, top + i * (height - bulb * 2) / 4), new Vector2(cx + bulb + 14, top + i * (height - bulb * 2) / 4));
                break;
            case HmiSymbol.Boiler:
                dc.FillRoundedRectangle(HmiDrawing.Border, new Rect(left, top, right - left, height * 0.8f), 9);
                dc.FillRoundedRectangle(HmiDrawing.Background, new Rect(left + 3, top + 3, right - left - 6, height * 0.8f - 6), 7);
                float water = Math.Max(1, height * 0.55f * fraction);
                dc.FillRoundedRectangle(HmiDrawing.Accent, new Rect(left + 6, top + height * 0.75f - water, right - left - 12, water), 3);
                Polyline(dc, Warning, new Vector2(cx - 12, bottom), new Vector2(cx - 6, bottom - 12), new Vector2(cx, bottom - 5), new Vector2(cx + 6, bottom - 18), new Vector2(cx + 12, bottom));
                dc.DrawLine(Outline, new Vector2(cx, top), new Vector2(cx, top - 8));
                break;
            case HmiSymbol.CoolingTower:
                Polyline(dc, Outline, new Vector2(left, bottom), new Vector2(left + 12, cy), new Vector2(left + 5, top), new Vector2(right - 5, top), new Vector2(right - 12, cy), new Vector2(right, bottom), new Vector2(left, bottom));
                for (int i = 0; i < 4; i++) dc.DrawLine(Accent, new Vector2(left + 15, cy + i * height * 0.1f), new Vector2(right - 15, cy + i * height * 0.1f));
                dc.DrawEllipse(status, Outline, new Vector2(cx, top + height * 0.2f), radius * 0.4f, radius * 0.15f);
                break;
        }
    }
    private static void Polyline(DrawingContext dc, Pen pen, params ReadOnlySpan<Vector2> points)
    {
        for (int i = 1; i < points.Length; i++) dc.DrawLine(pen, points[i - 1], points[i]);
    }
}
