using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable Strainer symbol on the shared retained HMI renderer.</summary>
public sealed class HmiStrainer : HmiControl
{
    public HmiStrainer() : base(HmiSymbol.Strainer) { }
}
