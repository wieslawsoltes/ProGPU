using ProGPU.Hmi;
namespace ProGPU.WinUI.Hmi;

public sealed class HmiThermometer : HmiControl
{
    public HmiThermometer() : base(HmiSymbol.Thermometer) { Width = 150; Height = 245; }
}
