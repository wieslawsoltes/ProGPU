using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

public sealed record HmiControlDescriptor(HmiSymbol Symbol, string Name, string Category, float Width, float Height);

public static class HmiControlCatalog
{
    public static IReadOnlyList<HmiControlDescriptor> Items { get; } = Array.AsReadOnly(new HmiControlDescriptor[]
    {
        new(HmiSymbol.HeatExchanger, "Heat exchanger", "Equipment", 220, 170),
        new(HmiSymbol.Filter, "Process filter", "Equipment", 170, 170),
        new(HmiSymbol.Compressor, "Compressor", "Equipment", 200, 175),
        new(HmiSymbol.Fan, "Ventilation fan", "Equipment", 170, 175),
        new(HmiSymbol.Heater, "Process heater", "Equipment", 180, 150),
        new(HmiSymbol.Thermometer, "Thermometer", "Equipment", 150, 245),
        new(HmiSymbol.Boiler, "Steam boiler", "Equipment", 205, 245),
        new(HmiSymbol.CoolingTower, "Cooling tower", "Equipment", 215, 235),
        new(HmiSymbol.Tank, "Storage tank", "Process", 200, 270),
        new(HmiSymbol.Pump, "Centrifugal pump", "Process", 170, 155),
        new(HmiSymbol.Valve, "Isolation valve", "Process", 135, 135),
        new(HmiSymbol.Motor, "Electric motor", "Process", 160, 150),
        new(HmiSymbol.Pipe, "Flow pipe", "Process", 250, 44),
        new(HmiSymbol.Conveyor, "Conveyor", "Process", 270, 125),
        new(HmiSymbol.Gauge, "Analog gauge", "Instruments", 250, 190),
        new(HmiSymbol.BarGraph, "Bar graph", "Instruments", 230, 100),
        new(HmiSymbol.NumericDisplay, "Numeric display", "Instruments", 210, 105),
        new(HmiSymbol.Indicator, "Status indicator", "Instruments", 145, 130),
        new(HmiSymbol.Trend, "Historical trend", "Monitoring", 400, 230),
        new(HmiSymbol.AlarmBanner, "Alarm banner", "Monitoring", 620, 70),
        new(HmiSymbol.AlarmList, "Alarm list", "Monitoring", 620, 240),
        new(HmiSymbol.PushButton, "Command button", "Operator input", 190, 65),
        new(HmiSymbol.ToggleSwitch, "Toggle switch", "Operator input", 230, 70),
        new(HmiSymbol.NumericInput, "Numeric input", "Operator input", 280, 95),
        new(HmiSymbol.NavigationButton, "Screen navigation", "Operator input", 230, 60),
        new(HmiSymbol.RecipeButton, "Recipe button", "Operator input", 230, 65),
        new(HmiSymbol.Label, "Text label", "Drawing", 260, 55),
        new(HmiSymbol.Rectangle, "Panel / rectangle", "Drawing", 280, 180)
    });
    public static HmiControl Create(HmiSymbol symbol)
    {
        var descriptor = Items.Single(d => d.Symbol == symbol);
        var control = symbol switch
        {
            HmiSymbol.HeatExchanger => (HmiControl)new HmiHeatExchanger(),
            HmiSymbol.Filter => (HmiControl)new HmiFilter(),
            HmiSymbol.Compressor => (HmiControl)new HmiCompressor(),
            HmiSymbol.Fan => (HmiControl)new HmiFan(),
            HmiSymbol.Heater => (HmiControl)new HmiHeater(),
            HmiSymbol.Thermometer => (HmiControl)new HmiThermometer(),
            HmiSymbol.Boiler => (HmiControl)new HmiBoiler(),
            HmiSymbol.CoolingTower => (HmiControl)new HmiCoolingTower(),
            HmiSymbol.Tank => (HmiControl)new HmiTank(),
            HmiSymbol.Pump => new HmiPump(),
            HmiSymbol.Valve => new HmiValve(),
            HmiSymbol.Motor => new HmiMotor(),
            HmiSymbol.Gauge => new HmiGauge(),
            HmiSymbol.Trend => new HmiTrend(),
            HmiSymbol.AlarmList => new HmiAlarmList(),
            HmiSymbol.NumericDisplay => new HmiNumericDisplay(),
            _ => new HmiControl(symbol)
        };
        control.ApplyDefinition(new HmiElement { Symbol = symbol, Name = descriptor.Name, Label = descriptor.Name, Width = descriptor.Width, Height = descriptor.Height });
        return control;
    }
}
