using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable Separator symbol on the shared retained HMI renderer.</summary>
public sealed class HmiSeparator : HmiControl
{
    public HmiSeparator() : base(HmiSymbol.Separator) { }
}
