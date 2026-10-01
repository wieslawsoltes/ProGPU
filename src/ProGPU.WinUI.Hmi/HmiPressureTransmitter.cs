using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable PressureTransmitter symbol on the shared retained HMI renderer.</summary>
public sealed class HmiPressureTransmitter : HmiControl
{
    public HmiPressureTransmitter() : base(HmiSymbol.PressureTransmitter) { }
}
