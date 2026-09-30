using System.Numerics;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.WinUI.Hmi;

/// <summary>
/// One resolution-independent equipment vocabulary for runtime controls and designer thumbnails.
/// Geometry is emitted into the host's retained DrawingContext; no bitmap, foreign canvas or GPU device is created.
/// </summary>
internal static partial class HmiSymbolRenderer
{
    internal static void DrawGlyph(DrawingContext context, HmiSymbol symbol, Rect bounds, HmiPalette palette,
        double value, double minimum, double maximum, bool active, bool unknown, HmiVisualTone tone,
        float phase = 0, bool ports = true, int quarterTurns = 0, bool mirrorHorizontal = false, bool mirrorVertical = false, HmiInstrumentLocation instrumentLocation = HmiInstrumentLocation.Field)
    {
        if (bounds.Width < 4 || bounds.Height < 4 || !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height)) return;
        bounds = HmiPortLayout.FitGlyph(symbol, bounds, quarterTurns);
        float fraction = maximum > minimum ? (float)Math.Clamp((value - minimum) / (maximum - minimum), 0, 1) : 0;
        var g = new Painter(context, bounds, palette, ports, quarterTurns, mirrorHorizontal, mirrorVertical);
        Brush signal = palette.Status(tone, active, unknown);
        Pen signalLine = palette.StatusPen(tone, active, unknown);
        Brush fluid = unknown ? palette.Track : palette.Accent;
        Pen fluidLine = unknown ? palette.Outline : palette.AccentLine;
        phase = active && !unknown && float.IsFinite(phase) ? phase - MathF.Floor(phase) : 0;
        if (HmiSymbolTraits.IsSchematicSymbol(symbol))
        {
            DrawSchematic(g, symbol, palette, instrumentLocation);
            return;
        }
        switch (symbol)
        {
            case HmiSymbol.Tank:
                g.R(palette.Body, 23, 12, 50, 76, 7);
                g.R(palette.Surface, 26, 15, 44, 70, 5);
                if (fraction > 0) g.R(fluid, 28, 84 - 64 * fraction, 40, 64 * fraction, 3);
                g.E(palette.Body, palette.Outline, 48, 14, 25, 8);
                g.Arc(palette.Outline, 48, 83, 25, 8, 0, 180);
                g.L(palette.Outline, 23, 14, 23, 83); g.L(palette.Outline, 73, 14, 73, 83);
                g.L(palette.Fine, 31, 25, 31, 77);
                for (int i = 0; i <= 5; i++) g.L(palette.Fine, 79, 20 + 12 * i, i % 5 == 0 ? 89 : 85, 20 + 12 * i);
                if (ports) { g.Port(10, 34, 23, 34); g.Port(73, 76, 90, 76); }
                g.L(palette.Outline, 32, 90, 32, 95); g.L(palette.Outline, 63, 90, 63, 95);
                break;
            case HmiSymbol.Pump:
                if (ports) { g.Port(5, 58, 25, 58); g.Port(70, 22, 93, 22); }
                g.R(palette.Body, 50, 15, 23, 22, 3);
                g.E(palette.Body, palette.Outline, 47, 55, 27, 29);
                g.E(palette.Surface, palette.Fine, 47, 55, 20, 22);
                g.Rotor(47, 55, 17, phase, 3, signalLine);
                g.E(signal, null, 47, 55, 4, 4);
                g.L(palette.Outline, 32, 79, 28, 89); g.L(palette.Outline, 61, 80, 66, 89);
                g.R(palette.Body, 22, 88, 50, 6, 2); g.L(palette.Fine, 25, 94, 69, 94);
                break;
            case HmiSymbol.Motor:
                g.R(palette.Body, 17, 30, 59, 48, 9);
                g.R(palette.Highlight, 21, 34, 50, 8, 3);
                g.E(palette.Body, palette.Outline, 20, 54, 10, 24);
                g.E(palette.Surface, palette.Fine, 20, 54, 5, 16);
                g.R(palette.Body, 40, 18, 23, 12, 2);
                g.L(palette.Outline, 44, 18, 44, 14); g.L(palette.Outline, 59, 18, 59, 14);
                for (int i = 0; i < 6; i++) g.L(palette.Fine, 34 + i * 6, 43, 34 + i * 6, 70);
                g.L(palette.Outline, 76, 44, 76, 64); g.R(palette.Body, 77, 49, 17, 10, 1);
                g.L(palette.Outline, 34, 79, 30, 89); g.L(palette.Outline, 66, 79, 71, 89);
                g.R(palette.Body, 24, 88, 54, 6, 1); g.L(signalLine, 40, 74, 65, 74);
                break;
            case HmiSymbol.Valve:
            case HmiSymbol.ControlValve:
            case HmiSymbol.CheckValve:
            case HmiSymbol.ButterflyValve:
                if (ports) { g.Port(4, 65, 21, 65); g.Port(79, 65, 96, 65); }
                if (symbol == HmiSymbol.ButterflyValve)
                {
                    g.E(palette.Body, palette.Outline, 50, 65, 27, 24);
                    g.E(palette.Surface, palette.Fine, 50, 65, 22, 19);
                    g.L(signalLine, active ? 31 : 40, active ? 65 : 49, active ? 69 : 60, active ? 65 : 81);
                }
                else if (symbol == HmiSymbol.CheckValve)
                {
                    g.Poly(signalLine, new(23, 43), new(74, 65), new(23, 87), new(23, 43));
                    g.L(palette.Strong, 74, 43, 74, 87); g.L(palette.Fine, 25, 39, 76, 39);
                }
                else
                {
                    g.Poly(signalLine, new(23, 44), new(77, 85), new(77, 44), new(23, 85), new(23, 44));
                }
                if (symbol != HmiSymbol.CheckValve)
                {
                    g.L(palette.Outline, 50, 43, 50, 25);
                    if (symbol == HmiSymbol.ControlValve)
                    {
                        g.E(palette.Body, palette.Outline, 50, 19, 22, 12);
                        g.R(palette.Surface, 25, 20, 50, 10, 0); g.L(palette.Outline, 28, 20, 72, 20);
                        g.L(palette.Fine, 37, 28, 63, 28);
                    }
                    else
                    {
                        g.E(null, palette.Outline, 50, 20, 22, 6);
                        g.L(palette.Fine, 29, 20, 71, 20); g.L(palette.Fine, 50, 14, 50, 26);
                    }
                }
                break;
            case HmiSymbol.Pipe:
                g.R(palette.Body, 2, 40, 96, 20, 3);
                g.R(palette.Surface, 2, 44, 96, 12, 2);
                g.L(palette.Fine, 2, 40, 98, 40); g.L(palette.Fine, 2, 60, 98, 60);
                if (active && !unknown)
                    for (int i = 0; i < 6; i++)
                    {
                        float x = 5 + (i * 15 + phase * 15) % 87;
                        g.Poly(signalLine, new(x, 46), new(x + 4, 50), new(x, 54));
                    }
                if (ports) { g.L(palette.Outline, 3, 33, 3, 67); g.L(palette.Outline, 97, 33, 97, 67); }
                break;
            case HmiSymbol.Conveyor:
                g.R(palette.Body, 4, 41, 92, 30, 12);
                g.R(palette.Surface, 7, 45, 86, 22, 10);
                for (int i = 0; i < 6; i++) g.E(palette.Body, palette.Outline, 14 + i * 14, 56, 6, 8);
                g.L(palette.Outline, 14, 73, 10, 89); g.L(palette.Outline, 84, 73, 88, 89);
                g.L(signalLine, 13, 38, 87, 38);
                for (int i = 0; i < 3; i++)
                {
                    float x = 13 + (i * 27 + phase * 27) % 67;
                    g.R(palette.Body, x, 20, 15, 17, 1); g.L(palette.Fine, x + 7, 20, x + 7, 28);
                }
                break;
            case HmiSymbol.HeatExchanger:
                g.R(palette.Body, 12, 25, 76, 49, 17); g.R(palette.Surface, 16, 30, 68, 39, 14);
                g.Poly(fluidLine, new(5, 43), new(30, 43), new(68, 43), new(34, 56), new(73, 56), new(95, 56));
                g.Port(32, 9, 32, 25); g.Port(68, 74, 68, 91);
                g.L(palette.Outline, 17, 28, 17, 70); g.L(palette.Outline, 83, 28, 83, 70);
                g.L(palette.Outline, 29, 76, 24, 86); g.L(palette.Outline, 73, 76, 78, 86);
                break;
            case HmiSymbol.Filter:
            case HmiSymbol.Strainer:
                if (symbol == HmiSymbol.Strainer)
                {
                    g.Poly(palette.Outline, new(13, 35), new(76, 35), new(76, 48), new(55, 48), new(78, 79), new(62, 90), new(30, 48), new(13, 48), new(13, 35));
                    for (int i = 0; i < 4; i++) g.L(palette.Fine, 40 + i * 6, 48 + i * 8, 50 + i * 6, 42 + i * 8);
                    if (ports) { g.Port(3, 41, 13, 41); g.Port(76, 41, 97, 41); }
                }
                else
                {
                    g.R(palette.Body, 24, 17, 52, 67, 5); g.R(palette.Surface, 29, 22, 42, 57, 2);
                    for (int i = 0; i < 6; i++) g.L(palette.Fine, 32 + i * 7, 25, 32 + i * 7, 76);
                    for (int i = 0; i < 5; i++) g.L(palette.Grid, 30, 28 + i * 10, 70, 28 + i * 10);
                    if (ports) { g.Port(5, 51, 24, 51); g.Port(76, 51, 95, 51); }
                    g.Port(50, 84, 50, 96);
                }
                break;
            case HmiSymbol.Compressor:
                g.E(palette.Body, palette.Outline, 50, 51, 32, 34);
                g.E(palette.Surface, palette.Fine, 50, 51, 26, 28);
                g.Poly(signalLine, new(35, 32), new(72, 51), new(35, 70), new(35, 32));
                if (ports) { g.Port(3, 51, 18, 51); g.Port(82, 51, 97, 51); }
                g.L(palette.Outline, 32, 81, 27, 92); g.L(palette.Outline, 68, 81, 73, 92);
                g.R(palette.Body, 23, 91, 54, 5, 1);
                break;
            case HmiSymbol.Fan:
                g.E(palette.Body, palette.Outline, 50, 49, 36, 38);
                g.E(palette.Surface, palette.Fine, 50, 49, 31, 33);
                g.Rotor(50, 49, 25, phase, 4, signalLine);
                g.E(signal, palette.Outline, 50, 49, 6, 6);
                g.L(palette.Outline, 32, 84, 25, 94); g.L(palette.Outline, 68, 84, 75, 94);
                break;
            case HmiSymbol.Heater:
                g.R(palette.Body, 10, 20, 80, 65, 5); g.R(palette.Surface, 14, 24, 72, 57, 3);
                for (int i = 0; i < 3; i++)
                {
                    float x = 31 + i * 19;
                    g.Poly(fluidLine, new(x, 73), new(x - 5, 60), new(x + 5, 47), new(x, 32));
                    g.Poly(palette.Outline, new(x - 5, 38), new(x, 31), new(x + 5, 38));
                }
                if (ports) { g.Port(2, 53, 10, 53); g.Port(90, 53, 98, 53); }
                break;
            case HmiSymbol.Thermometer:
                g.R(palette.Body, 38, 7, 15,  73, 7);
                g.R(palette.Surface, 42, 11, 7, 68, 3);
                if (fraction > 0) g.R(fluid, 43, 76 - 61 * fraction, 5, 61 * fraction, 2);
                g.E(palette.Body, palette.Outline, 45.5f, 82, 14, 14);
                g.E(fluid, null, 45.5f, 82, 9, 9);
                for (int i = 0; i <= 6; i++) g.L(palette.Fine, 60, 14 + i * 9, i % 3 == 0 ? 75 : 69, 14 + i * 9);
                break;
            case HmiSymbol.Boiler:
                g.R(palette.Body, 15, 15, 70, 61, 14); g.R(palette.Surface, 19, 19, 62, 53, 11);
                if (fraction > 0) g.R(fluid, 24, 66 - fraction * 39, 52, fraction * 39, 4);
                g.Port(34, 3, 34, 15); g.Port(85, 37, 96, 37);
                g.R(palette.Body, 30, 76, 40, 17, 3);
                g.Poly(signalLine, new(39, 89), new(44, 80), new(50, 88), new(56, 78), new(62, 89));
                g.L(palette.Outline, 20, 80, 17, 95); g.L(palette.Outline, 80, 80, 83, 95);
                break;
            case HmiSymbol.CoolingTower:
                g.Poly(palette.Outline, new(20, 9), new(80, 9), new(72, 41), new(89, 91), new(11, 91), new(28, 41), new(20, 9));
                g.E(palette.Body, palette.Outline, 50, 18, 25, 7);
                g.L(palette.Fine, 30, 18, 70, 18);
                for (int i = 0; i < 5; i++) g.L(fluidLine, 26 - i * 2,  50 + i * 7, 74 + i * 2, 50 + i * 7);
                g.Port(3, 73, 17, 73); g.Port(84, 78, 97, 78);
                break;
            case HmiSymbol.Silo:
            case HmiSymbol.Hopper:
                float upper = symbol == HmiSymbol.Silo ? 10 : 28;
                g.R(palette.Body,   20, upper, 60, 52 - upper, 3);
                g.Poly(palette.Outline, new(20, upper), new(80, upper), new(80,  60), new(56, 86), new(56, 94), new(44, 94), new(44, 86), new(20, 60), new(20, upper));
                if (symbol == HmiSymbol.Silo) g.E(palette.Body, palette.Outline, 50, upper, 30, 7);
                g.L(fluidLine, 25, 56 - 35 * fraction, 75, 56 - 35 * fraction);
                g.L(palette.Outline, 23, 67, 23, 95); g.L(palette.Outline, 77, 67, 77, 95);
                break;
            case HmiSymbol.Reactor:
            case HmiSymbol.Agitator:
            case HmiSymbol.Separator:
                g.R(palette.Body, 23, 25, 54, 61, 16); g.R(palette.Surface, 27, 29, 46, 53, 13);
                if (symbol != HmiSymbol.Separator)
                {
                    g.R(palette.Body, 42, 3, 16, 14, 3); g.L(palette.Outline, 50, 17, 50, 67);
                    float sweep = symbol == HmiSymbol.Agitator ? MathF.Cos(phase * MathF.Tau) * 15 : 15;
                    g.L(signalLine, 50 - sweep, 63, 50 + sweep, 72); g.L(signalLine, 50 - sweep, 72, 50 + sweep, 63);
                    if (symbol == HmiSymbol.Reactor)
                        for (int i = 0; i < 4; i++) g.L(palette.Fine, 28, 40 + i * 9, 33, 43 + i * 9);
                }
                else
                {
                    g.L(fluidLine, 29, 62, 71, 62); g.L(palette.Fine, 29, 48, 71, 48);
                    g.Port(50, 8, 50, 25); g.Port(50, 86, 50, 98);
                    g.Poly(palette.Fine, new(36, 37), new(45, 46), new(55, 37), new(65, 46));
                }
                if (ports) { g.Port(5,   40, 23, 40); g.Port(77, 74, 95, 74); }
                g.L(palette.Outline, 30, 84, 26, 95); g.L(palette.Outline, 70, 84, 74, 95);
                break;
            case HmiSymbol.Gauge:
                g.Arc(palette.Outline, 50, 57, 40, 40, 145, 250);
                g.Arc(palette.Grid, 50, 57, 32, 32, 145, 250);
                for (int i = 0; i <= 20; i++)
                {
                    float angle = (145 + i * 12.5f) * MathF.PI / 180;
                    float inner = i % 5 == 0 ? 32 : 35;
                    g.L(palette.Fine, 50 + MathF.Cos(angle) * inner, 57 + MathF.Sin(angle) * inner,
                        50 + MathF.Cos(angle) * 40, 57 + MathF.Sin(angle) * 40);
                }
                if (!unknown)
                {
                    float angle = (145 + fraction * 250) * MathF.PI / 180;
                    g.L(palette.Strong, 50 - MathF.Cos(angle) * 6, 57 - MathF.Sin(angle) * 6,
                        50 + MathF.Cos(angle) * 28, 57 + MathF.Sin(angle) * 28);
                }
                g.E(unknown ? palette.Track : palette.Accent, null, 50, 57, 5, 5);
                break;
            case HmiSymbol.BarGraph:
                g.R(palette.Track, 5, 36, 90, 22, 4); g.R(palette.Surface, 6, 37, 88, 20, 3);
                if (fraction > 0 && !unknown) g.R(palette.Accent, 7, 39, 86 * fraction, 16, 2);
                for (int i = 0; i <= 10; i++) g.L(palette.Fine, 7 + i * 8.6f, 64, 7 + i * 8.6f, i % 5 == 0 ? 75 : 70);
                break;
            case HmiSymbol.Indicator:
                g.E(palette.Body, palette.Outline, 50, 50, 26, 32);
                g.E(palette.Surface, palette.Fine, 50, 50, 20, 26);
                if (unknown) g.Poly(signalLine, new(43, 37), new(57, 37), new(57, 46), new(50, 52), new(50, 57));
                else if (active) g.Poly(signalLine, new(39, 49), new(47, 59), new(62,   40));
                else g.R(signal, 43, 40, 14, 20, 2);
                if (unknown) g.E(signal, null, 50, 64, 1.5f, 2);
                break;
            case HmiSymbol.FlowMeter:
            case HmiSymbol.PressureTransmitter:
            case HmiSymbol.LevelTransmitter:
                g.E(palette.Body, palette.Outline, 50, 40, 27, 28);
                g.E(palette.Surface, palette.Fine, 50, 40, 23, 24);
                g.L(palette.Fine, 25, 40, 75, 40);
                if (symbol == HmiSymbol.FlowMeter)
                {
                    g.Poly(fluidLine, new(35,  30), new(35,  22), new(49, 22));
                    g.L(fluidLine, 35, 28, 46, 28); g.Port(4, 80, 96, 80); g.L(palette.Outline, 50, 68, 50, 80);
                }
                else if (symbol == HmiSymbol.PressureTransmitter)
                {
                    g.Poly(fluidLine, new(42, 34), new(42, 20), new(56, 20), new(56, 28), new(42, 28));
                    g.Port(50, 68, 50, 94); g.L(palette.Outline, 42, 90, 58, 90);
                }
                else
                {
                    g.Poly(fluidLine, new(43, 20), new(43, 33), new(57, 33));
                    g.L(palette.Outline, 50, 68, 50, 94); g.L(palette.Fine, 40, 79, 60, 79); g.L(palette.Fine, 40, 85, 60, 85);
                }
                break;
            case HmiSymbol.NumericDisplay:
            case HmiSymbol.NumericInput:
                g.R(palette.Body, 8, 16, 84,  60, 4); g.R(palette.Surface, 12, 21, 76, 50, 3);
                g.L(fluidLine,  20,  40, 38, 40); g.L(fluidLine, 45, 40,  60, 40);
                g.L(fluidLine, 20, 54,  38, 54); g.L(fluidLine, 45, 54, 60, 54);
                if (symbol == HmiSymbol.NumericInput) { g.L(palette.Outline, 74, 36, 74, 58); g.L(palette.Outline, 68, 47, 80, 47); }
                break;
            case HmiSymbol.Trend:
                for (int i = 0; i <= 3; i++) g.L(palette.Grid, 8, 15 + i * 23, 95, 15 + i * 23);
                g.Poly(fluidLine, new(8, 73), new(20, 70), new(29,  40), new(40,  54), new(51, 48), new(65, 18), new(75,  34), new(95, 29));
                break;
            case HmiSymbol.AlarmBanner:
            case HmiSymbol.AlarmList:
                g.Poly(signalLine, new(50, 9), new(83, 74), new(17, 74), new(50, 9));
                g.L(signalLine, 50, 31, 50, 52); g.E(signal, null, 50, 63, 2, 3);
                if (symbol == HmiSymbol.AlarmList) { g.L(palette.Fine, 12, 85, 88, 85); g.L(palette.Fine, 12, 93,  70, 93); }
                break;
            case HmiSymbol.ToggleSwitch:
                g.R(palette.Body, 8, 30, 84,  40, 20);
                g.R(active && !unknown ? signal : palette.Track, 12, 34, 76, 32, 16);
                g.E(palette.Surface, palette.Outline, unknown ? 50 : active ? 73 : 27, 50, 13, 13);
                break;
            case HmiSymbol.PushButton:
            case HmiSymbol.NavigationButton:
            case HmiSymbol.RecipeButton:
                g.R(palette.Body, 7, 24, 86,  52, 8);
                if (symbol == HmiSymbol.NavigationButton)
                {
                    g.L(palette.Strong, 30, 50, 70, 50); g.Poly(palette.Strong, new(57, 36), new(70, 50), new(57, 64));
                }
                else if (symbol == HmiSymbol.RecipeButton)
                {
                    g.R(palette.Surface, 35, 32, 30, 36, 2); g.L(palette.Fine,  40, 41, 60, 41); g.L(palette.Fine, 40, 50, 60, 50); g.L(palette.Fine, 40, 59, 55, 59);
                }
                else { g.E(null, fluidLine, 50, 52, 14, 18); g.L(fluidLine, 50, 31, 50, 50); }
                break;
            case HmiSymbol.Label:
                g.L(palette.Strong, 26,  20, 74, 20); g.L(palette.Strong, 50, 20, 50, 82);
                g.L(palette.Strong, 40, 82, 60, 82); break;
            case HmiSymbol.Rectangle:
                g.R(palette.Body, 9, 13, 82, 74, 5); g.R(palette.Surface, 12, 27, 76, 57, 3);
                g.L(palette.Fine, 12, 25, 88, 25); break;
        }
    }

    private readonly ref struct Painter(DrawingContext context, Rect bounds, HmiPalette palette,
        bool showPorts, int quarterTurns, bool mirrorHorizontal, bool mirrorVertical)
    {
        private Vector2 P(float x, float y) => HmiPortLayout.TransformPoint(x, y, bounds, quarterTurns, mirrorHorizontal, mirrorVertical);
        internal void L(Pen pen, float x1, float y1, float x2, float y2) => context.DrawLine(pen, P(x1, y1), P(x2, y2));
        internal void Poly(Pen pen, params ReadOnlySpan<Vector2> points)
        {
            for (int i = 1; i < points.Length; i++) L(pen, points[i - 1].X, points[i - 1].Y, points[i].X, points[i].Y);
        }
        internal void R(Brush fill, float x, float y, float width, float height, float radius)
        {
            if (width <= 0 || height <= 0) return;
            var origin = P(x, y);
            var opposite = P(x + width, y + height);
            context.FillRoundedRectangle(fill, new Rect(Math.Min(origin.X, opposite.X), Math.Min(origin.Y, opposite.Y), Math.Abs(opposite.X - origin.X), Math.Abs(opposite.Y - origin.Y)),
                Math.Min(radius, Math.Min(width, height) * 0.5f) * Math.Min(bounds.Width, bounds.Height) * 0.01f);
        }
        internal void E(Brush? fill, Pen? pen, float x, float y, float rx, float ry)
        {
            if ((quarterTurns & 1) != 0) (rx, ry) = (ry, rx);
            context.DrawEllipse(fill, pen, P(x, y), rx * bounds.Width * 0.01f, ry * bounds.Height * 0.01f);
        }
        internal void Arc(Pen pen, float x, float y, float rx, float ry, float start, float sweep)
        {
            int steps = Math.Clamp((int)MathF.Ceiling(MathF.Abs(sweep) / 5), 1, 72);
            float angle = start * MathF.PI / 180;
            Vector2 last = P(x + MathF.Cos(angle) * rx, y + MathF.Sin(angle) * ry);
            for (int i = 1; i <= steps; i++)
            {
                angle = (start + sweep * i / steps) * MathF.PI / 180;
                var point = P(x + MathF.Cos(angle) * rx, y + MathF.Sin(angle) * ry);
                context.DrawLine(pen, last, point); last = point;
            }
        }
        internal void UprightLine(Pen pen, float x1, float y1, float x2, float y2) => context.DrawLine(pen,
            new(bounds.X + x1 * bounds.Width * .01f, bounds.Y + y1 * bounds.Height * .01f),
            new(bounds.X + x2 * bounds.Width * .01f, bounds.Y + y2 * bounds.Height * .01f));
        internal void Lead(float x1, float y1, float x2, float y2) { if (showPorts) L(palette.Outline, x1, y1, x2, y2); }
        internal void Port(float x1, float y1, float x2, float y2)
        {
            if (!showPorts) return;
            L(palette.Outline, x1, y1, x2, y2);
            float nx = y1 == y2 ? 0 : 4, ny = y1 == y2 ? 4 : 0;
            L(palette.Outline, x1 - nx, y1 - ny, x1 + nx, y1 + ny);
            L(palette.Outline, x2 - nx, y2 - ny, x2 + nx, y2 + ny);
        }
        internal void Rotor(float x, float y, float radius, float phase, int count, Pen pen)
        {
            for (int i = 0; i < count; i++)
            {
                float angle = (phase + (float)i / count) * MathF.Tau;
                float dx = MathF.Cos(angle), dy = MathF.Sin(angle);
                Poly(pen, new(x + dx * 5, y + dy * 5), new(x + dx * radius - dy * radius * 0.32f, y + dy * radius + dx * radius * 0.32f),
                    new(x + dx * radius + dy * radius * 0.14f, y + dy * radius - dx * radius * 0.14f), new(x + dx * 5, y + dy * 5));
            }
        }
    }
}
