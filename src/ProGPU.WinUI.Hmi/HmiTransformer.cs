using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable retained transformer symbol; no implicit controller or circuit logic.</summary>
public sealed class HmiTransformer : HmiControl
{
    public HmiTransformer() : base(HmiSymbol.Transformer) { }
}
