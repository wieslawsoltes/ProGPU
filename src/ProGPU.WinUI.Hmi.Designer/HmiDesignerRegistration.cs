using ProGPU.Hmi;
using ProGPU.WinUI.Designer;

namespace ProGPU.WinUI.Hmi.Designer;

/// <summary>Optional designer plug-in. Runtime controls remain independent of the designer assembly.</summary>
public static class HmiDesignerRegistration
{
    private static readonly object Gate = new();
    private static bool _registered;
    public static string ToolboxKey(HmiSymbol symbol) => "Hmi" + symbol;
    public static void Register()
    {
        lock (Gate)
        {
            if (_registered) return;
            foreach (var item in HmiControlCatalog.Items)
            {
                var symbol = item.Symbol;
                DesignerElementRegistry.Register<HmiControl>(ToolboxKey(symbol), () => HmiControlCatalog.Create(symbol), Copy, isAtomic: true);
            }
            Register<HmiTank>(HmiSymbol.Tank, () => new HmiTank());
            Register<HmiPump>(HmiSymbol.Pump, () => new HmiPump());
            Register<HmiValve>(HmiSymbol.Valve, () => new HmiValve());
            Register<HmiMotor>(HmiSymbol.Motor, () => new HmiMotor());
            Register<HmiGauge>(HmiSymbol.Gauge, () => new HmiGauge());
            Register<HmiTrend>(HmiSymbol.Trend, () => new HmiTrend());
            Register<HmiAlarmList>(HmiSymbol.AlarmList, () => new HmiAlarmList());
            Register<HmiNumericDisplay>(HmiSymbol.NumericDisplay, () => new HmiNumericDisplay());
            Register<HmiHeatExchanger>(HmiSymbol.HeatExchanger, () => new HmiHeatExchanger());
            Register<HmiFilter>(HmiSymbol.Filter, () => new HmiFilter());
            Register<HmiCompressor>(HmiSymbol.Compressor, () => new HmiCompressor());
            Register<HmiFan>(HmiSymbol.Fan, () => new HmiFan());
            Register<HmiHeater>(HmiSymbol.Heater, () => new HmiHeater());
            Register<HmiThermometer>(HmiSymbol.Thermometer, () => new HmiThermometer());
            Register<HmiBoiler>(HmiSymbol.Boiler, () => new HmiBoiler());
            Register<HmiCoolingTower>(HmiSymbol.CoolingTower, () => new HmiCoolingTower());
            _registered = true;
        }
    }
    private static void Register<T>(HmiSymbol symbol, Func<T> factory) where T : HmiControl
    {
        DesignerElementRegistry.Register(ToolboxKey(symbol), () =>
        {
            var control = factory();
            var descriptor = HmiControlCatalog.Items.Single(d => d.Symbol == symbol);
            control.ApplyDefinition(new HmiElement { Symbol = symbol, Name = descriptor.Name, Label = descriptor.Name, Width = descriptor.Width, Height = descriptor.Height });
            return control;
        }, (T source, T target) => Copy(source, target), isAtomic: true);
    }
    private static void Copy(HmiControl source, HmiControl target)
    {
        target.ApplyDefinition(source.CaptureDefinition());
        target.Font = source.Font;
        target.CommandsEnabled = false;
    }
}
