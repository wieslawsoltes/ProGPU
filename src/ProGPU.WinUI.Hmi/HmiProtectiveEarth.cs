using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable retained protective earth symbol; no implicit controller or circuit logic.</summary>
public sealed class HmiProtectiveEarth : HmiControl
{
    public HmiProtectiveEarth() : base(HmiSymbol.ProtectiveEarth) { }
}
