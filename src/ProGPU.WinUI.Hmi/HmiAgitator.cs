using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable Agitator symbol on the shared retained HMI renderer.</summary>
public sealed class HmiAgitator : HmiControl
{
    public HmiAgitator() : base(HmiSymbol.Agitator) { }
}
