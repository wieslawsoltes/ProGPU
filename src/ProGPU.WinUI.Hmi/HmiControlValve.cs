using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable ControlValve symbol on the shared retained HMI renderer.</summary>
public sealed class HmiControlValve : HmiControl
{
    public HmiControlValve() : base(HmiSymbol.ControlValve) { }
}
