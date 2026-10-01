using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable retained normally closed contact symbol; no implicit controller or circuit logic.</summary>
public sealed class HmiNormallyClosedContact : HmiControl
{
    public HmiNormallyClosedContact() : base(HmiSymbol.NormallyClosedContact) { }
}
