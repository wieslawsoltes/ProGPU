using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable retained instrument bubble symbol; no implicit controller or circuit logic.</summary>
public sealed class HmiInstrumentBubble : HmiControl
{
    public HmiInstrumentBubble() : base(HmiSymbol.InstrumentBubble) { }
}
