using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable Hopper symbol on the shared retained HMI renderer.</summary>
public sealed class HmiHopper : HmiControl
{
    public HmiHopper() : base(HmiSymbol.Hopper) { }
}
