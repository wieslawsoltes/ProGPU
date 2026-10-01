using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable FlowMeter symbol on the shared retained HMI renderer.</summary>
public sealed class HmiFlowMeter : HmiControl
{
    public HmiFlowMeter() : base(HmiSymbol.FlowMeter) { }
}
