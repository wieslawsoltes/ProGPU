using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable retained circuit breaker symbol; no implicit controller or circuit logic.</summary>
public sealed class HmiCircuitBreaker : HmiControl
{
    public HmiCircuitBreaker() : base(HmiSymbol.CircuitBreaker) { }
}
