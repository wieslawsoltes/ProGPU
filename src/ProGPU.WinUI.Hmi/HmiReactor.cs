using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable Reactor symbol on the shared retained HMI renderer.</summary>
public sealed class HmiReactor : HmiControl
{
    public HmiReactor() : base(HmiSymbol.Reactor) { }
}
