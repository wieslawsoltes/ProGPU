using WebGpuSharp.FFI;
using W = WebGpuSharp;

namespace ProGPU.Backend.Dawn;

public sealed unsafe partial class DawnGpuContext
{
    private static AdapterHandle RequestOwnedAdapter(
        InstanceHandle instance, RequestAdapterOptionsFFI options, string operation,
        bool? offscreenFallbackPolicy = null,
        DawnRequestAbandonmentProbe? cancellationProbe = null)
    {
        var state = new OwnedDawnRequest<AdapterHandle>(static owned => owned.Release(), cancellationProbe?.Adapter);
        W.Future queued = default;
        Exception? failure = null;
        try
        {
            var callback = new RequestAdapterCallbackInfoFFI
            {
                Mode = W.CallbackMode.WaitAnyOnly,
                Callback = &CompleteOwnedAdapterRequest,
                Userdata1 = (void*)state.BeginNativeUse()
            };
            try { queued = instance.RequestAdapter(&options, callback); }
            catch { state.CancelUnqueuedNativeUse(); throw; }
            cancellationProbe?.BeforeWait(deviceRequest: false);
            Wait(instance, queued, operation);
            return state.TakeHandle((int)W.RequestAdapterStatus.Success, operation);
        }
        catch (Exception error)
        {
            failure = error;
            if (offscreenFallbackPolicy.HasValue)
            {
                try
                {
                    DawnAdapterRequestFailureDiagnostics.Attach(error, options.BackendType,
                        offscreenFallbackPolicy.Value, options.FeatureLevel, options.PowerPreference);
                }
                catch (Exception diagnostic)
                {
                    try { error.Data["DawnAdapterDiagnosticFailure"] = diagnostic; } catch { }
                }
            }
            DrainAbandonedDawnRequest(instance, queued, state, error);
            throw;
        }
        finally { state.EndManagedUse(failure); }
    }

    // This control uses the ordinary typed RequestAdapter entry point on the
    // already verified provider, not the optional native LUID companion path.
    // It asserts request ownership, not which adapter the ordinary policy picks.
    internal static void VerifyDawnAdapterRequestAbandonmentForDiagnostics()
    {
        using DawnGpuContext owner = CreateSystemWarpOffscreen();
        var probe = new DawnRequestAbandonmentProbe(cancelDeviceRequest: false);
        var options = new RequestAdapterOptionsFFI
        {
            BackendType = W.BackendType.D3D12,
            PowerPreference = W.PowerPreference.HighPerformance
        };
        try
        {
            AdapterHandle unexpected = RequestOwnedAdapter(owner.Instance, options,
                "exercise ordinary adapter request retirement", cancellationProbe: probe);
            unexpected.Release();
            throw new InvalidOperationException("Ordinary adapter request abandonment was not reached.");
        }
        catch (Exception error) when (ReferenceEquals(error, probe.Failure))
        {
            if (error.Data.Count != 0)
                throw new InvalidOperationException("Ordinary adapter retirement reported a cleanup failure.", error);
        }
        probe.Adapter.Verify((int)W.RequestAdapterStatus.Success, releasedResults: 1);
    }
}
