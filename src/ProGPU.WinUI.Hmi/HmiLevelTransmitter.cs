using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable LevelTransmitter symbol on the shared retained HMI renderer.</summary>
public sealed class HmiLevelTransmitter : HmiControl
{
    public HmiLevelTransmitter() : base(HmiSymbol.LevelTransmitter) { }
}
