using System.Numerics;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.WinUI.Hmi;

/// <summary>Retained instrument frames and process symbols. Geometry, quality and status have independent visual channels.</summary>
internal static class HmiDrawing
{
    // Compatibility brushes for existing trend/alarm components; new component rendering resolves a scoped palette.
    internal static readonly Brush Text = new ThemeResourceBrush("TextPrimary");
    internal static readonly Brush Muted = new ThemeResourceBrush("TextSecondary");
    internal static readonly Brush Background = new ThemeResourceBrush("CardBackground");
    internal static readonly Brush Border = new ThemeResourceBrush("ControlBorder");
    internal static readonly Brush Accent = new ThemeResourceBrush("SystemAccentColor");
    internal static readonly Brush Active = HmiPalette.Light.Running;
    internal static readonly Brush Warning = HmiPalette.Light.Warning;
    internal static readonly Brush Danger = HmiPalette.Light.Fault;
    internal static readonly Brush Transparent = new SolidColorBrush(Vector4.Zero);

    internal static void Draw(DrawingContext context, HmiSymbol symbol, Vector2 size, double value, double min, double max,
        bool active, HmiQuality quality, IReadOnlyList<HmiTagSample> history, bool alarm, float phase,
        HmiVisualTone tone = HmiVisualTone.Normal, HmiAppearance? appearance = null, HmiColorScheme colorScheme = HmiColorScheme.Light)
    {
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X < 8 || size.Y < 8) return;
        appearance ??= DefaultAppearance;
        var p = HmiPalette.Get(colorScheme);
        bool unknown = quality != HmiQuality.Good || tone == HmiVisualTone.Unknown;
        bool signalActive = active && HmiSymbolTraits.IsBinary(symbol);
        bool card = HmiSymbolTraits.IsCard(symbol, appearance.Presentation);
        if (card)
        {
            context.FillRoundedRectangle(p.Track, new Rect(0, 0, size.X, size.Y), 9);
            context.FillRoundedRectangle(p.Surface, new Rect(1, 1, size.X - 2, size.Y - 2), 8);

        }
        var layout = HmiVisualLayout.Calculate(symbol, size.X, size.Y, appearance);
        if (symbol is not (HmiSymbol.NumericDisplay or HmiSymbol.NumericInput or HmiSymbol.Trend or HmiSymbol.Label or HmiSymbol.Rectangle))
        {
            HmiSymbolRenderer.DrawGlyph(context, symbol, layout.Glyph, p, value, min, max, active && !unknown, unknown,
                alarm && symbol is (HmiSymbol.AlarmBanner or HmiSymbol.AlarmList) ? HmiVisualTone.Fault : tone,
                appearance.AnimateFlow ? phase : 0, appearance.ShowConnectionPorts, appearance.QuarterTurns, appearance.MirrorHorizontal, appearance.MirrorVertical);
        }
        if (symbol is HmiSymbol.AlarmBanner or HmiSymbol.AlarmList)
            context.FillRoundedRectangle(alarm ? p.Fault : p.Track, new Rect(0, 6, 3, size.Y - 12), 1);
        else if (symbol == HmiSymbol.Rectangle)
            context.DrawLine(p.Grid, new Vector2(12, Math.Min(33, size.Y - 3)), new Vector2(Math.Max(12, size.X - 12), Math.Min(33, size.Y - 3)));
        if (layout.Quality.Width > 0 && symbol != HmiSymbol.Label)
        {
            var r = layout.Quality;
            var center = new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2);
            var brush = p.Status(tone, signalActive, unknown);
            if (unknown || tone is HmiVisualTone.Warning or HmiVisualTone.Fault)
            {
                var pen = p.StatusPen(tone, active, unknown);
                context.DrawLine(pen, new(r.X, r.Y), new(r.Right, r.Bottom));
                context.DrawLine(pen, new(r.Right, r.Y), new(r.X, r.Bottom));
            }
            else context.DrawEllipse(signalActive ? brush : null, p.Outline, center, 3, 3);
        }
        if (unknown)
        {
            // Never present last-known level or stopped motion as proof of healthy equipment.
            context.DrawLine(p.WarningLine, new Vector2(4, size.Y - 2), new Vector2(size.X - 4, size.Y - 2));
        }
    }
    private static readonly HmiAppearance DefaultAppearance = new();
}
