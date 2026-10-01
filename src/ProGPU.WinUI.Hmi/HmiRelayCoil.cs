using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable retained relay coil symbol; no implicit controller or circuit logic.</summary>
public sealed class HmiRelayCoil : HmiControl
{
    public HmiRelayCoil() : base(HmiSymbol.RelayCoil) { }
}
