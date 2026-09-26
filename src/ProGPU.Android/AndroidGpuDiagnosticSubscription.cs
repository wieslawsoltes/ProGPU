using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace ProGPU.Android;

/// <summary>
/// Bridges process-wide WebGPU failures to the active Android host's log sink.
/// It does not initialize, poll, recover, or otherwise change a GPU device.
/// </summary>
internal sealed class AndroidGpuDiagnosticSubscription : IDisposable
{
    private readonly object _gate = new();
    private Action<string>? _writeError;

    public AndroidGpuDiagnosticSubscription(Action<string> writeError)
    {
        ArgumentNullException.ThrowIfNull(writeError);
        _writeError = writeError;
        WgpuContext.OnWebGpuError += OnError;
        WgpuContext.OnWebGpuDeviceLost += OnDeviceLost;
    }

    private void OnError(ErrorType type, string message)
    {
        lock (_gate)
        {
            _writeError?.Invoke($"WebGPU error ({type}): {message}");
        }
    }

    private void OnDeviceLost(DeviceLostReason reason, string message)
    {
        lock (_gate)
        {
            _writeError?.Invoke($"WebGPU device lost ({reason}): {message}");
        }
    }

    public void Dispose()
    {
        // Clear the sink under the same lock as callbacks, so even an event's
        // already-captured invocation list cannot log into a released host.
        lock (_gate)
        {
            if (_writeError is null) return;
            _writeError = null;
        }
        WgpuContext.OnWebGpuError -= OnError;
        WgpuContext.OnWebGpuDeviceLost -= OnDeviceLost;
    }
}
