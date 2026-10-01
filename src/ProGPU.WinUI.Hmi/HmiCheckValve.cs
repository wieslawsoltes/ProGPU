using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable CheckValve symbol on the shared retained HMI renderer.</summary>
public sealed class HmiCheckValve : HmiControl
{
    public HmiCheckValve() : base(HmiSymbol.CheckValve) { }
}
