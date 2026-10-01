using ProGPU.Hmi;
namespace ProGPU.WinUI.Hmi;

public sealed class HmiFan : HmiControl
{
    public HmiFan() : base(HmiSymbol.Fan) { Width = 170; Height = 175; }
}
