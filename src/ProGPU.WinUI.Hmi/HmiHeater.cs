using ProGPU.Hmi;
namespace ProGPU.WinUI.Hmi;

public sealed class HmiHeater : HmiControl
{
    public HmiHeater() : base(HmiSymbol.Heater) { Width = 180; Height = 150; }
}
