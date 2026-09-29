using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Shared layout/preview classification, not inferred from enum ordinals or translated names.</summary>
public static class HmiSymbolTraits
{
    public static bool IsEquipment(HmiSymbol symbol) => symbol is
        HmiSymbol.Tank or HmiSymbol.Pump or HmiSymbol.Valve or HmiSymbol.Motor or HmiSymbol.Pipe or
        HmiSymbol.Conveyor or HmiSymbol.HeatExchanger or HmiSymbol.Filter or HmiSymbol.Compressor or
        HmiSymbol.Fan or HmiSymbol.Heater or HmiSymbol.Boiler or HmiSymbol.CoolingTower or
        HmiSymbol.ControlValve or HmiSymbol.CheckValve or HmiSymbol.ButterflyValve or HmiSymbol.Agitator or
        HmiSymbol.Silo or HmiSymbol.Hopper or HmiSymbol.Separator or HmiSymbol.Reactor or HmiSymbol.Strainer;

    public static bool IsRotating(HmiSymbol symbol) => symbol is
        HmiSymbol.Pump or HmiSymbol.Motor or HmiSymbol.Fan or HmiSymbol.Compressor or HmiSymbol.Agitator;

    public static bool IsCommand(HmiSymbol symbol) => symbol is
        HmiSymbol.PushButton or HmiSymbol.NavigationButton or HmiSymbol.RecipeButton;

    public static bool IsBinary(HmiSymbol symbol) => IsRotating(symbol) || symbol is
        HmiSymbol.Valve or HmiSymbol.CheckValve or HmiSymbol.ButterflyValve or HmiSymbol.ToggleSwitch or
        HmiSymbol.Indicator or HmiSymbol.Conveyor;

    public static bool IsCard(HmiSymbol symbol, HmiPresentation presentation) => presentation switch
    {
        HmiPresentation.Card => symbol != HmiSymbol.Label,
        HmiPresentation.Process => false,
        _ => !IsEquipment(symbol) && symbol != HmiSymbol.Label
    };
}
