using ProGPU.Hmi;
namespace ProGPU.WinUI.Hmi;

public sealed class HmiBoiler : HmiControl
{
    public HmiBoiler() : base(HmiSymbol.Boiler) { Width = 205; Height = 245; }
}
