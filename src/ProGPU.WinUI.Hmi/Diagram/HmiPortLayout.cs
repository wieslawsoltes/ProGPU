using System.Numerics;
using ProGPU.Hmi;
using ProGPU.Scene;

namespace ProGPU.WinUI.Hmi;

/// <summary>Exact shared glyph fitting/orientation for both retained ink and semantic nozzle anchors.</summary>
public static class HmiPortLayout
{
    public static Rect FitGlyph(HmiSymbol symbol, Rect bounds, int quarterTurns)
    {
        float aspect = symbol switch
        {
            HmiSymbol.Pipe => 5,
            HmiSymbol.Conveyor or HmiSymbol.HeatExchanger => 1.8f,
            HmiSymbol.Valve or HmiSymbol.ControlValve or HmiSymbol.CheckValve or HmiSymbol.ButterflyValve => 1.35f,
            HmiSymbol.Tank or HmiSymbol.Silo or HmiSymbol.Thermometer => 0.75f,
            HmiSymbol.BarGraph or HmiSymbol.ToggleSwitch or HmiSymbol.Trend => 2,
            _ => 1.1f
        };
        if ((quarterTurns & 1) != 0) aspect = 1 / aspect;
        float width = symbol == HmiSymbol.Pipe ? bounds.Width : Math.Min(bounds.Width, bounds.Height * aspect);
        float height = symbol == HmiSymbol.Pipe ? bounds.Height : width / aspect;
        return new(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height);
    }

    public static Vector2 TransformPoint(float x, float y, Rect fittedBounds, int quarterTurns, bool mirrorHorizontal, bool mirrorVertical)
    {
        if (mirrorHorizontal) x = 100 - x;
        if (mirrorVertical) y = 100 - y;
        (x, y) = (quarterTurns & 3) switch { 1 => (100 - y, x), 2 => (100 - x, 100 - y), 3 => (y, 100 - x), _ => (x, y) };
        return new(fittedBounds.X + x * fittedBounds.Width * 0.01f, fittedBounds.Y + y * fittedBounds.Height * 0.01f);
    }

    public static HmiRouteBox GetBounds(HmiElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var layout = HmiVisualLayout.Calculate(element.Symbol, element.Width, element.Height, element.Appearance);
        Rect b = FitGlyph(element.Symbol, layout.Glyph, element.Appearance.QuarterTurns);
        if (HmiSymbolTraits.IsCard(element.Symbol, element.Appearance.Presentation) ||
            !HmiSymbolTraits.IsEquipment(element.Symbol) && HmiSymbolPorts.GetPorts(element.Symbol).Count == 0 || b.Width < 4 || b.Height < 4)
            b = new(0, 0, element.Width, element.Height);
        return new(element.X + b.X, element.Y + b.Y, element.X + b.Right, element.Y + b.Bottom);
    }

    public static bool HasRoutableGlyph(HmiElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var layout = HmiVisualLayout.Calculate(element.Symbol, element.Width, element.Height, element.Appearance);
        var bounds = FitGlyph(element.Symbol, layout.Glyph, element.Appearance.QuarterTurns);
        return bounds.Width >= 4 && bounds.Height >= 4;
    }

    public static HmiRouteTerminal Resolve(HmiElement element, string portId)
    {
        ArgumentNullException.ThrowIfNull(element);
        var port = HmiSymbolPorts.GetPort(element.Symbol, portId);
        var appearance = element.Appearance;
        var layout = HmiVisualLayout.Calculate(element.Symbol, element.Width, element.Height, appearance);
        var bounds = FitGlyph(element.Symbol, layout.Glyph, appearance.QuarterTurns);
        if (bounds.Width < 4 || bounds.Height < 4) throw new InvalidOperationException("Enlarge the component to expose a routable glyph.");
        var point = TransformPoint(port.Position.X, port.Position.Y, bounds, appearance.QuarterTurns, appearance.MirrorHorizontal, appearance.MirrorVertical);
        int direction = (int)port.Direction;
        if (appearance.MirrorHorizontal && direction is 0 or 2) direction = 2 - direction;
        if (appearance.MirrorVertical && direction is 1 or 3) direction = 4 - direction;
        direction = (direction + appearance.QuarterTurns) & 3;
        var ownedBounds = GetBounds(element);
        var documentPoint = new HmiPoint(element.X + point.X, element.Y + point.Y);
        // Card presentations expose projected border ports. A route must never paint through
        // the card's title/readout or pretend to reach a nozzle hidden below an opaque card.
        if (HmiSymbolTraits.IsCard(element.Symbol, appearance.Presentation)) documentPoint = (HmiPortDirection)direction switch
        {
            HmiPortDirection.Left => new(ownedBounds.Left, documentPoint.Y),
            HmiPortDirection.Right => new(ownedBounds.Right, documentPoint.Y),
            HmiPortDirection.Top => new(documentPoint.X, ownedBounds.Top),
            _ => new(documentPoint.X, ownedBounds.Bottom)
        };
        return new(element.Id, documentPoint, (HmiPortDirection)direction, ownedBounds);
    }
}
