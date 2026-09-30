namespace ProGPU.Hmi;

/// <summary>
/// Stable nozzle identities from ProGPU's original retained equipment vocabulary.
/// These are diagram semantics, not vendor-specific process or controller type declarations.
/// </summary>
public static class HmiSymbolPorts
{
    private static HmiSymbolPort P(string id, string name, float x, float y, HmiPortDirection direction) => new(id, name, new(x, y), direction);
    private static IReadOnlyList<HmiSymbolPort> Ports(params HmiSymbolPort[] ports) => Array.AsReadOnly(ports);
    private static readonly IReadOnlyList<HmiSymbolPort> Empty = Array.Empty<HmiSymbolPort>();
    private static readonly IReadOnlyList<HmiSymbolPort> Tank = Ports(
        P("inlet", "Inlet", 10, 34, HmiPortDirection.Left), P("outlet", "Outlet", 90, 76, HmiPortDirection.Right));
    private static readonly IReadOnlyList<HmiSymbolPort> Pump = Ports(
        P("inlet", "Suction", 5, 58, HmiPortDirection.Left), P("outlet", "Discharge", 93, 22, HmiPortDirection.Right));
    private static readonly IReadOnlyList<HmiSymbolPort> Valve = Ports(
        P("inlet", "Inlet", 4, 65, HmiPortDirection.Left), P("outlet", "Outlet", 96, 65, HmiPortDirection.Right));
    private static readonly IReadOnlyList<HmiSymbolPort> Pipe = Ports(
        P("inlet", "Start", 2, 50, HmiPortDirection.Left), P("outlet", "End", 98, 50, HmiPortDirection.Right));
    private static readonly IReadOnlyList<HmiSymbolPort> Exchanger = Ports(
        P("inlet", "Tube inlet", 5, 43, HmiPortDirection.Left), P("outlet", "Tube outlet", 95, 56, HmiPortDirection.Right),
        P("shell-inlet", "Shell inlet", 32, 9, HmiPortDirection.Top), P("shell-outlet", "Shell outlet", 68, 91, HmiPortDirection.Bottom));
    private static readonly IReadOnlyList<HmiSymbolPort> Filter = Ports(
        P("inlet", "Inlet", 5, 51, HmiPortDirection.Left), P("outlet", "Outlet", 95, 51, HmiPortDirection.Right),
        P("drain", "Drain", 50, 96, HmiPortDirection.Bottom));
    private static readonly IReadOnlyList<HmiSymbolPort> Strainer = Ports(
        P("inlet", "Inlet", 3, 41, HmiPortDirection.Left), P("outlet", "Outlet", 97, 41, HmiPortDirection.Right));
    private static readonly IReadOnlyList<HmiSymbolPort> Compressor = Ports(
        P("inlet", "Suction", 3, 51, HmiPortDirection.Left), P("outlet", "Discharge", 97, 51, HmiPortDirection.Right));
    private static readonly IReadOnlyList<HmiSymbolPort> Heater = Ports(
        P("inlet", "Inlet", 2, 53, HmiPortDirection.Left), P("outlet", "Outlet", 98, 53, HmiPortDirection.Right));
    private static readonly IReadOnlyList<HmiSymbolPort> Boiler = Ports(
        P("inlet", "Top connection", 34, 3, HmiPortDirection.Top), P("outlet", "Side connection", 96, 37, HmiPortDirection.Right));
    private static readonly IReadOnlyList<HmiSymbolPort> Tower = Ports(
        P("inlet", "Inlet", 3, 73, HmiPortDirection.Left), P("outlet", "Outlet", 97, 78, HmiPortDirection.Right));
    private static readonly IReadOnlyList<HmiSymbolPort> Vessel = Ports(
        P("inlet", "Inlet", 5, 40, HmiPortDirection.Left), P("outlet", "Outlet", 95, 74, HmiPortDirection.Right));
    private static readonly IReadOnlyList<HmiSymbolPort> Separator = Ports(
        P("inlet", "Inlet", 5, 40, HmiPortDirection.Left), P("outlet", "Outlet", 95, 74, HmiPortDirection.Right),
        P("vent", "Vent", 50, 8, HmiPortDirection.Top), P("drain", "Drain", 50, 98, HmiPortDirection.Bottom));

    private static readonly IReadOnlyList<HmiSymbolPort> FlowMeter = Ports(
        P("inlet", "Inlet", 4, 80, HmiPortDirection.Left), P("outlet", "Outlet", 96, 80, HmiPortDirection.Right));
    private static readonly IReadOnlyList<HmiSymbolPort> Transmitter = Ports(
        P("process", "Process connection", 50, 94, HmiPortDirection.Bottom));

    public static IReadOnlyList<HmiSymbolPort> GetPorts(HmiSymbol symbol) => symbol switch
    {
        HmiSymbol.Tank => Tank, HmiSymbol.Pump => Pump,
        HmiSymbol.Valve or HmiSymbol.ControlValve or HmiSymbol.CheckValve or HmiSymbol.ButterflyValve => Valve,
        HmiSymbol.Pipe => Pipe, HmiSymbol.HeatExchanger => Exchanger, HmiSymbol.Filter => Filter,
        HmiSymbol.Strainer => Strainer, HmiSymbol.Compressor => Compressor, HmiSymbol.Heater => Heater,
        HmiSymbol.Boiler => Boiler, HmiSymbol.CoolingTower => Tower,
        HmiSymbol.Reactor or HmiSymbol.Agitator => Vessel, HmiSymbol.Separator => Separator,
        HmiSymbol.FlowMeter => FlowMeter, HmiSymbol.PressureTransmitter or HmiSymbol.LevelTransmitter => Transmitter,
        _ => Empty
    };

    public static HmiSymbolPort GetPort(HmiSymbol symbol, string id) =>
        GetPorts(symbol).FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException($"{symbol} has no port '{id}'.", nameof(id));
}
