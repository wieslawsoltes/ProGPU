using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable retained shared control function symbol; no implicit controller or circuit logic.</summary>
public sealed class HmiControlFunction : HmiControl
{
    public HmiControlFunction() : base(HmiSymbol.ControlFunction) { }
}
