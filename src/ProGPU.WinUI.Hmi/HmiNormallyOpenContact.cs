using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable retained normally open contact symbol; no implicit controller or circuit logic.</summary>
public sealed class HmiNormallyOpenContact : HmiControl
{
    public HmiNormallyOpenContact() : base(HmiSymbol.NormallyOpenContact) { }
}
