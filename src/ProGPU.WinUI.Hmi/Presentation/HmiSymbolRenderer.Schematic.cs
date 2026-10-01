using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

internal static partial class HmiSymbolRenderer
{
    // Original diagram vocabulary: reference-state geometry, not a circuit solver or certified symbol database.
    private static void DrawSchematic(Painter g, HmiSymbol symbol, HmiPalette p, HmiInstrumentLocation location)
    {
        switch (symbol)
        {
            case HmiSymbol.InstrumentBubble:
            case HmiSymbol.ControlFunction:
                if (symbol == HmiSymbol.ControlFunction)
                {
                    g.R(p.Surface, 11, 11, 78, 78, 0);
                    g.Poly(p.Outline, new(11, 11), new(89, 11), new(89, 89), new(11, 89), new(11, 11));
                }
                g.E(p.Surface, p.Strong, 50, 50, 34, 34);
                if (location == HmiInstrumentLocation.PanelFront) g.UprightLine(p.Outline, 16, 50, 84, 50);
                else if (location == HmiInstrumentLocation.PanelRear)
                    for (int x = 18; x < 82; x += 12) g.UprightLine(p.Outline, x, 50, x + 7, 50);
                g.Lead(50, symbol == HmiSymbol.ControlFunction ? 89 : 84, 50, 95);
                break;
            case HmiSymbol.NormallyOpenContact:
            case HmiSymbol.NormallyClosedContact:
                g.Lead(4, 50, 36, 50); g.Lead(64, 50, 96, 50);
                g.L(p.Strong, 36, 20, 36, 80); g.L(p.Strong, 64, 20, 64, 80);
                if (symbol == HmiSymbol.NormallyClosedContact) g.L(p.Strong, 26, 86, 74, 14);
                break;
            case HmiSymbol.RelayCoil:
                g.Lead(4, 50, 24, 50); g.Lead(76, 50, 96, 50);
                g.R(p.Surface, 24, 22, 52, 56, 0);
                g.Poly(p.Strong, new(24, 22), new(76, 22), new(76, 78), new(24, 78), new(24, 22));
                g.L(p.Fine, 32, 67, 68, 33);
                break;
            case HmiSymbol.CircuitBreaker:
                g.Lead(4, 50, 29, 50); g.Lead(72, 50, 96, 50);
                g.E(p.Surface, p.Outline, 29, 50, 3, 4);
                g.E(p.Surface, p.Outline, 72, 50, 3, 4);
                g.L(p.Strong, 29, 50, 63, 21);
                g.Poly(p.Fine, new(39, 70), new(47, 70), new(47, 60), new(57, 60));
                g.L(p.Fine, 46, 55, 46, 43);
                break;
            case HmiSymbol.Transformer:
                g.Lead(4, 50, 22, 50); g.Lead(78, 50, 96, 50);
                g.E(null, p.Strong, 39, 50, 19, 28); g.E(null, p.Strong, 61, 50, 19, 28);
                break;
            case HmiSymbol.ProtectiveEarth:
                g.Lead(50, 5, 50, 50);
                g.L(p.Strong, 17, 50, 83, 50); g.L(p.Strong, 29, 66, 71, 66);
                g.L(p.Strong, 41, 82, 59, 82);
                break;
        }
    }
}
