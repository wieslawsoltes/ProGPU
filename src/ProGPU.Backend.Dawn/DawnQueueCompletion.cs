using System.Runtime.InteropServices;
using W = WebGpuSharp;

namespace ProGPU.Backend.Dawn;

// A wait returning (including throwing) ends only managed ownership. Native
// ownership lasts through the actual completion/cancellation callback.
internal sealed class DawnQueueCompletion
{
    private readonly object _gate = new();
    private GCHandle _self;
    private int _uses = 1;
    private bool _started, _nativePending, _managedEnded, _completed;
    private W.QueueWorkDoneStatus _status;
    private string _message = string.Empty;
    private Exception? _decodeFailure;
    private int _callbackCount, _retirementCount;

    internal DawnQueueCompletion() => _self = GCHandle.Alloc(this);

    internal nint BeginNativeUse()
    {
        lock (_gate)
        {
            if (_started || _managedEnded) throw new InvalidOperationException("Queue completion ownership already started or ended.");
            _started = _nativePending = true;
            _uses++;
            return GCHandle.ToIntPtr(_self);
        }
    }

    internal void Complete(W.QueueWorkDoneStatus status, string message, Exception? decodeFailure = null)
    {
        try
        {
            lock (_gate)
            {
                _status = status;
                _message = message;
                _decodeFailure = decodeFailure;
                _completed = true;
                _callbackCount++;
            }
        }
        finally { EndUse(native: true); }
    }

    internal void RequireSuccess()
    {
        lock (_gate)
        {
            if (!_completed || _status != W.QueueWorkDoneStatus.Success || _decodeFailure != null)
                throw new InvalidOperationException(
                    $"Dawn queue wait failed: {(_completed ? _status.ToString() : "callback pending")}. {_message}", _decodeFailure);
        }
    }

    internal void CancelUnqueuedNativeUse() => EndUse(native: true);
    internal void EndManagedUse() => EndUse(native: false);

    private void EndUse(bool native)
    {
        lock (_gate)
        {
            if (native)
            {
                if (!_nativePending) return;
                _nativePending = false;
            }
            else
            {
                if (_managedEnded) return;
                _managedEnded = true;
            }
            if (--_uses != 0) return;
            _self.Free();
            _retirementCount++;
        }
    }

    // Observes actual callback/GCHandle retirement, not elapsed time or GPU counters.
    internal (int Callbacks, int Retirements) Retirement
    {
        get { lock (_gate) return (_callbackCount, _retirementCount); }
    }
}
