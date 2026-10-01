using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable Silo symbol on the shared retained HMI renderer.</summary>
public sealed class HmiSilo : HmiControl
{
    public HmiSilo() : base(HmiSymbol.Silo) { }
}
